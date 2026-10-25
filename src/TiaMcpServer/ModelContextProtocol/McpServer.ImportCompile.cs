using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        // Import + compile with rollback on compile errors.
        //
        // Callers: registered through Program.BuildTools() under '--allow-write'. Data:
        // ResponseImportCompile as structuredContent. Reads the import files; the project change
        // is in memory (committed or rolled back), and still needs SaveProject.

        [WriteTool]
        [McpServerTool(Name = "ImportAndCompile", Title = "Import and compile, roll back on errors", Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true),
         Description("Import one or more block files (.xml SimaticML, .s7dcl document, .scl/.db/.awl source) into one block group, then compile the PLC software. If the compile has errors the import is undone - blocks it created are deleted and the blocks it replaced are restored from backups taken first - and the errors are returned. Go offline first")]
        public static ResponseImportCompile ImportAndCompile(
            [Description("softwarePath: defines the path in the project structure to the PLC software")] string softwarePath,
            [Description("groupPath: root-relative block group to import into; empty for the Program blocks root")] string groupPath,
            [Description("importPaths: absolute paths of the files to import, in import order (data types and DBs before the code that uses them); comma or newline separated")] string importPaths,
            [Description("rollbackOnError: undo the import when the compile has errors (default true)")] bool rollbackOnError = true)
        {
            WritePolicy.EnsureEnabled(nameof(ImportAndCompile));

            return RunPlantTool(nameof(ImportAndCompile), () =>
            {
                var paths = (importPaths ?? string.Empty)
                    .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim().Trim('"'))
                    .Where(p => p.Length > 0)
                    .ToList();

                var result = Portal.ImportAndCompile(softwarePath, groupPath ?? string.Empty, paths, rollbackOnError);
                var failed = result.CompileErrors > 0;

                return new ResponseImportCompile
                {
                    Message = result.RolledBack
                        ? $"Compile had {result.CompileErrors} error(s); the import of {result.Imported.Count} file(s) was rolled back. Fix the files and try again."
                        : failed
                            ? $"Imported {result.Imported.Count} file(s); compile has {result.CompileErrors} error(s) and the import was kept (rollbackOnError=false). {SaveHint}"
                            : $"Imported {result.Imported.Count} file(s) and compiled with {result.CompileWarnings} warning(s). {SaveHint}",
                    Result = result,
                    Meta = failed ? new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = false } : OkMeta()
                };
            });
        }
    }
}
