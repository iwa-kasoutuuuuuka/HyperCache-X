using System;
using System.Buffers;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Fsp;
using HCX.Core;
using Microsoft.Win32.SafeHandles;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace HCX.VirtualFs;

/// <summary>
/// WinFsp user-mode passthrough filesystem accelerated by HybridCacheEngine (L1 RAM + L2 SSD).
/// </summary>
public sealed class HcxFileSystem : FileSystemBase
{
	private const int AllocationUnit = 4096;

	private readonly string _rootPath;
	private readonly HybridCacheEngine _engine;
	private readonly string _volumeLabel;
	private static readonly DirectoryEntryComparer EntryComparer = new DirectoryEntryComparer();

	private sealed class DirectoryEntryComparer : IComparer
	{
		public int Compare(object? x, object? y)
		{
			string? left = x is DictionaryEntry de1 ? de1.Key as string : null;
			string? right = y is DictionaryEntry de2 ? de2.Key as string : null;
			return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
		}
	}

	private sealed class FileDesc
	{
		public FileStream? Stream;
		public DirectoryInfo? DirInfo;
		public string FullPath;
		public string RelativePath;
		public DictionaryEntry[]? FileSystemInfos;
		public bool Deleted;

		public FileDesc(FileStream stream, string fullPath, string relativePath)
		{
			Stream = stream;
			FullPath = fullPath;
			RelativePath = relativePath;
		}

		public FileDesc(DirectoryInfo dirInfo, string fullPath, string relativePath)
		{
			DirInfo = dirInfo;
			FullPath = fullPath;
			RelativePath = relativePath;
		}
	}

	public HcxFileSystem(HybridCacheEngine engine, string volumeLabel = "HyperCache X")
	{
		_engine = engine ?? throw new ArgumentNullException(nameof(engine));
		_rootPath = Path.GetFullPath(engine.TargetRootPath).TrimEnd('\\');
		_volumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? "HyperCache X" : volumeLabel;
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
		host.FlushAndPurgeOnCleanup = false;
		host.VolumeCreationTime = (ulong)Directory.GetCreationTimeUtc(_rootPath).ToFileTimeUtc();
		host.VolumeSerialNumber = (uint)_engine.InstanceId.GetHashCode();
		host.FileSystemName = "HCX-NTFS";
		return STATUS_SUCCESS;
	}

	public override int GetVolumeInfo(out VolumeInfo volumeInfo)
	{
		volumeInfo = default;
		try
		{
			string? root = Path.GetPathRoot(_rootPath);
			if (!string.IsNullOrEmpty(root))
			{
				DriveInfo info = new DriveInfo(root);
				volumeInfo.TotalSize = (ulong)info.TotalSize;
				volumeInfo.FreeSize = (ulong)info.AvailableFreeSpace;
			}
			volumeInfo.SetVolumeLabel(_volumeLabel);
		}
		catch
		{
		}
		return STATUS_SUCCESS;
	}

	public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
	{
		string fullPath = ToFullPath(fileName);
		if (Directory.Exists(fullPath))
		{
			DirectoryInfo di = new DirectoryInfo(fullPath);
			fileAttributes = (uint)di.Attributes;
			if (securityDescriptor != null)
			{
				securityDescriptor = di.GetAccessControl().GetSecurityDescriptorBinaryForm();
			}
			return STATUS_SUCCESS;
		}
		if (!File.Exists(fullPath))
		{
			fileAttributes = 0;
			return STATUS_OBJECT_NAME_NOT_FOUND;
		}

		System.IO.FileInfo fi = new System.IO.FileInfo(fullPath);
		fileAttributes = (uint)fi.Attributes;
		if (securityDescriptor != null)
		{
			securityDescriptor = fi.GetAccessControl().GetSecurityDescriptorBinaryForm();
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
		FileDesc? desc = null;
		try
		{
			string rel = ToRelativePath(fileName);
			string fullPath = ToFullPath(fileName);

			if ((createOptions & FILE_DIRECTORY_FILE) == 0)
			{
				FileStream fs = new FileStream(
					fullPath,
					FileMode.CreateNew,
					FileAccess.ReadWrite,
					FileShare.ReadWrite | FileShare.Delete,
					4096,
					FileOptions.None);
				desc = new FileDesc(fs, fullPath, rel);
				SetFileAttributesInternal(desc, fileAttributes | (uint)FileAttributes.Archive);
				if (securityDescriptor != null && securityDescriptor.Length > 0)
				{
					TryApplyFileSecurity(fullPath, securityDescriptor);
				}
			}
			else
			{
				if (Directory.Exists(fullPath))
				{
					ThrowIoExceptionWithNtStatus(STATUS_OBJECT_NAME_COLLISION);
				}
				DirectoryInfo di = Directory.CreateDirectory(fullPath);
				desc = new FileDesc(di, fullPath, rel);
				SetFileAttributesInternal(desc, fileAttributes);
				if (securityDescriptor != null && securityDescriptor.Length > 0)
				{
					TryApplyDirSecurity(fullPath, securityDescriptor);
				}
			}

			fileNode = null!;
			fileDescOut = desc;
			normalizedName = null!;
			return GetFileInfoInternal(desc, out fileInfo);
		}
		catch
		{
			desc?.Stream?.Dispose();
			throw;
		}
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
		FileDesc? desc = null;
		try
		{
			string rel = ToRelativePath(fileName);
			string fullPath = ToFullPath(fileName);
			_engine.InvalidateBackingStatCache(rel);

			if (!Directory.Exists(fullPath))
			{
				if (!File.Exists(fullPath))
				{
					fileNode = null!;
					fileDescOut = null!;
					fileInfo = default;
					normalizedName = null!;
					return STATUS_OBJECT_NAME_NOT_FOUND;
				}

				FileAccess access = MapFileAccess(grantedAccess);
				FileStream fs;
				try
				{
					fs = new FileStream(fullPath, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);
				}
				catch (UnauthorizedAccessException) when (access == FileAccess.ReadWrite)
				{
					fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);
				}
				desc = new FileDesc(fs, fullPath, rel);
			}
			else
			{
				desc = new FileDesc(new DirectoryInfo(fullPath), fullPath, rel);
			}

			fileNode = null!;
			fileDescOut = desc;
			normalizedName = null!;
			return GetFileInfoInternal(desc, out fileInfo);
		}
		catch
		{
			desc?.Stream?.Dispose();
			throw;
		}
	}

	public override int Overwrite(
		object fileNode,
		object fileDescObj,
		uint fileAttributes,
		bool replaceFileAttributes,
		ulong allocationSize,
		out FileInfo fileInfo)
	{
		FileDesc desc = (FileDesc)fileDescObj;
		_engine.InvalidateFile(desc.RelativePath);
		if (replaceFileAttributes)
		{
			SetFileAttributesInternal(desc, fileAttributes | (uint)FileAttributes.Archive);
		}
		else if (fileAttributes != 0)
		{
			SetFileAttributesInternal(desc, GetFileAttributesInternal(desc) | fileAttributes | (uint)FileAttributes.Archive);
		}
		desc.Stream?.SetLength(0);
		_engine.InvalidateBackingStatCache(desc.RelativePath);
		return GetFileInfoInternal(desc, out fileInfo);
	}

	public override void Cleanup(object fileNode, object fileDescObj, string fileName, uint flags)
	{
		FileDesc desc = (FileDesc)fileDescObj;
		if ((flags & CleanupDelete) != 0)
		{
			desc.Deleted = true;
			_engine.InvalidateFile(desc.RelativePath);
			SetDispositionInternal(desc, safe: true);
			desc.Stream?.Dispose();
			desc.Stream = null;
		}
	}

	public override void Close(object fileNode, object fileDescObj)
	{
		FileDesc desc = (FileDesc)fileDescObj;
		desc.Stream?.Dispose();
		desc.Stream = null;
	}

	public override int Read(
		object fileNode,
		object fileDescObj,
		IntPtr buffer,
		ulong offset,
		uint length,
		out uint bytesTransferred)
	{
		FileDesc desc = (FileDesc)fileDescObj;
		if (offset > (ulong)long.MaxValue || (length > 0 && buffer == IntPtr.Zero))
		{
			ThrowIoExceptionWithWin32(87); // ERROR_INVALID_PARAMETER
		}
		if (length == 0)
		{
			bytesTransferred = 0;
			return STATUS_SUCCESS;
		}

		FileStream? stream = desc.Stream;
		int readLen = (int)Math.Min((uint)int.MaxValue, length);
		unsafe
		{
			Span<byte> destSpan = new Span<byte>(buffer.ToPointer(), readLen);
			int actual = _engine.ReadBlockRange(desc.RelativePath, (long)offset, destSpan, stream);
			if (actual <= 0)
			{
				bytesTransferred = 0;
				return STATUS_END_OF_FILE;
			}
			bytesTransferred = (uint)actual;
			return STATUS_SUCCESS;
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
		FileDesc desc = (FileDesc)fileDescObj;
		if (length > 0 && buffer == IntPtr.Zero)
		{
			ThrowIoExceptionWithWin32(87); // ERROR_INVALID_PARAMETER
		}
		FileStream? stream = desc.Stream;
		long currentSize = _engine.GetEffectiveFileSize(desc.RelativePath, stream);

		if (constrainedIo)
		{
			if (offset >= (ulong)currentSize)
			{
				bytesTransferred = 0;
				return GetFileInfoInternal(desc, out fileInfo);
			}
			if (offset + length > (ulong)currentSize)
			{
				length = (uint)((ulong)currentSize - offset);
			}
		}

		ulong writeOffset = writeToEndOfFile ? (ulong)currentSize : offset;
		if (writeOffset > (ulong)long.MaxValue || length > (uint)int.MaxValue || writeOffset + length > (ulong)long.MaxValue)
		{
			ThrowIoExceptionWithWin32(87); // ERROR_INVALID_PARAMETER
		}
		if (length == 0)
		{
			bytesTransferred = 0;
			return GetFileInfoInternal(desc, out fileInfo);
		}

		unsafe
		{
			ReadOnlySpan<byte> srcSpan = new ReadOnlySpan<byte>(buffer.ToPointer(), (int)length);
			int written = _engine.WriteBlockRange(desc.RelativePath, (long)writeOffset, srcSpan, stream);
			bytesTransferred = (uint)written;
			return GetFileInfoInternal(desc, out fileInfo);
		}
	}

	public override int Flush(object fileNode, object fileDescObj, out FileInfo fileInfo)
	{
		if (fileDescObj == null)
		{
			_engine.SyncAll();
			fileInfo = default;
			return STATUS_SUCCESS;
		}
		FileDesc desc = (FileDesc)fileDescObj;
		FileStream? stream = desc.Stream;
		_engine.FlushFile(desc.RelativePath, stream);
		stream?.Flush(flushToDisk: true);
		return GetFileInfoInternal(desc, out fileInfo);
	}

	public override int GetFileInfo(object fileNode, object fileDescObj, out FileInfo fileInfo)
	{
		FileDesc desc = (FileDesc)fileDescObj;
		return GetFileInfoInternal(desc, out fileInfo);
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
		FileDesc desc = (FileDesc)fileDescObj;
		SetBasicInfoInternal(desc, fileAttributes, creationTime, lastAccessTime, lastWriteTime);
		return GetFileInfoInternal(desc, out fileInfo);
	}

	public override int SetFileSize(
		object fileNode,
		object fileDescObj,
		ulong newSize,
		bool setAllocationSize,
		out FileInfo fileInfo)
	{
		FileDesc desc = (FileDesc)fileDescObj;
		if (newSize > (ulong)long.MaxValue)
		{
			ThrowIoExceptionWithWin32(87);
		}
		FileStream? stream = desc.Stream;
		if (stream != null)
		{
			long effectiveSize = _engine.GetEffectiveFileSize(desc.RelativePath, stream);
			if (!setAllocationSize || (ulong)effectiveSize > newSize)
			{
				_engine.FlushFile(desc.RelativePath, stream);
				stream.SetLength((long)newSize);
				_engine.TruncateFile(desc.RelativePath, (long)newSize);
			}
		}
		return GetFileInfoInternal(desc, out fileInfo);
	}

	public override int CanDelete(object fileNode, object fileDescObj, string fileName)
	{
		FileDesc desc = (FileDesc)fileDescObj;
		SetDispositionInternal(desc, safe: false);
		return STATUS_SUCCESS;
	}

	public override int Rename(
		object fileNode,
		object fileDescObj,
		string fileName,
		string newFileName,
		bool replaceIfExists)
	{
		string oldRel = ToRelativePath(fileName);
		string newRel = ToRelativePath(newFileName);
		string oldFull = ToFullPath(fileName);
		string newFull = ToFullPath(newFileName);

		_engine.FlushPrefix(oldRel);
		if (!MoveFileExW(oldFull, newFull, replaceIfExists ? 1U : 0U))
		{
			ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
		}
		if (replaceIfExists && !string.Equals(oldRel, newRel, StringComparison.OrdinalIgnoreCase))
		{
			_engine.InvalidateFile(newRel);
		}
		_engine.NotifyRename(oldRel, newRel);

		if (fileDescObj is FileDesc desc)
		{
			desc.FullPath = newFull;
			desc.RelativePath = newRel;
			if (desc.DirInfo != null)
			{
				desc.DirInfo = new DirectoryInfo(newFull);
				desc.FileSystemInfos = null;
			}
		}
		return STATUS_SUCCESS;
	}

	public override int GetSecurity(object fileNode, object fileDescObj, ref byte[] securityDescriptor)
	{
		FileDesc desc = (FileDesc)fileDescObj;
		if (desc.Stream != null)
		{
			securityDescriptor = new System.IO.FileInfo(desc.FullPath).GetAccessControl().GetSecurityDescriptorBinaryForm();
		}
		else if (desc.DirInfo != null)
		{
			securityDescriptor = desc.DirInfo.GetAccessControl().GetSecurityDescriptorBinaryForm();
		}
		return STATUS_SUCCESS;
	}

	public override int SetSecurity(
		object fileNode,
		object fileDescObj,
		AccessControlSections sections,
		byte[] securityDescriptor)
	{
		FileDesc desc = (FileDesc)fileDescObj;
		int secInfo = 0;
		if ((sections & AccessControlSections.Owner) != 0) secInfo |= 1;
		if ((sections & AccessControlSections.Group) != 0) secInfo |= 2;
		if ((sections & AccessControlSections.Access) != 0) secInfo |= 4;
		if ((sections & AccessControlSections.Audit) != 0) secInfo |= 8;

		FileStream? stream = desc.Stream;
		if (stream != null)
		{
			if (!SetKernelObjectSecurity(stream.SafeFileHandle, secInfo, securityDescriptor))
			{
				ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
			}
		}
		else if (desc.DirInfo != null)
		{
			if (!SetFileSecurityW(desc.DirInfo.FullName, secInfo, securityDescriptor))
			{
				ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
			}
		}
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
		FileDesc desc = (FileDesc)fileDescObj;
		if (desc.FileSystemInfos == null && desc.DirInfo != null)
		{
			string searchPattern = pattern != null
				? pattern.Replace('<', '*').Replace('>', '?').Replace('"', '.')
				: "*";
			SortedList list = new SortedList(EntryComparer);
			if (desc.DirInfo.Parent != null)
			{
				list.Add(".", desc.DirInfo);
				list.Add("..", desc.DirInfo.Parent);
			}
			foreach (FileSystemInfo fsi in desc.DirInfo.EnumerateFileSystemInfos(searchPattern))
			{
				if (!fsi.Name.StartsWith(".hcx_bench_", StringComparison.OrdinalIgnoreCase) &&
				    !string.Equals(fsi.Name, ".hcx", StringComparison.OrdinalIgnoreCase))
				{
					list.Add(fsi.Name, fsi);
				}
			}
			desc.FileSystemInfos = new DictionaryEntry[list.Count];
			list.CopyTo(desc.FileSystemInfos, 0);
		}

		DictionaryEntry[] entries = desc.FileSystemInfos ?? Array.Empty<DictionaryEntry>();
		int index;
		if (context == null)
		{
			index = 0;
			if (marker != null)
			{
				index = Array.BinarySearch(entries, new DictionaryEntry(marker, null), EntryComparer);
				index = index >= 0 ? index + 1 : ~index;
			}
		}
		else
		{
			index = (int)context;
		}

		if (index < entries.Length)
		{
			context = index + 1;
			fileName = (string)entries[index].Key;
			FileSystemInfo fsi = (FileSystemInfo)entries[index].Value!;
			GetFileInfoFromFileSystemInfo(fsi, out fileInfo);
			if (fsi is System.IO.FileInfo fi)
			{
				string rel = ToRelativePathFromFull(fi.FullName);
				long effSize = _engine.GetEffectiveFileSize(rel);
				if ((ulong)effSize > fileInfo.FileSize)
				{
					fileInfo.FileSize = (ulong)effSize;
					fileInfo.AllocationSize = (fileInfo.FileSize + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
				}
			}
			return true;
		}

		fileName = null!;
		fileInfo = default;
		return false;
	}

	private int GetFileInfoInternal(FileDesc desc, out FileInfo fileInfo)
	{
		FileStream? stream = desc.Stream;
		if (stream != null)
		{
			if (!GetFileInformationByHandle(stream.SafeFileHandle, out BY_HANDLE_FILE_INFORMATION info))
			{
				ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
			}
			long effectiveSize = _engine.GetEffectiveFileSize(desc.RelativePath, stream);
			fileInfo = new FileInfo
			{
				FileAttributes = info.dwFileAttributes,
				ReparseTag = 0,
				FileSize = (ulong)effectiveSize,
				AllocationSize = ((ulong)effectiveSize + AllocationUnit - 1) / AllocationUnit * AllocationUnit,
				CreationTime = info.ftCreationTime,
				LastAccessTime = info.ftLastAccessTime,
				LastWriteTime = info.ftLastWriteTime,
				ChangeTime = info.ftLastWriteTime,
				IndexNumber = 0,
				HardLinks = 0
			};
			return STATUS_SUCCESS;
		}

		GetFileInfoFromFileSystemInfo(desc.DirInfo!, out fileInfo);
		return STATUS_SUCCESS;
	}

	private static void GetFileInfoFromFileSystemInfo(FileSystemInfo fsi, out FileInfo fileInfo)
	{
		ulong size = fsi is System.IO.FileInfo fi ? (ulong)fi.Length : 0;
		fileInfo = new FileInfo
		{
			FileAttributes = (uint)fsi.Attributes,
			ReparseTag = 0,
			FileSize = size,
			AllocationSize = (size + AllocationUnit - 1) / AllocationUnit * AllocationUnit,
			CreationTime = (ulong)fsi.CreationTimeUtc.ToFileTimeUtc(),
			LastAccessTime = (ulong)fsi.LastAccessTimeUtc.ToFileTimeUtc(),
			LastWriteTime = (ulong)fsi.LastWriteTimeUtc.ToFileTimeUtc(),
			ChangeTime = (ulong)fsi.LastWriteTimeUtc.ToFileTimeUtc(),
			IndexNumber = 0,
			HardLinks = 0
		};
	}

	private uint GetFileAttributesInternal(FileDesc desc)
	{
		GetFileInfoInternal(desc, out FileInfo info);
		return info.FileAttributes;
	}

	private static void SetFileAttributesInternal(FileDesc desc, uint fileAttributes)
	{
		SetBasicInfoInternal(desc, fileAttributes, 0, 0, 0);
	}

	private static void SetBasicInfoInternal(
		FileDesc desc,
		uint fileAttributes,
		ulong creationTime,
		ulong lastAccessTime,
		ulong lastWriteTime)
	{
		if (fileAttributes == 0)
		{
			fileAttributes = (uint)FileAttributes.Normal;
		}
		FileStream? stream = desc.Stream;
		if (stream != null)
		{
			FILE_BASIC_INFO info = default;
			if (fileAttributes != unchecked((uint)-1)) info.FileAttributes = fileAttributes;
			if (creationTime != 0) info.CreationTime = creationTime;
			if (lastAccessTime != 0) info.LastAccessTime = lastAccessTime;
			if (lastWriteTime != 0) info.LastWriteTime = lastWriteTime;
			if (!SetFileInformationByHandle(stream.SafeFileHandle, 0, ref info, (uint)Marshal.SizeOf(info)))
			{
				ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
			}
		}
		else if (desc.DirInfo != null)
		{
			if (fileAttributes != unchecked((uint)-1)) desc.DirInfo.Attributes = (FileAttributes)fileAttributes;
			if (creationTime != 0) desc.DirInfo.CreationTimeUtc = DateTime.FromFileTimeUtc((long)creationTime);
			if (lastAccessTime != 0) desc.DirInfo.LastAccessTimeUtc = DateTime.FromFileTimeUtc((long)lastAccessTime);
			if (lastWriteTime != 0) desc.DirInfo.LastWriteTimeUtc = DateTime.FromFileTimeUtc((long)lastWriteTime);
		}
	}

	private static void SetDispositionInternal(FileDesc desc, bool safe)
	{
		FileStream? stream = desc.Stream;
		if (stream != null)
		{
			FILE_DISPOSITION_INFO info = new FILE_DISPOSITION_INFO { DeleteFile = true };
			if (!SetFileInformationByHandle(stream.SafeFileHandle, 4, ref info, (uint)Marshal.SizeOf(info)) && !safe)
			{
				ThrowIoExceptionWithWin32(Marshal.GetLastWin32Error());
			}
		}
		else if (desc.DirInfo != null)
		{
			try
			{
				desc.DirInfo.Delete();
			}
			catch (Exception ex) when (safe || true)
			{
				if (!safe)
				{
					ThrowIoExceptionWithHResult(ex.HResult);
				}
			}
		}
	}

	private static void TryApplyFileSecurity(string path, byte[] descriptor)
	{
		try
		{
			FileSecurity sec = new FileSecurity();
			sec.SetSecurityDescriptorBinaryForm(descriptor);
			new System.IO.FileInfo(path).SetAccessControl(sec);
		}
		catch
		{
		}
	}

	private static void TryApplyDirSecurity(string path, byte[] descriptor)
	{
		try
		{
			DirectorySecurity sec = new DirectorySecurity();
			sec.SetSecurityDescriptorBinaryForm(descriptor);
			new DirectoryInfo(path).SetAccessControl(sec);
		}
		catch
		{
		}
	}

	private static FileAccess MapFileAccess(uint grantedAccess)
	{
		const uint GenericWrite = 0x40000000;
		const uint GenericAll = 0x10000000;
		const uint FileWriteData = 0x00000002;
		const uint FileAppendData = 0x00000004;
		const uint FileWriteAttributes = 0x00000100;
		const uint MaximumAllowed = 0x02000000;

		bool wantsWrite = (grantedAccess & (GenericWrite | GenericAll | FileWriteData | FileAppendData | FileWriteAttributes | MaximumAllowed)) != 0;
		return wantsWrite ? FileAccess.ReadWrite : FileAccess.Read;
	}

	private string ToRelativePath(string winFspFileName)
	{
		return BlockKey.NormalizeRelativePath(winFspFileName);
	}

	private string ToFullPath(string winFspFileName)
	{
		if (string.IsNullOrEmpty(winFspFileName))
		{
			return _rootPath;
		}
		if (winFspFileName.IndexOf('\0') >= 0)
		{
			ThrowIoExceptionWithWin32(87); // ERROR_INVALID_PARAMETER
		}
		string rel = ToRelativePath(winFspFileName);
		if (string.IsNullOrEmpty(rel))
		{
			return _rootPath;
		}

		string combined = Path.GetFullPath(Path.Combine(_rootPath, rel));
		string rootWithSep = _rootPath + "\\";
		if (!string.Equals(combined, _rootPath, StringComparison.OrdinalIgnoreCase) &&
		    !combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
		{
			ThrowIoExceptionWithWin32(5); // ERROR_ACCESS_DENIED (path traversal attempt)
		}

		// Protect internal HCX cache directories from self-referential virtual FS recursion.
		string normalizedRel = combined.Length > rootWithSep.Length ? combined.Substring(rootWithSep.Length) : string.Empty;
		if (string.Equals(normalizedRel, ".hcx", StringComparison.OrdinalIgnoreCase) ||
		    normalizedRel.StartsWith(@".hcx\", StringComparison.OrdinalIgnoreCase) ||
		    normalizedRel.StartsWith(".hcx_bench_", StringComparison.OrdinalIgnoreCase))
		{
			ThrowIoExceptionWithWin32(5); // ERROR_ACCESS_DENIED
		}

		return combined;
	}

	private string ToRelativePathFromFull(string fullPath)
	{
		if (fullPath.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase))
		{
			return fullPath.Substring(_rootPath.Length).TrimStart('\\');
		}
		return Path.GetFileName(fullPath);
	}

	private static void ThrowIoExceptionWithHResult(int hresult) => throw new IOException(null, hresult);
	private static void ThrowIoExceptionWithWin32(int error) => ThrowIoExceptionWithHResult(unchecked((int)(0x80070000 | error)));
	private static void ThrowIoExceptionWithNtStatus(int status) => ThrowIoExceptionWithWin32((int)Win32FromNtStatus(status));

	[StructLayout(LayoutKind.Sequential, Pack = 4)]
	private struct BY_HANDLE_FILE_INFORMATION
	{
		public uint dwFileAttributes;
		public ulong ftCreationTime;
		public ulong ftLastAccessTime;
		public ulong ftLastWriteTime;
		public uint dwVolumeSerialNumber;
		public uint nFileSizeHigh;
		public uint nFileSizeLow;
		public uint nNumberOfLinks;
		public uint nFileIndexHigh;
		public uint nFileIndexLow;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FILE_BASIC_INFO
	{
		public ulong CreationTime;
		public ulong LastAccessTime;
		public ulong LastWriteTime;
		public ulong ChangeTime;
		public uint FileAttributes;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FILE_DISPOSITION_INFO
	{
		public bool DeleteFile;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass, ref FILE_BASIC_INFO lpFileInformation, uint dwBufferSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass, ref FILE_DISPOSITION_INFO lpFileInformation, uint dwBufferSize);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool MoveFileExW([MarshalAs(UnmanagedType.LPWStr)] string lpExistingFileName, [MarshalAs(UnmanagedType.LPWStr)] string lpNewFileName, uint dwFlags);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool SetFileSecurityW([MarshalAs(UnmanagedType.LPWStr)] string fileName, int securityInformation, byte[] securityDescriptor);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, int securityInformation, byte[] securityDescriptor);
}
