using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using HCX.Core;
using HCX.VirtualFs;
using Xunit;

namespace HCX.Tests;

public sealed class FeatureExpansionTests : IDisposable
{
	private readonly string _tempRoot;
	private readonly string _targetDir;
	private readonly string _ssdDir;

	public FeatureExpansionTests()
	{
		_tempRoot = Path.Combine(Path.GetTempPath(), $"HCX_FeatTests_{Guid.NewGuid():N}");
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
	public void CacheFilterRules_PresetsAndPinning_BypassMediaAndPrioritizeHotExtensions()
	{
		CacheFilterRules rules = new CacheFilterRules(WorkloadPreset.Gaming);
		Assert.Equal(CacheFilterAction.Bypass, rules.Evaluate(@"movies\intro.mp4"));
		Assert.Equal(CacheFilterAction.PriorityL1, rules.Evaluate(@"paks\level01.pak"));

		rules.ApplyPreset(WorkloadPreset.Development);
		Assert.Equal(CacheFilterAction.PriorityL1, rules.Evaluate(@"src\Program.cs"));
		Assert.Equal(CacheFilterAction.Bypass, rules.Evaluate(@"archives\sdk.zip"));

		rules.PinPath(@"custom\pinned_dir");
		Assert.Equal(CacheFilterAction.Pinned, rules.Evaluate(@"custom\pinned_dir\movie.mp4"));

		// Verify live engine bypasses .mp4 and immediately promotes .pak to L1 on first read.
		const int blockSize = 4096;
		byte[] mediaBytes = Enumerable.Repeat((byte)0x55, blockSize * 2).ToArray();
		byte[] pakBytes = Enumerable.Repeat((byte)0xAA, blockSize).ToArray();
		File.WriteAllBytes(Path.Combine(_targetDir, "video.mp4"), mediaBytes);
		File.WriteAllBytes(Path.Combine(_targetDir, "game.pak"), pakBytes);

		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			BlockSizeBytes = blockSize,
			RamCacheSizeBytes = 16 * blockSize,
			SsdCacheSizeBytes = 16 * blockSize,
			WorkloadPreset = WorkloadPreset.Gaming,
			EnableReadAhead = false
		});
		engine.Start();

		byte[] buf = new byte[blockSize * 2];
		engine.ReadBlockRange("video.mp4", 0, buf);
		Assert.Equal(0, engine.GetStatsSnapshot().RamCachedBlocks);

		byte[] pakBuf = new byte[blockSize];
		engine.ReadBlockRange("game.pak", 0, pakBuf);
		Assert.Equal(1, engine.GetStatsSnapshot().RamCachedBlocks);

		// Second read of game.pak must hit L1 RAM immediately.
		long beforeHits = engine.GetStatsSnapshot().RamHits;
		engine.ReadBlockRange("game.pak", 0, pakBuf);
		Assert.True(engine.GetStatsSnapshot().RamHits > beforeHits);
	}

	[Fact]
	public void WarmPath_PreWarmsDirectoryAndPinsFilesIntoL1Cache()
	{
		const int blockSize = 4096;
		string sub = Path.Combine(_targetDir, "assets");
		Directory.CreateDirectory(sub);
		byte[] a1 = Enumerable.Repeat((byte)0x11, blockSize * 2).ToArray();
		byte[] a2 = Enumerable.Repeat((byte)0x22, blockSize).ToArray();
		File.WriteAllBytes(Path.Combine(sub, "one.dat"), a1);
		File.WriteAllBytes(Path.Combine(sub, "two.dat"), a2);

		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			BlockSizeBytes = blockSize,
			RamCacheSizeBytes = 16 * blockSize,
			SsdCacheSizeBytes = 16 * blockSize,
			EnableReadAhead = false
		});
		engine.Start();

		WarmResult warm = engine.WarmPath("assets", maxBytes: 1024 * 1024, pin: true);
		Assert.Equal(2, warm.FilesWarmed);
		Assert.Equal(3, warm.BlocksWarmed);
		Assert.Equal(blockSize * 3, warm.BytesWarmed);
		Assert.True(warm.Pinned);
		Assert.Contains("assets", engine.FilterRules.GetPinnedPaths());

		byte[] readBuf = new byte[blockSize * 2];
		int read = engine.ReadBlockRange(@"assets\one.dat", 0, readBuf);
		Assert.Equal(a1.Length, read);
		Assert.True(readBuf.AsSpan().SequenceEqual(a1));
		Assert.True(engine.GetStatsSnapshot().RamHits >= 2);
		Assert.Equal(0, engine.GetStatsSnapshot().HddMisses);
	}

	[Fact]
	public void IdleFlush_And_DynamicSetWritePolicy_FlushDirtyBlocksSafely()
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
			FlushInterval = TimeSpan.FromMinutes(10),
			EnableIdleFlush = true,
			IdleFlushDelay = TimeSpan.FromMilliseconds(500)
		});
		engine.Start();

		byte[] payload1 = Enumerable.Repeat((byte)0x7A, blockSize).ToArray();
		engine.WriteBlockRange("idle.bin", 0, payload1);
		Assert.True(engine.GetStatsSnapshot().DirtyBlocks >= 1);

		// Idle flush should flush within ~2.5s even though FlushInterval is 10 minutes.
		bool idleFlushed = SpinWait.SpinUntil(() => engine.GetStatsSnapshot().DirtyBlocks == 0, TimeSpan.FromSeconds(4));
		Assert.True(idleFlushed, "Expected idle flush worker to sync dirty blocks when I/O became idle.");
		Assert.True(File.ReadAllBytes(Path.Combine(_targetDir, "idle.bin")).AsSpan().SequenceEqual(payload1));

		// Dynamic switch to WriteThrough flushes immediately and persists subsequent writes directly.
		byte[] payload2 = Enumerable.Repeat((byte)0x9C, blockSize).ToArray();
		engine.WriteBlockRange("safe.bin", 0, payload2);
		int synced = engine.SetWritePolicy(WritePolicy.WriteThrough);
		Assert.True(synced >= 1);
		Assert.Equal(WritePolicy.WriteThrough, engine.EffectiveWritePolicy);
		Assert.True(File.ReadAllBytes(Path.Combine(_targetDir, "safe.bin")).AsSpan().SequenceEqual(payload2));
	}

	[Fact]
	public void HotFileTop10_TracksPerFileHitsAndMissesAccurately()
	{
		const int blockSize = 4096;
		File.WriteAllBytes(Path.Combine(_targetDir, "hot.dat"), new byte[blockSize]);
		File.WriteAllBytes(Path.Combine(_targetDir, "warm.dat"), new byte[blockSize]);

		using HybridCacheEngine engine = new HybridCacheEngine(new CacheOptions
		{
			TargetRootPath = _targetDir,
			SsdCacheDirectory = _ssdDir,
			BlockSizeBytes = blockSize,
			RamCacheSizeBytes = 8 * blockSize,
			SsdCacheSizeBytes = 8 * blockSize,
			EnableReadAhead = false
		});
		engine.Start();

		byte[] buf = new byte[blockSize];
		for (int i = 0; i < 6; i++) engine.ReadBlockRange("hot.dat", 0, buf);
		for (int i = 0; i < 2; i++) engine.ReadBlockRange("warm.dat", 0, buf);

		HotFileStat[] top = engine.GetStatsSnapshot().TopHotFiles;
		Assert.True(top.Length >= 2);
		Assert.Equal("hot.dat", top[0].RelativePath);
		Assert.True(top[0].Hits + top[0].Misses >= 6);
	}

	[Fact]
	public void InMemoryVolumeStore_SaveAndLoadDirectory_RoundTripsTreeAndRejectsRawDevice()
	{
		string backupDir = Path.Combine(_tempRoot, "ram_backup");
		InMemoryVolumeStore store1 = new InMemoryVolumeStore(4 * 1024 * 1024);
		store1.CreateEntry(@"\sub", isDirectory: true, (uint)FileAttributes.Directory, null, 0);
		store1.CreateEntry(@"\sub\data.bin", isDirectory: false, (uint)FileAttributes.Archive, null, 0);
		byte[] expected = new byte[InMemoryVolumeStore.PageSize + 777];
		new Random(1234).NextBytes(expected);
		store1.WriteFile(@"\sub\data.bin", 0, expected, false, false, out _);

		(int filesSaved, int dirsSaved, long bytesSaved) = store1.SaveToDirectory(backupDir);
		Assert.Equal(1, filesSaved);
		Assert.Equal(1, dirsSaved);
		Assert.Equal(expected.Length, bytesSaved);

		InMemoryVolumeStore store2 = new InMemoryVolumeStore(4 * 1024 * 1024);
		(int filesLoaded, int dirsLoaded, long bytesLoaded) = store2.LoadFromDirectory(backupDir);
		Assert.Equal(1, filesLoaded);
		Assert.Equal(1, dirsLoaded);
		Assert.Equal(expected.Length, bytesLoaded);

		byte[] actual = new byte[expected.Length];
		int read = store2.ReadFile(@"\sub\data.bin", 0, actual, out bool eof);
		Assert.False(eof);
		Assert.Equal(expected.Length, read);
		Assert.True(actual.AsSpan().SequenceEqual(expected));

		Assert.Throws<ArgumentException>(() => store2.SaveToDirectory(@"\\.\PhysicalDrive0"));
	}

	[Fact]
	public void HcxControlPipeIpc_ServerAndClient_RoundTripLiveStatsAndSync()
	{
		string pipeName = $"HyperCacheX_TestPipe_{Guid.NewGuid():N}";
		using HcxControlPipeServer server = new HcxControlPipeServer((action, _) => action switch
		{
			"ping" => new { version = "2.4.0", ok = true },
			"sync_all" => new { totalFlushedBlocks = 7 },
			_ => throw new ArgumentException($"Unsupported: {action}")
		}, pipeName);

		bool okPing = HcxControlPipeClient.TrySend("ping", null, out JsonNode? pingData, out string? pingErr, pipeName);
		Assert.True(okPing, pingErr);
		Assert.Equal("2.4.0", pingData?["version"]?.GetValue<string>());

		bool okSync = HcxControlPipeClient.TrySend("sync_all", null, out JsonNode? syncData, out string? syncErr, pipeName);
		Assert.True(okSync, syncErr);
		Assert.Equal(7, syncData?["totalFlushedBlocks"]?.GetValue<int>());
	}
}
