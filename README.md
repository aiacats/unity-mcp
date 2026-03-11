# Unity MCP Bridge for Claude Code

[![Unity](https://img.shields.io/badge/Unity-2021.3%2B-000000.svg?logo=unity)](https://unity3d.com/get-unity/download)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE.md)
[![MCP](https://img.shields.io/badge/Protocol-MCP-green.svg)](https://modelcontextprotocol.io/)

A Unity Package Manager (UPM) package that enables [Claude Code](https://claude.ai/code) to directly control Unity Editor through the Model Context Protocol (MCP).

## Features

- **Direct Unity Control**: Claude Code can manipulate GameObjects, components, and scene hierarchy
- **Console Integration**: Send and retrieve Unity console logs
- **Hot Reload**: Trigger script compilation and asset refresh
- **Real-time Monitoring**: Get compilation status and error information
- **Menu Automation**: Execute Unity menu items programmatically
- **Package Management**: Add packages through Package Manager
- **Scene Management**: Read and modify scene hierarchy

## Requirements

- Unity 2021.3 or later
- Node.js 16+ (for MCP server)
- Claude Code with MCP support

## Installation

### Via Unity Package Manager (UPM)

1. Open Unity Package Manager (`Window > Package Manager`)
2. Click `+` and select `Add package from git URL...`
3. Enter: `https://github.com/aiacats/unity-mcp.git?path=Packages/com.aiacats.unity-mcp`

### Via Package Manager Manifest

Add to your `Packages/manifest.json`:

```json
{
    "dependencies": {
        "com.aiacats.unity-mcp": "https://github.com/aiacats/unity-mcp.git?path=Packages/com.aiacats.unity-mcp"
    }
}
```

### Via Git Submodule

If you prefer to manage the package as a Git submodule:

```bash
# Add as submodule
git submodule add https://github.com/aiacats/unity-mcp.git

# Initialize submodules (for cloned projects)
git submodule update --init --recursive
```

### Local Installation

1. Clone or download this repository
2. Copy the package folder to your project's `Packages/` directory
3. Unity will automatically detect and import the package

## Setup

### 1. Install Node.js Dependencies

Navigate to the `Server~` directory and install dependencies:

```bash
cd Packages/com.aiacats.unity-mcp/Server~
npm install
```

### 2. Automatic Server Start

The MCP server starts automatically when Unity Editor launches. No additional configuration required!

### 3. Manual Configuration

If you need custom settings:

1. Open `Tools > Claude Code MCP > Control Panel`
2. Configure server settings as needed
3. Test connection with built-in test tools

### 4. Claude Code Configuration

Create a `.mcp.json` file in your project root:

```json
{
    "mcpServers": {
        "claude-code-mcp-unity": {
            "command": "node",
            "args": ["Packages/com.aiacats.unity-mcp/Server~/index.js"],
            "env": {
                "MCP_UNITY_HTTP_URL": "http://localhost:8090"
            }
        }
    }
}
```

## Usage

### Basic Commands

Once installed, Claude Code can control Unity using natural language:

- _"Create a new GameObject named 'Player' in the scene"_
- _"Add a Rigidbody component to the selected GameObject"_
- _"Show me the current scene hierarchy"_
- _"Check for compilation errors"_
- _"Trigger a hot reload"_

### Available Tools

#### GameObject Manipulation

- **select_gameobject**: Select objects in hierarchy
- **get_gameobject_info**: Get detailed GameObject info (Transform, components, hierarchy)
- **update_gameobject**: Modify GameObject properties (or create new)
- **delete_gameobject**: Delete a GameObject from the scene
- **update_component**: Add/modify components
- **get_component_properties**: Read all serialized properties of a component
- **remove_component**: Remove a component from a GameObject

#### Scene & Asset Management

- **save_scene**: Save the active scene
- **open_scene**: Open a scene by path (single or additive)
- **find_assets**: Search AssetDatabase with filter, type, and folder
- **add_asset_to_scene**: Instantiate prefabs and assets
- **create_material**: Create Material assets with shader and color
- **execute_menu_item**: Run Unity menu commands
- **add_package**: Install packages via Package Manager

#### Development Workflow

- **play_mode_control**: Play/Stop/Pause control and status query
- **hot_reload**: Trigger script recompilation
- **force_compilation**: Force full compilation
- **screenshot**: Capture Game view screenshots

#### Monitoring & Debugging

- **send_console_log**: Send messages to Unity Console
- **get_console_logs**: Retrieve console messages
- **check_compilation_status**: Get real-time compilation status
- **get_compilation_errors**: Get build errors and warnings
- **run_tests**: Run Unity Test Runner tests
- **Scene Hierarchy Resource**: Access complete scene structure

### Control Panel

Access the control panel via `Tools > Claude Code MCP > Control Panel`:

- **Server Status**: Monitor connection and port status
- **Server Controls**: Start/stop/restart MCP server
- **Testing Tools**: Built-in connection and functionality tests
- **Configuration**: Copy .mcp.json configuration to clipboard

## Testing

### Built-in Tests

Use the Control Panel test buttons:

1. **Ping Test**: Verify server connectivity
2. **Console Log Test**: Test message sending
3. **Scene Hierarchy Test**: Verify data retrieval
4. **Hot Reload Test**: Test compilation triggers

### Manual Testing

```bash
# Test server directly
curl http://localhost:8090/mcp/ping

# Send test message
curl -X POST -H "Content-Type: application/json" \
  -d '{"message": "Test", "type": "info"}' \
  http://localhost:8090/mcp/tools/send_console_log
```

## License

This project is licensed under the MIT License.

## Acknowledgments

- Built on the [Model Context Protocol](https://modelcontextprotocol.io/) standard
- Powered by [Claude Code](https://claude.ai/code) AI assistant
