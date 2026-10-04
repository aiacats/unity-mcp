using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Debug = UnityEngine.Debug;

namespace ClaudeCodeMCP.Editor.Core.ViewRecording
{
    /// <summary>録画の指定（ハンドラが組み立てて Start へ渡す）</summary>
    internal sealed class RecordingPlan
    {
        public ViewSettings View;
        /// <summary>対象のヒエラルキーパス（書き込みスレッドから Unity の API を触らないよう、文字列で持つ）</summary>
        public string TargetPath;
        public ViewRig Rig;
        public TemporaryOverrides.Lease Lease;
        public string OutputDir;
        public int Fps;
        public int TotalFrames;
        public int PrerollFrames;
        /// <summary>null ならクリップを当てず、今の動き（ライブ受信・play_animation_clip）をそのまま撮る</summary>
        public AnimationClip Clip;
        public string ClipLabel;
        public double StartTime;
        public double EndTime;
        public float Speed;
    }

    /// <summary>
    /// 一定 FPS の連番 PNG を書く（start_view_recording）。
    /// Time.captureFramerate で 1 フレーム = 1/fps 秒に固定し（揺れものも同じ刻みで計算される）、
    /// クリップの時刻はフレーム番号から決める（実時間の遅れに左右されない）。
    /// 画素の読み出しまではメインスレッド、PNG 化と書き込みは専用のスレッド。書き込みが追いつかないときはメインスレッドが待つ（オフラインなので落とさない）。
    /// </summary>
    internal static class ViewRecorder
    {
        internal enum State { Idle, Recording, Finishing, Completed, Stopped, Failed }

        // 書き込み待ちに置ける画素バッファの数。描画と PNG 化を重ねるのに足りる数として 4
        // （1440x720 で 1 枚 3.1MB、計 12MB。PNG 化が 1 フレームの描画より遅い間はここで待ちが入る）
        private const int BufferCount = 4;
        // 書き込みスレッドの空きを待つ刻み。失敗を見て抜けるための間隔で、待ちの長さそのものではない
        private const int FreeWaitSliceMs = 100;
        // 書き込みスレッドが 1 枚も返さないまま待つ上限。PNG 1 枚の書き込みは数十 ms（実測 1440x720 で 27 ms/コマ）なので、
        // 30 秒返らなければディスクやウイルス対策で詰まっていると見なして止める（メインスレッドを永久に止めない）
        private const int FreeWaitLimitMs = 30000;

        private static readonly object Lock = new object();
        private static State _state = State.Idle;
        private static RecordingPlan _plan;
        private static ClipDriver _driver;
        private static int _frame;          // 予備フレームを含む通し番号（メインスレッドだけが触る）
        private static int _captured;
        private static int _written;
        private static int _prevCaptureFramerate;
        private static string _reason;
        private static string _error;
        private static DateTime _startedAt, _finishedAt;
        private static readonly Stopwatch Watch = new Stopwatch();
        private static string _filePattern;
        private static int _digits;

        private static BlockingCollection<Job> _jobs;
        private static ConcurrentQueue<byte[]> _pool;
        private static SemaphoreSlim _free;
        private static Thread _encoder;

        private struct Job
        {
            public int Index;
            public byte[] Pixels;
        }

        internal static bool IsBusy { get { lock (Lock) return _state == State.Recording || _state == State.Finishing; } }

        /// <summary>メインスレッドで呼ぶ（Play 中）。失敗したら例外。plan の Rig と Lease はここで引き取る</summary>
        internal static void Start(RecordingPlan plan)
        {
            lock (Lock)
            {
                if (_state == State.Recording || _state == State.Finishing)
                    throw new InvalidOperationException("A recording is already running. Call stop_view_recording first.");
            }

            _plan = plan;
            _driver = null;
            _prevCaptureFramerate = Time.captureFramerate;
            try
            {
                Begin(plan);
            }
            catch
            {
                // 始めかけたものを戻す（Rig と Lease の後始末は呼び出し側）
                Time.captureFramerate = _prevCaptureFramerate;
                if (_driver != null) { ClipPlayer.EndRecording(_driver); _driver = null; }
                lock (Lock) _state = State.Idle;
                _plan = null;
                throw;
            }
        }

        private static void Begin(RecordingPlan plan)
        {
            if (plan.Clip != null)
                _driver = ClipPlayer.BeginRecording(plan.View.Animator, plan.Clip, plan.StartTime, plan.Speed);

            _digits = Math.Max(5, plan.TotalFrames.ToString().Length);
            _filePattern = "frame_%0" + _digits + "d.png";
            int size = plan.Rig.BufferSize;
            _pool = new ConcurrentQueue<byte[]>();
            for (int i = 0; i < BufferCount; i++) _pool.Enqueue(new byte[size]);
            _free = new SemaphoreSlim(BufferCount, BufferCount);
            _jobs = new BlockingCollection<Job>();

            _frame = 0;
            _captured = 0;
            _written = 0;
            _reason = null;
            _error = null;
            _startedAt = DateTime.Now;
            _finishedAt = default;
            Watch.Restart();

            ViewRecordingLoop.EnsureInstalled();
            _encoder = new Thread(EncodeLoop) { Name = "ClaudeCodeMCP-ViewRecorder", IsBackground = true };
            Time.captureFramerate = plan.Fps;
            lock (Lock) _state = State.Recording;
            _encoder.Start();
        }

        /// <summary>ポーズの時点（毎フレーム）。録画が当てているクリップの時刻をフレーム番号から決める</summary>
        internal static void BeforePose()
        {
            if (_driver == null || _state != State.Recording) return;
            if (!_driver.IsValid)
            {
                // クリップが外れたまま撮り続けると、別のポーズの絵で completed になる
                Fail("the recording's clip driver was removed (Animator destroyed?)");
                return;
            }
            RecordingPlan p = _plan;
            _driver.Time = p.StartTime + (double)(_frame - p.PrerollFrames) * p.Speed / p.Fps;
        }

        /// <summary>スキニングの後（毎フレーム）。GC を出さない（文字列・LINQ を使わない）</summary>
        internal static void AfterSkinning()
        {
            if (_state != State.Recording) return;
            if (Volatile.Read(ref _error) != null)
            {
                Finish("write failed");
                return;
            }
            RecordingPlan p = _plan;
            if (_frame < p.PrerollFrames)
            {
                _frame++;
                return;
            }

            p.Rig.Render();
            int waited = 0;
            while (!_free.Wait(FreeWaitSliceMs))
            {
                waited += FreeWaitSliceMs;
                if (Volatile.Read(ref _error) != null)
                {
                    Finish("write failed");
                    return;
                }
                if (waited >= FreeWaitLimitMs)
                {
                    Fail("the PNG writer has not finished a frame for 30 s (disk blocked?)");
                    return;
                }
            }
            _pool.TryDequeue(out byte[] pixels);
            p.Rig.ReadInto(pixels);
            _jobs.Add(new Job { Index = _captured, Pixels = pixels });
            _frame++;
            _captured++;
            if (_captured >= p.TotalFrames) Finish("completed");
        }

        /// <summary>途中で止める（stop_view_recording）。書き込み待ちのコマは書き切る</summary>
        internal static bool Stop()
        {
            if (_state != State.Recording) return false;
            Finish("stopped by request");
            return true;
        }

        internal static void Abort(string reason)
        {
            if (_state == State.Recording) Finish("aborted: " + reason);
        }

        internal static void Fail(string message)
        {
            if (_state != State.Recording) return;
            Interlocked.CompareExchange(ref _error, message, null);
            Finish("failed");
        }

        private static void Finish(string reason)
        {
            lock (Lock)
            {
                _reason = reason;
                _state = State.Finishing;
            }
            // どれかの後始末が投げても、残りを戻し、書き込みスレッドは必ず終わらせる（busy のまま残さない）
            try
            {
                Watch.Stop();
                Time.captureFramerate = _prevCaptureFramerate;
                try { if (_driver != null) ClipPlayer.EndRecording(_driver); }
                catch (Exception ex) { Debug.LogException(ex); }
                _driver = null;
                try { TemporaryOverrides.Release(_plan.Lease); }
                catch (Exception ex) { Debug.LogException(ex); }
                try { _plan.Rig.Dispose(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
            finally
            {
                _jobs.CompleteAdding();
            }
        }

        /// <summary>ドメインリロードの前に、書き込み待ちのコマと recording.json を書き切るまで待つ</summary>
        internal static void WaitForWriter(int timeoutMs)
        {
            Thread t = _encoder;
            if (t != null && t.IsAlive) t.Join(timeoutMs);
        }

        private static void EncodeLoop()
        {
            RecordingPlan p = _plan;
            string dir = p.OutputDir;
            string format = "D" + _digits;
            foreach (Job job in _jobs.GetConsumingEnumerable())
            {
                try
                {
                    if (Volatile.Read(ref _error) == null)
                    {
                        byte[] png = ImageConversion.EncodeArrayToPNG(job.Pixels, GraphicsFormat.R8G8B8_SRGB, (uint)p.Rig.Width, (uint)p.Rig.Height);
                        File.WriteAllBytes(Path.Combine(dir, "frame_" + job.Index.ToString(format) + ".png"), png);
                        Interlocked.Increment(ref _written);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref _error, ex.Message, null);
                }
                finally
                {
                    _pool.Enqueue(job.Pixels);
                    _free.Release();
                }
            }

            // recording.json には確定した状態を書く。IsBusy は書き終えてから外す（wait の直後に json が無い、を避ける）
            DateTime finishedAt = DateTime.Now;
            try
            {
                File.WriteAllText(Path.Combine(dir, "recording.json"), Snapshot(FinalState(), finishedAt).ToString(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _error, "recording.json: " + ex.Message, null);
            }

            // 失敗は状態（error）で返す。このスレッドから Debug.Log は呼ばない（ログのロックを奪い合うことがある）
            lock (Lock)
            {
                _finishedAt = finishedAt;
                _state = FinalState();
            }
        }

        private static State FinalState()
        {
            if (Volatile.Read(ref _error) != null) return State.Failed;
            return _reason == "completed" ? State.Completed : State.Stopped;
        }

        internal static JObject Snapshot() => Snapshot(null, null);

        private static JObject Snapshot(State? state, DateTime? finishedAt)
        {
            lock (Lock)
            {
                var o = new JObject { ["state"] = (state ?? _state).ToString().ToLowerInvariant() };
                RecordingPlan p = _plan;
                if (p == null) return o;
                double elapsed = Watch.Elapsed.TotalSeconds;
                o["reason"] = _reason;
                o["error"] = _error;
                o["framesCaptured"] = _captured;
                o["framesWritten"] = Volatile.Read(ref _written);
                o["totalFrames"] = p.TotalFrames;
                o["prerollFrames"] = p.PrerollFrames;
                o["outputDir"] = p.OutputDir;
                o["filePattern"] = _filePattern;
                o["fps"] = p.Fps;
                o["width"] = p.Rig.Width;
                o["height"] = p.Rig.Height;
                var views = new JArray();
                for (int i = 0; i < p.View.Yaw.Length; i++) views.Add(new JObject { ["yaw"] = p.View.Yaw[i], ["pitch"] = p.View.Pitch[i] });
                o["views"] = views;
                o["target"] = p.TargetPath;
                o["focus"] = p.View.FocusName;
                o["front"] = p.Rig.FrontDescription;
                o["light"] = p.Rig.LightLayers;
                o["cullingMask"] = p.View.CullingMask;
                o["clip"] = p.ClipLabel;
                if (p.Clip != null)
                {
                    o["startTime"] = p.StartTime;
                    o["endTime"] = p.EndTime;
                    o["speed"] = p.Speed;
                }
                o["disabled"] = p.Lease == null ? new JArray() : new JArray(p.Lease.Disabled.ToArray());
                o["hidden"] = p.Lease == null ? new JArray() : new JArray(p.Lease.Hidden.ToArray());
                o["startedAt"] = _startedAt.ToString("yyyy-MM-dd HH:mm:ss.fff");
                DateTime fin = finishedAt ?? _finishedAt;
                o["finishedAt"] = fin == default ? null : fin.ToString("yyyy-MM-dd HH:mm:ss.fff");
                o["elapsedSeconds"] = Math.Round(elapsed, 2);
                o["msPerFrame"] = _captured > 0 ? Math.Round(elapsed * 1000.0 / _captured, 1) : 0.0;
                return o;
            }
        }
    }
}
