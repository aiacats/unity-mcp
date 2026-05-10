using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    internal class UpdateComponentHandler : HandlerBase
    {
        public UpdateComponentHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                string componentName = request["componentName"]?.ToString();
                var componentData = request["componentData"] as JObject;

                var target = FindGameObject(request);
                if (target == null)
                    return CreateErrorResponse("gameobject_not_found", "GameObject not found");

                var componentType = ResolveComponentType(componentName, target);
                if (componentType == null)
                    return CreateErrorResponse("component_type_not_found", $"Component type not found: {componentName}");

                var component = target.GetComponent(componentType);
                if (component == null)
                {
                    component = target.AddComponent(componentType);
                    Undo.RegisterCreatedObjectUndo(component, "Add Component via MCP");
                }

                Undo.RecordObject(component, "Update Component via MCP");

                if (componentData != null)
                {
                    foreach (var property in componentData)
                    {
                        try
                        {
                            FieldInfo field = FindFieldIncludingPrivate(componentType, property.Key);
                            PropertyInfo prop = componentType.GetProperty(property.Key,
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                            if (field != null)
                            {
                                field.SetValue(component, ConvertJsonToFieldValue(property.Value, field.FieldType));
                            }
                            else if (prop != null && prop.CanWrite)
                            {
                                prop.SetValue(component, ConvertJsonToFieldValue(property.Value, prop.PropertyType));
                            }
                            else
                            {
                                Debug.LogWarning($"[Claude Code MCP] No writable field/property '{property.Key}' on {componentType.Name}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[Claude Code MCP] Failed to set {property.Key}: {ex.Message}");
                        }
                    }
                }

                EditorUtility.SetDirty(target);
                return CreateSuccessResponse("component_updated", $"Updated {componentName} on {target.name}");
            });
        }

        /// <summary>
        /// Walk up the inheritance chain so private SerializeField on base classes are found.
        /// </summary>
        private static FieldInfo FindFieldIncludingPrivate(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, flags);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>
        /// Converts a JSON value to a typed value, with special-case resolution for UnityEngine.Object references
        /// (instanceId / objectPath / componentName) and recursive handling of List/Array/POCO with nested
        /// UnityEngine.Object fields.
        /// </summary>
        private object ConvertJsonToFieldValue(JToken value, Type targetType)
        {
            if (value == null || value.Type == JTokenType.Null) return null;

            // 1) UnityEngine.Object reference (the leaf case)
            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                int? instanceId = null;
                string objectPath = null;
                string componentName = null;
                string assetPath = null;
                string guid = null;
                string subAssetName = null;

                if (value.Type == JTokenType.Integer)
                {
                    instanceId = value.ToObject<int>();
                }
                else if (value.Type == JTokenType.Object)
                {
                    JObject obj = (JObject)value;
                    instanceId = obj["instanceId"]?.ToObject<int?>();
                    objectPath = obj["objectPath"]?.ToString();
                    componentName = obj["componentName"]?.ToString();
                    assetPath = obj["assetPath"]?.ToString();
                    guid = obj["guid"]?.ToString();
                    subAssetName = obj["subAssetName"]?.ToString();
                }
                else if (value.Type == JTokenType.String)
                {
                    string s = value.ToString();
                    // Heuristic: a path that looks like an asset reference goes via AssetDatabase.
                    if (s.StartsWith("Assets/") || s.StartsWith("Packages/")) assetPath = s;
                    else objectPath = s;
                }

                if (instanceId.HasValue)
                {
                    UnityEngine.Object resolved = EditorUtility.InstanceIDToObject(instanceId.Value);
                    if (resolved == null) return null;
                    if (targetType.IsAssignableFrom(resolved.GetType())) return resolved;
                    if (resolved is GameObject go && typeof(Component).IsAssignableFrom(targetType))
                    {
                        Component c = go.GetComponent(targetType);
                        if (c != null) return c;
                    }
                    return null;
                }

                // Asset reference (guid → path → load all + match by type/name).
                if (!string.IsNullOrEmpty(guid) && string.IsNullOrEmpty(assetPath))
                {
                    assetPath = AssetDatabase.GUIDToAssetPath(guid);
                }
                if (!string.IsNullOrEmpty(assetPath))
                {
                    UnityEngine.Object asset = ResolveAsset(assetPath, targetType, subAssetName);
                    if (asset != null) return asset;
                }

                if (!string.IsNullOrEmpty(objectPath))
                {
                    GameObject go = GameObject.Find(objectPath);
                    if (go == null) return null;
                    if (targetType == typeof(GameObject)) return go;
                    if (targetType == typeof(Transform) || typeof(Transform).IsAssignableFrom(targetType))
                        return go.transform;
                    if (typeof(Component).IsAssignableFrom(targetType))
                    {
                        if (!string.IsNullOrEmpty(componentName))
                        {
                            Type t = ResolveComponentType(componentName, go);
                            if (t != null) return go.GetComponent(t);
                        }
                        return go.GetComponent(targetType);
                    }
                }
                return null;
            }

            // 2) Generic List<T>: recurse element-wise so nested UE.Object refs resolve.
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(System.Collections.Generic.List<>))
            {
                Type elemType = targetType.GetGenericArguments()[0];
                IList listInstance = (IList)Activator.CreateInstance(targetType);
                if (value is JArray jArr)
                {
                    foreach (JToken item in jArr)
                    {
                        listInstance.Add(ConvertJsonToFieldValue(item, elemType));
                    }
                }
                return listInstance;
            }

            // 3) Array: same idea.
            if (targetType.IsArray)
            {
                Type elemType = targetType.GetElementType();
                if (value is JArray jArr2)
                {
                    Array arr = Array.CreateInstance(elemType, jArr2.Count);
                    for (int i = 0; i < jArr2.Count; i++)
                    {
                        arr.SetValue(ConvertJsonToFieldValue(jArr2[i], elemType), i);
                    }
                    return arr;
                }
                return null;
            }

            // 4) Plain POCO (incl. [Serializable] nested classes like SongLibrary.Entry).
            //    Only recurse on user types — leave Unity built-ins (Vector3, Color, etc.) and primitives to Json.NET.
            if (value is JObject jObj && targetType.IsClass && targetType != typeof(string) &&
                !typeof(UnityEngine.Object).IsAssignableFrom(targetType) &&
                !targetType.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal))
            {
                object instance;
                try { instance = Activator.CreateInstance(targetType); }
                catch { return value.ToObject(targetType); }

                foreach (var prop in jObj)
                {
                    FieldInfo f = FindFieldIncludingPrivate(targetType, prop.Key);
                    if (f != null)
                    {
                        try { f.SetValue(instance, ConvertJsonToFieldValue(prop.Value, f.FieldType)); }
                        catch { /* skip incompatible field */ }
                    }
                }
                return instance;
            }

            // 5) Fallback to Json.NET for primitives, enums, structs, Vector3, etc.
            return value.ToObject(targetType);
        }

        /// <summary>
        /// Loads an asset from <paramref name="assetPath"/> matching <paramref name="targetType"/>.
        /// If <paramref name="subAssetName"/> is non-empty, scans all sub-assets of the file and picks
        /// the first one whose name matches. Otherwise picks the first asset assignable to targetType
        /// (handles FBX → AnimationClip / Avatar / Mesh sub-asset cases without needing the fileID).
        /// </summary>
        private UnityEngine.Object ResolveAsset(string assetPath, Type targetType, string subAssetName)
        {
            // Fast path: main asset of matching type.
            UnityEngine.Object main = AssetDatabase.LoadAssetAtPath(assetPath, targetType);
            if (main != null && string.IsNullOrEmpty(subAssetName)) return main;

            UnityEngine.Object[] all = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            if (all == null || all.Length == 0) return main; // fall back to main if there are no sub-assets

            // Named sub-asset takes priority when specified.
            if (!string.IsNullOrEmpty(subAssetName))
            {
                foreach (UnityEngine.Object obj in all)
                {
                    if (obj == null) continue;
                    if (obj.name != subAssetName) continue;
                    if (targetType.IsAssignableFrom(obj.GetType())) return obj;
                }
            }

            // Otherwise return the first sub-asset that fits the target type and isn't a private preview.
            foreach (UnityEngine.Object obj in all)
            {
                if (obj == null) continue;
                if (obj.name != null && obj.name.StartsWith("__preview__")) continue;
                if (targetType.IsAssignableFrom(obj.GetType())) return obj;
            }
            return main;
        }
    }

    internal class RemoveComponentHandler : HandlerBase
    {
        public RemoveComponentHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                string componentName = request["componentName"]?.ToString();

                if (string.IsNullOrEmpty(componentName))
                    return CreateErrorResponse("missing_parameter", "componentName is required");

                var target = FindGameObject(request);
                if (target == null)
                    return CreateErrorResponse("gameobject_not_found", "GameObject not found");

                var componentType = ResolveComponentType(componentName);
                if (componentType == null)
                    return CreateErrorResponse("component_type_not_found", $"Component type not found: {componentName}");

                var component = target.GetComponent(componentType);
                if (component == null)
                    return CreateErrorResponse("component_not_found", $"Component {componentName} not found on {target.name}");

                Undo.DestroyObjectImmediate(component);
                return CreateSuccessResponse("component_removed", $"Removed {componentName} from {target.name}");
            });
        }
    }

    internal class GetComponentPropertiesHandler : HandlerBase
    {
        public GetComponentPropertiesHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                string componentName = request["componentName"]?.ToString();

                if (string.IsNullOrEmpty(componentName))
                    return CreateErrorResponse("missing_parameter", "componentName is required");

                var target = FindGameObject(request);
                if (target == null)
                    return CreateErrorResponse("gameobject_not_found", "GameObject not found");

                var componentType = ResolveComponentType(componentName, target);
                if (componentType == null)
                    return CreateErrorResponse("component_type_not_found", $"Component type not found: {componentName}");

                var component = target.GetComponent(componentType);
                if (component == null)
                    return CreateErrorResponse("component_not_found", $"Component {componentName} not found on {target.name}");

                var properties = SerializeComponentProperties(component);

                var result = new JObject
                {
                    ["gameObject"] = target.name,
                    ["componentType"] = componentType.Name,
                    ["properties"] = properties
                };

                return CreateSuccessResponse("component_properties", result);
            });
        }

        private static JObject SerializeComponentProperties(Component component)
        {
            var properties = new JObject();
            var serializedObject = new SerializedObject(component);
            var iterator = serializedObject.GetIterator();
            bool enterChildren = true;

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.name == "m_Script") continue;

                try
                {
                    properties[iterator.name] = SerializeProperty(iterator);
                }
                catch
                {
                    // Skip properties that can't be read
                }
            }

            serializedObject.Dispose();
            return properties;
        }

        private static JToken SerializeProperty(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue;
                case SerializedPropertyType.Boolean:
                    return prop.boolValue;
                case SerializedPropertyType.Float:
                    return prop.floatValue;
                case SerializedPropertyType.String:
                    return prop.stringValue;
                case SerializedPropertyType.Enum:
                    return prop.enumDisplayNames[prop.enumValueIndex];
                case SerializedPropertyType.Vector2:
                    var v2 = prop.vector2Value;
                    return new JObject { ["x"] = v2.x, ["y"] = v2.y };
                case SerializedPropertyType.Vector3:
                    var v3 = prop.vector3Value;
                    return new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
                case SerializedPropertyType.Vector4:
                    var v4 = prop.vector4Value;
                    return new JObject { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
                case SerializedPropertyType.Color:
                    var c = prop.colorValue;
                    return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
                case SerializedPropertyType.Bounds:
                    var b = prop.boundsValue;
                    return new JObject
                    {
                        ["center"] = new JObject { ["x"] = b.center.x, ["y"] = b.center.y, ["z"] = b.center.z },
                        ["size"] = new JObject { ["x"] = b.size.x, ["y"] = b.size.y, ["z"] = b.size.z }
                    };
                case SerializedPropertyType.Rect:
                    var r = prop.rectValue;
                    return new JObject { ["x"] = r.x, ["y"] = r.y, ["width"] = r.width, ["height"] = r.height };
                case SerializedPropertyType.ObjectReference:
                    if (prop.objectReferenceValue != null)
                    {
                        return new JObject
                        {
                            ["name"] = prop.objectReferenceValue.name,
                            ["type"] = prop.objectReferenceValue.GetType().Name,
                            ["instanceId"] = prop.objectReferenceValue.GetInstanceID()
                        };
                    }
                    return null;
                default:
                    return $"({prop.propertyType})";
            }
        }
    }
}
