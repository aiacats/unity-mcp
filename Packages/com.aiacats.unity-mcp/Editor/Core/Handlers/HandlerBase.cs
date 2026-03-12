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
                return GameObject.Find(objectPath);
            }

            return null;
        }

        protected Type ResolveComponentType(string componentName, GameObject target = null)
        {
            var type = Type.GetType($"UnityEngine.{componentName}, UnityEngine") ??
                       Type.GetType($"UnityEngine.{componentName}, UnityEngine.CoreModule") ??
                       Type.GetType($"UnityEngine.{componentName}, UnityEngine.PhysicsModule") ??
                       Type.GetType($"UnityEngine.{componentName}, UnityEngine.Physics2DModule") ??
                       Type.GetType($"UnityEngine.{componentName}, UnityEngine.UIModule") ??
                       Type.GetType($"UnityEngine.{componentName}, UnityEngine.AnimationModule");

            if (type == null && target != null)
            {
                foreach (var comp in target.GetComponents<Component>())
                {
                    if (comp != null && comp.GetType().Name == componentName)
                    {
                        return comp.GetType();
                    }
                }
            }

            return type;
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
