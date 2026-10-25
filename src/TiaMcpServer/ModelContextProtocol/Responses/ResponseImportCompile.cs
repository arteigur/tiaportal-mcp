using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    public class ResponseImportCompile : ResponseMessage
    {
        public ImportCompileResult? Result { get; set; }
    }
}
