using NLink.Core.ScreenShare;
using Xunit.Sdk;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Trait("Category", "Smoke")]
public sealed class ScreenSharePressureCaptureTests : ScreenShareTransportBoundaryTestBase
{
    [Fact]
    public void PressureStateWait_CorrelatesSessionAndTimestamp_WithoutFilteringPayloadOrClearingHistory()
    {
        var expected = new ScreenSharePressureStateV1
        {
            SessionId = "current-session",
            SentAtUtcMs = 150,
            Mode = ScreenSharePressureMode.ReduceFps,
            Reason = ScreenSharePressureProtocol.PressureReasonHighFrameAge,
        };
        var messages = new List<ScreenSharePressureStateV1>
        {
            expected with { SessionId = "other-session" },
            expected with { SentAtUtcMs = 100 },
            expected,
        };

        var actual = WaitForSinglePressureState(messages, ("current-session", 150));

        Assert.Same(expected, actual);
        Assert.Equal(3, SnapshotList(messages).Length);
    }

    [Theory]
    [InlineData("other-session", 150L)]
    [InlineData("current-session", 100L)]
    public void PressureStateWait_RejectsUncorrelatedMessage(string sessionId, long sentAtUtcMs)
    {
        var messages = new List<ScreenSharePressureStateV1>
        {
            new() { SessionId = sessionId, SentAtUtcMs = sentAtUtcMs },
        };

        Assert.Throws<SingleException>(() =>
            WaitForSinglePressureState(messages, ("current-session", 150)));
    }

    [Fact]
    public void PressureStateWait_RejectsDuplicateCorrelatedMessages()
    {
        var message = new ScreenSharePressureStateV1 { SessionId = "current-session", SentAtUtcMs = 150 };
        var messages = new List<ScreenSharePressureStateV1> { message, message with { } };

        Assert.Throws<SingleException>(() =>
            WaitForSinglePressureState(messages, ("current-session", 150)));
    }

    [Fact]
    public void PressureStateWait_RejectsNullCaptureInsteadOfDiscardingIt()
    {
        var messages = new List<ScreenSharePressureStateV1> { null! };

        Assert.Throws<AllException>(() =>
            WaitForSinglePressureState(messages, ("current-session", 150)));
    }

    [Fact]
    public async Task PressureStateWait_DefaultSelection_PreservesBackgroundTransportMessage()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        var message = new ScreenSharePressureStateV1 { SessionId = "current-session", SentAtUtcMs = 150 };
        var send = Task.Run(() => transport.SendScreenSharePressureStateAsync(message, CancellationToken.None));

        var actual = WaitForSinglePressureState(transport.SentPressureStates);
        await send;

        Assert.Same(message, actual);
    }
}
