using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;

namespace ClaudeCodeMCP.Editor.DevSetup.Util
{
    /// <summary>
    /// nuget.org から .nupkg を取得し、analyzers/dotnet/cs/*.dll を抽出する単機能フェッチャ。
    /// </summary>
    internal static class NuGetPackageFetcher
    {
        public static string MakeDownloadUrl(string packageId, string version)
        {
            string id = packageId.ToLowerInvariant();
            string ver = version.ToLowerInvariant();
            return string.Format("https://www.nuget.org/api/v2/package/{0}/{1}", id, ver);
        }

        /// <summary>
        /// .nupkg をテンポラリにDLし、analyzers/dotnet/cs/ 配下のDLLを destinationDir にコピーする。
        /// 戻り値: 抽出したDLLの絶対パス一覧。
        /// </summary>
        public static List<string> DownloadAndExtractAnalyzers(string packageId, string version, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);
            string tempNupkg = Path.Combine(Path.GetTempPath(), string.Format("{0}.{1}.nupkg", packageId, version));

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            using (WebClient wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "ClaudeCodeMCP-DevSetup");
                wc.DownloadFile(MakeDownloadUrl(packageId, version), tempNupkg);
            }

            List<string> extracted = new List<string>();
            try
            {
                using (FileStream fs = File.OpenRead(tempNupkg))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        string normalized = entry.FullName.Replace('\\', '/');
                        if (!normalized.StartsWith("analyzers/dotnet/cs/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                        string fileName = Path.GetFileName(normalized);
                        if (string.IsNullOrEmpty(fileName)) continue;
                        string outPath = Path.Combine(destinationDir, fileName);
                        using (Stream src = entry.Open())
                        using (FileStream dst = File.Create(outPath))
                        {
                            src.CopyTo(dst);
                        }
                        extracted.Add(outPath);
                    }
                }
            }
            finally
            {
                try { File.Delete(tempNupkg); } catch { }
            }

            return extracted;
        }
    }
}
