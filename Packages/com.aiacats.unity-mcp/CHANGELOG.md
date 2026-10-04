# Changelog

All notable changes to the Claude Code MCP Unity Bridge package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.6.0] - 2026-10-04

### Added
- **キャラの見え方を撮るツール群**（どの案件でも使える形に。対になる Claude Code のスキルは `unity-view-recording`）。
  - `play_animation_clip` / `stop_animation_clip`: Play 中に Animator へクリップ（.anim・FBX 内のクリップ、Humanoid 含む）を手動評価の PlayableGraph で当てる。
    評価は Animator 自身の評価（`PreLateUpdate.DirectorUpdateAnimationEnd`）の直後で、LateUpdate の補正スクリプトはその上に掛かる。
    Edit モードの PlayableGraph は Humanoid の骨に書かないので、Play 外は `not_playing` で拒否する。
  - `capture_views`: 顔の正面を基準にした複数の角度（yaw/pitch）から一時カメラで撮り、横に並べて 1 枚の PNG にする。Play 中は次のフレームのスキニング後に撮る。
  - `start_view_recording` / `wait_for_view_recording_done` / `get_view_recording_status` / `stop_view_recording`: `Time.captureFramerate` で一定 FPS の連番 PNG と `recording.json` を書く。
    `clipPath` を渡すとクリップの時刻をフレーム番号から決めて区間 [startTime, endTime) を流す（`preroll` で揺れものを先に落ち着かせられる）。
  - 撮るのは PlayerLoop の `PostLateUpdate.UpdateAllSkinnedMeshes` の直後（MonoBehaviour を置かないのでシーンに残らず、Game ビューの描画にも依存しない）。
    撮影中だけ対象の `SkinnedMeshRenderer.updateWhenOffscreen` を true にして戻す。
  - 撮影用ライトは URP の `UniversalAdditionalLightData.renderingLayers` を全ビットにする（リフレクション。URP の無い案件でもコンパイルできる）。撮る間だけ有効にする。
  - `disableComponents`（型名で一時無効化。ライブ受信が Animator を上書きするとき）と `hideObjects`（一時非表示）。対象ごとに参照数を数え、最初に借りた時点の状態へ戻す。
  - 顔の正面は既定で両目から求め、目の骨が無ければ黙って切り替えずにエラーにする（`front`: eyes / shoulders / hips / head / root / world を明示）。結果に使った方法を出す。
  - PNG 化と書き込みは専用スレッド（バッファ 4 枚を使い回し、毎フレームの部分で GC を出さない）。一時カメラ・ライト・RT は使い回して録画の終わりに遅延破棄する
    （撮影直後に DestroyImmediate すると Play 終了直後に D3D11 の BeginProfileEvent で落ちたため）。Play 終了・ドメインリロードの前にすべて止めて戻し、PlayerLoop から外す。
  - 実測 2026-10-04 SinkaiFes2026（Unity 6000.3.5f2 / URP 17）: 収録モーション 1295〜1335 秒を 2 角度 1440x720・30fps で 1200 コマ、32〜37 秒（27 ms/コマ）。
    手を口元へ上げる速いコマを拡大してマイク（手の子）と手のずれが無いことを確認。途中停止（160 コマ）と録画中の Play 終了（165 コマで aborted）でも書き切って復元した。
  - 録画が当てているクリップは play / stop_animation_clip から触れない。同じ Animator の play_animation_clip のクリップは録画の間だけ退避して戻す（lease も持ち越す）。

### Known issues
- 検証中に 1 回、Play 終了直後に Unity が `GfxDeviceWorker` の `ReleaseD3D11Buffer` で落ちた（2026-10-04 SinkaiFes2026、Play を 4 回抜けたうちの 1 回）。
  同じプロジェクトでは本ツール導入前から Play 終了直後のクラッシュ（`BeginProfileEvent`）が出ており、因果は未確定。

## [1.5.5] - 2026-10-04

### Added
- **`dump_scene_hierarchy`**: シーンの構成を書き出すツール。実装や検証でオブジェクトのオン・オフや一時処理を掛ける前に、
  構成を把握してプロジェクトの CLAUDE.md へ残すためのもの（ユーザー指示 2026-10-04）。
  - ヒエラルキー（`maxDepth` まで。より深い所は `+N children`）、アクティブ状態、部品名（既定で Transform・MeshFilter・Renderer などは省く。`componentFilter` で絞れる）、
    カメラの出力先（Display・RenderTexture）、ライトの種類と URP の Rendering Layer、Missing Script の数、Prefab インスタンスの根を返す。
  - URP の Rendering Layer は `UniversalAdditionalLightData.renderingLayers` をリフレクションで読む（URP は `Light.renderingLayerMask` ではなくこちらを使う。URP を使わないプロジェクトでもコンパイルできるよう型を参照しない）。
  - `scenePath` を省くと開いているシーンすべて。開いていないシーンは Additive で開いて読み、保存せずに閉じる（Play 中は拒否）。
  - JSON に加えて字下げしたテキストも返し、`outputPath` を渡すとテキストをファイルにも書く。応答が大きくなりすぎないよう `maxNodes`（既定 2000）で打ち切る。
  - 実測 2026-10-04 SinkaiFes2026: 開いている test_Azuki（maxDepth 1）で 47 ノード、閉じている test_Plate（maxDepth 2）を開いて 115 ノード読み、読んだ後に test_Plate が閉じられていることを確認。

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