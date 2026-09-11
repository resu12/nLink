namespace NLink.Core.FileTransfer;

internal sealed record FileTransferBootstrapAgreement(
    string CoordinatorInstanceId,
    FileTransferDirection Direction,
    FileTransferOfferV2 Offer,
    FileTransferCoordinatorStateBoundarySnapshot SourceBoundary,
    FileTransferAcceptV1? Accept = null,
    bool ReceiverSessionOpenValidated = false)
{
    internal bool IsEnabled => Accept?.FallbackBootstrapCorrelationVersion == FileTransferBootstrapCorrelation.Version &&
        (Direction == FileTransferDirection.Outbound || ReceiverSessionOpenValidated);
}

internal sealed record FileTransferBootstrapAgreementDecision(
    FileTransferCoordinatorState State, bool Accepted, string Reason,
    FileTransferCoordinatorTransitionEnvelope? SnapshotEnvelope = null);

internal static partial class FileTransferCoordinator
{
    internal static FileTransferBootstrapAgreementDecision PinFallbackBootstrapOffer(
        FileTransferCoordinatorState state, FileTransferOfferV2 offer)
        => FinalizeBootstrapAgreementSnapshot(state, PinFallbackBootstrapOfferCore(state, offer),
            nameof(PinFallbackBootstrapOffer), offer);

    private static FileTransferBootstrapAgreementDecision PinFallbackBootstrapOfferCore(
        FileTransferCoordinatorState state, FileTransferOfferV2 offer)
    {
        var current = state.PersistentState.FileTransferRouteTransition.BootstrapAgreement;
        if (!IsBootstrapNegotiationScopeCurrent(state, offer.SessionId, offer.TransferId) ||
            current is null && (state.CurrentRuntimeOwner is not null || state.CurrentLiveRouteEpoch is not null) ||
            !FileTransferPayloadCodec.TryDeserializeOffer(FileTransferPayloadCodec.Serialize(offer), out var normalized) ||
            normalized != offer ||
            state.Direction == FileTransferDirection.Outbound && offer.FallbackBootstrapCorrelationVersion is not null &&
            offer.FallbackBootstrapIssuerId != CreateLocalFallbackBootstrapIssuer(state))
        {
            return new(state, false, "bootstrap_offer_invalid");
        }
        if (current is not null)
        {
            return new(state, IsBootstrapAgreementCurrent(state, current) && current.Offer == offer,
                current.Offer == offer ? "bootstrap_offer_duplicate" : "bootstrap_offer_conflict");
        }
        return SetBootstrapAgreement(state, new(state.PersistentState.CoordinatorInstanceId!, state.Direction, offer, CreateStateBoundarySnapshot(state)),
            "bootstrap_offer_pinned");
    }

    internal static FileTransferBootstrapAgreementDecision ApplyFallbackBootstrapAccept(
        FileTransferCoordinatorState state, FileTransferAcceptV1 accept)
        => FinalizeBootstrapAgreementSnapshot(state, ApplyFallbackBootstrapAcceptCore(state, accept),
            nameof(ApplyFallbackBootstrapAccept), accept);

    private static FileTransferBootstrapAgreementDecision ApplyFallbackBootstrapAcceptCore(
        FileTransferCoordinatorState state, FileTransferAcceptV1 accept)
    {
        var current = state.PersistentState.FileTransferRouteTransition.BootstrapAgreement;
        if (current is null || !IsBootstrapAgreementCurrent(state, current) ||
            !IsBootstrapNegotiationScopeCurrent(state, accept.SessionId, accept.TransferId) ||
            !FileTransferPayloadCodec.TryDeserializeAccept(FileTransferPayloadCodec.Serialize(accept), out var normalized) ||
            normalized != accept || accept.AcceptedDataProtocolVersion != current.Offer.PreferredDataProtocolVersion ||
            accept.FallbackBootstrapCorrelationVersion is not null &&
            (current.Offer.FallbackBootstrapCorrelationVersion != FileTransferBootstrapCorrelation.Version ||
             state.Direction == FileTransferDirection.Inbound && accept.FallbackBootstrapIssuerId != CreateLocalFallbackBootstrapIssuer(state)))
        {
            return new(state, false, "bootstrap_accept_invalid");
        }
        if (current.Accept is not null)
        {
            return new(state, current.Accept == accept,
                current.Accept == accept ? "bootstrap_accept_duplicate" : "bootstrap_accept_conflict");
        }
        return SetBootstrapAgreement(state, current with { Accept = accept }, "bootstrap_accept_pinned");
    }

    internal static FileTransferBootstrapAgreementDecision ApplyFallbackBootstrapSessionOpen(
        FileTransferCoordinatorState state, FileTransferSessionOpenV2 open)
        => FinalizeBootstrapAgreementSnapshot(state, ApplyFallbackBootstrapSessionOpenCore(state, open),
            nameof(ApplyFallbackBootstrapSessionOpen), open);

    private static FileTransferBootstrapAgreementDecision ApplyFallbackBootstrapSessionOpenCore(
        FileTransferCoordinatorState state, FileTransferSessionOpenV2 open)
    {
        var current = state.PersistentState.FileTransferRouteTransition.BootstrapAgreement;
        if (current?.Accept is not { } accept || !IsBootstrapAgreementCurrent(state, current) ||
            !IsBootstrapNegotiationScopeCurrent(state, open.SessionId, open.TransferId) ||
            !FileTransferPayloadCodec.TryDeserializeSessionOpen(FileTransferPayloadCodec.Serialize(open), out var normalized) ||
            normalized != open || open.SessionRole != FileTransferProtocol.SessionRoleSender ||
            open.ProtocolVersion != accept.AcceptedDataProtocolVersion ||
            open.FallbackBootstrapCorrelationVersion != accept.FallbackBootstrapCorrelationVersion ||
            open.FallbackBootstrapIssuerId != (accept.FallbackBootstrapCorrelationVersion is null
                ? null : current.Offer.FallbackBootstrapIssuerId))
        {
            return new(state, false, "bootstrap_session_open_invalid");
        }
        if (state.Direction == FileTransferDirection.Outbound || current.ReceiverSessionOpenValidated)
        {
            return new(state, true, "bootstrap_session_open_validated");
        }
        return SetBootstrapAgreement(state, current with { ReceiverSessionOpenValidated = true },
            "bootstrap_receiver_session_open_applied");
    }

    internal static string CreateLocalFallbackBootstrapIssuer(FileTransferCoordinatorState state)
        => FileTransferBootstrapCorrelation.CreateIssuer(state.PersistentState.CoordinatorInstanceId!,
            state.SessionId, state.TransferId, state.Direction);

    private static bool IsFallbackBootstrapCorrelationSelected(FileTransferCoordinatorState state)
        => state.PersistentState.FileTransferRouteTransition.BootstrapAgreement?.Accept?.FallbackBootstrapCorrelationVersion ==
           FileTransferBootstrapCorrelation.Version;

    private static bool IsBootstrapNegotiationScopeCurrent(FileTransferCoordinatorState state, string sessionId, string transferId)
        => !state.IsTerminal && !string.IsNullOrWhiteSpace(state.PersistentState.CoordinatorInstanceId) &&
           state.Direction is FileTransferDirection.Outbound or FileTransferDirection.Inbound &&
           state.SessionId == sessionId && state.TransferId == transferId;

    private static bool IsBootstrapAgreementCurrent(FileTransferCoordinatorState state, FileTransferBootstrapAgreement agreement)
        => agreement.CoordinatorInstanceId == state.PersistentState.CoordinatorInstanceId && agreement.Direction == state.Direction &&
           IsBootstrapNegotiationScopeCurrent(state, agreement.Offer.SessionId, agreement.Offer.TransferId);

    private static FileTransferBootstrapAgreementDecision SetBootstrapAgreement(
        FileTransferCoordinatorState state, FileTransferBootstrapAgreement agreement, string reason)
        => new(state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferRouteTransition = state.PersistentState.FileTransferRouteTransition with
                {
                    BootstrapAgreement = agreement,
                },
            },
        }, true, reason);
}
