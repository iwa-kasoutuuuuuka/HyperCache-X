using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using HCX.Core;
using HCX.VirtualFs;
using Xunit;

namespace HCX.Tests;

public sealed class StabilityAndReliabilityTests : IDisposable
{
	private readonly string _tempRoot;
	private readonly string _targetDir;
	private readonly string _ssdDir;
	private readonly string _ramDiskDir;

	public StabilityAndReliabilityTests()
	{
		_tempRoot = Path.Combine(Path.GetTempPath(), $"HCX_StabilityTests_{Guid.NewGuid():N}");
		_targetDir = Path.Combine(_tempRoot, "hdd");
		_ssdDir = Path.Combine(_tempRoot, "ssd");
		_ramDiskDir = Path.Combine(_tempRoot, "ramdisk");
		Directory.CreateDirectory(_targetDir);
		Directory.CreateDirectory(_ssdDir);
		Directory.CreateDirectory(_ramDiskDir);
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_tempRoot))
			{
				Directory.Delete(_tempRoot, recursive: true);
			}
		}
		catch
		{
		}
	}

	[Fact]
	public void CrossFileEviction_UnderConcurrentReadWriteAndTruncate_IsDeadlockFreeAndLinearizable()
	{
		const int blockSize = 4096;
		const int fileCount = 8;

		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			BlockSizeBytes = blockSize,
			RamCacheSizeBytes = 2 * blockSize,
			SsdCacheSizeBytes = 3 * blockSize,
			WritePolicy = WritePolicy.WriteBack,
			FlushInterval = TimeSpan.FromMinutes(10),
			BackingStatCacheTtl = TimeSpan.FromMilliseconds(50)
		});
		Assert.True(engine.Start());

		object[] perFileLocks = Enumerable.Range(0, fileCount).Select(_ => new object()).ToArray();
		byte[][] expected = Enumerable.Range(0, fileCount).Select(_ => new byte[blockSize * 4]).ToArray();

		using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		Parallel.For(0, 6, workerId =>
		{
			Random rnd = new Random(1000 + workerId);
			byte[] patch = new byte[blockSize / 2];
			byte[] readBuf = new byte[blockSize * 4];

			for (int iter = 0; iter < 80 && !cts.IsCancellationRequested; iter++)
			{
				int f = rnd.Next(fileCount);
				string rel = $"cross_{f}.bin";
				int blockIdx = rnd.Next(3);
				int offsetInBlock = (iter % 2) * (blockSize / 2);
				long offset = (long)blockIdx * blockSize + offsetInBlock;

				lock (perFileLocks[f])
				{
					rnd.NextBytes(patch);
					patch.CopyTo(expected[f], offset);
					engine.WriteBlockRange(rel, offset, patch);

					Array.Clear(readBuf);
					int got = engine.ReadBlockRange(rel, 0, readBuf.AsSpan(0, expected[f].Length));
					Assert.True(got >= offset + patch.Length, $"File={rel} iter={iter} got={got} expectedMin={offset + patch.Length}");
					for (int bi = 0; bi < expected[f].Length; bi++)
					{
						if (readBuf[bi] != expected[f][bi])
						{
							string diskPath = Path.Combine(_targetDir, rel);
							long diskLen = File.Exists(diskPath) ? new FileInfo(diskPath).Length : -1;
							byte diskB0 = 0;
							if (File.Exists(diskPath) && diskLen > 0)
							{
								using FileStream diagFs = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
								int b = diagFs.ReadByte();
								if (b >= 0) diskB0 = (byte)b;
							}
							Assert.Fail($"Mismatch file={rel} iter={iter} writeOffset={offset} got={got} byteIdx={bi} expected={expected[f][bi]:X2} actual={readBuf[bi]:X2} diskLen={diskLen} diskB0={diskB0:X2} stats={engine.GetStatsSnapshot().DirtyBlocks}");
						}
					}
				}
			}
		});

		engine.SyncAll();

		for (int f = 0; f < fileCount; f++)
		{
			byte[] diskBytes = File.ReadAllBytes(Path.Combine(_targetDir, $"cross_{f}.bin"));
			byte[] finalRead = new byte[expected[f].Length];
			int read = engine.ReadBlockRange($"cross_{f}.bin", 0, finalRead);
			Assert.True(read >= expected[f].Length - blockSize * 4);
			Assert.True(diskBytes.AsSpan().SequenceEqual(expected[f].AsSpan(0, diskBytes.Length)));
		}
	}

	[Fact]
	public void HcxFileSystem_ReadUsesStatCacheWithOpenStream_AndOverwriteInvalidates()
	{
		const int blockSize = 4096;
		string rel = "open_stat.bin";
		string full = Path.Combine(_targetDir, rel);
		byte[] v1 = Enumerable.Repeat((byte)0x31, blockSize).ToArray();
		File.WriteAllBytes(full, v1);

		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			BlockSizeBytes = blockSize,
			RamCacheSizeBytes = 4 * blockSize,
			SsdCacheSizeBytes = 4 * blockSize,
			RamPromoteScore = 1.0,
			BackingStatCacheTtl = TimeSpan.FromSeconds(30)
		});
		Assert.True(engine.Start());

		HcxFileSystem fs = new HcxFileSystem(engine);
		nint bufPtr = Marshal.AllocHGlobal(blockSize);
		try
		{
			fs.Open(@"\" + rel, 0, 0x80000000, out object node, out object desc, out _, out _);
			fs.Read(node, desc, bufPtr, 0, blockSize, out uint n1);
			Assert.Equal((uint)blockSize, n1);
			Assert.Equal((byte)0x31, Marshal.ReadByte(bufPtr, 0));

			// External write on backing disk while handle remains open within 30s TTL:
			// Stat cache now works even with non-null openBackingStream!
			File.WriteAllBytes(full, Enumerable.Repeat((byte)0x42, blockSize).ToArray());
			File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddSeconds(5));

			fs.Read(node, desc, bufPtr, 0, blockSize, out uint n2);
			Assert.Equal((uint)blockSize, n2);
			Assert.Equal((byte)0x31, Marshal.ReadByte(bufPtr, 0));
			fs.Close(node, desc);

			// Re-opening via Open() invalidates backing stat cache and reads fresh v2 data.
			fs.Open(@"\" + rel, 0, 0xC0000000, out object node2, out object desc2, out _, out _);
			fs.Read(node2, desc2, bufPtr, 0, blockSize, out uint n3);
			Assert.Equal((uint)blockSize, n3);
			Assert.Equal((byte)0x42, Marshal.ReadByte(bufPtr, 0));

			// Overwrite truncates backing file to 0 and invalidates stat cache.
			fs.Overwrite(node2, desc2, (uint)FileAttributes.Archive, true, 0, out Fsp.Interop.FileInfo fi);
			Assert.Equal(0UL, fi.FileSize);
			int eofStatus = fs.Read(node2, desc2, bufPtr, 0, blockSize, out uint n4);
			Assert.Equal(HcxFileSystem.STATUS_END_OF_FILE, eofStatus);
			Assert.Equal(0U, n4);
			fs.Close(node2, desc2);
		}
		finally
		{
			Marshal.FreeHGlobal(bufPtr);
		}
	}

	[Fact]
	public void HcxFileSystem_DirectoryRename_FlushesSubtreeAndUpdatesFileDesc()
	{
		const int blockSize = 4096;
		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			BlockSizeBytes = blockSize,
			RamCacheSizeBytes = 8 * blockSize,
			SsdCacheSizeBytes = 8 * blockSize,
			WritePolicy = WritePolicy.WriteBack,
			FlushInterval = TimeSpan.FromMinutes(10)
		});
		Assert.True(engine.Start());

		HcxFileSystem fs = new HcxFileSystem(engine);
		byte[] payload = new byte[blockSize + 321];
		new Random(77).NextBytes(payload);
		nint ptr = Marshal.AllocHGlobal(payload.Length);
		try
		{
			Marshal.Copy(payload, 0, ptr, payload.Length);
			fs.Create(@"\dirA", 0x00000001 /* FILE_DIRECTORY_FILE */, 0x40000000, (uint)FileAttributes.Directory, null!, 0, out object dirNode, out object dirDesc, out _, out _);

			fs.Create(@"\dirA\sub.bin", 0, 0x40000000, (uint)FileAttributes.Archive, null!, 0, out object fNode, out object fDesc, out _, out _);
			fs.Write(fNode, fDesc, ptr, 0, (uint)payload.Length, false, false, out uint written, out _);
			Assert.Equal((uint)payload.Length, written);
			fs.Close(fNode, fDesc);

			// Rename dirA -> dirB while dirA\sub.bin still holds dirty Write-Back blocks!
			int renameStatus = fs.Rename(dirNode, dirDesc, @"\dirA", @"\dirB", replaceIfExists: false);
			Assert.Equal(HcxFileSystem.STATUS_SUCCESS, renameStatus);
			fs.Close(dirNode, dirDesc);

			string renamedBacking = Path.Combine(_targetDir, "dirB", "sub.bin");
			Assert.True(File.Exists(renamedBacking));
			Assert.True(File.ReadAllBytes(renamedBacking).AsSpan().SequenceEqual(payload));

			byte[] verifyBuf = new byte[payload.Length];
			int read = engine.ReadBlockRange(@"dirB\sub.bin", 0, verifyBuf);
			Assert.Equal(payload.Length, read);
			Assert.True(verifyBuf.AsSpan().SequenceEqual(payload));
		}
		finally
		{
			Marshal.FreeHGlobal(ptr);
		}
	}

	[Fact]
	public void AiAccessScorer_StripedConcurrency_AndSeqByFileBounding()
	{
		AiAccessScorer scorer = new AiAccessScorer(sequentialThreshold: 8, ssdPromoteScore: 1.2, ramPromoteScore: 2.0);
		DateTime now = DateTime.UtcNow;

		Parallel.For(0, 8, t =>
		{
			for (int i = 0; i < 2600; i++)
			{
				scorer.RecordAccess(new BlockKey($"f_{t}_{i}.dat", i % 5), isWrite: (i % 3 == 0), now);
				if (i % 500 == 0)
				{
					_ = scorer.GetMetrics();
				}
			}
		});

		Assert.True(scorer.TrackedFileSequenceCount <= 16_384);
		(int tracked, double avgScore, _, _) = scorer.GetMetrics();
		Assert.True(tracked > 0);
		Assert.True(avgScore > 0.0);
	}

	[Fact]
	public void DirtyMaxEndTracking_RandomWritesAndTailShrink_MaintainsExactSize()
	{
		const int blockSize = 4096;
		string rel = "maxend.bin";
		byte[] fullBlock = Enumerable.Repeat((byte)0x55, blockSize).ToArray();

		void AssertTierBehavior(Action<BlockKey, byte[], int, bool> put, Action<string, long> truncate, Func<string, long> getMaxEnd, Action<BlockKey, long, long> markClean)
		{
			BlockKey b2 = new BlockKey(rel, 2);
			BlockKey b0 = new BlockKey(rel, 0);

			put(b2, fullBlock, 1000, true);
			Assert.Equal(2 * blockSize + 1000L, getMaxEnd(rel));

			// Updating earlier dirty block should not corrupt max end.
			put(b0, fullBlock, 4000, true);
			Assert.Equal(2 * blockSize + 1000L, getMaxEnd(rel));

			// Shrinking tail dirty block must recompute max end accurately.
			put(b2, fullBlock, 500, true);
			Assert.Equal(2 * blockSize + 500L, getMaxEnd(rel));

			truncate(rel, 2 * blockSize + 250L);
			Assert.Equal(2 * blockSize + 250L, getMaxEnd(rel));

			// Cleaning tail block drops max end to block 0's dirty valid length.
			markClean(b2, 4000, 1L);
			Assert.Equal(4000L, getMaxEnd(rel));

			markClean(b0, 4000, 1L);
			Assert.Equal(0L, getMaxEnd(rel));
		}

		using (RamCacheTier ram = new RamCacheTier(16 * blockSize, blockSize))
		{
			AssertTierBehavior(
				(k, d, v, dirty) => ram.Put(k, d, v, dirty, 0, 0, 1.0),
				ram.TruncateFile,
				ram.GetTrackedMaxFileSize,
				ram.MarkClean);
		}

		using (RamDiskFileCacheTier ramDisk = new RamDiskFileCacheTier(_ramDiskDir, "maxend", 16 * blockSize, blockSize))
		{
			AssertTierBehavior(
				(k, d, v, dirty) => ramDisk.Put(k, d, v, dirty, 0, 0, 1.0),
				ramDisk.TruncateFile,
				ramDisk.GetTrackedMaxFileSize,
				ramDisk.MarkClean);
		}

		using (SsdFileCacheTier ssd = new SsdFileCacheTier(_ssdDir, "maxend", 16 * blockSize, blockSize))
		{
			AssertTierBehavior(
				(k, d, v, dirty) => ssd.TryWriteBlock(k, d, v, dirty, 0, 0, 1.0, out _),
				ssd.TruncateFile,
				ssd.GetTrackedMaxFileSize,
				ssd.MarkClean);
		}
	}

	[Fact]
	public void SsdShrinkAndRamDiskOrphanCleanup_WorkSafely()
	{
		const int blockSize = 4096;
		string ssdSub = Path.Combine(_ssdDir, "shrink");
		Directory.CreateDirectory(ssdSub);
		string binPath;
		using (SsdFileCacheTier large = new SsdFileCacheTier(ssdSub, "shrink1", 10 * blockSize, blockSize))
		{
			binPath = large.CacheFilePath;
			byte[] data = new byte[blockSize];
			for (int i = 0; i < 8; i++)
			{
				large.TryWriteBlock(new BlockKey("big.bin", i), data, blockSize, isDirty: false, backingSize: 8 * blockSize, backingTicks: 1, aiScore: 1.0, out _);
			}
		}
		Assert.Equal(8L * blockSize, new FileInfo(binPath).Length);

		// Re-open with smaller capacity (3 blocks) -> cache_{id}.bin shrinks accordingly.
		using (SsdFileCacheTier small = new SsdFileCacheTier(ssdSub, "shrink1", 3 * blockSize, blockSize))
		{
			Assert.Equal(3, small.MaxSlots);
		}
		Assert.Equal(3L * blockSize, new FileInfo(binPath).Length);
		Assert.True(SsdFileCacheTier.DeleteCacheFiles(ssdSub, "shrink1") >= 1);
		Assert.False(File.Exists(binPath));

		// Orphan L1 cleanup must remove unheld l1_cache_*.bin while skipping actively locked ones.
		string orphanPath = Path.Combine(_ramDiskDir, "l1_cache_orphan_dead.bin");
		File.WriteAllBytes(orphanPath, new byte[4096]);
		using RamDiskFileCacheTier active = new RamDiskFileCacheTier(_ramDiskDir, "active_live", 4 * blockSize, blockSize);
		int removed = RamDiskFileCacheTier.CleanupOrphanCacheFiles(_ramDiskDir);
		Assert.Equal(1, removed);
		Assert.False(File.Exists(orphanPath));
		Assert.True(File.Exists(active.CacheFilePath));
	}

	[Fact]
	public void AppSettingsManager_SaveLoadAndCorruptFallback()
	{
		string settingsPath = Path.Combine(_tempRoot, "settings.json");
		AppSettings saved = new AppSettings
		{
			TargetPath = _targetDir,
			L1BackendMode = "InProcessRam",
			RamSizeMb = 512,
			SsdRoot = _ssdDir,
			SsdSizeMb = 4096,
			WritePolicy = "WriteThrough",
			AutoRestoreSession = true
		};

		AppSettingsManager.Save(saved, settingsPath);
		AppSettings loaded = AppSettingsManager.Load(settingsPath);
		Assert.Equal(_targetDir, loaded.TargetPath);
		Assert.Equal("InProcessRam", loaded.L1BackendMode);
		Assert.Equal(512, loaded.RamSizeMb);
		Assert.Equal(4096, loaded.SsdSizeMb);
		Assert.Equal("WriteThrough", loaded.WritePolicy);
		Assert.True(loaded.AutoRestoreSession);

		// Corrupt JSON gracefully returns defaults instead of throwing.
		File.WriteAllText(settingsPath, "{ not valid json !!!");
		AppSettings fallback = AppSettingsManager.Load(settingsPath);
		Assert.Equal("AutoPreferRamDisk", fallback.L1BackendMode);
	}

	[Fact]
	public void WinFsp_LiveMount_E2E_WhenInstalled()
	{
		WinFspStatusInfo status = WinFspRuntime.GetStatus();
		if (status.State != WinFspInstallState.Installed)
		{
			// Host does not have WinFsp kernel driver installed; skip kernel mount cleanly.
			Assert.Equal(WinFspInstallState.NotInstalled, status.State);
			return;
		}

		string? freeLetter = DiskUtility.GetAvailableDriveLetters().LastOrDefault();
		if (string.IsNullOrEmpty(freeLetter))
		{
			return;
		}

		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			MountPoint = freeLetter,
			RamCacheSizeBytes = 16 * 1024 * 1024,
			SsdCacheSizeBytes = 32 * 1024 * 1024,
			WritePolicy = WritePolicy.WriteBack
		});
		Assert.True(engine.Start());

		using VirtualDriveHost host = new VirtualDriveHost(engine, "HCX_E2E");
		host.Mount(freeLetter);

		string mountedFile = Path.Combine(freeLetter + "\\", "e2e_test.bin");
		byte[] expected = Enumerable.Range(0, 8192).Select(i => (byte)(i & 0xFF)).ToArray();
		File.WriteAllBytes(mountedFile, expected);
		Assert.True(File.ReadAllBytes(mountedFile).AsSpan().SequenceEqual(expected));

		string renamed = Path.Combine(freeLetter + "\\", "e2e_renamed.bin");
		File.Move(mountedFile, renamed);
		Assert.True(File.Exists(renamed));
		File.Delete(renamed);
		Assert.False(File.Exists(renamed));
	}

	[Fact]
	public void WinFsp_LiveRamDisk_E2E_WhenInstalled()
	{
		WinFspStatusInfo status = WinFspRuntime.GetStatus();
		if (status.State != WinFspInstallState.Installed)
		{
			Assert.Equal(WinFspInstallState.NotInstalled, status.State);
			return;
		}

		string? freeLetter = DiskUtility.GetAvailableDriveLetters().LastOrDefault();
		if (string.IsNullOrEmpty(freeLetter))
		{
			return;
		}

		using RamDiskHost host = new RamDiskHost(16 * 1024 * 1024, "HCX_RAM_E2E");
		host.Mount(freeLetter);

		string mountedFile = Path.Combine(freeLetter + "\\", "ram_e2e.bin");
		byte[] expected = Enumerable.Range(0, 16384).Select(i => (byte)(i & 0xFF)).ToArray();
		File.WriteAllBytes(mountedFile, expected);
		Assert.True(File.ReadAllBytes(mountedFile).AsSpan().SequenceEqual(expected));
		Assert.True(host.Store.GetStats().UsedBytes > 0);

		File.Delete(mountedFile);
		Assert.False(File.Exists(mountedFile));
		host.Unmount();
		Assert.Equal(0, host.Store.GetStats().UsedBytes);
	}
}
