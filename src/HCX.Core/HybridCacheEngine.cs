using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace HCX.Core;

/// <summary>
/// 3-Tier Hybrid Storage Acceleration Engine:
/// Tier 1 (L1 RAM) -> Tier 2 (L2 SSD Cache File) -> Tier 3 (Backing HDD).
/// </summary>
public sealed class HybridCacheEngine : IDisposable
{
	private readonly struct CachedBackingStat
	{
		public bool Exists { get; init; }
		public long Size { get; init; }
		public long LastWriteUtcTicks { get; init; }
		public long CachedAtTimestamp { get; init; }
	}

	private sealed class InFlightEvictionEntry
	{
		public required byte[] Data { get; init; }
		public int ValidLength { get; set; }
		public long BackingFileSize { get; set; }
		public long BackingLastWriteUtcTicks { get; set; }
		public double AiScore { get; set; }
		public bool IsDirty { get; set; }
		public bool TrySsdDemote { get; set; }
	}

	private const int MaxCoalescedFlushBlocks = 16; // 1 MB per coalesced write
	private const int FileLockStripeCount = 512;
	private const int MaxStatCacheEntries = 8192;

	private readonly CacheOptions _options;
	private readonly string _targetRootFull;
	private readonly string _instanceId;
	private readonly int _blockSize;
	private readonly AiAccessScorer _scorer;
	private readonly IL1CacheTier _ram;
	private readonly SsdFileCacheTier _ssd;
	private readonly MetadataManager _metadata;
	private readonly ReaderWriterLockSlim[] _fileLockStripes = new ReaderWriterLockSlim[FileLockStripeCount];
	private readonly ConcurrentDictionary<string, CachedBackingStat> _statCache = new ConcurrentDictionary<string, CachedBackingStat>(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<BlockKey, InFlightEvictionEntry> _inFlightEvictions = new ConcurrentDictionary<BlockKey, InFlightEvictionEntry>();
	private readonly long _statTtlStopwatchTicks;

	private CancellationTokenSource? _cts;
	private Task? _flushTask;
	private Task? _autoSaveTask;
	private bool _isRunning;
	private bool _disposed;

	private long _totalReadBytes;
	private long _totalWriteBytes;
	private long _ramHits;
	private long _ssdHits;
	private long _hddMisses;

	public CacheOptions Options => _options;
	public string InstanceId => _instanceId;
	public string TargetRootPath => _targetRootFull;
	public string SsdCacheFilePath => _ssd.CacheFilePath;
	public string MetadataFilePath => _metadata.MetadataPath;
	public bool IsRunning => _isRunning;

	public event Action<string>? OnLog;

	public HybridCacheEngine(CacheOptions options)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_options.Validate();

		for (int i = 0; i < FileLockStripeCount; i++)
		{
			_fileLockStripes[i] = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
		}

		_targetRootFull = Path.GetFullPath(_options.TargetRootPath);
		_instanceId = _options.ResolveInstanceId();
		_blockSize = _options.BlockSizeBytes;

		string ssdDir = string.IsNullOrWhiteSpace(_options.SsdCacheDirectory)
			? string.Empty
			: Path.GetFullPath(_options.SsdCacheDirectory);

		_scorer = new AiAccessScorer(
			_options.SequentialThreshold,
			_options.SsdPromoteScore,
			_options.RamPromoteScore);

		_ram = CreateL1Tier(_options, _instanceId, _blockSize, msg => OnLog?.Invoke(msg));
		_ssd = new SsdFileCacheTier(ssdDir, _instanceId, _options.SsdCacheSizeBytes, _blockSize);

		string metaDir = !string.IsNullOrEmpty(ssdDir)
			? ssdDir
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperCacheX");
		_metadata = new MetadataManager(Path.Combine(metaDir, $"cache_metadata_{_instanceId}.dat"));
		_statTtlStopwatchTicks = _options.BackingStatCacheTtl <= TimeSpan.Zero
			? 0L
			: (long)(_options.BackingStatCacheTtl.TotalSeconds * Stopwatch.Frequency);
	}

	public bool Start()
	{
		if (_isRunning)
		{
			return true;
		}

		try
		{
			if (_ssd.IsEnabled)
			{
				(bool loaded, bool cleanShutdown, List<PersistedBlockEntry> entries) = _metadata.LoadMetadata(_blockSize);
				if (loaded && entries.Count > 0)
				{
					_ssd.RestoreEntries(entries);
					foreach (PersistedBlockEntry e in entries)
					{
						if (e.IsValid)
						{
							_scorer.UpdateTierState(new BlockKey(e.RelativePath, e.BlockIndex), CacheTier.SsdL2, e.IsDirty, e.AiScore);
						}
					}
					Log($"メタデータ復元完了: {entries.Count} ブロック (CleanShutdown={cleanShutdown})");
					if (!cleanShutdown)
					{
						int recoveredDirty = SyncAll();
						if (recoveredDirty > 0)
						{
							Log($"前回異常終了分の Dirty ブロック {recoveredDirty} 件を HDD へ同期しました。");
						}
					}
				}
			}

			_cts = new CancellationTokenSource();
			_isRunning = true;
			_flushTask = Task.Run(() => FlushLoopAsync(_cts.Token));
			_autoSaveTask = Task.Run(() => AutoSaveLoopAsync(_cts.Token));

			Log($"エンジン起動: Target={_targetRootFull} | L1={_ram.BackendDescription} ({_options.RamCacheSizeBytes / (1024 * 1024)}MB) | L2 SSD={_options.SsdCacheSizeBytes / (1024 * 1024)}MB | Policy={_options.WritePolicy}");
			return true;
		}
		catch (Exception ex)
		{
			Log($"エンジン起動エラー: {ex.Message}");
			return false;
		}
	}

	public void InvalidateBackingStatCache(string relativePath)
	{
		string rel = BlockKey.NormalizeRelativePath(relativePath);
		_statCache.TryRemove(rel, out _);
	}

	public int ReadBlockRange(string relativePath, long offset, Span<byte> destination, FileStream? openBackingStream = null)
	{
		if (offset < 0 || destination.IsEmpty)
		{
			return 0;
		}

		string rel = BlockKey.NormalizeRelativePath(relativePath);
		string fullPath = ResolveBackingPath(rel);
		ReaderWriterLockSlim fileLock = GetFileLock(rel);
		List<(BlockKey Key, InFlightEvictionEntry Entry)>? deferredFlushes = null;

		fileLock.EnterReadLock();
		byte[]? rentedBlockBuf = null;
		try
		{
			(bool exists, long backingSize, long backingTicks) = GetBackingInfoFast(rel, fullPath, openBackingStream);
			long effectiveSize = Math.Max(backingSize, Math.Max(_ram.GetTrackedMaxFileSize(rel), Math.Max(_ssd.GetTrackedMaxFileSize(rel), GetInFlightMaxFileSize(rel))));
			if ((!exists && effectiveSize == 0) || offset >= effectiveSize)
			{
				return 0;
			}

			int toRead = (int)Math.Min(destination.Length, effectiveSize - offset);
			int bytesReadTotal = 0;
			DateTime nowUtc = DateTime.UtcNow;

			while (bytesReadTotal < toRead)
			{
				long pos = offset + bytesReadTotal;
				long blockIndex = pos / _blockSize;
				int blockOffset = (int)(pos % _blockSize);
				int chunk = Math.Min(toRead - bytesReadTotal, _blockSize - blockOffset);
				BlockKey key = new BlockKey(rel, blockIndex);
				Span<byte> destSlice = destination.Slice(bytesReadTotal, chunk);

				if (_ram.TryReadSlice(key, blockOffset, destSlice, backingSize, backingTicks, out int ramCopied, out bool ramDirty, out bool ramStale))
				{
					if (ramCopied < chunk)
					{
						destSlice.Slice(ramCopied).Clear();
					}
					Interlocked.Increment(ref _ramHits);
					_scorer.RecordAccess(key, isWrite: false, nowUtc, CacheTier.RamL1, ramDirty);
					bytesReadTotal += chunk;
					continue;
				}
				if (ramStale)
				{
					_scorer.UpdateTierState(key, CacheTier.None, false);
				}

				ScoreDecision decision = _scorer.RecordAccess(key, isWrite: false, nowUtc);
				rentedBlockBuf ??= ArrayPool<byte>.Shared.Rent(_blockSize);
				Array.Clear(rentedBlockBuf, 0, _blockSize);

				if (_ssd.TryReadFullBlock(key, rentedBlockBuf, backingSize, backingTicks, out SsdBlockMeta? ssdMeta, out bool ssdStale) && ssdMeta != null)
				{
					rentedBlockBuf.AsSpan(blockOffset, chunk).CopyTo(destSlice);
					Interlocked.Increment(ref _ssdHits);

					// Keep dirty ownership single-place: only promote clean L2 blocks into L1 RAM.
					if (!ssdMeta.IsDirty && _ram.IsEnabled && decision.ShouldPromoteToRam)
					{
						PutInRamWithDemotion(key, rentedBlockBuf, ssdMeta.ValidLength, isDirty: false, backingSize, backingTicks, decision.Score, ref deferredFlushes);
						_scorer.UpdateTierState(key, CacheTier.RamL1, false, decision.Score);
					}
					else
					{
						_scorer.UpdateTierState(key, CacheTier.SsdL2, ssdMeta.IsDirty, decision.Score);
					}

					bytesReadTotal += chunk;
					continue;
				}
				if (ssdStale)
				{
					_scorer.UpdateTierState(key, CacheTier.None, false);
				}

				if (!_inFlightEvictions.IsEmpty && _inFlightEvictions.TryGetValue(key, out InFlightEvictionEntry? inflight))
				{
					int available = Math.Max(0, inflight.ValidLength - blockOffset);
					int copyCount = Math.Min(chunk, available);
					if (copyCount > 0)
					{
						inflight.Data.AsSpan(blockOffset, copyCount).CopyTo(destSlice);
					}
					if (copyCount < chunk)
					{
						destSlice.Slice(copyCount).Clear();
					}
					Interlocked.Increment(ref _ramHits);
					bytesReadTotal += chunk;
					continue;
				}

				// Tier 3: Backing HDD read
				Array.Clear(rentedBlockBuf, 0, _blockSize);
				int validLen = 0;
				if (exists && blockIndex * _blockSize < backingSize)
				{
					validLen = ReadBackingBlockLocked(fullPath, openBackingStream, blockIndex * _blockSize, rentedBlockBuf.AsSpan(0, _blockSize));
				}
				rentedBlockBuf.AsSpan(blockOffset, chunk).CopyTo(destSlice);
				Interlocked.Increment(ref _hddMisses);

				if (!decision.IsSequentialBypass && validLen > 0)
				{
					if (_ram.IsEnabled && decision.ShouldPromoteToRam)
					{
						PutInRamWithDemotion(key, rentedBlockBuf, validLen, isDirty: false, backingSize, backingTicks, decision.Score, ref deferredFlushes);
					}
					else if (_ssd.IsEnabled && decision.ShouldPromoteToSsd)
					{
						PutInSsdWithDeferredFlush(key, rentedBlockBuf, validLen, isDirty: false, backingSize, backingTicks, decision.Score, ref deferredFlushes);
					}
				}

				bytesReadTotal += chunk;
			}

			Interlocked.Add(ref _totalReadBytes, bytesReadTotal);
			return bytesReadTotal;
		}
		finally
		{
			if (rentedBlockBuf != null)
			{
				ArrayPool<byte>.Shared.Return(rentedBlockBuf);
			}
			fileLock.ExitReadLock();
			DrainDeferredEvictions(deferredFlushes);
		}
	}

	public int WriteBlockRange(string relativePath, long offset, ReadOnlySpan<byte> source, FileStream? openBackingStream = null)
	{
		if (offset < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(offset));
		}
		if (source.IsEmpty)
		{
			return 0;
		}

		string rel = BlockKey.NormalizeRelativePath(relativePath);
		string fullPath = ResolveBackingPath(rel);
		ReaderWriterLockSlim fileLock = GetFileLock(rel);
		List<(BlockKey Key, InFlightEvictionEntry Entry)>? deferredFlushes = null;

		fileLock.EnterWriteLock();
		byte[]? rentedWorkBuf = null;
		try
		{
			Interlocked.Add(ref _totalWriteBytes, source.Length);
			DateTime nowUtc = DateTime.UtcNow;

			bool useWriteBack = _options.WritePolicy == WritePolicy.WriteBack && (_ram.IsEnabled || _ssd.IsEnabled);
			if (!useWriteBack)
			{
				if (!_inFlightEvictions.IsEmpty)
				{
					FlushInFlightForFileLocked(rel, fullPath, openBackingStream);
				}

				WriteBackingRangeLocked(fullPath, openBackingStream, offset, source);
				(bool _, long newSize, long newTicks) = RefreshBackingInfoLocked(rel, fullPath, openBackingStream);
				_ram.UpdateBackingMetadataForFile(rel, newSize, newTicks);
				_ssd.UpdateBackingMetadataForFile(rel, newSize, newTicks);

				int written = 0;
				while (written < source.Length)
				{
					long pos = offset + written;
					long blockIndex = pos / _blockSize;
					int blockOffset = (int)(pos % _blockSize);
					int chunk = Math.Min(source.Length - written, _blockSize - blockOffset);
					BlockKey key = new BlockKey(rel, blockIndex);
					ScoreDecision decision = _scorer.RecordAccess(key, isWrite: true, nowUtc);
					ReadOnlySpan<byte> patch = source.Slice(written, chunk);

					bool inRam = _ram.TryPatchOrGetForWrite(key, blockOffset, patch, newSize, newTicks, decision.Score, markDirty: false);
					if (inRam)
					{
						_ssd.Remove(key);
						_scorer.UpdateTierState(key, CacheTier.RamL1, false, decision.Score);
					}
					else
					{
						_ssd.Remove(key);
						if (!decision.IsSequentialBypass && (decision.ShouldPromoteToRam || decision.ShouldPromoteToSsd))
						{
							rentedWorkBuf ??= ArrayPool<byte>.Shared.Rent(_blockSize);
							Array.Clear(rentedWorkBuf, 0, _blockSize);
							int validLen = ReadBackingBlockLocked(fullPath, openBackingStream, blockIndex * _blockSize, rentedWorkBuf.AsSpan(0, _blockSize));
							if (validLen > 0)
							{
								if (_ram.IsEnabled && decision.ShouldPromoteToRam)
								{
									PutInRamWithDemotion(key, rentedWorkBuf, validLen, isDirty: false, newSize, newTicks, decision.Score, ref deferredFlushes);
								}
								else if (_ssd.IsEnabled && decision.ShouldPromoteToSsd)
								{
									PutInSsdWithDeferredFlush(key, rentedWorkBuf, validLen, isDirty: false, newSize, newTicks, decision.Score, ref deferredFlushes);
								}
							}
						}
						else
						{
							_scorer.UpdateTierState(key, CacheTier.None, false);
						}
					}
					written += chunk;
				}
				return source.Length;
			}

			// Write-Back mode
			(bool exists, long backingSize, long backingTicks) = GetBackingInfoFast(rel, fullPath, openBackingStream);
			int totalWritten = 0;

			while (totalWritten < source.Length)
			{
				long pos = offset + totalWritten;
				long blockIndex = pos / _blockSize;
				int blockOffset = (int)(pos % _blockSize);
				int chunk = Math.Min(source.Length - totalWritten, _blockSize - blockOffset);
				BlockKey key = new BlockKey(rel, blockIndex);
				ScoreDecision decision = _scorer.RecordAccess(key, isWrite: true, nowUtc);
				ReadOnlySpan<byte> patch = source.Slice(totalWritten, chunk);

				if (_ram.TryPatchOrGetForWrite(key, blockOffset, patch, backingSize, backingTicks, decision.Score, markDirty: true))
				{
					_ssd.Remove(key);
					if (!_inFlightEvictions.IsEmpty)
					{
						_inFlightEvictions.TryRemove(key, out _);
					}
					_scorer.UpdateTierState(key, CacheTier.RamL1, isDirty: true, decision.Score);
					totalWritten += chunk;
					continue;
				}

				rentedWorkBuf ??= ArrayPool<byte>.Shared.Rent(_blockSize);
				Array.Clear(rentedWorkBuf, 0, _blockSize);
				int existingValidLen = 0;
				bool isPartial = blockOffset > 0 || chunk < _blockSize;
				InFlightEvictionEntry? staleInflight = null;
				if (!_inFlightEvictions.IsEmpty)
				{
					_inFlightEvictions.TryRemove(key, out staleInflight);
				}

				if (isPartial)
				{
					if (_ssd.TryReadFullBlock(key, rentedWorkBuf, backingSize, backingTicks, out SsdBlockMeta? ssdMeta, out _) && ssdMeta != null)
					{
						existingValidLen = ssdMeta.ValidLength;
					}
					else if (staleInflight != null)
					{
						Buffer.BlockCopy(staleInflight.Data, 0, rentedWorkBuf, 0, Math.Min(_blockSize, staleInflight.Data.Length));
						existingValidLen = staleInflight.ValidLength;
					}
					else if (exists && blockIndex * _blockSize < backingSize)
					{
						existingValidLen = ReadBackingBlockLocked(fullPath, openBackingStream, blockIndex * _blockSize, rentedWorkBuf.AsSpan(0, _blockSize));
					}
				}

				patch.CopyTo(rentedWorkBuf.AsSpan(blockOffset, chunk));
				int newValidLen = Math.Max(existingValidLen, blockOffset + chunk);

				if (_ram.IsEnabled)
				{
					_ssd.Remove(key);
					PutInRamWithDemotion(key, rentedWorkBuf, newValidLen, isDirty: true, backingSize, backingTicks, decision.Score, ref deferredFlushes);
				}
				else if (_ssd.IsEnabled)
				{
					if (!PutInSsdWithDeferredFlush(key, rentedWorkBuf, newValidLen, isDirty: true, backingSize, backingTicks, decision.Score, ref deferredFlushes))
					{
						WriteBackingRangeLocked(fullPath, openBackingStream, pos, patch);
						_statCache.TryRemove(rel, out _);
					}
				}
				else
				{
					WriteBackingRangeLocked(fullPath, openBackingStream, pos, patch);
					_statCache.TryRemove(rel, out _);
				}

				totalWritten += chunk;
			}

			return totalWritten;
		}
		finally
		{
			if (rentedWorkBuf != null)
			{
				ArrayPool<byte>.Shared.Return(rentedWorkBuf);
			}
			fileLock.ExitWriteLock();
			DrainDeferredEvictions(deferredFlushes);
		}
	}

	public long GetEffectiveFileSize(string relativePath, FileStream? openBackingStream = null)
	{
		string rel = BlockKey.NormalizeRelativePath(relativePath);
		string fullPath = ResolveBackingPath(rel);
		ReaderWriterLockSlim fileLock = GetFileLock(rel);
		fileLock.EnterReadLock();
		try
		{
			(_, long backingSize, _) = GetBackingInfoFast(rel, fullPath, openBackingStream);
			return Math.Max(backingSize, Math.Max(_ram.GetTrackedMaxFileSize(rel), Math.Max(_ssd.GetTrackedMaxFileSize(rel), GetInFlightMaxFileSize(rel))));
		}
		finally
		{
			fileLock.ExitReadLock();
		}
	}

	public int FlushFile(string relativePath, FileStream? openBackingStream = null)
	{
		string rel = BlockKey.NormalizeRelativePath(relativePath);
		string fullPath = ResolveBackingPath(rel);
		ReaderWriterLockSlim fileLock = GetFileLock(rel);
		fileLock.EnterWriteLock();
		try
		{
			return FlushFileLocked(rel, fullPath, openBackingStream);
		}
		finally
		{
			fileLock.ExitWriteLock();
		}
	}

	public int FlushPrefix(string relativePathPrefix)
	{
		string prefix = BlockKey.NormalizeRelativePath(relativePathPrefix);
		string subPrefix = string.IsNullOrEmpty(prefix) ? string.Empty : prefix.TrimEnd('\\') + "\\";

		HashSet<string> matchingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (RamBlockCopy item in _ram.SnapshotEntries(dirtyOnly: true))
		{
			if (MatchesPrefix(item.Key.RelativePath, prefix, subPrefix))
			{
				matchingFiles.Add(item.Key.RelativePath);
			}
		}
		foreach (SsdBlockMeta item in _ssd.SnapshotEntries(dirtyOnly: true))
		{
			if (MatchesPrefix(item.Key.RelativePath, prefix, subPrefix))
			{
				matchingFiles.Add(item.Key.RelativePath);
			}
		}
		if (!_inFlightEvictions.IsEmpty)
		{
			foreach (BlockKey k in _inFlightEvictions.Keys)
			{
				if (MatchesPrefix(k.RelativePath, prefix, subPrefix))
				{
					matchingFiles.Add(k.RelativePath);
				}
			}
		}

		int totalFlushed = 0;
		foreach (string rel in matchingFiles)
		{
			string fullPath = ResolveBackingPath(rel);
			ReaderWriterLockSlim fileLock = GetFileLock(rel);
			fileLock.EnterWriteLock();
			try
			{
				totalFlushed += FlushFileLocked(rel, fullPath, null);
			}
			finally
			{
				fileLock.ExitWriteLock();
			}
		}
		return totalFlushed;
	}

	public void InvalidateFile(string relativePath)
	{
		string rel = BlockKey.NormalizeRelativePath(relativePath);
		ReaderWriterLockSlim fileLock = GetFileLock(rel);
		fileLock.EnterWriteLock();
		try
		{
			_ram.RemoveByFile(rel);
			_ssd.RemoveByFile(rel);
			_scorer.RemoveByFile(rel);
			_statCache.TryRemove(rel, out _);
			if (!_inFlightEvictions.IsEmpty)
			{
				foreach (BlockKey k in _inFlightEvictions.Keys)
				{
					if (string.Equals(k.RelativePath, rel, StringComparison.OrdinalIgnoreCase))
					{
						_inFlightEvictions.TryRemove(k, out _);
					}
				}
			}
		}
		finally
		{
			fileLock.ExitWriteLock();
		}
	}

	public void TruncateFile(string relativePath, long newSize)
	{
		string rel = BlockKey.NormalizeRelativePath(relativePath);
		string fullPath = ResolveBackingPath(rel);
		ReaderWriterLockSlim fileLock = GetFileLock(rel);
		fileLock.EnterWriteLock();
		try
		{
			_ram.TruncateFile(rel, newSize);
			_ssd.TruncateFile(rel, newSize);
			if (!_inFlightEvictions.IsEmpty)
			{
				long lastValidBlock = newSize <= 0 ? -1 : (newSize - 1) / _blockSize;
				foreach (KeyValuePair<BlockKey, InFlightEvictionEntry> kv in _inFlightEvictions)
				{
					if (!string.Equals(kv.Key.RelativePath, rel, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					if (kv.Key.BlockIndex > lastValidBlock)
					{
						_inFlightEvictions.TryRemove(kv.Key, out _);
					}
					else if (kv.Key.BlockIndex == lastValidBlock)
					{
						int newValidLen = (int)(newSize - lastValidBlock * _blockSize);
						if (newValidLen < kv.Value.ValidLength)
						{
							kv.Value.ValidLength = newValidLen;
						}
					}
				}
			}
			(bool _, long backingSize, long backingTicks) = RefreshBackingInfoLocked(rel, fullPath, null);
			_ram.UpdateBackingMetadataForFile(rel, backingSize, backingTicks);
			_ssd.UpdateBackingMetadataForFile(rel, backingSize, backingTicks);
		}
		finally
		{
			fileLock.ExitWriteLock();
		}
	}

	public void NotifyRename(string oldRelativePath, string newRelativePath)
	{
		string oldRel = BlockKey.NormalizeRelativePath(oldRelativePath);
		string newRel = BlockKey.NormalizeRelativePath(newRelativePath);
		int oldStripe = GetFileLockIndex(oldRel);
		int newStripe = GetFileLockIndex(newRel);

		if (oldStripe == newStripe)
		{
			ReaderWriterLockSlim singleLock = _fileLockStripes[oldStripe];
			singleLock.EnterWriteLock();
			try
			{
				ApplyRenameLocked(oldRel, newRel);
			}
			finally
			{
				singleLock.ExitWriteLock();
			}
			return;
		}

		int first = Math.Min(oldStripe, newStripe);
		int second = Math.Max(oldStripe, newStripe);
		_fileLockStripes[first].EnterWriteLock();
		try
		{
			_fileLockStripes[second].EnterWriteLock();
			try
			{
				ApplyRenameLocked(oldRel, newRel);
			}
			finally
			{
				_fileLockStripes[second].ExitWriteLock();
			}
		}
		finally
		{
			_fileLockStripes[first].ExitWriteLock();
		}
	}

	public int SyncAll()
	{
		HashSet<string> dirtyFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (RamBlockCopy item in _ram.SnapshotEntries(dirtyOnly: true))
		{
			dirtyFiles.Add(item.Key.RelativePath);
		}
		foreach (SsdBlockMeta item in _ssd.SnapshotEntries(dirtyOnly: true))
		{
			dirtyFiles.Add(item.Key.RelativePath);
		}
		if (!_inFlightEvictions.IsEmpty)
		{
			foreach (BlockKey k in _inFlightEvictions.Keys)
			{
				dirtyFiles.Add(k.RelativePath);
			}
		}

		int totalFlushed = 0;
		foreach (string rel in dirtyFiles)
		{
			string fullPath = ResolveBackingPath(rel);
			ReaderWriterLockSlim fileLock = GetFileLock(rel);
			fileLock.EnterWriteLock();
			try
			{
				totalFlushed += FlushFileLocked(rel, fullPath, null);
			}
			finally
			{
				fileLock.ExitWriteLock();
			}
		}

		_ssd.FlushHandleToDisk();
		return totalFlushed;
	}

	public CacheStatsSnapshot GetStatsSnapshot()
	{
		(int ramCount, long ramUsed, int ramDirty) = _ram.GetCounts();
		(int ssdCount, long ssdUsed, int ssdDirty) = _ssd.GetCounts();
		(int tracked, double avgScore, long seqBypasses, HeatmapCell[] heatmap) = _scorer.GetMetrics();

		return new CacheStatsSnapshot
		{
			TotalReadBytes = Interlocked.Read(ref _totalReadBytes),
			TotalWriteBytes = Interlocked.Read(ref _totalWriteBytes),
			RamHits = Interlocked.Read(ref _ramHits),
			SsdHits = Interlocked.Read(ref _ssdHits),
			HddMisses = Interlocked.Read(ref _hddMisses),
			SequentialBypasses = seqBypasses,
			RamCachedBlocks = ramCount,
			RamUsedBytes = ramUsed,
			RamCapacityBytes = _options.RamCacheSizeBytes,
			SsdCachedBlocks = ssdCount,
			SsdUsedBytes = ssdUsed,
			SsdCapacityBytes = _options.SsdCacheSizeBytes,
			DirtyBlocks = ramDirty + ssdDirty + _inFlightEvictions.Count,
			TrackedBlocks = tracked,
			AverageAiScore = avgScore,
			HeatmapCells = heatmap,
			IsRunning = _isRunning,
			TargetRootPath = _targetRootFull,
			SsdCacheFilePath = _ssd.CacheFilePath,
			MountPoint = _options.MountPoint,
			L1BackendSummary = _ram.BackendDescription,
			WritePolicy = _options.WritePolicy
		};
	}

	public void PersistMetadata(bool cleanShutdown = false)
	{
		if (!_ssd.IsEnabled)
		{
			return;
		}
		try
		{
			List<SsdBlockMeta> entries = _ssd.SnapshotEntries();
			List<PersistedBlockEntry> persisted = new List<PersistedBlockEntry>(entries.Count);
			foreach (SsdBlockMeta e in entries)
			{
				persisted.Add(new PersistedBlockEntry
				{
					RelativePath = e.Key.RelativePath,
					BlockIndex = e.Key.BlockIndex,
					SlotIndex = e.SlotIndex,
					ValidLength = e.ValidLength,
					BackingFileSize = e.BackingFileSize,
					BackingLastWriteUtcTicks = e.BackingLastWriteUtcTicks,
					IsValid = true,
					IsDirty = e.IsDirty,
					AiScore = e.AiScore,
					LastAccessUtcTicks = e.LastAccessUtc.Ticks
				});
			}
			_metadata.SaveMetadata(_blockSize, cleanShutdown, persisted);
		}
		catch (Exception ex)
		{
			Log($"警告: メタデータ保存中にエラー: {ex.Message}");
		}
	}

	public void Stop()
	{
		if (!_isRunning)
		{
			return;
		}
		_isRunning = false;
		try
		{
			_cts?.Cancel();
			Task[] waitTasks = new[] { _flushTask, _autoSaveTask }.Where(t => t != null).Cast<Task>().ToArray();
			if (waitTasks.Length > 0)
			{
				Task.WaitAll(waitTasks, TimeSpan.FromSeconds(5));
			}
		}
		catch
		{
		}

		int synced = SyncAll();
		if (synced > 0)
		{
			Log($"[SyncAll] {synced} 個の Dirty ブロックを HDD へ同期しました。");
		}
		if (_options.DemoteL1ToL2OnStop && _ssd.IsEnabled && _ram.IsEnabled)
		{
			int demoted = DemoteL1ToL2BeforeShutdown();
			if (demoted > 0)
			{
				Log($"[L1→L2 退避] {demoted} 個のホットブロックを永続 SSD キャッシュへ保存しました。");
			}
		}
		PersistMetadata(cleanShutdown: true);
		_ram.Clear();
		_inFlightEvictions.Clear();
		_cts?.Dispose();
		_cts = null;
		Log($"エンジン停止: {_targetRootFull}");
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		Stop();
		_ram.Dispose();
		_ssd.Dispose();
		for (int i = 0; i < FileLockStripeCount; i++)
		{
			_fileLockStripes[i].Dispose();
		}
		_statCache.Clear();
		_inFlightEvictions.Clear();
	}

	private int DemoteL1ToL2BeforeShutdown()
	{
		List<RamBlockCopy> l1Entries = _ram.SnapshotEntries(dirtyOnly: false);
		List<(BlockKey Key, InFlightEvictionEntry Entry)>? deferredFlushes = null;
		int count = 0;
		foreach (RamBlockCopy entry in l1Entries)
		{
			if (entry.IsDirty)
			{
				continue;
			}
			if (_ssd.ContainsFresh(entry.Key, entry.BackingFileSize, entry.BackingLastWriteUtcTicks))
			{
				continue;
			}
			ReaderWriterLockSlim fileLock = GetFileLock(entry.Key.RelativePath);
			fileLock.EnterWriteLock();
			try
			{
				if (PutInSsdWithDeferredFlush(
					entry.Key,
					entry.Data,
					entry.ValidLength,
					isDirty: false,
					entry.BackingFileSize,
					entry.BackingLastWriteUtcTicks,
					entry.AiScore,
					ref deferredFlushes))
				{
					count++;
				}
			}
			finally
			{
				fileLock.ExitWriteLock();
			}
		}
		DrainDeferredEvictions(deferredFlushes);
		return count;
	}

	private static IL1CacheTier CreateL1Tier(CacheOptions options, string instanceId, int blockSize, Action<string> log)
	{
		if (options.L1BackendMode == L1BackendMode.Disabled || options.RamCacheSizeBytes <= 0)
		{
			return new RamCacheTier(0, blockSize);
		}

		if (options.L1BackendMode == L1BackendMode.InProcessRam)
		{
			return new RamCacheTier(options.RamCacheSizeBytes, blockSize);
		}

		if (options.L1BackendMode == L1BackendMode.RamDiskFile)
		{
			if (string.IsNullOrWhiteSpace(options.RamDiskCacheDirectory))
			{
				throw new ArgumentException("RamDiskCacheDirectory must be specified when L1BackendMode is RamDiskFile.", nameof(options));
			}
			string dir = Path.GetFullPath(options.RamDiskCacheDirectory);
			RamDiskFileCacheTier.CleanupOrphanCacheFiles(dir);
			return new RamDiskFileCacheTier(dir, instanceId, options.RamCacheSizeBytes, blockSize);
		}

		// AutoPreferRamDisk: use existing/specified RAMDisk directory when available and writable; otherwise fall back to In-Process RAM.
		if (!string.IsNullOrWhiteSpace(options.RamDiskCacheDirectory))
		{
			try
			{
				string dir = Path.GetFullPath(options.RamDiskCacheDirectory);
				string? root = Path.GetPathRoot(dir);
				if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
				{
					DriveInfo di = new DriveInfo(root);
					if (di.IsReady && di.AvailableFreeSpace >= Math.Min(options.RamCacheSizeBytes, 64L * 1024 * 1024))
					{
						RamDiskFileCacheTier.CleanupOrphanCacheFiles(dir);
						return new RamDiskFileCacheTier(dir, instanceId, options.RamCacheSizeBytes, blockSize);
					}
				}
			}
			catch (Exception ex)
			{
				log($"[L1 自動フォールバック] RAMDisk 初期化失敗のためプロセス内 RAM を使用します: {ex.Message}");
			}
		}

		return new RamCacheTier(options.RamCacheSizeBytes, blockSize);
	}

	private int FlushFileLocked(string rel, string fullPath, FileStream? openBackingStream)
	{
		List<(BlockKey Key, InFlightEvictionEntry Entry)>? inflightForFile = null;
		if (!_inFlightEvictions.IsEmpty)
		{
			foreach (KeyValuePair<BlockKey, InFlightEvictionEntry> kv in _inFlightEvictions)
			{
				if (string.Equals(kv.Key.RelativePath, rel, StringComparison.OrdinalIgnoreCase))
				{
					inflightForFile ??= new List<(BlockKey, InFlightEvictionEntry)>();
					inflightForFile.Add((kv.Key, kv.Value));
				}
			}
		}

		List<RamBlockCopy> ramDirty = _ram.SnapshotEntries(rel, dirtyOnly: true);
		List<SsdBlockMeta> ssdDirty = _ssd.SnapshotEntries(rel, dirtyOnly: true);
		if ((inflightForFile == null || inflightForFile.Count == 0) && ramDirty.Count == 0 && ssdDirty.Count == 0)
		{
			return 0;
		}

		Dictionary<long, (byte[] Data, int ValidLength, bool FromRam)> pending = new Dictionary<long, (byte[], int, bool)>();
		if (inflightForFile != null)
		{
			foreach ((BlockKey k, InFlightEvictionEntry entry) in inflightForFile)
			{
				pending[k.BlockIndex] = (entry.Data, entry.ValidLength, false);
			}
		}

		byte[] ssdReadBuf = ArrayPool<byte>.Shared.Rent(_blockSize);
		try
		{
			foreach (SsdBlockMeta sm in ssdDirty)
			{
				if (_ssd.TryReadBlockForFlush(sm.Key, ssdReadBuf, out int vlen))
				{
					byte[] copy = new byte[_blockSize];
					Buffer.BlockCopy(ssdReadBuf, 0, copy, 0, _blockSize);
					pending[sm.Key.BlockIndex] = (copy, vlen, false);
				}
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(ssdReadBuf);
		}
		foreach (RamBlockCopy rm in ramDirty)
		{
			pending[rm.Key.BlockIndex] = (rm.Data, rm.ValidLength, true);
		}

		if (pending.Count == 0)
		{
			return 0;
		}

		CoalescedWritePendingToBacking(fullPath, openBackingStream, pending);

		if (inflightForFile != null)
		{
			foreach ((BlockKey k, InFlightEvictionEntry entry) in inflightForFile)
			{
				_inFlightEvictions.TryRemove(new KeyValuePair<BlockKey, InFlightEvictionEntry>(k, entry));
			}
		}

		(_, long newSize, long newTicks) = RefreshBackingInfoLocked(rel, fullPath, openBackingStream);
		foreach (KeyValuePair<long, (byte[] Data, int ValidLength, bool FromRam)> kv in pending)
		{
			BlockKey k = new BlockKey(rel, kv.Key);
			_ram.MarkClean(k, newSize, newTicks);
			_ssd.MarkClean(k, newSize, newTicks);
			CacheTier tier = kv.Value.FromRam ? CacheTier.RamL1 : CacheTier.SsdL2;
			_scorer.UpdateTierState(k, tier, isDirty: false);
		}
		_ram.UpdateBackingMetadataForFile(rel, newSize, newTicks);
		_ssd.UpdateBackingMetadataForFile(rel, newSize, newTicks);

		return pending.Count;
	}

	private void FlushInFlightForFileLocked(string rel, string fullPath, FileStream? openBackingStream)
	{
		List<(BlockKey Key, InFlightEvictionEntry Entry)>? items = null;
		foreach (KeyValuePair<BlockKey, InFlightEvictionEntry> kv in _inFlightEvictions)
		{
			if (string.Equals(kv.Key.RelativePath, rel, StringComparison.OrdinalIgnoreCase))
			{
				items ??= new List<(BlockKey, InFlightEvictionEntry)>();
				items.Add((kv.Key, kv.Value));
			}
		}
		if (items == null || items.Count == 0)
		{
			return;
		}

		Dictionary<long, (byte[] Data, int ValidLength, bool FromRam)> pending = new Dictionary<long, (byte[], int, bool)>(items.Count);
		foreach ((BlockKey k, InFlightEvictionEntry entry) in items)
		{
			pending[k.BlockIndex] = (entry.Data, entry.ValidLength, false);
		}
		CoalescedWritePendingToBacking(fullPath, openBackingStream, pending);
		foreach ((BlockKey k, InFlightEvictionEntry entry) in items)
		{
			_inFlightEvictions.TryRemove(new KeyValuePair<BlockKey, InFlightEvictionEntry>(k, entry));
			_scorer.UpdateTierState(k, CacheTier.None, isDirty: false);
		}
	}

	private void CoalescedWritePendingToBacking(
		string fullPath,
		FileStream? openBackingStream,
		Dictionary<long, (byte[] Data, int ValidLength, bool FromRam)> pending)
	{
		string? dir = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}

		KeyValuePair<long, (byte[] Data, int ValidLength, bool FromRam)>[] sorted = pending.OrderBy(p => p.Key).ToArray();
		SafeFileHandle? ownedHandle = null;
		SafeFileHandle targetHandle;
		if (openBackingStream != null && openBackingStream.CanWrite)
		{
			targetHandle = openBackingStream.SafeFileHandle;
		}
		else
		{
			ownedHandle = File.OpenHandle(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
			targetHandle = ownedHandle;
		}

		byte[] coalesceBuf = ArrayPool<byte>.Shared.Rent(MaxCoalescedFlushBlocks * _blockSize);
		try
		{
			int idx = 0;
			while (idx < sorted.Length)
			{
				long runStartBlock = sorted[idx].Key;
				int runOffset = 0;
				int runBlocks = 0;
				long expectedNextBlock = runStartBlock;

				while (idx < sorted.Length && runBlocks < MaxCoalescedFlushBlocks && sorted[idx].Key == expectedNextBlock)
				{
					int vlen = sorted[idx].Value.ValidLength;
					Buffer.BlockCopy(sorted[idx].Value.Data, 0, coalesceBuf, runOffset, vlen);
					runOffset += vlen;
					runBlocks++;
					idx++;
					expectedNextBlock++;

					// Only keep coalescing if the current block filled a complete _blockSize slot.
					if (vlen < _blockSize)
					{
						break;
					}
				}

				RandomAccess.Write(targetHandle, coalesceBuf.AsSpan(0, runOffset), runStartBlock * _blockSize);
			}
			RandomAccess.FlushToDisk(targetHandle);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(coalesceBuf);
			ownedHandle?.Dispose();
		}
	}

	private void PutInRamWithDemotion(
		BlockKey key,
		ReadOnlySpan<byte> blockData,
		int validLength,
		bool isDirty,
		long backingSize,
		long backingTicks,
		double aiScore,
		ref List<(BlockKey Key, InFlightEvictionEntry Entry)>? deferredFlushes)
	{
		InFlightEvictionEntry? staged = null;
		BlockKey stagedKey = default;
		RamBlockCopy? evicted = _ram.Put(
			key,
			blockData,
			validLength,
			isDirty,
			backingSize,
			backingTicks,
			aiScore,
			onEvictDirty: (evKey, evData, evLen) =>
			{
				staged = new InFlightEvictionEntry
				{
					Data = evData,
					ValidLength = evLen,
					IsDirty = true,
					TrySsdDemote = _ssd.IsEnabled
				};
				stagedKey = evKey;
				_inFlightEvictions[evKey] = staged;
			});

		_scorer.UpdateTierState(key, CacheTier.RamL1, isDirty, aiScore);

		if (evicted != null)
		{
			if (evicted.IsDirty)
			{
				if (staged != null && stagedKey.Equals(evicted.Key))
				{
					staged.BackingFileSize = evicted.BackingFileSize;
					staged.BackingLastWriteUtcTicks = evicted.BackingLastWriteUtcTicks;
					staged.AiScore = evicted.AiScore;
					deferredFlushes ??= new List<(BlockKey, InFlightEvictionEntry)>();
					deferredFlushes.Add((evicted.Key, staged));
				}
			}
			else if (_ssd.IsEnabled)
			{
				InFlightEvictionEntry cleanDemote = new InFlightEvictionEntry
				{
					Data = evicted.Data,
					ValidLength = evicted.ValidLength,
					BackingFileSize = evicted.BackingFileSize,
					BackingLastWriteUtcTicks = evicted.BackingLastWriteUtcTicks,
					AiScore = evicted.AiScore,
					IsDirty = false,
					TrySsdDemote = true
				};
				deferredFlushes ??= new List<(BlockKey, InFlightEvictionEntry)>();
				deferredFlushes.Add((evicted.Key, cleanDemote));
			}
			else
			{
				_scorer.UpdateTierState(evicted.Key, CacheTier.None, false);
			}
		}
	}

	private bool PutInSsdWithDeferredFlush(
		BlockKey key,
		ReadOnlySpan<byte> blockData,
		int validLength,
		bool isDirty,
		long backingSize,
		long backingTicks,
		double aiScore,
		ref List<(BlockKey Key, InFlightEvictionEntry Entry)>? deferredFlushes)
	{
		if (!_ssd.IsEnabled)
		{
			return false;
		}
		try
		{
			InFlightEvictionEntry? staged = null;
			BlockKey stagedKey = default;
			bool ok = _ssd.TryWriteBlock(
				key,
				blockData,
				validLength,
				isDirty,
				backingSize,
				backingTicks,
				aiScore,
				out EvictedSsdBlock? evictedDirty,
				onEvictDirty: (evKey, evData, evLen) =>
				{
					staged = new InFlightEvictionEntry
					{
						Data = evData,
						ValidLength = evLen,
						IsDirty = true,
						TrySsdDemote = false
					};
					stagedKey = evKey;
					_inFlightEvictions[evKey] = staged;
				});

			if (evictedDirty != null && staged != null && stagedKey.Equals(evictedDirty.Key))
			{
				deferredFlushes ??= new List<(BlockKey, InFlightEvictionEntry)>();
				deferredFlushes.Add((evictedDirty.Key, staged));
			}

			if (ok)
			{
				_scorer.UpdateTierState(key, CacheTier.SsdL2, isDirty, aiScore);
			}
			return ok;
		}
		catch (Exception ex)
		{
			Log($"[警告] L2 SSD 書き込みフォールバック (HDD 直通へ切替): {ex.Message}");
			return false;
		}
	}

	private void DrainDeferredEvictions(List<(BlockKey Key, InFlightEvictionEntry Entry)>? deferredFlushes)
	{
		if (deferredFlushes == null || deferredFlushes.Count == 0)
		{
			return;
		}

		Queue<(BlockKey Key, InFlightEvictionEntry Entry)> queue = new Queue<(BlockKey, InFlightEvictionEntry)>(deferredFlushes);
		while (queue.Count > 0)
		{
			(BlockKey evKey, InFlightEvictionEntry entry) = queue.Dequeue();
			ReaderWriterLockSlim evLock = GetFileLock(evKey.RelativePath);
			List<(BlockKey Key, InFlightEvictionEntry Entry)>? extraSsdEvictions = null;

			evLock.EnterWriteLock();
			try
			{
				if (!entry.IsDirty)
				{
					if (_ssd.IsEnabled && !_ram.TryReadSlice(evKey, 0, Span<byte>.Empty, entry.BackingFileSize, entry.BackingLastWriteUtcTicks, out _, out _, out _))
					{
						string evFull = ResolveBackingPath(evKey.RelativePath);
						(bool exists, long ns, long nt) = RefreshBackingInfoLocked(evKey.RelativePath, evFull, null);
						if (exists && ns == entry.BackingFileSize && nt == entry.BackingLastWriteUtcTicks && !_ssd.ContainsFresh(evKey, ns, nt))
						{
							PutInSsdWithDeferredFlush(
								evKey,
								entry.Data,
								entry.ValidLength,
								isDirty: false,
								entry.BackingFileSize,
								entry.BackingLastWriteUtcTicks,
								entry.AiScore,
								ref extraSsdEvictions);
						}
					}
				}
				else if (_inFlightEvictions.TryGetValue(evKey, out InFlightEvictionEntry? current) && ReferenceEquals(current, entry))
				{
					bool handled = false;
					if (entry.TrySsdDemote && _ssd.IsEnabled)
					{
						if (PutInSsdWithDeferredFlush(
							evKey,
							entry.Data,
							entry.ValidLength,
							isDirty: true,
							entry.BackingFileSize,
							entry.BackingLastWriteUtcTicks,
							entry.AiScore,
							ref extraSsdEvictions))
						{
							_inFlightEvictions.TryRemove(new KeyValuePair<BlockKey, InFlightEvictionEntry>(evKey, entry));
							handled = true;
						}
					}

					if (!handled)
					{
						string evFull = ResolveBackingPath(evKey.RelativePath);
						WriteBackingRangeLocked(evFull, null, evKey.BlockIndex * _blockSize, entry.Data.AsSpan(0, entry.ValidLength));
						_inFlightEvictions.TryRemove(new KeyValuePair<BlockKey, InFlightEvictionEntry>(evKey, entry));

						(_, long ns, long nt) = RefreshBackingInfoLocked(evKey.RelativePath, evFull, null);
						_ram.UpdateBackingMetadataForFile(evKey.RelativePath, ns, nt);
						_ssd.UpdateBackingMetadataForFile(evKey.RelativePath, ns, nt);
						_scorer.UpdateTierState(evKey, CacheTier.None, false);
					}
				}
			}
			finally
			{
				evLock.ExitWriteLock();
			}

			if (extraSsdEvictions != null)
			{
				foreach ((BlockKey Key, InFlightEvictionEntry Entry) extra in extraSsdEvictions)
				{
					queue.Enqueue(extra);
				}
			}
		}
	}

	private long GetInFlightMaxFileSize(string normalizedRelPath)
	{
		if (_inFlightEvictions.IsEmpty)
		{
			return 0L;
		}
		long maxEnd = 0L;
		foreach (KeyValuePair<BlockKey, InFlightEvictionEntry> kv in _inFlightEvictions)
		{
			if (string.Equals(kv.Key.RelativePath, normalizedRelPath, StringComparison.OrdinalIgnoreCase))
			{
				long end = kv.Key.BlockIndex * _blockSize + kv.Value.ValidLength;
				if (end > maxEnd)
				{
					maxEnd = end;
				}
			}
		}
		return maxEnd;
	}

	private void ApplyRenameLocked(string oldRel, string newRel)
	{
		_ram.RenamePrefix(oldRel, newRel);
		_ssd.RenamePrefix(oldRel, newRel);
		_scorer.RenamePrefix(oldRel, newRel);
		_statCache.TryRemove(oldRel, out _);
		_statCache.TryRemove(newRel, out _);

		if (!_inFlightEvictions.IsEmpty)
		{
			string oldPrefix = oldRel.TrimEnd('\\') + "\\";
			string newPrefix = newRel.TrimEnd('\\') + "\\";
			foreach (KeyValuePair<BlockKey, InFlightEvictionEntry> kv in _inFlightEvictions)
			{
				string? nextPath = null;
				if (string.Equals(kv.Key.RelativePath, oldRel, StringComparison.OrdinalIgnoreCase))
				{
					nextPath = newRel;
				}
				else if (kv.Key.RelativePath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
				{
					nextPath = newPrefix + kv.Key.RelativePath.Substring(oldPrefix.Length);
				}
				if (nextPath != null && _inFlightEvictions.TryRemove(kv.Key, out InFlightEvictionEntry? removed))
				{
					_inFlightEvictions[new BlockKey(nextPath, kv.Key.BlockIndex)] = removed;
				}
			}
		}
	}

	private static bool MatchesPrefix(string relPath, string prefix, string subPrefix)
	{
		if (string.IsNullOrEmpty(prefix))
		{
			return true;
		}
		return string.Equals(relPath, prefix, StringComparison.OrdinalIgnoreCase)
			|| relPath.StartsWith(subPrefix, StringComparison.OrdinalIgnoreCase);
	}

	private (bool Exists, long Size, long LastWriteUtcTicks) GetBackingInfoFast(string rel, string fullPath, FileStream? openBackingStream)
	{
		if (_statTtlStopwatchTicks > 0 && _statCache.TryGetValue(rel, out CachedBackingStat cached))
		{
			if (Stopwatch.GetTimestamp() - cached.CachedAtTimestamp <= _statTtlStopwatchTicks)
			{
				return (cached.Exists, cached.Size, cached.LastWriteUtcTicks);
			}
		}
		return RefreshBackingInfoLocked(rel, fullPath, openBackingStream);
	}

	private (bool Exists, long Size, long LastWriteUtcTicks) RefreshBackingInfoLocked(string rel, string fullPath, FileStream? openBackingStream)
	{
		(bool Exists, long Size, long LastWriteUtcTicks) result;
		if (!File.Exists(fullPath))
		{
			if (openBackingStream != null)
			{
				result = (true, openBackingStream.Length, File.GetLastWriteTimeUtc(fullPath).Ticks);
			}
			else
			{
				result = (false, 0, 0);
			}
		}
		else
		{
			FileInfo fi = new FileInfo(fullPath);
			long size = openBackingStream != null ? Math.Max(fi.Length, openBackingStream.Length) : fi.Length;
			result = (true, size, fi.LastWriteTimeUtc.Ticks);
		}

		if (_statTtlStopwatchTicks > 0)
		{
			if (_statCache.Count >= MaxStatCacheEntries && !_statCache.ContainsKey(rel))
			{
				PruneStatCache(expiredOnly: false);
			}
			_statCache[rel] = new CachedBackingStat
			{
				Exists = result.Exists,
				Size = result.Size,
				LastWriteUtcTicks = result.LastWriteUtcTicks,
				CachedAtTimestamp = Stopwatch.GetTimestamp()
			};
		}
		return result;
	}

	private void PruneStatCache(bool expiredOnly)
	{
		if (_statCache.IsEmpty)
		{
			return;
		}
		long now = Stopwatch.GetTimestamp();
		foreach (KeyValuePair<string, CachedBackingStat> kv in _statCache)
		{
			if (now - kv.Value.CachedAtTimestamp > _statTtlStopwatchTicks)
			{
				_statCache.TryRemove(kv.Key, out _);
			}
		}

		if (!expiredOnly && _statCache.Count >= MaxStatCacheEntries)
		{
			int toDrop = _statCache.Count - MaxStatCacheEntries + MaxStatCacheEntries / 4;
			int dropped = 0;
			foreach (string key in _statCache.Keys)
			{
				if (_statCache.TryRemove(key, out _) && ++dropped >= toDrop)
				{
					break;
				}
			}
		}
	}

	private static int ReadBackingBlockLocked(string fullPath, FileStream? openBackingStream, long offset, Span<byte> buffer)
	{
		if (openBackingStream != null && openBackingStream.CanRead)
		{
			SafeFileHandle handle = openBackingStream.SafeFileHandle;
			long streamLen = RandomAccess.GetLength(handle);
			if (offset >= streamLen)
			{
				return 0;
			}
			return ReadExactHandle(handle, offset, buffer);
		}

		if (!File.Exists(fullPath))
		{
			return 0;
		}

		using SafeFileHandle owned = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		long len = RandomAccess.GetLength(owned);
		if (offset >= len)
		{
			return 0;
		}
		return ReadExactHandle(owned, offset, buffer);
	}

	private static int ReadExactHandle(SafeFileHandle handle, long offset, Span<byte> buffer)
	{
		int readTotal = 0;
		while (readTotal < buffer.Length)
		{
			int n = RandomAccess.Read(handle, buffer.Slice(readTotal), offset + readTotal);
			if (n <= 0)
			{
				break;
			}
			readTotal += n;
		}
		return readTotal;
	}

	private static void WriteBackingRangeLocked(string fullPath, FileStream? openBackingStream, long offset, ReadOnlySpan<byte> data)
	{
		string? dir = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		if (openBackingStream != null && openBackingStream.CanWrite)
		{
			RandomAccess.Write(openBackingStream.SafeFileHandle, data, offset);
			RandomAccess.FlushToDisk(openBackingStream.SafeFileHandle);
			return;
		}
		using SafeFileHandle handle = File.OpenHandle(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
		RandomAccess.Write(handle, data, offset);
		RandomAccess.FlushToDisk(handle);
	}

	private string ResolveBackingPath(string normalizedRelativePath)
	{
		string combined = Path.GetFullPath(Path.Combine(_targetRootFull, normalizedRelativePath));
		string rootWithSep = _targetRootFull.EndsWith('\\') ? _targetRootFull : _targetRootFull + "\\";
		if (!string.Equals(combined, _targetRootFull.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
		    !combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
		{
			throw new UnauthorizedAccessException($"Path escapes target root: {normalizedRelativePath}");
		}
		return combined;
	}

	private static int GetFileLockIndex(string normalizedRelPath)
	{
		return (StringComparer.OrdinalIgnoreCase.GetHashCode(normalizedRelPath) & 0x7FFFFFFF) & (FileLockStripeCount - 1);
	}

	private ReaderWriterLockSlim GetFileLock(string normalizedRelPath)
	{
		return _fileLockStripes[GetFileLockIndex(normalizedRelPath)];
	}

	private async Task FlushLoopAsync(CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(_options.FlushInterval, token);
				int flushed = SyncAll();
				if (flushed > 0)
				{
					Log($"[Write-Back Flush] {flushed} ブロックを HDD へ同期しました。");
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				Log($"[警告] バックグラウンドフラッシュエラー: {ex.Message}");
			}
		}
	}

	private async Task AutoSaveLoopAsync(CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(_options.AutoSaveInterval, token);
				PruneStatCache(expiredOnly: true);
				PersistMetadata(cleanShutdown: false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch
			{
			}
		}
	}

	private void Log(string message)
	{
		OnLog?.Invoke(message);
	}
}
