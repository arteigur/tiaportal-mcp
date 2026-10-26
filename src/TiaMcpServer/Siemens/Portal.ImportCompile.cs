using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // Import one or more block files and compile, undoing the import when the compile has
        // errors.
        //
        // Callers: the ImportAndCompile tool in McpServer.ImportCompile.cs. Affected API: the
        // existing ImportXmlBlock / ImportFromDocuments / ImportSourceBlock, CompilePlcSoftware,
        // PlcBlock.Export / Import / Delete. File I/O: backups of the blocks the import replaces
        // go to a temp folder that is deleted afterwards.
        //
        // Why not one transaction: TIA Portal does not compile inside an Openness transaction,
        // and the compile result is what decides. So the rollback is explicit - every block the
        // files will replace is exported first; on compile errors the blocks the import created
        // are deleted and the backups are imported back.

        private static readonly Regex SourceBlockName = new(
            @"^\s*(FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK)\s+""([^""]+)""",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

        public ImportCompileResult ImportAndCompile(string softwarePath, string groupPath, IReadOnlyList<string> importPaths, bool rollbackOnError)
        {
            return Operation.Run(_logger, nameof(ImportAndCompile), PortalErrorCode.ImportFailed,
                () =>
                {
                    if (importPaths == null || importPaths.Count == 0)
                    {
                        throw new PortalException(PortalErrorCode.InvalidParams, "Pass at least one file in 'importPaths'.");
                    }

                    foreach (var path in importPaths)
                    {
                        if (!File.Exists(path))
                        {
                            throw new PortalException(PortalErrorCode.InvalidParams,
                                $"Import file '{path}' does not exist on the machine running this server.");
                        }
                    }

                    var result = new ImportCompileResult();
                    var before = GetBlocks(softwarePath).ToDictionary(b => b.Name, b => b.ModifiedDate, StringComparer.OrdinalIgnoreCase);
                    var backupDir = Path.Combine(Path.GetTempPath(), $"TiaMcp_ImportBackup_{Guid.NewGuid():N}");
                    var backups = new List<(PlcBlockGroup Group, string Name, string File)>();

                    try
                    {
                        if (rollbackOnError)
                        {
                            Directory.CreateDirectory(backupDir);

                            foreach (var name in importPaths.SelectMany(PredictBlockNames).Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                var existing = GetBlocks(softwarePath).FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

                                if (existing == null)
                                {
                                    continue;
                                }

                                var file = Path.Combine(backupDir, $"{FileNames.Safe(existing.Name)}.xml");

                                try
                                {
                                    existing.Export(new FileInfo(file), ExportOptions.None);
                                }
                                catch (Exception ex)
                                {
                                    throw new PortalException(PortalErrorCode.ImportFailed,
                                        $"Cannot back up '{existing.Name}' before replacing it ({ex.Message}), so the import could not be undone. " +
                                        "Compile the PLC software so the block is consistent, or pass rollbackOnError=false.", null, ex);
                                }

                                backups.Add(((PlcBlockGroup)existing.Parent, existing.Name, file));
                                result.BackedUp.Add(existing.Name);
                            }
                        }

                        // The imports themselves commit together or not at all.
                        InTransaction("MCP: ImportAndCompile", () =>
                        {
                            foreach (var path in importPaths)
                            {
                                ImportOneFile(softwarePath, groupPath, path);
                                result.Imported.Add(path);
                            }
                        });

                        var compile = CompilePlcSoftware(softwarePath)
                            ?? throw new PortalException(PortalErrorCode.ImportFailed,
                                $"The import succeeded but compiling '{softwarePath}' failed to run; nothing was rolled back.");

                        result.CompileState = compile.State.ToString();
                        result.CompileErrors = compile.ErrorCount;
                        result.CompileWarnings = compile.WarningCount;
                        result.Messages = FlattenMessages(compile.Messages, CompilerResultState.Error).Take(50).ToList();
                        result.Warnings = FlattenMessages(compile.Messages, CompilerResultState.Warning).Take(100).ToList();

                        if (compile.State != CompilerResultState.Error || !rollbackOnError)
                        {
                            return result;
                        }

                        _logger?.LogInformation("ImportAndCompile: compile failed with {Errors} error(s); rolling back", compile.ErrorCount);

                        var backedUp = new HashSet<string>(backups.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);

                        foreach (var block in GetBlocks(softwarePath).ToList())
                        {
                            if (!before.ContainsKey(block.Name))
                            {
                                result.RemovedOnRollback.Add(block.Name);
                                block.Delete();
                            }
                            else if (!backedUp.Contains(block.Name) && before[block.Name] != block.ModifiedDate)
                            {
                                // Replaced by the import but not predicted from the files: cannot be restored.
                                result.NotRestored.Add(block.Name);
                            }
                        }

                        foreach (var (group, name, file) in backups)
                        {
                            group.Blocks.Import(new FileInfo(file), ImportOptions.Override);
                            result.Restored.Add(name);
                        }

                        result.RolledBack = true;

                        return result;
                    }
                    finally
                    {
                        if (Directory.Exists(backupDir))
                        {
                            Directory.Delete(backupDir, recursive: true);
                        }
                    }
                },
                ("softwarePath", softwarePath), ("groupPath", groupPath));
        }

        /// <summary>The block names a file will create or replace, read from its content.</summary>
        private static IEnumerable<string> PredictBlockNames(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".xml":
                    return XDocument.Load(path).Root?.Elements()
                        .Where(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal))
                        .Select(e => e.Element("AttributeList")?.Element("Name")?.Value)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select(n => n!)
                        .ToList() ?? new List<string>();

                case ".s7dcl":
                    return new[] { Path.GetFileNameWithoutExtension(path) };

                default:
                    return SourceBlockName.Matches(File.ReadAllText(path)).Cast<Match>().Select(m => m.Groups[2].Value).ToList();
            }
        }

        private void ImportOneFile(string softwarePath, string groupPath, string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();

            switch (extension)
            {
                case ".xml":
                    ImportXmlBlock(softwarePath, groupPath, path);
                    break;

                case ".s7dcl":
                    if (!ImportFromDocuments(softwarePath, groupPath, Path.GetDirectoryName(path) ?? string.Empty,
                            Path.GetFileNameWithoutExtension(path), ImportDocumentOptions.Override))
                    {
                        throw new PortalException(PortalErrorCode.ImportFailed, $"Import of document '{path}' failed.");
                    }
                    break;

                case ".scl":
                case ".db":
                case ".awl":
                    var imported = ImportSourceBlock(softwarePath, groupPath, path);
                    if (imported.Failures.Count > 0)
                    {
                        throw new PortalException(PortalErrorCode.ImportFailed,
                            $"Import of source '{path}' failed: {string.Join("; ", imported.Failures)}");
                    }
                    break;

                default:
                    throw new PortalException(PortalErrorCode.InvalidParams,
                        $"Cannot import '{path}': use .xml (SimaticML), .s7dcl (document) or .scl/.db/.awl (external source).");
            }
        }

        /// <summary>
        /// The texts of the compiler messages in one state ('path: description'), depth first.
        /// Only messages with a description count: TIA Portal also marks the parent nodes (the
        /// PLC, 'Program blocks', a block) with the worst state of their children.
        /// </summary>
        private static IEnumerable<string> FlattenMessages(CompilerResultMessageComposition? messages, CompilerResultState state)
        {
            if (messages == null)
            {
                yield break;
            }

            foreach (CompilerResultMessage message in messages)
            {
                if (message.State == state && !string.IsNullOrWhiteSpace(message.Description))
                {
                    yield return string.IsNullOrWhiteSpace(message.Path) ? message.Description : $"{message.Path}: {message.Description}";
                }

                foreach (var child in FlattenMessages(message.Messages, state))
                {
                    yield return child;
                }
            }
        }
    }

    public class ImportCompileResult
    {
        public List<string> Imported { get; set; } = new List<string>();
        public string CompileState { get; set; } = string.Empty;
        public int CompileErrors { get; set; }
        public int CompileWarnings { get; set; }
        /// <summary>Compiler error messages, at most 50.</summary>
        public List<string> Messages { get; set; } = new List<string>();
        /// <summary>Compiler warning messages, at most 100. CompilePlcSoftware with rebuildAll lists them for blocks compiled earlier.</summary>
        public List<string> Warnings { get; set; } = new List<string>();
        /// <summary>True when the compile had errors and the import was undone.</summary>
        public bool RolledBack { get; set; }
        /// <summary>Existing blocks exported before the import, to restore on rollback.</summary>
        public List<string> BackedUp { get; set; } = new List<string>();
        /// <summary>Blocks the import created, deleted again on rollback.</summary>
        public List<string> RemovedOnRollback { get; set; } = new List<string>();
        /// <summary>Backed-up blocks imported back on rollback.</summary>
        public List<string> Restored { get; set; } = new List<string>();
        /// <summary>Blocks the import changed that were not backed up; check them by hand.</summary>
        public List<string> NotRestored { get; set; } = new List<string>();
    }
}
