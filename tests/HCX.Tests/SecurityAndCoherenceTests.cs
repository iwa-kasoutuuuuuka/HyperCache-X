using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Threading.Tasks;
using HCX.Core;
using HCX.VirtualFs;
using Xunit;

namespace HCX.Tests;

public sealed class SecurityAndCoherenceTests : IDisposable
{
	private readonly string _tempRoot;
	private readonly string _targetDir;
	private readonly string _ssdDir;

	public SecurityAndCoherenceTests()
	{
		_tempRoot = Path.Combine(Path.GetTempPath(), $"HCX_SecTests_{Guid.NewGuid():N}");
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
	public void WriteThrough_InvalidatesExistingL2SsdBlock_EvenWhenSequentialBypassed()
	{
		const int blockSize = 65536;
		string rel = "wt_coherence.bin";
		string full = Path.Combine(_targetDir, rel);
		byte[] initial = Enumerable.Repeat((byte)0x11, 12 * blockSize).ToArray();
		File.WriteAllBytes(full, initial);

		// Force straight-to-L2 SSD by disabling L1 RAM, with SequentialThreshold = 2.
		CacheOptions opts = new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 0,
			SsdCacheSizeBytes = 16 * blockSize,
			WritePolicy = WritePolicy.WriteThrough,
			SequentialThreshold = 2,
			SsdPromoteScore = 1.2
		};

		using HybridCacheEngine engine = new HybridCacheEngine(opts);
		engine.Start();

		// Warm block 4 into L2 SSD with original 0x11 bytes.
		byte[] buf = new byte[blockSize];
		engine.ReadBlockRange(rel, 4L * blockSize, buf);
		Assert.Equal((byte)0x11, buf[0]);
		Assert.Equal(1, engine.GetStatsSnapshot().SsdCachedBlocks);

		// Perform sequential writes across blocks 0..4 so block 4 triggers sequential bypass!
		byte[] updated = Enumerable.Repeat((byte)0x7A, blockSize).ToArray();
		for (int b = 0; b <= 4; b++)
		{
			engine.WriteBlockRange(rel, (long)b * blockSize, updated);
		}

		// Reading block 4 MUST return 0x7A (never stale 0x11 from L2 SSD!).
		engine.ReadBlockRange(rel, 4L * blockSize, buf);
		Assert.All(buf, b => Assert.Equal((byte)0x7A, b));
	}

	[Fact]
	public async Task NotifyRename_ConcurrentCrossRenames_DoNotDeadlock()
	{
		CacheOptions opts = new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 65536 * 4,
			SsdCacheSizeBytes = 65536 * 4
		};

		using HybridCacheEngine engine = new HybridCacheEngine(opts);
		engine.Start();

		Task t1 = Task.Run(() =>
		{
			for (int i = 0; i < 200; i++)
			{
				engine.NotifyRename("alpha.bin", "beta.bin");
			}
		});
		Task t2 = Task.Run(() =>
		{
			for (int i = 0; i < 200; i++)
			{
				engine.NotifyRename("beta.bin", "alpha.bin");
			}
		});
		Task t3 = Task.Run(() =>
		{
			for (int i = 0; i < 200; i++)
			{
				engine.NotifyRename("alpha.bin", "ALPHA.bin");
			}
		});

		Task completed = await Task.WhenAny(Task.WhenAll(t1, t2, t3), Task.Delay(TimeSpan.FromSeconds(5)));
		Assert.True(completed.IsCompletedSuccessfully, "Concurrent NotifyRename deadlocked or failed.");
	}

	[Fact]
	public void MetadataManager_RejectsTamperedChecksum_ExcessiveCount_AndTraversalPaths()
	{
		string metaPath = Path.Combine(_ssdDir, "test_meta.dat");
		MetadataManager mgr = new MetadataManager(metaPath);

		// 1. Tampered SHA-256 checksum must be rejected.
		mgr.SaveMetadata(65536, true, new[]
		{
			new PersistedBlockEntry
			{
				RelativePath = "ok.bin",
				BlockIndex = 0,
				SlotIndex = 0,
				ValidLength = 65536,
				BackingFileSize = 65536,
				BackingLastWriteUtcTicks = 100,
				IsValid = true,
				IsDirty = false,
				AiScore = 2.0,
				LastAccessUtcTicks = DateTime.UtcNow.Ticks
			}
		});

		byte[] bytes = File.ReadAllBytes(metaPath);
		bytes[20] ^= 0xFF;
		File.WriteAllBytes(metaPath, bytes);
		var tampered = mgr.LoadMetadata(65536);
		Assert.False(tampered.Loaded);
		Assert.Empty(tampered.Entries);

		// 2. Traversal path (with valid checksum) must be rejected.
		mgr.SaveMetadata(65536, true, new[]
		{
			new PersistedBlockEntry
			{
				RelativePath = @"..\..\Windows\win.ini",
				BlockIndex = 0,
				SlotIndex = 0,
				ValidLength = 128,
				BackingFileSize = 128,
				BackingLastWriteUtcTicks = 100,
				IsValid = true,
				IsDirty = false,
				AiScore = 2.0,
				LastAccessUtcTicks = DateTime.UtcNow.Ticks
			}
		});
		var traversal = mgr.LoadMetadata(65536);
		Assert.False(traversal.Loaded);
		Assert.Empty(traversal.Entries);

		// 3. Forged count with valid checksum must be rejected without OOM.
		using (MemoryStream ms = new MemoryStream())
		using (BinaryWriter bw = new BinaryWriter(ms))
		{
			bw.Write(0x32584348u); // HCX2
			bw.Write(2u);
			bw.Write(65536);
			bw.Write((byte)1);
			bw.Write(500_000_000); // absurd count
			byte[] payload = ms.ToArray();
			byte[] hash = System.Security.Cryptography.SHA256.HashData(payload);
			using FileStream fs = File.Create(metaPath);
			fs.Write(payload);
			fs.Write(hash);
		}
		var forgedCount = mgr.LoadMetadata(65536);
		Assert.False(forgedCount.Loaded);
		Assert.Empty(forgedCount.Entries);
	}

	[Fact]
	public void HcxFileSystem_BlocksPathTraversal_AndInternalHcxFolder()
	{
		string outsideFile = Path.Combine(_tempRoot, "secret_outside.txt");
		File.WriteAllText(outsideFile, "top-secret");

		Directory.CreateDirectory(Path.Combine(_targetDir, ".hcx"));
		File.WriteAllText(Path.Combine(_targetDir, ".hcx", "internal.bin"), "internal");

		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 65536 * 2,
			SsdCacheSizeBytes = 65536 * 2
		});
		HcxFileSystem fs = new HcxFileSystem(engine);

		byte[]? sd = null;
		IOException exTraversal = Assert.Throws<IOException>(() =>
			fs.GetSecurityByName(@"\..\secret_outside.txt", out _, ref sd!));
		Assert.Equal(unchecked((int)0x80070005), exTraversal.HResult); // ERROR_ACCESS_DENIED

		IOException exInternal = Assert.Throws<IOException>(() =>
			fs.GetSecurityByName(@"\.hcx\internal.bin", out _, ref sd!));
		Assert.Equal(unchecked((int)0x80070005), exInternal.HResult);
	}

	[Fact]
	public void InMemoryVolumeStore_BlocksDirectoryCycleRename_AndOverflow_AndBadSd()
	{
		InMemoryVolumeStore store = new InMemoryVolumeStore(4 * 1024 * 1024);
		store.CreateEntry(@"\parent", isDirectory: true, (uint)FileAttributes.Directory, null, 0);
		store.CreateEntry(@"\parent\child", isDirectory: true, (uint)FileAttributes.Directory, null, 0);
		store.CreateEntry(@"\file.bin", isDirectory: false, (uint)FileAttributes.Archive, null, 0);

		// Moving \parent into \parent\child\parent must throw ERROR_INVALID_PARAMETER (0x80070057).
		IOException cycleEx = Assert.Throws<IOException>(() =>
			store.Rename(@"\parent", @"\parent\child\parent", replaceIfExists: false));
		Assert.Equal(unchecked((int)0x80070057), cycleEx.HResult);

		// Offset > long.MaxValue must throw ERROR_INVALID_PARAMETER.
		IOException overflowRead = Assert.Throws<IOException>(() =>
			store.ReadFile(@"\file.bin", (ulong)long.MaxValue + 10, new byte[16], out _));
		Assert.Equal(unchecked((int)0x80070057), overflowRead.HResult);

		IOException overflowWrite = Assert.Throws<IOException>(() =>
			store.WriteFile(@"\file.bin", (ulong)long.MaxValue - 2, new byte[16], false, false, out _));
		Assert.Equal(unchecked((int)0x80070057), overflowWrite.HResult);

		// Malformed security descriptor must throw ERROR_INVALID_SECURITY_DESCR (0x8007053A).
		IOException badSd = Assert.Throws<IOException>(() =>
			store.SetSecurity(@"\file.bin", AccessControlSections.Access, new byte[] { 0xFF, 0x00, 0x01 }));
		Assert.Equal(unchecked((int)0x8007053A), badSd.HResult);
	}
}
