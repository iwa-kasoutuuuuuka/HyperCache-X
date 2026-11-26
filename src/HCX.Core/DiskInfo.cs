namespace HCX.Core;

public enum StorageMediaKind
{
	Unknown = 0,
	Hdd = 1,
	Ssd = 2,
	RamDisk = 3,
	Removable = 4
}

public sealed class DiskInfo
{
	public string DriveLetter { get; set; } = string.Empty;

	public string RootPath { get; set; } = string.Empty;

	public string VolumeLabel { get; set; } = string.Empty;

	public string Model { get; set; } = string.Empty;

	public StorageMediaKind MediaKind { get; set; } = StorageMediaKind.Unknown;

	public long TotalBytes { get; set; }

	public long FreeBytes { get; set; }

	public string MediaTag => MediaKind switch
	{
		StorageMediaKind.RamDisk => "RAMDisk",
		StorageMediaKind.Ssd => "SSD",
		StorageMediaKind.Hdd => "HDD",
		StorageMediaKind.Removable => "Removable",
		_ => "Disk"
	};

	public string DisplayName
	{
		get
		{
			double freeGb = FreeBytes / (1024.0 * 1024.0 * 1024.0);
			string labelPart = string.IsNullOrWhiteSpace(VolumeLabel) ? Model : VolumeLabel;
			if (!string.IsNullOrWhiteSpace(Model) && !string.IsNullOrWhiteSpace(VolumeLabel) &&
			    !string.Equals(Model, VolumeLabel, System.StringComparison.OrdinalIgnoreCase))
			{
				labelPart = $"{VolumeLabel} / {Model}";
			}
			return $"{DriveLetter} [{MediaTag}] {labelPart} ({freeGb:F0} GB 空き)";
		}
	}
}
