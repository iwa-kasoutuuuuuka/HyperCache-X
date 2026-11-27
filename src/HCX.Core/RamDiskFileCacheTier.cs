using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HCX.Core;

/// <summary>
/// Tier-1 cache backed by a dedicated cache file on an existing RAMDisk drive (e.g. R:\.hcx\l1_cache_{id}.bin),
/// featuring sharded ReaderWriterLockSlim partitions (up to 16 shards), MemoryMappedFile zero-syscall I/O
/// (with automatic RandomAccess fallback), and O(1) dirty tracking.
/// </summary>
public sealed class RamDiskFileCacheTier : IL1CacheTier
{
	private sealed class SlotMeta
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

	private sealed class Shard : IDisposable
	{
		public readonly ReaderWriterLockSlim RwLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
		public readonly Dictionary<BlockKey, SlotMeta> Map = new Dictionary<BlockKey, SlotMeta>();
		public readonly List<BlockKey> Keys = new List<BlockKey>();
		public readonly Dictionary<string, HashSet<BlockKey>> KeysByFile = new Dictionary<string, HashSet<BlockKey>>(StringComparer.OrdinalIgnoreCase);
		public readonly Dictionary<string, Dictionary<long, int>> DirtyBlocksByFile = new Dictionary<string, Dictionary<long, int>>(StringComparer.OrdinalIgnoreCase);
		public readonly Dictionary<string, long> DirtyMaxEndByFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		public long AccessClock;
		public int ReadSampleCounter;
		public int EvictCursor;
		public int DirtyCount;

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
	private MemoryMappedFile? _mmf;
	private MemoryMappedViewAccessor? _mmfAccessor;
	private unsafe byte* _mmfBasePtr;
	private long _mmfCapacityBytes;
	private readonly int _blockSize;
	private readonly int _maxBlocks;
	private int _totalDirtyCount;
	private bool _disposed;

	public string CacheFilePath { get; }
	public int BlockSize => _blockSize;
	public int MaxBlocks => _maxBlocks;
	public int ShardCount => _shards.Length;
	public unsafe bool IsMemoryMapped => _mmfBasePtr != null && !_disposed;
	public bool IsEnabled => _handle != null && _maxBlocks > 0 && !_disposed;
	public string BackendDescription => IsEnabled
		? (IsMemoryMapped ? $"RAMDisk MMF ({CacheFilePath})" : $"RAMDisk ({CacheFilePath})")
		: "OFF";

	public RamDiskFileCacheTier(string ramDiskDirectory, string instanceId, long capacityBytes, int blockSizeBytes)
	{
		if (string.IsNullOrWhiteSpace(ramDiskDirectory))
		{
			throw new ArgumentException("RAMDisk directory must not be empty.", nameof(ramDiskDirectory));
		}

		_blockSize = blockSizeBytes;
		_maxBlocks = capacityBytes <= 0 ? 0 : (int)Math.Min(int.MaxValue, capacityBytes / blockSizeBytes);
		CacheFilePath = Path.Combine(ramDiskDirectory, $"l1_cache_{instanceId}.bin");

		int shardCount = _maxBlocks >= 64 ? 16 : (_maxBlocks >= 16 ? 4 : 1);
		_shards = new Shard[shardCount];
		_shardMask = shardCount - 1;
		for (int i = 0; i < shardCount; i++)
		{
			_shards[i] = new Shard();
		}

		if (_maxBlocks > 0)
		{
			Directory.CreateDirectory(ramDiskDirectory);
			_handle = File.OpenHandle(
				CacheFilePath,
				FileMode.Create,
				FileAccess.ReadWrite,
				FileShare.Read,
				FileOptions.RandomAccess);
			TryInitializeMemoryMap();
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
			if (_nextSlotToAllocate < _maxBlocks)
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

	private unsafe void TryInitializeMemoryMap()
	{
		if (_handle == null || _maxBlocks <= 0)
		{
			return;
		}
		long totalBytes = (long)_maxBlocks * _blockSize;
		if (totalBytes <= 0)
		{
			return;
		}
		try
		{
			_mmf = MemoryMappedFile.CreateFromFile(
				_handle,
				mapName: null,
				capacity: totalBytes,
				access: MemoryMappedFileAccess.ReadWrite,
				inheritability: HandleInheritability.None,
				leaveOpen: true);
			_mmfAccessor = _mmf.CreateViewAccessor(0, totalBytes, MemoryMappedFileAccess.ReadWrite);
			byte* ptr = null;
			_mmfAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
			if (ptr != null)
			{
				_mmfBasePtr = ptr + _mmfAccessor.PointerOffset;
				_mmfCapacityBytes = totalBytes;
			}
			else
			{
				ReleaseMemoryMapLocked();
			}
		}
		catch
		{
			ReleaseMemoryMapLocked();
		}
	}

	private unsafe void ReleaseMemoryMapLocked()
	{
		if (_mmfBasePtr != null && _mmfAccessor != null)
		{
			_mmfBasePtr = null;
			try { _mmfAccessor.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
		}
		_mmfCapacityBytes = 0;
		try { _mmfAccessor?.Dispose(); } catch { }
		_mmfAccessor = null;
		try { _mmf?.Dispose(); } catch { }
		_mmf = null;
	}

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
		if (!IsEnabled || _handle == null)
		{
			return false;
		}

		Shard shard = GetShard(key);
		bool needsEvictStale = false;
		shard.RwLock.EnterReadLock();
		try
		{
			if (!shard.Map.TryGetValue(key, out SlotMeta? e))
			{
				return false;
			}

			if (!e.IsDirty && (e.BackingFileSize != currentBackingSize || e.BackingLastWriteUtcTicks != currentBackingTicks))
			{
				needsEvictStale = true;
			}
			else
			{
				int available = Math.Max(0, e.ValidLength - blockOffset);
				int toRead = Math.Min(destination.Length, available);
				if (toRead > 0)
				{
					long fileOffset = (long)e.SlotIndex * _blockSize + blockOffset;
					int actual = ReadSlotBytes(fileOffset, destination.Slice(0, toRead));
					if (actual < toRead)
					{
						needsEvictStale = true;
						goto ExitRead;
					}
				}

				bytesCopied = toRead;
				isDirty = e.IsDirty;
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
				return true;
			}
			ExitRead:;
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
				if (shard.Map.TryGetValue(key, out SlotMeta? stale))
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
		if (!IsEnabled || _handle == null)
		{
			return null;
		}

		int shardIdx = key.GetHashCode() & _shardMask;
		Shard shard = _shards[shardIdx];
		bool needCrossShardEvict = false;

		shard.RwLock.EnterWriteLock();
		try
		{
			long stamp = Interlocked.Increment(ref shard.AccessClock);
			long utcTicks = DateTime.UtcNow.Ticks;

			if (shard.Map.TryGetValue(key, out SlotMeta? existing))
			{
				WriteFullSlot(existing.SlotIndex, blockData);
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

			RamBlockCopy? evicted = null;
			int slotIndex;
			if (TryAllocateSlot(out int allocated))
			{
				slotIndex = allocated;
			}
			else
			{
				SlotMeta? victim = SelectVictimLocked(shard);
				if (victim == null)
				{
					if (_shards.Length <= 1)
					{
						return null;
					}
					needCrossShardEvict = true;
					slotIndex = -1;
				}
				else
				{
					slotIndex = EvictVictimAndReclaimSlotLocked(shard, victim, out evicted, onEvictDirty, onEvictClean);
				}
			}

			if (!needCrossShardEvict)
			{
				WriteAndRegisterSlotLocked(shard, key, slotIndex, blockData, validLength, isDirty, backingSize, backingTicks, stamp, utcTicks, aiScore);
				return evicted;
			}
		}
		finally
		{
			shard.RwLock.ExitWriteLock();
		}

		int borrowedSlot = EvictSlotFromOtherShard(shardIdx, out RamBlockCopy? crossEvicted, onEvictDirty, onEvictClean);
		if (borrowedSlot < 0)
		{
			return crossEvicted;
		}

		shard.RwLock.EnterWriteLock();
		try
		{
			long stamp = Interlocked.Increment(ref shard.AccessClock);
			long utcTicks = DateTime.UtcNow.Ticks;
			if (shard.Map.TryGetValue(key, out SlotMeta? existing))
			{
				ReleaseSlot(borrowedSlot);
				WriteFullSlot(existing.SlotIndex, blockData);
				bool wasDirty = existing.IsDirty;
				existing.ValidLength = validLength;
				existing.IsDirty = isDirty;
				existing.BackingFileSize = backingSize;
				existing.BackingLastWriteUtcTicks = backingTicks;
				existing.LastAccessStamp = stamp;
				existing.LastAccessUtcTicks = utcTicks;
				existing.AiScore = aiScore;
				UpdateDirtyIndexOnStateChangeLocked(shard, key, wasDirty, isDirty, validLength);
				return crossEvicted;
			}

			WriteAndRegisterSlotLocked(shard, key, borrowedSlot, blockData, validLength, isDirty, backingSize, backingTicks, stamp, utcTicks, aiScore);
			return crossEvicted;
		}
		finally
		{
			shard.RwLock.ExitWriteLock();
		}
	}

	private int EvictVictimAndReclaimSlotLocked(
		Shard shard,
		SlotMeta victim,
		out RamBlockCopy? evicted,
		Action<BlockKey, byte[], int>? onEvictDirty,
		Action<RamBlockCopy>? onEvictClean)
	{
		evicted = null;
		int slotIndex = victim.SlotIndex;
		byte[] oldData = new byte[_blockSize];
		int read = ReadSlotBytes((long)slotIndex * _blockSize, oldData);
		if (read >= victim.ValidLength)
		{
			evicted = new RamBlockCopy
			{
				Key = victim.Key,
				Data = oldData,
				ValidLength = victim.ValidLength,
				IsDirty = victim.IsDirty,
				BackingFileSize = victim.BackingFileSize,
				BackingLastWriteUtcTicks = victim.BackingLastWriteUtcTicks,
				LastAccessUtc = new DateTime(Volatile.Read(ref victim.LastAccessUtcTicks), DateTimeKind.Utc),
				AiScore = victim.AiScore
			};
			if (victim.IsDirty)
			{
				onEvictDirty?.Invoke(victim.Key, oldData, victim.ValidLength);
			}
			else
			{
				onEvictClean?.Invoke(evicted);
			}
		}
		RemoveEntryLocked(shard, victim, pushFreeSlot: false);
		return slotIndex;
	}

	private int EvictSlotFromOtherShard(
		int excludeShardIdx,
		out RamBlockCopy? evicted,
		Action<BlockKey, byte[], int>? onEvictDirty,
		Action<RamBlockCopy>? onEvictClean)
	{
		evicted = null;
		for (int offset = 1; offset < _shards.Length; offset++)
		{
			Shard candidate = _shards[(excludeShardIdx + offset) & _shardMask];
			candidate.RwLock.EnterWriteLock();
			try
			{
				SlotMeta? victim = SelectVictimLocked(candidate);
				if (victim != null)
				{
					return EvictVictimAndReclaimSlotLocked(candidate, victim, out evicted, onEvictDirty, onEvictClean);
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
		WriteFullSlot(slotIndex, blockData);
		SlotMeta created = new SlotMeta
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

	public bool TryPatchOrGetForWrite(
		BlockKey key,
		int blockOffset,
		ReadOnlySpan<byte> patchData,
		long backingSize,
		long backingTicks,
		double aiScore,
		bool markDirty)
	{
		if (!IsEnabled || _handle == null)
		{
			return false;
		}

		Shard shard = GetShard(key);
		shard.RwLock.EnterWriteLock();
		try
		{
			if (!shard.Map.TryGetValue(key, out SlotMeta? e))
			{
				return false;
			}
			if (!e.IsDirty && (e.BackingFileSize != backingSize || e.BackingLastWriteUtcTicks != backingTicks))
			{
				RemoveEntryLocked(shard, e, pushFreeSlot: true);
				return false;
			}

			WriteSlotBytes((long)e.SlotIndex * _blockSize + blockOffset, patchData);
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
		if (!IsEnabled || _handle == null)
		{
			return result;
		}
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
								if (shard.Map.TryGetValue(new BlockKey(filterRelativePath, blockIdx), out SlotMeta? e) && e.IsDirty)
								{
									TryAddSnapshot(e, result);
								}
							}
						}
						continue;
					}
					if (shard.KeysByFile.TryGetValue(filterRelativePath, out HashSet<BlockKey>? keys))
					{
						foreach (BlockKey k in keys)
						{
							if (shard.Map.TryGetValue(k, out SlotMeta? e))
							{
								TryAddSnapshot(e, result);
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
							if (shard.Map.TryGetValue(new BlockKey(kv.Key, blockIdx), out SlotMeta? e) && e.IsDirty)
							{
								TryAddSnapshot(e, result);
							}
						}
					}
					continue;
				}

				foreach (SlotMeta e in shard.Map.Values)
				{
					TryAddSnapshot(e, result);
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
			if (shard.Map.TryGetValue(key, out SlotMeta? e))
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
						if (shard.Map.TryGetValue(k, out SlotMeta? e) && !e.IsDirty)
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
			if (shard.Map.TryGetValue(key, out SlotMeta? e))
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
					if (shard.Map.TryGetValue(k, out SlotMeta? e))
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
					if (!shard.Map.TryGetValue(k, out SlotMeta? e))
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
		List<(SlotMeta Entry, BlockKey NewKey)> crossShardMoves = new List<(SlotMeta, BlockKey)>();

		for (int i = 0; i < _shards.Length; i++)
		{
			Shard shard = _shards[i];
			shard.RwLock.EnterWriteLock();
			try
			{
				List<(SlotMeta Entry, BlockKey NewKey)>? renames = null;
				foreach (KeyValuePair<string, HashSet<BlockKey>> kv in shard.KeysByFile)
				{
					if (string.Equals(kv.Key, oldRelPath, StringComparison.OrdinalIgnoreCase) ||
					    kv.Key.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
					{
						foreach (BlockKey k in kv.Value)
						{
							if (!shard.Map.TryGetValue(k, out SlotMeta? e))
							{
								continue;
							}
							string nextPath = string.Equals(k.RelativePath, oldRelPath, StringComparison.OrdinalIgnoreCase)
								? newRelPath
								: newRelPath.TrimEnd('\\') + "\\" + k.RelativePath.Substring(oldPrefix.Length);
							renames ??= new List<(SlotMeta, BlockKey)>();
							renames.Add((e, new BlockKey(nextPath, k.BlockIndex)));
						}
					}
				}

				if (renames == null)
				{
					continue;
				}

				foreach ((SlotMeta entry, BlockKey newKey) in renames)
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

		foreach ((SlotMeta entry, BlockKey newKey) in crossShardMoves)
		{
			Shard targetShard = GetShard(newKey);
			targetShard.RwLock.EnterWriteLock();
			try
			{
				if (targetShard.Map.TryGetValue(newKey, out SlotMeta? existing))
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

	public void Clear()
	{
		for (int i = 0; i < _shards.Length; i++)
		{
			_shards[i].RwLock.EnterWriteLock();
		}
		try
		{
			for (int i = 0; i < _shards.Length; i++)
			{
				Shard shard = _shards[i];
				shard.Map.Clear();
				shard.Keys.Clear();
				shard.KeysByFile.Clear();
				shard.DirtyBlocksByFile.Clear();
				shard.DirtyMaxEndByFile.Clear();
				shard.DirtyCount = 0;
			}
			lock (_slotAllocLock)
			{
				_freeSlots.Clear();
				_nextSlotToAllocate = 0;
			}
			Volatile.Write(ref _totalDirtyCount, 0);
		}
		finally
		{
			for (int i = _shards.Length - 1; i >= 0; i--)
			{
				_shards[i].RwLock.ExitWriteLock();
			}
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		for (int i = 0; i < _shards.Length; i++)
		{
			_shards[i].RwLock.EnterWriteLock();
		}
		try
		{
			for (int i = 0; i < _shards.Length; i++)
			{
				Shard shard = _shards[i];
				shard.Map.Clear();
				shard.Keys.Clear();
				shard.KeysByFile.Clear();
				shard.DirtyBlocksByFile.Clear();
				shard.DirtyMaxEndByFile.Clear();
				shard.DirtyCount = 0;
			}
			lock (_slotAllocLock)
			{
				_freeSlots.Clear();
				_nextSlotToAllocate = 0;
			}
			Volatile.Write(ref _totalDirtyCount, 0);
			ReleaseMemoryMapLocked();
		}
		finally
		{
			for (int i = _shards.Length - 1; i >= 0; i--)
			{
				_shards[i].RwLock.ExitWriteLock();
			}
		}
		_handle?.Dispose();
		for (int i = 0; i < _shards.Length; i++)
		{
			_shards[i].Dispose();
		}

		try
		{
			if (File.Exists(CacheFilePath))
			{
				File.Delete(CacheFilePath);
			}
		}
		catch
		{
		}
	}

	private void TryAddSnapshot(SlotMeta e, List<RamBlockCopy> result)
	{
		if (_handle == null) return;
		byte[] dataCopy = new byte[_blockSize];
		int read = ReadSlotBytes((long)e.SlotIndex * _blockSize, dataCopy);
		if (read < e.ValidLength) return;
		result.Add(new RamBlockCopy
		{
			Key = e.Key,
			Data = dataCopy,
			ValidLength = e.ValidLength,
			IsDirty = e.IsDirty,
			BackingFileSize = e.BackingFileSize,
			BackingLastWriteUtcTicks = e.BackingLastWriteUtcTicks,
			LastAccessUtc = new DateTime(Volatile.Read(ref e.LastAccessUtcTicks), DateTimeKind.Utc),
			AiScore = e.AiScore
		});
	}

	private unsafe void WriteFullSlot(int slotIndex, ReadOnlySpan<byte> blockData)
	{
		long offset = (long)slotIndex * _blockSize;
		if (_mmfBasePtr != null && offset >= 0 && offset + _blockSize <= _mmfCapacityBytes)
		{
			Span<byte> dest = new Span<byte>(_mmfBasePtr + offset, _blockSize);
			int copyLen = Math.Min(blockData.Length, _blockSize);
			blockData.Slice(0, copyLen).CopyTo(dest);
			if (copyLen < _blockSize)
			{
				dest.Slice(copyLen).Clear();
			}
			return;
		}

		if (blockData.Length >= _blockSize)
		{
			RandomAccess.Write(_handle!, blockData.Slice(0, _blockSize), offset);
			return;
		}
		byte[] rented = ArrayPool<byte>.Shared.Rent(_blockSize);
		try
		{
			blockData.CopyTo(rented);
			Array.Clear(rented, blockData.Length, _blockSize - blockData.Length);
			RandomAccess.Write(_handle!, rented.AsSpan(0, _blockSize), offset);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rented);
		}
	}

	private unsafe void WriteSlotBytes(long offset, ReadOnlySpan<byte> data)
	{
		if (data.IsEmpty)
		{
			return;
		}
		if (_mmfBasePtr != null && offset >= 0 && offset + data.Length <= _mmfCapacityBytes)
		{
			data.CopyTo(new Span<byte>(_mmfBasePtr + offset, data.Length));
			return;
		}
		RandomAccess.Write(_handle!, data, offset);
	}

	private unsafe int ReadSlotBytes(long offset, Span<byte> buffer)
	{
		if (buffer.IsEmpty)
		{
			return 0;
		}
		if (_mmfBasePtr != null && offset >= 0 && offset + buffer.Length <= _mmfCapacityBytes)
		{
			new ReadOnlySpan<byte>(_mmfBasePtr + offset, buffer.Length).CopyTo(buffer);
			return buffer.Length;
		}
		return ReadExactHandle(_handle!, offset, buffer);
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

	private static SlotMeta? SelectVictimLocked(Shard shard)
	{
		int count = shard.Keys.Count;
		if (count == 0) return null;

		int samples = count <= 64 ? count : 32;
		int stride = count <= 64 ? 1 : Math.Max(1, count / samples);
		SlotMeta? oldest = null;
		long oldestStamp = long.MaxValue;
		int start = count <= 64 ? 0 : (shard.EvictCursor % count);
		for (int i = 0; i < samples; i++)
		{
			int idx = (start + i * stride) % count;
			SlotMeta candidate = shard.Map[shard.Keys[idx]];
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

	private void RemoveEntryLocked(Shard shard, SlotMeta e, bool pushFreeSlot)
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

	public static int CleanupOrphanCacheFiles(string ramDiskDirectory)
	{
		if (string.IsNullOrWhiteSpace(ramDiskDirectory) || !Directory.Exists(ramDiskDirectory))
		{
			return 0;
		}
		int removed = 0;
		try
		{
			if ((File.GetAttributes(ramDiskDirectory) & FileAttributes.ReparsePoint) != 0)
			{
				return 0;
			}
			foreach (string file in Directory.EnumerateFiles(ramDiskDirectory, "l1_cache_*.bin", SearchOption.TopDirectoryOnly))
			{
				try
				{
					if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
					{
						continue;
					}
					File.Delete(file);
					removed++;
				}
				catch (IOException)
				{
				}
				catch (UnauthorizedAccessException)
				{
				}
			}
		}
		catch
		{
		}
		return removed;
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
}
