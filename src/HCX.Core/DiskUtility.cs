using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

namespace HCX.Core;

public static class DiskUtility
{
	public static List<DiskInfo> GetDisks()
	{
		Dictionary<string, (string Model, StorageMediaKind Kind)> letterMetadata = QueryWmiMediaMap();
		List<DiskInfo> results = new List<DiskInfo>();

		foreach (DriveInfo drive in DriveInfo.GetDrives())
		{
			if (!drive.IsReady)
			{
				continue;
			}
			if (drive.DriveType != DriveType.Fixed &&
			    drive.DriveType != DriveType.Removable &&
			    drive.DriveType != DriveType.Ram)
			{
				continue;
			}

			string letter = drive.Name.TrimEnd('\\');
			string label = string.Empty;
			try { label = drive.VolumeLabel ?? string.Empty; } catch { }

			StorageMediaKind kind = StorageMediaKind.Unknown;
			string model = string.Empty;

			if (letterMetadata.TryGetValue(letter, out (string Model, StorageMediaKind Kind) wmi))
			{
				model = wmi.Model;
				kind = wmi.Kind;
			}

			if (drive.DriveType == DriveType.Ram ||
			    label.Contains("RAMDISK", StringComparison.OrdinalIgnoreCase) ||
			    model.Contains("RAMDISK", StringComparison.OrdinalIgnoreCase) ||
			    model.Contains("IMDISK", StringComparison.OrdinalIgnoreCase))
			{
				kind = StorageMediaKind.RamDisk;
			}
			else if (drive.DriveType == DriveType.Removable && kind == StorageMediaKind.Unknown)
			{
				kind = StorageMediaKind.Removable;
			}
			else if (kind == StorageMediaKind.Unknown)
			{
				if (label.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
				    model.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
				    model.Contains("NVME", StringComparison.OrdinalIgnoreCase))
				{
					kind = StorageMediaKind.Ssd;
				}
				else if (label.Contains("HDD", StringComparison.OrdinalIgnoreCase))
				{
					kind = StorageMediaKind.Hdd;
				}
			}

			results.Add(new DiskInfo
			{
				DriveLetter = letter,
				RootPath = drive.RootDirectory.FullName,
				VolumeLabel = label,
				Model = string.IsNullOrWhiteSpace(model) ? drive.DriveFormat : model,
				MediaKind = kind,
				TotalBytes = drive.TotalSize,
				FreeBytes = drive.AvailableFreeSpace
			});
		}

		return results;
	}

	/// <summary>
	/// Returns the ready RAMDisk drive with the largest available free space, or null if none is present.
	/// </summary>
	public static DiskInfo? FindPreferredRamDisk(IEnumerable<DiskInfo>? disks = null)
	{
		IEnumerable<DiskInfo> source = disks ?? GetDisks();
		return source
			.Where(d => d.MediaKind == StorageMediaKind.RamDisk)
			.OrderByDescending(d => d.FreeBytes)
			.FirstOrDefault();
	}

	/// <summary>
	/// Returns the ready SSD drive with the largest available free space for persistent L2 caching.
	/// </summary>
	public static DiskInfo? FindPreferredSsd(IEnumerable<DiskInfo>? disks = null)
	{
		IEnumerable<DiskInfo> source = disks ?? GetDisks();
		return source
			.Where(d => d.MediaKind == StorageMediaKind.Ssd)
			.OrderByDescending(d => d.FreeBytes)
			.FirstOrDefault();
	}

	public static List<string> GetAvailableDriveLetters()
	{
		HashSet<char> used = DriveInfo.GetDrives()
			.Select(d => char.ToUpperInvariant(d.Name[0]))
			.ToHashSet();

		string preferredOrder = "XYZWVUTSRQPONMLKJIHGFED";
		List<string> available = new List<string>();
		foreach (char c in preferredOrder)
		{
			if (!used.Contains(c))
			{
				available.Add($"{c}:");
			}
		}
		return available;
	}

	public static long GetTotalPhysicalMemoryBytes()
	{
		MEMORYSTATUSEX mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
		if (GlobalMemoryStatusEx(ref mem))
		{
			return (long)mem.ullTotalPhys;
		}
		return 8L * 1024 * 1024 * 1024;
	}

	private static Dictionary<string, (string Model, StorageMediaKind Kind)> QueryWmiMediaMap()
	{
		var result = new Dictionary<string, (string Model, StorageMediaKind Kind)>(StringComparer.OrdinalIgnoreCase);

		// 1. Try Windows Storage WMI (MSFT_PhysicalDisk -> MSFT_Partition) for accurate SSD vs HDD detection.
		Dictionary<uint, (string Model, StorageMediaKind Kind)> diskByNumber = new Dictionary<uint, (string, StorageMediaKind)>();
		try
		{
			using ManagementObjectSearcher searcher = new ManagementObjectSearcher(
				@"\\.\root\Microsoft\Windows\Storage",
				"SELECT DeviceId, FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk");
			foreach (ManagementBaseObject obj in searcher.Get())
			{
				string devIdStr = obj["DeviceId"]?.ToString() ?? string.Empty;
				if (!uint.TryParse(devIdStr, out uint diskNum))
				{
					continue;
				}
				string model = obj["FriendlyName"]?.ToString()?.Trim() ?? string.Empty;
				int mediaType = obj["MediaType"] != null ? Convert.ToInt32(obj["MediaType"]) : 0;
				int busType = obj["BusType"] != null ? Convert.ToInt32(obj["BusType"]) : 0;

				StorageMediaKind kind = mediaType switch
				{
					3 => StorageMediaKind.Hdd,
					4 => StorageMediaKind.Ssd,
					5 => StorageMediaKind.Ssd,
					_ => (busType == 17 || model.Contains("NVME", StringComparison.OrdinalIgnoreCase) || model.Contains("SSD", StringComparison.OrdinalIgnoreCase))
						? StorageMediaKind.Ssd
						: StorageMediaKind.Unknown
				};
				diskByNumber[diskNum] = (model, kind);
			}

			using ManagementObjectSearcher partSearcher = new ManagementObjectSearcher(
				@"\\.\root\Microsoft\Windows\Storage",
				"SELECT DiskNumber, DriveLetter FROM MSFT_Partition");
			foreach (ManagementBaseObject obj in partSearcher.Get())
			{
				object? dlObj = obj["DriveLetter"];
				if (dlObj == null)
				{
					continue;
				}
				char letterChar = Convert.ToChar(dlObj);
				if (!char.IsLetter(letterChar))
				{
					continue;
				}
				uint diskNum = Convert.ToUInt32(obj["DiskNumber"]);
				string key = $"{char.ToUpperInvariant(letterChar)}:";
				if (diskByNumber.TryGetValue(diskNum, out (string Model, StorageMediaKind Kind) info))
				{
					result[key] = info;
				}
			}
		}
		catch
		{
			// Fallback to CIMv2 below.
		}

		// 2. Supplement missing drive letters via Win32_DiskDrive -> Win32_LogicalDisk.
		try
		{
			using ManagementObjectSearcher driveSearcher = new ManagementObjectSearcher("SELECT DeviceID, Model, MediaType FROM Win32_DiskDrive");
			foreach (ManagementBaseObject disk in driveSearcher.Get())
			{
				string devId = disk["DeviceID"]?.ToString() ?? string.Empty;
				string model = disk["Model"]?.ToString()?.Trim() ?? string.Empty;
				StorageMediaKind kind = model.Contains("SSD", StringComparison.OrdinalIgnoreCase) || model.Contains("NVME", StringComparison.OrdinalIgnoreCase)
					? StorageMediaKind.Ssd
					: StorageMediaKind.Unknown;

				string escaped = devId.Replace("\\", "\\\\");
				using ManagementObjectSearcher partSearcher = new ManagementObjectSearcher(
					$"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{escaped}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
				foreach (ManagementBaseObject part in partSearcher.Get())
				{
					string partId = part["DeviceID"]?.ToString() ?? string.Empty;
					using ManagementObjectSearcher ldSearcher = new ManagementObjectSearcher(
						$"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partId}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
					foreach (ManagementBaseObject ld in ldSearcher.Get())
					{
						string? dl = ld["DeviceID"]?.ToString();
						if (!string.IsNullOrEmpty(dl) && !result.ContainsKey(dl))
						{
							result[dl] = (model, kind);
						}
					}
				}
			}
		}
		catch
		{
		}

		return result;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MEMORYSTATUSEX
	{
		public uint dwLength;
		public uint dwMemoryLoad;
		public ulong ullTotalPhys;
		public ulong ullAvailPhys;
		public ulong ullTotalPageFile;
		public ulong ullAvailPageFile;
		public ulong ullTotalVirtual;
		public ulong ullAvailVirtual;
		public ulong ullAvailExtendedVirtual;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
