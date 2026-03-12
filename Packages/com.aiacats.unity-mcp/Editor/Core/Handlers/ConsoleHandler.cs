using System;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    internal class ConsoleLogState
    {
        private readonly List<LogEntry> _logs = new List<LogEntry>();
        private readonly object _lock = new object();
        private const int MaxLogEntries = 100;

        private class LogEntry
        {
            public string Message;
            public string StackTrace;
            public LogType Type;
            public DateTime Timestamp;
        }

        public void OnLogMessageReceived(string message, string stackTrace, LogType type)
        {
            lock (_lock)
            {
                _logs.Add(new LogEntry
                {
                    Message = message,
                    StackTrace = stackTrace,
                    Type = type,
                    Timestamp = DateTime.Now
                });

                while (_logs.Count > MaxLogEntries)
                    _logs.RemoveAt(0);
            }
        }

        public JArray GetLogs(string filterType = null, int limit = 50, int offset = 0, bool includeStackTrace = true)
        {
            var result = new JArray();
            lock (_lock)
            {
                int skipped = 0;
                int added = 0;

                foreach (var log in _logs)
                {
                    string logType = "info";
                    switch (log.Type)
                    {
                        case LogType.Error:
                        case LogType.Exception:
                        case LogType.Assert:
                            logType = "error";
                            break;
                        case LogType.Warning:
                            logType = "warning";
                            break;
                    }

                    if (!string.IsNullOrEmpty(filterType) && logType != filterType)
                        continue;

                    if (skipped < offset)
                    {
                        skipped++;
                        continue;
                    }

                    if (added >= limit) break;

                    var entry = new JObject
                    {
                        ["message"] = log.Message,
                        ["type"] = logType,
                        ["timestamp"] = log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    };

                    if (includeStackTrace)
                        entry["stackTrace"] = log.StackTrace;

                    result.Add(entry);
                    added++;
                }
            }
            return result;
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
        private readonly ConsoleLogState _state;

        public GetConsoleLogsHandler(MCPHttpServer server, ConsoleLogState state) : base(server)
        {
            _state = state;
        }

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

            return CreateSuccessResponse("console_logs", _state.GetLogs(logType, limit, offset, includeStackTrace));
        }
    }
}
