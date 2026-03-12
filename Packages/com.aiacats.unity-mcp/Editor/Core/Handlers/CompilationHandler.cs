using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Compilation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    internal class CompilationState
    {
        private readonly object _lock = new object();
        private readonly List<CompilerMessage> _lastErrors = new List<CompilerMessage>();
        private string _lastCompiledAssembly = "";
        private DateTime _lastCompilationTime = DateTime.MinValue;

        public void ProcessMessages(string assemblyName, CompilerMessage[] messages)
        {
            lock (_lock)
            {
                _lastCompiledAssembly = assemblyName;
                _lastCompilationTime = DateTime.Now;
                _lastErrors.Clear();
                if (messages != null) _lastErrors.AddRange(messages);
            }

            if (messages == null || messages.Length == 0)
            {
                Debug.Log($"[Claude Code MCP] Compilation completed successfully for {assemblyName}");
                return;
            }

            int errorCount = 0, warningCount = 0;
            foreach (var msg in messages)
            {
                if (msg.type == CompilerMessageType.Error)
                {
                    errorCount++;
                    Debug.LogError($"[Claude Code MCP] Compilation Error: {msg.message}\nFile: {msg.file}:{msg.line}:{msg.column}");
                }
                else if (msg.type == CompilerMessageType.Warning)
                {
                    warningCount++;
                    Debug.LogWarning($"[Claude Code MCP] Compilation Warning: {msg.message}\nFile: {msg.file}:{msg.line}:{msg.column}");
                }
            }

            string summary = $"Assembly: {assemblyName} - Errors: {errorCount}, Warnings: {warningCount}";
            if (errorCount > 0) Debug.LogError($"[Claude Code MCP] {summary}");
            else if (warningCount > 0) Debug.LogWarning($"[Claude Code MCP] {summary}");
            else Debug.Log($"[Claude Code MCP] {summary}");
        }

        public JObject GetStatusSnapshot()
        {
            lock (_lock)
            {
                return new JObject
                {
                    ["lastCompilationTime"] = _lastCompilationTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    ["lastCompiledAssembly"] = _lastCompiledAssembly,
                    ["hasErrors"] = _lastErrors.Any(m => m.type == CompilerMessageType.Error),
                    ["hasWarnings"] = _lastErrors.Any(m => m.type == CompilerMessageType.Warning),
                    ["totalMessages"] = _lastErrors.Count
                };
            }
        }

        public JObject GetErrorsSnapshot()
        {
            lock (_lock)
            {
                var result = new JObject
                {
                    ["lastCompilationTime"] = _lastCompilationTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["lastCompiledAssembly"] = _lastCompiledAssembly,
                    ["isCompiling"] = EditorApplication.isCompiling,
                    ["hasErrors"] = _lastErrors.Any(m => m.type == CompilerMessageType.Error),
                    ["hasWarnings"] = _lastErrors.Any(m => m.type == CompilerMessageType.Warning),
                    ["totalMessages"] = _lastErrors.Count
                };

                var messagesArray = new JArray();
                foreach (var msg in _lastErrors)
                {
                    messagesArray.Add(new JObject
                    {
                        ["type"] = msg.type.ToString().ToLower(),
                        ["message"] = msg.message,
                        ["file"] = msg.file,
                        ["line"] = msg.line,
                        ["column"] = msg.column
                    });
                }
                result["messages"] = messagesArray;
                return result;
            }
        }
    }

    internal class HotReloadHandler : HandlerBase
    {
        public HotReloadHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                bool saveAssets = true;
                bool optimized = true;

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
                        Debug.LogWarning("[Claude Code MCP] Invalid JSON in hot reload request, using default values");
                    }
                }

                var startTime = DateTime.Now;

                if (saveAssets) AssetDatabase.SaveAssets();

                if (optimized)
                    CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.None);
                else
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    CompilationPipeline.RequestScriptCompilation();
                }

                var executionTime = (DateTime.Now - startTime).TotalMilliseconds;

                return CreateSuccessResponse("hot_reload_triggered", new JObject
                {
                    ["method"] = optimized ? "optimized" : "full_refresh",
                    ["savedAssets"] = saveAssets,
                    ["executionTime"] = executionTime,
                    ["compilationStarted"] = true,
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                });
            });
        }
    }

    internal class ForceCompilationHandler : HandlerBase
    {
        public ForceCompilationHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                bool forceUpdate = request["forceUpdate"]?.ToObject<bool>() ?? true;

                var startTime = DateTime.Now;

                if (forceUpdate) AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                CompilationPipeline.RequestScriptCompilation();

                var executionTime = (DateTime.Now - startTime).TotalMilliseconds;

                return CreateSuccessResponse("force_compilation_triggered", new JObject
                {
                    ["forceUpdate"] = forceUpdate,
                    ["executionTime"] = executionTime,
                    ["compilationStarted"] = true,
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                });
            });
        }
    }

    internal class CheckCompilationStatusHandler : HandlerBase
    {
        private readonly CompilationState _state;

        public CheckCompilationStatusHandler(MCPHttpServer server, CompilationState state) : base(server)
        {
            _state = state;
        }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                bool isCompiling = EditorApplication.isCompiling;
                bool isPlaying = EditorApplication.isPlaying;
                bool isPaused = EditorApplication.isPaused;

                string status = "ready";
                if (isCompiling) status = "compiling";
                else if (isPlaying) status = isPaused ? "playing_paused" : "playing";

                var result = new JObject
                {
                    ["status"] = status,
                    ["isCompiling"] = isCompiling,
                    ["isPlaying"] = isPlaying,
                    ["isPaused"] = isPaused,
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                };

                var stateSnapshot = _state.GetStatusSnapshot();
                foreach (var prop in stateSnapshot.Properties())
                    result[prop.Name] = prop.Value;

                return CreateSuccessResponse("compilation_status", result);
            });
        }
    }

    internal class GetCompilationErrorsHandler : HandlerBase
    {
        private readonly CompilationState _state;

        public GetCompilationErrorsHandler(MCPHttpServer server, CompilationState state) : base(server)
        {
            _state = state;
        }

        public override string Handle(string requestBody)
        {
            return CreateSuccessResponse("compilation_errors", _state.GetErrorsSnapshot());
        }
    }
}
