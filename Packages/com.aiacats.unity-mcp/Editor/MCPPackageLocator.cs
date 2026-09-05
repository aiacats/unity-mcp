using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace ClaudeCodeMCP.Editor
{
    /// <summary>
    /// このパッケージ自身の物理ルートを、自分の位置から解決する。
    /// Packages/ 配下（embedded / registry / file: 参照）でも、
    /// Assets/ 配下へ丸ごと配置した構成でも同じように動く。
    ///
    /// パスを "Packages/com.aiacats.unity-mcp" と直書きすると、置き場所を変えた瞬間に
    /// npm の自動インストールやサーバー起動が黙って失敗するため、解決はここへ集約する。
    /// </summary>
    internal static class MCPPackageLocator
    {
        /// <summary>パッケージルートの目印。UPM パッケージの必須ファイル。</summary>
        private const string RootMarkerFileName = "package.json";

        /// <summary>Node サーバー本体を収めたフォルダ名。末尾 ~ により Unity のインポート対象外。</summary>
        private const string ServerFolderName = "Server~";

        /// <summary>DevSetup が配布するテンプレート群のフォルダ名。同じく Unity のインポート対象外。</summary>
        private const string TemplatesFolderName = "Templates~";

        private static string _cachedRoot;
        private static bool _resolved;

        /// <summary>
        /// パッケージルートの絶対パス。解決できなければ null。
        /// </summary>
        public static string PackageRoot
        {
            get
            {
                if (!_resolved)
                {
                    _cachedRoot = Resolve();
                    _resolved = true;

                    if (string.IsNullOrEmpty(_cachedRoot))
                    {
                        Debug.LogWarning(
                            "[Claude Code MCP] パッケージルートを解決できませんでした。" +
                            $"'{RootMarkerFileName}' を含むフォルダが見つかりません。" +
                            "パッケージの一部だけをコピーしていないか確認してください。");
                    }
                }

                return _cachedRoot;
            }
        }

        /// <summary>Server~ の絶対パス。ルートを解決できなければ null。</summary>
        public static string ServerRoot
        {
            get { return CombineWithRoot(ServerFolderName); }
        }

        /// <summary>Node の MCP サーバー入口 index.js の絶対パス。ルートを解決できなければ null。</summary>
        public static string ServerEntryPoint
        {
            get
            {
                string serverRoot = ServerRoot;
                return string.IsNullOrEmpty(serverRoot) ? null : Path.Combine(serverRoot, "index.js");
            }
        }

        /// <summary>Templates~ の絶対パス。ルートを解決できなければ null。</summary>
        public static string TemplatesRoot
        {
            get { return CombineWithRoot(TemplatesFolderName); }
        }

        private static string CombineWithRoot(string relativePath)
        {
            string root = PackageRoot;
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, relativePath);
        }

        private static string Resolve()
        {
            // 1) Packages/ 配下（embedded / registry / file: 参照）は Package Manager が正確に答えられる。
            PackageInfo info = PackageInfo.FindForAssembly(typeof(MCPPackageLocator).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath) && Directory.Exists(info.resolvedPath))
            {
                return info.resolvedPath;
            }

            // 2) Assets/ 配下へ配置した構成。Packages/manifest.json と packages-lock.json を
            //    汚さずに導入したい場合に取る形で、Package Manager の管理外になる。
            //    自分自身のスクリプトをアセットとして引き当て、package.json を持つ親まで遡る。
            string[] guids = AssetDatabase.FindAssets(nameof(MCPPackageLocator) + " t:MonoScript");
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath)) continue;

                // 部分一致で同名の別スクリプトを掴まないよう、ファイル名の完全一致を要求する。
                if (!string.Equals(Path.GetFileNameWithoutExtension(assetPath), nameof(MCPPackageLocator), StringComparison.Ordinal))
                {
                    continue;
                }

                string root = FindRootUpwards(Path.GetDirectoryName(Path.GetFullPath(assetPath)));
                if (!string.IsNullOrEmpty(root)) return root;
            }

            return null;
        }

        /// <summary>
        /// 指定フォルダから親方向へ <see cref="RootMarkerFileName"/> を探す。
        /// 打ち切りは階層数の決め打ちではなく「Unity プロジェクトルートを越えたら」で判定する
        /// （パッケージは必ず &lt;projectRoot&gt;/Assets か &lt;projectRoot&gt;/Packages の下にあるため）。
        /// </summary>
        private static string FindRootUpwards(string startDirectory)
        {
            if (string.IsNullOrEmpty(startDirectory)) return null;

            string projectRoot = NormalizeDirectory(Path.Combine(Application.dataPath, ".."));
            string current = NormalizeDirectory(startDirectory);

            while (!string.IsNullOrEmpty(current)
                   && current.Length > projectRoot.Length
                   && current.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(current, RootMarkerFileName))) return current;

                DirectoryInfo parent = Directory.GetParent(current);
                if (parent == null) break;
                current = NormalizeDirectory(parent.FullName);
            }

            return null;
        }

        private static string NormalizeDirectory(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
