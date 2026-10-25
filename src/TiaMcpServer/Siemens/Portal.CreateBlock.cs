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

        private static readonly Dictionary<string, (int[] Preferred, string Inputs)> ObTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["ProgramCycle"] = (new[] { 1 }, Member("Initial_Call", "Bool") + Member("Remanence", "Bool")),
                ["CyclicInterrupt"] = (new[] { 30, 31, 32, 33, 34, 35, 36, 37, 38 }, Member("Initial_Call", "Bool") + Member("Event_Count", "Int")),
                ["Startup"] = (new[] { 100 }, Member("LostRetentive", "Bool") + Member("LostRTC", "Bool"))
            };

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
                            if (!ObTypes.TryGetValue(string.IsNullOrWhiteSpace(obType) ? "ProgramCycle" : obType, out var ob))
                            {
                                throw new PortalException(PortalErrorCode.InvalidParams,
                                    $"Unknown OB type '{obType}'. Use one of: {string.Join(", ", ObTypes.Keys)}.");
                            }

                            var obTypeName = ObTypes.Keys.First(k => string.Equals(k, string.IsNullOrWhiteSpace(obType) ? "ProgramCycle" : obType, StringComparison.OrdinalIgnoreCase));
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
                            // Openness takes the cycle time in microseconds.
                            block.SetAttribute("CyclicTime", cyclicTimeMs * 1000);
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
