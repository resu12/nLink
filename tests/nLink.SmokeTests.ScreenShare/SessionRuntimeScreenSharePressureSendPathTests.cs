using System;
using System.Linq;
using System.Threading;
using NLink.App.Services;
using NLink.App.Services.ScreenCapture;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using Xunit;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class SessionRuntimeScreenSharePressureSendPathTests : ScreenShareTransportBoundaryTestBase
{
    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_SendsPressureStateThroughResolvedTransport()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 17, 10, 0, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            string value = sessionRuntime.SecurityState.SessionId.Value.Value;
            ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
            ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(value, 1L, 18L, isKeyFrame: true, 0L);
            ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(value, 1L, "recovery_keyframe_applied", 18L, -1L);
            ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(value, 1L, 18L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount", 8);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 18L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyVisibleProgressActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressVisibleHeadFrameId", 18L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 18L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 8L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 1200.0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameCount", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameIndex", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameAgesMs", new long[3] { 300L, 0L, 0L });
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameAgeMs", 300L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastApplyCadenceMs", 180L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteApplyCadenceObserved", 8L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteTotalApplyCadenceMs", 1440L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.ReduceFps);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "high_frame_age");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "healthyScreenSharePressureIntervals", 3);
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            ScreenSharePressureStateV1 screenSharePressureStateV = ScreenShareTransportBoundaryTestBase.WaitForSinglePressureState(transport.SentPressureStates);
            Assert.Equal(value, screenSharePressureStateV.SessionId);
            Assert.Equal(18L, screenSharePressureStateV.VisibleHeadFrameId);
            Assert.Equal(18L, screenSharePressureStateV.VisibleRecoveryFloorFrameId);
            Assert.True(screenSharePressureStateV.CurrentEpochRecoveryKeyframeApplyCount >= 1);
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_HoldsCatchUpOnlyBeforeRecovering()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 2, 8, 0, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.CatchUpOnly);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "high_frame_age");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-1.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "healthyScreenSharePressureIntervals", 3);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 120L, 1L);
            Thread.Sleep(100);
            Assert.Empty(transport.SentPressureStates);
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_AllowsHealthyRecoveryWhileTransportHealthIsOnlyAdvisory()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 2, 8, 10, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble
        {
            RecentHealthIssueCount = 2L
        };
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.ReduceFps);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "high_frame_age");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "healthyScreenSharePressureIntervals", 3);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 120L, 1L);
            ScreenSharePressureStateV1 screenSharePressureStateV = ScreenShareTransportBoundaryTestBase.WaitForSinglePressureState(transport.SentPressureStates);
            Assert.Equal(ScreenSharePressureMode.Normal, screenSharePressureStateV.Mode);
            Assert.Equal("healthy", screenSharePressureStateV.Reason);
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_BridgeHealthQuarantine_SuppressesActionablePressure()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 18, 19, 0, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble
        {
            RecentHealthIssueCount = 2L,
            IsCongested = true,
            QueueDepth = 2
        };
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.ReduceFps);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "bridge_health");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddMilliseconds(-500.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "healthyScreenSharePressureIntervals", 3);
            string value = sessionRuntime.SecurityState.SessionId.Value.Value;
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "TrackHelperRemoteScreenShareAcceptedFrame", new ScreenShareFrameCompletedEventArgs(1L, 1280, 720, "h264", new byte[1] { 1 }, 0L, 0L, 0L, value, IsKeyFrame: false, 1L, null, ScreenShareRecoveryDeliveryClass.Normal, 0L));
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 120L, 1L, 1L, 1L, 1L, 4L);
            Thread.Sleep(100);
            Assert.DoesNotContain(transport.SentPressureStates, (ScreenSharePressureStateV1 sent) => sent.Mode == ScreenSharePressureMode.ReduceFps && string.Equals(sent.Reason, "bridge_health", StringComparison.Ordinal));
            Assert.DoesNotContain(transport.SentPressureStates, (ScreenSharePressureStateV1 sent) => sent.Mode == ScreenSharePressureMode.CatchUpOnly && string.Equals(sent.Reason, "bridge_health", StringComparison.Ordinal));
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BridgeHealthAdvisoryCount >= 1);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BridgeHealthQuarantineSuppressedCount >= 1);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BridgeHealthTicks);
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostQuarantineBridgeHealth_RequiresTwoCorrelatedEvaluations()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 18, 19, 5, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble
        {
            RecentHealthIssueCount = 2L,
            IsCongested = true,
            QueueDepth = 2
        };
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
            string value = sessionRuntime.SecurityState.SessionId.Value.Value;
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "TrackHelperRemoteScreenShareAcceptedFrame", new ScreenShareFrameCompletedEventArgs(1L, 1280, 720, "h264", new byte[1] { 1 }, 0L, 0L, 0L, value, IsKeyFrame: false, 1L, null, ScreenShareRecoveryDeliveryClass.Normal, 0L));
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 120L, 1L, 1L, 1L, 1L, 4L);
            Thread.Sleep(100);
            transport.SentPressureStates.Clear();
            now = now.AddMilliseconds(1700.0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "healthy");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "healthyScreenSharePressureIntervals", 4);
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            Assert.DoesNotContain(transport.SentPressureStates, (ScreenSharePressureStateV1 sent) => sent.Mode == ScreenSharePressureMode.ReduceFps && string.Equals(sent.Reason, "bridge_health", StringComparison.Ordinal));
            transport.SentPressureStates.Clear();
            ScreenSharePressureStateV1 screenSharePressureStateV = null;
            for (int num = 0; num < 3; num++)
            {
                if ((object)screenSharePressureStateV != null)
                {
                    break;
                }

                now = now.AddMilliseconds(1100.0);
                ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
                screenSharePressureStateV = transport.SentPressureStates.FirstOrDefault((ScreenSharePressureStateV1 sent) => sent.Mode == ScreenSharePressureMode.ReduceFps && string.Equals(sent.Reason, "bridge_health", StringComparison.Ordinal));
                if ((object)screenSharePressureStateV == null)
                {
                    transport.SentPressureStates.Clear();
                }
            }

            Assert.NotNull(screenSharePressureStateV);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BridgeHealthActionableCount >= 1);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BridgeHealthActionableWithoutQueueOrDropCount);
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_KeepsCatchUpOnlyDuringRepeatedStaleDrops()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 2, 8, 20, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.CatchUpOnly);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "repeated_stale_drops");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount", 3);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteStaleDrop(sessionRuntime, 1510L, 1L);
            Thread.Sleep(50);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteStaleDrop(sessionRuntime, 1550L, 1L);
            ScreenSharePressureStateV1 screenSharePressureStateV = ScreenShareTransportBoundaryTestBase.WaitForSinglePressureState(transport.SentPressureStates);
            Assert.Equal(ScreenSharePressureMode.CatchUpOnly, screenSharePressureStateV.Mode);
            Assert.Equal("repeated_stale_drops", screenSharePressureStateV.Reason);
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_SingleStaleDropAfterHealthyApply_DoesNotEscalateToCatchUpOnly()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 2, 8, 25, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 180L, 1L);
            Thread.Sleep(50);
            transport.SentPressureStates.Clear();
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteStaleDrop(sessionRuntime, 1510L, 1L);
            Thread.Sleep(100);
            Assert.Empty(transport.SentPressureStates);
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_SustainedVisibleProgressStall_SendsReduceFps()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 17, 8, 0, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            for (int num = 0; num < 8; num++)
            {
                ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 180L, 1L, num);
                now = now.AddMilliseconds(120.0);
            }

            Thread.Sleep(100);
            transport.SentPressureStates.Clear();
            now = now.AddMilliseconds(450.0);
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            Assert.Empty(transport.SentPressureStates);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.Equal(1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BaselineFrozenDueToStallCount);
            Assert.Equal(1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.CadenceStallWindowCount);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.CadenceStallTriggerCount);
            now = now.AddMilliseconds(300.0);
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            Assert.Empty(transport.SentPressureStates);
            now = now.AddMilliseconds(100.0);
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            ScreenSharePressureStateV1 screenSharePressureStateV = ScreenShareTransportBoundaryTestBase.WaitForSinglePressureState(transport.SentPressureStates);
            Assert.Equal(ScreenSharePressureMode.ReduceFps, screenSharePressureStateV.Mode);
            Assert.Contains(screenSharePressureStateV.Reason, new string[2] { "slow_apply_cadence", "high_frame_age" });
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2 = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.Equal(1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.BaselineFrozenDueToStallCount);
            Assert.Equal(1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.CadenceStallWindowCount);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.SteadyVisibleProgressActive);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.CurrentEpochProgressProven);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.NonHealthyClearSuppressedDueToProgressCount);
            if (string.Equals(screenSharePressureStateV.Reason, "slow_apply_cadence", StringComparison.Ordinal))
            {
                Assert.Equal(1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.CadenceStallTriggerCount);
            }
            else
            {
                Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.CadenceStallTriggerCount);
            }

            now = now.AddMilliseconds(50.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 220L, 1L, 12L);
            Assert.False(Assert.IsType<bool>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochCadenceStallTriggered")));
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_SingleSevereAgeSpike_DoesNotEscalateToCatchUpOnly()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 17, 8, 5, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            for (int num = 0; num < 8; num++)
            {
                ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 180L, 1L, num);
                now = now.AddMilliseconds(120.0);
            }

            Thread.Sleep(100);
            transport.SentPressureStates.Clear();
            now = now.AddMilliseconds(850.0);
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            Assert.DoesNotContain(transport.SentPressureStates, (ScreenSharePressureStateV1 sent) => sent.Mode == ScreenSharePressureMode.CatchUpOnly && string.Equals(sent.Reason, "high_frame_age", StringComparison.Ordinal));
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_SteadyVisibleProgress_SuppressesStandaloneHighFrameAge()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 18, 11, 0, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount", 10);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 17L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500.0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochAgePressureConsecutiveCount", 0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameCount", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameIndex", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameAgesMs", new long[3] { 800L, 0L, 0L });
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameAgeMs", 800L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastApplyCadenceMs", 120L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteApplyCadenceObserved", 10L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteTotalApplyCadenceMs", 1200L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyVisibleProgressActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressActivationFrameId", 15L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 17L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressVisibleHeadFrameId", 17L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 10L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "healthy");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureAgeMs", 220L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "healthyScreenSharePressureIntervals", 4);
            sessionRuntime.ReportHelperRemoteScreenShareSessionSnapshot(ScreenShareTransportBoundaryTestBase.CreateHelperSessionSnapshot(1L, 21L, 21L, 21L, 12L, -1L));
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            Assert.DoesNotContain(transport.SentPressureStates, (ScreenSharePressureStateV1 sent) => sent.Mode == ScreenSharePressureMode.ReduceFps && string.Equals(sent.Reason, "high_frame_age", StringComparison.Ordinal));
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.Equal(21L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.AppliedHeadFrameId);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.SteadyVisibleProgressActive);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.HighFrameAgeSuppressedDueToVisibleProgressCount >= 1);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.HighFrameAgeSuppressedDueToHeadAdvanceCount >= 1);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.NonHealthyClearSuppressedDueToProgressCount);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.ActionableHighFrameAgeCount);
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostRecoveryAgeGrace_SuppressesStandaloneHighFrameAge()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 18, 11, 2, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount", 10);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 18L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500.0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochAgePressureConsecutiveCount", 2);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastEvaluatedAppliedHeadFrameId", 18L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastEvaluatedStableVisibleHeadFrameId", 18L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameCount", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameIndex", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameAgesMs", new long[3] { 920L, 0L, 0L });
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameAgeMs", 920L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastApplyCadenceMs", 120L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteApplyCadenceObserved", 10L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteTotalApplyCadenceMs", 1200L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemotePostRecoveryAgeGraceEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemotePostRecoveryAgeGraceUntilUtc", now.AddMilliseconds(900.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyVisibleProgressActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressActivationFrameId", 15L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 18L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressVisibleHeadFrameId", 18L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 10L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "healthy");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureAgeMs", 220L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "healthyScreenSharePressureIntervals", 4);
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            Assert.DoesNotContain(transport.SentPressureStates, (ScreenSharePressureStateV1 sent) => sent.Mode == ScreenSharePressureMode.ReduceFps && string.Equals(sent.Reason, "high_frame_age", StringComparison.Ordinal));
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.PostRecoveryAgeGraceActive);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.PostRecoveryAgeGraceSuppressedCount >= 1);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.ActionableHighFrameAgeCount);
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_SteadyVisibleProgress_AllowsSustainedHighFrameAgeAfterHeadAdvances()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 18, 11, 5, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount", 10);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 20L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500.0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochAgePressureConsecutiveCount", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastEvaluatedAppliedHeadFrameId", 20L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastEvaluatedStableVisibleHeadFrameId", 20L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameCount", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameIndex", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameAgesMs", new long[3] { 1500L, 0L, 0L });
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameAgeMs", 1500L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastApplyCadenceMs", 120L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteApplyCadenceObserved", 10L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteTotalApplyCadenceMs", 1200L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyVisibleProgressActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressActivationFrameId", 15L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 20L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressVisibleHeadFrameId", 20L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 12L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "healthy");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureAgeMs", 220L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "healthyScreenSharePressureIntervals", 4);
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            ScreenSharePressureStateV1 screenSharePressureStateV = ScreenShareTransportBoundaryTestBase.WaitForSinglePressureState(transport.SentPressureStates);
            Assert.Equal(ScreenSharePressureMode.ReduceFps, screenSharePressureStateV.Mode);
            Assert.Equal("high_frame_age", screenSharePressureStateV.Reason);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.ActionableHighFrameAgeCount >= 1);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.SteadyVisibleProgressActive);
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_AgeOnlySampleWithHeadAdvance_DoesNotClearSteadyProgress()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 18, 11, 10, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount", 10);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 21L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500.0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochAgePressureConsecutiveCount", 0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastEvaluatedAppliedHeadFrameId", 18L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastEvaluatedStableVisibleHeadFrameId", 18L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameCount", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameIndex", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameAgesMs", new long[3] { 820L, 0L, 0L });
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameAgeMs", 820L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastApplyCadenceMs", 120L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteApplyCadenceObserved", 10L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteTotalApplyCadenceMs", 1200L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyVisibleProgressActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressActivationFrameId", 15L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 21L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressVisibleHeadFrameId", 21L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 12L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "healthy");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureAgeMs", 220L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "healthyScreenSharePressureIntervals", 4);
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            Assert.DoesNotContain(transport.SentPressureStates, (ScreenSharePressureStateV1 sent) => sent.Mode == ScreenSharePressureMode.ReduceFps && string.Equals(sent.Reason, "high_frame_age", StringComparison.Ordinal));
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.SteadyVisibleProgressActive);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.HighFrameAgeSuppressedDueToHeadAdvanceCount >= 1);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.ActionableHighFrameAgeCount);
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostRecoverySettleTimeout_IsNotUsedByPressureSendPath()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 17, 9, 0, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "ReportHelperRemoteScreenShareContinuityLost", 1L, "frame_gap", true, 5L, 11L, 12L, 10L);
            Thread.Sleep(100);
            transport.SentPressureStates.Clear();
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5.0));
            now = now.AddMilliseconds(50.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 10L, 10L, 0, "started");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "ReportHelperRemoteScreenShareRecoveryKeyframeApplied", 220L, 1L);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 220L, 1L, 10L);
            transport.SentPressureStates.Clear();
            now = now.AddMilliseconds(450.0);
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.PostRecoverySettleWindowCount);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.PostRecoverySettleWindowSuccessCount);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.PostRecoverySettleWindowTimeoutCount);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.VisibleAppliesDuringSettleCount);
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_FirstVisibleRecoveryFrame_UsesNormalPressureSendPath()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 17, 9, 30, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "ReportHelperRemoteScreenShareContinuityLost", 1L, "frame_gap", true, 5L, 11L, 12L, 10L);
            Thread.Sleep(100);
            transport.SentPressureStates.Clear();
            now = now.AddMilliseconds(50.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 10L, 10L, 0, "started");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "ReportHelperRemoteScreenShareRecoveryKeyframeApplied", 220L, 1L);
            transport.SentPressureStates.Clear();
            now = now.AddMilliseconds(50.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 230L, 1L, 10L);
            Thread.Sleep(100);
            Assert.Empty(transport.SentPressureStates);
            Assert.Equal(1, Assert.IsType<int>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount")));
            now = now.AddMilliseconds(50.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 10L, 11L, 1, "follower_applied");
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 240L, 1L, 11L);
            for (long num = 12L; num <= 15; num++)
            {
                if (transport.SentPressureStates.Count != 0)
                {
                    break;
                }

                now = now.AddMilliseconds(200.0);
                ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 240 + (int)(num - 11) * 10, 1L, num);
                ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            }

            ScreenSharePressureStateV1[] collection = transport.SentPressureStates.ToArray();
            Assert.Empty(collection);
            Assert.True(Assert.IsType<int>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount")) >= 2);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.RecoveryWindowProgressed);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.RecoveryWindowSucceeded);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.RecoveryWindowProgressedCount);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.RecoveryWindowSuccessCount);
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_AppliedHeadAdvanceDuringRecovery_BypassesPressureSend()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount", 15);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 19L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500.0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochAgePressureConsecutiveCount", 2);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastEvaluatedAppliedHeadFrameId", 15L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastEvaluatedStableVisibleHeadFrameId", -1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochVisibleAppliesDuringSettleCount", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameCount", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameIndex", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameAgesMs", new long[3] { 1500L, 0L, 0L });
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameAgeMs", 1500L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastApplyCadenceMs", 120L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteApplyCadenceObserved", 16L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteTotalApplyCadenceMs", 1920L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecoveryWindowActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecoveryWindowEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecoveryWindowRecoveryFrameId", 19L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecoveryWindowLastContiguousFrameId", 19L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecoveryWindowContiguousFollowerApplyCount", 0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteContinuityRecoveryActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteContinuityRecoveryEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteContinuityRecoveryStartedUtc", now.AddMilliseconds(-500.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentSteadyProgressEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentSteadyVisibleProgressActive", false);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentVisibleHeadFrameId", -1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentStableVisibleHeadFrameId", -1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentFramesAppliedSinceLastGap", 0L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentVisibleApplyFrameId", 15L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentAppliedHeadFrameId", 15L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.ReduceFps);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "high_frame_age");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureAgeMs", 1600L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddMilliseconds(-100.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-1.0));
            transport.SentPressureStates.Clear();
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            Thread.Sleep(100);
            ScreenSharePressureStateV1 screenSharePressureStateV = Assert.Single(transport.SentPressureStates);
            Assert.Equal(ScreenSharePressureMode.ReduceFps, screenSharePressureStateV.Mode);
            Assert.Equal("high_frame_age", screenSharePressureStateV.Reason);
            Assert.Equal(19L, screenSharePressureStateV.LastVisibleApplyFrameId);
            Assert.Equal(19L, screenSharePressureStateV.AppliedHeadFrameId);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.PressureSendBypassedForVisibleProgressCount > 0);
        }
        finally
        {
            if (transport != null)
            {
                ((IDisposable)transport).Dispose();
            }
        }
    }

}
