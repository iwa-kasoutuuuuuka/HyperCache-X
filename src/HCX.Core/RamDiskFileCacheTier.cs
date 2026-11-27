using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HCX.Core;

/// <summary>
/// Tier-1 cache backed by a dedicated cache file on an existing RAMDisk drive (e.g. R:\.hcx\l1_cache_{id}.bin),
/// supporting parallel ReaderWriterLockSlim reads, zero-alloc full-block writes, and O(1) dirty tracking.
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

	private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
	private readonly Dictionary<BlockKey, SlotMeta> _map = new Dictionary<BlockKey, SlotMeta>();
	private readonly List<BlockKey> _keys = new List<BlockKey>();
	private readonly Dictionary<string, HashSet<BlockKey>> _keysByFile = new Dictionary<string, HashSet<BlockKey>>(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, Dictionary<long, int>> _dirtyBlocksByFile = new Dictionary<string, Dictionary<long, int>>(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, long> _dirtyMaxEndByFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
	private readonly Stack<int> _freeSlots = new Stack<int>();

	private readonly SafeFileHandle? _handle;
	private readonly int _blockSize;
	private readonly int _maxBlocks;
	private int _nextSlotToAllocate;
	private long _accessClock;
	private int _evictCursor;
	private int _dirtyCount;
	private bool _disposed;

	public string CacheFilePath { get; }
	public int BlockSize => _blockSize;
	public int MaxBlocks => _maxBlocks;
	public bool IsEnabled => _handle != null && _maxBlocks > 0 && !_disposed;
	public string BackendDescription => IsEnabled ? $"RAMDisk ({CacheFilePath})" : "OFF";

	public RamDiskFileCacheTier(string ramDiskDirectory, string instanceId, long capacityBytes, int blockSizeBytes)
	{
		if (string.IsNullOrWhiteSpace(ramDiskDirectory))
		{
			throw new ArgumentException("RAMDisk directory must not be empty.", nameof(ramDiskDirectory));
		}

		_blockSize = blockSizeBytes;
		_maxBlocks = capacityBytes <= 0 ? 0 : (int)Math.Min(int.MaxValue, capacityBytes / blockSizeBytes);
		CacheFilePath = Path.Combine(ramDiskDirectory, $"l1_cache_{instanceId}.bin");

		if (_maxBlocks > 0)
		{
			Directory.CreateDirectory(ramDiskDirectory);
			_handle = File.OpenHandle(
				CacheFilePath,
				FileMode.Create,
				FileAccess.ReadWrite,
				FileShare.Read,
				FileOptions.RandomAccess);
		}
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

		bool needsEvictStale = false;
		_rwLock.EnterReadLock();
		try
		{
			if (!_map.TryGetValue(key, out SlotMeta? e))
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
					int actual = ReadExactHandle(_handle, fileOffset, destination.Slice(0, toRead));
					if (actual < toRead)
					{
						needsEvictStale = true;
						goto ExitRead;
					}
				}

				bytesCopied = toRead;
				isDirty = e.IsDirty;
				Volatile.Write(ref e.LastAccessStamp, Interlocked.Increment(ref _accessClock));
				Volatile.Write(ref e.LastAccessUtcTicks, DateTime.UtcNow.Ticks);
				return true;
			}
			ExitRead:;
		}
		finally
		{
			_rwLock.ExitReadLock();
		}

		if (needsEvictStale)
		{
			_rwLock.EnterWriteLock();
			try
			{
				if (_map.TryGetValue(key, out SlotMeta? stale))
				{
					RemoveEntryLocked(stale, pushFreeSlot: true);
					invalidatedStale = true;
				}
			}
			finally
			{
				_rwLock.ExitWriteLock();
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
		Action<BlockKey, byte[], int>? onEvictDirty = null)
	{
		if (!IsEnabled || _handle == null)
		{
			return null;
		}

		_rwLock.EnterWriteLock();
		try
		{
			long stamp = Interlocked.Increment(ref _accessClock);
			long utcTicks = DateTime.UtcNow.Ticks;

			if (_map.TryGetValue(key, out SlotMeta? existing))
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
				UpdateDirtyIndexOnStateChangeLocked(key, wasDirty, isDirty, validLength);
				return null;
			}

			RamBlockCopy? evicted = null;
			int slotIndex;
			if (_freeSlots.Count > 0)
			{
				slotIndex = _freeSlots.Pop();
			}
			else if (_nextSlotToAllocate < _maxBlocks)
			{
				slotIndex = _nextSlotToAllocate++;
			}
			else
			{
				SlotMeta? victim = SelectVictimLocked();
				if (victim == null)
				{
					return null;
				}
				slotIndex = victim.SlotIndex;
				byte[] oldData = new byte[_blockSize];
				int read = ReadExactHandle(_handle, (long)slotIndex * _blockSize, oldData);
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
				}
				RemoveEntryLocked(victim, pushFreeSlot: false);
			}

			WriteFullSlot(_handle, slotIndex, _blockSize, blockData);
			SlotMeta created = new SlotMeta
			{
				Key = key,
				SlotIndex = slotIndex,
				KeyIndex = _keys.Count,
				ValidLength = validLength,
				IsDirty = isDirty,
				BackingFileSize = backingSize,
				BackingLastWriteUtcTicks = backingTicks,
				LastAccessStamp = stamp,
				LastAccessUtcTicks = utcTicks,
				AiScore = aiScore
			};
			_keys.Add(key);
			_map[key] = created;
			AddFileKeyIndexLocked(key);
			if (isDirty)
			{
				_dirtyCount++;
				SetDirtyIndexLocked(key, validLength);
			}
			return evicted;
		}
		finally
		{
			_rwLock.ExitWriteLock();
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

		_rwLock.EnterWriteLock();
		try
		{
			if (!_map.TryGetValue(key, out SlotMeta? e))
			{
				return false;
			}
			if (!e.IsDirty && (e.BackingFileSize != backingSize || e.BackingLastWriteUtcTicks != backingTicks))
			{
				RemoveEntryLocked(e, pushFreeSlot: true);
				return false;
			}

			RandomAccess.Write(_handle, patchData, (long)e.SlotIndex * _blockSize + blockOffset);
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
			UpdateDirtyIndexOnStateChangeLocked(key, wasDirty, e.IsDirty, e.ValidLength);

			e.LastAccessStamp = Interlocked.Increment(ref _accessClock);
			e.LastAccessUtcTicks = DateTime.UtcNow.Ticks;
			e.AiScore = aiScore;
			return true;
		}
		finally
		{
			_rwLock.ExitWriteLock();
		}
	}

	public List<RamBlockCopy> SnapshotEntries(string? filterRelativePath = null, bool dirtyOnly = false)
	{
		if (!IsEnabled || _handle == null)
		{
			return new List<RamBlockCopy>();
		}

		_rwLock.EnterReadLock();
		try
		{
			List<RamBlockCopy> result = new List<RamBlockCopy>();
			if (filterRelativePath != null)
			{
				if (dirtyOnly)
				{
					if (_dirtyBlocksByFile.TryGetValue(filterRelativePath, out Dictionary<long, int>? dirtyMap))
					{
						foreach (long blockIdx in dirtyMap.Keys)
						{
							if (_map.TryGetValue(new BlockKey(filterRelativePath, blockIdx), out SlotMeta? e) && e.IsDirty)
							{
								TryAddSnapshot(e, result);
							}
						}
					}
					return result;
				}
				if (_keysByFile.TryGetValue(filterRelativePath, out HashSet<BlockKey>? keys))
				{
					foreach (BlockKey k in keys)
					{
						if (_map.TryGetValue(k, out SlotMeta? e))
						{
							TryAddSnapshot(e, result);
						}
					}
				}
				return result;
			}

			if (dirtyOnly)
			{
				foreach (KeyValuePair<string, Dictionary<long, int>> kv in _dirtyBlocksByFile)
				{
					foreach (long blockIdx in kv.Value.Keys)
					{
						if (_map.TryGetValue(new BlockKey(kv.Key, blockIdx), out SlotMeta? e) && e.IsDirty)
						{
							TryAddSnapshot(e, result);
						}
					}
				}
				return result;
			}

			foreach (SlotMeta e in _map.Values)
			{
				TryAddSnapshot(e, result);
			}
			return result;
		}
		finally
		{
			_rwLock.ExitReadLock();
		}
	}

	public void MarkClean(BlockKey key, long newBackingSize, long newBackingTicks)
	{
		_rwLock.EnterWriteLock();
		try
		{
			if (_map.TryGetValue(key, out SlotMeta? e))
			{
				if (e.IsDirty)
				{
					e.IsDirty = false;
					_dirtyCount = Math.Max(0, _dirtyCount - 1);
					RemoveDirtyIndexLocked(key);
				}
				e.BackingFileSize = newBackingSize;
				e.BackingLastWriteUtcTicks = newBackingTicks;
			}
		}
		finally
		{
			_rwLock.ExitWriteLock();
		}
	}

	public void UpdateBackingMetadataForFile(string normalizedRelPath, long newBackingSize, long newBackingTicks)
	{
		_rwLock.EnterWriteLock();
		try
		{
			if (_keysByFile.TryGetValue(normalizedRelPath, out HashSet<BlockKey>? keys))
			{
				foreach (BlockKey k in keys)
				{
					if (_map.TryGetValue(k, out SlotMeta? e) && !e.IsDirty)
					{
						e.BackingFileSize = newBackingSize;
						e.BackingLastWriteUtcTicks = newBackingTicks;
					}
				}
			}
		}
		finally
		{
			_rwLock.ExitWriteLock();
		}
	}

	public void Remove(BlockKey key)
	{
		_rwLock.EnterWriteLock();
		try
		{
			if (_map.TryGetValue(key, out SlotMeta? e))
			{
				RemoveEntryLocked(e, pushFreeSlot: true);
			}
		}
		finally
		{
			_rwLock.ExitWriteLock();
		}
	}

	public void RemoveByFile(string normalizedRelPath)
	{
		_rwLock.EnterWriteLock();
		try
		{
			if (!_keysByFile.TryGetValue(normalizedRelPath, out HashSet<BlockKey>? keys))
			{
				return;
			}
			List<BlockKey> toRemove = new List<BlockKey>(keys);
			foreach (BlockKey k in toRemove)
			{
				if (_map.TryGetValue(k, out SlotMeta? e))
				{
					RemoveEntryLocked(e, pushFreeSlot: true);
				}
			}
		}
		finally
		{
			_rwLock.ExitWriteLock();
		}
	}

	public void TruncateFile(string normalizedRelPath, long newSize)
	{
		_rwLock.EnterWriteLock();
		try
		{
			if (!_keysByFile.TryGetValue(normalizedRelPath, out HashSet<BlockKey>? keys))
			{
				return;
			}
			long lastValidBlock = newSize <= 0 ? -1 : (newSize - 1) / _blockSize;
			List<BlockKey> snapshot = new List<BlockKey>(keys);
			foreach (BlockKey k in snapshot)
			{
				if (!_map.TryGetValue(k, out SlotMeta? e))
				{
					continue;
				}
				if (k.BlockIndex > lastValidBlock)
				{
					RemoveEntryLocked(e, pushFreeSlot: true);
				}
				else if (k.BlockIndex == lastValidBlock)
				{
					int newValidLen = (int)(newSize - lastValidBlock * _blockSize);
					if (newValidLen < e.ValidLength)
					{
						e.ValidLength = newValidLen;
						if (e.IsDirty)
						{
							SetDirtyIndexLocked(k, newValidLen);
						}
					}
				}
			}
		}
		finally
		{
			_rwLock.ExitWriteLock();
		}
	}

	public void RenamePrefix(string oldRelPath, string newRelPath)
	{
		_rwLock.EnterWriteLock();
		try
		{
			string oldPrefix = oldRelPath.TrimEnd('\\') + "\\";
			List<(SlotMeta Entry, BlockKey NewKey)> renames = new List<(SlotMeta, BlockKey)>();
			foreach (KeyValuePair<string, HashSet<BlockKey>> kv in _keysByFile)
			{
				if (string.Equals(kv.Key, oldRelPath, StringComparison.OrdinalIgnoreCase) ||
				    kv.Key.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
				{
					foreach (BlockKey k in kv.Value)
					{
						if (!_map.TryGetValue(k, out SlotMeta? e))
						{
							continue;
						}
						string nextPath = string.Equals(k.RelativePath, oldRelPath, StringComparison.OrdinalIgnoreCase)
							? newRelPath
							: newRelPath.TrimEnd('\\') + "\\" + k.RelativePath.Substring(oldPrefix.Length);
						renames.Add((e, new BlockKey(nextPath, k.BlockIndex)));
					}
				}
			}

			foreach ((SlotMeta entry, BlockKey newKey) in renames)
			{
				BlockKey oldKey = entry.Key;
				bool wasDirty = entry.IsDirty;
				int validLen = entry.ValidLength;
				int idx = entry.KeyIndex;

				_map.Remove(oldKey);
				RemoveFileKeyIndexLocked(oldKey);
				if (wasDirty)
				{
					RemoveDirtyIndexLocked(oldKey);
				}

				entry.Key = newKey;
				_keys[idx] = newKey;
				_map[newKey] = entry;
				AddFileKeyIndexLocked(newKey);
				if (wasDirty)
				{
					SetDirtyIndexLocked(newKey, validLen);
				}
			}
		}
		finally
		{
			_rwLock.ExitWriteLock();
		}
	}

	public long GetTrackedMaxFileSize(string normalizedRelPath)
	{
		_rwLock.EnterReadLock();
		try
		{
			return _dirtyMaxEndByFile.TryGetValue(normalizedRelPath, out long maxEnd) ? maxEnd : 0L;
		}
		finally
		{
			_rwLock.ExitReadLock();
		}
	}

	public (int CachedBlocks, long UsedBytes, int DirtyBlocks) GetCounts()
	{
		_rwLock.EnterReadLock();
		try
		{
			return (_map.Count, (long)_map.Count * _blockSize, _dirtyCount);
		}
		finally
		{
			_rwLock.ExitReadLock();
		}
	}

	public void Clear()
	{
		_rwLock.EnterWriteLock();
		try
		{
			_map.Clear();
			_keys.Clear();
			_keysByFile.Clear();
			_dirtyBlocksByFile.Clear();
			_dirtyMaxEndByFile.Clear();
			_freeSlots.Clear();
			_nextSlotToAllocate = 0;
			_dirtyCount = 0;
		}
		finally
		{
			_rwLock.ExitWriteLock();
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		Clear();
		_handle?.Dispose();
		_rwLock.Dispose();

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
		int read = ReadExactHandle(_handle, (long)e.SlotIndex * _blockSize, dataCopy);
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

	private SlotMeta? SelectVictimLocked()
	{
		int count = _keys.Count;
		if (count == 0) return null;

		int samples = count <= 64 ? count : 32;
		int stride = count <= 64 ? 1 : Math.Max(1, count / samples);
		SlotMeta? oldest = null;
		long oldestStamp = long.MaxValue;
		int start = count <= 64 ? 0 : (_evictCursor % count);
		for (int i = 0; i < samples; i++)
		{
			int idx = (start + i * stride) % count;
			SlotMeta candidate = _map[_keys[idx]];
			long stamp = Volatile.Read(ref candidate.LastAccessStamp);
			if (stamp < oldestStamp)
			{
				oldestStamp = stamp;
				oldest = candidate;
			}
		}
		_evictCursor = (start + 1) % Math.Max(1, count);
		return oldest;
	}

	private void RemoveEntryLocked(SlotMeta e, bool pushFreeSlot)
	{
		int slot = e.SlotIndex;
		if (e.IsDirty)
		{
			_dirtyCount = Math.Max(0, _dirtyCount - 1);
			RemoveDirtyIndexLocked(e.Key);
		}
		_map.Remove(e.Key);
		RemoveFileKeyIndexLocked(e.Key);

		int idx = e.KeyIndex;
		int last = _keys.Count - 1;
		if (idx < last)
		{
			BlockKey moved = _keys[last];
			_keys[idx] = moved;
			_map[moved].KeyIndex = idx;
		}
		_keys.RemoveAt(last);
		if (pushFreeSlot)
		{
			_freeSlots.Push(slot);
		}
	}

	private void AddFileKeyIndexLocked(BlockKey key)
	{
		if (!_keysByFile.TryGetValue(key.RelativePath, out HashSet<BlockKey>? set))
		{
			set = new HashSet<BlockKey>();
			_keysByFile[key.RelativePath] = set;
		}
		set.Add(key);
	}

	private void RemoveFileKeyIndexLocked(BlockKey key)
	{
		if (_keysByFile.TryGetValue(key.RelativePath, out HashSet<BlockKey>? set))
		{
			set.Remove(key);
			if (set.Count == 0)
			{
				_keysByFile.Remove(key.RelativePath);
			}
		}
	}

	private void UpdateDirtyIndexOnStateChangeLocked(BlockKey key, bool wasDirty, bool isNowDirty, int validLen)
	{
		if (!wasDirty && isNowDirty)
		{
			_dirtyCount++;
			SetDirtyIndexLocked(key, validLen);
		}
		else if (wasDirty && !isNowDirty)
		{
			_dirtyCount = Math.Max(0, _dirtyCount - 1);
			RemoveDirtyIndexLocked(key);
		}
		else if (isNowDirty)
		{
			SetDirtyIndexLocked(key, validLen);
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
			foreach (string file in Directory.EnumerateFiles(ramDiskDirectory, "l1_cache_*.bin", SearchOption.TopDirectoryOnly))
			{
				try
				{
					File.Delete(file);
					removed++;
				}
				catch (IOException)
				{
					// File is actively held open by a running instance.
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

	private void SetDirtyIndexLocked(BlockKey key, int validLen)
	{
		if (!_dirtyBlocksByFile.TryGetValue(key.RelativePath, out Dictionary<long, int>? map))
		{
			map = new Dictionary<long, int>();
			_dirtyBlocksByFile[key.RelativePath] = map;
		}
		bool hadPrev = map.TryGetValue(key.BlockIndex, out int prevValidLen);
		long prevEnd = hadPrev ? key.BlockIndex * _blockSize + prevValidLen : -1L;
		map[key.BlockIndex] = validLen;
		long end = key.BlockIndex * _blockSize + validLen;
		if (!_dirtyMaxEndByFile.TryGetValue(key.RelativePath, out long curMax) || end > curMax)
		{
			_dirtyMaxEndByFile[key.RelativePath] = end;
		}
		else if (hadPrev && prevEnd >= curMax && end < prevEnd)
		{
			RecomputeDirtyMaxEndLocked(key.RelativePath, map);
		}
	}

	private void RemoveDirtyIndexLocked(BlockKey key)
	{
		if (!_dirtyBlocksByFile.TryGetValue(key.RelativePath, out Dictionary<long, int>? map)) return;
		if (!map.Remove(key.BlockIndex, out int removedValidLen)) return;
		if (map.Count == 0)
		{
			_dirtyBlocksByFile.Remove(key.RelativePath);
			_dirtyMaxEndByFile.Remove(key.RelativePath);
		}
		else if (_dirtyMaxEndByFile.TryGetValue(key.RelativePath, out long curMax) &&
		         key.BlockIndex * _blockSize + removedValidLen >= curMax)
		{
			RecomputeDirtyMaxEndLocked(key.RelativePath, map);
		}
	}

	private void RecomputeDirtyMaxEndLocked(string relPath, Dictionary<long, int> map)
	{
		long max = 0;
		foreach (KeyValuePair<long, int> kv in map)
		{
			long end = kv.Key * _blockSize + kv.Value;
			if (end > max) max = end;
		}
		_dirtyMaxEndByFile[relPath] = max;
	}
}
