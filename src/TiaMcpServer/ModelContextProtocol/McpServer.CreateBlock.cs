using ModelContextProtocol.Server;
using System.ComponentModel;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        // Create an empty OB, FC or global DB (Openness creates only FB and instance DB natively).
        //
        // Callers: registered through Program.BuildTools() under '--allow-write'. Data:
        // ResponseCreated. Portal writes and deletes one temporary XML file per call.

        [WriteTool]
        [McpServerTool(Name = "CreateBlock", Title = "Create OB, FC or global DB", Destructive = true, OpenWorld = false, UseStructuredContent = true),
         Description("Create an empty organization block (program cycle, startup, time of day, time delay, cyclic, hardware, time error, diagnostic error, pull/plug or rack failure interrupt), function or global data block. Never replaces an existing block. For FBs and instance DBs use 'CreateFB' / 'CreateInstanceDB'")]
        public static ResponseCreated CreateBlock(
            [Description("softwarePath: defines the path in the project structure to the PLC software")] string softwarePath,
            [Description("groupPath: root-relative block group; empty for the Program blocks root")] string groupPath,
            [Description("blockType: OB, FC or GlobalDB")] string blockType,
            [Description("name: block name, without a slash")] string name,
            [Description("number: block number; 0 picks the first free one (OB: the usual number for its type, else from 123)")] int number = 0,
            [Description("language: SCL (default), LAD, FBD or STL; ignored for a GlobalDB")] string language = "SCL",
            [Description("obType: for an OB - ProgramCycle (default, OB1/123+), Startup (OB100), TimeOfDay (OB10-17), TimeDelayInterrupt (OB20-23), CyclicInterrupt (OB30-38), HardwareInterrupt (OB40-47), TimeErrorInterrupt (OB80), DiagnosticErrorInterrupt (OB82), PullOrPlugOfModules (OB83) or RackOrStationFailure (OB86)")] string obType = "ProgramCycle",
            [Description("cyclicTimeMs: for a CyclicInterrupt OB, the cycle time in milliseconds (1-60000); 0 keeps TIA Portal's default (100 ms)")] int cyclicTimeMs = 0)
        {
            WritePolicy.EnsureEnabled(nameof(CreateBlock));

            return Guarded(nameof(CreateBlock), () =>
            {
                var block = Portal.CreateBlock(softwarePath, groupPath ?? string.Empty, blockType, name, number, language, obType, cyclicTimeMs);

                return Created($"{blockType} {block.Number}", block.Name, Portal.GetBlockPath(block));
            });
        }
    }
}
