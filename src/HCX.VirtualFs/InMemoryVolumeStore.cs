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

/// <summary>
/// Thread-safe, chunked 64 KB page in-memory filesystem store with strict capacity enforcement.
/// </summary>
public sealed class InMemoryVolumeStore
{
	public const int PageSize = 65536;
	private const int AllocationUnit = 4096;
	private const int ErrorDiskFullHResult = unchecked((int)0x80070070);

	private sealed class Node
	{
		public ulong Id;
		public string Name = string.Empty;
		public bool IsDirectory;
		public uint Attributes;
		public long CreationUtcTicks;
		public long LastAccessUtcTicks;
		public long LastWriteUtcTicks;
		public byte[] SecurityDescriptor = Array.Empty<byte>();
		public long FileSize;
		public readonly List<byte[]> Pages = new List<byte[]>();
		public Dictionary<string, Node>? Children;
	}

	private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
	private readonly long _maxPages;
	private readonly byte[] _defaultFileSd;
	private readonly byte[] _defaultDirSd;
	private Node _root;
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
		_maxPages = capacityBytes / PageSize;
		CapacityBytes = _maxPages * PageSize;

		_defaultFileSd = CreateDefaultSecurityDescriptor(isDirectory: false);
		_defaultDirSd = CreateDefaultSecurityDescriptor(isDirectory: true);
		_root = CreateRootNode();
	}

	public RamDiskStats GetStats()
	{
		_lock.EnterReadLock();
		try
		{
			long used = _allocatedPages * PageSize;
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
			info = ToFileInfo(node);
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
					return default;
				}
			}
			else
			{
				validatedSd = (byte[])(isDirectory ? _defaultDirSd : _defaultFileSd).Clone();
			}

			Node node = new Node
			{
				Id = _nextNodeId++,
				Name = name,
				IsDirectory = isDirectory,
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
				EnsureCapacityForDeltaPagesLocked(neededPages - node.Pages.Count);
				while (node.Pages.Count < neededPages)
				{
					node.Pages.Add(new byte[PageSize]);
					_allocatedPages++;
				}
			}

			parent.Children![name] = node;
			parent.LastWriteUtcTicks = now;
			if (isDirectory) _dirCount++;
			else _fileCount++;

			return ToFileInfo(node);
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	public int ReadFile(string path, ulong offset, Span<byte> destination, out bool endOfFile)
	{
		endOfFile = false;
		_lock.EnterReadLock();
		try
		{
			Node? node = ResolveNodeLocked(path);
			if (node == null)
			{
				ThrowWin32(2); // ERROR_FILE_NOT_FOUND
			}
			if (node!.IsDirectory)
			{
				ThrowWin32(5); // ERROR_ACCESS_DENIED
			}

			if (offset > (ulong)long.MaxValue)
			{
				ThrowWin32(87); // ERROR_INVALID_PARAMETER
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
			_lock.ExitReadLock();
		}
	}

	public InMemoryFileInfo WriteFile(string path, ulong offset, ReadOnlySpan<byte> source, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred)
	{
		_lock.EnterWriteLock();
		try
		{
			Node? node = ResolveNodeLocked(path);
			if (node == null)
			{
				ThrowWin32(2);
			}
			if (node!.IsDirectory)
			{
				ThrowWin32(5);
			}

			if (!writeToEndOfFile && offset > (ulong)long.MaxValue)
			{
				ThrowWin32(87); // ERROR_INVALID_PARAMETER
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
				EnsureCapacityForDeltaPagesLocked(requiredPages - node.Pages.Count);
				while (node.Pages.Count < requiredPages)
				{
					node.Pages.Add(new byte[PageSize]);
					_allocatedPages++;
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
			_lock.ExitWriteLock();
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
				_dirCount = Math.Max(0, _dirCount - 1);
			}
			else
			{
				_allocatedPages = Math.Max(0, _allocatedPages - target.Pages.Count);
				target.Pages.Clear();
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
						_dirCount = Math.Max(0, _dirCount - 1);
					}
					else
					{
						_allocatedPages = Math.Max(0, _allocatedPages - existingTarget.Pages.Count);
						existingTarget.Pages.Clear();
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
			_root = CreateRootNode();
			_allocatedPages = 0;
			_fileCount = 0;
			_dirCount = 0;
		}
		finally
		{
			_lock.ExitWriteLock();
		}
	}

	private Node CreateRootNode()
	{
		long now = DateTime.UtcNow.ToFileTimeUtc();
		return new Node
		{
			Id = _nextNodeId++,
			Name = string.Empty,
			IsDirectory = true,
			Attributes = (uint)FileAttributes.Directory,
			CreationUtcTicks = now,
			LastAccessUtcTicks = now,
			LastWriteUtcTicks = now,
			SecurityDescriptor = (byte[])_defaultDirSd.Clone(),
			Children = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase)
		};
	}

	private void EnsureCapacityForDeltaPagesLocked(long deltaPages)
	{
		if (deltaPages > 0 && _allocatedPages + deltaPages > _maxPages)
		{
			throw new IOException("RAM disk capacity exceeded.", ErrorDiskFullHResult);
		}
	}

	private void ResizeNodePagesLocked(Node node, long targetBytes)
	{
		long targetPages = targetBytes <= 0 ? 0 : (targetBytes + PageSize - 1) / PageSize;
		if (targetPages > node.Pages.Count)
		{
			EnsureCapacityForDeltaPagesLocked(targetPages - node.Pages.Count);
			while (node.Pages.Count < targetPages)
			{
				node.Pages.Add(new byte[PageSize]);
				_allocatedPages++;
			}
		}
		else if (targetPages < node.Pages.Count)
		{
			int toRemove = node.Pages.Count - (int)targetPages;
			node.Pages.RemoveRange((int)targetPages, toRemove);
			_allocatedPages = Math.Max(0, _allocatedPages - toRemove);
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
