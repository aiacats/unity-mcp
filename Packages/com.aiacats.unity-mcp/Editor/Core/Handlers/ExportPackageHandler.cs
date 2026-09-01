using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    /// <summary>
    /// Tracks state of the most recent .unitypackage export. Updated from the main thread by
    /// <see cref="ExportPackageHandler"/>. Polled by <see cref="WaitForExportDoneHandler"/> /
    /// <see cref="GetExportStatusHandler"/>.
    ///
    /// Exporting is a blocking main-thread call that compresses every included asset, so a
    /// 30MB package takes well over a minute. Running it inline would blow past the MCP request
    /// timeout, which is why this mirrors the async start/wait shape of BuildState.
    /// </summary>
    internal class ExportState
    {
        readonly object _lock = new object();
        bool _isRunning;
        DateTime _startedAt;
        DateTime _finishedAt;
        string _outputPath;
        int _assetCount;
        long _fileSize;
        string _lastError;

        public bool IsRunning { get { lock (_lock) return _isRunning; } }

        public void OnStarted(string outputPath, int assetCount)
        {
            lock (_lock)
            {
                _isRunning = true;
                _startedAt = DateTime.Now;
                _finishedAt = default;
                _outputPath = outputPath;
                _assetCount = assetCount;
                _fileSize = 0;
                _lastError = null;
            }
        }

        public void OnFinished(long fileSize, string error)
        {
            lock (_lock)
            {
                _isRunning = false;
                _finishedAt = DateTime.Now;
                _fileSize = fileSize;
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
                    ["outputPath"] = _outputPath,
                    ["assetCount"] = _assetCount,
                };

                if (_startedAt != default && _finishedAt != default)
                {
                    result["durationMs"] = (long)(_finishedAt - _startedAt).TotalMilliseconds;
                }
                if (_fileSize > 0)
                {
                    result["fileSize"] = _fileSize;
                }
                if (_lastError != null)
                {
                    result["exception"] = _lastError;
                }

                return result;
            }
        }
    }

    /// <summary>
    /// Exports the given asset paths to a .unitypackage. Starts asynchronously and returns
    /// immediately; use <see cref="WaitForExportDoneHandler"/> to block until it finishes.
    ///
    /// Dependency collection is OFF by default. Unity's IncludeDependencies pulls in whatever the
    /// selected assets happen to reference, which makes the contents of the resulting package
    /// unpredictable - the caller normally wants exactly what it asked for and nothing else.
    /// </summary>
    internal class ExportPackageHandler : HandlerBase
    {
        readonly ExportState _state;

        public ExportPackageHandler(MCPHttpServer server, ExportState state) : base(server) { _state = state; }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() =>
            {
                if (_state.IsRunning)
                {
                    return CreateErrorResponse("export_in_progress", "An export is already running. Wait for it to finish before starting another.");
                }

                JObject req;
                try { req = string.IsNullOrEmpty(requestBody) ? new JObject() : JObject.Parse(requestBody); }
                catch (JsonReaderException) { req = new JObject(); }

                string outputPath = req["outputPath"]?.ToString();
                if (string.IsNullOrEmpty(outputPath))
                {
                    return CreateErrorResponse("missing_parameter", "outputPath is required (absolute path to the .unitypackage to write).");
                }

                JArray assetsJArr = req["assetPaths"] as JArray;
                if (assetsJArr == null || assetsJArr.Count == 0)
                {
                    return CreateErrorResponse("missing_parameter", "assetPaths is required (array of project-relative paths, e.g. Assets/MyFolder).");
                }

                string[] roots = assetsJArr.Select(t => Normalize(t.ToString())).ToArray();

                string[] missing = roots.Where(p => !AssetExists(p)).ToArray();
                if (missing.Length > 0)
                {
                    return CreateErrorResponse("asset_not_found", $"Not found in the AssetDatabase: {string.Join(", ", missing)}");
                }

                string[] excluded = (req["excludePaths"] as JArray)?
                    .Select(t => Normalize(t.ToString()))
                    .ToArray() ?? new string[0];

                bool recurse = req["recurse"]?.ToObject<bool>() ?? true;
                bool includeDependencies = req["includeDependencies"]?.ToObject<bool>() ?? false;

                string[] paths;
                ExportPackageOptions options = ExportPackageOptions.Default;

                if (excluded.Length > 0)
                {
                    // Unity applies Recurse itself after the fact, so a folder handed over with
                    // Recurse would drag the excluded files back in. Expand the tree here instead
                    // and give Unity a flat, already-filtered list.
                    if (!recurse)
                    {
                        return CreateErrorResponse("invalid_parameter", "excludePaths requires recurse=true; without recursion there is nothing to exclude.");
                    }

                    HashSet<string> expanded = new HashSet<string>();
                    foreach (string root in roots)
                    {
                        expanded.Add(root);
                        foreach (string child in EnumerateUnder(root)) expanded.Add(child);
                    }

                    HashSet<string> excludedSet = new HashSet<string>(excluded);
                    paths = expanded.Where(p => !IsExcluded(p, excludedSet)).OrderBy(p => p).ToArray();

                    if (paths.Length == 0)
                    {
                        return CreateErrorResponse("nothing_to_export", "Every asset under assetPaths was removed by excludePaths.");
                    }
                }
                else
                {
                    paths = roots;
                    if (recurse) options |= ExportPackageOptions.Recurse;
                }

                if (includeDependencies) options |= ExportPackageOptions.IncludeDependencies;

                _state.OnStarted(outputPath, paths.Length);

                // Fire-and-forget on the main-thread queue (does not depend on EditorApplication.delayCall,
                // which is throttled while the editor is unfocused).
                Server.EnqueueOnMainThread(() => RunExport(paths, outputPath, options));

                return CreateSuccessResponse("export_started", _state.GetSnapshot());
            });
        }

        void RunExport(string[] paths, string outputPath, ExportPackageOptions options)
        {
            try
            {
                string dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                Debug.Log($"[Claude Code MCP / Export] Exporting {paths.Length} asset path(s) to {outputPath}");
                AssetDatabase.ExportPackage(paths, outputPath, options);

                long size = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
                _state.OnFinished(size, null);

                Debug.Log($"[Claude Code MCP / Export] Export finished: {outputPath} ({size / 1024 / 1024} MB)");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Claude Code MCP / Export] Export threw exception: {ex}");
                _state.OnFinished(0, ex.ToString());
            }
        }

        /// <summary>Accepts either slash style and trims trailing separators.</summary>
        static string Normalize(string path)
        {
            return (path ?? string.Empty).Replace((char)92, '/').TrimEnd('/');
        }

        static bool AssetExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return AssetDatabase.IsValidFolder(path) || File.Exists(path);
        }

        /// <summary>
        /// Every asset path under a folder, subfolders included. Uses the AssetDatabase rather
        /// than the file system so that what gets exported matches what Unity treats as an asset.
        /// </summary>
        static IEnumerable<string> EnumerateUnder(string root)
        {
            if (!AssetDatabase.IsValidFolder(root)) yield break;

            foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)) yield return path;
            }
        }

        /// <summary>A folder in excludePaths drops everything beneath it, not just the folder.</summary>
        static bool IsExcluded(string path, HashSet<string> excluded)
        {
            if (excluded.Contains(path)) return true;

            foreach (string e in excluded)
            {
                if (path.StartsWith(e + "/", StringComparison.Ordinal)) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Blocks the request thread until the most recent export finishes or until timeoutMs elapses.
    /// Returns the export snapshot.
    /// </summary>
    internal class WaitForExportDoneHandler : HandlerBase
    {
        readonly ExportState _state;

        public WaitForExportDoneHandler(MCPHttpServer server, ExportState state) : base(server) { _state = state; }

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
            return CreateSuccessResponse("export_done", snap);
        }
    }

    internal class GetExportStatusHandler : HandlerBase
    {
        readonly ExportState _state;

        public GetExportStatusHandler(MCPHttpServer server, ExportState state) : base(server) { _state = state; }

        public override string Handle(string requestBody)
        {
            return CreateSuccessResponse("export_status", _state.GetSnapshot());
        }
    }
}
