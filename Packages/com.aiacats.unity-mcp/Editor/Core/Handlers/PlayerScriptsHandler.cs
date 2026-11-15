using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEditor.Compilation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Debug = UnityEngine.Debug;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    /// <summary>
    /// Player 向けのスクリプトだけのコンパイル（PlayerBuildInterface.CompilePlayerScripts）の状態。
    /// 開始は <see cref="CompilePlayerScriptsHandler"/>、待ちは <see cref="WaitForPlayerScriptsDoneHandler"/>。
    ///
    /// CompilePlayerScripts はメインスレッドを塞ぐ同期呼び出しで、117 アセンブリの案件では数十秒〜数分かかる。
    /// 同期で返すと ExecuteOnMainThread の待ち（既定 5 秒）と、この橋の HTTP 応答の上限（300 秒）を越えうるため、
    /// build_player と同じく「開始して返す → wait で待つ」の形にする。
    ///
    /// コンパイラのメッセージは CompilationPipeline.assemblyCompilationFinished で集める。
    /// Unity 6000.3 / 2021.3 の EditorCompilation を逆コンパイルして確かめた経路:
    /// CompilePlayerScripts → (native) → EditorCompilation.CompileScriptsWithSettings → TickCompilationPipeline
    /// → ProcessCompilationResult → InvokeAssemblyCompilationFinished。Player 向け（BuildingForEditor = false）でも呼ばれる。
    /// ただし Bee が「最新」と判断して実際にはコンパイルしなかったアセンブリは assemblyCompilationNotRequired だけが来て
    /// メッセージが無い。失敗したノードは最新扱いにならないのでエラーは必ず届くが、警告はコンパイルし直した分しか数えられない。
    /// </summary>
    internal class PlayerScriptsState
    {
        class Message
        {
            public string Assembly;
            public string File;
            public int Line;
            public int Column;
            public string Text;
        }

        readonly object _lock = new object();
        bool _isRunning;
        DateTime _startedAt;
        DateTime _finishedAt;
        string _target;
        bool _development;
        string[] _extraDefines;

        // 終了後の結果
        bool _hasResult;
        int _assemblyCount;
        long _durationMs;
        readonly List<Message> _errors = new List<Message>();
        int _warningCount;
        int _assembliesRecompiled;
        int _assembliesUpToDate;
        readonly List<string> _otherErrors = new List<string>();
        string _exception;

        public bool IsRunning { get { lock (_lock) return _isRunning; } }

        /// <summary>
        /// CompilePlayerScripts の実行中だけ true（メインスレッドで読み書き）。
        /// MCPHttpServer は、この間に来た assemblyCompilationFinished を Editor のコンパイル結果
        /// （get_compilation_errors）として記録しない。
        /// </summary>
        public static bool IsCapturing { get; private set; }

        public void OnStarted(BuildTarget target, bool development, string[] extraDefines)
        {
            lock (_lock)
            {
                _isRunning = true;
                _startedAt = DateTime.Now;
                _finishedAt = default;
                _target = target.ToString();
                _development = development;
                _extraDefines = extraDefines;

                _hasResult = false;
                _assemblyCount = 0;
                _durationMs = 0;
                _errors.Clear();
                _warningCount = 0;
                _assembliesRecompiled = 0;
                _assembliesUpToDate = 0;
                _otherErrors.Clear();
                _exception = null;
            }
        }

        /// <summary>メインスレッドで呼ぶ。CompilePlayerScripts を実行し、メッセージを集めて結果を記録する。</summary>
        public void RunMainThread(ScriptCompilationSettings settings, string outputFolder)
        {
            var compilerErrors = new List<Message>();
            var logErrors = new List<string>();
            int warnings = 0, recompiled = 0, upToDate = 0, assemblyCount = 0;
            string exception = null;

            Action<string, CompilerMessage[]> onFinished = (assemblyPath, messages) =>
            {
                recompiled++;
                if (messages == null) return;
                string assemblyName = Path.GetFileName(assemblyPath);
                foreach (CompilerMessage m in messages)
                {
                    if (m.type == CompilerMessageType.Error)
                    {
                        compilerErrors.Add(new Message { Assembly = assemblyName, File = m.file, Line = m.line, Column = m.column, Text = m.message });
                    }
                    else if (m.type == CompilerMessageType.Warning)
                    {
                        warnings++;
                    }
                }
            };
            Action<string> onNotRequired = _ => upToDate++;
            // コンパイラ以外のエラー（asmdef の設定の誤り・Bee の内部エラーなど）は assemblyCompilationFinished に来ないので、
            // 実行中のログのエラーも別の欄（otherErrors）に分けて返す
            Application.LogCallback onLog = (condition, stackTrace, type) =>
            {
                if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
                if (condition != null && condition.StartsWith("[Claude Code MCP", StringComparison.Ordinal)) return;
                logErrors.Add(condition);
            };

            var sw = Stopwatch.StartNew();
            IsCapturing = true;
            CompilationPipeline.assemblyCompilationFinished += onFinished;
            CompilationPipeline.assemblyCompilationNotRequired += onNotRequired;
            Application.logMessageReceived += onLog;
            try
            {
                PrepareEmptyFolder(outputFolder);
                Debug.Log($"[Claude Code MCP / PlayerScripts] Compiling player scripts for {settings.target} (options={settings.options}) → {outputFolder}");
                ScriptCompilationResult result = PlayerBuildInterface.CompilePlayerScripts(settings, outputFolder);
                assemblyCount = result.assemblies?.Count ?? 0;
            }
            catch (Exception ex)
            {
                exception = ex.ToString();
            }
            finally
            {
                Application.logMessageReceived -= onLog;
                CompilationPipeline.assemblyCompilationNotRequired -= onNotRequired;
                CompilationPipeline.assemblyCompilationFinished -= onFinished;
                IsCapturing = false;
                sw.Stop();

                try { if (Directory.Exists(outputFolder)) Directory.Delete(outputFolder, true); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Claude Code MCP / PlayerScripts] Could not delete the temporary output folder {outputFolder}: {ex.Message}");
                }
            }

            // Unity はコンパイラのメッセージを Console にも出すので、ログ側の同じ内容は otherErrors から外す
            List<string> otherErrors = logErrors
                .Where(l => !string.IsNullOrEmpty(l))
                .Where(l => !compilerErrors.Any(e => !string.IsNullOrEmpty(e.Text) && (l.Contains(e.Text) || e.Text.Contains(l))))
                .Distinct()
                .ToList();

            lock (_lock)
            {
                _isRunning = false;
                _finishedAt = DateTime.Now;
                _hasResult = true;
                _assemblyCount = assemblyCount;
                _durationMs = sw.ElapsedMilliseconds;
                _errors.AddRange(compilerErrors);
                _warningCount = warnings;
                _assembliesRecompiled = recompiled;
                _assembliesUpToDate = upToDate;
                _otherErrors.AddRange(otherErrors);
                _exception = exception;
            }

            bool success = exception == null && compilerErrors.Count == 0 && assemblyCount > 0;
            string summary = $"[Claude Code MCP / PlayerScripts] {(success ? "Succeeded" : "Failed")}: assemblies={assemblyCount}, errors={compilerErrors.Count}, warnings={warnings}, otherErrors={otherErrors.Count}, {sw.ElapsedMilliseconds} ms";
            if (success) Debug.Log(summary); else Debug.LogWarning(summary);
        }

        static void PrepareEmptyFolder(string folder)
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
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
                if (_target != null)
                {
                    result["target"] = _target;
                    result["development"] = _development;
                    result["extraScriptingDefines"] = new JArray(_extraDefines ?? new string[0]);
                }
                if (_hasResult)
                {
                    // 成否: 例外なし・コンパイラのエラーなし・アセンブリが 1 つ以上できた
                    // （asmdef の設定の誤りなどではエラーのメッセージ無しでアセンブリが 0 になる。そのときの理由は otherErrors）
                    result["success"] = _exception == null && _errors.Count == 0 && _assemblyCount > 0;
                    result["assemblyCount"] = _assemblyCount;
                    result["errorCount"] = _errors.Count;
                    JArray errors = new JArray();
                    foreach (Message e in _errors)
                    {
                        errors.Add(new JObject
                        {
                            ["file"] = e.File,
                            ["line"] = e.Line,
                            ["column"] = e.Column,
                            ["message"] = e.Text,
                            ["assembly"] = e.Assembly,
                        });
                    }
                    result["errors"] = errors;
                    result["warningCount"] = _warningCount;
                    // 警告はコンパイルし直したアセンブリの分だけ。最新扱いで飛ばされた分（assembliesUpToDate）の警告は数えられない
                    result["assembliesRecompiled"] = _assembliesRecompiled;
                    result["assembliesUpToDate"] = _assembliesUpToDate;
                    result["otherErrors"] = new JArray(_otherErrors);
                    result["durationMs"] = _durationMs;
                    if (_exception != null) result["exception"] = _exception;
                }
                return result;
            }
        }
    }

    /// <summary>
    /// Player 向けにスクリプトだけをコンパイルする（シェーダーもシーンも作らない）。開始して直ちに返す。
    /// 待つのは <see cref="WaitForPlayerScriptsDoneHandler"/>。
    /// </summary>
    internal class CompilePlayerScriptsHandler : HandlerBase
    {
        readonly PlayerScriptsState _state;
        readonly BuildState _buildState;

        public CompilePlayerScriptsHandler(MCPHttpServer server, PlayerScriptsState state, BuildState buildState) : base(server)
        {
            _state = state;
            _buildState = buildState;
        }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() =>
            {
                if (_state.IsRunning)
                {
                    return CreateErrorResponse("player_scripts_in_progress", "A player script compilation is already running. Use wait_for_player_scripts_done.");
                }
                if (_buildState.IsRunning)
                {
                    return CreateErrorResponse("build_in_progress", "A player build is running. Wait for it to finish (wait_for_build_done) first.");
                }
                if (EditorApplication.isCompiling)
                {
                    return CreateErrorResponse("editor_compiling", "The editor is compiling scripts. Wait for it to finish (wait_for_compilation_done) first.");
                }

                JObject req;
                try { req = string.IsNullOrEmpty(requestBody) ? new JObject() : JObject.Parse(requestBody); }
                catch (JsonReaderException) { req = new JObject(); }

                string targetStr = req["target"]?.ToString() ?? "StandaloneWindows64";
                BuildTarget target;
                if (!Enum.TryParse(targetStr, out target) || !Enum.IsDefined(typeof(BuildTarget), target))
                {
                    return CreateErrorResponse("invalid_target", $"Unknown BuildTarget '{targetStr}'. Use Unity's BuildTarget enum names (e.g., StandaloneWindows64, StandaloneOSX, StandaloneLinux64, Android, iOS).");
                }
                BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
                if (!BuildPipeline.IsBuildTargetSupported(group, target))
                {
                    return CreateErrorResponse("target_not_supported", $"BuildTarget '{target}' is not installed in this editor (add the platform module from Unity Hub).");
                }

                bool development = req["development"]?.ToObject<bool>() ?? false;

                string[] extraDefines = new string[0];
                JToken definesToken = req["extraScriptingDefines"];
                if (definesToken != null && definesToken.Type != JTokenType.Null)
                {
                    JArray arr = definesToken as JArray;
                    if (arr == null)
                    {
                        return CreateErrorResponse("invalid_parameter", "extraScriptingDefines must be an array of strings.");
                    }
                    extraDefines = arr.Select(t => t.ToString().Trim()).Where(s => s.Length > 0).ToArray();
                }

                var settings = new ScriptCompilationSettings
                {
                    target = target,
                    group = group,
                    options = development ? ScriptCompilationOptions.DevelopmentBuild : ScriptCompilationOptions.None,
                    extraScriptingDefines = extraDefines,
                };

                // 出力はプロジェクトの Temp（Editor の終了で消える）。毎回同じ場所にするのは、Bee の DAG の名前が
                // 出力先のハッシュで決まるため（毎回別の場所だと Library/Bee に DAG がたまり、差分コンパイルも効かない）
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                string outputFolder = Path.Combine(projectRoot, "Temp", "ClaudeCodeMCP", "PlayerScripts");

                _state.OnStarted(target, development, extraDefines);
                Server.EnqueueOnMainThread(() => _state.RunMainThread(settings, outputFolder));

                return CreateSuccessResponse("player_scripts_started", _state.GetSnapshot());
            });
        }
    }

    /// <summary>
    /// 直近の compile_player_scripts が終わるまで（または timeoutMs まで）要求スレッドで待つ。メインスレッドには触らない
    /// （コンパイル中はメインスレッドが塞がっている）。
    /// </summary>
    internal class WaitForPlayerScriptsDoneHandler : HandlerBase
    {
        readonly PlayerScriptsState _state;

        public WaitForPlayerScriptsDoneHandler(MCPHttpServer server, PlayerScriptsState state) : base(server) { _state = state; }

        public override string Handle(string requestBody)
        {
            // 既定 240 秒: この橋の HTTP 応答の上限 300 秒（Node の fetch の headersTimeout）を下回るように
            int timeoutMs = 240000;
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
            return CreateSuccessResponse("player_scripts_done", snap);
        }
    }

    internal class GetPlayerScriptsStatusHandler : HandlerBase
    {
        readonly PlayerScriptsState _state;

        public GetPlayerScriptsStatusHandler(MCPHttpServer server, PlayerScriptsState state) : base(server) { _state = state; }

        public override string Handle(string requestBody)
        {
            return CreateSuccessResponse("player_scripts_status", _state.GetSnapshot());
        }
    }
}
