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
                        target = CreateGameObjectAtPath(objectPath);
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

                // Optional reparenting: parentPath or parentInstanceId
                string parentPath = gameObjectData["parentPath"]?.ToString();
                int? parentId = gameObjectData["parentInstanceId"]?.ToObject<int?>();
                Transform newParent = null;
                if (parentId.HasValue)
                {
                    var po = EditorUtility.InstanceIDToObject(parentId.Value) as GameObject;
                    if (po != null) newParent = po.transform;
                }
                else if (!string.IsNullOrEmpty(parentPath))
                {
                    var po = GameObject.Find(parentPath);
                    if (po != null) newParent = po.transform;
                }
                if (newParent != null && target.transform.parent != newParent)
                {
                    Undo.SetTransformParent(target.transform, newParent, "Reparent via MCP");
                }

                // Optional sibling ordering: -1 = first, otherwise zero-based index (clamped).
                if (gameObjectData["siblingIndex"] != null)
                {
                    int idx = gameObjectData["siblingIndex"].ToObject<int>();
                    if (idx < 0) target.transform.SetAsFirstSibling();
                    else target.transform.SetSiblingIndex(idx);
                }

                EditorUtility.SetDirty(target);
                return CreateSuccessResponse("gameobject_updated", $"{(isNew ? "Created" : "Updated")}: {target.name}");
            });
        }

        /// <summary>
        /// Walk a slash-separated path and create missing intermediate GameObjects, returning the leaf.
        /// Existing nodes along the path are reused.
        /// </summary>
        private static GameObject CreateGameObjectAtPath(string path)
        {
            string[] segments = path.Split('/');
            Transform parent = null;
            GameObject current = null;
            string accPath = "";

            foreach (string raw in segments)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                accPath = string.IsNullOrEmpty(accPath) ? raw : accPath + "/" + raw;

                GameObject found = GameObject.Find(accPath);
                if (found != null)
                {
                    current = found;
                    parent = found.transform;
                    continue;
                }

                current = new GameObject(raw);
                Undo.RegisterCreatedObjectUndo(current, "Create GameObject via MCP");
                if (parent != null) current.transform.SetParent(parent, worldPositionStays: false);
                parent = current.transform;
            }

            return current;
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
