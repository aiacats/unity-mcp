using System;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeMCP.Editor.Core.Handlers
{
    internal class ExecuteMenuItemHandler : HandlerBase
    {
        public ExecuteMenuItemHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                string menuPath = request["menuPath"]?.ToString();

                if (string.IsNullOrEmpty(menuPath))
                    return CreateErrorResponse("missing_menu_path", "Menu path is required");

                bool menuExists = Menu.GetEnabled(menuPath);
                if (!menuExists)
                    return CreateErrorResponse("menu_item_not_found", $"Menu item not found: {menuPath}");

                var startTime = DateTime.Now;
                bool wasCompiling = EditorApplication.isCompiling;
                bool result = EditorApplication.ExecuteMenuItem(menuPath);
                var executionTime = (DateTime.Now - startTime).TotalMilliseconds;

                var data = new JObject
                {
                    ["menuPath"] = menuPath,
                    ["executionTime"] = executionTime,
                    ["compilingChanged"] = EditorApplication.isCompiling != wasCompiling,
                    ["executionResult"] = result,
                    ["menuExists"] = menuExists
                };

                return result
                    ? CreateSuccessResponse("menu_item_executed", data)
                    : CreateErrorResponse("menu_execution_failed", $"Menu execution returned false: {menuPath}");
            });
        }
    }

    internal class AddPackageHandler : HandlerBase
    {
        public AddPackageHandler(MCPHttpServer server) : base(server) { }

        public override string Handle(string requestBody)
        {
            var request = JObject.Parse(requestBody);
            string source = request["source"]?.ToString();

            if (string.IsNullOrEmpty(source))
                return CreateErrorResponse("missing_source", "Source is required");

            string packageId;
            switch (source.ToLower())
            {
                case "registry":
                {
                    string packageName = request["packageName"]?.ToString();
                    string version = request["version"]?.ToString();
                    if (string.IsNullOrEmpty(packageName))
                        return CreateErrorResponse("missing_package_name", "Package name is required for registry source");
                    packageId = string.IsNullOrEmpty(version) ? packageName : $"{packageName}@{version}";
                    break;
                }
                case "github":
                {
                    string repositoryUrl = request["repositoryUrl"]?.ToString();
                    string path = request["path"]?.ToString();
                    string branch = request["branch"]?.ToString();
                    if (string.IsNullOrEmpty(repositoryUrl))
                        return CreateErrorResponse("missing_repository_url", "Repository URL is required for GitHub source");
                    packageId = repositoryUrl;
                    if (!string.IsNullOrEmpty(path)) packageId += $"?path={path}";
                    if (!string.IsNullOrEmpty(branch))
                        packageId += packageId.Contains("?") ? $"&revision={branch}" : $"#revision={branch}";
                    break;
                }
                case "disk":
                {
                    string path = request["path"]?.ToString();
                    if (string.IsNullOrEmpty(path))
                        return CreateErrorResponse("missing_path", "Path is required for disk source");
                    packageId = $"file:{path}";
                    break;
                }
                default:
                    return CreateErrorResponse("invalid_source", "Source must be 'registry', 'github', or 'disk'");
            }

            UnityEditor.PackageManager.Client.Add(packageId);
            return CreateSuccessResponse("package_added", $"Package {packageId} added to Package Manager");
        }
    }

    internal class TestRunState
    {
        private readonly object _lock = new object();
        private JArray _lastResults;
        private bool _isRunning;
        private string _status = "idle";

        public void OnTestRunCompleted(JArray results)
        {
            lock (_lock)
            {
                _lastResults = results;
                _isRunning = false;
                _status = "completed";
            }
        }

        public void StartRun()
        {
            lock (_lock)
            {
                _lastResults = null;
                _isRunning = true;
                _status = "running";
            }
        }

        public void SetError()
        {
            lock (_lock)
            {
                _isRunning = false;
                _status = "error";
            }
        }

        public bool IsRunning
        {
            get { lock (_lock) { return _isRunning; } }
        }

        public JObject GetSnapshot()
        {
            lock (_lock)
            {
                var result = new JObject
                {
                    ["status"] = _status,
                    ["isRunning"] = _isRunning
                };
                if (_lastResults != null)
                {
                    result["results"] = _lastResults;
                    result["totalCount"] = _lastResults.Count;
                    result["passedCount"] = _lastResults.Count(r => r["result"]?.ToString() == "Passed");
                    result["failedCount"] = _lastResults.Count(r => r["result"]?.ToString() == "Failed");
                }
                return result;
            }
        }
    }

    internal class RunTestsHandler : HandlerBase
    {
        private readonly TestRunState _state;

        public RunTestsHandler(MCPHttpServer server, TestRunState state) : base(server)
        {
            _state = state;
        }

        public override string Handle(string requestBody)
        {
            return ExecuteOnMainThread(() => {
                var request = JObject.Parse(requestBody);
                bool queryOnly = request["queryOnly"]?.ToObject<bool>() ?? false;

                if (queryOnly)
                    return CreateSuccessResponse("test_results", _state.GetSnapshot());

                if (_state.IsRunning)
                    return CreateErrorResponse("tests_already_running", "Tests are already running. Use queryOnly to check status.");

                string testModeStr = request["testMode"]?.ToString() ?? "EditMode";
                string testFilter = request["testFilter"]?.ToString();
                bool returnOnlyFailures = request["returnOnlyFailures"]?.ToObject<bool>() ?? true;
                bool returnWithLogs = request["returnWithLogs"]?.ToObject<bool>() ?? false;

                TestMode mode;
                switch (testModeStr.ToLower())
                {
                    case "playmode":
                    case "play":
                        mode = TestMode.PlayMode;
                        break;
                    default:
                        mode = TestMode.EditMode;
                        break;
                }

                var testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
                var callback = ScriptableObject.CreateInstance<MCPTestRunCallback>();
                callback.Init(returnOnlyFailures, returnWithLogs, _state);
                testRunnerApi.RegisterCallbacks(callback);

                _state.StartRun();

                var filter = new Filter { testMode = mode };
                if (!string.IsNullOrEmpty(testFilter))
                    filter.testNames = new[] { testFilter };

                testRunnerApi.Execute(new ExecutionSettings(filter));

                return CreateSuccessResponse("tests_started", new JObject
                {
                    ["message"] = $"Tests started in {testModeStr} mode",
                    ["testMode"] = testModeStr,
                    ["filter"] = testFilter ?? "(all)",
                    ["hint"] = "Use run_tests with queryOnly=true to check results"
                });
            });
        }
    }
}
