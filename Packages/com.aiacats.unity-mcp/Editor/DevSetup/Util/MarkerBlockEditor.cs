using System;
using System.IO;
using System.Text;

namespace ClaudeCodeMCP.Editor.DevSetup.Util
{
    /// <summary>
    /// マーカ行で囲まれたブロックをテキストファイルに冪等挿入／削除する。
    /// .editorconfig / pre-commit hook など Unity 管理外のテキストを安全に操作するために使用。
    /// </summary>
    internal static class MarkerBlockEditor
    {
        public const string DefaultMarker = "ClaudeCodeMCP DevSetup";

        public static string MakeStartMarker(string markerKey, string commentPrefix)
        {
            return string.Format("{0} === added by {1} START ===", commentPrefix, markerKey);
        }

        public static string MakeEndMarker(string markerKey, string commentPrefix)
        {
            return string.Format("{0} === {1} END ===", commentPrefix, markerKey);
        }

        /// <summary>
        /// 指定ファイルに対して冪等にブロック挿入。既にブロックがあれば中身を差し替える。
        /// </summary>
        public static void EnsureBlock(string filePath, string blockBody, string markerKey = DefaultMarker, string commentPrefix = "#")
        {
            string startMarker = MakeStartMarker(markerKey, commentPrefix);
            string endMarker = MakeEndMarker(markerKey, commentPrefix);

            string original = File.Exists(filePath) ? File.ReadAllText(filePath) : string.Empty;
            int start = original.IndexOf(startMarker, StringComparison.Ordinal);
            int end = original.IndexOf(endMarker, StringComparison.Ordinal);

            string before;
            string after;
            if (start >= 0 && end > start)
            {
                before = original.Substring(0, start);
                int endLineEnd = original.IndexOf('\n', end);
                after = endLineEnd >= 0 ? original.Substring(endLineEnd + 1) : string.Empty;
            }
            else
            {
                before = original;
                after = string.Empty;
                if (before.Length > 0 && !before.EndsWith("\n")) before += "\n";
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(before);
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
            sb.AppendLine(startMarker);
            sb.Append(blockBody);
            if (blockBody.Length > 0 && !blockBody.EndsWith("\n")) sb.Append('\n');
            sb.AppendLine(endMarker);
            sb.Append(after);

            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, sb.ToString());
        }

        /// <summary>
        /// マーカブロックを削除する。マーカがなければ何もしない。trueなら削除された。
        /// </summary>
        public static bool RemoveBlock(string filePath, string markerKey = DefaultMarker, string commentPrefix = "#")
        {
            if (!File.Exists(filePath)) return false;

            string startMarker = MakeStartMarker(markerKey, commentPrefix);
            string endMarker = MakeEndMarker(markerKey, commentPrefix);

            string original = File.ReadAllText(filePath);
            int start = original.IndexOf(startMarker, StringComparison.Ordinal);
            int end = original.IndexOf(endMarker, StringComparison.Ordinal);
            if (start < 0 || end < start) return false;

            int blockStart = start;
            int prevNewline = original.LastIndexOf('\n', Math.Max(0, start - 1));
            if (prevNewline >= 0) blockStart = prevNewline + 1;

            int endLineEnd = original.IndexOf('\n', end);
            int blockEnd = endLineEnd >= 0 ? endLineEnd + 1 : original.Length;

            string updated = original.Substring(0, blockStart) + original.Substring(blockEnd);
            File.WriteAllText(filePath, updated);
            return true;
        }
    }
}
