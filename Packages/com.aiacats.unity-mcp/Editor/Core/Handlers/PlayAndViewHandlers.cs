using System;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    /// <summary>
    /// Play モードへ入る。ウィンドウフォーカスに依存せず EditorApplication.isPlaying を
    /// メインスレッドで設定するため、ヘッドレス/非フォーカスでも確実に動く。
    /// </summary>
    internal class EnterPlayModeHandler : HandlerBase
    {
        public EnterPlayModeHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() =>
            {
                if (EditorApplication.isCompiling)
                    return CreateErrorResponse("still_compiling", "Cannot enter play mode while compiling");
                if (EditorApplication.isPlaying)
                    return CreateSuccessResponse("already_playing", new JObject { ["isPlaying"] = true });

                EditorApplication.isPlaying = true;
                return CreateSuccessResponse("entering_play_mode", new JObject
                {
                    ["requested"] = true,
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                });
            });
        }
    }

    /// <summary>Play モードを抜けて Edit モードへ戻る。</summary>
    internal class ExitPlayModeHandler : HandlerBase
    {
        public ExitPlayModeHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() =>
            {
                if (!EditorApplication.isPlaying)
                    return CreateSuccessResponse("already_stopped", new JObject { ["isPlaying"] = false });

                EditorApplication.isPlaying = false;
                return CreateSuccessResponse("exiting_play_mode", new JObject
                {
                    ["requested"] = true,
                    ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                });
            });
        }
    }

    /// <summary>現在の Editor play 状態を返す。</summary>
    internal class GetPlayStateHandler : HandlerBase
    {
        public GetPlayStateHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => CreateSuccessResponse("play_state", new JObject
            {
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
            }));
        }
    }

    /// <summary>
    /// Game View の表示 Display を切り替える("Display 1/2/..." ドロップダウン相当)。
    /// GameView は internal のためリフレクションで操作する。Unity バージョン差を吸収するため
    /// プロパティ/フィールド両方を試し、適用後にウィンドウを再描画する。
    /// </summary>
    internal class SetGameViewDisplayHandler : HandlerBase
    {
        public SetGameViewDisplayHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() =>
            {
                int display = 0;
                try
                {
                    var req = JObject.Parse(string.IsNullOrEmpty(requestBody) ? "{}" : requestBody);
                    display = req["display"]?.ToObject<int>() ?? 0;
                }
                catch { /* デフォルト 0 */ }
                if (display < 0) display = 0;

                var gvType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gvType == null)
                    return CreateErrorResponse("gameview_type_not_found", "UnityEditor.GameView 型が見つかりません。");

                var gv = EditorWindow.GetWindow(gvType, false, "Game", true);
                if (gv == null)
                    return CreateErrorResponse("gameview_not_found", "Game View を取得できません。");

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                bool applied = false;

                // 1) プロパティ "targetDisplay"(setter が表示更新まで行う版)。
                var prop = gvType.GetProperty("targetDisplay", flags);
                if (prop != null && prop.CanWrite)
                {
                    try { prop.SetValue(gv, display); applied = true; } catch { }
                }

                // 2) フィールド "m_TargetDisplay"。
                if (!applied)
                {
                    var field = gvType.GetField("m_TargetDisplay", flags);
                    if (field != null)
                    {
                        try { field.SetValue(gv, display); applied = true; } catch { }
                    }
                }

                if (applied)
                {
                    gv.Focus();
                    gv.Repaint();
                    EditorApplication.QueuePlayerLoopUpdate();
                    return CreateSuccessResponse("game_view_display_set", new JObject
                    {
                        ["display"] = display,
                        ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    });
                }

                return CreateErrorResponse("set_display_failed",
                    "Game View の targetDisplay を設定できませんでした(このバージョンの内部APIに不一致)。");
            });
        }
    }

    /// <summary>Editor を現在のプロジェクトで再起動する(EditorApplication.OpenProject)。</summary>
    internal class RestartEditorHandler : HandlerBase
    {
        public RestartEditorHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            // OpenProject はエディタを閉じて開き直すため、レスポンスを返してから fire-and-forget で実行する。
            Server.EnqueueOnMainThread(() =>
            {
                var projectPath = System.IO.Path.GetDirectoryName(Application.dataPath);
                Debug.Log($"[Claude Code MCP] Restarting editor: {projectPath}");
                EditorApplication.OpenProject(projectPath);
            });
            return CreateSuccessResponse("restart_requested", new JObject
            {
                ["message"] = "Editor restart requested. The MCP server will drop and come back after Unity relaunches.",
                ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
            });
        }
    }

    /// <summary>Unity コンソールのログをクリアする(UnityEditor.LogEntries.Clear、internal をリフレクション)。</summary>
    internal class ClearConsoleHandler : HandlerBase
    {
        public ClearConsoleHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() =>
            {
                try
                {
                    var t = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
                    var m = t?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m == null)
                        return CreateErrorResponse("clear_unavailable", "UnityEditor.LogEntries.Clear が見つかりません。");
                    m.Invoke(null, null);
                    return CreateSuccessResponse("console_cleared", new JObject
                    {
                        ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    });
                }
                catch (Exception e)
                {
                    return CreateErrorResponse("clear_failed", e.Message);
                }
            });
        }
    }
}
