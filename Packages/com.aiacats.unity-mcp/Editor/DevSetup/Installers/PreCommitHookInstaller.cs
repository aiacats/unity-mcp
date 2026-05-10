using System.IO;
using ClaudeCodeMCP.Editor.DevSetup.Util;

namespace ClaudeCodeMCP.Editor.DevSetup.Installers
{
    /// <summary>
    /// .git/hooks/pre-commit にステージ済み .cs ファイルへ dotnet format を当てるブロックを冪等挿入する。
    /// 既存フックがあればマーカブロックを末尾に追記、無ければ新規作成。
    /// </summary>
    internal static class PreCommitHookInstaller
    {
        public static bool IsInstalled()
        {
            string path = ProjectPaths.PreCommitHookPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            string content = File.ReadAllText(path);
            string startMarker = MarkerBlockEditor.MakeStartMarker(MarkerBlockEditor.DefaultMarker, "#");
            return content.Contains(startMarker);
        }

        public static void Install(out string summary)
        {
            string path = ProjectPaths.PreCommitHookPath;
            if (string.IsNullOrEmpty(path))
            {
                summary = "Could not locate .git directory. Are you outside a git repository?";
                return;
            }
            string templatePath = Path.Combine(ProjectPaths.PackageTemplatesRoot, "pre-commit.sh");
            if (!File.Exists(templatePath))
            {
                summary = "Template missing: " + templatePath;
                return;
            }

            string body = File.ReadAllText(templatePath);
            bool isNewFile = !File.Exists(path);
            if (isNewFile)
            {
                string shebang = "#!/bin/sh\n";
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, shebang);
            }

            MarkerBlockEditor.EnsureBlock(path, body);

#if !UNITY_EDITOR_WIN
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = "+x \"" + path + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch { }
#endif

            summary = (isNewFile ? "Created " : "Updated ") + path +
                "\nHook will run dotnet format on staged .cs files. Requires `dotnet` on PATH.";
        }

        public static void Uninstall(out string summary)
        {
            string path = ProjectPaths.PreCommitHookPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                summary = "pre-commit hook not present; nothing to remove.";
                return;
            }
            bool removed = MarkerBlockEditor.RemoveBlock(path);
            string content = File.ReadAllText(path).TrimEnd();
            if (content == "#!/bin/sh" || string.IsNullOrWhiteSpace(content))
            {
                File.Delete(path);
                summary = "Removed entire pre-commit hook (only shebang/empty remained).";
                return;
            }
            summary = removed ? "Removed DevSetup block from " + path : "DevSetup block not found in " + path;
        }
    }
}
