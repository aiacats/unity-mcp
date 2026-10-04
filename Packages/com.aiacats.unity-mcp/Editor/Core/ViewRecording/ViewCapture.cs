using System.Threading;
using UnityEditor;

namespace ClaudeCodeMCP.Editor.Core.ViewRecording
{
    /// <summary>
    /// 静止画 1 枚（capture_views）。Play 中（一時停止でない）は次のフレームのスキニングの後で撮り、
    /// Edit モードと一時停止中はその場で撮る（フレームが進まないので、今の形がそのまま最新）。
    /// 読み出した画素を呼び出し側（HTTP のスレッド）へ渡し、PNG 化と書き込みはそちらで行う。
    /// </summary>
    internal static class ViewCapture
    {
        internal sealed class Request
        {
            public ViewRig Rig;
            public TemporaryOverrides.Lease Lease;
            public byte[] Pixels;
            public string Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static Request _pending;

        internal static bool NeedsNextFrame => EditorApplication.isPlaying && !EditorApplication.isPaused;

        /// <summary>メインスレッドで呼ぶ。Play 中なら予約して返し、それ以外はその場で撮って Done にする</summary>
        internal static Request Begin(ViewRig rig, TemporaryOverrides.Lease lease)
        {
            var req = new Request { Rig = rig, Lease = lease };
            if (NeedsNextFrame)
            {
                if (_pending != null)
                {
                    Finish(req, "another capture_views is waiting for the next frame");
                    return req;
                }
                ViewRecordingLoop.EnsureInstalled();
                _pending = req;
                return req;
            }
            Shoot(req);
            return req;
        }

        internal static void AfterSkinning()
        {
            Request req = _pending;
            if (req == null) return;
            _pending = null;
            Shoot(req);
        }

        internal static void Fail(string reason)
        {
            Request req = _pending;
            if (req == null) return;
            _pending = null;
            Finish(req, reason);
        }

        private static void Shoot(Request req)
        {
            try
            {
                req.Rig.Render();
                req.Pixels = new byte[req.Rig.BufferSize];
                req.Rig.ReadInto(req.Pixels);
                Finish(req, null);
            }
            catch (System.Exception ex)
            {
                Finish(req, ex.Message);
            }
        }

        private static void Finish(Request req, string error)
        {
            req.Error = error;
            req.Rig?.Dispose();
            TemporaryOverrides.Release(req.Lease);
            req.Done.Set();
        }
    }
}
