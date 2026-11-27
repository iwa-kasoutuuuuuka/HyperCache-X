using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using HCX.Core;
using HCX.VirtualFs;
using Xunit;

namespace HCX.Tests;

public sealed class PerformanceOptimizationTests : IDisposable
{
	private readonly string _tempRoot;
	private readonly string _targetDir;
	private readonly string _ssdDir;

	public PerformanceOptimizationTests()
	{
		_tempRoot = Path.Combine(Path.GetTempPath(), $"HCX_PerfTests_{Guid.NewGuid():N}");
		_targetDir = Path.Combine(_tempRoot, "hdd");
		_ssdDir = Path.Combine(_tempRoot, "ssd");
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
	public void ParallelReads_Across8Threads_L1AndL2_ReturnExactBytes()
	{
		const int blockSize = 65536;
		const int totalBlocks = 32;
		string rel = "parallel_data.bin";
		byte[] seed = new byte[totalBlocks * blockSize];
		new Random(2026).NextBytes(seed);
		File.WriteAllBytes(Path.Combine(_targetDir, rel), seed);

		// L1 holds 16 blocks, L2 holds 32 blocks so threads hit both L1 RAM and L2 SSD concurrently.
		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 16 * blockSize,
			SsdCacheSizeBytes = 32 * blockSize,
			SequentialThreshold = 1000,
			SsdPromoteScore = 1.0,
			RamPromoteScore = 1.0
		});
		engine.Start();

		byte[] warmup = new byte[blockSize];
		for (int b = 0; b < totalBlocks; b++)
		{
			engine.ReadBlockRange(rel, (long)b * blockSize, warmup);
		}

		Parallel.For(0, 8, t =>
		{
			byte[] local = new byte[blockSize];
			for (int i = 0; i < 64; i++)
			{
				int b = (i * 7 + t * 5) % totalBlocks;
				int read = engine.ReadBlockRange(rel, (long)b * blockSize, local);
				Assert.Equal(blockSize, read);
				Assert.True(local.AsSpan().SequenceEqual(seed.AsSpan(b * blockSize, blockSize)));
			}
		});

		CacheStatsSnapshot snap = engine.GetStatsSnapshot();
		Assert.True(
			snap.RamHits + snap.SsdHits >= 512,
			$"RamHits={snap.RamHits}, SsdHits={snap.SsdHits}, HddMisses={snap.HddMisses}, Seq={snap.SequentialBypasses}, RamBlocks={snap.RamCachedBlocks}, SsdBlocks={snap.SsdCachedBlocks}");
	}

	[Fact]
	public void CoalescedWriteBackFlush_PersistsContiguousAndDisjointRunsAccurately()
	{
		const int blockSize = 65536;
		string rel = "coalesced.bin";
		string full = Path.Combine(_targetDir, rel);

		// Write 20 contiguous blocks (exceeds 16-block batch limit -> 2 runs: 16 + 4),
		// leave block 20..21 as zero/gap, then write blocks 22..24 (3 blocks, last partial).
		byte[] fullExpected = new byte[24 * blockSize + 1234];
		Random rnd = new Random(909);
		byte[] run1 = new byte[20 * blockSize];
		rnd.NextBytes(run1);
		run1.CopyTo(fullExpected, 0);

		byte[] run2 = new byte[2 * blockSize + 1234];
		rnd.NextBytes(run2);
		run2.CopyTo(fullExpected, 22 * blockSize);

		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 64 * blockSize,
			SsdCacheSizeBytes = 64 * blockSize,
			WritePolicy = WritePolicy.WriteBack,
			FlushInterval = TimeSpan.FromMinutes(10)
		});
		engine.Start();

		engine.WriteBlockRange(rel, 0, run1);
		engine.WriteBlockRange(rel, 22L * blockSize, run2);

		int flushed = engine.SyncAll();
		Assert.Equal(23, flushed);

		byte[] actual = File.ReadAllBytes(full);
		Assert.Equal(fullExpected.Length, actual.Length);
		Assert.True(actual.AsSpan().SequenceEqual(fullExpected));
	}

	[Fact]
	public void BackingStatCacheTtl_ServesWithinTtlAndRefreshesOnInvalidation()
	{
		const int blockSize = 65536;
		string rel = "stat_ttl.bin";
		string full = Path.Combine(_targetDir, rel);
		File.WriteAllBytes(full, Enumerable.Repeat((byte)0x10, blockSize).ToArray());

		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 4 * blockSize,
			SsdCacheSizeBytes = 4 * blockSize,
			RamPromoteScore = 1.0,
			BackingStatCacheTtl = TimeSpan.FromSeconds(30)
		});
		engine.Start();

		byte[] buf = new byte[blockSize];
		engine.ReadBlockRange(rel, 0, buf);
		Assert.Equal((byte)0x10, buf[0]);

		// External modification while within 30s stat TTL: cached stat serves L1 hit until invalidated.
		File.WriteAllBytes(full, Enumerable.Repeat((byte)0x22, blockSize).ToArray());
		File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddSeconds(5));
		engine.ReadBlockRange(rel, 0, buf);
		Assert.Equal((byte)0x10, buf[0]);

		// Explicit invalidation (as called on HcxFileSystem.Open) forces stat refresh and detects stale cache.
		engine.InvalidateBackingStatCache(rel);
		engine.ReadBlockRange(rel, 0, buf);
		Assert.Equal((byte)0x22, buf[0]);
	}

	[Fact]
	public void HcxFileSystem_ZeroCopyNativeBufferReadWrite_RoundTripsAccurately()
	{
		const int blockSize = 65536;
		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 8 * blockSize,
			SsdCacheSizeBytes = 8 * blockSize,
			WritePolicy = WritePolicy.WriteBack
		});
		engine.Start();

		HcxFileSystem fs = new HcxFileSystem(engine);
		byte[] payload = new byte[blockSize + 777];
		new Random(444).NextBytes(payload);

		nint writePtr = Marshal.AllocHGlobal(payload.Length);
		nint readPtr = Marshal.AllocHGlobal(payload.Length);
		try
		{
			Marshal.Copy(payload, 0, writePtr, payload.Length);
			fs.Create(@"\native_io.bin", 0, 0x40000000, (uint)FileAttributes.Archive, null!, 0, out object node, out object desc, out _, out _);
			fs.Write(node, desc, writePtr, 0, (uint)payload.Length, false, false, out uint written, out _);
			Assert.Equal((uint)payload.Length, written);

			fs.Read(node, desc, readPtr, 0, (uint)payload.Length, out uint bytesRead);
			Assert.Equal((uint)payload.Length, bytesRead);

			byte[] actual = new byte[payload.Length];
			Marshal.Copy(readPtr, actual, 0, payload.Length);
			Assert.True(actual.AsSpan().SequenceEqual(payload));

			fs.Flush(node, desc, out _);
			fs.Close(node, desc);

			byte[] onDisk = File.ReadAllBytes(Path.Combine(_targetDir, "native_io.bin"));
			Assert.True(onDisk.AsSpan().SequenceEqual(payload));
		}
		finally
		{
			Marshal.FreeHGlobal(writePtr);
			Marshal.FreeHGlobal(readPtr);
		}
	}

	[Fact]
	public void MemoryFileSystem_ZeroCopyNativeBufferReadWrite_RoundTripsAccurately()
	{
		InMemoryVolumeStore store = new InMemoryVolumeStore(4 * 1024 * 1024);
		MemoryFileSystem memFs = new MemoryFileSystem(store, "TEST_RAM");

		byte[] payload = new byte[InMemoryVolumeStore.PageSize + 513];
		new Random(555).NextBytes(payload);

		nint writePtr = Marshal.AllocHGlobal(payload.Length);
		nint readPtr = Marshal.AllocHGlobal(payload.Length);
		try
		{
			Marshal.Copy(payload, 0, writePtr, payload.Length);
			memFs.Create(@"\ram_native.bin", 0, 0x40000000, (uint)FileAttributes.Archive, null!, 0, out object node, out object desc, out _, out _);
			memFs.Write(node, desc, writePtr, 0, (uint)payload.Length, false, false, out uint written, out _);
			Assert.Equal((uint)payload.Length, written);

			memFs.Read(node, desc, readPtr, 0, (uint)payload.Length, out uint bytesRead);
			Assert.Equal((uint)payload.Length, bytesRead);

			byte[] actual = new byte[payload.Length];
			Marshal.Copy(readPtr, actual, 0, payload.Length);
			Assert.True(actual.AsSpan().SequenceEqual(payload));
			memFs.Close(node, desc);
		}
		finally
		{
			Marshal.FreeHGlobal(writePtr);
			Marshal.FreeHGlobal(readPtr);
		}
	}

	[Fact]
	public void ShardedRamAndSsdTiers_ParallelReadWriteAcross16Threads_PreserveExactIntegrity()
	{
		const int blockSize = 4096;
		const int blockCount = 128; // Triggers 16 RAM shards & 8 SSD shards
		using RamCacheTier ram = new RamCacheTier((long)blockCount * blockSize, blockSize);
		using SsdFileCacheTier ssd = new SsdFileCacheTier(_ssdDir, "sharded_perf", (long)blockCount * blockSize, blockSize);

		Assert.Equal(16, ram.ShardCount);
		Assert.Equal(8, ssd.ShardCount);

		byte[][] blocks = Enumerable.Range(0, blockCount).Select(i =>
		{
			byte[] b = new byte[blockSize];
			new Random(7000 + i).NextBytes(b);
			return b;
		}).ToArray();

		Parallel.For(0, blockCount, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
		{
			BlockKey key = new BlockKey($"file_{i % 8}.bin", i);
			ram.Put(key, blocks[i], blockSize, isDirty: false, blockSize, 100L, 5.0);
			Assert.True(ssd.TryWriteBlock(key, blocks[i], blockSize, isDirty: false, blockSize, 100L, 5.0, out _));
		});

		Parallel.For(0, 16, tid =>
		{
			byte[] buf = new byte[blockSize];
			for (int round = 0; round < 32; round++)
			{
				int i = (tid * 13 + round) % blockCount;
				BlockKey key = new BlockKey($"file_{i % 8}.bin", i);
				Assert.True(ram.TryReadSlice(key, 0, buf, blockSize, 100L, out int ramLen, out _, out _));
				Assert.Equal(blockSize, ramLen);
				Assert.True(buf.AsSpan().SequenceEqual(blocks[i]));

				Array.Clear(buf);
				Assert.True(ssd.TryReadFullBlock(key, buf, blockSize, 100L, out SsdBlockMeta? meta, out _));
				Assert.NotNull(meta);
				Assert.True(buf.AsSpan().SequenceEqual(blocks[i]));
			}
		});
	}

	[Fact]
	public void SequentialBypass_CoalescedBulkBackingRead_ReturnsExactBytesForLargeFile()
	{
		const int blockSize = 4096;
		byte[] expected = new byte[blockSize * 20];
		new Random(8080).NextBytes(expected);
		string rel = "seq_bulk.bin";
		File.WriteAllBytes(Path.Combine(_targetDir, rel), expected);

		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			BlockSizeBytes = blockSize,
			RamCacheSizeBytes = 8 * blockSize,
			SsdCacheSizeBytes = 8 * blockSize,
			SequentialThreshold = 4,
			EnableReadAhead = false
		});
		engine.Start();

		byte[] actual = new byte[expected.Length];
		int read = engine.ReadBlockRange(rel, 0, actual);
		Assert.Equal(expected.Length, read);
		Assert.True(actual.AsSpan().SequenceEqual(expected));
		Assert.True(engine.GetStatsSnapshot().SequentialBypasses >= 1);
	}

	[Fact]
	public void InMemoryVolumeStore_ParallelMultiFileReadWriteByHandle_IsThreadSafeAndAccurate()
	{
		InMemoryVolumeStore store = new InMemoryVolumeStore(16 * 1024 * 1024);
		const int fileCount = 8;
		const int fileSize = InMemoryVolumeStore.PageSize * 2 + 1234;
		InMemoryNodeHandle[] handles = new InMemoryNodeHandle[fileCount];
		byte[][] expected = new byte[fileCount][];

		for (int i = 0; i < fileCount; i++)
		{
			store.CreateEntryWithHandle($@"\par_{i}.bin", isDirectory: false, (uint)FileAttributes.Archive, null, 0, out handles[i]);
			expected[i] = new byte[fileSize];
			new Random(9100 + i).NextBytes(expected[i]);
		}

		Parallel.For(0, fileCount, i =>
		{
			store.WriteFileByHandle(handles[i], 0, expected[i], writeToEndOfFile: false, constrainedIo: false, out uint written);
			Assert.Equal((uint)fileSize, written);

			byte[] readBack = new byte[fileSize];
			int got = store.ReadFileByHandle(handles[i], 0, readBack, out bool eof);
			Assert.False(eof);
			Assert.Equal(fileSize, got);
			Assert.True(readBack.AsSpan().SequenceEqual(expected[i]));
		});

		RamDiskStats stats = store.GetStats();
		Assert.Equal(fileCount, stats.FileCount);
		Assert.True(stats.UsedBytes >= (long)fileCount * fileSize);
	}

	[Fact]
	public void RamDiskFileCacheTier_MemoryMappedMode_RoundTripsAndCleansUpCleanly()
	{
		const int blockSize = 4096;
		string dir = Path.Combine(_tempRoot, "mmf_l1");
		string cachePath;
		using (RamDiskFileCacheTier tier = new RamDiskFileCacheTier(dir, "mmf_test", 16 * blockSize, blockSize))
		{
			cachePath = tier.CacheFilePath;
			Assert.True(tier.IsMemoryMapped);
			Assert.Contains("MMF", tier.BackendDescription);

			byte[] data = new byte[blockSize];
			new Random(4242).NextBytes(data);
			BlockKey key = new BlockKey("mmf.bin", 0);
			tier.Put(key, data, blockSize, isDirty: true, blockSize, 55L, 3.0);

			byte[] patch = { 0xDE, 0xAD, 0xBE, 0xEF };
			Assert.True(tier.TryPatchOrGetForWrite(key, 100, patch, blockSize, 55L, 4.0, markDirty: true));
			Buffer.BlockCopy(patch, 0, data, 100, patch.Length);

			byte[] actual = new byte[blockSize];
			Assert.True(tier.TryReadSlice(key, 0, actual, blockSize, 55L, out int copied, out bool dirty, out _));
			Assert.Equal(blockSize, copied);
			Assert.True(dirty);
			Assert.True(actual.AsSpan().SequenceEqual(data));
		}

		Assert.False(File.Exists(cachePath));
	}

	[Fact]
	public void AsyncSequentialReadAhead_PrefetchesUpcomingBlocks_WithoutCorruptingData()
	{
		const int blockSize = 4096;
		byte[] expected = new byte[blockSize * 12];
		new Random(5151).NextBytes(expected);
		string rel = "readahead.bin";
		File.WriteAllBytes(Path.Combine(_targetDir, rel), expected);

		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			BlockSizeBytes = blockSize,
			RamCacheSizeBytes = 16 * blockSize,
			SsdCacheSizeBytes = 16 * blockSize,
			SequentialThreshold = 10,
			EnableReadAhead = true,
			ReadAheadBlocks = 2
		});
		engine.Start();

		byte[] buf = new byte[blockSize];
		for (int b = 0; b < 5; b++)
		{
			int read = engine.ReadBlockRange(rel, (long)b * blockSize, buf);
			Assert.Equal(blockSize, read);
			Assert.True(buf.AsSpan().SequenceEqual(expected.AsSpan(b * blockSize, blockSize)));
		}

		// Give background read-ahead worker a brief moment to prefetch upcoming blocks 5..6.
		bool prefetched = System.Threading.SpinWait.SpinUntil(() => engine.PrefetchedBlocks > 0, TimeSpan.FromSeconds(2));
		Assert.True(prefetched, "Expected async read-ahead worker to prefetch upcoming sequential blocks.");

		for (int b = 5; b < 8; b++)
		{
			int read = engine.ReadBlockRange(rel, (long)b * blockSize, buf);
			Assert.Equal(blockSize, read);
			Assert.True(buf.AsSpan().SequenceEqual(expected.AsSpan(b * blockSize, blockSize)));
		}
		Assert.True(engine.GetStatsSnapshot().PrefetchedBlocks > 0);
	}

	[Fact]
	public void ShardedTiers_EmptyShardCollisionAtCapacity_EvictsFromOtherShardWithoutExceedingCap()
	{
		const int blockSize = 4096;
		const int cap = 64; // 16 RAM shards / 8 SSD shards / 16 RAMDisk shards
		using RamCacheTier ram = new RamCacheTier((long)cap * blockSize, blockSize);
		using SsdFileCacheTier ssd = new SsdFileCacheTier(_ssdDir, "empty_shard_ssd", (long)cap * blockSize, blockSize);
		using RamDiskFileCacheTier rdisk = new RamDiskFileCacheTier(Path.Combine(_tempRoot, "empty_shard_rdisk"), "es", (long)cap * blockSize, blockSize);

		byte[] data = new byte[blockSize];
		new Random(3131).NextBytes(data);

		// Insert 64 keys that all hash to shard 0 so shards 1..N-1 are completely empty while global capacity is full.
		int inserted = 0;
		for (int idx = 0; inserted < cap && idx < 50_000; idx++)
		{
			BlockKey k = new BlockKey("skew.bin", idx);
			if ((k.GetHashCode() & 15) == 0)
			{
				ram.Put(k, data, blockSize, isDirty: false, blockSize, 1L, 1.0);
				Assert.True(ssd.TryWriteBlock(k, data, blockSize, isDirty: false, blockSize, 1L, 1.0, out _));
				rdisk.Put(k, data, blockSize, isDirty: false, blockSize, 1L, 1.0);
				inserted++;
			}
		}
		Assert.Equal(cap, inserted);

		// Now insert a key hashing into an empty shard (shard 1): must evict from shard 0 and never exceed capacity.
		BlockKey otherShardKey = default;
		for (int idx = 0; idx < 50_000; idx++)
		{
			BlockKey k = new BlockKey("other.bin", idx);
			if ((k.GetHashCode() & 15) == 1)
			{
				otherShardKey = k;
				break;
			}
		}

		ram.Put(otherShardKey, data, blockSize, isDirty: false, blockSize, 1L, 1.0);
		Assert.True(ssd.TryWriteBlock(otherShardKey, data, blockSize, isDirty: false, blockSize, 1L, 1.0, out _));
		rdisk.Put(otherShardKey, data, blockSize, isDirty: false, blockSize, 1L, 1.0);

		Assert.Equal(cap, ram.GetCounts().CachedBlocks);
		Assert.Equal(cap, ssd.GetCounts().CachedBlocks);
		Assert.Equal(cap, rdisk.GetCounts().CachedBlocks);
	}

	[Fact]
	public void InMemoryVolumeStore_ClearInvalidatesExistingNodeHandlesImmediately()
	{
		InMemoryVolumeStore store = new InMemoryVolumeStore(4 * 1024 * 1024);
		store.CreateEntryWithHandle(@"\gen.bin", isDirectory: false, (uint)FileAttributes.Archive, null, 0, out InMemoryNodeHandle handle);
		byte[] payload = { 1, 2, 3, 4 };
		store.WriteFileByHandle(handle, 0, payload, false, false, out uint written);
		Assert.Equal(4u, written);
		Assert.True(store.TryGetInfoByHandle(handle, out _));

		store.Clear();

		Assert.False(store.TryGetInfoByHandle(handle, out _));
		Assert.Throws<IOException>(() => store.ReadFileByHandle(handle, 0, new byte[4], out _));
		Assert.Throws<IOException>(() => store.WriteFileByHandle(handle, 0, payload, false, false, out _));
	}

	[Fact]
	public void RamDiskFileCacheTier_ShardedMmf_ParallelReadWriteAcross16Threads_PreservesIntegrity()
	{
		const int blockSize = 4096;
		const int blockCount = 128; // 16 shards + MMF
		using RamDiskFileCacheTier tier = new RamDiskFileCacheTier(Path.Combine(_tempRoot, "sharded_mmf"), "smmf", (long)blockCount * blockSize, blockSize);
		Assert.Equal(16, tier.ShardCount);
		Assert.True(tier.IsMemoryMapped);

		byte[][] blocks = Enumerable.Range(0, blockCount).Select(i =>
		{
			byte[] b = new byte[blockSize];
			new Random(6200 + i).NextBytes(b);
			return b;
		}).ToArray();

		Parallel.For(0, blockCount, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
		{
			tier.Put(new BlockKey($"rd_{i % 8}.bin", i), blocks[i], blockSize, isDirty: false, blockSize, 77L, 4.0);
		});

		Parallel.For(0, 16, tid =>
		{
			byte[] buf = new byte[blockSize];
			for (int r = 0; r < 32; r++)
			{
				int i = (tid * 11 + r) % blockCount;
				Assert.True(tier.TryReadSlice(new BlockKey($"rd_{i % 8}.bin", i), 0, buf, blockSize, 77L, out int copied, out _, out _));
				Assert.Equal(blockSize, copied);
				Assert.True(buf.AsSpan().SequenceEqual(blocks[i]));
			}
		});
	}
}
