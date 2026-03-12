using System;
using System.Net;
using System.Text;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEditor;
using UnityEditor.Compilation;
using Newtonsoft.Json.Linq;
using ClaudeCodeMCP.Editor.Core.Handlers;

namespace ClaudeCodeMCP.Editor.Core
{
    [InitializeOnLoad]
    public class MCPHttpServer : IDisposable
    {
        private static MCPHttpServer _instance;
        private static bool _isInitializing;
        private static bool _delayCallScheduled;
        private static bool _isDomainReloading;

        private HttpListener _httpListener;
        private Thread _requestHandlerThread;
        private bool _isRunning;
        private int _port = 8090;

        // Main thread execution
        private readonly Queue<Action> _mainThreadQueue = new Queue<Action>();
        private readonly object _queueLock = new object();

        // Async response handling
        private readonly Dictionary<string, ManualResetEvent> _pendingRequests = new Dictionary<string, ManualResetEvent>();
        private readonly Dictionary<string, string> _responses = new Dictionary<string, string>();
        private readonly object _responseLock = new object();

        // Health check
        private float _lastHealthCheckTime;
        private const float HealthCheckInterval = 10f;

        // Shared state objects
        internal readonly CompilationState CompilationState = new CompilationState();
        internal readonly ConsoleLogState ConsoleLogState = new ConsoleLogState();
        internal readonly TestRunState TestRunState = new TestRunState();

        // Request handlers
        private readonly Dictionary<string, IMCPHandler> _handlers = new Dictionary<string, IMCPHandler>();

        public static MCPHttpServer Instance
        {
            get
            {
                if (_instance == null && !_isInitializing)
                {
                    _isInitializing = true;
                    try { _instance = new MCPHttpServer(); }
                    finally { _isInitializing = false; }
                }
                return _instance;
            }
        }

        public bool IsRunning => _isRunning;

        static MCPHttpServer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReloadStatic;

            if (!_delayCallScheduled)
            {
                _delayCallScheduled = true;
                EditorApplication.delayCall += InitializeServerDelayed;
            }
        }

        private static void InitializeServerDelayed()
        {
            _delayCallScheduled = false;

            if (_isDomainReloading || EditorApplication.isCompiling)
            {
                EditorApplication.delayCall += InitializeServerDelayed;
                _delayCallScheduled = true;
                return;
            }

            if (_instance == null && !_isInitializing)
            {
                var _ = Instance;
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            _isDomainReloading = true;
            _delayCallScheduled = false;

            if (_instance != null)
            {
                try { _instance.StopServerAndWait(); }
                catch { /* Ignore cleanup errors */ }
            }
            _instance = null;
            _isInitializing = false;
        }

        private static void OnAfterAssemblyReloadStatic()
        {
            _isDomainReloading = false;
        }

        private MCPHttpServer()
        {
            RegisterHandlers();

            EditorApplication.quitting += OnEditorQuitting;
            CompilationPipeline.assemblyCompilationFinished += OnAfterAssemblyReload;
            EditorApplication.update += ProcessMainThreadQueue;
            Application.logMessageReceived += ConsoleLogState.OnLogMessageReceived;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            if (!_isDomainReloading && !EditorApplication.isCompiling)
            {
                EditorApplication.delayCall += () => {
                    if (_isDomainReloading || EditorApplication.isCompiling) return;
                    try { StartServer(); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Claude Code MCP] Auto-start failed: {ex.Message}. You can start manually from Tools menu.");
                    }
                };
            }
        }

        private void RegisterHandlers()
        {
            // GameObject
            _handlers["/mcp/tools/select_gameobject"] = new SelectGameObjectHandler(this);
            _handlers["/mcp/tools/get_gameobject_info"] = new GetGameObjectInfoHandler(this);
            _handlers["/mcp/tools/update_gameobject"] = new UpdateGameObjectHandler(this);
            _handlers["/mcp/tools/delete_gameobject"] = new DeleteGameObjectHandler(this);

            // Component
            _handlers["/mcp/tools/update_component"] = new UpdateComponentHandler(this);
            _handlers["/mcp/tools/remove_component"] = new RemoveComponentHandler(this);
            _handlers["/mcp/tools/get_component_properties"] = new GetComponentPropertiesHandler(this);

            // Console
            _handlers["/mcp/tools/send_console_log"] = new SendConsoleLogHandler(this);
            _handlers["/mcp/tools/get_console_logs"] = new GetConsoleLogsHandler(this, ConsoleLogState);

            // Compilation
            _handlers["/mcp/tools/hot_reload"] = new HotReloadHandler(this);
            _handlers["/mcp/tools/force_compilation"] = new ForceCompilationHandler(this);
            _handlers["/mcp/tools/check_compilation_status"] = new CheckCompilationStatusHandler(this, CompilationState);
            _handlers["/mcp/tools/get_compilation_errors"] = new GetCompilationErrorsHandler(this, CompilationState);
            _handlers["/mcp/tools/auto_fix_errors"] = new AutoFixErrorsHandler(this);

            // Scene & Assets
            _handlers["/mcp/tools/save_scene"] = new SaveSceneHandler(this);
            _handlers["/mcp/tools/open_scene"] = new OpenSceneHandler(this);
            _handlers["/mcp/resources/scenes_hierarchy"] = new GetScenesHierarchyHandler(this);
            _handlers["/mcp/tools/find_assets"] = new FindAssetsHandler(this);
            _handlers["/mcp/tools/add_asset_to_scene"] = new AddAssetToSceneHandler(this);
            _handlers["/mcp/tools/create_material"] = new CreateMaterialHandler(this);
            _handlers["/mcp/tools/screenshot"] = new ScreenshotHandler(this);

            // Editor control
            _handlers["/mcp/tools/execute_menu_item"] = new ExecuteMenuItemHandler(this);
            _handlers["/mcp/tools/play_mode_control"] = new PlayModeControlHandler(this);
            _handlers["/mcp/tools/add_package"] = new AddPackageHandler(this);
            _handlers["/mcp/tools/run_tests"] = new RunTestsHandler(this, TestRunState);
        }

        #region Server Lifecycle

        public void StartServer()
        {
            if (_isRunning && _httpListener != null && _httpListener.IsListening)
            {
                Debug.Log("[Claude Code MCP] Server is already running and listening");
                return;
            }

            if (_httpListener != null)
            {
                try { _httpListener.Stop(); _httpListener.Close(); }
                catch { /* Ignore cleanup errors */ }
                _httpListener = null;
            }

            _isRunning = false;

            try
            {
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://+:{_port}/");
                _httpListener.Start();
                _isRunning = true;

                Debug.Log($"[Claude Code MCP] HTTP Server started on port {_port}");

                _requestHandlerThread = new Thread(HandleRequests)
                {
                    Name = "MCP-RequestHandler",
                    IsBackground = true
                };
                _requestHandlerThread.Start();
            }
            catch (HttpListenerException)
            {
                TryStartAlternativePort();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] Failed to start server: {ex.Message}");
            }
        }

        private void TryStartAlternativePort()
        {
            _httpListener?.Close();
            _httpListener = null;

            for (int port = 8090; port <= 8099; port++)
            {
                try
                {
                    _httpListener = new HttpListener();
                    _httpListener.Prefixes.Add($"http://+:{port}/");
                    _httpListener.Start();

                    _port = port;
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
                }
            }

            Debug.LogError("[Claude Code MCP] Failed to start server on any port between 8090-8099");
        }

        public void StopServer() => StopServerInternal(false);
        public void StopServerAndWait() => StopServerInternal(true);

        private void StopServerInternal(bool waitForThread)
        {
            try
            {
                _isRunning = false;

                if (_httpListener != null)
                {
                    try
                    {
                        if (_httpListener.IsListening) _httpListener.Stop();
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

                if (waitForThread && _requestHandlerThread != null && _requestHandlerThread.IsAlive)
                {
                    try
                    {
                        if (!_requestHandlerThread.Join(500))
                            Debug.LogWarning("[Claude Code MCP] Request handler thread did not terminate in time");
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

        #endregion

        #region Request Processing

        private void HandleRequests()
        {
            Debug.Log("[Claude Code MCP] Request handler thread started");

            while (_isRunning && _httpListener != null)
            {
                try
                {
                    if (!_httpListener.IsListening) break;
                    var context = _httpListener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => ProcessRequest(context));
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException httpEx) when (httpEx.ErrorCode == 995) { break; }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Claude Code MCP] Error handling request: {ex.Message}");
                    if (ex is HttpListenerException || ex is InvalidOperationException)
                    {
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
                        requestBody = reader.ReadToEnd();
                }

                string responseJson = RouteRequest(request.Url.AbsolutePath, requestBody);

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
                try { response.StatusCode = 500; response.Close(); }
                catch { /* Ignore */ }
            }
        }

        private string RouteRequest(string path, string requestBody)
        {
            try
            {
                Debug.Log($"[Claude Code MCP] Processing request: {path}");

                if (path == "/mcp/ping")
                {
                    return new JObject
                    {
                        ["success"] = true,
                        ["type"] = "pong",
                        ["data"] = "Claude Code MCP Server is running",
                        ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    }.ToString();
                }

                if (_handlers.TryGetValue(path, out var handler))
                {
                    return handler.Handle(requestBody);
                }

                return new JObject
                {
                    ["success"] = false,
                    ["error"] = new JObject
                    {
                        ["type"] = "unknown_endpoint",
                        ["message"] = $"Unknown endpoint: {path}"
                    },
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                }.ToString();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] Error in RouteRequest: {ex.Message}");
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = new JObject
                    {
                        ["type"] = "internal_error",
                        ["message"] = ex.Message
                    },
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                }.ToString();
            }
        }

        #endregion

        #region Main Thread Execution

        internal string ExecuteOnMainThreadWithResult(Func<string> function, int timeoutMs = 5000)
        {
            string requestId = Guid.NewGuid().ToString();
            string result = null;

            var resetEvent = new ManualResetEvent(false);

            lock (_responseLock) { _pendingRequests[requestId] = resetEvent; }

            lock (_queueLock)
            {
                _mainThreadQueue.Enqueue(() => {
                    try
                    {
                        result = function();
                        lock (_responseLock)
                        {
                            _responses[requestId] = result;
                            if (_pendingRequests.TryGetValue(requestId, out var evt))
                                evt.Set();
                        }
                    }
                    catch (Exception ex)
                    {
                        result = new JObject
                        {
                            ["success"] = false,
                            ["error"] = new JObject { ["type"] = "main_thread_error", ["message"] = ex.Message },
                            ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        }.ToString();

                        lock (_responseLock)
                        {
                            _responses[requestId] = result;
                            if (_pendingRequests.TryGetValue(requestId, out var evt))
                                evt.Set();
                        }
                    }
                });
            }

            if (resetEvent.WaitOne(timeoutMs))
            {
                lock (_responseLock)
                {
                    if (_responses.TryGetValue(requestId, out result))
                        _responses.Remove(requestId);
                    _pendingRequests.Remove(requestId);
                }
            }
            else
            {
                lock (_responseLock)
                {
                    _pendingRequests.Remove(requestId);
                    _responses.Remove(requestId);
                }
                result = new JObject
                {
                    ["success"] = false,
                    ["error"] = new JObject { ["type"] = "timeout", ["message"] = "Operation timed out" },
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                }.ToString();
            }

            resetEvent.Dispose();
            return result ?? new JObject
            {
                ["success"] = false,
                ["error"] = new JObject { ["type"] = "unknown_error", ["message"] = "Unknown error occurred" },
                ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            }.ToString();
        }

        private void ProcessMainThreadQueue()
        {
            if (_isDomainReloading) return;

            lock (_queueLock)
            {
                while (_mainThreadQueue.Count > 0)
                {
                    try { _mainThreadQueue.Dequeue()?.Invoke(); }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Claude Code MCP] Main thread execution error: {ex.Message}");
                    }
                }
            }

            try
            {
                float currentTime = Time.realtimeSinceStartup;
                if (currentTime - _lastHealthCheckTime >= HealthCheckInterval)
                {
                    _lastHealthCheckTime = currentTime;
                    CheckAndMaintainServerHealth();
                }
            }
            catch { /* Ignore timing errors */ }
        }

        private void CheckAndMaintainServerHealth()
        {
            if (_isDomainReloading || EditorApplication.isCompiling) return;

            if (!_isRunning)
            {
                Debug.LogWarning("[Claude Code MCP] Server not running, attempting auto-restart...");
                try { StartServer(); }
                catch (Exception ex) { Debug.LogWarning($"[Claude Code MCP] Auto-restart failed: {ex.Message}"); }
            }
            else if (_httpListener != null && !_httpListener.IsListening)
            {
                Debug.LogWarning("[Claude Code MCP] Listener not active, restarting...");
                try { StopServer(); StartServer(); }
                catch (Exception ex) { Debug.LogWarning($"[Claude Code MCP] Server restart failed: {ex.Message}"); }
            }
        }

        #endregion

        #region Event Handlers

        private void OnAfterAssemblyReload(string assemblyName, CompilerMessage[] messages)
        {
            CompilationState.ProcessMessages(assemblyName, messages);
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (_isDomainReloading) return;

            if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.EnteredEditMode)
            {
                Debug.Log($"[Claude Code MCP] Play mode changed: {state}, ensuring server stability");
                EditorApplication.delayCall += () => {
                    if (_isDomainReloading || EditorApplication.isCompiling) return;

                    if (!_isRunning || (_httpListener != null && !_httpListener.IsListening))
                    {
                        Debug.Log("[Claude Code MCP] Restarting server after play mode change");
                        try { StopServer(); StartServer(); }
                        catch (Exception ex) { Debug.LogWarning($"[Claude Code MCP] Play mode restart failed: {ex.Message}"); }
                    }
                };
            }
        }

        private void OnEditorQuitting() => Dispose();

        #endregion

        #region Cleanup

        public void Dispose()
        {
            StopServerAndWait();
            Application.logMessageReceived -= ConsoleLogState.OnLogMessageReceived;
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
