using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HCX.Core;

/// <summary>
/// Configuration options for a HybridCacheEngine instance.
/// </summary>
public sealed class CacheOptions
{
	public const int DefaultBlockSizeBytes = 65536;

	public string TargetRootPath { get; set; } = string.Empty;

	public string SsdCacheDirectory { get; set; } = string.Empty;

	public string? RamDiskCacheDirectory { get; set; }

	public L1BackendMode L1BackendMode { get; set; } = L1BackendMode.AutoPreferRamDisk;

	public bool DemoteL1ToL2OnStop { get; set; } = true;

	public string MountPoint { get; set; } = string.Empty;

	public int BlockSizeBytes { get; set; } = DefaultBlockSizeBytes;

	public long RamCacheSizeBytes { get; set; } = 256L * 1024 * 1024;

	public long SsdCacheSizeBytes { get; set; } = 1024L * 1024 * 1024;

	public WritePolicy WritePolicy { get; set; } = WritePolicy.WriteBack;

	public TimeSpan AiInterval { get; set; } = TimeSpan.FromSeconds(3);

	public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);

	public TimeSpan AutoSaveInterval { get; set; } = TimeSpan.FromSeconds(10);

	public TimeSpan BackingStatCacheTtl { get; set; } = TimeSpan.FromMilliseconds(500);

	public int SequentialThreshold { get; set; } = 8;

	public bool EnableReadAhead { get; set; } = true;

	public int ReadAheadBlocks { get; set; } = 2;

	public double SsdPromoteScore { get; set; } = 1.2;

	public double RamPromoteScore { get; set; } = 2.0;

	public string? InstanceId { get; set; }

	public string ResolveInstanceId()
	{
		if (!string.IsNullOrWhiteSpace(InstanceId))
		{
			return SanitizeId(InstanceId);
		}
		string normalizedTarget = Path.GetFullPath(TargetRootPath).TrimEnd('\\').ToUpperInvariant();
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedTarget));
		return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
	}

	public void Validate()
	{
		if (string.IsNullOrWhiteSpace(TargetRootPath))
		{
			throw new ArgumentException("TargetRootPath must not be empty.", nameof(TargetRootPath));
		}
		string fullTarget = Path.GetFullPath(TargetRootPath);
		if (!Directory.Exists(fullTarget))
		{
			throw new DirectoryNotFoundException($"Target root directory not found: {fullTarget}");
		}
		if (BlockSizeBytes < 4096 || (BlockSizeBytes & (BlockSizeBytes - 1)) != 0)
		{
			throw new ArgumentOutOfRangeException(nameof(BlockSizeBytes), "BlockSizeBytes must be a power of 2 >= 4096.");
		}
		if (RamCacheSizeBytes < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(RamCacheSizeBytes));
		}
		if (SsdCacheSizeBytes < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(SsdCacheSizeBytes));
		}
		if (SsdCacheSizeBytes > 0)
		{
			if (string.IsNullOrWhiteSpace(SsdCacheDirectory))
			{
				throw new ArgumentException("SsdCacheDirectory must be provided when SsdCacheSizeBytes > 0.", nameof(SsdCacheDirectory));
			}
			if (SsdCacheDirectory.StartsWith(@"\\?\", StringComparison.Ordinal) ||
			    SsdCacheDirectory.StartsWith(@"\\.\", StringComparison.Ordinal))
			{
				throw new ArgumentException("Raw device/volume paths are disallowed for safety. Provide a filesystem directory for the cache file.", nameof(SsdCacheDirectory));
			}
		}
		if (!string.IsNullOrWhiteSpace(RamDiskCacheDirectory))
		{
			if (RamDiskCacheDirectory.StartsWith(@"\\?\", StringComparison.Ordinal) ||
			    RamDiskCacheDirectory.StartsWith(@"\\.\", StringComparison.Ordinal))
			{
				throw new ArgumentException("Raw device/volume paths are disallowed for safety. Provide a filesystem directory for the L1 RAMDisk cache file.", nameof(RamDiskCacheDirectory));
			}
		}
	}

	private static string SanitizeId(string id)
	{
		StringBuilder sb = new StringBuilder(id.Length);
		foreach (char c in id)
		{
			sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
		}
		return sb.ToString();
	}
}
