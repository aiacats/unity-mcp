using UnityEngine;
using UnityEditor;
using ClaudeCodeMCP.Editor.Core;
using System.Net.NetworkInformation;
using System.Net;

namespace ClaudeCodeMCP.Editor
{
    /// <summary>
    /// Claude Code MCP Unity Editor Window
    /// Provides server status monitoring, control, and configuration
    /// </summary>
    public class ClaudeCodeMCPWindow : EditorWindow
    {
        private Vector2 scrollPosition;
        private bool autoRefresh = true;
        private double lastRefreshTime;
        private const double REFRESH_INTERVAL = 2.0; // Update every 2 seconds

        [MenuItem("Tools/Claude Code MCP/Control Panel")]
        public static void ShowWindow()
        {
            var window = GetWindow<ClaudeCodeMCPWindow>("Claude Code MCP");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }

        private void OnEnable()
        {
            lastRefreshTime = EditorApplication.timeSinceStartup;
        }

        private void OnGUI()
        {
            // Auto refresh
            if (autoRefresh && EditorApplication.timeSinceStartup - lastRefreshTime > REFRESH_INTERVAL)
            {
                Repaint();
                lastRefreshTime = EditorApplication.timeSinceStartup;
            }

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            EditorGUILayout.Space(10);
            
            // Header
            EditorGUILayout.LabelField("Claude Code MCP Unity", EditorStyles.largeLabel);
            EditorGUILayout.Space(5);

            DrawServerStatus();
            EditorGUILayout.Space(10);
            
            DrawServerControls();
            EditorGUILayout.Space(10);
            
            DrawConnectionInfo();
            EditorGUILayout.Space(10);
            
            DrawSettings();
            EditorGUILayout.Space(10);
            
            DrawTestingTools();

            EditorGUILayout.EndScrollView();
        }

        private void DrawServerStatus()
        {
            EditorGUILayout.LabelField("Server Status", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginVertical("box");
            
            var server = MCPUnityServer.Instance;
            bool isRunning = server.IsRunning;
            
            // Status display
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Status:", GUILayout.Width(80));
            
            GUIStyle statusStyle = new GUIStyle(EditorStyles.label);
            statusStyle.normal.textColor = isRunning ? Color.green : Color.red;
            statusStyle.fontStyle = FontStyle.Bold;
            
            EditorGUILayout.LabelField(isRunning ? "Running" : "Stopped", statusStyle);
            EditorGUILayout.EndHorizontal();
            
            // Port display
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Port:", GUILayout.Width(80));
            EditorGUILayout.LabelField("8090");
            EditorGUILayout.EndHorizontal();
            
            // Check port availability
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Port Status:", GUILayout.Width(80));
            bool portInUse = IsPortInUse(8090);
            GUIStyle portStyle = new GUIStyle(EditorStyles.label);
            portStyle.normal.textColor = portInUse ? Color.green : Color.gray;
            EditorGUILayout.LabelField(portInUse ? "In Use" : "Available", portStyle);
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.EndVertical();
        }

        private void DrawServerControls()
        {
            EditorGUILayout.LabelField("Server Control", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginVertical("box");
            
            var server = MCPUnityServer.Instance;
            bool isRunning = server.IsRunning;
            
            EditorGUILayout.BeginHorizontal();
            
            // Start button
            GUI.enabled = !isRunning;
            if (GUILayout.Button("Start Server", GUILayout.Height(30)))
            {
                server.StartServer();
            }
            
            // Stop button
            GUI.enabled = isRunning;
            if (GUILayout.Button("Stop Server", GUILayout.Height(30)))
            {
                server.StopServer();
            }
            
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.Space(5);
            
            // Restart button
            if (GUILayout.Button("Restart Server", GUILayout.Height(25)))
            {
                server.StopServer();
                EditorApplication.delayCall += () => server.StartServer();
            }
            
            EditorGUILayout.EndVertical();
        }

        private void DrawConnectionInfo()
        {
            EditorGUILayout.LabelField("Connection Info", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginVertical("box");
            
            EditorGUILayout.LabelField("Unity HTTP Server:");
            EditorGUILayout.SelectableLabel("http://localhost:8090", EditorStyles.textField, GUILayout.Height(18));
            
            EditorGUILayout.Space(5);
            
            EditorGUILayout.LabelField("Node.js MCP Server:");
            string serverPath = System.IO.Path.GetFullPath("Assets/ClaudeCodeMCP/Server/index.js");
            EditorGUILayout.SelectableLabel(serverPath, EditorStyles.textField, GUILayout.Height(18));
            
            EditorGUILayout.Space(5);
            
            EditorGUILayout.LabelField(".mcp.json Configuration:");
            if (GUILayout.Button("Copy Configuration to Clipboard", GUILayout.Height(25)))
            {
                string configJson = @"{
  ""mcpServers"": {
    ""claude-code-mcp-unity"": {
      ""command"": ""node"",
      ""args"": [
        """ + serverPath.Replace("\\", "/") + @"""
      ],
      ""env"": {
        ""MCP_UNITY_HTTP_URL"": ""http://localhost:8090""
      }
    }
  }
}";
                EditorGUIUtility.systemCopyBuffer = configJson;
                Debug.Log("[Claude Code MCP] Configuration copied to clipboard");
            }
            
            EditorGUILayout.EndVertical();
        }

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginVertical("box");
            
            // Auto refresh setting
            autoRefresh = EditorGUILayout.Toggle("Auto Refresh", autoRefresh);
            
            EditorGUILayout.Space(5);
            
            // Manual refresh button
            if (GUILayout.Button("Refresh Now", GUILayout.Height(25)))
            {
                Repaint();
            }
            
            EditorGUILayout.EndVertical();
        }

        private void DrawTestingTools()
        {
            EditorGUILayout.LabelField("Development Tools", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginVertical("box");
            
            // Hot Reload section
            EditorGUILayout.LabelField("Hot Reload", EditorStyles.miniBoldLabel);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Hot Reload (Optimized)", GUILayout.Height(30)))
            {
                TestHotReload(true);
            }
            
            if (GUILayout.Button("Force Compilation", GUILayout.Height(30)))
            {
                TestForceCompilation();
            }
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.Space(5);
            
            if (GUILayout.Button("Check Compilation Status", GUILayout.Height(25)))
            {
                TestCheckCompilationStatus();
            }
            
            EditorGUILayout.Space(10);
            
            // Test section
            EditorGUILayout.LabelField("Connection Tests", EditorStyles.miniBoldLabel);
            
            if (GUILayout.Button("Ping Test", GUILayout.Height(25)))
            {
                TestPing();
            }
            
            EditorGUILayout.Space(5);
            
            if (GUILayout.Button("Console Log Test", GUILayout.Height(25)))
            {
                TestConsoleLog();
            }
            
            EditorGUILayout.Space(5);
            
            if (GUILayout.Button("Scene Hierarchy Test", GUILayout.Height(25)))
            {
                TestSceneHierarchy();
            }
            
            EditorGUILayout.EndVertical();
        }

        private bool IsPortInUse(int port)
        {
            try
            {
                IPGlobalProperties ipProperties = IPGlobalProperties.GetIPGlobalProperties();
                IPEndPoint[] listeners = ipProperties.GetActiveTcpListeners();
                
                foreach (IPEndPoint listener in listeners)
                {
                    if (listener.Port == port)
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void TestPing()
        {
            var server = MCPUnityServer.Instance;
            if (!server.IsRunning)
            {
                Debug.LogWarning("[Claude Code MCP] Server is not running");
                return;
            }
            
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add("Content-Type", "application/json");
                    string response = client.DownloadString("http://localhost:8090/mcp/ping");
                    Debug.Log($"[Claude Code MCP] Ping successful: {response}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] Ping failed: {ex.Message}");
            }
        }

        private void TestConsoleLog()
        {
            var server = MCPUnityServer.Instance;
            if (!server.IsRunning)
            {
                Debug.LogWarning("[Claude Code MCP] Server is not running");
                return;
            }
            
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add("Content-Type", "application/json");
                    string json = @"{""message"": ""Test message from Claude Code MCP Editor Window"", ""type"": ""info""}";
                    string response = client.UploadString("http://localhost:8090/mcp/tools/send_console_log", json);
                    Debug.Log($"[Claude Code MCP] Console log test successful: {response}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] Console log test failed: {ex.Message}");
            }
        }

        private void TestSceneHierarchy()
        {
            var server = MCPUnityServer.Instance;
            if (!server.IsRunning)
            {
                Debug.LogWarning("[Claude Code MCP] Server is not running");
                return;
            }
            
            try
            {
                using (var client = new WebClient())
                {
                    string response = client.DownloadString("http://localhost:8090/mcp/resources/scenes_hierarchy");
                    Debug.Log($"[Claude Code MCP] Scene hierarchy retrieval successful:\n{response}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] Scene hierarchy retrieval failed: {ex.Message}");
            }
        }

        private void TestHotReload(bool optimized = true)
        {
            var server = MCPUnityServer.Instance;
            if (!server.IsRunning)
            {
                Debug.LogWarning("[Claude Code MCP] Server is not running");
                return;
            }
            
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add("Content-Type", "application/json");
                    string json = $@"{{""saveAssets"": true, ""optimized"": {optimized.ToString().ToLower()}}}";
                    string response = client.UploadString("http://localhost:8090/mcp/tools/hot_reload", json);
                    Debug.Log($"[Claude Code MCP] Hot reload test successful:\n{response}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] Hot reload test failed: {ex.Message}");
            }
        }

        private void TestForceCompilation()
        {
            var server = MCPUnityServer.Instance;
            if (!server.IsRunning)
            {
                Debug.LogWarning("[Claude Code MCP] Server is not running");
                return;
            }
            
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add("Content-Type", "application/json");
                    string json = @"{""forceUpdate"": true}";
                    string response = client.UploadString("http://localhost:8090/mcp/tools/force_compilation", json);
                    Debug.Log($"[Claude Code MCP] Force compilation test successful:\n{response}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] Force compilation test failed: {ex.Message}");
            }
        }

        private void TestCheckCompilationStatus()
        {
            var server = MCPUnityServer.Instance;
            if (!server.IsRunning)
            {
                Debug.LogWarning("[Claude Code MCP] Server is not running");
                return;
            }
            
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add("Content-Type", "application/json");
                    string response = client.UploadString("http://localhost:8090/mcp/tools/check_compilation_status", "{}");
                    Debug.Log($"[Claude Code MCP] Compilation status check successful:\n{response}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] Compilation status check failed: {ex.Message}");
            }
        }
    }
}