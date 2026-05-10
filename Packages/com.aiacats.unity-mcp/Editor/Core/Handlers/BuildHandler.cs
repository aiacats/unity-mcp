using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Build.Reporting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    /// <summary>
    /// Tracks state of the most recent player build. Updated from the main thread by
    /// <see cref="BuildPlayerHandler"/>. Polled by <see cref="WaitForBuildDoneHandler"/> /
    /// <see cref="GetBuildStatusHandler"/>.
    /// </summary>
    internal class BuildState
    {
        // Extracted snapshot of a BuildReport. Captured on the main thread when the build finishes
        // (Unity's BuildReport.summary getter is main-thread-only).
        class FinishedSnapshot
        {
            public string Result;
            public int TotalErrors;
            public int TotalWarnings;
            public ulong TotalSize;
            public long DurationMs;
            public List<JObject> Errors = new List<JObject>();
        }

        readonly object _lock = new object();
        bool _isRunning;
        DateTime _startedAt;
        DateTime _finishedAt;
        FinishedSnapshot _lastFinished;
        string _lastError;
        string[] _lastScenes;
        string _lastTarget;
        string _lastOutputPath;

        public bool IsRunning { get { lock (_lock) return _isRunning; } }

        public void OnBuildStarted(BuildPlayerOptions options)
        {
            lock (_lock)
            {
                _isRunning = true;
                _startedAt = DateTime.Now;
                _finishedAt = default;
                _lastFinished = null;
                _lastError = null;
                _lastScenes = options.scenes;
                _lastTarget = options.target.ToString();
                _lastOutputPath = options.locationPathName;
            }
        }

        // MUST be called from the main thread (BuildReport.summary access).
        public void OnBuildFinishedMainThread(BuildReport report, string error)
        {
            FinishedSnapshot snap = null;
            if (report != null)
            {
                BuildSummary s = report.summary;
                snap = new FinishedSnapshot
                {
                    Result = s.result.ToString(),
                    TotalErrors = s.totalErrors,
                    TotalWarnings = s.totalWarnings,
                    TotalSize = s.totalSize,
                    DurationMs = (long)s.totalTime.TotalMilliseconds,
                };
                foreach (BuildStep step in report.steps)
                {
                    foreach (BuildStepMessage msg in step.messages)
                    {
                        if (msg.type == LogType.Error || msg.type == LogType.Exception || msg.type == LogType.Assert)
                        {
                            snap.Errors.Add(new JObject
                            {
                                ["step"] = step.name,
                                ["type"] = msg.type.ToString(),
                                ["content"] = msg.content,
                            });
                        }
                    }
                }
            }
            lock (_lock)
            {
                _isRunning = false;
                _finishedAt = DateTime.Now;
                _lastFinished = snap;
                _lastError = error;
            }
        }

        public JObject GetSnapshot()
        {
            lock (_lock)
            {
                JObject result = new JObject
                {
                    ["isRunning"] = _isRunning,
                    ["startedAt"] = _startedAt == default ? null : _startedAt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    ["finishedAt"] = _finishedAt == default ? null : _finishedAt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                };
                if (_lastScenes != null)
                {
                    result["scenes"] = new JArray(_lastScenes);
                    result["target"] = _lastTarget;
                    result["outputPath"] = _lastOutputPath;
                }
                if (_lastError != null)
                {
                    result["exception"] = _lastError;
                }
                if (_lastFinished != null)
                {
                    result["result"] = _lastFinished.Result;
                    result["totalErrors"] = _lastFinished.TotalErrors;
                    result["totalWarnings"] = _lastFinished.TotalWarnings;
                    result["totalSize"] = _lastFinished.TotalSize;
                    result["durationMs"] = _lastFinished.DurationMs;
                    JArray errors = new JArray();
                    foreach (JObject e in _lastFinished.Errors) errors.Add(e);
                    result["errors"] = errors;
                }
                return result;
            }
        }
    }

    /// <summary>
    /// Triggers a Player build on the main thread asynchronously and returns immediately.
    /// Use <see cref="WaitForBuildDoneHandler"/> to block until completion.
    /// </summary>
    internal class BuildPlayerHandler : HandlerBase
    {
        readonly BuildState _state;

        public BuildPlayerHandler(MCPHttpServer server, BuildState state) : base(server) { _state = state; }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() =>
            {
                if (_state.IsRunning)
                {
                    return CreateErrorResponse("build_in_progress", "A build is already running. Wait for it to finish before starting another.");
                }

                JObject req;
                try { req = string.IsNullOrEmpty(requestBody) ? new JObject() : JObject.Parse(requestBody); }
                catch (JsonReaderException) { req = new JObject(); }

                string outputPath = req["outputPath"]?.ToString();
                if (string.IsNullOrEmpty(outputPath))
                {
                    return CreateErrorResponse("missing_parameter", "outputPath is required (absolute path to the player executable to write).");
                }

                string targetStr = req["target"]?.ToString() ?? "StandaloneWindows64";
                BuildTarget target;
                if (!Enum.TryParse(targetStr, out target))
                {
                    return CreateErrorResponse("invalid_target", $"Unknown BuildTarget '{targetStr}'. Use Unity's BuildTarget enum names (e.g., StandaloneWindows64, StandaloneOSX, StandaloneLinux64, Android, iOS).");
                }

                string[] scenes;
                JArray scenesJArr = req["scenes"] as JArray;
                if (scenesJArr != null && scenesJArr.Count > 0)
                {
                    scenes = scenesJArr.Select(t => t.ToString()).ToArray();
                }
                else
                {
                    scenes = EditorBuildSettings.scenes
                        .Where(s => s.enabled)
                        .Select(s => s.path)
                        .ToArray();
                    if (scenes.Length == 0)
                    {
                        return CreateErrorResponse("no_scenes", "No scenes provided and EditorBuildSettings has no enabled scenes.");
                    }
                }

                bool development = req["development"]?.ToObject<bool>() ?? false;
                bool autoRunPlayer = req["autoRunPlayer"]?.ToObject<bool>() ?? false;

                BuildOptions buildOptions = BuildOptions.None;
                if (development) buildOptions |= BuildOptions.Development;
                if (autoRunPlayer) buildOptions |= BuildOptions.AutoRunPlayer;

                BuildPlayerOptions options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outputPath,
                    target = target,
                    targetGroup = BuildPipeline.GetBuildTargetGroup(target),
                    options = buildOptions,
                };

                _state.OnBuildStarted(options);

                // Fire-and-forget enqueue on the main-thread queue (does not depend on EditorApplication.delayCall,
                // which is throttled when the editor is unfocused).
                Server.EnqueueOnMainThread(() => RunBuild(options));

                return CreateSuccessResponse("build_started", _state.GetSnapshot());
            });
        }

        void RunBuild(BuildPlayerOptions options)
        {
            try
            {
                string dir = Path.GetDirectoryName(options.locationPathName);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                Debug.Log($"[Claude Code MCP / Build] Starting build → {options.locationPathName}");
                BuildReport report = BuildPipeline.BuildPlayer(options);
                _state.OnBuildFinishedMainThread(report, null);
                Debug.Log($"[Claude Code MCP / Build] Build finished: {report.summary.result} (errors={report.summary.totalErrors}, warnings={report.summary.totalWarnings})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Claude Code MCP / Build] Build threw exception: {ex}");
                _state.OnBuildFinishedMainThread(null, ex.ToString());
            }
        }
    }

    /// <summary>
    /// Blocks the request thread until the most recent build finishes or until timeoutMs elapses.
    /// Returns the build snapshot.
    /// </summary>
    internal class WaitForBuildDoneHandler : HandlerBase
    {
        readonly BuildState _state;

        public WaitForBuildDoneHandler(MCPHttpServer server, BuildState state) : base(server) { _state = state; }

        public override string Handle(string requestBody)
        {
            int timeoutMs = 600000;
            int pollMs = 500;
            if (!string.IsNullOrEmpty(requestBody))
            {
                try
                {
                    JObject req = JObject.Parse(requestBody);
                    timeoutMs = req["timeoutMs"]?.ToObject<int?>() ?? timeoutMs;
                    pollMs = req["pollMs"]?.ToObject<int?>() ?? pollMs;
                }
                catch (JsonReaderException) { /* defaults */ }
            }

            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                if (!_state.IsRunning) break;
                Thread.Sleep(pollMs);
            }

            JObject snap = _state.GetSnapshot();
            snap["timedOut"] = _state.IsRunning;
            return CreateSuccessResponse("build_done", snap);
        }
    }

    internal class GetBuildStatusHandler : HandlerBase
    {
        readonly BuildState _state;

        public GetBuildStatusHandler(MCPHttpServer server, BuildState state) : base(server) { _state = state; }

        public override string Handle(string requestBody)
        {
            return CreateSuccessResponse("build_status", _state.GetSnapshot());
        }
    }
}
