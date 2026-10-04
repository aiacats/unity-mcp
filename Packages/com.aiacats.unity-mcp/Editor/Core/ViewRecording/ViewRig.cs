using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ClaudeCodeMCP.Editor.Core.ViewRecording
{
    /// <summary>顔（体）の正面の求め方。黙って別の方法へ切り替えず、使った方法を結果に出す</summary>
    internal enum FrontMode
    {
        /// <summary>Humanoid の LeftEye → RightEye に直交する水平方向</summary>
        Eyes,
        /// <summary>LeftUpperArm → RightUpperArm に直交する水平方向（目の骨が無いキャラ向け）</summary>
        Shoulders,
        /// <summary>LeftUpperLeg → RightUpperLeg に直交する水平方向</summary>
        Hips,
        /// <summary>最初のフレームの肩の正面を Head 骨のローカルへ写し、以後は Head の回転に追従させる</summary>
        Head,
        /// <summary>対象（Animator）の Transform の forward</summary>
        Root,
        /// <summary>ワールドの +Z 固定</summary>
        World,
    }

    /// <summary>撮影の設定（capture_views / start_view_recording 共通）</summary>
    internal sealed class ViewSettings
    {
        public Animator Animator;
        public GameObject Target;
        public float[] Yaw;
        public float[] Pitch;
        public Transform Focus;
        public string FocusName;
        /// <summary>正面基準の (右, 上, 前) のずれ [m]</summary>
        public Vector3 FocusOffset;
        public float Distance;
        public float Fov;
        public int Width;
        public int Height;
        public int CullingMask;
        public Color Background;
        public bool Light;
        public float LightIntensity;
        public FrontMode Front;
        /// <summary>true なら毎フレーム正面を求め直す（顔の向きに付いて回る）。false なら最初のフレームの正面で固定</summary>
        public bool FollowFront;
    }

    /// <summary>
    /// 一時カメラ（角度ごとに 1 台、横に並べて 1 枚の RT へ描く）と撮影用ライト。
    /// 撮影の間は使い回し、片付けは遅延破棄（撮った直後に DestroyImmediate すると、Play 終了直後に D3D11 の BeginProfileEvent で落ちた）。
    /// </summary>
    internal sealed class ViewRig
    {
        // 近クリップ: 顔へ数十 cm まで寄っても切れない 1cm。遠クリップ: 対象のレイヤーしか描かないので、
        // ステージ規模（数十 m）を収めつつ深度の精度（遠/近 = 1e4）を保つ 100m とする
        private const float NearClip = 0.01f;
        private const float FarClip = 100f;
        // 撮影用ライトの下向きの傾き（前方 1 に対して 0.3 ≒ 17°）。2026-10-04 SinkaiFes2026 の検証で顔に影が落ちすぎない向きとして使った値
        private const float LightDownTilt = 0.3f;

        private readonly ViewSettings _s;
        private GameObject _holder;
        private Camera[] _cams = new Camera[0];
        private Light _light;
        private RenderTexture _rt;
        private Texture2D _tex;
        private readonly Transform _a, _b, _head;
        private Vector3 _headLocalFront;
        private Vector3 _lockedFront;
        private bool _hasFront;
        private TemporaryOverrides.Lease _skinLease;
        private bool _disposed;

        public string FrontDescription { get; }
        public string LightLayers { get; private set; }
        public int Width => _s.Width;
        public int Height => _s.Height;
        public int BufferSize => _s.Width * _s.Height * 3;

        private ViewRig(ViewSettings s, Transform a, Transform b, Transform head, string frontDescription)
        {
            _s = s;
            _a = a;
            _b = b;
            _head = head;
            FrontDescription = frontDescription;
            try
            {
                Build(s);
            }
            catch
            {
                // 途中で失敗したら、作りかけの HideAndDontSave を残さない
                Dispose();
                throw;
            }
        }

        private void Build(ViewSettings s)
        {
            _holder = new GameObject("_ClaudeCodeMCP_ViewRig") { hideFlags = HideFlags.HideAndDontSave };
            int n = s.Yaw.Length;
            _rt = new RenderTexture(s.Width, s.Height, 24) { name = "_ClaudeCodeMCP_ViewRig", hideFlags = HideFlags.HideAndDontSave };
            _rt.Create();
            _tex = new Texture2D(s.Width, s.Height, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
            _cams = new Camera[n];
            for (int i = 0; i < n; i++)
            {
                var go = new GameObject("_ViewCam" + i) { hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetParent(_holder.transform, false);
                Camera cam = go.AddComponent<Camera>();
                cam.enabled = false;
                cam.nearClipPlane = NearClip;
                cam.farClipPlane = FarClip;
                cam.fieldOfView = s.Fov;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = s.Background;
                cam.cullingMask = s.CullingMask;
                cam.targetTexture = _rt;
                cam.rect = new Rect((float)i / n, 0f, 1f / n, 1f);
                _cams[i] = cam;
            }

            if (s.Light)
            {
                var lgo = new GameObject("_ViewLight") { hideFlags = HideFlags.HideAndDontSave };
                lgo.transform.SetParent(_holder.transform, false);
                _light = lgo.AddComponent<Light>();
                _light.type = LightType.Directional;
                _light.intensity = s.LightIntensity;
                _light.renderingLayerMask = -1;
                LightLayers = SetUrpRenderingLayersAll(_light) ? "all (URP UniversalAdditionalLightData)" : "all (Light.renderingLayerMask; URP not found)";
                _light.enabled = false;
            }
            else
            {
                LightLayers = "no light";
            }

            // 一時カメラにしか写らないキャラは画面外とみなされ、スキンメッシュが更新されないことがある。撮影の間は常に更新させる
            _skinLease = TemporaryOverrides.AcquireSkins(s.Target.GetComponentsInChildren<SkinnedMeshRenderer>(true));
        }

        /// <summary>正面を求める骨が揃っていなければ null と理由を返す（別の方法へは切り替えない）</summary>
        public static ViewRig Create(ViewSettings s, out string error)
        {
            error = null;
            Animator an = s.Animator;
            Transform a = null, b = null, head = null;
            string desc;
            switch (s.Front)
            {
                case FrontMode.Eyes:
                    a = Bone(an, HumanBodyBones.LeftEye); b = Bone(an, HumanBodyBones.RightEye);
                    desc = "eyes (LeftEye/RightEye)";
                    break;
                case FrontMode.Shoulders:
                    a = Bone(an, HumanBodyBones.LeftUpperArm); b = Bone(an, HumanBodyBones.RightUpperArm);
                    desc = "shoulders (LeftUpperArm/RightUpperArm)";
                    break;
                case FrontMode.Hips:
                    a = Bone(an, HumanBodyBones.LeftUpperLeg); b = Bone(an, HumanBodyBones.RightUpperLeg);
                    desc = "hips (LeftUpperLeg/RightUpperLeg)";
                    break;
                case FrontMode.Head:
                    a = Bone(an, HumanBodyBones.LeftUpperArm); b = Bone(an, HumanBodyBones.RightUpperArm);
                    head = Bone(an, HumanBodyBones.Head);
                    desc = "head (Head bone rotation, initialized from shoulders)";
                    if (head == null) { error = "front=head needs a Humanoid Head bone."; return null; }
                    break;
                case FrontMode.Root:
                    desc = "root (" + an.name + ".forward)";
                    break;
                default:
                    desc = "world (+Z)";
                    break;
            }
            if ((s.Front == FrontMode.Eyes || s.Front == FrontMode.Shoulders || s.Front == FrontMode.Hips || s.Front == FrontMode.Head)
                && (a == null || b == null))
            {
                error = $"front={s.Front.ToString().ToLowerInvariant()} needs Humanoid bones that '{an.name}' does not have. " +
                        "Choose another front explicitly (eyes / shoulders / hips / head / root / world).";
                return null;
            }
            return new ViewRig(s, a, b, head, desc);
        }

        private static Transform Bone(Animator an, HumanBodyBones bone) => an.isHuman ? an.GetBoneTransform(bone) : null;

        /// <summary>水平の正面（単位ベクトル）。毎フレーム呼ぶので確保しない</summary>
        public Vector3 ComputeFront()
        {
            if (_hasFront && !_s.FollowFront) return _lockedFront;
            Vector3 f;
            switch (_s.Front)
            {
                case FrontMode.Root: f = _s.Animator.transform.forward; break;
                case FrontMode.World: f = Vector3.forward; break;
                case FrontMode.Head:
                    if (!_hasFront) _headLocalFront = Quaternion.Inverse(_head.rotation) * Across(_a, _b);
                    f = _head.rotation * _headLocalFront;
                    break;
                default: f = Across(_a, _b); break;
            }
            f = Vector3.ProjectOnPlane(f, Vector3.up);
            // 真上・真下を向いて水平成分が無いときは、直前の正面を使い続ける
            if (f.sqrMagnitude < 1e-8f) return _hasFront ? _lockedFront : Vector3.forward;
            f.Normalize();
            _lockedFront = f;
            _hasFront = true;
            return f;
        }

        // 左 → 右のベクトルと上の外積が前（Unity の左手系: right × up = forward）
        private static Vector3 Across(Transform left, Transform right) => Vector3.Cross(right.position - left.position, Vector3.up);

        public Vector3 FocusPoint(Vector3 front)
        {
            Vector3 right = Vector3.Cross(Vector3.up, front);
            Vector3 o = _s.FocusOffset;
            return _s.Focus.position + right * o.x + Vector3.up * o.y + front * o.z;
        }

        /// <summary>全角度を RT へ描く。スキニングの後（ViewRecordingLoop の撮影の時点）に呼ぶ</summary>
        public void Render()
        {
            Vector3 front = ComputeFront();
            Vector3 focus = FocusPoint(front);
            Vector3 right = Vector3.Cross(Vector3.up, front);
            if (_light != null)
            {
                _light.transform.rotation = Quaternion.LookRotation(-front + Vector3.down * LightDownTilt);
                _light.enabled = true;
            }
            for (int i = 0; i < _cams.Length; i++)
            {
                // yaw: 正で被写体の右側へ回り込む / pitch: 正で上から見下ろす
                Vector3 dir = Quaternion.AngleAxis(_s.Yaw[i], Vector3.up) * (Quaternion.AngleAxis(-_s.Pitch[i], right) * front);
                Transform t = _cams[i].transform;
                t.position = focus + dir * _s.Distance;
                t.LookAt(focus, Vector3.up);
                _cams[i].Render();
            }
            if (_light != null) _light.enabled = false;
        }

        /// <summary>RT を buffer（RGB24、下の行から）へ読み出す。確保しない</summary>
        public void ReadInto(byte[] buffer)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _rt;
            _tex.ReadPixels(new Rect(0, 0, _s.Width, _s.Height), 0, 0, false);
            RenderTexture.active = prev;
            _tex.GetRawTextureData<byte>().CopyTo(buffer);
        }

        /// <summary>スキンの設定を戻し、カメラ・ライト・RT を遅延で破棄する</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            TemporaryOverrides.Release(_skinLease);
            for (int i = 0; i < _cams.Length; i++)
                if (_cams[i] != null) _cams[i].targetTexture = null;

            GameObject holder = _holder;
            RenderTexture rt = _rt;
            Texture2D tex = _tex;
            if (Application.isPlaying && !ViewRecordingLoop.IsExitingPlayMode)
            {
                // Destroy は遅延で、RT の GPU 側もそのとき解放される（撮った直後に Release で即時に手放さない）
                if (holder != null) UnityEngine.Object.Destroy(holder);
                if (rt != null) UnityEngine.Object.Destroy(rt);
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
            else
            {
                // Edit モード・Play 終了の最中は Destroy が使えない／描画の途中なので、次の Editor の更新で破棄する
                EditorApplication.delayCall += () =>
                {
                    if (holder != null) UnityEngine.Object.DestroyImmediate(holder);
                    if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                    if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                };
                // 背面の Editor は更新ティックが来ず delayCall が回らないので、1 回起こす
                EditorApplication.QueuePlayerLoopUpdate();
            }
        }

        /// <summary>
        /// URP の UniversalAdditionalLightData.renderingLayers を全ビットにする（URP は Light.renderingLayerMask ではなくこちらで当たる対象を決める）。
        /// URP の無いプロジェクトでもコンパイルできるよう、型はリフレクションで扱う。URP が無ければ false。
        /// </summary>
        private static bool SetUrpRenderingLayersAll(Light light)
        {
            Type type = null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Unity.RenderPipelines.Universal.Runtime") continue;
                type = asm.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalLightData");
                break;
            }
            if (type == null) return false;
            Component data = light.GetComponent(type);
            if (data == null) data = light.gameObject.AddComponent(type);
            PropertyInfo prop = type.GetProperty("renderingLayers");
            if (prop == null || !prop.CanWrite)
                throw new InvalidOperationException("UniversalAdditionalLightData.renderingLayers is not writable on this URP version.");
            Type pt = prop.PropertyType;
            if (pt == typeof(uint)) prop.SetValue(data, uint.MaxValue);
            else if (pt == typeof(int)) prop.SetValue(data, -1);
            else
            {
                // URP のバージョンによっては RenderingLayerMask 構造体（uint からの暗黙変換を持つ）
                MethodInfo op = pt.GetMethod("op_Implicit", new[] { typeof(uint) });
                if (op == null) throw new InvalidOperationException("Unsupported renderingLayers type: " + pt.FullName);
                prop.SetValue(data, op.Invoke(null, new object[] { uint.MaxValue }));
            }
            return true;
        }
    }
}
