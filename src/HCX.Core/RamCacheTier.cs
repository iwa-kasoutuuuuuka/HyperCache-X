using System;
using System.Collections.Generic;
using System.Threading;

namespace HCX.Core;

public sealed class RamBlockCopy
{
	public required BlockKey Key { get; init; }
	public required byte[] Data { get; init; }
	public int ValidLength { get; init; }
	public bool IsDirty { get; init; }
	public long BackingFileSize { get; init; }
	public long BackingLastWriteUtcTicks { get; init; }
	public DateTime LastAccessUtc { get; init; }
	public double AiScore { get; init; }
}

/// <summary>
/// Thread-safe Tier-1 in-memory 64 KB block cache with sharded ReaderWriterLockSlim partitions,
/// low-contention LRU clock, O(1) per-file dirty tracking (fast-zero skip), and sampled LRU eviction.
/// </summary>
public sealed class RamCacheTier : IL1CacheTier
{
	private sealed class Entry
	{
		public required BlockKey Key;
		public required byte[] Data;
		public int KeyIndex;
		public int ValidLength;
		public bool IsDirty;
		public long BackingFileSize;
		public long BackingLastWriteUtcTicks;
		public long LastAccessStamp;
		public long LastAccessUtcTicks;
		public double AiScore;
	}

	private sealed class Shard : IDisposable
	{
		public readonly ReaderWriterLockSlim RwLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
		public readonly Dictionary<BlockKey, Entry> Map = new Dictionary<BlockKey, Entry>();
		public readonly List<BlockKey> Keys = new List<BlockKey>();
		public readonly Dictionary<string, HashSet<BlockKey>> KeysByFile = new Dictionary<string, HashSet<BlockKey>>(StringComparer.OrdinalIgnoreCase);
		public readonly Dictionary<string, Dictionary<long, int>> DirtyBlocksByFile = new Dictionary<string, Dictionary<long, int>>(StringComparer.OrdinalIgnoreCase);
		public readonly Dictionary<string, long> DirtyMaxEndByFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		public readonly int MaxBlocks;
		public long AccessClock;
		public int ReadSampleCounter;
		public int EvictCursor;
		public int DirtyCount;

		public Shard(int maxBlocks)
		{
			MaxBlocks = maxBlocks;
		}

		public void Dispose()
		{
			RwLock.Dispose();
		}
	}

	private readonly Shard[] _shards;
	private readonly int _shardMask;
	private readonly int _blockSize;
	private readonly int _maxBlocks;
	private int _totalBlocks;
	private int _totalDirtyCount;
	private bool _disposed;

	public int BlockSize => _blockSize;
	public int MaxBlocks => _maxBlocks;
	public int ShardCount => _shards.Length;
	public bool IsEnabled => _maxBlocks > 0 && !_disposed;
	public string BackendDescription => IsEnabled ? "In-Process RAM" : "OFF";

	public RamCacheTier(long capacityBytes, int blockSizeBytes)
	{
		_blockSize = blockSizeBytes;
		_maxBlocks = capacityBytes <= 0 ? 0 : (int)Math.Min(int.MaxValue, capacityBytes / blockSizeBytes);

		int shardCount = _maxBlocks >= 64 ? 16 : (_maxBlocks >= 16 ? 4 : 1);
		_shards = new Shard[shardCount];
		_shardMask = shardCount - 1;

		int basePerShard = _maxBlocks / shardCount;
		int remainder = _maxBlocks % shardCount;
		for (int i = 0; i < shardCount; i++)
		{
			_shards[i] = new Shard(basePerShard + (i < remainder ? 1 : 0));
		}
	}

	private Shard GetShard(BlockKey key) => _shards[key.GetHashCode() & _shardMask];

	public bool TryReadSlice(
		BlockKey key,
		int blockOffset,
		Span<byte> destination,
		long currentBackingSize,
		long currentBackingTicks,
		out int bytesCopied,
		out bool isDirty,
		out bool invalidatedStale)
	{
		bytesCopied = 0;
		isDirty = false;
		invalidatedStale = false;
		if (!IsEnabled)
		{
			return false;
		}

		Shard shard = GetShard(key);
		bool needsEvictStale = false;
		shard.RwLock.EnterReadLock();
		try
		{
			if (!shard.Map.TryGetValue(key, out Entry? e))
			{
				return false;
			}

			if (!e.IsDirty && (e.BackingFileSize != currentBackingSize || e.BackingLastWriteUtcTicks != currentBackingTicks))
			{
				needsEvictStale = true;
			}
			else
			{
				long currentClock = Volatile.Read(ref shard.AccessClock);
				if (Volatile.Read(ref e.LastAccessStamp) != currentClock)
				{
					long stamp = Interlocked.Increment(ref shard.AccessClock);
					Volatile.Write(ref e.LastAccessStamp, stamp);
					if ((unchecked(++shard.ReadSampleCounter) & 15) == 0)
					{
						Volatile.Write(ref e.LastAccessUtcTicks, DateTime.UtcNow.Ticks);
					}
				}

				isDirty = e.IsDirty;
				int available = Math.Max(0, e.ValidLength - blockOffset);
				bytesCopied = Math.Min(destination.Length, available);
				if (bytesCopied > 0)
				{
					e.Data.AsSpan(blockOffset, bytesCopied).CopyTo(destination);
				}
				return true;
			}
		}
		finally
		{
			shard.RwLock.ExitReadLock();
		}

		if (needsEvictStale)
		{
			shard.RwLock.EnterWriteLock();
			try
			{
				if (shard.Map.TryGetValue(key, out Entry? stale) &&
				    !stale.IsDirty &&
				    (stale.BackingFileSize != currentBackingSize || stale.BackingLastWriteUtcTicks != currentBackingTicks))
				{
					RemoveEntryLocked(shard, stale);
					invalidatedStale = true;
				}
			}
			finally
			{
				shard.RwLock.ExitWriteLock();
			}
		}
		return false;
	}

	public RamBlockCopy? Put(
		BlockKey key,
		ReadOnlySpan<byte> blockData,
		int validLength,
		bool isDirty,
		long backingSize,
		long backingTicks,
		double aiScore,
		Action<BlockKey, byte[], int>? onEvictDirty = null,
		Action<RamBlockCopy>? onEvictClean = null)
	{
		if (!IsEnabled)
		{
			return null;
		}

		int shardIdx = key.GetHashCode() & _shardMask;
		Shard shard = _shards[shardIdx];
		RamBlockCopy? preEvicted = null;
		bool needCrossShardEvict = false;

		shard.RwLock.EnterWriteLock();
		try
		{
			long stamp = Interlocked.Increment(ref shard.AccessClock);
			long utcTicks = DateTime.UtcNow.Ticks;

			if (shard.Map.TryGetValue(key, out Entry? existing))
			{
				int copyLen = Math.Min(blockData.Length, _blockSize);
				blockData.Slice(0, copyLen).CopyTo(existing.Data);
				if (validLength < _blockSize)
				{
					Array.Clear(existing.Data, validLength, _blockSize - validLength);
				}
				bool wasDirty = existing.IsDirty;
				existing.ValidLength = validLength;
				existing.IsDirty = isDirty;
				existing.BackingFileSize = backingSize;
				existing.BackingLastWriteUtcTicks = backingTicks;
				existing.LastAccessStamp = stamp;
				existing.LastAccessUtcTicks = utcTicks;
				existing.AiScore = aiScore;
				UpdateDirtyIndexOnStateChangeLocked(shard, key, wasDirty, isDirty, validLength);
				return null;
			}

			bool needsEvict = Volatile.Read(ref _totalBlocks) >= _maxBlocks;
			if (needsEvict && shard.Map.Count == 0 && _shards.Length > 1)
			{
				needCrossShardEvict = true;
			}
			else
			{
				RamBlockCopy? evicted = null;
				if (needsEvict && shard.Map.Count > 0)
				{
					evicted = EvictVictimLocked(shard, onEvictDirty, onEvictClean);
				}
				InsertCreatedEntryLocked(shard, key, blockData, validLength, isDirty, backingSize, backingTicks, stamp, utcTicks, aiScore);
				return evicted;
			}
		}
		finally
		{
			shard.RwLock.ExitWriteLock();
		}

		if (needCrossShardEvict)
		{
			preEvicted = EvictFromAnyOtherShard(shardIdx, onEvictDirty, onEvictClean);
		}

		shard.RwLock.EnterWriteLock();
		try
		{
			long stamp = Interlocked.Increment(ref shard.AccessClock);
			long utcTicks = DateTime.UtcNow.Ticks;
			if (shard.Map.TryGetValue(key, out Entry? existing))
			{
				int copyLen = Math.Min(blockData.Length, _blockSize);
				blockData.Slice(0, copyLen).CopyTo(existing.Data);
				if (validLength < _blockSize)
				{
					Array.Clear(existing.Data, validLength, _blockSize - validLength);
				}
				bool wasDirty = existing.IsDirty;
				existing.ValidLength = validLength;
				existing.IsDirty = isDirty;
				existing.BackingFileSize = backingSize;
				existing.BackingLastWriteUtcTicks = backingTicks;
				existing.LastAccessStamp = stamp;
				existing.LastAccessUtcTicks = utcTicks;
				existing.AiScore = aiScore;
				UpdateDirtyIndexOnStateChangeLocked(shard, key, wasDirty, isDirty, validLength);
				return preEvicted;
			}
			if (preEvicted == null && Volatile.Read(ref _totalBlocks) >= _maxBlocks && shard.Map.Count > 0)
			{
				preEvicted = EvictVictimLocked(shard, onEvictDirty, onEvictClean);
			}
			InsertCreatedEntryLocked(shard, key, blockData, validLength, isDirty, backingSize, backingTicks, stamp, utcTicks, aiScore);
			return preEvicted;
		}
		finally
		{
			shard.RwLock.ExitWriteLock();
		}
	}

	private RamBlockCopy? EvictVictimLocked(
		Shard shard,
		Action<BlockKey, byte[], int>? onEvictDirty,
		Action<RamBlockCopy>? onEvictClean)
	{
		Entry? victim = SelectVictimLocked(shard);
		if (victim == null)
		{
			return null;
		}
		RamBlockCopy evicted = new RamBlockCopy
		{
			Key = victim.Key,
			Data = victim.Data,
			ValidLength = victim.ValidLength,
			IsDirty = victim.IsDirty,
			BackingFileSize = victim.BackingFileSize,
			BackingLastWriteUtcTicks = victim.BackingLastWriteUtcTicks,
			LastAccessUtc = new DateTime(Volatile.Read(ref victim.LastAccessUtcTicks), DateTimeKind.Utc),
			AiScore = victim.AiScore
		};
		if (victim.IsDirty)
		{
			onEvictDirty?.Invoke(victim.Key, victim.Data, victim.ValidLength);
		}
		else
		{
			onEvictClean?.Invoke(evicted);
		}
		RemoveEntryLocked(shard, victim);
		return evicted;
	}

	private RamBlockCopy? EvictFromAnyOtherShard(
		int excludeShardIdx,
		Action<BlockKey, byte[], int>? onEvictDirty,
		Action<RamBlockCopy>? onEvictClean)
	{
		for (int offset = 1; offset < _shards.Length; offset++)
		{
			Shard candidate = _shards[(excludeShardIdx + offset) & _shardMask];
			candidate.RwLock.EnterWriteLock();
			try
			{
				if (candidate.Map.Count > 0)
				{
					return EvictVictimLocked(candidate, onEvictDirty, onEvictClean);
				}
			}
			finally
			{
				candidate.RwLock.ExitWriteLock();
			}
		}
		return null;
	}

	private void InsertCreatedEntryLocked(
		Shard shard,
		BlockKey key,
		ReadOnlySpan<byte> blockData,
		int validLength,
		bool isDirty,
		long backingSize,
		long backingTicks,
		long stamp,
		long utcTicks,
		double aiScore)
	{
		byte[] buffer = new byte[_blockSize];
		blockData.Slice(0, Math.Min(blockData.Length, _blockSize)).CopyTo(buffer);
		Entry created = new Entry
		{
			Key = key,
			Data = buffer,
			KeyIndex = shard.Keys.Count,
			ValidLength = validLength,
			IsDirty = isDirty,
			BackingFileSize = backingSize,
			BackingLastWriteUtcTicks = backingTicks,
			LastAccessStamp = stamp,
			LastAccessUtcTicks = utcTicks,
			AiScore = aiScore
		};
		shard.Keys.Add(key);
		shard.Map[key] = created;
		Interlocked.Increment(ref _totalBlocks);
		AddFileKeyIndexLocked(shard, key);
		if (isDirty)
		{
			shard.DirtyCount++;
			Interlocked.Increment(ref _totalDirtyCount);
			SetDirtyIndexLocked(shard, key, validLength);
		}
	}

	public bool TryPatchOrGetForWrite(
		BlockKey key,
		int blockOffset,
		ReadOnlySpan<byte> patchData,
		long backingSize,
		long backingTicks,
		double aiScore,
		bool markDirty)
	{
		if (!IsEnabled)
		{
			return false;
		}

		Shard shard = GetShard(key);
		shard.RwLock.EnterWriteLock();
		try
		{
			if (!shard.Map.TryGetValue(key, out Entry? e))
			{
				return false;
			}
			if (!e.IsDirty && (e.BackingFileSize != backingSize || e.BackingLastWriteUtcTicks != backingTicks))
			{
				RemoveEntryLocked(shard, e);
				return false;
			}

			patchData.CopyTo(e.Data.AsSpan(blockOffset));
			int end = blockOffset + patchData.Length;
			if (end > e.ValidLength)
			{
				e.ValidLength = end;
			}
			bool wasDirty = e.IsDirty;
			if (markDirty)
			{
				e.IsDirty = true;
			}
			else
			{
				e.BackingFileSize = backingSize;
				e.BackingLastWriteUtcTicks = backingTicks;
			}
			UpdateDirtyIndexOnStateChangeLocked(shard, key, wasDirty, e.IsDirty, e.ValidLength);

			e.LastAccessStamp = Interlocked.Increment(ref shard.AccessClock);
			e.LastAccessUtcTicks = DateTime.UtcNow.Ticks;
			e.AiScore = aiScore;
			return true;
		}
		finally
		{
			shard.RwLock.ExitWriteLock();
		}
	}

	public List<RamBlockCopy> SnapshotEntries(string? filterRelativePath = null, bool dirtyOnly = false)
	{
		List<RamBlockCopy> result = new List<RamBlockCopy>();
		if (dirtyOnly && Volatile.Read(ref _totalDirtyCount) <= 0)
		{
			return result;
		}

		for (int i = 0; i < _shards.Length; i++)
		{
			Shard shard = _shards[i];
			shard.RwLock.EnterReadLock();
			try
			{
				if (filterRelativePath != null)
				{
					if (dirtyOnly)
					{
						if (shard.DirtyBlocksByFile.TryGetValue(filterRelativePath, out Dictionary<long, int>? dirtyBlocks))
						{
							foreach (long blockIdx in dirtyBlocks.Keys)
							{
								if (shard.Map.TryGetValue(new BlockKey(filterRelativePath, blockIdx), out Entry? e) && e.IsDirty)
								{
									result.Add(CloneEntry(e));
								}
							}
						}
						continue;
					}

					if (shard.KeysByFile.TryGetValue(filterRelativePath, out HashSet<BlockKey>? keys))
					{
						foreach (BlockKey k in keys)
						{
							if (shard.Map.TryGetValue(k, out Entry? e))
							{
								result.Add(CloneEntry(e));
							}
						}
					}
					continue;
				}

				if (dirtyOnly)
				{
					foreach (KeyValuePair<string, Dictionary<long, int>> fileKv in shard.DirtyBlocksByFile)
					{
						foreach (long blockIdx in fileKv.Value.Keys)
						{
							if (shard.Map.TryGetValue(new BlockKey(fileKv.Key, blockIdx), out Entry? e) && e.IsDirty)
							{
								result.Add(CloneEntry(e));
							}
						}
					}
					continue;
				}

				foreach (Entry e in shard.Map.Values)
				{
					result.Add(CloneEntry(e));
				}
			}
			finally
			{
				shard.RwLock.ExitReadLock();
			}
		}
		return result;
	}

	public void MarkClean(BlockKey key, long newBackingSize, long newBackingTicks)
	{
		Shard shard = GetShard(key);
		shard.RwLock.EnterWriteLock();
		try
		{
			if (shard.Map.TryGetValue(key, out Entry? e))
			{
				if (e.IsDirty)
				{
					e.IsDirty = false;
					shard.DirtyCount = Math.Max(0, shard.DirtyCount - 1);
					Interlocked.Decrement(ref _totalDirtyCount);
					RemoveDirtyIndexLocked(shard, key);
				}
				e.BackingFileSize = newBackingSize;
				e.BackingLastWriteUtcTicks = newBackingTicks;
			}
		}
		finally
		{
			shard.RwLock.ExitWriteLock();
		}
	}

	public void UpdateBackingMetadataForFile(string normalizedRelPath, long newBackingSize, long newBackingTicks)
	{
		for (int i = 0; i < _shards.Length; i++)
		{
			Shard shard = _shards[i];
			shard.RwLock.EnterWriteLock();
			try
			{
				if (shard.KeysByFile.TryGetValue(normalizedRelPath, out HashSet<BlockKey>? keys))
				{
					foreach (BlockKey k in keys)
					{
						if (shard.Map.TryGetValue(k, out Entry? e) && !e.IsDirty)
						{
							e.BackingFileSize = newBackingSize;
							e.BackingLastWriteUtcTicks = newBackingTicks;
						}
					}
				}
			}
			finally
			{
				shard.RwLock.ExitWriteLock();
			}
		}
	}

	public void Remove(BlockKey key)
	{
		Shard shard = GetShard(key);
		shard.RwLock.EnterWriteLock();
		try
		{
			if (shard.Map.TryGetValue(key, out Entry? e))
			{
				RemoveEntryLocked(shard, e);
			}
		}
		finally
		{
			shard.RwLock.ExitWriteLock();
		}
	}

	public void RemoveByFile(string normalizedRelPath)
	{
		for (int i = 0; i < _shards.Length; i++)
		{
			Shard shard = _shards[i];
			shard.RwLock.EnterWriteLock();
			try
			{
				if (!shard.KeysByFile.TryGetValue(normalizedRelPath, out HashSet<BlockKey>? keys))
				{
					continue;
				}
				List<BlockKey> toRemove = new List<BlockKey>(keys);
				foreach (BlockKey k in toRemove)
				{
					if (shard.Map.TryGetValue(k, out Entry? e))
					{
						RemoveEntryLocked(shard, e);
					}
				}
			}
			finally
			{
				shard.RwLock.ExitWriteLock();
			}
		}
	}

	public void TruncateFile(string normalizedRelPath, long newSize)
	{
		long lastValidBlock = newSize <= 0 ? -1 : (newSize - 1) / _blockSize;
		for (int i = 0; i < _shards.Length; i++)
		{
			Shard shard = _shards[i];
			shard.RwLock.EnterWriteLock();
			try
			{
				if (!shard.KeysByFile.TryGetValue(normalizedRelPath, out HashSet<BlockKey>? keys))
				{
					continue;
				}
				List<BlockKey> snapshot = new List<BlockKey>(keys);
				foreach (BlockKey k in snapshot)
				{
					if (!shard.Map.TryGetValue(k, out Entry? e))
					{
						continue;
					}
					if (k.BlockIndex > lastValidBlock)
					{
						RemoveEntryLocked(shard, e);
					}
					else if (k.BlockIndex == lastValidBlock)
					{
						int newValidLen = (int)(newSize - lastValidBlock * _blockSize);
						if (newValidLen < e.ValidLength)
						{
							Array.Clear(e.Data, newValidLen, e.ValidLength - newValidLen);
							e.ValidLength = newValidLen;
							if (e.IsDirty)
							{
								SetDirtyIndexLocked(shard, k, newValidLen);
							}
						}
					}
				}
			}
			finally
			{
				shard.RwLock.ExitWriteLock();
			}
		}
	}

	public void RenamePrefix(string oldRelPath, string newRelPath)
	{
		string oldPrefix = oldRelPath.TrimEnd('\\') + "\\";
		List<(Entry Entry, BlockKey NewKey)> crossShardMoves = new List<(Entry, BlockKey)>();

		for (int i = 0; i < _shards.Length; i++)
		{
			Shard shard = _shards[i];
			shard.RwLock.EnterWriteLock();
			try
			{
				List<(Entry Entry, BlockKey NewKey)>? renames = null;
				foreach (KeyValuePair<string, HashSet<BlockKey>> kv in shard.KeysByFile)
				{
					if (string.Equals(kv.Key, oldRelPath, StringComparison.OrdinalIgnoreCase) ||
					    kv.Key.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
					{
						foreach (BlockKey k in kv.Value)
						{
							if (!shard.Map.TryGetValue(k, out Entry? e))
							{
								continue;
							}
							string nextPath = string.Equals(k.RelativePath, oldRelPath, StringComparison.OrdinalIgnoreCase)
								? newRelPath
								: newRelPath.TrimEnd('\\') + "\\" + k.RelativePath.Substring(oldPrefix.Length);
							renames ??= new List<(Entry, BlockKey)>();
							renames.Add((e, new BlockKey(nextPath, k.BlockIndex)));
						}
					}
				}

				if (renames == null)
				{
					continue;
				}

				foreach ((Entry entry, BlockKey newKey) in renames)
				{
					int targetShardIdx = newKey.GetHashCode() & _shardMask;
					if (targetShardIdx == i)
					{
						BlockKey oldKey = entry.Key;
						bool wasDirty = entry.IsDirty;
						int validLen = entry.ValidLength;
						int idx = entry.KeyIndex;

						shard.Map.Remove(oldKey);
						RemoveFileKeyIndexLocked(shard, oldKey);
						if (wasDirty)
						{
							RemoveDirtyIndexLocked(shard, oldKey);
						}

						entry.Key = newKey;
						shard.Keys[idx] = newKey;
						shard.Map[newKey] = entry;
						AddFileKeyIndexLocked(shard, newKey);
						if (wasDirty)
						{
							SetDirtyIndexLocked(shard, newKey, validLen);
						}
					}
					else
					{
						RemoveEntryLocked(shard, entry);
						entry.Key = newKey;
						crossShardMoves.Add((entry, newKey));
					}
				}
			}
			finally
			{
				shard.RwLock.ExitWriteLock();
			}
		}

		foreach ((Entry entry, BlockKey newKey) in crossShardMoves)
		{
			Shard targetShard = GetShard(newKey);
			targetShard.RwLock.EnterWriteLock();
			try
			{
				if (targetShard.Map.TryGetValue(newKey, out Entry? existing))
				{
					RemoveEntryLocked(targetShard, existing);
				}
				entry.KeyIndex = targetShard.Keys.Count;
				targetShard.Keys.Add(newKey);
				targetShard.Map[newKey] = entry;
				Interlocked.Increment(ref _totalBlocks);
				AddFileKeyIndexLocked(targetShard, newKey);
				if (entry.IsDirty)
				{
					targetShard.DirtyCount++;
					Interlocked.Increment(ref _totalDirtyCount);
					SetDirtyIndexLocked(targetShard, newKey, entry.ValidLength);
				}
			}
			finally
			{
				targetShard.RwLock.ExitWriteLock();
			}
		}
	}

	public long GetTrackedMaxFileSize(string normalizedRelPath)
	{
		if (Volatile.Read(ref _totalDirtyCount) <= 0)
		{
			return 0L;
		}
		long maxEnd = 0L;
		for (int i = 0; i < _shards.Length; i++)
		{
			Shard shard = _shards[i];
			shard.RwLock.EnterReadLock();
			try
			{
				if (shard.DirtyMaxEndByFile.TryGetValue(normalizedRelPath, out long shardMax) && shardMax > maxEnd)
				{
					maxEnd = shardMax;
				}
			}
			finally
			{
				shard.RwLock.ExitReadLock();
			}
		}
		return maxEnd;
	}

	public (int CachedBlocks, long UsedBytes, int DirtyBlocks) GetCounts()
	{
		int totalBlocks = 0;
		int totalDirty = 0;
		for (int i = 0; i < _shards.Length; i++)
		{
			Shard shard = _shards[i];
			shard.RwLock.EnterReadLock();
			try
			{
				totalBlocks += shard.Map.Count;
				totalDirty += shard.DirtyCount;
			}
			finally
			{
				shard.RwLock.ExitReadLock();
			}
		}
		return (totalBlocks, (long)totalBlocks * _blockSize, totalDirty);
	}

	public void Clear()
	{
		for (int i = 0; i < _shards.Length; i++)
		{
			Shard shard = _shards[i];
			shard.RwLock.EnterWriteLock();
			try
			{
				shard.Map.Clear();
				shard.Keys.Clear();
				shard.KeysByFile.Clear();
				shard.DirtyBlocksByFile.Clear();
				shard.DirtyMaxEndByFile.Clear();
				shard.DirtyCount = 0;
			}
			finally
			{
				shard.RwLock.ExitWriteLock();
			}
		}
		Volatile.Write(ref _totalBlocks, 0);
		Volatile.Write(ref _totalDirtyCount, 0);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		Clear();
		for (int i = 0; i < _shards.Length; i++)
		{
			_shards[i].Dispose();
		}
	}

	private RamBlockCopy CloneEntry(Entry e)
	{
		byte[] copy = new byte[_blockSize];
		Buffer.BlockCopy(e.Data, 0, copy, 0, _blockSize);
		return new RamBlockCopy
		{
			Key = e.Key,
			Data = copy,
			ValidLength = e.ValidLength,
			IsDirty = e.IsDirty,
			BackingFileSize = e.BackingFileSize,
			BackingLastWriteUtcTicks = e.BackingLastWriteUtcTicks,
			LastAccessUtc = new DateTime(Volatile.Read(ref e.LastAccessUtcTicks), DateTimeKind.Utc),
			AiScore = e.AiScore
		};
	}

	private static Entry? SelectVictimLocked(Shard shard)
	{
		int count = shard.Keys.Count;
		if (count == 0)
		{
			return null;
		}

		int samples = count <= 64 ? count : 32;
		int stride = count <= 64 ? 1 : Math.Max(1, count / samples);
		Entry? oldest = null;
		long oldestStamp = long.MaxValue;

		int start = count <= 64 ? 0 : (shard.EvictCursor % count);
		for (int i = 0; i < samples; i++)
		{
			int idx = (start + i * stride) % count;
			Entry candidate = shard.Map[shard.Keys[idx]];
			long stamp = Volatile.Read(ref candidate.LastAccessStamp);
			if (stamp < oldestStamp)
			{
				oldestStamp = stamp;
				oldest = candidate;
			}
		}
		shard.EvictCursor = (start + 1) % Math.Max(1, count);
		return oldest;
	}

	private void RemoveEntryLocked(Shard shard, Entry e)
	{
		if (e.IsDirty)
		{
			shard.DirtyCount = Math.Max(0, shard.DirtyCount - 1);
			Interlocked.Decrement(ref _totalDirtyCount);
			RemoveDirtyIndexLocked(shard, e.Key);
		}
		shard.Map.Remove(e.Key);
		Interlocked.Decrement(ref _totalBlocks);
		RemoveFileKeyIndexLocked(shard, e.Key);

		int idx = e.KeyIndex;
		int last = shard.Keys.Count - 1;
		if (idx < last)
		{
			BlockKey movedKey = shard.Keys[last];
			shard.Keys[idx] = movedKey;
			shard.Map[movedKey].KeyIndex = idx;
		}
		shard.Keys.RemoveAt(last);
	}

	private static void AddFileKeyIndexLocked(Shard shard, BlockKey key)
	{
		if (!shard.KeysByFile.TryGetValue(key.RelativePath, out HashSet<BlockKey>? set))
		{
			set = new HashSet<BlockKey>();
			shard.KeysByFile[key.RelativePath] = set;
		}
		set.Add(key);
	}

	private static void RemoveFileKeyIndexLocked(Shard shard, BlockKey key)
	{
		if (shard.KeysByFile.TryGetValue(key.RelativePath, out HashSet<BlockKey>? set))
		{
			set.Remove(key);
			if (set.Count == 0)
			{
				shard.KeysByFile.Remove(key.RelativePath);
			}
		}
	}

	private void UpdateDirtyIndexOnStateChangeLocked(Shard shard, BlockKey key, bool wasDirty, bool isNowDirty, int validLen)
	{
		if (!wasDirty && isNowDirty)
		{
			shard.DirtyCount++;
			Interlocked.Increment(ref _totalDirtyCount);
			SetDirtyIndexLocked(shard, key, validLen);
		}
		else if (wasDirty && !isNowDirty)
		{
			shard.DirtyCount = Math.Max(0, shard.DirtyCount - 1);
			Interlocked.Decrement(ref _totalDirtyCount);
			RemoveDirtyIndexLocked(shard, key);
		}
		else if (isNowDirty)
		{
			SetDirtyIndexLocked(shard, key, validLen);
		}
	}

	private void SetDirtyIndexLocked(Shard shard, BlockKey key, int validLen)
	{
		if (!shard.DirtyBlocksByFile.TryGetValue(key.RelativePath, out Dictionary<long, int>? map))
		{
			map = new Dictionary<long, int>();
			shard.DirtyBlocksByFile[key.RelativePath] = map;
		}
		bool hadPrev = map.TryGetValue(key.BlockIndex, out int prevValidLen);
		long prevEnd = hadPrev ? key.BlockIndex * _blockSize + prevValidLen : -1L;
		map[key.BlockIndex] = validLen;
		long end = key.BlockIndex * _blockSize + validLen;
		if (!shard.DirtyMaxEndByFile.TryGetValue(key.RelativePath, out long curMax) || end > curMax)
		{
			shard.DirtyMaxEndByFile[key.RelativePath] = end;
		}
		else if (hadPrev && prevEnd >= curMax && end < prevEnd)
		{
			RecomputeDirtyMaxEndLocked(shard, key.RelativePath, map);
		}
	}

	private void RemoveDirtyIndexLocked(Shard shard, BlockKey key)
	{
		if (!shard.DirtyBlocksByFile.TryGetValue(key.RelativePath, out Dictionary<long, int>? map))
		{
			return;
		}
		if (!map.Remove(key.BlockIndex, out int removedValidLen))
		{
			return;
		}
		if (map.Count == 0)
		{
			shard.DirtyBlocksByFile.Remove(key.RelativePath);
			shard.DirtyMaxEndByFile.Remove(key.RelativePath);
		}
		else if (shard.DirtyMaxEndByFile.TryGetValue(key.RelativePath, out long curMax) &&
		         key.BlockIndex * _blockSize + removedValidLen >= curMax)
		{
			RecomputeDirtyMaxEndLocked(shard, key.RelativePath, map);
		}
	}

	private void RecomputeDirtyMaxEndLocked(Shard shard, string relPath, Dictionary<long, int> map)
	{
		long max = 0;
		foreach (KeyValuePair<long, int> kv in map)
		{
			long end = kv.Key * _blockSize + kv.Value;
			if (end > max)
			{
				max = end;
			}
		}
		shard.DirtyMaxEndByFile[relPath] = max;
	}
}
