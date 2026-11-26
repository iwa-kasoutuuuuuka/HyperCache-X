using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HCX.Core;

public sealed class PersistedBlockEntry
{
	public required string RelativePath { get; init; }
	public long BlockIndex { get; init; }
	public int SlotIndex { get; init; }
	public int ValidLength { get; init; }
	public long BackingFileSize { get; init; }
	public long BackingLastWriteUtcTicks { get; init; }
	public bool IsValid { get; init; }
	public bool IsDirty { get; init; }
	public double AiScore { get; init; }
	public long LastAccessUtcTicks { get; init; }
}

/// <summary>
/// Persists L2 SSD cache slot mappings atomically with SHA-256 verification.
/// </summary>
public sealed class MetadataManager
{
	private const uint MagicV2 = 0x32584348; // "HCX2"
	private const uint FormatVersion = 2;

	private readonly string _metadataPath;

	public string MetadataPath => _metadataPath;

	public MetadataManager(string metadataPath)
	{
		_metadataPath = metadataPath;
	}

	public void SaveMetadata(int blockSize, bool cleanShutdown, IReadOnlyList<PersistedBlockEntry> entries)
	{
		string? dir = Path.GetDirectoryName(_metadataPath);
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}

		string tempPath = _metadataPath + ".tmp";
		try
		{
			using MemoryStream payloadStream = new MemoryStream();
			using (BinaryWriter pw = new BinaryWriter(payloadStream, Encoding.UTF8, leaveOpen: true))
			{
				pw.Write(MagicV2);
				pw.Write(FormatVersion);
				pw.Write(blockSize);
				pw.Write(cleanShutdown ? (byte)1 : (byte)0);
				pw.Write(entries.Count);

				foreach (PersistedBlockEntry e in entries)
				{
					byte[] pathBytes = Encoding.UTF8.GetBytes(e.RelativePath);
					pw.Write(pathBytes.Length);
					pw.Write(pathBytes);
					pw.Write(e.BlockIndex);
					pw.Write(e.SlotIndex);
					pw.Write(e.ValidLength);
					pw.Write(e.BackingFileSize);
					pw.Write(e.BackingLastWriteUtcTicks);
					byte flags = (byte)((e.IsValid ? 2 : 0) | (e.IsDirty ? 1 : 0));
					pw.Write(flags);
					pw.Write(e.AiScore);
					pw.Write(e.LastAccessUtcTicks);
				}
			}

			byte[] payload = payloadStream.ToArray();
			byte[] sha256 = SHA256.HashData(payload);

			using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				fs.Write(payload, 0, payload.Length);
				fs.Write(sha256, 0, sha256.Length);
				fs.Flush(flushToDisk: true);
			}

			File.Move(tempPath, _metadataPath, overwrite: true);
		}
		catch
		{
			if (File.Exists(tempPath))
			{
				try { File.Delete(tempPath); } catch { }
			}
			throw;
		}
	}

	public (bool Loaded, bool CleanShutdown, List<PersistedBlockEntry> Entries) LoadMetadata(int expectedBlockSize)
	{
		if (!File.Exists(_metadataPath))
		{
			return (false, true, new List<PersistedBlockEntry>());
		}

		byte[] allBytes = File.ReadAllBytes(_metadataPath);
		if (allBytes.Length < 49) // minimum header (17) + sha256 (32)
		{
			return (false, false, new List<PersistedBlockEntry>());
		}

		int payloadLength = allBytes.Length - 32;
		ReadOnlySpan<byte> payload = allBytes.AsSpan(0, payloadLength);
		ReadOnlySpan<byte> storedHash = allBytes.AsSpan(payloadLength, 32);
		byte[] actualHash = SHA256.HashData(payload);
		if (!storedHash.SequenceEqual(actualHash))
		{
			return (false, false, new List<PersistedBlockEntry>());
		}

		using MemoryStream ms = new MemoryStream(allBytes, 0, payloadLength, writable: false);
		using BinaryReader br = new BinaryReader(ms, Encoding.UTF8);

		uint magic = br.ReadUInt32();
		uint version = br.ReadUInt32();
		int blockSize = br.ReadInt32();
		bool cleanShutdown = br.ReadByte() != 0;
		int count = br.ReadInt32();

		const int minEntryBytes = 4 + 1 + 8 + 4 + 4 + 8 + 8 + 1 + 8 + 8; // 54 bytes
		long remainingPayload = ms.Length - ms.Position;
		if (magic != MagicV2 || version != FormatVersion || blockSize != expectedBlockSize ||
		    count < 0 || count > 10_000_000 || (long)count * minEntryBytes > remainingPayload)
		{
			return (false, false, new List<PersistedBlockEntry>());
		}

		List<PersistedBlockEntry> entries = new List<PersistedBlockEntry>(count);
		for (int i = 0; i < count; i++)
		{
			if (ms.Length - ms.Position < minEntryBytes - 1)
			{
				return (false, false, new List<PersistedBlockEntry>());
			}
			int pathLen = br.ReadInt32();
			if (pathLen <= 0 || pathLen > 32768 || ms.Length - ms.Position < pathLen + 49)
			{
				return (false, false, new List<PersistedBlockEntry>());
			}
			byte[] pathBytes = br.ReadBytes(pathLen);
			string relPath = Encoding.UTF8.GetString(pathBytes);
			if (!IsSafeRelativePath(relPath))
			{
				return (false, false, new List<PersistedBlockEntry>());
			}
			long blockIndex = br.ReadInt64();
			int slotIndex = br.ReadInt32();
			int validLength = br.ReadInt32();
			long backingSize = br.ReadInt64();
			long backingTicks = br.ReadInt64();
			byte flags = br.ReadByte();
			double aiScore = br.ReadDouble();
			long lastAccessTicks = br.ReadInt64();

			entries.Add(new PersistedBlockEntry
			{
				RelativePath = relPath,
				BlockIndex = blockIndex,
				SlotIndex = slotIndex,
				ValidLength = validLength,
				BackingFileSize = backingSize,
				BackingLastWriteUtcTicks = backingTicks,
				IsValid = (flags & 2) != 0,
				IsDirty = (flags & 1) != 0,
				AiScore = aiScore,
				LastAccessUtcTicks = lastAccessTicks
			});
		}

		return (true, cleanShutdown, entries);
	}

	private static bool IsSafeRelativePath(string relPath)
	{
		if (string.IsNullOrWhiteSpace(relPath) || relPath.IndexOf('\0') >= 0 || Path.IsPathRooted(relPath))
		{
			return false;
		}
		string[] segments = relPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string seg in segments)
		{
			if (seg == "..")
			{
				return false;
			}
		}
		return true;
	}
}
