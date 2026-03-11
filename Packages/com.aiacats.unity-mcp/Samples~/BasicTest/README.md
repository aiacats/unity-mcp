# MCP Basic Test Sample

This sample demonstrates basic MCP functionality with Unity.

## What's Included

- **MCPBasicTest.cs**: A MonoBehaviour with methods that can be controlled via Claude Code MCP
- **TestScene.unity**: A simple scene with the test component pre-configured

## How to Use

1. **Import the Sample**
   - In Unity Package Manager, find "Claude Code MCP Unity Bridge"
   - Click on the package and go to "Samples" tab
   - Import "Basic MCP Test"

2. **Open the Test Scene**
   - Navigate to `Assets/Samples/Claude Code MCP Unity Bridge/1.0.0/BasicTest/`
   - Open `TestScene.unity`

3. **Test with Claude Code**
   - Ensure MCP server is running (`Tools > Claude Code MCP > Control Panel`)
   - Ask Claude Code to interact with the test object:
     - "Show me the current scene hierarchy"
     - "Select the MCPTestObject in the scene"
     - "Update the testValue property of MCPBasicTest component to 50"
     - "Call the LogTestMessage method on the test object"

## Available Methods

The MCPBasicTest component provides these methods for MCP interaction:

### Context Menu Methods (Manual Testing)
- `LogTestMessage()`: Log an info message
- `LogWarning()`: Log a warning message
- `LogError()`: Log an error message (fake error)
- `PerformFullTest()`: Log all component information

### MCP-Controllable Methods
- `SetTestValue(float)`: Update the test value
- `SetTestMessage(string)`: Update the test message
- `SetTestActive(bool)`: Toggle active state
- `SetTestColor(Color)`: Change test color
- `RandomizeValues()`: Randomize all test values

## Example Claude Code Commands

Try these commands with Claude Code:

```
"Create a test cube and add the MCPBasicTest component to it"

"Set the testValue of the MCPBasicTest component to 42"

"Call the PerformFullTest method on the test object"

"Change the testMessage to 'Hello from Claude Code!'"

"Randomize the values on the test component"
```

## Troubleshooting

- **Component not found**: Make sure the GameObject has the MCPBasicTest component attached
- **Methods not working**: Verify the MCP server is running and connected
- **No response**: Check Unity Console for MCP-related messages