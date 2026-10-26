using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public class Helper
    {
        public static List<Attribute> GetAttributeList(IEngineeringObject obj)
        {
            var attributes = new List<Attribute>();

            if (obj != null)
            {
                foreach (var attr in obj.GetAttributeInfos())
                {
                    object? value;

                    try
                    {
                        value = ToJsonValue(obj.GetAttribute(attr.Name), 0);
                    }
                    catch (Exception)
                    {
                        // Some attributes cannot be read in every state (e.g. hardware parameters of
                        // a module that is not plugged); one bad attribute must not fail the call.
                        value = null;
                    }

                    attributes.Add(new Attribute
                    {
                        Name = attr.Name,
                        Value = value,
                        AccessMode = Enum.GetName(typeof(EngineeringAttributeAccessMode), attr.AccessMode)
                    });
                }
            }

            return attributes;
        }

        /// <summary>
        /// An attribute value the JSON serializer can always write. The response is serialized
        /// after the tool method returns, so a value it cannot write (an engineering object with
        /// a Parent cycle, a property that throws) fails the call with the SDK's bare "An error
        /// occurred invoking ..." - which is what GetDevices / GetDeviceInfo did for devices.
        /// Engineering objects become their name, enums their name, collections a short list.
        /// </summary>
        private static object? ToJsonValue(object? value, int depth)
        {
            switch (value)
            {
                case null:
                    return null;
                case string or bool or char:
                    return value;
                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    return value;
                case Enum:
                    return value.ToString();
                case DateTime or DateTimeOffset:
                    return value;
                case TimeSpan or Guid or Version:
                    return value.ToString();
                case MultilingualText text:
                    return FirstText(text);
                case byte[] bytes:
                    return BitConverter.ToString(bytes);
                case IEngineeringObject engineeringObject:
                    return EngineeringObjectName(engineeringObject);
                case System.Collections.IEnumerable sequence when depth == 0:
                    var list = new List<object?>();
                    foreach (var item in sequence)
                    {
                        if (list.Count == 50)
                        {
                            list.Add("...");
                            break;
                        }

                        list.Add(ToJsonValue(item, depth + 1));
                    }

                    return list;
                default:
                    return value.ToString();
            }
        }

        private static string? EngineeringObjectName(IEngineeringObject engineeringObject)
        {
            try
            {
                if (engineeringObject.GetAttributeInfos().Any(a => a.Name == "Name"))
                {
                    return engineeringObject.GetAttribute("Name")?.ToString();
                }
            }
            catch (Exception)
            {
                // fall through to ToString
            }

            return engineeringObject.ToString();
        }

        /// <summary>
        /// Name, position, type and I/O addresses of a device item. The addresses include those of
        /// every item below it, each naming its owner, because on an S7-1200 a module's addresses
        /// sit on a sub item of the same name and the CPU's on-board I/O on 'DI 14/DQ 10_1' etc.
        /// </summary>
        public static DeviceItemSummary DescribeDeviceItem(DeviceItem deviceItem)
        {
            var addresses = new List<IoAddress>();
            CollectAddresses(deviceItem, deviceItem.Name, addresses);

            return new DeviceItemSummary
            {
                Name = deviceItem.Name,
                Path = TiaMcpServer.Siemens.Portal.GetDeviceItemPath(deviceItem),
                PositionNumber = TryRead(() => (int?)deviceItem.PositionNumber),
                TypeIdentifier = TryRead(() => deviceItem.TypeIdentifier),
                OrderNumber = TryRead(() => deviceItem.GetAttribute("OrderNumber")?.ToString()),
                Classification = TryRead(() => deviceItem.Classification.ToString()),
                Addresses = addresses
            };
        }

        private static void CollectAddresses(DeviceItem deviceItem, string owner, List<IoAddress> addresses)
        {
            var own = TryRead(() => deviceItem.Addresses?.ToList()) ?? new List<Address>();

            foreach (var address in own)
            {
                var ioType = TryRead(() => (AddressIoType?)address.IoType) ?? AddressIoType.None;
                var start = TryRead(() => (int?)address.StartAddress) ?? -1;
                var length = TryRead(() => (int?)address.Length) ?? 0;
                var area = ioType == AddressIoType.Input ? "I" : ioType == AddressIoType.Output ? "Q" : string.Empty;
                var bytes = (length + 7) / 8;

                addresses.Add(new IoAddress
                {
                    Owner = owner,
                    IoType = ioType.ToString(),
                    StartAddress = start,
                    LengthBits = length,
                    Range = area.Length > 0 && start >= 0 && bytes > 0
                        ? bytes == 1 ? $"%{area}B{start}" : $"%{area}B{start}..%{area}B{start + bytes - 1}"
                        : null
                });
            }

            foreach (var child in deviceItem.DeviceItems)
            {
                CollectAddresses(child, $"{owner}/{child.Name}", addresses);
            }
        }

        private static T? TryRead<T>(Func<T> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return default;
            }
        }

        /// <summary>
        /// First available translation of a MultilingualText (tag and constant comments), or
        /// null when the text has no items. Openness returns one item per project language.
        /// </summary>
        public static string? FirstText(MultilingualText? text)
        {
            if (text == null)
            {
                return null;
            }

            foreach (var item in text.Items)
            {
                return item.Text;
            }

            return null;
        }

        public static BlockGroupInfo BuildBlockHierarchy(PlcBlockGroup group, TiaMcpServer.Siemens.Portal? portal = null)
        {
            var groupInfo = new BlockGroupInfo
            {
                Name = group.Name
            };

            var blockList = new List<ResponseBlockInfo>();
            foreach (var block in group.Blocks)
            {
                var attributes = Helper.GetAttributeList(block);
                blockList.Add(new ResponseBlockInfo
                {
                    Path = portal == null ? null : portal.GetBlockPath(block),
                    Name = block.Name,
                    TypeName = block.GetType().Name,
                    Namespace = block.Namespace,
                    ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), block.ProgrammingLanguage),
                    MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                    IsConsistent = block.IsConsistent,
                    HeaderName = block.HeaderName,
                    ModifiedDate = block.ModifiedDate,
                    IsKnowHowProtected = block.IsKnowHowProtected,
                    Attributes = attributes,
                    Description = block.ToString()
                });
            }
            groupInfo.Blocks = blockList;

            var groupList = new List<BlockGroupInfo>();
            foreach (var subGroup in group.Groups)
            {
                groupList.Add(BuildBlockHierarchy(subGroup, portal));
            }
            groupInfo.Groups = groupList;

            return groupInfo;
        }
    }
}
