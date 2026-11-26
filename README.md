# HyperCache X (HCX) v2.0

低速な HDD の I/O を、**Tier 1 (既存 RAMDisk 優先 / プロセス内 RAM) + Tier 2 (高速 SSD 上の専用キャッシュファイル)** の 3 階層ハイブリッドキャッシュで安全に高速化する Windows 向けユーザーモード・ストレージ加速エンジン＋仮想 RAM ディスクツールです。

---

## v2.0 の主な特徴

- **3階層ハイブリッドアーキテクチャ（既存 RAMDisk 優先対応）**:
  - **Tier 1 (L1 RAM / 既存 RAMDisk)**: `AutoPreferRamDisk` モードにより、PC 上に既存の RAMDisk（例: `R:\`）が存在する場合は優先して L1 キャッシュ（`.hcx\l1_cache_{id}.bin`）に利用し、無ければプロセス内 RAM を自動使用します（上限設定可: 256 MB 〜 16 GB）。
  - **Tier 2 (L2 SSD 永続キャッシュ)**: 選択 SSD 上の専用キャッシュファイル（`.hcx\cache_{id}.bin`）に `RandomAccess` で格納。**生ボリュームのオフセット 0 への直接書き込みは一切行いません。**
  - **停止時 L1→L2 自動退避**: エンジン停止時に L1 のホットブロックを L2 SSD へ自動退避させ、再起動後もウォームスタートを維持します。
  - **Tier 3 (HDD Backing)**: 元の HDD ボリューム / フォルダ。
- **内蔵 WinFsp 仮想 RAM ディスク作成機能 (`HCX-RAMFS`)**:
  - 64 KB ページ単位の動的割り当て・容量上限制御・ファイル削除時の即時メモリ解放を備えた揮発性 RAM ディスクを、追加カーネルドライバなしで任意のドライブ文字（例: `M:\`）として作成・マウントできます。
- **ユーザーモード完結（BSOD / テスト署名不要）**:
  - カスタムカーネルドライバ（`.sys`）を廃止し、**WinFsp** によるユーザーモード仮想ドライブ（例: `D:\` → `X:\`）で安全に透過加速を実現。
  - WinFsp が未導入の環境でも、内蔵の「実測ベンチマーク」により 3 階層キャッシュとデータ整合性をそのまま検証可能。
- **AI アクセスパターン最適化 & スマートフィルタ**:
  - 連続アクセス（シーケンシャルスキャン）を自動検出してキャッシュ昇格から除外し、SSD の摩耗を抑制。
  - 時間減衰付きヒートスコアによりランダム頻出ブロックを L2 SSD / L1 RAM へ動的昇格。
- **Write-Back / Write-Through & 整合性保証**:
  - ライトバック時は L1/L2 で吸収した変更をバックグラウンド同期および停止時 `SyncAll()` で HDD へフラッシュ。
  - ライトスルー時は書き込み先ブロックの既存 L2 SSD キャッシュを確実に無効化・更新し、連続アクセス除外時でも古いキャッシュを返しません。
  - 外部からの元ファイル変更（サイズ・更新日時変化）、リネーム、切り詰め、削除に対してキャッシュを自動追従・無効化。
- **永続化メタデータ (`HCX2` フォーマット)**:
  - SHA-256 検証付きのアトミック保存（`cache_metadata_{id}.dat`）により再起動後も L2 キャッシュを復元。

---

## セキュリティ・堅牢性設計

- **生ディスク／生ボリューム書き込み禁止**: `CacheOptions.Validate()` により `\\?\` / `\\.\` の生デバイス・ボリュームパスを拒否し、ブートセクタやファイルシステム破壊を防止します。
- **パストラバーサル & 再帰キャッシュ防止**: `HcxFileSystem` および `HybridCacheEngine` で全パスを正規化・ルート境界検証し、`..` 脱出、NUL バイト、および内部キャッシュディレクトリ（`.hcx` / `.hcx_bench_*`）への自己参照アクセスを `ERROR_ACCESS_DENIED` で遮断します。
- **メタデータ改ざん・異常割り当て DoS 対策**: `MetadataManager` は SHA-256 検証に加え、エントリ数・残バイト数・パス長を事前検証し、`..` やルート付きパスを含む不正データを拒否します。
- **ロック順序制御によるデッドロック防止**: `NotifyRename` での複数ファイルロック取得を正規化パス順（または単一ロック）で固定し、並列クロスリネーム時の AB-BA デッドロックを防止します。
- **インメモリ RAM ディスク保護**: `InMemoryVolumeStore` は容量上限超過を `ERROR_DISK_FULL` で拒否し、整数オーバーフロー・ディレクトリ循環リネーム (`\a` → `\a\b\a`)・不正なセキュリティ記述子 (`ERROR_INVALID_SECURITY_DESCR`) をすべて検証して遮断します。
- **最小権限 & 依存パッケージ監査**: マニフェストは `asInvoker`（通常権限で Explorer から WinFsp マウントをそのまま参照可能）。NuGet 依存パッケージ脆弱性スキャン（`dotnet list package --vulnerable --include-transitive`）で既知脆弱性 **0 件**、REA 静的解析で最小限の Win32 P/Invoke 境界を確認済みです。

---

## プロジェクト構成

- [`src/HCX.Core`](src/HCX.Core/HCX.Core.csproj): 3 階層キャッシュエンジン (`HybridCacheEngine`, `IL1CacheTier`, `RamCacheTier`, `RamDiskFileCacheTier`, `SsdFileCacheTier`, `AiAccessScorer`, `MetadataManager`, `DiskUtility`, `CacheBenchmarkRunner`)
- [`src/HCX.VirtualFs`](src/HCX.VirtualFs/HCX.VirtualFs.csproj): WinFsp 連携仮想ファイルシステム (`HcxFileSystem`, `VirtualDriveHost`, `InMemoryVolumeStore`, `MemoryFileSystem`, `RamDiskHost`, `WinFspRuntime`)
- [`src/HCX.App`](src/HCX.App/HCX.App.csproj): ダークモード WPF ダッシュボード（ハイブリッド加速 / 内蔵 RAM ディスクの 2 タブ構成、リアルタイム 32×8 ヒートマップ、トレイ常駐 UI、埋め込みマルチサイズアイコン）
- [`tests/HCX.Tests`](tests/HCX.Tests/HCX.Tests.csproj): xUnit 自動テストスイート（機能・永続化・整合性・セキュリティ・並列性能テスト 34 件）

---

## パフォーマンス最適化と実測結果

- **O(1) キャッシュ管理 & `ReaderWriterLockSlim` 並列化**: L1/L2 各層を `ReaderWriterLockSlim` 化し、ファイル単位の Dirty 最大サイズ追跡を O(1) インデックス化。LRU 追出しも 32 点サンプリングで O(1) 化。
- **stat TTL キャッシュ & ゼロアロケーション**: `BackingStatCacheTtl`（既定 500ms）により L1 ヒット時の HDD メタデータ問い合わせを削減し、`ArrayPool<byte>` の遅延レンタルおよび L1 ヒット時の AI スコア単一ロック更新を実装。
- **WinFsp 並列 I/O & `unsafe` ゼロコピー**: `MountEx(..., synchronized: false)` で WinFsp の直列化を解除し、`HcxFileSystem` / `MemoryFileSystem` の Read・Write を `Span<byte>` でネイティブバッファへ直結。
- **Write-Back 連続ブロック結合フラッシュ**: `FlushFileLocked` で連続する Dirty ブロックを最大 16 ブロック（1 MB）単位に結合して書き戻し。

| 計測項目 | 最適化前 | 最適化後 | 改善倍率 |
| :--- | ---: | ---: | ---: |
| L1 RAM 読込 (4,096 blk 保有・5,000 回) | 567.49 ms (`550.7 MB/s`) | **14.55 ms (`21,480.6 MB/s`)** | **約 39.0 倍** |
| L2 SSD 4スレッド並列読込 (計 256 MB) | 190.72 ms (`1,310.8 MB/s`) | **44.10 ms (`5,668.6 MB/s`)** | **約 4.3 倍** |
| Write-Back 4MB 同期 (`SyncAll`) | 15.25 ms (`262.3 MB/s`) | **14.46 ms (`276.6 MB/s`)** | 連続 1MB 結合 |
| 内蔵 RAM ディスク 4スレッド並列読込 | 2.34 ms (`106,773.7 MB/s`) | **2.42 ms (`103,182.1 MB/s`)** | 同等維持＋WinFsp ゼロコピー |

---

## ビルド・テスト・検証結果 (v2.0)

- **ビルド**: `Debug` / `Release` ともに **0 警告 / 0 エラー**
- **自動テスト**: `dotnet test HCX.sln -c Release --no-build` — **34 / 34 件合格**
- **依存関係脆弱性**: `dotnet list HCX.sln package --vulnerable --include-transitive` — **0 件**
- **起動確認**: `HCX.App.exe`（Release ビルド）実機起動スモークテスト合格

### ビルド・テスト実行手順

必要環境: **.NET 10 SDK**（Windows）

```powershell
dotnet restore HCX.sln
dotnet build HCX.sln -c Release --no-restore
dotnet test HCX.sln -c Release --no-build
```

仮想ドライブマウント機能および内蔵 RAM ディスク機能を利用する場合は、WinFsp ランタイムを導入してください:

```powershell
winget install WinFsp.WinFsp
```

（または `HCX.App` 画面内の「WinFsp をインストール (winget)」ボタンからも導入できます）
