using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>A device item (rack, CPU, module, sub module) with the I/O addresses it and its sub items use.</summary>
    public class DeviceItemSummary
    {
        public string? Name { get; set; }

        /// <summary>Path as GetDeviceItemInfo takes it, e.g. 'House station/AQ 2x14BIT_House'.</summary>
        public string? Path { get; set; }

        /// <summary>Slot or position in the parent; the CPU of an S7-1200 is 1, signal modules 2-9.</summary>
        public int? PositionNumber { get; set; }

        public string? TypeIdentifier { get; set; }

        public string? OrderNumber { get; set; }

        public string? Classification { get; set; }

        /// <summary>I/O addresses of this item and every item below it.</summary>
        public IEnumerable<IoAddress>? Addresses { get; set; }
    }

    public class IoAddress
    {
        /// <summary>Item that holds the address, relative to the described item, e.g. 'HousePLC/AI 2/AQ 2_1'.</summary>
        public string? Owner { get; set; }

        /// <summary>Input, Output, Diagnosis, Substitute or None.</summary>
        public string? IoType { get; set; }

        /// <summary>First byte of the address range in the process image, e.g. 96 for %QW96.</summary>
        public int? StartAddress { get; set; }

        /// <summary>Length in bits.</summary>
        public int? LengthBits { get; set; }

        /// <summary>The byte range as tags address it, e.g. '%QB96..%QB99'; null for diagnosis addresses.</summary>
        public string? Range { get; set; }
    }
}
