using System.IO;
using ClaudeCodeMCP.Editor.DevSetup.Util;
using UnityEditor;

namespace ClaudeCodeMCP.Editor.DevSetup.Installers
{
    /// <summary>
    /// Assets/Tests/EditMode と Assets/Tests/PlayMode を雛形付きで生成する。
    /// 既存ファイルは上書きしない（冪等）。
    /// </summary>
    internal static class TestAsmdefGenerator
    {
        public static string TestsRoot
        {
            get { return Path.Combine(ProjectPaths.AssetsRoot, "Tests"); }
        }

        public static string EditModeRoot
        {
            get { return Path.Combine(TestsRoot, "EditMode"); }
        }

        public static string PlayModeRoot
        {
            get { return Path.Combine(TestsRoot, "PlayMode"); }
        }

        public static bool IsInstalled()
        {
            return File.Exists(Path.Combine(EditModeRoot, "Tests.EditMode.asmdef"))
                && File.Exists(Path.Combine(PlayModeRoot, "Tests.PlayMode.asmdef"));
        }

        public static void Install(out string summary)
        {
            string templates = ProjectPaths.PackageTemplatesRoot;

            Directory.CreateDirectory(EditModeRoot);
            CopyIfMissing(Path.Combine(templates, "EditMode.asmdef.template"),
                Path.Combine(EditModeRoot, "Tests.EditMode.asmdef"));
            CopyIfMissing(Path.Combine(templates, "SampleEditModeTest.cs.template"),
                Path.Combine(EditModeRoot, "SampleEditModeTest.cs"));

            Directory.CreateDirectory(PlayModeRoot);
            CopyIfMissing(Path.Combine(templates, "PlayMode.asmdef.template"),
                Path.Combine(PlayModeRoot, "Tests.PlayMode.asmdef"));
            CopyIfMissing(Path.Combine(templates, "SamplePlayModeTest.cs.template"),
                Path.Combine(PlayModeRoot, "SamplePlayModeTest.cs"));

            AssetDatabase.Refresh();
            summary = "Test scaffold generated under Assets/Tests/{EditMode,PlayMode}.";
        }

        public static void Uninstall(out string summary)
        {
            if (!Directory.Exists(TestsRoot))
            {
                summary = "Assets/Tests does not exist; nothing to remove.";
                return;
            }
            summary =
                "Skipped automatic deletion of Assets/Tests to avoid losing user-written tests.\n" +
                "Delete Assets/Tests manually if you no longer need it.";
        }

        private static void CopyIfMissing(string templatePath, string destinationPath)
        {
            if (File.Exists(destinationPath)) return;
            if (!File.Exists(templatePath)) return;
            File.Copy(templatePath, destinationPath);
        }
    }
}
