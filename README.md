# HyperCache X (HCX) v2.2.0

低速な HDD の I/O を、**Tier 1 (既存 RAMDisk 優先 / プロセス内 RAM) + Tier 2 (高速 SSD 上の専用キャッシュファイル)** の 3 階層ハイブリッドキャッシュで安全に高速化する Windows 向けユーザーモード・ストレージ加速エンジン＋仮想 RAM ディスクツールです。

---

## v2.2.0 の主な新機能・改善ポイント

- **二重起動防止 & 既存ウィンドウ復帰**: `Local\HyperCacheX_SingleInstance_Mutex`＋イベント IPC により、トレイ常駐中の多重起動を防止し、2 回目以降の起動で既存インスタンスの画面を前面表示します。
- **複数ドライブ & RAM ディスクのプロファイル永続化**: `SavedSessions` / `SavedRamDisks` により、複数の加速ターゲットや内蔵 RAM ディスクをまとめて `%LOCALAPPDATA%\HyperCacheX\settings.json` へ保存し、次回起動時に自動復元可能。
- **Write-Back 同期間隔設定 & L1 RAM メモリ上限確認**: UI から Write-Back の同期間隔（1〜60 秒）を選択でき、プロセス内 L1 RAM 合計が物理メモリ総量の 50% を超える場合は起動前に確認ダイアログを表示します。
- **ターゲット内シンボリックリンク／ジャンクション越境ブロック**: `HcxFileSystem.ToFullPath` に `EnsureNoEscapingReparsePoint` を追加し、対象フォルダ内部のジャンクション・リンクがルート外や `.hcx` へ脱出するアクセスを `ERROR_ACCESS_DENIED` で遮断します。
- **デュアル配布パッケージ & 署名対応 (`scripts/build-release.ps1`)**:
  - **Self-Contained 版**: .NET 10 ランタイム同梱でそのまま起動可能。
  - **Portable 版**: .NET 10 Desktop Runtime 導入済み環境向けの軽量版。
  - `-CertThumbprint` 指定による Authenticode 署名フックおよび `SHA256SUMS.txt` 自動生成に対応。
- **WinFsp 実機マウント E2E スクリプト (`scripts/run-winfsp-e2e.ps1`)**: WinFsp 導入環境で `HcxFileSystem`＋`RamDiskHost` の実ドライブ I/O を一括検証する専用スクリプトを追加。

### これまでの v2.1.x 基盤機能

- **3階層ハイブリッド（既存 RAMDisk 優先対応）**: `AutoPreferRamDisk` により既存の RAMDisk（例: `R:\`）を L1 に優先活用（無ければプロセス内 RAM）、L2 は SSD 上の専用キャッシュファイル（`.hcx\cache_{id}.bin`）を使用し生ディスク書き込みを排除。
- **内蔵 WinFsp 仮想 RAM ディスク (`HCX-RAMFS`)**: 64 KB 動的ページ＋容量上限＋ファイル削除時即座に解放。
- **高並列・ロック分離設計**: ファイルロック 512 ストライプ、`AiAccessScorer` 64 ストライプ、`_inFlightEvictions` による線形化クロス追い出し、`BackingStatCacheTtl`（500 ms）、`unsafe Span<byte>` ゼロコピー、Write-Back 連続ブロック結合。
- **OS シャットダウン保護・UI リーク解消**: `SessionEnding` / 未処理例外時の安全同期、トレイメニューの単一インスタンス化、監査ログ 500 行上限リング化、起動時 WMI 非同期化。

---

## セキュリティ・堅牢性設計

- **生ディスク／生ボリューム書き込み禁止**: `CacheOptions.Validate()` および `AppSettingsManager.Sanitize()` により `\\?\` / `\\.\` の生デバイス・ボリュームパスを拒否します。
- **パストラバーサル & リンク越境 & 再帰キャッシュ防止**: 字句境界検証、`..`・NUL バイト・`.hcx` / `.hcx_bench_*` の遮断に加え、ジャンクション・シンボリックリンクの解決先ルート境界チェックおよび孤立ファイル削除時の Reparse Point スキップを実施します。
- **ネイティブポインタ & インメモリ境界保護**: `HcxFileSystem` / `MemoryFileSystem` の `IntPtr.Zero` 拒否、`InMemoryVolumeStore` の容量上限 (`ERROR_DISK_FULL`)・整数オーバーフロー・循環リネーム・不正セキュリティ記述子検証、`SafeFileHandle` ベースの P/Invoke を徹底。
- **メタデータ・設定・スタートアップ保護**: `MetadataManager` の SHA-256＋件数/残バイト検証、`AppSettingsManager` の HKCU `Run` 引用符・制御文字・UNC 拒否。
- **最小権限 & 依存パッケージ監査**: `asInvoker` マニフェスト。NuGet 依存パッケージ脆弱性スキャンで既知脆弱性 **0 件**。

---

## プロジェクト構成

- [`src/HCX.Core`](src/HCX.Core/HCX.Core.csproj): 3 階層キャッシュエンジン・設定管理 (`HybridCacheEngine`, `IL1CacheTier`, `RamCacheTier`, `RamDiskFileCacheTier`, `SsdFileCacheTier`, `AiAccessScorer`, `MetadataManager`, `AppSettingsManager`, `DiskUtility`, `CacheBenchmarkRunner`)
- [`src/HCX.VirtualFs`](src/HCX.VirtualFs/HCX.VirtualFs.csproj): WinFsp 連携仮想ファイルシステム (`HcxFileSystem`, `VirtualDriveHost`, `InMemoryVolumeStore`, `MemoryFileSystem`, `RamDiskHost`, `WinFspRuntime`)
- [`src/HCX.App`](src/HCX.App/HCX.App.csproj): ダークモード WPF ダッシュボード（ハイブリッド加速 / 内蔵 RAM ディスクの 2 タブ構成、リアルタイム 32×8 ヒートマップ、単一起動制御、複数プロファイル自動復元、同期間隔・L2 キャッシュ消去 UI）
- [`tests/HCX.Tests`](tests/HCX.Tests/HCX.Tests.csproj): xUnit 自動テストスイート（**49 件**）
- [`scripts/build-release.ps1`](scripts/build-release.ps1): テスト＋Self-Contained / Portable デュアル ZIP＋オプション署名＋`SHA256SUMS.txt` 生成スクリプト
- [`scripts/run-winfsp-e2e.ps1`](scripts/run-winfsp-e2e.ps1): WinFsp 実機マウント E2E 検証スクリプト

---

## ビルド・テスト・配布パッケージ作成 (v2.2.0)

- **ビルド**: `Debug` / `Release` ともに **0 警告 / 0 エラー**
- **自動テスト**: `dotnet test HCX.sln -c Release --no-build` — **49 / 49 件合格**（ジャンクション越境遮断、複数プロファイルサニタイズ、実機 `R:\` RAMDisk ベンチマーク等を含む。WinFsp 未導入時は実マウント E2E を安全スキップ）
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
