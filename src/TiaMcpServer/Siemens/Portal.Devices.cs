using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.Safety;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        #region devices

        public List<Device> GetDevices(string regexName = "")
        {
            _logger?.LogInformation("Getting devices...");

            if (IsProjectNull())
            {
                return [];
            }

            return EnumerateAllDevices()
                .Where(device => MatchesRegex(device, regexName))
                .ToList();
        }

        public Device? GetDevice(string devicePath)
        {
            _logger?.LogInformation($"Getting device by path: {devicePath}");

            if (IsProjectNull())
            {
                return null;
            }

            // Retrieve the device by its path
            return GetDeviceByPath(devicePath);
        }

        public DeviceItem? GetDeviceItem(string deviceItemPath)
        {
            _logger?.LogInformation($"Getting device item by path: {deviceItemPath}");

            if (IsProjectNull())
            {
                return null;
            }

            // Retrieve the device by its path
            return GetDeviceItemByPath(deviceItemPath);

        }

        #endregion

        #region get...by path

        private Device? GetDeviceByPath(string devicePath)
        {
            if (_project?.Devices == null || string.IsNullOrWhiteSpace(devicePath))
                return null;

            // A device name can itself contain '/' (e.g. 'S7-1500/ET200MP station_1'), so try the
            // whole input as a name before splitting it into group/device segments.
            var rootDevices = _project.Devices.Concat(EnumerateUngroupedDevices()).ToList();
            var exactMatch = FindDeviceByName(rootDevices, devicePath.Trim('/'));
            if (exactMatch != null)
            {
                return exactMatch;
            }

            var pathSegments = devicePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (pathSegments.Length == 0)
            {
                return null;
            }

            // Try top-level device first
            if (pathSegments.Length == 1)
            {
                return null;
            }

            // Traverse device groups
            DeviceUserGroupComposition? groups = _project.DeviceGroups;
            DeviceUserGroup? group = groups?.FirstOrDefault(g => g.Name.Equals(pathSegments[0], StringComparison.OrdinalIgnoreCase));

            if (group is null)
            {
                return null;
            }

            for (int i = 1; i < pathSegments.Length; i++)
            {
                // Try to find device in current group; the remaining segments may form one name containing '/'
                var device = FindDeviceByName(group.Devices, string.Join("/", pathSegments.Skip(i)))
                    ?? FindDeviceByName(group.Devices, pathSegments[i]);
                if (device != null)
                {
                    return device;
                }

                // Try to find subgroup
                group = group.Groups.FirstOrDefault(g => g.Name.Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase));
                if (group is null)
                {
                    break;
                }
            }

            return null;
        }

        private static Device? FindDeviceByName(IEnumerable<Device> devices, string name)
        {
            return devices.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private DeviceItem? GetDeviceItemByPath(string deviceItemPath)
        {
            if (_project is null || _project.Devices == null || string.IsNullOrWhiteSpace(deviceItemPath))
            {
                return null;
            }

            var path = deviceItemPath.Trim().Trim('/');

            // The device part of the path is matched as a whole name (a device name can contain
            // '/', e.g. 'S7-1500/ET200MP station_1'), optionally preceded by its group path. The
            // longest match wins; the rest of the path names device items below that device.
            var candidates = EnumerateAllDevices()
                .SelectMany(d => new[] { (Device: d, Prefix: GetDevicePath(d)), (Device: d, Prefix: d.Name) })
                .Where(c => path.StartsWith(c.Prefix + "/", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Prefix.Length);

            foreach (var (device, prefix) in candidates)
            {
                var deviceItem = FindDeviceItem(device, path.Substring(prefix.Length + 1));
                if (deviceItem != null)
                {
                    return deviceItem;
                }
            }

            // No device named: a hardware PLC's device name ('S7-1500/ET200MP station_1') is not
            // shown in the TIA Portal IDE, so a path may start at the device item itself.
            foreach (var device in EnumerateAllDevices())
            {
                var deviceItem = FindDeviceItem(device, path);
                if (deviceItem != null)
                {
                    return deviceItem;
                }
            }

            return null;
        }

        /// <summary>
        /// Walks the '/' separated item path below a device. Each segment is looked up in the
        /// current item's DeviceItems and, for a rack, its Items (the modules plugged into it), so
        /// both 'Rack_0/AQ 2x14BIT_House' and 'AQ 2x14BIT_House' find a module of an S7-1200.
        /// An item name may itself contain '/' ('AI 2/AQ 2_1'), so longer names are tried first.
        /// </summary>
        private static DeviceItem? FindDeviceItem(Device device, string itemPath)
        {
            var segments = itemPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            return segments.Length == 0 ? null : FindDeviceItem(device.DeviceItems, null, segments, 0);
        }

        private static DeviceItem? FindDeviceItem(DeviceItemComposition? deviceItems, DeviceItemAssociation? items, string[] segments, int index)
        {
            var children = (deviceItems ?? Enumerable.Empty<DeviceItem>())
                .Concat(items ?? Enumerable.Empty<DeviceItem>())
                .ToList();

            for (var count = segments.Length - index; count >= 1; count--)
            {
                var name = string.Join("/", segments, index, count);

                foreach (var child in children.Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (index + count == segments.Length)
                    {
                        return child;
                    }

                    var found = FindDeviceItem(child.DeviceItems, child.Items, segments, index + count);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }

            return null;
        }

        /// <summary>The device name, preceded by its device group path, e.g. 'Group1/PLC_1'.</summary>
        public static string GetDevicePath(Device device)
        {
            var path = device.Name;

            for (var parent = device.Parent; parent is DeviceUserGroup group; parent = group.Parent)
            {
                path = group.Name + "/" + path;
            }

            return path;
        }

        /// <summary>Path of a device item as the device tools take it: device path, then the item names below it.</summary>
        public static string GetDeviceItemPath(DeviceItem deviceItem)
        {
            var path = deviceItem.Name;
            IEngineeringObject? parent = deviceItem.Parent;

            while (parent is DeviceItem parentItem)
            {
                path = parentItem.Name + "/" + path;
                parent = parentItem.Parent;
            }

            return parent is Device device ? GetDevicePath(device) + "/" + path : path;
        }

        #endregion

        #region get recursive ...

        /// <summary>
        /// All devices of the open project: top level, every user group (recursively) and the
        /// "Ungrouped devices" system group, which is a DeviceSystemGroup and not part of DeviceGroups.
        /// </summary>
        private IEnumerable<Device> EnumerateAllDevices()
        {
            if (_project is null)
            {
                yield break;
            }

            if (_project.Devices != null)
            {
                foreach (var device in _project.Devices)
                {
                    yield return device;
                }
            }

            if (_project.DeviceGroups != null)
            {
                foreach (var group in _project.DeviceGroups)
                {
                    foreach (var device in EnumerateGroupDevices(group))
                    {
                        yield return device;
                    }
                }
            }

            foreach (var device in EnumerateUngroupedDevices())
            {
                yield return device;
            }
        }

        private IEnumerable<Device> EnumerateUngroupedDevices()
        {
            var devices = _project?.UngroupedDevicesGroup?.Devices;
            if (devices is null)
            {
                return [];
            }

            return devices;
        }

        private static IEnumerable<Device> EnumerateGroupDevices(DeviceUserGroup group)
        {
            foreach (var device in group.Devices)
            {
                yield return device;
            }

            foreach (var subgroup in group.Groups)
            {
                foreach (var device in EnumerateGroupDevices(subgroup))
                {
                    yield return device;
                }
            }
        }

        private static bool MatchesRegex(Device device, string regexName)
        {
            if (string.IsNullOrEmpty(regexName))
            {
                return true;
            }

            try
            {
                return Regex.IsMatch(device.Name, regexName, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                // Invalid regex pattern, skip this device
                return false;
            }
        }

        #endregion
    }
}
