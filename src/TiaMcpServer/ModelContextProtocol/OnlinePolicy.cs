using ModelContextProtocol;
using System;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Marks a tool method that connects to a real PLC (goes online). Program.cs leaves marked
    /// tools out of the registered tool list unless '--allow-online' was passed, the same way
    /// [WriteTool] works for '--allow-write'.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class OnlineToolAttribute : System.Attribute
    {
    }

    /// <summary>
    /// Gates the tools that go online behind the '--allow-online' command line flag.
    ///
    /// Callers: Program.cs (sets AllowOnline from CliOptions and conditionally registers the
    /// online tools) and every [OnlineTool] tool in McpServer. Affected API: none existing - new
    /// type. Reads/writes no data files.
    ///
    /// Going online is separate from writing: an online tool reads from the PLC but never
    /// downloads to it, so '--allow-write' does not imply '--allow-online' or the other way round.
    /// EnsureEnabled is the first statement of every online tool, because the tests invoke the
    /// tool methods directly, bypassing registration.
    /// </summary>
    public static class OnlinePolicy
    {
        public static bool AllowOnline { get; set; }

        public static void EnsureEnabled(string toolName)
        {
            if (!AllowOnline)
            {
                throw new McpException(
                    $"Tool '{toolName}' connects to the PLC online and is disabled. " +
                    "Restart the MCP server with '--allow-online' to enable online tools.");
            }
        }
    }
}
