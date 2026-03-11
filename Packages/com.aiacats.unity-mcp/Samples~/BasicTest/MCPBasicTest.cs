using UnityEngine;
using System;

namespace ClaudeCodeMCP.Samples
{
    /// <summary>
    /// Basic test component to demonstrate MCP functionality
    /// This component can be controlled via Claude Code MCP
    /// </summary>
    public class MCPBasicTest : MonoBehaviour
    {
        [Header("MCP Test Settings")]
        [SerializeField] private float testValue = 1.0f;
        [SerializeField] private string testMessage = "Hello from MCP!";
        [SerializeField] private bool isTestActive = true;
        [SerializeField] private Color testColor = Color.white;
        
        [Header("Auto Test")]
        [SerializeField] private bool enableAutoTest = false;
        [SerializeField] private float autoTestInterval = 5f;
        
        private float lastAutoTestTime = 0f;
        private int testCounter = 0;

        void Start()
        {
            LogTestMessage("MCPBasicTest component started", "info");
        }

        void Update()
        {
            if (enableAutoTest && Time.time - lastAutoTestTime > autoTestInterval)
            {
                PerformAutoTest();
                lastAutoTestTime = Time.time;
            }
        }

        [ContextMenu("Log Test Message")]
        public void LogTestMessage()
        {
            LogTestMessage($"Test message #{testCounter}: {testMessage}", "info");
            testCounter++;
        }

        [ContextMenu("Log Warning")]
        public void LogWarning()
        {
            LogTestMessage($"Test warning #{testCounter}: This is a warning from {gameObject.name}", "warning");
            testCounter++;
        }

        [ContextMenu("Log Error")]
        public void LogError()
        {
            LogTestMessage($"Test error #{testCounter}: This is an error from {gameObject.name} (not a real error)", "error");
            testCounter++;
        }

        [ContextMenu("Perform Full Test")]
        public void PerformFullTest()
        {
            LogTestMessage("=== Starting MCP Basic Test ===", "info");
            LogTestMessage($"GameObject: {gameObject.name}", "info");
            LogTestMessage($"Transform Position: {transform.position}", "info");
            LogTestMessage($"Test Value: {testValue}", "info");
            LogTestMessage($"Test Message: {testMessage}", "info");
            LogTestMessage($"Is Test Active: {isTestActive}", "info");
            LogTestMessage($"Test Color: {testColor}", "info");
            LogTestMessage("=== MCP Basic Test Complete ===", "info");
            testCounter++;
        }

        private void PerformAutoTest()
        {
            LogTestMessage($"Auto Test #{testCounter} - MCP system operational at {DateTime.Now:HH:mm:ss}", "info");
            testCounter++;
        }

        private void LogTestMessage(string message, string logType = "info")
        {
            string prefix = "[MCP BasicTest]";
            string fullMessage = $"{prefix} {message}";

            switch (logType.ToLower())
            {
                case "warning":
                    Debug.LogWarning(fullMessage);
                    break;
                case "error":
                    Debug.LogError(fullMessage);
                    break;
                default:
                    Debug.Log(fullMessage);
                    break;
            }
        }

        // Methods that can be called via MCP update_component tool
        public void SetTestValue(float value)
        {
            testValue = value;
            LogTestMessage($"Test value updated to: {testValue}", "info");
        }

        public void SetTestMessage(string message)
        {
            testMessage = message;
            LogTestMessage($"Test message updated to: {testMessage}", "info");
        }

        public void SetTestActive(bool active)
        {
            isTestActive = active;
            LogTestMessage($"Test active state updated to: {isTestActive}", "info");
        }

        public void SetTestColor(Color color)
        {
            testColor = color;
            LogTestMessage($"Test color updated to: {testColor}", "info");
            
            // Apply color to renderer if available
            var renderer = GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = testColor;
            }
        }

        public void RandomizeValues()
        {
            testValue = UnityEngine.Random.Range(0f, 100f);
            testMessage = $"Random message {UnityEngine.Random.Range(1000, 9999)}";
            isTestActive = UnityEngine.Random.value > 0.5f;
            testColor = new Color(
                UnityEngine.Random.value,
                UnityEngine.Random.value,
                UnityEngine.Random.value,
                1f
            );
            
            LogTestMessage("Values randomized!", "info");
        }
    }
}