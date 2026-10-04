using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ClaudeCodeMCP.Editor.Core.ViewRecording;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    /// <summary>
    /// キャラの見え方を撮るツール群の共通部分（対象・クリップ・撮影設定の解釈）。
    /// 引数の誤りは ArgumentException で返し、呼び出し側がそのまま error にする。
    /// </summary>
    internal abstract class ViewHandlerBase : HandlerBase
    {
        // 既定値の由来
        // - 距離 1m・FOV 30°: 2026-10-04 SinkaiFes2026 の検証で、顔から手元（口元のマイク）までが 1 コマに収まった値
        // - 1 角度あたり 720x720: 同じ検証で、指先とマイクの位置関係が拡大して読めた解像度
        // - 背景 0.25 の灰: 黒い髪・輪郭線が背景に溶けず、白い衣装も飛ばない中間の明るさ
        // - ライト 1.2: 同じ検証で、シーンのライトが当たらない向きからでも顔が黒潰れしなかった強さ
        protected const float DefaultDistance = 1f;
        protected const float DefaultFov = 30f;
        protected const int DefaultViewSize = 720;
        protected const float DefaultBackground = 0.25f;
        protected const float DefaultLightIntensity = 1.2f;

        // メインスレッドでの準備を待つ上限。既定の 5 秒だと、背面で更新が間引かれた Editor や多数の古いコマの削除で間に合わず、
        // 呼び出し側には timeout を返したのに処理は後でキューから走る。30 秒あれば 1 フレームが数秒に間引かれても届く
        protected const int MainThreadTimeoutMs = 30000;

        protected ViewHandlerBase(MCPHttpServer server) : base(server) { }

        protected static JObject Parse(string body) => string.IsNullOrEmpty(body) ? new JObject() : JObject.Parse(body);

        protected static GameObject ResolveObject(string path, string argName)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException(argName + " is required.");
            GameObject go = FindIncludingInactive(path);
            if (go == null) throw new ArgumentException($"{argName}: GameObject not found: {path}");
            return go;
        }

        /// <summary>target の Animator。本人に無ければ子に 1 つだけあるものを使う（複数なら選ばせる）</summary>
        protected static Animator ResolveAnimator(GameObject target)
        {
            Animator an = target.GetComponent<Animator>();
            if (an != null) return an;
            Animator[] found = target.GetComponentsInChildren<Animator>(true);
            if (found.Length == 1) return found[0];
            if (found.Length == 0) throw new ArgumentException($"target: no Animator on or under '{target.name}'.");
            var names = new List<string>();
            foreach (Animator a in found) names.Add(TemporaryOverrides.PathOf(a.transform));
            throw new ArgumentException("target: several Animators found; pass one of: " + string.Join(", ", names));
        }

        protected static AnimationClip LoadClip(string clipPath, string clipName)
        {
            if (string.IsNullOrEmpty(clipPath)) throw new ArgumentException("clipPath is required.");
            var clips = new List<AnimationClip>();
            foreach (UnityEngine.Object o in AssetDatabase.LoadAllAssetsAtPath(clipPath))
                if (o is AnimationClip c && !c.name.StartsWith("__preview__", StringComparison.Ordinal)) clips.Add(c);
            if (clips.Count == 0) throw new ArgumentException("clipPath: no AnimationClip in " + clipPath);
            if (!string.IsNullOrEmpty(clipName))
            {
                foreach (AnimationClip c in clips) if (c.name == clipName) return c;
                throw new ArgumentException($"clipName '{clipName}' not in {clipPath}. Clips: {ClipNames(clips)}");
            }
            if (clips.Count > 1) throw new ArgumentException($"{clipPath} has several clips; pass clipName. Clips: {ClipNames(clips)}");
            return clips[0];
        }

        private static string ClipNames(List<AnimationClip> clips)
        {
            var names = new List<string>();
            foreach (AnimationClip c in clips) names.Add(c.name);
            return string.Join(", ", names);
        }

        protected static TemporaryOverrides.Lease AcquireOverrides(JObject req, GameObject scope)
        {
            string[] types = req["disableComponents"]?.ToObject<string[]>();
            string[] hidePaths = req["hideObjects"]?.ToObject<string[]>();
            GameObject[] hide = null;
            if (hidePaths != null)
            {
                hide = new GameObject[hidePaths.Length];
                for (int i = 0; i < hidePaths.Length; i++) hide[i] = ResolveObject(hidePaths[i], "hideObjects");
            }
            if ((types == null || types.Length == 0) && hide == null) return null;
            return TemporaryOverrides.Acquire(scope, types, hide);
        }

        /// <summary>capture_views / start_view_recording 共通の撮影設定</summary>
        protected static ViewSettings ParseView(JObject req, GameObject target, Animator animator)
        {
            var s = new ViewSettings { Animator = animator, Target = target };

            var yaw = new List<float>();
            var pitch = new List<float>();
            JToken viewsToken = req["views"];
            if (viewsToken != null && !(viewsToken is JArray))
                throw new ArgumentException("views must be an array of {yaw, pitch}.");
            if (viewsToken is JArray views)
            {
                if (views.Count == 0) throw new ArgumentException("views is empty; omit it for one front view.");
                foreach (JToken v in views)
                {
                    yaw.Add(v["yaw"]?.ToObject<float>() ?? 0f);
                    pitch.Add(v["pitch"]?.ToObject<float>() ?? 0f);
                }
            }
            else
            {
                yaw.Add(0f);
                pitch.Add(0f);
            }
            s.Yaw = yaw.ToArray();
            s.Pitch = pitch.ToArray();

            string focusPath = req["focusPath"]?.ToString();
            if (!string.IsNullOrEmpty(focusPath))
            {
                s.Focus = ResolveObject(focusPath, "focusPath").transform;
                s.FocusName = TemporaryOverrides.PathOf(s.Focus);
            }
            else
            {
                string boneName = req["focusBone"]?.ToString() ?? "Head";
                if (!Enum.TryParse(boneName, true, out HumanBodyBones bone) || bone == HumanBodyBones.LastBone)
                    throw new ArgumentException("focusBone: not a HumanBodyBones name: " + boneName);
                if (!animator.isHuman) throw new ArgumentException($"focusBone needs a Humanoid Animator ('{animator.name}' is not). Pass focusPath instead.");
                s.Focus = animator.GetBoneTransform(bone);
                if (s.Focus == null) throw new ArgumentException($"focusBone: '{animator.name}' has no {bone} bone. Pass another bone or focusPath.");
                s.FocusName = bone + " (" + s.Focus.name + ")";
            }
            float[] off = req["focusOffset"]?.ToObject<float[]>();
            if (off != null && off.Length != 3) throw new ArgumentException("focusOffset must be [right, up, forward] in meters.");
            s.FocusOffset = off == null ? Vector3.zero : new Vector3(off[0], off[1], off[2]);

            s.Distance = req["distance"]?.ToObject<float>() ?? DefaultDistance;
            s.Fov = req["fov"]?.ToObject<float>() ?? DefaultFov;
            if (s.Distance <= 0f) throw new ArgumentException("distance must be > 0.");
            if (s.Fov <= 0f || s.Fov >= 180f) throw new ArgumentException("fov must be between 0 and 180.");

            int n = s.Yaw.Length;
            s.Width = req["width"]?.ToObject<int>() ?? DefaultViewSize * n;
            s.Height = req["height"]?.ToObject<int>() ?? DefaultViewSize;
            int maxSize = SystemInfo.maxTextureSize;
            if (s.Width < n || s.Height < 1 || s.Width > maxSize || s.Height > maxSize)
                throw new ArgumentException($"width/height must be within {n}..{maxSize} (width is split evenly among {n} view(s)).");
            if (s.Width % n != 0)
                throw new ArgumentException($"width {s.Width} is not divisible by the {n} views; each view must get the same width.");

            if (req["cullingMask"] != null)
            {
                s.CullingMask = req["cullingMask"].ToObject<int>();
            }
            else
            {
                int mask = 1 << target.layer;
                foreach (Renderer r in target.GetComponentsInChildren<Renderer>(true)) mask |= 1 << r.gameObject.layer;
                s.CullingMask = mask;
            }

            float[] bg = req["background"]?.ToObject<float[]>();
            if (bg != null && bg.Length != 3 && bg.Length != 4) throw new ArgumentException("background must be [r, g, b] (0..1).");
            s.Background = bg == null ? new Color(DefaultBackground, DefaultBackground, DefaultBackground, 1f) : new Color(bg[0], bg[1], bg[2], 1f);
            s.Light = req["light"]?.ToObject<bool>() ?? true;
            s.LightIntensity = req["lightIntensity"]?.ToObject<float>() ?? DefaultLightIntensity;

            string front = req["front"]?.ToString() ?? "eyes";
            if (!Enum.TryParse(front, true, out FrontMode mode))
                throw new ArgumentException("front must be one of: eyes, shoulders, hips, head, root, world.");
            s.Front = mode;
            s.FollowFront = req["followFront"]?.ToObject<bool>() ?? true;
            return s;
        }

        protected static JObject Describe(ViewSettings s, ViewRig rig, TemporaryOverrides.Lease lease)
        {
            var views = new JArray();
            for (int i = 0; i < s.Yaw.Length; i++) views.Add(new JObject { ["yaw"] = s.Yaw[i], ["pitch"] = s.Pitch[i] });
            return new JObject
            {
                ["target"] = TemporaryOverrides.PathOf(s.Animator.transform),
                ["views"] = views,
                ["focus"] = s.FocusName,
                ["front"] = rig.FrontDescription,
                ["followFront"] = s.FollowFront,
                ["light"] = rig.LightLayers,
                ["cullingMask"] = s.CullingMask,
                ["width"] = s.Width,
                ["height"] = s.Height,
                ["disabled"] = lease == null ? new JArray() : new JArray(lease.Disabled.ToArray()),
                ["hidden"] = lease == null ? new JArray() : new JArray(lease.Hidden.ToArray()),
            };
        }

        /// <summary>引数の誤りは invalid_argument、それ以外は error_type で返す</summary>
        protected string Guard(Func<string> body, string errorType)
        {
            try { return body(); }
            catch (ArgumentException ex) { return CreateErrorResponse("invalid_argument", ex.Message); }
            catch (Exception ex) { return CreateErrorResponse(errorType, ex.Message); }
        }
    }

    /// <summary>Play 中に、Animator へクリップ（FBX 内のものも）を当てる。止めるまで当て続ける</summary>
    internal class PlayAnimationClipHandler : ViewHandlerBase
    {
        public PlayAnimationClipHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => Guard(() =>
            {
                JObject req = Parse(requestBody);
                if (!EditorApplication.isPlaying)
                    return CreateErrorResponse("not_playing", "play_animation_clip works only in Play Mode (an Edit Mode PlayableGraph does not write Humanoid bones). Call enter_play_mode first.");
                GameObject target = ResolveObject(req["target"]?.ToString(), "target");
                Animator animator = ResolveAnimator(target);
                AnimationClip clip = LoadClip(req["clipPath"]?.ToString(), req["clipName"]?.ToString());
                double time = req["time"]?.ToObject<double>() ?? 0.0;
                float speed = req["speed"]?.ToObject<float>() ?? 1f;
                bool advance = req["advance"]?.ToObject<bool>() ?? true;
                TemporaryOverrides.Lease lease = AcquireOverrides(req, target);
                try { ClipPlayer.Play(animator, clip, time, speed, advance, lease); }
                catch { TemporaryOverrides.Release(lease); throw; }
                return CreateSuccessResponse("clip_playing", new JObject
                {
                    ["animator"] = TemporaryOverrides.PathOf(animator.transform),
                    ["clip"] = clip.name,
                    ["clipLength"] = clip.length,
                    ["clipFrameRate"] = clip.frameRate,
                    ["humanMotion"] = clip.humanMotion,
                    ["time"] = time,
                    ["speed"] = speed,
                    ["advance"] = advance,
                    ["disabled"] = lease == null ? new JArray() : new JArray(lease.Disabled.ToArray()),
                    ["hidden"] = lease == null ? new JArray() : new JArray(lease.Hidden.ToArray()),
                });
            }, "play_clip_failed"));
        }
    }

    /// <summary>当てたクリップを外し、無効化・非表示にしたものを戻す。target を省くとすべて</summary>
    internal class StopAnimationClipHandler : ViewHandlerBase
    {
        public StopAnimationClipHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => Guard(() =>
            {
                JObject req = Parse(requestBody);
                string path = req["target"]?.ToString();
                if (string.IsNullOrEmpty(path))
                {
                    int n = ClipPlayer.StopAllUserClips();
                    return CreateSuccessResponse("clip_stopped", new JObject { ["stopped"] = n, ["note"] = "a clip driven by a running view recording is kept" });
                }
                Animator animator = ResolveAnimator(ResolveObject(path, "target"));
                bool stopped = ClipPlayer.Stop(animator);
                return CreateSuccessResponse("clip_stopped", new JObject
                {
                    ["animator"] = TemporaryOverrides.PathOf(animator.transform),
                    ["stopped"] = stopped,
                });
            }, "stop_clip_failed"));
        }
    }

    /// <summary>
    /// 今の姿を、複数の角度から 1 枚の PNG（角度を横に並べる）に撮る。
    /// Play 中は次のフレームのスキニングの後で撮る（LateUpdate の中で撮ると手と持ち物が 1 フレームずれる）。
    /// </summary>
    internal class CaptureViewsHandler : ViewHandlerBase
    {
        // Play 中に次のフレームを待つ上限。Editor が背面だと Play の更新が間引かれ、1 フレームに数秒かかることがあるため長めに取る
        private const int NextFrameTimeoutMs = 30000;

        public CaptureViewsHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            ViewCapture.Request pending = null;
            string outputPath = null;
            JObject info = null;
            int width = 0, height = 0;
            string started = ExecuteOnMainThread(() => Guard(() =>
            {
                JObject req = Parse(requestBody);
                outputPath = req["outputPath"]?.ToString();
                if (string.IsNullOrEmpty(outputPath)) throw new ArgumentException("outputPath (.png) is required.");
                GameObject target = ResolveObject(req["target"]?.ToString(), "target");
                Animator animator = ResolveAnimator(target);
                ViewSettings s = ParseView(req, target, animator);
                ViewRig rig = ViewRig.Create(s, out string error);
                if (rig == null) throw new ArgumentException(error);
                TemporaryOverrides.Lease lease;
                try { lease = AcquireOverrides(req, target); }
                catch { rig.Dispose(); throw; }
                info = Describe(s, rig, lease);
                info["capturedAfterNextFrame"] = ViewCapture.NeedsNextFrame;
                width = s.Width;
                height = s.Height;
                pending = ViewCapture.Begin(rig, lease);
                // 空文字 = 続行（ExecuteOnMainThread は null を「不明なエラー」に変えるので null は返さない）
                return string.Empty;
            }, "capture_failed"), MainThreadTimeoutMs);
            if (started.Length > 0) return started;

            if (!pending.Done.Wait(NextFrameTimeoutMs))
            {
                ExecuteOnMainThread(() => { ViewCapture.Fail("timed out"); return string.Empty; });
                return CreateErrorResponse("timeout", $"No Play Mode frame within {NextFrameTimeoutMs} ms (paused externally or Editor throttled).");
            }
            if (pending.Error != null) return CreateErrorResponse("capture_failed", pending.Error);

            // PNG 化と書き込みはメインスレッドの外（この HTTP のスレッド）で行う
            try
            {
                byte[] png = ImageConversion.EncodeArrayToPNG(pending.Pixels, GraphicsFormat.R8G8B8_SRGB, (uint)width, (uint)height);
                string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(outputPath, png);
                info["outputPath"] = Path.GetFullPath(outputPath);
                info["bytes"] = png.Length;
                return CreateSuccessResponse("views_captured", info);
            }
            catch (Exception ex)
            {
                return CreateErrorResponse("write_failed", ex.Message);
            }
        }
    }

    /// <summary>一定 FPS の連番 PNG を書き始めて、すぐ返す。wait_for_view_recording_done で終わりを待つ</summary>
    internal class StartViewRecordingHandler : ViewHandlerBase
    {
        public StartViewRecordingHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => Guard(() =>
            {
                JObject req = Parse(requestBody);
                if (!EditorApplication.isPlaying)
                    return CreateErrorResponse("not_playing", "start_view_recording works only in Play Mode. Call enter_play_mode first.");
                if (ViewRecorder.IsBusy)
                    return CreateErrorResponse("busy", "A recording is already running. Call stop_view_recording or wait_for_view_recording_done first.");

                string outputDir = req["outputDir"]?.ToString();
                if (string.IsNullOrEmpty(outputDir)) throw new ArgumentException("outputDir is required.");
                outputDir = Path.GetFullPath(outputDir);
                double fpsValue = req["fps"]?.ToObject<double>() ?? 30.0;
                // Time.captureFramerate は整数しか取れない（29.97 を黙って 30 に丸めない）
                if (fpsValue <= 0 || fpsValue != Math.Floor(fpsValue))
                    throw new ArgumentException("fps must be a positive integer (Time.captureFramerate is an int).");
                int fps = (int)fpsValue;

                GameObject target = ResolveObject(req["target"]?.ToString(), "target");
                Animator animator = ResolveAnimator(target);

                var plan = new RecordingPlan { OutputDir = outputDir, Fps = fps, TargetPath = TemporaryOverrides.PathOf(animator.transform) };
                string clipPath = req["clipPath"]?.ToString();
                double seconds;
                if (!string.IsNullOrEmpty(clipPath))
                {
                    plan.Clip = LoadClip(clipPath, req["clipName"]?.ToString());
                    plan.ClipLabel = clipPath + " : " + plan.Clip.name;
                    plan.StartTime = req["startTime"]?.ToObject<double>() ?? 0.0;
                    plan.EndTime = req["endTime"]?.ToObject<double>() ?? plan.Clip.length;
                    plan.Speed = req["speed"]?.ToObject<float>() ?? 1f;
                    if (plan.Speed <= 0f) throw new ArgumentException("speed must be > 0.");
                    if (plan.EndTime <= plan.StartTime) throw new ArgumentException("endTime must be greater than startTime.");
                    if (plan.StartTime < 0 || plan.EndTime > plan.Clip.length)
                        throw new ArgumentException($"startTime/endTime must be within the clip (0..{plan.Clip.length:F3} s).");
                    seconds = (plan.EndTime - plan.StartTime) / plan.Speed;
                }
                else
                {
                    seconds = req["duration"]?.ToObject<double>() ?? 0.0;
                    if (seconds <= 0) throw new ArgumentException("Without clipPath, pass duration (seconds) to record the current motion.");
                    plan.ClipLabel = ClipPlayer.Find(animator) is ClipDriver d ? "(current) " + d.Clip.name : "(current motion, no clip)";
                }
                // 区間 [start, end) を 1/fps 刻みで撮る。浮動小数の誤差で 1 コマ増えないよう 1e-6 を引いてから切り上げる
                plan.TotalFrames = (int)Math.Ceiling(seconds * fps - 1e-6);
                double preroll = req["preroll"]?.ToObject<double>() ?? 0.0;
                if (preroll < 0) throw new ArgumentException("preroll must be >= 0.");
                plan.PrerollFrames = (int)Math.Round(preroll * fps);
                if (plan.Clip != null && plan.StartTime - plan.PrerollFrames * (double)plan.Speed / fps < 0)
                    throw new ArgumentException("preroll reaches before the clip start; lower preroll or raise startTime.");

                bool overwrite = req["overwrite"]?.ToObject<bool>() ?? false;
                // 古いコマを消すのは引数の検証が全部済んでから（誤った引数で消えて始まらない、を避ける）
                CheckOutputDir(outputDir, overwrite);
                plan.View = ParseView(req, target, animator);
                plan.Rig = ViewRig.Create(plan.View, out string error);
                if (plan.Rig == null) throw new ArgumentException(error);
                try
                {
                    PrepareOutputDir(outputDir, overwrite);
                    plan.Lease = AcquireOverrides(req, target);
                    ViewRecorder.Start(plan);
                }
                catch
                {
                    TemporaryOverrides.Release(plan.Lease);
                    plan.Rig.Dispose();
                    throw;
                }
                return CreateSuccessResponse("view_recording_started", ViewRecorder.Snapshot());
            }, "start_recording_failed"), MainThreadTimeoutMs);
        }

        private static void CheckOutputDir(string dir, bool overwrite)
        {
            if (overwrite || !Directory.Exists(dir)) return;
            int old = Directory.GetFiles(dir, "frame_*.png").Length;
            if (old > 0)
                throw new ArgumentException($"{dir} already has {old} frame_*.png. Pass overwrite=true to delete them, or use another outputDir.");
        }

        /// <summary>前回の連番が残っていると mp4 に混ざるので、overwrite でなければ拒む。消すのはこのツールが書く名前だけ</summary>
        private static void PrepareOutputDir(string dir, bool overwrite)
        {
            Directory.CreateDirectory(dir);
            if (!overwrite) return;
            foreach (string f in Directory.GetFiles(dir, "frame_*.png")) File.Delete(f);
            string json = Path.Combine(dir, "recording.json");
            if (File.Exists(json)) File.Delete(json);
        }
    }

    internal class GetViewRecordingStatusHandler : ViewHandlerBase
    {
        public GetViewRecordingStatusHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody) =>
            CreateSuccessResponse("view_recording_status", ViewRecorder.Snapshot());
    }

    /// <summary>録画の終わり（全コマの書き込みまで）を待つ。get_view_recording_status を繰り返し呼ばずにこちらを使う</summary>
    internal class WaitForViewRecordingDoneHandler : ViewHandlerBase
    {
        // MCP の Node 側（fetch）は応答の頭を 300 秒で打ち切るので、その手前の 240 秒を既定の上限とする
        private const int DefaultTimeoutMs = 240000;
        // 状態を見る刻み。書き込みの終わりを数百 ms の遅れで拾えれば足りる
        private const int PollMs = 500;

        public WaitForViewRecordingDoneHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            int timeoutMs = DefaultTimeoutMs;
            try { timeoutMs = Parse(requestBody)["timeoutMs"]?.ToObject<int?>() ?? timeoutMs; }
            catch (Newtonsoft.Json.JsonReaderException) { /* 既定値 */ }

            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (ViewRecorder.IsBusy && DateTime.Now < deadline) Thread.Sleep(PollMs);
            JObject snap = ViewRecorder.Snapshot();
            snap["timedOut"] = ViewRecorder.IsBusy;
            return CreateSuccessResponse("view_recording_done", snap);
        }
    }

    internal class StopViewRecordingHandler : ViewHandlerBase
    {
        public StopViewRecordingHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() =>
            {
                bool stopped = ViewRecorder.Stop();
                JObject snap = ViewRecorder.Snapshot();
                snap["stopRequested"] = stopped;
                return CreateSuccessResponse("view_recording_stopped", snap);
            });
        }
    }
}
