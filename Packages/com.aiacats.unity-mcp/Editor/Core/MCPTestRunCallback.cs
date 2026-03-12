using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor.TestTools.TestRunner.Api;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.Core
{
    /// <summary>
    /// Callback handler for Unity Test Runner API.
    /// Collects test results and reports them back to the MCP server.
    /// </summary>
    internal class MCPTestRunCallback : ScriptableObject, ICallbacks
    {
        private bool _returnOnlyFailures;
        private bool _returnWithLogs;
        private List<JObject> _results = new List<JObject>();

        public void Init(bool returnOnlyFailures, bool returnWithLogs)
        {
            _returnOnlyFailures = returnOnlyFailures;
            _returnWithLogs = returnWithLogs;
            _results = new List<JObject>();
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
            {
                resultsArray.Add(r);
            }

            MCPUnityServer.OnTestRunCompleted(resultsArray);
        }

        public void TestStarted(ITestAdaptor test)
        {
            // Only log leaf tests, not suites
            if (!test.IsSuite)
            {
                Debug.Log($"[Claude Code MCP] Running test: {test.FullName}");
            }
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            // Skip suites, only collect leaf test results
            if (result.Test.IsSuite) return;

            string testResult = result.TestStatus.ToString();

            // Skip passed tests if returnOnlyFailures
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
            {
                entry["stackTrace"] = result.StackTrace ?? "";
            }

            if (_returnWithLogs && !string.IsNullOrEmpty(result.Output))
            {
                entry["output"] = result.Output;
            }

            _results.Add(entry);
        }
    }
}
