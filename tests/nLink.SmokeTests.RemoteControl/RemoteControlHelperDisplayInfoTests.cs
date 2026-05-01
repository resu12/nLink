using System.Reflection;
using System.Threading;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.Core;
using NLink.Core.RemoteControl;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Trait("Area", "RemoteControl")]
public sealed class RemoteControlHelperDisplayInfoTests : RemoteControlP4TestBase
{
    [Fact]
    public async Task HelperRequestScopeReset_ClearsMapping_AndBlocksInputUntilFreshDisplayInfoArrives()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helper);
        SetRemoteControlState(runtime, ControlState.Active, "helpee-peer", "req-old");

        transport.InjectIncomingControlDisplayInfo(
            CreateDisplayInfoMessage(
                displayId: "primary",
                revision: 1,
                captureX: 0,
                captureY: 0,
                captureWidth: 1920,
                captureHeight: 1080,
                frameWidth: 1920,
                frameHeight: 1080),
            peerId: "helpee-peer");
        await WaitUntilAsync(() => runtime.RemoteControlMappingAvailable, TimeSpan.FromSeconds(1));

        SetRemoteControlState(runtime, ControlState.Active, "helpee-peer", "req-new");
        InvokePrivateMethod(runtime, "ResetRemoteControlRequestScopedTracking", "request_id_changed");

        Assert.False(runtime.RemoteControlMappingAvailable);
        var blocked = await runtime.SendRemoteControlInputAsync(
            new ControlInputMessageV1
            {
                Kind = "mouse_button",
                Action = "down",
                Button = "left",
                Nx = 0.5,
                Ny = 0.5,
            },
            CancellationToken.None);
        Assert.False(blocked);
        Assert.Equal(0, transport.SentControlInputCount);

        var stateChangedCount = 0;
        runtime.RemoteControlStateChanged += (_, _) => stateChangedCount++;

        transport.InjectIncomingControlDisplayInfo(
            CreateDisplayInfoMessage(
                displayId: "primary",
                revision: 2,
                captureX: 10,
                captureY: 20,
                captureWidth: 1280,
                captureHeight: 720,
                frameWidth: 1280,
                frameHeight: 720),
            peerId: "helpee-peer");
        await WaitUntilAsync(
            () => runtime.RemoteControlMappingAvailable && stateChangedCount > 0,
            TimeSpan.FromSeconds(1));

        var sent = await runtime.SendRemoteControlInputAsync(
            new ControlInputMessageV1
            {
                Kind = "mouse_button",
                Action = "down",
                Button = "left",
                Nx = 0.25,
                Ny = 0.75,
            },
            CancellationToken.None);
        Assert.True(sent);
        Assert.Equal(1, transport.SentControlInputCount);
        Assert.Equal("req-new", transport.GetLastSentControlInput()?.RequestId);
        Assert.Equal(2, transport.GetLastSentControlInput()?.DisplayInfoRevision);
    }


    [Fact]
    public async Task HelperReceivesDisplayInfoChange_ShowsScreenChangedTransientStatus()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helper);

        var first = CreateDisplayInfoMessage(
            displayId: "primary",
            revision: 1,
            captureX: 0,
            captureY: 0,
            captureWidth: 1920,
            captureHeight: 1080,
            frameWidth: 1920,
            frameHeight: 1080);
        var changed = CreateDisplayInfoMessage(
            displayId: "primary",
            revision: 2,
            captureX: 100,
            captureY: 50,
            captureWidth: 1280,
            captureHeight: 720,
            frameWidth: 1280,
            frameHeight: 720);

        transport.InjectIncomingControlDisplayInfo(first, peerId: "controller-peer");
        await WaitUntilAsync(() => runtime.RemoteControlMappingAvailable, TimeSpan.FromSeconds(1));

        transport.InjectIncomingControlDisplayInfo(changed, peerId: "controller-peer");
        await WaitUntilAsync(
            () => runtime.IsTransientStatusVisible &&
                  string.Equals(runtime.TransientStatusText, "Screen changed", StringComparison.Ordinal),
            TimeSpan.FromSeconds(1));

        await WaitUntilAsync(
            () => !runtime.IsTransientStatusVisible ||
                  !string.Equals(runtime.TransientStatusText, "Screen changed", StringComparison.Ordinal),
            TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task HelperDisplayInfoChange_WhenActive_AutoStopsRemoteControl()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helper);
        SetRemoteControlState(runtime, ControlState.Active, "helpee-peer", "req-1");

        var first = CreateDisplayInfoMessage(
            displayId: "primary",
            revision: 1,
            captureX: 0,
            captureY: 0,
            captureWidth: 1920,
            captureHeight: 1080,
            frameWidth: 1920,
            frameHeight: 1080);
        var changed = CreateDisplayInfoMessage(
            displayId: "primary",
            revision: 2,
            captureX: 100,
            captureY: 50,
            captureWidth: 1280,
            captureHeight: 720,
            frameWidth: 1280,
            frameHeight: 720);

        transport.InjectIncomingControlDisplayInfo(first, peerId: "helpee-peer");
        await WaitUntilAsync(() => runtime.RemoteControlMappingAvailable, TimeSpan.FromSeconds(1));

        transport.InjectIncomingControlDisplayInfo(changed, peerId: "helpee-peer");
        await WaitUntilAsync(() => transport.SentControlStopCount >= 1, TimeSpan.FromSeconds(1));

        Assert.Equal(ControlState.Off, runtime.ControlState);
        Assert.Equal("screen_changed", transport.GetLastSentControlStop()?.Reason);
    }

    [Fact]
    public async Task HelperDisplayInfoFrameSizeChange_WhenActive_DoesNotAutoStopRemoteControl()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helper);
        SetRemoteControlState(runtime, ControlState.Active, "helpee-peer", "req-1");

        var first = CreateDisplayInfoMessage(
            displayId: "primary",
            revision: 1,
            captureX: 0,
            captureY: 0,
            captureWidth: 1920,
            captureHeight: 1080,
            frameWidth: 1280,
            frameHeight: 720);
        var frameOnlyChanged = CreateDisplayInfoMessage(
            displayId: "primary",
            revision: 2,
            captureX: 0,
            captureY: 0,
            captureWidth: 1920,
            captureHeight: 1080,
            frameWidth: 960,
            frameHeight: 540);

        transport.InjectIncomingControlDisplayInfo(first, peerId: "helpee-peer");
        await WaitUntilAsync(() => runtime.RemoteControlMappingAvailable, TimeSpan.FromSeconds(1));

        transport.InjectIncomingControlDisplayInfo(frameOnlyChanged, peerId: "helpee-peer");
        await Task.Delay(150);

        Assert.Equal(ControlState.Active, runtime.ControlState);
        Assert.Equal(0, transport.SentControlStopCount);
    }

    [Fact]
    public async Task HelperDisplayInfoFirstReceive_WhenRequesting_DoesNotAutoStopRemoteControl()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helper);
        SetRemoteControlState(runtime, ControlState.Requesting, "helpee-peer", "req-1");

        var first = CreateDisplayInfoMessage(
            displayId: "primary",
            revision: 1,
            captureX: 0,
            captureY: 0,
            captureWidth: 1920,
            captureHeight: 1080,
            frameWidth: 960,
            frameHeight: 540);

        transport.InjectIncomingControlDisplayInfo(first, peerId: "helpee-peer");
        await WaitUntilAsync(() => runtime.RemoteControlMappingAvailable, TimeSpan.FromSeconds(1));

        Assert.Equal(ControlState.Requesting, runtime.ControlState);
        Assert.Equal(0, transport.SentControlStopCount);
    }

}
