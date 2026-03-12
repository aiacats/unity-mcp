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
                var gameObjectData = request["gameObjectData"] as JObject;

                if (gameObjectData == null)
                    return CreateErrorResponse("missing_parameter", "gameObjectData is required");

                GameObject target = FindGameObject(request);
                bool isNew = false;

                if (target == null)
                {
                    string objectPath = request["objectPath"]?.ToString();
                    if (!string.IsNullOrEmpty(objectPath))
                    {
                        target = new GameObject(objectPath);
                        Undo.RegisterCreatedObjectUndo(target, "Create GameObject via MCP");
                        isNew = true;
                    }
                    else
                    {
                        return CreateErrorResponse("gameobject_not_found", "GameObject not found and no objectPath to create");
                    }
                }

                if (!isNew) Undo.RecordObject(target, "Update GameObject via MCP");

                if (gameObjectData["name"] != null)
                    target.name = gameObjectData["name"].ToString();
                if (gameObjectData["activeSelf"] != null)
                    target.SetActive(gameObjectData["activeSelf"].ToObject<bool>());
                if (gameObjectData["tag"] != null)
                    target.tag = gameObjectData["tag"].ToString();
                if (gameObjectData["layer"] != null)
                    target.layer = gameObjectData["layer"].ToObject<int>();
                if (gameObjectData["isStatic"] != null)
                    target.isStatic = gameObjectData["isStatic"].ToObject<bool>();

                EditorUtility.SetDirty(target);
                return CreateSuccessResponse("gameobject_updated", $"{(isNew ? "Created" : "Updated")}: {target.name}");
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
