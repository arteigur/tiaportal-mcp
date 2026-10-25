using Siemens.Engineering.HW;
using Siemens.Engineering.HW.HardwareCatalog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        // Hardware catalog search, free plug locations, and adding devices and modules.
        //
        // Callers: the tools in McpServer.Hardware.cs. Affected API: TiaPortal.HardwareCatalog,
        // DeviceComposition.CreateWithItem, HardwareObject.GetPlugLocations / CanPlugNew /
        // PlugNew. Only additions: nothing here changes or deletes existing hardware, and network
        // settings (addresses, subnets, PROFINET names) are out of scope on purpose.

        public List<CatalogEntry> SearchHardwareCatalog(string filter, int maxResults)
        {
            return Operation.Run(_logger, nameof(SearchHardwareCatalog), PortalErrorCode.NotFound,
                () =>
                {
                    if (_portal == null)
                    {
                        throw new PortalException(PortalErrorCode.InvalidState, "Not connected to TIA Portal. Use 'Connect' first.");
                    }

                    if (string.IsNullOrWhiteSpace(filter))
                    {
                        throw new PortalException(PortalErrorCode.InvalidParams,
                            "Pass a filter, e.g. an article number '6ES7 231-5PD32-0XB0' or a name 'SM 1231'.");
                    }

                    return _portal.HardwareCatalog.Find(filter.Trim())
                        .Take(Math.Max(1, maxResults))
                        .ToList();
                },
                ("filter", filter));
        }

        public (HardwareObject Parent, List<(int Position, string Label)> Locations) GetPlugLocations(string parentPath)
        {
            return Operation.Run(_logger, nameof(GetPlugLocations), PortalErrorCode.NotFound,
                () =>
                {
                    var parent = GetHardwareObjectOrThrow(parentPath);

                    var locations = parent.GetPlugLocations()
                        .Select(l => (l.PositionNumber, l.Label ?? string.Empty))
                        .ToList();

                    return (parent, locations);
                },
                ("parentPath", parentPath));
        }

        /// <summary>
        /// Creates a device with its main item, e.g. a station with a CPU. typeIdentifier as the
        /// catalog reports it, 'OrderNumber:6ES7 215-1AG40-0XB0/V4.6'.
        /// </summary>
        public Device CreateDevice(string typeIdentifier, string deviceItemName, string deviceName)
        {
            return Operation.Run(_logger, nameof(CreateDevice), PortalErrorCode.HardwareFailed,
                () =>
                {
                    if (IsProjectNull())
                    {
                        throw new PortalException(PortalErrorCode.InvalidState, "No project is open. Open one with 'OpenProject' first.");
                    }

                    if (string.IsNullOrWhiteSpace(typeIdentifier) || string.IsNullOrWhiteSpace(deviceItemName))
                    {
                        throw new PortalException(PortalErrorCode.InvalidParams,
                            "Pass typeIdentifier (from 'SearchHardwareCatalog') and deviceItemName.");
                    }

                    var name = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;

                    if (name != null && EnumerateAllDevices().Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new PortalException(PortalErrorCode.InvalidParams, $"A device named '{name}' already exists.");
                    }

                    try
                    {
                        return _project!.Devices.CreateWithItem(typeIdentifier.Trim(), deviceItemName.Trim(), name);
                    }
                    catch (Exception ex) when (ex is not PortalException)
                    {
                        throw new PortalException(PortalErrorCode.HardwareFailed,
                            $"TIA Portal could not create '{typeIdentifier}': {ex.Message}", null, ex);
                    }
                },
                ("typeIdentifier", typeIdentifier), ("deviceItemName", deviceItemName));
        }

        /// <summary>Plugs a new module into a free position of a rack or device item.</summary>
        public DeviceItem PlugModule(string parentPath, string typeIdentifier, string name, int positionNumber)
        {
            return Operation.Run(_logger, nameof(PlugModule), PortalErrorCode.HardwareFailed,
                () =>
                {
                    var parent = GetHardwareObjectOrThrow(parentPath);

                    if (!parent.CanPlugNew(typeIdentifier, name, positionNumber))
                    {
                        var free = string.Join(", ", parent.GetPlugLocations().Select(l => l.PositionNumber));

                        throw new PortalException(PortalErrorCode.InvalidParams,
                            $"'{typeIdentifier}' cannot be plugged into '{parentPath}' at position {positionNumber}. " +
                            $"Free positions: {(free.Length > 0 ? free : "none")}. Check the module fits this rack (and firmware).");
                    }

                    try
                    {
                        return parent.PlugNew(typeIdentifier, name, positionNumber);
                    }
                    catch (Exception ex) when (ex is not PortalException)
                    {
                        throw new PortalException(PortalErrorCode.HardwareFailed,
                            $"TIA Portal could not plug '{typeIdentifier}' into '{parentPath}': {ex.Message}", null, ex);
                    }
                },
                ("parentPath", parentPath), ("typeIdentifier", typeIdentifier), ("positionNumber", positionNumber.ToString()));
        }

        private HardwareObject GetHardwareObjectOrThrow(string path)
        {
            return (HardwareObject?)GetDeviceItem(path) ?? GetDevice(path)
                ?? throw new PortalException(PortalErrorCode.NotFound,
                    $"No device or device item at '{path}'. 'GetProjectTree' shows them, e.g. 'S7-1200 station_1/Rack_0'.");
        }
    }
}
