using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace HCX.Core;

/// <summary>
/// Local per-user Named Pipe IPC server/client enabling HCX.Mcp (Antigravity / Claude Code)
/// to inspect and control a live running HCX.App desktop instance securely.
/// </summary>
public sealed class HcxControlPipeServer : IDisposable
{
	private const int MaxConcurrentClients = 4;
	private const int MaxRequestLineChars = 65536;
	private const int ClientReadTimeoutMs = 5000;

	private readonly string _pipeName;
	private readonly Func<string, JsonElement, object> _handler;
	private readonly CancellationTokenSource _cts = new CancellationTokenSource();
	private readonly SemaphoreSlim _clientSlots = new SemaphoreSlim(MaxConcurrentClients, MaxConcurrentClients);
	private readonly Task _acceptTask;
	private bool _disposed;

	public string PipeName => _pipeName;

	public static string GetDefaultPipeName()
	{
		string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
		return $"HyperCacheX_Control_{sid}";
	}

	public HcxControlPipeServer(Func<string, JsonElement, object> handler, string? pipeName = null)
	{
		_handler = handler ?? throw new ArgumentNullException(nameof(handler));
		_pipeName = string.IsNullOrWhiteSpace(pipeName) ? GetDefaultPipeName() : pipeName;
		_acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
	}

	private async Task AcceptLoopAsync(CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			NamedPipeServerStream? pipe = null;
			try
			{
				await _clientSlots.WaitAsync(token).ConfigureAwait(false);
				pipe = CreateUserScopedServerPipe(_pipeName);
				await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);

				NamedPipeServerStream connectedPipe = pipe;
				pipe = null;
				_ = Task.Run(async () =>
				{
					try
					{
						await HandleClientAsync(connectedPipe, token).ConfigureAwait(false);
					}
					catch { }
					finally
					{
						try { connectedPipe.Dispose(); } catch { }
						try { _clientSlots.Release(); } catch { }
					}
				}, CancellationToken.None);
			}
			catch (OperationCanceledException)
			{
				if (pipe != null)
				{
					try { pipe.Dispose(); } catch { }
					try { _clientSlots.Release(); } catch { }
				}
				break;
			}
			catch
			{
				if (pipe != null)
				{
					try { pipe.Dispose(); } catch { }
					try { _clientSlots.Release(); } catch { }
				}
				try { await Task.Delay(100, token).ConfigureAwait(false); } catch { break; }
			}
		}
	}

	private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
	{
		using CancellationTokenSource readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
		readCts.CancelAfter(ClientReadTimeoutMs);

		Encoding utf8 = new UTF8Encoding(false);
		using StreamReader reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
		using StreamWriter writer = new StreamWriter(pipe, utf8, bufferSize: 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };

		string? line = await ReadBoundedLineAsync(reader, MaxRequestLineChars, readCts.Token).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(line))
		{
			return;
		}

		string responseJson;
		try
		{
			using JsonDocument doc = JsonDocument.Parse(line);
			string action = doc.RootElement.TryGetProperty("action", out JsonElement a) ? (a.GetString() ?? string.Empty) : string.Empty;
			JsonElement payload = doc.RootElement.TryGetProperty("payload", out JsonElement p) ? p : default;
			object result = _handler(action, payload);
			responseJson = JsonSerializer.Serialize(new { ok = true, data = result });
		}
		catch (Exception ex)
		{
			responseJson = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
		}

		await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
	}

	private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int maxChars, CancellationToken token)
	{
		StringBuilder sb = new StringBuilder(256);
		char[] buf = new char[1];
		while (sb.Length < maxChars)
		{
			int n = await reader.ReadAsync(buf.AsMemory(0, 1), token).ConfigureAwait(false);
			if (n <= 0)
			{
				return sb.Length > 0 ? sb.ToString() : null;
			}
			char c = buf[0];
			if (c == '\n')
			{
				return sb.ToString();
			}
			if (c != '\r')
			{
				sb.Append(c);
			}
		}
		throw new InvalidOperationException($"IPC request exceeded maximum allowed length ({maxChars} chars).");
	}

	private static NamedPipeServerStream CreateUserScopedServerPipe(string pipeName)
	{
		SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
		if (currentUser != null)
		{
			PipeSecurity security = new PipeSecurity();
			security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
			return NamedPipeServerStreamAcl.Create(
				pipeName,
				PipeDirection.InOut,
				NamedPipeServerStream.MaxAllowedServerInstances,
				PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous,
				inBufferSize: 8192,
				outBufferSize: 8192,
				security);
		}
		return new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		try { _cts.Cancel(); } catch { }
		try { _acceptTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
		_cts.Dispose();
		_clientSlots.Dispose();
	}
}

public static class HcxControlPipeClient
{
	public static bool TrySend(string action, object? payload, out JsonNode? data, out string? error, string? pipeName = null, int timeoutMs = 2000)
	{
		data = null;
		error = null;
		string targetPipe = string.IsNullOrWhiteSpace(pipeName) ? HcxControlPipeServer.GetDefaultPipeName() : pipeName;
		try
		{
			using NamedPipeClientStream client = new NamedPipeClientStream(".", targetPipe, PipeDirection.InOut, PipeOptions.None);
			client.Connect(timeoutMs);

			Encoding utf8 = new UTF8Encoding(false);
			using StreamWriter writer = new StreamWriter(client, utf8, bufferSize: 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
			using StreamReader reader = new StreamReader(client, utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

			string req = JsonSerializer.Serialize(new { action, payload });
			writer.WriteLine(req);
			string? respLine = reader.ReadLine();
			if (string.IsNullOrWhiteSpace(respLine))
			{
				error = "Empty response from HCX.App control pipe.";
				return false;
			}

			JsonNode? parsed = JsonNode.Parse(respLine);
			bool ok = parsed?["ok"]?.GetValue<bool>() ?? false;
			if (!ok)
			{
				error = parsed?["error"]?.GetValue<string>() ?? "Unknown HCX.App IPC error.";
				return false;
			}
			data = parsed?["data"];
			return true;
		}
		catch (TimeoutException)
		{
			error = "HCX.App is not running (control pipe timed out).";
			return false;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}
}
