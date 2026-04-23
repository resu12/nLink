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
public sealed class RemoteControlHelpeeDisplayMappingTests : RemoteControlP4TestBase
{
    [Fact]
    public async Task RemoteControlInput_MouseMove_WithoutDisplayInfo_DoesNotInject()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-1",
                Kind = "mouse_move",
                Nx = 0.5d,
                Ny = 0.5d,
                DisplayId = "primary",
                DisplayInfoRevision = 1,
            },
            peerId: "controller-peer");

        await Task.Delay(150);
        Assert.Equal(0, injector.MouseMoveCalls);
        Assert.Equal(ControlState.Active, runtime.ControlState);
    }

    [Fact]
    public async Task DisplayInfoChange_WhenControlActiveOnHelpee_AutoStopsRemoteControl()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");

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
            captureX: 0,
            captureY: 0,
            captureWidth: 1280,
            captureHeight: 720,
            frameWidth: 1280,
            frameHeight: 720);

        await SendDisplayInfoAsync(runtime, first);
        await WaitUntilAsync(() => transport.SentControlDisplayInfoCount == 1, TimeSpan.FromSeconds(1));
        Assert.Equal(ControlState.Active, runtime.ControlState);

        await SendDisplayInfoAsync(runtime, changed);
        await WaitUntilAsync(() => transport.SentControlStopCount >= 1, TimeSpan.FromSeconds(1));
        Assert.Equal(ControlState.Off, runtime.ControlState);
        Assert.Equal("screen_changed", transport.GetLastSentControlStop()?.Reason);
    }

    [Fact]
    public async Task DisplayInfoFrameSizeChange_WhenControlActiveOnHelpee_DoesNotAutoStopRemoteControl()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");

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

        await SendDisplayInfoAsync(runtime, first);
        await WaitUntilAsync(() => transport.SentControlDisplayInfoCount == 1, TimeSpan.FromSeconds(1));
        Assert.Equal(ControlState.Active, runtime.ControlState);

        await SendDisplayInfoAsync(runtime, frameOnlyChanged);
        await Task.Delay(150);
        Assert.Equal(ControlState.Active, runtime.ControlState);
        Assert.Equal(0, transport.SentControlStopCount);
    }

    [Fact]
    public async Task RemoteControlInput_MouseMove_UsesCaptureRegionOffsetMapping()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");

        var displayInfo = CreateDisplayInfoMessage(
            displayId: "primary",
            revision: 3,
            captureX: 100,
            captureY: 200,
            captureWidth: 1000,
            captureHeight: 500,
            frameWidth: 1000,
            frameHeight: 500);
        await SendDisplayInfoAsync(runtime, displayInfo);

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-1",
                Kind = "mouse_move",
                Nx = 0.5d,
                Ny = 0.25d,
                DisplayId = "primary",
                DisplayInfoRevision = 3,
            },
            peerId: "controller-peer");

        await WaitUntilAsync(() => injector.MouseMoveCalls == 1, TimeSpan.FromSeconds(1));
        Assert.Equal(600, injector.LastMouseMoveX);
        Assert.Equal(325, injector.LastMouseMoveY);
    }

    [Fact]
    public async Task RemoteControlInput_MouseWheel_FractionalDeltasAreAccumulated()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");

        var wheelMessage = new ControlInputMessageV1
        {
            RequestId = "req-1",
            Kind = "mouse_wheel",
            Nx = 0.5d,
            Ny = 0.5d,
            DeltaX = 0d,
            DeltaY = 0.4d,
        };

        InvokeProcessRemoteControlInjection(
            runtime,
            wheelMessage with { Seq = 1, TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            "controller-peer");
        InvokeProcessRemoteControlInjection(
            runtime,
            wheelMessage with { Seq = 2, TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            "controller-peer");
        InvokeProcessRemoteControlInjection(
            runtime,
            wheelMessage with { Seq = 3, TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            "controller-peer");

        await WaitUntilAsync(() => injector.WheelCalls >= 1, TimeSpan.FromSeconds(1));
        Assert.Equal(1, injector.WheelCalls);
        Assert.Equal(0, injector.LastWheelDeltaX);
        Assert.Equal(1, injector.LastWheelDeltaY);
    }

    [Theory]
    [InlineData(-1.0d, 2.0d, 300, 400, 640, 480, 300, 879)]
    [InlineData(0.6d, 0.6d, 10, 20, 3, 5, 11, 22)]
    public async Task RemoteControlInput_MouseMove_ClampsWithinCaptureRegion(
        double nx,
        double ny,
        int captureX,
        int captureY,
        int captureWidth,
        int captureHeight,
        int expectedX,
        int expectedY)
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");

        var displayInfo = CreateDisplayInfoMessage(
            displayId: "primary",
            revision: 4,
            captureX: captureX,
            captureY: captureY,
            captureWidth: captureWidth,
            captureHeight: captureHeight,
            frameWidth: Math.Max(captureWidth, 1),
            frameHeight: Math.Max(captureHeight, 1));
        await SendDisplayInfoAsync(runtime, displayInfo);

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-1",
                Kind = "mouse_move",
                Nx = nx,
                Ny = ny,
                DisplayId = "primary",
                DisplayInfoRevision = 4,
            },
            peerId: "controller-peer");

        await WaitUntilAsync(() => injector.MouseMoveCalls == 1, TimeSpan.FromSeconds(1));
        Assert.Equal(expectedX, injector.LastMouseMoveX);
        Assert.Equal(expectedY, injector.LastMouseMoveY);
    }

    [Fact]
    public async Task RemoteControlInput_WhenDisplayIdMismatch_AutoStopsAndSkipsInjection()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");

        var displayInfo = CreateDisplayInfoMessage(
            displayId: "primary",
            revision: 7,
            captureX: 0,
            captureY: 0,
            captureWidth: 1920,
            captureHeight: 1080,
            frameWidth: 1920,
            frameHeight: 1080);
        await SendDisplayInfoAsync(runtime, displayInfo);

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-1",
                Kind = "mouse_move",
                Nx = 0.5d,
                Ny = 0.5d,
                DisplayId = "secondary",
                DisplayInfoRevision = 7,
            },
            peerId: "controller-peer");

        await WaitUntilAsync(() => transport.SentControlStopCount >= 1, TimeSpan.FromSeconds(1));
        Assert.Equal(0, injector.MouseMoveCalls);
        Assert.Equal(ControlState.Off, runtime.ControlState);
        Assert.Equal("display_id_mismatch", transport.GetLastSentControlStop()?.Reason);
    }

    [Fact]
    public async Task RemoteControlInput_WhenRevisionMismatchButDisplayMatches_IsRejected()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");

        var displayInfo = CreateDisplayInfoMessage(
            displayId: "primary",
            revision: 9,
            captureX: 50,
            captureY: 60,
            captureWidth: 200,
            captureHeight: 100,
            frameWidth: 200,
            frameHeight: 100);
        await SendDisplayInfoAsync(runtime, displayInfo);

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-1",
                Kind = "mouse_move",
                Nx = 0.5d,
                Ny = 0.5d,
                DisplayId = "primary",
                DisplayInfoRevision = 1, // stale and now rejected until mapping is refreshed
            },
            peerId: "controller-peer");

        await Task.Delay(150);
        Assert.Equal(0, injector.MouseMoveCalls);
        Assert.Equal(ControlState.Active, runtime.ControlState);
        Assert.Equal(0, transport.SentControlStopCount);
    }


    [Fact]
    public async Task HelpeeDisplayInfoSend_WhenCapturedSessionDoesNotMatchCurrentSession_IsIgnored()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");

        var staleSessionId = "stale-session";
        var currentSessionId = runtime.SecurityState.SessionId?.ToString();
        Assert.NotEqual(staleSessionId, currentSessionId);

        await InvokePrivateAsync(
            runtime,
            "SendRemoteControlDisplayInfoAsync",
            staleSessionId,
            CreateDisplayInfoMessage(
                displayId: "primary",
                revision: 1,
                captureX: 0,
                captureY: 0,
                captureWidth: 1920,
                captureHeight: 1080,
                frameWidth: 1920,
                frameHeight: 1080),
            CancellationToken.None);

        Assert.Equal(0, transport.SentControlDisplayInfoCount);
    }


    [Fact]
    public async Task RemoteControlInput_AfterHelpeeStop_IgnoresStaleInputsImmediately()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");

        var displayInfo = CreateDisplayInfoMessage(
            displayId: "primary",
            revision: 10,
            captureX: 0,
            captureY: 0,
            captureWidth: 1280,
            captureHeight: 720,
            frameWidth: 1280,
            frameHeight: 720);
        await SendDisplayInfoAsync(runtime, displayInfo);

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-1",
                Kind = "mouse_move",
                Nx = 0.2d,
                Ny = 0.3d,
                DisplayId = "primary",
                DisplayInfoRevision = 10,
            },
            peerId: "controller-peer");

        await WaitUntilAsync(() => injector.MouseMoveCalls == 1, TimeSpan.FromSeconds(1));
        Assert.True(await runtime.StopRemoteControlAsync("helpee_stop"));
        Assert.Equal(ControlState.Off, runtime.ControlState);

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-1",
                Kind = "mouse_move",
                Nx = 0.8d,
                Ny = 0.9d,
                DisplayId = "primary",
                DisplayInfoRevision = 10,
            },
            peerId: "controller-peer");

        await Task.Delay(150);
        Assert.Equal(1, injector.MouseMoveCalls);
    }

}
