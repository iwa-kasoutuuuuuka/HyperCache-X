using System;
using System.IO;
using Fsp;
using HCX.Core;

namespace HCX.VirtualFs;

/// <summary>
/// Manages mounting and clean unmounting of an accelerated virtual drive via WinFsp.
/// </summary>
public sealed class VirtualDriveHost : IDisposable
{
	private readonly HybridCacheEngine _engine;
	private readonly HcxFileSystem _fs;
	private FileSystemHost? _host;
	private bool _disposed;

	public string? MountedPoint { get; private set; }
	public bool IsMounted => _host != null;
	public HybridCacheEngine Engine => _engine;

	public VirtualDriveHost(HybridCacheEngine engine, string volumeLabel = "HyperCache X")
	{
		_engine = engine ?? throw new ArgumentNullException(nameof(engine));
		_fs = new HcxFileSystem(_engine, volumeLabel);
	}

	public bool Mount(string mountPoint)
	{
		if (_host != null)
		{
			return true;
		}

		WinFspStatusInfo status = WinFspRuntime.GetStatus();
		if (status.State != WinFspInstallState.Installed)
		{
			throw new InvalidOperationException("WinFsp ランタイムがインストールされていません。UI の [WinFsp セットアップ] または 'winget install WinFsp.WinFsp' で導入してください。");
		}

		string normalizedMount = mountPoint.Trim().TrimEnd('\\');
		FileSystemHost host = new FileSystemHost(_fs);
		int result = host.MountEx(normalizedMount, 0, null, false, 0);
		if (result < 0)
		{
			host.Dispose();
			throw new IOException($"WinFsp マウントに失敗しました (NTSTATUS=0x{result:X8}, MountPoint={normalizedMount})。");
		}

		_host = host;
		MountedPoint = host.MountPoint();
		return true;
	}

	public void Unmount()
	{
		if (_host != null)
		{
			try
			{
				_host.Unmount();
			}
			finally
			{
				_host.Dispose();
				_host = null;
				MountedPoint = null;
			}
		}
		_engine.SyncAll();
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		Unmount();
	}
}
