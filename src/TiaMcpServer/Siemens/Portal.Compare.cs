using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Compare;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // Comparing the open project with files on disk, with another PLC software, and with the
        // PLC online.
        //
        // Callers: the Compare* tools in McpServer.Compare.cs. Affected API: none existing - every
        // member here is new. ExportXmlBlock, ExportSourceBlock and ExportAsDocuments are reused
        // unchanged to produce the project side of a file comparison.
        //
        // File I/O: the project side is exported into a scratch directory under the temp path
        // (SourceScope) and removed again; the files on disk are only read. PlcSoftware.CompareTo
        // and CompareToOnline only read. None of these modify the project, so none is a write tool.
        // CompareSoftwareToOnline connects to the PLC; it never downloads or writes values, and it
        // takes the PLC offline again when it was offline before.

        #region file compare

        public const string CompareIdentical = "identical";
        public const string CompareDifferent = "different";
        public const string CompareMissingInProject = "missingInProject";
        public const string CompareMissingOnDisk = "missingOnDisk";
        public const string CompareSkipped = "skipped";
        public const string CompareError = "error";

        /// <summary>The comparison format for a file extension, or null when it is not a block file.</summary>
        public static string? CompareFormatOf(string filePath)
        {
            switch (Path.GetExtension(filePath).ToLowerInvariant())
            {
                case ".xml":
                    return FormatXml;
                case ".s7dcl":
                case ".s7res":
                    return FormatDocuments;
                case ".scl":
                case ".db":
                case ".awl":
                    return FormatSource;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Exports one block in the format of <paramref name="filePath"/> and diffs it against
        /// that file. Without <paramref name="blockPath"/> the block is found by name: the
        /// &lt;Name&gt; inside a SimaticML file, otherwise the file name.
        /// </summary>
        public BlockCompareResult CompareBlockToFile(string softwarePath, string filePath, string? blockPath = null,
            bool ignoreWhitespace = false, int contextLines = 3, int maxChars = 40000)
        {
            return Operation.Run(_logger, nameof(CompareBlockToFile), PortalErrorCode.ExportFailed,
                () =>
                {
                    if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    {
                        throw new PortalException(PortalErrorCode.NotFound, $"File not found: '{filePath}'.");
                    }

                    if (maxChars <= 0)
                    {
                        throw new PortalException(PortalErrorCode.InvalidParams, "'maxChars' must be greater than zero.");
                    }

                    var format = CompareFormatOf(filePath)
                        ?? throw new PortalException(PortalErrorCode.InvalidParams,
                            $"'{Path.GetExtension(filePath)}' is not a block file. Use .xml (SimaticML), .s7dcl/.s7res (source documents) or .scl/.db/.awl (external sources).");

                    GetPlcSoftwareOrThrow(softwarePath);

                    var blocks = GetBlocks(softwarePath);
                    PlcBlock? block;
                    string? reason = null;

                    if (!string.IsNullOrWhiteSpace(blockPath))
                    {
                        block = GetBlock(softwarePath, blockPath!)
                            ?? throw new PortalException(PortalErrorCode.NotFound,
                                $"Block not found at '{blockPath}'. Use 'ResolveObjectPath' or 'GetBlocks' to find its path.");
                    }
                    else
                    {
                        block = FindBlockForFile(blocks, filePath, format, out reason);

                        if (block == null)
                        {
                            throw new PortalException(PortalErrorCode.NotFound,
                                reason ?? $"No block in '{softwarePath}' matches '{Path.GetFileName(filePath)}'. Pass 'blockPath' to name it.");
                        }
                    }

                    var result = CompareBlockFiles(softwarePath, block, format, DiskFilesFor(Path.GetFullPath(filePath), format), ignoreWhitespace, contextLines);

                    (result.Diff, result.Truncated) = CompareText.Truncate(result.Diff ?? string.Empty, maxChars);

                    return result;
                },
                ("softwarePath", softwarePath), ("filePath", filePath), ("blockPath", blockPath));
        }

        /// <summary>
        /// Compares every block file in a folder with the project. Documents of one block
        /// (.s7dcl and .s7res) count as one entry. Blocks below <paramref name="groupPath"/> that
        /// have no file are reported as missing on disk when <paramref name="includeMissingOnDisk"/> is set.
        /// </summary>
        public FolderCompareResult CompareBlocksToFolder(string softwarePath, string folderPath, bool recursive = false,
            string? groupPath = null, bool includeMissingOnDisk = true, bool ignoreWhitespace = false,
            int contextLines = 3, int maxCharsPerBlock = 2000)
        {
            return Operation.Run(_logger, nameof(CompareBlocksToFolder), PortalErrorCode.ExportFailed,
                () =>
                {
                    if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                    {
                        throw new PortalException(PortalErrorCode.NotFound, $"Folder not found: '{folderPath}'.");
                    }

                    GetPlcSoftwareOrThrow(softwarePath);

                    var blocks = GetBlocks(softwarePath);
                    var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var result = new FolderCompareResult { Folder = Path.GetFullPath(folderPath) };

                    var files = Directory
                        .GetFiles(folderPath, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                        .Where(f => CompareFormatOf(f) != null)
                        .Select(Path.GetFullPath)
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // One entry per SimaticML / source file; one per block for documents.
                    var entries = files
                        .GroupBy(f => CompareFormatOf(f) == FormatDocuments
                            ? Path.Combine(Path.GetDirectoryName(f) ?? string.Empty, Path.GetFileNameWithoutExtension(f)) + "|documents"
                            : f, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.OrderBy(f => Path.GetExtension(f).Equals(".s7dcl", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList())
                        .ToList();

                    foreach (var entryFiles in entries)
                    {
                        var primary = entryFiles[0];
                        var format = CompareFormatOf(primary)!;
                        BlockCompareResult item;

                        var block = FindBlockForFile(blocks, primary, format, out var reason);

                        if (block == null)
                        {
                            item = new BlockCompareResult
                            {
                                Name = reason != null && reason.StartsWith("not a program block", StringComparison.Ordinal)
                                    ? Path.GetFileNameWithoutExtension(primary)
                                    : NameFromFile(primary, format) ?? Path.GetFileNameWithoutExtension(primary),
                                Format = format,
                                Status = reason != null && reason.StartsWith("not a program block", StringComparison.Ordinal)
                                    ? CompareSkipped
                                    : CompareMissingInProject,
                                Message = reason,
                                Files = entryFiles
                            };
                        }
                        else
                        {
                            matched.Add(GetBlockPath(block));

                            try
                            {
                                item = CompareBlockFiles(softwarePath, block, format, entryFiles, ignoreWhitespace, contextLines);
                                (item.Diff, item.Truncated) = maxCharsPerBlock > 0
                                    ? CompareText.Truncate(item.Diff ?? string.Empty, maxCharsPerBlock)
                                    : (null, !string.IsNullOrEmpty(item.Diff));
                            }
                            catch (Exception ex)
                            {
                                item = new BlockCompareResult
                                {
                                    Name = block.Name,
                                    BlockPath = GetBlockPath(block),
                                    Format = format,
                                    Status = CompareError,
                                    Message = ex.Message,
                                    Files = entryFiles
                                };
                            }
                        }

                        result.Items.Add(item);
                    }

                    if (includeMissingOnDisk)
                    {
                        var prefix = NormalizeGroupPath(groupPath ?? string.Empty);

                        foreach (var block in blocks)
                        {
                            var path = GetBlockPath(block);

                            if (matched.Contains(path))
                            {
                                continue;
                            }

                            if (prefix.Length > 0 && !path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            result.Items.Add(new BlockCompareResult
                            {
                                Name = block.Name,
                                BlockPath = path,
                                Status = CompareMissingOnDisk
                            });
                        }
                    }

                    return result;
                },
                ("softwarePath", softwarePath), ("folderPath", folderPath));
        }

        // The disk files that belong to one comparison: the file itself and, for source
        // documents, the other half (.s7dcl / .s7res) when it exists next to it.
        private static List<string> DiskFilesFor(string filePath, string format)
        {
            var files = new List<string> { filePath };

            if (format == FormatDocuments)
            {
                var other = Path.ChangeExtension(filePath,
                    Path.GetExtension(filePath).Equals(".s7dcl", StringComparison.OrdinalIgnoreCase) ? ".s7res" : ".s7dcl");

                if (File.Exists(other))
                {
                    files.Add(other);
                }

                files = files.OrderBy(f => Path.GetExtension(f).Equals(".s7dcl", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            }

            return files;
        }

        /// <summary>
        /// Exports <paramref name="block"/> in <paramref name="format"/> into a scratch directory
        /// and diffs each disk file against the exported file with the same extension.
        /// </summary>
        private BlockCompareResult CompareBlockFiles(string softwarePath, PlcBlock block, string format, List<string> diskFiles,
            bool ignoreWhitespace, int contextLines)
        {
            var blockPath = GetBlockPath(block);

            if (block.IsKnowHowProtected)
            {
                throw new PortalException(PortalErrorCode.NotSupported, $"Block '{block.Name}' is know-how protected; TIA Portal does not export its code.");
            }

            if (!block.IsConsistent)
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    $"Block '{block.Name}' is inconsistent; TIA Portal cannot export it. Compile the software first.");
            }

            var result = new BlockCompareResult
            {
                Name = block.Name,
                BlockPath = blockPath,
                Format = format,
                Files = diskFiles
            };

            using (var scope = new SourceScope(_logger))
            {
                switch (format)
                {
                    case FormatXml:
                        ExportXmlBlock(softwarePath, blockPath, scope.Directory);
                        break;
                    case FormatSource:
                        ExportSourceBlock(softwarePath, blockPath, scope.Directory);
                        break;
                    case FormatDocuments:
                        if (!ExportAsDocuments(softwarePath, blockPath, scope.Directory))
                        {
                            throw new PortalException(PortalErrorCode.ExportFailed, $"TIA Portal did not export '{blockPath}' as source documents.");
                        }
                        break;
                }

                var exported = Directory.GetFiles(scope.Directory, "*.*", SearchOption.AllDirectories);
                var diffs = new List<string>();

                foreach (var diskFile in diskFiles)
                {
                    var extension = Path.GetExtension(diskFile);
                    var candidates = exported.Where(f => Path.GetExtension(f).Equals(extension, StringComparison.OrdinalIgnoreCase)).ToList();

                    // A source file's extension on disk may not be the one TIA Portal generates
                    // (a data block saved as '.scl'); there is only one source file, so use it.
                    if (candidates.Count == 0 && format == FormatSource && exported.Length == 1)
                    {
                        candidates.Add(exported[0]);
                        result.Notes.Add($"TIA Portal generates '{Path.GetExtension(exported[0])}' for this block; compared with '{Path.GetFileName(diskFile)}' anyway.");
                    }
                    var projectFile = candidates.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(FileNames.Safe(block.Name), StringComparison.OrdinalIgnoreCase))
                        ?? candidates.FirstOrDefault();

                    string projectText;
                    if (projectFile == null)
                    {
                        // e.g. a .db file for a block that exports as .scl, or no .s7res for a block without texts
                        result.Notes.Add($"The project export has no '{extension}' file for '{block.Name}'; compared against an empty text.");
                        projectText = string.Empty;
                    }
                    else
                    {
                        projectText = Normalize(format, SourceFileEncoding.Decode(File.ReadAllBytes(projectFile)), ignoreWhitespace);
                    }

                    var diskText = Normalize(format, SourceFileEncoding.Decode(File.ReadAllBytes(diskFile)), ignoreWhitespace);

                    var diff = CompareText.Diff(diskText, projectText,
                        $"file: {Path.GetFileName(diskFile)}",
                        $"project: {blockPath}{(diskFiles.Count > 1 ? extension : string.Empty)}",
                        contextLines);

                    result.AddedLines += diff.AddedLines;
                    result.RemovedLines += diff.RemovedLines;
                    result.Hunks += diff.Hunks;

                    if (!diff.Identical)
                    {
                        diffs.Add(diff.UnifiedDiff);
                    }
                }

                if (format == FormatDocuments)
                {
                    foreach (var extra in exported.Where(f => !diskFiles.Any(d => Path.GetExtension(d).Equals(Path.GetExtension(f), StringComparison.OrdinalIgnoreCase))))
                    {
                        result.Notes.Add($"The project export also has '{Path.GetFileName(extra)}', which has no counterpart on disk and was not compared.");
                    }
                }

                result.Identical = result.AddedLines == 0 && result.RemovedLines == 0;
                result.Status = result.Identical == true ? CompareIdentical : CompareDifferent;
                result.Diff = string.Join(string.Empty, diffs);
                result.DiffChars = result.Diff.Length;
            }

            return result;
        }

        private static string Normalize(string format, string text, bool ignoreWhitespace)
        {
            if (format == FormatXml)
            {
                try
                {
                    return CompareText.NormalizeSimaticMl(text);
                }
                catch (XmlException ex)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, $"The file is not valid XML: {ex.Message}", null, ex);
                }
            }

            return CompareText.NormalizeSourceText(text, ignoreWhitespace);
        }

        /// <summary>
        /// The project block a file was exported from, by name. Returns null with a reason when
        /// the file is no block export or no block of that name exists.
        /// </summary>
        private PlcBlock? FindBlockForFile(List<PlcBlock> blocks, string filePath, string format, out string? reason)
        {
            reason = null;
            string? name;

            if (format == FormatXml)
            {
                string? objectType;
                try
                {
                    (objectType, name) = ReadSimaticMlObject(filePath);
                }
                catch (Exception ex) when (ex is XmlException || ex is IOException)
                {
                    reason = $"not a program block: the file cannot be read as XML ({ex.Message})";
                    return null;
                }

                if (objectType == null || !objectType.StartsWith("SW.Blocks.", StringComparison.Ordinal))
                {
                    reason = $"not a program block export ({objectType ?? "no SimaticML object"})";
                    return null;
                }
            }
            else
            {
                name = Path.GetFileNameWithoutExtension(filePath);
            }

            if (string.IsNullOrEmpty(name))
            {
                reason = $"'{Path.GetFileName(filePath)}' names no block";
                return null;
            }

            var block = blocks.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? blocks.FirstOrDefault(b => string.Equals(FileNames.Safe(b.Name), name, StringComparison.OrdinalIgnoreCase));

            if (block == null)
            {
                reason = $"No block named '{name}' in the project.";
            }

            return block;
        }

        private static string? NameFromFile(string filePath, string format)
        {
            if (format != FormatXml)
            {
                return Path.GetFileNameWithoutExtension(filePath);
            }

            try
            {
                return ReadSimaticMlObject(filePath).Name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The element name (e.g. 'SW.Blocks.FB') and &lt;Name&gt; of the first object in a SimaticML file.</summary>
        private static (string? ObjectType, string? Name) ReadSimaticMlObject(string filePath)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true };

            using (var reader = XmlReader.Create(filePath, settings))
            {
                string? objectType = null;

                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    if (objectType == null && reader.Depth == 1 && reader.LocalName != "Engineering" && reader.LocalName != "DocumentInfo")
                    {
                        objectType = reader.LocalName;
                    }
                    else if (objectType != null && reader.Depth == 3 && reader.LocalName == "Name")
                    {
                        return (objectType, reader.ReadElementContentAsString());
                    }
                    else if (objectType != null && reader.Depth <= 1)
                    {
                        break;
                    }
                }

                return (objectType, null);
            }
        }

        #endregion

        #region software compare

        /// <summary>
        /// Compares one PLC software with another (PlcSoftware.CompareTo). The target is a PLC
        /// software of the open project, or of the project at <paramref name="targetProjectPath"/>:
        /// an already open one is used as it is, otherwise it is opened as a secondary
        /// (reference) project and closed again afterwards unless <paramref name="keepTargetOpen"/>.
        /// </summary>
        public SoftwareCompareResult CompareSoftware(string softwarePath, string targetSoftwarePath, string? targetProjectPath = null,
            bool includeIdentical = false, int maxItems = 500, bool keepTargetOpen = false)
        {
            return Operation.Run(_logger, nameof(CompareSoftware), PortalErrorCode.NotSupported,
                () =>
                {
                    var left = GetPlcSoftwareOrThrow(softwarePath);

                    Project? openedHere = null;
                    var targetProjectName = _project?.Name;

                    try
                    {
                        PlcSoftware right;

                        if (string.IsNullOrWhiteSpace(targetProjectPath))
                        {
                            right = GetPlcSoftwareOrThrow(targetSoftwarePath);
                        }
                        else
                        {
                            // By path only: a copy of the open project has the same name and must not be
                            // mistaken for it.
                            var fullPath = Path.GetFullPath(targetProjectPath);
                            var targetProject = GetProjects().FirstOrDefault(p => HasPath(p, fullPath));

                            if (targetProject == null)
                            {
                                if (!File.Exists(fullPath))
                                {
                                    throw new PortalException(PortalErrorCode.NotFound, $"Project file not found: '{targetProjectPath}'.");
                                }

                                // Secondary: opened next to the primary project, which stays as it is.
                                // Open, not OpenWithUpgrade: an upgrade would rewrite the file on disk.
                                try
                                {
                                    openedHere = _portal!.Projects.Open(new FileInfo(fullPath), null, ProjectOpenMode.Secondary);
                                }
                                catch (Exception ex) when (ex is not PortalException)
                                {
                                    throw new PortalException(PortalErrorCode.InvalidState,
                                        $"TIA Portal could not open '{fullPath}' as a secondary project: {ex.Message}", null, ex);
                                }
                                targetProject = openedHere;
                            }

                            targetProjectName = openedHere != null ? $"{targetProject.Name} ({fullPath})" : targetProject.Name;

                            right = GetSoftwareContainer(targetProject, targetSoftwarePath)?.Software as PlcSoftware
                                ?? throw new PortalException(PortalErrorCode.NotFound,
                                    $"No PLC software found at '{targetSoftwarePath}' in project '{targetProject.Name}'.");
                        }

                        CompareResult compare;
                        try
                        {
                            compare = left.CompareTo(right);
                        }
                        catch (Exception ex) when (ex is not PortalException)
                        {
                            throw new PortalException(PortalErrorCode.NotSupported, $"PlcSoftware.CompareTo failed: {ex.Message}", null, ex);
                        }

                        var result = Flatten(compare, includeIdentical, maxItems);
                        result.LeftName = $"{_project?.Name}/{softwarePath}";
                        result.RightName = $"{targetProjectName}/{targetSoftwarePath}";
                        result.TargetOpenedAsSecondary = openedHere != null;

                        return result;
                    }
                    finally
                    {
                        if (openedHere != null && !keepTargetOpen)
                        {
                            try
                            {
                                openedHere.Close();
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Could not close secondary project '{Path}'", targetProjectPath);
                            }
                        }
                    }
                },
                ("softwarePath", softwarePath), ("targetSoftwarePath", targetSoftwarePath), ("targetProjectPath", targetProjectPath));
        }

        /// <summary>
        /// Compares one PLC software with the PLC online (PlcSoftware.CompareToOnline), over the
        /// online access configured in the project. CompareToOnline needs an online connection,
        /// so a PLC that is offline is taken online first (OnlineProvider.GoOnline) and offline
        /// again afterwards. Read-only towards the PLC: nothing is downloaded and no value is written.
        /// </summary>
        public SoftwareCompareResult CompareSoftwareToOnline(string softwarePath, bool includeIdentical = false, int maxItems = 500,
            bool goOfflineAfter = true)
        {
            return Operation.Run(_logger, nameof(CompareSoftwareToOnline), PortalErrorCode.NotSupported,
                () =>
                {
                    var plc = GetPlcSoftwareOrThrow(softwarePath);
                    var deviceItem = FindDeviceItem(GetSoftwareContainer(softwarePath), plc);
                    var provider = deviceItem?.GetService<OnlineProvider>()
                        ?? throw new PortalException(PortalErrorCode.NotSupported,
                            $"'{softwarePath}' offers no online access (OnlineProvider) in this project.");

                    var before = TryGetOnlineState(provider);
                    var wentOnline = false;
                    string? after = null;
                    string? final = null;
                    SoftwareCompareResult result;

                    try
                    {
                        if (before != nameof(OnlineState.Online))
                        {
                            OnlineState state;
                            try
                            {
                                wentOnline = true;
                                state = provider.GoOnline();
                            }
                            catch (Exception ex) when (ex is not PortalException)
                            {
                                throw new PortalException(PortalErrorCode.InvalidState,
                                    $"Could not go online to '{softwarePath}' over the online access configured in the project: {ex.Message}", null, ex);
                            }

                            if (state != OnlineState.Online)
                            {
                                throw new PortalException(PortalErrorCode.InvalidState,
                                    $"Could not go online to '{softwarePath}': the online state is {state}. Check that the PLC is reachable from this PC.");
                            }
                        }

                        CompareResult compare;
                        try
                        {
                            compare = plc.CompareToOnline();
                        }
                        catch (Exception ex) when (ex is not PortalException)
                        {
                            throw new PortalException(PortalErrorCode.NotSupported, $"PlcSoftware.CompareToOnline failed: {ex.Message}", null, ex);
                        }

                        result = Flatten(compare, includeIdentical, maxItems);
                    }
                    finally
                    {
                        after = TryGetOnlineState(provider);
                        final = after;

                        if (goOfflineAfter && wentOnline && after != nameof(OnlineState.Offline))
                        {
                            try
                            {
                                provider.GoOffline();
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Could not take '{SoftwarePath}' offline after the online compare", softwarePath);
                            }

                            final = TryGetOnlineState(provider);
                        }
                    }

                    result.LeftName = $"{_project?.Name}/{softwarePath} (offline)";
                    result.RightName = $"{deviceItem?.Name ?? softwarePath} (online)";
                    result.OnlineStateBefore = before;
                    result.OnlineStateAfterCompare = after;
                    result.OnlineStateFinal = final;

                    return result;
                },
                ("softwarePath", softwarePath));
        }

        private static string? TryGetOnlineState(OnlineProvider? provider)
        {
            try
            {
                return provider?.State.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private SoftwareContainer? GetSoftwareContainer(ProjectBase project, string softwarePath)
        {
            var segments = softwarePath.Split('/');

            return (project.Devices != null ? GetSoftwareContainerInDevices(project.Devices, segments, 0) : null)
                ?? (project.DeviceGroups != null ? GetSoftwareContainerInGroups(project.DeviceGroups, segments, 0) : null);
        }

        /// <summary>
        /// The CompareResult tree as a flat list in tree order. Paths join the element names
        /// (left name, or right name when the left side is missing) with '/'.
        /// </summary>
        private static SoftwareCompareResult Flatten(CompareResult compare, bool includeIdentical, int maxItems)
        {
            var result = new SoftwareCompareResult();
            var root = compare.RootElement;

            if (root == null)
            {
                return result;
            }

            result.RootState = root.ComparisonResult.ToString();

            void Walk(CompareResultElement element, string parentPath, int depth)
            {
                var name = !string.IsNullOrEmpty(element.LeftName) ? element.LeftName : element.RightName;
                var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
                var state = element.ComparisonResult;

                result.Total++;
                result.StateCounts[state.ToString()] = (result.StateCounts.TryGetValue(state.ToString(), out var n) ? n : 0) + 1;

                var isIdentical = state == CompareResultState.ObjectsIdentical
                                  || state == CompareResultState.FolderContentsIdentical
                                  || state == CompareResultState.CompareIrrelevant;

                if (includeIdentical || !isIdentical)
                {
                    if (result.Items.Count < maxItems)
                    {
                        result.Items.Add(new SoftwareCompareItem
                        {
                            Path = path,
                            LeftName = element.LeftName,
                            RightName = element.RightName,
                            State = state.ToString(),
                            Detail = string.IsNullOrWhiteSpace(element.DetailedInformation) ? null : element.DetailedInformation.Trim(),
                            Depth = depth
                        });
                    }
                    else
                    {
                        result.Truncated = true;
                    }
                }

                foreach (var child in element.Elements)
                {
                    Walk(child, path, depth + 1);
                }
            }

            Walk(root, string.Empty, 0);

            result.Identical = root.ComparisonResult == CompareResultState.ObjectsIdentical
                               || root.ComparisonResult == CompareResultState.FolderContentsIdentical;

            return result;
        }

        #endregion
    }

    public class BlockCompareResult
    {
        public string Name { get; set; } = string.Empty;
        public string? BlockPath { get; set; }
        public string? Format { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool? Identical { get; set; }
        public int AddedLines { get; set; }
        public int RemovedLines { get; set; }
        public int Hunks { get; set; }
        public string? Diff { get; set; }
        public int DiffChars { get; set; }
        public bool Truncated { get; set; }
        public string? Message { get; set; }
        public List<string> Files { get; set; } = new List<string>();
        public List<string> Notes { get; set; } = new List<string>();
    }

    public class FolderCompareResult
    {
        public string Folder { get; set; } = string.Empty;
        public List<BlockCompareResult> Items { get; set; } = new List<BlockCompareResult>();
    }

    public class SoftwareCompareItem
    {
        public string Path { get; set; } = string.Empty;
        public string? LeftName { get; set; }
        public string? RightName { get; set; }
        public string State { get; set; } = string.Empty;
        public string? Detail { get; set; }
        public int Depth { get; set; }
    }

    public class SoftwareCompareResult
    {
        public string? LeftName { get; set; }
        public string? RightName { get; set; }
        public string? RootState { get; set; }
        public bool Identical { get; set; }
        public int Total { get; set; }
        public bool Truncated { get; set; }
        public bool TargetOpenedAsSecondary { get; set; }
        public string? OnlineStateBefore { get; set; }
        public string? OnlineStateAfterCompare { get; set; }
        public string? OnlineStateFinal { get; set; }
        public Dictionary<string, int> StateCounts { get; set; } = new Dictionary<string, int>();
        public List<SoftwareCompareItem> Items { get; set; } = new List<SoftwareCompareItem>();
    }
}
