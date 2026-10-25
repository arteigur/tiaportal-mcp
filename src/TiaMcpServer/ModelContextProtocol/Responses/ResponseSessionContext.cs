using System.Collections.Generic;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseSessionContext : ResponseMessage
    {
        public SessionContext? Context { get; set; }
        public bool AllowWrite { get; set; }
        public bool AllowOnline { get; set; }
        public bool AllowDownload { get; set; }
        public bool AllowHardware { get; set; }
        public Dictionary<string, string>? Presets { get; set; }
    }
}
