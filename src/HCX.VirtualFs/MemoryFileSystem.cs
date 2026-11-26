using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Fsp;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace HCX.VirtualFs;

/// <summary>
/// Pure volatile WinFsp user-mode RAM disk filesystem backed by InMemoryVolumeStore.
/// </summary>
public sealed class MemoryFileSystem : FileSystemBase
{
	private const int AllocationUnit = 4096;
	private readonly InMemoryVolumeStore _store;
	private string _volumeLabel;
	private readonly ulong _creationTimeUtc;
	private readonly uint _serialNumber;

	private sealed class MemDesc
	{
		public string Path;
		public List<InMemoryDirectoryEntry>? DirEntries;

		public MemDesc(string path)
		{
			Path = path;
		}
	}

	public MemoryFileSystem(InMemoryVolumeStore store, string volumeLabel = "HCX_RAMDISK")
	{
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_volumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? "HCX_RAMDISK" : volumeLabel;
		_creationTimeUtc = (ulong)DateTime.UtcNow.ToFileTimeUtc();
		_serialNumber = (uint)Guid.NewGuid().GetHashCode();
	}

	public override int ExceptionHandler(Exception ex)
	{
		int hr = ex.HResult;
		if ((hr & 0xFFFF0000) == unchecked((int)0x80070000))
		{
			return NtStatusFromWin32((uint)(hr & 0xFFFF));
		}
		return STATUS_UNEXPECTED_IO_ERROR;
	}

	public override int Init(object hostObj)
	{
		FileSystemHost host = (FileSystemHost)hostObj;
		host.SectorSize = AllocationUnit;
		host.SectorsPerAllocationUnit = 1;
		host.MaxComponentLength = 255;
		host.FileInfoTimeout = 1000;
		host.CaseSensitiveSearch = false;
		host.CasePreservedNames = true;
		host.UnicodeOnDisk = true;
		host.PersistentAcls = true;
		host.PostCleanupWhenModifiedOnly = true;
		host.PassQueryDirectoryPattern = true;
		host.VolumeCreationTime = _creationTimeUtc;
		host.VolumeSerialNumber = _serialNumber;
		host.FileSystemName = "HCX-RAMFS";
		return STATUS_SUCCESS;
	}

	public override int GetVolumeInfo(out VolumeInfo volumeInfo)
	{
		RamDiskStats stats = _store.GetStats();
		volumeInfo = default;
		volumeInfo.TotalSize = (ulong)stats.CapacityBytes;
		volumeInfo.FreeSize = (ulong)stats.FreeBytes;
		volumeInfo.SetVolumeLabel(_volumeLabel);
		return STATUS_SUCCESS;
	}

	public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
	{
		_volumeLabel = volumeLabel ?? string.Empty;
		return GetVolumeInfo(out volumeInfo);
	}

	public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
	{
		if (!_store.TryGetInfo(fileName, out InMemoryFileInfo info, out byte[] sd))
		{
			fileAttributes = 0;
			return STATUS_OBJECT_NAME_NOT_FOUND;
		}
		fileAttributes = info.FileAttributes;
		if (securityDescriptor != null)
		{
			securityDescriptor = sd;
		}
		return STATUS_SUCCESS;
	}

	public override int Create(
		string fileName,
		uint createOptions,
		uint grantedAccess,
		uint fileAttributes,
		byte[] securityDescriptor,
		ulong allocationSize,
		out object fileNode,
		out object fileDescOut,
		out FileInfo fileInfo,
		out string normalizedName)
	{
		bool isDir = (createOptions & FILE_DIRECTORY_FILE) != 0;
		InMemoryFileInfo info = _store.CreateEntry(fileName, isDir, fileAttributes, securityDescriptor, allocationSize);
		fileNode = null!;
		fileDescOut = new MemDesc(fileName);
		fileInfo = ConvertInfo(info);
		normalizedName = null!;
		return STATUS_SUCCESS;
	}

	public override int Open(
		string fileName,
		uint createOptions,
		uint grantedAccess,
		out object fileNode,
		out object fileDescOut,
		out FileInfo fileInfo,
		out string normalizedName)
	{
		if (!_store.TryGetInfo(fileName, out InMemoryFileInfo info, out _))
		{
			fileNode = null!;
			fileDescOut = null!;
			fileInfo = default;
			normalizedName = null!;
			return STATUS_OBJECT_NAME_NOT_FOUND;
		}
		fileNode = null!;
		fileDescOut = new MemDesc(fileName);
		fileInfo = ConvertInfo(info);
		normalizedName = null!;
		return STATUS_SUCCESS;
	}

	public override int Overwrite(
		object fileNode,
		object fileDescObj,
		uint fileAttributes,
		bool replaceFileAttributes,
		ulong allocationSize,
		out FileInfo fileInfo)
	{
		MemDesc desc = (MemDesc)fileDescObj;
		fileInfo = ConvertInfo(_store.OverwriteFile(desc.Path, fileAttributes, replaceFileAttributes, allocationSize));
		return STATUS_SUCCESS;
	}

	public override void Cleanup(object fileNode, object fileDescObj, string fileName, uint flags)
	{
		MemDesc desc = (MemDesc)fileDescObj;
		if ((flags & CleanupDelete) != 0)
		{
			_store.Delete(desc.Path);
		}
	}

	public override void Close(object fileNode, object fileDescObj)
	{
	}

	public override int Read(
		object fileNode,
		object fileDescObj,
		IntPtr buffer,
		ulong offset,
		uint length,
		out uint bytesTransferred)
	{
		MemDesc desc = (MemDesc)fileDescObj;
		byte[] rented = ArrayPool<byte>.Shared.Rent((int)Math.Max(1u, length));
		try
		{
			int read = _store.ReadFile(desc.Path, offset, rented.AsSpan(0, (int)length), out bool eof);
			if (eof)
			{
				bytesTransferred = 0;
				return STATUS_END_OF_FILE;
			}
			if (read > 0)
			{
				Marshal.Copy(rented, 0, buffer, read);
			}
			bytesTransferred = (uint)read;
			return STATUS_SUCCESS;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rented);
		}
	}

	public override int Write(
		object fileNode,
		object fileDescObj,
		IntPtr buffer,
		ulong offset,
		uint length,
		bool writeToEndOfFile,
		bool constrainedIo,
		out uint bytesTransferred,
		out FileInfo fileInfo)
	{
		MemDesc desc = (MemDesc)fileDescObj;
		byte[] rented = ArrayPool<byte>.Shared.Rent((int)Math.Max(1u, length));
		try
		{
			if (length > 0)
			{
				Marshal.Copy(buffer, rented, 0, (int)length);
			}
			InMemoryFileInfo info = _store.WriteFile(desc.Path, offset, rented.AsSpan(0, (int)length), writeToEndOfFile, constrainedIo, out bytesTransferred);
			fileInfo = ConvertInfo(info);
			return STATUS_SUCCESS;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rented);
		}
	}

	public override int Flush(object fileNode, object fileDescObj, out FileInfo fileInfo)
	{
		if (fileDescObj == null)
		{
			fileInfo = default;
			return STATUS_SUCCESS;
		}
		MemDesc desc = (MemDesc)fileDescObj;
		if (_store.TryGetInfo(desc.Path, out InMemoryFileInfo info, out _))
		{
			fileInfo = ConvertInfo(info);
			return STATUS_SUCCESS;
		}
		fileInfo = default;
		return STATUS_OBJECT_NAME_NOT_FOUND;
	}

	public override int GetFileInfo(object fileNode, object fileDescObj, out FileInfo fileInfo)
	{
		MemDesc desc = (MemDesc)fileDescObj;
		if (!_store.TryGetInfo(desc.Path, out InMemoryFileInfo info, out _))
		{
			fileInfo = default;
			return STATUS_OBJECT_NAME_NOT_FOUND;
		}
		fileInfo = ConvertInfo(info);
		return STATUS_SUCCESS;
	}

	public override int SetBasicInfo(
		object fileNode,
		object fileDescObj,
		uint fileAttributes,
		ulong creationTime,
		ulong lastAccessTime,
		ulong lastWriteTime,
		ulong changeTime,
		out FileInfo fileInfo)
	{
		MemDesc desc = (MemDesc)fileDescObj;
		fileInfo = ConvertInfo(_store.SetBasicInfo(desc.Path, fileAttributes, creationTime, lastAccessTime, lastWriteTime));
		return STATUS_SUCCESS;
	}

	public override int SetFileSize(
		object fileNode,
		object fileDescObj,
		ulong newSize,
		bool setAllocationSize,
		out FileInfo fileInfo)
	{
		MemDesc desc = (MemDesc)fileDescObj;
		fileInfo = ConvertInfo(_store.SetFileSize(desc.Path, (long)newSize, setAllocationSize));
		return STATUS_SUCCESS;
	}

	public override int CanDelete(object fileNode, object fileDescObj, string fileName)
	{
		MemDesc desc = (MemDesc)fileDescObj;
		_store.CanDelete(desc.Path);
		return STATUS_SUCCESS;
	}

	public override int Rename(
		object fileNode,
		object fileDescObj,
		string fileName,
		string newFileName,
		bool replaceIfExists)
	{
		MemDesc desc = (MemDesc)fileDescObj;
		_store.Rename(fileName, newFileName, replaceIfExists);
		desc.Path = newFileName;
		return STATUS_SUCCESS;
	}

	public override int GetSecurity(object fileNode, object fileDescObj, ref byte[] securityDescriptor)
	{
		MemDesc desc = (MemDesc)fileDescObj;
		securityDescriptor = _store.GetSecurity(desc.Path);
		return STATUS_SUCCESS;
	}

	public override int SetSecurity(
		object fileNode,
		object fileDescObj,
		AccessControlSections sections,
		byte[] securityDescriptor)
	{
		MemDesc desc = (MemDesc)fileDescObj;
		_store.SetSecurity(desc.Path, sections, securityDescriptor);
		return STATUS_SUCCESS;
	}

	public override bool ReadDirectoryEntry(
		object fileNode,
		object fileDescObj,
		string pattern,
		string marker,
		ref object context,
		out string fileName,
		out FileInfo fileInfo)
	{
		MemDesc desc = (MemDesc)fileDescObj;
		desc.DirEntries ??= _store.EnumerateDirectory(desc.Path, pattern);

		int index;
		if (context == null)
		{
			index = 0;
			if (marker != null)
			{
				index = desc.DirEntries.FindIndex(e => string.Compare(e.Name, marker, StringComparison.OrdinalIgnoreCase) > 0);
				if (index < 0)
				{
					index = desc.DirEntries.Count;
				}
			}
		}
		else
		{
			index = (int)context;
		}

		if (index < desc.DirEntries.Count)
		{
			context = index + 1;
			InMemoryDirectoryEntry entry = desc.DirEntries[index];
			fileName = entry.Name;
			fileInfo = ConvertInfo(entry.Info);
			return true;
		}

		fileName = null!;
		fileInfo = default;
		return false;
	}

	private static FileInfo ConvertInfo(InMemoryFileInfo src)
	{
		return new FileInfo
		{
			FileAttributes = src.FileAttributes,
			ReparseTag = 0,
			FileSize = src.FileSize,
			AllocationSize = src.AllocationSize,
			CreationTime = src.CreationTimeUtc,
			LastAccessTime = src.LastAccessTimeUtc,
			LastWriteTime = src.LastWriteTimeUtc,
			ChangeTime = src.LastWriteTimeUtc,
			IndexNumber = src.IndexNumber,
			HardLinks = 0
		};
	}
}
