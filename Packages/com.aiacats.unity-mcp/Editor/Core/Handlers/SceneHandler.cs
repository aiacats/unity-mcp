using System;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    internal class SaveSceneHandler : HandlerBase
    {
        public SaveSceneHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

                if (string.IsNullOrEmpty(scene.path))
                    return CreateErrorResponse("scene_not_saved", "Scene has no path. Save it manually first via File > Save Scene As.");

                bool saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);

                return saved
                    ? CreateSuccessResponse("scene_saved", $"Saved scene: {scene.name} ({scene.path})")
                    : CreateErrorResponse("save_scene_failed", $"Failed to save scene: {scene.name}");
            });
        }
    }

    internal class OpenSceneHandler : HandlerBase
    {
        public OpenSceneHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                string scenePath = request["scenePath"]?.ToString();
                bool additive = request["additive"]?.ToObject<bool>() ?? false;
                // When true, force a reload from the .unity file on disk: any unsaved in-memory edits in
                // the Editor are dropped, and the file's current contents become the active scene state.
                // Required when the .unity file was edited externally and you want those edits applied
                // (the default save-first path would otherwise overwrite the external edits).
                bool reloadFromDisk = request["reloadFromDisk"]?.ToObject<bool>()
                                       ?? request["discardChanges"]?.ToObject<bool>() // legacy name kept for back-compat
                                       ?? false;

                if (string.IsNullOrEmpty(scenePath))
                    return CreateErrorResponse("missing_parameter", "scenePath is required");

                var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (currentScene.isDirty && !additive && !string.IsNullOrEmpty(currentScene.path))
                {
                    if (reloadFromDisk)
                    {
                        // Save-to-temp trick: SaveScene(scene, tempPath, saveAsCopy=false) renames the
                        // in-memory scene to the temp path and clears its dirty flag without touching
                        // the real .unity file. Open the real path next, then delete the temp asset.
                        string tempPath = "Assets/_mcp_scene_reload_" + System.Guid.NewGuid().ToString("N") + ".unity";
                        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(currentScene, tempPath, false);
                        try
                        {
                            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath,
                                UnityEditor.SceneManagement.OpenSceneMode.Single);
                        }
                        finally
                        {
                            UnityEditor.AssetDatabase.DeleteAsset(tempPath);
                        }
                        return CreateSuccessResponse("scene_opened", $"Reloaded from disk (in-memory edits dropped): {scenePath}");
                    }
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(currentScene);
                }

                var mode = additive
                    ? UnityEditor.SceneManagement.OpenSceneMode.Additive
                    : UnityEditor.SceneManagement.OpenSceneMode.Single;

                var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath, mode);
                return CreateSuccessResponse("scene_opened", $"Opened scene: {scene.name} ({scenePath})");
            });
        }
    }

    /// <summary>
    /// シーンの構成（ヒエラルキー・部品・カメラの出力先・ライト・Missing Script）を書き出す。
    /// 実装や検証でオブジェクトのオン・オフや一時処理を掛ける前に、構成を把握してプロジェクトの CLAUDE.md へ残すためのもの。
    /// 開いていないシーンは Additive で開いて読み、保存せずに閉じる（開いていたシーンは閉じない）。
    /// </summary>
    internal class DumpSceneHierarchyHandler : HandlerBase
    {
        public DumpSceneHierarchyHandler(MCPHttpServer server) : base(server) { }

        // 役割が読み取れない基本部品は既定で省く（Transform は全オブジェクトにある。描画の部品は数が多く埋もれる）
        private static readonly string[] DefaultExcludedComponents =
        {
            "Transform", "RectTransform", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "CanvasRenderer"
        };

        private class Options
        {
            public int MaxDepth;
            public bool IncludeInactive;
            public string[] Filter;
            public int MaxNodes;
            public int NodeCount;
            public bool Truncated;
        }

        public override string Handle(string requestBody)
        {
            // 開いていないシーンを Additive で読むと数秒かかるので、既定の 5 秒より長く待つ
            // （大きいシーンで 10 秒程度を実測の目安とし、余裕を見て 60 秒で打ち切る）
            return ExecuteOnMainThread(() => {
                var request = string.IsNullOrEmpty(requestBody) ? new JObject() : JObject.Parse(requestBody);
                string scenePath = request["scenePath"]?.ToString();
                string outputPath = request["outputPath"]?.ToString();
                var options = new Options
                {
                    MaxDepth = request["maxDepth"]?.ToObject<int>() ?? 2,
                    IncludeInactive = request["includeInactive"]?.ToObject<bool>() ?? true,
                    Filter = request["componentFilter"]?.ToObject<string[]>(),
                    // 応答が大きくなりすぎないよう上限を置く（1 ノード 100〜200 文字なので 2000 で 300KB 前後）
                    MaxNodes = request["maxNodes"]?.ToObject<int>() ?? 2000,
                };

                var scenes = new System.Collections.Generic.List<UnityEngine.SceneManagement.Scene>();
                UnityEngine.SceneManagement.Scene openedHere = default;
                if (string.IsNullOrEmpty(scenePath))
                {
                    for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                        scenes.Add(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i));
                }
                else
                {
                    var loaded = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);
                    if (loaded.IsValid() && loaded.isLoaded)
                    {
                        scenes.Add(loaded);
                    }
                    else
                    {
                        if (EditorApplication.isPlaying)
                            return CreateErrorResponse("play_mode", "Cannot open another scene while in Play Mode. Exit Play Mode or omit scenePath.");
                        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
                            return CreateErrorResponse("scene_not_found", $"Scene not found: {scenePath}");
                        openedHere = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                            scenePath, UnityEditor.SceneManagement.OpenSceneMode.Additive);
                        scenes.Add(openedHere);
                    }
                }

                var text = new System.Text.StringBuilder();
                var sceneArray = new JArray();
                try
                {
                    foreach (var scene in scenes)
                    {
                        text.AppendLine($"# {(string.IsNullOrEmpty(scene.path) ? scene.name : scene.path)}");
                        var roots = new JArray();
                        foreach (var root in scene.GetRootGameObjects())
                        {
                            var node = Walk(root.transform, 0, options, text);
                            if (node != null) roots.Add(node);
                        }
                        sceneArray.Add(new JObject { ["scene"] = scene.path, ["roots"] = roots });
                    }
                }
                finally
                {
                    if (openedHere.IsValid())
                        UnityEditor.SceneManagement.EditorSceneManager.CloseScene(openedHere, true);
                }

                if (options.Truncated)
                    text.AppendLine($"(truncated at {options.MaxNodes} nodes; raise maxNodes or lower maxDepth)");

                if (!string.IsNullOrEmpty(outputPath))
                {
                    string dir = System.IO.Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(outputPath, text.ToString(), new System.Text.UTF8Encoding(false));
                }

                return CreateSuccessResponse("scene_hierarchy_dump", new JObject
                {
                    ["scenes"] = sceneArray,
                    ["text"] = text.ToString(),
                    ["nodeCount"] = options.NodeCount,
                    ["truncated"] = options.Truncated,
                    ["outputPath"] = outputPath
                });
            }, 60000);
        }

        private static JObject Walk(Transform t, int depth, Options options, System.Text.StringBuilder text)
        {
            if (!options.IncludeInactive && !t.gameObject.activeInHierarchy) return null;
            if (options.NodeCount >= options.MaxNodes)
            {
                options.Truncated = true;
                return null;
            }
            options.NodeCount++;

            var all = t.GetComponents<Component>();
            int missing = all.Count(c => c == null);
            var names = all.Where(c => c != null).Select(c => c.GetType().Name).Distinct()
                .Where(n => options.Filter != null && options.Filter.Length > 0
                    ? options.Filter.Contains(n)
                    : !DefaultExcludedComponents.Contains(n))
                .ToArray();

            var node = new JObject
            {
                ["name"] = t.name,
                ["active"] = t.gameObject.activeSelf,
                ["components"] = new JArray(names)
            };
            var extra = new System.Text.StringBuilder();
            if (missing > 0)
            {
                node["missingScripts"] = missing;
                extra.Append($" missing={missing}");
            }
            if (PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject))
            {
                node["prefabInstance"] = true;
                extra.Append(" prefab");
            }
            if (t.TryGetComponent<Camera>(out var cam))
            {
                node["camera"] = new JObject
                {
                    ["display"] = cam.targetDisplay + 1,
                    ["targetTexture"] = cam.targetTexture != null ? cam.targetTexture.name : null
                };
                extra.Append($" display={cam.targetDisplay + 1}");
                if (cam.targetTexture != null) extra.Append($" rt={cam.targetTexture.name}");
            }
            if (t.TryGetComponent<Light>(out var light))
            {
                var lightJson = new JObject { ["type"] = light.type.ToString() };
                extra.Append($" light={light.type}");
                uint? layers = GetUrpRenderingLayers(light);
                if (layers.HasValue)
                {
                    lightJson["renderingLayers"] = layers.Value;
                    extra.Append($" renderingLayers=0x{layers.Value:X}");
                }
                node["light"] = lightJson;
            }

            int childCount = t.childCount;
            text.Append(new string(' ', depth * 2)).Append("- ").Append(t.name)
                .Append(t.gameObject.activeSelf ? "" : " (inactive)")
                .Append(names.Length > 0 ? " [" + string.Join(",", names) + "]" : "")
                .Append(extra)
                .Append(childCount > 0 && depth >= options.MaxDepth ? $" (+{childCount} children)" : "")
                .AppendLine();

            if (childCount > 0)
            {
                if (depth >= options.MaxDepth)
                {
                    node["childCount"] = childCount;
                }
                else
                {
                    var children = new JArray();
                    foreach (Transform c in t)
                    {
                        var child = Walk(c, depth + 1, options, text);
                        if (child != null) children.Add(child);
                    }
                    node["children"] = children;
                }
            }
            return node;
        }

        /// <summary>
        /// URP の UniversalAdditionalLightData.renderingLayers をリフレクションで読む。
        /// URP は Light.renderingLayerMask ではなくこちらでライトの当たる対象を決める。
        /// URP を使っていないプロジェクトでもコンパイルできるよう型を直接参照しない。
        /// </summary>
        private static uint? GetUrpRenderingLayers(Light light)
        {
            foreach (var comp in light.GetComponents<Component>())
            {
                if (comp == null || comp.GetType().Name != "UniversalAdditionalLightData") continue;
                var prop = comp.GetType().GetProperty("renderingLayers");
                if (prop == null) return null;
                object value = prop.GetValue(comp);
                if (value is uint u) return u;
                // URP のバージョンによっては RenderingLayerMask 構造体で返る（value プロパティに uint を持つ）
                var valueProp = value?.GetType().GetProperty("value");
                if (valueProp != null) return Convert.ToUInt32(valueProp.GetValue(value));
                return null;
            }
            return null;
        }
    }

    internal class GetScenesHierarchyHandler : HandlerBase
    {
        public GetScenesHierarchyHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                var rootObjects = scene.GetRootGameObjects();

                var gameObjects = new JArray();
                foreach (var rootObj in rootObjects)
                {
                    gameObjects.Add(SerializeGameObject(rootObj, true));
                }

                var hierarchy = new JObject
                {
                    ["sceneName"] = scene.name,
                    ["gameObjects"] = gameObjects
                };

                return CreateSuccessResponse("scenes_hierarchy", hierarchy);
            });
        }

        private static JObject SerializeGameObject(GameObject obj, bool includeChildren)
        {
            var jsonObj = new JObject
            {
                ["name"] = obj.name,
                ["instanceId"] = obj.GetInstanceID(),
                ["isActive"] = obj.activeInHierarchy,
                ["tag"] = obj.tag,
                ["layer"] = obj.layer
            };

            if (includeChildren && obj.transform.childCount > 0)
            {
                var children = new JArray();
                for (int i = 0; i < obj.transform.childCount; i++)
                {
                    children.Add(SerializeGameObject(obj.transform.GetChild(i).gameObject, true));
                }
                jsonObj["children"] = children;
            }

            return jsonObj;
        }
    }

    internal class FindAssetsHandler : HandlerBase
    {
        public FindAssetsHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                string searchFilter = request["filter"]?.ToString();
                string searchInFolder = request["searchInFolder"]?.ToString();
                string type = request["type"]?.ToString();
                int limit = request["limit"]?.ToObject<int>() ?? 50;

                if (string.IsNullOrEmpty(searchFilter) && string.IsNullOrEmpty(type))
                    return CreateErrorResponse("missing_parameter", "filter or type is required");

                string filter = searchFilter ?? "";
                if (!string.IsNullOrEmpty(type))
                    filter = $"t:{type} {filter}".Trim();

                string[] guids = !string.IsNullOrEmpty(searchInFolder)
                    ? AssetDatabase.FindAssets(filter, new[] { searchInFolder })
                    : AssetDatabase.FindAssets(filter);

                var assets = new JArray();
                int count = Math.Min(guids.Length, limit);
                for (int i = 0; i < count; i++)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                    var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                    assets.Add(new JObject
                    {
                        ["guid"] = guids[i],
                        ["path"] = assetPath,
                        ["type"] = assetType?.Name ?? "Unknown",
                        ["name"] = System.IO.Path.GetFileNameWithoutExtension(assetPath)
                    });
                }

                return CreateSuccessResponse("assets_found", new JObject
                {
                    ["totalFound"] = guids.Length,
                    ["returned"] = count,
                    ["assets"] = assets
                });
            });
        }
    }

    internal class AddAssetToSceneHandler : HandlerBase
    {
        public AddAssetToSceneHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                string assetPath = request["assetPath"]?.ToString();
                string guid = request["guid"]?.ToString();
                string parentPath = request["parentPath"]?.ToString();
                int? parentId = request["parentId"]?.ToObject<int?>();
                var position = request["position"] as JObject;

                UnityEngine.Object asset = null;
                if (!string.IsNullOrEmpty(assetPath))
                    asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                else if (!string.IsNullOrEmpty(guid))
                    asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(AssetDatabase.GUIDToAssetPath(guid));

                if (asset == null)
                    return CreateErrorResponse("asset_not_found", "Asset not found");

                GameObject instance = PrefabUtility.InstantiatePrefab(asset) as GameObject;
                if (instance == null)
                    return CreateErrorResponse("instantiate_failed", "Failed to instantiate asset");

                if (parentId.HasValue)
                {
                    var parent = EditorUtility.InstanceIDToObject(parentId.Value) as GameObject;
                    if (parent != null) instance.transform.SetParent(parent.transform);
                }
                else if (!string.IsNullOrEmpty(parentPath))
                {
                    var parent = GameObject.Find(parentPath);
                    if (parent != null) instance.transform.SetParent(parent.transform);
                }

                if (position != null)
                {
                    instance.transform.position = new Vector3(
                        position["x"]?.ToObject<float>() ?? 0f,
                        position["y"]?.ToObject<float>() ?? 0f,
                        position["z"]?.ToObject<float>() ?? 0f
                    );
                }

                Undo.RegisterCreatedObjectUndo(instance, "Add Asset to Scene");
                Selection.activeGameObject = instance;
                return CreateSuccessResponse("asset_added", $"Added {instance.name} to scene");
            });
        }
    }

    internal class CreateMaterialHandler : HandlerBase
    {
        public CreateMaterialHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                string materialName = request["name"]?.ToString() ?? "New Material";
                string shaderName = request["shader"]?.ToString() ?? "Standard";
                string savePath = request["savePath"]?.ToString();
                var colorData = request["color"] as JObject;

                var shader = Shader.Find(shaderName);
                if (shader == null)
                    return CreateErrorResponse("shader_not_found", $"Shader not found: {shaderName}");

                var material = new Material(shader) { name = materialName };

                if (colorData != null)
                {
                    material.color = new Color(
                        colorData["r"]?.ToObject<float>() ?? 1f,
                        colorData["g"]?.ToObject<float>() ?? 1f,
                        colorData["b"]?.ToObject<float>() ?? 1f,
                        colorData["a"]?.ToObject<float>() ?? 1f
                    );
                }

                if (string.IsNullOrEmpty(savePath))
                    savePath = $"Assets/{materialName}.mat";

                string directory = System.IO.Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                    System.IO.Directory.CreateDirectory(directory);

                AssetDatabase.CreateAsset(material, savePath);
                AssetDatabase.SaveAssets();

                return CreateSuccessResponse("material_created", new JObject
                {
                    ["name"] = materialName,
                    ["shader"] = shaderName,
                    ["path"] = savePath,
                    ["guid"] = AssetDatabase.AssetPathToGUID(savePath)
                });
            });
        }
    }

    internal class GetMaterialPropertiesHandler : HandlerBase
    {
        public GetMaterialPropertiesHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                string assetPath = request["assetPath"]?.ToString();
                string guid = request["guid"]?.ToString();

                Material material = LoadMaterial(assetPath, guid);
                if (material == null)
                    return CreateErrorResponse("material_not_found", "Material not found. Specify assetPath or guid.");

                var shader = material.shader;
                int propertyCount = shader.GetPropertyCount();

                var properties = new JArray();
                for (int i = 0; i < propertyCount; i++)
                {
                    string propName = shader.GetPropertyName(i);
                    var propType = shader.GetPropertyType(i);
                    string description = shader.GetPropertyDescription(i);
                    var flags = shader.GetPropertyFlags(i);

                    var propObj = new JObject
                    {
                        ["name"] = propName,
                        ["description"] = description,
                        ["type"] = propType.ToString(),
                        ["flags"] = flags.ToString()
                    };

                    switch (propType)
                    {
                        case ShaderPropertyType.Color:
                            var color = material.GetColor(propName);
                            propObj["value"] = new JObject
                            {
                                ["r"] = color.r, ["g"] = color.g,
                                ["b"] = color.b, ["a"] = color.a
                            };
                            break;
                        case ShaderPropertyType.Vector:
                            var vec = material.GetVector(propName);
                            propObj["value"] = new JObject
                            {
                                ["x"] = vec.x, ["y"] = vec.y,
                                ["z"] = vec.z, ["w"] = vec.w
                            };
                            break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                            propObj["value"] = material.GetFloat(propName);
                            if (propType == ShaderPropertyType.Range)
                            {
                                var range = shader.GetPropertyRangeLimits(i);
                                propObj["min"] = range.x;
                                propObj["max"] = range.y;
                            }
                            break;
                        case ShaderPropertyType.Texture:
                            var tex = material.GetTexture(propName);
                            if (tex != null)
                            {
                                string texPath = AssetDatabase.GetAssetPath(tex);
                                propObj["value"] = new JObject
                                {
                                    ["name"] = tex.name,
                                    ["path"] = texPath,
                                    ["type"] = tex.GetType().Name
                                };
                                var offset = material.GetTextureOffset(propName);
                                var scale = material.GetTextureScale(propName);
                                propObj["offset"] = new JObject { ["x"] = offset.x, ["y"] = offset.y };
                                propObj["scale"] = new JObject { ["x"] = scale.x, ["y"] = scale.y };
                            }
                            else
                            {
                                propObj["value"] = null;
                            }
                            break;
                        case ShaderPropertyType.Int:
                            propObj["value"] = material.GetInteger(propName);
                            break;
                    }

                    properties.Add(propObj);
                }

                // Collect enabled keywords
                var keywords = new JArray();
                foreach (var keyword in material.enabledKeywords)
                    keywords.Add(keyword.name);

                var result = new JObject
                {
                    ["name"] = material.name,
                    ["shader"] = shader.name,
                    ["renderQueue"] = material.renderQueue,
                    ["passCount"] = shader.passCount,
                    ["enabledKeywords"] = keywords,
                    ["properties"] = properties
                };

                return CreateSuccessResponse("material_properties", result);
            });
        }

        private static Material LoadMaterial(string assetPath, string guid)
        {
            if (!string.IsNullOrEmpty(assetPath))
                return AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (!string.IsNullOrEmpty(guid))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    return AssetDatabase.LoadAssetAtPath<Material>(path);
            }
            return null;
        }
    }

    internal class SetMaterialPropertyHandler : HandlerBase
    {
        public SetMaterialPropertyHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                string assetPath = request["assetPath"]?.ToString();
                string guid = request["guid"]?.ToString();
                string propertyName = request["propertyName"]?.ToString();
                string propertyType = request["propertyType"]?.ToString();
                var value = request["value"];

                Material material = LoadMaterial(assetPath, guid);
                if (material == null)
                    return CreateErrorResponse("material_not_found", "Material not found. Specify assetPath or guid.");

                // Shader keyword toggle
                if (string.Equals(propertyType, "Keyword", StringComparison.OrdinalIgnoreCase))
                {
                    string keyword = request["keyword"]?.ToString() ?? propertyName;
                    bool enabled = value?.ToObject<bool>() ?? true;
                    if (string.IsNullOrEmpty(keyword))
                        return CreateErrorResponse("missing_parameter", "keyword is required for Keyword type.");

                    Undo.RecordObject(material, "Set Material Keyword");
                    if (enabled)
                        material.EnableKeyword(keyword);
                    else
                        material.DisableKeyword(keyword);

                    EditorUtility.SetDirty(material);
                    AssetDatabase.SaveAssets();
                    return CreateSuccessResponse("material_keyword_set", new JObject
                    {
                        ["keyword"] = keyword,
                        ["enabled"] = enabled
                    });
                }

                // RenderQueue special property
                if (string.Equals(propertyName, "renderQueue", StringComparison.OrdinalIgnoreCase))
                {
                    int queue = value?.ToObject<int>() ?? -1;
                    Undo.RecordObject(material, "Set Material RenderQueue");
                    material.renderQueue = queue;
                    EditorUtility.SetDirty(material);
                    AssetDatabase.SaveAssets();
                    return CreateSuccessResponse("material_property_set", new JObject
                    {
                        ["property"] = "renderQueue",
                        ["value"] = queue
                    });
                }

                if (string.IsNullOrEmpty(propertyName))
                    return CreateErrorResponse("missing_parameter", "propertyName is required.");

                if (!material.HasProperty(propertyName))
                    return CreateErrorResponse("property_not_found", $"Property '{propertyName}' not found on material '{material.name}'.");

                // Auto-detect type from shader if not specified
                if (string.IsNullOrEmpty(propertyType))
                    propertyType = DetectPropertyType(material.shader, propertyName);

                if (string.IsNullOrEmpty(propertyType))
                    return CreateErrorResponse("missing_parameter", "propertyType is required (or could not be auto-detected).");

                Undo.RecordObject(material, "Set Material Property");

                string resultType = "material_property_set";
                JToken resultValue;

                switch (propertyType.ToLower())
                {
                    case "float":
                    case "range":
                    {
                        float f = value?.ToObject<float>() ?? 0f;
                        material.SetFloat(propertyName, f);
                        resultValue = f;
                        break;
                    }
                    case "int":
                    case "integer":
                    {
                        int iv = value?.ToObject<int>() ?? 0;
                        material.SetInteger(propertyName, iv);
                        resultValue = iv;
                        break;
                    }
                    case "color":
                    {
                        var colorObj = value as JObject;
                        if (colorObj == null)
                            return CreateErrorResponse("invalid_value", "Color value must be an object with r, g, b, a fields.");
                        var color = new Color(
                            colorObj["r"]?.ToObject<float>() ?? 0f,
                            colorObj["g"]?.ToObject<float>() ?? 0f,
                            colorObj["b"]?.ToObject<float>() ?? 0f,
                            colorObj["a"]?.ToObject<float>() ?? 1f
                        );
                        material.SetColor(propertyName, color);
                        resultValue = new JObject
                        {
                            ["r"] = color.r, ["g"] = color.g,
                            ["b"] = color.b, ["a"] = color.a
                        };
                        break;
                    }
                    case "vector":
                    {
                        var vecObj = value as JObject;
                        if (vecObj == null)
                            return CreateErrorResponse("invalid_value", "Vector value must be an object with x, y, z, w fields.");
                        var vec = new Vector4(
                            vecObj["x"]?.ToObject<float>() ?? 0f,
                            vecObj["y"]?.ToObject<float>() ?? 0f,
                            vecObj["z"]?.ToObject<float>() ?? 0f,
                            vecObj["w"]?.ToObject<float>() ?? 0f
                        );
                        material.SetVector(propertyName, vec);
                        resultValue = new JObject
                        {
                            ["x"] = vec.x, ["y"] = vec.y,
                            ["z"] = vec.z, ["w"] = vec.w
                        };
                        break;
                    }
                    case "texture":
                    {
                        var texObj = value as JObject;
                        string texPath = texObj?["path"]?.ToString() ?? value?.ToString();
                        string texGuid = texObj?["guid"]?.ToString();

                        Texture texture = null;
                        if (!string.IsNullOrEmpty(texPath))
                            texture = AssetDatabase.LoadAssetAtPath<Texture>(texPath);
                        else if (!string.IsNullOrEmpty(texGuid))
                        {
                            string p = AssetDatabase.GUIDToAssetPath(texGuid);
                            if (!string.IsNullOrEmpty(p))
                                texture = AssetDatabase.LoadAssetAtPath<Texture>(p);
                        }

                        // null texture is valid (clears the texture slot)
                        material.SetTexture(propertyName, texture);

                        // Optional offset/scale
                        var offsetObj = texObj?["offset"] as JObject;
                        var scaleObj = texObj?["scale"] as JObject;
                        if (offsetObj != null)
                        {
                            material.SetTextureOffset(propertyName, new Vector2(
                                offsetObj["x"]?.ToObject<float>() ?? 0f,
                                offsetObj["y"]?.ToObject<float>() ?? 0f
                            ));
                        }
                        if (scaleObj != null)
                        {
                            material.SetTextureScale(propertyName, new Vector2(
                                scaleObj["x"]?.ToObject<float>() ?? 1f,
                                scaleObj["y"]?.ToObject<float>() ?? 1f
                            ));
                        }

                        resultValue = texture != null
                            ? new JObject
                            {
                                ["name"] = texture.name,
                                ["path"] = AssetDatabase.GetAssetPath(texture)
                            }
                            : JValue.CreateNull();
                        break;
                    }
                    default:
                        return CreateErrorResponse("invalid_type", $"Unsupported property type: {propertyType}. Use: Float, Int, Color, Vector, Texture, Range, Keyword.");
                }

                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();

                return CreateSuccessResponse(resultType, new JObject
                {
                    ["material"] = material.name,
                    ["property"] = propertyName,
                    ["type"] = propertyType,
                    ["value"] = resultValue
                });
            });
        }

        private static string DetectPropertyType(Shader shader, string propertyName)
        {
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyName(i) == propertyName)
                    return shader.GetPropertyType(i).ToString();
            }
            return null;
        }

        private static Material LoadMaterial(string assetPath, string guid)
        {
            if (!string.IsNullOrEmpty(assetPath))
                return AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (!string.IsNullOrEmpty(guid))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    return AssetDatabase.LoadAssetAtPath<Material>(path);
            }
            return null;
        }
    }

    internal class ScreenshotHandler : HandlerBase
    {
        public ScreenshotHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                string savePath = request["savePath"]?.ToString();

#if UNITY_EDITOR_WIN
                return CaptureWithWin32(savePath);
#else
                return CreateErrorResponse("platform_not_supported",
                    "Screenshot capture is only supported on Windows.");
#endif
            });
        }

#if UNITY_EDITOR_WIN
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
            byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
        }

        private static IntPtr FindUnityMainWindow()
        {
            uint currentProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;

            EnumWindows((hWnd, lParam) => {
                GetWindowThreadProcessId(hWnd, out uint processId);
                if (processId != currentProcessId || !IsWindowVisible(hWnd))
                    return true;

                var sb = new System.Text.StringBuilder(256);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                if (title.Contains("Unity") && (title.Contains("-") || title.Contains("Editor")))
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }

        private string CaptureWithWin32(string savePath)
        {
            IntPtr hWnd = FindUnityMainWindow();
            if (hWnd == IntPtr.Zero)
                return CreateErrorResponse("window_not_found", "Unity Editor window not found.");

            if (!GetWindowRect(hWnd, out RECT rect))
                return CreateErrorResponse("capture_failed", "Failed to get window dimensions.");

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
                return CreateErrorResponse("capture_failed", "Invalid window dimensions.");

            IntPtr hdcScreen = GetDC(IntPtr.Zero);
            IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
            IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
            IntPtr hOld = SelectObject(hdcMem, hBitmap);

            // PW_RENDERFULLCONTENT = 2: captures even if window is occluded
            bool captured = PrintWindow(hWnd, hdcMem, 2);
            if (!captured)
            {
                // Fallback: PW_CLIENTONLY = 0
                captured = PrintWindow(hWnd, hdcMem, 0);
            }

            byte[] pngBytes = null;
            if (captured)
            {
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = width;
                bmi.bmiHeader.biHeight = -height; // top-down
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0; // BI_RGB

                byte[] bits = new byte[width * height * 4];
                GetDIBits(hdcMem, hBitmap, 0, (uint)height, bits, ref bmi, 0);

                // Convert BGRA → RGBA for Texture2D
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                var pixels = new Color32[width * height];

                for (int i = 0; i < pixels.Length; i++)
                {
                    int offset = i * 4;
                    pixels[i] = new Color32(bits[offset + 2], bits[offset + 1], bits[offset], 255);
                }

                texture.SetPixels32(pixels);
                texture.Apply();
                pngBytes = texture.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(texture);
            }

            // Cleanup GDI resources
            SelectObject(hdcMem, hOld);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);

            if (pngBytes == null)
                return CreateErrorResponse("capture_failed", "Failed to capture window content.");

            // Save to file
            if (string.IsNullOrEmpty(savePath))
            {
                string directory = "Assets/Screenshots";
                if (!System.IO.Directory.Exists(directory))
                    System.IO.Directory.CreateDirectory(directory);
                savePath = $"{directory}/Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            }

            string dir = System.IO.Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            System.IO.File.WriteAllBytes(savePath, pngBytes);
            EditorApplication.delayCall += () => AssetDatabase.Refresh();

            string base64 = Convert.ToBase64String(pngBytes);

            return CreateSuccessResponse("screenshot_taken", new JObject
            {
                ["path"] = savePath,
                ["width"] = width,
                ["height"] = height,
                ["base64"] = base64
            });
        }
#endif
    }
}
