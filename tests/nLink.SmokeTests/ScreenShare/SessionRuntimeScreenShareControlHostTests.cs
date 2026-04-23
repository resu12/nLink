using System.Threading.Tasks;
using NLink.App.Services;
using NLink.App.Services.ScreenCapture;
using NLink.Core.ScreenShare;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class SessionRuntimeScreenShareControlHostTests
{
    [Fact]
    public void ObserveAcceptedFrame_SetsActiveState_AndForwards()
    {
        var context = new FakeScreenShareControlContext();
        var host = new SessionRuntimeScreenShareControlHost(context);

        host.ObserveAcceptedFrame(new ScreenShareFrameCompletedEventArgs(
            FrameId: 7,
            Width: 1280,
            Height: 720,
            Encoding: "h264",
            EncodedFrameBytes: new byte[] { 1 },
            SessionId: "session",
            StreamEpoch: 3));

        Assert.True(host.RemoteScreenShareActive);
        Assert.Equal(1, context.TrackAcceptedFrameCoreCount);
    }

    [Fact]
    public void HandleTransportScreenShareRecoveryReceiptReceived_MirrorsLegacyDiagnostics_AndForwards()
    {
        var context = new FakeScreenShareControlContext();
        var host = new SessionRuntimeScreenShareControlHost(context);
        var receipt = new ScreenShareRecoveryReceiptV1
        {
            SessionId = "session-1",
            StreamEpoch = 9,
            OwnerFrameId = 40,
            VisibleRecoveryFrameId = 42,
            VisibleHeadFrameId = 43,
            ReceiptKind = ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind,
        };

        host.HandleTransportScreenShareRecoveryReceiptReceived(
            sender: new object(),
            new ScreenShareRecoveryReceiptReceivedEventArgs(receipt, "peer-a"));

        Assert.Equal(1, host.RemoteScreenShareRecoveryReceiptReceivedCount);
        Assert.Equal(9, host.RemoteScreenShareLastRecoveryReceiptStreamEpoch);
        Assert.Equal(40, host.RemoteScreenShareLastRecoveryReceiptOwnerFrameId);
        Assert.Equal(42, host.RemoteScreenShareLastRecoveryReceiptVisibleRecoveryFrameId);
        Assert.Equal(43, host.RemoteScreenShareLastRecoveryReceiptVisibleHeadFrameId);
        Assert.Equal(ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind, host.RemoteScreenShareLastRecoveryReceiptKind);
        Assert.Equal(1, context.ReceivedRecoveryReceiptCoreCount);
        Assert.Contains("event=screenshare_recovery_receipt_received_runtime", context.LastTransportLogMessage);
    }

    [Fact]
    public void HandleTransportScreenSharePressureStateReceived_HonorsValidationGate()
    {
        var context = new FakeScreenShareControlContext
        {
            TryValidateScreenShareSessionResult = false,
        };
        var host = new SessionRuntimeScreenShareControlHost(context);
        var message = new ScreenSharePressureStateV1
        {
            SessionId = "session-2",
            Mode = ScreenSharePressureMode.ReduceFps,
            Reason = ScreenSharePressureProtocol.PressureReasonHighFrameAge,
        };

        host.HandleTransportScreenSharePressureStateReceived(
            sender: new object(),
            new ScreenSharePressureStateReceivedEventArgs(message, "peer-b"));

        Assert.Equal(0, context.ReceivedPressureStateCoreCount);
    }

    [Fact]
    public void OnHelperRemoteScreenSharePressureTimerTick_SendsPressureStateThroughContext()
    {
        var context = new FakeScreenShareControlContext
        {
            Role = SessionRuntimeRole.Helper,
        };
        var publishTarget = new FakePressurePublishTarget();
        var host = new SessionRuntimeScreenShareControlHost(
            context,
            new HelperRemoteScreenSharePressurePublisher(publishTarget));
        host.ObserveAcceptedFrame(new ScreenShareFrameCompletedEventArgs(
            FrameId: 1,
            Width: 800,
            Height: 600,
            Encoding: "h264",
            EncodedFrameBytes: new byte[] { 1 },
            SessionId: "session",
            StreamEpoch: 2));

        host.OnHelperRemoteScreenSharePressureTimerTick();

        Assert.Equal(1, publishTarget.PublishCount);
    }

    private sealed class FakePressurePublishTarget : IHelperRemoteScreenSharePressurePublishTarget
    {
        public int PublishCount { get; private set; }

        public void PublishHelperRemoteScreenSharePressureState(bool timerDriven)
        {
            _ = timerDriven;
            PublishCount++;
        }
    }

    private sealed class FakeScreenShareControlContext : ISessionRuntimeScreenShareControlContext
    {
        public bool IsDisposed { get; set; }

        public SessionRuntimeRole Role { get; set; } = SessionRuntimeRole.Helpee;

        public SessionRuntimeState RuntimeState { get; set; } = SessionRuntimeState.Connected;

        public bool IsFromCurrentTransportResult { get; set; } = true;

        public bool TryValidateScreenShareSessionResult { get; set; } = true;

        public bool RequireCapabilityResult { get; set; } = true;

        public int ReceivedPressureStateCoreCount { get; private set; }

        public int ReceivedRecoveryReceiptCoreCount { get; private set; }

        public int RequestedRemoteKeyFrameCount { get; private set; }

        public int TrackAcceptedFrameCoreCount { get; private set; }

        public string LastTransportLogMessage { get; private set; } = string.Empty;

        public bool IsFromCurrentTransport(object? sender)
        {
            _ = sender;
            return IsFromCurrentTransportResult;
        }

        public bool TryValidateScreenShareSession(string sessionId, string stage, string kind)
        {
            _ = sessionId;
            _ = stage;
            _ = kind;
            return TryValidateScreenShareSessionResult;
        }

        public bool RequireCapability(SessionCapability capability, string stage)
        {
            _ = capability;
            _ = stage;
            return RequireCapabilityResult;
        }

        public string GetTransportNameForLog(object? sender)
        {
            _ = sender;
            return "fake";
        }

        public void LogScreenShareTransportInfo(string message)
        {
            LastTransportLogMessage = message;
        }

        public void ApplyRemotePressureState(ScreenSharePressureStateV1 message)
        {
            _ = message;
            ReceivedPressureStateCoreCount++;
        }

        public void ApplyRemoteRecoveryReceipt(ScreenShareRecoveryReceiptV1 message)
        {
            _ = message;
            ReceivedRecoveryReceiptCoreCount++;
        }

        public void RequestRemoteKeyFrame(string reason)
        {
            _ = reason;
            RequestedRemoteKeyFrameCount++;
        }

        public void RunScreenShareBackgroundTask(Func<Task> work, bool countAsTransportTask = false)
        {
            _ = countAsTransportTask;
            work().GetAwaiter().GetResult();
        }

        public string SanitizeTransportExceptionMessage(string? message)
        {
            return string.IsNullOrWhiteSpace(message) ? "(none)" : message!;
        }

        public void ReportHelperRemoteScreenShareFrameApplied(long ageMs, long streamEpoch, long frameId, long visibleHeadFrameId, long stableVisibleHeadFrameId, long framesAppliedSinceLastGap)
        {
            _ = ageMs;
            _ = streamEpoch;
            _ = frameId;
            _ = visibleHeadFrameId;
            _ = stableVisibleHeadFrameId;
            _ = framesAppliedSinceLastGap;
        }

        public void ReportHelperRemoteScreenShareFrameApplied(
            long ageMs,
            long streamEpoch,
            long frameId,
            long visibleHeadFrameId,
            long stableVisibleHeadFrameId,
            long framesAppliedSinceLastGap,
            HelperRemoteSessionSnapshot sessionSnapshot)
        {
            _ = ageMs;
            _ = streamEpoch;
            _ = frameId;
            _ = visibleHeadFrameId;
            _ = stableVisibleHeadFrameId;
            _ = framesAppliedSinceLastGap;
            _ = sessionSnapshot;
        }

        public void ReportHelperRemoteScreenShareSessionSnapshot(HelperRemoteSessionSnapshot snapshot)
        {
            _ = snapshot;
        }

        public void ReportHelperRemoteScreenShareDecodeNeedsMoreInput(long streamEpoch)
        {
            _ = streamEpoch;
        }

        public void ReportHelperRemoteScreenShareContinuityLost(long streamEpoch, string reason, bool shouldRequestRecoveryKeyframe, long currentEpochNeedMoreInputCount, long expectedNextFrameId, long receivedFrameId, long lastCleanFrameId)
        {
            _ = streamEpoch;
            _ = reason;
            _ = shouldRequestRecoveryKeyframe;
            _ = currentEpochNeedMoreInputCount;
            _ = expectedNextFrameId;
            _ = receivedFrameId;
            _ = lastCleanFrameId;
        }

        public void ReportHelperRemoteScreenShareRecoveryKeyframeApplied(long ageMs, long streamEpoch)
        {
            _ = ageMs;
            _ = streamEpoch;
        }

        public void ReportHelperRemoteScreenShareRecoveryWindowStateChanged(long streamEpoch, long recoveryFrameId, long lastContiguousFrameId, int contiguousFollowerApplyCount, string status, string? abortReason)
        {
            _ = streamEpoch;
            _ = recoveryFrameId;
            _ = lastContiguousFrameId;
            _ = contiguousFollowerApplyCount;
            _ = status;
            _ = abortReason;
        }

        public void ReportHelperRemoteScreenShareStaleFrameDropped(long renderedAgeMs, long streamEpoch)
        {
            _ = renderedAgeMs;
            _ = streamEpoch;
        }

        public void TrackHelperRemoteScreenShareAcceptedFrame(ScreenShareFrameCompletedEventArgs e)
        {
            _ = e;
            TrackAcceptedFrameCoreCount++;
        }

        public void NotifyRemoteScreenShareStopped(string reason, object? sender, bool localStop)
        {
            _ = reason;
            _ = sender;
            _ = localStop;
        }

        public SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests()
        {
            throw new NotSupportedException();
        }

    }
}
