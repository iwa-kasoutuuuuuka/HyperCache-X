using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HCX.Mcp;
using Xunit;

namespace HCX.Tests;

public sealed class McpServerTests : IDisposable
{
	private readonly string _tempRoot;
	private readonly string _targetDir;
	private readonly string _ssdDir;

	public McpServerTests()
	{
		_tempRoot = Path.Combine(Path.GetTempPath(), $"HCX_McpTests_{Guid.NewGuid():N}");
		_targetDir = Path.Combine(_tempRoot, "target");
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
	public void McpHandshake_Ping_ToolsList_And_ErrorHandling_ConformToJsonRpc2()
	{
		using HcxMcpServer server = new HcxMcpServer(TextWriter.Null);

		// 1. initialize (negotiates client protocol version)
		string? initRaw = server.ProcessMessage("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""");
		Assert.NotNull(initRaw);
		using (JsonDocument doc = JsonDocument.Parse(initRaw!))
		{
			Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt32());
			JsonElement res = doc.RootElement.GetProperty("result");
			Assert.Equal("2025-03-26", res.GetProperty("protocolVersion").GetString());
			Assert.Equal(HcxMcpServer.ServerName, res.GetProperty("serverInfo").GetProperty("name").GetString());
		}

		// 2. notifications/initialized (must return null / no response)
		Assert.Null(server.ProcessMessage("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));

		// 3. ping (with string id preserved)
		string? pingRaw = server.ProcessMessage("""{"jsonrpc":"2.0","id":"p-1","method":"ping"}""");
		Assert.NotNull(pingRaw);
		using (JsonDocument doc = JsonDocument.Parse(pingRaw!))
		{
			Assert.Equal("p-1", doc.RootElement.GetProperty("id").GetString());
			Assert.True(doc.RootElement.TryGetProperty("result", out _));
		}

		// 4. tools/list returns all 12 HCX tools
		string? listRaw = server.ProcessMessage("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
		Assert.NotNull(listRaw);
		using (JsonDocument doc = JsonDocument.Parse(listRaw!))
		{
			JsonElement tools = doc.RootElement.GetProperty("result").GetProperty("tools");
			Assert.Equal(12, tools.GetArrayLength());
			string[] names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray();
			Assert.Contains("hcx_get_system_info", names);
			Assert.Contains("hcx_run_benchmark", names);
			Assert.Contains("hcx_start_session", names);
			Assert.Contains("hcx_cleanup_cache", names);
			Assert.Contains("hcx_warm_path", names);
			Assert.Contains("hcx_get_hot_files", names);
			Assert.Contains("hcx_live_app_status", names);
			Assert.Contains("hcx_live_app_sync", names);
		}

		// 5. Parse error (-32700) and unknown method (-32601)
		string? badJson = server.ProcessMessage("{not-valid-json");
		Assert.NotNull(badJson);
		using (JsonDocument doc = JsonDocument.Parse(badJson!))
		{
			Assert.Equal(-32700, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
		}

		string? unknown = server.ProcessMessage("""{"jsonrpc":"2.0","id":3,"method":"does/not/exist"}""");
		Assert.NotNull(unknown);
		using (JsonDocument doc = JsonDocument.Parse(unknown!))
		{
			Assert.Equal(-32601, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
		}
	}

	[Fact]
	public void McpTools_SessionLifecycle_Benchmark_Cleanup_And_SafetyGuards_WorkEndToEnd()
	{
		using HcxMcpServer server = new HcxMcpServer(TextWriter.Null);

		// Security: reject raw device path in hcx_start_session
		string? rawErr = server.ProcessMessage("""{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"hcx_start_session","arguments":{"target_path":"\\\\.\\PhysicalDrive0"}}}""");
		Assert.NotNull(rawErr);
		using (JsonDocument doc = JsonDocument.Parse(rawErr!))
		{
			Assert.True(doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
		}

		// Start session
		string startReq = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			id = 11,
			method = "tools/call",
			@params = new
			{
				name = "hcx_start_session",
				arguments = new
				{
					target_path = _targetDir,
					ssd_cache_dir = _ssdDir,
					ram_mb = 4,
					ssd_mb = 8,
					write_policy = "WriteBack"
				}
			}
		});
		string? startResp = server.ProcessMessage(startReq);
		Assert.NotNull(startResp);
		string sessionId;
		using (JsonDocument doc = JsonDocument.Parse(startResp!))
		{
			JsonElement res = doc.RootElement.GetProperty("result");
			Assert.False(res.GetProperty("isError").GetBoolean());
			string text = res.GetProperty("content")[0].GetProperty("text").GetString()!;
			using JsonDocument payload = JsonDocument.Parse(text);
			sessionId = payload.RootElement.GetProperty("sessionId").GetString()!;
			Assert.False(string.IsNullOrWhiteSpace(sessionId));
		}
		Assert.Equal(1, server.ActiveSessionCount);

		// Get stats & sync
		string statsReq = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			id = 12,
			method = "tools/call",
			@params = new { name = "hcx_get_session_stats", arguments = new { session_id = sessionId } }
		});
		using (JsonDocument doc = JsonDocument.Parse(server.ProcessMessage(statsReq)!))
		{
			Assert.False(doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
		}

		string syncReq = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			id = 13,
			method = "tools/call",
			@params = new { name = "hcx_sync_session", arguments = new { session_id = sessionId } }
		});
		using (JsonDocument doc = JsonDocument.Parse(server.ProcessMessage(syncReq)!))
		{
			Assert.False(doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
		}

		// Stop session
		string stopReq = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			id = 14,
			method = "tools/call",
			@params = new { name = "hcx_stop_session", arguments = new { session_id = sessionId } }
		});
		using (JsonDocument doc = JsonDocument.Parse(server.ProcessMessage(stopReq)!))
		{
			Assert.False(doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
		}
		Assert.Equal(0, server.ActiveSessionCount);

		// Cleanup cache directory
		string cleanupReq = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			id = 15,
			method = "tools/call",
			@params = new { name = "hcx_cleanup_cache", arguments = new { cache_dir = _ssdDir, scope = "both" } }
		});
		using (JsonDocument doc = JsonDocument.Parse(cleanupReq.Length > 0 ? server.ProcessMessage(cleanupReq)! : "{}"))
		{
			Assert.False(doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
		}

		// Benchmark tool
		string benchReq = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			id = 16,
			method = "tools/call",
			@params = new { name = "hcx_run_benchmark", arguments = new { target_path = _targetDir, ram_mb = 4, ssd_mb = 8 } }
		});
		using (JsonDocument doc = JsonDocument.Parse(server.ProcessMessage(benchReq)!))
		{
			JsonElement res = doc.RootElement.GetProperty("result");
			Assert.False(res.GetProperty("isError").GetBoolean());
			string text = res.GetProperty("content")[0].GetProperty("text").GetString()!;
			using JsonDocument payload = JsonDocument.Parse(text);
			Assert.True(payload.RootElement.GetProperty("dataIntegrityVerified").GetBoolean());
		}
	}
}
