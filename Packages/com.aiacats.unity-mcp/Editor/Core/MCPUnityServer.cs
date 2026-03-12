using System;
using System.Net;
using System.Text;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.IO;
using UnityEditor.Compilation;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using UnityEditor.TestTools.TestRunner.Api;

namespace ClaudeCodeMCP.Editor.Core
{
    /// <summary>
    /// Unity 2021.3対応のMCP HTTPサーバー
    /// WebSocketの代わりにHTTP RESTを使用して安定した通信を実現
    /// </summary>
    [InitializeOnLoad]
    public class MCPUnityServer : IDisposable
    {
        private static MCPUnityServer _instance;
        private static bool _isInitializing = false;
        private static bool _delayCallScheduled = false;
        private static bool _isDomainReloading = false;
        private HttpListener _httpListener;
        private Thread _requestHandlerThread;
        private bool _isRunning = false;
        private int _port = 8090;
        
        // Main thread execution queue
        private readonly Queue<System.Action> _mainThreadQueue = new Queue<System.Action>();
        private readonly object _queueLock = new object();
        
        // Response handling for async operations
        private readonly Dictionary<string, ManualResetEvent> _pendingRequests = new Dictionary<string, ManualResetEvent>();
        private readonly Dictionary<string, string> _responses = new Dictionary<string, string>();
        private readonly object _responseLock = new object();
        
        // Compilation error tracking
        private readonly List<CompilerMessage> _lastCompilationErrors = new List<CompilerMessage>();
        private readonly object _compilationLock = new object();
        private string _lastCompiledAssembly = "";
        private DateTime _lastCompilationTime = DateTime.MinValue;
        
        // Console log tracking
        private readonly List<LogEntry> _consoleLogs = new List<LogEntry>();
        private readonly object _consoleLogLock = new object();
        private const int MAX_LOG_ENTRIES = 100;
        
        private class LogEntry
        {
            public string message;
            public string stackTrace;
            public LogType type;
            public DateTime timestamp;
        }
        
        public static MCPUnityServer Instance
        {
            get
            {
                if (_instance == null && !_isInitializing)
                {
                    _isInitializing = true;
                    try
                    {
                        _instance = new MCPUnityServer();
                    }
                    finally
                    {
                        _isInitializing = false;
                    }
                }
                return _instance;
            }
        }

        public bool IsRunning => _isRunning;

        static MCPUnityServer()
        {
            // ドメインリロード時に静的フィールドをリセット
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReloadStatic;

            // delayCallは一度だけスケジュール
            if (!_delayCallScheduled)
            {
                _delayCallScheduled = true;
                EditorApplication.delayCall += InitializeServerDelayed;
            }
        }

        private static void InitializeServerDelayed()
        {
            _delayCallScheduled = false;

            // ドメインリロード中またはコンパイル中は初期化しない
            if (_isDomainReloading || EditorApplication.isCompiling)
            {
                // 後で再試行
                EditorApplication.delayCall += InitializeServerDelayed;
                _delayCallScheduled = true;
                return;
            }

            if (_instance == null && !_isInitializing)
            {
                var instance = Instance;
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            _isDomainReloading = true;
            _delayCallScheduled = false;

            if (_instance != null)
            {
                try
                {
                    _instance.StopServerAndWait();
                }
                catch { /* Ignore cleanup errors */ }
            }
            _instance = null;
            _isInitializing = false;
        }

        private static void OnAfterAssemblyReloadStatic()
        {
            _isDomainReloading = false;
        }

        private MCPUnityServer()
        {
            EditorApplication.quitting += OnEditorQuitting;
            CompilationPipeline.assemblyCompilationFinished += OnAfterAssemblyReload;
            EditorApplication.update += ProcessMainThreadQueue;

            // Console log callback
            Application.logMessageReceived += OnLogMessageReceived;

            // Additional stability hooks
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            // Auto-start server with delay to avoid initialization issues
            // ドメインリロード中でなければサーバーを起動
            if (!_isDomainReloading && !EditorApplication.isCompiling)
            {
                EditorApplication.delayCall += () => {
                    if (_isDomainReloading || EditorApplication.isCompiling)
                    {
                        return;
                    }

                    try
                    {
                        StartServer();
                    }
                    catch (System.Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[Claude Code MCP] Auto-start failed: {ex.Message}. You can start manually from Tools menu.");
                    }
                };
            }
        }
        
        /// <summary>
        /// Execute action on main thread
        /// </summary>
        private void ExecuteOnMainThread(System.Action action)
        {
            lock (_queueLock)
            {
                _mainThreadQueue.Enqueue(action);
            }
        }
        
        // ヘルスチェックの間隔管理
        private float _lastHealthCheckTime = 0f;
        private const float HEALTH_CHECK_INTERVAL = 10f;

        /// <summary>
        /// Process queued main thread actions and monitor server health
        /// </summary>
        private void ProcessMainThreadQueue()
        {
            // ドメインリロード中は何もしない
            if (_isDomainReloading)
            {
                return;
            }

            lock (_queueLock)
            {
                while (_mainThreadQueue.Count > 0)
                {
                    try
                    {
                        var action = _mainThreadQueue.Dequeue();
                        action?.Invoke();
                    }
                    catch (System.Exception ex)
                    {
                        UnityEngine.Debug.LogError($"[Claude Code MCP] Main thread execution error: {ex.Message}");
                    }
                }
            }

            // 定期的にサーバー状態をチェック（安全なタイミングでのみ）
            try
            {
                float currentTime = Time.realtimeSinceStartup;
                if (currentTime - _lastHealthCheckTime >= HEALTH_CHECK_INTERVAL)
                {
                    _lastHealthCheckTime = currentTime;
                    CheckAndMaintainServerHealth();
                }
            }
            catch
            {
                // Time APIアクセス中のエラーを無視
            }
        }

        private void CheckAndMaintainServerHealth()
        {
            // コンパイル中やドメインリロード中は何もしない
            if (_isDomainReloading || EditorApplication.isCompiling)
            {
                return;
            }

            if (!_isRunning)
            {
                Debug.LogWarning("[Claude Code MCP] Server not running, attempting auto-restart...");
                try
                {
                    StartServer();
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Claude Code MCP] Auto-restart failed: {ex.Message}");
                }
            }
            else if (_isRunning && _httpListener != null && !_httpListener.IsListening)
            {
                Debug.LogWarning("[Claude Code MCP] Server marked as running but listener not active, restarting...");
                try
                {
                    StopServer();
                    StartServer();
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Claude Code MCP] Server restart failed: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Execute function on main thread and wait for result
        /// </summary>
        private string ExecuteOnMainThreadWithResult(System.Func<string> function, int timeoutMs = 5000)
        {
            string requestId = System.Guid.NewGuid().ToString();
            string result = null;
            
            var resetEvent = new ManualResetEvent(false);
            
            lock (_responseLock)
            {
                _pendingRequests[requestId] = resetEvent;
            }
            
            ExecuteOnMainThread(() => {
                try
                {
                    result = function();
                    
                    lock (_responseLock)
                    {
                        _responses[requestId] = result;
                        if (_pendingRequests.TryGetValue(requestId, out var evt))
                        {
                            evt.Set();
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    result = CreateErrorResponse("main_thread_error", ex.Message);
                    
                    lock (_responseLock)
                    {
                        _responses[requestId] = result;
                        if (_pendingRequests.TryGetValue(requestId, out var evt))
                        {
                            evt.Set();
                        }
                    }
                }
            });
            
            // Wait for result with timeout
            if (resetEvent.WaitOne(timeoutMs))
            {
                lock (_responseLock)
                {
                    if (_responses.TryGetValue(requestId, out result))
                    {
                        _responses.Remove(requestId);
                    }
                    _pendingRequests.Remove(requestId);
                }
            }
            else
            {
                // Timeout
                lock (_responseLock)
                {
                    _pendingRequests.Remove(requestId);
                    _responses.Remove(requestId);
                }
                result = CreateErrorResponse("timeout", "Operation timed out waiting for main thread execution");
            }
            
            resetEvent.Dispose();
            return result ?? CreateErrorResponse("unknown_error", "Unknown error occurred");
        }

        public void StartServer()
        {
            if (_isRunning && _httpListener != null && _httpListener.IsListening)
            {
                Debug.Log("[Claude Code MCP] Server is already running and listening");
                return;
            }
            
            // Clean shutdown any existing listener
            if (_httpListener != null)
            {
                try
                {
                    _httpListener.Stop();
                    _httpListener.Close();
                }
                catch { /* Ignore cleanup errors */ }
                _httpListener = null;
            }
            
            _isRunning = false;

            try
            {
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://+:{_port}/");
                
                // Try to start the listener
                _httpListener.Start();
                _isRunning = true;

                Debug.Log($"[Claude Code MCP] HTTP Server started on port {_port}");

                // Start handling requests in background thread
                _requestHandlerThread = new Thread(HandleRequests)
                {
                    Name = "MCP-RequestHandler",
                    IsBackground = true
                };
                _requestHandlerThread.Start();
            }
            catch (HttpListenerException httpEx)
            {
                Debug.LogError($"[Claude Code MCP] Failed to start HTTP server on port {_port}: {httpEx.Message}");
                Debug.LogError("[Claude Code MCP] This might be due to insufficient permissions or port conflicts.");
                Debug.LogError("[Claude Code MCP] Try running Unity as Administrator or check if another process is using the port.");
                
                // Try alternative approach
                TryStartAlternativeServer();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] Failed to start server: {ex.Message}");
                Debug.LogError($"[Claude Code MCP] Exception type: {ex.GetType().Name}");
            }
        }
        
        private void TryStartAlternativeServer()
        {
            Debug.Log("[Claude Code MCP] Attempting to start server with alternative configuration...");
            
            try
            {
                // Clean up previous instance
                _httpListener?.Close();
                _httpListener = null;
                
                // Try with a different port range
                for (int port = 8090; port <= 8099; port++)
                {
                    try
                    {
                        _httpListener = new HttpListener();
                        _httpListener.Prefixes.Add($"http://+:{port}/");
                        _httpListener.Start();
                        
                        _port = port;  // Update port field (need to make it non-readonly)
                        _isRunning = true;

                        Debug.Log($"[Claude Code MCP] HTTP Server started on alternative port {port}");

                        _requestHandlerThread = new Thread(HandleRequests)
                        {
                            Name = "MCP-RequestHandler",
                            IsBackground = true
                        };
                        _requestHandlerThread.Start();
                        return;
                    }
                    catch
                    {
                        _httpListener?.Close();
                        _httpListener = null;
                        continue;
                    }
                }
                
                Debug.LogError("[Claude Code MCP] Failed to start server on any port between 8090-8099");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] Alternative server start failed: {ex.Message}");
            }
        }

        public void StopServer()
        {
            StopServerInternal(waitForThread: false);
        }

        /// <summary>
        /// サーバーを停止し、バックグラウンドスレッドの終了を待機
        /// </summary>
        public void StopServerAndWait()
        {
            StopServerInternal(waitForThread: true);
        }

        private void StopServerInternal(bool waitForThread)
        {
            try
            {
                _isRunning = false;

                if (_httpListener != null)
                {
                    try
                    {
                        if (_httpListener.IsListening)
                        {
                            _httpListener.Stop();
                        }
                        _httpListener.Close();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Claude Code MCP] Warning during listener cleanup: {ex.Message}");
                    }
                    finally
                    {
                        _httpListener = null;
                    }
                }

                // バックグラウンドスレッドの終了を待機
                if (waitForThread && _requestHandlerThread != null && _requestHandlerThread.IsAlive)
                {
                    try
                    {
                        // 最大500ms待機
                        if (!_requestHandlerThread.Join(500))
                        {
                            Debug.LogWarning("[Claude Code MCP] Request handler thread did not terminate in time");
                        }
                    }
                    catch { /* Ignore */ }
                }
                _requestHandlerThread = null;

                Debug.Log("[Claude Code MCP] Server stopped cleanly");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] Error stopping server: {ex.Message}");
            }
        }

        private void HandleRequests()
        {
            Debug.Log("[Claude Code MCP] Request handler thread started");
            
            while (_isRunning && _httpListener != null)
            {
                try
                {
                    if (!_httpListener.IsListening)
                    {
                        Debug.LogWarning("[Claude Code MCP] Listener not active, breaking request loop");
                        break;
                    }
                    
                    var context = _httpListener.GetContext();
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => ProcessRequest(context));
                }
                catch (ObjectDisposedException)
                {
                    Debug.Log("[Claude Code MCP] Request handler stopped (ObjectDisposed)");
                    break;
                }
                catch (HttpListenerException httpEx) when (httpEx.ErrorCode == 995) // ERROR_OPERATION_ABORTED
                {
                    Debug.Log("[Claude Code MCP] Request handler stopped (Operation Aborted)");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Claude Code MCP] Error handling request: {ex.Message} ({ex.GetType().Name})");
                    
                    // If we get repeated errors, break to prevent spam
                    if (ex is HttpListenerException || ex is InvalidOperationException)
                    {
                        Debug.LogWarning("[Claude Code MCP] Breaking request loop due to listener error");
                        _isRunning = false;
                        break;
                    }
                }
            }
            
            Debug.Log("[Claude Code MCP] Request handler thread ended");
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                // Set CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                string requestBody = "";
                if (request.HttpMethod == "POST")
                {
                    using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                    {
                        requestBody = reader.ReadToEnd();
                    }
                }

                string responseJson = ProcessMCPRequest(request.Url.AbsolutePath, requestBody);

                byte[] buffer = Encoding.UTF8.GetBytes(responseJson);
                response.ContentType = "application/json";
                response.ContentLength64 = buffer.Length;
                response.StatusCode = 200;

                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.Close();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] Error processing request: {ex.Message}");
                
                try
                {
                    response.StatusCode = 500;
                    response.Close();
                }
                catch { }
            }
        }

        private string ProcessMCPRequest(string path, string requestBody)
        {
            try
            {
                Debug.Log($"[Claude Code MCP] Processing request: {path}");
                Debug.Log($"[Claude Code MCP] Request body: {requestBody}");

                switch (path)
                {
                    case "/mcp/tools/send_console_log":
                        return HandleSendConsoleLog(requestBody);
                    
                    case "/mcp/tools/select_gameobject":
                        return HandleSelectGameObject(requestBody);
                    
                    case "/mcp/tools/update_gameobject":
                        return HandleUpdateGameObject(requestBody);
                    
                    case "/mcp/tools/update_component":
                        return HandleUpdateComponent(requestBody);
                    
                    case "/mcp/tools/get_console_logs":
                        return HandleGetConsoleLogs(requestBody);
                    
                    case "/mcp/tools/get_compilation_errors":
                        return HandleGetCompilationErrors(requestBody);
                    
                    case "/mcp/tools/auto_fix_errors":
                        return HandleAutoFixErrors(requestBody);
                    
                    case "/mcp/tools/execute_menu_item":
                        return HandleExecuteMenuItem(requestBody);
                    
                    case "/mcp/tools/hot_reload":
                        return HandleHotReload(requestBody);
                    
                    case "/mcp/tools/force_compilation":
                        return HandleForceCompilation(requestBody);
                    
                    case "/mcp/tools/check_compilation_status":
                        return HandleCheckCompilationStatus(requestBody);
                    
                    case "/mcp/tools/delete_gameobject":
                        return HandleDeleteGameObject(requestBody);

                    case "/mcp/tools/remove_component":
                        return HandleRemoveComponent(requestBody);

                    case "/mcp/tools/add_package":
                        return HandleAddPackage(requestBody);
                    
                    case "/mcp/tools/run_tests":
                        return HandleRunTests(requestBody);
                    
                    case "/mcp/tools/add_asset_to_scene":
                        return HandleAddAssetToScene(requestBody);
                    
                    case "/mcp/tools/get_gameobject_info":
                        return HandleGetGameObjectInfo(requestBody);

                    case "/mcp/tools/get_component_properties":
                        return HandleGetComponentProperties(requestBody);

                    case "/mcp/tools/save_scene":
                        return HandleSaveScene(requestBody);

                    case "/mcp/tools/open_scene":
                        return HandleOpenScene(requestBody);

                    case "/mcp/tools/play_mode_control":
                        return HandlePlayModeControl(requestBody);

                    case "/mcp/tools/find_assets":
                        return HandleFindAssets(requestBody);

                    case "/mcp/tools/create_material":
                        return HandleCreateMaterial(requestBody);

                    case "/mcp/tools/screenshot":
                        return HandleScreenshot(requestBody);

                    case "/mcp/resources/scenes_hierarchy":
                        return HandleGetScenesHierarchy();

                    case "/mcp/ping":
                        return CreateSuccessResponse("pong", "Claude Code MCP Server is running");

                    default:
                        return CreateErrorResponse("unknown_endpoint", $"Unknown endpoint: {path}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] Error in ProcessMCPRequest: {ex.Message}");
                return CreateErrorResponse("internal_error", ex.Message);
            }
        }

        #region Tool Handlers

        private string HandleSendConsoleLog(string requestBody)
        {
            try
            {
                var request = JObject.Parse(requestBody);
                string message = request["message"]?.ToString() ?? "Test message";
                string type = request["type"]?.ToString()?.ToLower() ?? "info";

                switch (type)
                {
                    case "error":
                        Debug.LogError($"[Claude Code MCP] {message}");
                        break;
                    case "warning":
                        Debug.LogWarning($"[Claude Code MCP] {message}");
                        break;
                    default:
                        Debug.Log($"[Claude Code MCP] {message}");
                        break;
                }

                return CreateSuccessResponse("console_log_sent", $"Message logged: {message}");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse("console_log_error", ex.Message);
            }
        }

        private string HandleSelectGameObject(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var request = JObject.Parse(requestBody);
                    string objectPath = request["objectPath"]?.ToString();
                    int? instanceId = request["instanceId"]?.ToObject<int?>();

                    GameObject target = null;

                    if (instanceId.HasValue)
                    {
                        target = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
                    }
                    else if (!string.IsNullOrEmpty(objectPath))
                    {
                        target = GameObject.Find(objectPath);
                    }

                    if (target != null)
                    {
                        Selection.activeGameObject = target;
                        EditorGUIUtility.PingObject(target);
                        return CreateSuccessResponse("gameobject_selected", $"Selected: {target.name}");
                    }
                    else
                    {
                        return CreateErrorResponse("gameobject_not_found", "GameObject not found");
                    }
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("select_gameobject_error", ex.Message);
                }
            });
        }

        private string HandleUpdateGameObject(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var request = JObject.Parse(requestBody);
                    string objectPath = request["objectPath"]?.ToString();
                    var gameObjectData = request["gameObjectData"] as JObject;

                    GameObject target = null;

                    if (!string.IsNullOrEmpty(objectPath))
                    {
                        target = GameObject.Find(objectPath);
                        
                        // If not found, create new GameObject
                        if (target == null)
                        {
                            target = new GameObject(objectPath);
                            Undo.RegisterCreatedObjectUndo(target, "Create GameObject via MCP");
                        }
                    }

                    if (target != null && gameObjectData != null)
                    {
                        Undo.RecordObject(target, "Update GameObject via MCP");

                        // Update properties
                        if (gameObjectData["name"] != null)
                            target.name = gameObjectData["name"].ToString();
                        
                        if (gameObjectData["activeSelf"] != null)
                            target.SetActive(gameObjectData["activeSelf"].ToObject<bool>());
                        
                        if (gameObjectData["tag"] != null)
                            target.tag = gameObjectData["tag"].ToString();

                        EditorUtility.SetDirty(target);

                        return CreateSuccessResponse("gameobject_updated", $"Updated: {target.name}");
                    }

                    return CreateErrorResponse("gameobject_update_failed", "Failed to update GameObject");
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("update_gameobject_error", ex.Message);
                }
            });
        }

        private string HandleUpdateComponent(string requestBody)
        {
            try
            {
                var request = JObject.Parse(requestBody);
                string objectPath = request["objectPath"]?.ToString();
                string componentName = request["componentName"]?.ToString();
                var componentData = request["componentData"] as JObject;

                GameObject target = GameObject.Find(objectPath);
                if (target == null)
                {
                    return CreateErrorResponse("gameobject_not_found", "GameObject not found");
                }

                // Get or add component
                var componentType = System.Type.GetType($"UnityEngine.{componentName}, UnityEngine") ??
                                   System.Type.GetType($"UnityEngine.{componentName}, UnityEngine.CoreModule");

                if (componentType == null)
                {
                    return CreateErrorResponse("component_type_not_found", $"Component type not found: {componentName}");
                }

                var component = target.GetComponent(componentType);
                if (component == null)
                {
                    component = target.AddComponent(componentType);
                    Undo.RegisterCreatedObjectUndo(component, "Add Component via MCP");
                }

                Undo.RecordObject(component, "Update Component via MCP");

                // Update component properties
                if (componentData != null)
                {
                    foreach (var property in componentData)
                    {
                        try
                        {
                            var field = componentType.GetField(property.Key);
                            var prop = componentType.GetProperty(property.Key);

                            if (field != null)
                            {
                                var value = property.Value.ToObject(field.FieldType);
                                field.SetValue(component, value);
                            }
                            else if (prop != null && prop.CanWrite)
                            {
                                var value = property.Value.ToObject(prop.PropertyType);
                                prop.SetValue(component, value);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[Claude Code MCP] Failed to set {property.Key}: {ex.Message}");
                        }
                    }
                }

                EditorUtility.SetDirty(target);

                return CreateSuccessResponse("component_updated", $"Updated {componentName} on {target.name}");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse("update_component_error", ex.Message);
            }
        }

        private string HandleDeleteGameObject(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var request = JObject.Parse(requestBody);
                    string objectPath = request["objectPath"]?.ToString();
                    int? instanceId = request["instanceId"]?.ToObject<int?>();

                    GameObject target = null;

                    if (instanceId.HasValue)
                    {
                        target = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
                    }
                    else if (!string.IsNullOrEmpty(objectPath))
                    {
                        target = GameObject.Find(objectPath);
                    }

                    if (target == null)
                    {
                        return CreateErrorResponse("gameobject_not_found", "GameObject not found");
                    }

                    string targetName = target.name;
                    Undo.DestroyObjectImmediate(target);

                    return CreateSuccessResponse("gameobject_deleted", $"Deleted GameObject: {targetName}");
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("delete_gameobject_error", ex.Message);
                }
            });
        }

        private string HandleRemoveComponent(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var request = JObject.Parse(requestBody);
                    string objectPath = request["objectPath"]?.ToString();
                    int? instanceId = request["instanceId"]?.ToObject<int?>();
                    string componentName = request["componentName"]?.ToString();

                    if (string.IsNullOrEmpty(componentName))
                    {
                        return CreateErrorResponse("missing_parameter", "componentName is required");
                    }

                    GameObject target = null;

                    if (instanceId.HasValue)
                    {
                        target = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
                    }
                    else if (!string.IsNullOrEmpty(objectPath))
                    {
                        target = GameObject.Find(objectPath);
                    }

                    if (target == null)
                    {
                        return CreateErrorResponse("gameobject_not_found", "GameObject not found");
                    }

                    var componentType = System.Type.GetType($"UnityEngine.{componentName}, UnityEngine") ??
                                       System.Type.GetType($"UnityEngine.{componentName}, UnityEngine.CoreModule");

                    if (componentType == null)
                    {
                        return CreateErrorResponse("component_type_not_found", $"Component type not found: {componentName}");
                    }

                    var component = target.GetComponent(componentType);
                    if (component == null)
                    {
                        return CreateErrorResponse("component_not_found", $"Component {componentName} not found on {target.name}");
                    }

                    Undo.DestroyObjectImmediate(component);

                    return CreateSuccessResponse("component_removed", $"Removed {componentName} from {target.name}");
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("remove_component_error", ex.Message);
                }
            });
        }

        private string HandleGetGameObjectInfo(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var request = JObject.Parse(requestBody);
                    string objectPath = request["objectPath"]?.ToString();
                    int? instanceId = request["instanceId"]?.ToObject<int?>();

                    GameObject target = null;

                    if (instanceId.HasValue)
                    {
                        target = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
                    }
                    else if (!string.IsNullOrEmpty(objectPath))
                    {
                        target = GameObject.Find(objectPath);
                    }

                    if (target == null)
                    {
                        return CreateErrorResponse("gameobject_not_found", "GameObject not found");
                    }

                    var info = new JObject
                    {
                        ["name"] = target.name,
                        ["instanceId"] = target.GetInstanceID(),
                        ["isActive"] = target.activeSelf,
                        ["isActiveInHierarchy"] = target.activeInHierarchy,
                        ["tag"] = target.tag,
                        ["layer"] = target.layer,
                        ["layerName"] = LayerMask.LayerToName(target.layer),
                        ["isStatic"] = target.isStatic
                    };

                    // Transform
                    var transform = target.transform;
                    info["transform"] = new JObject
                    {
                        ["position"] = new JObject { ["x"] = transform.position.x, ["y"] = transform.position.y, ["z"] = transform.position.z },
                        ["localPosition"] = new JObject { ["x"] = transform.localPosition.x, ["y"] = transform.localPosition.y, ["z"] = transform.localPosition.z },
                        ["rotation"] = new JObject { ["x"] = transform.eulerAngles.x, ["y"] = transform.eulerAngles.y, ["z"] = transform.eulerAngles.z },
                        ["localRotation"] = new JObject { ["x"] = transform.localEulerAngles.x, ["y"] = transform.localEulerAngles.y, ["z"] = transform.localEulerAngles.z },
                        ["localScale"] = new JObject { ["x"] = transform.localScale.x, ["y"] = transform.localScale.y, ["z"] = transform.localScale.z }
                    };

                    // Parent
                    if (transform.parent != null)
                    {
                        info["parentName"] = transform.parent.name;
                        info["parentInstanceId"] = transform.parent.gameObject.GetInstanceID();
                    }

                    // Children count
                    info["childCount"] = transform.childCount;

                    // Components
                    var components = new JArray();
                    foreach (var comp in target.GetComponents<Component>())
                    {
                        if (comp == null) continue;
                        components.Add(new JObject
                        {
                            ["type"] = comp.GetType().Name,
                            ["fullType"] = comp.GetType().FullName,
                            ["instanceId"] = comp.GetInstanceID()
                        });
                    }
                    info["components"] = components;

                    return CreateSuccessResponse("gameobject_info", info);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("get_gameobject_info_error", ex.Message);
                }
            });
        }

        private string HandleGetComponentProperties(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var request = JObject.Parse(requestBody);
                    string objectPath = request["objectPath"]?.ToString();
                    int? instanceId = request["instanceId"]?.ToObject<int?>();
                    string componentName = request["componentName"]?.ToString();

                    if (string.IsNullOrEmpty(componentName))
                    {
                        return CreateErrorResponse("missing_parameter", "componentName is required");
                    }

                    GameObject target = null;

                    if (instanceId.HasValue)
                    {
                        target = EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
                    }
                    else if (!string.IsNullOrEmpty(objectPath))
                    {
                        target = GameObject.Find(objectPath);
                    }

                    if (target == null)
                    {
                        return CreateErrorResponse("gameobject_not_found", "GameObject not found");
                    }

                    var componentType = System.Type.GetType($"UnityEngine.{componentName}, UnityEngine") ??
                                       System.Type.GetType($"UnityEngine.{componentName}, UnityEngine.CoreModule") ??
                                       System.Type.GetType($"UnityEngine.{componentName}, UnityEngine.PhysicsModule") ??
                                       System.Type.GetType($"UnityEngine.{componentName}, UnityEngine.Physics2DModule") ??
                                       System.Type.GetType($"UnityEngine.{componentName}, UnityEngine.UIModule") ??
                                       System.Type.GetType($"UnityEngine.{componentName}, UnityEngine.AnimationModule");

                    if (componentType == null)
                    {
                        // Try to find by iterating components
                        foreach (var comp in target.GetComponents<Component>())
                        {
                            if (comp != null && comp.GetType().Name == componentName)
                            {
                                componentType = comp.GetType();
                                break;
                            }
                        }
                    }

                    if (componentType == null)
                    {
                        return CreateErrorResponse("component_type_not_found", $"Component type not found: {componentName}");
                    }

                    var component = target.GetComponent(componentType);
                    if (component == null)
                    {
                        return CreateErrorResponse("component_not_found", $"Component {componentName} not found on {target.name}");
                    }

                    var properties = new JObject();
                    var serializedObject = new SerializedObject(component);
                    var iterator = serializedObject.GetIterator();
                    bool enterChildren = true;

                    while (iterator.NextVisible(enterChildren))
                    {
                        enterChildren = false;
                        try
                        {
                            var propName = iterator.name;
                            if (propName == "m_Script") continue;

                            switch (iterator.propertyType)
                            {
                                case SerializedPropertyType.Integer:
                                    properties[propName] = iterator.intValue;
                                    break;
                                case SerializedPropertyType.Boolean:
                                    properties[propName] = iterator.boolValue;
                                    break;
                                case SerializedPropertyType.Float:
                                    properties[propName] = iterator.floatValue;
                                    break;
                                case SerializedPropertyType.String:
                                    properties[propName] = iterator.stringValue;
                                    break;
                                case SerializedPropertyType.Enum:
                                    properties[propName] = iterator.enumDisplayNames[iterator.enumValueIndex];
                                    break;
                                case SerializedPropertyType.Vector2:
                                    var v2 = iterator.vector2Value;
                                    properties[propName] = new JObject { ["x"] = v2.x, ["y"] = v2.y };
                                    break;
                                case SerializedPropertyType.Vector3:
                                    var v3 = iterator.vector3Value;
                                    properties[propName] = new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
                                    break;
                                case SerializedPropertyType.Vector4:
                                    var v4 = iterator.vector4Value;
                                    properties[propName] = new JObject { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
                                    break;
                                case SerializedPropertyType.Color:
                                    var color = iterator.colorValue;
                                    properties[propName] = new JObject { ["r"] = color.r, ["g"] = color.g, ["b"] = color.b, ["a"] = color.a };
                                    break;
                                case SerializedPropertyType.Bounds:
                                    var bounds = iterator.boundsValue;
                                    properties[propName] = new JObject
                                    {
                                        ["center"] = new JObject { ["x"] = bounds.center.x, ["y"] = bounds.center.y, ["z"] = bounds.center.z },
                                        ["size"] = new JObject { ["x"] = bounds.size.x, ["y"] = bounds.size.y, ["z"] = bounds.size.z }
                                    };
                                    break;
                                case SerializedPropertyType.Rect:
                                    var rect = iterator.rectValue;
                                    properties[propName] = new JObject { ["x"] = rect.x, ["y"] = rect.y, ["width"] = rect.width, ["height"] = rect.height };
                                    break;
                                case SerializedPropertyType.ObjectReference:
                                    if (iterator.objectReferenceValue != null)
                                    {
                                        properties[propName] = new JObject
                                        {
                                            ["name"] = iterator.objectReferenceValue.name,
                                            ["type"] = iterator.objectReferenceValue.GetType().Name,
                                            ["instanceId"] = iterator.objectReferenceValue.GetInstanceID()
                                        };
                                    }
                                    else
                                    {
                                        properties[propName] = null;
                                    }
                                    break;
                                default:
                                    properties[propName] = $"({iterator.propertyType})";
                                    break;
                            }
                        }
                        catch
                        {
                            // Skip properties that can't be read
                        }
                    }

                    serializedObject.Dispose();

                    var result = new JObject
                    {
                        ["gameObject"] = target.name,
                        ["componentType"] = componentType.Name,
                        ["properties"] = properties
                    };

                    return CreateSuccessResponse("component_properties", result);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("get_component_properties_error", ex.Message);
                }
            });
        }

        private string HandleSaveScene(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    bool saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);

                    if (saved)
                    {
                        return CreateSuccessResponse("scene_saved", $"Saved scene: {scene.name} ({scene.path})");
                    }
                    else
                    {
                        return CreateErrorResponse("save_scene_failed", $"Failed to save scene: {scene.name}");
                    }
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("save_scene_error", ex.Message);
                }
            });
        }

        private string HandleOpenScene(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var request = JObject.Parse(requestBody);
                    string scenePath = request["scenePath"]?.ToString();
                    bool additive = request["additive"]?.ToObject<bool>() ?? false;

                    if (string.IsNullOrEmpty(scenePath))
                    {
                        return CreateErrorResponse("missing_parameter", "scenePath is required");
                    }

                    // Prompt to save current scene if dirty
                    var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    if (currentScene.isDirty && !additive)
                    {
                        UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
                    }

                    var mode = additive
                        ? UnityEditor.SceneManagement.OpenSceneMode.Additive
                        : UnityEditor.SceneManagement.OpenSceneMode.Single;

                    var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath, mode);

                    return CreateSuccessResponse("scene_opened", $"Opened scene: {scene.name} ({scenePath})");
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("open_scene_error", ex.Message);
                }
            });
        }

        private string HandlePlayModeControl(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var request = JObject.Parse(requestBody);
                    string action = request["action"]?.ToString()?.ToLower();

                    switch (action)
                    {
                        case "play":
                            if (!EditorApplication.isPlaying)
                            {
                                EditorApplication.isPlaying = true;
                            }
                            return CreateSuccessResponse("play_mode", "Play mode started");

                        case "stop":
                            if (EditorApplication.isPlaying)
                            {
                                EditorApplication.isPlaying = false;
                            }
                            return CreateSuccessResponse("play_mode", "Play mode stopped");

                        case "pause":
                            EditorApplication.isPaused = !EditorApplication.isPaused;
                            return CreateSuccessResponse("play_mode", $"Play mode paused: {EditorApplication.isPaused}");

                        case "status":
                            var status = new JObject
                            {
                                ["isPlaying"] = EditorApplication.isPlaying,
                                ["isPaused"] = EditorApplication.isPaused,
                                ["isCompiling"] = EditorApplication.isCompiling
                            };
                            return CreateSuccessResponse("play_mode_status", status);

                        default:
                            return CreateErrorResponse("invalid_action", "action must be one of: play, stop, pause, status");
                    }
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("play_mode_control_error", ex.Message);
                }
            });
        }

        private string HandleFindAssets(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var request = JObject.Parse(requestBody);
                    string searchFilter = request["filter"]?.ToString();
                    string searchInFolder = request["searchInFolder"]?.ToString();
                    string type = request["type"]?.ToString();
                    int limit = request["limit"]?.ToObject<int>() ?? 50;

                    if (string.IsNullOrEmpty(searchFilter) && string.IsNullOrEmpty(type))
                    {
                        return CreateErrorResponse("missing_parameter", "filter or type is required");
                    }

                    string filter = searchFilter ?? "";
                    if (!string.IsNullOrEmpty(type))
                    {
                        filter = $"t:{type} {filter}".Trim();
                    }

                    string[] guids;
                    if (!string.IsNullOrEmpty(searchInFolder))
                    {
                        guids = AssetDatabase.FindAssets(filter, new[] { searchInFolder });
                    }
                    else
                    {
                        guids = AssetDatabase.FindAssets(filter);
                    }

                    var assets = new JArray();
                    int count = Math.Min(guids.Length, limit);
                    for (int i = 0; i < count; i++)
                    {
                        string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                        var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                        assets.Add(new JObject
                        {
                            ["guid"] = guids[i],
                            ["path"] = assetPath,
                            ["type"] = assetType?.Name ?? "Unknown",
                            ["name"] = System.IO.Path.GetFileNameWithoutExtension(assetPath)
                        });
                    }

                    var result = new JObject
                    {
                        ["totalFound"] = guids.Length,
                        ["returned"] = count,
                        ["assets"] = assets
                    };

                    return CreateSuccessResponse("assets_found", result);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("find_assets_error", ex.Message);
                }
            });
        }

        private string HandleCreateMaterial(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var request = JObject.Parse(requestBody);
                    string materialName = request["name"]?.ToString() ?? "New Material";
                    string shaderName = request["shader"]?.ToString() ?? "Standard";
                    string savePath = request["savePath"]?.ToString();
                    var colorData = request["color"] as JObject;

                    var shader = Shader.Find(shaderName);
                    if (shader == null)
                    {
                        return CreateErrorResponse("shader_not_found", $"Shader not found: {shaderName}");
                    }

                    var material = new Material(shader);
                    material.name = materialName;

                    if (colorData != null)
                    {
                        float r = colorData["r"]?.ToObject<float>() ?? 1f;
                        float g = colorData["g"]?.ToObject<float>() ?? 1f;
                        float b = colorData["b"]?.ToObject<float>() ?? 1f;
                        float a = colorData["a"]?.ToObject<float>() ?? 1f;
                        material.color = new Color(r, g, b, a);
                    }

                    if (string.IsNullOrEmpty(savePath))
                    {
                        savePath = $"Assets/{materialName}.mat";
                    }

                    // Ensure directory exists
                    string directory = System.IO.Path.GetDirectoryName(savePath);
                    if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                    {
                        System.IO.Directory.CreateDirectory(directory);
                    }

                    AssetDatabase.CreateAsset(material, savePath);
                    AssetDatabase.SaveAssets();

                    var result = new JObject
                    {
                        ["name"] = materialName,
                        ["shader"] = shaderName,
                        ["path"] = savePath,
                        ["guid"] = AssetDatabase.AssetPathToGUID(savePath)
                    };

                    return CreateSuccessResponse("material_created", result);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("create_material_error", ex.Message);
                }
            });
        }

        private string HandleScreenshot(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var request = JObject.Parse(requestBody);
                    string savePath = request["savePath"]?.ToString();
                    int superSize = request["superSize"]?.ToObject<int>() ?? 1;

                    if (string.IsNullOrEmpty(savePath))
                    {
                        string directory = "Assets/Screenshots";
                        if (!System.IO.Directory.Exists(directory))
                        {
                            System.IO.Directory.CreateDirectory(directory);
                        }
                        savePath = $"{directory}/Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                    }

                    // Ensure directory exists
                    string dir = System.IO.Path.GetDirectoryName(savePath);
                    if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    {
                        System.IO.Directory.CreateDirectory(dir);
                    }

                    ScreenCapture.CaptureScreenshot(savePath, superSize);

                    // Refresh so the screenshot appears in the project
                    EditorApplication.delayCall += () => AssetDatabase.Refresh();

                    var result = new JObject
                    {
                        ["path"] = savePath,
                        ["superSize"] = superSize
                    };

                    return CreateSuccessResponse("screenshot_taken", result);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("screenshot_error", ex.Message);
                }
            });
        }

        private string HandleGetConsoleLogs(string requestBody)
        {
            var logs = new JArray();
            
            lock (_consoleLogLock)
            {
                foreach (var log in _consoleLogs)
                {
                    var logType = "info";
                    switch (log.type)
                    {
                        case LogType.Error:
                        case LogType.Exception:
                        case LogType.Assert:
                            logType = "error";
                            break;
                        case LogType.Warning:
                            logType = "warning";
                            break;
                    }
                    
                    logs.Add(new JObject
                    {
                        ["message"] = log.message,
                        ["stackTrace"] = log.stackTrace,
                        ["type"] = logType,
                        ["timestamp"] = log.timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    });
                }
            }

            return CreateSuccessResponse("console_logs", logs);
        }

        private string HandleGetCompilationErrors(string requestBody)
        {
            try
            {
                lock (_compilationLock)
                {
                    var result = new JObject
                    {
                        ["lastCompilationTime"] = _lastCompilationTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["lastCompiledAssembly"] = _lastCompiledAssembly,
                        ["isCompiling"] = EditorApplication.isCompiling,
                        ["hasErrors"] = _lastCompilationErrors.Any(m => m.type == CompilerMessageType.Error),
                        ["hasWarnings"] = _lastCompilationErrors.Any(m => m.type == CompilerMessageType.Warning),
                        ["totalMessages"] = _lastCompilationErrors.Count
                    };

                    var messagesArray = new JArray();
                    foreach (var message in _lastCompilationErrors)
                    {
                        messagesArray.Add(new JObject
                        {
                            ["type"] = message.type.ToString().ToLower(),
                            ["message"] = message.message,
                            ["file"] = message.file,
                            ["line"] = message.line,
                            ["column"] = message.column
                        });
                    }
                    result["messages"] = messagesArray;

                    return CreateSuccessResponse("compilation_errors", result);
                }
            }
            catch (Exception ex)
            {
                return CreateErrorResponse("get_compilation_errors_error", ex.Message);
            }
        }

        private string HandleAutoFixErrors(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    Debug.Log("[Claude Code MCP] Auto-fix functionality has been removed for safety");
                    
                    var result = new JObject
                    {
                        ["message"] = "Auto-fix functionality has been permanently disabled",
                        ["reason"] = "Safety measure to prevent automatic code modification"
                    };

                    return CreateSuccessResponse("auto_fix_disabled", result);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("auto_fix_errors_error", ex.Message);
                }
            });
        }

        private string HandleExecuteMenuItem(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var request = JObject.Parse(requestBody);
                    string menuPath = request["menuPath"]?.ToString();

                    if (string.IsNullOrEmpty(menuPath))
                    {
                        return CreateErrorResponse("missing_menu_path", "Menu path is required");
                    }

                    // Check if menu item exists
                    bool menuExists = Menu.GetEnabled(menuPath);
                    if (!menuExists)
                    {
                        Debug.LogError($"[Claude Code MCP] Menu item does not exist: {menuPath}");
                        return CreateErrorResponse("menu_item_not_found", $"Menu item not found: {menuPath}");
                    }

                    // Record initial state for verification
                    bool wasCompiling = EditorApplication.isCompiling;
                    var executionStartTime = DateTime.Now;

                    // Execute menu item
                    bool executionResult = EditorApplication.ExecuteMenuItem(menuPath);
                    
                    // Verify execution
                    var executionTime = (DateTime.Now - executionStartTime).TotalMilliseconds;
                    bool compilingChanged = EditorApplication.isCompiling != wasCompiling;
                    
                    // Create detailed response
                    var verificationData = new JObject
                    {
                        ["menuPath"] = menuPath,
                        ["executionTime"] = executionTime,
                        ["compilingChanged"] = compilingChanged,
                        ["executionResult"] = executionResult,
                        ["menuExists"] = menuExists
                    };

                    if (executionResult)
                    {
                        Debug.Log($"[Claude Code MCP] ✅ Successfully executed: {menuPath} ({executionTime:F1}ms)");
                        return CreateSuccessResponse("menu_item_executed", verificationData);
                    }
                    else
                    {
                        Debug.LogWarning($"[Claude Code MCP] ⚠️ Menu execution returned false: {menuPath}");
                        return CreateErrorResponse("menu_execution_failed", $"Menu execution returned false: {menuPath}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Claude Code MCP] ❌ Exception executing menu item: {ex.Message}");
                    return CreateErrorResponse("execute_menu_item_error", ex.Message);
                }
            });
        }

        private string HandleAddPackage(string requestBody)
        {
            try
            {
                var request = JObject.Parse(requestBody);
                string source = request["source"]?.ToString();
                string packageName = request["packageName"]?.ToString();
                string repositoryUrl = request["repositoryUrl"]?.ToString();
                string path = request["path"]?.ToString();
                string branch = request["branch"]?.ToString();
                string version = request["version"]?.ToString();

                if (string.IsNullOrEmpty(source))
                {
                    return CreateErrorResponse("missing_source", "Source is required");
                }

                string packageId = "";
                switch (source.ToLower())
                {
                    case "registry":
                        if (string.IsNullOrEmpty(packageName))
                        {
                            return CreateErrorResponse("missing_package_name", "Package name is required for registry source");
                        }
                        packageId = string.IsNullOrEmpty(version) ? packageName : $"{packageName}@{version}";
                        break;
                    
                    case "github":
                        if (string.IsNullOrEmpty(repositoryUrl))
                        {
                            return CreateErrorResponse("missing_repository_url", "Repository URL is required for GitHub source");
                        }
                        packageId = repositoryUrl;
                        if (!string.IsNullOrEmpty(path))
                        {
                            packageId += $"?path={path}";
                        }
                        if (!string.IsNullOrEmpty(branch))
                        {
                            packageId += packageId.Contains("?") ? $"&revision={branch}" : $"#revision={branch}";
                        }
                        break;
                    
                    case "disk":
                        if (string.IsNullOrEmpty(path))
                        {
                            return CreateErrorResponse("missing_path", "Path is required for disk source");
                        }
                        packageId = $"file:{path}";
                        break;
                    
                    default:
                        return CreateErrorResponse("invalid_source", "Source must be 'registry', 'github', or 'disk'");
                }

                UnityEditor.PackageManager.Client.Add(packageId);
                return CreateSuccessResponse("package_added", $"Package {packageId} added to Package Manager");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse("add_package_error", ex.Message);
            }
        }

        // Test runner state
        private static readonly object _testResultLock = new object();
        private static JArray _lastTestResults = null;
        private static bool _testsRunning = false;
        private static string _testRunStatus = "idle";

        private string HandleRunTests(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var request = JObject.Parse(requestBody);
                    string testMode = request["testMode"]?.ToString() ?? "EditMode";
                    string testFilter = request["testFilter"]?.ToString();
                    bool returnOnlyFailures = request["returnOnlyFailures"]?.ToObject<bool>() ?? true;
                    bool returnWithLogs = request["returnWithLogs"]?.ToObject<bool>() ?? false;
                    bool queryOnly = request["queryOnly"]?.ToObject<bool>() ?? false;

                    // If queryOnly, return last test results
                    if (queryOnly)
                    {
                        lock (_testResultLock)
                        {
                            var statusResult = new JObject
                            {
                                ["status"] = _testRunStatus,
                                ["isRunning"] = _testsRunning
                            };
                            if (_lastTestResults != null)
                            {
                                statusResult["results"] = _lastTestResults;
                                statusResult["totalCount"] = _lastTestResults.Count;
                                int failCount = _lastTestResults.Count(r => r["result"]?.ToString() == "Failed");
                                int passCount = _lastTestResults.Count(r => r["result"]?.ToString() == "Passed");
                                statusResult["passedCount"] = passCount;
                                statusResult["failedCount"] = failCount;
                            }
                            return CreateSuccessResponse("test_results", statusResult);
                        }
                    }

                    if (_testsRunning)
                    {
                        return CreateErrorResponse("tests_already_running", "Tests are already running. Use queryOnly to check status.");
                    }

                    // Parse test mode
                    TestMode mode;
                    switch (testMode.ToLower())
                    {
                        case "playmode":
                        case "play":
                            mode = TestMode.PlayMode;
                            break;
                        case "editmode":
                        case "edit":
                        default:
                            mode = TestMode.EditMode;
                            break;
                    }

                    // Setup test runner
                    var testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
                    var callbackHandler = ScriptableObject.CreateInstance<MCPTestRunCallback>();
                    callbackHandler.Init(returnOnlyFailures, returnWithLogs);

                    testRunnerApi.RegisterCallbacks(callbackHandler);

                    lock (_testResultLock)
                    {
                        _lastTestResults = null;
                        _testsRunning = true;
                        _testRunStatus = "running";
                    }

                    // Build filter
                    var filter = new Filter
                    {
                        testMode = mode
                    };

                    if (!string.IsNullOrEmpty(testFilter))
                    {
                        filter.testNames = new[] { testFilter };
                    }

                    testRunnerApi.Execute(new ExecutionSettings(filter));

                    return CreateSuccessResponse("tests_started", new JObject
                    {
                        ["message"] = $"Tests started in {testMode} mode",
                        ["testMode"] = testMode,
                        ["filter"] = testFilter ?? "(all)",
                        ["hint"] = "Use run_tests with queryOnly=true to check results"
                    });
                }
                catch (Exception ex)
                {
                    lock (_testResultLock)
                    {
                        _testsRunning = false;
                        _testRunStatus = "error";
                    }
                    return CreateErrorResponse("run_tests_error", ex.Message);
                }
            });
        }

        internal static void OnTestRunCompleted(JArray results)
        {
            lock (_testResultLock)
            {
                _lastTestResults = results;
                _testsRunning = false;
                _testRunStatus = "completed";
            }
        }

        private string HandleAddAssetToScene(string requestBody)
        {
            try
            {
                var request = JObject.Parse(requestBody);
                string assetPath = request["assetPath"]?.ToString();
                string guid = request["guid"]?.ToString();
                string parentPath = request["parentPath"]?.ToString();
                int? parentId = request["parentId"]?.ToObject<int?>();
                var position = request["position"] as JObject;

                // Get asset reference
                UnityEngine.Object asset = null;
                if (!string.IsNullOrEmpty(assetPath))
                {
                    asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                }
                else if (!string.IsNullOrEmpty(guid))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                }

                if (asset == null)
                {
                    return CreateErrorResponse("asset_not_found", "Asset not found");
                }

                // Instantiate asset
                GameObject instance = PrefabUtility.InstantiatePrefab(asset) as GameObject;
                if (instance == null)
                {
                    return CreateErrorResponse("instantiate_failed", "Failed to instantiate asset");
                }

                // Set parent
                if (parentId.HasValue)
                {
                    var parent = EditorUtility.InstanceIDToObject(parentId.Value) as GameObject;
                    if (parent != null)
                    {
                        instance.transform.SetParent(parent.transform);
                    }
                }
                else if (!string.IsNullOrEmpty(parentPath))
                {
                    var parent = GameObject.Find(parentPath);
                    if (parent != null)
                    {
                        instance.transform.SetParent(parent.transform);
                    }
                }

                // Set position
                if (position != null)
                {
                    float x = position["x"]?.ToObject<float>() ?? 0f;
                    float y = position["y"]?.ToObject<float>() ?? 0f;
                    float z = position["z"]?.ToObject<float>() ?? 0f;
                    instance.transform.position = new Vector3(x, y, z);
                }

                Undo.RegisterCreatedObjectUndo(instance, "Add Asset to Scene");
                Selection.activeGameObject = instance;

                return CreateSuccessResponse("asset_added", $"Added {instance.name} to scene");
            }
            catch (Exception ex)
            {
                return CreateErrorResponse("add_asset_error", ex.Message);
            }
        }

        private string HandleGetScenesHierarchy()
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var hierarchy = new JObject();
                    var gameObjects = new JArray();

                    // Get all root GameObjects in the current scene
                    var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    var rootObjects = scene.GetRootGameObjects();

                foreach (var rootObj in rootObjects)
                {
                    gameObjects.Add(SerializeGameObject(rootObj, true));
                }

                    hierarchy["sceneName"] = scene.name;
                    hierarchy["gameObjects"] = gameObjects;

                    return CreateSuccessResponse("scenes_hierarchy", hierarchy);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("get_hierarchy_error", ex.Message);
                }
            });
        }

        private JObject SerializeGameObject(GameObject obj, bool includeChildren = false)
        {
            var jsonObj = new JObject
            {
                ["name"] = obj.name,
                ["instanceId"] = obj.GetInstanceID(),
                ["isActive"] = obj.activeInHierarchy,
                ["tag"] = obj.tag,
                ["layer"] = obj.layer
            };

            if (includeChildren && obj.transform.childCount > 0)
            {
                var children = new JArray();
                for (int i = 0; i < obj.transform.childCount; i++)
                {
                    children.Add(SerializeGameObject(obj.transform.GetChild(i).gameObject, true));
                }
                jsonObj["children"] = children;
            }

            return jsonObj;
        }

        private string HandleHotReload(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    // Default values
                    bool saveAssets = true;
                    bool optimized = true;
                    
                    // Parse JSON parameters if request body is provided
                    if (!string.IsNullOrEmpty(requestBody))
                    {
                        try
                        {
                            var request = JObject.Parse(requestBody);
                            saveAssets = request["saveAssets"]?.ToObject<bool>() ?? true;
                            optimized = request["optimized"]?.ToObject<bool>() ?? true;
                        }
                        catch (JsonReaderException)
                        {
                            // If JSON parsing fails, use default values and continue
                            Debug.LogWarning("[Claude Code MCP] Invalid JSON in hot reload request, using default values");
                        }
                    }
                    
                    Debug.Log($"[Claude Code MCP] 🔄 Performing hot reload (optimized: {optimized}, saveAssets: {saveAssets})");
                    
                    var startTime = DateTime.Now;
                    
                    if (saveAssets)
                    {
                        AssetDatabase.SaveAssets();
                    }
                    
                    if (optimized)
                    {
                        // Optimized approach - script compilation only
                        CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.None);
                    }
                    else
                    {
                        // Full refresh approach
                        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                        CompilationPipeline.RequestScriptCompilation();
                    }
                    
                    var executionTime = (DateTime.Now - startTime).TotalMilliseconds;
                    
                    var result = new JObject
                    {
                        ["method"] = optimized ? "optimized" : "full_refresh",
                        ["savedAssets"] = saveAssets,
                        ["executionTime"] = executionTime,
                        ["compilationStarted"] = true,
                        ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    };
                    
                    Debug.Log($"[Claude Code MCP] ✅ Hot reload triggered successfully ({executionTime:F1}ms)");
                    return CreateSuccessResponse("hot_reload_triggered", result);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("hot_reload_error", ex.Message);
                }
            });
        }
        
        private string HandleForceCompilation(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    var request = JObject.Parse(requestBody);
                    bool forceUpdate = request["forceUpdate"]?.ToObject<bool>() ?? true;
                    
                    Debug.Log($"[Claude Code MCP] 🔨 Forcing compilation (forceUpdate: {forceUpdate})");
                    
                    var startTime = DateTime.Now;
                    
                    if (forceUpdate)
                    {
                        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    }
                    
                    CompilationPipeline.RequestScriptCompilation();
                    
                    var executionTime = (DateTime.Now - startTime).TotalMilliseconds;
                    
                    var result = new JObject
                    {
                        ["forceUpdate"] = forceUpdate,
                        ["executionTime"] = executionTime,
                        ["compilationStarted"] = true,
                        ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    };
                    
                    Debug.Log($"[Claude Code MCP] ✅ Force compilation triggered successfully ({executionTime:F1}ms)");
                    return CreateSuccessResponse("force_compilation_triggered", result);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("force_compilation_error", ex.Message);
                }
            });
        }
        
        private string HandleCheckCompilationStatus(string requestBody)
        {
            return ExecuteOnMainThreadWithResult(() => {
                try
                {
                    bool isCompiling = EditorApplication.isCompiling;
                    bool isPlaying = EditorApplication.isPlaying;
                    bool isPaused = EditorApplication.isPaused;
                
                string status = "ready";
                if (isCompiling)
                    status = "compiling";
                else if (isPlaying)
                    status = isPaused ? "playing_paused" : "playing";
                
                var result = new JObject
                {
                    ["status"] = status,
                    ["isCompiling"] = isCompiling,
                    ["isPlaying"] = isPlaying,
                    ["isPaused"] = isPaused,
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                };
                
                lock (_compilationLock)
                {
                    result["lastCompilationTime"] = _lastCompilationTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    result["lastCompiledAssembly"] = _lastCompiledAssembly;
                    result["hasErrors"] = _lastCompilationErrors.Any(m => m.type == CompilerMessageType.Error);
                    result["hasWarnings"] = _lastCompilationErrors.Any(m => m.type == CompilerMessageType.Warning);
                    result["totalMessages"] = _lastCompilationErrors.Count;
                }
                
                    Debug.Log($"[Claude Code MCP] 📊 Compilation status: {status}");
                    return CreateSuccessResponse("compilation_status", result);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("check_compilation_status_error", ex.Message);
                }
            });
        }

        #endregion

        #region Response Helpers

        private string CreateSuccessResponse(string type, object data)
        {
            var response = new JObject
            {
                ["success"] = true,
                ["type"] = type,
                ["data"] = JToken.FromObject(data),
                ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            return response.ToString();
        }

        private string CreateErrorResponse(string errorType, string message)
        {
            var response = new JObject
            {
                ["success"] = false,
                ["error"] = new JObject
                {
                    ["type"] = errorType,
                    ["message"] = message
                },
                ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            return response.ToString();
        }

        #endregion

        #region Cleanup

        private void OnEditorQuitting()
        {
            Dispose();
        }

        private void OnLogMessageReceived(string logString, string stackTrace, LogType type)
        {
            lock (_consoleLogLock)
            {
                _consoleLogs.Add(new LogEntry
                {
                    message = logString,
                    stackTrace = stackTrace,
                    type = type,
                    timestamp = DateTime.Now
                });
                
                // Keep only the last MAX_LOG_ENTRIES
                while (_consoleLogs.Count > MAX_LOG_ENTRIES)
                {
                    _consoleLogs.RemoveAt(0);
                }
            }
        }
        
        private void OnAfterAssemblyReload(string assemblyName, CompilerMessage[] messages)
        {
            // コンパイルエラーを自動取得・ログ出力
            ProcessCompilationMessages(assemblyName, messages);

            // コンパイル後のサーバー状態確認はCheckAndMaintainServerHealthに任せる
            // ここでの再起動処理を削除してドメインリロードループを防止
        }
        
        private void ProcessCompilationMessages(string assemblyName, CompilerMessage[] messages)
        {
            lock (_compilationLock)
            {
                _lastCompiledAssembly = assemblyName;
                _lastCompilationTime = DateTime.Now;
                _lastCompilationErrors.Clear();
                
                if (messages != null)
                {
                    _lastCompilationErrors.AddRange(messages);
                }
            }
            
            if (messages == null || messages.Length == 0)
            {
                Debug.Log($"[Claude Code MCP] Compilation completed successfully for {assemblyName}");
                return;
            }
            
            int errorCount = 0;
            int warningCount = 0;
            
            foreach (var message in messages)
            {
                switch (message.type)
                {
                    case CompilerMessageType.Error:
                        errorCount++;
                        Debug.LogError($"[Claude Code MCP] Compilation Error: {message.message}\nFile: {message.file}:{message.line}:{message.column}");
                        break;
                    case CompilerMessageType.Warning:
                        warningCount++;
                        Debug.LogWarning($"[Claude Code MCP] Compilation Warning: {message.message}\nFile: {message.file}:{message.line}:{message.column}");
                        break;
                }
            }
            
            string summary = $"Assembly: {assemblyName} - Errors: {errorCount}, Warnings: {warningCount}";
            if (errorCount > 0)
            {
                Debug.LogError($"[Claude Code MCP] {summary}");
            }
            else if (warningCount > 0)
            {
                Debug.LogWarning($"[Claude Code MCP] {summary}");
            }
            else
            {
                Debug.Log($"[Claude Code MCP] {summary}");
            }
        }
        










        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // ドメインリロード中は何もしない
            if (_isDomainReloading)
            {
                return;
            }

            if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
            {
                Debug.Log($"[Claude Code MCP] Play mode changed: {state}, ensuring server stability");
                EditorApplication.delayCall += () => {
                    // 再度チェック
                    if (_isDomainReloading || EditorApplication.isCompiling)
                    {
                        return;
                    }

                    if (!_isRunning || (_httpListener != null && !_httpListener.IsListening))
                    {
                        Debug.Log("[Claude Code MCP] Restarting server after play mode change");
                        try
                        {
                            StopServer();
                            StartServer();
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"[Claude Code MCP] Play mode restart failed: {ex.Message}");
                        }
                    }
                };
            }
        }
        
        public void Dispose()
        {
            StopServerAndWait();
            Application.logMessageReceived -= OnLogMessageReceived;
            EditorApplication.quitting -= OnEditorQuitting;
            CompilationPipeline.assemblyCompilationFinished -= OnAfterAssemblyReload;
            EditorApplication.update -= ProcessMainThreadQueue;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReloadStatic;
        }

        #endregion
    }
}
