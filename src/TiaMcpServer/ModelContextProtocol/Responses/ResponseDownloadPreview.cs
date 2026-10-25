using System.Collections.Generic;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseDownloadPreview : ResponseMessage
    {
        public DownloadPreview? Preview { get; set; }
    }
}
