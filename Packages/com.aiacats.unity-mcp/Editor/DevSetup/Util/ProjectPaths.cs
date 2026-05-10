using System.IO;
using UnityEngine;

namespace ClaudeCodeMCP.Editor.DevSetup.Util
{
    /// <summary>
    /// Unityプロジェクトルート / Gitルート / .mcp.json などのパスを解決する。
    /// Unityプロジェクトはサブフォルダ構成（Gitルートの直下にUnityフォルダがある）に対応する。
    /// </summary>
    internal static class ProjectPaths
    {
        public static string PackageRoot
        {
            get { return Path.GetFullPath("Packages/com.aiacats.unity-mcp"); }
        }

        public static string PackageTemplatesRoot
        {
            get { return Path.Combine(PackageRoot, "Templates~"); }
        }

        public static string UnityProjectRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
        }

        public static string AssetsRoot
        {
            get { return Application.dataPath; }
        }

        public static string DevSetupAssetsRoot
        {
            get { return Path.Combine(AssetsRoot, "Plugins", "ClaudeCodeMCP_DevSetup"); }
        }

        public static string PackagesManifestJsonPath
        {
            get { return Path.Combine(UnityProjectRoot, "Packages", "manifest.json"); }
        }

        public static string EditorConfigPath
        {
            get
            {
                string atUnityRoot = Path.Combine(UnityProjectRoot, ".editorconfig");
                if (File.Exists(atUnityRoot)) return atUnityRoot;
                string gitRoot = TryFindGitRoot();
                if (!string.IsNullOrEmpty(gitRoot))
                {
                    string atGitRoot = Path.Combine(gitRoot, ".editorconfig");
                    if (File.Exists(atGitRoot)) return atGitRoot;
                }
                return atUnityRoot;
            }
        }

        public static string McpJsonPath
        {
            get
            {
                string gitRoot = TryFindGitRoot();
                string baseDir = string.IsNullOrEmpty(gitRoot) ? UnityProjectRoot : gitRoot;
                return Path.Combine(baseDir, ".mcp.json");
            }
        }

        public static string ClaudeSettingsLocalPath
        {
            get
            {
                string gitRoot = TryFindGitRoot();
                string baseDir = string.IsNullOrEmpty(gitRoot) ? UnityProjectRoot : gitRoot;
                return Path.Combine(baseDir, ".claude", "settings.local.json");
            }
        }

        public static string PreCommitHookPath
        {
            get
            {
                string gitRoot = TryFindGitRoot();
                if (string.IsNullOrEmpty(gitRoot)) return null;
                return Path.Combine(gitRoot, ".git", "hooks", "pre-commit");
            }
        }

        public static string TryFindGitRoot()
        {
            string current = UnityProjectRoot;
            for (int i = 0; i < 16 && !string.IsNullOrEmpty(current); i++)
            {
                string dotGit = Path.Combine(current, ".git");
                if (Directory.Exists(dotGit) || File.Exists(dotGit)) return current;
                DirectoryInfo parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
            return null;
        }
    }
}
