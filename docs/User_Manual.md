# HyperCache X User Manual / 取扱説明書

## 1. Introduction / はじめに
HyperCache X is a tool designed to make your slow HDD feel like an SSD. By using a small portion of your actual SSD as a cache, it learns your usage patterns and speeds up your most frequent tasks.
HyperCache X は、低速な HDD を SSD のような速さに変えるツールです。お使いの SSD の一部をキャッシュとして活用し、利用パターンを学習することで、よく使うアプリやデータの通信を高速化します。

---

## 2. Installation / インストール手順
1. **Run as Administrator**: Right-click `HCX.App.exe` and select "Run as Administrator".
   **管理者として実行**: `HCX.App.exe` を右クリックし、「管理者として実行」を選択してください。
2. **Setup Driver**: In the "Engine Configuration" section, click **[Setup Driver]**. The status should change to "Running (ACTIVE)".
   **ドライバの準備**: 「エンジン構成設定」セクションで **[ドライバのセットアップ]** をクリックします。ステータスが「実行中 (ACTIVE)」になれば成功です。

---

## 3. How to Start Acceleration / 加速の開始方法
1. **Select Drive**: Choose your HDD from the "Target Volume" list.
   **ドライブ選択**: 「加速ターゲット」リストから高速化したい HDD を選択します。
2. **Select SSD**: Choose your SSD from the "SSD Accelerator" list.
   **SSD 選択**: 「SSD アクセラレータ」リストからキャッシュとして使う SSD を選択します。
3. **Go!**: Click **[Start Engine]**. You will see the dashboard coming alive with real-time stats.
   **開始**: **[エンジンの起動]** をクリックします。ダッシュボードにリアルタイムの統計が表示され始めます。

---

## 4. Background Operation / バックグラウンド動作
- **Close to Tray**: Clicking the [X] button will hide the app to the task tray. Acceleration continues in the background.
   **トレイに格納**: [×] ボタンを押すと、アプリはタスクトレイに格納されます。加速処理はバックグラウンドで継続されます。
- **Restore**: Double-click the Shield icon in the tray to restore the dashboard.
   **再表示**: トレイの Shield アイコンをダブルクリックすると、ダッシュボードが再表示されます。

---

## 5. Safety & Sync / 安全性と同期
- **Stop Engine**: Clicking **[Stop]** or selecting "Stop Acceleration" from the tray menu will safely sync all cached data back to the HDD.
   **エンジンの停止**: **[停止]** ボタン、またはトレイメニューの「加速をすべて停止」を選択すると、キャッシュされたデータが安全に HDD へ同期（書き戻し）されます。

---

## 6. Troubleshooting / トラブルシューティング
- **"HCXDriver.sys not found"**: Ensure the driver file is in the same folder as the app, or use "Simulation Mode" for testing.
   **「HCXDriver.sys が見つかりません」**: ドライバファイルがアプリと同じフォルダにあるか確認してください。テスト用には「シミュレーションモード」を活用してください。
