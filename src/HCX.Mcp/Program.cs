using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HCX.Mcp;

internal static class Program
{
	public static async Task<int> Main()
	{
		Encoding utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
		using StreamReader stdin = new StreamReader(Console.OpenStandardInput(), utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
		using StreamWriter stdout = new StreamWriter(Console.OpenStandardOutput(), utf8NoBom, bufferSize: 8192, leaveOpen: true)
		{
			NewLine = "\n",
			AutoFlush = false
		};

		using CancellationTokenSource cts = new CancellationTokenSource();
		using HcxMcpServer server = new HcxMcpServer(Console.Error);

		Console.CancelKeyPress += (_, e) =>
		{
			e.Cancel = true;
			try { cts.Cancel(); } catch (ObjectDisposedException) { }
		};
		EventHandler onProcessExit = (_, _) =>
		{
			try { cts.Cancel(); } catch (ObjectDisposedException) { }
			server.Dispose();
		};
		AppDomain.CurrentDomain.ProcessExit += onProcessExit;

		try
		{
			await server.RunAsync(stdin, stdout, cts.Token).ConfigureAwait(false);
			return 0;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[HCX.Mcp] Fatal error: {ex.Message}");
			return 1;
		}
		finally
		{
			AppDomain.CurrentDomain.ProcessExit -= onProcessExit;
		}
	}
}
