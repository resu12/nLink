namespace NLink.Core.FileTransfer;

internal static class RuntimeUnlockRetryDispatchTelemetry
{
    internal static string FormatAuthorityTuple(
        RuntimeUnlockRetryDispatchAuthority authority)
    {
        var owner = authority.RuntimeOwner;
        var publication = authority.SourcePublicationAuthority;
        return $"authority_schema={FileTransferAuthoritySchema.Current}; authority_id={SanitizeToken(authority.AuthorityId.ToString())}; authority_generation={authority.AuthorityGeneration}; predecessor_authority_id={SanitizeToken(authority.PredecessorAuthorityId?.ToString() ?? "(none)")}; direction={authority.Direction.ToString().ToLowerInvariant()}; session_id={SanitizeToken(authority.SessionId)}; transfer_id={SanitizeToken(authority.TransferId)}; route={SanitizeToken(authority.RouteToken)}; route_kind={SanitizeToken(authority.Route.ToString())}; protocol_version={authority.ProtocolVersion}; live_route_epoch={authority.LiveRouteEpoch}; runtime_generation={authority.RuntimeGeneration}; terminal_generation={authority.TerminalGeneration}; leg_generation={authority.LegGeneration}; transport_epoch={authority.TransportEpoch}; bridge_recovery_generation={authority.BridgeRecoveryGeneration}; bridge_decision_generation={authority.BridgeDecisionGeneration}; route_publication_generation={authority.RoutePublicationGeneration}; route_commit_generation={authority.RouteCommitGeneration ?? 0}; contract_generation={authority.RecoveryContractGeneration}; retired_generation={authority.RetiredOfferGeneration}; dispatch_attempt_generation={authority.DispatchAttemptGeneration}; missing_dispatch_successor_generation={authority.MissingDispatchSuccessorGeneration}; missing_dispatch_successor_limit={FileTransferCoordinator.RuntimeUnlockRetryMissingDispatchSuccessorLimit}; listener_rearm_requirement={SanitizeToken(authority.ListenerRearmRequirement.ToString())}; retry_reason={SanitizeToken(authority.RetryReason)}; recovery_reason={SanitizeToken(authority.RecoveryReason)}; release_kind={SanitizeToken(authority.ReleaseKind.ToString())}; release_authority_id={SanitizeToken(authority.ReleaseAuthorityId?.ToString() ?? "(none)")}; release_generation={authority.ReleaseGeneration}; release_outcome={SanitizeToken(authority.ReleaseOutcome)}; target_route={SanitizeToken(FileTransferRouteResolver.Resolve(authority.TargetRoute).TelemetryToken)}; target_route_kind={SanitizeToken(authority.TargetRoute.ToString())}; target_protocol_version={authority.TargetProtocolVersion}; target_transport={FormatTransport(authority.TargetTransport)}; runtime_owner_session_id={SanitizeToken(owner.SessionId)}; runtime_owner_transfer_id={SanitizeToken(owner.TransferId)}; runtime_owner_direction={owner.Direction.ToString().ToLowerInvariant()}; runtime_owner_route={SanitizeToken(owner.RouteToken)}; runtime_owner_route_kind={SanitizeToken(owner.Route.ToString())}; runtime_owner_protocol_version={owner.ProtocolVersion}; runtime_owner_live_route_epoch={owner.LiveRouteEpoch}; runtime_owner_runtime_generation={owner.RuntimeGeneration}; runtime_owner_terminal_generation={owner.TerminalGeneration}; runtime_owner_family={SanitizeToken(owner.RuntimeFamily)}; runtime_owner_created_reason={SanitizeToken(owner.CreatedReason)}; source_publication_session_id={SanitizeToken(publication.SessionId)}; source_publication_transfer_id={SanitizeToken(publication.TransferId)}; source_publication_direction={publication.Direction.ToString().ToLowerInvariant()}; source_publication_route={SanitizeToken(publication.RouteToken)}; source_publication_route_kind={SanitizeToken(publication.Route.ToString())}; source_publication_protocol_version={publication.ProtocolVersion}; source_publication_live_route_epoch={publication.LiveRouteEpoch}; source_publication_runtime_generation={publication.RuntimeGeneration}; source_publication_terminal_generation={publication.TerminalGeneration}; source_publication_leg_generation={publication.LegGeneration}; source_publication_route_commit_generation={publication.RouteCommitGeneration ?? 0}; source_publication_target_route={SanitizeToken(FileTransferRouteResolver.Resolve(publication.TargetRoute).TelemetryToken)}; source_publication_target_route_kind={SanitizeToken(publication.TargetRoute.ToString())}; source_publication_target_protocol_version={publication.TargetProtocolVersion}; source_publication_target_transport={FormatTransport(publication.TargetTransport)}; source_publication_generation={publication.PublicationGeneration}; source_publication_action_kind={SanitizeToken(publication.ActionKind.ToString())}; source_publication_reason={SanitizeToken(publication.Reason)}; source_publication_runtime_owner_session_id={SanitizeToken(publication.RuntimeOwner.SessionId)}; source_publication_runtime_owner_transfer_id={SanitizeToken(publication.RuntimeOwner.TransferId)}; source_publication_runtime_owner_direction={publication.RuntimeOwner.Direction.ToString().ToLowerInvariant()}; source_publication_runtime_owner_route={SanitizeToken(publication.RuntimeOwner.RouteToken)}; source_publication_runtime_owner_route_kind={SanitizeToken(publication.RuntimeOwner.Route.ToString())}; source_publication_runtime_owner_protocol_version={publication.RuntimeOwner.ProtocolVersion}; source_publication_runtime_owner_live_route_epoch={publication.RuntimeOwner.LiveRouteEpoch}; source_publication_runtime_owner_runtime_generation={publication.RuntimeOwner.RuntimeGeneration}; source_publication_runtime_owner_terminal_generation={publication.RuntimeOwner.TerminalGeneration}; source_publication_runtime_owner_family={SanitizeToken(publication.RuntimeOwner.RuntimeFamily)}; source_publication_runtime_owner_created_reason={SanitizeToken(publication.RuntimeOwner.CreatedReason)}";
    }

    private static string FormatTransport(FileTransferTransportKind transport)
        => transport switch
        {
            FileTransferTransportKind.RegularNkn => "regular_nkn",
            FileTransferTransportKind.Tuna => "tuna",
            _ => "unknown",
        };

    internal static string SanitizeToken(string? value)
    {
        var safe = string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();
        if (safe.Length > 160)
        {
            safe = safe[..160];
        }

        return safe
            .Replace(";", ",", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }
}
