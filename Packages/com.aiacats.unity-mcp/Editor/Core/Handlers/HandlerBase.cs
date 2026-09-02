using System;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    internal abstract class HandlerBase : IMCPHandler
    {
        protected readonly MCPHttpServer Server;

        protected HandlerBase(MCPHttpServer server)
        {
            Server = server;
        }

        public abstract string Handle(string requestBody);

        protected GameObject FindGameObject(JObject request)
        {
            string objectPath = request["objectPath"]?.ToString();
            int? instanceId = request["instanceId"]?.ToObject<int?>();

            if (instanceId.HasValue)
            {
                return EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
            }

            if (!string.IsNullOrEmpty(objectPath))
            {
                // GameObject.Find は有効なオブジェクトしか返さない。非アクティブを掴めないと
                // 「一度無効にしたら二度と戻せない」うえ、呼び出し側が「存在しない」と誤解する
                return GameObject.Find(objectPath) ?? FindIncludingInactive(objectPath);
            }

            return null;
        }

        /// <summary>
        /// 読み込み済みの全シーンを走査して、パスまたは名前で GameObject を探す。
        /// 非アクティブも対象。パス指定が一致したものを優先し、無ければ末尾の名前で拾う。
        /// </summary>
        protected static GameObject FindIncludingInactive(string objectPath)
        {
            if (string.IsNullOrEmpty(objectPath)) return null;

            string wanted = objectPath.TrimStart('/');
            string leaf = wanted.Contains("/") ? wanted.Substring(wanted.LastIndexOf('/') + 1) : wanted;

            GameObject byLeaf = null;

            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (GetPath(t).TrimStart('/') == wanted) return t.gameObject;
                        if (byLeaf == null && t.name == leaf) byLeaf = t.gameObject;
                    }
                }
            }

            return byLeaf;
        }

        static string GetPath(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        protected Type ResolveComponentType(string componentName, GameObject target = null)
        {
            // Built-in Unity modules first.
            var type = Type.GetType($"UnityEngine.{componentName}, UnityEngine") ??
                       Type.GetType($"UnityEngine.{componentName}, UnityEngine.CoreModule") ??
                       Type.GetType($"UnityEngine.{componentName}, UnityEngine.PhysicsModule") ??
                       Type.GetType($"UnityEngine.{componentName}, UnityEngine.Physics2DModule") ??
                       Type.GetType($"UnityEngine.{componentName}, UnityEngine.UIModule") ??
                       Type.GetType($"UnityEngine.{componentName}, UnityEngine.AnimationModule");

            if (type != null) return type;

            // Already present on the GameObject (covers any namespace).
            if (target != null)
            {
                foreach (var comp in target.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    var t = comp.GetType();
                    if (t.Name == componentName || t.FullName == componentName) return t;
                }
            }

            // Search all loaded assemblies. Accepts simple name or fully qualified name.
            var componentBase = typeof(Component);
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                Type candidate = null;
                try
                {
                    candidate = asm.GetType(componentName, throwOnError: false, ignoreCase: false);
                }
                catch { /* ignore */ }
                if (candidate != null && componentBase.IsAssignableFrom(candidate)) return candidate;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (!componentBase.IsAssignableFrom(t)) continue;
                    if (t.Name == componentName || t.FullName == componentName) return t;
                }
            }

            return null;
        }

        protected string CreateSuccessResponse(string type, object data)
        {
            var response = new JObject
            {
                ["success"] = true,
                ["type"] = type,
                ["data"] = JToken.FromObject(data),
                ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            return response.ToString();
        }

        protected string CreateErrorResponse(string errorType, string message)
        {
            var response = new JObject
            {
                ["success"] = false,
                ["error"] = new JObject
                {
                    ["type"] = errorType,
                    ["message"] = message
                },
                ["timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            return response.ToString();
        }

        protected string ExecuteOnMainThread(Func<string> function, int timeoutMs = 5000)
        {
            return Server.ExecuteOnMainThreadWithResult(function, timeoutMs);
        }
    }
}
