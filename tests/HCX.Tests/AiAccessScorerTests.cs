using System;
using HCX.Core;
using Xunit;

namespace HCX.Tests;

public sealed class AiAccessScorerTests
{
	[Fact]
	public void RepeatedRandomAccess_ReachesSsdAndRamPromotionThresholds()
	{
		AiAccessScorer scorer = new AiAccessScorer(sequentialThreshold: 8, ssdPromoteScore: 1.2, ramPromoteScore: 2.0);
		BlockKey key = new BlockKey("data\\sample.bin", 12);
		DateTime t0 = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

		ScoreDecision d1 = scorer.RecordAccess(key, isWrite: false, t0);
		Assert.False(d1.IsSequentialBypass);
		Assert.True(d1.ShouldPromoteToSsd);
		Assert.False(d1.ShouldPromoteToRam);

		ScoreDecision d2 = scorer.RecordAccess(key, isWrite: false, t0.AddMilliseconds(50));
		Assert.False(d2.IsSequentialBypass);
		Assert.True(d2.ShouldPromoteToSsd);
		Assert.True(d2.ShouldPromoteToRam);
		Assert.True(d2.Score > 2.0);
	}

	[Fact]
	public void SequentialScan_TriggersSmartFilterBypass()
	{
		AiAccessScorer scorer = new AiAccessScorer(sequentialThreshold: 8, ssdPromoteScore: 1.2, ramPromoteScore: 2.0);
		DateTime t = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

		for (int i = 0; i < 8; i++)
		{
			ScoreDecision d = scorer.RecordAccess(new BlockKey("video.mp4", i), isWrite: false, t);
			Assert.False(d.IsSequentialBypass);
		}

		ScoreDecision bypassed = scorer.RecordAccess(new BlockKey("video.mp4", 8), isWrite: false, t);
		Assert.True(bypassed.IsSequentialBypass);
		Assert.False(bypassed.ShouldPromoteToSsd);
		Assert.False(bypassed.ShouldPromoteToRam);

		var (_, _, seqBypasses, _) = scorer.GetMetrics();
		Assert.Equal(1, seqBypasses);
	}

	[Fact]
	public void TimeDecay_ReducesStaleBlockScore()
	{
		AiAccessScorer scorer = new AiAccessScorer();
		BlockKey key = new BlockKey("hot.dat", 5);
		DateTime t0 = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

		ScoreDecision hot = scorer.RecordAccess(key, isWrite: true, t0);
		hot = scorer.RecordAccess(key, isWrite: true, t0.AddSeconds(1));
		Assert.True(hot.Score > 3.0);

		ScoreDecision afterHour = scorer.RecordAccess(key, isWrite: false, t0.AddHours(1));
		Assert.True(afterHour.Score < hot.Score);
	}

	[Fact]
	public void RenamePrefix_And_RemoveByFile_UpdateTrackedKeys()
	{
		AiAccessScorer scorer = new AiAccessScorer();
		DateTime t0 = DateTime.UtcNow;
		BlockKey k1 = new BlockKey("dirA\\sub\\file.bin", 1);
		BlockKey k2 = new BlockKey("other.bin", 2);

		scorer.RecordAccess(k1, isWrite: false, t0);
		scorer.RecordAccess(k2, isWrite: false, t0);
		scorer.RenamePrefix("dirA", "dirB");

		var (trackedAfterRename, _, _, _) = scorer.GetMetrics();
		Assert.Equal(2, trackedAfterRename);

		scorer.RemoveByFile("dirB\\sub\\file.bin");
		var (trackedAfterRemove, _, _, _) = scorer.GetMetrics();
		Assert.Equal(1, trackedAfterRemove);
	}
}
