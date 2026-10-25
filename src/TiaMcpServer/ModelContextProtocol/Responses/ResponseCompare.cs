using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>One block compared with its file(s) on disk.</summary>
    public class ResponseCompareBlockToFile : ResponseMessage
    {
        public string? Name { get; set; }

        /// <summary>Root-relative block path in the project; empty when the block is missing in the project.</summary>
        public string? BlockPath { get; set; }

        /// <summary>xml, source or documents - chosen by the file extension.</summary>
        public string? Format { get; set; }

        /// <summary>identical, different, missingInProject, missingOnDisk, skipped or error.</summary>
        public string? Status { get; set; }

        public bool? Identical { get; set; }

        /// <summary>Lines only in the project export ('+' in the diff).</summary>
        public int? AddedLines { get; set; }

        /// <summary>Lines only in the file on disk ('-' in the diff).</summary>
        public int? RemovedLines { get; set; }

        /// <summary>AddedLines + RemovedLines.</summary>
        public int? ChangedLines { get; set; }

        public int? Hunks { get; set; }

        /// <summary>Unified diff after normalisation; '-' is the file on disk, '+' the project.</summary>
        public string? Diff { get; set; }

        /// <summary>Length of the full diff before truncation.</summary>
        public int? DiffChars { get; set; }

        public bool? Truncated { get; set; }

        public IEnumerable<string>? Files { get; set; }

        public IEnumerable<string>? Notes { get; set; }
    }

    public class ResponseCompareBlocksToFolder : ResponseMessage
    {
        public string? Folder { get; set; }

        public int? Identical { get; set; }

        public int? Different { get; set; }

        public int? MissingInProject { get; set; }

        public int? MissingOnDisk { get; set; }

        public int? Skipped { get; set; }

        public int? Errors { get; set; }

        public IEnumerable<ResponseCompareBlockToFile>? Items { get; set; }
    }

    public class ResponseCompareSoftwareItem
    {
        /// <summary>Element names from the compare root, joined with '/'.</summary>
        public string? Path { get; set; }

        public string? LeftName { get; set; }

        public string? RightName { get; set; }

        /// <summary>CompareResultState, e.g. ObjectsDifferent, LeftMissing, RightMissing, FolderContentsDifferent.</summary>
        public string? State { get; set; }

        public string? Detail { get; set; }

        public int? Depth { get; set; }
    }

    public class ResponseCompareSoftware : ResponseMessage
    {
        public string? Left { get; set; }

        public string? Right { get; set; }

        /// <summary>CompareResultState of the root element.</summary>
        public string? RootState { get; set; }

        public bool? Identical { get; set; }

        /// <summary>Number of elements in the whole compare tree, identical ones included.</summary>
        public int? Total { get; set; }

        /// <summary>Element count per CompareResultState, over the whole tree.</summary>
        public Dictionary<string, int>? StateCounts { get; set; }

        public bool? Truncated { get; set; }

        /// <summary>CompareSoftware: the target project was opened as a secondary project for this call.</summary>
        public bool? TargetOpenedAsSecondary { get; set; }

        /// <summary>CompareSoftwareToOnline: OnlineProvider.State before the compare.</summary>
        public string? OnlineStateBefore { get; set; }

        /// <summary>CompareSoftwareToOnline: OnlineProvider.State right after the compare.</summary>
        public string? OnlineStateAfterCompare { get; set; }

        /// <summary>CompareSoftwareToOnline: OnlineProvider.State when the tool returned.</summary>
        public string? OnlineStateFinal { get; set; }

        public IEnumerable<ResponseCompareSoftwareItem>? Items { get; set; }
    }
}
