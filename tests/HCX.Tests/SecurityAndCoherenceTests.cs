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

	[Fact]
	public void HcxFileSystem_And_MemoryFileSystem_RejectNullNativeBufferOnReadWrite()
	{
		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			RamCacheSizeBytes = 65536 * 2,
			SsdCacheSizeBytes = 65536 * 2
		});
		HcxFileSystem fs = new HcxFileSystem(engine);
		fs.Create(@"\nullbuf.bin", 0, 0x40000000, (uint)FileAttributes.Archive, null!, 0, out object node, out object desc, out _, out _);
		try
		{
			IOException exRead = Assert.Throws<IOException>(() => fs.Read(node, desc, IntPtr.Zero, 0, 16, out _));
			Assert.Equal(unchecked((int)0x80070057), exRead.HResult);
			IOException exWrite = Assert.Throws<IOException>(() => fs.Write(node, desc, IntPtr.Zero, 0, 16, false, false, out _, out _));
			Assert.Equal(unchecked((int)0x80070057), exWrite.HResult);
		}
		finally
		{
			fs.Close(node, desc);
		}

		InMemoryVolumeStore store = new InMemoryVolumeStore(1024 * 1024);
		MemoryFileSystem mfs = new MemoryFileSystem(store);
		mfs.Create(@"\memnull.bin", 0, 0x40000000, (uint)FileAttributes.Archive, null!, 0, out object mNode, out object mDesc, out _, out _);
		IOException mRead = Assert.Throws<IOException>(() => mfs.Read(mNode, mDesc, IntPtr.Zero, 0, 16, out _));
		Assert.Equal(unchecked((int)0x80070057), mRead.HResult);
		IOException mWrite = Assert.Throws<IOException>(() => mfs.Write(mNode, mDesc, IntPtr.Zero, 0, 16, false, false, out _, out _));
		Assert.Equal(unchecked((int)0x80070057), mWrite.HResult);
		mfs.Close(mNode, mDesc);
	}

	[Fact]
	public void DrainDeferredEvictions_IsolatesBackingIoFailure_WithoutCrashingUnrelatedCaller()
	{
		const int blockSize = 4096;
		// Force a directory at the backing path of victim.bin so opening it as a file fails during eviction flush.
		Directory.CreateDirectory(Path.Combine(_targetDir, "victim.bin"));

		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			BlockSizeBytes = blockSize,
			RamCacheSizeBytes = blockSize, // 1 block L1
			SsdCacheSizeBytes = 0,         // L2 off so eviction tries direct HDD flush
			WritePolicy = WritePolicy.WriteBack,
			FlushInterval = TimeSpan.FromMinutes(10)
		});
		Assert.True(engine.Start());

		byte[] victimPayload = Enumerable.Repeat((byte)0x5A, blockSize).ToArray();
		engine.WriteBlockRange("victim.bin", 0, victimPayload);

		// Writing an unrelated file evicts victim.bin; DrainDeferredEvictions must catch backing failure without throwing here.
		byte[] otherPayload = Enumerable.Repeat((byte)0xA5, blockSize).ToArray();
		int written = engine.WriteBlockRange("healthy.bin", 0, otherPayload);
		Assert.Equal(blockSize, written);

		byte[] readHealthy = new byte[blockSize];
		Assert.Equal(blockSize, engine.ReadBlockRange("healthy.bin", 0, readHealthy));
		Assert.True(readHealthy.AsSpan().SequenceEqual(otherPayload));

		// Victim stays in _inFlightEvictions and remains readable despite backing failure.
		Assert.True(engine.GetStatsSnapshot().DirtyBlocks >= 1);
		byte[] readVictim = new byte[blockSize];
		Assert.Equal(blockSize, engine.ReadBlockRange("victim.bin", 0, readVictim));
		Assert.True(readVictim.AsSpan().SequenceEqual(victimPayload));

		// Remove the directory blocker so Dispose/SyncAll can flush cleanly without lingering test state.
		Directory.Delete(Path.Combine(_targetDir, "victim.bin"));
		engine.SyncAll();
	}

	[Fact]
	public void AppSettingsManager_SanitizesTamperedJson_AndRejectsUnsafeAutoStartPath()
	{
		string path = Path.Combine(_tempRoot, "tampered.json");
		string tamperedJson = """
		{
		  "TargetPath": "\\\\.\\PhysicalDrive0",
		  "L1BackendMode": "BogusMode",
		  "RamSizeMb": -500,
		  "PreferredRamDiskRoot": "\\\\?\\Volume{1234}",
		  "SsdRoot": "C:\\.hcx_bad\u0000null",
		  "SsdSizeMb": 99999999,
		  "WritePolicy": "EvilPolicy",
		  "PerformanceModeIndex": 42,
		  "RamDiskLabel": "BAD\n\r\tLABEL_123456789012345678901234567890"
		}
		""";
		File.WriteAllText(path, tamperedJson);

		AppSettings loaded = AppSettingsManager.Load(path);
		Assert.Null(loaded.TargetPath);
		Assert.Null(loaded.PreferredRamDiskRoot);
		Assert.Null(loaded.SsdRoot);
		Assert.Equal(nameof(L1BackendMode.AutoPreferRamDisk), loaded.L1BackendMode);
		Assert.Equal(nameof(WritePolicy.WriteBack), loaded.WritePolicy);
		Assert.Equal(0L, loaded.RamSizeMb);
		Assert.Equal(1_048_576L, loaded.SsdSizeMb);
		Assert.Equal(2, loaded.PerformanceModeIndex);
		Assert.True(loaded.RamDiskLabel.Length <= 32);
		Assert.DoesNotContain("\n", loaded.RamDiskLabel);

		Assert.False(AppSettingsManager.SetWindowsAutoStart(true, @"C:\bad""quote\HCX.App.exe"));
		Assert.False(AppSettingsManager.SetWindowsAutoStart(true, "relative.exe"));
		Assert.False(AppSettingsManager.SetWindowsAutoStart(true, @"\\unc\share\HCX.App.exe"));
	}

	[Fact]
	public void LiveRamDiskCacheBenchmark_WhenRamDiskExists_VerifiesDataIntegrity()
	{
		DiskInfo? ramDisk = DiskUtility.FindPreferredRamDisk();
		BenchmarkResult result = CacheBenchmarkRunner.Run(
			_targetDir,
			_ssdDir,
			32L * 1024 * 1024,
			64L * 1024 * 1024,
			null,
			ramDisk?.RootPath);

		Assert.True(result.VerifiedIntegrity);
		Assert.True(result.RamHits > 0);
	}

	[Fact]
	public void HcxFileSystem_BlocksDirectoryJunctionEscapingTargetRoot()
	{
		string outsideDir = Path.Combine(_tempRoot, "outside_secret_dir");
		Directory.CreateDirectory(outsideDir);
		File.WriteAllText(Path.Combine(outsideDir, "secret.txt"), "top-secret-junction");

		string junctionPath = Path.Combine(_targetDir, "escape_junction");
		var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{outsideDir}\"")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		using (System.Diagnostics.Process? p = System.Diagnostics.Process.Start(psi))
		{
			p?.WaitForExit(5000);
			Assert.True(p != null && p.ExitCode == 0 && Directory.Exists(junctionPath), "Failed to create directory junction for test.");
		}

		try
		{
			using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
			{
				TargetRootPath = _targetDir,
				SsdCacheDirectory = _ssdDir,
				RamCacheSizeBytes = 65536 * 2,
				SsdCacheSizeBytes = 65536 * 2
			});
			HcxFileSystem fs = new HcxFileSystem(engine);

			byte[]? sd = null;
			IOException exSec = Assert.Throws<IOException>(() =>
				fs.GetSecurityByName(@"\escape_junction\secret.txt", out _, ref sd!));
			Assert.Equal(unchecked((int)0x80070005), exSec.HResult);

			IOException exOpen = Assert.Throws<IOException>(() =>
				fs.Open(@"\escape_junction\secret.txt", 0, 0x80000000, out _, out _, out _, out _));
			Assert.Equal(unchecked((int)0x80070005), exOpen.HResult);
		}
		finally
		{
			try
			{
				if (Directory.Exists(junctionPath))
				{
					Directory.Delete(junctionPath, recursive: false);
				}
			}
			catch
			{
			}
		}
	}

	[Fact]
	public void AppSettingsManager_PersistsAndSanitizesMultiSessionAndRamDiskProfiles()
	{
		string settingsPath = Path.Combine(_tempRoot, "multi_profiles.json");
		AppSettings raw = new AppSettings
		{
			TargetPath = _targetDir,
			FlushIntervalSeconds = 0, // below min 1 -> clamped to 1
			AutoRestoreSession = true,
			SavedSessions =
			{
				new SessionProfile
				{
					TargetPath = _targetDir,
					L1BackendMode = "InProcessRam",
					RamSizeMb = 2048,
					SsdRoot = _ssdDir,
					SsdSizeMb = 4096,
					WritePolicy = "WriteThrough",
					MountPoint = "x:",
					PerformanceModeIndex = 2,
					FlushIntervalSeconds = 9999 // clamped to 300
				},
				new SessionProfile
				{
					TargetPath = @"\\.\PhysicalDrive0", // unsafe -> dropped
					RamSizeMb = 512
				},
				new SessionProfile
				{
					TargetPath = Path.Combine(_targetDir, ".hcx"), // internal cache -> dropped
					RamSizeMb = 512
				}
			},
			SavedRamDisks =
			{
				new RamDiskProfile
				{
					MountPoint = "m:",
					CapacityMb = 1024,
					VolumeLabel = "MY\nRAM\tDISK"
				},
				new RamDiskProfile
				{
					MountPoint = "INVALID", // dropped
					CapacityMb = 1024
				}
			}
		};

		AppSettingsManager.Save(raw, settingsPath);
		AppSettings loaded = AppSettingsManager.Load(settingsPath);

		Assert.Equal(1, loaded.FlushIntervalSeconds);
		Assert.Single(loaded.SavedSessions);
		Assert.Equal(_targetDir, loaded.SavedSessions[0].TargetPath);
		Assert.Equal("WriteThrough", loaded.SavedSessions[0].WritePolicy);
		Assert.Equal("X:", loaded.SavedSessions[0].MountPoint);
		Assert.Equal(300, loaded.SavedSessions[0].FlushIntervalSeconds);

		Assert.Single(loaded.SavedRamDisks);
		Assert.Equal("M:", loaded.SavedRamDisks[0].MountPoint);
		Assert.Equal(1024, loaded.SavedRamDisks[0].CapacityMb);
		Assert.Equal("MYRAMDISK", loaded.SavedRamDisks[0].VolumeLabel);
	}
}
