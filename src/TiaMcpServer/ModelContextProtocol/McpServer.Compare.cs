using ModelContextProtocol;
using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        // Compare tools: the open project against block files on disk, against another PLC
        // software, and against the PLC online.
        //
        // Callers: registered through Program.BuildTools() and invoked directly by the tests.
        // Data: return the Response*Compare* types as MCP structuredContent. The file compares
        // export into a scratch directory that is removed again and only read the given files.
        // None of the tools modifies the project, so none is a [WriteTool].
        // CompareSoftwareToOnline goes online and is an [OnlineTool] ('--allow-online').

        private const string CompareNormalisation =
            "SimaticML is compared after removing the XML declaration, <Engineering version>, <DocumentInfo> " +
            "(export time, installed products), every ID attribute and the block timestamps " +
            "(CreationDate, ModifiedDate, CompileDate and the like), and re-indenting. Source texts are " +
            "compared with line ends and trailing whitespace normalised. '-' lines are only in the file, '+' lines only in the project";

        [McpServerTool(Name = "CompareBlockToFile", Title = "Compare block to file", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
         Description("Export one block in the format of a file on disk (.xml SimaticML, .s7dcl/.s7res documents, .scl/.db/.awl source) and return a unified diff against it. " +
                     CompareNormalisation)]
        public static ResponseCompareBlockToFile CompareBlockToFile(
            [Description("softwarePath: defines the path in the project structure to the PLC software")] string softwarePath,
            [Description("filePath: absolute path of the block file to compare with; its extension picks the export format")] string filePath,
            [Description("blockPath: root-relative path of the block, e.g. '0_OBs/Main'. Default: the block named in the file (<Name> in SimaticML, else the file name)")] string? blockPath = null,
            [Description("ignoreWhitespace: for source texts, also ignore indentation and runs of blanks. Default false")] bool ignoreWhitespace = false,
            [Description("contextLines: unchanged lines shown around each change (default 3)")] int contextLines = 3,
            [Description("maxChars: truncate the diff at this many characters, on a line boundary (default 40000)")] int maxChars = 40000)
        {
            try
            {
                var result = Portal.CompareBlockToFile(softwarePath, filePath, blockPath, ignoreWhitespace, contextLines, maxChars);
                var response = ToResponse(result);

                response.Message = result.Identical == true
                    ? $"Block '{result.BlockPath}' is identical to '{filePath}'"
                    : $"Block '{result.BlockPath}' differs from '{filePath}': +{result.AddedLines} -{result.RemovedLines} lines in {result.Hunks} hunk(s)" +
                      (result.Truncated ? $" (diff truncated to {result.Diff?.Length} of {result.DiffChars} characters)" : string.Empty);
                response.Meta = Ok(new JsonObject
                {
                    ["identical"] = result.Identical,
                    ["changedLines"] = result.AddedLines + result.RemovedLines
                });

                return response;
            }
            catch (PortalException pex)
            {
                throw new McpException(pex.Message, pex);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error comparing '{filePath}': {ex.Message}", ex);
            }
        }

        [McpServerTool(Name = "CompareBlocksToFolder", Title = "Compare blocks to folder", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
         Description("Compare every block file in a folder (.xml, .s7dcl/.s7res, .scl/.db/.awl) with the project, like 'CompareBlockToFile'. " +
                     "Status per block: identical, different (with a short diff), missingInProject, missingOnDisk, skipped (an XML file that is no block export) or error. " +
                     "Normalisation as in 'CompareBlockToFile'")]
        public static ResponseCompareBlocksToFolder CompareBlocksToFolder(
            [Description("softwarePath: defines the path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: absolute path of the folder with the block files")] string folderPath,
            [Description("recursive: also compare the files in subfolders. Default false")] bool recursive = false,
            [Description("includeMissingOnDisk: also list project blocks that have no file in the folder. Default true")] bool includeMissingOnDisk = true,
            [Description("groupPath: only report blocks below this block group as missing on disk, e.g. 'Hydro'. Default: all blocks")] string? groupPath = null,
            [Description("ignoreWhitespace: for source texts, also ignore indentation and runs of blanks. Default false")] bool ignoreWhitespace = false,
            [Description("contextLines: unchanged lines shown around each change (default 3)")] int contextLines = 3,
            [Description("maxCharsPerBlock: truncate each block's diff at this many characters; 0 leaves the diffs out (default 2000)")] int maxCharsPerBlock = 2000)
        {
            try
            {
                var result = Portal.CompareBlocksToFolder(softwarePath, folderPath, recursive, groupPath, includeMissingOnDisk,
                    ignoreWhitespace, contextLines, maxCharsPerBlock);

                var items = result.Items.Select(ToResponse).ToList();
                int Count(string status) => result.Items.Count(i => i.Status == status);

                var response = new ResponseCompareBlocksToFolder
                {
                    Folder = result.Folder,
                    Identical = Count(TiaMcpServer.Siemens.Portal.CompareIdentical),
                    Different = Count(TiaMcpServer.Siemens.Portal.CompareDifferent),
                    MissingInProject = Count(TiaMcpServer.Siemens.Portal.CompareMissingInProject),
                    MissingOnDisk = Count(TiaMcpServer.Siemens.Portal.CompareMissingOnDisk),
                    Skipped = Count(TiaMcpServer.Siemens.Portal.CompareSkipped),
                    Errors = Count(TiaMcpServer.Siemens.Portal.CompareError),
                    Items = items
                };

                response.Message = $"Compared '{result.Folder}': {response.Identical} identical, {response.Different} different, " +
                                   $"{response.MissingInProject} missing in project, {response.MissingOnDisk} missing on disk, " +
                                   $"{response.Skipped} skipped, {response.Errors} error(s)";
                response.Meta = Ok(new JsonObject
                {
                    ["items"] = items.Count
                });

                return response;
            }
            catch (PortalException pex)
            {
                throw new McpException(pex.Message, pex);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error comparing the folder '{folderPath}': {ex.Message}", ex);
            }
        }

        [McpServerTool(Name = "CompareSoftware", Title = "Compare PLC software", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
         Description("Compare one PLC software with another offline (Openness PlcSoftware.CompareTo): in the same project, in another open project, or in a project file that is opened " +
                     "as a write-protected secondary project for the call. Returns the compare tree as a flat list of (path, state); identical elements are left out unless includeIdentical")]
        public static ResponseCompareSoftware CompareSoftware(
            [Description("softwarePath: the left PLC software, in the open project")] string softwarePath,
            [Description("targetSoftwarePath: the right PLC software, in the open project or in targetProjectPath")] string targetSoftwarePath,
            [Description("targetProjectPath: absolute path of the project (.apXX) that holds targetSoftwarePath. Default: the open project. A project that is not open is opened as a secondary project and closed afterwards; it is never upgraded or saved")] string? targetProjectPath = null,
            [Description("includeIdentical: also list identical and irrelevant elements. Default false")] bool includeIdentical = false,
            [Description("maxItems: return at most this many elements (default 500)")] int maxItems = 500)
        {
            try
            {
                var result = Portal.CompareSoftware(softwarePath, targetSoftwarePath, targetProjectPath, includeIdentical, maxItems);
                var response = ToResponse(result);

                response.Message = $"Compared '{result.LeftName}' with '{result.RightName}': {result.RootState}, " +
                                   $"{response.Items?.Count() ?? 0} of {result.Total} element(s) listed" +
                                   (result.Truncated ? " (truncated)" : string.Empty);

                return response;
            }
            catch (PortalException pex)
            {
                throw new McpException(pex.Message, pex);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error comparing '{softwarePath}' with '{targetSoftwarePath}': {ex.Message}", ex);
            }
        }

        [OnlineTool]
        [McpServerTool(Name = "CompareSoftwareToOnline", Title = "Compare PLC software to online", ReadOnly = true, OpenWorld = true, UseStructuredContent = true),
         Description("Compare one PLC software with the real PLC online (Openness PlcSoftware.CompareToOnline) over the online access configured in the project. " +
                     "Goes online but never downloads or writes values; a PLC that was offline before is taken offline again. Returns the compare tree as in 'CompareSoftware' and the online state before and after")]
        public static ResponseCompareSoftware CompareSoftwareToOnline(
            [Description("softwarePath: defines the path in the project structure to the PLC software")] string softwarePath,
            [Description("includeIdentical: also list identical and irrelevant elements. Default false")] bool includeIdentical = false,
            [Description("maxItems: return at most this many elements (default 500)")] int maxItems = 500,
            [Description("goOfflineAfter: take the PLC offline again when it was not online before the call. Default true")] bool goOfflineAfter = true)
        {
            OnlinePolicy.EnsureEnabled(nameof(CompareSoftwareToOnline));

            try
            {
                var result = Portal.CompareSoftwareToOnline(softwarePath, includeIdentical, maxItems, goOfflineAfter);
                var response = ToResponse(result);

                response.Message = $"Compared '{result.LeftName}' with '{result.RightName}': {result.RootState}, " +
                                   $"{response.Items?.Count() ?? 0} of {result.Total} element(s) listed" +
                                   (result.Truncated ? " (truncated)" : string.Empty) +
                                   $"; online state {result.OnlineStateBefore ?? "?"} -> {result.OnlineStateAfterCompare ?? "?"} -> {result.OnlineStateFinal ?? "?"}";

                return response;
            }
            catch (PortalException pex)
            {
                throw new McpException(pex.Message, pex);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error comparing '{softwarePath}' with the PLC online: {ex.Message}", ex);
            }
        }

        private static ResponseCompareBlockToFile ToResponse(BlockCompareResult result)
        {
            var compared = result.Status == TiaMcpServer.Siemens.Portal.CompareIdentical
                           || result.Status == TiaMcpServer.Siemens.Portal.CompareDifferent;

            return new ResponseCompareBlockToFile
            {
                Message = result.Message,
                Name = result.Name,
                BlockPath = result.BlockPath,
                Format = result.Format,
                Status = result.Status,
                Identical = compared ? result.Identical : null,
                AddedLines = compared ? result.AddedLines : null,
                RemovedLines = compared ? result.RemovedLines : null,
                ChangedLines = compared ? result.AddedLines + result.RemovedLines : null,
                Hunks = compared ? result.Hunks : null,
                Diff = string.IsNullOrEmpty(result.Diff) ? null : result.Diff,
                DiffChars = compared ? result.DiffChars : null,
                Truncated = compared ? result.Truncated : null,
                Files = result.Files.Count > 0 ? result.Files : null,
                Notes = result.Notes.Count > 0 ? result.Notes : null
            };
        }

        private static ResponseCompareSoftware ToResponse(SoftwareCompareResult result)
        {
            return new ResponseCompareSoftware
            {
                Left = result.LeftName,
                Right = result.RightName,
                RootState = result.RootState,
                Identical = result.Identical,
                Total = result.Total,
                StateCounts = result.StateCounts,
                Truncated = result.Truncated,
                TargetOpenedAsSecondary = result.TargetOpenedAsSecondary ? true : null,
                OnlineStateBefore = result.OnlineStateBefore,
                OnlineStateAfterCompare = result.OnlineStateAfterCompare,
                OnlineStateFinal = result.OnlineStateFinal,
                Items = result.Items.Select(i => new ResponseCompareSoftwareItem
                {
                    Path = i.Path,
                    LeftName = i.LeftName,
                    RightName = i.RightName,
                    State = i.State,
                    Detail = i.Detail,
                    Depth = i.Depth
                }).ToList(),
                Meta = Ok(new JsonObject
                {
                    ["identical"] = result.Identical,
                    ["total"] = result.Total
                })
            };
        }
    }
}
