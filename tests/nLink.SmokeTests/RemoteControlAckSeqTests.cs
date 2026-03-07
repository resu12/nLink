using System.Diagnostics;
using System.Reflection;
using System.Text;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.Core;
using NLink.Core.RemoteControl;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class RemoteControlAckSeqTests
{
    private static readonly SemaphoreSlim FeatureFlagEnvGate = new(1, 1);

    [Trait("Category", "Smoke")]
    [Fact]
    public void RemoteControlAck_CodecRoundtrip_AndValidation()
    {
        var ack = new ControlInputAckV1
        {
            RequestId = "req-ack-1",
            AckSeq = 42,
            TsUtcMs = 1234567890,
        };

        var payload = RemoteControlPayloadCodec.Serialize(ack);
        Assert.True(RemoteControlPayloadCodec.TryDeserializeControlAck(payload, out var parsed));
        Assert.Equal(ack.RequestId, parsed.RequestId);
        Assert.Equal(ack.AckSeq, parsed.AckSeq);
        Assert.Equal(ack.TsUtcMs, parsed.TsUtcMs);

        Assert.False(RemoteControlPayloadCodec.TryDeserializeControlAck(
            Encoding.UTF8.GetBytes("""{"requestId":" ","ackSeq":1,"tsUtcMs":1}"""),
            out _));
        Assert.False(RemoteControlPayloadCodec.TryDeserializeControlAck(
            Encoding.UTF8.GetBytes("""{"requestId":"req","ackSeq":0,"tsUtcMs":1}"""),
            out _));
        Assert.False(RemoteControlPayloadCodec.TryDeserializeControlAck(
            Encoding.UTF8.GetBytes("""{"requestId":"req","ackSeq":1,"tsUtcMs":0}"""),
            out _));
        Assert.False(RemoteControlPayloadCodec.TryDeserializeControlAck(
            Encoding.UTF8.GetBytes("not-json"),
            out _));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void RemoteControlStateSnapshot_CodecRoundtrip_AndValidation()
    {
        var snapshot = new ControlStateSnapshotV1
        {
            RequestId = "req-snap-1",
            Seq = 7,
            TsUtcMs = 1234567890,
            ModifiersMask = (int)(RemoteControlModifiersMask.Shift | RemoteControlModifiersMask.Ctrl),
            MouseButtonsMask = (int)(RemoteControlMouseButtonsMask.Left | RemoteControlMouseButtonsMask.Right),
        };

        var payload = RemoteControlPayloadCodec.Serialize(snapshot);
        Assert.True(RemoteControlPayloadCodec.TryDeserializeControlStateSnapshot(payload, out var parsed));
        Assert.Equal(snapshot.RequestId, parsed.RequestId);
        Assert.Equal(snapshot.Seq, parsed.Seq);
        Assert.Equal(snapshot.TsUtcMs, parsed.TsUtcMs);
        Assert.Equal(snapshot.ModifiersMask, parsed.ModifiersMask);
        Assert.Equal(snapshot.MouseButtonsMask, parsed.MouseButtonsMask);

        Assert.False(RemoteControlPayloadCodec.TryDeserializeControlStateSnapshot(
            Encoding.UTF8.GetBytes("""{"requestId":" ","seq":1,"tsUtcMs":1,"modifiersMask":0,"mouseButtonsMask":0}"""),
            out _));
        Assert.False(RemoteControlPayloadCodec.TryDeserializeControlStateSnapshot(
            Encoding.UTF8.GetBytes("""{"requestId":"req","seq":0,"tsUtcMs":1,"modifiersMask":0,"mouseButtonsMask":0}"""),
            out _));
        Assert.False(RemoteControlPayloadCodec.TryDeserializeControlStateSnapshot(
            Encoding.UTF8.GetBytes("""{"requestId":"req","seq":1,"tsUtcMs":0,"modifiersMask":0,"mouseButtonsMask":0}"""),
            out _));
        Assert.False(RemoteControlPayloadCodec.TryDeserializeControlStateSnapshot(
            Encoding.UTF8.GetBytes("not-json"),
            out _));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void RemoteControlSeqGate_StrictMode_DedupesAndOnlyInjectsNextSeq()
    {
        FeatureFlagEnvGate.Wait();
        try
        {
            using var seqFlag = new EnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_SEQ_GATE", "1");
            using var ackFlag = new EnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_ACK", "0");

            var transport = new LinkedAckTransport("helpee-peer");
            var injector = new CountingRemoteInputInjector();
            using var runtime = new SessionRuntime(
                () => transport,
                SessionRuntimeWatchdogOptions.Default,
                remoteInputInjector: injector,
                remoteCoordinateMapper: new FixedRemoteCoordinateMapper());

            AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
            SetPrivateField(
                runtime,
                "remoteControlSessionState",
                new RemoteControlSessionState(
                    ControlState.Active,
                    ControllerPeerId: "helper-peer",
                    CurrentControlRequestId: "req-seq",
                    ConsentToken: null,
                    SupportsRemoteControl: true,
                    PeerSupportsRemoteControl: true));
            SetPrivateField(runtime, "lastRemoteControlInjectedSeq", 10L);

            InvokeProcessRemoteControlInjection(runtime, BuildKeyInput("req-seq", 10), "helper-peer");
            InvokeProcessRemoteControlInjection(runtime, BuildKeyInput("req-seq", 9), "helper-peer");
            InvokeProcessRemoteControlInjection(runtime, BuildKeyInput("req-seq", 12), "helper-peer");
            Assert.Equal(0, injector.KeyCalls);

            InvokeProcessRemoteControlInjection(runtime, BuildKeyInput("req-seq", 11), "helper-peer");
            Assert.Equal(1, injector.KeyCalls);
        }
        finally
        {
            FeatureFlagEnvGate.Release();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void RemoteControlSnapshotSeqGate_ApplyStage_DropsQueuedSnapshotOlderThanLastApplied()
    {
        FeatureFlagEnvGate.Wait();
        try
        {
            using var snapshotFlag = new EnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_STATE_SNAPSHOT", "1");

            var transport = new LinkedAckTransport("helpee-peer");
            var injector = new CountingRemoteInputInjector();
            using var runtime = new SessionRuntime(
                () => transport,
                SessionRuntimeWatchdogOptions.Default,
                remoteInputInjector: injector,
                remoteCoordinateMapper: new FixedRemoteCoordinateMapper());

            AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
            SetPrivateField(
                runtime,
                "remoteControlSessionState",
                new RemoteControlSessionState(
                    ControlState.Active,
                    ControllerPeerId: "helper-peer",
                    CurrentControlRequestId: "req-snapshot",
                    ConsentToken: null,
                    SupportsRemoteControl: true,
                    PeerSupportsRemoteControl: true));
            SetPrivateField(runtime, "remoteControlSnapshotLastAppliedSeq", 10L);

            InvokeProcessRemoteControlSnapshot(
                runtime,
                BuildSnapshot("req-snapshot", 9),
                "helper-peer");

            Assert.Equal(0L, (long)(GetPrivateField(runtime, "remoteControlSnapshotAppliedCount") ?? 0L));
            Assert.Equal(10L, (long)(GetPrivateField(runtime, "remoteControlSnapshotLastAppliedSeq") ?? 0L));
        }
        finally
        {
            FeatureFlagEnvGate.Release();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControlAck_HelperTracksAck_WhenHelpeeAcksPostInjection()
    {
        await FeatureFlagEnvGate.WaitAsync();
        try
        {
            using var seqFlag = new EnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_SEQ_GATE", "0");
            using var ackFlag = new EnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_ACK", "1");

            var helperTransport = new LinkedAckTransport("helper-peer");
            var helpeeTransport = new LinkedAckTransport("helpee-peer");
            helperTransport.Peer = helpeeTransport;
            helpeeTransport.Peer = helperTransport;

            var helpeeInjector = new CountingRemoteInputInjector();
            using var helperRuntime = new SessionRuntime(
                () => helperTransport,
                SessionRuntimeWatchdogOptions.Default,
                remoteInputInjector: new CountingRemoteInputInjector(),
                remoteCoordinateMapper: new FixedRemoteCoordinateMapper());
            using var helpeeRuntime = new SessionRuntime(
                () => helpeeTransport,
                SessionRuntimeWatchdogOptions.Default,
                remoteInputInjector: helpeeInjector,
                remoteCoordinateMapper: new FixedRemoteCoordinateMapper());

            AttachConnectedRuntime(helperRuntime, helperTransport, SessionRuntimeRole.Helper);
            AttachConnectedRuntime(helpeeRuntime, helpeeTransport, SessionRuntimeRole.Helpee);

            SetPrivateField(
                helperRuntime,
                "remoteControlSessionState",
                new RemoteControlSessionState(
                    ControlState.Active,
                    ControllerPeerId: "helper-peer",
                    CurrentControlRequestId: "req-ack-flow",
                    ConsentToken: null,
                    SupportsRemoteControl: true,
                    PeerSupportsRemoteControl: true));
            SetPrivateField(
                helpeeRuntime,
                "remoteControlSessionState",
                new RemoteControlSessionState(
                    ControlState.Active,
                    ControllerPeerId: "helper-peer",
                    CurrentControlRequestId: "req-ack-flow",
                    ConsentToken: null,
                    SupportsRemoteControl: true,
                    PeerSupportsRemoteControl: true));
            SetPrivateField(
                helperRuntime,
                "latestRemoteControlDisplayInfo",
                BuildDisplayInfo("display-1", revision: 1));

            var sent = await helperRuntime.SendRemoteControlInputAsync(
                new ControlInputMessageV1
                {
                    Kind = "key",
                    Action = "down",
                    Key = "A",
                },
                CancellationToken.None);
            Assert.True(sent);

            await WaitUntilAsync(
                () => (long)(GetPrivateField(helperRuntime, "helperRemoteControlLastAckSeq") ?? 0L) >= 1L,
                TimeSpan.FromSeconds(2));

            Assert.Equal(1L, (long)(GetPrivateField(helperRuntime, "helperRemoteControlLastAckSeq") ?? 0L));
            Assert.Equal(1, helpeeInjector.KeyCalls);
        }
        finally
        {
            FeatureFlagEnvGate.Release();
        }
    }

    private static ControlInputMessageV1 BuildKeyInput(string requestId, long seq)
    {
        return new ControlInputMessageV1
        {
            RequestId = requestId,
            Seq = seq,
            Kind = "key",
            Action = "down",
            Key = "A",
            TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private static ControlDisplayInfoMessageV1 BuildDisplayInfo(string displayId, long revision)
    {
        return new ControlDisplayInfoMessageV1
        {
            DisplayId = displayId,
            VirtualDesktopX = 0,
            VirtualDesktopY = 0,
            VirtualDesktopWidth = 1920,
            VirtualDesktopHeight = 1080,
            CaptureRegionX = 0,
            CaptureRegionY = 0,
            CaptureRegionWidth = 1920,
            CaptureRegionHeight = 1080,
            FrameWidth = 1920,
            FrameHeight = 1080,
            DpiScale = 1.0,
            Revision = revision,
            TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    private static ControlStateSnapshotV1 BuildSnapshot(string requestId, long seq)
    {
        return new ControlStateSnapshotV1
        {
            RequestId = requestId,
            Seq = seq,
            TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ModifiersMask = 0,
            MouseButtonsMask = 0,
        };
    }

    private static void AttachConnectedRuntime(
        SessionRuntime runtime,
        LinkedAckTransport transport,
        SessionRuntimeRole role)
    {
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        runtime.SetRoleForTests(role);
        _ = InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                helpeeIdentity: role == SessionRuntimeRole.Helpee
                    ? transport.LocalPeerId
                    : transport.Peer?.LocalPeerId ?? "helpee-peer",
                helperIdentity: role == SessionRuntimeRole.Helper
                    ? transport.LocalPeerId
                    : transport.Peer?.LocalPeerId ?? "helper-peer"));
        _ = InvokePrivateMethod(runtime, "RefreshRemoteControlCapabilitiesFromTransport");
    }

    private static SessionSecurityState CreateApprovedSecurityState(
        string helpeeIdentity,
        string helperIdentity,
        CapabilityGrant capabilities = CapabilityGrant.RemoteControl)
    {
        var sessionId = new SessionId(
            $"rc_ack_{NormalizeSessionToken(helpeeIdentity)}_{NormalizeSessionToken(helperIdentity)}");
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

    private static void InvokeProcessRemoteControlInjection(
        SessionRuntime runtime,
        ControlInputMessageV1 message,
        string peerId)
    {
        var workItemType = typeof(SessionRuntime).GetNestedType(
            "RemoteControlInjectionWorkItem",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(workItemType);
        var ctor = workItemType!.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single();
        var stopEpoch = (long)(GetPrivateField(runtime, "remoteControlStopPriorityEpoch") ?? 0L);
        var workItem = ctor.Invoke(new object?[] { message, null, peerId, stopEpoch });

        _ = InvokePrivateMethod(runtime, "ProcessRemoteControlInjectionWorkItem", workItem);
    }

    private static void InvokeProcessRemoteControlSnapshot(
        SessionRuntime runtime,
        ControlStateSnapshotV1 snapshot,
        string peerId)
    {
        var workItemType = typeof(SessionRuntime).GetNestedType(
            "RemoteControlInjectionWorkItem",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(workItemType);
        var ctor = workItemType!.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single();
        var stopEpoch = (long)(GetPrivateField(runtime, "remoteControlStopPriorityEpoch") ?? 0L);
        var workItem = ctor.Invoke(new object?[] { null, snapshot, peerId, stopEpoch });

        _ = InvokePrivateMethod(runtime, "ProcessRemoteControlInjectionWorkItem", workItem);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), $"Condition was not met within {timeout}.");
    }

    private static object? InvokePrivateMethod(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(target, args);
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static object? GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target);
    }

    private sealed class EnvironmentOverride : IDisposable
    {
        private readonly string name;
        private readonly string? previous;
        private bool disposed;

        public EnvironmentOverride(string name, string value)
        {
            this.name = name;
            previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    private sealed class FixedRemoteCoordinateMapper : IRemoteCoordinateMapper
    {
        public bool IsMappingAvailable => true;

        public (int xPx, int yPx) MapNormalizedToVirtualDesktop(double nx, double ny) => (100, 100);
    }

    private sealed class CountingRemoteInputInjector : IRemoteInputInjector
    {
        private int keyCalls;

        public bool IsSupported => true;

        public int KeyCalls => Volatile.Read(ref keyCalls);

        public void InjectMouseMoveAbsolute(int xPx, int yPx)
        {
        }

        public void InjectMouseButton(RemoteMouseButton button, RemoteButtonAction action)
        {
        }

        public void InjectMouseWheel(int deltaX, int deltaY)
        {
        }

        public void InjectKey(RemoteKey key, RemoteKeyAction action, RemoteKeyModifiers mods)
        {
            Interlocked.Increment(ref keyCalls);
        }
    }

#pragma warning disable CS0067
    private sealed class LinkedAckTransport :
        ISignalingTransport,
        IAddressTargetSignalingTransport,
        IAddressHostSignalingTransport,
        ISessionSecuritySignalingTransport,
        IRemoteControlCapabilityProvider,
        IRemoteControlSignalingTransport
    {
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public LinkedAckTransport(string localPeerId)
        {
            LocalPeerId = localPeerId;
        }

        public string LocalPeerId { get; }

        public LinkedAckTransport? Peer { get; set; }

        public bool LocalSupportsRemoteControl => true;

        public bool RemoteSupportsRemoteControl => true;

        public bool SessionSupportsRemoteControl => true;
        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

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

        public Task SendControlRequestAsync(ControlRequestMessageV1 message, CancellationToken ct)
        {
            Peer?.InjectIncomingControlRequest(message, LocalPeerId);
            return Task.CompletedTask;
        }

        public Task SendControlResponseAsync(ControlResponseMessageV1 message, CancellationToken ct)
        {
            Peer?.InjectIncomingControlResponse(message, LocalPeerId);
            return Task.CompletedTask;
        }

        public Task SendControlStartAsync(ControlStartMessageV1 message, CancellationToken ct)
        {
            Peer?.InjectIncomingControlStart(message, LocalPeerId);
            return Task.CompletedTask;
        }

        public Task SendControlStopAsync(ControlStopMessageV1 message, CancellationToken ct)
        {
            Peer?.InjectIncomingControlStop(message, LocalPeerId);
            return Task.CompletedTask;
        }

        public Task SendControlInputAsync(ControlInputMessageV1 message, CancellationToken ct)
        {
            Peer?.InjectIncomingControlInput(message, LocalPeerId);
            return Task.CompletedTask;
        }

        public Task SendControlAckAsync(ControlInputAckV1 message, CancellationToken ct)
        {
            Peer?.InjectIncomingControlAck(message, LocalPeerId);
            return Task.CompletedTask;
        }

        public Task SendControlStateSnapshotAsync(ControlStateSnapshotV1 message, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlDisplayInfoAsync(ControlDisplayInfoMessageV1 message, CancellationToken ct)
        {
            Peer?.InjectIncomingControlDisplayInfo(message, LocalPeerId);
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

        public void InjectIncomingControlRequest(ControlRequestMessageV1 message, string? peerId)
        {
            RemoteControlRequestReceived?.Invoke(this, new RemoteControlRequestReceivedEventArgs(message, peerId));
        }

        public void InjectIncomingControlResponse(ControlResponseMessageV1 message, string? peerId)
        {
            RemoteControlResponseReceived?.Invoke(this, new RemoteControlResponseReceivedEventArgs(message, peerId));
        }

        public void InjectIncomingControlStart(ControlStartMessageV1 message, string? peerId)
        {
            RemoteControlStartReceived?.Invoke(this, new RemoteControlStartReceivedEventArgs(message, peerId));
        }

        public void InjectIncomingControlStop(ControlStopMessageV1 message, string? peerId)
        {
            RemoteControlStopReceived?.Invoke(this, new RemoteControlStopReceivedEventArgs(message, peerId));
        }

        public void InjectIncomingControlInput(ControlInputMessageV1 message, string? peerId)
        {
            RemoteControlInputReceived?.Invoke(this, new RemoteControlInputReceivedEventArgs(message, peerId));
        }

        public void InjectIncomingControlAck(ControlInputAckV1 message, string? peerId)
        {
            RemoteControlAckReceived?.Invoke(this, new RemoteControlAckReceivedEventArgs(message, peerId));
        }

        public void InjectIncomingControlDisplayInfo(ControlDisplayInfoMessageV1 message, string? peerId)
        {
            RemoteControlDisplayInfoReceived?.Invoke(this, new RemoteControlDisplayInfoReceivedEventArgs(message, peerId));
        }
    }
#pragma warning restore CS0067
}
