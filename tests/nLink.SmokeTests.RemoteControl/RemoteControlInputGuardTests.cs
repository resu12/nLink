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
public sealed class RemoteControlInputGuardTests : RemoteControlP4TestBase
{
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
}
