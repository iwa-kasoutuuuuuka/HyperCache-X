using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HCX.Core;

/// <summary>
/// Runs an end-to-end self-verification benchmark against a real temporary backing file and 3-tier cache.
/// </summary>
public static class CacheBenchmarkRunner
{
	public static BenchmarkResult Run(
		string targetBaseDir,
		string ssdCacheBaseDir,
		long ramSizeBytes,
		long ssdSizeBytes,
		Action<string>? log = null,
		string? ramDiskBaseDir = null)
	{
		string benchId = Guid.NewGuid().ToString("N")[..8];
		string benchTarget = Path.Combine(targetBaseDir, $".hcx_bench_target_{benchId}");
		string benchSsd = Path.Combine(ssdCacheBaseDir, $".hcx_bench_ssd_{benchId}");
		string? benchRamDisk = string.IsNullOrWhiteSpace(ramDiskBaseDir)
			? null
			: Path.Combine(ramDiskBaseDir, $".hcx_bench_ram_{benchId}");
		Directory.CreateDirectory(benchTarget);
		Directory.CreateDirectory(benchSsd);
		if (benchRamDisk != null)
		{
			Directory.CreateDirectory(benchRamDisk);
		}

		try
		{
			const int blockSize = 65536;
			const int totalBlocks = 128; // 8 MB test file
			string relFile = "bench_payload.bin";
			string fullFile = Path.Combine(benchTarget, relFile);

			byte[] seed = new byte[totalBlocks * blockSize];
			new Random(42).NextBytes(seed);
			File.WriteAllBytes(fullFile, seed);
			log?.Invoke($"[Bench] 8 MB テストファイルを生成しました: {fullFile}");

			CacheOptions options = new CacheOptions
			{
				TargetRootPath = benchTarget,
				SsdCacheDirectory = benchSsd,
				RamDiskCacheDirectory = benchRamDisk,
				L1BackendMode = benchRamDisk != null ? L1BackendMode.AutoPreferRamDisk : L1BackendMode.InProcessRam,
				RamCacheSizeBytes = ramSizeBytes,
				SsdCacheSizeBytes = ssdSizeBytes,
				WritePolicy = WritePolicy.WriteBack,
				SequentialThreshold = 8,
				SsdPromoteScore = 1.2,
				RamPromoteScore = 2.0
			};

			using HybridCacheEngine engine = new HybridCacheEngine(options);
			engine.Start();

			byte[] readBuf = new byte[blockSize];
			int[] hotBlocks = { 2, 17, 35, 51, 77, 99, 111, 125 };

			// 1. Cold pass (HDD baseline)
			Stopwatch swCold = Stopwatch.StartNew();
			foreach (int b in hotBlocks)
			{
				engine.ReadBlockRange(relFile, (long)b * blockSize, readBuf);
			}
			swCold.Stop();

			// Warm up AI threshold (2nd access promotes to L2/L1)
			foreach (int b in hotBlocks)
			{
				engine.ReadBlockRange(relFile, (long)b * blockSize, readBuf);
			}

			// 2. Hot pass (80 random block reads across hot set -> L1/L2 hits)
			const int hotIterations = 80;
			Stopwatch swHot = Stopwatch.StartNew();
			bool integrityOk = true;
			for (int i = 0; i < hotIterations; i++)
			{
				int b = hotBlocks[i % hotBlocks.Length];
				int n = engine.ReadBlockRange(relFile, (long)b * blockSize, readBuf);
				if (n != blockSize || !readBuf.AsSpan().SequenceEqual(seed.AsSpan(b * blockSize, blockSize)))
				{
					integrityOk = false;
				}
			}
			swHot.Stop();

			// 3. Sequential scan (should trigger Smart Filter bypass)
			for (int b = 0; b < 24; b++)
			{
				engine.ReadBlockRange(relFile, (long)b * blockSize, readBuf);
			}

			// 4. Write-Back test + SyncAll + byte-level verification
			byte[] writePayload = new byte[blockSize];
			new Random(99).NextBytes(writePayload);
			Stopwatch swWrite = Stopwatch.StartNew();
			engine.WriteBlockRange(relFile, 5L * blockSize, writePayload);
			swWrite.Stop();

			engine.SyncAll();
			byte[] diskVerify = new byte[blockSize];
			using (FileStream fs = File.OpenRead(fullFile))
			{
				fs.Seek(5L * blockSize, SeekOrigin.Begin);
				fs.ReadExactly(diskVerify, 0, blockSize);
			}
			if (!diskVerify.AsSpan().SequenceEqual(writePayload))
			{
				integrityOk = false;
			}

			CacheStatsSnapshot snap = engine.GetStatsSnapshot();
			engine.Stop();

			double coldBytesMb = (double)hotBlocks.Length * blockSize / (1024.0 * 1024.0);
			double hotBytesMb = (double)hotIterations * blockSize / (1024.0 * 1024.0);
			double writeBytesMb = (double)blockSize / (1024.0 * 1024.0);

			double coldMs = Math.Max(0.01, swCold.Elapsed.TotalMilliseconds);
			double hotMs = Math.Max(0.01, swHot.Elapsed.TotalMilliseconds);
			double writeMs = Math.Max(0.01, swWrite.Elapsed.TotalMilliseconds);

			double coldMBps = coldBytesMb / (coldMs / 1000.0);
			double hotMBps = hotBytesMb / (hotMs / 1000.0);
			double writeMBps = writeBytesMb / (writeMs / 1000.0);

			string summary =
				$"[Bench] Cold: {coldMBps:F1} MB/s ({coldMs:F2} ms) | Hot(L1/L2): {hotMBps:F1} MB/s ({hotMs:F2} ms) | " +
				$"WriteBack: {writeMBps:F1} MB/s | HitRate: {snap.HitRatePercent:F1}% (RAM:{snap.RamHits}, SSD:{snap.SsdHits}) | " +
				$"SeqBypass: {snap.SequentialBypasses} | 整合性: {(integrityOk ? "OK" : "NG")}";
			log?.Invoke(summary);

			return new BenchmarkResult
			{
				ColdReadMs = coldMs,
				ColdReadMBps = coldMBps,
				HotReadMs = hotMs,
				HotReadMBps = hotMBps,
				WriteBackMs = writeMs,
				WriteBackMBps = writeMBps,
				HitRatePercent = snap.HitRatePercent,
				RamHits = snap.RamHits,
				SsdHits = snap.SsdHits,
				SequentialBypasses = snap.SequentialBypasses,
				VerifiedIntegrity = integrityOk,
				Summary = summary
			};
		}
		finally
		{
			try { Directory.Delete(benchTarget, recursive: true); } catch { }
			try { Directory.Delete(benchSsd, recursive: true); } catch { }
			if (benchRamDisk != null)
			{
				try { Directory.Delete(benchRamDisk, recursive: true); } catch { }
			}
		}
	}
}
