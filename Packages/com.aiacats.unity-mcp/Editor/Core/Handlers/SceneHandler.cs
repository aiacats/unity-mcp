using System;
using System.Linq;
using UnityEngine;
using UnityEditor;
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

                if (string.IsNullOrEmpty(scenePath))
                    return CreateErrorResponse("missing_parameter", "scenePath is required");

                var currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (currentScene.isDirty && !additive && !string.IsNullOrEmpty(currentScene.path))
                {
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

    internal class ScreenshotHandler : HandlerBase
    {
        public ScreenshotHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                string savePath = request["savePath"]?.ToString();
                int superSize = request["superSize"]?.ToObject<int>() ?? 1;

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

                ScreenCapture.CaptureScreenshot(savePath, superSize);
                EditorApplication.delayCall += () => AssetDatabase.Refresh();

                return CreateSuccessResponse("screenshot_taken", new JObject
                {
                    ["path"] = savePath,
                    ["superSize"] = superSize
                });
            });
        }
    }
}
