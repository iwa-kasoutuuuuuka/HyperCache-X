using System;
using System.Collections.Generic;

namespace HCX.Core;

/// <summary>
/// Abstraction for the Tier-1 (L1) cache backend: either in-process RAM or an existing RAMDisk file.
/// </summary>
public interface IL1CacheTier : IDisposable
{
	int BlockSize { get; }
	int MaxBlocks { get; }
	bool IsEnabled { get; }
	string BackendDescription { get; }

	bool TryReadSlice(
		BlockKey key,
		int blockOffset,
		Span<byte> destination,
		long currentBackingSize,
		long currentBackingTicks,
		out int bytesCopied,
		out bool isDirty,
		out bool invalidatedStale);

	RamBlockCopy? Put(
		BlockKey key,
		ReadOnlySpan<byte> blockData,
		int validLength,
		bool isDirty,
		long backingSize,
		long backingTicks,
		double aiScore,
		Action<BlockKey, byte[], int>? onEvictDirty = null,
		Action<RamBlockCopy>? onEvictClean = null);

	bool TryPatchOrGetForWrite(
		BlockKey key,
		int blockOffset,
		ReadOnlySpan<byte> patchData,
		long backingSize,
		long backingTicks,
		double aiScore,
		bool markDirty);

	List<RamBlockCopy> SnapshotEntries(string? filterRelativePath = null, bool dirtyOnly = false);

	void MarkClean(BlockKey key, long newBackingSize, long newBackingTicks);

	void UpdateBackingMetadataForFile(string normalizedRelPath, long newBackingSize, long newBackingTicks);

	void Remove(BlockKey key);

	void RemoveByFile(string normalizedRelPath);

	void TruncateFile(string normalizedRelPath, long newSize);

	void RenamePrefix(string oldRelPath, string newRelPath);

	long GetTrackedMaxFileSize(string normalizedRelPath);

	(int CachedBlocks, long UsedBytes, int DirtyBlocks) GetCounts();

	void Clear();
}
