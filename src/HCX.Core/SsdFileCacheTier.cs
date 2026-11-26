using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace HCX.Core;

public sealed class SsdBlockMeta
{
	public required BlockKey Key { get; set; }
	public int SlotIndex { get; init; }
	public int ValidLength { get; set; }
	public bool IsDirty { get; set; }
	public long BackingFileSize { get; set; }
	public long BackingLastWriteUtcTicks { get; set; }
	public DateTime LastAccessUtc { get; set; }
	public double AiScore { get; set; }
}

public sealed class EvictedSsdBlock
{
	public required BlockKey Key { get; init; }
	public required byte[] Data { get; init; }
	public int ValidLength { get; init; }
}

/// <summary>
/// Safe Tier-2 SSD cache backed by a dedicated cache file (&lt;SsdDir&gt;\cache_{id}.bin) using RandomAccess.
/// Never opens or writes raw physical volumes.
/// </summary>
public sealed class SsdFileCacheTier : IDisposable
{
	private readonly object _sync = new object();
	private readonly Dictionary<BlockKey, LinkedListNode<SsdBlockMeta>> _map = new Dictionary<BlockKey, LinkedListNode<SsdBlockMeta>>();
	private readonly LinkedList<SsdBlockMeta> _lru = new LinkedList<SsdBlockMeta>();
	private readonly Stack<int> _freeSlots = new Stack<int>();
	private readonly SafeFileHandle? _handle;
	private readonly int _blockSize;
	private readonly int _maxSlots;
	private int _nextSlotToAllocate;
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

		lock (_sync)
		{
			if (!_map.TryGetValue(key, out LinkedListNode<SsdBlockMeta>? node))
			{
				return false;
			}

			SsdBlockMeta entry = node.Value;
			if (!entry.IsDirty &&
			    (entry.BackingFileSize != currentBackingSize || entry.BackingLastWriteUtcTicks != currentBackingTicks))
			{
				RemoveNodeLocked(node);
				invalidatedStale = true;
				return false;
			}

			long fileOffset = (long)entry.SlotIndex * _blockSize;
			int totalRead = ReadExactLocked(fileOffset, destination64K.AsSpan(0, _blockSize));
			if (totalRead < entry.ValidLength)
			{
				RemoveNodeLocked(node);
				return false;
			}

			_lru.Remove(node);
			_lru.AddFirst(node);
			entry.LastAccessUtc = DateTime.UtcNow;
			meta = CloneMeta(entry);
			return true;
		}
	}

	public bool ContainsFresh(BlockKey key, long currentBackingSize, long currentBackingTicks)
	{
		if (!IsEnabled)
		{
			return false;
		}
		lock (_sync)
		{
			return _map.TryGetValue(key, out LinkedListNode<SsdBlockMeta>? node)
				&& node.Value.BackingFileSize == currentBackingSize
				&& node.Value.BackingLastWriteUtcTicks == currentBackingTicks;
		}
	}

	public bool TryReadBlockForFlush(BlockKey key, byte[] destination64K, out int validLength)
	{
		validLength = 0;
		if (!IsEnabled || _handle == null)
		{
			return false;
		}

		lock (_sync)
		{
			if (!_map.TryGetValue(key, out LinkedListNode<SsdBlockMeta>? node))
			{
				return false;
			}
			SsdBlockMeta entry = node.Value;
			long fileOffset = (long)entry.SlotIndex * _blockSize;
			int read = ReadExactLocked(fileOffset, destination64K.AsSpan(0, _blockSize));
			if (read < entry.ValidLength)
			{
				return false;
			}
			validLength = entry.ValidLength;
			return true;
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
		out EvictedSsdBlock? evictedDirty)
	{
		evictedDirty = null;
		if (!IsEnabled || _handle == null)
		{
			return false;
		}

		byte[] padded = new byte[_blockSize];
		blockData.Slice(0, Math.Min(blockData.Length, _blockSize)).CopyTo(padded);

		lock (_sync)
		{
			if (_map.TryGetValue(key, out LinkedListNode<SsdBlockMeta>? existing))
			{
				SsdBlockMeta entry = existing.Value;
				RandomAccess.Write(_handle, padded, (long)entry.SlotIndex * _blockSize);
				entry.ValidLength = validLength;
				if (!entry.IsDirty && isDirty)
				{
					_dirtyCount++;
				}
				else if (entry.IsDirty && !isDirty)
				{
					_dirtyCount = Math.Max(0, _dirtyCount - 1);
				}
				entry.IsDirty = isDirty;
				entry.BackingFileSize = backingSize;
				entry.BackingLastWriteUtcTicks = backingTicks;
				entry.LastAccessUtc = DateTime.UtcNow;
				entry.AiScore = aiScore;
				_lru.Remove(existing);
				_lru.AddFirst(existing);
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
			else if (_lru.Last != null)
			{
				LinkedListNode<SsdBlockMeta> tail = _lru.Last;
				SsdBlockMeta tailMeta = tail.Value;
				slotIndex = tailMeta.SlotIndex;

				if (tailMeta.IsDirty)
				{
					byte[] oldData = new byte[_blockSize];
					int oldRead = ReadExactLocked((long)slotIndex * _blockSize, oldData);
					if (oldRead >= tailMeta.ValidLength)
					{
						evictedDirty = new EvictedSsdBlock
						{
							Key = tailMeta.Key,
							Data = oldData,
							ValidLength = tailMeta.ValidLength
						};
					}
				}
				RemoveNodeWithoutFreeLocked(tail);
			}
			else
			{
				return false;
			}

			RandomAccess.Write(_handle, padded, (long)slotIndex * _blockSize);
			SsdBlockMeta created = new SsdBlockMeta
			{
				Key = key,
				SlotIndex = slotIndex,
				ValidLength = validLength,
				IsDirty = isDirty,
				BackingFileSize = backingSize,
				BackingLastWriteUtcTicks = backingTicks,
				LastAccessUtc = DateTime.UtcNow,
				AiScore = aiScore
			};
			LinkedListNode<SsdBlockMeta> newNode = _lru.AddFirst(created);
			_map[key] = newNode;
			if (isDirty)
			{
				_dirtyCount++;
			}
			return true;
		}
	}

	public void RestoreEntries(IEnumerable<PersistedBlockEntry> entries)
	{
		if (!IsEnabled || _handle == null)
		{
			return;
		}

		long fileLength = RandomAccess.GetLength(_handle);
		lock (_sync)
		{
			_map.Clear();
			_lru.Clear();
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
				SsdBlockMeta meta = new SsdBlockMeta
				{
					Key = key,
					SlotIndex = item.SlotIndex,
					ValidLength = Math.Clamp(item.ValidLength, 0, _blockSize),
					IsDirty = item.IsDirty,
					BackingFileSize = item.BackingFileSize,
					BackingLastWriteUtcTicks = item.BackingLastWriteUtcTicks,
					LastAccessUtc = new DateTime(item.LastAccessUtcTicks, DateTimeKind.Utc),
					AiScore = item.AiScore
				};
				LinkedListNode<SsdBlockMeta> node = _lru.AddLast(meta);
				_map[key] = node;
				if (meta.IsDirty)
				{
					_dirtyCount++;
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
		}
	}

	public List<SsdBlockMeta> SnapshotEntries(string? filterRelativePath = null, bool dirtyOnly = false)
	{
		lock (_sync)
		{
			List<SsdBlockMeta> result = new List<SsdBlockMeta>();
			foreach (SsdBlockMeta e in _lru)
			{
				if (dirtyOnly && !e.IsDirty)
				{
					continue;
				}
				if (filterRelativePath != null &&
				    !string.Equals(e.Key.RelativePath, filterRelativePath, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				result.Add(CloneMeta(e));
			}
			return result;
		}
	}

	public void MarkClean(BlockKey key, long newBackingSize, long newBackingTicks)
	{
		lock (_sync)
		{
			if (_map.TryGetValue(key, out LinkedListNode<SsdBlockMeta>? node))
			{
				if (node.Value.IsDirty)
				{
					node.Value.IsDirty = false;
					_dirtyCount = Math.Max(0, _dirtyCount - 1);
				}
				node.Value.BackingFileSize = newBackingSize;
				node.Value.BackingLastWriteUtcTicks = newBackingTicks;
			}
		}
	}

	public void UpdateBackingMetadataForFile(string normalizedRelPath, long newBackingSize, long newBackingTicks)
	{
		lock (_sync)
		{
			foreach (SsdBlockMeta e in _lru)
			{
				if (!e.IsDirty && string.Equals(e.Key.RelativePath, normalizedRelPath, StringComparison.OrdinalIgnoreCase))
				{
					e.BackingFileSize = newBackingSize;
					e.BackingLastWriteUtcTicks = newBackingTicks;
				}
			}
		}
	}

	public void Remove(BlockKey key)
	{
		lock (_sync)
		{
			if (_map.TryGetValue(key, out LinkedListNode<SsdBlockMeta>? node))
			{
				RemoveNodeLocked(node);
			}
		}
	}

	public void RemoveByFile(string normalizedRelPath)
	{
		lock (_sync)
		{
			List<LinkedListNode<SsdBlockMeta>> toRemove = new List<LinkedListNode<SsdBlockMeta>>();
			for (LinkedListNode<SsdBlockMeta>? n = _lru.First; n != null; n = n.Next)
			{
				if (string.Equals(n.Value.Key.RelativePath, normalizedRelPath, StringComparison.OrdinalIgnoreCase))
				{
					toRemove.Add(n);
				}
			}
			foreach (LinkedListNode<SsdBlockMeta> n in toRemove)
			{
				RemoveNodeLocked(n);
			}
		}
	}

	public void TruncateFile(string normalizedRelPath, long newSize)
	{
		if (!IsEnabled || _handle == null)
		{
			return;
		}
		lock (_sync)
		{
			long lastValidBlock = newSize <= 0 ? -1 : (newSize - 1) / _blockSize;
			List<LinkedListNode<SsdBlockMeta>> toRemove = new List<LinkedListNode<SsdBlockMeta>>();
			for (LinkedListNode<SsdBlockMeta>? n = _lru.First; n != null; n = n.Next)
			{
				SsdBlockMeta e = n.Value;
				if (!string.Equals(e.Key.RelativePath, normalizedRelPath, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (e.Key.BlockIndex > lastValidBlock)
				{
					toRemove.Add(n);
				}
				else if (e.Key.BlockIndex == lastValidBlock)
				{
					int newValidLen = (int)(newSize - lastValidBlock * _blockSize);
					if (newValidLen < e.ValidLength)
					{
						e.ValidLength = newValidLen;
					}
				}
			}
			foreach (LinkedListNode<SsdBlockMeta> n in toRemove)
			{
				RemoveNodeLocked(n);
			}
		}
	}

	public void RenamePrefix(string oldRelPath, string newRelPath)
	{
		lock (_sync)
		{
			string oldPrefix = oldRelPath.TrimEnd('\\') + "\\";
			List<(LinkedListNode<SsdBlockMeta> Node, BlockKey NewKey)> renames = new List<(LinkedListNode<SsdBlockMeta>, BlockKey)>();
			for (LinkedListNode<SsdBlockMeta>? n = _lru.First; n != null; n = n.Next)
			{
				BlockKey k = n.Value.Key;
				if (string.Equals(k.RelativePath, oldRelPath, StringComparison.OrdinalIgnoreCase))
				{
					renames.Add((n, new BlockKey(newRelPath, k.BlockIndex)));
				}
				else if (k.RelativePath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
				{
					string suffix = k.RelativePath.Substring(oldPrefix.Length);
					renames.Add((n, new BlockKey(newRelPath.TrimEnd('\\') + "\\" + suffix, k.BlockIndex)));
				}
			}
			foreach ((LinkedListNode<SsdBlockMeta> node, BlockKey newKey) in renames)
			{
				_map.Remove(node.Value.Key);
				node.Value.Key = newKey;
				_map[newKey] = node;
			}
		}
	}

	public long GetTrackedMaxFileSize(string normalizedRelPath)
	{
		lock (_sync)
		{
			long maxEnd = 0;
			foreach (SsdBlockMeta e in _lru)
			{
				if (e.IsDirty && string.Equals(e.Key.RelativePath, normalizedRelPath, StringComparison.OrdinalIgnoreCase))
				{
					long end = e.Key.BlockIndex * _blockSize + e.ValidLength;
					if (end > maxEnd)
					{
						maxEnd = end;
					}
				}
			}
			return maxEnd;
		}
	}

	public (int CachedBlocks, long UsedBytes, int DirtyBlocks) GetCounts()
	{
		lock (_sync)
		{
			return (_map.Count, (long)_map.Count * _blockSize, _dirtyCount);
		}
	}

	public void FlushHandleToDisk()
	{
		if (!IsEnabled || _handle == null)
		{
			return;
		}
		lock (_sync)
		{
			RandomAccess.FlushToDisk(_handle);
		}
	}

	public void Dispose()
	{
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			_handle?.Dispose();
		}
	}

	private int ReadExactLocked(long offset, Span<byte> buffer)
	{
		if (_handle == null)
		{
			return 0;
		}
		int total = 0;
		while (total < buffer.Length)
		{
			int n = RandomAccess.Read(_handle, buffer.Slice(total), offset + total);
			if (n <= 0)
			{
				break;
			}
			total += n;
		}
		return total;
	}

	private void RemoveNodeLocked(LinkedListNode<SsdBlockMeta> node)
	{
		int slot = node.Value.SlotIndex;
		RemoveNodeWithoutFreeLocked(node);
		_freeSlots.Push(slot);
	}

	private void RemoveNodeWithoutFreeLocked(LinkedListNode<SsdBlockMeta> node)
	{
		if (node.Value.IsDirty)
		{
			_dirtyCount = Math.Max(0, _dirtyCount - 1);
		}
		_map.Remove(node.Value.Key);
		_lru.Remove(node);
	}

	private static SsdBlockMeta CloneMeta(SsdBlockMeta src)
	{
		return new SsdBlockMeta
		{
			Key = src.Key,
			SlotIndex = src.SlotIndex,
			ValidLength = src.ValidLength,
			IsDirty = src.IsDirty,
			BackingFileSize = src.BackingFileSize,
			BackingLastWriteUtcTicks = src.BackingLastWriteUtcTicks,
			LastAccessUtc = src.LastAccessUtc,
			AiScore = src.AiScore
		};
	}
}
