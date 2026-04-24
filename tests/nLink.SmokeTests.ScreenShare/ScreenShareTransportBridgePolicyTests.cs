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
public sealed class ScreenShareTransportBridgePolicyTests : ScreenShareTransportBoundaryTestBase
{
[Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_SenderDegradedMode_UpdatesTransportCatchUpPolicy()
    {
        using var transport = new ScreenSharePolicyAwareTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        SetPrivateField(GetScreenShareControlHost(runtime), "remoteScreenShareActive", true);
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("policy.helpee"),
                new PeerAddress("policy.helper"),
                CapabilityGrant.ScreenShare));

        var handler = runtime.GetType().GetMethod("OnScreenShareSenderDegradedModeChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);
        var degradedArgsType = runtime.GetType().Assembly.GetType("NLink.App.Services.ScreenCapture.ScreenShareSenderDegradedModeChangedEventArgs");
        Assert.NotNull(degradedArgsType);
        var degradedEnteredArgs = Activator.CreateInstance(
            degradedArgsType!,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object?[] { true },
            culture: null);
        Assert.NotNull(degradedEnteredArgs);

        handler!.Invoke(runtime, new[] { null, degradedEnteredArgs });
        WaitUntil(() => transport.PolicyUpdates.Count == 1);
        Assert.True(transport.PolicyUpdates[^1]);

        var degradedExitedArgs = Activator.CreateInstance(
            degradedArgsType!,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object?[] { false },
            culture: null);
        Assert.NotNull(degradedExitedArgs);

        handler.Invoke(runtime, new[] { null, degradedExitedArgs });
        WaitUntil(() => transport.PolicyUpdates.Count == 2);
        Assert.False(transport.PolicyUpdates[^1]);
    }

[Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_ScreenShareBridgePolicy_StartRetry_WaitsForRunningBridge()
    {
        var client = new BridgePolicyCapabilityClient();
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("bridge-policy-test", "bridge.policy.test.addr");

        using var transport = new NknSignalingTransport(client, options, identity);

        await InvokePrivateAsync(
            transport,
            "EnsureScreenShareBridgeSessionStartedAsync",
            CancellationToken.None);

        Assert.Empty(client.PolicyApplications);
        Assert.Equal(0L, GetPrivateLongField(transport, "screenShareBridgePolicyGeneration"));

        client.IsBridgeProcessRunning = true;

        await InvokePrivateAsync(
            transport,
            "EnsureScreenShareBridgeSessionStartedAsync",
            CancellationToken.None);

        var application = Assert.Single(client.PolicyApplications);
        Assert.Equal(BridgeScreenShareQueueMode.Normal, application.Mode);
        Assert.False(application.FlushQueued);
        Assert.Equal(1L, application.Generation);
        Assert.Equal(1L, GetPrivateLongField(transport, "screenShareBridgePolicyGeneration"));
    }

}
