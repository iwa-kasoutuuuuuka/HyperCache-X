using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HCX.Core;

public enum WorkloadPreset
{
	Balanced = 0,
	Gaming = 1,
	Development = 2,
	MediaEditing = 3
}

public enum CacheFilterAction
{
	Normal = 0,
	Bypass = 1,
	PriorityL1 = 2,
	Pinned = 3
}

/// <summary>
/// Fast extension and path-prefix filter rules with workload presets and pinned hot paths.
/// </summary>
public sealed class CacheFilterRules
{
	private readonly HashSet<string> _bypassExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _priorityExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, byte> _pinnedPrefixes = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
	private readonly object _sync = new object();

	public WorkloadPreset Preset { get; private set; } = WorkloadPreset.Balanced;

	public CacheFilterRules(WorkloadPreset preset = WorkloadPreset.Balanced)
	{
		ApplyPreset(preset);
	}

	public void ApplyPreset(WorkloadPreset preset)
	{
		lock (_sync)
		{
			Preset = preset;
			_bypassExtensions.Clear();
			_priorityExtensions.Clear();

			switch (preset)
			{
				case WorkloadPreset.Gaming:
					AddRange(_bypassExtensions, ".mp4", ".mkv", ".avi", ".mov", ".webm", ".iso", ".zip", ".7z", ".rar", ".cab");
					AddRange(_priorityExtensions, ".pak", ".ucas", ".utoc", ".upk", ".assets", ".bundle", ".shader", ".cso", ".dxil", ".dll", ".exe", ".ini", ".cfg");
					break;

				case WorkloadPreset.Development:
					AddRange(_bypassExtensions, ".mp4", ".mkv", ".iso", ".zip", ".7z", ".tar", ".gz", ".zst", ".dmg", ".vhdx");
					AddRange(_priorityExtensions, ".cs", ".ts", ".tsx", ".js", ".json", ".csproj", ".sln", ".dll", ".pdb", ".h", ".hpp", ".cpp", ".rs", ".py", ".db", ".sqlite", ".idx");
					break;

				case WorkloadPreset.MediaEditing:
					AddRange(_bypassExtensions, ".iso", ".zip", ".7z", ".rar", ".gz", ".bak");
					AddRange(_priorityExtensions, ".prproj", ".drp", ".aep", ".xml", ".json", ".lut", ".cube", ".otf", ".ttf", ".db", ".idx", ".pek", ".cfa");
					break;

				case WorkloadPreset.Balanced:
				default:
					AddRange(_bypassExtensions, ".mp4", ".mkv", ".avi", ".mov", ".iso", ".zip", ".7z", ".rar", ".gz", ".zst");
					AddRange(_priorityExtensions, ".dll", ".exe", ".db", ".sqlite", ".idx", ".pak");
					break;
			}
		}
	}

	public void AddBypassExtension(string ext)
	{
		string norm = NormalizeExtension(ext);
		if (norm.Length > 0)
		{
			lock (_sync) { _bypassExtensions.Add(norm); _priorityExtensions.Remove(norm); }
		}
	}

	public void AddPriorityExtension(string ext)
	{
		string norm = NormalizeExtension(ext);
		if (norm.Length > 0)
		{
			lock (_sync) { _priorityExtensions.Add(norm); _bypassExtensions.Remove(norm); }
		}
	}

	public void PinPath(string relativePathOrPrefix)
	{
		string norm = BlockKey.NormalizeRelativePath(relativePathOrPrefix).TrimEnd('\\');
		if (!string.IsNullOrEmpty(norm))
		{
			_pinnedPrefixes[norm] = 1;
		}
	}

	public void UnpinPath(string relativePathOrPrefix)
	{
		string norm = BlockKey.NormalizeRelativePath(relativePathOrPrefix).TrimEnd('\\');
		if (!string.IsNullOrEmpty(norm))
		{
			_pinnedPrefixes.TryRemove(norm, out _);
		}
	}

	public string[] GetPinnedPaths() => _pinnedPrefixes.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();

	public CacheFilterAction Evaluate(string normalizedRelPath)
	{
		if (string.IsNullOrEmpty(normalizedRelPath))
		{
			return CacheFilterAction.Normal;
		}

		if (!_pinnedPrefixes.IsEmpty)
		{
			foreach (string pin in _pinnedPrefixes.Keys)
			{
				if (string.Equals(normalizedRelPath, pin, StringComparison.OrdinalIgnoreCase) ||
				    (normalizedRelPath.Length > pin.Length &&
				     normalizedRelPath[pin.Length] == '\\' &&
				     normalizedRelPath.StartsWith(pin, StringComparison.OrdinalIgnoreCase)))
				{
					return CacheFilterAction.Pinned;
				}
			}
		}

		string ext = Path.GetExtension(normalizedRelPath);
		if (string.IsNullOrEmpty(ext))
		{
			return CacheFilterAction.Normal;
		}

		lock (_sync)
		{
			if (_priorityExtensions.Contains(ext))
			{
				return CacheFilterAction.PriorityL1;
			}
			if (_bypassExtensions.Contains(ext))
			{
				return CacheFilterAction.Bypass;
			}
		}
		return CacheFilterAction.Normal;
	}

	private static void AddRange(HashSet<string> set, params string[] items)
	{
		foreach (string item in items)
		{
			set.Add(item);
		}
	}

	private static string NormalizeExtension(string ext)
	{
		if (string.IsNullOrWhiteSpace(ext)) return string.Empty;
		string trimmed = ext.Trim();
		return trimmed.StartsWith('.') ? trimmed : "." + trimmed;
	}
}
