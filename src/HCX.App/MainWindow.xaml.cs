using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
using System.Windows.Threading;
using HCX.Core;
using HCX.VirtualFs;
using Microsoft.Win32;

namespace HCX.App;

public partial class MainWindow : Window
{
	private sealed class EngineSession : IDisposable
	{
		public required HybridCacheEngine Engine { get; init; }
		public VirtualDriveHost? VirtualDrive { get; set; }
		public required SessionProfile Profile { get; init; }
		public long InProcessRamBytes { get; init; }
		public string DisplayLabel { get; init; } = string.Empty;

		public void Dispose()
		{
			VirtualDrive?.Dispose();
			Engine.Dispose();
		}
	}

	private const int MaxLogLines = 500;

	private static readonly SolidColorBrush BrushCold = CreateFrozenBrush(15, 15, 25);
	private static readonly SolidColorBrush BrushRamLow = CreateFrozenBrush(0, 120, 145);
	private static readonly SolidColorBrush BrushRamHot = CreateFrozenBrush(0, 242, 254);
	private static readonly SolidColorBrush BrushSsdLow = CreateFrozenBrush(26, 70, 125);
	private static readonly SolidColorBrush BrushSsdHot = CreateFrozenBrush(79, 172, 254);
	private static readonly SolidColorBrush BrushDirty = CreateFrozenBrush(255, 179, 0);

	private readonly List<EngineSession> _sessions = new List<EngineSession>();
	private readonly List<RamDiskHost> _ramDiskHosts = new List<RamDiskHost>();
	private readonly System.Windows.Shapes.Rectangle[] _heatmapRects = new System.Windows.Shapes.Rectangle[256];
	private readonly Queue<string> _logLines = new Queue<string>();
	private readonly bool _startMinimized;

	private DispatcherTimer? _updateTimer;
	private long _lastIoBytes;
	private DateTime _lastUpdateUtc = DateTime.UtcNow;
	private NotifyIcon? _notifyIcon;
	private ContextMenuStrip? _trayMenu;
	private ToolStripItem? _trayStatusSeparator;
	private ToolStripItem? _trayStatusItem;
	private ToolStripItem? _traySyncItem;
	private ToolStripItem? _trayStopItem;
	private bool _isExplicitExit;
	private int _shutdownExecuted;
	private int _initStarted;
	private string? _customTargetFolder;
	private List<DiskInfo> _cachedDisks = new List<DiskInfo>();
	private AppSettings _settings = new AppSettings();

	public MainWindow() : this(startMinimized: false)
	{
	}

	public MainWindow(bool startMinimized)
	{
		_startMinimized = startMinimized;
		InitializeComponent();
		ApplyAppIcon();
		InitializeHeatmap();
		UpdateWinFspStatus();
		InitializeTray();

		StateChanged += OnStateChanged;
		Closing += OnWindowClosing;
		SystemEvents.SessionEnding += OnSystemSessionEnding;

		if (_startMinimized)
		{
			WindowState = WindowState.Minimized;
			Hide();
		}

		Log("HyperCache X v2.3.0 起動中 (ディスク情報を非同期読み込みしています)...");
		Dispatcher.BeginInvoke(() => _ = InitializeAsync(), DispatcherPriority.Loaded);
	}

	private async Task InitializeAsync()
	{
		if (Interlocked.Exchange(ref _initStarted, 1) != 0)
		{
			return;
		}

		_settings = AppSettingsManager.Load();
		ChkAutoRestore.IsChecked = _settings.AutoRestoreSession;
		ChkAutoStartWindows.IsChecked = AppSettingsManager.IsWindowsAutoStartEnabled();

		await RefreshDisksAndOptionsAsync();

		ApplySavedSettingsToUi(_settings);
		Log("HyperCache X v2.3.0 準備完了 (シャード化L1/L2 + MMF RAMDisk + 非同期先読み)。");

		DiskInfo? preferredRam = DiskUtility.FindPreferredRamDisk(_cachedDisks);
		if (preferredRam != null)
		{
			Log($"既存 RAMDisk を検出しました: {preferredRam.DisplayName} (L1 キャッシュで優先使用します)");
		}

		if (_settings.AutoRestoreSession)
		{
			if (_settings.SavedRamDisks.Count > 0 && WinFspRuntime.GetStatus().State == WinFspInstallState.Installed)
			{
				bool mountedAnyRamDisk = false;
				foreach (RamDiskProfile rp in _settings.SavedRamDisks)
				{
					if (!string.IsNullOrWhiteSpace(rp.MountPoint) && Directory.Exists(rp.MountPoint + "\\"))
					{
						continue;
					}
					if (!string.IsNullOrWhiteSpace(rp.MountPoint) &&
					    CreateAndMountRamDisk(rp.MountPoint, rp.CapacityMb, rp.VolumeLabel, isAutoRestore: true))
					{
						mountedAnyRamDisk = true;
					}
				}
				if (mountedAnyRamDisk)
				{
					await RefreshDisksAndOptionsAsync();
				}
			}

			if (_settings.SavedSessions.Count > 0)
			{
				Log($"前回の構成を自動復元します ({_settings.SavedSessions.Count} セッション)...");
				foreach (SessionProfile sp in _settings.SavedSessions)
				{
					if (!string.IsNullOrWhiteSpace(sp.TargetPath) && Directory.Exists(sp.TargetPath))
					{
						StartEngineFromUi(profileOverride: sp, isAutoRestore: true);
					}
				}
			}
			else if (!string.IsNullOrWhiteSpace(_settings.TargetPath) && Directory.Exists(_settings.TargetPath))
			{
				Log("前回セッションの設定を自動復元してエンジンを起動します...");
				StartEngineFromUi(isAutoRestore: true);
			}
		}
	}

	private void ApplyAppIcon()
	{
		try
		{
			Uri iconUri = new Uri("pack://application:,,,/Assets/hcx.ico", UriKind.Absolute);
			Icon = BitmapFrame.Create(iconUri);
		}
		catch
		{
		}
	}

	private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
	{
		SolidColorBrush brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
		brush.Freeze();
		return brush;
	}

	private void InitializeTray()
	{
		_notifyIcon = new NotifyIcon();
		try
		{
			StreamResourceInfo? sri = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/hcx.ico", UriKind.Absolute));
			_notifyIcon.Icon = sri != null ? new Icon(sri.Stream) : SystemIcons.Shield;
		}
		catch
		{
			_notifyIcon.Icon = SystemIcons.Shield;
		}

		_trayMenu = new ContextMenuStrip();
		_trayMenu.Items.Add("ダッシュボードを表示", null, (_, _) => ShowWindow());
		_trayStatusSeparator = new ToolStripSeparator();
		_trayMenu.Items.Add(_trayStatusSeparator);
		_trayStatusItem = _trayMenu.Items.Add("待機中", null, null);
		_trayStatusItem.Enabled = false;
		_traySyncItem = _trayMenu.Items.Add("今すぐすべて HDD へ同期", null, (_, _) => BtnSyncNow_Click(this, new RoutedEventArgs()));
		_trayStopItem = _trayMenu.Items.Add("加速をすべて停止", null, (_, _) => BtnStop_Click(this, new RoutedEventArgs()));
		_trayMenu.Items.Add(new ToolStripSeparator());
		_trayMenu.Items.Add("完全に終了", null, (_, _) => ExitApp());

		_notifyIcon.ContextMenuStrip = _trayMenu;
		_notifyIcon.Visible = true;
		_notifyIcon.Text = "HyperCache X v2.3.0 - 準備完了";
		_notifyIcon.DoubleClick += (_, _) => ShowWindow();
		RefreshTrayMenu();
	}

	private void RefreshTrayMenu()
	{
		if (_trayStatusItem == null || _trayStatusSeparator == null || _traySyncItem == null || _trayStopItem == null)
		{
			return;
		}

		bool hasAny = _sessions.Count > 0 || _ramDiskHosts.Count > 0;
		bool hasSessions = _sessions.Count > 0;

		_trayStatusSeparator.Visible = hasAny;
		_trayStatusItem.Visible = hasAny;
		_trayStatusItem.Text = $"稼働: 加速{_sessions.Count} / RAMDisk{_ramDiskHosts.Count} - Hit: {HitRateText.Text}";
		_traySyncItem.Visible = hasSessions;
		_trayStopItem.Visible = hasSessions;
	}

	private void OnWindowClosing(object? sender, CancelEventArgs e)
	{
		if (!_isExplicitExit)
		{
			e.Cancel = true;
			Hide();
		}
	}

	private void OnStateChanged(object? sender, EventArgs e)
	{
		if (WindowState == WindowState.Minimized)
		{
			Hide();
		}
	}

	public void RestoreFromTray()
	{
		ShowWindow();
	}

	private void ShowWindow()
	{
		Show();
		WindowState = WindowState.Normal;
		Activate();
	}

	private void OnSystemSessionEnding(object sender, SessionEndingEventArgs e)
	{
		PerformSafeShutdown();
	}

	public void PerformSafeShutdown()
	{
		if (Interlocked.Exchange(ref _shutdownExecuted, 1) != 0)
		{
			return;
		}
		_isExplicitExit = true;
		if (Dispatcher.CheckAccess())
		{
			try { SaveCurrentSettings(); } catch { }
		}
		try
		{
			SystemEvents.SessionEnding -= OnSystemSessionEnding;
		}
		catch
		{
		}

		StopAllSessions();
		UnmountAllRamDisksInternal();

		try
		{
			if (_notifyIcon != null)
			{
				_notifyIcon.Visible = false;
				_notifyIcon.Dispose();
				_notifyIcon = null;
			}
			_trayMenu?.Dispose();
			_trayMenu = null;
		}
		catch
		{
		}
	}

	public void EmergencySafeShutdown()
	{
		PerformSafeShutdown();
	}

	private void ExitApp()
	{
		PerformSafeShutdown();
		System.Windows.Application.Current.Shutdown();
	}

	private async Task RefreshDisksAndOptionsAsync()
	{
		List<DiskInfo> disks = await Task.Run(DiskUtility.GetDisks);
		_cachedDisks = disks;
		PopulateDiskCombos(disks);
	}

	private void PopulateDiskCombos(List<DiskInfo> disks)
	{
		DiskList.ItemsSource = disks;
		SsdList.ItemsSource = disks;

		int hddIndex = disks.FindIndex(d => d.MediaKind == StorageMediaKind.Hdd);
		DiskList.SelectedIndex = hddIndex >= 0 ? hddIndex : (disks.Count > 0 ? 0 : -1);

		DiskInfo? preferredSsd = DiskUtility.FindPreferredSsd(disks);
		if (preferredSsd != null)
		{
			SsdList.SelectedItem = preferredSsd;
		}
		else if (disks.Count > 1)
		{
			SsdList.SelectedIndex = 1;
		}
		else if (disks.Count > 0)
		{
			SsdList.SelectedIndex = 0;
		}

		List<DiskInfo> ramDisks = disks.Where(d => d.MediaKind == StorageMediaKind.RamDisk).ToList();
		L1RamDiskCombo.ItemsSource = ramDisks;
		DiskInfo? preferredRam = DiskUtility.FindPreferredRamDisk(disks);
		if (preferredRam != null)
		{
			L1RamDiskCombo.SelectedItem = preferredRam;
			TxtExistingRamDiskInfo.Text = $"既存 RAMDisk を検出: {preferredRam.DisplayName}\n「自動 (既存RAMDisk優先→RAM)」設定により、L1 キャッシュとして優先使用されます。";
		}
		else
		{
			TxtExistingRamDiskInfo.Text = "既存の RAMDisk は未検出です。\nL1 はプロセス内 RAM を使用するか、下から新しい WinFsp RAMディスクを作成できます。";
		}

		double ramGb = DiskUtility.GetTotalPhysicalMemoryBytes() / (1024.0 * 1024.0 * 1024.0);
		TxtHostRamLabel.Text = $"L1 キャッシュ (搭載RAM {ramGb:F0} GB)";

		RefreshMountPointOptions();
	}

	private void RefreshMountPointOptions()
	{
		List<string> freeLetters = DiskUtility.GetAvailableDriveLetters();

		List<string> accelOptions = new List<string>();
		accelOptions.AddRange(freeLetters);
		accelOptions.Add("マウントなし (直接検証モード)");
		MountPointCombo.ItemsSource = accelOptions;
		if (accelOptions.Count > 0)
		{
			MountPointCombo.SelectedIndex = WinFspRuntime.GetStatus().State == WinFspInstallState.Installed
				? 0
				: accelOptions.Count - 1;
		}

		RamDiskMountCombo.ItemsSource = freeLetters;
		if (freeLetters.Count > 0)
		{
			int mIdx = freeLetters.IndexOf("M:");
			RamDiskMountCombo.SelectedIndex = mIdx >= 0 ? mIdx : (freeLetters.Count > 1 ? 1 : 0);
		}
	}

	private void ApplySavedSettingsToUi(AppSettings settings)
	{
		if (!string.IsNullOrWhiteSpace(settings.TargetPath) && Directory.Exists(settings.TargetPath))
		{
			DiskInfo? matchingDisk = _cachedDisks.FirstOrDefault(d =>
				string.Equals(d.RootPath.TrimEnd('\\'), settings.TargetPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
			if (matchingDisk != null)
			{
				DiskList.SelectedItem = matchingDisk;
				_customTargetFolder = null;
			}
			else
			{
				_customTargetFolder = settings.TargetPath;
				TxtTargetPath.Text = $"カスタムフォルダ: {_customTargetFolder}";
			}
		}

		SelectComboItemByTag(L1BackendCombo, settings.L1BackendMode);
		SelectComboItemByTag(RamSizeCombo, settings.RamSizeMb.ToString());

		if (!string.IsNullOrWhiteSpace(settings.PreferredRamDiskRoot) && L1RamDiskCombo.ItemsSource is IEnumerable<DiskInfo> ramItems)
		{
			DiskInfo? ramMatch = ramItems.FirstOrDefault(d =>
				string.Equals(d.RootPath.TrimEnd('\\'), settings.PreferredRamDiskRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
			if (ramMatch != null)
			{
				L1RamDiskCombo.SelectedItem = ramMatch;
			}
		}

		if (!string.IsNullOrWhiteSpace(settings.SsdRoot))
		{
			DiskInfo? ssdMatch = _cachedDisks.FirstOrDefault(d =>
				string.Equals(d.RootPath.TrimEnd('\\'), settings.SsdRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
			if (ssdMatch != null)
			{
				SsdList.SelectedItem = ssdMatch;
			}
		}

		SelectComboItemByTag(SsdSizeCombo, settings.SsdSizeMb.ToString());
		SelectComboItemByTag(WritePolicyCombo, settings.WritePolicy);
		SelectComboItemByTag(FlushIntervalCombo, settings.FlushIntervalSeconds.ToString());
		FlushIntervalCombo.IsEnabled = !string.Equals(settings.WritePolicy, nameof(WritePolicy.WriteThrough), StringComparison.OrdinalIgnoreCase);

		if (settings.PerformanceModeIndex >= 0 && settings.PerformanceModeIndex < PerformanceMode.Items.Count)
		{
			PerformanceMode.SelectedIndex = settings.PerformanceModeIndex;
		}

		if (!string.IsNullOrWhiteSpace(settings.MountPoint) && MountPointCombo.ItemsSource is IEnumerable<string> mounts)
		{
			string? match = mounts.FirstOrDefault(m => string.Equals(m, settings.MountPoint, StringComparison.OrdinalIgnoreCase));
			if (match != null)
			{
				MountPointCombo.SelectedItem = match;
			}
		}

		SelectComboItemByTag(RamDiskCapacityCombo, settings.RamDiskCapacityMb.ToString());
		if (!string.IsNullOrWhiteSpace(settings.RamDiskLabel))
		{
			TxtRamDiskLabel.Text = settings.RamDiskLabel;
		}
	}

	private SessionProfile BuildCurrentSessionProfileFromUi()
	{
		return new SessionProfile
		{
			TargetPath = _customTargetFolder ?? (DiskList.SelectedItem as DiskInfo)?.RootPath,
			L1BackendMode = (L1BackendCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "AutoPreferRamDisk",
			RamSizeMb = GetSelectedTagLong(RamSizeCombo, 1024),
			PreferredRamDiskRoot = (L1RamDiskCombo.SelectedItem as DiskInfo)?.RootPath,
			SsdRoot = (SsdList.SelectedItem as DiskInfo)?.RootPath,
			SsdSizeMb = GetSelectedTagLong(SsdSizeCombo, 2048),
			WritePolicy = (WritePolicyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "WriteBack",
			MountPoint = MountPointCombo.SelectedItem as string,
			PerformanceModeIndex = PerformanceMode.SelectedIndex,
			FlushIntervalSeconds = (int)GetSelectedTagLong(FlushIntervalCombo, 10)
		};
	}

	private void SaveCurrentSettings()
	{
		SessionProfile uiProfile = BuildCurrentSessionProfileFromUi();
		AppSettings current = new AppSettings
		{
			TargetPath = uiProfile.TargetPath,
			L1BackendMode = uiProfile.L1BackendMode,
			RamSizeMb = uiProfile.RamSizeMb,
			PreferredRamDiskRoot = uiProfile.PreferredRamDiskRoot,
			SsdRoot = uiProfile.SsdRoot,
			SsdSizeMb = uiProfile.SsdSizeMb,
			WritePolicy = uiProfile.WritePolicy,
			MountPoint = uiProfile.MountPoint,
			PerformanceModeIndex = uiProfile.PerformanceModeIndex,
			FlushIntervalSeconds = uiProfile.FlushIntervalSeconds,
			RamDiskMountPoint = RamDiskMountCombo.SelectedItem as string,
			RamDiskCapacityMb = GetSelectedTagLong(RamDiskCapacityCombo, 2048),
			RamDiskLabel = string.IsNullOrWhiteSpace(TxtRamDiskLabel.Text) ? "HCX_RAMDISK" : TxtRamDiskLabel.Text.Trim(),
			AutoStartWithWindows = ChkAutoStartWindows.IsChecked == true,
			AutoRestoreSession = ChkAutoRestore.IsChecked == true,
			SavedSessions = _sessions.Select(s => s.Profile).ToList(),
			SavedRamDisks = _ramDiskHosts
				.Where(h => !string.IsNullOrWhiteSpace(h.MountedPoint))
				.Select(h => new RamDiskProfile
				{
					MountPoint = h.MountedPoint,
					CapacityMb = h.CapacityBytes / (1024L * 1024L),
					VolumeLabel = h.VolumeLabel
				})
				.ToList()
		};
		_settings = current;
		AppSettingsManager.Save(current);
	}

	private static void SelectComboItemByTag(System.Windows.Controls.ComboBox combo, string? tag)
	{
		if (string.IsNullOrWhiteSpace(tag))
		{
			return;
		}
		foreach (object itemObj in combo.Items)
		{
			if (itemObj is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
			{
				combo.SelectedItem = item;
				break;
			}
		}
	}

	private void InitializeHeatmap()
	{
		HeatmapGrid.Children.Clear();
		for (int i = 0; i < 256; i++)
		{
			System.Windows.Shapes.Rectangle rect = new System.Windows.Shapes.Rectangle
			{
				Margin = new Thickness(1.0),
				Fill = BrushCold,
				RadiusX = 3.0,
				RadiusY = 3.0
			};
			_heatmapRects[i] = rect;
			HeatmapGrid.Children.Add(rect);
		}
	}

	private void DiskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		_customTargetFolder = null;
		if (DiskList.SelectedItem is DiskInfo d)
		{
			TxtTargetPath.Text = $"ターゲット: {d.RootPath}";
		}
	}

	private void L1BackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (L1RamDiskCombo == null || RamSizeCombo == null)
		{
			return;
		}
		string modeTag = (L1BackendCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "AutoPreferRamDisk";
		L1RamDiskCombo.IsEnabled = modeTag is "AutoPreferRamDisk" or "RamDiskFile";
		RamSizeCombo.IsEnabled = modeTag != "Disabled";
	}

	private void WritePolicyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (FlushIntervalCombo == null || WritePolicyCombo == null)
		{
			return;
		}
		string policyTag = (WritePolicyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "WriteBack";
		FlushIntervalCombo.IsEnabled = !string.Equals(policyTag, "WriteThrough", StringComparison.OrdinalIgnoreCase);
	}

	private void BtnBrowseTarget_Click(object sender, RoutedEventArgs e)
	{
		using FolderBrowserDialog dlg = new FolderBrowserDialog
		{
			Description = "加速対象にする HDD 上のフォルダまたはドライブを選択してください",
			UseDescriptionForTitle = true
		};
		if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && Directory.Exists(dlg.SelectedPath))
		{
			_customTargetFolder = dlg.SelectedPath;
			TxtTargetPath.Text = $"カスタムフォルダ: {_customTargetFolder}";
		}
	}

	private void UpdateWinFspStatus()
	{
		WinFspStatusInfo status = WinFspRuntime.GetStatus();
		if (status.State == WinFspInstallState.Installed)
		{
			TxtWinFspStatus.Text = status.Summary;
			TxtWinFspStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
			BtnSetupWinFsp.Content = "WinFsp 状態を更新";
		}
		else
		{
			TxtWinFspStatus.Text = "未インストール (仮想ドライブ不可)";
			TxtWinFspStatus.Foreground = System.Windows.Media.Brushes.Orange;
			BtnSetupWinFsp.Content = "WinFsp をインストール (winget)";
		}
	}

	private void BtnSetupWinFsp_Click(object sender, RoutedEventArgs e)
	{
		WinFspStatusInfo status = WinFspRuntime.GetStatus();
		if (status.State == WinFspInstallState.Installed)
		{
			UpdateWinFspStatus();
			RefreshMountPointOptions();
			Log($"WinFsp 検出済み: {status.DllPath} (v{status.Version})");
			return;
		}

		MessageBoxResult confirm = System.Windows.MessageBox.Show(
			"仮想ドライブ機能 (X:\\ マウント / 内蔵 RAMディスク) を使うには WinFsp が必要です。\n\nwinget を使って WinFsp をインストールしますか？\n(インストール後、もう一度このボタンを押して状態を更新してください)",
			"WinFsp セットアップ",
			MessageBoxButton.YesNo,
			MessageBoxImage.Information);

		if (confirm == MessageBoxResult.Yes)
		{
			if (WinFspRuntime.LaunchWinGetInstall())
			{
				Log("winget による WinFsp インストールを起動しました。完了後に状態更新を押してください。");
			}
			else
			{
				Log("winget の起動に失敗しました。手動で 'winget install WinFsp.WinFsp' を実行してください。");
			}
		}
		UpdateWinFspStatus();
	}

	private void BtnStart_Click(object sender, RoutedEventArgs e)
	{
		StartEngineFromUi();
	}

	private void StartEngineFromUi(SessionProfile? profileOverride = null, bool isAutoRestore = false)
	{
		try
		{
			SessionProfile profile = profileOverride ?? BuildCurrentSessionProfileFromUi();
			string? targetPath = profile.TargetPath;
			if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
			{
				if (!isAutoRestore)
				{
					System.Windows.MessageBox.Show("有効な加速ターゲット (HDD またはフォルダ) を選択してください。");
				}
				return;
			}
			string normalizedTarget = Path.GetFullPath(targetPath).TrimEnd('\\');
			if (Path.GetFileName(normalizedTarget).Equals(".hcx", StringComparison.OrdinalIgnoreCase) ||
			    normalizedTarget.Contains(@"\.hcx\", StringComparison.OrdinalIgnoreCase))
			{
				if (!isAutoRestore)
				{
					System.Windows.MessageBox.Show("内部キャッシュ用フォルダ (.hcx) を加速ターゲットに指定することはできません。");
				}
				return;
			}

			if (_sessions.Any(s => string.Equals(Path.GetFullPath(s.Engine.TargetRootPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)))
			{
				if (!isAutoRestore)
				{
					System.Windows.MessageBox.Show("このターゲットは既に稼働中です。");
				}
				return;
			}

			string l1Tag = profile.L1BackendMode;
			L1BackendMode l1Mode = l1Tag switch
			{
				"RamDiskFile" => L1BackendMode.RamDiskFile,
				"InProcessRam" => L1BackendMode.InProcessRam,
				"Disabled" => L1BackendMode.Disabled,
				_ => L1BackendMode.AutoPreferRamDisk
			};

			long l1Mb = l1Mode == L1BackendMode.Disabled ? 0 : profile.RamSizeMb;
			long l2Mb = profile.SsdSizeMb;
			if (l1Mb == 0 && l2Mb == 0)
			{
				if (!isAutoRestore)
				{
					System.Windows.MessageBox.Show("L1 と L2 の両方を OFF にすることはできません。どちらか一方を有効にしてください。");
				}
				return;
			}

			string? ramDiskDir = null;
			if (l1Mode == L1BackendMode.AutoPreferRamDisk || l1Mode == L1BackendMode.RamDiskFile)
			{
				if (!string.IsNullOrWhiteSpace(profile.PreferredRamDiskRoot) && Directory.Exists(profile.PreferredRamDiskRoot))
				{
					ramDiskDir = Path.Combine(profile.PreferredRamDiskRoot, ".hcx");
				}
				else if (L1RamDiskCombo.SelectedItem is DiskInfo selectedRamDisk)
				{
					ramDiskDir = Path.Combine(selectedRamDisk.RootPath, ".hcx");
				}
				else if (l1Mode == L1BackendMode.RamDiskFile)
				{
					if (!isAutoRestore)
					{
						System.Windows.MessageBox.Show("「RAMDisk ファイルのみ」が選択されていますが、利用可能な RAMDisk が見つかりません。");
					}
					return;
				}
			}

			long totalRam = DiskUtility.GetTotalPhysicalMemoryBytes();
			bool willUseInProcessRam = l1Mb > 0 && (l1Mode == L1BackendMode.InProcessRam || (l1Mode == L1BackendMode.AutoPreferRamDisk && ramDiskDir == null));
			long plannedInProcessBytes = willUseInProcessRam ? l1Mb * 1024L * 1024L : 0L;
			long cumulativeInProcessBytes = _sessions.Sum(s => s.InProcessRamBytes) + plannedInProcessBytes;
			if (willUseInProcessRam && cumulativeInProcessBytes > totalRam / 2)
			{
				if (!isAutoRestore)
				{
					MessageBoxResult ramWarn = System.Windows.MessageBox.Show(
						$"プロセス内 L1 RAM キャッシュ合計 ({cumulativeInProcessBytes / (1024.0 * 1024.0 * 1024.0):F1} GB) が物理メモリ総量の半分を超えています。\n続行しますか？",
						"L1 RAM 容量の確認",
						MessageBoxButton.YesNo,
						MessageBoxImage.Warning);
					if (ramWarn != MessageBoxResult.Yes)
					{
						return;
					}
				}
				else
				{
					Log($"[警告] プロセス内 L1 RAM 合計 ({cumulativeInProcessBytes / (1024 * 1024)} MB) が搭載 RAM の半分を超えています。");
				}
			}

			string ssdDir = string.Empty;
			if (l2Mb > 0)
			{
				string? ssdRoot = profile.SsdRoot;
				if (string.IsNullOrWhiteSpace(ssdRoot) && SsdList.SelectedItem is DiskInfo ssdDisk)
				{
					ssdRoot = ssdDisk.RootPath;
				}
				if (string.IsNullOrWhiteSpace(ssdRoot) || !Directory.Exists(ssdRoot))
				{
					if (!isAutoRestore)
					{
						System.Windows.MessageBox.Show("L2 キャッシュ先の SSD を選択してください。");
					}
					return;
				}
				ssdDir = Path.Combine(ssdRoot, ".hcx");
			}

			string selectedMount = profile.MountPoint ?? string.Empty;
			bool shouldMount = selectedMount.Length == 2 && selectedMount[1] == ':';
			if (shouldMount && WinFspRuntime.GetStatus().State != WinFspInstallState.Installed)
			{
				if (!isAutoRestore)
				{
					System.Windows.MessageBox.Show(
						"仮想ドライブのマウントには WinFsp が必要です。\n[WinFsp をインストール] を行うか、「マウントなし (直接検証モード)」を選択してください。",
						"WinFsp 未インストール",
						MessageBoxButton.OK,
						MessageBoxImage.Warning);
				}
				return;
			}

			WritePolicy policy = string.Equals(profile.WritePolicy, "WriteThrough", StringComparison.OrdinalIgnoreCase)
				? WritePolicy.WriteThrough
				: WritePolicy.WriteBack;

			(TimeSpan aiInterval, double ssdThresh, double ramThresh) = profile.PerformanceModeIndex switch
			{
				0 => (TimeSpan.FromSeconds(10), 2.5, 4.0),
				2 => (TimeSpan.FromSeconds(1), 1.0, 1.5),
				_ => (TimeSpan.FromSeconds(3), 1.2, 2.0)
			};

			int flushSeconds = Math.Clamp(profile.FlushIntervalSeconds, 1, 300);
			CacheOptions options = new CacheOptions
			{
				TargetRootPath = targetPath,
				SsdCacheDirectory = ssdDir,
				RamDiskCacheDirectory = ramDiskDir,
				L1BackendMode = l1Mode,
				MountPoint = shouldMount ? selectedMount : string.Empty,
				RamCacheSizeBytes = l1Mb * 1024L * 1024L,
				SsdCacheSizeBytes = l2Mb * 1024L * 1024L,
				WritePolicy = policy,
				FlushInterval = TimeSpan.FromSeconds(flushSeconds),
				AiInterval = aiInterval,
				SsdPromoteScore = ssdThresh,
				RamPromoteScore = ramThresh
			};

			HybridCacheEngine engine = new HybridCacheEngine(options);
			engine.OnLog += Log;
			if (!engine.Start())
			{
				engine.Dispose();
				return;
			}

			VirtualDriveHost? driveHost = null;
			if (shouldMount)
			{
				try
				{
					driveHost = new VirtualDriveHost(engine, $"HCX ({targetPath.TrimEnd('\\')})");
					driveHost.Mount(selectedMount);
					Log($"仮想ドライブをマウントしました: {selectedMount}\\ -> {targetPath}");
				}
				catch (Exception mountEx)
				{
					driveHost?.Dispose();
					engine.Dispose();
					if (!isAutoRestore)
					{
						System.Windows.MessageBox.Show($"仮想ドライブのマウントに失敗しました: {mountEx.Message}");
					}
					else
					{
						Log($"[警告] 自動マウント失敗 ({selectedMount}): {mountEx.Message}");
					}
					return;
				}
			}

			string l1Summary = engine.GetStatsSnapshot().L1BackendSummary;
			bool actualInProcess = l1Summary.StartsWith("In-Process RAM", StringComparison.Ordinal);
			string l1TagShort = l1Summary.StartsWith("RAMDisk", StringComparison.Ordinal)
				? "RAMDisk"
				: (l1Mb > 0 ? "RAM" : "OFF");
			string label = shouldMount
				? $"{targetPath} → {selectedMount}\\ [L1:{l1TagShort} / L2:{(l2Mb > 0 ? "SSD" : "OFF")}]"
				: $"{targetPath} (直接) [L1:{l1TagShort}]";

			_sessions.Add(new EngineSession
			{
				Engine = engine,
				VirtualDrive = driveHost,
				Profile = profile,
				InProcessRamBytes = actualInProcess ? l1Mb * 1024L * 1024L : 0L,
				DisplayLabel = label
			});

			BtnStop.IsEnabled = true;
			BtnSyncNow.IsEnabled = true;
			BtnClearL2Cache.IsEnabled = false;
			StatusDot.Fill = System.Windows.Media.Brushes.LimeGreen;
			TxtStatusLabel.Text = "オンライン";
			TxtStatusLabel.Foreground = System.Windows.Media.Brushes.LimeGreen;

			if (!isAutoRestore)
			{
				SaveCurrentSettings();
			}
			EnsureTimerRunning();
			UpdateStats();
			RefreshMountPointOptions();
			RefreshTrayMenu();
		}
		catch (Exception ex)
		{
			if (!isAutoRestore)
			{
				System.Windows.MessageBox.Show($"起動エラー: {ex.Message}");
			}
			else
			{
				Log($"[警告] 自動復元起動エラー: {ex.Message}");
			}
		}
	}

	private void BtnClearL2Cache_Click(object sender, RoutedEventArgs e)
	{
		if (_sessions.Count > 0)
		{
			System.Windows.MessageBox.Show("キャッシュを消去する前に、稼働中のエンジンを停止してください。", "L2 キャッシュ消去", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}
		if (SsdList.SelectedItem is not DiskInfo ssdDisk)
		{
			System.Windows.MessageBox.Show("消去対象の L2 SSD ドライブを選択してください。");
			return;
		}

		string ssdHcxDir = Path.Combine(ssdDisk.RootPath, ".hcx");
		MessageBoxResult confirm = System.Windows.MessageBox.Show(
			$"{ssdHcxDir} 内の永続 L2 キャッシュファイル (`cache_*.bin` / `cache_metadata_*.dat`) を削除します。\n(元の HDD 上のファイルは影響を受けません)\n\n続行しますか？",
			"L2 キャッシュ消去の確認",
			MessageBoxButton.YesNo,
			MessageBoxImage.Question);
		if (confirm != MessageBoxResult.Yes)
		{
			return;
		}

		int deleted = SsdFileCacheTier.DeleteCacheFiles(ssdHcxDir);
		Log($"[L2 キャッシュ消去] {ssdHcxDir} から {deleted} 件のキャッシュファイルを削除しました。");
		System.Windows.MessageBox.Show($"L2 キャッシュファイル {deleted} 件を削除しました。", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
	}

	private void ChkAutoRestore_Click(object sender, RoutedEventArgs e)
	{
		SaveCurrentSettings();
		Log(ChkAutoRestore.IsChecked == true
			? "設定の自動保存・起動時復元を有効にしました。"
			: "起動時の自動セッション復元を無効にしました。");
	}

	private void ChkAutoStartWindows_Click(object sender, RoutedEventArgs e)
	{
		bool enable = ChkAutoStartWindows.IsChecked == true;
		if (!AppSettingsManager.SetWindowsAutoStart(enable))
		{
			ChkAutoStartWindows.IsChecked = !enable;
			System.Windows.MessageBox.Show("Windows スタートアップ登録の変更に失敗しました。", "設定エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}
		SaveCurrentSettings();
		Log(enable
			? "Windows 起動時のトレイ常駐 (--minimized) を登録しました。"
			: "Windows 起動時の自動起動を解除しました。");
	}

	private async void BtnCreateRamDisk_Click(object sender, RoutedEventArgs e)
	{
		if (WinFspRuntime.GetStatus().State != WinFspInstallState.Installed)
		{
			System.Windows.MessageBox.Show(
				"内蔵 RAMディスクのマウントには WinFsp が必要です。\n「ハイブリッド加速」タブの [WinFsp をインストール] から導入してください。",
				"WinFsp 未インストール",
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
			return;
		}

		string? mountPoint = RamDiskMountCombo.SelectedItem as string;
		if (string.IsNullOrWhiteSpace(mountPoint) || mountPoint.Length != 2 || mountPoint[1] != ':')
		{
			System.Windows.MessageBox.Show("利用可能なドライブ文字がありません。");
			return;
		}

		long capMb = GetSelectedTagLong(RamDiskCapacityCombo, 2048);
		string label = string.IsNullOrWhiteSpace(TxtRamDiskLabel.Text) ? "HCX_RAMDISK" : TxtRamDiskLabel.Text.Trim();
		if (CreateAndMountRamDisk(mountPoint, capMb, label, isAutoRestore: false))
		{
			SaveCurrentSettings();
			EnsureTimerRunning();
			await RefreshDisksAndOptionsAsync();
			UpdateStats();
			RefreshTrayMenu();
		}
	}

	private bool CreateAndMountRamDisk(string mountPoint, long capMb, string label, bool isAutoRestore)
	{
		try
		{
			if (WinFspRuntime.GetStatus().State != WinFspInstallState.Installed)
			{
				return false;
			}
			long capBytes = capMb * 1024L * 1024L;
			long totalRam = DiskUtility.GetTotalPhysicalMemoryBytes();
			if (capBytes > totalRam / 2 && !isAutoRestore)
			{
				MessageBoxResult warn = System.Windows.MessageBox.Show(
					$"指定容量 ({capMb / 1024.0:F1} GB) が物理メモリ総量の半分を超えています。続行しますか？",
					"RAMディスク容量の確認",
					MessageBoxButton.YesNo,
					MessageBoxImage.Warning);
				if (warn != MessageBoxResult.Yes)
				{
					return false;
				}
			}

			RamDiskHost host = new RamDiskHost(capBytes, label);
			host.Mount(mountPoint);
			_ramDiskHosts.Add(host);

			BtnUnmountAllRamDisks.IsEnabled = true;
			Log($"内蔵 RAMディスクを作成・マウントしました: {mountPoint}\\ ({capMb} MB, Label={label})");
			return true;
		}
		catch (Exception ex)
		{
			if (!isAutoRestore)
			{
				System.Windows.MessageBox.Show($"RAMディスク作成エラー: {ex.Message}");
			}
			else
			{
				Log($"[警告] 内蔵 RAMディスクの自動復元に失敗 ({mountPoint}): {ex.Message}");
			}
			return false;
		}
	}

	private async void BtnUnmountAllRamDisks_Click(object sender, RoutedEventArgs e)
	{
		UnmountAllRamDisksInternal();
		await RefreshDisksAndOptionsAsync();
		UpdateStats();
		RefreshTrayMenu();
	}

	private void UnmountAllRamDisksInternal()
	{
		if (_ramDiskHosts.Count == 0)
		{
			return;
		}
		foreach (RamDiskHost host in _ramDiskHosts)
		{
			try { host.Dispose(); } catch { }
		}
		_ramDiskHosts.Clear();
		if (Dispatcher.CheckAccess())
		{
			BtnUnmountAllRamDisks.IsEnabled = false;
		}
		Log("作成した内蔵 RAMディスクをすべて解除し、メモリを解放しました。");
	}

	private void EnsureTimerRunning()
	{
		if (_updateTimer == null)
		{
			_lastUpdateUtc = DateTime.UtcNow;
			_updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
			_updateTimer.Tick += (_, _) => UpdateStats();
			_updateTimer.Start();
		}
	}

	private void UpdateStats()
	{
		UpdateRamDiskSummary();

		if (_sessions.Count == 0)
		{
			HitRateText.Text = "0.0%";
			HitDetailText.Text = "L1: 0.0% / L2: 0.0%";
			CachedBlocksText.Text = "0 MB";
			CachedDetailText.Text = "L1: 0 / L2: 0 blk";
			AiScoreText.Text = "0.00";
			DirtyBlocksText.Text = "Dirty: 0 blk";
			SeqBypassText.Text = "Seq除外: 0";
			TxtThroughput.Text = "0.0 MB/s";
			TxtStats.Text = _ramDiskHosts.Count > 0 ? $"内蔵 RAMDisk {_ramDiskHosts.Count} 件稼働中" : "待機中...";
			TxtActiveMappings.Text = _ramDiskHosts.Count > 0
				? $"RAMDisk: {string.Join(", ", _ramDiskHosts.Select(r => $"{r.MountedPoint}\\ ({r.CapacityBytes / (1024 * 1024)}MB)"))}"
				: "なし（設定を選択して「エンジンの起動」を押してください）";
			for (int i = 0; i < 256; i++)
			{
				_heatmapRects[i].Fill = BrushCold;
			}
			RefreshTrayMenu();
			return;
		}

		List<CacheStatsSnapshot> snapshots = _sessions.Select(s => s.Engine.GetStatsSnapshot()).ToList();

		DateTime now = DateTime.UtcNow;
		double dt = Math.Max(0.1, (now - _lastUpdateUtc).TotalSeconds);
		long totalIoBytes = snapshots.Sum(s => s.TotalIoBytes);
		double mbPerSec = Math.Max(0.0, (totalIoBytes - _lastIoBytes) / 1024.0 / 1024.0 / dt);
		_lastIoBytes = totalIoBytes;
		_lastUpdateUtc = now;

		long ramHits = snapshots.Sum(s => s.RamHits);
		long ssdHits = snapshots.Sum(s => s.SsdHits);
		long misses = snapshots.Sum(s => s.HddMisses);
		long lookups = ramHits + ssdHits + misses;
		double hitRate = lookups == 0 ? 0.0 : (double)(ramHits + ssdHits) * 100.0 / lookups;
		double ramShare = lookups == 0 ? 0.0 : (double)ramHits * 100.0 / lookups;
		double ssdShare = lookups == 0 ? 0.0 : (double)ssdHits * 100.0 / lookups;

		int ramBlocks = snapshots.Sum(s => s.RamCachedBlocks);
		int ssdBlocks = snapshots.Sum(s => s.SsdCachedBlocks);
		long usedMb = snapshots.Sum(s => s.RamUsedBytes + s.SsdUsedBytes) / (1024 * 1024);
		int dirtyBlocks = snapshots.Sum(s => s.DirtyBlocks);
		long seqBypasses = snapshots.Sum(s => s.SequentialBypasses);
		long prefetched = snapshots.Sum(s => s.PrefetchedBlocks);
		double avgAi = snapshots.Average(s => s.AverageAiScore);

		TxtThroughput.Text = $"{mbPerSec:F1} MB/s";
		HitRateText.Text = $"{hitRate:F1}%";
		HitDetailText.Text = $"L1: {ramShare:F1}% / L2: {ssdShare:F1}%";
		CachedBlocksText.Text = $"{usedMb:N0} MB";
		CachedDetailText.Text = $"L1: {ramBlocks:N0} / L2: {ssdBlocks:N0} blk";
		AiScoreText.Text = $"{avgAi:F2}";
		DirtyBlocksText.Text = $"Dirty: {dirtyBlocks:N0} blk";
		SeqBypassText.Text = $"Seq除外: {seqBypasses:N0} | 先読: {prefetched:N0}";

		double totalReadMb = snapshots.Sum(s => s.TotalReadBytes) / (1024.0 * 1024.0);
		double totalWriteMb = snapshots.Sum(s => s.TotalWriteBytes) / (1024.0 * 1024.0);
		TxtStats.Text = $"稼働中: {_sessions.Count} | 累計読込: {totalReadMb:F1} MB | 累計書込: {totalWriteMb:F1} MB";
		TxtActiveMappings.Text = string.Join(" | ", _sessions.Select(s => s.DisplayLabel));

		for (int i = 0; i < 256; i++)
		{
			HeatmapCell merged = default;
			foreach (CacheStatsSnapshot snap in snapshots)
			{
				HeatmapCell c = snap.HeatmapCells[i];
				if (c.HasDirty)
				{
					merged = new HeatmapCell { Tier = c.Tier, Intensity = Math.Max(merged.Intensity, c.Intensity), HasDirty = true, HitCount = merged.HitCount + c.HitCount };
				}
				else if (c.Tier == CacheTier.RamL1 || (c.Tier == CacheTier.SsdL2 && merged.Tier == CacheTier.None))
				{
					merged = new HeatmapCell { Tier = c.Tier, Intensity = Math.Max(merged.Intensity, c.Intensity), HasDirty = merged.HasDirty, HitCount = merged.HitCount + c.HitCount };
				}
			}

			_heatmapRects[i].Fill = merged.HasDirty
				? BrushDirty
				: merged.Tier switch
				{
					CacheTier.RamL1 => merged.Intensity >= 0.5 ? BrushRamHot : BrushRamLow,
					CacheTier.SsdL2 => merged.Intensity >= 0.5 ? BrushSsdHot : BrushSsdLow,
					_ => BrushCold
				};
		}

		if (_notifyIcon != null)
		{
			_notifyIcon.Text = $"HCX v2.3.0 - {_sessions.Count}件稼働 (Hit: {hitRate:F1}%)";
		}
		RefreshTrayMenu();
	}

	private void UpdateRamDiskSummary()
	{
		if (_ramDiskHosts.Count == 0)
		{
			TxtActiveRamDisks.Text = "なし";
			return;
		}
		List<string> lines = new List<string>();
		foreach (RamDiskHost host in _ramDiskHosts)
		{
			RamDiskStats s = host.Store.GetStats();
			lines.Add($"{host.MountedPoint}\\ [{host.VolumeLabel}] — 使用: {s.UsedBytes / (1024.0 * 1024.0):F1} / {s.CapacityBytes / (1024.0 * 1024.0):F0} MB (ファイル: {s.FileCount}, フォルダ: {s.DirectoryCount})");
		}
		TxtActiveRamDisks.Text = string.Join("\n", lines);
	}

	private void BtnSyncNow_Click(object sender, RoutedEventArgs e)
	{
		if (_sessions.Count == 0)
		{
			return;
		}
		int totalSynced = 0;
		foreach (EngineSession s in _sessions)
		{
			totalSynced += s.Engine.SyncAll();
		}
		Log($"[手動同期] {totalSynced} 個の Dirty ブロックを HDD へフラッシュしました。");
		UpdateStats();
	}

	private async void BtnBenchmark_Click(object sender, RoutedEventArgs e)
	{
		string targetRoot = _customTargetFolder
			?? (DiskList.SelectedItem as DiskInfo)?.RootPath
			?? Path.GetTempPath();
		string ssdRoot = (SsdList.SelectedItem as DiskInfo)?.RootPath ?? Path.GetTempPath();
		string l1Tag = (L1BackendCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "AutoPreferRamDisk";
		string? ramDiskRoot = (l1Tag is "AutoPreferRamDisk" or "RamDiskFile")
			? (L1RamDiskCombo.SelectedItem as DiskInfo)?.RootPath
			: null;
		long ramBytes = Math.Max(16L, GetSelectedTagLong(RamSizeCombo, 256)) * 1024L * 1024L;
		long ssdBytes = Math.Max(16L, GetSelectedTagLong(SsdSizeCombo, 1024)) * 1024L * 1024L;

		BtnBenchmark.IsEnabled = false;
		Log($"[Bench] 実測ベンチマークを開始します (Target={targetRoot}, L1={(ramDiskRoot ?? "In-Process RAM")}, L2={ssdRoot})...");
		try
		{
			BenchmarkResult result = await Task.Run(() =>
				CacheBenchmarkRunner.Run(targetRoot, ssdRoot, ramBytes, ssdBytes, Log, ramDiskRoot));

			System.Windows.MessageBox.Show(
				$"実測ベンチマーク完了\n\n" +
				$"Cold Read (HDD): {result.ColdReadMBps:F1} MB/s ({result.ColdReadMs:F2} ms)\n" +
				$"Hot Read (L1/L2): {result.HotReadMBps:F1} MB/s ({result.HotReadMs:F2} ms)\n" +
				$"高速化倍率: {result.SpeedupFactor:F1}x\n" +
				$"キャッシュヒット率: {result.HitRatePercent:F1}% (RAM: {result.RamHits}, SSD: {result.SsdHits})\n" +
				$"連続アクセス除外: {result.SequentialBypasses}\n" +
				$"データ整合性検証: {(result.VerifiedIntegrity ? "合格 (100%一致)" : "不一致あり")}",
				"HyperCache X ベンチマーク結果",
				MessageBoxButton.OK,
				result.VerifiedIntegrity ? MessageBoxImage.Information : MessageBoxImage.Warning);
		}
		catch (Exception ex)
		{
			Log($"[Bench エラー] {ex.Message}");
			System.Windows.MessageBox.Show($"ベンチマーク実行エラー: {ex.Message}");
		}
		finally
		{
			BtnBenchmark.IsEnabled = true;
		}
	}

	private void BtnStop_Click(object sender, RoutedEventArgs e)
	{
		StopAllSessions();
		SaveCurrentSettings();
		UpdateStats();
		RefreshMountPointOptions();
		RefreshTrayMenu();
	}

	private void StopAllSessions()
	{
		foreach (EngineSession session in _sessions)
		{
			try { session.Dispose(); } catch { }
		}
		_sessions.Clear();
		if (Dispatcher.CheckAccess())
		{
			if (_ramDiskHosts.Count == 0)
			{
				_updateTimer?.Stop();
				_updateTimer = null;
			}
			_lastIoBytes = 0;

			BtnStop.IsEnabled = false;
			BtnSyncNow.IsEnabled = false;
			BtnClearL2Cache.IsEnabled = true;
			StatusDot.Fill = System.Windows.Media.Brushes.Crimson;
			TxtStatusLabel.Text = "停止中";
			TxtStatusLabel.Foreground = System.Windows.Media.Brushes.Crimson;
		}
		if (_notifyIcon != null)
		{
			_notifyIcon.Text = "HyperCache X v2.3.0 - 準備完了";
		}
		Log("すべての仮想ドライブを安全にアンマウントし、HDD 同期を完了しました。");
	}

	private void Log(string message)
	{
		if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
		{
			return;
		}

		string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
		Dispatcher.BeginInvoke(() =>
		{
			if (_logLines.Count < MaxLogLines)
			{
				_logLines.Enqueue(line);
				LogOutput.AppendText(line + "\n");
			}
			else
			{
				_logLines.Enqueue(line);
				while (_logLines.Count > MaxLogLines)
				{
					_logLines.Dequeue();
				}
				LogOutput.Text = string.Join("\n", _logLines) + "\n";
			}
			LogScroller.ScrollToEnd();
		});
	}

	private static long GetSelectedTagLong(System.Windows.Controls.ComboBox combo, long defaultVal)
	{
		if (combo.SelectedItem is ComboBoxItem item && long.TryParse(item.Tag?.ToString(), out long val))
		{
			return val;
		}
		return defaultVal;
	}
}
