using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace HCX.Core;

public sealed class SessionProfile
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
	public int FlushIntervalSeconds { get; set; } = 10;

	public WorkloadPreset WorkloadPreset { get; set; } = WorkloadPreset.Balanced;

	public bool EnableBatterySafeMode { get; set; } = true;
}

public sealed class RamDiskProfile
{
	public string? MountPoint { get; set; }
	public long CapacityMb { get; set; } = 2048;
	public string VolumeLabel { get; set; } = "HCX_RAMDISK";
	public string? BackupDirectory { get; set; }
	public bool AutoPersist { get; set; }
}

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
	public int FlushIntervalSeconds { get; set; } = 10;

	public WorkloadPreset WorkloadPreset { get; set; } = WorkloadPreset.Balanced;

	public bool EnableBatterySafeMode { get; set; } = true;
	public string? RamDiskMountPoint { get; set; }
	public long RamDiskCapacityMb { get; set; } = 2048;
	public string RamDiskLabel { get; set; } = "HCX_RAMDISK";
	public bool AutoStartWithWindows { get; set; }
	public bool AutoRestoreSession { get; set; }
	public List<SessionProfile> SavedSessions { get; set; } = new List<SessionProfile>();
	public List<RamDiskProfile> SavedRamDisks { get; set; } = new List<RamDiskProfile>();
}

/// <summary>
/// Persists HyperCache X user configuration atomically in %LOCALAPPDATA%\HyperCacheX\settings.json
/// and manages HKCU Run startup registration with strict path/value sanitization.
/// </summary>
public static class AppSettingsManager
{
	private const string RunSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
	private const string RunValueName = "HyperCacheX";
	private const long MaxCacheSizeMb = 1_048_576; // 1 TB upper clamp
	private const int MaxProfiles = 16;

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
			AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
			return loaded == null ? new AppSettings() : Sanitize(loaded);
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
		AppSettings sanitized = Sanitize(settings);
		string target = string.IsNullOrWhiteSpace(path) ? DefaultSettingsPath : path;
		string? dir = Path.GetDirectoryName(target);
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		string temp = target + ".tmp";
		string json = JsonSerializer.Serialize(sanitized, JsonOptions);
		File.WriteAllText(temp, json);
		File.Move(temp, target, overwrite: true);
	}

	public static AppSettings Sanitize(AppSettings raw)
	{
		List<SessionProfile> cleanSessions = new List<SessionProfile>();
		if (raw.SavedSessions != null)
		{
			foreach (SessionProfile sp in raw.SavedSessions)
			{
				if (sp == null)
				{
					continue;
				}
				string? targetPath = SanitizeFsDirectory(sp.TargetPath, forbidInternalHcx: true);
				if (string.IsNullOrWhiteSpace(targetPath))
				{
					continue;
				}
				cleanSessions.Add(new SessionProfile
				{
					TargetPath = targetPath,
					L1BackendMode = SanitizeL1Mode(sp.L1BackendMode),
					RamSizeMb = Math.Clamp(sp.RamSizeMb, 0L, MaxCacheSizeMb),
					PreferredRamDiskRoot = SanitizeFsDirectory(sp.PreferredRamDiskRoot, forbidInternalHcx: false),
					SsdRoot = SanitizeFsDirectory(sp.SsdRoot, forbidInternalHcx: false),
					SsdSizeMb = Math.Clamp(sp.SsdSizeMb, 0L, MaxCacheSizeMb),
					WritePolicy = SanitizeWritePolicy(sp.WritePolicy),
					MountPoint = SanitizeMountPoint(sp.MountPoint),
					PerformanceModeIndex = Math.Clamp(sp.PerformanceModeIndex, 0, 2),
					FlushIntervalSeconds = Math.Clamp(sp.FlushIntervalSeconds, 1, 300)
				});
				if (cleanSessions.Count >= MaxProfiles)
				{
					break;
				}
			}
		}

		List<RamDiskProfile> cleanRamDisks = new List<RamDiskProfile>();
		if (raw.SavedRamDisks != null)
		{
			foreach (RamDiskProfile rp in raw.SavedRamDisks)
			{
				if (rp == null)
				{
					continue;
				}
				string? mount = SanitizeMountPoint(rp.MountPoint);
				if (string.IsNullOrWhiteSpace(mount) || mount.Length != 2 || mount[1] != ':')
				{
					continue;
				}
				cleanRamDisks.Add(new RamDiskProfile
				{
					MountPoint = mount,
					CapacityMb = Math.Clamp(rp.CapacityMb, 64L, MaxCacheSizeMb),
					VolumeLabel = SanitizeLabel(rp.VolumeLabel),
					BackupDirectory = SanitizeFsDirectory(rp.BackupDirectory, forbidInternalHcx: true),
					AutoPersist = rp.AutoPersist
				});
				if (cleanRamDisks.Count >= MaxProfiles)
				{
					break;
				}
			}
		}

		return new AppSettings
		{
			TargetPath = SanitizeFsDirectory(raw.TargetPath, forbidInternalHcx: true),
			L1BackendMode = SanitizeL1Mode(raw.L1BackendMode),
			RamSizeMb = Math.Clamp(raw.RamSizeMb, 0L, MaxCacheSizeMb),
			PreferredRamDiskRoot = SanitizeFsDirectory(raw.PreferredRamDiskRoot, forbidInternalHcx: false),
			SsdRoot = SanitizeFsDirectory(raw.SsdRoot, forbidInternalHcx: false),
			SsdSizeMb = Math.Clamp(raw.SsdSizeMb, 0L, MaxCacheSizeMb),
			WritePolicy = SanitizeWritePolicy(raw.WritePolicy),
			MountPoint = SanitizeMountPoint(raw.MountPoint),
			PerformanceModeIndex = Math.Clamp(raw.PerformanceModeIndex, 0, 2),
			FlushIntervalSeconds = Math.Clamp(raw.FlushIntervalSeconds, 1, 300),
			RamDiskMountPoint = SanitizeMountPoint(raw.RamDiskMountPoint),
			RamDiskCapacityMb = Math.Clamp(raw.RamDiskCapacityMb, 0L, MaxCacheSizeMb),
			RamDiskLabel = SanitizeLabel(raw.RamDiskLabel),
			AutoStartWithWindows = raw.AutoStartWithWindows,
			AutoRestoreSession = raw.AutoRestoreSession,
			SavedSessions = cleanSessions,
			SavedRamDisks = cleanRamDisks
		};
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
				if (!TryValidateExecutablePath(path, out string normalizedPath))
				{
					return false;
				}
				key.SetValue(RunValueName, $"\"{normalizedPath}\" --minimized");
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

	private static string SanitizeL1Mode(string? mode)
	{
		return Enum.TryParse<L1BackendMode>(mode, ignoreCase: true, out L1BackendMode parsed)
			? parsed.ToString()
			: nameof(HCX.Core.L1BackendMode.AutoPreferRamDisk);
	}

	private static string SanitizeWritePolicy(string? policy)
	{
		return Enum.TryParse<WritePolicy>(policy, ignoreCase: true, out WritePolicy parsed)
			? parsed.ToString()
			: nameof(HCX.Core.WritePolicy.WriteBack);
	}

	private static bool TryValidateExecutablePath(string path, out string normalizedPath)
	{
		normalizedPath = string.Empty;
		if (string.IsNullOrWhiteSpace(path) ||
		    path.IndexOf('"') >= 0 ||
		    path.Any(char.IsControl) ||
		    !Path.IsPathRooted(path))
		{
			return false;
		}
		try
		{
			normalizedPath = Path.GetFullPath(path);
			return !string.IsNullOrWhiteSpace(normalizedPath) && !normalizedPath.StartsWith(@"\\", StringComparison.Ordinal);
		}
		catch
		{
			return false;
		}
	}

	private static string? SanitizeFsDirectory(string? path, bool forbidInternalHcx)
	{
		if (string.IsNullOrWhiteSpace(path) ||
		    path.IndexOf('\0') >= 0 ||
		    path.Any(char.IsControl) ||
		    path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
		    path.StartsWith(@"\\?\", StringComparison.Ordinal))
		{
			return null;
		}
		string trimmed = path.Trim();
		if (forbidInternalHcx)
		{
			string norm = trimmed.TrimEnd('\\');
			if (Path.GetFileName(norm).Equals(".hcx", StringComparison.OrdinalIgnoreCase) ||
			    norm.Contains(@"\.hcx\", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
		}
		return trimmed;
	}

	private static string? SanitizeMountPoint(string? mount)
	{
		if (string.IsNullOrWhiteSpace(mount) || mount.Any(char.IsControl))
		{
			return null;
		}
		string trimmed = mount.Trim();
		if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
		{
			return $"{char.ToUpperInvariant(trimmed[0])}:";
		}
		return trimmed.Length <= 64 ? trimmed : trimmed.Substring(0, 64);
	}

	private static string SanitizeLabel(string? label)
	{
		if (string.IsNullOrWhiteSpace(label))
		{
			return "HCX_RAMDISK";
		}
		StringBuilder sb = new StringBuilder(32);
		foreach (char c in label.Trim())
		{
			if (!char.IsControl(c))
			{
				sb.Append(c);
				if (sb.Length >= 32)
				{
					break;
				}
			}
		}
		return sb.Length > 0 ? sb.ToString() : "HCX_RAMDISK";
	}
}
