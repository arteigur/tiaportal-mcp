using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        // Online state, go online / offline, and the two-step download.
        //
        // Callers: registered through Program.BuildTools(); GoOnline / GoOffline only with
        // '--allow-online', PreviewDownload / Download only with '--allow-download' (PlantPolicy).
        // Data: structuredContent responses; no files. GetOnlineState and GetConnectionTargets read
        // the project's connection settings and never connect, so they are always registered.

        [McpServerTool(Name = "GetOnlineState", Title = "Get PLC online state", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
         Description("Show whether TIA Portal is online to a PLC (Offline, Connecting, Online, NotReachable, Protected, ...) without connecting. Imports and most writes need 'Offline'")]
        public static ResponseOnlineState GetOnlineState(
            [Description("softwarePath: defines the path in the project structure to the PLC software")] string softwarePath)
        {
            return RunPlantTool(nameof(GetOnlineState), () =>
            {
                var (plc, state, isConfigured) = Portal.GetOnlineState(softwarePath);

                return new ResponseOnlineState
                {
                    Message = $"'{plc.Name}' is {state}",
                    PlcName = plc.Name,
                    State = state.ToString(),
                    IsConnectionConfigured = isConfigured,
                    Meta = Ok()
                };
            });
        }

        [McpServerTool(Name = "GetConnectionTargets", Title = "List PLC connection targets", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
         Description("List the mode / PC interface / target interface combinations TIA Portal offers for reaching a PLC, i.e. the values 'GoOnline' and 'PreviewDownload' take. Does not connect")]
        public static ResponseConnectionTargets GetConnectionTargets(
            [Description("softwarePath: defines the path in the project structure to the PLC software")] string softwarePath)
        {
            return RunPlantTool(nameof(GetConnectionTargets), () =>
            {
                var targets = Portal.GetConnectionTargets(softwarePath);

                return new ResponseConnectionTargets
                {
                    Message = $"{targets.Count} connection target(s) for '{softwarePath}'",
                    Targets = targets,
                    Meta = Ok()
                };
            });
        }

        [OnlineTool]
        [McpServerTool(Name = "GoOnline", Title = "Go online to a PLC", Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true),
         Description("Connect TIA Portal online to the real PLC, for monitoring and comparing. Does not download or change values. Without a target it reuses the connection last used in TIA Portal; otherwise pass one from 'GetConnectionTargets'. Call 'GoOffline' before importing")]
        public static ResponseOnlineState GoOnline(
            [Description("softwarePath: defines the path in the project structure to the PLC software")] string softwarePath,
            [Description("mode: connection mode from 'GetConnectionTargets', e.g. 'PN/IE'; empty to reuse the configured connection")] string mode = "",
            [Description("pcInterface: PC interface name from 'GetConnectionTargets'")] string pcInterface = "",
            [Description("pcInterfaceNumber: PC interface number from 'GetConnectionTargets'")] int pcInterfaceNumber = 1,
            [Description("targetInterface: target interface name from 'GetConnectionTargets', e.g. '1 X1'")] string targetInterface = "")
        {
            PlantPolicy.EnsureOnline(nameof(GoOnline));

            return RunPlantTool(nameof(GoOnline), () =>
            {
                var state = Portal.GoOnline(softwarePath, mode, pcInterface, pcInterfaceNumber, targetInterface);

                return new ResponseOnlineState
                {
                    Message = state == global::Siemens.Engineering.Online.OnlineState.Online
                        ? $"'{softwarePath}' is online. Call 'GoOffline' when done; imports fail while online."
                        : $"Going online to '{softwarePath}' ended in state {state}.",
                    PlcName = softwarePath,
                    State = state.ToString(),
                    IsConnectionConfigured = true,
                    Meta = Ok()
                };
            });
        }

        [OnlineTool]
        [McpServerTool(Name = "GoOffline", Title = "Go offline from a PLC", Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true),
         Description("Disconnect TIA Portal from the PLC. Needed before imports and most writes")]
        public static ResponseOnlineState GoOffline(
            [Description("softwarePath: defines the path in the project structure to the PLC software")] string softwarePath)
        {
            PlantPolicy.EnsureOnline(nameof(GoOffline));

            return RunPlantTool(nameof(GoOffline), () =>
            {
                var state = Portal.GoOffline(softwarePath);

                return new ResponseOnlineState
                {
                    Message = $"'{softwarePath}' is {state}",
                    PlcName = softwarePath,
                    State = state.ToString(),
                    Meta = Ok()
                };
            });
        }

        [DownloadTool]
        [McpServerTool(Name = "PreviewDownload", Title = "Prepare a PLC download", Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true),
         Description("Step 1 of a download; sends nothing to the PLC. Checks the target, compiles the PLC software and returns a 'confirmToken' valid for 15 minutes (none if the compile has errors). Show the preview to the user and get an explicit yes before calling 'Download' with the token")]
        public static ResponseDownloadPreview PreviewDownload(
            [Description("softwarePath: defines the path in the project structure to the PLC software")] string softwarePath,
            [Description("mode: connection mode from 'GetConnectionTargets', e.g. 'PN/IE'")] string mode,
            [Description("pcInterface: PC interface name from 'GetConnectionTargets'")] string pcInterface,
            [Description("pcInterfaceNumber: PC interface number from 'GetConnectionTargets'")] int pcInterfaceNumber,
            [Description("targetInterface: target interface name from 'GetConnectionTargets', e.g. '1 X1'")] string targetInterface,
            [Description("options: what to download - 'SoftwareOnlyChanges' (default), 'Software', 'Hardware', or 'Hardware, Software'")] string options = "SoftwareOnlyChanges")
        {
            PlantPolicy.EnsureDownload(nameof(PreviewDownload));

            return RunPlantTool(nameof(PreviewDownload), () =>
            {
                var preview = Portal.PreviewDownload(softwarePath, mode, pcInterface, pcInterfaceNumber, targetInterface, options);
                var ready = preview.ConfirmToken != null;

                return new ResponseDownloadPreview
                {
                    Message = ready
                        ? $"Ready to download {preview.Options} of '{preview.PlcName}' via {preview.Target}. Ask the user to approve; then call 'Download' with confirmToken '{preview.ConfirmToken}'. " +
                          "The download stops the CPU or re-initialises data blocks only if 'Download' is called with allowStop / allowReinitialize; otherwise TIA Portal aborts when it needs either."
                        : $"Not ready: the compile of '{softwarePath}' has {preview.CompileErrors} error(s). Fix them with 'CompilePlcSoftware' and preview again.",
                    Preview = preview,
                    Meta = ready ? Ok() : new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false }
                };
            });
        }

        [DownloadTool]
        [McpServerTool(Name = "Download", Title = "Download to a PLC", Destructive = true, Idempotent = false, OpenWorld = true, UseStructuredContent = true),
         Description("Step 2: download to the real PLC with the settings from 'PreviewDownload'. Only call after the user explicitly approved this download. 'allowStop' lets TIA Portal stop the CPU (it is started again afterwards); 'allowReinitialize' lets it reset data block values. Without them TIA Portal aborts when it needs either. Every question TIA Portal asked, and the answer given, is returned")]
        public static ResponseDownload Download(
            [Description("confirmToken: the token from 'PreviewDownload'; single use")] string confirmToken,
            [Description("allowStop: true only if the user approved stopping the CPU for this download")] bool allowStop = false,
            [Description("allowReinitialize: true only if the user approved re-initialising data blocks (start values replace actual values)")] bool allowReinitialize = false)
        {
            PlantPolicy.EnsureDownload(nameof(Download));

            return RunPlantTool(nameof(Download), () =>
            {
                var outcome = Portal.Download(confirmToken, allowStop, allowReinitialize);
                var failed = string.Equals(outcome.State, "Error", StringComparison.OrdinalIgnoreCase);

                return new ResponseDownload
                {
                    Message = failed
                        ? $"Download to '{outcome.PlcName}' failed with {outcome.ErrorCount} error(s); see 'Messages' and 'Decisions' (Blocking answers are what TIA Portal needed but was not allowed)."
                        : $"Download to '{outcome.PlcName}' finished: {outcome.State}, {outcome.WarningCount} warning(s). Verify the change on the plant (event log DB91).",
                    Outcome = outcome,
                    Meta = failed ? new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false } : Ok()
                };
            });
        }

        /// <summary>Maps PortalException to McpException with its message, everything else with context.</summary>
        private static T RunPlantTool<T>(string toolName, Func<T> body)
        {
            try
            {
                return body();
            }
            catch (TiaMcpServer.Siemens.PortalException pex)
            {
                throw new McpException(pex.Message, pex);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error in '{toolName}': {ex.Message}", ex);
            }
        }
    }
}
