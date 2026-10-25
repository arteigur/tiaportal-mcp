using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseHardwareCatalog : ResponseMessage
    {
        public List<ResponseCatalogEntry>? Entries { get; set; }
    }
}
