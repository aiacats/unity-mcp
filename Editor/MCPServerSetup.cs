using UnityEngine;
using UnityEditor;
using System.IO;

#if UNITY_EDITOR_WIN
using System.Diagnostics;
#endif

namespace ClaudeCodeMCP.Editor
{
    /// <summary>
    /// Unity Editor内からWindows側のMCPサーバーセットアップを実行
    /// </summary>
    public class MCPServerSetup
    {
        [MenuItem("Tools/Claude Code MCP/Setup: Install Dependencies")]
        public static void InstallDependencies()
        {
            UnityEngine.Debug.Log("[Claude Code MCP] Installing Node.js dependencies...");
            
            string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
            string batchFile = System.IO.Path.Combine(projectRoot, "setup-mcp-server.bat");
            
            if (!File.Exists(batchFile))
            {
                UnityEngine.Debug.LogError($"[Claude Code MCP] Batch file not found: {batchFile}");
                EditorUtility.DisplayDialog(
                    "File Not Found", 
                    $"Batch file not found:\n{batchFile}\n\nPlease ensure the setup files are in the project root.", 
                    "OK"
                );
                return;
            }
            
#if UNITY_EDITOR_WIN
            try
            {
                // Windows側でバッチファイルを実行
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = batchFile,
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    WorkingDirectory = projectRoot
                };
                
                Process process = Process.Start(startInfo);
                
                UnityEngine.Debug.Log($"[Claude Code MCP] Batch file started: {batchFile}");
                UnityEngine.Debug.Log("[Claude Code MCP] Please check the command prompt window for progress.");
                
                EditorUtility.DisplayDialog(
                    "MCP Server Setup", 
                    "Dependencies installation started!\n\nCheck the command prompt window for progress.\n\nThe process will:\n1. Install npm dependencies\n2. Test Unity connection\n3. Start MCP server", 
                    "OK"
                );
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[Claude Code MCP] Failed to start setup: {ex.Message}");
                EditorUtility.DisplayDialog(
                    "Setup Error", 
                    $"Failed to start setup:\n{ex.Message}", 
                    "OK"
                );
            }
#else
            EditorUtility.DisplayDialog(
                "Platform Not Supported", 
                "This setup method is only available on Windows.\n\nPlease install dependencies manually using:\nnpm install", 
                "OK"
            );
            UnityEngine.Debug.LogWarning("[Claude Code MCP] Process execution is only supported on Windows Editor.");
#endif
        }
        
        [MenuItem("Tools/Claude Code MCP/Setup: Install via Node.js")]
        public static void InstallViaNodeJS()
        {
            UnityEngine.Debug.Log("[Claude Code MCP] Installing dependencies via Node.js...");
            
            string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
            string serverPath = System.IO.Path.Combine(projectRoot, "Assets", "ClaudeCodeMCP", "Server");
            
#if UNITY_EDITOR_WIN
            // Try different Node.js paths
            string[] nodePaths = {
                @"C:\Program Files\nodejs\node.exe",
                @"C:\Program Files\Unity\Hub\Editor\2021.3.40f1\Editor\Data\Tools\nodejs\node.exe",
                @"node.exe"
            };
            
            string[] npmPaths = {
                @"C:\Program Files\nodejs\npm.cmd",
                @"npm.cmd",
                @"npm"
            };
            
            foreach (string nodePath in nodePaths)
            {
                if (File.Exists(nodePath) || nodePath.Contains("node.exe"))
                {
                    try
                    {
                        // Test Node.js version
                        ProcessStartInfo nodeTest = new ProcessStartInfo
                        {
                            FileName = nodePath,
                            Arguments = "--version",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };
                        
                        Process nodeProcess = Process.Start(nodeTest);
                        nodeProcess.WaitForExit();
                        string nodeVersion = nodeProcess.StandardOutput.ReadToEnd();
                        
                        UnityEngine.Debug.Log($"[Claude Code MCP] Found Node.js: {nodePath} - Version: {nodeVersion.Trim()}");
                        
                        // Install npm dependencies
                        foreach (string npmPath in npmPaths)
                        {
                            try
                            {
                                ProcessStartInfo npmInstall = new ProcessStartInfo
                                {
                                    FileName = npmPath,
                                    Arguments = "install",
                                    UseShellExecute = false,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true,
                                    CreateNoWindow = false,
                                    WorkingDirectory = serverPath
                                };
                                
                                UnityEngine.Debug.Log($"[Claude Code MCP] Running: {npmPath} install in {serverPath}");
                                Process npmProcess = Process.Start(npmInstall);
                                
                                EditorUtility.DisplayDialog(
                                    "MCP Dependencies", 
                                    $"Installing dependencies using:\n{npmPath}\n\nWorking directory:\n{serverPath}\n\nPlease wait for completion...", 
                                    "OK"
                                );
                                
                                return;
                            }
                            catch (System.Exception ex)
                            {
                                UnityEngine.Debug.LogWarning($"[Claude Code MCP] Failed to use {npmPath}: {ex.Message}");
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[Claude Code MCP] Failed to test {nodePath}: {ex.Message}");
                    }
                }
            }
            
            UnityEngine.Debug.LogError("[Claude Code MCP] No working Node.js installation found!");
            EditorUtility.DisplayDialog(
                "Error", 
                "No working Node.js installation found!\n\nPlease install Node.js from:\nhttps://nodejs.org/", 
                "OK"
            );
#else
            EditorUtility.DisplayDialog(
                "Platform Not Supported", 
                "Node.js process execution is only available on Windows.\n\nPlease install dependencies manually:\n1. Open terminal\n2. Navigate to: " + serverPath + "\n3. Run: npm install", 
                "OK"
            );
            UnityEngine.Debug.LogWarning("[Claude Code MCP] Process execution is only supported on Windows Editor.");
#endif
        }
        
        [MenuItem("Tools/Claude Code MCP/Setup: Test Connection")]
        public static void TestConnection()
        {
            UnityEngine.Debug.Log("[Claude Code MCP] Testing MCP connection...");
            
            try
            {
                // Test Unity HTTP server safely
                bool serverRunning = false;
                string serverStatus = "Not initialized";
                
                try
                {
                    var server = ClaudeCodeMCP.Editor.Core.MCPUnityServer.Instance;
                    serverRunning = server.IsRunning;
                    serverStatus = serverRunning ? "Running on port 8090" : "Not running";
                }
                catch (System.Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[Claude Code MCP] Server check failed: {ex.Message}");
                    serverStatus = $"Error: {ex.Message}";
                }
                
                if (serverRunning)
                {
                    UnityEngine.Debug.Log($"[Claude Code MCP] ✓ Unity HTTP Server: {serverStatus}");
                    
                    // Test if dependencies are installed
                    string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
                    string nodeModules = System.IO.Path.Combine(projectRoot, "Assets", "ClaudeCodeMCP", "Server", "node_modules");
                    
                    if (Directory.Exists(nodeModules))
                    {
                        UnityEngine.Debug.Log("[Claude Code MCP] ✓ Node.js dependencies are installed");
                        EditorUtility.DisplayDialog(
                            "Connection Test", 
                            "✓ Unity HTTP Server: Running\n✓ Dependencies: Installed\n\nReady for MCP connection!\n\nNext step: Start MCP server", 
                            "OK"
                        );
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning("[Claude Code MCP] ✗ Node.js dependencies not found");
                        EditorUtility.DisplayDialog(
                            "Connection Test", 
                            "✓ Unity HTTP Server: Running\n✗ Dependencies: Not installed\n\nPlease install dependencies first.", 
                            "OK"
                        );
                    }
                }
                else
                {
                    UnityEngine.Debug.LogWarning($"[Claude Code MCP] ✗ Unity HTTP Server: {serverStatus}");
                    EditorUtility.DisplayDialog(
                        "Connection Test", 
                        $"✗ Unity HTTP Server: {serverStatus}\n\nTry starting the server:\nTools/Claude Code MCP/Start Server", 
                        "OK"
                    );
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[Claude Code MCP] Connection test failed: {ex.Message}");
                EditorUtility.DisplayDialog(
                    "Connection Test Error", 
                    $"Failed to test connection:\n{ex.Message}\n\nCheck the console for details.", 
                    "OK"
                );
            }
        }
        
        [MenuItem("Tools/Claude Code MCP/Server: Start HTTP Server")]
        public static void StartServer()
        {
            UnityEngine.Debug.Log("[Claude Code MCP] Starting HTTP Server...");
            
            try
            {
                var server = ClaudeCodeMCP.Editor.Core.MCPUnityServer.Instance;
                server.StartServer();
                
                EditorUtility.DisplayDialog(
                    "MCP Server", 
                    "HTTP Server start command sent.\n\nCheck the console for status messages.", 
                    "OK"
                );
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[Claude Code MCP] Failed to start server: {ex.Message}");
                EditorUtility.DisplayDialog(
                    "Server Start Error", 
                    $"Failed to start server:\n{ex.Message}", 
                    "OK"
                );
            }
        }
        
        [MenuItem("Tools/Claude Code MCP/Server: Stop HTTP Server")]
        public static void StopServer()
        {
            UnityEngine.Debug.Log("[Claude Code MCP] Stopping HTTP Server...");
            
            try
            {
                var server = ClaudeCodeMCP.Editor.Core.MCPUnityServer.Instance;
                server.StopServer();
                
                EditorUtility.DisplayDialog(
                    "MCP Server", 
                    "HTTP Server stopped.", 
                    "OK"
                );
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[Claude Code MCP] Failed to stop server: {ex.Message}");
                EditorUtility.DisplayDialog(
                    "Server Stop Error", 
                    $"Failed to stop server:\n{ex.Message}", 
                    "OK"
                );
            }
        }
        
        [MenuItem("Tools/Claude Code MCP/Development/Hot Reload Scripts")]
        public static void HotReload()
        {
            UnityEngine.Debug.Log("[Claude Code MCP] Performing hot reload...");
            
            try
            {
                var server = ClaudeCodeMCP.Editor.Core.MCPUnityServer.Instance;
                if (server.IsRunning)
                {
                    // Call the hot reload endpoint directly
                    string json = @"{""saveAssets"": true, ""optimized"": true}";
                    using (var client = new System.Net.WebClient())
                    {
                        client.Headers.Add("Content-Type", "application/json");
                        string response = client.UploadString("http://localhost:8090/mcp/tools/hot_reload", json);
                        UnityEngine.Debug.Log($"[Claude Code MCP] Hot reload response: {response}");
                    }
                    
                    EditorUtility.DisplayDialog(
                        "Hot Reload", 
                        "Hot reload triggered successfully!\n\nCheck console for details.", 
                        "OK"
                    );
                }
                else
                {
                    // Fallback to direct Unity compilation
                    AssetDatabase.SaveAssets();
                    UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
                    
                    UnityEngine.Debug.Log("[Claude Code MCP] Hot reload triggered (fallback method)");
                    EditorUtility.DisplayDialog(
                        "Hot Reload", 
                        "Hot reload triggered using fallback method.\n\n(HTTP Server not running)", 
                        "OK"
                    );
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[Claude Code MCP] Hot reload failed: {ex.Message}");
                
                // Fallback to direct Unity compilation
                try
                {
                    AssetDatabase.SaveAssets();
                    UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
                    UnityEngine.Debug.Log("[Claude Code MCP] Fallback hot reload successful");
                    
                    EditorUtility.DisplayDialog(
                        "Hot Reload", 
                        "Hot reload completed using fallback method.\n\nHTTP method failed, but direct compilation succeeded.", 
                        "OK"
                    );
                }
                catch (System.Exception fallbackEx)
                {
                    EditorUtility.DisplayDialog(
                        "Hot Reload Error", 
                        $"Hot reload failed:\n{ex.Message}\n\nFallback also failed:\n{fallbackEx.Message}", 
                        "OK"
                    );
                }
            }
        }
        
        [MenuItem("Tools/Claude Code MCP/Development/Force Full Compilation")]
        public static void ForceCompilation()
        {
            UnityEngine.Debug.Log("[Claude Code MCP] Forcing full compilation...");
            
            try
            {
                var server = ClaudeCodeMCP.Editor.Core.MCPUnityServer.Instance;
                if (server.IsRunning)
                {
                    // Call the force compilation endpoint
                    string json = @"{""forceUpdate"": true}";
                    using (var client = new System.Net.WebClient())
                    {
                        client.Headers.Add("Content-Type", "application/json");
                        string response = client.UploadString("http://localhost:8090/mcp/tools/force_compilation", json);
                        UnityEngine.Debug.Log($"[Claude Code MCP] Force compilation response: {response}");
                    }
                    
                    EditorUtility.DisplayDialog(
                        "Force Compilation", 
                        "Force compilation triggered successfully!\n\nCheck console for details.", 
                        "OK"
                    );
                }
                else
                {
                    // Fallback to direct Unity compilation
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
                    
                    UnityEngine.Debug.Log("[Claude Code MCP] Force compilation triggered (fallback method)");
                    EditorUtility.DisplayDialog(
                        "Force Compilation", 
                        "Force compilation triggered using fallback method.\n\n(HTTP Server not running)", 
                        "OK"
                    );
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[Claude Code MCP] Force compilation failed: {ex.Message}");
                
                // Fallback to direct Unity compilation
                try
                {
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
                    UnityEngine.Debug.Log("[Claude Code MCP] Fallback force compilation successful");
                    
                    EditorUtility.DisplayDialog(
                        "Force Compilation", 
                        "Force compilation completed using fallback method.\n\nHTTP method failed, but direct compilation succeeded.", 
                        "OK"
                    );
                }
                catch (System.Exception fallbackEx)
                {
                    EditorUtility.DisplayDialog(
                        "Force Compilation Error", 
                        $"Force compilation failed:\n{ex.Message}\n\nFallback also failed:\n{fallbackEx.Message}", 
                        "OK"
                    );
                }
            }
        }
    }
}