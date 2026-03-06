using System.Reflection;
using System.Threading;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.Core;
using NLink.Core.RemoteControl;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

public sealed class RemoteControlP4Tests
{
    private static readonly SemaphoreSlim FeatureFlagEnvGate = new(1, 1);

    [Fact]
    public async Task RemoteControlInput_WhenControlStateNotActive_DoesNotInject()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, ControlState.Off, "controller-peer", "req-1");

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-1",
                Kind = "key",
                Action = "down",
                Key = "A",
            },
            peerId: "controller-peer");

        await Task.Delay(120);
        Assert.Equal(0, injector.TotalCalls);
    }

    [Fact]
    public async Task RemoteControlInput_WhenRoleIsNotHelpee_DoesNotInject()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helper);
        SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-1",
                Kind = "key",
                Action = "down",
                Key = "A",
            },
            peerId: "controller-peer");

        await Task.Delay(120);
        Assert.Equal(0, injector.TotalCalls);
    }

    [Fact]
    public async Task RemoteControlInput_WhenControllerMismatch_DoesNotInject()
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
                Kind = "key",
                Action = "down",
                Key = "A",
            },
            peerId: "other-peer");

        await Task.Delay(120);
        Assert.Equal(0, injector.TotalCalls);
    }

    [Fact]
    public async Task RemoteControlInput_WhenAllGuardsPass_InjectsInput()
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
                Kind = "key",
                Action = "down",
                Key = "A",
            },
            peerId: "controller-peer");

        await WaitUntilAsync(() => injector.TotalCalls == 1, TimeSpan.FromSeconds(1));
        Assert.Equal(1, injector.TotalCalls);
    }

    [Fact]
    public async Task RemoteControlInput_WhenApprovalRevoked_StopsAndBlocksFurtherInjection()
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
                Kind = "key",
                Action = "down",
                Key = "A",
            },
            peerId: "controller-peer");

        await WaitUntilAsync(() => injector.TotalCalls == 1, TimeSpan.FromSeconds(1));

        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                helperIdentity: "controller-peer",
                helpeeIdentity: "helpee-peer",
                capabilities: CapabilityGrant.Chat));

        await WaitUntilAsync(() => runtime.ControlState == ControlState.Off, TimeSpan.FromSeconds(1));

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-1",
                Kind = "key",
                Action = "down",
                Key = "B",
            },
            peerId: "controller-peer");

        await Task.Delay(150);
        Assert.Equal(1, injector.TotalCalls);
        Assert.False(runtime.CanPerform(SessionCapability.RemoteControl));
    }

    [Fact]
    public async Task RemoteControlSnapshot_WhenButtonWasAppliedButSnapshotHasNone_InjectsButtonUpOnce()
    {
        await FeatureFlagEnvGate.WaitAsync();
        try
        {
            using var snapshotFlag = new EnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_STATE_SNAPSHOT", "1");

            var transport = new TestRemoteControlTransport();
            var injector = new CountingRemoteInputInjector();
            var mapper = new FixedRemoteCoordinateMapper();
            using var runtime = CreateRuntime(transport, injector, mapper);
            AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
            SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");
            SetPrivateField(runtime, "remoteControlAppliedMouseButtonsMask", RemoteControlMouseButtonsMask.Left);

            transport.InjectIncomingControlStateSnapshot(
                new ControlStateSnapshotV1
                {
                    RequestId = "req-1",
                    Seq = 1,
                    TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ModifiersMask = 0,
                    MouseButtonsMask = 0,
                },
                peerId: "controller-peer");

            await WaitUntilAsync(
                () => injector.MouseButtonCalls == 1 &&
                      injector.LastMouseButton == RemoteMouseButton.Left &&
                      injector.LastMouseButtonAction == RemoteButtonAction.Up,
                TimeSpan.FromSeconds(1));
            Assert.Equal(1, injector.MouseButtonCalls);
            Assert.Equal(RemoteMouseButton.Left, injector.LastMouseButton);
            Assert.Equal(RemoteButtonAction.Up, injector.LastMouseButtonAction);
        }
        finally
        {
            FeatureFlagEnvGate.Release();
        }
    }

    [Fact]
    public async Task RemoteControlSnapshot_WhenSnapshotOnlyIndicatesDown_DoesNotInjectConservativePress()
    {
        await FeatureFlagEnvGate.WaitAsync();
        try
        {
            using var snapshotFlag = new EnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_STATE_SNAPSHOT", "1");

            var transport = new TestRemoteControlTransport();
            var injector = new CountingRemoteInputInjector();
            var mapper = new FixedRemoteCoordinateMapper();
            using var runtime = CreateRuntime(transport, injector, mapper);
            AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
            SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");
            SetPrivateField(runtime, "remoteControlAppliedMouseButtonsMask", RemoteControlMouseButtonsMask.None);

            transport.InjectIncomingControlStateSnapshot(
                new ControlStateSnapshotV1
                {
                    RequestId = "req-1",
                    Seq = 2,
                    TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ModifiersMask = 0,
                    MouseButtonsMask = (int)RemoteControlMouseButtonsMask.Left,
                },
                peerId: "controller-peer");

            await Task.Delay(150);
            Assert.Equal(0, injector.MouseButtonCalls);
        }
        finally
        {
            FeatureFlagEnvGate.Release();
        }
    }

    [Fact]
    public async Task RemoteControlSnapshot_ForceDownEnabled_ReappliesDownAfterStableSnapshotStream()
    {
        await FeatureFlagEnvGate.WaitAsync();
        try
        {
            using var snapshotFlag = new EnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_STATE_SNAPSHOT", "1");
            using var forceDownFlag = new EnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_STATE_SNAPSHOT_FORCE_DOWN", "1");

            var transport = new TestRemoteControlTransport();
            var injector = new CountingRemoteInputInjector();
            var mapper = new FixedRemoteCoordinateMapper();
            using var runtime = CreateRuntime(transport, injector, mapper);
            AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
            SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");
            SetPrivateField(runtime, "remoteControlAppliedMouseButtonsMask", RemoteControlMouseButtonsMask.Left);

            transport.InjectIncomingControlStateSnapshot(
                new ControlStateSnapshotV1
                {
                    RequestId = "req-1",
                    Seq = 1,
                    TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ModifiersMask = 0,
                    MouseButtonsMask = 0,
                },
                peerId: "controller-peer");

            await WaitUntilAsync(
                () => injector.MouseButtonCalls == 1 &&
                      injector.LastMouseButton == RemoteMouseButton.Left &&
                      injector.LastMouseButtonAction == RemoteButtonAction.Up,
                TimeSpan.FromSeconds(1));

            for (var seq = 2; seq <= 6; seq++)
            {
                await Task.Delay(100);
                transport.InjectIncomingControlStateSnapshot(
                    new ControlStateSnapshotV1
                    {
                        RequestId = "req-1",
                        Seq = seq,
                        TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ModifiersMask = 0,
                        MouseButtonsMask = 0,
                    },
                    peerId: "controller-peer");
            }

            transport.InjectIncomingControlStateSnapshot(
                new ControlStateSnapshotV1
                {
                    RequestId = "req-1",
                    Seq = 7,
                    TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ModifiersMask = 0,
                    MouseButtonsMask = (int)RemoteControlMouseButtonsMask.Left,
                },
                peerId: "controller-peer");

            await WaitUntilAsync(
                () => injector.MouseButtonCalls >= 2 &&
                      injector.LastMouseButton == RemoteMouseButton.Left &&
                      injector.LastMouseButtonAction == RemoteButtonAction.Down,
                TimeSpan.FromSeconds(1));

            Assert.True(injector.MouseButtonCalls >= 2);
            Assert.Equal(RemoteMouseButton.Left, injector.LastMouseButton);
            Assert.Equal(RemoteButtonAction.Down, injector.LastMouseButtonAction);
        }
        finally
        {
            FeatureFlagEnvGate.Release();
        }
    }

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

        await InvokePrivateAsync(runtime, "SendRemoteControlDisplayInfoAsync", first, CancellationToken.None);
        await WaitUntilAsync(() => transport.SentControlDisplayInfoCount == 1, TimeSpan.FromSeconds(1));
        Assert.Equal(ControlState.Active, runtime.ControlState);

        await InvokePrivateAsync(runtime, "SendRemoteControlDisplayInfoAsync", changed, CancellationToken.None);
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

        await InvokePrivateAsync(runtime, "SendRemoteControlDisplayInfoAsync", first, CancellationToken.None);
        await WaitUntilAsync(() => transport.SentControlDisplayInfoCount == 1, TimeSpan.FromSeconds(1));
        Assert.Equal(ControlState.Active, runtime.ControlState);

        await InvokePrivateAsync(runtime, "SendRemoteControlDisplayInfoAsync", frameOnlyChanged, CancellationToken.None);
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
        await InvokePrivateAsync(runtime, "SendRemoteControlDisplayInfoAsync", displayInfo, CancellationToken.None);

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

        transport.InjectIncomingControlInput(wheelMessage, peerId: "controller-peer");
        transport.InjectIncomingControlInput(wheelMessage, peerId: "controller-peer");
        transport.InjectIncomingControlInput(wheelMessage, peerId: "controller-peer");

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
        await InvokePrivateAsync(runtime, "SendRemoteControlDisplayInfoAsync", displayInfo, CancellationToken.None);

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
        await InvokePrivateAsync(runtime, "SendRemoteControlDisplayInfoAsync", displayInfo, CancellationToken.None);

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
    public async Task RemoteControlInput_WhenRevisionMismatchButDisplayMatches_IsAccepted()
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
        await InvokePrivateAsync(runtime, "SendRemoteControlDisplayInfoAsync", displayInfo, CancellationToken.None);

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-1",
                Kind = "mouse_move",
                Nx = 0.5d,
                Ny = 0.5d,
                DisplayId = "primary",
                DisplayInfoRevision = 1, // stale, should still be accepted when displayId matches
            },
            peerId: "controller-peer");

        await WaitUntilAsync(() => injector.MouseMoveCalls == 1, TimeSpan.FromSeconds(1));
        Assert.Equal(ControlState.Active, runtime.ControlState);
        Assert.Equal(0, transport.SentControlStopCount);
        Assert.Equal(150, injector.LastMouseMoveX);
        Assert.Equal(110, injector.LastMouseMoveY);
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
        await InvokePrivateAsync(runtime, "SendRemoteControlDisplayInfoAsync", displayInfo, CancellationToken.None);

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
    public void DefaultRemoteCoordinateMapper_ClampsCoordinates()
    {
        var (x, y) = DefaultRemoteCoordinateMapper.MapNormalizedToBounds(
            nx: -0.2d,
            ny: 1.4d,
            bounds: new RemoteDesktopBounds(Left: 100, Top: 50, Width: 1920, Height: 1080));

        Assert.Equal(100, x);
        Assert.Equal(1129, y);
    }

    [Fact]
    public void DefaultRemoteCoordinateMapper_MapsMidpointForTypicalBounds()
    {
        var (x, y) = DefaultRemoteCoordinateMapper.MapNormalizedToBounds(
            nx: 0.5d,
            ny: 0.5d,
            bounds: new RemoteDesktopBounds(Left: 100, Top: 50, Width: 1920, Height: 1080));

        Assert.Equal(1060, x);
        Assert.Equal(590, y);
    }

    [Fact]
    public void WindowsRemoteInputMath_PixelToAbsoluteCoordinate_ClampsToRange()
    {
        Assert.Equal(0, WindowsRemoteInputMath.PixelToAbsoluteCoordinate(pixelValue: -500, origin: 0, length: 1920));
        Assert.Equal(0, WindowsRemoteInputMath.PixelToAbsoluteCoordinate(pixelValue: 0, origin: 0, length: 1920));
        Assert.Equal(65535, WindowsRemoteInputMath.PixelToAbsoluteCoordinate(pixelValue: 1919, origin: 0, length: 1920));
        Assert.Equal(65535, WindowsRemoteInputMath.PixelToAbsoluteCoordinate(pixelValue: 999999, origin: 0, length: 1920));
    }

    [Fact]
    public void WindowsRemoteInputMath_ScaleWheelDelta_UsesWheelTicks()
    {
        Assert.Equal(120, WindowsRemoteInputMath.ScaleWheelDelta(1));
        Assert.Equal(-240, WindowsRemoteInputMath.ScaleWheelDelta(-2));
        Assert.Equal(int.MaxValue, WindowsRemoteInputMath.ScaleWheelDelta(int.MaxValue));
        Assert.Equal(int.MinValue, WindowsRemoteInputMath.ScaleWheelDelta(int.MinValue));
    }

    private static SessionRuntime CreateRuntime(
        TestRemoteControlTransport transport,
        CountingRemoteInputInjector injector,
        FixedRemoteCoordinateMapper mapper)
    {
        return new SessionRuntime(
            () => transport,
            watchdogOptions: null,
            watchdogDelayAsync: null,
            telemetrySink: null,
            bridgeReusePolicy: null,
            bridgeIdleDelayAsync: null,
            remoteInputInjector: injector,
            remoteCoordinateMapper: mapper);
    }

    private static void AttachConnectedRuntime(
        SessionRuntime runtime,
        TestRemoteControlTransport transport,
        SessionRuntimeRole role)
    {
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        runtime.SetRoleForTests(role);
        _ = InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                helperIdentity: role == SessionRuntimeRole.Helpee ? "controller-peer" : "helper-peer",
                helpeeIdentity: "helpee-peer"));
        _ = InvokePrivateMethod(runtime, "RefreshRemoteControlCapabilitiesFromTransport");
    }

    private static SessionSecurityState CreateApprovedSecurityState(
        string helperIdentity,
        string helpeeIdentity,
        CapabilityGrant capabilities = CapabilityGrant.RemoteControl)
    {
        var sessionId = new SessionId(
            $"rc_p4_{NormalizeSessionToken(helpeeIdentity)}_{NormalizeSessionToken(helperIdentity)}");
        var helpeeAddress = new PeerAddress(helpeeIdentity);
        var helperAddress = new PeerAddress(helperIdentity);
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = true,
        }).WithHandshakeVerified(helperAddress)
          .WithApproval(new SessionGrant(
              helperAddress,
              capabilities,
              sessionId,
              DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    private static string NormalizeSessionToken(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        foreach (var ch in value)
        {
            buffer[length++] = char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_';
        }

        return new string(buffer[..length]);
    }

    private static void SetRemoteControlState(
        SessionRuntime runtime,
        ControlState controlState,
        string controllerPeerId,
        string requestId)
    {
        SetPrivateField(
            runtime,
            "remoteControlSessionState",
            new RemoteControlSessionState(
                ControlState: controlState,
                ControllerPeerId: controllerPeerId,
                CurrentControlRequestId: requestId,
                ConsentToken: null,
                SupportsRemoteControl: true,
                PeerSupportsRemoteControl: true));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        while ((DateTimeOffset.UtcNow - start) < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), $"Condition was not met within {timeout}.");
    }

    private static async Task InvokePrivateAsync(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(target, args);
        if (result is Task task)
        {
            await task.ConfigureAwait(false);
        }
    }

    private static ControlDisplayInfoMessageV1 CreateDisplayInfoMessage(
        string displayId,
        long revision,
        int captureX,
        int captureY,
        int captureWidth,
        int captureHeight,
        int frameWidth,
        int frameHeight)
    {
        return new ControlDisplayInfoMessageV1
        {
            DisplayId = displayId,
            VirtualDesktopX = 0,
            VirtualDesktopY = 0,
            VirtualDesktopWidth = 3840,
            VirtualDesktopHeight = 2160,
            CaptureRegionX = captureX,
            CaptureRegionY = captureY,
            CaptureRegionWidth = captureWidth,
            CaptureRegionHeight = captureHeight,
            FrameWidth = frameWidth,
            FrameHeight = frameHeight,
            DpiScale = 1.0d,
            Revision = revision,
            TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static object? InvokePrivateMethod(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(target, args);
    }

    private sealed class EnvironmentOverride : IDisposable
    {
        private readonly string key;
        private readonly string? previousValue;
        private bool disposed;

        public EnvironmentOverride(string key, string value)
        {
            this.key = key;
            previousValue = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            Environment.SetEnvironmentVariable(key, previousValue);
            disposed = true;
        }
    }

    private sealed class CountingRemoteInputInjector : IRemoteInputInjector
    {
        private int totalCalls;
        private int mouseMoveCalls;
        private int mouseButtonCalls;
        private int wheelCalls;
        private int lastMouseMoveX;
        private int lastMouseMoveY;
        private int lastWheelDeltaX;
        private int lastWheelDeltaY;
        private int lastMouseButton;
        private int lastMouseButtonAction;

        public bool IsSupported => true;

        public int TotalCalls => Volatile.Read(ref totalCalls);
        public int MouseMoveCalls => Volatile.Read(ref mouseMoveCalls);
        public int MouseButtonCalls => Volatile.Read(ref mouseButtonCalls);
        public int WheelCalls => Volatile.Read(ref wheelCalls);
        public int LastMouseMoveX => Volatile.Read(ref lastMouseMoveX);
        public int LastMouseMoveY => Volatile.Read(ref lastMouseMoveY);
        public int LastWheelDeltaX => Volatile.Read(ref lastWheelDeltaX);
        public int LastWheelDeltaY => Volatile.Read(ref lastWheelDeltaY);
        public RemoteMouseButton LastMouseButton => (RemoteMouseButton)Volatile.Read(ref lastMouseButton);
        public RemoteButtonAction LastMouseButtonAction => (RemoteButtonAction)Volatile.Read(ref lastMouseButtonAction);

        public void InjectMouseMoveAbsolute(int xPx, int yPx)
        {
            Volatile.Write(ref lastMouseMoveX, xPx);
            Volatile.Write(ref lastMouseMoveY, yPx);
            Interlocked.Increment(ref mouseMoveCalls);
            Interlocked.Increment(ref totalCalls);
        }

        public void InjectMouseButton(RemoteMouseButton button, RemoteButtonAction action)
        {
            Volatile.Write(ref lastMouseButton, (int)button);
            Volatile.Write(ref lastMouseButtonAction, (int)action);
            Interlocked.Increment(ref mouseButtonCalls);
            Interlocked.Increment(ref totalCalls);
        }

        public void InjectMouseWheel(int deltaX, int deltaY)
        {
            Volatile.Write(ref lastWheelDeltaX, deltaX);
            Volatile.Write(ref lastWheelDeltaY, deltaY);
            Interlocked.Increment(ref wheelCalls);
            Interlocked.Increment(ref totalCalls);
        }

        public void InjectKey(RemoteKey key, RemoteKeyAction action, RemoteKeyModifiers mods)
        {
            Interlocked.Increment(ref totalCalls);
        }
    }

    private sealed class FixedRemoteCoordinateMapper : IRemoteCoordinateMapper
    {
        public bool IsMappingAvailable => true;

        public (int xPx, int yPx) MapNormalizedToVirtualDesktop(double nx, double ny)
        {
            return (100, 200);
        }
    }

#pragma warning disable CS0067
    private sealed class TestRemoteControlTransport :
        ISignalingTransport,
        IAddressTargetSignalingTransport,
        IAddressHostSignalingTransport,
        ISessionSecuritySignalingTransport,
        IRemoteControlCapabilityProvider,
        IRemoteControlSignalingTransport
    {
        private readonly object gate = new();
        private readonly List<ControlStopMessageV1> sentControlStops = new();
        private readonly List<ControlDisplayInfoMessageV1> sentControlDisplayInfos = new();
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public bool LocalSupportsRemoteControl => true;

        public bool RemoteSupportsRemoteControl => true;

        public bool SessionSupportsRemoteControl => true;
        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public int SentControlStopCount
        {
            get
            {
                lock (gate)
                {
                    return sentControlStops.Count;
                }
            }
        }

        public int SentControlDisplayInfoCount
        {
            get
            {
                lock (gate)
                {
                    return sentControlDisplayInfos.Count;
                }
            }
        }

        public ControlStopMessageV1? GetLastSentControlStop()
        {
            lock (gate)
            {
                return sentControlStops.Count == 0 ? null : sentControlStops[^1];
            }
        }

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;

        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;

        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;

        public event EventHandler? Approved;

        public event EventHandler? Rejected;

        public event EventHandler? Disconnected;
        public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;

        public event EventHandler<RemoteControlRequestReceivedEventArgs>? RemoteControlRequestReceived;

        public event EventHandler<RemoteControlResponseReceivedEventArgs>? RemoteControlResponseReceived;

        public event EventHandler<RemoteControlStartReceivedEventArgs>? RemoteControlStartReceived;

        public event EventHandler<RemoteControlStopReceivedEventArgs>? RemoteControlStopReceived;

        public event EventHandler<RemoteControlInputReceivedEventArgs>? RemoteControlInputReceived;
        public event EventHandler<RemoteControlAckReceivedEventArgs>? RemoteControlAckReceived;
        public event EventHandler<RemoteControlStateSnapshotReceivedEventArgs>? RemoteControlStateSnapshotReceived;
        public event EventHandler<RemoteControlDisplayInfoReceivedEventArgs>? RemoteControlDisplayInfoReceived;

        public void Dispose()
        {
        }

        public Task HostByAddressAsync(CancellationToken ct) => Task.CompletedTask;

        public Task JoinByAddressAsync(string peerAddress, CancellationToken ct) => Task.CompletedTask;

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlRequestAsync(ControlRequestMessageV1 message, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlResponseAsync(ControlResponseMessageV1 message, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlStartAsync(ControlStartMessageV1 message, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlStopAsync(ControlStopMessageV1 message, CancellationToken ct)
        {
            lock (gate)
            {
                sentControlStops.Add(message);
            }

            return Task.CompletedTask;
        }

        public Task SendControlInputAsync(ControlInputMessageV1 message, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlAckAsync(ControlInputAckV1 message, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlStateSnapshotAsync(ControlStateSnapshotV1 message, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlDisplayInfoAsync(ControlDisplayInfoMessageV1 message, CancellationToken ct)
        {
            lock (gate)
            {
                sentControlDisplayInfos.Add(message);
            }

            return Task.CompletedTask;
        }

        public void SetSessionSecurityStateForTests(SessionSecurityState nextState)
        {
            if (Equals(currentSessionSecurityState, nextState))
            {
                return;
            }

            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        }

        public void InjectIncomingControlInput(ControlInputMessageV1 message, string? peerId)
        {
            RemoteControlInputReceived?.Invoke(this, new RemoteControlInputReceivedEventArgs(message, peerId));
        }

        public void InjectIncomingControlStateSnapshot(ControlStateSnapshotV1 snapshot, string? peerId)
        {
            RemoteControlStateSnapshotReceived?.Invoke(
                this,
                new RemoteControlStateSnapshotReceivedEventArgs(snapshot, peerId ?? string.Empty));
        }

        public void InjectIncomingControlDisplayInfo(ControlDisplayInfoMessageV1 message, string? peerId)
        {
            RemoteControlDisplayInfoReceived?.Invoke(this, new RemoteControlDisplayInfoReceivedEventArgs(message, peerId));
        }
    }
#pragma warning restore CS0067
}
