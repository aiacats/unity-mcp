using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ClaudeCodeMCP.Editor.Core.ViewRecording
{
    /// <summary>1 つの Animator に 1 本のクリップを当てる。評価は ViewRecordingLoop のポーズの時点で手動で行う</summary>
    internal sealed class ClipDriver
    {
        public readonly Animator Animator;
        public readonly AnimationClip Clip;
        public double Time;
        public float Speed;
        /// <summary>true の間は毎フレーム deltaTime × Speed ぶん進める（false なら Time で止める）</summary>
        public bool Advancing;
        /// <summary>録画が当てているクリップ。時刻は録画が決め、play / stop_animation_clip からは触らせない</summary>
        public bool OwnedByRecording;
        public TemporaryOverrides.Lease Lease;

        private PlayableGraph _graph;
        private AnimationClipPlayable _playable;
        private bool _disposed;

        public ClipDriver(Animator animator, AnimationClip clip)
        {
            Animator = animator;
            Clip = clip;
            _graph = PlayableGraph.Create("ClaudeCodeMCP.ClipDriver");
            // 進め方はこちらで決める（GameTime にすると Unity の評価と二重になり、時刻も実時間に引きずられる）
            _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            _playable = AnimationClipPlayable.Create(_graph, clip);
            // 収録モーションの足をそのまま見せる（足 IK で接地を補正しない）
            _playable.SetApplyFootIK(false);
            var output = AnimationPlayableOutput.Create(_graph, "ClaudeCodeMCP.ClipDriver", animator);
            output.SetSourcePlayable(_playable);
            _graph.Play();
        }

        public bool IsValid => !_disposed && _graph.IsValid() && Animator != null;

        public void Evaluate(float deltaTime)
        {
            if (!IsValid) return;
            if (Advancing && !OwnedByRecording) Time += deltaTime * Speed;
            _playable.SetTime(Time);
            _graph.Evaluate(0f);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_graph.IsValid()) _graph.Destroy();
            TemporaryOverrides.Release(Lease);
            Lease = null;
        }
    }

    /// <summary>
    /// Play 中に Humanoid などのクリップを Animator へ当てる。Edit モードの PlayableGraph は Humanoid の骨に書かないので Play 中だけ。
    /// Animator 自身の評価（DirectorUpdateAnimationEnd）の後に上書きし、LateUpdate の補正スクリプトはその上に掛かる。
    /// 録画がクリップを当てる間、同じ Animator に play_animation_clip で当てていたものは退避し（評価しない・lease は持ったまま）、録画の後に戻す。
    /// </summary>
    internal static class ClipPlayer
    {
        private static readonly List<ClipDriver> Drivers = new List<ClipDriver>();
        private static readonly List<ClipDriver> Suspended = new List<ClipDriver>();

        /// <summary>play_animation_clip 用。録画が当てている Animator には当てない（録画の絵が黙って変わるため）</summary>
        internal static ClipDriver Play(Animator animator, AnimationClip clip, double time, float speed, bool advancing, TemporaryOverrides.Lease lease)
        {
            ClipDriver current = Find(animator);
            if (current != null && current.OwnedByRecording)
                throw new InvalidOperationException("A view recording is driving this Animator. Stop the recording first.");
            Stop(animator);
            var driver = new ClipDriver(animator, clip) { Time = time, Speed = speed, Advancing = advancing, Lease = lease };
            Drivers.Add(driver);
            ViewRecordingLoop.EnsureInstalled();
            return driver;
        }

        /// <summary>録画用。同じ Animator の play_animation_clip のドライバは退避して、EndRecording で戻す</summary>
        internal static ClipDriver BeginRecording(Animator animator, AnimationClip clip, double time, float speed)
        {
            for (int i = Drivers.Count - 1; i >= 0; i--)
            {
                if (Drivers[i].Animator != animator) continue;
                Suspended.Add(Drivers[i]);
                Drivers.RemoveAt(i);
            }
            var driver = new ClipDriver(animator, clip) { Time = time, Speed = speed, OwnedByRecording = true };
            Drivers.Add(driver);
            ViewRecordingLoop.EnsureInstalled();
            return driver;
        }

        internal static void EndRecording(ClipDriver driver)
        {
            if (driver == null) return;
            driver.Dispose();
            Drivers.Remove(driver);
            for (int i = Suspended.Count - 1; i >= 0; i--)
            {
                if (Suspended[i].Animator != driver.Animator) continue;
                if (Suspended[i].IsValid) Drivers.Add(Suspended[i]);
                else Suspended[i].Dispose();
                Suspended.RemoveAt(i);
            }
        }

        internal static ClipDriver Find(Animator animator)
        {
            for (int i = 0; i < Drivers.Count; i++)
                if (Drivers[i].Animator == animator) return Drivers[i];
            return null;
        }

        /// <summary>stop_animation_clip 用。録画が当てているものは外さない（false を返す）</summary>
        internal static bool Stop(Animator animator)
        {
            bool stopped = false;
            for (int i = Drivers.Count - 1; i >= 0; i--)
            {
                if (Drivers[i].Animator != animator || Drivers[i].OwnedByRecording) continue;
                Drivers[i].Dispose();
                Drivers.RemoveAt(i);
                stopped = true;
            }
            for (int i = Suspended.Count - 1; i >= 0; i--)
            {
                if (Suspended[i].Animator != animator) continue;
                Suspended[i].Dispose();
                Suspended.RemoveAt(i);
                stopped = true;
            }
            return stopped;
        }

        /// <summary>stop_animation_clip（target 省略）用。録画が当てているものは残す。外した数を返す</summary>
        internal static int StopAllUserClips()
        {
            int n = 0;
            for (int i = Drivers.Count - 1; i >= 0; i--)
            {
                if (Drivers[i].OwnedByRecording) continue;
                Drivers[i].Dispose();
                Drivers.RemoveAt(i);
                n++;
            }
            for (int i = 0; i < Suspended.Count; i++) Suspended[i].Dispose();
            n += Suspended.Count;
            Suspended.Clear();
            return n;
        }

        /// <summary>Play 終了・リロードの前の後始末（録画のものも含めてすべて）</summary>
        internal static void DisposeAll()
        {
            for (int i = 0; i < Drivers.Count; i++) Drivers[i].Dispose();
            for (int i = 0; i < Suspended.Count; i++) Suspended[i].Dispose();
            Drivers.Clear();
            Suspended.Clear();
        }

        /// <summary>毎フレーム呼ばれる。GC を出さないよう for で回す</summary>
        internal static void EvaluateAll(float deltaTime)
        {
            for (int i = Drivers.Count - 1; i >= 0; i--)
            {
                ClipDriver d = Drivers[i];
                if (!d.IsValid)
                {
                    d.Dispose();
                    Drivers.RemoveAt(i);
                    continue;
                }
                d.Evaluate(deltaTime);
            }
        }
    }
}
