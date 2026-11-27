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
}
