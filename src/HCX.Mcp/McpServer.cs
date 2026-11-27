using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HCX.Core;
using HCX.VirtualFs;

namespace HCX.Mcp;

/// <summary>
/// Dependency-free Model Context Protocol (MCP) JSON-RPC 2.0 stdio server for HyperCache X.
/// Compatible with Antigravity, Claude Code, and any standard MCP stdio client.
/// </summary>
public sealed class HcxMcpServer : IDisposable
{
	public const string ServerName = "hypercache-x";
	public const string ServerVersion = "2.4.0";
	private const int MaxActiveSessions = 8;

	private static readonly string[] SupportedProtocolVersions =
	{
		"2025-06-18",
		"2025-03-26",
		"2024-11-05"
	};

	private static readonly JsonSerializerOptions CompactJson = new JsonSerializerOptions
	{
		WriteIndented = false,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private static readonly JsonSerializerOptions PrettyResultJson = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private readonly ConcurrentDictionary<string, HybridCacheEngine> _sessions = new ConcurrentDictionary<string, HybridCacheEngine>(StringComparer.OrdinalIgnoreCase);
	private readonly TextWriter _stderr;
	private bool _disposed;

	public HcxMcpServer(TextWriter? stderr = null)
	{
		_stderr = stderr ?? Console.Error;
	}

	public int ActiveSessionCount => _sessions.Count;

	public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			string? line;
			try
			{
				line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}

			if (line == null)
			{
				break;
			}
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			string? response = ProcessMessage(line);
			if (response != null)
			{
				await output.WriteLineAsync(response).ConfigureAwait(false);
				await output.FlushAsync(cancellationToken).ConfigureAwait(false);
			}
		}
	}

	/// <summary>
	/// Processes a single JSON-RPC 2.0 message line and returns a serialized response line,
	/// or null for notifications that do not expect a response.
	/// </summary>
	public string? ProcessMessage(string jsonLine)
	{
		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse(jsonLine);
		}
		catch (JsonException ex)
		{
			return BuildErrorResponse(null, -32700, $"Parse error: {ex.Message}");
		}

		using (doc)
		{
			JsonElement root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return BuildErrorResponse(null, -32600, "Invalid Request: expected JSON object.");
			}

			bool hasId = root.TryGetProperty("id", out JsonElement idElem) && idElem.ValueKind != JsonValueKind.Undefined;
			JsonNode? idNode = hasId ? JsonNode.Parse(idElem.GetRawText()) : null;

			if (!root.TryGetProperty("method", out JsonElement methodElem) || methodElem.ValueKind != JsonValueKind.String)
			{
				return hasId ? BuildErrorResponse(idNode, -32600, "Invalid Request: missing method.") : null;
			}

			string method = methodElem.GetString() ?? string.Empty;
			JsonElement paramsElem = root.TryGetProperty("params", out JsonElement p) ? p : default;

			// Notifications (no id) must never produce a JSON-RPC response.
			if (!hasId)
			{
				return null;
			}

			try
			{
				return method switch
				{
					"initialize" => HandleInitialize(idNode, paramsElem),
					"ping" => BuildResultResponse(idNode, new JsonObject()),
					"tools/list" => HandleToolsList(idNode),
					"tools/call" => HandleToolsCall(idNode, paramsElem),
					"resources/list" => BuildResultResponse(idNode, new JsonObject { ["resources"] = new JsonArray() }),
					"prompts/list" => BuildResultResponse(idNode, new JsonObject { ["prompts"] = new JsonArray() }),
					_ => BuildErrorResponse(idNode, -32601, $"Method not found: {method}")
				};
			}
			catch (Exception ex)
			{
				_stderr.WriteLine($"[HCX.Mcp] Unhandled error in {method}: {ex.Message}");
				return BuildErrorResponse(idNode, -32603, $"Internal error: {ex.Message}");
			}
		}
	}

	private static string HandleInitialize(JsonNode? idNode, JsonElement paramsElem)
	{
		string negotiated = SupportedProtocolVersions[0];
		if (paramsElem.ValueKind == JsonValueKind.Object &&
		    paramsElem.TryGetProperty("protocolVersion", out JsonElement pv) &&
		    pv.ValueKind == JsonValueKind.String)
		{
			string requested = pv.GetString() ?? string.Empty;
			if (SupportedProtocolVersions.Contains(requested, StringComparer.Ordinal))
			{
				negotiated = requested;
			}
		}

		JsonObject result = new JsonObject
		{
			["protocolVersion"] = negotiated,
			["capabilities"] = new JsonObject
			{
				["tools"] = new JsonObject { ["listChanged"] = false }
			},
			["serverInfo"] = new JsonObject
			{
				["name"] = ServerName,
				["version"] = ServerVersion
			},
			["instructions"] = "HyperCache X (HCX) MCP server for inspecting drives/RAMDisks/WinFsp, running 3-tier cache benchmarks, managing in-process HybridCacheEngine sessions, and safely cleaning cache directories."
		};
		return BuildResultResponse(idNode, result);
	}

	private static string HandleToolsList(JsonNode? idNode)
	{
		JsonArray tools = new JsonArray
		{
			BuildToolSchema(
				"hcx_get_system_info",
				"Inspect physical drives, detected RAMDisks, free drive letters, WinFsp runtime status, and HCX settings path.",
				new JsonObject(),
				Array.Empty<string>()),

			BuildToolSchema(
				"hcx_get_settings",
				"Load and return sanitized HyperCache X user settings (SavedSessions, SavedRamDisks, flush interval, policies).",
				new JsonObject(),
				Array.Empty<string>()),

			BuildToolSchema(
				"hcx_run_benchmark",
				"Run a self-verifying 3-tier cache benchmark (cold HDD vs hot L1/L2) in a temporary subfolder under target_path and return MB/s, hit rate, and integrity results.",
				new JsonObject
				{
					["target_path"] = Prop("string", "Existing target directory to benchmark (must not be a raw device path)."),
					["ssd_cache_dir"] = Prop("string", "Optional SSD cache directory (defaults to a temporary folder)."),
					["ram_disk_dir"] = Prop("string", "Optional RAMDisk directory for L1 MMF testing."),
					["ram_mb"] = Prop("integer", "L1 RAM cache size in MB (1-512, default 16)."),
					["ssd_mb"] = Prop("integer", "L2 SSD cache size in MB (4-1024, default 64).")
				},
				new[] { "target_path" }),

			BuildToolSchema(
				"hcx_start_session",
				"Start a managed HybridCacheEngine acceleration session inside the MCP server and return its session_id.",
				new JsonObject
				{
					["target_path"] = Prop("string", "Existing target root directory to accelerate."),
					["ssd_cache_dir"] = Prop("string", "Directory for L2 SSD cache file (.hcx). Optional if ssd_mb is 0."),
					["ram_disk_dir"] = Prop("string", "Optional external RAMDisk directory for L1 MMF cache."),
					["ram_mb"] = Prop("integer", "L1 cache size in MB (0-4096, default 64)."),
					["ssd_mb"] = Prop("integer", "L2 SSD cache size in MB (0-16384, default 256)."),
					["write_policy"] = Prop("string", "WriteBack (default) or WriteThrough."),
					["enable_read_ahead"] = Prop("boolean", "Enable bounded async sequential read-ahead (default true)."),
					["flush_interval_seconds"] = Prop("integer", "Write-Back flush interval in seconds (1-300, default 10).")
				},
				new[] { "target_path" }),

			BuildToolSchema(
				"hcx_get_session_stats",
				"Get live CacheStatsSnapshot metrics (HitRate, RamHits, SsdHits, HddMisses, SequentialBypasses, PrefetchedBlocks, DirtyBlocks) for one or all active MCP sessions.",
				new JsonObject
				{
					["session_id"] = Prop("string", "Optional session_id returned by hcx_start_session. Omit to list all active sessions.")
				},
				Array.Empty<string>()),

			BuildToolSchema(
				"hcx_sync_session",
				"Flush Write-Back dirty blocks to backing storage via SyncAll() for one or all active MCP sessions.",
				new JsonObject
				{
					["session_id"] = Prop("string", "Optional session_id. Omit to sync all active sessions.")
				},
				Array.Empty<string>()),

			BuildToolSchema(
				"hcx_stop_session",
				"Safely stop and dispose an active MCP session (flushes dirty blocks, demotes L1 to L2, saves metadata).",
				new JsonObject
				{
					["session_id"] = Prop("string", "session_id to stop, or '*' to stop all sessions.")
				},
				new[] { "session_id" }),

			BuildToolSchema(
				"hcx_cleanup_cache",
				"Safely clean up orphan/unused HCX cache files (cache_*.bin, metadata_*.json, l1_cache_*.bin) in a directory. Skips reparse points and actively locked files.",
				new JsonObject
				{
					["cache_dir"] = Prop("string", "Directory containing HCX cache files."),
					["scope"] = Prop("string", "ssd, ramdisk, or both (default both).")
				},
				new[] { "cache_dir" }),

			BuildToolSchema(
				"hcx_warm_path",
				"Pre-warm a file or directory into L1/L2 cache (optionally pinning it against LRU eviction) on an active MCP session or the live HCX.App desktop instance.",
				new JsonObject
				{
					["relative_path"] = Prop("string", "Relative file or folder path inside the accelerated target root."),
					["session_id"] = Prop("string", "Optional MCP session_id. Omit or pass 'live' to forward to running HCX.App via Named Pipe IPC."),
					["max_mb"] = Prop("integer", "Maximum MB to pre-warm (1-1024, default 128)."),
					["pin"] = Prop("boolean", "Pin warmed path so it stays prioritized in L1 (default false).")
				},
				new[] { "relative_path" }),

			BuildToolSchema(
				"hcx_get_hot_files",
				"Get Top 10 hot files (hits, misses, hit rate, bytes) for an active MCP session or the live HCX.App desktop instance.",
				new JsonObject
				{
					["session_id"] = Prop("string", "Optional MCP session_id. Omit or pass 'live' to query running HCX.App.")
				},
				Array.Empty<string>()),

			BuildToolSchema(
				"hcx_live_app_status",
				"Query the live running HCX.App desktop instance over user-scoped Named Pipe IPC for active virtual drives, hit rates, prefetch counts, Top 10 hot files, and RAMDisks.",
				new JsonObject(),
				Array.Empty<string>()),

			BuildToolSchema(
				"hcx_live_app_sync",
				"Trigger immediate SyncAll() on all active virtual drives in the live running HCX.App desktop instance via Named Pipe IPC.",
				new JsonObject(),
				Array.Empty<string>())
		};

		return BuildResultResponse(idNode, new JsonObject { ["tools"] = tools });
	}

	private string HandleToolsCall(JsonNode? idNode, JsonElement paramsElem)
	{
		if (paramsElem.ValueKind != JsonValueKind.Object ||
		    !paramsElem.TryGetProperty("name", out JsonElement nameElem) ||
		    nameElem.ValueKind != JsonValueKind.String)
		{
			return BuildErrorResponse(idNode, -32602, "Invalid params: missing tool name.");
		}

		string toolName = nameElem.GetString() ?? string.Empty;
		JsonElement args = paramsElem.TryGetProperty("arguments", out JsonElement a) && a.ValueKind == JsonValueKind.Object
			? a
			: default;

		try
		{
			object payload = toolName switch
			{
				"hcx_get_system_info" => ToolGetSystemInfo(),
				"hcx_get_settings" => ToolGetSettings(),
				"hcx_run_benchmark" => ToolRunBenchmark(args),
				"hcx_start_session" => ToolStartSession(args),
				"hcx_get_session_stats" => ToolGetSessionStats(args),
				"hcx_sync_session" => ToolSyncSession(args),
				"hcx_stop_session" => ToolStopSession(args),
				"hcx_cleanup_cache" => ToolCleanupCache(args),
				"hcx_warm_path" => ToolWarmPath(args),
				"hcx_get_hot_files" => ToolGetHotFiles(args),
				"hcx_live_app_status" => ToolLiveAppStatus(),
				"hcx_live_app_sync" => ToolLiveAppSync(),
				_ => throw new KeyNotFoundException($"Unknown tool: {toolName}")
			};

			string text = JsonSerializer.Serialize(payload, PrettyResultJson);
			return BuildToolSuccess(idNode, text);
		}
		catch (KeyNotFoundException ex)
		{
			return BuildErrorResponse(idNode, -32602, ex.Message);
		}
		catch (Exception ex)
		{
			return BuildToolError(idNode, ex.Message);
		}
	}

	private object ToolGetSystemInfo()
	{
		List<DiskInfo> disks = DiskUtility.GetDisks();
		DiskInfo? preferredRamDisk = DiskUtility.FindPreferredRamDisk(disks);
		List<string> freeLetters = DiskUtility.GetAvailableDriveLetters();
		WinFspStatusInfo winFsp = WinFspRuntime.GetStatus();

		return new
		{
			serverVersion = ServerVersion,
			settingsPath = AppSettingsManager.DefaultSettingsPath,
			activeMcpSessions = _sessions.Count,
			winFsp = new
			{
				installed = winFsp.State == WinFspInstallState.Installed,
				version = winFsp.Version,
				dllPath = winFsp.DllPath,
				summary = winFsp.Summary
			},
			preferredRamDisk = preferredRamDisk == null ? null : new
			{
				preferredRamDisk.DriveLetter,
				preferredRamDisk.RootPath,
				preferredRamDisk.VolumeLabel,
				totalGb = Math.Round(preferredRamDisk.TotalBytes / (1024.0 * 1024.0 * 1024.0), 2),
				freeGb = Math.Round(preferredRamDisk.FreeBytes / (1024.0 * 1024.0 * 1024.0), 2)
			},
			availableDriveLetters = freeLetters,
			disks = disks.Select(d => new
			{
				d.DriveLetter,
				d.RootPath,
				d.VolumeLabel,
				d.Model,
				mediaKind = d.MediaKind.ToString(),
				totalGb = Math.Round(d.TotalBytes / (1024.0 * 1024.0 * 1024.0), 2),
				freeGb = Math.Round(d.FreeBytes / (1024.0 * 1024.0 * 1024.0), 2)
			}).ToArray()
		};
	}

	private static object ToolGetSettings()
	{
		AppSettings settings = AppSettingsManager.Load();
		return new
		{
			settingsPath = AppSettingsManager.DefaultSettingsPath,
			settings
		};
	}

	private static object ToolRunBenchmark(JsonElement args)
	{
		string targetPath = RequireSafeDirectory(args, "target_path", mustExist: true);
		string? ssdDir = GetOptionalSafeDirectory(args, "ssd_cache_dir", mustExist: false);
		string? ramDiskDir = GetOptionalSafeDirectory(args, "ram_disk_dir", mustExist: true);
		int ramMb = Math.Clamp(GetOptionalInt(args, "ram_mb", 16), 1, 512);
		int ssdMb = Math.Clamp(GetOptionalInt(args, "ssd_mb", 64), 4, 1024);

		bool createdTempSsd = false;
		if (string.IsNullOrWhiteSpace(ssdDir))
		{
			ssdDir = Path.Combine(Path.GetTempPath(), $"HCX_McpBench_{Guid.NewGuid():N}");
			Directory.CreateDirectory(ssdDir);
			createdTempSsd = true;
		}
		else
		{
			Directory.CreateDirectory(ssdDir);
		}

		try
		{
			BenchmarkResult res = CacheBenchmarkRunner.Run(
				targetPath,
				ssdDir,
				(long)ramMb * 1024L * 1024L,
				(long)ssdMb * 1024L * 1024L,
				log: null,
				ramDiskBaseDir: ramDiskDir);

			return new
			{
				dataIntegrityVerified = res.VerifiedIntegrity,
				coldThroughputMbPerSec = Math.Round(res.ColdReadMBps, 2),
				hotThroughputMbPerSec = Math.Round(res.HotReadMBps, 2),
				writeBackMbPerSec = Math.Round(res.WriteBackMBps, 2),
				speedupFactor = Math.Round(res.SpeedupFactor, 2),
				hitRatePercent = Math.Round(res.HitRatePercent, 2),
				res.RamHits,
				res.SsdHits,
				res.SequentialBypasses,
				res.PrefetchedBlocks,
				res.Summary
			};
		}
		finally
		{
			if (createdTempSsd)
			{
				try { Directory.Delete(ssdDir, recursive: true); } catch { }
			}
		}
	}

	private object ToolStartSession(JsonElement args)
	{
		if (_sessions.Count >= MaxActiveSessions)
		{
			throw new InvalidOperationException($"Maximum active MCP sessions ({MaxActiveSessions}) reached. Stop an existing session first.");
		}

		string targetPath = RequireSafeDirectory(args, "target_path", mustExist: true);
		int ramMb = Math.Clamp(GetOptionalInt(args, "ram_mb", 64), 0, 4096);
		int ssdMb = Math.Clamp(GetOptionalInt(args, "ssd_mb", 256), 0, 16384);
		string? ssdDir = GetOptionalSafeDirectory(args, "ssd_cache_dir", mustExist: false);
		string? ramDiskDir = GetOptionalSafeDirectory(args, "ram_disk_dir", mustExist: true);
		string policyStr = GetOptionalString(args, "write_policy") ?? "WriteBack";
		bool enableReadAhead = GetOptionalBool(args, "enable_read_ahead", true);
		int flushSeconds = Math.Clamp(GetOptionalInt(args, "flush_interval_seconds", 10), 1, 300);

		if (ssdMb > 0 && string.IsNullOrWhiteSpace(ssdDir))
		{
			ssdDir = Path.Combine(Path.GetTempPath(), "HCX_McpCache", Guid.NewGuid().ToString("N")[..8]);
		}
		if (!string.IsNullOrWhiteSpace(ssdDir))
		{
			Directory.CreateDirectory(ssdDir);
		}

		WritePolicy policy = string.Equals(policyStr, "WriteThrough", StringComparison.OrdinalIgnoreCase)
			? WritePolicy.WriteThrough
			: WritePolicy.WriteBack;

		CacheOptions options = new CacheOptions
		{
			TargetRootPath = targetPath,
			SsdCacheDirectory = ssdDir ?? string.Empty,
			RamDiskCacheDirectory = ramDiskDir,
			L1BackendMode = !string.IsNullOrWhiteSpace(ramDiskDir) ? L1BackendMode.AutoPreferRamDisk : L1BackendMode.InProcessRam,
			RamCacheSizeBytes = (long)ramMb * 1024L * 1024L,
			SsdCacheSizeBytes = (long)ssdMb * 1024L * 1024L,
			WritePolicy = policy,
			EnableReadAhead = enableReadAhead,
			FlushInterval = TimeSpan.FromSeconds(flushSeconds)
		};

		HybridCacheEngine engine = new HybridCacheEngine(options);
		if (!engine.Start())
		{
			engine.Dispose();
			throw new InvalidOperationException("Failed to start HybridCacheEngine.");
		}

		string sessionId = $"mcp-{Guid.NewGuid():N}"[..12];
		if (!_sessions.TryAdd(sessionId, engine))
		{
			engine.Dispose();
			throw new InvalidOperationException("Session ID collision.");
		}

		CacheStatsSnapshot snap = engine.GetStatsSnapshot();
		return new
		{
			sessionId,
			instanceId = engine.InstanceId,
			targetRootPath = engine.TargetRootPath,
			ssdCacheFilePath = engine.SsdCacheFilePath,
			l1Backend = snap.L1BackendSummary,
			writePolicy = snap.WritePolicy.ToString(),
			ramCapacityMb = ramMb,
			ssdCapacityMb = ssdMb,
			enableReadAhead
		};
	}

	private object ToolGetSessionStats(JsonElement args)
	{
		string? sessionId = GetOptionalString(args, "session_id");
		if (!string.IsNullOrWhiteSpace(sessionId))
		{
			if (!_sessions.TryGetValue(sessionId, out HybridCacheEngine? engine))
			{
				throw new ArgumentException($"Session not found: {sessionId}");
			}
			return FormatSessionSnapshot(sessionId, engine.GetStatsSnapshot());
		}

		return new
		{
			activeCount = _sessions.Count,
			sessions = _sessions.Select(kv => FormatSessionSnapshot(kv.Key, kv.Value.GetStatsSnapshot())).ToArray()
		};
	}

	private object ToolSyncSession(JsonElement args)
	{
		string? sessionId = GetOptionalString(args, "session_id");
		if (!string.IsNullOrWhiteSpace(sessionId))
		{
			if (!_sessions.TryGetValue(sessionId, out HybridCacheEngine? engine))
			{
				throw new ArgumentException($"Session not found: {sessionId}");
			}
			int flushed = engine.SyncAll();
			return new { sessionId, flushedBlocks = flushed };
		}

		int totalFlushed = 0;
		List<object> perSession = new List<object>();
		foreach (KeyValuePair<string, HybridCacheEngine> kv in _sessions)
		{
			int flushed = kv.Value.SyncAll();
			totalFlushed += flushed;
			perSession.Add(new { sessionId = kv.Key, flushedBlocks = flushed });
		}
		return new { totalFlushedBlocks = totalFlushed, sessions = perSession };
	}

	private object ToolStopSession(JsonElement args)
	{
		string sessionId = GetRequiredString(args, "session_id");
		if (sessionId == "*")
		{
			int stopped = 0;
			foreach (string key in _sessions.Keys.ToArray())
			{
				if (_sessions.TryRemove(key, out HybridCacheEngine? engine))
				{
					engine.Dispose();
					stopped++;
				}
			}
			return new { stoppedAll = true, stoppedCount = stopped };
		}

		if (!_sessions.TryRemove(sessionId, out HybridCacheEngine? target))
		{
			throw new ArgumentException($"Session not found: {sessionId}");
		}

		target.Dispose();
		return new { sessionId, stopped = true };
	}

	private static object ToolCleanupCache(JsonElement args)
	{
		string cacheDir = RequireSafeDirectory(args, "cache_dir", mustExist: true);
		string scope = (GetOptionalString(args, "scope") ?? "both").ToLowerInvariant();

		int ssdDeleted = 0;
		int ramDiskDeleted = 0;
		if (scope is "ssd" or "both")
		{
			ssdDeleted = SsdFileCacheTier.DeleteCacheFiles(cacheDir);
		}
		if (scope is "ramdisk" or "both")
		{
			ramDiskDeleted = RamDiskFileCacheTier.CleanupOrphanCacheFiles(cacheDir);
		}
		if (scope is not ("ssd" or "ramdisk" or "both"))
		{
			throw new ArgumentException("scope must be 'ssd', 'ramdisk', or 'both'.");
		}

		return new
		{
			cacheDir,
			scope,
			ssdFilesDeleted = ssdDeleted,
			ramDiskFilesDeleted = ramDiskDeleted,
			totalDeleted = ssdDeleted + ramDiskDeleted
		};
	}

	private object ToolWarmPath(JsonElement args)
	{
		string relPath = GetRequiredString(args, "relative_path");
		if (relPath.IndexOf('\0') >= 0 || relPath.Contains("..", StringComparison.Ordinal))
		{
			throw new ArgumentException("relative_path must not contain NUL or '..' traversal segments.");
		}
		string? sessionId = GetOptionalString(args, "session_id");
		int maxMb = Math.Clamp(GetOptionalInt(args, "max_mb", 128), 1, 1024);
		bool pin = GetOptionalBool(args, "pin", false);

		if (string.IsNullOrWhiteSpace(sessionId) || string.Equals(sessionId, "live", StringComparison.OrdinalIgnoreCase))
		{
			if (!HcxControlPipeClient.TrySend("warm_path", new { relativePath = relPath, maxMb, pin }, out JsonNode? data, out string? error))
			{
				throw new InvalidOperationException(error ?? "Failed to reach live HCX.App.");
			}
			return new { source = "live-app", result = data };
		}

		if (!_sessions.TryGetValue(sessionId, out HybridCacheEngine? engine))
		{
			throw new ArgumentException($"Session not found: {sessionId}");
		}

		WarmResult res = engine.WarmPath(relPath, (long)maxMb * 1024L * 1024L, pin);
		return new
		{
			source = "mcp-session",
			sessionId,
			res.FilesWarmed,
			res.BlocksWarmed,
			res.BytesWarmed,
			res.Pinned
		};
	}

	private object ToolGetHotFiles(JsonElement args)
	{
		string? sessionId = GetOptionalString(args, "session_id");
		if (string.IsNullOrWhiteSpace(sessionId) || string.Equals(sessionId, "live", StringComparison.OrdinalIgnoreCase))
		{
			if (!HcxControlPipeClient.TrySend("get_live_stats", null, out JsonNode? data, out string? error))
			{
				throw new InvalidOperationException(error ?? "Failed to reach live HCX.App.");
			}
			return new { source = "live-app", live = data };
		}

		if (!_sessions.TryGetValue(sessionId, out HybridCacheEngine? engine))
		{
			throw new ArgumentException($"Session not found: {sessionId}");
		}
		return new
		{
			source = "mcp-session",
			sessionId,
			topHotFiles = engine.GetTopHotFiles(10)
		};
	}

	private static object ToolLiveAppStatus()
	{
		if (!HcxControlPipeClient.TrySend("get_live_stats", null, out JsonNode? data, out string? error))
		{
			return new { connected = false, message = error };
		}
		return new { connected = true, live = data };
	}

	private static object ToolLiveAppSync()
	{
		if (!HcxControlPipeClient.TrySend("sync_all", null, out JsonNode? data, out string? error))
		{
			throw new InvalidOperationException(error ?? "Failed to reach live HCX.App.");
		}
		return new { connected = true, result = data };
	}

	private static object FormatSessionSnapshot(string sessionId, CacheStatsSnapshot snap)
	{
		return new
		{
			sessionId,
			snap.IsRunning,
			snap.TargetRootPath,
			snap.L1BackendSummary,
			writePolicy = snap.WritePolicy.ToString(),
			hitRatePercent = Math.Round(snap.HitRatePercent, 2),
			snap.RamHits,
			snap.SsdHits,
			snap.HddMisses,
			snap.SequentialBypasses,
			snap.PrefetchedBlocks,
			snap.RamCachedBlocks,
			snap.SsdCachedBlocks,
			snap.DirtyBlocks,
			snap.TotalReadBytes,
			snap.TotalWriteBytes
		};
	}

	private static string RequireSafeDirectory(JsonElement args, string name, bool mustExist)
	{
		string path = GetRequiredString(args, name);
		return ValidateSafeDirectoryPath(path, name, mustExist);
	}

	private static string? GetOptionalSafeDirectory(JsonElement args, string name, bool mustExist)
	{
		string? path = GetOptionalString(args, name);
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}
		return ValidateSafeDirectoryPath(path, name, mustExist);
	}

	private static string ValidateSafeDirectoryPath(string rawPath, string paramName, bool mustExist)
	{
		if (rawPath.IndexOf('\0') >= 0)
		{
			throw new ArgumentException($"{paramName} contains invalid NUL character.");
		}
		if (rawPath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
		    rawPath.StartsWith(@"\\.\", StringComparison.Ordinal))
		{
			throw new ArgumentException($"{paramName} must not be a raw device/volume path.");
		}
		string full = Path.GetFullPath(rawPath);
		if (mustExist && !Directory.Exists(full))
		{
			throw new DirectoryNotFoundException($"Directory not found for {paramName}: {full}");
		}
		if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
		{
			throw new UnauthorizedAccessException($"{paramName} must not be a symbolic link or directory junction: {full}");
		}
		return full;
	}

	private static string GetRequiredString(JsonElement args, string name)
	{
		string? value = GetOptionalString(args, name);
		if (string.IsNullOrWhiteSpace(value))
		{
			throw new ArgumentException($"Missing required string argument: {name}");
		}
		return value;
	}

	private static string? GetOptionalString(JsonElement args, string name)
	{
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement prop))
		{
			return null;
		}
		return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
	}

	private static int GetOptionalInt(JsonElement args, string name, int defaultValue)
	{
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement prop))
		{
			return defaultValue;
		}
		if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int v))
		{
			return v;
		}
		return defaultValue;
	}

	private static bool GetOptionalBool(JsonElement args, string name, bool defaultValue)
	{
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement prop))
		{
			return defaultValue;
		}
		return prop.ValueKind switch
		{
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			_ => defaultValue
		};
	}

	private static JsonObject BuildToolSchema(string name, string description, JsonObject properties, string[] required)
	{
		JsonArray reqArr = new JsonArray();
		foreach (string r in required)
		{
			reqArr.Add(r);
		}
		return new JsonObject
		{
			["name"] = name,
			["description"] = description,
			["inputSchema"] = new JsonObject
			{
				["type"] = "object",
				["properties"] = properties,
				["required"] = reqArr,
				["additionalProperties"] = false
			}
		};
	}

	private static JsonObject Prop(string type, string description) => new JsonObject
	{
		["type"] = type,
		["description"] = description
	};

	private static string BuildResultResponse(JsonNode? idNode, JsonNode result)
	{
		JsonObject envelope = new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"] = idNode?.DeepClone(),
			["result"] = result
		};
		return envelope.ToJsonString(CompactJson);
	}

	private static string BuildErrorResponse(JsonNode? idNode, int code, string message)
	{
		JsonObject envelope = new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"] = idNode?.DeepClone(),
			["error"] = new JsonObject
			{
				["code"] = code,
				["message"] = message
			}
		};
		return envelope.ToJsonString(CompactJson);
	}

	private static string BuildToolSuccess(JsonNode? idNode, string text)
	{
		JsonObject result = new JsonObject
		{
			["content"] = new JsonArray
			{
				new JsonObject
				{
					["type"] = "text",
					["text"] = text
				}
			},
			["isError"] = false
		};
		return BuildResultResponse(idNode, result);
	}

	private static string BuildToolError(JsonNode? idNode, string errorMessage)
	{
		JsonObject result = new JsonObject
		{
			["content"] = new JsonArray
			{
				new JsonObject
				{
					["type"] = "text",
					["text"] = errorMessage
				}
			},
			["isError"] = true
		};
		return BuildResultResponse(idNode, result);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		foreach (string key in _sessions.Keys.ToArray())
		{
			if (_sessions.TryRemove(key, out HybridCacheEngine? engine))
			{
				try { engine.Dispose(); } catch { }
			}
		}
	}
}
