using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TiaMcpServer
{
    /// <summary>
    /// Serves the MCP server over Streamable HTTP, so agents on another machine (e.g. Linux on the
    /// host of the Windows VM that runs TIA Portal) can use it.
    ///
    /// Callers: Program.RunHttpHost when '--http' is given. Affected API: none existing - stdio
    /// stays the default. ASP.NET Core does not run on .NET Framework 4.8, which Openness needs, so
    /// this hosts the SDK's transport-agnostic StreamableHttpServerTransport on HttpListener, the
    /// way the SDK's ASP.NET Core handler does: one transport and one server per MCP session,
    /// keyed by the Mcp-Session-Id header. All sessions share the one Portal, so they also share
    /// the attached TIA Portal instance; Operation.Run already serializes Openness calls.
    ///
    /// Security: every request needs 'Authorization: Bearer &lt;token&gt;' unless the server only
    /// listens on localhost, and requests that carry an Origin header (browsers) are refused, so a
    /// web page cannot drive TIA Portal. The optional GET stream is not offered (405); responses
    /// travel on the POST response, which is all this server needs.
    /// </summary>
    public sealed class HttpHost
    {
        private const string SessionHeader = "Mcp-Session-Id";
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(8);

        private readonly string _prefix;
        private readonly string _path;
        private readonly byte[]? _token;
        private readonly McpServerOptions _serverOptions;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IServiceProvider _services;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

        private sealed class Session
        {
            public Session(StreamableHttpServerTransport transport, global::ModelContextProtocol.Server.McpServer server, Task run)
            {
                Transport = transport;
                Server = server;
                Run = run;
            }

            public StreamableHttpServerTransport Transport { get; }
            public global::ModelContextProtocol.Server.McpServer Server { get; }
            public Task Run { get; }
            public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
        }

        public HttpHost(string url, string? token, McpServerOptions serverOptions, ILoggerFactory loggerFactory, IServiceProvider services)
        {
            if (!Uri.TryCreate(url.Replace("://+", "://plus-host").Replace("://*", "://star-host"), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException($"'--http {url}' is not an http(s) URL, e.g. http://localhost:8765/mcp or http://+:8765/mcp.");
            }

            var path = uri.AbsolutePath.TrimEnd('/');
            _path = path.Length == 0 ? "/mcp" : path;
            _prefix = url.Substring(0, url.Length - uri.PathAndQuery.Length).TrimEnd('/') + "/";

            var loopback = uri.Host is "localhost" or "127.0.0.1" or "[::1]";

            if (string.IsNullOrEmpty(token) && !loopback)
            {
                throw new ArgumentException(
                    "Listening on a network address needs a token: set the environment variable TIA_MCP_HTTP_TOKEN (or pass --http-token). " +
                    "Without one, only http://localhost:<port>/ is allowed.");
            }

            _token = string.IsNullOrEmpty(token) ? null : Encoding.UTF8.GetBytes(token);
            _serverOptions = serverOptions;
            _loggerFactory = loggerFactory;
            _services = services;
            _logger = loggerFactory.CreateLogger<HttpHost>();
        }

        /// <summary>The URL clients put in their MCP configuration.</summary>
        public string Endpoint => _prefix.Replace("://+", "://<this-host>").Replace("://*", "://<this-host>").TrimEnd('/') + _path;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add(_prefix);

            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                throw new InvalidOperationException(
                    $"Windows refused to listen on '{_prefix}' (access denied). A non-localhost address needs a one-time URL reservation " +
                    $"from an administrator: netsh http add urlacl url={_prefix} user={Environment.UserDomainName}\\{Environment.UserName}", ex);
            }

            Console.Error.WriteLine($"TIA Portal MCP server listening on {Endpoint} ({(_token == null ? "localhost only, no token" : "bearer token required")})");

            using var registration = cancellationToken.Register(() => listener.Stop());
            using var sweeper = new Timer(_ => SweepIdleSessions(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (HttpListenerException ex)
                {
                    _logger.LogWarning(ex, "HTTP listener error");
                    continue;
                }

                _ = Task.Run(() => HandleAsync(context, cancellationToken));
            }

            foreach (var id in _sessions.Keys.ToList())
            {
                await EndSessionAsync(id).ConfigureAwait(false);
            }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                if (!string.Equals(request.Url.AbsolutePath.TrimEnd('/'), _path, StringComparison.OrdinalIgnoreCase))
                {
                    Reply(response, 404, "Not found. The MCP endpoint is " + _path);
                    return;
                }

                if (!string.IsNullOrEmpty(request.Headers["Origin"]))
                {
                    // Agents do not send Origin; browsers do. Refusing it keeps web pages out (DNS rebinding).
                    Reply(response, 403, "Requests from a browser (with an Origin header) are not accepted.");
                    return;
                }

                if (!IsAuthorized(request))
                {
                    response.AddHeader("WWW-Authenticate", "Bearer");
                    Reply(response, 401, "Missing or wrong bearer token.");
                    return;
                }

                switch (request.HttpMethod)
                {
                    case "POST":
                        await HandlePostAsync(context, cancellationToken).ConfigureAwait(false);
                        break;

                    case "DELETE":
                        var id = request.Headers[SessionHeader];
                        if (id != null && await EndSessionAsync(id).ConfigureAwait(false))
                        {
                            Reply(response, 200, string.Empty);
                        }
                        else
                        {
                            Reply(response, 404, "Unknown session.");
                        }
                        break;

                    default:
                        // GET would open the optional server-to-client stream, which this server does not need.
                        response.AddHeader("Allow", "POST, DELETE");
                        Reply(response, 405, "Use POST.");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HTTP request failed");

                try
                {
                    Reply(response, 500, "Internal error: " + ex.Message);
                }
                catch (Exception)
                {
                    // The response may already be streaming; nothing more to tell the client.
                }
            }
            finally
            {
                try
                {
                    response.Close();
                }
                catch (Exception)
                {
                    // Client went away.
                }
            }
        }

        private async Task HandlePostAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            var request = context.Request;
            var response = context.Response;

            JsonRpcMessage? message;

            try
            {
                message = await JsonSerializer.DeserializeAsync<JsonRpcMessage>(request.InputStream, McpJsonUtilities.DefaultOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                Reply(response, 400, "The body is not a JSON-RPC message: " + ex.Message);
                return;
            }

            if (message == null)
            {
                Reply(response, 400, "Empty body.");
                return;
            }

            var sessionId = request.Headers[SessionHeader];
            Session? session;

            if (string.IsNullOrEmpty(sessionId))
            {
                if (message is not JsonRpcRequest { Method: RequestMethods.Initialize })
                {
                    Reply(response, 400, $"Missing {SessionHeader} header: start with an 'initialize' request.");
                    return;
                }

                sessionId = NewSessionId();
                session = StartSession(sessionId);
            }
            else if (!_sessions.TryGetValue(sessionId, out session))
            {
                // The client starts a new session on 404, e.g. after a server restart.
                Reply(response, 404, "Unknown or expired session; initialize again.");
                return;
            }

            session.LastUsedUtc = DateTime.UtcNow;
            response.AddHeader(SessionHeader, sessionId);
            response.StatusCode = 200;
            response.ContentType = "text/event-stream";
            response.AddHeader("Cache-Control", "no-cache");
            response.SendChunked = true;

            var wrote = await session.Transport.HandlePostRequestAsync(message, response.OutputStream, cancellationToken).ConfigureAwait(false);

            if (!wrote)
            {
                // A notification or response from the client: nothing to send back.
                response.SendChunked = false;
                response.StatusCode = 202;
                response.ContentType = null;
                response.ContentLength64 = 0;
            }
        }

        private Session StartSession(string sessionId)
        {
            var transport = new StreamableHttpServerTransport(_loggerFactory) { SessionId = sessionId };
            var server = global::ModelContextProtocol.Server.McpServer.Create(transport, _serverOptions, _loggerFactory, _services);
            var run = Task.Run(() => server.RunAsync());
            var session = new Session(transport, server, run);

            _sessions[sessionId] = session;
            _logger.LogInformation("MCP session {SessionId} started", sessionId);

            return session;
        }

        private async Task<bool> EndSessionAsync(string sessionId)
        {
            if (!_sessions.TryRemove(sessionId, out var session))
            {
                return false;
            }

            try
            {
                await session.Server.DisposeAsync().ConfigureAwait(false);
                await session.Transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ending MCP session {SessionId} failed", sessionId);
            }

            _logger.LogInformation("MCP session {SessionId} ended", sessionId);

            return true;
        }

        private void SweepIdleSessions()
        {
            foreach (var pair in _sessions.Where(p => DateTime.UtcNow - p.Value.LastUsedUtc > IdleTimeout).ToList())
            {
                _ = EndSessionAsync(pair.Key);
            }
        }

        private bool IsAuthorized(HttpListenerRequest request)
        {
            if (_token == null)
            {
                return true;
            }

            var header = request.Headers["Authorization"] ?? string.Empty;
            const string scheme = "Bearer ";

            if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var given = Encoding.UTF8.GetBytes(header.Substring(scheme.Length).Trim());

            // Constant time, so the comparison does not leak how much of the token was right.
            var difference = given.Length ^ _token.Length;
            for (var i = 0; i < given.Length; i++)
            {
                difference |= given[i] ^ _token[i % _token.Length];
            }

            return difference == 0;
        }

        private static string NewSessionId()
        {
            var bytes = new byte[16];

            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void Reply(HttpListenerResponse response, int status, string text)
        {
            response.StatusCode = status;
            response.ContentType = "text/plain; charset=utf-8";

            var body = Encoding.UTF8.GetBytes(text);
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body, 0, body.Length);
        }
    }
}
