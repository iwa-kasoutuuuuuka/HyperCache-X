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
/// Thread-safe Tier-1 in-memory 64 KB block cache with ReaderWriterLockSlim parallel reads,
/// O(1) per-file dirty tracking, and sampled LRU eviction.
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

	private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
	private readonly Dictionary<BlockKey, Entry> _map = new Dictionary<BlockKey, Entry>();
	private readonly List<BlockKey> _keys = new List<BlockKey>();
	private readonly Dictionary<string, HashSet<BlockKey>> _keysByFile = new Dictionary<string, HashSet<BlockKey>>(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, Dictionary<long, int>> _dirtyBlocksByFile = new Dictionary<string, Dictionary<long, int>>(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, long> _dirtyMaxEndByFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

	private readonly int _blockSize;
	private readonly int _maxBlocks;
	private long _accessClock;
	private int _evictCursor;
	private int _dirtyCount;
	private bool _disposed;

	public int BlockSize => _blockSize;
	public int MaxBlocks => _maxBlocks;
	public bool IsEnabled => _maxBlocks > 0 && !_disposed;
	public string BackendDescription => IsEnabled ? "In-Process RAM" : "OFF";

	public RamCacheTier(long capacityBytes, int blockSizeBytes)
	{
		_blockSize = blockSizeBytes;
		_maxBlocks = capacityBytes <= 0 ? 0 : (int)Math.Min(int.MaxValue, capacityBytes / blockSizeBytes);
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
		if (!IsEnabled)
		{
			return false;
		}

		bool needsEvictStale = false;
		_rwLock.EnterReadLock();
		try
		{
			if (!_map.TryGetValue(key, out Entry? e))
			{
				return false;
			}

			if (!e.IsDirty && (e.BackingFileSize != currentBackingSize || e.BackingLastWriteUtcTicks != currentBackingTicks))
			{
				needsEvictStale = true;
			}
			else
			{
				long stamp = Interlocked.Increment(ref _accessClock);
				Volatile.Write(ref e.LastAccessStamp, stamp);
				Volatile.Write(ref e.LastAccessUtcTicks, DateTime.UtcNow.Ticks);
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
			_rwLock.ExitReadLock();
		}

		if (needsEvictStale)
		{
			_rwLock.EnterWriteLock();
			try
			{
				if (_map.TryGetValue(key, out Entry? stale) &&
				    !stale.IsDirty &&
				    (stale.BackingFileSize != currentBackingSize || stale.BackingLastWriteUtcTicks != currentBackingTicks))
				{
					RemoveEntryLocked(stale);
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
		Action<BlockKey, byte[], int>? onEvictDirty = null,
		Action<RamBlockCopy>? onEvictClean = null)
	{
		if (!IsEnabled)
		{
			return null;
		}

		_rwLock.EnterWriteLock();
		try
		{
			long stamp = Interlocked.Increment(ref _accessClock);
			long utcTicks = DateTime.UtcNow.Ticks;

			if (_map.TryGetValue(key, out Entry? existing))
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
				UpdateDirtyIndexOnStateChangeLocked(key, wasDirty, isDirty, validLength);
				return null;
			}

			RamBlockCopy? evicted = null;
			if (_map.Count >= _maxBlocks)
			{
				Entry? victim = SelectVictimLocked();
				if (victim != null)
				{
					evicted = new RamBlockCopy
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
					RemoveEntryLocked(victim);
				}
			}

			byte[] buffer = new byte[_blockSize];
			blockData.Slice(0, Math.Min(blockData.Length, _blockSize)).CopyTo(buffer);
			Entry created = new Entry
			{
				Key = key,
				Data = buffer,
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
		if (!IsEnabled)
		{
			return false;
		}

		_rwLock.EnterWriteLock();
		try
		{
			if (!_map.TryGetValue(key, out Entry? e))
			{
				return false;
			}
			if (!e.IsDirty && (e.BackingFileSize != backingSize || e.BackingLastWriteUtcTicks != backingTicks))
			{
				RemoveEntryLocked(e);
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
		_rwLock.EnterReadLock();
		try
		{
			List<RamBlockCopy> result = new List<RamBlockCopy>();
			if (filterRelativePath != null)
			{
				if (dirtyOnly)
				{
					if (_dirtyBlocksByFile.TryGetValue(filterRelativePath, out Dictionary<long, int>? dirtyBlocks))
					{
						foreach (long blockIdx in dirtyBlocks.Keys)
						{
							if (_map.TryGetValue(new BlockKey(filterRelativePath, blockIdx), out Entry? e) && e.IsDirty)
							{
								result.Add(CloneEntry(e));
							}
						}
					}
					return result;
				}

				if (_keysByFile.TryGetValue(filterRelativePath, out HashSet<BlockKey>? keys))
				{
					foreach (BlockKey k in keys)
					{
						if (_map.TryGetValue(k, out Entry? e))
						{
							result.Add(CloneEntry(e));
						}
					}
				}
				return result;
			}

			if (dirtyOnly)
			{
				foreach (KeyValuePair<string, Dictionary<long, int>> fileKv in _dirtyBlocksByFile)
				{
					foreach (long blockIdx in fileKv.Value.Keys)
					{
						if (_map.TryGetValue(new BlockKey(fileKv.Key, blockIdx), out Entry? e) && e.IsDirty)
						{
							result.Add(CloneEntry(e));
						}
					}
				}
				return result;
			}

			foreach (Entry e in _map.Values)
			{
				result.Add(CloneEntry(e));
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
			if (_map.TryGetValue(key, out Entry? e))
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
					if (_map.TryGetValue(k, out Entry? e) && !e.IsDirty)
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
			if (_map.TryGetValue(key, out Entry? e))
			{
				RemoveEntryLocked(e);
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
				if (_map.TryGetValue(k, out Entry? e))
				{
					RemoveEntryLocked(e);
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
				if (!_map.TryGetValue(k, out Entry? e))
				{
					continue;
				}
				if (k.BlockIndex > lastValidBlock)
				{
					RemoveEntryLocked(e);
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
			List<(Entry Entry, BlockKey NewKey)> renames = new List<(Entry, BlockKey)>();
			foreach (KeyValuePair<string, HashSet<BlockKey>> kv in _keysByFile)
			{
				if (string.Equals(kv.Key, oldRelPath, StringComparison.OrdinalIgnoreCase) ||
				    kv.Key.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
				{
					foreach (BlockKey k in kv.Value)
					{
						if (!_map.TryGetValue(k, out Entry? e))
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

			foreach ((Entry entry, BlockKey newKey) in renames)
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
		_rwLock.Dispose();
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

	private Entry? SelectVictimLocked()
	{
		int count = _keys.Count;
		if (count == 0)
		{
			return null;
		}

		int samples = count <= 64 ? count : 32;
		int stride = count <= 64 ? 1 : Math.Max(1, count / samples);
		Entry? oldest = null;
		long oldestStamp = long.MaxValue;

		int start = count <= 64 ? 0 : (_evictCursor % count);
		for (int i = 0; i < samples; i++)
		{
			int idx = (start + i * stride) % count;
			Entry candidate = _map[_keys[idx]];
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

	private void RemoveEntryLocked(Entry e)
	{
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
			BlockKey movedKey = _keys[last];
			_keys[idx] = movedKey;
			_map[movedKey].KeyIndex = idx;
		}
		_keys.RemoveAt(last);
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
		if (!_dirtyBlocksByFile.TryGetValue(key.RelativePath, out Dictionary<long, int>? map))
		{
			return;
		}
		if (!map.Remove(key.BlockIndex, out int removedValidLen))
		{
			return;
		}
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
			if (end > max)
			{
				max = end;
			}
		}
		_dirtyMaxEndByFile[relPath] = max;
	}
}
