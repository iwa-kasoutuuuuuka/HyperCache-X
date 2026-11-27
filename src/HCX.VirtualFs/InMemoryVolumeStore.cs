using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace HCX.VirtualFs;

public readonly struct InMemoryFileInfo
{
	public uint FileAttributes { get; init; }
	public ulong FileSize { get; init; }
	public ulong AllocationSize { get; init; }
	public ulong CreationTimeUtc { get; init; }
	public ulong LastAccessTimeUtc { get; init; }
	public ulong LastWriteTimeUtc { get; init; }
	public ulong IndexNumber { get; init; }
	public bool IsDirectory { get; init; }
}

public readonly struct InMemoryDirectoryEntry
{
	public string Name { get; init; }
	public InMemoryFileInfo Info { get; init; }
}

public readonly struct RamDiskStats
{
	public long CapacityBytes { get; init; }
	public long UsedBytes { get; init; }
	public long FreeBytes { get; init; }
	public int FileCount { get; init; }
	public int DirectoryCount { get; init; }
}

public sealed class InMemoryNodeHandle
{
	internal readonly object NodeRef;

	internal InMemoryNodeHandle(object nodeRef)
	{
		NodeRef = nodeRef;
	}
}

/// <summary>
/// Thread-safe, chunked 64 KB page in-memory filesystem store with strict capacity enforcement,
/// direct node handles, and per-file DataLock for parallel multi-file I/O.
/// </summary>
public sealed class InMemoryVolumeStore
{
	public const int PageSize = 65536;
	private const int AllocationUnit = 4096;
	private const int ErrorDiskFullHResult = unchecked((int)0x80070070);

	private sealed class Node
	{
		public ulong Id;
		public long Generation;
		public string Name = string.Empty;
		public bool IsDirectory;
		public volatile bool Deleted;
		public uint Attributes;
		public long CreationUtcTicks;
		public long LastAccessUtcTicks;
		public long LastWriteUtcTicks;
		public byte[] SecurityDescriptor = Array.Empty<byte>();
		public long FileSize;
		public readonly List<byte[]> Pages = new List<byte[]>();
		public Dictionary<string, Node>? Children;

		public Node(bool isDirectory, long generation)
		{
			IsDirectory = isDirectory;
			Generation = generation;
		}
	}

	private const int DataLockStripeCount = 64;
	private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
	private readonly ReaderWriterLockSlim[] _dataLockStripes = new ReaderWriterLockSlim[DataLockStripeCount];
	private readonly long _maxPages;
	private readonly byte[] _defaultFileSd;
	private readonly byte[] _defaultDirSd;
	private Node _root;
	private long _generation = 1;
	private ulong _nextNodeId = 1;
	private long _allocatedPages;
	private int _fileCount;
	private int _dirCount;

	public long CapacityBytes { get; }

	public InMemoryVolumeStore(long capacityBytes)
	{
		if (capacityBytes < PageSize)
		{
			throw new ArgumentOutOfRangeException(nameof(capacityBytes), $"Capacity must be at least {PageSize} bytes.");
		}
		for (int i = 0; i < DataLockStripeCount; i++)
		{
			_dataLockStripes[i] = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
		}
		_maxPages = capacityBytes / PageSize;
		CapacityBytes = _maxPages * PageSize;

		_defaultFileSd = CreateDefaultSecurityDescriptor(isDirectory: false);
		_defaultDirSd = CreateDefaultSecurityDescriptor(isDirectory: true);
		_root = CreateRootNode();
	}

	private ReaderWriterLockSlim GetDataLock(Node node) => _dataLockStripes[(int)(node.Id & (DataLockStripeCount - 1))];

	private bool IsNodeLive(Node node) => !node.Deleted && node.Generation == Volatile.Read(ref _generation);

	public RamDiskStats GetStats()
	{
		_lock.EnterReadLock();
		try
		{
			long used = Volatile.Read(ref _allocatedPages) * PageSize;
			return new RamDiskStats
			{
				CapacityBytes = CapacityBytes,
				UsedBytes = used,
				FreeBytes = Math.Max(0, CapacityBytes - used),
				FileCount = _fileCount,
				DirectoryCount = _dirCount
			};
		}
		finally
		{
			_lock.ExitReadLock();
		}
	}

	public bool TryOpenHandle(string path, out InMemoryNodeHandle? handle, out InMemoryFileInfo info)
	{
		_lock.EnterReadLock();
		try
		{
			Node? node = ResolveNodeLocked(path);
			if (node == null)
			{
				handle = null;
				info = default;
				return false;
			}
			handle = new InMemoryNodeHandle(node);
			info = ReadNodeInfoSafely(node);
			return true;
		}
		finally
		{
			_lock.ExitReadLock();
		}
	}

	public bool TryGetInfoByHandle(InMemoryNodeHandle handle, out InMemoryFileInfo info)
	{
		Node node = (Node)handle.NodeRef;
		if (!IsNodeLive(node))
		{
			info = default;
			return false;
		}
		info = ReadNodeInfoSafely(node);
		return true;
	}

	private InMemoryFileInfo ReadNodeInfoSafely(Node node)
	{
		if (!node.IsDirectory)
		{
			ReaderWriterLockSlim dataLock = GetDataLock(node);
			dataLock.EnterReadLock();
			try
			{
				return ToFileInfo(node);
			}
			finally
			{
				dataLock.ExitReadLock();
			}
		}
		return ToFileInfo(node);
	}

	public bool Exists(string path)
	{
		_lock.EnterReadLock();
		try
		{
			return ResolveNodeLocked(path) != null;
		}
		finally
		{
			_lock.ExitReadLock();
		}
	}

	public bool TryGetInfo(string path, out InMemoryFileInfo info, out byte[] securityDescriptor)
	{
		_lock.EnterReadLock();
		try
		{
			Node? node = ResolveNodeLocked(path);
			if (node == null)
			{
				info = default;
				securityDescriptor = Array.Empty<byte>();
				return false;
			}
			info = ReadNodeInfoSafely(node);
			securityDescriptor = (byte[])node.SecurityDescriptor.Clone();
			return true;
		}
		finally
		{
			_lock.ExitReadLock();
		}
	}

	public InMemoryFileInfo CreateEntry(string path, bool isDirectory, uint attributes, byte[]? securityDescriptor, ulong allocationSize)
	{
		return CreateEntryWithHandle(path, isDirectory, attributes, securityDescriptor, allocationSize, out _);
	}

	public InMemoryFileInfo CreateEntryWithHandle(string path, bool isDirectory, uint attributes, byte[]? securityDescriptor, ulong allocationSize, out InMemoryNodeHandle handle)
	{
		_lock.EnterWriteLock();
		try
		{
			(Node? parent, string name) = ResolveParentLocked(path);
			if (parent == null || !parent.IsDirectory || string.IsNullOrEmpty(name))
			{
				ThrowWin32(3); // ERROR_PATH_NOT_FOUND
			}
			if (parent.Children!.ContainsKey(name))
			{
				ThrowWin32(isDirectory ? 183 : 80); // ERROR_ALREADY_EXISTS / ERROR_FILE_EXISTS
			}

			long now = DateTime.UtcNow.ToFileTimeUtc();
			uint cleanAttr = isDirectory
				? (attributes == 0 ? (uint)FileAttributes.Directory : (attributes | (uint)FileAttributes.Directory))
				: (attributes == 0 ? (uint)FileAttributes.Archive : ((attributes & ~(uint)FileAttributes.Directory) | (uint)FileAttributes.Archive));

			byte[] validatedSd;
			if (securityDescriptor != null && securityDescriptor.Length > 0)
			{
				try
				{
					RawSecurityDescriptor parsed = new RawSecurityDescriptor(securityDescriptor, 0);
					validatedSd = new byte[parsed.BinaryLength];
					parsed.GetBinaryForm(validatedSd, 0);
				}
				catch (Exception)
				{
					ThrowWin32(1338); // ERROR_INVALID_SECURITY_DESCR
					handle = null!;
					return default;
				}
			}
			else
			{
				validatedSd = (byte[])(isDirectory ? _defaultDirSd : _defaultFileSd).Clone();
			}

			Node node = new Node(isDirectory, Volatile.Read(ref _generation))
			{
				Id = _nextNodeId++,
				Name = name,
				Attributes = cleanAttr,
				CreationUtcTicks = now,
				LastAccessUtcTicks = now,
				LastWriteUtcTicks = now,
				SecurityDescriptor = validatedSd,
				Children = isDirectory ? new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase) : null
			};

			if (!isDirectory && allocationSize > 0)
			{
				if (allocationSize > (ulong)CapacityBytes)
				{
					throw new IOException("RAM disk capacity exceeded.", ErrorDiskFullHResult);
				}
				long neededPages = (long)((allocationSize + PageSize - 1) / PageSize);
				ReservePagesAtomic(neededPages);
				while (node.Pages.Count < neededPages)
				{
					node.Pages.Add(new byte[PageSize]);
				}
			}

			parent.Children![name] = node;
			parent.LastWriteUtcTicks = now;
			if (isDirectory) _dirCount++;
			else _fileCount++;

			handle = new InMemoryNodeHandle(node);
			return ToFileInfo(node);
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	public int ReadFile(string path, ulong offset, Span<byte> destination, out bool endOfFile)
	{
		Node? node;
		_lock.EnterReadLock();
		try
		{
			node = ResolveNodeLocked(path);
		}
		finally
		{
			_lock.ExitReadLock();
		}
		if (node == null)
		{
			ThrowWin32(2); // ERROR_FILE_NOT_FOUND
		}
		return ReadFileFromNode(node!, offset, destination, out endOfFile);
	}

	public int ReadFileByHandle(InMemoryNodeHandle handle, ulong offset, Span<byte> destination, out bool endOfFile)
	{
		Node node = (Node)handle.NodeRef;
		if (!IsNodeLive(node))
		{
			ThrowWin32(2); // ERROR_FILE_NOT_FOUND
		}
		return ReadFileFromNode(node, offset, destination, out endOfFile);
	}

	private int ReadFileFromNode(Node node, ulong offset, Span<byte> destination, out bool endOfFile)
	{
		endOfFile = false;
		if (node.IsDirectory)
		{
			ThrowWin32(5); // ERROR_ACCESS_DENIED
		}
		if (offset > (ulong)long.MaxValue)
		{
			ThrowWin32(87); // ERROR_INVALID_PARAMETER
		}

		ReaderWriterLockSlim dataLock = GetDataLock(node);
		dataLock.EnterReadLock();
		try
		{
			if (!IsNodeLive(node))
			{
				ThrowWin32(2);
			}
			if (offset >= (ulong)node.FileSize)
			{
				endOfFile = true;
				return 0;
			}
			if (destination.IsEmpty)
			{
				return 0;
			}

			int toRead = (int)Math.Min((ulong)destination.Length, (ulong)node.FileSize - offset);
			int copied = 0;
			while (copied < toRead)
			{
				long pos = (long)offset + copied;
				int pageIndex = (int)(pos / PageSize);
				int pageOffset = (int)(pos % PageSize);
				int chunk = Math.Min(toRead - copied, PageSize - pageOffset);

				if (pageIndex < node.Pages.Count)
				{
					node.Pages[pageIndex].AsSpan(pageOffset, chunk).CopyTo(destination.Slice(copied, chunk));
				}
				else
				{
					destination.Slice(copied, chunk).Clear();
				}
				copied += chunk;
			}
			return copied;
		}
		finally
		{
			dataLock.ExitReadLock();
		}
	}

	public InMemoryFileInfo WriteFile(string path, ulong offset, ReadOnlySpan<byte> source, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred)
	{
		Node? node;
		_lock.EnterReadLock();
		try
		{
			node = ResolveNodeLocked(path);
		}
		finally
		{
			_lock.ExitReadLock();
		}
		if (node == null)
		{
			ThrowWin32(2);
		}
		return WriteFileToNode(node!, offset, source, writeToEndOfFile, constrainedIo, out bytesTransferred);
	}

	public InMemoryFileInfo WriteFileByHandle(InMemoryNodeHandle handle, ulong offset, ReadOnlySpan<byte> source, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred)
	{
		Node node = (Node)handle.NodeRef;
		if (!IsNodeLive(node))
		{
			ThrowWin32(2);
		}
		return WriteFileToNode(node, offset, source, writeToEndOfFile, constrainedIo, out bytesTransferred);
	}

	private InMemoryFileInfo WriteFileToNode(Node node, ulong offset, ReadOnlySpan<byte> source, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred)
	{
		if (node.IsDirectory)
		{
			ThrowWin32(5);
		}
		if (!writeToEndOfFile && offset > (ulong)long.MaxValue)
		{
			ThrowWin32(87); // ERROR_INVALID_PARAMETER
		}

		ReaderWriterLockSlim dataLock = GetDataLock(node);
		dataLock.EnterWriteLock();
		try
		{
			if (!IsNodeLive(node))
			{
				ThrowWin32(2);
			}
			long writeOffset = writeToEndOfFile ? node.FileSize : (long)offset;
			int length = source.Length;

			if (constrainedIo)
			{
				if (writeOffset >= node.FileSize)
				{
					bytesTransferred = 0;
					return ToFileInfo(node);
				}
				if (writeOffset + length > node.FileSize)
				{
					length = (int)(node.FileSize - writeOffset);
				}
			}

			if (length == 0)
			{
				bytesTransferred = 0;
				return ToFileInfo(node);
			}

			if (writeOffset > long.MaxValue - length)
			{
				ThrowWin32(87); // ERROR_INVALID_PARAMETER
			}
			long newEnd = writeOffset + length;
			if (newEnd > CapacityBytes)
			{
				throw new IOException("RAM disk capacity exceeded.", ErrorDiskFullHResult);
			}
			long requiredPages = (newEnd + PageSize - 1) / PageSize;
			if (requiredPages > node.Pages.Count)
			{
				long delta = requiredPages - node.Pages.Count;
				ReservePagesAtomic(delta);
				while (node.Pages.Count < requiredPages)
				{
					node.Pages.Add(new byte[PageSize]);
				}
			}

			int written = 0;
			while (written < length)
			{
				long pos = writeOffset + written;
				int pageIndex = (int)(pos / PageSize);
				int pageOffset = (int)(pos % PageSize);
				int chunk = Math.Min(length - written, PageSize - pageOffset);

				source.Slice(written, chunk).CopyTo(node.Pages[pageIndex].AsSpan(pageOffset, chunk));
				written += chunk;
			}

			if (newEnd > node.FileSize)
			{
				node.FileSize = newEnd;
			}
			long now = DateTime.UtcNow.ToFileTimeUtc();
			node.LastWriteUtcTicks = now;
			node.LastAccessUtcTicks = now;

			bytesTransferred = (uint)written;
			return ToFileInfo(node);
		}
		finally
		{
			dataLock.ExitWriteLock();
		}
	}

	public InMemoryFileInfo OverwriteFile(string path, uint attributes, bool replaceAttributes, ulong allocationSize)
	{
		_lock.EnterWriteLock();
		try
		{
			Node? node = ResolveNodeLocked(path);
			if (node == null || node.IsDirectory)
			{
				ThrowWin32(2);
			}

			if (allocationSize > (ulong)CapacityBytes)
			{
				throw new IOException("RAM disk capacity exceeded.", ErrorDiskFullHResult);
			}
			ReaderWriterLockSlim dataLock = GetDataLock(node!);
			dataLock.EnterWriteLock();
			try
			{
				ResizeNodePagesLocked(node!, (long)allocationSize);
				node!.FileSize = 0;
				if (replaceAttributes)
				{
					node.Attributes = attributes | (uint)FileAttributes.Archive;
				}
				else if (attributes != 0)
				{
					node.Attributes |= attributes | (uint)FileAttributes.Archive;
				}
				long now = DateTime.UtcNow.ToFileTimeUtc();
				node.LastWriteUtcTicks = now;
				node.LastAccessUtcTicks = now;
				return ToFileInfo(node);
			}
			finally
			{
				dataLock.ExitWriteLock();
			}
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	public InMemoryFileInfo SetFileSize(string path, long newSize, bool setAllocationSize)
	{
		_lock.EnterWriteLock();
		try
		{
			Node? node = ResolveNodeLocked(path);
			if (node == null || node.IsDirectory)
			{
				ThrowWin32(2);
			}

			if (newSize < 0)
			{
				ThrowWin32(87);
			}
			ReaderWriterLockSlim dataLock = GetDataLock(node!);
			dataLock.EnterWriteLock();
			try
			{
				if (setAllocationSize)
				{
					long targetBytes = Math.Max(newSize, node!.FileSize);
					if (newSize < node.FileSize)
					{
						TrimEndOfFileBytesLocked(node, newSize);
						node.FileSize = newSize;
						targetBytes = newSize;
					}
					ResizeNodePagesLocked(node, targetBytes);
				}
				else
				{
					if (newSize < node!.FileSize)
					{
						TrimEndOfFileBytesLocked(node, newSize);
					}
					ResizeNodePagesLocked(node, newSize);
					node.FileSize = newSize;
				}
				node.LastWriteUtcTicks = DateTime.UtcNow.ToFileTimeUtc();
				return ToFileInfo(node);
			}
			finally
			{
				dataLock.ExitWriteLock();
			}
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	public InMemoryFileInfo SetBasicInfo(string path, uint attributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime)
	{
		_lock.EnterWriteLock();
		try
		{
			Node? node = ResolveNodeLocked(path);
			if (node == null)
			{
				ThrowWin32(2);
			}
			if (attributes != unchecked((uint)-1))
			{
				node!.Attributes = attributes == 0 ? (uint)FileAttributes.Normal : attributes;
				if (node.IsDirectory)
				{
					node.Attributes |= (uint)FileAttributes.Directory;
				}
			}
			if (creationTime != 0) node!.CreationUtcTicks = (long)creationTime;
			if (lastAccessTime != 0) node!.LastAccessUtcTicks = (long)lastAccessTime;
			if (lastWriteTime != 0) node!.LastWriteUtcTicks = (long)lastWriteTime;
			return ToFileInfo(node!);
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	public void CanDelete(string path)
	{
		_lock.EnterReadLock();
		try
		{
			Node? node = ResolveNodeLocked(path);
			if (node == null)
			{
				ThrowWin32(2);
			}
			if (node!.IsDirectory && node.Children != null && node.Children.Count > 0)
			{
				ThrowWin32(145); // ERROR_DIR_NOT_EMPTY
			}
		}
		finally
		{
			_lock.ExitReadLock();
		}
	}

	public bool Delete(string path)
	{
		_lock.EnterWriteLock();
		try
		{
			(Node? parent, string name) = ResolveParentLocked(path);
			if (parent == null || !parent.IsDirectory || string.IsNullOrEmpty(name))
			{
				return false;
			}
			if (!parent.Children!.TryGetValue(name, out Node? target))
			{
				return false;
			}
			if (target.IsDirectory && target.Children != null && target.Children.Count > 0)
			{
				return false;
			}

			parent.Children.Remove(name);
			parent.LastWriteUtcTicks = DateTime.UtcNow.ToFileTimeUtc();

			if (target.IsDirectory)
			{
				target.Deleted = true;
				_dirCount = Math.Max(0, _dirCount - 1);
			}
			else
			{
				ReaderWriterLockSlim dataLock = GetDataLock(target);
				dataLock.EnterWriteLock();
				try
				{
					target.Deleted = true;
					ReleasePagesAtomic(target.Pages.Count);
					target.Pages.Clear();
					target.FileSize = 0;
				}
				finally
				{
					dataLock.ExitWriteLock();
				}
				_fileCount = Math.Max(0, _fileCount - 1);
			}
			return true;
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	public void Rename(string oldPath, string newPath, bool replaceIfExists)
	{
		_lock.EnterWriteLock();
		try
		{
			(Node? oldParent, string oldName) = ResolveParentLocked(oldPath);
			Node? moving = null;
			if (oldParent == null || !oldParent.IsDirectory || !oldParent.Children!.TryGetValue(oldName, out moving))
			{
				ThrowWin32(2);
			}

			(Node? newParent, string newName) = ResolveParentLocked(newPath);
			if (newParent == null || !newParent.IsDirectory || string.IsNullOrEmpty(newName))
			{
				ThrowWin32(3);
			}
			if (moving!.IsDirectory && IsSubtreeContainsLocked(moving, newParent!))
			{
				ThrowWin32(87); // ERROR_INVALID_PARAMETER (cannot move directory into itself/descendant)
			}

			if (newParent!.Children!.TryGetValue(newName, out Node? existingTarget))
			{
				if (!ReferenceEquals(existingTarget, moving))
				{
					if (!replaceIfExists)
					{
						ThrowWin32(183); // ERROR_ALREADY_EXISTS
					}
					if (existingTarget.IsDirectory && existingTarget.Children!.Count > 0)
					{
						ThrowWin32(5); // ERROR_ACCESS_DENIED
					}
					newParent.Children.Remove(newName);
					if (existingTarget.IsDirectory)
					{
						existingTarget.Deleted = true;
						_dirCount = Math.Max(0, _dirCount - 1);
					}
					else
					{
						ReaderWriterLockSlim dataLock = GetDataLock(existingTarget);
						dataLock.EnterWriteLock();
						try
						{
							existingTarget.Deleted = true;
							ReleasePagesAtomic(existingTarget.Pages.Count);
							existingTarget.Pages.Clear();
							existingTarget.FileSize = 0;
						}
						finally
						{
							dataLock.ExitWriteLock();
						}
						_fileCount = Math.Max(0, _fileCount - 1);
					}
				}
			}

			oldParent!.Children!.Remove(oldName);
			moving!.Name = newName;
			newParent.Children[newName] = moving;
			long now = DateTime.UtcNow.ToFileTimeUtc();
			oldParent.LastWriteUtcTicks = now;
			newParent.LastWriteUtcTicks = now;
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	public byte[] GetSecurity(string path)
	{
		_lock.EnterReadLock();
		try
		{
			Node? node = ResolveNodeLocked(path);
			if (node == null)
			{
				ThrowWin32(2);
			}
			return (byte[])node!.SecurityDescriptor.Clone();
		}
		finally
		{
			_lock.ExitReadLock();
		}
	}

	public void SetSecurity(string path, AccessControlSections sections, byte[] modifierDescriptor)
	{
		_lock.EnterWriteLock();
		try
		{
			Node? node = ResolveNodeLocked(path);
			if (node == null)
			{
				ThrowWin32(2);
			}

			RawSecurityDescriptor current = new RawSecurityDescriptor(node!.SecurityDescriptor, 0);
			RawSecurityDescriptor incoming;
			try
			{
				incoming = new RawSecurityDescriptor(modifierDescriptor, 0);
			}
			catch (Exception)
			{
				ThrowWin32(1338); // ERROR_INVALID_SECURITY_DESCR
				return;
			}

			if ((sections & AccessControlSections.Owner) != 0 && incoming.Owner != null) current.Owner = incoming.Owner;
			if ((sections & AccessControlSections.Group) != 0 && incoming.Group != null) current.Group = incoming.Group;
			if ((sections & AccessControlSections.Access) != 0) current.DiscretionaryAcl = incoming.DiscretionaryAcl;
			if ((sections & AccessControlSections.Audit) != 0) current.SystemAcl = incoming.SystemAcl;

			byte[] updated = new byte[current.BinaryLength];
			current.GetBinaryForm(updated, 0);
			node.SecurityDescriptor = updated;
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	public List<InMemoryDirectoryEntry> EnumerateDirectory(string path, string? pattern)
	{
		_lock.EnterReadLock();
		try
		{
			(Node? dir, Node? parent) = ResolveNodeWithParentLocked(path);
			if (dir == null || !dir.IsDirectory || dir.Children == null)
			{
				return new List<InMemoryDirectoryEntry>();
			}

			string normalizedPattern = string.IsNullOrEmpty(pattern) ? "*" : pattern.Replace('<', '*').Replace('>', '?').Replace('"', '.');
			List<InMemoryDirectoryEntry> list = new List<InMemoryDirectoryEntry>();

			if (parent != null)
			{
				list.Add(new InMemoryDirectoryEntry { Name = ".", Info = ToFileInfo(dir) });
				list.Add(new InMemoryDirectoryEntry { Name = "..", Info = ToFileInfo(parent) });
			}

			foreach (Node child in dir.Children.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
			{
				if (WildcardMatch(child.Name, normalizedPattern))
				{
					list.Add(new InMemoryDirectoryEntry
					{
						Name = child.Name,
						Info = ToFileInfo(child)
					});
				}
			}
			return list;
		}
		finally
		{
			_lock.ExitReadLock();
		}
	}

	public void Clear()
	{
		_lock.EnterWriteLock();
		try
		{
			Interlocked.Increment(ref _generation);
			_root = CreateRootNode();
			Volatile.Write(ref _allocatedPages, 0);
			_fileCount = 0;
			_dirCount = 0;
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	/// <summary>
	/// Persists the entire in-memory directory tree and file contents to a host directory for restart recovery.
	/// Rejects raw device paths and skips reparse points.
	/// </summary>
	public (int FilesSaved, int DirectoriesSaved, long BytesSaved) SaveToDirectory(string backupDirectory)
	{
		ValidateBackupDirectory(backupDirectory, mustExist: false);
		string rootFull = Path.GetFullPath(backupDirectory);
		Directory.CreateDirectory(rootFull);

		int filesSaved = 0;
		int dirsSaved = 0;
		long bytesSaved = 0;

		_lock.EnterReadLock();
		try
		{
			SaveNodeRecursiveLocked(_root, rootFull, ref filesSaved, ref dirsSaved, ref bytesSaved);
		}
		finally
		{
			_lock.ExitReadLock();
		}
		return (filesSaved, dirsSaved, bytesSaved);
	}

	/// <summary>
	/// Restores files and directories from a backup directory into the in-memory RAM disk within CapacityBytes.
	/// </summary>
	public (int FilesLoaded, int DirectoriesLoaded, long BytesLoaded) LoadFromDirectory(string backupDirectory)
	{
		ValidateBackupDirectory(backupDirectory, mustExist: false);
		string rootFull = Path.GetFullPath(backupDirectory);
		if (!Directory.Exists(rootFull))
		{
			return (0, 0, 0);
		}

		int filesLoaded = 0;
		int dirsLoaded = 0;
		long bytesLoaded = 0;

		LoadDirectoryRecursive(rootFull, "\\", ref filesLoaded, ref dirsLoaded, ref bytesLoaded);
		return (filesLoaded, dirsLoaded, bytesLoaded);
	}

	private void SaveNodeRecursiveLocked(Node dirNode, string currentHostDir, ref int filesSaved, ref int dirsSaved, ref long bytesSaved)
	{
		if (!dirNode.IsDirectory || dirNode.Children == null)
		{
			return;
		}
		foreach (Node child in dirNode.Children.Values)
		{
			if (string.IsNullOrEmpty(child.Name) || child.Name == "." || child.Name == ".." || child.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			{
				continue;
			}
			string targetPath = Path.Combine(currentHostDir, child.Name);
			if ((File.Exists(targetPath) || Directory.Exists(targetPath)) &&
			    (File.GetAttributes(targetPath) & FileAttributes.ReparsePoint) != 0)
			{
				continue;
			}
			if (child.IsDirectory)
			{
				Directory.CreateDirectory(targetPath);
				dirsSaved++;
				SaveNodeRecursiveLocked(child, targetPath, ref filesSaved, ref dirsSaved, ref bytesSaved);
			}
			else
			{
				ReaderWriterLockSlim dataLock = GetDataLock(child);
				dataLock.EnterReadLock();
				try
				{
					using FileStream fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
					long remaining = child.FileSize;
					for (int p = 0; p < child.Pages.Count && remaining > 0; p++)
					{
						int toWrite = (int)Math.Min(remaining, PageSize);
						fs.Write(child.Pages[p], 0, toWrite);
						remaining -= toWrite;
						bytesSaved += toWrite;
					}
					filesSaved++;
				}
				finally
				{
					dataLock.ExitReadLock();
				}
			}
		}
	}

	private void LoadDirectoryRecursive(string hostDir, string virtualParent, ref int filesLoaded, ref int dirsLoaded, ref long bytesLoaded)
	{
		foreach (string dir in Directory.EnumerateDirectories(hostDir))
		{
			try
			{
				if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
				string name = Path.GetFileName(dir);
				string vPath = virtualParent == "\\" ? "\\" + name : virtualParent + "\\" + name;
				if (!Exists(vPath))
				{
					CreateEntry(vPath, isDirectory: true, (uint)FileAttributes.Directory, null, 0);
					dirsLoaded++;
				}
				LoadDirectoryRecursive(dir, vPath, ref filesLoaded, ref dirsLoaded, ref bytesLoaded);
			}
			catch
			{
			}
		}

		foreach (string file in Directory.EnumerateFiles(hostDir))
		{
			try
			{
				if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
				FileInfo fi = new FileInfo(file);
				if (fi.Length > CapacityBytes) continue;

				string name = Path.GetFileName(file);
				string vPath = virtualParent == "\\" ? "\\" + name : virtualParent + "\\" + name;
				InMemoryNodeHandle handle;
				if (!TryOpenHandle(vPath, out InMemoryNodeHandle? existing, out _))
				{
					CreateEntryWithHandle(vPath, isDirectory: false, (uint)FileAttributes.Archive, null, 0, out handle);
				}
				else
				{
					handle = existing!;
				}

				byte[] chunkBuf = new byte[PageSize];
				using FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				ulong offset = 0;
				while (true)
				{
					int read = fs.Read(chunkBuf, 0, chunkBuf.Length);
					if (read <= 0) break;
					WriteFileByHandle(handle, offset, chunkBuf.AsSpan(0, read), writeToEndOfFile: false, constrainedIo: false, out uint written);
					offset += written;
					bytesLoaded += written;
				}
				filesLoaded++;
			}
			catch (IOException ex) when (ex.HResult == ErrorDiskFullHResult)
			{
				break;
			}
			catch
			{
			}
		}
	}

	private static void ValidateBackupDirectory(string backupDirectory, bool mustExist)
	{
		if (string.IsNullOrWhiteSpace(backupDirectory) || backupDirectory.IndexOf('\0') >= 0)
		{
			throw new ArgumentException("Backup directory path is invalid.", nameof(backupDirectory));
		}
		if (backupDirectory.StartsWith(@"\\?\", StringComparison.Ordinal) ||
		    backupDirectory.StartsWith(@"\\.\", StringComparison.Ordinal))
		{
			throw new ArgumentException("Raw device paths are disallowed for RAM disk backup.", nameof(backupDirectory));
		}
		string full = Path.GetFullPath(backupDirectory);
		if ((Directory.Exists(full) || File.Exists(full)) &&
		    (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
		{
			throw new UnauthorizedAccessException($"Refusing to use reparse point as RAM disk backup directory: {full}");
		}
		if (mustExist && !Directory.Exists(full))
		{
			throw new DirectoryNotFoundException($"Backup directory not found: {backupDirectory}");
		}
	}

	private Node CreateRootNode()
	{
		long now = DateTime.UtcNow.ToFileTimeUtc();
		return new Node(isDirectory: true, Volatile.Read(ref _generation))
		{
			Id = _nextNodeId++,
			Name = string.Empty,
			Attributes = (uint)FileAttributes.Directory,
			CreationUtcTicks = now,
			LastAccessUtcTicks = now,
			LastWriteUtcTicks = now,
			SecurityDescriptor = (byte[])_defaultDirSd.Clone(),
			Children = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase)
		};
	}

	private void ReservePagesAtomic(long deltaPages)
	{
		if (deltaPages <= 0)
		{
			return;
		}
		while (true)
		{
			long current = Volatile.Read(ref _allocatedPages);
			long next = current + deltaPages;
			if (next > _maxPages)
			{
				throw new IOException("RAM disk capacity exceeded.", ErrorDiskFullHResult);
			}
			if (Interlocked.CompareExchange(ref _allocatedPages, next, current) == current)
			{
				return;
			}
		}
	}

	private void ReleasePagesAtomic(long deltaPages)
	{
		if (deltaPages <= 0)
		{
			return;
		}
		while (true)
		{
			long current = Volatile.Read(ref _allocatedPages);
			long next = Math.Max(0, current - deltaPages);
			if (Interlocked.CompareExchange(ref _allocatedPages, next, current) == current)
			{
				return;
			}
		}
	}

	private void ResizeNodePagesLocked(Node node, long targetBytes)
	{
		long targetPages = targetBytes <= 0 ? 0 : (targetBytes + PageSize - 1) / PageSize;
		if (targetPages > node.Pages.Count)
		{
			ReservePagesAtomic(targetPages - node.Pages.Count);
			while (node.Pages.Count < targetPages)
			{
				node.Pages.Add(new byte[PageSize]);
			}
		}
		else if (targetPages < node.Pages.Count)
		{
			int toRemove = node.Pages.Count - (int)targetPages;
			node.Pages.RemoveRange((int)targetPages, toRemove);
			ReleasePagesAtomic(toRemove);
		}
	}

	private static void TrimEndOfFileBytesLocked(Node node, long newSize)
	{
		if (newSize <= 0)
		{
			return;
		}
		int lastPageIndex = (int)((newSize - 1) / PageSize);
		int usedInLastPage = (int)(newSize - (long)lastPageIndex * PageSize);
		if (lastPageIndex < node.Pages.Count && usedInLastPage < PageSize)
		{
			Array.Clear(node.Pages[lastPageIndex], usedInLastPage, PageSize - usedInLastPage);
		}
	}

	private Node? ResolveNodeLocked(string path)
	{
		(Node? node, _) = ResolveNodeWithParentLocked(path);
		return node;
	}

	private (Node? Node, Node? Parent) ResolveNodeWithParentLocked(string path)
	{
		string trimmed = (path ?? string.Empty).Replace('/', '\\').Trim('\\');
		if (trimmed.Length == 0)
		{
			return (_root, null);
		}

		string[] parts = trimmed.Split('\\', StringSplitOptions.RemoveEmptyEntries);
		Node current = _root;
		Node? parent = null;
		foreach (string part in parts)
		{
			if (!current.IsDirectory || current.Children == null || !current.Children.TryGetValue(part, out Node? next))
			{
				return (null, null);
			}
			parent = current;
			current = next;
		}
		return (current, parent);
	}

	private (Node? Parent, string Name) ResolveParentLocked(string path)
	{
		string trimmed = (path ?? string.Empty).Replace('/', '\\').Trim('\\');
		if (trimmed.Length == 0)
		{
			return (null, string.Empty);
		}
		int lastSep = trimmed.LastIndexOf('\\');
		if (lastSep < 0)
		{
			return (_root, trimmed);
		}
		string parentPath = trimmed.Substring(0, lastSep);
		string name = trimmed.Substring(lastSep + 1);
		return (ResolveNodeLocked(parentPath), name);
	}

	private static InMemoryFileInfo ToFileInfo(Node n)
	{
		ulong size = n.IsDirectory ? 0UL : (ulong)n.FileSize;
		return new InMemoryFileInfo
		{
			FileAttributes = n.Attributes,
			FileSize = size,
			AllocationSize = (size + AllocationUnit - 1) / AllocationUnit * AllocationUnit,
			CreationTimeUtc = (ulong)n.CreationUtcTicks,
			LastAccessTimeUtc = (ulong)n.LastAccessUtcTicks,
			LastWriteTimeUtc = (ulong)n.LastWriteUtcTicks,
			IndexNumber = n.Id,
			IsDirectory = n.IsDirectory
		};
	}

	private static byte[] CreateDefaultSecurityDescriptor(bool isDirectory)
	{
		FileSystemSecurity sec = isDirectory ? new DirectorySecurity() : new FileSecurity();
		SecurityIdentifier everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
		InheritanceFlags inherit = isDirectory
			? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
			: InheritanceFlags.None;
		sec.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
		return sec.GetSecurityDescriptorBinaryForm();
	}

	private static bool WildcardMatch(string text, string pattern)
	{
		if (pattern == "*" || pattern == "*.*")
		{
			return true;
		}

		int ti = 0, pi = 0, starIdx = -1, matchIdx = 0;
		while (ti < text.Length)
		{
			if (pi < pattern.Length && (pattern[pi] == '?' || char.ToUpperInvariant(pattern[pi]) == char.ToUpperInvariant(text[ti])))
			{
				ti++;
				pi++;
			}
			else if (pi < pattern.Length && pattern[pi] == '*')
			{
				starIdx = pi++;
				matchIdx = ti;
			}
			else if (starIdx != -1)
			{
				pi = starIdx + 1;
				ti = ++matchIdx;
			}
			else
			{
				return false;
			}
		}
		while (pi < pattern.Length && pattern[pi] == '*')
		{
			pi++;
		}
		return pi == pattern.Length;
	}

	private static bool IsSubtreeContainsLocked(Node root, Node target)
	{
		if (ReferenceEquals(root, target))
		{
			return true;
		}
		if (!root.IsDirectory || root.Children == null)
		{
			return false;
		}
		foreach (Node child in root.Children.Values)
		{
			if (IsSubtreeContainsLocked(child, target))
			{
				return true;
			}
		}
		return false;
	}

	[System.Diagnostics.CodeAnalysis.DoesNotReturn]
	private static void ThrowWin32(int win32Error)
	{
		throw new IOException(null, unchecked((int)(0x80070000 | (uint)win32Error)));
	}
}
