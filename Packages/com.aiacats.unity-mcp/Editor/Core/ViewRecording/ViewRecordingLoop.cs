using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.LowLevel;

namespace ClaudeCodeMCP.Editor.Core.ViewRecording
{
    /// <summary>
    /// Play 中のフレームに 2 か所だけ処理を差し込む（PlayerLoop）。
    /// - ポーズ: PreLateUpdate.DirectorUpdateAnimationEnd の直後。Animator 自身の評価より後で、LateUpdate の補正スクリプトより前。
    /// - 撮影: PostLateUpdate.UpdateAllSkinnedMeshes の直後。LateUpdate の中で Camera.Render するとスキニング前で、
    ///   骨に付けた持ち物と手のメッシュが 1 フレームずれて写る。スキニングの後なら最新の形が写る。
    /// MonoBehaviour を置かないので、シーンに何も残らず、Game ビューが描画されていなくても毎フレーム動く
    /// （WaitForEndOfFrame は Editor では Game ビューの描画に引きずられる）。
    /// 差し込みは Play 中だけ。Play を抜ける・ドメインリロードの前に取り除き、撮影の後始末もここから呼ぶ。
    /// </summary>
    [InitializeOnLoad]
    internal static class ViewRecordingLoop
    {
        private struct ClipPoseStep { }
        private struct ViewCaptureStep { }

        private static bool _installed;

        /// <summary>Play を抜けている最中（ExitingPlayMode を受けてから EnteredEditMode まで）。後始末で即時破棄を避ける判定に使う</summary>
        internal static bool IsExitingPlayMode { get; private set; }

        static ViewRecordingLoop()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        /// <summary>差し込む。見つからない Unity では例外（別の時点へ黙って差し替えない）</summary>
        internal static void EnsureInstalled()
        {
            if (_installed) return;
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            RemoveOurs(ref loop);
            if (!InsertAfter(ref loop, typeof(UnityEngine.PlayerLoop.PreLateUpdate),
                    typeof(UnityEngine.PlayerLoop.PreLateUpdate.DirectorUpdateAnimationEnd),
                    new PlayerLoopSystem { type = typeof(ClipPoseStep), updateDelegate = OnClipPose }))
                throw new InvalidOperationException("PlayerLoop has no PreLateUpdate.DirectorUpdateAnimationEnd; cannot apply clips at the expected point on this Unity version.");
            if (!InsertAfter(ref loop, typeof(UnityEngine.PlayerLoop.PostLateUpdate),
                    typeof(UnityEngine.PlayerLoop.PostLateUpdate.UpdateAllSkinnedMeshes),
                    new PlayerLoopSystem { type = typeof(ViewCaptureStep), updateDelegate = OnViewCapture }))
                throw new InvalidOperationException("PlayerLoop has no PostLateUpdate.UpdateAllSkinnedMeshes; cannot capture after skinning on this Unity version.");
            PlayerLoop.SetPlayerLoop(loop);
            _installed = true;
        }

        internal static void Uninstall()
        {
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            if (RemoveOurs(ref loop)) PlayerLoop.SetPlayerLoop(loop);
            _installed = false;
        }

        private static void OnClipPose()
        {
            try
            {
                ViewRecorder.BeforePose();
                ClipPlayer.EvaluateAll(Time.deltaTime);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                ViewRecorder.Fail("clip step: " + ex.Message);
            }
        }

        private static void OnViewCapture()
        {
            // 録画と静止画は別々に守る（片方の例外でもう片方を巻き添えにしない）
            try { ViewRecorder.AfterSkinning(); }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                ViewRecorder.Fail("capture step: " + ex.Message);
            }
            try { ViewCapture.AfterSkinning(); }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                ViewCapture.Fail("capture step: " + ex.Message);
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingPlayMode:
                    IsExitingPlayMode = true;
                    CleanUpAll("exited Play Mode");
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    IsExitingPlayMode = false;
                    break;
                case PlayModeStateChange.ExitingEditMode:
                    // Edit 中の静止画用に作った撮影一式が残っていれば、Play に入る前に片付ける
                    ViewCapture.Fail("entering Play Mode");
                    break;
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            // 古いアセンブリの関数を PlayerLoop に残すと、リロード後に解放済みのコードを呼ぶ
            CleanUpAll("assembly reload");
            // 静的状態ごと消える前に、書き込み待ちのコマと recording.json を書き切る。
            // 待つ上限は、バッファ 4 枚ぶんの PNG（実測 27 ms/コマ）に余裕を見た 10 秒。超えたら残りは書かれない
            ViewRecorder.WaitForWriter(10000);
        }

        private static void CleanUpAll(string reason)
        {
            try { ViewRecorder.Abort(reason); } catch (Exception ex) { Debug.LogException(ex); }
            try { ViewCapture.Fail(reason); } catch (Exception ex) { Debug.LogException(ex); }
            try { ClipPlayer.DisposeAll(); } catch (Exception ex) { Debug.LogException(ex); }
            try { TemporaryOverrides.ReleaseAll(); } catch (Exception ex) { Debug.LogException(ex); }
            Uninstall();
        }

        private static bool InsertAfter(ref PlayerLoopSystem loop, Type phase, Type anchor, PlayerLoopSystem step)
        {
            if (loop.subSystemList == null) return false;
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (loop.subSystemList[i].type != phase) continue;
                PlayerLoopSystem[] subs = loop.subSystemList[i].subSystemList;
                if (subs == null) return false;
                int index = Array.FindIndex(subs, s => s.type == anchor);
                if (index < 0) return false;
                var list = new List<PlayerLoopSystem>(subs);
                list.Insert(index + 1, step);
                loop.subSystemList[i].subSystemList = list.ToArray();
                return true;
            }
            return false;
        }

        private static bool RemoveOurs(ref PlayerLoopSystem system)
        {
            if (system.subSystemList == null) return false;
            bool removed = false;
            var kept = new List<PlayerLoopSystem>(system.subSystemList.Length);
            for (int i = 0; i < system.subSystemList.Length; i++)
            {
                PlayerLoopSystem sub = system.subSystemList[i];
                if (sub.type == typeof(ClipPoseStep) || sub.type == typeof(ViewCaptureStep)
                    // ドメインリロードをまたいで残った古い型（別の Type インスタンス）も名前で拾う
                    || (sub.type != null && sub.type.DeclaringType != null && sub.type.DeclaringType.FullName == typeof(ViewRecordingLoop).FullName))
                {
                    removed = true;
                    continue;
                }
                if (RemoveOurs(ref sub)) removed = true;
                kept.Add(sub);
            }
            if (removed) system.subSystemList = kept.ToArray();
            return removed;
        }
    }
}
