using UnityEngine;
using UnityEditor;

namespace ClaudeCodeMCP.Editor.Core
{
    /// <summary>
    /// Test utility for the MCP Unity Server
    /// </summary>
    public class MCPUnityServerTest
    {
        [MenuItem("Tools/Claude Code MCP/Debug: Control Panel")]
        public static void OpenControlPanel()
        {
            ClaudeCodeMCP.Editor.ClaudeCodeMCPWindow.ShowWindow();
        }
        
        [MenuItem("Tools/Claude Code MCP/Quick Status")]
        public static void QuickStatus()
        {
            var server = MCPUnityServer.Instance;
            
            if (server.IsRunning)
            {
                Debug.Log("[Claude Code MCP] ✓ サーバー実行中 - http://localhost:8090");
                EditorUtility.DisplayDialog("Claude Code MCP", "サーバー実行中\nhttp://localhost:8090", "OK");
            }
            else
            {
                Debug.LogWarning("[Claude Code MCP] ✗ サーバー停止中");
                if (EditorUtility.DisplayDialog("Claude Code MCP", "サーバーが停止しています。\n開始しますか？", "開始", "キャンセル"))
                {
                    server.StartServer();
                }
            }
        }
        
        [MenuItem("Tools/Claude Code MCP/Test Server")]
        public static void TestServer()
        {
            var server = MCPUnityServer.Instance;
            
            if (server.IsRunning)
            {
                Debug.Log("[Claude Code MCP Test] Server is running successfully!");
                Debug.Log($"[Claude Code MCP Test] Server should be available at http://localhost:8090");
                
                // Test ping endpoint
                Debug.Log("[Claude Code MCP Test] You can test the server by sending a GET request to http://localhost:8090/mcp/ping");
            }
            else
            {
                Debug.LogError("[Claude Code MCP Test] Server is not running!");
            }
        }
        
        [MenuItem("Tools/Claude Code MCP/Debug: Start Server")]
        public static void StartServer()
        {
            MCPUnityServer.Instance.StartServer();
        }
        
        [MenuItem("Tools/Claude Code MCP/Debug: Stop Server")]
        public static void StopServer()
        {
            MCPUnityServer.Instance.StopServer();
        }
        
        [MenuItem("Tools/Claude Code MCP/Debug: Start Server", true)]
        public static bool ValidateStartServer()
        {
            return !MCPUnityServer.Instance.IsRunning;
        }
        
        [MenuItem("Tools/Claude Code MCP/Debug: Stop Server", true)]
        public static bool ValidateStopServer()
        {
            return MCPUnityServer.Instance.IsRunning;
        }
    }
}