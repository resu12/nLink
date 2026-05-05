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
    public AccelerationStateChangedEventArgs(bool isAvailable, string reason)
    {
        IsAvailable = isAvailable;
        Reason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
    }

    public bool IsAvailable { get; }

    public string Reason { get; }
}

internal interface INknAccelerationLane : IDisposable
{
    bool IsAvailable { get; }

    NknAccelerationLaneDiagnostics GetDiagnosticsSnapshot();

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
    long SequenceReordered)
{
    public static NknAccelerationLaneDiagnostics Empty { get; } = new(false, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

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

    Task<bool> EnsureListenerSidecarConnectedAsync(string expectedRemotePeer, CancellationToken ct);

    Task<bool> StartDialerSidecarAsync(string tunaAddress, string expectedRemotePeer, CancellationToken ct);

    Task StopAsync(string reason, CancellationToken ct);
}

internal sealed record NknTunaListenerStartRequest(
    string ExpectedRemotePeer,
    NknAccelerationLaneKind Lanes);

internal sealed record NknTunaListenerSidecarEndpoint(
    string LocalIpc,
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
    decimal? CumulativeSpendNkn);

internal interface INknTunaUsageTelemetrySink
{
    void RecordPayment(NknTunaPaymentTelemetry payment);

    void RecordSummary(NknTunaSessionUsageTelemetry summary);
}

internal interface INknTunaListenerSidecarSupervisor : IDisposable
{
    Task<NknTunaListenerSidecarEndpoint?> EnsureStartedAsync(NknTunaListenerStartRequest request, CancellationToken ct);

    void Stop(string reason);
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
