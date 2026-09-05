using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ClaudeCodeMCP.Editor.Core
{
    /// <summary>
    /// 実際にバインドできたポートと、このプロジェクトの素性を Library 配下へ書き出し、
    /// Node ブリッジ（Server~/index.js）から読めるようにする。
    ///
    /// Unity は 8090 が埋まっていれば 8091.. へ自動で逃げる（<see cref="MCPHttpServer"/> の
    /// TryStartAlternativePort）。一方 Node ブリッジは既定で 8090 へ繋ぐため、複数プロジェクトを
    /// 同時に開くと 2 つ目以降の Claude Code が「別プロジェクトの Unity」を操作してしまう。
    /// しかも応答は正常に返るので気づけない。それを防ぐための受け渡しファイル。
    ///
    /// 置き場所を Library/ にするのは、Unity が管理していて git 管理外であり、
    /// プロジェクトごとに 1 つだけ存在するため（プロジェクトの成果物を汚さない）。
    /// </summary>
    internal static class MCPEndpointFile
    {
        private const string DirectoryName = "ClaudeCodeMCP";
        private const string FileName = "endpoint.json";

        /// <summary>Unity プロジェクトルートの絶対パス（区切りは "/" に正規化）。</summary>
        public static string ProjectRoot
        {
            get { return Normalize(Path.Combine(Application.dataPath, "..")); }
        }

        public static string FilePath
        {
            get { return Path.Combine(ProjectRoot, "Library", DirectoryName, FileName); }
        }

        /// <summary>
        /// Node ブリッジと突き合わせるための素性。identity エンドポイントでも同じ内容を返す。
        /// </summary>
        public static JObject BuildIdentity(int port)
        {
            string root = ProjectRoot;
            return new JObject
            {
                ["port"] = port,
                ["projectPath"] = root,
                ["projectName"] = new DirectoryInfo(root).Name,
                ["unityVersion"] = Application.unityVersion,
                ["processId"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                ["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }

        public static void Write(int port)
        {
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, BuildIdentity(port).ToString());
            }
            catch (Exception ex)
            {
                // 書けなくてもサーバー自体は動く。ただし Node 側がポートを特定できず
                // 「別プロジェクトへ繋ぐ」事故の温床になるため、黙って捨てずに警告する。
                Debug.LogWarning($"[Claude Code MCP] endpoint.json を書き出せませんでした: {ex.Message}");
            }
        }

        public static void Delete()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Claude Code MCP] endpoint.json を削除できませんでした: {ex.Message}");
            }
        }

        /// <summary>
        /// 区切りを "/" に統一し、末尾の区切りを落とす。Node 側と同じ規則で比較できるようにする。
        /// </summary>
        private static string Normalize(string path)
        {
            return Path.GetFullPath(path).Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/');
        }
    }
}
