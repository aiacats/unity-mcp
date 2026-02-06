using UnityEngine;
using UnityEditor;
using ClaudeCodeMCP.Editor.Core;

namespace ClaudeCodeMCP.Editor
{
    /// <summary>
    /// Displays Claude Code MCP server status in Unity Editor status bar
    /// </summary>
    [InitializeOnLoad]
    public static class ClaudeCodeMCPStatusBar
    {
        private static GUIStyle statusStyle;
        private static double lastUpdateTime;
        private const double UPDATE_INTERVAL = 1.0; // Update every second

        static ClaudeCodeMCPStatusBar()
        {
            EditorApplication.update += UpdateStatusBar;
        }

        private static void UpdateStatusBar()
        {
            // Update every second
            if (EditorApplication.timeSinceStartup - lastUpdateTime < UPDATE_INTERVAL)
                return;

            lastUpdateTime = EditorApplication.timeSinceStartup;

            // Update status bar
            if (EditorWindow.focusedWindow != null)
            {
                EditorWindow.focusedWindow.Repaint();
            }
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            // Reinitialize status style after script reload
            statusStyle = null;
        }

        // Display status in Scene view
        [UnityEditor.Callbacks.DidReloadScripts]
        private static void InitializeSceneGUI()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            DrawStatusOverlay();
        }

        private static void DrawStatusOverlay()
        {
            if (statusStyle == null)
            {
                statusStyle = new GUIStyle(EditorStyles.label);
                statusStyle.normal.textColor = Color.white;
                statusStyle.fontStyle = FontStyle.Bold;
                statusStyle.fontSize = 12;
            }

            var server = MCPUnityServer.Instance;
            bool isRunning = server.IsRunning;

            // Status display settings
            string statusText = isRunning ? "MCP: ON" : "MCP: OFF";
            Color statusColor = isRunning ? Color.green : Color.red;

            // Background box style
            GUIStyle boxStyle = new GUIStyle(GUI.skin.box);
            boxStyle.normal.background = MakeColorTexture(2, 2, new Color(0, 0, 0, 0.7f));

            // Display in top-right of Scene view
            if (SceneView.lastActiveSceneView == null) return;

            // Create a fixed position rect for the status
            Rect statusRect = new Rect(10, 10, 80, 20);

            Handles.BeginGUI();

            // Background box
            GUI.Box(statusRect, "", boxStyle);

            // Status text
            statusStyle.normal.textColor = statusColor;
            statusStyle.alignment = TextAnchor.MiddleCenter;
            GUI.Label(statusRect, statusText, statusStyle);

            Handles.EndGUI();
        }

        private static Texture2D MakeColorTexture(int width, int height, Color color)
        {
            Color[] pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }

            Texture2D texture = new Texture2D(width, height);
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }
    }

    /// <summary>
    /// Display status in Project window
    /// </summary>
    [InitializeOnLoad]
    public static class ClaudeCodeMCPProjectStatus
    {
        static ClaudeCodeMCPProjectStatus()
        {
            // EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;
        }

        private static void OnProjectWindowItemGUI(string guid, Rect selectionRect)
        {
            // Display only for the first item in Project window
            if (Event.current.type == EventType.Repaint && selectionRect.y < 50)
            {
                DrawProjectStatus();
            }
        }

        private static void DrawProjectStatus()
        {
            var server = MCPUnityServer.Instance;
            bool isRunning = server.IsRunning;

            string statusText = $"Claude Code MCP: {(isRunning ? "Running" : "Stopped")}";
            Color statusColor = isRunning ? Color.green : Color.red;

            // Display at the bottom of Project window
            Rect statusRect = new Rect(10, EditorGUIUtility.singleLineHeight * 2, 200, EditorGUIUtility.singleLineHeight);

            GUIStyle statusStyle = new GUIStyle(EditorStyles.miniLabel);
            statusStyle.normal.textColor = statusColor;
            statusStyle.fontStyle = FontStyle.Bold;

            EditorGUI.LabelField(statusRect, statusText, statusStyle);
        }
    }
}
