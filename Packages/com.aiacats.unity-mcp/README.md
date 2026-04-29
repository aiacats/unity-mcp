# Unity MCP Bridge for Claude Code

Unity Editor を Claude Code から直接制御するための MCP (Model Context Protocol) パッケージです。

## セットアップ

### 1. Node.js 依存関係のインストール

**自動インストール（推奨）**: パッケージ導入後、Unity Editor を起動すると `Server~/node_modules` の有無を検出し、未インストールであれば `npm install` を自動実行します。コンソールに `[Claude Code MCP] npm install completed successfully.` が出れば完了です。

無効化したい場合は `Tools > Claude Code MCP > Setup: Toggle Auto Install on Editor Load` で OFF にできます（EditorPrefs に保存）。

**手動インストール**:

```bash
cd Packages/com.aiacats.unity-mcp/Server~
npm install
```

### 2. MCP 設定ファイルの作成

プロジェクトルートに `.mcp.json` を作成：

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

### 3. 動作確認

Unity Editor を開くと HTTP サーバーがポート 8090 で自動起動します。
Scene ビュー左上に `MCP: ON` と表示されていれば正常です。

## 利用可能なツール一覧

### GameObject 操作

| ツール名 | 説明 | 主要パラメータ |
|---------|------|--------------|
| `select_gameobject` | Hierarchy 上のオブジェクトを選択 | `objectPath` or `instanceId` |
| `get_gameobject_info` | Transform・コンポーネント・階層情報を取得 | `objectPath` or `instanceId` |
| `update_gameobject` | プロパティ変更（存在しない場合は新規作成） | `objectPath`, `gameObjectData{name, activeSelf, tag, layer, isStatic}` |
| `delete_gameobject` | シーンから削除（Undo 対応） | `objectPath` or `instanceId` |

### コンポーネント操作

| ツール名 | 説明 | 主要パラメータ |
|---------|------|--------------|
| `update_component` | コンポーネントの追加・プロパティ変更 | `objectPath`, `componentName`, `componentData` |
| `get_component_properties` | SerializedProperty 経由で全プロパティ値を取得 | `objectPath`, `componentName` |
| `remove_component` | コンポーネントを削除（Undo 対応） | `objectPath`, `componentName` |

### シーン・アセット管理

| ツール名 | 説明 | 主要パラメータ |
|---------|------|--------------|
| `save_scene` | アクティブシーンを保存 | なし |
| `open_scene` | シーンをパスで開く | `scenePath`, `additive` |
| `find_assets` | AssetDatabase 検索 | `filter`, `type`, `searchInFolder`, `limit` |
| `add_asset_to_scene` | Prefab 等をシーンに配置 | `assetPath` or `guid`, `position` |
| `create_material` | マテリアルを作成 | `name`, `shader`, `color{r,g,b,a}`, `savePath` |
| `get_material_properties` | マテリアルの全Shaderプロパティと現在値を取得 | `assetPath` or `guid` |
| `set_material_property` | マテリアルのShaderプロパティを設定 | `assetPath` or `guid`, `propertyName`, `propertyType`, `value` |
| `execute_menu_item` | メニューコマンドを実行 | `menuPath` |
| `add_package` | Package Manager でパッケージ追加 | `source(registry/github/disk)`, `packageName` |

### 開発ワークフロー

| ツール名 | 説明 | 主要パラメータ |
|---------|------|--------------|
| `hot_reload` | スクリプト再コンパイル | `saveAssets`, `optimized` |
| `force_compilation` | 強制フルコンパイル | `forceUpdate` |
| `screenshot` | Unity Editor ウィンドウ全体のスクリーンショット (Windows API) | `savePath` |

### モニタリング・デバッグ

| ツール名 | 説明 | 主要パラメータ |
|---------|------|--------------|
| `send_console_log` | Unity Console にメッセージ送信 | `message`, `type(info/warning/error)` |
| `get_console_logs` | コンソールログ取得 | `logType`, `limit`, `offset`, `includeStackTrace` |
| `check_compilation_status` | コンパイル状態の確認 | なし |
| `get_compilation_errors` | コンパイルエラー・警告の取得 | なし |
| `run_tests` | Unity Test Runner でテスト実行 | `testMode(EditMode/PlayMode)`, `testFilter`, `queryOnly` |

### リソース

| リソース名 | 説明 |
|-----------|------|
| `scenes_hierarchy` | 現在のシーン階層（全 GameObject のツリー構造） |

## 自動開発フロー

Claude Code が Unity プロジェクトでコードを実装する際の推奨フロー：

```
1. コード実装（C# スクリプト編集）
2. hot_reload 呼出し（スクリプト再コンパイル）
3. check_compilation_status をポーリング（タイムアウト: 5分）
4. get_compilation_errors でエラー確認 → エラーがあれば 1 に戻る
5. run_tests でテスト実行（EditMode / PlayMode）
6. run_tests を queryOnly: true でポーリング（タイムアウト: 5分）
7. テスト失敗があれば 1 に戻る
```

テストコードはこのパッケージには含まれていません。プロジェクト側で `Assets/Tests/` 等に `.asmdef` 付きで配置してください。

## Play Mode でのゲームループ実行

Unity の Play Mode を MCP から直接制御することはできません（ドメインリロードにより HTTP 接続が切断されるため）。

ゲームループを伴う実行・検証には、**`run_tests` の `testMode: "PlayMode"` を使用**してください。Unity Test Framework の PlayMode テストは以下を自動的に行います：

1. Unity が Play Mode に入りゲームループが開始される
2. テストコード内で `yield return null` 等でフレームを進めながらランタイム動作を検証
3. テスト完了後、自動的に Play Mode を終了
4. 結果が `queryOnly: true` で取得可能になる

プロジェクト側での PlayMode テストの作成例：

```csharp
// Assets/Tests/PlayMode/SamplePlayModeTest.cs
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class SamplePlayModeTest
{
    [UnityTest]
    public IEnumerator ObjectFallsByGravity()
    {
        var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        obj.AddComponent<Rigidbody>();
        float startY = obj.transform.position.y;

        // 60フレーム（約1秒）ゲームループを実行
        for (int i = 0; i < 60; i++)
            yield return null;

        Assert.Less(obj.transform.position.y, startY, "Object should have fallen");
        Object.Destroy(obj);
    }
}
```

PlayMode テスト用 `.asmdef` には `UnityEngine.TestRunner` と `UnityEditor.TestRunner` への参照が必要です。

## Unity Editor UI

- **Control Panel**: `Tools > Claude Code MCP > Control Panel` でサーバー状態確認・制御
- **Scene ビュー**: 左上に MCP サーバーの ON/OFF ステータスを表示
- **Setup メニュー**: `Tools > Claude Code MCP` から依存関係インストール・接続テスト

## トラブルシューティング

- **サーバーが起動しない**: ポート 8090 が他プロセスで使用されていないか確認。自動的に 8090-8099 の範囲で代替ポートを試みます
- **MCP 接続エラー**: `npm install` が `Server~/` で実行済みか確認
- **コンパイル後に接続が切れる**: ドメインリロード後に自動復帰します。数秒待ってからリトライしてください
