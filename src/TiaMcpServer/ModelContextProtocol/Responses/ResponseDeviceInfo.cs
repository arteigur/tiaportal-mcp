using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseDeviceInfo : ResponseAttributes
    {
        public string? Name { get; set; }

        /// <summary>Device path as GetDeviceInfo takes it: device group path, then the device name.</summary>
        public string? Path { get; set; }

        public string? TypeIdentifier { get; set; }

        public string? Description { get; set; }

        /// <summary>The device's top-level items (rack, CPU, modules) with their I/O addresses; GetDeviceInfo only.</summary>
        public IEnumerable<DeviceItemSummary>? Items { get; set; }
    }
}
