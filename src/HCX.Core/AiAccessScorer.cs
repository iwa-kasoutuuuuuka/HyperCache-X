using System;
using System.Collections.Generic;

namespace HCX.Core;

public readonly struct ScoreDecision
{
	public bool IsSequentialBypass { get; init; }

	public double Score { get; init; }

	public bool ShouldPromoteToSsd { get; init; }

	public bool ShouldPromoteToRam { get; init; }
}

/// <summary>
/// Scores per-block access heat using time-decayed frequency/recency while bypassing large sequential streams.
/// </summary>
public sealed class AiAccessScorer
{
	private sealed class FileSequenceState
	{
		public long LastBlockIndex = long.MinValue;
		public int SequentialStreak;
	}

	private sealed class BlockHeatState
	{
		public double Score;
		public DateTime LastAccessUtc;
		public long HitCount;
		public CacheTier Tier;
		public bool IsDirty;
	}

	private readonly object _sync = new object();
	private readonly Dictionary<string, FileSequenceState> _seqByFile = new Dictionary<string, FileSequenceState>(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<BlockKey, BlockHeatState> _blocks = new Dictionary<BlockKey, BlockHeatState>();
	private readonly int _sequentialThreshold;
	private readonly double _ssdPromoteScore;
	private readonly double _ramPromoteScore;
	private readonly int _maxTrackedBlocks;
	private long _sequentialBypasses;

	public AiAccessScorer(int sequentialThreshold = 8, double ssdPromoteScore = 1.2, double ramPromoteScore = 2.0, int maxTrackedBlocks = 250_000)
	{
		_sequentialThreshold = Math.Max(1, sequentialThreshold);
		_ssdPromoteScore = ssdPromoteScore;
		_ramPromoteScore = ramPromoteScore;
		_maxTrackedBlocks = Math.Max(1024, maxTrackedBlocks);
	}

	public ScoreDecision RecordAccess(
		BlockKey key,
		bool isWrite,
		DateTime utcNow,
		CacheTier? updateTier = null,
		bool? updateDirty = null)
	{
		lock (_sync)
		{
			if (!_seqByFile.TryGetValue(key.RelativePath, out FileSequenceState? seq))
			{
				seq = new FileSequenceState();
				_seqByFile[key.RelativePath] = seq;
			}

			if (seq.LastBlockIndex != long.MinValue && key.BlockIndex == seq.LastBlockIndex + 1)
			{
				seq.SequentialStreak++;
			}
			else
			{
				seq.SequentialStreak = 0;
			}
			seq.LastBlockIndex = key.BlockIndex;

			bool isSequentialBypass = seq.SequentialStreak >= _sequentialThreshold;
			if (isSequentialBypass)
			{
				_sequentialBypasses++;
			}

			if (!_blocks.TryGetValue(key, out BlockHeatState? state))
			{
				if (_blocks.Count >= _maxTrackedBlocks)
				{
					PruneColdEntriesLocked();
				}
				state = new BlockHeatState { LastAccessUtc = utcNow };
				_blocks[key] = state;
			}

			double elapsedSeconds = (utcNow - state.LastAccessUtc).TotalSeconds;
			double decayed = elapsedSeconds >= 1.0
				? state.Score * Math.Exp(-elapsedSeconds / 300.0)
				: state.Score;
			double increment = isSequentialBypass
				? 0.05
				: (isWrite ? 1.8 : (seq.SequentialStreak == 0 ? 1.5 : 1.0));

			state.Score = Math.Min(100.0, decayed + increment);
			state.LastAccessUtc = utcNow;
			state.HitCount++;
			if (updateTier.HasValue)
			{
				state.Tier = updateTier.Value;
			}
			if (updateDirty.HasValue)
			{
				state.IsDirty = updateDirty.Value;
			}

			return new ScoreDecision
			{
				IsSequentialBypass = isSequentialBypass,
				Score = state.Score,
				ShouldPromoteToSsd = !isSequentialBypass && state.Score >= _ssdPromoteScore,
				ShouldPromoteToRam = !isSequentialBypass && state.Score >= _ramPromoteScore
			};
		}
	}

	public void UpdateTierState(BlockKey key, CacheTier tier, bool isDirty, double? scoreOverride = null)
	{
		lock (_sync)
		{
			if (!_blocks.TryGetValue(key, out BlockHeatState? state))
			{
				state = new BlockHeatState
				{
					LastAccessUtc = DateTime.UtcNow,
					Score = scoreOverride ?? 1.0
				};
				_blocks[key] = state;
			}
			state.Tier = tier;
			state.IsDirty = isDirty;
			if (scoreOverride.HasValue)
			{
				state.Score = Math.Max(state.Score, scoreOverride.Value);
			}
		}
	}

	public void RemoveByFile(string normalizedRelPath)
	{
		lock (_sync)
		{
			_seqByFile.Remove(normalizedRelPath);
			List<BlockKey> toRemove = new List<BlockKey>();
			foreach (BlockKey k in _blocks.Keys)
			{
				if (string.Equals(k.RelativePath, normalizedRelPath, StringComparison.OrdinalIgnoreCase))
				{
					toRemove.Add(k);
				}
			}
			foreach (BlockKey k in toRemove)
			{
				_blocks.Remove(k);
			}
		}
	}

	public void RenamePrefix(string oldRelPath, string newRelPath)
	{
		lock (_sync)
		{
			string oldPrefix = oldRelPath.TrimEnd('\\') + "\\";
			List<(BlockKey OldKey, BlockKey NewKey, BlockHeatState State)> updates = new List<(BlockKey, BlockKey, BlockHeatState)>();
			foreach (KeyValuePair<BlockKey, BlockHeatState> kv in _blocks)
			{
				if (string.Equals(kv.Key.RelativePath, oldRelPath, StringComparison.OrdinalIgnoreCase))
				{
					updates.Add((kv.Key, new BlockKey(newRelPath, kv.Key.BlockIndex), kv.Value));
				}
				else if (kv.Key.RelativePath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
				{
					string suffix = kv.Key.RelativePath.Substring(oldPrefix.Length);
					updates.Add((kv.Key, new BlockKey(newRelPath.TrimEnd('\\') + "\\" + suffix, kv.Key.BlockIndex), kv.Value));
				}
			}
			foreach ((BlockKey oldK, BlockKey newK, BlockHeatState st) in updates)
			{
				_blocks.Remove(oldK);
				_blocks[newK] = st;
			}
			_seqByFile.Remove(oldRelPath);
		}
	}

	public (int TrackedBlocks, double AverageAiScore, long SequentialBypasses, HeatmapCell[] Heatmap) GetMetrics()
	{
		lock (_sync)
		{
			HeatmapCell[] cells = new HeatmapCell[256];
			if (_blocks.Count == 0)
			{
				return (0, 0.0, _sequentialBypasses, cells);
			}

			double totalScore = 0.0;
			double[] maxScore = new double[256];
			long[] hits = new long[256];
			CacheTier[] tiers = new CacheTier[256];
			bool[] dirty = new bool[256];

			foreach (KeyValuePair<BlockKey, BlockHeatState> kv in _blocks)
			{
				totalScore += kv.Value.Score;
				int bucket = (kv.Key.GetHashCode() & 0x7FFFFFFF) % 256;
				hits[bucket] += kv.Value.HitCount;
				if (kv.Value.Score > maxScore[bucket])
				{
					maxScore[bucket] = kv.Value.Score;
				}
				if (kv.Value.Tier == CacheTier.RamL1 || (kv.Value.Tier == CacheTier.SsdL2 && tiers[bucket] == CacheTier.None))
				{
					tiers[bucket] = kv.Value.Tier;
				}
				if (kv.Value.IsDirty)
				{
					dirty[bucket] = true;
				}
			}

			for (int i = 0; i < 256; i++)
			{
				if (hits[i] > 0 || tiers[i] != CacheTier.None || dirty[i])
				{
					cells[i] = new HeatmapCell
					{
						Tier = tiers[i],
						Intensity = Math.Clamp(maxScore[i] / 8.0, 0.12, 1.0),
						HasDirty = dirty[i],
						HitCount = hits[i]
					};
				}
			}

			return (_blocks.Count, totalScore / _blocks.Count, _sequentialBypasses, cells);
		}
	}

	private void PruneColdEntriesLocked()
	{
		int toRemove = _blocks.Count / 4;
		List<BlockKey> candidates = new List<BlockKey>(toRemove);
		foreach (KeyValuePair<BlockKey, BlockHeatState> kv in _blocks)
		{
			if (kv.Value.Tier == CacheTier.None && !kv.Value.IsDirty)
			{
				candidates.Add(kv.Key);
				if (candidates.Count >= toRemove)
				{
					break;
				}
			}
		}
		foreach (BlockKey k in candidates)
		{
			_blocks.Remove(k);
		}
	}
}
