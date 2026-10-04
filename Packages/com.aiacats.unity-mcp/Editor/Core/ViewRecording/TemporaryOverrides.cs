using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClaudeCodeMCP.Editor.Core.ViewRecording
{
    /// <summary>
    /// 撮影の間だけ、部品を無効化・オブジェクトを非表示にし、終わったら元に戻す。
    /// ライブ受信（例: モーションキャプチャの受信部品）が Animator を上書きする・同じ位置に別のキャラが立つ、のを避けるためのもの。
    /// クリップ再生と録画が同じ対象を別々に借りても正しく戻るよう、対象ごとに参照数を数え、最初に借りた時点の状態へ戻す。
    /// </summary>
    internal static class TemporaryOverrides
    {
        private sealed class Entry
        {
            public UnityEngine.Object Target;
            public bool Original;
            public int Refs;
        }

        internal sealed class Lease
        {
            internal readonly List<int> BehaviourIds = new List<int>();
            internal readonly List<int> ObjectIds = new List<int>();
            internal readonly List<int> SkinIds = new List<int>();
            public readonly List<string> Disabled = new List<string>();
            public readonly List<string> Hidden = new List<string>();
            internal bool Released;
        }

        private static readonly Dictionary<int, Entry> Behaviours = new Dictionary<int, Entry>();
        private static readonly Dictionary<int, Entry> Objects = new Dictionary<int, Entry>();
        private static readonly Dictionary<int, Entry> Skins = new Dictionary<int, Entry>();

        /// <summary>
        /// 撮影の間だけ SkinnedMeshRenderer.updateWhenOffscreen を true にする（一時カメラにしか写らないメッシュもスキンを更新させる）。
        /// 静止画と録画が前後をまたいでも元の値へ戻るよう、ほかと同じく参照数で数える。
        /// </summary>
        internal static Lease AcquireSkins(SkinnedMeshRenderer[] skins)
        {
            var lease = new Lease();
            foreach (SkinnedMeshRenderer s in skins)
            {
                if (s == null) continue;
                Take(Skins, s, s.updateWhenOffscreen);
                s.updateWhenOffscreen = true;
                lease.SkinIds.Add(s.GetInstanceID());
            }
            return lease;
        }

        /// <summary>
        /// scope 以下（scope 自身を含む・非アクティブも含む）で、型名（Name か FullName の完全一致）が合う Behaviour を無効にし、
        /// hide のオブジェクトを非アクティブにする。型名が 1 つも当たらなければ何も変えずに例外（指定の誤りを黙って通さない）。
        /// </summary>
        internal static Lease Acquire(GameObject scope, string[] typeNames, GameObject[] hide)
        {
            var lease = new Lease();
            var matched = new List<Behaviour>();
            if (typeNames != null && typeNames.Length > 0)
            {
                Behaviour[] all = scope.GetComponentsInChildren<Behaviour>(true);
                foreach (string name in typeNames)
                {
                    int before = matched.Count;
                    foreach (Behaviour b in all)
                    {
                        if (b == null) continue;
                        Type t = b.GetType();
                        if (t.Name == name || t.FullName == name) matched.Add(b);
                    }
                    if (matched.Count == before)
                        throw new ArgumentException($"disableComponents: no component of type '{name}' under '{scope.name}'.");
                }
            }

            foreach (Behaviour b in matched)
            {
                Take(Behaviours, b, b.enabled);
                b.enabled = false;
                lease.BehaviourIds.Add(b.GetInstanceID());
                lease.Disabled.Add(PathOf(b.transform) + " [" + b.GetType().Name + "]");
            }
            if (hide != null)
            {
                foreach (GameObject go in hide)
                {
                    if (go == null) continue;
                    Take(Objects, go, go.activeSelf);
                    go.SetActive(false);
                    lease.ObjectIds.Add(go.GetInstanceID());
                    lease.Hidden.Add(PathOf(go.transform));
                }
            }
            return lease;
        }

        internal static void Release(Lease lease)
        {
            if (lease == null || lease.Released) return;
            lease.Released = true;
            foreach (int id in lease.BehaviourIds) Give(Behaviours, id, (o, v) => ((Behaviour)o).enabled = v);
            foreach (int id in lease.ObjectIds) Give(Objects, id, (o, v) => ((GameObject)o).SetActive(v));
            foreach (int id in lease.SkinIds) Give(Skins, id, (o, v) => ((SkinnedMeshRenderer)o).updateWhenOffscreen = v);
        }

        /// <summary>Play 終了・リロードの前に、借りているものをすべて元へ戻す</summary>
        internal static void ReleaseAll()
        {
            foreach (Entry e in Behaviours.Values)
                if (e.Target != null) ((Behaviour)e.Target).enabled = e.Original;
            foreach (Entry e in Objects.Values)
                if (e.Target != null) ((GameObject)e.Target).SetActive(e.Original);
            foreach (Entry e in Skins.Values)
                if (e.Target != null) ((SkinnedMeshRenderer)e.Target).updateWhenOffscreen = e.Original;
            Behaviours.Clear();
            Objects.Clear();
            Skins.Clear();
        }

        private static void Take(Dictionary<int, Entry> map, UnityEngine.Object target, bool current)
        {
            int id = target.GetInstanceID();
            if (map.TryGetValue(id, out Entry e)) { e.Refs++; return; }
            map[id] = new Entry { Target = target, Original = current, Refs = 1 };
        }

        private static void Give(Dictionary<int, Entry> map, int id, Action<UnityEngine.Object, bool> restore)
        {
            if (!map.TryGetValue(id, out Entry e)) return;
            if (--e.Refs > 0) return;
            map.Remove(id);
            if (e.Target != null) restore(e.Target, e.Original);
        }

        internal static string PathOf(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }
    }
}
