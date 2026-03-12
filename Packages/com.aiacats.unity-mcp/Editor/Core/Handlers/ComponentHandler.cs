using System;
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
                            var field = componentType.GetField(property.Key);
                            var prop = componentType.GetProperty(property.Key);

                            if (field != null)
                            {
                                field.SetValue(component, property.Value.ToObject(field.FieldType));
                            }
                            else if (prop != null && prop.CanWrite)
                            {
                                prop.SetValue(component, property.Value.ToObject(prop.PropertyType));
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
