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
/// with ReaderWriterLockSlim parallel reads, zero-alloc full-block writes, and O(1) dirty tracking.
/// </summary>
public sealed class SsdFileCacheTier : IDisposable
{
	private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
	private readonly Dictionary<BlockKey, SsdSlotEntry> _map = new Dictionary<BlockKey, SsdSlotEntry>();
	private readonly List<BlockKey> _keys = new List<BlockKey>();
	private readonly Dictionary<string, HashSet<BlockKey>> _keysByFile = new Dictionary<string, HashSet<BlockKey>>(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, Dictionary<long, int>> _dirtyBlocksByFile = new Dictionary<string, Dictionary<long, int>>(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, long> _dirtyMaxEndByFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
	private readonly Stack<int> _freeSlots = new Stack<int>();

	private readonly SafeFileHandle? _handle;
	private readonly int _blockSize;
	private readonly int _maxSlots;
	private int _nextSlotToAllocate;
	private long _accessClock;
	private int _evictCursor;
	private int _dirtyCount;
	private bool _disposed;

	public string CacheFilePath { get; }
	public int BlockSize => _blockSize;
	public int MaxSlots => _maxSlots;
	public bool IsEnabled => _handle != null && _maxSlots > 0 && !_disposed;

	public SsdFileCacheTier(string cacheDirectory, string instanceId, long capacityBytes, int blockSizeBytes)
	{
		_blockSize = blockSizeBytes;
		_maxSlots = capacityBytes <= 0 ? 0 : (int)Math.Min(int.MaxValue, capacityBytes / blockSizeBytes);
		CacheFilePath = string.IsNullOrWhiteSpace(cacheDirectory)
			? string.Empty
			: Path.Combine(cacheDirectory, $"cache_{instanceId}.bin");

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

		bool needsEvictStale = false;
		_rwLock.EnterReadLock();
		try
		{
			if (!_map.TryGetValue(key, out SsdSlotEntry? entry))
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
					Volatile.Write(ref entry.LastAccessStamp, Interlocked.Increment(ref _accessClock));
					Volatile.Write(ref entry.LastAccessUtcTicks, DateTime.UtcNow.Ticks);
					meta = CloneMeta(entry);
					return true;
				}
			}
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
				if (_map.TryGetValue(key, out SsdSlotEntry? stale))
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

	public bool ContainsFresh(BlockKey key, long currentBackingSize, long currentBackingTicks)
	{
		if (!IsEnabled)
		{
			return false;
		}
		_rwLock.EnterReadLock();
		try
		{
			return _map.TryGetValue(key, out SsdSlotEntry? entry)
				&& entry.BackingFileSize == currentBackingSize
				&& entry.BackingLastWriteUtcTicks == currentBackingTicks;
		}
		finally
		{
			_rwLock.ExitReadLock();
		}
	}

	public bool TryReadBlockForFlush(BlockKey key, byte[] destination64K, out int validLength)
	{
		validLength = 0;
		if (!IsEnabled || _handle == null)
		{
			return false;
		}

		_rwLock.EnterReadLock();
		try
		{
			if (!_map.TryGetValue(key, out SsdSlotEntry? entry))
			{
				return false;
			}
			long fileOffset = (long)entry.SlotIndex * _blockSize;
			int read = ReadExactHandle(_handle, fileOffset, destination64K.AsSpan(0, _blockSize));
			if (read < entry.ValidLength)
			{
				return false;
			}
			validLength = entry.ValidLength;
			return true;
		}
		finally
		{
			_rwLock.ExitReadLock();
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

		_rwLock.EnterWriteLock();
		try
		{
			long stamp = Interlocked.Increment(ref _accessClock);
			long utcTicks = DateTime.UtcNow.Ticks;

			if (_map.TryGetValue(key, out SsdSlotEntry? existing))
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
				return true;
			}

			int slotIndex;
			if (_freeSlots.Count > 0)
			{
				slotIndex = _freeSlots.Pop();
			}
			else if (_nextSlotToAllocate < _maxSlots)
			{
				slotIndex = _nextSlotToAllocate++;
			}
			else
			{
				SsdSlotEntry? victim = SelectVictimLocked();
				if (victim == null)
				{
					return false;
				}
				slotIndex = victim.SlotIndex;

				if (victim.IsDirty)
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
				RemoveEntryLocked(victim, pushFreeSlot: false);
			}

			WriteFullSlot(_handle, slotIndex, _blockSize, blockData);
			SsdSlotEntry created = new SsdSlotEntry
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
			return true;
		}
		finally
		{
			_rwLock.ExitWriteLock();
		}
	}

	public void RestoreEntries(IEnumerable<PersistedBlockEntry> entries)
	{
		if (!IsEnabled || _handle == null)
		{
			return;
		}

		long fileLength = RandomAccess.GetLength(_handle);
		_rwLock.EnterWriteLock();
		try
		{
			_map.Clear();
			_keys.Clear();
			_keysByFile.Clear();
			_dirtyBlocksByFile.Clear();
			_dirtyMaxEndByFile.Clear();
			_freeSlots.Clear();
			_dirtyCount = 0;
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
				SsdSlotEntry created = new SsdSlotEntry
				{
					Key = key,
					SlotIndex = item.SlotIndex,
					KeyIndex = _keys.Count,
					ValidLength = Math.Clamp(item.ValidLength, 0, _blockSize),
					IsDirty = item.IsDirty,
					BackingFileSize = item.BackingFileSize,
					BackingLastWriteUtcTicks = item.BackingLastWriteUtcTicks,
					LastAccessStamp = Interlocked.Increment(ref _accessClock),
					LastAccessUtcTicks = item.LastAccessUtcTicks,
					AiScore = item.AiScore
				};
				_keys.Add(key);
				_map[key] = created;
				AddFileKeyIndexLocked(key);
				if (created.IsDirty)
				{
					_dirtyCount++;
					SetDirtyIndexLocked(key, created.ValidLength);
				}
				if (item.SlotIndex > maxSlotSeen)
				{
					maxSlotSeen = item.SlotIndex;
				}
			}

			_nextSlotToAllocate = maxSlotSeen + 1;
			for (int s = 0; s < _nextSlotToAllocate; s++)
			{
				if (!usedSlots.Contains(s))
				{
					_freeSlots.Push(s);
				}
			}
			ShrinkFileIfNeededLocked();
		}
		finally
		{
			_rwLock.ExitWriteLock();
		}
	}

	public List<SsdBlockMeta> SnapshotEntries(string? filterRelativePath = null, bool dirtyOnly = false)
	{
		_rwLock.EnterReadLock();
		try
		{
			List<SsdBlockMeta> result = new List<SsdBlockMeta>();
			if (filterRelativePath != null)
			{
				if (dirtyOnly)
				{
					if (_dirtyBlocksByFile.TryGetValue(filterRelativePath, out Dictionary<long, int>? dirtyMap))
					{
						foreach (long blockIdx in dirtyMap.Keys)
						{
							if (_map.TryGetValue(new BlockKey(filterRelativePath, blockIdx), out SsdSlotEntry? e) && e.IsDirty)
							{
								result.Add(CloneMeta(e));
							}
						}
					}
					return result;
				}
				if (_keysByFile.TryGetValue(filterRelativePath, out HashSet<BlockKey>? keys))
				{
					foreach (BlockKey k in keys)
					{
						if (_map.TryGetValue(k, out SsdSlotEntry? e))
						{
							result.Add(CloneMeta(e));
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
						if (_map.TryGetValue(new BlockKey(kv.Key, blockIdx), out SsdSlotEntry? e) && e.IsDirty)
						{
							result.Add(CloneMeta(e));
						}
					}
				}
				return result;
			}

			foreach (SsdSlotEntry e in _map.Values)
			{
				result.Add(CloneMeta(e));
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
			if (_map.TryGetValue(key, out SsdSlotEntry? e))
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
					if (_map.TryGetValue(k, out SsdSlotEntry? e) && !e.IsDirty)
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
			if (_map.TryGetValue(key, out SsdSlotEntry? e))
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
				if (_map.TryGetValue(k, out SsdSlotEntry? e))
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
		if (!IsEnabled || _handle == null)
		{
			return;
		}
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
				if (!_map.TryGetValue(k, out SsdSlotEntry? e))
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
			List<(SsdSlotEntry Entry, BlockKey NewKey)> renames = new List<(SsdSlotEntry, BlockKey)>();
			foreach (KeyValuePair<string, HashSet<BlockKey>> kv in _keysByFile)
			{
				if (string.Equals(kv.Key, oldRelPath, StringComparison.OrdinalIgnoreCase) ||
				    kv.Key.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
				{
					foreach (BlockKey k in kv.Value)
					{
						if (!_map.TryGetValue(k, out SsdSlotEntry? e))
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

			foreach ((SsdSlotEntry entry, BlockKey newKey) in renames)
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

	public void FlushHandleToDisk()
	{
		if (!IsEnabled || _handle == null)
		{
			return;
		}
		_rwLock.EnterWriteLock();
		try
		{
			RandomAccess.FlushToDisk(_handle);
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
		_handle?.Dispose();
		_rwLock.Dispose();
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

	private SsdSlotEntry? SelectVictimLocked()
	{
		int count = _keys.Count;
		if (count == 0) return null;

		int samples = count <= 64 ? count : 32;
		int stride = count <= 64 ? 1 : Math.Max(1, count / samples);
		SsdSlotEntry? oldest = null;
		long oldestStamp = long.MaxValue;
		int start = count <= 64 ? 0 : (_evictCursor % count);
		for (int i = 0; i < samples; i++)
		{
			int idx = (start + i * stride) % count;
			SsdSlotEntry candidate = _map[_keys[idx]];
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

	private void RemoveEntryLocked(SsdSlotEntry e, bool pushFreeSlot)
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

	public static int DeleteCacheFiles(string cacheDirectory, string? instanceId = null)
	{
		if (string.IsNullOrWhiteSpace(cacheDirectory) || !Directory.Exists(cacheDirectory))
		{
			return 0;
		}
		int deleted = 0;
		string[] patterns = string.IsNullOrWhiteSpace(instanceId)
			? new[] { "cache_*.bin", "cache_metadata_*.dat", "cache_metadata_*.dat.tmp" }
			: new[] { $"cache_{instanceId}.bin", $"cache_metadata_{instanceId}.dat", $"cache_metadata_{instanceId}.dat.tmp" };

		foreach (string pattern in patterns)
		{
			foreach (string file in Directory.EnumerateFiles(cacheDirectory, pattern, SearchOption.TopDirectoryOnly))
			{
				try
				{
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

	private void ShrinkFileIfNeededLocked()
	{
		if (_handle == null || _maxSlots <= 0)
		{
			return;
		}
		long maxBytes = (long)_maxSlots * _blockSize;
		if (_nextSlotToAllocate <= _maxSlots && RandomAccess.GetLength(_handle) > maxBytes)
		{
			RandomAccess.SetLength(_handle, maxBytes);
		}
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
