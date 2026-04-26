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

public abstract class ScreenShareTransportBoundaryTestBase
{
internal static SessionSecurityState CreateApprovedSecurityState(
        PeerAddress helpeeAddress,
        PeerAddress helperAddress,
        CapabilityGrant capabilities)
    {
        var sessionId = new SessionId($"screenshare_boundary_{Guid.NewGuid():N}");
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = true,
        }).WithHandshakeVerified(helperAddress)
          .WithApproval(new SessionGrant(helperAddress, capabilities, sessionId, DateTimeOffset.UtcNow.Add(SessionSecurityDefaults.GrantLifetime)));
    }

internal static byte[] CreateFramePayload(string sessionId)
    {
        return ScreenShareVideoPayloadCodec.SerializeFragment(
            new ScreenShareVideoFragmentV1
            {
                Type = ScreenShareVideoPayloadCodec.ScreenShareVideoFragmentTypeV1,
                SessionId = sessionId,
                StreamEpoch = 1,
                FrameId = 1,
                Width = 1,
                Height = 1,
                CapturedTsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Encoding = "h264",
                IsKeyFrame = true,
                FragmentIndex = 0,
                FragmentCount = 1,
                Data = new byte[] { 0x01 },
            });
    }

internal static void ConfigureNknTransportForScreenShareControlTests(
        NknSignalingTransport transport,
        SessionSecurityState securityState,
        byte[] controlKey)
    {
        SetPrivateField(transport, "currentSessionSecurityState", securityState);
        SetPrivateField(transport, "remoteEndpoint", securityState.HelperAddress!.Value.Value);
        SetPrivateField(transport, "currentEnvelopeCode", "screenshare-recovery-receipt-envelope");
        SetPrivateField(transport, "controlSessionSharedKey", controlKey);
    }

internal static byte[] CreateControlSharedKey()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(i + 1);
        }

        return key;
    }

internal static Envelope BuildSecureScreenShareRecoveryReceiptEnvelope(
        NknSignalingTransport senderTransport,
        SessionSecurityState securityState,
        byte[] controlKey,
        ScreenShareRecoveryReceiptV1 message,
        long sequence)
    {
        return BuildSecureScreenShareRecoveryReceiptEnvelope(
            senderTransport,
            securityState,
            controlKey,
            ScreenShareRecoveryReceiptCodec.Serialize(message),
            sequence);
    }

internal static Envelope BuildSecureScreenShareRecoveryReceiptEnvelope(
        NknSignalingTransport senderTransport,
        SessionSecurityState securityState,
        byte[] controlKey,
        byte[] plaintext,
        long sequence)
    {
        var envelopeCode = Assert.IsType<string>(GetPrivateField(senderTransport, "currentEnvelopeCode"));
        var senderIdentity = securityState.HelperAddress ?? new PeerAddress("receipt.helper");
        var securePayload = SessionSecureEnvelopeCodec.Encrypt(
            controlKey,
            new SessionSecureEnvelopeMetadata(
                Family: SessionSecureMessageFamily.RemoteControl,
                MessageType: "screenshare_recovery_receipt",
                SessionId: Assert.IsType<SessionId>(securityState.SessionId),
                SenderIdentity: senderIdentity,
                Sequence: sequence,
                RequestId: null),
            plaintext);

        return new Envelope(
            Version: 1,
            Code: envelopeCode,
            MessageId: Guid.NewGuid().ToString("N"),
            Type: MsgType.ScreenShareRecoveryReceipt,
            Payload: securePayload,
            UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyTo: null);
    }

internal static void ReportHelperRemoteFrameApplied(SessionRuntime runtime, long ageMs, long streamEpoch)
    {
        InvokePrivateMethod(runtime, "ReportHelperRemoteScreenShareFrameApplied", ageMs, streamEpoch);
    }

internal static void ReportHelperRemoteFrameApplied(SessionRuntime runtime, long ageMs, long streamEpoch, long frameId)
    {
        InvokePrivateMethod(runtime, "ReportHelperRemoteScreenShareFrameApplied", ageMs, streamEpoch, frameId);
    }

internal static void ReportHelperRemoteFrameApplied(
        SessionRuntime runtime,
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap)
    {
        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareFrameApplied",
            ageMs,
            streamEpoch,
            frameId,
            visibleHeadFrameId,
            stableVisibleHeadFrameId,
            framesAppliedSinceLastGap);
    }

internal static void ReportHelperRemoteFrameApplied(
        SessionRuntime runtime,
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap,
        HelperRemoteSessionSnapshot sessionSnapshot)
    {
        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareFrameApplied",
            ageMs,
            streamEpoch,
            frameId,
            visibleHeadFrameId,
            stableVisibleHeadFrameId,
            framesAppliedSinceLastGap,
            sessionSnapshot);
    }

internal static HelperRemoteSessionSnapshot CreateHelperSessionSnapshot(
        long currentEpoch,
        long visibleHeadFrameId,
        long appliedHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap,
        long visibleRecoveryFloorFrameId = -1,
        HelperRemoteSessionPhase phase = HelperRemoteSessionPhase.VisibleStable,
        HelperRemoteRecoveryMechanism recoveryMechanism = HelperRemoteRecoveryMechanism.None)
    {
        var provenHeadFrameId = Math.Max(
            Math.Max(visibleHeadFrameId, appliedHeadFrameId),
            Math.Max(stableVisibleHeadFrameId, visibleRecoveryFloorFrameId));
        return new HelperRemoteSessionSnapshot(
            CurrentEpoch: currentEpoch,
            Phase: phase,
            RecoveryMechanism: recoveryMechanism,
            BaselineEstablished: provenHeadFrameId >= 0,
            SteadyVisibleProgressActive:
                phase == HelperRemoteSessionPhase.VisibleStable &&
                recoveryMechanism == HelperRemoteRecoveryMechanism.None &&
                provenHeadFrameId >= 0,
            VisibleHeadFrameId: visibleHeadFrameId,
            AppliedHeadFrameId: appliedHeadFrameId,
            StableVisibleHeadFrameId: stableVisibleHeadFrameId,
            VisibleRecoveryFloorFrameId: visibleRecoveryFloorFrameId,
            ProvenHeadFrameId: provenHeadFrameId,
            FramesAppliedSinceLastGap: framesAppliedSinceLastGap,
            CurrentEpochProgressProven: provenHeadFrameId >= 0,
            CurrentEpochProgressProofSource:
                visibleRecoveryFloorFrameId >= 0 && provenHeadFrameId >= visibleRecoveryFloorFrameId
                    ? "recovery_floor_plus_head"
                    : stableVisibleHeadFrameId >= 0
                        ? "stable_visible_head"
                        : appliedHeadFrameId >= 0
                            ? "applied_head"
                            : visibleHeadFrameId >= 0
                                ? "visible_head"
                                : "none",
            RecoveryActive: recoveryMechanism == HelperRemoteRecoveryMechanism.WaitingForRecoveryKeyframe,
            RecoveryCorridorActive: recoveryMechanism == HelperRemoteRecoveryMechanism.RecoveryCorridor,
            RunwayCleanupActive: recoveryMechanism == HelperRemoteRecoveryMechanism.RunwayCleanup,
            PostRecoveryStabilizationActive: recoveryMechanism == HelperRemoteRecoveryMechanism.FollowerWindow);
    }

internal static void ReportHelperRemoteRecoveryWindowStateChanged(
        SessionRuntime runtime,
        long streamEpoch,
        long recoveryFrameId,
        long lastContiguousFrameId,
        int contiguousFollowerApplyCount,
        string status,
        string? abortReason = null)
    {
        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareRecoveryWindowStateChanged",
            streamEpoch,
            recoveryFrameId,
            lastContiguousFrameId,
            contiguousFollowerApplyCount,
            status,
            abortReason);
    }

internal static void ReportHelperRemoteStaleDrop(
    SessionRuntime runtime,
    long renderedAgeMs,
    long streamEpoch,
    bool referenceContinuityPreserved = false)
    {
        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareStaleFrameDropped",
            renderedAgeMs,
            streamEpoch,
            referenceContinuityPreserved);
    }

internal static ScreenSharePressureStateV1 WaitForSinglePressureState(List<ScreenSharePressureStateV1> sentMessages)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (sentMessages.Count == 1)
            {
                return sentMessages[0];
            }

            Thread.Sleep(25);
        }

        return Assert.Single(sentMessages);
    }

internal static ScreenShareRecoveryReceiptV1 WaitForSingleRecoveryReceipt(List<ScreenShareRecoveryReceiptV1> sentMessages)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (sentMessages.Count == 1)
            {
                return sentMessages[0];
            }

            Thread.Sleep(25);
        }

        return Assert.Single(sentMessages);
    }

internal static T[] SnapshotList<T>(List<T> messages)
    {
        lock (messages)
        {
            return messages.ToArray();
        }
    }

internal static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

internal static object GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<object>(field!.GetValue(target));
    }

internal static long GetPrivateLongField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<long>(field!.GetValue(target));
    }

internal static object GetScreenShareControlHost(SessionRuntime runtime)
    {
        return GetPrivateField(runtime, "screenShareControlHost");
    }

internal static object GetScreenShareControlHostField(SessionRuntime runtime, string fieldName)
    {
        return GetPrivateField(GetScreenShareControlHost(runtime), fieldName);
    }

internal static long GetScreenShareControlHostLongField(SessionRuntime runtime, string fieldName)
    {
        return GetPrivateLongField(GetScreenShareControlHost(runtime), fieldName);
    }

internal static string GetFileTransferFlowControlMode(SessionRuntime runtime)
    {
        var fileTransferService = GetPrivateField(runtime, "fileTransferService");
        var policyField = fileTransferService.GetType().GetField("flowControlPolicy", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(policyField);
        var policy = Assert.IsAssignableFrom<object>(policyField!.GetValue(fileTransferService));
        var modeProperty = policy.GetType().GetProperty("Mode", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(modeProperty);
        return Assert.IsAssignableFrom<object>(modeProperty!.GetValue(policy)).ToString()!;
    }

internal static void WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            Thread.Sleep(20);
        }

        Assert.True(predicate(), "Condition not met before timeout.");
    }

internal static object? InvokePrivateMethod(object target, string methodName, params object?[] args)
    {
        var methods = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(methods);
        var method = methods.FirstOrDefault(m => m.GetParameters().Length == args.Length) ?? methods[0];
        return method.Invoke(target, args);
    }

internal static object? InvokePublicMethod(object target, string methodName, params object?[] args)
    {
        var methods = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(methods);
        var method = methods.FirstOrDefault(m => m.GetParameters().Length == args.Length) ?? methods[0];
        return method.Invoke(target, args);
    }

internal static async Task InvokePrivateAsync(object target, string methodName, params object?[] args)
    {
        var task = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(target, methodName, args));
        await task.ConfigureAwait(false);
    }

#pragma warning disable CS0067

internal sealed class ScreenShareSignalingTransportDouble : ISignalingTransport, IScreenShareSignalingTransport, ISessionSecuritySignalingTransport
    {
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public List<byte[]> SentPayloads { get; } = new();
        public List<ScreenSharePressureStateV1> SentPressureStates { get; } = new();
        public List<ScreenShareRecoveryReceiptV1> SentRecoveryReceipts { get; } = new();
        public List<ScreenShareVideoStreamConfigV1> SentVideoStreamConfigs { get; } = new();
        public List<ScreenShareVideoKeyframeRequestV1> SentVideoKeyframeRequests { get; } = new();
        public List<ScreenShareCursorStateV1> SentCursorStates { get; } = new();

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
        public event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted;
        public event EventHandler? ScreenShareStopped;
        public event EventHandler<ScreenSharePressureStateReceivedEventArgs>? ScreenSharePressureStateReceived;
        public event EventHandler<ScreenShareRecoveryReceiptReceivedEventArgs>? ScreenShareRecoveryReceiptReceived;
        public event EventHandler<ScreenShareVideoStreamConfigReceivedEventArgs>? ScreenShareVideoStreamConfigReceived;
        public event EventHandler<ScreenShareVideoKeyframeRequestReceivedEventArgs>? ScreenShareVideoKeyframeRequestReceived;
        public event EventHandler<ScreenShareCursorStateReceivedEventArgs>? ScreenShareCursorStateReceived;

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

        public Task SendScreenSharePressureStateAsync(ScreenSharePressureStateV1 message, CancellationToken ct)
        {
            lock (SentPressureStates)
            {
                SentPressureStates.Add(message);
            }

            return Task.CompletedTask;
        }

        public Task SendScreenShareRecoveryReceiptAsync(ScreenShareRecoveryReceiptV1 message, CancellationToken ct)
        {
            SentRecoveryReceipts.Add(message);
            return Task.CompletedTask;
        }

        public Task SendScreenShareVideoStreamConfigAsync(ScreenShareVideoStreamConfigV1 message, CancellationToken ct)
        {
            SentVideoStreamConfigs.Add(message);
            return Task.CompletedTask;
        }

        public Task SendScreenShareVideoKeyframeRequestAsync(ScreenShareVideoKeyframeRequestV1 message, CancellationToken ct)
        {
            SentVideoKeyframeRequests.Add(message);
            return Task.CompletedTask;
        }

        public Task SendScreenShareCursorStateAsync(ScreenShareCursorStateV1 message, CancellationToken ct)
        {
            SentCursorStates.Add(message);
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

        public void RaiseScreenShareRecoveryReceiptReceived(ScreenShareRecoveryReceiptV1 message)
        {
            ScreenShareRecoveryReceiptReceived?.Invoke(this, new ScreenShareRecoveryReceiptReceivedEventArgs(message, peerId: "screenshare-double-peer"));
        }

        public void SetSessionSecurityStateForTests(SessionSecurityState nextState)
        {
            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        }
    }

internal class ScreenShareAwareSignalingTransportDouble : ISignalingTransport, IRemoteControlSignalingTransport, IScreenShareSignalingTransport, ISessionSecuritySignalingTransport, IScreenShareTransportBackpressureProbe
    {
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public List<byte[]> SentScreenSharePayloads { get; } = new();
        public List<ControlDisplayInfoMessageV1> SentDisplayInfoMessages { get; } = new();
        public List<ScreenSharePressureStateV1> SentPressureStates { get; } = new();
        public List<ScreenShareRecoveryReceiptV1> SentRecoveryReceipts { get; } = new();
        public List<ScreenShareVideoStreamConfigV1> SentVideoStreamConfigs { get; } = new();
        public List<ScreenShareVideoKeyframeRequestV1> SentVideoKeyframeRequests { get; } = new();
        public List<ScreenShareCursorStateV1> SentCursorStates { get; } = new();
        public long RecentHealthIssueCount { get; set; }
        public bool IsHealthSeverelyDegraded { get; set; }
        public bool IsCongested { get; set; }
        public bool IsSeverelyCongested { get; set; }
        public int QueueDepth { get; set; }
        public int QueuedBytes { get; set; }
        public long OldestQueuedAgeMs { get; set; }
        public long RecentDropCount { get; set; }

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
        public event EventHandler<ScreenSharePressureStateReceivedEventArgs>? ScreenSharePressureStateReceived;
        public event EventHandler<ScreenShareRecoveryReceiptReceivedEventArgs>? ScreenShareRecoveryReceiptReceived;
        public event EventHandler<ScreenShareVideoStreamConfigReceivedEventArgs>? ScreenShareVideoStreamConfigReceived;
        public event EventHandler<ScreenShareVideoKeyframeRequestReceivedEventArgs>? ScreenShareVideoKeyframeRequestReceived;
        public event EventHandler<ScreenShareCursorStateReceivedEventArgs>? ScreenShareCursorStateReceived;

        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;
        public bool IsScreenShareTransportCongested => IsCongested;
        public bool IsScreenShareTransportSeverelyCongested => IsSeverelyCongested;
        public int ScreenShareTransportQueueDepth => QueueDepth;
        public int ScreenShareTransportQueuedBytes => QueuedBytes;
        public long ScreenShareTransportOldestQueuedAgeMs => OldestQueuedAgeMs;
        public long ScreenShareTransportRecentDropCount => RecentDropCount;
        public long ScreenShareTransportRecentHealthIssueCount => RecentHealthIssueCount;
        public bool IsScreenShareTransportHealthSeverelyDegraded => IsHealthSeverelyDegraded;

        public void Dispose()
        {
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;

        public Task SendScreenSharePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            SentScreenSharePayloads.Add(payload.ToArray());
            return Task.CompletedTask;
        }

        public Task SendScreenSharePressureStateAsync(ScreenSharePressureStateV1 message, CancellationToken ct)
        {
            lock (SentPressureStates)
            {
                SentPressureStates.Add(message);
            }

            return Task.CompletedTask;
        }

        public Task SendScreenShareRecoveryReceiptAsync(ScreenShareRecoveryReceiptV1 message, CancellationToken ct)
        {
            SentRecoveryReceipts.Add(message);
            return Task.CompletedTask;
        }

        public Task SendScreenShareVideoStreamConfigAsync(ScreenShareVideoStreamConfigV1 message, CancellationToken ct)
        {
            SentVideoStreamConfigs.Add(message);
            return Task.CompletedTask;
        }

        public Task SendScreenShareVideoKeyframeRequestAsync(ScreenShareVideoKeyframeRequestV1 message, CancellationToken ct)
        {
            SentVideoKeyframeRequests.Add(message);
            return Task.CompletedTask;
        }

        public Task SendScreenShareCursorStateAsync(ScreenShareCursorStateV1 message, CancellationToken ct)
        {
            SentCursorStates.Add(message);
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

        public void RaiseScreenShareRecoveryReceiptReceived(ScreenShareRecoveryReceiptV1 message)
        {
            ScreenShareRecoveryReceiptReceived?.Invoke(this, new ScreenShareRecoveryReceiptReceivedEventArgs(message, peerId: "screenshare-aware-double-peer"));
        }

        public void SetSessionSecurityStateForTests(SessionSecurityState nextState)
        {
            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        }
    }

internal sealed class BridgePolicyCapabilityClient : INknClient, IAuthoritativeConnectedAddressSource, IBridgeScreenShareQueueCapability
    {
        public List<(BridgeScreenShareQueueMode Mode, long Generation, bool FlushQueued)> PolicyApplications { get; } = new();
        public List<(string Destination, byte[] Payload)> SentMessages { get; } = new();

        public bool IsBridgeProcessRunning { get; set; }

        public string Address => "bridge.policy.control";

        public string MediaAddress => "bridge.policy.media";

        public string BulkAddress => "bridge.policy.bulk";

        public BridgeScreenShareQueueState CurrentScreenShareQueueState =>
            new(
                QueueDepth: 0,
                QueuedBytes: 0,
                OldestQueuedAgeMs: 0,
                InFlight: false,
                DroppedSinceLast: 0,
                IsCongested: false,
                IsSevere: false,
                Mode: BridgeScreenShareQueueMode.Normal);

        public BridgeScreenShareHealthState CurrentScreenShareHealthState =>
            new(
                RecentIssueCount: 0,
                IsSevere: false,
                OldestIssueAgeMs: 0);

        bool IAuthoritativeConnectedAddressSource.HasAuthoritativeConnectedAddress => true;

        public event EventHandler<NknIncomingMessage>? MessageReceived;
        public event EventHandler? Disconnected;
        public event EventHandler<BridgeScreenShareQueueStateChangedEventArgs>? ScreenShareQueueStateChanged;

        public void Dispose()
        {
        }

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public Task DisconnectAsync() => Task.CompletedTask;

        public Task SubscribeAsync(string topic, CancellationToken ct) => Task.CompletedTask;

        public Task UnsubscribeAsync(string topic) => Task.CompletedTask;

        public Task PublishAsync(string topic, byte[] payload, CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(string destination, byte[] payload, CancellationToken ct)
        {
            SentMessages.Add((destination, payload));
            return Task.CompletedTask;
        }

        public Task SendMediaAsync(string destination, byte[] payload, CancellationToken ct) => Task.CompletedTask;

        public Task SendBulkAsync(string destination, byte[] payload, CancellationToken ct) => Task.CompletedTask;

        public Task SetScreenSharePolicyAsync(BridgeScreenShareQueueMode mode, long generation, bool flushQueued, CancellationToken ct)
        {
            PolicyApplications.Add((mode, generation, flushQueued));
            return Task.CompletedTask;
        }
    }

}


