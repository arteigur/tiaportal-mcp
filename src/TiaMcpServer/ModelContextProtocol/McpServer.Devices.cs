using ModelContextProtocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.HW;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public static partial class McpServer
    {
        #region devices

        [McpServerTool(Name = "GetDeviceInfo", Title = "Get device info", ReadOnly = true, OpenWorld = false, UseStructuredContent = true), Description("Show name, attributes and the device items (rack, CPU, modules) of one device, each with its position, type, order number and I/O addresses, so tags can be mapped to the plugged modules")]
        public static ResponseDeviceInfo GetDeviceInfo(
            [Description("devicePath: defines the path in the project structure to the device, e.g. 'S7-1200 station_1' or 'Group1/PLC_1'")] string devicePath)
        {
            try
            {
                var device = Portal.GetDevice(devicePath);

                if (device is not null)
                {
                    var response = DescribeDevice(device, withItems: true);
                    response.Message = $"Device info retrieved from '{devicePath}'";
                    response.Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true
                    };

                    return response;
                }
                else
                {
                    throw new McpException($"Device not found at '{devicePath}'. 'GetDevices' lists the device paths.");
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving device info from '{devicePath}': {ex.Message}", ex);
            }
        }

        [McpServerTool(Name = "GetDeviceItemInfo", Title = "Get device item info", ReadOnly = true, OpenWorld = false, UseStructuredContent = true), Description("Show name, attributes, position, type, order number and I/O start addresses of one device item (CPU, module, sub module) and of the items below it")]
        public static ResponseDeviceItemInfo GetDeviceItemInfo(
            [Description("deviceItemPath: device path, then the item names below it, e.g. 'House station/Rack_0/AQ 2x14BIT_House' or 'House station/AQ 2x14BIT_House'")] string deviceItemPath)
        {
            try
            {
                var deviceItem = Portal.GetDeviceItem(deviceItemPath);

                if (deviceItem is not null)
                {
                    var summary = Helper.DescribeDeviceItem(deviceItem);

                    return new ResponseDeviceItemInfo
                    {
                        Message = $"Device item info retrieved from '{deviceItemPath}'",
                        Name = deviceItem.Name,
                        Path = summary.Path,
                        PositionNumber = summary.PositionNumber,
                        TypeIdentifier = summary.TypeIdentifier,
                        OrderNumber = summary.OrderNumber,
                        Classification = summary.Classification,
                        Addresses = summary.Addresses,
                        Children = deviceItem.DeviceItems.Select(d => d.Name).ToList(),
                        Attributes = Helper.GetAttributeList(deviceItem),
                        Description = deviceItem.ToString(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Device item not found at '{deviceItemPath}'. 'GetDeviceInfo' lists the item paths of a device.");
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving device item info from '{deviceItemPath}': {ex.Message}", ex);
            }
        }

        [McpServerTool(Name = "GetDevices", Title = "Get devices", ReadOnly = true, OpenWorld = false, UseStructuredContent = true), Description("List all devices of the current project or session with their path and attributes. 'GetDeviceInfo' adds the modules and I/O addresses of one device")]
        public static ResponseDevices GetDevices()
        {
            try
            {
                var list = Portal.GetDevices();
                var responseList = new List<ResponseDeviceInfo>();

                if (list != null)
                {
                    foreach (var device in list)
                    {
                        if (device != null)
                        {
                            responseList.Add(DescribeDevice(device, withItems: false));
                        }
                    }

                    return new ResponseDevices
                    {
                        Message = "Devices retrieved",
                        Items = responseList,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed retrieving devices");
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving devices: {ex.Message}", ex);
            }
        }

        [WriteTool]
        [McpServerTool(Name = "CreateDeviceGroup", Title = "Create device group", Destructive = true, OpenWorld = false, UseStructuredContent = true),
         Description("Create a device group (a folder for devices in the project tree) below the project root or below an existing device group. Creates only the folder: no device is moved and no hardware or network setting changes")]
        public static ResponseCreated CreateDeviceGroup(
            [Description("parentGroupPath: path of the parent device group, e.g. 'Plant' or 'Plant/House'; empty creates directly below the project")] string parentGroupPath,
            [Description("name: name of the new group, without a slash")] string name)
        {
            return Guarded(nameof(CreateDeviceGroup), () =>
            {
                Portal.CreateDeviceGroup(parentGroupPath ?? string.Empty, name);
                return Created("Device group", name, JoinPath(parentGroupPath ?? string.Empty, name));
            });
        }

        private static ResponseDeviceInfo DescribeDevice(Device device, bool withItems)
        {
            string? typeIdentifier;

            try
            {
                typeIdentifier = device.TypeIdentifier;
            }
            catch (Exception)
            {
                typeIdentifier = null;
            }

            return new ResponseDeviceInfo
            {
                Name = device.Name,
                Path = TiaMcpServer.Siemens.Portal.GetDevicePath(device),
                TypeIdentifier = typeIdentifier,
                Attributes = Helper.GetAttributeList(device),
                Description = device.ToString(),
                Items = withItems ? device.DeviceItems.Select(Helper.DescribeDeviceItem).ToList() : null
            };
        }

        #endregion
    }
}
