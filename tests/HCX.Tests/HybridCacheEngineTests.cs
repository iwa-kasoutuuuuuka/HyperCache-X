using System;
using System.IO;
using System.Linq;
using HCX.Core;
using Xunit;

namespace HCX.Tests;

public sealed class HybridCacheEngineTests : IDisposable
{
	private readonly string _tempRoot;
	private readonly string _targetDir;
	private readonly string _ssdDir;

	public HybridCacheEngineTests()
	{
		_tempRoot = Path.Combine(Path.GetTempPath(), $"HCX_Tests_{Guid.NewGuid():N}");
		_targetDir = Path.Combine(_tempRoot, "hdd");
		_ssdDir = Path.Combine(_tempRoot, "ssd_cache");
		Directory.CreateDirectory(_targetDir);
		Directory.CreateDirectory(_ssdDir);
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
	public void ReadPromotion_ServesFromRamAndDemotesEvictedBlocksToSsd()
	{
		const int blockSize = 65536;
		byte[] fileData = new byte[4 * blockSize];
		new Random(101).NextBytes(fileData);
		File.WriteAllBytes(Path.Combine(_targetDir, "data.bin"), fileData);

		// L1 RAM holds only 1 block; L2 SSD holds 8 blocks.
		CacheOptions opts = new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = blockSize,
			SsdCacheSizeBytes = 8 * blockSize,
			SsdPromoteScore = 1.2,
			RamPromoteScore = 2.0
		};

		using HybridCacheEngine engine = new HybridCacheEngine(opts);
		Assert.True(engine.Start());

		byte[] buf = new byte[blockSize];

		// Block 0: 1st read -> HDD miss, 2nd read -> promoted to RAM.
		Assert.Equal(blockSize, engine.ReadBlockRange("data.bin", 0, buf));
		Assert.Equal(blockSize, engine.ReadBlockRange("data.bin", 0, buf));
		Assert.Equal(1, engine.GetStatsSnapshot().RamCachedBlocks);

		// Block 2 (non-sequential): 1st read -> HDD miss, 2nd read -> promoted to RAM, demoting Block 0 to L2 SSD.
		Assert.Equal(blockSize, engine.ReadBlockRange("data.bin", 2L * blockSize, buf));
		Assert.Equal(blockSize, engine.ReadBlockRange("data.bin", 2L * blockSize, buf));

		CacheStatsSnapshot mid = engine.GetStatsSnapshot();
		Assert.Equal(1, mid.RamCachedBlocks);
		Assert.Equal(2, mid.SsdCachedBlocks);

		// Reading Block 0 now hits L2 SSD!
		long ssdHitsBefore = mid.SsdHits;
		Assert.Equal(blockSize, engine.ReadBlockRange("data.bin", 0, buf));
		Assert.True(buf.AsSpan().SequenceEqual(fileData.AsSpan(0, blockSize)));
		Assert.Equal(ssdHitsBefore + 1, engine.GetStatsSnapshot().SsdHits);

		// Reading Block 0 again hits L1 RAM!
		long ramHitsBefore = engine.GetStatsSnapshot().RamHits;
		Assert.Equal(blockSize, engine.ReadBlockRange("data.bin", 0, buf));
		Assert.Equal(ramHitsBefore + 1, engine.GetStatsSnapshot().RamHits);
	}

	[Fact]
	public void WriteBack_BuffersDirtyDataAndSyncAllFlushesBitForBit()
	{
		const int blockSize = 65536;
		string rel = "writeback.dat";
		string full = Path.Combine(_targetDir, rel);
		byte[] initial = Enumerable.Repeat((byte)0xAA, 2 * blockSize).ToArray();
		File.WriteAllBytes(full, initial);

		CacheOptions opts = new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 4 * blockSize,
			SsdCacheSizeBytes = 4 * blockSize,
			WritePolicy = WritePolicy.WriteBack,
			FlushInterval = TimeSpan.FromMinutes(10)
		};

		using HybridCacheEngine engine = new HybridCacheEngine(opts);
		engine.Start();

		byte[] patch = Enumerable.Repeat((byte)0x5C, 1024).ToArray();
		engine.WriteBlockRange(rel, 128, patch);

		// Backing file on disk still has old bytes until flushed.
		byte[] diskBefore = File.ReadAllBytes(full);
		Assert.Equal((byte)0xAA, diskBefore[128]);

		// Reading via engine immediately returns patched bytes from cache.
		byte[] readBack = new byte[1024];
		engine.ReadBlockRange(rel, 128, readBack);
		Assert.True(readBack.AsSpan().SequenceEqual(patch));
		Assert.True(engine.GetStatsSnapshot().DirtyBlocks > 0);

		// SyncAll flushes dirty blocks to backing HDD and clears dirty count.
		int flushed = engine.SyncAll();
		Assert.True(flushed >= 1);
		Assert.Equal(0, engine.GetStatsSnapshot().DirtyBlocks);

		byte[] diskAfter = File.ReadAllBytes(full);
		Assert.True(diskAfter.AsSpan(128, 1024).SequenceEqual(patch));
		Assert.Equal((byte)0xAA, diskAfter[0]);
		Assert.Equal((byte)0xAA, diskAfter[128 + 1024]);
	}

	[Fact]
	public void WriteBack_ExtendingFileUpdatesEffectiveSizeBeforeFlush()
	{
		const int blockSize = 65536;
		string rel = "extend.bin";
		CacheOptions opts = new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 2 * blockSize,
			SsdCacheSizeBytes = 2 * blockSize,
			WritePolicy = WritePolicy.WriteBack,
			FlushInterval = TimeSpan.FromMinutes(10)
		};

		using HybridCacheEngine engine = new HybridCacheEngine(opts);
		engine.Start();

		byte[] payload = new byte[1500];
		new Random(7).NextBytes(payload);
		engine.WriteBlockRange(rel, 0, payload);

		Assert.Equal(1500, engine.GetEffectiveFileSize(rel));
		byte[] readBuf = new byte[1500];
		int read = engine.ReadBlockRange(rel, 0, readBuf);
		Assert.Equal(1500, read);
		Assert.True(readBuf.AsSpan().SequenceEqual(payload));

		engine.Stop();
		Assert.Equal(1500, new FileInfo(Path.Combine(_targetDir, rel)).Length);
	}

	[Fact]
	public void WriteBack_SsdEvictionFlushesDirtyBlockToBackingFile()
	{
		const int blockSize = 65536;
		string rel = "evict_dirty.bin";
		CacheOptions opts = new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 0, // force straight to L2 SSD
			SsdCacheSizeBytes = blockSize, // 1 slot only!
			WritePolicy = WritePolicy.WriteBack,
			FlushInterval = TimeSpan.FromMinutes(10)
		};

		using HybridCacheEngine engine = new HybridCacheEngine(opts);
		engine.Start();

		byte[] b0 = Enumerable.Repeat((byte)0x11, blockSize).ToArray();
		byte[] b1 = Enumerable.Repeat((byte)0x22, blockSize).ToArray();
		engine.WriteBlockRange(rel, 0, b0);
		engine.WriteBlockRange(rel, blockSize, b1); // evicts b0 -> must flush b0 to HDD

		byte[] read0 = new byte[blockSize];
		engine.ReadBlockRange(rel, 0, read0);
		Assert.True(read0.AsSpan().SequenceEqual(b0));

		engine.Stop();
		byte[] finalFile = File.ReadAllBytes(Path.Combine(_targetDir, rel));
		Assert.Equal(2 * blockSize, finalFile.Length);
		Assert.True(finalFile.AsSpan(0, blockSize).SequenceEqual(b0));
		Assert.True(finalFile.AsSpan(blockSize, blockSize).SequenceEqual(b1));
	}

	[Fact]
	public void WriteThrough_PersistsImmediatelyToBackingFile()
	{
		const int blockSize = 65536;
		string rel = "writethrough.bin";
		string full = Path.Combine(_targetDir, rel);
		File.WriteAllBytes(full, new byte[blockSize]);

		CacheOptions opts = new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 2 * blockSize,
			SsdCacheSizeBytes = 2 * blockSize,
			WritePolicy = WritePolicy.WriteThrough
		};

		using HybridCacheEngine engine = new HybridCacheEngine(opts);
		engine.Start();

		byte[] data = Enumerable.Repeat((byte)0x7E, 512).ToArray();
		engine.WriteBlockRange(rel, 256, data);

		Assert.Equal(0, engine.GetStatsSnapshot().DirtyBlocks);
		byte[] onDisk = File.ReadAllBytes(full);
		Assert.True(onDisk.AsSpan(256, 512).SequenceEqual(data));
	}

	[Fact]
	public void Coherence_ExternalModificationInvalidatesStaleCache()
	{
		const int blockSize = 65536;
		string rel = "coherent.bin";
		string full = Path.Combine(_targetDir, rel);
		File.WriteAllBytes(full, Enumerable.Repeat((byte)0x01, blockSize).ToArray());

		CacheOptions opts = new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 2 * blockSize,
			SsdCacheSizeBytes = 2 * blockSize,
			RamPromoteScore = 1.0
		};

		using HybridCacheEngine engine = new HybridCacheEngine(opts);
		engine.Start();

		byte[] buf = new byte[blockSize];
		engine.ReadBlockRange(rel, 0, buf);
		Assert.Equal((byte)0x01, buf[0]);

		// Simulate external modification outside HCX with distinct timestamp and content.
		byte[] updated = Enumerable.Repeat((byte)0x99, blockSize).ToArray();
		File.WriteAllBytes(full, updated);
		File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddSeconds(5));

		engine.ReadBlockRange(rel, 0, buf);
		Assert.Equal((byte)0x99, buf[0]);
	}

	[Fact]
	public void Coherence_TruncateAndRenameAndInvalidateWorkCleanly()
	{
		const int blockSize = 65536;
		string relOld = "old_name.bin";
		string relNew = "new_name.bin";
		string fullOld = Path.Combine(_targetDir, relOld);
		string fullNew = Path.Combine(_targetDir, relNew);

		byte[] data = Enumerable.Repeat((byte)0x33, 2 * blockSize).ToArray();
		File.WriteAllBytes(fullOld, data);

		CacheOptions opts = new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 4 * blockSize,
			SsdCacheSizeBytes = 4 * blockSize,
			RamPromoteScore = 1.0
		};

		using HybridCacheEngine engine = new HybridCacheEngine(opts);
		engine.Start();

		byte[] buf = new byte[blockSize];
		engine.ReadBlockRange(relOld, 0, buf);
		engine.ReadBlockRange(relOld, blockSize, buf);

		File.Move(fullOld, fullNew);
		engine.NotifyRename(relOld, relNew);
		Assert.Equal(2 * blockSize, engine.GetEffectiveFileSize(relNew));

		using (FileStream fs = new FileStream(fullNew, FileMode.Open, FileAccess.ReadWrite))
		{
			fs.SetLength(1000);
		}
		engine.TruncateFile(relNew, 1000);
		Assert.Equal(1000, engine.GetEffectiveFileSize(relNew));

		engine.InvalidateFile(relNew);
		Assert.Equal(0, engine.GetStatsSnapshot().RamCachedBlocks);
	}

	[Fact]
	public void Persistence_RestoresWarmSsdCacheAcrossEngineRestart()
	{
		const int blockSize = 65536;
		string rel = "persist.bin";
		byte[] data = new byte[blockSize];
		new Random(55).NextBytes(data);
		File.WriteAllBytes(Path.Combine(_targetDir, rel), data);

		CacheOptions opts = new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 0, // place directly in L2 SSD
			SsdCacheSizeBytes = 4 * blockSize,
			SsdPromoteScore = 1.2
		};

		using (HybridCacheEngine first = new HybridCacheEngine(opts))
		{
			first.Start();
			byte[] tmp = new byte[blockSize];
			first.ReadBlockRange(rel, 0, tmp); // score 1.5 >= 1.2 -> promoted to L2 SSD
			Assert.Equal(1, first.GetStatsSnapshot().SsdCachedBlocks);
			first.Stop();
			Assert.True(File.Exists(first.MetadataFilePath));
			Assert.True(File.Exists(first.SsdCacheFilePath));
		}

		using (HybridCacheEngine second = new HybridCacheEngine(opts))
		{
			second.Start();
			Assert.Equal(1, second.GetStatsSnapshot().SsdCachedBlocks);

			byte[] restored = new byte[blockSize];
			second.ReadBlockRange(rel, 0, restored);
			Assert.True(restored.AsSpan().SequenceEqual(data));
			Assert.Equal(1, second.GetStatsSnapshot().SsdHits);
			Assert.Equal(0, second.GetStatsSnapshot().HddMisses);
		}
	}

	[Fact]
	public void MultiDriveIsolation_SeparatesCacheFilesAndMetadata()
	{
		string dir1 = Path.Combine(_tempRoot, "drive1");
		string dir2 = Path.Combine(_tempRoot, "drive2");
		Directory.CreateDirectory(dir1);
		Directory.CreateDirectory(dir2);

		using HybridCacheEngine e1 = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = dir1,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 65536 * 4,
			SsdCacheSizeBytes = 65536 * 4
		});
		using HybridCacheEngine e2 = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = dir2,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 65536 * 4,
			SsdCacheSizeBytes = 65536 * 4
		});

		Assert.NotEqual(e1.InstanceId, e2.InstanceId);
		Assert.NotEqual(e1.SsdCacheFilePath, e2.SsdCacheFilePath);
		Assert.NotEqual(e1.MetadataFilePath, e2.MetadataFilePath);
	}

	[Fact]
	public void BenchmarkRunner_ExecutesAndVerifiesDataIntegrity()
	{
		BenchmarkResult result = CacheBenchmarkRunner.Run(
			_targetDir,
			_ssdDir,
			ramSizeBytes: 16L * 1024 * 1024,
			ssdSizeBytes: 32L * 1024 * 1024);

		Assert.True(result.VerifiedIntegrity);
		Assert.True(result.HitRatePercent > 50.0);
		Assert.True(result.RamHits + result.SsdHits >= 80);
		Assert.True(result.SequentialBypasses > 0);
	}
}
