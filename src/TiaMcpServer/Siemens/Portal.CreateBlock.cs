using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // Create an empty OB, FC or global DB.
        //
        // Callers: the CreateBlock tool in McpServer.CreateBlock.cs. Affected API: Openness can
        // only create FBs and instance DBs directly (PlcBlockComposition.CreateFB /
        // CreateInstanceDB), so the other kinds are written as a minimal SimaticML document to a
        // temp file and imported with ImportOptions.None - which refuses to replace an existing
        // block. The temp file is deleted afterwards.

        // OB event classes: the SimaticML SecondaryType (the names TIA Portal V21 accepts), the
        // usual numbers, and the start information TIA Portal gives an optimized OB of that class
        // on an S7-1200 / S7-1500. ProgrammingError (OB121) and IOAccessError (OB122) exist on
        // the S7-1500 only and are left out.
        private static readonly Dictionary<string, (int[] Preferred, string Inputs)> ObTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["ProgramCycle"] = (new[] { 1 }, Member("Initial_Call", "Bool") + Member("Remanence", "Bool")),
                ["Startup"] = (new[] { 100 }, Member("LostRetentive", "Bool") + Member("LostRTC", "Bool")),
                ["TimeOfDay"] = (new[] { 10, 11, 12, 13, 14, 15, 16, 17 }, Member("CaughtUp", "Bool") + Member("SecondTimes", "Bool")),
                ["TimeDelayInterrupt"] = (new[] { 20, 21, 22, 23 }, Member("Sign", "Word")),
                ["CyclicInterrupt"] = (new[] { 30, 31, 32, 33, 34, 35, 36, 37, 38 }, Member("Initial_Call", "Bool") + Member("Event_Count", "Int")),
                ["HardwareInterrupt"] = (new[] { 40, 41, 42, 43, 44, 45, 46, 47 },
                    Member("LADDR", "HW_IO") + Member("USI", "Word") + Member("IChannel", "USInt") + Member("EventType", "Byte")),
                ["TimeErrorInterrupt"] = (new[] { 80 }, Member("fault_id", "Byte") + Member("csg_OBnr", "OB_ANY") + Member("csg_prio", "UInt")),
                ["DiagnosticErrorInterrupt"] = (new[] { 82 },
                    Member("IO_state", "Word") + Member("laddr", "HW_ANY") + Member("channel", "UInt") + Member("multi_error", "Bool")),
                ["PullOrPlugOfModules"] = (new[] { 83 }, Member("LADDR", "HW_IO") + Member("Event_Class", "Byte") + Member("Fault_ID", "Byte")),
                ["RackOrStationFailure"] = (new[] { 86 }, Member("LADDR", "HW_IO") + Member("Event_Class", "Byte") + Member("Fault_ID", "Byte"))
            };

        // Short names agents reach for, mapped to the SimaticML name.
        private static readonly Dictionary<string, string> ObTypeAliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Cyclic"] = "CyclicInterrupt",
                ["TimeError"] = "TimeErrorInterrupt",
                ["DiagnosticError"] = "DiagnosticErrorInterrupt",
                ["Diagnostic"] = "DiagnosticErrorInterrupt",
                ["PullPlug"] = "PullOrPlugOfModules",
                ["PullOrPlug"] = "PullOrPlugOfModules",
                ["RackFailure"] = "RackOrStationFailure",
                ["RackStationFailure"] = "RackOrStationFailure",
                ["TimeOfDayInterrupt"] = "TimeOfDay",
                ["TimeDelay"] = "TimeDelayInterrupt",
                ["Hardware"] = "HardwareInterrupt",
                ["HardwareProcessInterrupt"] = "HardwareInterrupt"
            };

        /// <summary>Highest cycle time of a cyclic interrupt OB, in ms (S7-1200 and S7-1500: 1 ms to 60 s).</summary>
        private const int MaxCyclicTimeMs = 60000;

        public PlcBlock CreateBlock(string softwarePath, string groupPath, string blockType, string name, int number, string language, string obType, int cyclicTimeMs)
        {
            return Operation.Run(_logger, nameof(CreateBlock), PortalErrorCode.CreateFailed,
                () =>
                {
                    EnsureValidBlockName(name);

                    var group = GetPlcBlockGroupByPath(softwarePath, groupPath)
                        ?? throw new PortalException(PortalErrorCode.NotFound, $"Block group not found at '{groupPath}'.");

                    var existing = GetBlocks(softwarePath);

                    if (existing.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new PortalException(PortalErrorCode.InvalidParams, $"A block named '{name}' already exists in '{softwarePath}'.");
                    }

                    var kind = blockType.Trim().ToUpperInvariant();
                    string xml;
                    int chosen;

                    switch (kind)
                    {
                        case "OB":
                            var requested = string.IsNullOrWhiteSpace(obType) ? "ProgramCycle" : obType.Trim();

                            if (ObTypeAliases.TryGetValue(requested, out var alias))
                            {
                                requested = alias;
                            }

                            if (!ObTypes.TryGetValue(requested, out var ob))
                            {
                                throw new PortalException(PortalErrorCode.InvalidParams,
                                    $"Unknown OB type '{obType}'. Use one of: {string.Join(", ", ObTypes.Keys)}.");
                            }

                            var obTypeName = ObTypes.Keys.First(k => string.Equals(k, requested, StringComparison.OrdinalIgnoreCase));

                            if (cyclicTimeMs != 0 && obTypeName != "CyclicInterrupt")
                            {
                                throw new PortalException(PortalErrorCode.InvalidParams,
                                    $"'cyclicTimeMs' applies to a CyclicInterrupt OB only, not to '{obTypeName}'.");
                            }

                            if (cyclicTimeMs < 0 || cyclicTimeMs > MaxCyclicTimeMs)
                            {
                                throw new PortalException(PortalErrorCode.InvalidParams,
                                    $"'cyclicTimeMs' must be between 1 and {MaxCyclicTimeMs} ms, or 0 for TIA Portal's default (100 ms).");
                            }

                            chosen = ChooseNumber(number, existing.OfType<OB>().Select(b => b.Number), ob.Preferred);
                            xml = BlockXml("SW.Blocks.OB", name, chosen, CodeLanguage(language),
                                Section("Input", ob.Inputs) + Section("Temp") + Section("Constant"),
                                $"<SecondaryType>{obTypeName}</SecondaryType>");
                            break;

                        case "FC":
                            chosen = ChooseNumber(number, existing.OfType<FC>().Select(b => b.Number), Array.Empty<int>());
                            xml = BlockXml("SW.Blocks.FC", name, chosen, CodeLanguage(language),
                                Section("Input") + Section("Output") + Section("InOut") + Section("Temp") + Section("Constant") +
                                Section("Return", Member("Ret_Val", "Void", informative: false)),
                                string.Empty);
                            break;

                        case "GLOBALDB":
                        case "DB":
                            chosen = ChooseNumber(number, existing.OfType<DataBlock>().Select(b => b.Number), Array.Empty<int>());
                            xml = BlockXml("SW.Blocks.GlobalDB", name, chosen, "DB", Section("Static"), string.Empty);
                            break;

                        default:
                            throw new PortalException(PortalErrorCode.InvalidParams,
                                $"Unknown block type '{blockType}'. Use OB, FC or GlobalDB; FBs and instance DBs have 'CreateFB' and 'CreateInstanceDB'.");
                    }

                    var file = Path.Combine(Path.GetTempPath(), $"TiaMcp_CreateBlock_{Guid.NewGuid():N}.xml");

                    try
                    {
                        File.WriteAllText(file, xml, new UTF8Encoding(false));

                        PlcBlock block;

                        try
                        {
                            block = group.Blocks.Import(new FileInfo(file), ImportOptions.None).FirstOrDefault()
                                ?? throw new PortalException(PortalErrorCode.CreateFailed, $"TIA Portal created no block for '{name}'.");
                        }
                        catch (Exception ex) when (ex is not PortalException)
                        {
                            throw new PortalException(PortalErrorCode.CreateFailed,
                                $"Creating {kind} '{name}' failed: {ImportErrors.Describe(ex)}", null, ex);
                        }

                        if (kind == "OB" && cyclicTimeMs > 0)
                        {
                            SetCyclicTime(block, name, cyclicTimeMs);
                        }

                        return block;
                    }
                    finally
                    {
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                        }
                    }
                },
                ("softwarePath", softwarePath), ("groupPath", groupPath), ("name", name), ("blockType", blockType));
        }

        /// <summary>
        /// Sets the cycle time of a freshly created cyclic interrupt OB. The OB attribute
        /// 'CyclicTime' is in milliseconds in TIA Portal V21 (a 100 ms OB30 reads back 100); the
        /// earlier code passed microseconds, which TIA Portal rejects as out of range. The value is
        /// converted to the attribute's own type. On failure the OB is deleted again, so a failed
        /// call does not leave an OB with the wrong cycle time behind.
        /// </summary>
        private static void SetCyclicTime(PlcBlock block, string name, int cyclicTimeMs)
        {
            try
            {
                var current = block.GetAttribute("CyclicTime");
                var value = current == null ? cyclicTimeMs : Convert.ChangeType(cyclicTimeMs, current.GetType());

                block.SetAttribute("CyclicTime", value);
            }
            catch (Exception ex)
            {
                try
                {
                    block.Delete();
                }
                catch (Exception)
                {
                    // Reported below: the caller has to remove the OB by hand.
                    throw new PortalException(PortalErrorCode.CreateFailed,
                        $"OB '{name}' was created but setting its cycle time to {cyclicTimeMs} ms failed, and deleting it again failed too; " +
                        $"set the cycle time in TIA Portal or delete the OB: {ImportErrors.Describe(ex)}", null, ex);
                }

                throw new PortalException(PortalErrorCode.CreateFailed,
                    $"Setting the cycle time of OB '{name}' to {cyclicTimeMs} ms failed, so the OB was not created: {ImportErrors.Describe(ex)}", null, ex);
            }
        }

        private static void EnsureValidBlockName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '"', '\\' }) >= 0)
            {
                throw new PortalException(PortalErrorCode.InvalidParams, $"'{name}' is not a valid block name.");
            }
        }

        /// <summary>The requested number, or the first free preferred number, or the first free number from 1 (OBs: from 123).</summary>
        private static int ChooseNumber(int requested, IEnumerable<int> used, int[] preferred)
        {
            var taken = new HashSet<int>(used);

            if (requested > 0)
            {
                if (taken.Contains(requested))
                {
                    throw new PortalException(PortalErrorCode.InvalidParams, $"Number {requested} is already used.");
                }

                return requested;
            }

            foreach (var candidate in preferred)
            {
                if (!taken.Contains(candidate))
                {
                    return candidate;
                }
            }

            var start = preferred.Length > 0 ? 123 : 1;

            for (var candidate = start; ; candidate++)
            {
                if (!taken.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        private static string CodeLanguage(string language)
        {
            var value = string.IsNullOrWhiteSpace(language) ? "SCL" : language.Trim().ToUpperInvariant();

            if (value != "SCL" && value != "LAD" && value != "FBD" && value != "STL")
            {
                throw new PortalException(PortalErrorCode.InvalidParams, $"Unknown language '{language}'. Use SCL, LAD, FBD or STL.");
            }

            return value;
        }

        private static string Member(string name, string dataType, bool informative = true) =>
            $"<Member Name=\"{name}\" Datatype=\"{dataType}\"{(informative ? " Informative=\"true\"" : string.Empty)} />";

        private static string Section(string name, string members = "") =>
            members.Length == 0 ? $"<Section Name=\"{name}\" />" : $"<Section Name=\"{name}\">{members}</Section>";

        private static string BlockXml(string element, string name, int number, string language, string sections, string extraAttributes)
        {
            return
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<Document>\n" +
                $"  <Engineering version=\"V{Engineering.TiaMajorVersion}\" />\n" +
                $"  <{element} ID=\"0\">\n" +
                "    <AttributeList>\n" +
                $"      <Interface><Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v5\">{sections}</Sections></Interface>\n" +
                "      <MemoryLayout>Optimized</MemoryLayout>\n" +
                $"      <Name>{SecurityElement.Escape(name)}</Name>\n" +
                "      <Namespace />\n" +
                $"      <Number>{number}</Number>\n" +
                $"      <ProgrammingLanguage>{language}</ProgrammingLanguage>\n" +
                (extraAttributes.Length > 0 ? $"      {extraAttributes}\n" : string.Empty) +
                "    </AttributeList>\n" +
                // An SCL block needs one compile unit; LAD / FBD / STL and DBs may have none.
                (language == "SCL"
                    ? "    <ObjectList>\n" +
                      "      <SW.Blocks.CompileUnit ID=\"1\" CompositionName=\"CompileUnits\">\n" +
                      "        <AttributeList>\n" +
                      "          <NetworkSource><StructuredText xmlns=\"http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4\"><NewLine Num=\"1\" UId=\"21\" /></StructuredText></NetworkSource>\n" +
                      "          <ProgrammingLanguage>SCL</ProgrammingLanguage>\n" +
                      "        </AttributeList>\n" +
                      "      </SW.Blocks.CompileUnit>\n" +
                      "    </ObjectList>\n"
                    : string.Empty) +
                $"  </{element}>\n" +
                "</Document>\n";
        }
    }
}
