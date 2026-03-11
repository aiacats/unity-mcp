# Changelog

All notable changes to the Claude Code MCP Unity Bridge package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2025-01-07

### Added
- Initial release of Claude Code MCP Unity Bridge
- HTTP server integration for Unity Editor (MCPUnityServer.cs)
- Node.js MCP server bridge (Server/index.js)
- Comprehensive tool set for Unity control:
  - GameObject manipulation (select, update, create)
  - Component management (add, update, configure)
  - Console log integration (send, retrieve)
  - Development workflow tools (hot reload, compilation)
  - Scene hierarchy access
  - Menu item execution
  - Package Manager integration
- Unity Editor control panel (Tools > Claude Code MCP > Control Panel)
- Built-in testing tools and diagnostics
- Automatic server startup and health monitoring
- Support for Unity 2021.3+
- Comprehensive documentation and samples

### Features
- **🔗 Direct Unity Control**: Complete GameObject and component manipulation
- **📝 Console Integration**: Bidirectional console log communication
- **🔄 Hot Reload**: Script recompilation and asset refresh triggers
- **📊 Real-time Monitoring**: Compilation status and error reporting
- **🎯 Menu Automation**: Programmatic Unity menu item execution
- **📦 Package Management**: Unity Package Manager integration
- **🏗️ Scene Management**: Full scene hierarchy read/write access

### Dependencies
- com.unity.nuget.newtonsoft-json: 3.2.1
- Node.js 16+ (for MCP server bridge)

### Compatibility
- Unity 2021.3 or later
- Windows, macOS, Linux
- Claude Code with MCP support

## [Unreleased]

### Planned
- Advanced debugging tools
- Multi-scene support
- Custom tool registration API
- Performance profiling integration
- Asset import/export automation
- Build pipeline integration