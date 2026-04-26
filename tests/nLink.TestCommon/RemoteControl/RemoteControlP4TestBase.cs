using System.Reflection;
using System.Threading;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.Core;
using NLink.Core.RemoteControl;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

public abstract class RemoteControlP4TestBase
{
    private protected static readonly SemaphoreSlim FeatureFlagEnvGate = new(1, 1);

    private protected static SessionRuntime CreateRuntime(
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

    private protected static void AttachConnectedRuntime(
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

    private protected static SessionSecurityState CreateApprovedSecurityState(
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

    private protected static string NormalizeSessionToken(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        foreach (var ch in value)
        {
            buffer[length++] = char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_';
        }

        return new string(buffer[..length]);
    }

    private protected static void SetRemoteControlState(
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

    private protected static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
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

    private protected static async Task InvokePrivateAsync(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(target, args);
        if (result is Task task)
        {
            await task.ConfigureAwait(false);
        }
    }

    private protected static Task SendDisplayInfoAsync(SessionRuntime runtime, ControlDisplayInfoMessageV1 message)
    {
        var sessionId = runtime.SecurityState.SessionId?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        return InvokePrivateAsync(
            runtime,
            "SendRemoteControlDisplayInfoAsync",
            sessionId!,
            message,
            CancellationToken.None);
    }

    private protected static ControlDisplayInfoMessageV1 CreateDisplayInfoMessage(
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

    private protected static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private protected static object? GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target);
    }

    private protected static object? InvokePrivateMethod(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(target, args);
    }

    private protected static void InvokeProcessRemoteControlInjection(
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

    private protected sealed class EnvironmentOverride : IDisposable
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

    private protected sealed class CountingRemoteInputInjector : IRemoteInputInjector
    {
        private int totalCalls;
        private int mouseMoveCalls;
        private int mouseButtonCalls;
        private int wheelCalls;
        private int keyCalls;
        private int lastMouseMoveX;
        private int lastMouseMoveY;
        private int lastWheelDeltaX;
        private int lastWheelDeltaY;
        private int lastMouseButton;
        private int lastMouseButtonAction;
        private string? lastKeyLogical;
        private int lastKeyAction;

        public bool IsSupported => true;

        public int TotalCalls => Volatile.Read(ref totalCalls);
        public int MouseMoveCalls => Volatile.Read(ref mouseMoveCalls);
        public int MouseButtonCalls => Volatile.Read(ref mouseButtonCalls);
        public int WheelCalls => Volatile.Read(ref wheelCalls);
        public int KeyCalls => Volatile.Read(ref keyCalls);
        public int LastMouseMoveX => Volatile.Read(ref lastMouseMoveX);
        public int LastMouseMoveY => Volatile.Read(ref lastMouseMoveY);
        public int LastWheelDeltaX => Volatile.Read(ref lastWheelDeltaX);
        public int LastWheelDeltaY => Volatile.Read(ref lastWheelDeltaY);
        public RemoteMouseButton LastMouseButton => (RemoteMouseButton)Volatile.Read(ref lastMouseButton);
        public RemoteButtonAction LastMouseButtonAction => (RemoteButtonAction)Volatile.Read(ref lastMouseButtonAction);
        public string? LastKeyLogical => Volatile.Read(ref lastKeyLogical);
        public RemoteKeyAction LastKeyAction => (RemoteKeyAction)Volatile.Read(ref lastKeyAction);

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
            Volatile.Write(ref lastKeyLogical, key.LogicalKey);
            Volatile.Write(ref lastKeyAction, (int)action);
            Interlocked.Increment(ref keyCalls);
            Interlocked.Increment(ref totalCalls);
        }
    }

    private protected sealed class FixedRemoteCoordinateMapper : IRemoteCoordinateMapper
    {
        public bool IsMappingAvailable => true;

        public (int xPx, int yPx) MapNormalizedToVirtualDesktop(double nx, double ny)
        {
            return (100, 200);
        }
    }

#pragma warning disable CS0067
    private protected sealed class TestRemoteControlTransport :
        ISignalingTransport,
        IAddressTargetSignalingTransport,
        IAddressHostSignalingTransport,
        ISessionSecuritySignalingTransport,
        IRemoteControlCapabilityProvider,
        IRemoteControlSignalingTransport
    {
        private readonly object gate = new();
        private readonly List<ControlStopMessageV1> sentControlStops = new();
        private readonly List<ControlInputMessageV1> sentControlInputs = new();
        private readonly List<ControlDisplayInfoMessageV1> sentControlDisplayInfos = new();
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;
        private long nextIncomingControlInputSeq;

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

        public int SentControlInputCount
        {
            get
            {
                lock (gate)
                {
                    return sentControlInputs.Count;
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

        public ControlInputMessageV1? GetLastSentControlInput()
        {
            lock (gate)
            {
                return sentControlInputs.Count == 0 ? null : sentControlInputs[^1];
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

        public Task SendControlInputAsync(ControlInputMessageV1 message, CancellationToken ct)
        {
            lock (gate)
            {
                sentControlInputs.Add(message);
            }

            return Task.CompletedTask;
        }

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
            if (message.Seq <= 0 || message.TsUtcMs.GetValueOrDefault() <= 0)
            {
                message = message with
                {
                    Seq = message.Seq > 0 ? message.Seq : Interlocked.Increment(ref nextIncomingControlInputSeq),
                    TsUtcMs = message.TsUtcMs.GetValueOrDefault() > 0
                        ? message.TsUtcMs
                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
            }

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
