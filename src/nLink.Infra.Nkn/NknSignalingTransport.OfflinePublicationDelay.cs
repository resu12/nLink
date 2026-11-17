using System.Collections.Immutable;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

// Scheduling facts only. No payload, mutable context, validator or authority token.
internal sealed record OfflinePublicationEnvelope(string MessageId, int Length, string Sha256);
internal sealed record OfflinePublicationMetadata(FileTransferStateFrameV4 State,
    string Source, string ControlDestination, string? BulkDestination,
    OfflinePublicationEnvelope Control, OfflinePublicationEnvelope? Bulk,
    FileTransferDirection? Direction, string? Route, int Protocol, long TransportEpoch,
    long LiveRouteEpoch, long LegGeneration, long BridgeRecoveryGeneration,
    bool PeerVisibleRequired, bool IgnoreCallerCancellation, int PeerCopyAttempts, TimeSpan CopyTimeout);

internal interface IOfflinePublicationDelay
{
    CancellationToken Lifetime { get; }
    Task BeforeCopyAsync(string role, long? timeoutStarted, CancellationToken ct);
    // Nonblocking observation of the original result, including validation failure.
    void CopyFinished(string role, bool succeeded);
}

internal sealed record OfflinePublicationDelayRegistration(string SessionId, string TransferId,
    Func<OfflinePublicationMetadata, IOfflinePublicationDelay?> Bind, Action<string> Fail);

public sealed partial class NknSignalingTransport
{
    // Instance-scoped and absent in ordinary operation. Registration must not block.
    internal OfflinePublicationDelayRegistration? OfflinePublicationDelayForTests { get; set; }

    private IOfflinePublicationDelay? BindOfflinePublicationDelay(
        FileTransferControlPlaneDeliveryRequest request, Envelope control, Envelope? bulk)
    {
        var registration = OfflinePublicationDelayForTests;
        if (registration is null || request.Kind != FileTransferControlPlaneKind.ReceiverState ||
            registration.SessionId != request.Frame.SessionId || registration.TransferId != request.Frame.TransferId) return null;
        try
        {
            if (request.Frame is not FileTransferStateFrameV4 state || state.MissingRanges.Count > 8 ||
                (state.GetType() != typeof(FileTransferStateFrameV4) && state.GetType() != typeof(FileTransferReceiverStateFrameV6)))
                throw new InvalidOperationException("offline_publication_state_unavailable");
            var snapshot = state with { MissingRanges = state.MissingRanges.Select(r => new FileTransferRangeV4
                { StartChunkIndex = r.StartChunkIndex, ChunkCount = r.ChunkCount }).ToImmutableArray() };
            return registration.Bind(new(snapshot, client.Address, remoteEndpoint!, remoteBulkEndpoint,
                Describe(control), bulk is null ? null : Describe(bulk), request.Direction, request.RouteToken,
                request.ProtocolVersion, request.TransportEpoch, request.LiveRouteEpoch,
                request.TransferLegGeneration, request.BridgeRecoveryGeneration, request.PeerVisibleRequired,
                request.IgnoreCallerCancellation, request.PeerCopyAttempts,
                request.CopyTimeout is { } timeout && timeout > TimeSpan.Zero ? timeout : ControlPlaneLifecycleCopyTimeout));
        }
        catch { registration.Fail("offline_publication_metadata_or_binding_failed"); throw; }

        static OfflinePublicationEnvelope Describe(Envelope envelope)
        {
            var bytes = EnvelopeCodec.Serialize(envelope);
            if (bytes.Length > 16384) throw new InvalidOperationException("offline_publication_envelope_capacity");
            return new(envelope.MessageId, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }
}
