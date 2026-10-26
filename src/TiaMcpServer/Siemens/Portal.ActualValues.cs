using Siemens.Engineering;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Blocks.Interface;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // Actual (live) values of one data block, read from the running PLC.
        //
        // Callers: GetDbActualValues in McpServer.Online.cs ('--allow-online'). Affected API: the
        // ValueService of the block's interface (CreateSnapshot) and the block's InterfaceSnapshot
        // service (Export), the same "snapshot of the monitored values" TIA Portal offers in the
        // DB editor. Openness V21 has no other way to read values from a PLC. Data: one SimaticML
        // file in a scratch directory that is removed again, unless the caller asks for a copy.
        //
        // ValueService also has LoadSnapshotAsActualValues and LoadStartValuesAsActualValues,
        // which write to the PLC. Neither is called here, and no tool exposes them.
        //
        // TIA Portal keeps the snapshot with the offline block (the DB editor's Snapshot column),
        // so each call replaces the block's previous snapshot. The block's ModifiedDate does not
        // change (checked on V21). Whether the project's IsModified flag is set could not be
        // checked on a clean project yet, so the result reports IsModified before and after.

        private const int MaxActualValues = 5000;

        public DbActualValues GetDbActualValues(string softwarePath, string blockPath, string? memberFilter, int maxItems, string? exportFile)
        {
            return Operation.Run(_logger, nameof(GetDbActualValues), PortalErrorCode.NotFound,
                () =>
                {
                    var block = GetBlock(softwarePath, blockPath)
                        ?? throw new PortalException(PortalErrorCode.NotFound,
                            $"Block not found at '{blockPath}'. Use 'ResolveObjectPath' or 'GetBlocks' to find its path.");

                    if (block is not DataBlock dataBlock)
                    {
                        throw new PortalException(PortalErrorCode.NotSupported,
                            $"'{block.Name}' is a {block.GetType().Name}; actual values can only be read from a data block (global or instance DB).");
                    }

                    var plc = GetPlcDeviceItemOrThrow(softwarePath);
                    var state = GetOnlineProviderOrThrow(plc, softwarePath).State;

                    if (state != OnlineState.Online)
                    {
                        throw new PortalException(PortalErrorCode.InvalidState,
                            $"'{plc.Name}' is {state}. Actual values come from the running PLC: call 'GoOnline' first, and 'GoOffline' when done.");
                    }

                    // V21 hangs ValueService on the block's interface; the snapshot it creates is
                    // then exported through the block's InterfaceSnapshot service.
                    var blockInterface = dataBlock.Interface;
                    var valueService = blockInterface?.GetService<ValueService>() ?? block.GetService<ValueService>()
                        ?? throw new PortalException(PortalErrorCode.NotSupported,
                            $"'{block.Name}' offers no value service (interface {(blockInterface == null ? "missing" : ServiceNames(blockInterface))}; block {ServiceNames(block)}). Know-how protected blocks hide their interface.");

                    var project = _project as ProjectBase;
                    var modifiedBefore = project?.IsModified;

                    valueService.CreateSnapshot();

                    var readAt = DateTime.Now;
                    var modifiedAfter = project?.IsModified;

                    var snapshot = block.GetService<InterfaceSnapshot>()
                        ?? throw new PortalException(PortalErrorCode.NotSupported,
                            $"TIA Portal took the snapshot of '{block.Name}' but exposes no InterfaceSnapshot to export it ({ServiceNames(block)}).");

                    var directory = CreateTempExportDirectory();

                    try
                    {
                        var file = new FileInfo(Path.Combine(directory, FileNames.Safe(block.Name) + ".snapshot.xml"));

                        // The snapshot values are read-only attributes: ExportOptions.None leaves
                        // them out and exports only the block name.
                        snapshot.Export(file, ExportOptions.WithDefaults | ExportOptions.WithReadOnly);

                        if (!string.IsNullOrWhiteSpace(exportFile))
                        {
                            var target = Path.GetFullPath(exportFile);
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            File.Copy(file.FullName, target, overwrite: true);
                        }

                        var document = XDocument.Load(file.FullName);
                        var values = ParseSnapshotValues(document);

                        if (values.Count == 0)
                        {
                            var content = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "SW.Blocks.InterfaceSnapshot") ?? document.Root;
                            var text = content?.ToString() ?? string.Empty;
                            throw new PortalException(PortalErrorCode.ExportFailed,
                                $"The snapshot of '{block.Name}' has no values this server can read (snapshot {AttributeNames(snapshot)}). SimaticML: " +
                                (text.Length > 2000 ? text.Substring(0, 2000) + " ..." : text));
                        }

                        var filtered = string.IsNullOrWhiteSpace(memberFilter)
                            ? values
                            : values.Where(v => v.Path.IndexOf(memberFilter!.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                        var limit = maxItems <= 0 ? 500 : Math.Min(maxItems, MaxActualValues);

                        return new DbActualValues
                        {
                            PlcName = plc.Name,
                            BlockName = block.Name,
                            BlockPath = blockPath,
                            OnlineState = state.ToString(),
                            SnapshotTimestamp = FindSnapshotTimestamp(document),
                            ReadAt = readAt,
                            Total = values.Count,
                            Matched = filtered.Count,
                            Truncated = filtered.Count > limit,
                            ProjectModifiedBefore = modifiedBefore,
                            ProjectModifiedAfter = modifiedAfter,
                            ExportFile = string.IsNullOrWhiteSpace(exportFile) ? null : Path.GetFullPath(exportFile),
                            Values = filtered.Take(limit).ToList()
                        };
                    }
                    finally
                    {
                        DeleteTempExportDirectory(directory);
                    }
                },
                ("softwarePath", softwarePath), ("blockPath", blockPath));
        }

        private static string ServiceNames(IEngineeringServiceProvider provider)
        {
            try
            {
                var names = provider.GetServiceInfos().Select(s => s.Type.Name).ToList();

                return names.Count == 0 ? "no services" : "services: " + string.Join(", ", names);
            }
            catch (Exception)
            {
                return "services unknown";
            }
        }

        private static string AttributeNames(IEngineeringObject engineeringObject)
        {
            try
            {
                var names = engineeringObject.GetAttributeInfos().Select(a => a.Name).ToList();

                return names.Count == 0 ? "has no attributes" : "attributes: " + string.Join(", ", names);
            }
            catch (Exception)
            {
                return "attributes unknown";
            }
        }

        /// <summary>
        /// Flattens the values of an exported snapshot. V21 writes them as
        /// Snapshot/SnapshotValues/Value elements, one per leaf member, with the member path
        /// (nested structs and array elements included) and the data type as attributes:
        /// &lt;Value Path="rFrequency" Type="Real"&gt;50.03101&lt;/Value&gt;.
        /// </summary>
        internal static List<DbActualValue> ParseSnapshotValues(XDocument document)
        {
            return document.Descendants()
                .Where(e => e.Name.LocalName == "Value" && e.Attribute("Path") != null &&
                            e.Parent?.Name.LocalName == "SnapshotValues")
                .Select(e => new DbActualValue
                {
                    Path = (string)e.Attribute("Path")!,
                    DataType = (string?)e.Attribute("Type"),
                    Value = e.Value
                })
                .ToList();
        }

        /// <summary>The time TIA Portal took the snapshot (the SnapshotDate attribute, UTC).</summary>
        private static string? FindSnapshotTimestamp(XDocument document)
        {
            return document.Descendants().FirstOrDefault(e => e.Name.LocalName == "SnapshotDate")?.Value;
        }
    }

    public class DbActualValues
    {
        public string PlcName { get; set; } = string.Empty;
        public string BlockName { get; set; } = string.Empty;
        public string BlockPath { get; set; } = string.Empty;
        public string OnlineState { get; set; } = string.Empty;
        /// <summary>The snapshot time from the SimaticML, when it has one.</summary>
        public string? SnapshotTimestamp { get; set; }
        /// <summary>Local time on this machine right after CreateSnapshot returned.</summary>
        public DateTime ReadAt { get; set; }
        public int Total { get; set; }
        public int Matched { get; set; }
        public bool Truncated { get; set; }
        public bool? ProjectModifiedBefore { get; set; }
        public bool? ProjectModifiedAfter { get; set; }
        public string? ExportFile { get; set; }
        public List<DbActualValue> Values { get; set; } = new List<DbActualValue>();
    }

    public class DbActualValue
    {
        /// <summary>Member path, e.g. 'Static_1.a' or 'Values[3]'.</summary>
        public string Path { get; set; } = string.Empty;
        public string? DataType { get; set; }
        public string? Value { get; set; }
    }
}
