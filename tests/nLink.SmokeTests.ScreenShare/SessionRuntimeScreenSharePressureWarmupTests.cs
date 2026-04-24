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
public sealed class SessionRuntimeScreenSharePressureWarmupTests : ScreenShareTransportBoundaryTestBase
{
    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_StartupWarmupSuppressesEarlyHighFrameAgePressure()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 2, 8, 30, 0, TimeSpan.Zero);
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
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 1250L, 1L);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 1320L, 1L);
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_ThreeConsecutiveHighAppliedFrames_WithoutBaseline_DoNotSendCatchUpOnly()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 2, 8, 31, 0, TimeSpan.Zero);
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
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 1250L, 1L);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 1320L, 1L);
            Thread.Sleep(50);
            transport.SentPressureStates.Clear();
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 1290L, 1L);
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_StartupWarmupSuppressesStaleDropPressure()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 2, 8, 32, 0, TimeSpan.Zero);
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
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 2600L, 1L);
            Thread.Sleep(50);
            transport.SentPressureStates.Clear();
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteStaleDrop(sessionRuntime, 2400L, 1L);
            Thread.Sleep(50);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteStaleDrop(sessionRuntime, 2300L, 1L);
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_IsolatedLateSamplesWithOngoingProgress_DoNotSendReduceFps()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 14, 12, 0, 0, TimeSpan.Zero);
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
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 210L, 1L, 9L, 9L, 9L, 10L, ScreenShareTransportBoundaryTestBase.CreateHelperSessionSnapshot(1L, 9L, 9L, 9L, 10L, -1L));
            now = now.AddMilliseconds(610.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 230L, 1L, 10L, 10L, 10L, 11L, ScreenShareTransportBoundaryTestBase.CreateHelperSessionSnapshot(1L, 10L, 10L, 10L, 11L, -1L));
            now = now.AddMilliseconds(640.0);
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 250L, 1L, 11L, 11L, 11L, 12L, ScreenShareTransportBoundaryTestBase.CreateHelperSessionSnapshot(1L, 11L, 11L, 11L, 12L, -1L));
            Thread.Sleep(100);
            Assert.DoesNotContain(transport.SentPressureStates, (ScreenSharePressureStateV1 sent) => sent.Mode != ScreenSharePressureMode.Normal || !string.Equals(sent.Reason, "healthy", StringComparison.Ordinal));
            SessionRuntime.HelperRemoteScreenSharePressureDiagnosticsSnapshot helperRemoteScreenSharePressureDiagnosticsSnapshotForTests = sessionRuntime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.SteadyVisibleProgressActive);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.CurrentEpochProgressProven);
            Assert.True(helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.AppliedHeadAdvancedSinceLastEvaluation || helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.StableVisibleHeadAdvancedSinceLastEvaluation);
            Assert.NotEqual("none", helperRemoteScreenSharePressureDiagnosticsSnapshotForTests.HelperHealthyStateEstablishedBy);
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_NewEpochNeedMoreInputBeforeFirstApply_DoesNotDemote()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 13, 19, 0, 0, TimeSpan.Zero);
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
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "TrackHelperRemoteScreenShareAcceptedFrame", new ScreenShareFrameCompletedEventArgs(2L, 1280, 720, "h264", new byte[1] { 1 }, 0L, 0L, 0L, value, IsKeyFrame: false, 2L, null, ScreenShareRecoveryDeliveryClass.Normal, 0L));
            sessionRuntime.ReportHelperRemoteScreenShareDecodeNeedsMoreInput(2L);
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
    public void SessionRuntime_HelperScreenSharePressureFeedback_NewEpochResetsTailMetricsBeforeHealthyApply()
    {
        DateTimeOffset now = new DateTimeOffset(2026, 4, 13, 19, 5, 0, TimeSpan.Zero);
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
            string value = sessionRuntime.SecurityState.SessionId.Value.Value;
            ScreenShareTransportBoundaryTestBase.InvokePrivateMethod(sessionRuntime, "TrackHelperRemoteScreenShareAcceptedFrame", new ScreenShareFrameCompletedEventArgs(3L, 1280, 720, "h264", new byte[1] { 2 }, 0L, 0L, 0L, value, IsKeyFrame: false, 2L, null, ScreenShareRecoveryDeliveryClass.Normal, 0L));
            ScreenShareTransportBoundaryTestBase.ReportHelperRemoteFrameApplied(sessionRuntime, 180L, 2L, 3L);
            Thread.Sleep(100);
            Assert.Empty(transport.SentPressureStates);
            Assert.False(Assert.IsType<bool>(ScreenShareTransportBoundaryTestBase.GetPrivateField(sessionRuntime, "helperRemoteCurrentPressureEpochBaselineEstablished")));
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
