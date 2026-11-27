# HyperCache X (HCX) v2.3.0

低速な HDD の I/O を、**Tier 1 (既存 RAMDisk MMF 優先 / プロセス内 RAM) + Tier 2 (高速 SSD 上の専用キャッシュファイル)** の 3 階層ハイブリッドキャッシュで安全かつ高並列に高速化する Windows 向けユーザーモード・ストレージ加速エンジン＋仮想 RAM ディスクツールです。

---

## v2.3.0 の主な新機能・高速化・改善ポイント

- **全階層キャッシュのシャード並列化（L1 RAM 16シャード / L1 RAMDisk 16シャード / L2 SSD 8シャード）**:
  - `RamCacheTier`・`RamDiskFileCacheTier`・`SsdFileCacheTier` をシャード単位の `ReaderWriterLockSlim` へ分割し、NVMe Queue Depth やマルチコア並列読み書きのスケーラビリティを大幅に引き上げました。
  - 満杯時の空シャード衝突でも他シャードから自動退避するフォールバックと、ダーティブロック 0 件時のロックゼロスキップを備えています。
- **外部 RAMDisk L1 の `MemoryMappedFile` ゼロシステムコール化**:
  - `RamDiskFileCacheTier` が `MemoryMappedFile` + `unsafe Span` 直接転送に対応し、GC 負荷ゼロのままカーネル遷移を排除しました（空き容量・アドレス空間不足時は `RandomAccess` へ自動フォールバック）。
- **`AiAccessScorer` ロック競合解消 & L1 ヒット時軽量パス**:
  - 毎ブロック発生していた `ConcurrentDictionary.Count` 全ロックを廃止し、L1 ヒット時はサンプリング軽量更新（`RecordL1HitLightweight`）で CPU・ロック競合を大幅削減。
- **シーケンシャル一括バッチ Read & 非同期シーケンシャル先読み（Read-Ahead）**:
  - クリーンファイルへのシーケンシャルバイパス時は残り領域を 1 回の `RandomAccess.Read` で直接一括転送。
  - 有界チャネル（最大 32 ブロック）＋ノンブロッキング読み取りロックによるバックグラウンド先読みワーカーを追加し、`CacheStatsSnapshot` および UI へ先読みブロック数（`先読`）を表示します。
- **仮想 FS メタデータ高速化 & 内蔵 RAM ディスク並列化**:
  - `HcxFileSystem.ToFullPath` のジャンクション境界検証を Win32 `GetFileAttributesW` 1 回呼び出し＋ゼロアロケーション走査へ統合し、`Open` / `GetSecurityByName` の重複システムコールも排除。
  - `InMemoryVolumeStore` / `MemoryFileSystem` にノード直参照ハンドル（`InMemoryNodeHandle`）・固定 64 ストライプ `ReaderWriterLockSlim`・CAS 容量管理・`Clear()` 世代管理を導入し、複数ファイル同時書き込みを完全並列化。

### これまでの v2.2.x / v2.1.x 基盤機能

- **二重起動防止 & 既存ウィンドウ復帰**: `Local\HyperCacheX_SingleInstance_Mutex`＋イベント IPC で多重起動を防止し、既存画面を前面復帰。
- **複数ドライブ & RAM ディスクのプロファイル永続化**: `SavedSessions` / `SavedRamDisks` を `%LOCALAPPDATA%\HyperCacheX\settings.json` へ保存し自動復元。
- **Write-Back 同期間隔 UI（1〜60 秒） & L1 RAM 50% 超過確認ダイアログ**。
- **デュアル配布パッケージ (`scripts/build-release.ps1`)**: Self-Contained 版 / Portable 版の 2 種 ZIP、`SHA256SUMS.txt`、`-CertThumbprint` 署名フック対応。
- **WinFsp 実機マウント E2E スクリプト (`scripts/run-winfsp-e2e.ps1`)**。

---

## セキュリティ・堅牢性設計

- **生ディスク／生ボリューム書き込み禁止**: `CacheOptions.Validate()` および `AppSettingsManager.Sanitize()` により `\\?\` / `\\.\` の生デバイス・ボリュームパスを拒否します。
- **パストラバーサル & リンク越境 & 再帰キャッシュ防止**: 字句境界検証、`..`・NUL バイト・`.hcx` / `.hcx_bench_*` の遮断に加え、ジャンクション・シンボリックリンクの解決先ルート境界チェックおよび孤立ファイル削除時の Reparse Point スキップを実施します。
- **ネイティブポインタ & インメモリ境界保護**: `HcxFileSystem` / `MemoryFileSystem` の `IntPtr.Zero` 拒否、`InMemoryVolumeStore` の容量上限 (`ERROR_DISK_FULL`)・整数オーバーフロー・循環リネーム・世代管理による旧ハンドル遮断・不正セキュリティ記述子検証、`SafeFileHandle` ベースの P/Invoke を徹底。
- **メタデータ・設定・スタートアップ保護**: `MetadataManager` の SHA-256＋件数/残バイト検証、`AppSettingsManager` の HKCU `Run` 引用符・制御文字・UNC 拒否。
- **最小権限 & 依存パッケージ監査**: `asInvoker` マニフェスト。NuGet 依存パッケージ脆弱性スキャンで既知脆弱性 **0 件**。

---

## プロジェクト構成

- [`src/HCX.Core`](src/HCX.Core/HCX.Core.csproj): 3 階層キャッシュエンジン・設定管理 (`HybridCacheEngine`, `IL1CacheTier`, `RamCacheTier`, `RamDiskFileCacheTier`, `SsdFileCacheTier`, `AiAccessScorer`, `MetadataManager`, `AppSettingsManager`, `DiskUtility`, `CacheBenchmarkRunner`)
- [`src/HCX.VirtualFs`](src/HCX.VirtualFs/HCX.VirtualFs.csproj): WinFsp 連携仮想ファイルシステム (`HcxFileSystem`, `VirtualDriveHost`, `InMemoryVolumeStore`, `MemoryFileSystem`, `RamDiskHost`, `WinFspRuntime`)
- [`src/HCX.App`](src/HCX.App/HCX.App.csproj): ダークモード WPF ダッシュボード（ハイブリッド加速 / 内蔵 RAM ディスクの 2 タブ構成、リアルタイム 32×8 ヒートマップ、単一起動制御、複数プロファイル自動復元、先読み・Seq 除外統計表示）
- [`tests/HCX.Tests`](tests/HCX.Tests/HCX.Tests.csproj): xUnit 自動テストスイート（**57 件**）
- [`scripts/build-release.ps1`](scripts/build-release.ps1): テスト＋Self-Contained / Portable デュアル ZIP＋オプション署名＋`SHA256SUMS.txt` 生成スクリプト
- [`scripts/run-winfsp-e2e.ps1`](scripts/run-winfsp-e2e.ps1): WinFsp 実機マウント E2E 検証スクリプト

---

## ビルド・テスト・配布パッケージ作成 (v2.3.0)

- **ビルド**: `Debug` / `Release` ともに **0 警告 / 0 エラー**
- **自動テスト**: `dotnet test HCX.sln -c Release --no-build` — **57 / 57 件合格**（16シャード並列、空シャード退避、MMF RAMDisk、非同期先読み、世代ハンドル無効化、ジャンクション越境遮断、実機 `R:\` RAMDisk ベンチマーク等を含む。WinFsp 未導入時は実マウント E2E を安全スキップ）
- **依存関係脆弱性**: `dotnet list HCX.sln package --vulnerable --include-transitive` — **0 件**

### コマンド一覧

必要環境: **.NET 10 SDK**（Windows）

```powershell
dotnet restore HCX.sln
dotnet build HCX.sln -c Release --no-restore
dotnet test HCX.sln -c Release --no-build

# 配布 ZIP 2種 (Self-Contained / Portable) + SHA256SUMS.txt の生成
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1

# WinFsp 導入環境での実仮想ドライブ + RAM ディスク E2E 検証
powershell -ExecutionPolicy Bypass -File .\scripts\run-winfsp-e2e.ps1
```

仮想ドライブマウント機能および内蔵 RAM ディスク機能を利用する場合は、WinFsp ランタイムを導入してください:

```powershell
winget install WinFsp.WinFsp
```

（または `HCX.App` 画面内の「WinFsp をインストール (winget)」ボタンからも導入できます）
