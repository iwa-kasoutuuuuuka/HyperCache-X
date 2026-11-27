# HyperCache X (HCX) v2.4.0

低速な HDD の I/O を、**Tier 1 (既存 RAMDisk MMF 優先 / プロセス内 RAM) + Tier 2 (高速 SSD 上の専用キャッシュファイル)** の 3 階層ハイブリッドキャッシュで安全かつ高並列に高速化する Windows 向けユーザーモード・ストレージ加速エンジン＋永続化対応仮想 RAM ディスク＋MCP サーバーです。

---

## v2.4.0 の主な新機能

- **稼働中 `HCX.App` ↔ `HCX.Mcp` ライブ連携（Named Pipe IPC）**:
  - 現ユーザー SID のみにアクセス許可を絞ったローカル Named Pipe（`HcxControlPipeServer`）を `HCX.App` に搭載。Antigravity / Claude Code から稼働中の `HCX.App` 本体のリアルタイム統計取得（`hcx_live_app_status`）、即時同期（`hcx_live_app_sync`）、事前ウォームアップ（`hcx_warm_path`）、ホットファイル Top 10 取得（`hcx_get_hot_files`）が可能です。
- **事前ウォームアップ（Pre-Warm）＆ ホットファイルピン留め（Pinning）**:
  - ゲームのアセットフォルダや開発プロジェクトを起動前に一括先読みし、L1 優先ピン留めでコールドスタートを解消します（UI ボタン＋MCP ツール両対応）。
- **永続 RAM ディスクモード（終了時自動保存・起動時自動復元）**:
  - 内蔵 WinFsp RAM ディスク（`InMemoryVolumeStore`）に `SaveToDirectory` / `LoadFromDirectory` を追加。バックアップ先フォルダを指定することで、RAM 速度のまま再起動を跨いでデータを維持できます。
- **拡張子・パス別フィルタ＆ワークロード別プリセット**:
  - `Balanced` / `Gaming` / `Development` / `MediaEditing` の 4 プリセットを搭載。動画・圧縮アーカイブ（`.mp4`, `.mkv`, `.zip` 等）は先頭から即バイパスして汚染を防ぎ、`.pak`, `.dll`, `.db`, `.cs` 等を初回から L1 優先昇格します。
- **アイドル時先行フラッシュ & バッテリー低下時セーフモード**:
  - Write-Back 稼働中でも I/O が途切れたアイドル時（既定 3 秒）に Dirty ブロックを先行同期。ノート PC でバッテリー残量 ≦ 20% になると自動で `WriteThrough` へ切替＆即時 `SyncAll()` します。
- **ホットファイル Top 10 可視化・60 秒リアルタイムスパークライン・JSON レポート出力**:
  - スループット／ヒット率の 60 秒推移グラフ、ホットファイル Top 10 表示、診断レポート JSON エクスポートをダッシュボードに追加。

### これまでの v2.3.x / v2.2.x / v2.1.x 基盤機能

- **全階層キャッシュのシャード並列化**: `RamCacheTier`（最大 16 シャード）、`RamDiskFileCacheTier`（最大 16 シャード + `MemoryMappedFile` ゼロシステムコール）、`SsdFileCacheTier`（最大 8 シャード並列 NVMe I/O）＋空シャード退避フォールバック。
- **高速化エンジン**: `AiAccessScorer` ロック競合解消＆L1 ヒット時軽量パス、シーケンシャル一括バッチ Read、有界チャネルによる非同期シーケンシャル先読み（Read-Ahead）、`HcxFileSystem` の Win32 `GetFileAttributesW` 1 回化、`InMemoryVolumeStore` 固定 64 ストライプ＆世代ハンドル管理。
- **運用・配布**: 二重起動防止（Mutex＋IPC）、複数ドライブ・RAMDisk プロファイル永続化、デュアル ZIP パッケージ（`HCX.App.exe` + `HCX.Mcp.exe` 同梱）＋`SHA256SUMS.txt`、WinFsp 実機 E2E スクリプト。

---

## セキュリティ・堅牢性設計

- **生ディスク／生ボリューム書き込み禁止**: `CacheOptions.Validate()`・`AppSettingsManager.Sanitize()`・`InMemoryVolumeStore`・`HCX.Mcp` の全入口で `\\?\` / `\\.\` の生デバイスパスを拒否。
- **Named Pipe ユーザー分離 ACL**: `HcxControlPipeServer` は `WindowsIdentity.GetCurrent().User` のみに `FullControl` を付与し、他ユーザープロセスからの操作を遮断。
- **パストラバーサル & リンク越境 & 再帰キャッシュ防止**: 字句境界検証、`..`・NUL バイト・`.hcx` / `.hcx_bench_*` の遮断、ジャンクション／シンボリックリンクの解決先ルート境界チェック、Reparse Point スキップを徹底。
- **最小権限 & 依存パッケージ監査**: `asInvoker` マニフェスト。全 5 プロジェクトで NuGet 既知脆弱性 **0 件**。

---

## MCP サーバー対応（Antigravity / Claude Code）

外部 NuGet 依存ゼロの **MCP JSON-RPC 2.0 over stdio サーバー**（[`src/HCX.Mcp`](src/HCX.Mcp/HCX.Mcp.csproj) / `HCX.Mcp.exe`）を同梱しています。

### 提供ツール一覧（12 ツール）

| ツール名 | 機能 |
|---|---|
| `hcx_live_app_status` | 稼働中 `HCX.App` 本体のライブ統計・Top 10 ホットファイル・RAMDisk 状態を取得（Named Pipe IPC） |
| `hcx_live_app_sync` | 稼働中 `HCX.App` 本体の全 Dirty ブロックを即時 `SyncAll()` |
| `hcx_warm_path` | 指定フォルダ／ファイルを事前ウォームアップ＆ピン留め（`HCX.App` 本体／MCP セッション両対応） |
| `hcx_get_hot_files` | ホットファイル Top 10（ヒット数・ミス数・ヒット率）を取得 |
| `hcx_get_system_info` | 物理ドライブ、検出 RAMDisk、空きドライブレター、WinFsp 導入状態、設定パスを取得 |
| `hcx_get_settings` | サニタイズ済みの `settings.json` を取得 |
| `hcx_run_benchmark` | 3 階層キャッシュ実測ベンチマークを実行し、MB/s・ヒット率・整合性を返却 |
| `hcx_start_session` | MCP 内で独立した `HybridCacheEngine` セッションを起動（最大 8 セッション） |
| `hcx_get_session_stats` | MCP セッションの `CacheStatsSnapshot` を取得 |
| `hcx_sync_session` | MCP セッションの Dirty ブロックを即時フラッシュ |
| `hcx_stop_session` | MCP セッションを安全停止 |
| `hcx_cleanup_cache` | キャッシュフォルダの孤立ファイル（`cache_*.bin`, `l1_cache_*.bin` 等）を安全削除 |

### Claude Code での接続方法

リポジトリ直下に [`.mcp.json`](.mcp.json) を配置済みのため、Claude Code でプロジェクトを開くだけで `hypercache-x` が自動認識されます。グローバル登録する場合：

```powershell
claude mcp add hypercache-x -- "E:\HCX\src\HCX.Mcp\bin\Release\net10.0-windows\HCX.Mcp.exe"
```

### Antigravity での接続方法

`%USERPROFILE%\.gemini\antigravity\mcp_config.json` の `mcpServers` に追加してください：

```json
{
  "mcpServers": {
    "hypercache-x": {
      "command": "E:\\HCX\\src\\HCX.Mcp\\bin\\Release\\net10.0-windows\\HCX.Mcp.exe",
      "args": []
    }
  }
}
```

---

## プロジェクト構成 & 検証結果 (v2.4.0)

- [`src/HCX.Core`](src/HCX.Core/HCX.Core.csproj): 3 階層シャード化キャッシュエンジン・フィルタ・IPC (`HybridCacheEngine`, `CacheFilterRules`, `HcxControlPipeServer`, `RamCacheTier`, `RamDiskFileCacheTier`, `SsdFileCacheTier`, `AiAccessScorer`, `MetadataManager`, `AppSettingsManager`)
- [`src/HCX.VirtualFs`](src/HCX.VirtualFs/HCX.VirtualFs.csproj): WinFsp 仮想 FS・永続化対応内蔵 RAM ディスク (`HcxFileSystem`, `InMemoryVolumeStore`, `MemoryFileSystem`, `RamDiskHost`, `WinFspRuntime`)
- [`src/HCX.App`](src/HCX.App/HCX.App.csproj): ダークモード WPF ダッシュボード（32×8 ヒートマップ、60 秒スパークライン、ホットファイル Top 10、事前ウォームアップ、永続 RAMDisk、バッテリー保護、ライブ MCP IPC）
- [`src/HCX.Mcp`](src/HCX.Mcp/HCX.Mcp.csproj): 外部依存ゼロの MCP stdio サーバー（12 ツール）
- [`tests/HCX.Tests`](tests/HCX.Tests/HCX.Tests.csproj): xUnit 自動テストスイート（**65 件**）

### ビルド・テスト・配布コマンド

```powershell
dotnet restore HCX.sln
dotnet build HCX.sln -c Release --no-restore
dotnet test HCX.sln -c Release --no-build

# 配布 ZIP 2種 (HCX.App.exe + HCX.Mcp.exe 同梱) + SHA256SUMS.txt の生成
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1

# WinFsp 導入環境での実仮想ドライブ + RAM ディスク E2E 検証
powershell -ExecutionPolicy Bypass -File .\scripts\run-winfsp-e2e.ps1
```

仮想ドライブマウント機能および内蔵 RAM ディスク機能を利用する場合は WinFsp を導入してください: `winget install WinFsp.WinFsp`
