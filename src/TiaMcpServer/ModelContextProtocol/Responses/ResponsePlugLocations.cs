using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponsePlugLocations : ResponseMessage
    {
        public string? Parent { get; set; }
        public List<ResponsePlugLocation>? Locations { get; set; }
    }
}
