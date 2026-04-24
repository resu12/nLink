using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using NLink.App;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Metrics;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.Retry;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Core.Logging;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class SessionRuntimeTransportStateTests : SessionRuntimeConnectionTestBase
{
    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionRuntime_TransportStateMachine_AllowsExpectedTransitions_AndStoresMonotonicTimestamps()
    {
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.Idle, TransportState.TransportInitializing));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.Idle, TransportState.Connected));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.TransportInitializing, TransportState.BridgeStarting));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.BridgeStarting, TransportState.BridgeReady));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.BridgeStarting, TransportState.Handshake));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.BridgeReady, TransportState.Connecting));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.Connecting, TransportState.Handshake));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.Handshake, TransportState.Connected));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.Connected, TransportState.Reconnecting));
        Assert.True(SessionRuntime.IsTransportTransitionAllowed(TransportState.Reconnecting, TransportState.Idle));
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        var idleTs = runtime.GetTransportStateEntryTimestamp(TransportState.Idle);
        Assert.True(idleTs > 0);
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "test"));
        Assert.True(runtime.GetTransportStateEntryTimestamp(TransportState.TransportInitializing) >= idleTs);
        Assert.Equal(TransportState.TransportInitializing, runtime.TransportLifecycleState);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionRuntime_TransportStateMachine_BlocksInvalidTransitions()
    {
        Assert.False(SessionRuntime.IsTransportTransitionAllowed(TransportState.Idle, TransportState.Handshake));
        Assert.False(SessionRuntime.IsTransportTransitionAllowed(TransportState.Disposed, TransportState.Idle));
        Assert.False(SessionRuntime.IsTransportTransitionAllowed(TransportState.BridgeReady, TransportState.BridgeStarting));
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        var idleTs = runtime.GetTransportStateEntryTimestamp(TransportState.Idle);
        var changed = runtime.TryTransitionTransportStateForTests(TransportState.Handshake, "invalid_test");
        Assert.False(changed);
        Assert.Equal(TransportState.Idle, runtime.TransportLifecycleState);
        Assert.Equal(idleTs, runtime.GetTransportStateEntryTimestamp(TransportState.Idle));
        Assert.Equal(0, runtime.GetTransportStateEntryTimestamp(TransportState.Handshake));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionRuntime_TransportDurations_AreRecorded_OnSuccess_AndNonNegative()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "test_start"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.BridgeStarting, "bridge"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.BridgeReady, "bridge_ready"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Connecting, "connect"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Handshake, "hs"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Connected, "done"));
        var bridgeMs = runtime.GetLastDurationMetricMilliseconds("bridge_start_duration_ms");
        var initMs = runtime.GetLastDurationMetricMilliseconds("transport_init_duration_ms");
        var connectMs = runtime.GetLastDurationMetricMilliseconds("connect_duration_ms");
        var handshakeMs = runtime.GetLastDurationMetricMilliseconds("handshake_duration_ms");
        Assert.NotNull(bridgeMs);
        Assert.NotNull(initMs);
        Assert.NotNull(connectMs);
        Assert.NotNull(handshakeMs);
        Assert.True(bridgeMs!.Value >= 0);
        Assert.True(initMs!.Value >= 0);
        Assert.True(connectMs!.Value >= 0);
        Assert.True(handshakeMs!.Value >= 0);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionRuntime_TransportDurations_AreRecorded_OnFailure_AndNonNegative()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "test_start"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.BridgeStarting, "bridge"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Failed, "bridge_fail"));
        var bridgeMs = runtime.GetLastDurationMetricMilliseconds("bridge_start_duration_ms");
        var initMs = runtime.GetLastDurationMetricMilliseconds("transport_init_duration_ms");
        var connectMs = runtime.GetLastDurationMetricMilliseconds("connect_duration_ms");
        Assert.NotNull(bridgeMs);
        Assert.NotNull(initMs);
        Assert.NotNull(connectMs);
        Assert.True(bridgeMs!.Value >= 0);
        Assert.True(initMs!.Value >= 0);
        Assert.True(connectMs!.Value >= 0);
    }

}
