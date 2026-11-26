using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HCX.Core;
using HCX.VirtualFs;
using Xunit;

namespace HCX.Tests;

public sealed class RamDiskAndL1BackendTests : IDisposable
{
	private readonly string _tempRoot;
	private readonly string _targetDir;
	private readonly string _ssdDir;
	private readonly string _ramDiskDir;

	public RamDiskAndL1BackendTests()
	{
		_tempRoot = Path.Combine(Path.GetTempPath(), $"HCX_RamDiskTests_{Guid.NewGuid():N}");
		_targetDir = Path.Combine(_tempRoot, "hdd");
		_ssdDir = Path.Combine(_tempRoot, "ssd_l2");
		_ramDiskDir = Path.Combine(_tempRoot, "ramdisk_l1");
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
	public void AutoPreferRamDisk_UsesRamDiskFileWhenPresent_AndFallsBackToInProcessRamWhenAbsent()
	{
		using (HybridCacheEngine withRamDisk = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamDiskCacheDirectory = _ramDiskDir,
			L1BackendMode = L1BackendMode.AutoPreferRamDisk,
			RamCacheSizeBytes = 65536 * 4,
			SsdCacheSizeBytes = 65536 * 4
		}))
		{
			Assert.StartsWith("RAMDisk", withRamDisk.GetStatsSnapshot().L1BackendSummary);
		}

		using (HybridCacheEngine withoutRamDisk = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamDiskCacheDirectory = null,
			L1BackendMode = L1BackendMode.AutoPreferRamDisk,
			RamCacheSizeBytes = 65536 * 4,
			SsdCacheSizeBytes = 65536 * 4
		}))
		{
			Assert.Equal("In-Process RAM", withoutRamDisk.GetStatsSnapshot().L1BackendSummary);
		}
	}

	[Fact]
	public void RamDiskFileCacheTier_SupportsReadWriteEvictionAndCleansUpOnDispose()
	{
		const int blockSize = 65536;
		string expectedFile;

		using (RamDiskFileCacheTier l1 = new RamDiskFileCacheTier(_ramDiskDir, "unit_l1", blockSize * 2, blockSize))
		{
			expectedFile = l1.CacheFilePath;
			Assert.True(File.Exists(expectedFile));

			byte[] b0 = Enumerable.Repeat((byte)0x1A, blockSize).ToArray();
			byte[] b1 = Enumerable.Repeat((byte)0x2B, blockSize).ToArray();
			byte[] b2 = Enumerable.Repeat((byte)0x3C, blockSize).ToArray();

			Assert.Null(l1.Put(new BlockKey("a.bin", 0), b0, blockSize, false, blockSize * 3, 100, 3.0));
			Assert.Null(l1.Put(new BlockKey("a.bin", 1), b1, blockSize, false, blockSize * 3, 100, 3.0));

			byte[] slice = new byte[128];
			Assert.True(l1.TryReadSlice(new BlockKey("a.bin", 0), 64, slice, blockSize * 3, 100, out int copied, out _, out _));
			Assert.Equal(128, copied);
			Assert.All(slice, b => Assert.Equal((byte)0x1A, b));

			// Reading block 0 promoted it to MRU, so inserting block 2 must evict block 1.
			RamBlockCopy? evicted = l1.Put(new BlockKey("a.bin", 2), b2, blockSize, true, blockSize * 3, 100, 4.0);
			Assert.NotNull(evicted);
			Assert.Equal(1, evicted!.Key.BlockIndex);
			Assert.All(evicted.Data, b => Assert.Equal((byte)0x2B, b));
		}

		Assert.False(File.Exists(expectedFile));
	}

	[Fact]
	public void Stop_DemotesWarmL1BlocksToL2Ssd_SoRestartStartsWarm()
	{
		const int blockSize = 65536;
		byte[] payload = new byte[blockSize];
		new Random(77).NextBytes(payload);
		File.WriteAllBytes(Path.Combine(_targetDir, "warm.bin"), payload);

		// RamPromoteScore = 1.0 so the very first read goes straight to L1 RAMDisk without touching L2 yet.
		CacheOptions opts = new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamDiskCacheDirectory = _ramDiskDir,
			L1BackendMode = L1BackendMode.RamDiskFile,
			RamCacheSizeBytes = blockSize * 4,
			SsdCacheSizeBytes = blockSize * 4,
			SsdPromoteScore = 1.0,
			RamPromoteScore = 1.0,
			DemoteL1ToL2OnStop = true
		};

		using (HybridCacheEngine first = new HybridCacheEngine(opts))
		{
			first.Start();
			byte[] tmp = new byte[blockSize];
			first.ReadBlockRange("warm.bin", 0, tmp);
			Assert.Equal(1, first.GetStatsSnapshot().RamCachedBlocks);
			Assert.Equal(0, first.GetStatsSnapshot().SsdCachedBlocks);
			first.Stop();
			Assert.Equal(1, first.GetStatsSnapshot().SsdCachedBlocks);
		}

		using (HybridCacheEngine second = new HybridCacheEngine(opts))
		{
			second.Start();
			Assert.Equal(1, second.GetStatsSnapshot().SsdCachedBlocks);
			byte[] restored = new byte[blockSize];
			second.ReadBlockRange("warm.bin", 0, restored);
			Assert.True(restored.AsSpan().SequenceEqual(payload));
			Assert.Equal(1, second.GetStatsSnapshot().SsdHits);
			Assert.Equal(0, second.GetStatsSnapshot().HddMisses);
		}
	}

	[Fact]
	public void InMemoryVolumeStore_SupportsCrudResizeRenameDeleteAndWildcardEnumeration()
	{
		InMemoryVolumeStore store = new InMemoryVolumeStore(4 * 1024 * 1024);
		store.CreateEntry(@"\docs", isDirectory: true, (uint)FileAttributes.Directory, null, 0);
		store.CreateEntry(@"\docs\hello.txt", isDirectory: false, (uint)FileAttributes.Archive, null, 0);
		store.CreateEntry(@"\docs\note.md", isDirectory: false, (uint)FileAttributes.Archive, null, 0);

		byte[] msg = System.Text.Encoding.UTF8.GetBytes("HyperCache X RAMDisk!");
		store.WriteFile(@"\docs\hello.txt", 0, msg, writeToEndOfFile: false, constrainedIo: false, out uint written);
		Assert.Equal((uint)msg.Length, written);

		byte[] read = new byte[msg.Length];
		int actual = store.ReadFile(@"\docs\hello.txt", 0, read, out bool eof);
		Assert.False(eof);
		Assert.Equal(msg.Length, actual);
		Assert.True(read.AsSpan().SequenceEqual(msg));

		var txtEntries = store.EnumerateDirectory(@"\docs", "*.txt");
		Assert.Contains(txtEntries, e => e.Name == "hello.txt");
		Assert.DoesNotContain(txtEntries, e => e.Name == "note.md");

		store.Rename(@"\docs\hello.txt", @"\docs\renamed.txt", replaceIfExists: false);
		Assert.False(store.Exists(@"\docs\hello.txt"));
		Assert.True(store.Exists(@"\docs\renamed.txt"));

		store.SetFileSize(@"\docs\renamed.txt", 10, setAllocationSize: false);
		Assert.True(store.TryGetInfo(@"\docs\renamed.txt", out InMemoryFileInfo info, out _));
		Assert.Equal(10UL, info.FileSize);

		Assert.True(store.Delete(@"\docs\renamed.txt"));
		Assert.False(store.Exists(@"\docs\renamed.txt"));
	}

	[Fact]
	public void InMemoryVolumeStore_EnforcesCapacityAndReclaimsPagesOnDelete()
	{
		const long capacity = InMemoryVolumeStore.PageSize * 4; // 256 KB
		InMemoryVolumeStore store = new InMemoryVolumeStore(capacity);
		store.CreateEntry(@"\big.bin", isDirectory: false, (uint)FileAttributes.Archive, null, 0);

		byte[] chunk = new byte[InMemoryVolumeStore.PageSize * 3];
		new Random(1).NextBytes(chunk);
		store.WriteFile(@"\big.bin", 0, chunk, false, false, out _);
		Assert.Equal(InMemoryVolumeStore.PageSize * 3, store.GetStats().UsedBytes);

		// Writing 2 more pages (total 5 > 4) must throw ERROR_DISK_FULL (0x80070070).
		byte[] overflow = new byte[InMemoryVolumeStore.PageSize * 2];
		IOException ex = Assert.Throws<IOException>(() =>
			store.WriteFile(@"\big.bin", (ulong)chunk.Length, overflow, false, false, out _));
		Assert.Equal(unchecked((int)0x80070070), ex.HResult);

		// Deleting file reclaims all pages.
		Assert.True(store.Delete(@"\big.bin"));
		Assert.Equal(0, store.GetStats().UsedBytes);
		Assert.Equal(capacity, store.GetStats().FreeBytes);
	}

	[Fact]
	public void InMemoryVolumeStore_HandlesMultiPage64KBoundaryIo()
	{
		InMemoryVolumeStore store = new InMemoryVolumeStore(2 * 1024 * 1024);
		store.CreateEntry(@"\span.bin", isDirectory: false, (uint)FileAttributes.Archive, null, 0);

		byte[] payload = new byte[InMemoryVolumeStore.PageSize * 2 + 137];
		new Random(123).NextBytes(payload);
		store.WriteFile(@"\span.bin", 512, payload, false, false, out uint written);
		Assert.Equal((uint)payload.Length, written);

		byte[] read = new byte[payload.Length];
		int actual = store.ReadFile(@"\span.bin", 512, read, out bool eof);
		Assert.False(eof);
		Assert.Equal(payload.Length, actual);
		Assert.True(read.AsSpan().SequenceEqual(payload));
	}

	[Fact]
	public void DiskUtility_FindPreferredRamDisk_PrioritizesLargestRamDisk()
	{
		List<DiskInfo> mock = new List<DiskInfo>
		{
			new DiskInfo { DriveLetter = "C:", MediaKind = StorageMediaKind.Ssd, FreeBytes = 500_000_000_000 },
			new DiskInfo { DriveLetter = "D:", MediaKind = StorageMediaKind.Hdd, FreeBytes = 4_000_000_000_000 },
			new DiskInfo { DriveLetter = "M:", MediaKind = StorageMediaKind.RamDisk, FreeBytes = 2_000_000_000 },
			new DiskInfo { DriveLetter = "R:", MediaKind = StorageMediaKind.RamDisk, FreeBytes = 15_000_000_000 }
		};

		DiskInfo? bestRam = DiskUtility.FindPreferredRamDisk(mock);
		Assert.NotNull(bestRam);
		Assert.Equal("R:", bestRam!.DriveLetter);

		DiskInfo? bestSsd = DiskUtility.FindPreferredSsd(mock);
		Assert.NotNull(bestSsd);
		Assert.Equal("C:", bestSsd!.DriveLetter);
	}
}
