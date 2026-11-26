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

	private const int MaxCoalescedFlushBlocks = 16; // 1 MB per coalesced write

	private readonly CacheOptions _options;
	private readonly string _targetRootFull;
	private readonly string _instanceId;
	private readonly int _blockSize;
	private readonly AiAccessScorer _scorer;
	private readonly IL1CacheTier _ram;
	private readonly SsdFileCacheTier _ssd;
	private readonly MetadataManager _metadata;
	private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _fileLocks = new ConcurrentDictionary<string, ReaderWriterLockSlim>(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, CachedBackingStat> _statCache = new ConcurrentDictionary<string, CachedBackingStat>(StringComparer.OrdinalIgnoreCase);
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

		fileLock.EnterReadLock();
		byte[]? rentedBlockBuf = null;
		try
		{
			(bool exists, long backingSize, long backingTicks) = GetBackingInfoFast(rel, fullPath, openBackingStream);
			long effectiveSize = Math.Max(backingSize, Math.Max(_ram.GetTrackedMaxFileSize(rel), _ssd.GetTrackedMaxFileSize(rel)));
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
						PutInRamWithDemotionLocked(key, rentedBlockBuf, ssdMeta.ValidLength, isDirty: false, backingSize, backingTicks, decision.Score);
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
						PutInRamWithDemotionLocked(key, rentedBlockBuf, validLen, isDirty: false, backingSize, backingTicks, decision.Score);
					}
					else if (_ssd.IsEnabled && decision.ShouldPromoteToSsd)
					{
						PutInSsdWithEvictionFlushLocked(key, rentedBlockBuf, validLen, isDirty: false, backingSize, backingTicks, decision.Score, fullPath, openBackingStream);
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

		fileLock.EnterWriteLock();
		byte[]? rentedWorkBuf = null;
		try
		{
			Interlocked.Add(ref _totalWriteBytes, source.Length);
			DateTime nowUtc = DateTime.UtcNow;

			bool useWriteBack = _options.WritePolicy == WritePolicy.WriteBack && (_ram.IsEnabled || _ssd.IsEnabled);
			if (!useWriteBack)
			{
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
									PutInRamWithDemotionLocked(key, rentedWorkBuf, validLen, isDirty: false, newSize, newTicks, decision.Score);
								}
								else if (_ssd.IsEnabled && decision.ShouldPromoteToSsd)
								{
									PutInSsdWithEvictionFlushLocked(key, rentedWorkBuf, validLen, isDirty: false, newSize, newTicks, decision.Score, fullPath, openBackingStream);
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
					_scorer.UpdateTierState(key, CacheTier.RamL1, isDirty: true, decision.Score);
					totalWritten += chunk;
					continue;
				}

				rentedWorkBuf ??= ArrayPool<byte>.Shared.Rent(_blockSize);
				Array.Clear(rentedWorkBuf, 0, _blockSize);
				int existingValidLen = 0;
				bool isPartial = blockOffset > 0 || chunk < _blockSize;
				if (isPartial)
				{
					if (_ssd.TryReadFullBlock(key, rentedWorkBuf, backingSize, backingTicks, out SsdBlockMeta? ssdMeta, out _) && ssdMeta != null)
					{
						existingValidLen = ssdMeta.ValidLength;
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
					PutInRamWithDemotionLocked(key, rentedWorkBuf, newValidLen, isDirty: true, backingSize, backingTicks, decision.Score);
				}
				else if (_ssd.IsEnabled)
				{
					if (!PutInSsdWithEvictionFlushLocked(key, rentedWorkBuf, newValidLen, isDirty: true, backingSize, backingTicks, decision.Score, fullPath, openBackingStream))
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
			return Math.Max(backingSize, Math.Max(_ram.GetTrackedMaxFileSize(rel), _ssd.GetTrackedMaxFileSize(rel)));
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
		if (string.Equals(oldRel, newRel, StringComparison.OrdinalIgnoreCase))
		{
			ReaderWriterLockSlim singleLock = GetFileLock(oldRel);
			singleLock.EnterWriteLock();
			try
			{
				_ram.RenamePrefix(oldRel, newRel);
				_ssd.RenamePrefix(oldRel, newRel);
				_scorer.RenamePrefix(oldRel, newRel);
				_statCache.TryRemove(oldRel, out _);
			}
			finally
			{
				singleLock.ExitWriteLock();
			}
			return;
		}

		bool oldFirst = StringComparer.OrdinalIgnoreCase.Compare(oldRel, newRel) < 0;
		ReaderWriterLockSlim firstLock = GetFileLock(oldFirst ? oldRel : newRel);
		ReaderWriterLockSlim secondLock = GetFileLock(oldFirst ? newRel : oldRel);
		firstLock.EnterWriteLock();
		try
		{
			secondLock.EnterWriteLock();
			try
			{
				_ram.RenamePrefix(oldRel, newRel);
				_ssd.RenamePrefix(oldRel, newRel);
				_scorer.RenamePrefix(oldRel, newRel);
				_statCache.TryRemove(oldRel, out _);
				_statCache.TryRemove(newRel, out _);
			}
			finally
			{
				secondLock.ExitWriteLock();
			}
		}
		finally
		{
			firstLock.ExitWriteLock();
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
			DirtyBlocks = ramDirty + ssdDirty,
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
		foreach (ReaderWriterLockSlim rw in _fileLocks.Values)
		{
			rw.Dispose();
		}
		_fileLocks.Clear();
	}

	private int DemoteL1ToL2BeforeShutdown()
	{
		List<RamBlockCopy> l1Entries = _ram.SnapshotEntries(dirtyOnly: false);
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
			string fullPath = ResolveBackingPath(entry.Key.RelativePath);
			ReaderWriterLockSlim fileLock = GetFileLock(entry.Key.RelativePath);
			fileLock.EnterWriteLock();
			try
			{
				if (PutInSsdWithEvictionFlushLocked(
					entry.Key,
					entry.Data,
					entry.ValidLength,
					isDirty: false,
					entry.BackingFileSize,
					entry.BackingLastWriteUtcTicks,
					entry.AiScore,
					fullPath,
					openBackingStream: null))
				{
					count++;
				}
			}
			finally
			{
				fileLock.ExitWriteLock();
			}
		}
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
		List<RamBlockCopy> ramDirty = _ram.SnapshotEntries(rel, dirtyOnly: true);
		List<SsdBlockMeta> ssdDirty = _ssd.SnapshotEntries(rel, dirtyOnly: true);
		if (ramDirty.Count == 0 && ssdDirty.Count == 0)
		{
			return 0;
		}

		Dictionary<long, (byte[] Data, int ValidLength, bool FromRam)> pending = new Dictionary<long, (byte[], int, bool)>();
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

		(_, long newSize, long newTicks) = RefreshBackingInfoLocked(rel, fullPath, openBackingStream);
		foreach (KeyValuePair<long, (byte[] Data, int ValidLength, bool FromRam)> kv in sorted)
		{
			BlockKey k = new BlockKey(rel, kv.Key);
			_ram.MarkClean(k, newSize, newTicks);
			_ssd.MarkClean(k, newSize, newTicks);
			CacheTier tier = kv.Value.FromRam ? CacheTier.RamL1 : CacheTier.SsdL2;
			_scorer.UpdateTierState(k, tier, isDirty: false);
		}
		_ram.UpdateBackingMetadataForFile(rel, newSize, newTicks);
		_ssd.UpdateBackingMetadataForFile(rel, newSize, newTicks);

		return sorted.Length;
	}

	private void PutInRamWithDemotionLocked(
		BlockKey key,
		ReadOnlySpan<byte> blockData,
		int validLength,
		bool isDirty,
		long backingSize,
		long backingTicks,
		double aiScore)
	{
		RamBlockCopy? evicted = _ram.Put(key, blockData, validLength, isDirty, backingSize, backingTicks, aiScore);
		_scorer.UpdateTierState(key, CacheTier.RamL1, isDirty, aiScore);

		if (evicted != null)
		{
			if (_ssd.IsEnabled)
			{
				string evictedPath = ResolveBackingPath(evicted.Key.RelativePath);
				bool demoted = PutInSsdWithEvictionFlushLocked(
					evicted.Key,
					evicted.Data,
					evicted.ValidLength,
					evicted.IsDirty,
					evicted.BackingFileSize,
					evicted.BackingLastWriteUtcTicks,
					evicted.AiScore,
					evictedPath,
					openBackingStream: null);
				if (!demoted && evicted.IsDirty)
				{
					WriteBackingRangeLocked(evictedPath, null, evicted.Key.BlockIndex * _blockSize, evicted.Data.AsSpan(0, evicted.ValidLength));
					(_, long ns, long nt) = RefreshBackingInfoLocked(evicted.Key.RelativePath, evictedPath, null);
					_ram.UpdateBackingMetadataForFile(evicted.Key.RelativePath, ns, nt);
					_ssd.UpdateBackingMetadataForFile(evicted.Key.RelativePath, ns, nt);
					_scorer.UpdateTierState(evicted.Key, CacheTier.None, false);
				}
			}
			else if (evicted.IsDirty)
			{
				string evictedPath = ResolveBackingPath(evicted.Key.RelativePath);
				WriteBackingRangeLocked(evictedPath, null, evicted.Key.BlockIndex * _blockSize, evicted.Data.AsSpan(0, evicted.ValidLength));
				(_, long ns, long nt) = RefreshBackingInfoLocked(evicted.Key.RelativePath, evictedPath, null);
				_ram.UpdateBackingMetadataForFile(evicted.Key.RelativePath, ns, nt);
				_ssd.UpdateBackingMetadataForFile(evicted.Key.RelativePath, ns, nt);
				_scorer.UpdateTierState(evicted.Key, CacheTier.None, false);
			}
			else
			{
				_scorer.UpdateTierState(evicted.Key, CacheTier.None, false);
			}
		}
	}

	private bool PutInSsdWithEvictionFlushLocked(
		BlockKey key,
		ReadOnlySpan<byte> blockData,
		int validLength,
		bool isDirty,
		long backingSize,
		long backingTicks,
		double aiScore,
		string currentFullPath,
		FileStream? openBackingStream)
	{
		if (!_ssd.IsEnabled)
		{
			return false;
		}
		try
		{
			bool ok = _ssd.TryWriteBlock(key, blockData, validLength, isDirty, backingSize, backingTicks, aiScore, out EvictedSsdBlock? evictedDirty);
			if (evictedDirty != null)
			{
				string evFull = ResolveBackingPath(evictedDirty.Key.RelativePath);
				FileStream? streamToUse = string.Equals(evFull, currentFullPath, StringComparison.OrdinalIgnoreCase)
					? openBackingStream
					: null;
				WriteBackingRangeLocked(evFull, streamToUse, evictedDirty.Key.BlockIndex * _blockSize, evictedDirty.Data.AsSpan(0, evictedDirty.ValidLength));
				(_, long ns, long nt) = RefreshBackingInfoLocked(evictedDirty.Key.RelativePath, evFull, streamToUse);
				_ram.UpdateBackingMetadataForFile(evictedDirty.Key.RelativePath, ns, nt);
				_ssd.UpdateBackingMetadataForFile(evictedDirty.Key.RelativePath, ns, nt);
				_scorer.UpdateTierState(evictedDirty.Key, CacheTier.None, false);
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

	private (bool Exists, long Size, long LastWriteUtcTicks) GetBackingInfoFast(string rel, string fullPath, FileStream? openBackingStream)
	{
		if (_statTtlStopwatchTicks > 0 && openBackingStream == null && _statCache.TryGetValue(rel, out CachedBackingStat cached))
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

		if (_statTtlStopwatchTicks > 0 && openBackingStream == null)
		{
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

	private ReaderWriterLockSlim GetFileLock(string normalizedRelPath)
	{
		return _fileLocks.GetOrAdd(normalizedRelPath, _ => new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion));
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
