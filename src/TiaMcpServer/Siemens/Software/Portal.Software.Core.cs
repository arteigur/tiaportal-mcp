using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.WatchAndForceTables;
using Siemens.Engineering.Safety;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security;
using System.Text.RegularExpressions;

namespace TiaMcpServer.Siemens
{
    public partial class Portal
    {
        #region software

        public PlcSoftware? GetPlcSoftware(string softwarePath)
        {
            _logger?.LogInformation($"Getting software by path: {softwarePath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);

            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                return plcSoftware;
            }

            return null;
        }

        public CompilerResult? CompilePlcSoftware(string softwarePath, string password = "")
        {
            _logger?.LogInformation($"Compiling software by path: {softwarePath}");

            if (IsProjectNull())
            {
                return null; // "Error, no project";
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);

            if (!TryLoginSafetyOffline(softwareContainer, password))
            {
                return null; // "Error, login to safety offline program failed";
            }

            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var result = CompileService(plcSoftware);

                // Remembered for GetSessionContext (Portal.Context.cs).
                RecordCompile(softwarePath, result);

                return result;
            }

            return null; // "Error";
        }

        /// <summary>
        /// "Rebuild all": compiles every program block on its own, then the PLC software. Openness
        /// has no rebuild-all call - ICompilable.Compile on the PLC software only compiles what
        /// changed, so a PLC that is already compiled reports none of its warnings again. A block
        /// compiled on its own reports its messages every time. Returns the PLC software result
        /// last; null when the path is not a PLC software or the safety login fails.
        /// </summary>
        public List<(string Target, CompilerResult Result)>? RebuildPlcSoftware(string softwarePath, string password = "")
        {
            _logger?.LogInformation($"Rebuilding software by path: {softwarePath}");

            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);

            if (!TryLoginSafetyOffline(softwareContainer, password) || softwareContainer?.Software is not PlcSoftware plcSoftware)
            {
                return null;
            }

            var results = new List<(string Target, CompilerResult Result)>();

            foreach (var block in GetBlocks(softwarePath))
            {
                var result = CompileService(block);

                if (result != null)
                {
                    results.Add((GetBlockPath(block), result));
                }
            }

            var software = CompileService(plcSoftware);

            if (software != null)
            {
                RecordCompile(softwarePath, software);
                results.Add((softwarePath, software));
            }

            return results;
        }

        public CompilerResult? CompileCodeBlock(string softwarePath, string blockPath, string password = "")
        {
            _logger?.LogInformation($"Compiling code block by path: {blockPath}");

            if (IsProjectNull())
            {
                return null; // "Error, no project";
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);

            if (!TryLoginSafetyOffline(softwareContainer, password))
            {
                return null; // "Error, login to safety offline program failed";
            }

            var block = GetBlock(softwarePath, blockPath);

            return block is null ? null : CompileService(block);
        }

        private static CompilerResult? CompileService(IEngineeringServiceProvider provider)
        {
            try
            {
                return provider.GetService<ICompilable>()?.Compile();
            }
            catch (Exception)
            {
                return null; // "Error, compiling failed";
            }
        }

        private static bool TryLoginSafetyOffline(SoftwareContainer? softwareContainer, string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return true;
            }

            var admin = (softwareContainer?.Parent as DeviceItem)?.GetService<SafetyAdministration>();
            if (admin == null || admin.IsLoggedOnToSafetyOfflineProgram)
            {
                return true;
            }

            try
            {
                SecureString secString = new NetworkCredential("", password).SecurePassword;
                admin.LoginToSafetyOfflineProgram(secString);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        #endregion

        #region get software container ...

        private SoftwareContainer? GetSoftwareContainer(string softwarePath)
        {
            if (_project == null)
            {
                return null;
            }

            string[] pathSegments = softwarePath.Split('/');
            int index = 0;

            if (index >= pathSegments.Length)
                return null;

            string segment = pathSegments[index];
            SoftwareContainer? softwareContainer = null;

            // in Devices
            if (_project.Devices != null)
            {
                softwareContainer = GetSoftwareContainerInDevices(_project.Devices, pathSegments, index);
                if (softwareContainer is not null)
                {
                    return softwareContainer;
                }
            }

            // in Groups
            if (_project.DeviceGroups != null)
            {
                softwareContainer = GetSoftwareContainerInGroups(_project.DeviceGroups, pathSegments, index);
                if (softwareContainer is not null)
                {
                    return softwareContainer;
                }
            }

            return null;
        }

        private SoftwareContainer? GetSoftwareContainerInDevices(DeviceComposition devices, string[] pathSegments, int index)
        {

            if (index >= pathSegments.Length)
                return null;

            string segment = pathSegments[index];
            string nextSegment = index + 1 < pathSegments.Length ? pathSegments[index + 1] : string.Empty;

            if (devices != null)
            {
                SoftwareContainer? softwareContainer = null;
                Device? device = null;
                DeviceItem? deviceItem = null;

                // a pc based plc has a Device.Name = 'PC-System_1' or something like that, which is visible in the TIA-Portal IDE
                // use segment to find device
                device = devices.FirstOrDefault(d => d.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
                if (device is not null)
                {
                    // then use next segment to find device item
                    deviceItem = device.DeviceItems.FirstOrDefault(di => di.Name.Equals(nextSegment, StringComparison.OrdinalIgnoreCase));
                    // but here we use next segment to find device item
                    softwareContainer = GetSoftwareContainerInDeviceItem(deviceItem, pathSegments, index + 1);
                    if (softwareContainer is not null)
                    {
                        return softwareContainer;
                    }
                }

                // a hardware plc has a Device.Name = 'S7-1500/ET200MP-Station_1' or something like that, which is not visible in the TIA-Portal IDE
                // ignored segment for Device.Name and use it for DeviceItem.Name
                deviceItem = devices
                    .SelectMany(d => d.DeviceItems)
                    .FirstOrDefault(di => di.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
                if (deviceItem is not null)
                {
                    return GetSoftwareContainerInDeviceItem(deviceItem, pathSegments, index);
                }

            }

            return null;
        }

        private SoftwareContainer? GetSoftwareContainerInGroups(DeviceUserGroupComposition groups, string[] pathSegments, int index)
        {
            if (index >= pathSegments.Length)
                return null;

            string segment = pathSegments[index];
            SoftwareContainer? softwareContainer = null;

            if (groups != null)
            {
                var group = groups.FirstOrDefault(g => g.Name.Equals(segment));
                if (group is not null)
                {
                    // when segment matched
                    softwareContainer = GetSoftwareContainerInDevices(group.Devices, pathSegments, index + 1);
                    if (softwareContainer is not null)
                    {
                        return softwareContainer;
                    }

                    return GetSoftwareContainerInGroups(group.Groups, pathSegments, index + 1);
                }
            }

            return null;
        }

        private SoftwareContainer? GetSoftwareContainerInDeviceItem(DeviceItem deviceItem, string[] pathSegments, int index)
        {
            if (deviceItem != null)
            {
                // when segment matched
                if (index == pathSegments.Length - 1)
                {
                    // get from DeviceItem
                    var softwareContainer = deviceItem.GetService<SoftwareContainer>();
                    if (softwareContainer != null)
                    {
                        return softwareContainer;
                    }
                }
            }

            return null;
        }

        #endregion
    }
}
