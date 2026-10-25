using System.Collections.Generic;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseDownload : ResponseMessage
    {
        public DownloadOutcome? Outcome { get; set; }
    }
}
