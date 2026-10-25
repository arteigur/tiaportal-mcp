using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.HW;
using Siemens.Engineering.Online;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // Online state, go online / offline, and a two-step download of one PLC.
        //
        // Callers: the tools in McpServer.Online.cs. Affected API: OnlineProvider and
        // DownloadProvider, both services of the PLC's device item, and ConnectionConfiguration
        // for choosing the PC interface and target interface.
        //
        // Reading the online state does not connect to anything, so GetOnlineState is a plain
        // read. GoOnline / GoOffline connect to the real PLC. A download is split into
        // PreviewDownload, which compiles and hands out a short-lived token, and Download, which
        // needs that token: the caller is meant to show the preview to a human and wait for an
        // explicit yes before calling Download.

        private static readonly TimeSpan DownloadTicketLifetime = TimeSpan.FromMinutes(15);
        private readonly ConcurrentDictionary<string, DownloadTicket> _downloadTickets = new();

        #region online state and connection targets

        public (DeviceItem Plc, OnlineState State, bool IsConfigured) GetOnlineState(string softwarePath)
        {
            return Operation.Run(_logger, nameof(GetOnlineState), PortalErrorCode.NotFound,
                () =>
                {
                    var plc = GetPlcDeviceItemOrThrow(softwarePath);
                    var online = GetOnlineProviderOrThrow(plc, softwarePath);

                    return (plc, online.State, online.Configuration?.IsConfigured ?? false);
                },
                ("softwarePath", softwarePath));
        }

        /// <summary>
        /// Lists every mode / PC interface / target interface combination TIA Portal offers for
        /// this PLC, which are the values GoOnline and PreviewDownload take.
        /// </summary>
        public List<ConnectionTarget> GetConnectionTargets(string softwarePath)
        {
            return Operation.Run(_logger, nameof(GetConnectionTargets), PortalErrorCode.NotFound,
                () =>
                {
                    var plc = GetPlcDeviceItemOrThrow(softwarePath);
                    var configuration = GetOnlineProviderOrThrow(plc, softwarePath).Configuration
                        ?? throw new PortalException(PortalErrorCode.NotSupported,
                            $"'{softwarePath}' has no online connection configuration.");

                    var targets = new List<ConnectionTarget>();

                    foreach (ConfigurationMode mode in configuration.Modes)
                    {
                        foreach (ConfigurationPcInterface pcInterface in mode.PcInterfaces)
                        {
                            foreach (ConfigurationTargetInterface target in pcInterface.TargetInterfaces)
                            {
                                targets.Add(new ConnectionTarget
                                {
                                    Mode = mode.Name,
                                    PcInterface = pcInterface.Name,
                                    PcInterfaceNumber = pcInterface.Number,
                                    TargetInterface = target.Name,
                                    Addresses = target.Addresses.Select(a => a.Name).ToList()
                                });
                            }
                        }
                    }

                    return targets;
                },
                ("softwarePath", softwarePath));
        }

        #endregion

        #region go online / offline

        /// <summary>
        /// Goes online to the PLC. Without a target the connection last used in TIA Portal is
        /// taken; with one it is applied first. Returns the resulting state, which can be
        /// NotReachable or Protected rather than Online.
        /// </summary>
        public OnlineState GoOnline(string softwarePath, string? mode, string? pcInterface, int pcInterfaceNumber, string? targetInterface)
        {
            return Operation.Run(_logger, nameof(GoOnline), PortalErrorCode.OnlineFailed,
                () =>
                {
                    var plc = GetPlcDeviceItemOrThrow(softwarePath);
                    var online = GetOnlineProviderOrThrow(plc, softwarePath);

                    if (!string.IsNullOrWhiteSpace(targetInterface))
                    {
                        var target = FindTargetInterface(online.Configuration, mode, pcInterface, pcInterfaceNumber, targetInterface!, softwarePath);

                        if (!online.Configuration.ApplyConfiguration(target))
                        {
                            throw new PortalException(PortalErrorCode.OnlineFailed,
                                $"TIA Portal did not accept the connection '{mode}/{pcInterface}/{targetInterface}'.");
                        }
                    }
                    else if (online.Configuration == null || !online.Configuration.IsConfigured)
                    {
                        throw new PortalException(PortalErrorCode.InvalidParams,
                            $"No online connection is configured for '{softwarePath}'. Pick one from 'GetConnectionTargets' and pass mode, pcInterface, pcInterfaceNumber and targetInterface.");
                    }

                    return online.GoOnline();
                },
                ("softwarePath", softwarePath), ("targetInterface", targetInterface ?? string.Empty));
        }

        public OnlineState GoOffline(string softwarePath)
        {
            return Operation.Run(_logger, nameof(GoOffline), PortalErrorCode.OnlineFailed,
                () =>
                {
                    var plc = GetPlcDeviceItemOrThrow(softwarePath);
                    var online = GetOnlineProviderOrThrow(plc, softwarePath);

                    if (online.State != OnlineState.Offline)
                    {
                        online.GoOffline();
                    }

                    return online.State;
                },
                ("softwarePath", softwarePath));
        }

        #endregion

        #region download

        /// <summary>
        /// Step one of a download: resolves the target, compiles the PLC software, and returns a
        /// token for <see cref="Download"/>. Nothing is sent to the PLC. A compile with errors
        /// returns no token.
        /// </summary>
        public DownloadPreview PreviewDownload(string softwarePath, string mode, string pcInterface, int pcInterfaceNumber, string targetInterface, string options)
        {
            return Operation.Run(_logger, nameof(PreviewDownload), PortalErrorCode.DownloadFailed,
                () =>
                {
                    var plc = GetPlcDeviceItemOrThrow(softwarePath);
                    var download = plc.GetService<DownloadProvider>()
                        ?? throw new PortalException(PortalErrorCode.NotSupported,
                            $"'{softwarePath}' does not support download through Openness.");

                    var downloadOptions = ParseDownloadOptions(options);

                    // Resolve now so a typo fails here, not after the human said yes.
                    FindTargetInterface(download.Configuration, mode, pcInterface, pcInterfaceNumber, targetInterface, softwarePath);

                    var compile = CompilePlcSoftware(softwarePath)
                        ?? throw new PortalException(PortalErrorCode.DownloadFailed,
                            $"Compiling '{softwarePath}' before the download failed.");

                    var preview = new DownloadPreview
                    {
                        PlcName = plc.Name,
                        SoftwarePath = softwarePath,
                        Target = $"{mode} / {pcInterface} ({pcInterfaceNumber}) / {targetInterface}",
                        Options = downloadOptions.ToString(),
                        OnlineState = GetOnlineProviderOrThrow(plc, softwarePath).State.ToString(),
                        CompileState = compile.State.ToString(),
                        CompileErrors = compile.ErrorCount,
                        CompileWarnings = compile.WarningCount,
                        HasUnsavedChanges = (_project as ProjectBase)?.IsModified ?? false
                    };

                    if (compile.State == CompilerResultState.Error)
                    {
                        return preview;
                    }

                    PurgeExpiredTickets();

                    var ticket = new DownloadTicket
                    {
                        Token = Guid.NewGuid().ToString("N").Substring(0, 12),
                        SoftwarePath = softwarePath,
                        ProjectPath = SafePath(_project!) ?? string.Empty,
                        Mode = mode,
                        PcInterface = pcInterface,
                        PcInterfaceNumber = pcInterfaceNumber,
                        TargetInterface = targetInterface,
                        Options = downloadOptions,
                        ExpiresUtc = DateTime.UtcNow + DownloadTicketLifetime
                    };

                    _downloadTickets[ticket.Token] = ticket;

                    preview.ConfirmToken = ticket.Token;
                    preview.ExpiresUtc = ticket.ExpiresUtc;

                    return preview;
                },
                ("softwarePath", softwarePath), ("targetInterface", targetInterface));
        }

        /// <summary>
        /// Step two: downloads with the settings fixed by the preview. Every question TIA Portal
        /// asks before and after the download is answered by policy and logged in the result:
        /// stopping the CPU only with <paramref name="allowStop"/> (and then the CPU is started
        /// again afterwards), re-initialising memory only with <paramref name="allowReinitialize"/>,
        /// anything destructive never. A question left unanswered makes TIA Portal abort.
        /// </summary>
        public DownloadOutcome Download(string confirmToken, bool allowStop, bool allowReinitialize)
        {
            return Operation.Run(_logger, nameof(Download), PortalErrorCode.DownloadFailed,
                () =>
                {
                    PurgeExpiredTickets();

                    if (string.IsNullOrWhiteSpace(confirmToken) || !_downloadTickets.TryRemove(confirmToken.Trim(), out var ticket))
                    {
                        throw new PortalException(PortalErrorCode.InvalidParams,
                            "Unknown or expired confirmToken. Call 'PreviewDownload' again, show it to the user, and use the new token.");
                    }

                    if (!string.Equals(ticket.ProjectPath, SafePath(_project!) ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new PortalException(PortalErrorCode.InvalidState,
                            "The open project changed since the preview. Call 'PreviewDownload' again.");
                    }

                    var plc = GetPlcDeviceItemOrThrow(ticket.SoftwarePath);
                    var download = plc.GetService<DownloadProvider>()
                        ?? throw new PortalException(PortalErrorCode.NotSupported,
                            $"'{ticket.SoftwarePath}' does not support download through Openness.");
                    var target = FindTargetInterface(download.Configuration, ticket.Mode, ticket.PcInterface, ticket.PcInterfaceNumber, ticket.TargetInterface, ticket.SoftwarePath);

                    var decisions = new List<DownloadDecision>();

                    void Pre(DownloadConfiguration configuration) =>
                        decisions.Add(AnswerPreDownload(configuration, allowStop, allowReinitialize, ticket.Options));

                    void Post(DownloadConfiguration configuration) =>
                        decisions.Add(AnswerPostDownload(configuration, allowStop));

                    _logger?.LogWarning("Downloading '{SoftwarePath}' to '{Target}' ({Options}), allowStop={AllowStop}, allowReinitialize={AllowReinitialize}",
                        ticket.SoftwarePath, ticket.TargetInterface, ticket.Options, allowStop, allowReinitialize);

                    var result = download.Download(target, Pre, Post, ticket.Options);

                    return new DownloadOutcome
                    {
                        PlcName = plc.Name,
                        SoftwarePath = ticket.SoftwarePath,
                        State = result.State.ToString(),
                        ErrorCount = result.ErrorCount,
                        WarningCount = result.WarningCount,
                        Messages = FlattenDownloadMessages(result.Messages, 0).ToList(),
                        Decisions = decisions
                    };
                },
                ("confirmToken", confirmToken));
        }

        private static DownloadDecision AnswerPreDownload(DownloadConfiguration configuration, bool allowStop, bool allowReinitialize, DownloadOptions options)
        {
            var decision = new DownloadDecision { Phase = "pre", Kind = configuration.GetType().Name, Message = configuration.Message };

            switch (configuration)
            {
                case StopModules stop:
                    stop.CurrentSelection = allowStop ? StopModulesSelections.StopAll : StopModulesSelections.NoAction;
                    decision.Answer = stop.CurrentSelection.ToString();
                    decision.Blocking = !allowStop;
                    break;

                case InitializeMemory initialize:
                    initialize.CurrentSelection = allowReinitialize ? InitializeMemorySelections.AcceptAll : InitializeMemorySelections.NoAction;
                    decision.Answer = initialize.CurrentSelection.ToString();
                    decision.Blocking = !allowReinitialize;
                    break;

                case OverwriteSystemData overwrite:
                    // System data is the hardware configuration: overwrite it only for a hardware download.
                    var hardware = (options & DownloadOptions.Hardware) != 0;
                    overwrite.CurrentSelection = hardware ? OverwriteSystemDataSelections.Overwrite : OverwriteSystemDataSelections.NoAction;
                    decision.Answer = overwrite.CurrentSelection.ToString();
                    decision.Blocking = !hardware;
                    break;

                case AlarmTextLibrariesDownload alarms:
                    alarms.CurrentSelection = AlarmTextLibrariesDownloadSelections.ConsistentDownload;
                    decision.Answer = alarms.CurrentSelection.ToString();
                    break;

                case DownloadPasswordConfiguration:
                    decision.Answer = "NotSupplied";
                    decision.Blocking = true;
                    break;

                default:
                    // Reset module, selective delete, different target, active tests, user
                    // management, protection level and the rest keep TIA Portal's default, which
                    // for every destructive one is "no action" and therefore aborts.
                    decision.Answer = "Default";
                    break;
            }

            return decision;
        }

        private static DownloadDecision AnswerPostDownload(DownloadConfiguration configuration, bool allowStop)
        {
            var decision = new DownloadDecision { Phase = "post", Kind = configuration.GetType().Name, Message = configuration.Message };

            if (configuration is StartModules start)
            {
                // A CPU this download was allowed to stop is started again; leaving the plant in
                // STOP is never the intended outcome.
                start.CurrentSelection = allowStop ? StartModulesSelections.StartModule : StartModulesSelections.NoAction;
                decision.Answer = start.CurrentSelection.ToString();
            }
            else
            {
                decision.Answer = "Default";
            }

            return decision;
        }

        private static IEnumerable<DownloadMessage> FlattenDownloadMessages(DownloadResultMessageComposition? messages, int depth)
        {
            if (messages == null)
            {
                yield break;
            }

            foreach (DownloadResultMessage message in messages)
            {
                yield return new DownloadMessage { State = message.State.ToString(), Message = message.Message, Depth = depth };

                foreach (var child in FlattenDownloadMessages(message.Messages, depth + 1))
                {
                    yield return child;
                }
            }
        }

        private static DownloadOptions ParseDownloadOptions(string options)
        {
            var text = string.IsNullOrWhiteSpace(options) ? nameof(DownloadOptions.SoftwareOnlyChanges) : options;

            try
            {
                var parsed = (DownloadOptions)Enum.Parse(typeof(DownloadOptions), text, ignoreCase: true);

                if (parsed == DownloadOptions.None)
                {
                    throw new ArgumentException("None");
                }

                return parsed;
            }
            catch (ArgumentException)
            {
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"Unknown download options '{options}'. Use 'SoftwareOnlyChanges' (default), 'Software', 'Hardware', or 'Hardware, Software'.");
            }
        }

        private void PurgeExpiredTickets()
        {
            foreach (var expired in _downloadTickets.Where(t => t.Value.ExpiresUtc < DateTime.UtcNow).Select(t => t.Key).ToList())
            {
                _downloadTickets.TryRemove(expired, out _);
            }
        }

        #endregion

        #region helpers

        private DeviceItem GetPlcDeviceItemOrThrow(string softwarePath)
        {
            var plcSoftware = GetPlcSoftwareOrThrow(softwarePath);

            return FindDeviceItem(GetSoftwareContainer(softwarePath), plcSoftware)
                ?? throw new PortalException(PortalErrorCode.NotFound,
                    $"No PLC device item found for '{softwarePath}'. Use 'GetSoftwareTree' to list the PLC software.");
        }

        private static OnlineProvider GetOnlineProviderOrThrow(DeviceItem plc, string softwarePath)
        {
            return plc.GetService<OnlineProvider>()
                ?? throw new PortalException(PortalErrorCode.NotSupported,
                    $"'{softwarePath}' does not support going online through Openness.");
        }

        private static ConfigurationTargetInterface FindTargetInterface(ConnectionConfiguration? configuration, string? mode, string? pcInterface, int pcInterfaceNumber, string targetInterface, string softwarePath)
        {
            const string hint = "Use 'GetConnectionTargets' for the valid values.";

            if (configuration == null)
            {
                throw new PortalException(PortalErrorCode.NotSupported, $"'{softwarePath}' has no connection configuration.");
            }

            var foundMode = configuration.Modes.Find(mode ?? string.Empty)
                ?? throw new PortalException(PortalErrorCode.InvalidParams, $"Connection mode '{mode}' not found. {hint}");
            var foundPc = foundMode.PcInterfaces.Find(pcInterface ?? string.Empty, pcInterfaceNumber)
                ?? throw new PortalException(PortalErrorCode.InvalidParams, $"PC interface '{pcInterface}' ({pcInterfaceNumber}) not found in mode '{mode}'. {hint}");

            return foundPc.TargetInterfaces.Find(targetInterface)
                ?? throw new PortalException(PortalErrorCode.InvalidParams, $"Target interface '{targetInterface}' not found on '{pcInterface}'. {hint}");
        }

        #endregion
    }

    public class ConnectionTarget
    {
        public string Mode { get; set; } = string.Empty;
        public string PcInterface { get; set; } = string.Empty;
        public int PcInterfaceNumber { get; set; }
        public string TargetInterface { get; set; } = string.Empty;
        public List<string> Addresses { get; set; } = new List<string>();
    }

    public class DownloadPreview
    {
        public string PlcName { get; set; } = string.Empty;
        public string SoftwarePath { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Options { get; set; } = string.Empty;
        public string OnlineState { get; set; } = string.Empty;
        public string CompileState { get; set; } = string.Empty;
        public int CompileErrors { get; set; }
        public int CompileWarnings { get; set; }
        public bool HasUnsavedChanges { get; set; }
        public string? ConfirmToken { get; set; }
        public DateTime? ExpiresUtc { get; set; }
    }

    public class DownloadOutcome
    {
        public string PlcName { get; set; } = string.Empty;
        public string SoftwarePath { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public List<DownloadMessage> Messages { get; set; } = new List<DownloadMessage>();
        public List<DownloadDecision> Decisions { get; set; } = new List<DownloadDecision>();
    }

    public class DownloadMessage
    {
        public string State { get; set; } = string.Empty;
        public string? Message { get; set; }
        public int Depth { get; set; }
    }

    public class DownloadDecision
    {
        public string Phase { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string? Message { get; set; }
        public string Answer { get; set; } = string.Empty;
        /// <summary>True when the answer is "no action" to a question that needs one, so TIA Portal aborts.</summary>
        public bool Blocking { get; set; }
    }

    internal class DownloadTicket
    {
        public string Token { get; set; } = string.Empty;
        public string SoftwarePath { get; set; } = string.Empty;
        public string ProjectPath { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string PcInterface { get; set; } = string.Empty;
        public int PcInterfaceNumber { get; set; }
        public string TargetInterface { get; set; } = string.Empty;
        public DownloadOptions Options { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }
}
