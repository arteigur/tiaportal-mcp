using System.Collections.Generic;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseConnectionTargets : ResponseMessage
    {
        public List<ConnectionTarget>? Targets { get; set; }
    }
}
