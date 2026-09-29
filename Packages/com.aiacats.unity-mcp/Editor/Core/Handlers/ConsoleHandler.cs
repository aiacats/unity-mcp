using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    /// <summary>
    /// Unity の Console ウィンドウの中身（UnityEditor.LogEntries）をそのまま読む。
    /// 以前は MCP 独自に Application.logMessageReceived を 100 件ためていたが、
    /// ドメインリロード（Play 投入・再コンパイル）で消え、サーバーが立ち上がるまでのログ（Play 直後の Awake/Start の例外など）が入らず、
    /// clear_console でも消えなかった。Console 本体を読めば、リロードをまたいでも残り、Console の見た目とも一致する。
    /// LogEntries は internal API なので、型やメソッドが見つからなければ理由を返して止める（別の経路へは逃げない）。
    /// </summary>
    internal static class UnityConsoleReader
    {
        private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type LogEntriesType = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
        private static readonly Type LogEntryType = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntry");
        private static readonly Type ModeType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ConsoleWindow+Mode");
        private static readonly Type ConsoleFlagsType = typeof(EditorWindow).Assembly.GetType("UnityEditor.ConsoleWindow+ConsoleFlags");

        private static readonly MethodInfo StartGettingEntries = LogEntriesType?.GetMethod("StartGettingEntries", StaticMembers);
        private static readonly MethodInfo EndGettingEntries = LogEntriesType?.GetMethod("EndGettingEntries", StaticMembers);
        private static readonly MethodInfo GetEntryInternal = LogEntriesType?.GetMethod("GetEntryInternal", StaticMembers);
        private static readonly MethodInfo SetConsoleFlag = LogEntriesType?.GetMethod("SetConsoleFlag", StaticMembers);
        private static readonly MethodInfo GetFilteringText = LogEntriesType?.GetMethod("GetFilteringText", StaticMembers);
        private static readonly MethodInfo SetFilteringText = LogEntriesType?.GetMethod("SetFilteringText", StaticMembers);
        private static readonly PropertyInfo ConsoleFlags = LogEntriesType?.GetProperty("consoleFlags", StaticMembers);

        private static readonly FieldInfo MessageField = LogEntryType?.GetField("message", InstanceMembers);
        private static readonly FieldInfo ModeField = LogEntryType?.GetField("mode", InstanceMembers);
        private static readonly FieldInfo CallstackStartField = LogEntryType?.GetField("callstackTextStartUTF16", InstanceMembers);
        private static readonly FieldInfo FileField = LogEntryType?.GetField("file", InstanceMembers);
        private static readonly FieldInfo LineField = LogEntryType?.GetField("line", InstanceMembers);

        /// <summary>見つからなかった API の名前。空なら読める</summary>
        public static string MissingApi
        {
            get
            {
                var missing = new List<string>();
                if (LogEntriesType == null) missing.Add("UnityEditor.LogEntries");
                if (LogEntryType == null) missing.Add("UnityEditor.LogEntry");
                if (ModeType == null) missing.Add("UnityEditor.ConsoleWindow+Mode");
                if (ConsoleFlagsType == null) missing.Add("UnityEditor.ConsoleWindow+ConsoleFlags");
                if (StartGettingEntries == null) missing.Add("LogEntries.StartGettingEntries");
                if (EndGettingEntries == null) missing.Add("LogEntries.EndGettingEntries");
                if (GetEntryInternal == null) missing.Add("LogEntries.GetEntryInternal");
                if (SetConsoleFlag == null) missing.Add("LogEntries.SetConsoleFlag");
                if (GetFilteringText == null) missing.Add("LogEntries.GetFilteringText");
                if (SetFilteringText == null) missing.Add("LogEntries.SetFilteringText");
                if (ConsoleFlags == null) missing.Add("LogEntries.consoleFlags");
                if (MessageField == null) missing.Add("LogEntry.message");
                if (ModeField == null) missing.Add("LogEntry.mode");
                return string.Join(", ", missing);
            }
        }

        private static int _errorMask = -1;
        private static int _warningMask = -1;

        /// <summary>Mode の名前から種類を決める（値を直書きしない。Unity の版で増減しても名前で追える）</summary>
        private static void EnsureMasks()
        {
            if (_errorMask >= 0) return;
            int error = 0, warning = 0;
            foreach (var name in Enum.GetNames(ModeType))
            {
                int value = Convert.ToInt32(Enum.Parse(ModeType, name));
                if (name.IndexOf("Warning", StringComparison.Ordinal) >= 0) warning |= value;
                else if (name.IndexOf("Error", StringComparison.Ordinal) >= 0
                    || name.IndexOf("Exception", StringComparison.Ordinal) >= 0
                    || name.IndexOf("Assert", StringComparison.Ordinal) >= 0
                    || name == "Fatal") error |= value;
            }
            _errorMask = error;
            _warningMask = warning;
        }

        private static int Flag(string name) => Convert.ToInt32(Enum.Parse(ConsoleFlagsType, name));

        /// <summary>
        /// 新しい方から offset 件を飛ばし、そこから limit 件を古い順に並べて返す（最新のログが末尾）。
        /// Console の Collapse・種類の表示切り替え・検索文字列は読む間だけ外し、元に戻す。メインスレッドで呼ぶこと。
        /// </summary>
        public static JObject Read(string logType, int limit, int offset, bool includeStackTrace)
        {
            EnsureMasks();
            int savedFlags = (int)ConsoleFlags.GetValue(null);
            string savedFilter = (string)GetFilteringText.Invoke(null, null);
            var picked = new List<JObject>();
            try
            {
                SetConsoleFlag.Invoke(null, new object[] { Flag("Collapse"), false });
                SetConsoleFlag.Invoke(null, new object[] { Flag("LogLevelLog"), true });
                SetConsoleFlag.Invoke(null, new object[] { Flag("LogLevelWarning"), true });
                SetConsoleFlag.Invoke(null, new object[] { Flag("LogLevelError"), true });
                SetFilteringText.Invoke(null, new object[] { "" });

                int count = (int)StartGettingEntries.Invoke(null, null);
                try
                {
                    var entry = Activator.CreateInstance(LogEntryType);
                    int skipped = 0;
                    for (int row = count - 1; row >= 0 && picked.Count < limit; --row)
                    {
                        if (!(bool)GetEntryInternal.Invoke(null, new object[] { row, entry })) continue;
                        int mode = (int)ModeField.GetValue(entry);
                        string type = (mode & _errorMask) != 0 ? "error" : (mode & _warningMask) != 0 ? "warning" : "info";
                        if (!string.IsNullOrEmpty(logType) && type != logType) continue;
                        if (skipped < offset) { skipped++; continue; }

                        string text = (string)MessageField.GetValue(entry) ?? "";
                        int callstackStart = CallstackStartField != null ? (int)CallstackStartField.GetValue(entry) : 0;
                        string message = callstackStart > 0 && callstackStart <= text.Length ? text.Substring(0, callstackStart) : text;
                        var item = new JObject
                        {
                            ["index"] = row,
                            ["type"] = type,
                            ["message"] = message.TrimEnd(),
                        };
                        if (includeStackTrace)
                        {
                            item["stackTrace"] = callstackStart > 0 && callstackStart < text.Length ? text.Substring(callstackStart) : "";
                            if (FileField != null) item["file"] = (string)FileField.GetValue(entry);
                            if (LineField != null) item["line"] = (int)LineField.GetValue(entry);
                        }
                        picked.Add(item);
                    }
                }
                finally
                {
                    EndGettingEntries.Invoke(null, null);
                }
            }
            finally
            {
                foreach (var name in new[] { "Collapse", "LogLevelLog", "LogLevelWarning", "LogLevelError" })
                {
                    int flag = Flag(name);
                    SetConsoleFlag.Invoke(null, new object[] { flag, (savedFlags & flag) != 0 });
                }
                SetFilteringText.Invoke(null, new object[] { savedFilter ?? "" });
            }

            picked.Reverse();
            var logs = new JArray();
            foreach (var item in picked) logs.Add(item);
            return new JObject
            {
                ["logs"] = logs,
                ["returned"] = logs.Count,
                ["order"] = "oldest first; the last item is the newest. offset counts back from the newest",
            };
        }
    }

    internal class SendConsoleLogHandler : HandlerBase
    {
        public SendConsoleLogHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            var request = JObject.Parse(requestBody);
            string message = request["message"]?.ToString() ?? "Test message";
            string type = request["type"]?.ToString()?.ToLower() ?? "info";

            switch (type)
            {
                case "error":
                    Debug.LogError($"[Claude Code MCP] {message}");
                    break;
                case "warning":
                    Debug.LogWarning($"[Claude Code MCP] {message}");
                    break;
                default:
                    Debug.Log($"[Claude Code MCP] {message}");
                    break;
            }

            return CreateSuccessResponse("console_log_sent", $"Message logged: {message}");
        }
    }

    internal class GetConsoleLogsHandler : HandlerBase
    {
        public GetConsoleLogsHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            var request = JObject.Parse(requestBody);
            string logType = request["logType"]?.ToString()?.ToLower();
            int limit = request["limit"]?.ToObject<int>() ?? 50;
            int offset = request["offset"]?.ToObject<int>() ?? 0;
            bool includeStackTrace = request["includeStackTrace"]?.ToObject<bool>() ?? true;

            if (limit < 1) limit = 1;
            if (limit > 500) limit = 500;
            if (offset < 0) offset = 0;
            if (!string.IsNullOrEmpty(logType) && logType != "info" && logType != "warning" && logType != "error")
                return CreateErrorResponse("invalid_log_type", $"logType must be info, warning or error: {logType}");

            string missing = UnityConsoleReader.MissingApi;
            if (missing.Length > 0)
                return CreateErrorResponse("console_unavailable", $"Unity の Console を読む内部 API が見つかりません（Unity の版で変わった可能性）: {missing}");

            // LogEntries はメインスレッドからしか触れない
            return ExecuteOnMainThread(() =>
                CreateSuccessResponse("console_logs", UnityConsoleReader.Read(logType, limit, offset, includeStackTrace)));
        }
    }
}
