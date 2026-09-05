namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    /// <summary>
    /// 「今つながっている Unity がどのプロジェクトか」を返す。
    /// Node ブリッジが接続先を照合するために使う。複数プロジェクトを同時に開いたとき、
    /// ポートのずれで別プロジェクトを操作してしまう事故を検知できるようにするのが目的。
    /// </summary>
    internal class IdentityHandler : HandlerBase
    {
        public IdentityHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return CreateSuccessResponse("identity", MCPEndpointFile.BuildIdentity(Server.Port));
        }
    }
}
