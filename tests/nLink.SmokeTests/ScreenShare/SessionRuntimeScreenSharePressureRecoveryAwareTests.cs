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
public sealed class SessionRuntimeScreenSharePressureRecoveryAwareTests : ScreenShareTransportBoundaryTestBase
{
    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_RecoveryKeyframeResetsTailMetricsAndSuppressesImmediateRepressure()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 14, 15, 0, 0, TimeSpan.Zero);
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
            now = now.AddMilliseconds(620.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 220L, 1L, 8L);
            now = now.AddMilliseconds(620.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 230L, 1L, 9L);
            now = now.AddMilliseconds(620.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 240L, 1L, 10L);
            Thread.Sleep(100);
            Assert.Empty(transport.SentPressureStates);
            transport.SentPressureStates.Clear();
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "ReportHelperRemoteScreenShareContinuityLost", 1L, "need_more_input_burst", true, 4L, -1L, -1L, 12L);
            Thread.Sleep(100);
            transport.SentPressureStates.Clear();
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5.0));
            now = now.AddMilliseconds(50.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 20L, 20L, 0, "started");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "ReportHelperRemoteScreenShareRecoveryKeyframeApplied", 480L, 1L);
            transport.SentPressureStates.Clear();
            Assert.Equal(0L, ScreenShareTransportBoundaryTestBase.GetPrivateLongField(sessionRuntime, "helperRemoteCurrentPressureEpochNeedMoreInputCount"));
            Assert.Equal(0L, ScreenShareTransportBoundaryTestBase.GetPrivateLongField(sessionRuntime, "helperRemoteCurrentPressureEpochStaleDropCount"));
            Assert.Equal(0, Assert.IsType<int>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount")));
            Assert.False(Assert.IsType<bool>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochFirstApplySeen")));
            Assert.Equal(-1L, ScreenShareTransportBoundaryTestBase.GetPrivateLongField(sessionRuntime, "helperRemoteLastApplyCadenceMs"));
            Assert.False(Assert.IsType<bool>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineEstablished")));
            now = now.AddMilliseconds(50.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 320L, 1L, 20L);
            Assert.Equal(1, Assert.IsType<int>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount")));
            Assert.True(Assert.IsType<bool>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochFirstApplySeen")));
            now = now.AddMilliseconds(850.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 20L, 21L, 1, "follower_applied");
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 340L, 1L, 21L);
            Thread.Sleep(100);
            Assert.Empty(transport.SentPressureStates);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.RecoveryWindowProgressed);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.RecoveryWindowSucceeded);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.RecoveryWindowProgressedCount);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.RecoveryWindowSuccessCount);
            Assert.Equal(0, Assert.IsType<int>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochAgePressureConsecutiveCount")));
            Assert.Equal(0, Assert.IsType<int>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount")));
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostRecoveryStabilization_SuppressesImmediateHighFrameAgeRepressure()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 14, 15, 10, 0, TimeSpan.Zero);
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
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "ReportHelperRemoteScreenShareContinuityLost", 1L, "need_more_input_burst", true, 6L, -1L, -1L, 40L);
            Thread.Sleep(100);
            transport.SentPressureStates.Clear();
            now = now.AddMilliseconds(50.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 40L, 40L, 0, "started");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "ReportHelperRemoteScreenShareRecoveryKeyframeApplied", 520L, 1L);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 520L, 1L, 40L);
            Thread.Sleep(100);
            transport.SentPressureStates.Clear();
            now = now.AddMilliseconds(150.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 40L, 41L, 1, "follower_applied");
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 760L, 1L, 41L);
            now = now.AddMilliseconds(150.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 40L, 42L, 2, "follower_applied");
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 40L, 42L, 2, "succeeded");
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 780L, 1L, 42L);
            now = now.AddMilliseconds(150.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 790L, 1L, 43L);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.DoesNotContain(transport.SentPressureStates, (ScreenSharePressureStateV1 sent) => sent.Mode == ScreenSharePressureMode.CatchUpOnly);
            Assert.DoesNotContain(transport.SentPressureStates, (ScreenSharePressureStateV1 sent) => string.Equals(sent.Reason, "high_frame_age", StringComparison.Ordinal) || string.Equals(sent.Reason, "slow_apply_cadence", StringComparison.Ordinal));
            foreach (ScreenSharePressureStateV1 sentPressureState in transport.SentPressureStates)
            {
                Assert.Equal(ScreenSharePressureMode.Normal, sentPressureState.Mode);
                Assert.Equal("continuity_loss", sentPressureState.Reason);
            }

            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.VisibleAppliesDuringSettleCount);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.PostRecoverySettleWindowCount);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.PostRecoverySettleWindowSuccessCount);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.PostRecoverySettleWindowTimeoutCount);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.RecoveryWindowProgressed);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.RecoveryWindowSucceeded);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.RecoveryWindowProgressedCount);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.RecoveryWindowSuccessCount);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BaselineReseedInProgress);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BaselineReseedAfterRecoveryCount);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BaselineEstablished);
            Assert.Equal(-1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.VisibleAppliesBeforePressureReenabled);
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_RecoverySuccess_ReseedsBaselineAndSuppressesCadenceDuringReseedWindow()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 18, 8, 30, 0, TimeSpan.Zero);
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
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "ReportHelperRemoteScreenShareContinuityLost", 1L, "frame_gap", true, 6L, 41L, 42L, 40L);
            Thread.Sleep(100);
            transport.SentPressureStates.Clear();
            now = now.AddMilliseconds(50.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 40L, 40L, 0, "started");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "ReportHelperRemoteScreenShareRecoveryKeyframeApplied", 520L, 1L);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 520L, 1L, 40L);
            now = now.AddMilliseconds(120.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 40L, 41L, 1, "follower_applied");
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 540L, 1L, 41L);
            now = now.AddMilliseconds(120.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 40L, 42L, 2, "follower_applied");
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 40L, 42L, 2, "succeeded");
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 560L, 1L, 42L);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BaselineReseedInProgress);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BaselineReseedAfterRecoveryCount);
            transport.SentPressureStates.Clear();
            now = now.AddMilliseconds(120.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 980L, 1L, 43L);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2 = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.BaselineReseedInProgress);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.BaselineEstablished);
            transport.SentPressureStates.Clear();
            now = now.AddMilliseconds(900.0);
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            Assert.Empty(transport.SentPressureStates);
            now = now.AddMilliseconds(120.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 570L, 1L, 44L);
            now = now.AddMilliseconds(120.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 580L, 1L, 45L);
            now = now.AddMilliseconds(120.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 590L, 1L, 46L);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests3 = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests3.BaselineEstablished);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests3.BaselineCaptureToRenderMs <= 0);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests3.BaselineReseedAfterRecoveryCount);
            Assert.Equal(0, Assert.IsType<int>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochAgePressureConsecutiveCount")));
            Assert.Equal(0, Assert.IsType<int>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount")));
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_SteadyVisibleProgressProof_StaysStickyAcrossLaterHealthyTicks()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 18, 9, 20, 0, TimeSpan.Zero);
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
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount", 4);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9.0));
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 210L, 1L, 12L, 12L, 12L, 8L);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.SteadyVisibleProgressActive);
            Assert.Equal(12L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.StableVisibleHeadFrameId);
            Assert.Equal(8L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.FramesAppliedSinceLastGap);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.DerivedPostRecoveryHealthyActive);
            Assert.Equal("stable_visible_plus_applies", helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.DerivedPostRecoveryHealthySource);
            Assert.Equal(12L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.DerivedPostRecoveryProofFrameId);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount", 2);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 2L);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2 = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.SteadyVisibleProgressActive);
            Assert.Equal(12L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.StableVisibleHeadFrameId);
            Assert.Equal(12L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.LastVisibleApplyFrameId);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.FramesAppliedSinceLastGap >= 8);
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostRecoveryHealthyLatch_ActivatesFromRecoveryFloorAndLaterApply()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        DateTimeOffset now = new DateTimeOffset(2026, 4, 21, 8, 0, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "sessionId", "pressure-latch-session");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            string value = sessionRuntime.SecurityState.SessionId.Value.Value;
            ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(value, 1L, 10L);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 180L, 1L, 10L, 10L, 10L, 1L);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.SteadyVisibleProgressActive);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BaselineEstablished);
            now = now.AddMilliseconds(100.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 190L, 1L, 11L, 11L, 11L, 2L);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2 = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.SteadyVisibleProgressActive);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.BaselineEstablished);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.FramesAppliedSinceLastGap >= 2);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.DerivedPostRecoveryHealthyActive);
            Assert.Equal("recovery_floor_plus_head", helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.DerivedPostRecoveryHealthySource);
            Assert.Equal(11L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.DerivedPostRecoveryProofFrameId);
            Assert.Equal(1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.PostRecoveryHealthyLatchCount);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.PostRecoveryHealthyLatchClearCount);
            Assert.Equal("none", helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.DominantPressureBlocker);
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_DerivedHealthyState_UsesEpochFactsEvenWhenLatchStateIsCleared()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        DateTimeOffset now = new DateTimeOffset(2026, 4, 21, 8, 5, 0, TimeSpan.Zero);
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
            ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(value, 1L, 33L);
            ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(value, 1L, 80L, isKeyFrame: false, 0L);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 180L, 1L, 80L, 80L, 80L, 48L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressEpoch", 0L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyVisibleProgressActive", false);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressActivationFrameId", -1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressVisibleHeadFrameId", -1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", -1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 0L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochContinuityLossTicks", 7L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupTicks", 5L);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.DerivedPostRecoveryHealthyActive);
            Assert.Equal("recovery_floor_plus_head", helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.DerivedPostRecoveryHealthySource);
            Assert.Equal(80L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.DerivedPostRecoveryProofFrameId);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.SteadyVisibleProgressActive);
            Assert.Equal(48L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.FramesAppliedSinceLastGap);
            Assert.Equal("none", helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.DominantPressureBlocker);
            Assert.Equal(7L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.ContinuityLossTicks);
            Assert.Equal(5L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.WarmupTicks);
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostRecoveryHealthyLatch_PersistsAcrossNonHealthyPressure()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 21, 8, 10, 0, TimeSpan.Zero);
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
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount", 12);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500.0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 2L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameCount", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameIndex", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameAgesMs", new long[3] { 1400L, 0L, 0L });
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameAgeMs", 1400L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastApplyCadenceMs", 120L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteApplyCadenceObserved", 12L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteTotalApplyCadenceMs", 1440L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyVisibleProgressActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressActivationFrameId", 10L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressVisibleHeadFrameId", 15L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 15L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 6L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemotePostRecoveryHealthyLatchCount", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemotePostRecoveryHealthyLastHeadAdvanceUtc", now.AddMilliseconds(-100.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "healthy");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureAgeMs", 700L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "healthyScreenSharePressureIntervals", 4);
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.SteadyVisibleProgressActive);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BaselineEstablished);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.FramesAppliedSinceLastGap > 0);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.PostRecoveryHealthyLatchClearCount);
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostRecoveryHealthyLatch_ClearsOnlyOnRealStall()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 21, 8, 20, 0, TimeSpan.Zero);
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
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 220.0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 2L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameCount", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameIndex", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameAgesMs", new long[3] { 220L, 0L, 0L });
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameAgeMs", 220L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-800.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastApplyCadenceMs", 120L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteApplyCadenceObserved", 10L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteTotalApplyCadenceMs", 1200L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyVisibleProgressActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressActivationFrameId", 10L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressVisibleHeadFrameId", 15L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 15L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 6L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemotePostRecoveryHealthyLatchCount", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemotePostRecoveryHealthyLastHeadAdvanceUtc", now.AddMilliseconds(-800.0));
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.SteadyVisibleProgressActive);
            Assert.Equal(1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.PostRecoveryHealthyLatchClearCount);
            Assert.Equal("post_recovery_stall", helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.PostRecoveryHealthyLatchClearReason);
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostRecoveryStallRelatch_ReseedsBaselineInsteadOfAnchoringToSingleHighAgeFrame()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        DateTimeOffset now = new DateTimeOffset(2026, 4, 23, 7, 10, 0, TimeSpan.Zero);
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
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochFirstVisibleApplyUtc", now.AddSeconds(-9.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 15L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 220.0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 2L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameCount", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameIndex", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameAgesMs", new long[3] { 220L, 0L, 0L });
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameAgeMs", 220L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-800.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastApplyCadenceMs", 120L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteApplyCadenceObserved", 10L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteTotalApplyCadenceMs", 1200L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyVisibleProgressActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressActivationFrameId", 10L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressVisibleHeadFrameId", 15L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 15L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 6L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemotePostRecoveryHealthyLatchCount", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemotePostRecoveryHealthyLastHeadAdvanceUtc", now.AddMilliseconds(-800.0));
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.SteadyVisibleProgressActive);
            Assert.Equal("post_recovery_stall", helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.PostRecoveryHealthyLatchClearReason);
            Assert.Equal(1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BaselineFrozenDueToStallCount);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BaselineReseedInProgress);
            now = now.AddMilliseconds(100.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 419L, 1L, 16L, 16L, 16L, 17L);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2 = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.SteadyVisibleProgressActive);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.BaselineReseedInProgress);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.BaselineEstablished);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.BaselineCaptureToRenderMs <= 0);
            now = now.AddMilliseconds(120.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 200L, 1L, 17L, 17L, 17L, 18L);
            now = now.AddMilliseconds(120.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 210L, 1L, 18L, 18L, 18L, 19L);
            now = now.AddMilliseconds(120.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 220L, 1L, 19L, 19L, 19L, 20L);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests3 = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests3.BaselineEstablished);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests3.BaselineReseedInProgress);
            Assert.InRange(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests3.BaselineCaptureToRenderMs, 200L, 220L);
            ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_HealthyPressureResendsWhenStableHeadAdvances()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 18, 9, 30, 0, TimeSpan.Zero);
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
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount", 8);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500.0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameCount", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameIndex", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameAgesMs", new long[3] { 220L, 0L, 0L });
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameAgeMs", 220L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastApplyCadenceMs", 120L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteApplyCadenceObserved", 8L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteTotalApplyCadenceMs", 960L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "healthy");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureAgeMs", 220L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "healthyScreenSharePressureIntervals", 4);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentSteadyProgressEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentSteadyVisibleProgressActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentVisibleHeadFrameId", 12L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentStableVisibleHeadFrameId", 12L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentFramesAppliedSinceLastGap", 8L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentVisibleApplyFrameId", 12L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentAppliedHeadFrameId", 12L);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 230L, 1L, 16L, 16L, 16L, 12L);
            transport.SentPressureStates.Clear();
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "healthy");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureAgeMs", 220L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "healthyScreenSharePressureIntervals", 4);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentSteadyProgressEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentSteadyVisibleProgressActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentVisibleHeadFrameId", 12L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentStableVisibleHeadFrameId", 12L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentFramesAppliedSinceLastGap", 8L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentVisibleApplyFrameId", 12L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentAppliedHeadFrameId", 12L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteProofKeepaliveSendCount", 0L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastProofKeepaliveHeadFrameId", -1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastProofKeepaliveSentUtc", default(DateTimeOffset));
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            ScreenSharePressureStateV1[] collection = transport.SentPressureStates.ToArray();
            ScreenSharePressureStateV1 screenSharePressureStateV = Assert.Single(collection);
            Assert.Equal(ScreenSharePressureMode.Normal, screenSharePressureStateV.Mode);
            Assert.Equal("healthy", screenSharePressureStateV.Reason);
            Assert.Equal(16L, screenSharePressureStateV.AppliedHeadFrameId);
            Assert.Equal(16L, screenSharePressureStateV.StableVisibleHeadFrameId);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.Equal(1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.ProofKeepaliveSendCount);
            Assert.Equal(16L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.ProofKeepaliveLastHeadFrameId);
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_HealthyPressure_ResendsBoundedProofKeepaliveWithoutHeadAdvance()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 21, 8, 30, 0, TimeSpan.Zero);
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
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount", 8);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 16L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500.0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 230L, 1L, 16L, 16L, 16L, 8L);
            transport.SentPressureStates.Clear();
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "healthy");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureAgeMs", 220L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddMilliseconds(-450.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "healthyScreenSharePressureIntervals", 4);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentSteadyProgressEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentSteadyVisibleProgressActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentVisibleHeadFrameId", 16L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentStableVisibleHeadFrameId", 16L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentFramesAppliedSinceLastGap", 8L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentVisibleApplyFrameId", 16L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentAppliedHeadFrameId", 16L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteProofKeepaliveSendCount", 0L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastProofKeepaliveHeadFrameId", -1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastProofKeepaliveSentUtc", default(DateTimeOffset));
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            ScreenSharePressureStateV1[] collection = transport.SentPressureStates.ToArray();
            ScreenSharePressureStateV1 screenSharePressureStateV = Assert.Single(collection);
            Assert.Equal(ScreenSharePressureMode.Normal, screenSharePressureStateV.Mode);
            Assert.Equal("healthy", screenSharePressureStateV.Reason);
            Assert.Equal(16L, screenSharePressureStateV.AppliedHeadFrameId);
            Assert.Equal(16L, screenSharePressureStateV.StableVisibleHeadFrameId);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.Equal(1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.ProofKeepaliveSendCount);
            Assert.Equal(16L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.ProofKeepaliveLastHeadFrameId);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.ProofKeepaliveLastSendAgeMs);
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_TimerDrivenHealthyProofKeepalive_RefreshesWithoutFrameCallbacks()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 21, 8, 35, 0, TimeSpan.Zero);
        ScreenShareAwareSignalingTransportDouble transport = new ScreenShareAwareSignalingTransportDouble();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default, null, null, null, null, null, null, () => now);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "transport", transport);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(ScreenShareTransportBoundaryTestBase.GetScreenShareControlHost(sessionRuntime), "remoteScreenShareActive", true);
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "WireTransport", transport);
            transport.SetSessionSecurityStateForTests(ScreenShareTransportBoundaryTestBase.CreateApprovedSecurityState(new PeerAddress("pressure.helpee"), new PeerAddress("pressure.helper"), CapabilityGrant.ScreenShare));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc", now.AddSeconds(-5.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount", 8);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 16L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 200.0);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameCount", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameIndex", 1);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteRecentAppliedFrameAgesMs", new long[3]);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameAgeMs", 0L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-300.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastApplyCadenceMs", 120L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteApplyCadenceObserved", 8L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteTotalApplyCadenceMs", 960L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyVisibleProgressActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressActivationFrameId", 12L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 16L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressVisibleHeadFrameId", 16L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 8L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "healthy");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureAgeMs", 0L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now.AddMilliseconds(-450.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5.0));
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "healthyScreenSharePressureIntervals", 4);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentSteadyProgressEpoch", 1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentSteadyVisibleProgressActive", true);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentVisibleHeadFrameId", 16L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentStableVisibleHeadFrameId", 16L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentFramesAppliedSinceLastGap", 8L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentVisibleApplyFrameId", 16L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastSentAppliedHeadFrameId", 16L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteProofKeepaliveSendCount", 0L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteProofKeepaliveTimerDrivenSendCount", 0L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastProofKeepaliveHeadFrameId", -1L);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "helperRemoteLastProofKeepaliveSentUtc", default(DateTimeOffset));
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "OnHelperRemoteScreenSharePressureTimerTick");
            ScreenSharePressureStateV1 screenSharePressureStateV = ScreenShareTransportBoundaryTestBase.WaitForSinglePressureState(transport.SentPressureStates);
            Assert.Equal(ScreenSharePressureMode.Normal, screenSharePressureStateV.Mode);
            Assert.Equal("healthy", screenSharePressureStateV.Reason);
            Assert.Equal(16L, screenSharePressureStateV.AppliedHeadFrameId);
            Assert.Equal(16L, screenSharePressureStateV.StableVisibleHeadFrameId);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.Equal(1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.ProofKeepaliveSendCount);
            Assert.Equal(1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.ProofKeepaliveTimerDrivenSendCount);
            Assert.Equal(16L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.ProofKeepaliveLastHeadFrameId);
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_DuplicateVisibleApplyForSameFrame_DoesNotDoubleCountProgress()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 17, 9, 45, 0, TimeSpan.Zero);
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
            now = now.AddMilliseconds(50.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 10L, 10L, 0, "started");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "ReportHelperRemoteScreenShareRecoveryKeyframeApplied", 220L, 1L);
            transport.SentPressureStates.Clear();
            now = now.AddMilliseconds(50.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 230L, 1L, 10L);
            transport.SentPressureStates.Clear();
            now = now.AddMilliseconds(50.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 240L, 1L, 10L);
            Thread.Sleep(100);
            Assert.Empty(transport.SentPressureStates);
            Assert.Equal(1, Assert.IsType<int>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount")));
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_FirstVisibleApplyDuringContinuityLoss_BypassesThrottle()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 20, 10, 0, 0, TimeSpan.Zero);
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
            transport.SentPressureStates.Clear();
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureReason", "continuity_loss");
            ScreenShareTransportBoundaryTestBase.SetPrivateField(sessionRuntime, "lastSentScreenSharePressureUtc", now);
            now = now.AddMilliseconds(50.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 230L, 1L, 10L);
            Thread.Sleep(100);
            ScreenSharePressureStateV1 screenSharePressureStateV = Assert.Single(transport.SentPressureStates);
            Assert.Equal(ScreenSharePressureMode.Normal, screenSharePressureStateV.Mode);
            Assert.Equal("continuity_loss", screenSharePressureStateV.Reason);
            Assert.Equal(10L, screenSharePressureStateV.LastVisibleApplyFrameId);
            transport.SentPressureStates.Clear();
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "MaybeSendScreenSharePressureState");
            Assert.Empty(transport.SentPressureStates);
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

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureDiagnosticsSnapshot_CorrelatesVisibleProgressWithRecoveryState()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        DateTimeOffset now = new DateTimeOffset(2026, 4, 14, 15, 20, 0, TimeSpan.Zero);
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
            ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(value, 1L, "gap_detected", 4L, 6L);
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "ReportHelperRemoteScreenShareContinuityLost", 1L, "frame_gap", true, 4L, 6L, -1L, 12L);
            Thread.Sleep(100);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.Equal(1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.StreamEpoch);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.ContinuityLossTicks > 0);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.WarmupTicks > 0);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.BeforeFirstVisibleApplyTicks > 0);
            Assert.Equal("continuity_loss", helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.DominantPressureBlocker);
            ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(value, 1L, 10L, isKeyFrame: true, 0L);
            ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(value, 1L, "recovery_keyframe_applied", 10L, -1L);
            ScreenShareFrameLossAttributionRegistry.ObserveRecoveryKeyframeResync(value, 1L, 10L);
            ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(value, 1L, 10L);
            now = now.AddMilliseconds(50.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteRecoveryWindowStateChanged(sessionRuntime, 1L, 10L, 10L, 0, "started");
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "ReportHelperRemoteScreenShareRecoveryKeyframeApplied", 180L, 1L);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 180L, 1L, 10L);
            now = now.AddMilliseconds(50.0);
            ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(value, 1L, 11L, isKeyFrame: false, 0L);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 175L, 1L, 11L);
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2 = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.Equal(11L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.LastVisibleApplyFrameId);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.FramesAppliedSinceLastGap >= 2);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.DerivedPostRecoveryHealthyActive);
            Assert.Equal("recovery_floor_plus_head", helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.DerivedPostRecoveryHealthySource);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.CurrentEpochGapCount >= 1);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.CurrentEpochRecoveryKeyframeApplyCount >= 1);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.CurrentEpochResyncCount >= 1);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.PostRecoverySettleWindowCount);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.PostRecoverySettleWindowSuccessCount);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.RecoveryWindowProgressed);
            Assert.False(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.RecoveryWindowSucceeded);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.RecoveryWindowProgressedCount);
            Assert.Equal(0L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.RecoveryWindowSuccessCount);
            Assert.Equal(HelperRemoteSessionPhase.VisibleStable, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.HelperSessionPhase);
            Assert.Equal(HelperRemoteRecoveryMechanism.None, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.HelperRecoveryMechanism);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.CurrentEpochProgressProven);
            Assert.Equal("none", helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.DominantPressureBlocker);
            Assert.Equal(-1L, helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.VisibleAppliesBeforePressureReenabled);
            Assert.Equal(Assert.IsType<int>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochApplyCount")), (int)helperRemoteScreenSharePressureDiagnosticsSnapshotForTests2.FramesAppliedSinceLastGap);
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
