using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace HCX.Core;

public readonly struct ScoreDecision
{
	public bool IsSequentialBypass { get; init; }

	public int SequentialStreak { get; init; }

	public double Score { get; init; }

	public bool ShouldPromoteToSsd { get; init; }

	public bool ShouldPromoteToRam { get; init; }
}

/// <summary>
/// Scores per-block access heat using time-decayed frequency/recency while bypassing large sequential streams.
/// Uses 64 striped block buckets and bounded per-file sequence state for scalable parallel throughput.
/// </summary>
public sealed class AiAccessScorer
{
	private const int StripeCount = 64;
	private const int MaxTrackedFiles = 16_384;

	private sealed class FileSequenceState
	{
		public readonly object Sync = new object();
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

	private sealed class StripeBucket
	{
		public readonly object Sync = new object();
		public readonly Dictionary<BlockKey, BlockHeatState> Blocks = new Dictionary<BlockKey, BlockHeatState>();
	}

	private readonly StripeBucket[] _stripes = new StripeBucket[StripeCount];
	private readonly ConcurrentDictionary<string, FileSequenceState> _seqByFile = new ConcurrentDictionary<string, FileSequenceState>(StringComparer.OrdinalIgnoreCase);
	private readonly int _sequentialThreshold;
	private readonly double _ssdPromoteScore;
	private readonly double _ramPromoteScore;
	private readonly int _maxBlocksPerStripe;
	private long _sequentialBypasses;
	private int _seqTrackedCount;
	private int _seqPruneInProgress;
	private int _l1HitSampleCounter;

	public AiAccessScorer(int sequentialThreshold = 8, double ssdPromoteScore = 1.2, double ramPromoteScore = 2.0, int maxTrackedBlocks = 250_000)
	{
		_sequentialThreshold = Math.Max(1, sequentialThreshold);
		_ssdPromoteScore = ssdPromoteScore;
		_ramPromoteScore = ramPromoteScore;
		_maxBlocksPerStripe = Math.Max(16, Math.Max(1024, maxTrackedBlocks) / StripeCount);
		for (int i = 0; i < StripeCount; i++)
		{
			_stripes[i] = new StripeBucket();
		}
	}

	public int TrackedFileSequenceCount => _seqByFile.Count;

	/// <summary>
	/// Fast-path sampling recorder for L1 RAM hits: avoids full ConcurrentDictionary + stripe locking on 75% of hits
	/// since the block is already promoted to L1.
	/// </summary>
	public void RecordL1HitLightweight(BlockKey key, DateTime utcNow, bool isDirty)
	{
		if ((Interlocked.Increment(ref _l1HitSampleCounter) & 3) != 0)
		{
			return;
		}
		StripeBucket stripe = GetStripe(key);
		lock (stripe.Sync)
		{
			if (stripe.Blocks.TryGetValue(key, out BlockHeatState? state))
			{
				state.Score = Math.Min(100.0, state.Score + 1.0);
				state.LastAccessUtc = utcNow;
				state.HitCount += 4;
				state.Tier = CacheTier.RamL1;
				state.IsDirty = isDirty;
			}
			else
			{
				if (stripe.Blocks.Count >= _maxBlocksPerStripe)
				{
					PruneColdEntriesLocked(stripe.Blocks);
				}
				stripe.Blocks[key] = new BlockHeatState
				{
					Score = _ramPromoteScore,
					LastAccessUtc = utcNow,
					HitCount = 4,
					Tier = CacheTier.RamL1,
					IsDirty = isDirty
				};
			}
		}
	}

	public ScoreDecision RecordAccess(
		BlockKey key,
		bool isWrite,
		DateTime utcNow,
		CacheTier? updateTier = null,
		bool? updateDirty = null)
	{
		if (!_seqByFile.TryGetValue(key.RelativePath, out FileSequenceState? seq))
		{
			FileSequenceState created = new FileSequenceState();
			if (_seqByFile.TryAdd(key.RelativePath, created))
			{
				seq = created;
				if (Interlocked.Increment(ref _seqTrackedCount) > MaxTrackedFiles)
				{
					PruneSequenceMap();
				}
			}
			else
			{
				seq = _seqByFile.GetOrAdd(key.RelativePath, static _ => new FileSequenceState());
			}
		}
		int currentStreak;
		bool isSequentialBypass;
		lock (seq.Sync)
		{
			if (seq.LastBlockIndex != long.MinValue && key.BlockIndex == seq.LastBlockIndex + 1)
			{
				seq.SequentialStreak++;
			}
			else
			{
				seq.SequentialStreak = 0;
			}
			seq.LastBlockIndex = key.BlockIndex;
			currentStreak = seq.SequentialStreak;
			isSequentialBypass = currentStreak >= _sequentialThreshold;
		}

		if (isSequentialBypass)
		{
			Interlocked.Increment(ref _sequentialBypasses);
		}

		StripeBucket stripe = GetStripe(key);
		lock (stripe.Sync)
		{
			if (!stripe.Blocks.TryGetValue(key, out BlockHeatState? state))
			{
				if (stripe.Blocks.Count >= _maxBlocksPerStripe)
				{
					PruneColdEntriesLocked(stripe.Blocks);
				}
				state = new BlockHeatState { LastAccessUtc = utcNow };
				stripe.Blocks[key] = state;
			}

			double elapsedSeconds = (utcNow - state.LastAccessUtc).TotalSeconds;
			double decayed = elapsedSeconds >= 1.0
				? state.Score * Math.Exp(-elapsedSeconds / 300.0)
				: state.Score;
			double increment = isSequentialBypass
				? 0.05
				: (isWrite ? 1.8 : (currentStreak == 0 ? 1.5 : 1.0));

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
				SequentialStreak = currentStreak,
				Score = state.Score,
				ShouldPromoteToSsd = !isSequentialBypass && state.Score >= _ssdPromoteScore,
				ShouldPromoteToRam = !isSequentialBypass && state.Score >= _ramPromoteScore
			};
		}
	}

	public void UpdateTierState(BlockKey key, CacheTier tier, bool isDirty, double? scoreOverride = null)
	{
		StripeBucket stripe = GetStripe(key);
		lock (stripe.Sync)
		{
			if (!stripe.Blocks.TryGetValue(key, out BlockHeatState? state))
			{
				state = new BlockHeatState
				{
					LastAccessUtc = DateTime.UtcNow,
					Score = scoreOverride ?? 1.0
				};
				stripe.Blocks[key] = state;
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
		if (_seqByFile.TryRemove(normalizedRelPath, out _))
		{
			Interlocked.Decrement(ref _seqTrackedCount);
		}
		for (int i = 0; i < StripeCount; i++)
		{
			StripeBucket stripe = _stripes[i];
			lock (stripe.Sync)
			{
				List<BlockKey>? toRemove = null;
				foreach (BlockKey k in stripe.Blocks.Keys)
				{
					if (string.Equals(k.RelativePath, normalizedRelPath, StringComparison.OrdinalIgnoreCase))
					{
						toRemove ??= new List<BlockKey>();
						toRemove.Add(k);
					}
				}
				if (toRemove != null)
				{
					foreach (BlockKey k in toRemove)
					{
						stripe.Blocks.Remove(k);
					}
				}
			}
		}
	}

	public void RenamePrefix(string oldRelPath, string newRelPath)
	{
		string oldPrefix = oldRelPath.TrimEnd('\\') + "\\";
		string newPrefix = newRelPath.TrimEnd('\\') + "\\";

		foreach (string seqKey in _seqByFile.Keys)
		{
			if (string.Equals(seqKey, oldRelPath, StringComparison.OrdinalIgnoreCase) ||
			    seqKey.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
			{
				if (_seqByFile.TryRemove(seqKey, out _))
				{
					Interlocked.Decrement(ref _seqTrackedCount);
				}
			}
		}

		List<(BlockKey NewKey, BlockHeatState State)> crossStripeMoves = new List<(BlockKey, BlockHeatState)>();
		for (int i = 0; i < StripeCount; i++)
		{
			StripeBucket stripe = _stripes[i];
			lock (stripe.Sync)
			{
				List<(BlockKey OldKey, BlockKey NewKey, BlockHeatState State)>? updates = null;
				foreach (KeyValuePair<BlockKey, BlockHeatState> kv in stripe.Blocks)
				{
					if (string.Equals(kv.Key.RelativePath, oldRelPath, StringComparison.OrdinalIgnoreCase))
					{
						updates ??= new List<(BlockKey, BlockKey, BlockHeatState)>();
						updates.Add((kv.Key, new BlockKey(newRelPath, kv.Key.BlockIndex), kv.Value));
					}
					else if (kv.Key.RelativePath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
					{
						updates ??= new List<(BlockKey, BlockKey, BlockHeatState)>();
						string suffix = kv.Key.RelativePath.Substring(oldPrefix.Length);
						updates.Add((kv.Key, new BlockKey(newPrefix + suffix, kv.Key.BlockIndex), kv.Value));
					}
				}
				if (updates != null)
				{
					foreach ((BlockKey oldK, BlockKey newK, BlockHeatState st) in updates)
					{
						stripe.Blocks.Remove(oldK);
						int newStripeIdx = GetStripeIndex(newK);
						if (newStripeIdx == i)
						{
							stripe.Blocks[newK] = st;
						}
						else
						{
							crossStripeMoves.Add((newK, st));
						}
					}
				}
			}
		}

		foreach ((BlockKey newK, BlockHeatState st) in crossStripeMoves)
		{
			StripeBucket targetStripe = GetStripe(newK);
			lock (targetStripe.Sync)
			{
				targetStripe.Blocks[newK] = st;
			}
		}
	}

	public (int TrackedBlocks, double AverageAiScore, long SequentialBypasses, HeatmapCell[] Heatmap) GetMetrics()
	{
		HeatmapCell[] cells = new HeatmapCell[256];
		double[] maxScore = new double[256];
		long[] hits = new long[256];
		CacheTier[] tiers = new CacheTier[256];
		bool[] dirty = new bool[256];

		double totalScore = 0.0;
		int totalBlocks = 0;

		for (int i = 0; i < StripeCount; i++)
		{
			StripeBucket stripe = _stripes[i];
			lock (stripe.Sync)
			{
				foreach (KeyValuePair<BlockKey, BlockHeatState> kv in stripe.Blocks)
				{
					totalBlocks++;
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
			}
		}

		long seqBypasses = Interlocked.Read(ref _sequentialBypasses);
		if (totalBlocks == 0)
		{
			return (0, 0.0, seqBypasses, cells);
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

		return (totalBlocks, totalScore / totalBlocks, seqBypasses, cells);
	}

	private StripeBucket GetStripe(BlockKey key) => _stripes[GetStripeIndex(key)];

	private static int GetStripeIndex(BlockKey key) => key.GetHashCode() & (StripeCount - 1);

	private void PruneSequenceMap()
	{
		if (Interlocked.Exchange(ref _seqPruneInProgress, 1) != 0)
		{
			return;
		}
		try
		{
			int current = _seqByFile.Count;
			Volatile.Write(ref _seqTrackedCount, current);
			if (current <= MaxTrackedFiles)
			{
				return;
			}
			int toRemove = Math.Max(1, current - MaxTrackedFiles + MaxTrackedFiles / 4);
			int removed = 0;
			foreach (string k in _seqByFile.Keys)
			{
				if (_seqByFile.TryRemove(k, out _))
				{
					Interlocked.Decrement(ref _seqTrackedCount);
					if (++removed >= toRemove)
					{
						break;
					}
				}
			}
		}
		finally
		{
			Volatile.Write(ref _seqPruneInProgress, 0);
		}
	}

	private static void PruneColdEntriesLocked(Dictionary<BlockKey, BlockHeatState> blocks)
	{
		int toRemove = blocks.Count / 4;
		if (toRemove <= 0)
		{
			return;
		}
		List<BlockKey> candidates = new List<BlockKey>(toRemove);
		foreach (KeyValuePair<BlockKey, BlockHeatState> kv in blocks)
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
			blocks.Remove(k);
		}
	}
}
