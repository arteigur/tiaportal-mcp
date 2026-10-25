using ModelContextProtocol.Server;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        // Session context in one call: replaces GetState + GetProjectTree + GetSoftwareTree +
        // GetOnlineState for an agent that is planning its next steps.
        //
        // Callers: registered through Program.BuildTools(). Data: ResponseSessionContext as
        // structuredContent. Read-only; never connects to a PLC and never compiles.

        [McpServerTool(Name = "GetSessionContext", Title = "Get session context", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
         Description("Start here. One call returns the attached portal, open project and unsaved changes, every PLC with its softwarePath, CPU type, online state and last compile result, which gated tool sets are enabled, and the server presets")]
        public static ResponseSessionContext GetSessionContext()
        {
            return RunPlantTool(nameof(GetSessionContext), () =>
            {
                var context = Portal.GetSessionContext();

                var message = context.ProjectName == null
                    ? (context.IsConnected ? "Connected, no project open. Use 'OpenTiaProject'." : "Not connected. Use 'Connect' or 'OpenTiaProject'.")
                    : $"Project '{context.ProjectName}' with {context.Plcs.Count} PLC software(s)" +
                      (context.HasUnsavedChanges ? "; unsaved changes, 'SaveProject' persists them" : "; no unsaved changes");

                return new ResponseSessionContext
                {
                    Message = message,
                    Context = context,
                    AllowWrite = WritePolicy.AllowWrite,
                    AllowOnline = PlantPolicy.AllowOnline,
                    AllowDownload = PlantPolicy.AllowDownload,
                    AllowHardware = PlantPolicy.AllowHardware,
                    Presets = ToolDefaults.Presets.ToDictionary(p => p.Key, p => p.Value.ToString()),
                    Meta = Ok()
                };
            });
        }
    }
}
