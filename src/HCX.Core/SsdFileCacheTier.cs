using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HCX.Core;

public sealed class SsdBlockMeta
{
	public required BlockKey Key { get; set; }
	public int SlotIndex { get; init; }
	public int KeyIndex { get; set; }
	public int ValidLength { get; set; }
	public bool IsDirty { get; set; }
	public long BackingFileSize { get; set; }
	public long BackingLastWriteUtcTicks { get; set; }
	public DateTime LastAccessUtc { get; set; }
	public double AiScore { get; set; }
}

internal sealed class SsdSlotEntry
{
	public required BlockKey Key;
	public int SlotIndex;
	public int KeyIndex;
	public int ValidLength;
	public bool IsDirty;
	public long BackingFileSize;
	public long BackingLastWriteUtcTicks;
	public long LastAccessStamp;
	public long LastAccessUtcTicks;
	public double AiScore;
}

public sealed class EvictedSsdBlock
{
	public required BlockKey Key { get; init; }
	public required byte[] Data { get; init; }
	public int ValidLength { get; init; }
}

/// <summary>
/// Safe Tier-2 SSD cache backed by a dedicated cache file (&lt;SsdDir&gt;\cache_{id}.bin)
/// with sharded ReaderWriterLockSlim partitions for concurrent NVMe Queue Depth I/O,
/// zero-alloc full-block writes, and O(1) dirty tracking.
/// </summary>
public sealed class SsdFileCacheTier : IDisposable
{
	private sealed class Shard : IDisposable
	{
		public readonly ReaderWriterLockSlim RwLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
		public readonly Dictionary<BlockKey, SsdSlotEntry> Map = new Dictionary<BlockKey, SsdSlotEntry>();
		public readonly List<BlockKey> Keys = new List<BlockKey>();
		public readonly Dictionary<string, HashSet<BlockKey>> KeysByFile = new Dictionary<string, HashSet<BlockKey>>(StringComparer.OrdinalIgnoreCase);
		public readonly Dictionary<string, Dictionary<long, int>> DirtyBlocksByFile = new Dictionary<string, Dictionary<long, int>>(StringComparer.OrdinalIgnoreCase);
		public readonly Dictionary<string, long> DirtyMaxEndByFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		public readonly int MaxSlots;
		public long AccessClock;
		public int ReadSampleCounter;
		public int EvictCursor;
		public int DirtyCount;

		public Shard(int maxSlots)
		{
			MaxSlots = maxSlots;
		}

		public void Dispose()
		{
			RwLock.Dispose();
		}
	}

	private readonly Shard[] _shards;
	private readonly int _shardMask;
	private readonly object _slotAllocLock = new object();
	private readonly Stack<int> _freeSlots = new Stack<int>();
	private int _nextSlotToAllocate;

	private readonly SafeFileHandle? _handle;
	private readonly int _blockSize;
	private readonly int _maxSlots;
	private int _totalDirtyCount;
	private bool _disposed;

	public string CacheFilePath { get; }
	public int BlockSize => _blockSize;
	public int MaxSlots => _maxSlots;
	public int ShardCount => _shards.Length;
	public bool IsEnabled => _handle != null && _maxSlots > 0 && !_disposed;

	public SsdFileCacheTier(string cacheDirectory, string instanceId, long capacityBytes, int blockSizeBytes)
	{
		_blockSize = blockSizeBytes;
		_maxSlots = capacityBytes <= 0 ? 0 : (int)Math.Min(int.MaxValue, capacityBytes / blockSizeBytes);
		CacheFilePath = string.IsNullOrWhiteSpace(cacheDirectory)
			? string.Empty
			: Path.Combine(cacheDirectory, $"cache_{instanceId}.bin");

		int shardCount = _maxSlots >= 64 ? 8 : (_maxSlots >= 16 ? 4 : 1);
		_shards = new Shard[shardCount];
		_shardMask = shardCount - 1;
		int basePerShard = _maxSlots / shardCount;
		int remainder = _maxSlots % shardCount;
		for (int i = 0; i < shardCount; i++)
		{
			_shards[i] = new Shard(basePerShard + (i < remainder ? 1 : 0));
		}

		if (_maxSlots > 0 && !string.IsNullOrEmpty(CacheFilePath))
		{
			Directory.CreateDirectory(cacheDirectory);
			_handle = File.OpenHandle(
				CacheFilePath,
				FileMode.OpenOrCreate,
				FileAccess.ReadWrite,
				FileShare.Read,
				FileOptions.RandomAccess);
			ShrinkFileIfNeededLocked();
		}
	}

	private Shard GetShard(BlockKey key) => _shards[key.GetHashCode() & _shardMask];

	private bool TryAllocateSlot(out int slotIndex)
	{
		lock (_slotAllocLock)
		{
			if (_freeSlots.Count > 0)
			{
				slotIndex = _freeSlots.Pop();
				return true;
			}
			if (_nextSlotToAllocate < _maxSlots)
			{
				slotIndex = _nextSlotToAllocate++;
				return true;
			}
			slotIndex = -1;
			return false;
		}
	}

	private void ReleaseSlot(int slotIndex)
	{
		lock (_slotAllocLock)
		{
			_freeSlots.Push(slotIndex);
		}
	}

	public bool TryReadFullBlock(
		BlockKey key,
		byte[] destination64K,
		long currentBackingSize,
		long currentBackingTicks,
		out SsdBlockMeta? meta,
		out bool invalidatedStale)
	{
		meta = null;
		invalidatedStale = false;
		if (!IsEnabled || _handle == null)
		{
			return false;
		}

		Shard shard = GetShard(key);
		bool needsEvictStale = false;
		shard.RwLock.EnterReadLock();
		try
		{
			if (!shard.Map.TryGetValue(key, out SsdSlotEntry? entry))
			{
				return false;
			}

			if (!entry.IsDirty &&
			    (entry.BackingFileSize != currentBackingSize || entry.BackingLastWriteUtcTicks != currentBackingTicks))
			{
				needsEvictStale = true;
			}
			else
			{
				long fileOffset = (long)entry.SlotIndex * _blockSize;
				int totalRead = ReadExactHandle(_handle, fileOffset, destination64K.AsSpan(0, _blockSize));
				if (totalRead < entry.ValidLength)
				{
					needsEvictStale = true;
				}
				else
				{
					long currentClock = Volatile.Read(ref shard.AccessClock);
					if (Volatile.Read(ref entry.LastAccessStamp) != currentClock)
					{
						long stamp = Interlocked.Increment(ref shard.AccessClock);
						Volatile.Write(ref entry.LastAccessStamp, stamp);
						if ((unchecked(++shard.ReadSampleCounter) & 15) == 0)
						{
							Volatile.Write(ref entry.LastAccessUtcTicks, DateTime.UtcNow.Ticks);
						}
					}
					meta = CloneMeta(entry);
					return true;
				}
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
				if (shard.Map.TryGetValue(key, out SsdSlotEntry? stale))
				{
					RemoveEntryLocked(shard, stale, pushFreeSlot: true);
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

	public bool ContainsFresh(BlockKey key, long currentBackingSize, long currentBackingTicks)
	{
		if (!IsEnabled)
		{
			return false;
		}
		Shard shard = GetShard(key);
		shard.RwLock.EnterReadLock();
		try
		{
			if (!shard.Map.TryGetValue(key, out SsdSlotEntry? e))
			{
				return false;
			}
			return e.IsDirty || (e.BackingFileSize == currentBackingSize && e.BackingLastWriteUtcTicks == currentBackingTicks);
		}
		finally
		{
			shard.RwLock.ExitReadLock();
		}
	}

	public bool TryReadSlotRaw(int slotIndex, byte[] destination64K, out int bytesRead)
	{
		bytesRead = 0;
		if (!IsEnabled || _handle == null || slotIndex < 0 || slotIndex >= _maxSlots)
		{
			return false;
		}
		bytesRead = ReadExactHandle(_handle, (long)slotIndex * _blockSize, destination64K.AsSpan(0, _blockSize));
		return bytesRead > 0;
	}

	public bool TryReadBlockForFlush(BlockKey key, byte[] destination64K, out int validLength)
	{
		validLength = 0;
		if (!IsEnabled || _handle == null)
		{
			return false;
		}
		Shard shard = GetShard(key);
		shard.RwLock.EnterReadLock();
		try
		{
			if (!shard.Map.TryGetValue(key, out SsdSlotEntry? entry) || !entry.IsDirty)
			{
				return false;
			}
			int read = ReadExactHandle(_handle, (long)entry.SlotIndex * _blockSize, destination64K.AsSpan(0, _blockSize));
			if (read < entry.ValidLength)
			{
				return false;
			}
			validLength = entry.ValidLength;
			return true;
		}
		finally
		{
			shard.RwLock.ExitReadLock();
		}
	}

	public bool TryWriteBlock(
		BlockKey key,
		ReadOnlySpan<byte> blockData,
		int validLength,
		bool isDirty,
		long backingSize,
		long backingTicks,
		double aiScore,
		out EvictedSsdBlock? evictedDirty,
		Action<BlockKey, byte[], int>? onEvictDirty = null)
	{
		evictedDirty = null;
		if (!IsEnabled || _handle == null)
		{
			return false;
		}

		int shardIdx = key.GetHashCode() & _shardMask;
		Shard shard = _shards[shardIdx];
		int borrowedSlot = -1;
		bool needCrossShardEvict = false;

		shard.RwLock.EnterWriteLock();
		try
		{
			long stamp = Interlocked.Increment(ref shard.AccessClock);
			long utcTicks = DateTime.UtcNow.Ticks;

			if (shard.Map.TryGetValue(key, out SsdSlotEntry? existing))
			{
				WriteFullSlot(_handle, existing.SlotIndex, _blockSize, blockData);
				bool wasDirty = existing.IsDirty;
				existing.ValidLength = validLength;
				existing.IsDirty = isDirty;
				existing.BackingFileSize = backingSize;
				existing.BackingLastWriteUtcTicks = backingTicks;
				existing.LastAccessStamp = stamp;
				existing.LastAccessUtcTicks = utcTicks;
				existing.AiScore = aiScore;
				UpdateDirtyIndexOnStateChangeLocked(shard, key, wasDirty, isDirty, validLength);
				return true;
			}

			int slotIndex;
			if (TryAllocateSlot(out int allocated))
			{
				slotIndex = allocated;
			}
			else
			{
				SsdSlotEntry? victim = SelectVictimLocked(shard);
				if (victim == null)
				{
					if (_shards.Length <= 1)
					{
						return false;
					}
					needCrossShardEvict = true;
					slotIndex = -1;
				}
				else
				{
					slotIndex = EvictVictimAndReclaimSlotLocked(shard, victim, out evictedDirty, onEvictDirty);
				}
			}

			if (!needCrossShardEvict)
			{
				WriteAndRegisterSlotLocked(shard, key, slotIndex, blockData, validLength, isDirty, backingSize, backingTicks, stamp, utcTicks, aiScore);
				return true;
			}
		}
		finally
		{
			shard.RwLock.ExitWriteLock();
		}

		borrowedSlot = EvictSlotFromOtherShard(shardIdx, out evictedDirty, onEvictDirty);
		if (borrowedSlot < 0)
		{
			return false;
		}

		shard.RwLock.EnterWriteLock();
		try
		{
			long stamp = Interlocked.Increment(ref shard.AccessClock);
			long utcTicks = DateTime.UtcNow.Ticks;
			if (shard.Map.TryGetValue(key, out SsdSlotEntry? existing))
			{
				ReleaseSlot(borrowedSlot);
				WriteFullSlot(_handle, existing.SlotIndex, _blockSize, blockData);
				bool wasDirty = existing.IsDirty;
				existing.ValidLength = validLength;
				existing.IsDirty = isDirty;
				existing.BackingFileSize = backingSize;
				existing.BackingLastWriteUtcTicks = backingTicks;
				existing.LastAccessStamp = stamp;
				existing.LastAccessUtcTicks = utcTicks;
				existing.AiScore = aiScore;
				UpdateDirtyIndexOnStateChangeLocked(shard, key, wasDirty, isDirty, validLength);
				return true;
			}

			WriteAndRegisterSlotLocked(shard, key, borrowedSlot, blockData, validLength, isDirty, backingSize, backingTicks, stamp, utcTicks, aiScore);
			return true;
		}
		finally
		{
			shard.RwLock.ExitWriteLock();
		}
	}

	private int EvictVictimAndReclaimSlotLocked(
		Shard shard,
		SsdSlotEntry victim,
		out EvictedSsdBlock? evictedDirty,
		Action<BlockKey, byte[], int>? onEvictDirty)
	{
		evictedDirty = null;
		int slotIndex = victim.SlotIndex;
		if (victim.IsDirty && _handle != null)
		{
			byte[] oldData = new byte[_blockSize];
			int oldRead = ReadExactHandle(_handle, (long)slotIndex * _blockSize, oldData);
			if (oldRead >= victim.ValidLength)
			{
				evictedDirty = new EvictedSsdBlock
				{
					Key = victim.Key,
					Data = oldData,
					ValidLength = victim.ValidLength
				};
				onEvictDirty?.Invoke(victim.Key, oldData, victim.ValidLength);
			}
		}
		RemoveEntryLocked(shard, victim, pushFreeSlot: false);
		return slotIndex;
	}

	private int EvictSlotFromOtherShard(
		int excludeShardIdx,
		out EvictedSsdBlock? evictedDirty,
		Action<BlockKey, byte[], int>? onEvictDirty)
	{
		evictedDirty = null;
		for (int offset = 1; offset < _shards.Length; offset++)
		{
			Shard candidate = _shards[(excludeShardIdx + offset) & _shardMask];
			candidate.RwLock.EnterWriteLock();
			try
			{
				SsdSlotEntry? victim = SelectVictimLocked(candidate);
				if (victim != null)
				{
					return EvictVictimAndReclaimSlotLocked(candidate, victim, out evictedDirty, onEvictDirty);
				}
			}
			finally
			{
				candidate.RwLock.ExitWriteLock();
			}
		}
		return TryAllocateSlot(out int allocated) ? allocated : -1;
	}

	private void WriteAndRegisterSlotLocked(
		Shard shard,
		BlockKey key,
		int slotIndex,
		ReadOnlySpan<byte> blockData,
		int validLength,
		bool isDirty,
		long backingSize,
		long backingTicks,
		long stamp,
		long utcTicks,
		double aiScore)
	{
		WriteFullSlot(_handle!, slotIndex, _blockSize, blockData);
		SsdSlotEntry created = new SsdSlotEntry
		{
			Key = key,
			SlotIndex = slotIndex,
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
		AddFileKeyIndexLocked(shard, key);
		if (isDirty)
		{
			shard.DirtyCount++;
			Interlocked.Increment(ref _totalDirtyCount);
			SetDirtyIndexLocked(shard, key, validLength);
		}
	}

	public void RestoreEntries(IEnumerable<PersistedBlockEntry> entries)
	{
		if (!IsEnabled || _handle == null)
		{
			return;
		}

		long fileLength = RandomAccess.GetLength(_handle);
		for (int i = 0; i < _shards.Length; i++)
		{
			_shards[i].RwLock.EnterWriteLock();
		}
		try
		{
			for (int i = 0; i < _shards.Length; i++)
			{
				Shard s = _shards[i];
				s.Map.Clear();
				s.Keys.Clear();
				s.KeysByFile.Clear();
				s.DirtyBlocksByFile.Clear();
				s.DirtyMaxEndByFile.Clear();
				s.DirtyCount = 0;
			}
			Volatile.Write(ref _totalDirtyCount, 0);

			int maxSlotSeen = -1;
			HashSet<int> usedSlots = new HashSet<int>();

			foreach (PersistedBlockEntry item in entries)
			{
				if (!item.IsValid || item.SlotIndex < 0 || item.SlotIndex >= _maxSlots)
				{
					continue;
				}
				long requiredEnd = ((long)item.SlotIndex + 1) * _blockSize;
				if (requiredEnd > fileLength || !usedSlots.Add(item.SlotIndex))
				{
					continue;
				}

				BlockKey key = new BlockKey(item.RelativePath, item.BlockIndex);
				Shard shard = GetShard(key);
				SsdSlotEntry created = new SsdSlotEntry
				{
					Key = key,
					SlotIndex = item.SlotIndex,
					KeyIndex = shard.Keys.Count,
					ValidLength = Math.Clamp(item.ValidLength, 0, _blockSize),
					IsDirty = item.IsDirty,
					BackingFileSize = item.BackingFileSize,
					BackingLastWriteUtcTicks = item.BackingLastWriteUtcTicks,
					LastAccessStamp = Interlocked.Increment(ref shard.AccessClock),
					LastAccessUtcTicks = item.LastAccessUtcTicks,
					AiScore = item.AiScore
				};
				shard.Keys.Add(key);
				shard.Map[key] = created;
				AddFileKeyIndexLocked(shard, key);
				if (created.IsDirty)
				{
					shard.DirtyCount++;
					Interlocked.Increment(ref _totalDirtyCount);
					SetDirtyIndexLocked(shard, key, created.ValidLength);
				}
				if (item.SlotIndex > maxSlotSeen)
				{
					maxSlotSeen = item.SlotIndex;
				}
			}

			lock (_slotAllocLock)
			{
				_freeSlots.Clear();
				_nextSlotToAllocate = maxSlotSeen + 1;
				for (int s = 0; s < _nextSlotToAllocate; s++)
				{
					if (!usedSlots.Contains(s))
					{
						_freeSlots.Push(s);
					}
				}
			}
			ShrinkFileIfNeededLocked();
		}
		finally
		{
			for (int i = _shards.Length - 1; i >= 0; i--)
			{
				_shards[i].RwLock.ExitWriteLock();
			}
		}
	}

	public List<SsdBlockMeta> SnapshotEntries(string? filterRelativePath = null, bool dirtyOnly = false)
	{
		List<SsdBlockMeta> result = new List<SsdBlockMeta>();
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
						if (shard.DirtyBlocksByFile.TryGetValue(filterRelativePath, out Dictionary<long, int>? dirtyMap))
						{
							foreach (long blockIdx in dirtyMap.Keys)
							{
								if (shard.Map.TryGetValue(new BlockKey(filterRelativePath, blockIdx), out SsdSlotEntry? e) && e.IsDirty)
								{
									result.Add(CloneMeta(e));
								}
							}
						}
						continue;
					}
					if (shard.KeysByFile.TryGetValue(filterRelativePath, out HashSet<BlockKey>? keys))
					{
						foreach (BlockKey k in keys)
						{
							if (shard.Map.TryGetValue(k, out SsdSlotEntry? e))
							{
								result.Add(CloneMeta(e));
							}
						}
					}
					continue;
				}

				if (dirtyOnly)
				{
					foreach (KeyValuePair<string, Dictionary<long, int>> kv in shard.DirtyBlocksByFile)
					{
						foreach (long blockIdx in kv.Value.Keys)
						{
							if (shard.Map.TryGetValue(new BlockKey(kv.Key, blockIdx), out SsdSlotEntry? e) && e.IsDirty)
							{
								result.Add(CloneMeta(e));
							}
						}
					}
					continue;
				}

				foreach (SsdSlotEntry e in shard.Map.Values)
				{
					result.Add(CloneMeta(e));
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
			if (shard.Map.TryGetValue(key, out SsdSlotEntry? e))
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
						if (shard.Map.TryGetValue(k, out SsdSlotEntry? e) && !e.IsDirty)
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
			if (shard.Map.TryGetValue(key, out SsdSlotEntry? e))
			{
				RemoveEntryLocked(shard, e, pushFreeSlot: true);
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
					if (shard.Map.TryGetValue(k, out SsdSlotEntry? e))
					{
						RemoveEntryLocked(shard, e, pushFreeSlot: true);
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
		if (!IsEnabled || _handle == null)
		{
			return;
		}
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
					if (!shard.Map.TryGetValue(k, out SsdSlotEntry? e))
					{
						continue;
					}
					if (k.BlockIndex > lastValidBlock)
					{
						RemoveEntryLocked(shard, e, pushFreeSlot: true);
					}
					else if (k.BlockIndex == lastValidBlock)
					{
						int newValidLen = (int)(newSize - lastValidBlock * _blockSize);
						if (newValidLen < e.ValidLength)
						{
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
		List<(SsdSlotEntry Entry, BlockKey NewKey)> crossShardMoves = new List<(SsdSlotEntry, BlockKey)>();

		for (int i = 0; i < _shards.Length; i++)
		{
			Shard shard = _shards[i];
			shard.RwLock.EnterWriteLock();
			try
			{
				List<(SsdSlotEntry Entry, BlockKey NewKey)>? renames = null;
				foreach (KeyValuePair<string, HashSet<BlockKey>> kv in shard.KeysByFile)
				{
					if (string.Equals(kv.Key, oldRelPath, StringComparison.OrdinalIgnoreCase) ||
					    kv.Key.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
					{
						foreach (BlockKey k in kv.Value)
						{
							if (!shard.Map.TryGetValue(k, out SsdSlotEntry? e))
							{
								continue;
							}
							string nextPath = string.Equals(k.RelativePath, oldRelPath, StringComparison.OrdinalIgnoreCase)
								? newRelPath
								: newRelPath.TrimEnd('\\') + "\\" + k.RelativePath.Substring(oldPrefix.Length);
							renames ??= new List<(SsdSlotEntry, BlockKey)>();
							renames.Add((e, new BlockKey(nextPath, k.BlockIndex)));
						}
					}
				}

				if (renames == null)
				{
					continue;
				}

				foreach ((SsdSlotEntry entry, BlockKey newKey) in renames)
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
						RemoveEntryLocked(shard, entry, pushFreeSlot: false);
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

		foreach ((SsdSlotEntry entry, BlockKey newKey) in crossShardMoves)
		{
			Shard targetShard = GetShard(newKey);
			targetShard.RwLock.EnterWriteLock();
			try
			{
				if (targetShard.Map.TryGetValue(newKey, out SsdSlotEntry? existing))
				{
					RemoveEntryLocked(targetShard, existing, pushFreeSlot: true);
				}
				entry.KeyIndex = targetShard.Keys.Count;
				targetShard.Keys.Add(newKey);
				targetShard.Map[newKey] = entry;
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

	public void FlushHandleToDisk()
	{
		if (!IsEnabled || _handle == null)
		{
			return;
		}
		RandomAccess.FlushToDisk(_handle);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_handle?.Dispose();
		for (int i = 0; i < _shards.Length; i++)
		{
			_shards[i].Dispose();
		}
	}

	public static int DeleteCacheFiles(string cacheDirectory, string? instanceId = null)
	{
		if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory))
		{
			return 0;
		}
		int deleted = 0;
		string suffix = string.IsNullOrWhiteSpace(instanceId) ? "*" : instanceId;
		string[] patterns = { $"cache_{suffix}.bin", $"metadata_{suffix}.json", $"metadata_{suffix}.json.tmp" };
		foreach (string pattern in patterns)
		{
			foreach (string file in Directory.EnumerateFiles(cacheDirectory, pattern, SearchOption.TopDirectoryOnly))
			{
				try
				{
					if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
					{
						continue;
					}
					File.Delete(file);
					deleted++;
				}
				catch (IOException)
				{
				}
				catch (UnauthorizedAccessException)
				{
				}
			}
		}
		return deleted;
	}

	private static void WriteFullSlot(SafeFileHandle handle, int slotIndex, int blockSize, ReadOnlySpan<byte> blockData)
	{
		long offset = (long)slotIndex * blockSize;
		if (blockData.Length >= blockSize)
		{
			RandomAccess.Write(handle, blockData.Slice(0, blockSize), offset);
			return;
		}
		byte[] rented = ArrayPool<byte>.Shared.Rent(blockSize);
		try
		{
			blockData.CopyTo(rented);
			Array.Clear(rented, blockData.Length, blockSize - blockData.Length);
			RandomAccess.Write(handle, rented.AsSpan(0, blockSize), offset);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rented);
		}
	}

	private static int ReadExactHandle(SafeFileHandle handle, long offset, Span<byte> buffer)
	{
		int total = 0;
		while (total < buffer.Length)
		{
			int n = RandomAccess.Read(handle, buffer.Slice(total), offset + total);
			if (n <= 0) break;
			total += n;
		}
		return total;
	}

	private static SsdSlotEntry? SelectVictimLocked(Shard shard)
	{
		int count = shard.Keys.Count;
		if (count == 0) return null;

		int samples = count <= 64 ? count : 32;
		int stride = count <= 64 ? 1 : Math.Max(1, count / samples);
		SsdSlotEntry? oldest = null;
		long oldestStamp = long.MaxValue;
		int start = count <= 64 ? 0 : (shard.EvictCursor % count);
		for (int i = 0; i < samples; i++)
		{
			int idx = (start + i * stride) % count;
			SsdSlotEntry candidate = shard.Map[shard.Keys[idx]];
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

	private void RemoveEntryLocked(Shard shard, SsdSlotEntry e, bool pushFreeSlot)
	{
		int slot = e.SlotIndex;
		if (e.IsDirty)
		{
			shard.DirtyCount = Math.Max(0, shard.DirtyCount - 1);
			Interlocked.Decrement(ref _totalDirtyCount);
			RemoveDirtyIndexLocked(shard, e.Key);
		}
		shard.Map.Remove(e.Key);
		RemoveFileKeyIndexLocked(shard, e.Key);

		int idx = e.KeyIndex;
		int last = shard.Keys.Count - 1;
		if (idx < last)
		{
			BlockKey moved = shard.Keys[last];
			shard.Keys[idx] = moved;
			shard.Map[moved].KeyIndex = idx;
		}
		shard.Keys.RemoveAt(last);
		if (pushFreeSlot)
		{
			ReleaseSlot(slot);
		}
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

	private void ShrinkFileIfNeededLocked()
	{
		if (_handle == null || _maxSlots <= 0)
		{
			return;
		}
		long maxBytes = (long)_maxSlots * _blockSize;
		int allocated;
		lock (_slotAllocLock)
		{
			allocated = _nextSlotToAllocate;
		}
		if (allocated <= _maxSlots && RandomAccess.GetLength(_handle) > maxBytes)
		{
			RandomAccess.SetLength(_handle, maxBytes);
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
		if (!shard.DirtyBlocksByFile.TryGetValue(key.RelativePath, out Dictionary<long, int>? map)) return;
		if (!map.Remove(key.BlockIndex, out int removedValidLen)) return;
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
			if (end > max) max = end;
		}
		shard.DirtyMaxEndByFile[relPath] = max;
	}

	private static SsdBlockMeta CloneMeta(SsdSlotEntry src)
	{
		return new SsdBlockMeta
		{
			Key = src.Key,
			SlotIndex = src.SlotIndex,
			KeyIndex = src.KeyIndex,
			ValidLength = src.ValidLength,
			IsDirty = src.IsDirty,
			BackingFileSize = src.BackingFileSize,
			BackingLastWriteUtcTicks = src.BackingLastWriteUtcTicks,
			LastAccessUtc = new DateTime(Volatile.Read(ref src.LastAccessUtcTicks), DateTimeKind.Utc),
			AiScore = src.AiScore
		};
	}
}
