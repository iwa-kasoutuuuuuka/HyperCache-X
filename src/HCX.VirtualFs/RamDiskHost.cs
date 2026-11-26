using System;
using System.IO;
using Fsp;

namespace HCX.VirtualFs;

/// <summary>
/// Hosts a standalone volatile WinFsp RAM disk drive (e.g. M:\) backed by InMemoryVolumeStore.
/// </summary>
public sealed class RamDiskHost : IDisposable
{
	private readonly InMemoryVolumeStore _store;
	private readonly MemoryFileSystem _fs;
	private FileSystemHost? _host;
	private bool _disposed;

	public InMemoryVolumeStore Store => _store;
	public string VolumeLabel { get; }
	public long CapacityBytes => _store.CapacityBytes;
	public string? MountedPoint { get; private set; }
	public bool IsMounted => _host != null;

	public RamDiskHost(long capacityBytes, string volumeLabel = "HCX_RAMDISK")
	{
		VolumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? "HCX_RAMDISK" : volumeLabel;
		_store = new InMemoryVolumeStore(capacityBytes);
		_fs = new MemoryFileSystem(_store, VolumeLabel);
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
			throw new InvalidOperationException("RAM ディスクの作成・マウントには WinFsp ランタイムが必要です。");
		}

		string normalizedMount = mountPoint.Trim().TrimEnd('\\');
		FileSystemHost host = new FileSystemHost(_fs);
		int result = host.Mount(normalizedMount, null, true, 0);
		if (result < 0)
		{
			host.Dispose();
			throw new IOException($"RAM ディスクのマウントに失敗しました (NTSTATUS=0x{result:X8}, MountPoint={normalizedMount})。");
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
		_store.Clear();
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
