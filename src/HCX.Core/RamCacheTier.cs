using System;
using System.Collections.Generic;

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
/// Thread-safe Tier-1 in-memory 64 KB block cache with strict capacity and LRU eviction.
/// </summary>
public sealed class RamCacheTier : IL1CacheTier
{
	private sealed class Entry
	{
		public required BlockKey Key;
		public required byte[] Data;
		public int ValidLength;
		public bool IsDirty;
		public long BackingFileSize;
		public long BackingLastWriteUtcTicks;
		public DateTime LastAccessUtc;
		public double AiScore;
	}

	private readonly object _sync = new object();
	private readonly Dictionary<BlockKey, LinkedListNode<Entry>> _map = new Dictionary<BlockKey, LinkedListNode<Entry>>();
	private readonly LinkedList<Entry> _lru = new LinkedList<Entry>();
	private readonly int _blockSize;
	private readonly int _maxBlocks;
	private int _dirtyCount;

	public int BlockSize => _blockSize;
	public int MaxBlocks => _maxBlocks;
	public bool IsEnabled => _maxBlocks > 0;
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

		lock (_sync)
		{
			if (!_map.TryGetValue(key, out LinkedListNode<Entry>? node))
			{
				return false;
			}

			Entry e = node.Value;
			if (!e.IsDirty && (e.BackingFileSize != currentBackingSize || e.BackingLastWriteUtcTicks != currentBackingTicks))
			{
				RemoveNodeLocked(node);
				invalidatedStale = true;
				return false;
			}

			_lru.Remove(node);
			_lru.AddFirst(node);
			e.LastAccessUtc = DateTime.UtcNow;
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

	/// <summary>
	/// Inserts or updates a block in L1 RAM. Returns an evicted entry if capacity was exceeded.
	/// </summary>
	public RamBlockCopy? Put(
		BlockKey key,
		ReadOnlySpan<byte> blockData,
		int validLength,
		bool isDirty,
		long backingSize,
		long backingTicks,
		double aiScore)
	{
		if (!IsEnabled)
		{
			return null;
		}

		lock (_sync)
		{
			if (_map.TryGetValue(key, out LinkedListNode<Entry>? existing))
			{
				Entry e = existing.Value;
				blockData.Slice(0, Math.Min(blockData.Length, _blockSize)).CopyTo(e.Data);
				if (validLength < _blockSize)
				{
					Array.Clear(e.Data, validLength, _blockSize - validLength);
				}
				e.ValidLength = validLength;
				if (!e.IsDirty && isDirty)
				{
					_dirtyCount++;
				}
				else if (e.IsDirty && !isDirty)
				{
					_dirtyCount = Math.Max(0, _dirtyCount - 1);
				}
				e.IsDirty = isDirty;
				e.BackingFileSize = backingSize;
				e.BackingLastWriteUtcTicks = backingTicks;
				e.LastAccessUtc = DateTime.UtcNow;
				e.AiScore = aiScore;
				_lru.Remove(existing);
				_lru.AddFirst(existing);
				return null;
			}

			RamBlockCopy? evicted = null;
			if (_map.Count >= _maxBlocks && _lru.Last != null)
			{
				LinkedListNode<Entry> tail = _lru.Last;
				Entry tailEntry = tail.Value;
				byte[] copy = new byte[_blockSize];
				Buffer.BlockCopy(tailEntry.Data, 0, copy, 0, _blockSize);
				evicted = new RamBlockCopy
				{
					Key = tailEntry.Key,
					Data = copy,
					ValidLength = tailEntry.ValidLength,
					IsDirty = tailEntry.IsDirty,
					BackingFileSize = tailEntry.BackingFileSize,
					BackingLastWriteUtcTicks = tailEntry.BackingLastWriteUtcTicks,
					LastAccessUtc = tailEntry.LastAccessUtc,
					AiScore = tailEntry.AiScore
				};
				RemoveNodeLocked(tail);
			}

			byte[] buffer = new byte[_blockSize];
			blockData.Slice(0, Math.Min(blockData.Length, _blockSize)).CopyTo(buffer);
			Entry created = new Entry
			{
				Key = key,
				Data = buffer,
				ValidLength = validLength,
				IsDirty = isDirty,
				BackingFileSize = backingSize,
				BackingLastWriteUtcTicks = backingTicks,
				LastAccessUtc = DateTime.UtcNow,
				AiScore = aiScore
			};
			LinkedListNode<Entry> newNode = _lru.AddFirst(created);
			_map[key] = newNode;
			if (isDirty)
			{
				_dirtyCount++;
			}
			return evicted;
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
		lock (_sync)
		{
			if (!_map.TryGetValue(key, out LinkedListNode<Entry>? node))
			{
				return false;
			}
			Entry e = node.Value;
			if (!e.IsDirty && (e.BackingFileSize != backingSize || e.BackingLastWriteUtcTicks != backingTicks))
			{
				RemoveNodeLocked(node);
				return false;
			}
			patchData.CopyTo(e.Data.AsSpan(blockOffset));
			int end = blockOffset + patchData.Length;
			if (end > e.ValidLength)
			{
				e.ValidLength = end;
			}
			if (markDirty && !e.IsDirty)
			{
				e.IsDirty = true;
				_dirtyCount++;
			}
			if (!markDirty)
			{
				e.BackingFileSize = backingSize;
				e.BackingLastWriteUtcTicks = backingTicks;
			}
			e.LastAccessUtc = DateTime.UtcNow;
			e.AiScore = aiScore;
			_lru.Remove(node);
			_lru.AddFirst(node);
			return true;
		}
	}

	public List<RamBlockCopy> SnapshotEntries(string? filterRelativePath = null, bool dirtyOnly = false)
	{
		lock (_sync)
		{
			List<RamBlockCopy> result = new List<RamBlockCopy>();
			foreach (Entry e in _lru)
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
				byte[] dataCopy = new byte[_blockSize];
				Buffer.BlockCopy(e.Data, 0, dataCopy, 0, _blockSize);
				result.Add(new RamBlockCopy
				{
					Key = e.Key,
					Data = dataCopy,
					ValidLength = e.ValidLength,
					IsDirty = e.IsDirty,
					BackingFileSize = e.BackingFileSize,
					BackingLastWriteUtcTicks = e.BackingLastWriteUtcTicks,
					LastAccessUtc = e.LastAccessUtc,
					AiScore = e.AiScore
				});
			}
			return result;
		}
	}

	public void MarkClean(BlockKey key, long newBackingSize, long newBackingTicks)
	{
		lock (_sync)
		{
			if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
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
			foreach (Entry e in _lru)
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
			if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
			{
				RemoveNodeLocked(node);
			}
		}
	}

	public void RemoveByFile(string normalizedRelPath)
	{
		lock (_sync)
		{
			List<LinkedListNode<Entry>> toRemove = new List<LinkedListNode<Entry>>();
			for (LinkedListNode<Entry>? n = _lru.First; n != null; n = n.Next)
			{
				if (string.Equals(n.Value.Key.RelativePath, normalizedRelPath, StringComparison.OrdinalIgnoreCase))
				{
					toRemove.Add(n);
				}
			}
			foreach (LinkedListNode<Entry> n in toRemove)
			{
				RemoveNodeLocked(n);
			}
		}
	}

	public void TruncateFile(string normalizedRelPath, long newSize)
	{
		lock (_sync)
		{
			long lastValidBlock = newSize <= 0 ? -1 : (newSize - 1) / _blockSize;
			List<LinkedListNode<Entry>> toRemove = new List<LinkedListNode<Entry>>();
			for (LinkedListNode<Entry>? n = _lru.First; n != null; n = n.Next)
			{
				Entry e = n.Value;
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
						Array.Clear(e.Data, newValidLen, e.ValidLength - newValidLen);
						e.ValidLength = newValidLen;
					}
				}
			}
			foreach (LinkedListNode<Entry> n in toRemove)
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
			List<(LinkedListNode<Entry> Node, BlockKey NewKey)> renames = new List<(LinkedListNode<Entry>, BlockKey)>();
			for (LinkedListNode<Entry>? n = _lru.First; n != null; n = n.Next)
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
			foreach ((LinkedListNode<Entry> node, BlockKey newKey) in renames)
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
			foreach (Entry e in _lru)
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

	public void Clear()
	{
		lock (_sync)
		{
			_map.Clear();
			_lru.Clear();
			_dirtyCount = 0;
		}
	}

	public void Dispose()
	{
		Clear();
	}

	private void RemoveNodeLocked(LinkedListNode<Entry> node)
	{
		if (node.Value.IsDirty)
		{
			_dirtyCount = Math.Max(0, _dirtyCount - 1);
		}
		_map.Remove(node.Value.Key);
		_lru.Remove(node);
	}
}
