# HyperCache X (HCX) v2.1

低速な HDD の I/O を、**Tier 1 (既存 RAMDisk 優先 / プロセス内 RAM) + Tier 2 (高速 SSD 上の専用キャッシュファイル)** の 3 階層ハイブリッドキャッシュで安全に高速化する Windows 向けユーザーモード・ストレージ加速エンジン＋仮想 RAM ディスクツールです。

---

## v2.1 の主な機能・強化ポイント

- **3階層ハイブリッドアーキテクチャ（既存 RAMDisk 優先対応）**:
  - **Tier 1 (L1 RAM / 既存 RAMDisk)**: `AutoPreferRamDisk` モードにより、PC 上に既存の RAMDisk（例: `R:\`）が存在する場合は優先して L1 キャッシュ（`.hcx\l1_cache_{id}.bin`）に利用し、無ければプロセス内 RAM を自動使用します（上限設定可: 256 MB 〜 16 GB）。起動時に残存する孤立 `l1_cache_*.bin` ファイルも自動掃除します。
  - **Tier 2 (L2 SSD 永続キャッシュ)**: 選択 SSD 上の専用キャッシュファイル（`.hcx\cache_{id}.bin`）に `RandomAccess` で格納。容量縮小時のキャッシュファイル自動トリム、および UI からの「L2 キャッシュ消去」機能を搭載。**生ボリュームのオフセット 0 への直接書き込みは一切行いません。**
  - **停止時 L1→L2 自動退避**: エンジン停止時に L1 のホットブロックを L2 SSD へ自動退避させ、再起動後もウォームスタートを維持します。
  - **Tier 3 (HDD Backing)**: 元の HDD ボリューム / フォルダ。
- **ロックフリー＆線形化 Eviction・高並列エンジン**:
  - ファイルロックを **512 ストライプの `ReaderWriterLockSlim`** へ最適化し、`AiAccessScorer` は **64 ストライプ + 上限付きファイルシーケンス追跡** へ拡張。
  - クロスファイル追い出し（Eviction）を `_inFlightEvictions`＋呼び出し元ロック解放後の一括ドレイン方式へ刷新し、AB-BA デッドロックや別ファイル Dirty 上書き競合を解消。
  - L1/L2 各層の Dirty 最大オフセット更新を O(1) 化し、ランダムライト性能劣化を防止。
- **WinFsp 仮想ドライブ最適化 & ハンドル安全性向上**:
  - `HcxFileSystem.Read` の重複サイズ問い合わせを削減し、`desc.Stream` 使用時も `BackingStatCacheTtl`（既定 500ms）を有効化。
  - ディレクトリリネーム時のサブツリー Dirty 一括フラッシュ (`FlushPrefix`)・`Overwrite` 後の stat 無効化・`SafeFileHandle` ベースの P/Invoke を徹底。
- **内蔵 WinFsp 仮想 RAM ディスク作成機能 (`HCX-RAMFS`)**:
  - 64 KB ページ単位の動的割り当て・容量上限制御・ファイル削除時の即時メモリ解放を備えた揮発性 RAM ディスクを、追加カーネルドライバなしで任意のドライブ文字（例: `M:\`）として作成・マウントできます。
- **OS シャットダウン保護・設定永続化・UI 安定化**:
  - `SessionEnding` / 未処理例外時の緊急安全同期フックにより、Write-Back Dirty データの安全フラッシュを保証。
  - 起動時 WMI ディスク列挙の非同期化、1 秒ごとのトレイメニュー再生成によるハンドルリーク解消、監査ログの 500 行上限リング化を実装。
  - `%LOCALAPPDATA%\HyperCacheX\settings.json` への設定保存・起動時自動復元、および Windows スタートアップ登録 (`--minimized`) に対応。
- **永続化メタデータ (`HCX2` フォーマット)**:
  - SHA-256 検証付きのアトミック保存（`cache_metadata_{id}.dat`）により再起動後も L2 キャッシュを復元。

---

## セキュリティ・堅牢性設計

- **生ディスク／生ボリューム書き込み禁止**: `CacheOptions.Validate()` により `\\?\` / `\\.\` の生デバイス・ボリュームパスを拒否し、ブートセクタやファイルシステム破壊を防止します。
- **パストラバーサル & 再帰キャッシュ防止**: `HcxFileSystem` および `HybridCacheEngine` で全パスを正規化・ルート境界検証し、`..` 脱出、NUL バイト、および内部キャッシュディレクトリ（`.hcx` / `.hcx_bench_*`）への自己参照アクセスを `ERROR_ACCESS_DENIED` で遮断します。
- **メタデータ改ざん・異常割り当て DoS 対策**: `MetadataManager` は SHA-256 検証に加え、エントリ数・残バイト数・パス長を事前検証し、`..` やルート付きパスを含む不正データを拒否します。
- **ロック順序制御 & Eviction 線形化**: 512 ストライプのインデックス順ロックと `_inFlightEvictions` により、並列クロスリネーム・並列追い出し時のデッドロックおよびデータ競合を防止します。
- **インメモリ RAM ディスク保護**: `InMemoryVolumeStore` は容量上限超過を `ERROR_DISK_FULL` で拒否し、整数オーバーフロー・ディレクトリ循環リネーム (`\a` → `\a\b\a`)・不正なセキュリティ記述子 (`ERROR_INVALID_SECURITY_DESCR`) をすべて検証して遮断します。
- **最小権限 & 依存パッケージ監査**: マニフェストは `asInvoker`（通常権限で Explorer から WinFsp マウントをそのまま参照可能）。NuGet 依存パッケージ脆弱性スキャン（`dotnet list package --vulnerable --include-transitive`）で既知脆弱性 **0 件**。

---

## プロジェクト構成

- [`src/HCX.Core`](src/HCX.Core/HCX.Core.csproj): 3 階層キャッシュエンジン・設定管理 (`HybridCacheEngine`, `IL1CacheTier`, `RamCacheTier`, `RamDiskFileCacheTier`, `SsdFileCacheTier`, `AiAccessScorer`, `MetadataManager`, `AppSettingsManager`, `DiskUtility`, `CacheBenchmarkRunner`)
- [`src/HCX.VirtualFs`](src/HCX.VirtualFs/HCX.VirtualFs.csproj): WinFsp 連携仮想ファイルシステム (`HcxFileSystem`, `VirtualDriveHost`, `InMemoryVolumeStore`, `MemoryFileSystem`, `RamDiskHost`, `WinFspRuntime`)
- [`src/HCX.App`](src/HCX.App/HCX.App.csproj): ダークモード WPF ダッシュボード（ハイブリッド加速 / 内蔵 RAM ディスクの 2 タブ構成、リアルタイム 32×8 ヒートマップ、トレイ常駐 UI、設定自動保存・L2 キャッシュ消去）
- [`tests/HCX.Tests`](tests/HCX.Tests/HCX.Tests.csproj): xUnit 自動テストスイート（機能・永続化・整合性・セキュリティ・並列追い出しストレステスト **42 件**）
- [`scripts/build-release.ps1`](scripts/build-release.ps1): テスト実行＋自己完結型 (`win-x64`) リリース ZIP・`SHA256SUMS.txt` 生成スクリプト

---

## パフォーマンス最適化と実測結果

- **O(1) キャッシュ管理 & 並列化**: L1/L2 各層の `ReaderWriterLockSlim` 化、ファイルロック 512 ストライプ化、AI スコアラー 64 ストライプ化、Dirty 最大終端追跡の O(1) 化、LRU 追出しの 32 点サンプリング O(1) 化。
- **stat TTL キャッシュ & ゼロアロケーション**: `BackingStatCacheTtl`（既定 500ms）を直接アクセス・WinFsp オープンハンドル双方で有効化し、`ArrayPool<byte>` の遅延レンタルを実装。
- **WinFsp 並列 I/O & `unsafe` ゼロコピー**: `MountEx(..., synchronized: false)` で WinFsp の直列化を解除し、`HcxFileSystem` / `MemoryFileSystem` の Read・Write を `Span<byte>` でネイティブバッファへ直結。
- **Write-Back 連続ブロック結合フラッシュ**: `FlushFileLocked` で連続する Dirty ブロックを最大 16 ブロック（1 MB）単位に結合して書き戻し。

| 計測項目 | 最適化前 (v2.0 初期) | 最適化後 (v2.0 後半) | v2.1 検証結果 |
| :--- | ---: | ---: | ---: |
| L1 RAM 読込 (4,096 blk 保有・5,000 回) | 567.49 ms (`550.7 MB/s`) | 14.55 ms (`21,480.6 MB/s`) | **24.52 ms (`12,743.0 MB/s`)** |
| L2 SSD 4スレッド並列読込 (計 256 MB) | 190.72 ms (`1,310.8 MB/s`) | 44.10 ms (`5,668.6 MB/s`) | **55.70 ms (`4,488.0 MB/s`)** |
| Write-Back 4MB 同期 (`SyncAll`) | 15.25 ms (`262.3 MB/s`) | 14.46 ms (`276.6 MB/s`) | **15.45 ms (`258.9 MB/s`)** |
| 内蔵 RAM ディスク 4スレッド並列読込 | 2.34 ms (`106,773.7 MB/s`) | 2.42 ms (`103,182.1 MB/s`) | **2.31 ms (`108,243.9 MB/s`)** |

---

## ビルド・テスト・配布パッケージ作成 (v2.1)

- **ビルド**: `Debug` / `Release` ともに **0 警告 / 0 エラー**
- **自動テスト**: `dotnet test HCX.sln -c Release --no-build` — **42 / 42 件合格**（ホスト環境に WinFsp 未導入時は実マウント E2E を安全スキップ）
- **依存関係脆弱性**: `dotnet list HCX.sln package --vulnerable --include-transitive` — **0 件**

### ビルド・テスト・パッケージ生成手順

必要環境: **.NET 10 SDK**（Windows）

```powershell
dotnet restore HCX.sln
dotnet build HCX.sln -c Release --no-restore
dotnet test HCX.sln -c Release --no-build

# 自己完結型 (win-x64) 配布 ZIP + SHA256SUMS.txt の生成
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

仮想ドライブマウント機能および内蔵 RAM ディスク機能を利用する場合は、WinFsp ランタイムを導入してください:

```powershell
winget install WinFsp.WinFsp
```

（または `HCX.App` 画面内の「WinFsp をインストール (winget)」ボタンからも導入できます）
