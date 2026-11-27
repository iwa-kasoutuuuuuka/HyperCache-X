using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace HCX.Core;

public sealed class AppSettings
{
	public string? TargetPath { get; set; }
	public string L1BackendMode { get; set; } = "AutoPreferRamDisk";
	public long RamSizeMb { get; set; } = 1024;
	public string? PreferredRamDiskRoot { get; set; }
	public string? SsdRoot { get; set; }
	public long SsdSizeMb { get; set; } = 2048;
	public string WritePolicy { get; set; } = "WriteBack";
	public string? MountPoint { get; set; }
	public int PerformanceModeIndex { get; set; } = 1;
	public string? RamDiskMountPoint { get; set; }
	public long RamDiskCapacityMb { get; set; } = 2048;
	public string RamDiskLabel { get; set; } = "HCX_RAMDISK";
	public bool AutoStartWithWindows { get; set; }
	public bool AutoRestoreSession { get; set; }
}

/// <summary>
/// Persists HyperCache X user configuration atomically in %LOCALAPPDATA%\HyperCacheX\settings.json
/// and manages HKCU Run startup registration.
/// </summary>
public static class AppSettingsManager
{
	private const string RunSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
	private const string RunValueName = "HyperCacheX";

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	public static string DefaultSettingsPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"HyperCacheX",
		"settings.json");

	public static AppSettings Load(string? path = null)
	{
		string target = string.IsNullOrWhiteSpace(path) ? DefaultSettingsPath : path;
		try
		{
			if (!File.Exists(target))
			{
				return new AppSettings();
			}
			string json = File.ReadAllText(target);
			if (string.IsNullOrWhiteSpace(json))
			{
				return new AppSettings();
			}
			return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
		}
		catch
		{
			return new AppSettings();
		}
	}

	public static void Save(AppSettings settings, string? path = null)
	{
		if (settings == null)
		{
			throw new ArgumentNullException(nameof(settings));
		}
		string target = string.IsNullOrWhiteSpace(path) ? DefaultSettingsPath : path;
		string? dir = Path.GetDirectoryName(target);
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		string temp = target + ".tmp";
		string json = JsonSerializer.Serialize(settings, JsonOptions);
		File.WriteAllText(temp, json);
		File.Move(temp, target, overwrite: true);
	}

	public static bool IsWindowsAutoStartEnabled()
	{
		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: false);
			return key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
		}
		catch
		{
			return false;
		}
	}

	public static bool SetWindowsAutoStart(bool enabled, string? exePath = null)
	{
		try
		{
			using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: true);
			if (key == null)
			{
				return false;
			}
			if (enabled)
			{
				string path = string.IsNullOrWhiteSpace(exePath)
					? (Environment.ProcessPath ?? string.Empty)
					: exePath;
				if (string.IsNullOrWhiteSpace(path))
				{
					return false;
				}
				key.SetValue(RunValueName, $"\"{path}\" --minimized");
			}
			else
			{
				key.DeleteValue(RunValueName, throwOnMissingValue: false);
			}
			return true;
		}
		catch
		{
			return false;
		}
	}
}
