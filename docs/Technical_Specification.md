# HyperCache X Technical Specification / 詳細仕様書

## 1. Overview / 概要
**HyperCache X (HCX)** is a high-performance storage acceleration engine that utilizes SSD recovery and AI-driven caching to boost HDD read/write speeds.
**HyperCache X (HCX)** は、SSD をキャッシュとして活用し、AI ベースのデータ配置最適化を行うことで、HDD の読み書き速度を飛躍的に向上させる高速化エンジンです。

---

## 2. System Architecture / システム構成
### 2.1 Hardware Layer / ハードウェア層
- **Target Drive (HDD)**: The volume to be accelerated. / 加速対象のボリューム。
- **Cache Drive (SSD)**: High-speed storage for block-level caching. / 高速キャッシュとして機能する SSD。

### 2.2 Kernel Driver (HCXDriver.sys) / カーネルドライバ
- **Type**: Windows File System MiniFilter Driver. / Windows ファイルシステムフィルタ・ドライバ。
- **Function**: Intercepts IRPs (I/O Request Packets) and redirects them to the SSD cache if the block hit occurs. / I/O 要求をインターセプトし、キャッシュヒット時に SSD へリダイレクト。
- **Altitude**: 265000 (Continuous Filter). / 高度 265000。

### 2.3 Core Engine (HCX.Core) / コアエンジン
- **AI Scoring**: Detects sequential access patterns and excludes them from caching to preserve SSD longevity. / 連続アクセスを検知・除外して SSD の摩耗を抑制。
- **LRU Management**: Implements Least Recently Used eviction policy using atomic slot-based coordination. / アトミックなスロット管理による LRU（追出し）ロジック。

---

## 3. Communication Protocol / 通信プロトコル
### IOCTL Commands / IOCTL 制御コマンド
| Code / コード | Function / 機能 | Description / 説明 |
| :--- | :--- | :--- |
| `0x801` | `SET_SSD` | Sets the SSD cache file path in the kernel. / カーネルに SSD キャッシュパスを設定。 |
| `0x802` | `ADD_MAP` | Registers a physical block mapping (LBA -> SSD Offset). / ブロックマッピングを登録。 |
| `0x804` | `SYNC` | Flushes all dirty blocks to the physical HDD. / 全ブロックを HDD に同期。 |

---

## 4. Cache Management Logic / キャッシュ管理ロジック
- **Block Size**: 64 KB (Optimized for both random I/O and overhead). / 64 KB 単位（ランダム I/O と負荷の最適バランス）。
- **Eviction (LRU)**: When the 1GB SSD limit is reached, the engine uses sampling to identify and replace the least active blocks. / 1GB 上限到達時、サンプリングに基づき非破壊的な追出しを実行。
- **Data Integrity**: Uses `MapLock` (SpinLock) in kernel mode and atomic updates in user mode to prevent data corruption. / カーネル内の SpinLock とユーザーモードのアトミック更新によりデータ整合性を担保。

---

## 5. UI Features / UI 機能
- **Dynamic Stats**: Real-time throughput (MB/s), hit rate, and AI scores. / スループット、ヒット率、AI スコアのリアルタイム表示。
- **Tray Residency**: Minimizes to the system tray with a right-click "Emergency Stop" feature. / タスクトレイ常駐および緊急停止機能。
- **Simulation Mode**: Allows full UI and logic testing without installing the kernel driver. / ドライバなしでの全機能シミュレーション。
