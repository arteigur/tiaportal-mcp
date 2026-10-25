using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Linq;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        // Hardware: catalog search and free plug locations (read-only, always registered), and
        // adding devices and modules ([HardwareTool]: needs '--allow-write --allow-hardware').
        //
        // Callers: registered through Program.BuildTools(). Data: structuredContent responses; no
        // files. Additions only: changing or deleting hardware and network settings stay with the
        // human in the TIA Portal UI.

        [McpServerTool(Name = "SearchHardwareCatalog", Title = "Search the hardware catalog", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
         Description("Search the TIA Portal hardware catalog by article number or name and return each entry's typeIdentifier, the value 'CreateDevice' and 'PlugModule' take")]
        public static ResponseHardwareCatalog SearchHardwareCatalog(
            [Description("filter: article number such as '6ES7 231-5PD32-0XB0' or text such as 'SM 1231'")] string filter,
            [Description("maxResults: maximum number of entries (default 25)")] int maxResults = 25)
        {
            return RunPlantTool(nameof(SearchHardwareCatalog), () =>
            {
                var entries = Portal.SearchHardwareCatalog(filter, maxResults)
                    .Select(e => new ResponseCatalogEntry
                    {
                        TypeIdentifier = e.TypeIdentifier,
                        ArticleNumber = e.ArticleNumber,
                        TypeName = e.TypeName,
                        Version = e.Version,
                        Description = e.Description,
                        CatalogPath = e.CatalogPath
                    })
                    .ToList();

                return new ResponseHardwareCatalog
                {
                    Message = $"{entries.Count} catalog entr{(entries.Count == 1 ? "y" : "ies")} for '{filter}'",
                    Entries = entries,
                    Meta = Ok()
                };
            });
        }

        [McpServerTool(Name = "GetPlugLocations", Title = "List free plug locations", ReadOnly = true, OpenWorld = false, UseStructuredContent = true),
         Description("List the free plug positions of a rack or device item, e.g. 'S7-1200 station_1/Rack_0', before calling 'PlugModule'")]
        public static ResponsePlugLocations GetPlugLocations(
            [Description("parentPath: path of the device or device item (rack) as 'GetProjectTree' shows it")] string parentPath)
        {
            return RunPlantTool(nameof(GetPlugLocations), () =>
            {
                var (parent, locations) = Portal.GetPlugLocations(parentPath);

                return new ResponsePlugLocations
                {
                    Message = $"{locations.Count} free position(s) on '{parent.Name}'",
                    Parent = parent.Name,
                    Locations = locations.Select(l => new ResponsePlugLocation { PositionNumber = l.Position, Label = l.Label }).ToList(),
                    Meta = Ok()
                };
            });
        }

        [HardwareTool]
        [McpServerTool(Name = "CreateDevice", Title = "Add a device", Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true),
         Description("Add a new device (station) with its main item, e.g. a CPU, to the project. Tell the user what you are adding before calling it. Does not set IP addresses or subnets")]
        public static ResponseCreated CreateDevice(
            [Description("typeIdentifier: from 'SearchHardwareCatalog', e.g. 'OrderNumber:6ES7 215-1AG40-0XB0/V4.6'")] string typeIdentifier,
            [Description("deviceItemName: name of the main item, e.g. 'PLC_2'")] string deviceItemName,
            [Description("deviceName: name of the station; empty lets TIA Portal choose")] string deviceName = "")
        {
            PlantPolicy.EnsureHardware(nameof(CreateDevice));

            return Guarded(nameof(CreateDevice), () =>
            {
                var device = Portal.CreateDevice(typeIdentifier, deviceItemName, deviceName);

                return Created("Device", device.Name, device.Name);
            });
        }

        [HardwareTool]
        [McpServerTool(Name = "PlugModule", Title = "Plug a module", Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true),
         Description("Plug a new module (signal module, communication module, ...) into a free position of a rack. Tell the user what you are adding before calling it. Use 'GetPlugLocations' for free positions")]
        public static ResponseCreated PlugModule(
            [Description("parentPath: rack or device item, e.g. 'S7-1200 station_1/Rack_0'")] string parentPath,
            [Description("typeIdentifier: from 'SearchHardwareCatalog', e.g. 'OrderNumber:6ES7 231-5PD32-0XB0/V2.0'")] string typeIdentifier,
            [Description("name: name of the new module")] string name,
            [Description("positionNumber: free slot from 'GetPlugLocations'")] int positionNumber)
        {
            PlantPolicy.EnsureHardware(nameof(PlugModule));

            return Guarded(nameof(PlugModule), () =>
            {
                var item = Portal.PlugModule(parentPath, typeIdentifier, name, positionNumber);

                return Created("Module", item.Name, $"{parentPath}/{item.Name}");
            });
        }
    }
}
