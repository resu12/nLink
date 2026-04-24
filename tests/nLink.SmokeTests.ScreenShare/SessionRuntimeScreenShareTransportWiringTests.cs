using System.Reflection;
using NLink.App.Services;
using NLink.App.Services.ScreenCapture;
using NLink.Core;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class SessionRuntimeScreenShareTransportWiringTests : ScreenShareTransportBoundaryTestBase
{
[Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_WiresScreenShareEventsThroughResolvedScreenShareTransport()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);
        var frameCount = 0;
        var stopCount = 0;

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("media-only.helpee"),
                new PeerAddress("media-only.helper"),
                CapabilityGrant.ScreenShare));

        runtime.ScreenShareFrameCompleted += (_, _) => frameCount++;
        runtime.ScreenShareStopped += (_, _) => stopCount++;

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        transport.RaiseScreenShareFrameCompleted(
            new ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", new byte[] { 0x01 }, SessionId: sessionId));
        transport.RaiseScreenShareStopped();

        Assert.Equal(1, frameCount);
        Assert.Equal(1, stopCount);
    }

[Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_SendScreenSharePayloadAsync_PreservesCapabilityAndSessionValidation()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("screen-send.helpee"),
                new PeerAddress("screen-send.helper"),
                CapabilityGrant.ScreenShare));

        await InvokePrivateAsync(
            runtime,
            "SendScreenSharePayloadCoreAsync",
            new ReadOnlyMemory<byte>(CreateFramePayload("other_session")),
            CancellationToken.None);
        Assert.Empty(transport.SentScreenSharePayloads);

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        await InvokePrivateAsync(
            runtime,
            "SendScreenSharePayloadCoreAsync",
            new ReadOnlyMemory<byte>(CreateFramePayload(sessionId)),
            CancellationToken.None);

        Assert.Single(transport.SentScreenSharePayloads);
    }

[Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_SendRemoteControlDisplayInfoAsync_StaysOnControlPlane()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("display-info.helpee"),
                new PeerAddress("display-info.helper"),
                CapabilityGrant.ScreenShare | CapabilityGrant.RemoteControl));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        await InvokePrivateAsync(
            runtime,
            "SendRemoteControlDisplayInfoAsync",
            sessionId,
            new ControlDisplayInfoMessageV1
            {
                SessionId = sessionId,
                DisplayId = "display-1",
                VirtualDesktopWidth = 1920,
                VirtualDesktopHeight = 1080,
                CaptureRegionWidth = 1920,
                CaptureRegionHeight = 1080,
                FrameWidth = 1920,
                FrameHeight = 1080,
                Revision = 1,
                TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            CancellationToken.None);

        Assert.Single(transport.SentDisplayInfoMessages);
        Assert.Empty(transport.SentScreenSharePayloads);
    }

[Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_SenderFiltering_AcceptsCurrentScreenShareTransportAndRejectsStaleScreenShareTransport()
    {
        using var currentTransport = new ScreenShareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => currentTransport);
        var frameCount = 0;

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", currentTransport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", currentTransport);
        currentTransport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("adapter-filter.helpee"),
                new PeerAddress("adapter-filter.helper"),
                CapabilityGrant.ScreenShare));

        runtime.ScreenShareFrameCompleted += (_, _) => frameCount++;
        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        InvokePrivateMethod(
            runtime,
            "OnTransportScreenShareFrameCompleted",
            currentTransport,
            new ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", new byte[] { 0x01 }, SessionId: sessionId));

        using var staleTransport = new ScreenShareSignalingTransportDouble();
        InvokePrivateMethod(
            runtime,
            "OnTransportScreenShareFrameCompleted",
            staleTransport,
            new ScreenShareFrameCompletedEventArgs(2, 1, 1, "jpeg", new byte[] { 0x02 }, SessionId: sessionId));

        Assert.Equal(1, frameCount);
    }

[Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_RemoteScreenShareActivity_SwitchesFileTransferFlowControlMode()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("flow-mode.helpee"),
                new PeerAddress("flow-mode.helper"),
                CapabilityGrant.ScreenShare));

        Assert.Equal("Background", GetFileTransferFlowControlMode(runtime));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        transport.RaiseScreenShareFrameCompleted(
            new ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", new byte[] { 0x01 }, SessionId: sessionId));

        Assert.Equal("Interactive", GetFileTransferFlowControlMode(runtime));

        transport.RaiseScreenShareStopped();

        Assert.Equal("Background", GetFileTransferFlowControlMode(runtime));
    }

private sealed class ScreenSharePolicyAwareTransportDouble :
        ScreenShareAwareSignalingTransportDouble,
        IScreenShareTransportPolicyController
    {
        public List<bool> PolicyUpdates { get; } = new();
        public List<string> QueueFlushReasons { get; } = new();

        public Task SetScreenShareTransportCatchUpOnlyAsync(bool active, CancellationToken ct)
        {
            PolicyUpdates.Add(active);
            return Task.CompletedTask;
        }

        public void FlushScreenShareTransportQueue(string reason)
        {
            QueueFlushReasons.Add(reason);
        }
    }
#pragma warning restore CS0067

}
