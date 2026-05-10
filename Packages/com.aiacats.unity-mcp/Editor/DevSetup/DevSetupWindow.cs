using System;
using ClaudeCodeMCP.Editor.DevSetup.Installers;
using UnityEditor;
using UnityEngine;

namespace ClaudeCodeMCP.Editor.DevSetup
{
    /// <summary>
    /// 開発インフラ（Roslyn Analyzers / ZLogger / context7 MCP / Test asmdef / pre-commit）の
    /// インストール状況を一覧し、個別にインストール／アンインストールできるウィンドウ。
    /// </summary>
    public class DevSetupWindow : EditorWindow
    {
        private Vector2 scroll;

        [MenuItem("Tools/Claude Code MCP/Dev Setup")]
        public static void Open()
        {
            DevSetupWindow window = GetWindow<DevSetupWindow>(false, "MCP Dev Setup", true);
            window.minSize = new Vector2(520, 480);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Claude Code MCP - Dev Setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "各機能は冪等インストール／アンインストール可能。アンインストール時、編集された外部設定は\n" +
                "極力元に戻すが、NuGetForUnity が取得した DLL や Assets/Tests のユーザ追記等は保持される。",
                MessageType.Info);

            scroll = EditorGUILayout.BeginScrollView(scroll);

            DrawSection(
                "Roslyn Analyzers (Microsoft.Unity.Analyzers + Roslynator)",
                "NuGet から analyzers/dotnet/cs/*.dll を Assets/Plugins/ClaudeCodeMCP_DevSetup/Analyzers/ に取得し、" +
                "RoslynAnalyzer ラベルを付けて Unity に認識させる。globalconfig も同梱。",
                AnalyzerInstaller.IsInstalled,
                () => Run("Install Analyzers", AnalyzerInstaller.Install),
                () => Run("Uninstall Analyzers", AnalyzerInstaller.Uninstall));

            DrawSection(
                "ZLogger (via NuGetForUnity)",
                "OpenUPM スコープレジストリ + NuGetForUnity を Packages/manifest.json に追加し、" +
                "Assets/packages.config に ZLogger エントリを書き込む。実DLL取得は NuGetForUnity の自動復元に委譲。",
                ZLoggerInstaller.IsInstalled,
                () => Run("Install ZLogger", ZLoggerInstaller.Install),
                () => Run("Uninstall ZLogger", ZLoggerInstaller.Uninstall));

            DrawSection(
                "context7 MCP server",
                ".mcp.json に context7 エントリを追加し、.claude/settings.local.json の enabledMcpjsonServers に登録する。" +
                "Claude Code の再起動で有効化。",
                Context7McpInstaller.IsInstalled,
                () => Run("Install context7", Context7McpInstaller.Install),
                () => Run("Uninstall context7", Context7McpInstaller.Uninstall));

            DrawSection(
                "Test scaffold (EditMode + PlayMode asmdef)",
                "Assets/Tests/EditMode と Assets/Tests/PlayMode に asmdef とサンプルテストを生成する。既存ファイルは保持。",
                TestAsmdefGenerator.IsInstalled,
                () => Run("Generate Tests", TestAsmdefGenerator.Install),
                () => Run("Uninstall Tests (no-op)", TestAsmdefGenerator.Uninstall));

            DrawSection(
                "pre-commit hook (dotnet format)",
                ".git/hooks/pre-commit にステージ済み .cs に dotnet format を当てるブロックを冪等挿入。マーカで囲んで安全に削除可能。",
                PreCommitHookInstaller.IsInstalled,
                () => Run("Install Hook", PreCommitHookInstaller.Install),
                () => Run("Uninstall Hook", PreCommitHookInstaller.Uninstall));

            EditorGUILayout.EndScrollView();
        }

        private void DrawSection(string title, string description, Func<bool> isInstalled, Action onInstall, Action onUninstall)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                bool installed = false;
                try { installed = isInstalled(); } catch { /* ignore status probe failures */ }
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField(installed ? "[Installed]" : "[Not installed]",
                        installed ? EditorStyles.miniLabel : EditorStyles.miniLabel,
                        GUILayout.Width(110));
                }
                EditorGUILayout.LabelField(description, EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(installed ? "Reinstall / Update" : "Install", GUILayout.Width(160)))
                    {
                        onInstall();
                        Repaint();
                    }
                    if (GUILayout.Button("Uninstall", GUILayout.Width(120)))
                    {
                        onUninstall();
                        Repaint();
                    }
                }
            }
            EditorGUILayout.Space(4);
        }

        private delegate void InstallerOp(out string summary);

        private static void Run(string label, InstallerOp op)
        {
            try
            {
                string summary;
                op(out summary);
                Debug.Log("[Claude Code MCP / DevSetup] " + label + "\n" + summary);
                EditorUtility.DisplayDialog(label, summary, "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError("[Claude Code MCP / DevSetup] " + label + " failed: " + ex);
                EditorUtility.DisplayDialog(label + " failed", ex.Message, "OK");
            }
        }
    }
}
