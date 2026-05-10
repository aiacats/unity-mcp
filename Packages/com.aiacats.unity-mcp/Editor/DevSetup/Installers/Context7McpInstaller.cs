using ClaudeCodeMCP.Editor.DevSetup.Util;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.DevSetup.Installers
{
    /// <summary>
    /// Claude Code 用の context7 MCP サーバーを .mcp.json と .claude/settings.local.json に冪等登録する。
    /// </summary>
    internal static class Context7McpInstaller
    {
        public const string ServerName = "context7";

        public static bool IsInstalled()
        {
            JObject mcp = JsonFileEditor.ReadOrEmpty(ProjectPaths.McpJsonPath);
            JObject servers = mcp["mcpServers"] as JObject;
            bool inMcp = servers != null && servers[ServerName] != null;

            JObject settings = JsonFileEditor.ReadOrEmpty(ProjectPaths.ClaudeSettingsLocalPath);
            JArray enabled = settings["enabledMcpjsonServers"] as JArray;
            bool inEnabled = false;
            if (enabled != null)
            {
                foreach (JToken t in enabled)
                {
                    if (t.Type == JTokenType.String && (string)t == ServerName) { inEnabled = true; break; }
                }
            }
            return inMcp && inEnabled;
        }

        public static void Install(out string summary)
        {
            EnsureMcpJson();
            EnsureSettingsLocalEnabled();
            summary =
                ".mcp.json: context7 entry ensured.\n" +
                ".claude/settings.local.json: enabledMcpjsonServers contains 'context7'.\n" +
                "Restart Claude Code for the MCP server to be picked up.";
        }

        public static void Uninstall(out string summary)
        {
            JObject mcp = JsonFileEditor.ReadOrEmpty(ProjectPaths.McpJsonPath);
            JObject servers = mcp["mcpServers"] as JObject;
            bool removedMcp = servers != null && JsonFileEditor.RemoveObjectKey(servers, ServerName);
            JsonFileEditor.Write(ProjectPaths.McpJsonPath, mcp);

            JObject settings = JsonFileEditor.ReadOrEmpty(ProjectPaths.ClaudeSettingsLocalPath);
            bool removedEnabled = JsonFileEditor.RemoveStringFromArray(settings, "enabledMcpjsonServers", ServerName);
            JsonFileEditor.Write(ProjectPaths.ClaudeSettingsLocalPath, settings);

            summary = string.Format("Removed context7 from .mcp.json={0}, settings.local.json={1}", removedMcp, removedEnabled);
        }

        private static void EnsureMcpJson()
        {
            JObject mcp = JsonFileEditor.ReadOrEmpty(ProjectPaths.McpJsonPath);
            JObject servers = mcp["mcpServers"] as JObject;
            if (servers == null)
            {
                servers = new JObject();
                mcp["mcpServers"] = servers;
            }
            if (servers[ServerName] == null)
            {
                servers[ServerName] = new JObject
                {
                    ["command"] = "npx",
                    ["args"] = new JArray { "-y", "@upstash/context7-mcp@latest" }
                };
            }
            JsonFileEditor.Write(ProjectPaths.McpJsonPath, mcp);
        }

        private static void EnsureSettingsLocalEnabled()
        {
            JObject settings = JsonFileEditor.ReadOrEmpty(ProjectPaths.ClaudeSettingsLocalPath);
            JsonFileEditor.EnsureStringInArray(settings, "enabledMcpjsonServers", ServerName);
            JsonFileEditor.Write(ProjectPaths.ClaudeSettingsLocalPath, settings);
        }
    }
}
