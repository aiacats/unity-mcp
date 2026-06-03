using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    /// <summary>
    /// 汎用リフレクション/JSON 変換ユーティリティ。
    /// UpdateComponentHandler の実績ある JSON→型付き値変換ロジックを、コンポーネント以外
    /// （ScriptableObject アセット・メソッド引数）にも使えるよう共有化したもの。
    /// </summary>
    internal static class MCPReflection
    {
        /// <summary>完全修飾名 or 単純名から Type を解決する。</summary>
        public static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            var t = Type.GetType(typeName, false);
            if (t != null) return t;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType(typeName, false); }
                catch { t = null; }
                if (t != null) return t;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }

                foreach (var c in types)
                {
                    if (c == null) continue;
                    if (c.FullName == typeName || c.Name == typeName) return c;
                }
            }
            return null;
        }

        public static FieldInfo FindFieldIncludingPrivate(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, flags);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>JSON 値を targetType の値へ変換。UnityEngine.Object 参照・List/配列・ネスト POCO に対応。</summary>
        public static object ConvertJsonToFieldValue(JToken value, Type targetType)
        {
            if (value == null || value.Type == JTokenType.Null) return null;

            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                return ResolveUnityObject(value, targetType);
            }

            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type elemType = targetType.GetGenericArguments()[0];
                IList listInstance = (IList)Activator.CreateInstance(targetType);
                if (value is JArray jArr)
                    foreach (JToken item in jArr) listInstance.Add(ConvertJsonToFieldValue(item, elemType));
                return listInstance;
            }

            if (targetType.IsArray)
            {
                Type elemType = targetType.GetElementType();
                if (value is JArray jArr2)
                {
                    Array arr = Array.CreateInstance(elemType, jArr2.Count);
                    for (int i = 0; i < jArr2.Count; i++)
                        arr.SetValue(ConvertJsonToFieldValue(jArr2[i], elemType), i);
                    return arr;
                }
                return null;
            }

            if (value is JObject jObj && targetType.IsClass && targetType != typeof(string) &&
                !typeof(UnityEngine.Object).IsAssignableFrom(targetType) &&
                (targetType.Namespace == null || !targetType.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal)))
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

            return value.ToObject(targetType);
        }

        /// <summary>JSON 値から UnityEngine.Object を解決（instanceId / assetPath / guid / objectPath(+componentName)）。</summary>
        public static UnityEngine.Object ResolveUnityObject(JToken value, Type targetType)
        {
            int? instanceId = null;
            string objectPath = null, componentName = null, assetPath = null, guid = null, subAssetName = null;

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
                if (s.StartsWith("Assets/") || s.StartsWith("Packages/")) assetPath = s;
                else objectPath = s;
            }

            if (instanceId.HasValue)
            {
                var resolved = EditorUtility.InstanceIDToObject(instanceId.Value);
                if (resolved == null) return null;
                if (targetType.IsAssignableFrom(resolved.GetType())) return resolved;
                if (resolved is GameObject go && typeof(Component).IsAssignableFrom(targetType))
                {
                    var c = go.GetComponent(targetType);
                    if (c != null) return c;
                }
                return null;
            }

            if (!string.IsNullOrEmpty(guid) && string.IsNullOrEmpty(assetPath))
                assetPath = AssetDatabase.GUIDToAssetPath(guid);

            if (!string.IsNullOrEmpty(assetPath))
            {
                var asset = ResolveAsset(assetPath, targetType, subAssetName);
                if (asset != null) return asset;
            }

            if (!string.IsNullOrEmpty(objectPath))
            {
                GameObject go = GameObject.Find(objectPath);
                if (go == null) return null;
                if (targetType == typeof(GameObject)) return go;
                if (typeof(Transform).IsAssignableFrom(targetType)) return go.transform;
                if (typeof(Component).IsAssignableFrom(targetType))
                {
                    if (!string.IsNullOrEmpty(componentName))
                    {
                        Type t = ResolveType(componentName);
                        if (t != null) { var c = go.GetComponent(t); if (c != null) return c; }
                    }
                    return go.GetComponent(targetType);
                }
            }
            return null;
        }

        public static UnityEngine.Object ResolveAsset(string assetPath, Type targetType, string subAssetName)
        {
            UnityEngine.Object main = AssetDatabase.LoadAssetAtPath(assetPath, targetType ?? typeof(UnityEngine.Object));
            if (main != null && string.IsNullOrEmpty(subAssetName)) return main;

            UnityEngine.Object[] all = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            if (all == null || all.Length == 0) return main;

            if (!string.IsNullOrEmpty(subAssetName))
                foreach (var obj in all)
                    if (obj != null && obj.name == subAssetName && (targetType == null || targetType.IsAssignableFrom(obj.GetType())))
                        return obj;

            foreach (var obj in all)
            {
                if (obj == null) continue;
                if (obj.name != null && obj.name.StartsWith("__preview__")) continue;
                if (targetType == null || targetType.IsAssignableFrom(obj.GetType())) return obj;
            }
            return main;
        }

        /// <summary>戻り値を軽量に JSON 化（UnityEngine.Object は name/type/instanceId、その他は ToString）。</summary>
        public static JToken DescribeResult(object result)
        {
            if (result == null) return JValue.CreateNull();
            if (result is UnityEngine.Object uo)
            {
                return new JObject
                {
                    ["name"] = uo == null ? null : uo.name,
                    ["type"] = uo.GetType().Name,
                    ["instanceId"] = uo == null ? 0 : uo.GetInstanceID()
                };
            }
            if (result is bool || result is int || result is long || result is float || result is double || result is string)
                return new JValue(result);
            return new JValue(result.ToString());
        }
    }

    /// <summary>
    /// 任意の static/instance メソッドをリフレクションで呼び出す（エディタ自動化の汎用プリミティブ）。
    /// 例: VcamRigBuilder.Generate(chief) や プロジェクト側の移行ルーチンを起動できる。
    /// セキュリティ: localhost 開発ツール用。呼び出しは毎回ログ出力する。
    /// </summary>
    internal class InvokeMethodHandler : HandlerBase
    {
        public InvokeMethodHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() =>
            {
                var request = JObject.Parse(requestBody);
                string typeName = request["typeName"]?.ToString();
                string methodName = request["methodName"]?.ToString();
                var argsArray = request["args"] as JArray;
                JToken targetToken = request["target"];

                if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
                    return CreateErrorResponse("missing_parameter", "typeName and methodName are required");

                Type type = MCPReflection.ResolveType(typeName);
                if (type == null)
                    return CreateErrorResponse("type_not_found", $"Type not found: {typeName}");

                int argCount = argsArray?.Count ?? 0;

                // target が指定されていればインスタンスメソッド、無ければ static を優先。
                object targetInstance = null;
                if (targetToken != null && targetToken.Type != JTokenType.Null)
                {
                    targetInstance = MCPReflection.ResolveUnityObject(targetToken, typeof(UnityEngine.Object));
                    if (targetInstance == null)
                        return CreateErrorResponse("target_not_found", "Could not resolve 'target' instance");
                }

                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                     (targetInstance != null ? BindingFlags.Instance : BindingFlags.Static);

                MethodInfo method = type.GetMethods(flags)
                    .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == argCount);
                if (method == null)
                    return CreateErrorResponse("method_not_found",
                        $"Method not found: {typeName}.{methodName} with {argCount} parameter(s)");

                var paramInfos = method.GetParameters();
                object[] callArgs = new object[argCount];
                for (int i = 0; i < argCount; i++)
                {
                    try { callArgs[i] = MCPReflection.ConvertJsonToFieldValue(argsArray[i], paramInfos[i].ParameterType); }
                    catch (Exception ex)
                    {
                        return CreateErrorResponse("arg_conversion_failed",
                            $"Failed to convert arg {i} to {paramInfos[i].ParameterType.Name}: {ex.Message}");
                    }
                }

                Debug.Log($"[Claude Code MCP] invoke_method: {typeName}.{methodName}({argCount} args){(targetInstance != null ? " [instance]" : " [static]")}");

                object result;
                try { result = method.Invoke(targetInstance, callArgs); }
                catch (TargetInvocationException tie)
                {
                    return CreateErrorResponse("invocation_exception", tie.InnerException?.Message ?? tie.Message);
                }
                catch (Exception ex)
                {
                    return CreateErrorResponse("invocation_exception", ex.Message);
                }

                return CreateSuccessResponse("method_invoked", new JObject
                {
                    ["type"] = typeName,
                    ["method"] = methodName,
                    ["returnType"] = method.ReturnType.Name,
                    ["result"] = MCPReflection.DescribeResult(result)
                });
            }, timeoutMs: 60000);
        }
    }

    /// <summary>
    /// ScriptableObject 等のアセットを生成する（任意で初期フィールドを設定）。
    /// </summary>
    internal class CreateAssetHandler : HandlerBase
    {
        public CreateAssetHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() =>
            {
                var request = JObject.Parse(requestBody);
                string typeName = request["typeName"]?.ToString();
                string path = request["path"]?.ToString();
                var data = request["data"] as JObject;
                bool unique = request["unique"]?.ToObject<bool?>() ?? true;

                if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(path))
                    return CreateErrorResponse("missing_parameter", "typeName and path are required");
                if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/"))
                    return CreateErrorResponse("invalid_path", "path must start with 'Assets/' or 'Packages/'");

                Type type = MCPReflection.ResolveType(typeName);
                if (type == null)
                    return CreateErrorResponse("type_not_found", $"Type not found: {typeName}");
                if (!typeof(ScriptableObject).IsAssignableFrom(type))
                    return CreateErrorResponse("unsupported_type", $"create_asset currently supports ScriptableObject types only: {typeName}");

                var so = ScriptableObject.CreateInstance(type);
                if (so == null)
                    return CreateErrorResponse("create_failed", $"CreateInstance returned null for {typeName}");

                if (data != null)
                {
                    foreach (var prop in data)
                    {
                        FieldInfo f = MCPReflection.FindFieldIncludingPrivate(type, prop.Key);
                        if (f != null)
                        {
                            try { f.SetValue(so, MCPReflection.ConvertJsonToFieldValue(prop.Value, f.FieldType)); }
                            catch (Exception ex) { Debug.LogWarning($"[Claude Code MCP] create_asset: failed to set {prop.Key}: {ex.Message}"); }
                        }
                    }
                }

                string finalPath = unique ? AssetDatabase.GenerateUniqueAssetPath(path) : path;
                AssetDatabase.CreateAsset(so, finalPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                return CreateSuccessResponse("asset_created", new JObject
                {
                    ["path"] = finalPath,
                    ["guid"] = AssetDatabase.AssetPathToGUID(finalPath),
                    ["type"] = type.Name
                });
            }, timeoutMs: 30000);
        }
    }

    /// <summary>
    /// 任意の UnityEngine.Object（コンポーネント or アセット）のフィールド/参照を設定する。
    /// update_component をアセットにも一般化したもの。対象は instanceId / assetPath(+typeName) / guid /
    /// objectPath(+componentName) で指定。
    /// </summary>
    internal class SetObjectPropertiesHandler : HandlerBase
    {
        public SetObjectPropertiesHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() =>
            {
                var request = JObject.Parse(requestBody);
                var data = request["data"] as JObject;
                if (data == null)
                    return CreateErrorResponse("missing_parameter", "data (object of fields) is required");

                // 対象解決: target トークン（instanceId/assetPath/guid/objectPath）。
                Type hintType = null;
                string typeName = request["typeName"]?.ToString();
                if (!string.IsNullOrEmpty(typeName)) hintType = MCPReflection.ResolveType(typeName);

                JToken targetToken = request["target"] ?? request;
                UnityEngine.Object target = MCPReflection.ResolveUnityObject(targetToken, hintType ?? typeof(UnityEngine.Object));
                if (target == null)
                    return CreateErrorResponse("target_not_found", "Could not resolve target object (use instanceId / assetPath / guid / objectPath[+componentName])");

                Type objType = target.GetType();
                Undo.RecordObject(target, "Set Object Properties via MCP");

                int set = 0;
                foreach (var prop in data)
                {
                    FieldInfo field = MCPReflection.FindFieldIncludingPrivate(objType, prop.Key);
                    PropertyInfo pinfo = objType.GetProperty(prop.Key, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    try
                    {
                        if (field != null)
                        {
                            field.SetValue(target, MCPReflection.ConvertJsonToFieldValue(prop.Value, field.FieldType));
                            set++;
                        }
                        else if (pinfo != null && pinfo.CanWrite)
                        {
                            pinfo.SetValue(target, MCPReflection.ConvertJsonToFieldValue(prop.Value, pinfo.PropertyType));
                            set++;
                        }
                        else
                        {
                            Debug.LogWarning($"[Claude Code MCP] set_object_properties: no writable field/property '{prop.Key}' on {objType.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Claude Code MCP] set_object_properties: failed to set {prop.Key}: {ex.Message}");
                    }
                }

                EditorUtility.SetDirty(target);
                if (AssetDatabase.Contains(target)) AssetDatabase.SaveAssets();

                return CreateSuccessResponse("object_properties_set", new JObject
                {
                    ["target"] = target.name,
                    ["type"] = objType.Name,
                    ["fieldsSet"] = set
                });
            }, timeoutMs: 30000);
        }
    }
}
