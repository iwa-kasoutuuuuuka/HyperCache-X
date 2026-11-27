using System;

namespace HCX.Core;

/// <summary>
/// Cache storage tier where a 64 KB block resides.
/// </summary>
public enum CacheTier
{
	None = 0,
	RamL1 = 1,
	SsdL2 = 2
}

/// <summary>
/// Write caching policy for the hybrid cache engine.
/// </summary>
public enum WritePolicy
{
	WriteBack = 0,
	WriteThrough = 1
}

/// <summary>
/// Backing storage selection for the Tier-1 (L1) cache.
/// </summary>
public enum L1BackendMode
{
	AutoPreferRamDisk = 0,
	RamDiskFile = 1,
	InProcessRam = 2,
	Disabled = 3
}

/// <summary>
/// Identifies a fixed-size block within a backing file relative to the accelerated root path.
/// </summary>
public readonly struct BlockKey : IEquatable<BlockKey>
{
	public string RelativePath { get; }

	public long BlockIndex { get; }

	public BlockKey(string relativePath, long blockIndex)
	{
		RelativePath = NormalizeRelativePath(relativePath);
		BlockIndex = blockIndex;
	}

	public static string NormalizeRelativePath(string relativePath)
	{
		if (string.IsNullOrWhiteSpace(relativePath))
		{
			return string.Empty;
		}
		string normalized = relativePath.Replace('/', '\\').TrimStart('\\');
		return normalized;
	}

	public bool Equals(BlockKey other)
	{
		return BlockIndex == other.BlockIndex
			&& string.Equals(RelativePath, other.RelativePath, StringComparison.OrdinalIgnoreCase);
	}

	public override bool Equals(object? obj)
	{
		return obj is BlockKey other && Equals(other);
	}

	public override int GetHashCode()
	{
		return HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(RelativePath), BlockIndex);
	}

	public override string ToString()
	{
		return $"{RelativePath}#{BlockIndex}";
	}
}

/// <summary>
/// Represents one cell in the 32x8 (256-cell) dashboard heatmap.
/// </summary>
public readonly struct HeatmapCell
{
	public CacheTier Tier { get; init; }

	public double Intensity { get; init; }

	public bool HasDirty { get; init; }

	public long HitCount { get; init; }
}

/// <summary>
/// Point-in-time telemetry snapshot from a running or stopped HybridCacheEngine.
/// </summary>
public sealed class CacheStatsSnapshot
{
	public long TotalReadBytes { get; init; }

	public long TotalWriteBytes { get; init; }

	public long TotalIoBytes => TotalReadBytes + TotalWriteBytes;

	public long RamHits { get; init; }

	public long SsdHits { get; init; }

	public long HddMisses { get; init; }

	public long SequentialBypasses { get; init; }

	public long PrefetchedBlocks { get; init; }

	public double HitRatePercent
	{
		get
		{
			long total = RamHits + SsdHits + HddMisses;
			return total == 0 ? 0.0 : (double)(RamHits + SsdHits) * 100.0 / total;
		}
	}

	public double RamHitSharePercent
	{
		get
		{
			long total = RamHits + SsdHits + HddMisses;
			return total == 0 ? 0.0 : (double)RamHits * 100.0 / total;
		}
	}

	public double SsdHitSharePercent
	{
		get
		{
			long total = RamHits + SsdHits + HddMisses;
			return total == 0 ? 0.0 : (double)SsdHits * 100.0 / total;
		}
	}

	public int RamCachedBlocks { get; init; }

	public long RamUsedBytes { get; init; }

	public long RamCapacityBytes { get; init; }

	public int SsdCachedBlocks { get; init; }

	public long SsdUsedBytes { get; init; }

	public long SsdCapacityBytes { get; init; }

	public int DirtyBlocks { get; init; }

	public int TrackedBlocks { get; init; }

	public double AverageAiScore { get; init; }

	public HeatmapCell[] HeatmapCells { get; init; } = new HeatmapCell[256];

	public bool IsRunning { get; init; }

	public string TargetRootPath { get; init; } = string.Empty;

	public string SsdCacheFilePath { get; init; } = string.Empty;

	public string MountPoint { get; init; } = string.Empty;

	public string L1BackendSummary { get; init; } = string.Empty;

	public WritePolicy WritePolicy { get; init; }

	public WorkloadPreset WorkloadPreset { get; init; }

	public HotFileStat[] TopHotFiles { get; init; } = Array.Empty<HotFileStat>();
}

public sealed class HotFileStat
{
	public string RelativePath { get; init; } = string.Empty;

	public long Hits { get; init; }

	public long Misses { get; init; }

	public long TotalBytes { get; init; }

	public double HitRatePercent => (Hits + Misses) == 0 ? 0.0 : (double)Hits * 100.0 / (Hits + Misses);
}

public sealed class WarmResult
{
	public int FilesWarmed { get; init; }

	public int BlocksWarmed { get; init; }

	public long BytesWarmed { get; init; }

	public bool Pinned { get; init; }
}

/// <summary>
/// Result of running the built-in self-verification cache benchmark.
/// </summary>
public sealed class BenchmarkResult
{
	public double ColdReadMs { get; init; }

	public double ColdReadMBps { get; init; }

	public double HotReadMs { get; init; }

	public double HotReadMBps { get; init; }

	public double WriteBackMs { get; init; }

	public double WriteBackMBps { get; init; }

	public double SpeedupFactor => ColdReadMs > 0 && HotReadMs > 0 ? ColdReadMs / HotReadMs : 1.0;

	public double HitRatePercent { get; init; }

	public long RamHits { get; init; }

	public long SsdHits { get; init; }

	public long SequentialBypasses { get; init; }

	public long PrefetchedBlocks { get; init; }

	public bool VerifiedIntegrity { get; init; }

	public string Summary { get; init; } = string.Empty;
}
