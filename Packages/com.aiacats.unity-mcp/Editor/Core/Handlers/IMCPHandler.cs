using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    internal interface IMCPHandler
    {
        string Handle(string requestBody);
    }
}
