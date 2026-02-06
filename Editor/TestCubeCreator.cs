using UnityEngine;
using UnityEditor;
using ClaudeCodeMCP.Editor.Core;
using System.Net;
using System.Text;

namespace ClaudeCodeMCP.Editor
{
    /// <summary>
    /// MCPサーバーのテスト用Cube作成ツール
    /// </summary>
    public class TestCubeCreator
    {
        [MenuItem("Tools/Claude Code MCP/Test: Create Cube")]
        public static void CreateTestCube()
        {
            // Unity Editor直接作成
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.position = Vector3.zero;
            cube.name = "TestCube_Direct";
            Selection.activeGameObject = cube;
            
            Debug.Log("[Claude Code MCP] Unity Editor直接作成: TestCube_Direct を中央(0,0,0)に配置しました");
            
            // MCPサーバー経由もテスト
            TestMCPServerCubeCreation();
        }
        
        private static void TestMCPServerCubeCreation()
        {
            var server = MCPUnityServer.Instance;
            if (!server.IsRunning)
            {
                Debug.LogWarning("[Claude Code MCP] サーバーが起動していません。サーバーを開始します...");
                server.StartServer();
                
                // サーバー起動待機
                EditorApplication.delayCall += () => {
                    System.Threading.Thread.Sleep(1000); // 1秒待機
                    ExecuteMCPCubeCreation();
                };
            }
            else
            {
                ExecuteMCPCubeCreation();
            }
        }
        
        private static void ExecuteMCPCubeCreation()
        {
            try
            {
                // 1. メニューアイテム実行でCube作成
                TestExecuteMenuItem();
                
                // 2. GameObjectの更新テスト
                TestUpdateGameObject();
                
                // 3. シーン階層取得テスト
                TestGetSceneHierarchy();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] MCP テスト失敗: {ex.Message}");
            }
        }
        
        private static void TestExecuteMenuItem()
        {
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add("Content-Type", "application/json");
                    string json = @"{""menuPath"": ""GameObject/3D Object/Cube""}";
                    string response = client.UploadString("http://localhost:8090/mcp/tools/execute_menu_item", json);
                    Debug.Log($"[Claude Code MCP] execute_menu_item成功: {response}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] execute_menu_item失敗: {ex.Message}");
            }
        }
        
        private static void TestUpdateGameObject()
        {
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add("Content-Type", "application/json");
                    string json = @"{
                        ""objectPath"": ""TestCube_MCP"",
                        ""gameObjectData"": {
                            ""name"": ""TestCube_MCP"",
                            ""activeSelf"": true
                        }
                    }";
                    string response = client.UploadString("http://localhost:8090/mcp/tools/update_gameobject", json);
                    Debug.Log($"[Claude Code MCP] update_gameobject成功: {response}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] update_gameobject失敗: {ex.Message}");
            }
        }
        
        private static void TestGetSceneHierarchy()
        {
            try
            {
                using (var client = new WebClient())
                {
                    string response = client.DownloadString("http://localhost:8090/mcp/resources/scenes_hierarchy");
                    Debug.Log($"[Claude Code MCP] scenes_hierarchy成功:\n{response}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] scenes_hierarchy失敗: {ex.Message}");
            }
        }
        
        [MenuItem("Tools/Claude Code MCP/Test: Server Status")]
        public static void TestServerStatus()
        {
            var server = MCPUnityServer.Instance;
            Debug.Log($"[Claude Code MCP] サーバー状態: {(server.IsRunning ? "実行中" : "停止中")}");
            
            if (server.IsRunning)
            {
                try
                {
                    using (var client = new WebClient())
                    {
                        string response = client.DownloadString("http://localhost:8090/mcp/ping");
                        Debug.Log($"[Claude Code MCP] Ping成功: {response}");
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[Claude Code MCP] Ping失敗: {ex.Message}");
                }
            }
            else
            {
                Debug.LogWarning("[Claude Code MCP] サーバーが停止しています");
            }
        }
        
        [MenuItem("Tools/Claude Code MCP/Test: Force Start Server")]
        public static void ForceStartServer()
        {
            var server = MCPUnityServer.Instance;
            if (server.IsRunning)
            {
                server.StopServer();
                System.Threading.Thread.Sleep(500);
            }
            server.StartServer();
            Debug.Log("[Claude Code MCP] サーバーを強制再起動しました");
        }
    }
}