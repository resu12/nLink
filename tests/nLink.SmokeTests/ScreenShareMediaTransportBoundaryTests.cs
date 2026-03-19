using System.Reflection;
using NLink.App.Services;
using NLink.App.Services.ScreenCapture;
using NLink.Core;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

public sealed class ScreenShareMediaTransportBoundaryTests
{
    [Trait("Category", "Smoke")]
    [Fact]
    public async Task LegacyScreenShareMediaTransportAdapter_ForwardsSendScreenSharePayloadAsync()
    {
        var legacyTransport = new LegacyScreenShareTransportDouble();
        var adapter = new LegacyScreenShareMediaTransportAdapter(legacyTransport, legacyTransport);
        var payload = new byte[] { 0x01, 0x02, 0x03 };

        await adapter.SendScreenSharePayloadAsync(payload, CancellationToken.None);

        var sent = Assert.Single(legacyTransport.SentPayloads);
        Assert.Equal(payload, sent);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void LegacyScreenShareMediaTransportAdapter_ForwardsScreenShareFrameCompleted()
    {
        var legacyTransport = new LegacyScreenShareTransportDouble();
        var adapter = new LegacyScreenShareMediaTransportAdapter(legacyTransport);
        ScreenShareFrameCompletedEventArgs? observed = null;

        adapter.ScreenShareFrameCompleted += (_, e) => observed = e;
        var expected = new ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", new byte[] { 0x01 }, SessionId: "sess_adapter_frame");

        legacyTransport.RaiseScreenShareFrameCompleted(expected);

        Assert.Equal(expected, observed);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void LegacyScreenShareMediaTransportAdapter_ForwardsScreenShareStopped()
    {
        var legacyTransport = new LegacyScreenShareTransportDouble();
        var adapter = new LegacyScreenShareMediaTransportAdapter(legacyTransport);
        var stopCount = 0;

        adapter.ScreenShareStopped += (_, _) => stopCount++;
        legacyTransport.RaiseScreenShareStopped();

        Assert.Equal(1, stopCount);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void LegacyScreenShareMediaTransportAdapter_ReportsCongestionFromBackpressureProbe()
    {
        var legacyTransport = new LegacyScreenShareTransportDouble
        {
            IsScreenShareTransportCongested = true,
        };
        var adapter = new LegacyScreenShareMediaTransportAdapter(legacyTransport, legacyTransport);

        Assert.True(adapter.IsCongested);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void LegacyScreenShareMediaTransportAdapter_DefaultsCongestionToFalseWithoutProbe()
    {
        var adapter = new LegacyScreenShareMediaTransportAdapter(new LegacyScreenShareTransportDouble());

        Assert.False(adapter.IsCongested);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_WiresScreenShareEventsThroughResolvedMediaTransport()
    {
        using var transport = new MediaOnlySignalingTransport();
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
    public async Task SessionRuntime_SendScreenShareMediaPayloadAsync_PreservesCapabilityAndSessionValidation()
    {
        using var transport = new MediaOnlySignalingTransport();
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
        using var transport = new MediaOnlySignalingTransport();
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
    public void SessionRuntime_SenderFiltering_AcceptsCurrentMediaAdapterAndRejectsStaleMediaAdapter()
    {
        using var currentTransport = new LegacyScreenShareTransportDouble();
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

        using var staleTransport = new LegacyScreenShareTransportDouble();
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
        using var transport = new MediaOnlySignalingTransport();
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

    private static SessionSecurityState CreateApprovedSecurityState(
        PeerAddress helpeeAddress,
        PeerAddress helperAddress,
        CapabilityGrant capabilities)
    {
        var sessionId = new SessionId($"media_boundary_{Guid.NewGuid():N}");
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = true,
        }).WithHandshakeVerified(helperAddress)
          .WithApproval(new SessionGrant(helperAddress, capabilities, sessionId, DateTimeOffset.UtcNow.Add(SessionSecurityDefaults.GrantLifetime)));
    }

    private static byte[] CreateFramePayload(string sessionId)
    {
        return ScreenSharePayloadCodec.Serialize(
            new ScreenShareFrameChunkV1
            {
                Type = ScreenSharePayloadCodec.ScreenShareFrameTypeV1,
                SessionId = sessionId,
                FrameId = 1,
                Width = 1,
                Height = 1,
                Encoding = "jpeg",
                ChunkIndex = 0,
                ChunkCount = 1,
                DataBase64 = Convert.ToBase64String(new byte[] { 0x01 }),
            });
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<object>(field!.GetValue(target));
    }

    private static string GetFileTransferFlowControlMode(SessionRuntime runtime)
    {
        var fileTransferService = GetPrivateField(runtime, "fileTransferService");
        var policyField = fileTransferService.GetType().GetField("flowControlPolicy", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(policyField);
        var policy = Assert.IsAssignableFrom<object>(policyField!.GetValue(fileTransferService));
        var modeProperty = policy.GetType().GetProperty("Mode", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(modeProperty);
        return Assert.IsAssignableFrom<object>(modeProperty!.GetValue(policy)).ToString()!;
    }

    private static object? InvokePrivateMethod(object target, string methodName, params object?[] args)
    {
        var methods = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(methods);
        var method = methods.FirstOrDefault(m => m.GetParameters().Length == args.Length) ?? methods[0];
        return method.Invoke(target, args);
    }

    private static async Task InvokePrivateAsync(object target, string methodName, params object?[] args)
    {
        var task = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(target, methodName, args));
        await task.ConfigureAwait(false);
    }

#pragma warning disable CS0067
    private sealed class LegacyScreenShareTransportDouble : ISignalingTransport, IScreenShareSignalingTransport, IScreenShareTransportBackpressureProbe, ISessionSecuritySignalingTransport
    {
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public List<byte[]> SentPayloads { get; } = new();

        public bool IsScreenShareTransportCongested { get; set; }

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
        public event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted;
        public event EventHandler? ScreenShareStopped;

        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public void Dispose()
        {
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;

        public Task SendScreenSharePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            SentPayloads.Add(payload.ToArray());
            return Task.CompletedTask;
        }

        public void RaiseScreenShareFrameCompleted(ScreenShareFrameCompletedEventArgs e)
        {
            ScreenShareFrameCompleted?.Invoke(this, e);
        }

        public void RaiseScreenShareStopped()
        {
            ScreenShareStopped?.Invoke(this, EventArgs.Empty);
        }

        public void SetSessionSecurityStateForTests(SessionSecurityState nextState)
        {
            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        }
    }

    private sealed class MediaOnlySignalingTransport : ISignalingTransport, IRemoteControlSignalingTransport, IScreenShareMediaTransport, IScreenShareSignalingTransport, ISessionSecuritySignalingTransport
    {
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public List<byte[]> SentScreenSharePayloads { get; } = new();
        public List<ControlDisplayInfoMessageV1> SentDisplayInfoMessages { get; } = new();

        public bool IsCongested { get; set; }

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
        public event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted;
        public event EventHandler? ScreenShareStopped;

        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public void Dispose()
        {
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;

        public Task SendScreenSharePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            SentScreenSharePayloads.Add(payload.ToArray());
            return Task.CompletedTask;
        }

        public Task SendControlRequestAsync(ControlRequestMessageV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlResponseAsync(ControlResponseMessageV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlStartAsync(ControlStartMessageV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlStopAsync(ControlStopMessageV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlInputAsync(ControlInputMessageV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlAckAsync(ControlInputAckV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlStateSnapshotAsync(ControlStateSnapshotV1 message, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlDisplayInfoAsync(ControlDisplayInfoMessageV1 message, CancellationToken ct)
        {
            SentDisplayInfoMessages.Add(message);
            return Task.CompletedTask;
        }

        public void RaiseScreenShareFrameCompleted(ScreenShareFrameCompletedEventArgs e)
        {
            ScreenShareFrameCompleted?.Invoke(this, e);
        }

        public void RaiseScreenShareStopped()
        {
            ScreenShareStopped?.Invoke(this, EventArgs.Empty);
        }

        public void SetSessionSecurityStateForTests(SessionSecurityState nextState)
        {
            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        }
    }
#pragma warning restore CS0067
}
