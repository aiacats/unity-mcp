using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.DevSetup.Util
{
    /// <summary>
    /// JSONファイルを冪等に編集するためのヘルパ。manifest.json / .mcp.json / settings.local.json で使用する。
    /// 元ファイルが存在しない場合は新規作成、既存ならパース→編集→書き戻し。インデントは4スペース固定。
    /// </summary>
    internal static class JsonFileEditor
    {
        public static JObject ReadOrEmpty(string path)
        {
            if (!File.Exists(path)) return new JObject();
            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return new JObject();
            return JObject.Parse(text);
        }

        public static void Write(string path, JObject root)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string text = root.ToString(Formatting.Indented);
            File.WriteAllText(path, text + "\n");
        }

        /// <summary>
        /// 配列に文字列を冪等追加する。重複していれば何もしない。trueなら追加された。
        /// </summary>
        public static bool EnsureStringInArray(JObject root, string arrayKey, string value)
        {
            JToken token = root[arrayKey];
            JArray array = token as JArray;
            if (array == null)
            {
                array = new JArray();
                root[arrayKey] = array;
            }
            if (array.OfType<JValue>().Any(v => v.Type == JTokenType.String && (string)v == value))
            {
                return false;
            }
            array.Add(value);
            return true;
        }

        public static bool RemoveStringFromArray(JObject root, string arrayKey, string value)
        {
            JArray array = root[arrayKey] as JArray;
            if (array == null) return false;
            JValue target = array.OfType<JValue>().FirstOrDefault(v => v.Type == JTokenType.String && (string)v == value);
            if (target == null) return false;
            target.Remove();
            if (array.Count == 0) root.Remove(arrayKey);
            return true;
        }

        /// <summary>
        /// オブジェクト配下のキーに値を冪等にセットする。既存値が同じならfalseを返す。
        /// </summary>
        public static bool EnsureObjectKey(JObject parent, string key, JToken value)
        {
            JToken existing = parent[key];
            if (existing != null && JToken.DeepEquals(existing, value)) return false;
            parent[key] = value;
            return true;
        }

        public static bool RemoveObjectKey(JObject parent, string key)
        {
            if (parent[key] == null) return false;
            parent.Remove(key);
            return true;
        }
    }
}
