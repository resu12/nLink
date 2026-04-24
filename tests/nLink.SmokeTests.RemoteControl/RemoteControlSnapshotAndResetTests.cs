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
public sealed class RemoteControlSnapshotAndResetTests : RemoteControlP4TestBase
{
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
    public async Task RemoteControlSnapshot_WhenModifierWasAppliedButSnapshotHasNone_InjectsModifierUpOnce()
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
            SetPrivateField(runtime, "remoteControlAppliedModifiersMask", RemoteControlModifiersMask.Shift);

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
                () => injector.KeyCalls == 1 &&
                      injector.LastKeyAction == RemoteKeyAction.Up &&
                      string.Equals(injector.LastKeyLogical, "Shift", StringComparison.Ordinal),
                TimeSpan.FromSeconds(1));
            Assert.Equal(1, injector.KeyCalls);
            Assert.Equal(RemoteKeyAction.Up, injector.LastKeyAction);
            Assert.Equal("Shift", injector.LastKeyLogical);
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
    public async Task RemoteControlSnapshot_WhenSeqReplayedOrOutOfOrder_IsIgnored()
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
                    Seq = 5,
                    TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ModifiersMask = 0,
                    MouseButtonsMask = 0,
                },
                peerId: "controller-peer");

            await WaitUntilAsync(
                () => injector.MouseButtonCalls == 1 &&
                      (long)(GetPrivateField(runtime, "remoteControlSnapshotAppliedCount") ?? 0L) == 1L,
                TimeSpan.FromSeconds(1));

            transport.InjectIncomingControlStateSnapshot(
                new ControlStateSnapshotV1
                {
                    RequestId = "req-1",
                    Seq = 5,
                    TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ModifiersMask = 0,
                    MouseButtonsMask = 0,
                },
                peerId: "controller-peer");
            transport.InjectIncomingControlStateSnapshot(
                new ControlStateSnapshotV1
                {
                    RequestId = "req-1",
                    Seq = 4,
                    TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ModifiersMask = 0,
                    MouseButtonsMask = 0,
                },
                peerId: "controller-peer");

            await Task.Delay(150);

            Assert.Equal(1, injector.MouseButtonCalls);
            Assert.Equal(1L, (long)(GetPrivateField(runtime, "remoteControlSnapshotAppliedCount") ?? 0L));
            Assert.Equal(1L, (long)(GetPrivateField(runtime, "remoteControlSnapshotReceivedCount") ?? 0L));
            Assert.Equal(5L, (long)(GetPrivateField(runtime, "remoteControlSnapshotLastReceivedSeq") ?? 0L));
            Assert.Equal(5L, (long)(GetPrivateField(runtime, "remoteControlSnapshotLastAppliedSeq") ?? 0L));
        }
        finally
        {
            FeatureFlagEnvGate.Release();
        }
    }

    [Fact]
    public async Task RemoteControlSnapshot_WhenSeqSkipsForward_IsAccepted()
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
                () => (long)(GetPrivateField(runtime, "remoteControlSnapshotAppliedCount") ?? 0L) == 1L,
                TimeSpan.FromSeconds(1));

            transport.InjectIncomingControlStateSnapshot(
                new ControlStateSnapshotV1
                {
                    RequestId = "req-1",
                    Seq = 3,
                    TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ModifiersMask = 0,
                    MouseButtonsMask = 0,
                },
                peerId: "controller-peer");

            await WaitUntilAsync(
                () => (long)(GetPrivateField(runtime, "remoteControlSnapshotAppliedCount") ?? 0L) == 2L,
                TimeSpan.FromSeconds(1));

            Assert.Equal(2L, (long)(GetPrivateField(runtime, "remoteControlSnapshotReceivedCount") ?? 0L));
            Assert.Equal(3L, (long)(GetPrivateField(runtime, "remoteControlSnapshotLastReceivedSeq") ?? 0L));
            Assert.Equal(3L, (long)(GetPrivateField(runtime, "remoteControlSnapshotLastAppliedSeq") ?? 0L));
            Assert.Equal(0, injector.MouseButtonCalls);
        }
        finally
        {
            FeatureFlagEnvGate.Release();
        }
    }


    [Fact]
    public void RemoteControlStateReset_ReleasesAppliedButtonsAndModifiers_Once()
    {
        var transport = new TestRemoteControlTransport();
        var injector = new CountingRemoteInputInjector();
        var mapper = new FixedRemoteCoordinateMapper();
        using var runtime = CreateRuntime(transport, injector, mapper);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, ControlState.Active, "controller-peer", "req-1");
        SetPrivateField(runtime, "remoteControlAppliedMouseButtonsMask", RemoteControlMouseButtonsMask.Left);
        SetPrivateField(runtime, "remoteControlAppliedModifiersMask", RemoteControlModifiersMask.Shift);

        InvokePrivateMethod(runtime, "ResetRemoteControlState", "test_reset");

        Assert.Equal(1, injector.MouseButtonCalls);
        Assert.Equal(RemoteMouseButton.Left, injector.LastMouseButton);
        Assert.Equal(RemoteButtonAction.Up, injector.LastMouseButtonAction);
        Assert.Equal(1, injector.KeyCalls);
        Assert.Equal("Shift", injector.LastKeyLogical);
        Assert.Equal(RemoteKeyAction.Up, injector.LastKeyAction);
        Assert.Equal(RemoteControlMouseButtonsMask.None, (RemoteControlMouseButtonsMask)(GetPrivateField(runtime, "remoteControlAppliedMouseButtonsMask") ?? RemoteControlMouseButtonsMask.Left));
        Assert.Equal(RemoteControlModifiersMask.None, (RemoteControlModifiersMask)(GetPrivateField(runtime, "remoteControlAppliedModifiersMask") ?? RemoteControlModifiersMask.Shift));

        InvokePrivateMethod(runtime, "ResetRemoteControlState", "test_reset_again");

        Assert.Equal(1, injector.MouseButtonCalls);
        Assert.Equal(1, injector.KeyCalls);
    }

}
