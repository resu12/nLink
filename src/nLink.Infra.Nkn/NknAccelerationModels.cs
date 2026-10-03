using System.Text.Json.Serialization;

namespace NLink.Infra.Nkn;

[Flags]
internal enum NknAccelerationLaneKind
{
    None = 0,
    Screen = 1 << 0,
    File = 1 << 1,
}

internal sealed class AccelerationStateChangedEventArgs : EventArgs
{
    public AccelerationStateChangedEventArgs(
        bool isAvailable,
        string reason,
        NknAccelerationFailureOrigin? failureOrigin = null,
        string? sessionPreparationExecutionIdentity = null,
        NknTunaListenerAdoptionSnapshot? listenerFailureSource = null)
    {
        IsAvailable = isAvailable;
        Reason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
        FailureOrigin = failureOrigin;
        SessionPreparationExecutionIdentity = sessionPreparationExecutionIdentity;
        ListenerFailureSource = listenerFailureSource;
    }

    public bool IsAvailable { get; }

    public string Reason { get; }

    public NknAccelerationFailureOrigin? FailureOrigin { get; }

    public string? SessionPreparationExecutionIdentity { get; }

    public NknTunaListenerAdoptionSnapshot? ListenerFailureSource { get; }
}

internal readonly record struct NknAccelerationFailureOrigin(
    Guid LaneInstanceId,
    long ClientGeneration,
    string ProcessRunId,
    long FailureGeneration)
{
    public bool IsDefault =>
        LaneInstanceId == Guid.Empty || ClientGeneration <= 0 ||
        string.IsNullOrWhiteSpace(ProcessRunId) || FailureGeneration <= 0;
}

internal interface INknAccelerationLane : IDisposable
{
    bool IsAvailable { get; }

    NknAccelerationLaneDiagnostics GetDiagnosticsSnapshot();

    bool IsFailureOriginCurrent(NknAccelerationFailureOrigin origin) => false;

    bool TryApplyFailureOrigin(NknAccelerationFailureOrigin origin, Func<bool> apply) => false;

    Task<bool> TrySendAsync(NknBridgeChannel lane, byte[] envelopeBytes, CancellationToken ct);

    event EventHandler<NknIncomingMessage>? MessageReceived;

    event EventHandler<AccelerationStateChangedEventArgs>? StateChanged;
}

internal readonly record struct NknAccelerationLaneDiagnostics(
    bool IsAvailable,
    string LastUnavailableReason,
    long ControlFramesAccepted,
    long MediaFramesAccepted,
    long BulkFramesAccepted,
    long ControlFramesWritten,
    long MediaFramesWritten,
    long BulkFramesWritten,
    long ControlFramesReceived,
    long MediaFramesReceived,
    long BulkFramesReceived,
    long SendRejected,
    long QueueOverflow,
    long SequenceGap,
    long SequenceReordered,
    string TerminalSidecarReason,
    long FallbackEpoch)
{
    public static NknAccelerationLaneDiagnostics Empty { get; } = new(false, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, string.Empty, 0);

    public long AcceptedFor(NknBridgeChannel channel)
        => channel switch
        {
            NknBridgeChannel.Media => MediaFramesAccepted,
            NknBridgeChannel.Bulk => BulkFramesAccepted,
            _ => ControlFramesAccepted,
        };

    public long WrittenFor(NknBridgeChannel channel)
        => channel switch
        {
            NknBridgeChannel.Media => MediaFramesWritten,
            NknBridgeChannel.Bulk => BulkFramesWritten,
            _ => ControlFramesWritten,
        };

    public long ReceivedFor(NknBridgeChannel channel)
        => channel switch
        {
            NknBridgeChannel.Media => MediaFramesReceived,
            NknBridgeChannel.Bulk => BulkFramesReceived,
            _ => ControlFramesReceived,
        };
}

internal interface INknTunaAccelerationSession : INknAccelerationLane
{
    bool CanOfferListener { get; }

    NknAccelerationLaneKind ConfiguredLanes { get; }

    NknAccelerationLaneKind SupportedLanes { get; }

    string? LocalTunaAddress { get; }

    bool IsLocalPaidListenerActive { get; }

    Task<bool> EnsureListenerSidecarConnectedAsync(string expectedRemotePeer, CancellationToken ct);

    Task<bool> EnsureListenerSidecarConnectedAsync(
        NknTunaListenerStartRequest request,
        CancellationToken ct)
        => EnsureListenerSidecarConnectedAsync(request.ExpectedRemotePeer, ct);

    NknTunaListenerAdoptionSnapshot? CaptureCurrentListenerAdoptionSnapshot()
        => null;

    bool TryAdoptCurrentListener(
        NknTunaListenerAdoptionSnapshot snapshot,
        NknTunaListenerStartRequest successorRequest,
        out NknTunaListenerAdoptionReceipt receipt)
    {
        receipt = default!;
        return false;
    }

    Task<bool> StartDialerSidecarAsync(string tunaAddress, string expectedRemotePeer, CancellationToken ct);

    Task StopAsync(string reason, CancellationToken ct);

    // Physical ownership only; neither receipt nor cleanup grants file authority.
    NknTunaLaneLease? CaptureCurrentLaneLease() => null;

    bool TryStopCurrentLaneLease(NknTunaLaneLease lease, string reason) => false;

    Task<NknTunaLaneLease?> StartPreparationLaneAsync(NknTunaLanePreparationRequest request, CancellationToken ct)
        => Task.FromResult<NknTunaLaneLease?>(null);

    bool TryRetainCurrentLaneLease(NknTunaLaneLease lease, out NknTunaLaneLease? retained)
    {
        retained = null;
        return false;
    }

    bool TryTransferPreparationLaneLease(NknTunaLaneLease lease, string identity, bool activate,
        out NknTunaLaneLease? successor)
    {
        successor = null;
        return false;
    }
}

internal sealed record NknTunaLaneLease(
    Guid LaneInstanceId, long StartGeneration, long ClientGeneration,
    NknTunaSidecarClient Client, NknTunaSidecarProcessOwner? DialerOwner,
    NknTunaListenerAdoptionSnapshot? Listener);

// A physical-resource request, not Core authorization. Callers must hold the
// current preparation permit and revoke the supplied token on its retirement.
internal sealed record NknTunaLanePreparationRequest(
    string ExecutionIdentity, string ExpectedRemotePeer, bool LocalIsPayer, string? RemoteTunaAddress = null,
    NknTunaListenerLifetimePolicy ListenerLifetimePolicy = NknTunaListenerLifetimePolicy.SessionPreparation);

internal enum NknTunaListenerLifetimePolicy
{
    GenericBounded = 0,
    CoordinatorScopedInitialActivation = 1,
    SessionPreparation = 2,
    CoordinatorAdmittedTransfer = 3,
}

internal sealed record NknTunaListenerStartRequest(
    string ExpectedRemotePeer,
    NknAccelerationLaneKind Lanes,
    NknTunaListenerLifetimePolicy LifetimePolicy =
        NknTunaListenerLifetimePolicy.GenericBounded,
    string ExecutionIdentity = "", bool PreparationOnly = false);

internal sealed record NknTunaListenerSidecarEndpoint(
    string LocalIpc,
    string TunaAddress);

internal sealed record NknTunaListenerAdoptionSnapshot(
    NknTunaListenerStartRequest CurrentRequest,
    NknTunaListenerSidecarEndpoint Endpoint,
    int ProcessId,
    string ProcessRunId,
    DateTimeOffset ProcessStartedUtc,
    long OwnershipGeneration = 0);

internal sealed record NknTunaListenerAdoptionReceipt(
    NknTunaListenerAdoptionSnapshot Previous,
    NknTunaListenerStartRequest SuccessorRequest,
    string TunaAddress);

internal sealed record NknTunaPaymentTelemetry(
    decimal AmountNkn,
    decimal CumulativeSpendNkn,
    long BytesMoved,
    decimal? NknPerMb);

internal sealed record NknTunaSessionUsageTelemetry(
    long BytesMoved,
    string Reason,
    bool PaymentTelemetryObserved,
    decimal? CumulativeSpendNkn,
    int PaymentEventCount = 0,
    string PaymentStatus = "",
    bool CapReached = false,
    string CapReason = "",
    string FallbackReason = "",
    decimal? NknPerMb = null);

internal interface INknTunaUsageTelemetrySink
{
    void RecordPayment(NknTunaPaymentTelemetry payment);

    void RecordSummary(NknTunaSessionUsageTelemetry summary);

    void RecordIncomplete(string reason);
}

internal interface INknTunaListenerSidecarSupervisor : IDisposable
{
    bool CanOfferListener { get; }

    Task<NknTunaListenerSidecarEndpoint?> EnsureStartedAsync(NknTunaListenerStartRequest request, CancellationToken ct);

    Task<NknTunaListenerAdoptionSnapshot?> EnsureStartedIfIdleAsync(NknTunaListenerStartRequest request, CancellationToken ct)
        => Task.FromResult<NknTunaListenerAdoptionSnapshot?>(null);

    NknTunaListenerAdoptionSnapshot? CaptureCurrentListenerAdoptionSnapshot();

    bool TryAdoptCurrentListener(
        NknTunaListenerAdoptionSnapshot snapshot,
        NknTunaListenerStartRequest successorRequest);

    void Stop(string reason);

    // Atomically detach an exact owner; execute its cleanup outside lane locks.
    bool TryDetachCurrentListener(NknTunaListenerAdoptionSnapshot snapshot, out Action<string>? stop)
    {
        stop = null;
        return false;
    }

    event EventHandler<AccelerationStateChangedEventArgs>? StateChanged;
}

internal sealed record NknAccelerationLocalEndpoint(
    string TunaAddress,
    NknAccelerationLaneKind Lanes,
    int ProtocolVersion);

internal sealed class TransportAccelerationOfferPayload
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("senderRole")]
    public string SenderRole { get; set; } = string.Empty;

    [JsonPropertyName("tunaAddress")]
    public string TunaAddress { get; set; } = string.Empty;

    [JsonPropertyName("supportedLanes")]
    public string[] SupportedLanes { get; set; } = [];

    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = string.Empty;

    [JsonPropertyName("payerDecisionId")]
    public long PayerDecisionId { get; set; }

    [JsonPropertyName("sentAtUnixMs")]
    public long SentAtUnixMs { get; set; }

    [JsonPropertyName("expiresAtUnixMs")]
    public long ExpiresAtUnixMs { get; set; }

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyName("sidecarProtocolVersion")]
    public int SidecarProtocolVersion { get; set; }
}

internal sealed class TransportAccelerationAnswerPayload
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("supportedLanes")]
    public string[] SupportedLanes { get; set; } = [];

    [JsonPropertyName("expiresAtUnixMs")]
    public long ExpiresAtUnixMs { get; set; }

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyName("sidecarProtocolVersion")]
    public int SidecarProtocolVersion { get; set; }

    [JsonPropertyName("rejectReason")]
    public string? RejectReason { get; set; }

    [JsonPropertyName("payerDecisionId")]
    public long PayerDecisionId { get; set; }
}

internal sealed class TransportAccelerationAnswerAckPayload
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("supportedLanes")]
    public string[] SupportedLanes { get; set; } = [];

    [JsonPropertyName("sentAtUnixMs")]
    public long SentAtUnixMs { get; set; }

    [JsonPropertyName("expiresAtUnixMs")]
    public long ExpiresAtUnixMs { get; set; }

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyName("sidecarProtocolVersion")]
    public int SidecarProtocolVersion { get; set; }

    [JsonPropertyName("payerDecisionId")]
    public long PayerDecisionId { get; set; }
}

internal sealed class TransportAccelerationDownPayload
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("supportedLanes")]
    public string[] SupportedLanes { get; set; } = [];

    [JsonPropertyName("sentAtUnixMs")]
    public long SentAtUnixMs { get; set; }

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyName("sidecarProtocolVersion")]
    public int SidecarProtocolVersion { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("payerDecisionId")]
    public long PayerDecisionId { get; set; }
}

internal sealed class TransportAccelerationPayerIntentPayload
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("senderRole")]
    public string SenderRole { get; set; } = string.Empty;

    [JsonPropertyName("intent")]
    public string Intent { get; set; } = string.Empty;

    [JsonPropertyName("supportedLanes")]
    public string[] SupportedLanes { get; set; } = [];

    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = string.Empty;

    [JsonPropertyName("payerDecisionId")]
    public long PayerDecisionId { get; set; }

    [JsonPropertyName("sentAtUnixMs")]
    public long SentAtUnixMs { get; set; }

    [JsonPropertyName("expiresAtUnixMs")]
    public long ExpiresAtUnixMs { get; set; }

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyName("sidecarProtocolVersion")]
    public int SidecarProtocolVersion { get; set; }
}

internal static class NknAccelerationLaneCodec
{
    public static string[] ToNames(NknAccelerationLaneKind lanes)
    {
        var values = new List<string>(2);
        if ((lanes & NknAccelerationLaneKind.File) == NknAccelerationLaneKind.File)
        {
            values.Add("file");
        }

        if ((lanes & NknAccelerationLaneKind.Screen) == NknAccelerationLaneKind.Screen)
        {
            values.Add("screen");
        }

        return values.ToArray();
    }

    public static NknAccelerationLaneKind FromNames(IEnumerable<string>? values)
    {
        var lanes = NknAccelerationLaneKind.None;
        if (values is null)
        {
            return lanes;
        }

        foreach (var value in values)
        {
            if (string.Equals(value?.Trim(), "file", StringComparison.OrdinalIgnoreCase))
            {
                lanes |= NknAccelerationLaneKind.File;
            }
            else if (string.Equals(value?.Trim(), "screen", StringComparison.OrdinalIgnoreCase))
            {
                lanes |= NknAccelerationLaneKind.Screen;
            }
        }

        return lanes;
    }

    public static NknTunaSidecarLane ToSidecarLane(NknBridgeChannel lane)
        => lane switch
        {
            NknBridgeChannel.Media => NknTunaSidecarLane.Media,
            NknBridgeChannel.Bulk => NknTunaSidecarLane.Bulk,
            _ => NknTunaSidecarLane.Control,
        };

    public static NknBridgeChannel FromSidecarLane(NknTunaSidecarLane lane)
        => lane switch
        {
            NknTunaSidecarLane.Media => NknBridgeChannel.Media,
            NknTunaSidecarLane.Bulk => NknBridgeChannel.Bulk,
            _ => NknBridgeChannel.Control,
        };
}
