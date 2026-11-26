using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace HCX.Core;

/// <summary>
/// Tier-1 cache backed by a dedicated cache file on an existing RAMDisk drive (e.g. R:\.hcx\l1_cache_{id}.bin).
/// </summary>
public sealed class RamDiskFileCacheTier : IL1CacheTier
{
	private sealed class SlotMeta
	{
		public required BlockKey Key;
		public int SlotIndex;
		public int ValidLength;
		public bool IsDirty;
		public long BackingFileSize;
		public long BackingLastWriteUtcTicks;
		public DateTime LastAccessUtc;
		public double AiScore;
	}

	private readonly object _sync = new object();
	private readonly Dictionary<BlockKey, LinkedListNode<SlotMeta>> _map = new Dictionary<BlockKey, LinkedListNode<SlotMeta>>();
	private readonly LinkedList<SlotMeta> _lru = new LinkedList<SlotMeta>();
	private readonly Stack<int> _freeSlots = new Stack<int>();
	private readonly SafeFileHandle? _handle;
	private readonly int _blockSize;
	private readonly int _maxBlocks;
	private int _nextSlotToAllocate;
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

		lock (_sync)
		{
			if (!_map.TryGetValue(key, out LinkedListNode<SlotMeta>? node))
			{
				return false;
			}

			SlotMeta e = node.Value;
			if (!e.IsDirty && (e.BackingFileSize != currentBackingSize || e.BackingLastWriteUtcTicks != currentBackingTicks))
			{
				RemoveNodeLocked(node);
				invalidatedStale = true;
				return false;
			}

			int available = Math.Max(0, e.ValidLength - blockOffset);
			bytesCopied = Math.Min(destination.Length, available);
			if (bytesCopied > 0)
			{
				long fileOffset = (long)e.SlotIndex * _blockSize + blockOffset;
				int actual = ReadExactLocked(fileOffset, destination.Slice(0, bytesCopied));
				if (actual < bytesCopied)
				{
					RemoveNodeLocked(node);
					bytesCopied = 0;
					return false;
				}
			}

			_lru.Remove(node);
			_lru.AddFirst(node);
			e.LastAccessUtc = DateTime.UtcNow;
			isDirty = e.IsDirty;
			return true;
		}
	}

	public RamBlockCopy? Put(
		BlockKey key,
		ReadOnlySpan<byte> blockData,
		int validLength,
		bool isDirty,
		long backingSize,
		long backingTicks,
		double aiScore)
	{
		if (!IsEnabled || _handle == null)
		{
			return null;
		}

		byte[] padded = new byte[_blockSize];
		blockData.Slice(0, Math.Min(blockData.Length, _blockSize)).CopyTo(padded);

		lock (_sync)
		{
			if (_map.TryGetValue(key, out LinkedListNode<SlotMeta>? existing))
			{
				SlotMeta e = existing.Value;
				RandomAccess.Write(_handle, padded, (long)e.SlotIndex * _blockSize);
				e.ValidLength = validLength;
				if (!e.IsDirty && isDirty) _dirtyCount++;
				else if (e.IsDirty && !isDirty) _dirtyCount = Math.Max(0, _dirtyCount - 1);
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
			int slotIndex;
			if (_freeSlots.Count > 0)
			{
				slotIndex = _freeSlots.Pop();
			}
			else if (_nextSlotToAllocate < _maxBlocks)
			{
				slotIndex = _nextSlotToAllocate++;
			}
			else if (_lru.Last != null)
			{
				LinkedListNode<SlotMeta> tail = _lru.Last;
				SlotMeta tailMeta = tail.Value;
				slotIndex = tailMeta.SlotIndex;

				byte[] oldData = new byte[_blockSize];
				int read = ReadExactLocked((long)slotIndex * _blockSize, oldData);
				if (read >= tailMeta.ValidLength)
				{
					evicted = new RamBlockCopy
					{
						Key = tailMeta.Key,
						Data = oldData,
						ValidLength = tailMeta.ValidLength,
						IsDirty = tailMeta.IsDirty,
						BackingFileSize = tailMeta.BackingFileSize,
						BackingLastWriteUtcTicks = tailMeta.BackingLastWriteUtcTicks,
						LastAccessUtc = tailMeta.LastAccessUtc,
						AiScore = tailMeta.AiScore
					};
				}
				RemoveNodeWithoutFreeLocked(tail);
			}
			else
			{
				return null;
			}

			RandomAccess.Write(_handle, padded, (long)slotIndex * _blockSize);
			SlotMeta created = new SlotMeta
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
			LinkedListNode<SlotMeta> newNode = _lru.AddFirst(created);
			_map[key] = newNode;
			if (isDirty) _dirtyCount++;
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
		if (!IsEnabled || _handle == null)
		{
			return false;
		}

		lock (_sync)
		{
			if (!_map.TryGetValue(key, out LinkedListNode<SlotMeta>? node))
			{
				return false;
			}
			SlotMeta e = node.Value;
			if (!e.IsDirty && (e.BackingFileSize != backingSize || e.BackingLastWriteUtcTicks != backingTicks))
			{
				RemoveNodeLocked(node);
				return false;
			}

			RandomAccess.Write(_handle, patchData, (long)e.SlotIndex * _blockSize + blockOffset);
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
		if (!IsEnabled || _handle == null)
		{
			return new List<RamBlockCopy>();
		}

		lock (_sync)
		{
			List<RamBlockCopy> result = new List<RamBlockCopy>();
			foreach (SlotMeta e in _lru)
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
				int read = ReadExactLocked((long)e.SlotIndex * _blockSize, dataCopy);
				if (read < e.ValidLength)
				{
					continue;
				}
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
			if (_map.TryGetValue(key, out LinkedListNode<SlotMeta>? node))
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
			foreach (SlotMeta e in _lru)
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
			if (_map.TryGetValue(key, out LinkedListNode<SlotMeta>? node))
			{
				RemoveNodeLocked(node);
			}
		}
	}

	public void RemoveByFile(string normalizedRelPath)
	{
		lock (_sync)
		{
			List<LinkedListNode<SlotMeta>> toRemove = new List<LinkedListNode<SlotMeta>>();
			for (LinkedListNode<SlotMeta>? n = _lru.First; n != null; n = n.Next)
			{
				if (string.Equals(n.Value.Key.RelativePath, normalizedRelPath, StringComparison.OrdinalIgnoreCase))
				{
					toRemove.Add(n);
				}
			}
			foreach (LinkedListNode<SlotMeta> n in toRemove)
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
			List<LinkedListNode<SlotMeta>> toRemove = new List<LinkedListNode<SlotMeta>>();
			for (LinkedListNode<SlotMeta>? n = _lru.First; n != null; n = n.Next)
			{
				SlotMeta e = n.Value;
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
			foreach (LinkedListNode<SlotMeta> n in toRemove)
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
			List<(LinkedListNode<SlotMeta> Node, BlockKey NewKey)> renames = new List<(LinkedListNode<SlotMeta>, BlockKey)>();
			for (LinkedListNode<SlotMeta>? n = _lru.First; n != null; n = n.Next)
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
			foreach ((LinkedListNode<SlotMeta> node, BlockKey newKey) in renames)
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
			foreach (SlotMeta e in _lru)
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
			_freeSlots.Clear();
			_nextSlotToAllocate = 0;
			_dirtyCount = 0;
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
			Clear();
			_handle?.Dispose();
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

	private void RemoveNodeLocked(LinkedListNode<SlotMeta> node)
	{
		int slot = node.Value.SlotIndex;
		RemoveNodeWithoutFreeLocked(node);
		_freeSlots.Push(slot);
	}

	private void RemoveNodeWithoutFreeLocked(LinkedListNode<SlotMeta> node)
	{
		if (node.Value.IsDirty)
		{
			_dirtyCount = Math.Max(0, _dirtyCount - 1);
		}
		_map.Remove(node.Value.Key);
		_lru.Remove(node);
	}
}
