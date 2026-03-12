using System;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    internal class SelectGameObjectHandler : HandlerBase
    {
        public SelectGameObjectHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                var target = FindGameObject(request);

                if (target == null)
                    return CreateErrorResponse("gameobject_not_found", "GameObject not found");

                Selection.activeGameObject = target;
                EditorGUIUtility.PingObject(target);
                return CreateSuccessResponse("gameobject_selected", $"Selected: {target.name}");
            });
        }
    }

    internal class GetGameObjectInfoHandler : HandlerBase
    {
        public GetGameObjectInfoHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                var target = FindGameObject(request);

                if (target == null)
                    return CreateErrorResponse("gameobject_not_found", "GameObject not found");

                var transform = target.transform;
                var info = new JObject
                {
                    ["name"] = target.name,
                    ["instanceId"] = target.GetInstanceID(),
                    ["isActive"] = target.activeSelf,
                    ["isActiveInHierarchy"] = target.activeInHierarchy,
                    ["tag"] = target.tag,
                    ["layer"] = target.layer,
                    ["layerName"] = LayerMask.LayerToName(target.layer),
                    ["isStatic"] = target.isStatic,
                    ["transform"] = new JObject
                    {
                        ["position"] = SerializeVector3(transform.position),
                        ["localPosition"] = SerializeVector3(transform.localPosition),
                        ["rotation"] = SerializeVector3(transform.eulerAngles),
                        ["localRotation"] = SerializeVector3(transform.localEulerAngles),
                        ["localScale"] = SerializeVector3(transform.localScale)
                    },
                    ["childCount"] = transform.childCount
                };

                if (transform.parent != null)
                {
                    info["parentName"] = transform.parent.name;
                    info["parentInstanceId"] = transform.parent.gameObject.GetInstanceID();
                }

                var components = new JArray();
                foreach (var comp in target.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    components.Add(new JObject
                    {
                        ["type"] = comp.GetType().Name,
                        ["fullType"] = comp.GetType().FullName,
                        ["instanceId"] = comp.GetInstanceID()
                    });
                }
                info["components"] = components;

                return CreateSuccessResponse("gameobject_info", info);
            });
        }

        private static JObject SerializeVector3(Vector3 v)
        {
            return new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
        }
    }

    internal class UpdateGameObjectHandler : HandlerBase
    {
        public UpdateGameObjectHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                string objectPath = request["objectPath"]?.ToString();
                var gameObjectData = request["gameObjectData"] as JObject;

                GameObject target = null;
                if (!string.IsNullOrEmpty(objectPath))
                {
                    target = GameObject.Find(objectPath);
                    if (target == null)
                    {
                        target = new GameObject(objectPath);
                        Undo.RegisterCreatedObjectUndo(target, "Create GameObject via MCP");
                    }
                }

                if (target == null || gameObjectData == null)
                    return CreateErrorResponse("gameobject_update_failed", "Failed to update GameObject");

                Undo.RecordObject(target, "Update GameObject via MCP");

                if (gameObjectData["name"] != null)
                    target.name = gameObjectData["name"].ToString();
                if (gameObjectData["activeSelf"] != null)
                    target.SetActive(gameObjectData["activeSelf"].ToObject<bool>());
                if (gameObjectData["tag"] != null)
                    target.tag = gameObjectData["tag"].ToString();

                EditorUtility.SetDirty(target);
                return CreateSuccessResponse("gameobject_updated", $"Updated: {target.name}");
            });
        }
    }

    internal class DeleteGameObjectHandler : HandlerBase
    {
        public DeleteGameObjectHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                var target = FindGameObject(request);

                if (target == null)
                    return CreateErrorResponse("gameobject_not_found", "GameObject not found");

                string targetName = target.name;
                Undo.DestroyObjectImmediate(target);
                return CreateSuccessResponse("gameobject_deleted", $"Deleted GameObject: {targetName}");
            });
        }
    }
}
