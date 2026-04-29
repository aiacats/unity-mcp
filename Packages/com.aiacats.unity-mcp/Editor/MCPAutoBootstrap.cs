using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ClaudeCodeMCP.Editor
{
    /// <summary>
    /// Editor 起動時に Server~/node_modules の有無を確認し、
    /// 未インストールであれば自動的に `npm install` を実行する。
    /// SessionState に試行済みフラグを保存し、同一エディタセッションで二重実行しない。
    /// EditorPrefs キー "ClaudeCodeMCP.AutoInstall" が false の場合はスキップ。
    /// </summary>
    [InitializeOnLoad]
    internal static class MCPAutoBootstrap
    {
        private const string PackageRelativePath = "Packages/com.aiacats.unity-mcp";
        private const string SessionKey = "ClaudeCodeMCP.AutoInstallAttempted";
        private const string PrefsKey = "ClaudeCodeMCP.AutoInstall";

        static MCPAutoBootstrap()
        {
            if (SessionState.GetBool(SessionKey, false)) return;
            SessionState.SetBool(SessionKey, true);

            if (!EditorPrefs.GetBool(PrefsKey, true)) return;

            EditorApplication.delayCall += TryInstall;
        }

        [MenuItem("Tools/Claude Code MCP/Setup: Auto Install (force)")]
        private static void ManualInvoke()
        {
            SessionState.SetBool(SessionKey, true);
            TryInstall();
        }

        [MenuItem("Tools/Claude Code MCP/Setup: Toggle Auto Install on Editor Load")]
        private static void ToggleAutoInstall()
        {
            bool next = !EditorPrefs.GetBool(PrefsKey, true);
            EditorPrefs.SetBool(PrefsKey, next);
            Debug.Log($"[Claude Code MCP] Auto install on Editor load: {(next ? "ENABLED" : "DISABLED")}");
        }

        private static void TryInstall()
        {
            string serverPath = Path.GetFullPath(Path.Combine(PackageRelativePath, "Server~"));
            if (!Directory.Exists(serverPath))
            {
                return;
            }

            string nodeModules = Path.Combine(serverPath, "node_modules");
            string sdkMarker = Path.Combine(nodeModules, "@modelcontextprotocol", "sdk");
            if (Directory.Exists(sdkMarker))
            {
                return;
            }

            string packageJson = Path.Combine(serverPath, "package.json");
            if (!File.Exists(packageJson))
            {
                Debug.LogWarning($"[Claude Code MCP] package.json not found at {packageJson}, skip auto install.");
                return;
            }

            Debug.Log($"[Claude Code MCP] node_modules missing. Running `npm install` in {serverPath} ...");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = GetNpmCommandName(),
                    Arguments = "install --no-audit --no-fund",
                    WorkingDirectory = serverPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                var proc = Process.Start(psi);
                if (proc == null)
                {
                    Debug.LogError("[Claude Code MCP] Failed to spawn npm process.");
                    return;
                }

                proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.Log("[Claude Code MCP/npm] " + e.Data); };
                proc.ErrorDataReceived  += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.LogWarning("[Claude Code MCP/npm] " + e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    if (proc.ExitCode == 0)
                    {
                        Debug.Log("[Claude Code MCP] `npm install` completed successfully.");
                    }
                    else
                    {
                        Debug.LogError($"[Claude Code MCP] `npm install` exited with code {proc.ExitCode}. " +
                                       $"Run manually: cd \"{serverPath}\" && npm install");
                    }
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Claude Code MCP] Failed to start `npm install`: {ex.Message}\n" +
                               $"Run manually: cd \"{serverPath}\" && npm install");
            }
        }

        private static string GetNpmCommandName()
        {
#if UNITY_EDITOR_WIN
            return "npm.cmd";
#else
            return "npm";
#endif
        }
    }
}
