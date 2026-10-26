using ModelContextProtocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.Compiler;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        #region plc software

        [McpServerTool(Name = "GetSoftwareInfo", Title = "Get software info", ReadOnly = true, OpenWorld = false, UseStructuredContent = true), Description("Show name, description and attributes of one PLC software")]
        public static ResponseSoftwareInfo GetSoftwareInfo(
            [Description("softwarePath: defines the path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var software = Portal.GetPlcSoftware(softwarePath);
                if (software is not null)
                {

                    var attributes = Helper.GetAttributeList(software);

                    return new ResponseSoftwareInfo
                    {
                        Message = $"Software info retrieved from '{softwarePath}'",
                        Name = software.Name,
                        Attributes = attributes,
                        Description = software.ToString(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Software not found at '{softwarePath}'");
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving software info from '{softwarePath}': {ex.Message}", ex);
            }
        }

        [McpServerTool(Name = "CompilePlcSoftware", Title = "Compile PLC software", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true),
         Description("Compile the whole PLC software and report every compiler message with the object it belongs to, so errors can be fixed without re-reading the whole PLC. Warnings are reported as a successful compile with detail; errors set meta.success to false and the call still returns every message. A plain compile only compiles changes, so it reports no warnings for blocks compiled earlier; pass rebuildAll=true to compile every block and get all warnings. To compile a single block use 'CompileCodeBlock'")]
        public static ResponseCompile CompilePlcSoftware(
            [Description("softwarePath: defines the path in the project structure to the PLC software")] string softwarePath,
            [Description("password: the password to access adminsitration, default: no password")] string password = "",
            [Description("rebuildAll: compile every program block on its own and then the PLC software, so the messages of blocks compiled earlier are reported too (slower). Default false: compile changes only")] bool rebuildAll = false)
        {
            try
            {
                if (rebuildAll)
                {
                    var results = Portal.RebuildPlcSoftware(softwarePath, password);

                    if (results == null)
                    {
                        throw new McpException(
                            $"Failed rebuilding software '{softwarePath}'. Check that the path names a PLC software " +
                            "('GetProjectTree' lists them) and, for a safety program, that 'password' is correct.");
                    }

                    return BuildRebuildResponse(results, softwarePath);
                }

                var result = Portal.CompilePlcSoftware(softwarePath, password);

                if (result == null)
                {
                    throw new McpException(
                        $"Failed compiling software '{softwarePath}'. Check that the path names a PLC software " +
                        "('GetProjectTree' lists them) and, for a safety program, that 'password' is correct.");
                }

                return BuildCompileResponse(result, softwarePath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error compiling software '{softwarePath}': {ex.Message}", ex);
            }
        }

        [McpServerTool(Name = "CompileCodeBlock", Title = "Compile code block", Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true),
         Description("Compile a single code block (OB, FC, FB or DB) and report its compiler messages with the object each belongs to. Warnings are reported as a successful compile with detail; errors set meta.success to false and the call still returns every message. To compile the whole PLC use 'CompilePlcSoftware'")]
        public static ResponseCompile CompileCodeBlock(
            [Description("softwarePath: defines the path in the project structure to the PLC software")] string softwarePath,
            [Description("blockPath: defines the path in the project structure to the block, e.g. '0_OBs/Main_1'")] string blockPath,
            [Description("password: the password to access adminsitration, default: no password")] string password = "")
        {
            try
            {
                var result = Portal.CompileCodeBlock(softwarePath, blockPath, password);

                if (result == null)
                {
                    throw new McpException(
                        $"Failed compiling block '{blockPath}' in '{softwarePath}'. Check that the block exists " +
                        "('GetBlocks' lists them) and, for a safety program, that 'password' is correct.");
                }

                return BuildCompileResponse(result, blockPath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error compiling block '{blockPath}': {ex.Message}", ex);
            }
        }

        private static ResponseCompile BuildCompileResponse(CompilerResult result, string target)
        {
            var messages = FlattenCompilerMessages(result.Messages, 0).ToList();
            var state = result.State.ToString();
            var failed = result.State == CompilerResultState.Error;

            if (failed)
            {
                // The message text is what a client surfaces, so name the first few offenders
                // rather than making the caller re-query to find out what broke.
                var offenders = messages
                    .Where(m => string.Equals(m.State, nameof(CompilerResultState.Error), StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .Select(m => $"{m.Path}: {m.Description}");

                throw new McpException(
                    $"Compiling '{target}' failed with {result.ErrorCount} error(s). " +
                    string.Join(" | ", offenders));
            }

            return new ResponseCompile
            {
                Message = $"Compiling '{target}' finished with state '{state}': " +
                          $"{result.ErrorCount} error(s), {result.WarningCount} warning(s)",
                State = state,
                ErrorCount = result.ErrorCount,
                WarningCount = result.WarningCount,
                Messages = messages,
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = true,
                    ["state"] = state,
                    ["errorCount"] = result.ErrorCount,
                    ["warningCount"] = result.WarningCount,
                    ["messageCount"] = messages.Count
                }
            };
        }

        /// <summary>
        /// One response for a rebuild: the messages of every block compile and the final PLC
        /// software compile, each block's messages under a node naming the block. Counts are
        /// summed over the block compiles and the software compile.
        /// </summary>
        private static ResponseCompile BuildRebuildResponse(List<(string Target, CompilerResult Result)> results, string target)
        {
            var messages = new List<CompileMessage>();
            var errors = 0;
            var warnings = 0;
            var worst = CompilerResultState.Success;

            foreach (var (blockPath, result) in results)
            {
                errors += result.ErrorCount;
                warnings += result.WarningCount;

                if (Severity(result.State) > Severity(worst))
                {
                    worst = result.State;
                }

                // Blocks without messages would only add noise.
                if (result.ErrorCount == 0 && result.WarningCount == 0)
                {
                    continue;
                }

                messages.AddRange(FlattenCompilerMessages(result.Messages, 0));
            }

            var state = worst.ToString();

            if (worst == CompilerResultState.Error)
            {
                var offenders = messages
                    .Where(m => string.Equals(m.State, nameof(CompilerResultState.Error), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Description))
                    .Take(5)
                    .Select(m => $"{m.Path}: {m.Description}");

                throw new McpException(
                    $"Rebuilding '{target}' found {errors} error(s). " +
                    string.Join(" | ", offenders));
            }

            return new ResponseCompile
            {
                Message = $"Rebuilding '{target}' ({results.Count - 1} block(s), then the PLC software) finished with state '{state}': " +
                          $"{errors} error(s), {warnings} warning(s)",
                State = state,
                ErrorCount = errors,
                WarningCount = warnings,
                Messages = messages,
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = true,
                    ["state"] = state,
                    ["errorCount"] = errors,
                    ["warningCount"] = warnings,
                    ["messageCount"] = messages.Count,
                    ["rebuildAll"] = true
                }
            };
        }

        private static int Severity(CompilerResultState state) => state switch
        {
            CompilerResultState.Error => 3,
            CompilerResultState.Warning => 2,
            CompilerResultState.Information => 1,
            _ => 0
        };

        /// <summary>
        /// Walks the recursive CompilerResultMessage tree depth-first. Openness nests messages
        /// per object and then per detail, and the previous implementation discarded all of it
        /// in favour of CompilerResult.ToString().
        /// </summary>
        private static IEnumerable<CompileMessage> FlattenCompilerMessages(CompilerResultMessageComposition? messages, int depth)
        {
            if (messages == null)
            {
                yield break;
            }

            foreach (var message in messages)
            {
                if (message == null)
                {
                    continue;
                }

                yield return new CompileMessage
                {
                    Path = message.Path,
                    State = message.State.ToString(),
                    Description = message.Description,
                    ErrorCount = message.ErrorCount,
                    WarningCount = message.WarningCount,
                    Depth = depth
                };

                foreach (var child in FlattenCompilerMessages(message.Messages, depth + 1))
                {
                    yield return child;
                }
            }
        }

        #endregion
    }
}
