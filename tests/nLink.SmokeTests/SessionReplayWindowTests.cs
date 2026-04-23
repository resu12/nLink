using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

public sealed class SessionReplayWindowTests
{
    [Fact]
    public void ReplayWindow_Accepts_First_And_InOrder_Sequences()
    {
        var window = new SessionReplayWindow(windowSize: 8, maxForwardAdvance: 32);

        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(1));
        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(2));
        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(3));
        Assert.True(window.HasHighestSequence);
        Assert.Equal(3, window.HighestAcceptedSequence);
    }

    [Fact]
    public void ReplayWindow_Rejects_Duplicate_Sequence()
    {
        var window = new SessionReplayWindow(windowSize: 8, maxForwardAdvance: 32);

        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(10));
        Assert.Equal(SessionReplaySequenceResult.Duplicate, window.EvaluateAndTrack(10));
    }

    [Fact]
    public void ReplayWindow_Accepts_OutOfOrder_Sequence_Within_Window_Once()
    {
        var window = new SessionReplayWindow(windowSize: 8, maxForwardAdvance: 32);

        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(10));
        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(12));
        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(11));
        Assert.Equal(SessionReplaySequenceResult.Duplicate, window.EvaluateAndTrack(11));
    }

    [Fact]
    public void ReplayWindow_Accepts_Large_OutOfOrder_Gap_When_Within_Configured_Window()
    {
        var window = new SessionReplayWindow(windowSize: 4096, maxForwardAdvance: 32768);

        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(300));
        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(100));
        Assert.Equal(300, window.HighestAcceptedSequence);
        Assert.Equal(1, window.LowestAcceptedSequence);
    }

    [Fact]
    public void ReplayWindow_Rejects_Stale_Sequence_Outside_Window()
    {
        var window = new SessionReplayWindow(windowSize: 4, maxForwardAdvance: 32);

        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(10));
        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(11));
        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(12));
        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(13));
        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(14));

        Assert.Equal(SessionReplaySequenceResult.Stale, window.EvaluateAndTrack(10));
    }

    [Fact]
    public void ReplayWindow_Rejects_Sequence_Too_Far_Ahead()
    {
        var window = new SessionReplayWindow(windowSize: 8, maxForwardAdvance: 16);

        Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(10));
        Assert.Equal(SessionReplaySequenceResult.TooFarAhead, window.EvaluateAndTrack(27));
        Assert.Equal(10, window.HighestAcceptedSequence);
    }

    [Fact]
    public void ReplayWindow_Prunes_Tracked_State_To_Window_Size()
    {
        var window = new SessionReplayWindow(windowSize: 4, maxForwardAdvance: 32);

        for (var sequence = 1; sequence <= 10; sequence++)
        {
            Assert.Equal(SessionReplaySequenceResult.Accepted, window.EvaluateAndTrack(sequence));
        }

        Assert.Equal(10, window.HighestAcceptedSequence);
        Assert.Equal(7, window.LowestAcceptedSequence);
        Assert.InRange(window.TrackedSequenceCount, 1, 4);
    }

    [Fact]
    public void ReplayWindow_Rejects_Invalid_Sequence()
    {
        var window = new SessionReplayWindow();

        Assert.Equal(SessionReplaySequenceResult.Invalid, window.EvaluateAndTrack(0));
        Assert.Equal(SessionReplaySequenceResult.Invalid, window.EvaluateAndTrack(-1));
    }

    [Fact]
    public void ReplayDedupeCache_Rejects_Duplicates_And_Evicts_Oldest()
    {
        var cache = new SessionReplayDedupeCache(capacity: 2);

        Assert.Equal(SessionReplayDedupeResult.Accepted, cache.EvaluateAndTrack("id-1"));
        Assert.Equal(SessionReplayDedupeResult.Accepted, cache.EvaluateAndTrack("id-2"));
        Assert.Equal(SessionReplayDedupeResult.Duplicate, cache.EvaluateAndTrack("id-1"));
        Assert.Equal(SessionReplayDedupeResult.Accepted, cache.EvaluateAndTrack("id-3"));
        Assert.Equal(2, cache.Count);
        Assert.Equal(SessionReplayDedupeResult.Accepted, cache.EvaluateAndTrack("id-2"));
    }

    [Fact]
    public void ReplayDedupeCache_Rejects_Invalid_Id()
    {
        var cache = new SessionReplayDedupeCache();

        Assert.Equal(SessionReplayDedupeResult.Invalid, cache.EvaluateAndTrack(null));
        Assert.Equal(SessionReplayDedupeResult.Invalid, cache.EvaluateAndTrack(" "));
    }
}
