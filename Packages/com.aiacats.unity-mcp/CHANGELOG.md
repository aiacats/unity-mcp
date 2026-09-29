# Changelog

All notable changes to the Claude Code MCP Unity Bridge package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.5.4] - 2026-09-29

### Changed
- **`get_console_logs` は Unity の Console ウィンドウの中身（`UnityEditor.LogEntries`）を読むようにした**。
  以前は MCP 独自に `Application.logMessageReceived` を 100 件ためていたため、次の問題があった。
  - ドメインリロード（Play 投入・再コンパイル）で消え、サーバーが立ち上がるまでのログ（Play 直後の Awake / Start の例外など）が入らなかった。
    実測 2026-09-29 RemoteLipSync: Start で出た `PlatformNotSupportedException` が取れず、原因の特定が 1 往復遅れた。
  - 古い方から返していたので、新しいログが見えなかった。
  - `clear_console` で消えなかった。
- 返す順序: 新しい方から `offset` 件を飛ばし、`limit` 件を古い順に並べる（最後が最新）。
  レスポンスの `data` は `{ logs: [...], returned, order }` になり、各ログに `index`（Console の行番号）を付けた。`timestamp` は Console に無いのでなくした。
- 読む間だけ Console の Collapse・種類の表示切り替え・検索文字列を外して、読み終えたら元に戻す（表示で絞っていても全部読む）。
- `LogEntries` は internal API なので、見つからなければ `console_unavailable` と見つからなかった名前を返す（独自バッファへは逃げない）。
- 不正な `logType` は `invalid_log_type` を返す。

## [1.5.3] - 2026-09-29

### Fixed
- **Play 中に Editor がメモリを使い切って落ちる問題**。SceneView 左上の「MCP: ON」表示が、描画のたびに背景用の `Texture2D` を作って破棄していなかった。
  Play 中は SceneView が毎フレーム描き直されるため、約 1 時間でテクスチャ ID の上限（1048575）を超え、
  `Resource ID out of range` / `d3d11: failed to create 2D texture` を 8,600 万行吐いたうえで 40GB 確保して OOM でクラッシュした（実測 2026-09-29 RemoteLipSync）。
  背景のテクスチャとスタイルを 1 つだけ作って使い回すようにした。`SceneView.duringSceneGui` の多重登録も防ぐ。

## [1.5.2] - 2026-09-29

### Fixed
- **`execute_menu_item` が実在するメニューでも「Menu item not found」で実行を拒否する問題**。
  実行前の存在確認に `Menu.GetEnabled` を使っていたが、メニューの検証がまだ回っていない状態では
  組み込みの `Window/General/Console` を含む全メニューで false を返していた（Play 前後・ドメインリロード後に発生）。
  事前チェックをやめ、`EditorApplication.ExecuteMenuItem` の戻り値で判定するようにした。
  `Menu.GetEnabled` の値はレスポンスの `menuEnabled` に参考値として残す。
  実測 2026-09-29 RemoteLipSync: 修正前は全メニュー not found、修正後は実在メニューが実行され、存在しないメニューは従来どおりエラー。

## [1.5.1] - 2026-09-14

### Fixed
- **Unity Editor が背面にある間にドメインリロードすると、Editor を前面にするまで MCP サーバーが起動しない問題**。
  起動・再起動を `EditorApplication.delayCall` に積んでいたが、delayCall はエディタの更新ティックでしか実行されず、
  背面で待機中の Editor にはティックが来ない。Claude Code 側からリフレッシュ（再コンパイル）させた直後に
  「Unity の MCP サーバーが起動していません」となり、Unity をクリックするまで復帰しなかった。
- 起動・再起動の delayCall を積む箇所で `EditorApplication.QueuePlayerLoopUpdate()` を呼んで Editor を起こすようにした
  （リクエスト処理の `ExecuteOnMainThread` が既に行っている手当てと同じ）。
  実測: 背面のまま再コンパイル → 修正前は 3 分以上未起動、修正後は約 15 秒で応答。
- `Editor/Core/Handlers/ExportPackageHandler.cs.meta` がリポジトリに無く、取り込み先ごとに Unity が別 GUID で生成していた。

## [1.5.0] - 2026-09-06

### Added
- **`/mcp/identity` エンドポイント**。接続先の Unity がどのプロジェクトかを返す。
- **`Library/ClaudeCodeMCP/endpoint.json`**。実際にバインドできたポートとプロジェクトの素性を書き出す。
  `Library/` は Unity 管理下かつ git 管理外なので、プロジェクトの成果物を汚さない。
  サーバー停止時に削除するので、古いポートへ繋がせない。

### Fixed
- **複数の Unity プロジェクトを同時に開くと、別プロジェクトの Unity を操作してしまう問題**。
  Unity 側は 8090 が埋まっていれば 8091.. へ自動で逃げる（`TryStartAlternativePort`）のに対し、
  Node ブリッジは 8090 固定で繋いでいた。このため 2 つ目以降の Claude Code が
  1 つ目の Unity を掴み、しかも応答は正常に返るため気づけなかった。
  （`force_compilation` で別プロジェクトがコンパイルされ、`save_scene` で別プロジェクトの
  シーンが保存されるなど、被害が分かりにくい形で出る。）
- Node ブリッジは接続先を固定で持たず、呼び出しのたびに次の順で解決するようにした。
  1. `MCP_UNITY_HTTP_URL` が明示されていればそれ（既存構成を壊さない）
  2. 無ければ `index.js` の位置から上方向に Unity プロジェクトルートを探し、`endpoint.json` を読む
  3. どちらも取れなければ**明示エラー**（8090 への暗黙の接続はしない）
- 解決した接続先に対して毎回 `/mcp/identity` で照合し、プロジェクトパスが一致しなければ
  操作を中止する。キャッシュしないのは、Unity の起動順が変わるとポートの持ち主が入れ替わるため。

## [1.4.0] - 2026-09-06

### Changed
- **パッケージルートの解決を自己位置ベースへ変更**（`Editor/MCPPackageLocator.cs` を新設）。
  これまで `"Packages/com.aiacats.unity-mcp"` を 4 ファイルへ直書きしていたため、パッケージを
  別の場所へ置くと npm 自動インストール・サーバー起動・DevSetup が黙って失敗していた。
  解決を 1 箇所へ集約し、次の 2 経路で求める。解決できなければ警告を出す（握り潰さない）。
  - `PackageInfo.FindForAssembly` … `Packages/` 配下（embedded / registry / file: 参照）
  - 自分自身の `MonoScript` から `package.json` を持つ親フォルダまで遡る … `Assets/` 配下へ置いた構成
- これにより **`Assets/` 配下へ丸ごと置いても動作する**ようになった。
  `Packages/manifest.json` と `Packages/packages-lock.json` を一切変更せずに導入したい場合に使う。
  Unity は `Packages/` 配下のパッケージを manifest 記載なしでも認識するが、
  `packages-lock.json` には `source: "embedded"` のエントリを自動生成してしまうため。
- 既存の `Packages/` 配下での利用に変更はない（経路 1 で従来どおり解決される）。

## [1.3.0] - 2026-06-03

### Added
- **汎用エディタ自動化ツール 3 種**（`Editor/Core/Handlers/AutomationHandlers.cs`）。
  これにより、専用ハンドラを増やさずに ScriptableObject 生成・任意オブジェクト/アセットの
  フィールド設定・プロジェクト側エディタルーチンの起動が MCP から行える。
  - `invoke_method`: 任意の static / instance メソッドをリフレクション呼び出し。引数は JSON 値を
    パラメータ型へ変換（`instanceId` / `assetPath` / `guid` / `objectPath` による UnityEngine.Object 解決を含む）。
    prefab 編集（`PrefabUtility.LoadPrefabContents`/`SaveAsPrefabAsset`）などはプロジェクト側ルーチンを
    本ツールで起動する形を想定。**任意エディタコードを実行しうるためローカル開発専用**。
  - `create_asset`: `ScriptableObject.CreateInstance` + `AssetDatabase.CreateAsset`。初期フィールド値の設定可。
  - `set_object_properties`: `update_component` をアセットにも一般化。コンポーネント／単体アセット双方の
    フィールド・プロパティを設定（参照・ネスト List/POCO 対応、Undo 記録、ディスク上アセットは保存）。
  - JSON→型付き値変換ロジックを `MCPReflection` 静的ユーティリティに共有化。

## [1.2.0] - 2026-06-01

### Added
- **Play モード制御ツール**（フォーカス非依存）: `enter_play_mode` / `exit_play_mode` / `get_play_state`。
  `EditorApplication.isPlaying` をメインスレッドで操作するため、ウィンドウフォーカスやリモートデスクトップに
  左右されず確実に Play/Stop できる（`Ctrl+P` 送出の代替）。`Editor/Core/Handlers/PlayAndViewHandlers.cs`。
- **Game View Display 切替ツール**: `set_game_view_display`（0-based、0 = Display 1）。GameView 内部 API を
  リフレクションで操作し、UIDocument/PanelSettings や特定カメラが対象とする Display へ Game View を合わせられる。
- **パッケージ解決ツール**: `remove_package`（`Client.Remove`）/ `resolve_packages`（`Client.Resolve`）を追加。
  `add_package` を**メインスレッド実行に修正**（従来は listener スレッドから `Client.Add` を呼んで
  "Add can only be called from the main thread" で失敗していた）。manifest 外部編集後の再解決が、
  packages-lock.json 削除や Editor 再起動なしで行える。
- **`restart_editor`**: 現在のプロジェクトで Editor を再起動（`EditorApplication.OpenProject`）。
- **`clear_console`**: コンソールログのクリア（`UnityEditor.LogEntries.Clear`）。

### Removed
- **`screenshot` ツールを削除**。スクリーンショット系機能は WinGui MCP に委譲する方針のため、Unity MCP からは
  ツール定義（Server~/index.js）とハンドラ登録（MCPHttpServer）を撤去（Node 側の image 返却特別処理も削除）。

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