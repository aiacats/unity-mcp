using System.Collections.Generic;
using UnityEngine;
using UnityEditor.TestTools.TestRunner.Api;
using Newtonsoft.Json.Linq;
using ClaudeCodeMCP.Editor.Core.Handlers;

namespace ClaudeCodeMCP.Editor.Core
{
    internal class MCPTestRunCallback : ScriptableObject, ICallbacks
    {
        private bool _returnOnlyFailures;
        private bool _returnWithLogs;
        private List<JObject> _results = new List<JObject>();
        private TestRunState _state;

        public void Init(bool returnOnlyFailures, bool returnWithLogs, TestRunState state)
        {
            _returnOnlyFailures = returnOnlyFailures;
            _returnWithLogs = returnWithLogs;
            _results = new List<JObject>();
            _state = state;
        }

        public void RunStarted(ITestAdaptor testsToRun)
        {
            Debug.Log($"[Claude Code MCP] Test run started: {testsToRun.Name}");
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            Debug.Log($"[Claude Code MCP] Test run finished: {result.TestStatus} " +
                      $"(Passed: {result.PassCount}, Failed: {result.FailCount}, Skipped: {result.SkipCount})");

            var resultsArray = new JArray();
            foreach (var r in _results)
                resultsArray.Add(r);

            _state.OnTestRunCompleted(resultsArray);
        }

        public void TestStarted(ITestAdaptor test)
        {
            if (!test.IsSuite)
                Debug.Log($"[Claude Code MCP] Running test: {test.FullName}");
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            if (result.Test.IsSuite) return;

            string testResult = result.TestStatus.ToString();
            if (_returnOnlyFailures && testResult == "Passed") return;

            var entry = new JObject
            {
                ["name"] = result.Test.Name,
                ["fullName"] = result.FullName,
                ["result"] = testResult,
                ["duration"] = result.Duration,
                ["message"] = result.Message ?? ""
            };

            if (testResult == "Failed")
                entry["stackTrace"] = result.StackTrace ?? "";

            if (_returnWithLogs && !string.IsNullOrEmpty(result.Output))
                entry["output"] = result.Output;

            _results.Add(entry);
        }
    }
}
