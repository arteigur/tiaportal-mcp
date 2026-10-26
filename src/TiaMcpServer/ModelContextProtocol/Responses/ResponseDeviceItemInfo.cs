using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseDeviceItemInfo : ResponseAttributes
    {
        public string? Name { get; set; }

        /// <summary>Path as GetDeviceItemInfo takes it.</summary>
        public string? Path { get; set; }

        public int? PositionNumber { get; set; }

        public string? TypeIdentifier { get; set; }

        public string? OrderNumber { get; set; }

        public string? Classification { get; set; }

        /// <summary>I/O addresses of this item and every item below it, each naming its owner.</summary>
        public IEnumerable<IoAddress>? Addresses { get; set; }

        /// <summary>Names of the device items directly below this one.</summary>
        public IEnumerable<string>? Children { get; set; }

        public string? Description { get; set; }
    }
}
