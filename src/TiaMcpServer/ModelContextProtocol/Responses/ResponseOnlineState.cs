using System.Collections.Generic;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseOnlineState : ResponseMessage
    {
        public string? PlcName { get; set; }
        public string? State { get; set; }
        public bool IsConnectionConfigured { get; set; }
    }
}
