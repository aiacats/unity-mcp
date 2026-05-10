# Changelog

All notable changes to the Claude Code MCP Unity Bridge package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.1.0] - 2026-04-29

### Added
- **Dev Setup ウィンドウ** (`Tools > Claude Code MCP > Dev Setup`): 開発インフラを 1 ウィンドウで管理する EditorWindow。各機能は独立してインストール／アンインストール可能（冪等）。
- **Roslyn Analyzer 自動取得** (`Editor/DevSetup/Installers/AnalyzerInstaller.cs`): `Microsoft.Unity.Analyzers` と `Roslynator.Analyzers` を NuGet から取得し、`Assets/Plugins/ClaudeCodeMCP_DevSetup/Analyzers/` に配置。`RoslynAnalyzer` ラベル＋PluginImporter 設定を自動適用。重要度設定の `globalconfig` も同梱。
- **ZLogger 導入支援** (`ZLoggerInstaller`): OpenUPM スコープレジストリ＋NuGetForUnity を `Packages/manifest.json` に冪等追加し、`Assets/packages.config` に ZLogger エントリを記述。実 DLL の取得は NuGetForUnity の自動復元に委譲。
- **context7 MCP セットアップ** (`Context7McpInstaller`): プロジェクトの `.mcp.json` と `.claude/settings.local.json` の `enabledMcpjsonServers` に context7 を冪等登録。
- **テスト雛形ジェネレータ** (`TestAsmdefGenerator`): `Assets/Tests/EditMode` と `Assets/Tests/PlayMode` に asmdef とサンプルテストを生成（既存ファイルは保持）。
- **pre-commit フック** (`PreCommitHookInstaller`): `.git/hooks/pre-commit` にステージ済み `.cs` を `dotnet format` するブロックをマーカ囲みで冪等挿入。アンインストール時はマーカ部のみ削除し他フックを保持。
- ユーティリティ群: `ProjectPaths`（Unity/Git ルート解決） / `JsonFileEditor`（Newtonsoft.Json による冪等編集） / `MarkerBlockEditor`（テキストブロック冪等編集） / `NuGetPackageFetcher`（.nupkg DL + analyzer DLL 抽出）。
- `Templates~/`: pre-commit.sh / EditMode・PlayMode asmdef テンプレート / サンプルテスト雛形。

### Pinned versions
- Microsoft.Unity.Analyzers: 1.23.0
- Roslynator.Analyzers: 4.12.9
- NuGetForUnity: 4.5.0
- ZLogger: 2.5.10
- Microsoft.Extensions.Logging: 8.0.0

## [1.0.1]

### Added
- `MCPAutoBootstrap.cs`: Editor 起動時に `Server~/node_modules` の有無を検出し、未インストールなら自動で `npm install` を実行する InitializeOnLoad スクリプトを追加。`Tools > Claude Code MCP > Setup: Toggle Auto Install on Editor Load` で無効化可能。
- `Tools > Claude Code MCP > Setup: Auto Install (force)` メニューを追加（手動再実行用）。

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