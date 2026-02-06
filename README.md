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
3. Enter: `https://github.com/YOUR_USERNAME/unity-mcp.git`

### Via Package Manager Manifest

Add to your `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.komao.unity-mcp": "https://github.com/YOUR_USERNAME/unity-mcp.git"
  }
}
```

### Via Git Submodule

If you prefer to manage the package as a Git submodule:

```bash
# Add as submodule
git submodule add https://github.com/YOUR_USERNAME/unity-mcp.git Packages/com.komao.unity-mcp

# Initialize submodules (for cloned projects)
git submodule update --init --recursive
```

### Local Installation

1. Clone or download this repository
2. Copy the package folder to your project's `Packages/` directory
3. Unity will automatically detect and import the package

## Setup

### 1. Install Node.js Dependencies

Navigate to the `Server` directory and install dependencies:

```bash
cd Packages/com.komao.unity-mcp/Server
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
      "args": [
        "Packages/com.komao.unity-mcp/Server/index.js"
      ],
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

- *"Create a new GameObject named 'Player' in the scene"*
- *"Add a Rigidbody component to the selected GameObject"*
- *"Show me the current scene hierarchy"*
- *"Check for compilation errors"*
- *"Trigger a hot reload"*

### Available Tools

#### GameObject Manipulation
- **select_gameobject**: Select objects in hierarchy
- **update_gameobject**: Modify GameObject properties
- **update_component**: Add/modify components

#### Development Workflow
- **send_console_log**: Send messages to Unity Console
- **get_console_logs**: Retrieve console messages
- **hot_reload**: Trigger script recompilation
- **force_compilation**: Force full compilation
- **get_compilation_errors**: Get build errors and warnings

#### Scene & Asset Management
- **execute_menu_item**: Run Unity menu commands
- **add_package**: Install packages via Package Manager
- **add_asset_to_scene**: Instantiate prefabs and assets

#### Monitoring & Debugging
- **check_compilation_status**: Get real-time compilation status
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
