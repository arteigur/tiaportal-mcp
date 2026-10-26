using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseDbActualValues : ResponseMessage
    {
        public DbActualValues? Result { get; set; }
    }
}
