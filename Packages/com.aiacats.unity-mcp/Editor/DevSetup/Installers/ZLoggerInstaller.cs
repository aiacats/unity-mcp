using System.IO;
using ClaudeCodeMCP.Editor.DevSetup.Util;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace ClaudeCodeMCP.Editor.DevSetup.Installers
{
    /// <summary>
    /// NuGetForUnity を OpenUPM スコープレジストリ経由で導入し、
    /// Assets/packages.config に ZLogger 系の依存を冪等に追加する。
    /// 実際の DLL 取得は Unity 起動後に NuGetForUnity の自動復元に委譲する。
    /// </summary>
    internal static class ZLoggerInstaller
    {
        public const string OpenUpmRegistryName = "OpenUPM";
        public const string OpenUpmRegistryUrl = "https://package.openupm.com";
        public const string NuGetForUnityScope = "com.github-glitchenzo.nugetforunity";
        public const string NuGetForUnityVersion = "4.5.0";

        public const string ZLoggerVersion = "2.5.10";
        public const string MsExtLoggingVersion = "8.0.0";

        public static string PackagesConfigPath
        {
            get { return Path.Combine(ProjectPaths.AssetsRoot, "packages.config"); }
        }

        public static bool IsInstalled()
        {
            JObject manifest = JsonFileEditor.ReadOrEmpty(ProjectPaths.PackagesManifestJsonPath);
            JObject deps = manifest["dependencies"] as JObject;
            bool hasNuGetForUnity = deps != null && deps[NuGetForUnityScope] != null;
            bool hasZLoggerEntry = File.Exists(PackagesConfigPath) && File.ReadAllText(PackagesConfigPath).Contains("ZLogger");
            return hasNuGetForUnity && hasZLoggerEntry;
        }

        public static void Install(out string summary)
        {
            EnsureScopedRegistry();
            EnsureNuGetForUnityDependency();
            EnsureZLoggerInPackagesConfig();
            AssetDatabase.Refresh();
            summary =
                "OpenUPM scoped registry, NuGetForUnity dependency, and packages.config updated.\n" +
                "After Unity reloads, NuGetForUnity will auto-restore ZLogger and its dependencies.\n" +
                "If restore does not start automatically, open Window > NuGet > Restore Packages.";
        }

        public static void Uninstall(out string summary)
        {
            JObject manifest = JsonFileEditor.ReadOrEmpty(ProjectPaths.PackagesManifestJsonPath);
            JObject deps = manifest["dependencies"] as JObject;
            bool removedDep = deps != null && JsonFileEditor.RemoveObjectKey(deps, NuGetForUnityScope);
            bool removedScope = RemoveScopedRegistryScope(manifest);
            JsonFileEditor.Write(ProjectPaths.PackagesManifestJsonPath, manifest);

            bool removedConfig = false;
            if (File.Exists(PackagesConfigPath))
            {
                File.Delete(PackagesConfigPath);
                string meta = PackagesConfigPath + ".meta";
                if (File.Exists(meta)) File.Delete(meta);
                removedConfig = true;
            }
            AssetDatabase.Refresh();
            summary = string.Format(
                "Removed: NuGetForUnity dep={0}, scope={1}, packages.config={2}.\n" +
                "Note: DLLs already downloaded by NuGetForUnity into Assets/Packages must be removed manually.",
                removedDep, removedScope, removedConfig);
        }

        private static void EnsureScopedRegistry()
        {
            JObject manifest = JsonFileEditor.ReadOrEmpty(ProjectPaths.PackagesManifestJsonPath);
            JArray registries = manifest["scopedRegistries"] as JArray;
            if (registries == null)
            {
                registries = new JArray();
                manifest["scopedRegistries"] = registries;
            }

            JObject openUpm = null;
            foreach (JToken t in registries)
            {
                JObject o = t as JObject;
                if (o != null && (string)o["url"] == OpenUpmRegistryUrl)
                {
                    openUpm = o;
                    break;
                }
            }
            if (openUpm == null)
            {
                openUpm = new JObject
                {
                    ["name"] = OpenUpmRegistryName,
                    ["url"] = OpenUpmRegistryUrl,
                    ["scopes"] = new JArray()
                };
                registries.Add(openUpm);
            }

            JArray scopes = openUpm["scopes"] as JArray;
            if (scopes == null)
            {
                scopes = new JArray();
                openUpm["scopes"] = scopes;
            }
            bool hasScope = false;
            foreach (JToken t in scopes)
            {
                if (t.Type == JTokenType.String && (string)t == NuGetForUnityScope) { hasScope = true; break; }
            }
            if (!hasScope) scopes.Add(NuGetForUnityScope);

            JsonFileEditor.Write(ProjectPaths.PackagesManifestJsonPath, manifest);
        }

        private static bool RemoveScopedRegistryScope(JObject manifest)
        {
            JArray registries = manifest["scopedRegistries"] as JArray;
            if (registries == null) return false;
            bool removed = false;
            for (int i = registries.Count - 1; i >= 0; i--)
            {
                JObject o = registries[i] as JObject;
                if (o == null) continue;
                if ((string)o["url"] != OpenUpmRegistryUrl) continue;
                JArray scopes = o["scopes"] as JArray;
                if (scopes != null)
                {
                    for (int j = scopes.Count - 1; j >= 0; j--)
                    {
                        if (scopes[j].Type == JTokenType.String && (string)scopes[j] == NuGetForUnityScope)
                        {
                            scopes[j].Remove();
                            removed = true;
                        }
                    }
                    if (scopes.Count == 0) registries[i].Remove();
                }
            }
            if (registries.Count == 0) manifest.Remove("scopedRegistries");
            return removed;
        }

        private static void EnsureNuGetForUnityDependency()
        {
            JObject manifest = JsonFileEditor.ReadOrEmpty(ProjectPaths.PackagesManifestJsonPath);
            JObject deps = manifest["dependencies"] as JObject;
            if (deps == null)
            {
                deps = new JObject();
                manifest["dependencies"] = deps;
            }
            if (deps[NuGetForUnityScope] == null)
            {
                deps[NuGetForUnityScope] = NuGetForUnityVersion;
            }
            JsonFileEditor.Write(ProjectPaths.PackagesManifestJsonPath, manifest);
        }

        private static void EnsureZLoggerInPackagesConfig()
        {
            string body =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<packages>\n" +
                "  <package id=\"ZLogger\" version=\"" + ZLoggerVersion + "\" />\n" +
                "  <package id=\"Microsoft.Extensions.Logging\" version=\"" + MsExtLoggingVersion + "\" />\n" +
                "</packages>\n";
            if (!File.Exists(PackagesConfigPath))
            {
                File.WriteAllText(PackagesConfigPath, body);
                return;
            }
            string existing = File.ReadAllText(PackagesConfigPath);
            if (existing.Contains("id=\"ZLogger\"")) return;
            int closeIdx = existing.LastIndexOf("</packages>", System.StringComparison.OrdinalIgnoreCase);
            if (closeIdx < 0)
            {
                File.WriteAllText(PackagesConfigPath, body);
                return;
            }
            string insert = "  <package id=\"ZLogger\" version=\"" + ZLoggerVersion + "\" />\n" +
                            "  <package id=\"Microsoft.Extensions.Logging\" version=\"" + MsExtLoggingVersion + "\" />\n";
            string updated = existing.Substring(0, closeIdx) + insert + existing.Substring(closeIdx);
            File.WriteAllText(PackagesConfigPath, updated);
        }
    }
}
