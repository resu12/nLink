using System.Globalization;

namespace NLink.Core.FileTransfer;

internal static partial class FileTransferCoordinator
{
    internal static string CreateFallbackBootstrapCommandId(
        string issuer,
        FileTransferFallbackV6ProofBootstrapCommand command,
        string dataSessionIdentity,
        string? checkpointRequestId,
        int legCheckpointGeneration)
    {
        if (!FileTransferBootstrapCorrelation.IsIssuerId(issuer) || command.IsDefault ||
            string.IsNullOrWhiteSpace(dataSessionIdentity))
        {
            throw new ArgumentException("Exact bootstrap command and data-session scope are required.");
        }
        var fields = new List<string>
        {
            "fallback-bootstrap-scope-v1", issuer, command.Observation.SessionId, command.Observation.TransferId,
            command.Observation.Direction == FileTransferDirection.Outbound ? "outbound" : "inbound",
            Number(command.BootstrapGeneration), command.AuthorityId.Value,
        };
        AddOwner(command.SourceRuntimeOwner);
        AddPublication(command.SourceRoutePublication);
        AddCommit(command.SourceRouteCommit);
        fields.AddRange([
            Number(command.SourceTerminalGeneration), Number(command.SourceLiveRouteEpoch), Number(command.SourceLegGeneration),
            Number(command.SourceTransportEpoch), Number(command.TargetTransportEpoch), Number(command.SourceBridgeRecoveryGeneration),
            dataSessionIdentity, Number(command.SourceDataSessionGeneration),
        ]);
        var checkpoint = command.Observation.DurableCheckpoint;
        AddOwner(checkpoint.SourceRuntimeOwner);
        AddPublication(checkpoint.SourceRoutePublication);
        fields.AddRange([
            Number(checkpoint.SourceTerminalGeneration), Number(checkpoint.SourceLegGeneration), Number(checkpoint.SourceTransportEpoch),
            Number(checkpoint.SourceBridgeRecoveryGeneration), Number(checkpoint.DataSessionGeneration), Number(checkpoint.StateEpoch),
            Number(checkpoint.ContiguousCommittedChunkIndex), Number(checkpoint.DurableHighestChunkIndex), Number(checkpoint.ChunkCount),
            Number(checkpoint.CheckpointGeneration), "leg-checkpoint", Number(legCheckpointGeneration),
            checkpointRequestId is null ? "absent" : "present", checkpointRequestId ?? string.Empty,
        ]);
        var scope = FileTransferBootstrapCorrelation.HashFields(fields.ToArray());
        return $"fbc1:{issuer}:{Number(command.BootstrapGeneration)}:{scope}";

        void AddOwner(RuntimeOwnerToken owner)
            => fields.AddRange([
                "owner", owner.SessionId, owner.TransferId, Number((int)owner.Direction), Number((int)owner.Route), owner.RouteToken,
                Number(owner.ProtocolVersion), Number(owner.LiveRouteEpoch), Number(owner.RuntimeGeneration),
                Number(owner.TerminalGeneration), owner.RuntimeFamily, owner.CreatedReason,
            ]);

        void AddPublication(RoutePublicationToken publication)
        {
            fields.AddRange(["publication", publication.SessionId, publication.TransferId, Number((int)publication.Direction)]);
            AddOwner(publication.RuntimeOwner);
            fields.AddRange([
                Number((int)publication.Route), publication.RouteToken, Number(publication.ProtocolVersion), Number(publication.LiveRouteEpoch),
                Number(publication.RuntimeGeneration), Number(publication.TerminalGeneration), Number(publication.LegGeneration),
                publication.RouteCommitGeneration is null ? "absent" : "present", Number(publication.RouteCommitGeneration ?? 0),
                Number((int)publication.TargetRoute), Number(publication.TargetProtocolVersion), Number((int)publication.TargetTransport),
                Number(publication.PublicationGeneration), Number((int)publication.ActionKind), publication.Reason,
            ]);
        }

        void AddCommit(RouteCommitToken commit)
        {
            fields.AddRange(["commit", commit.SessionId, commit.TransferId, Number((int)commit.Direction)]);
            AddOwner(commit.RuntimeOwner);
            var admission = commit.RuntimeUnlockAdmission;
            fields.AddRange(["admission", admission.SessionId, admission.TransferId, Number((int)admission.Direction)]);
            AddOwner(admission.RuntimeOwner);
            fields.AddRange([
                Number((int)admission.Route), admission.RouteToken, Number(admission.ProtocolVersion), Number(admission.LiveRouteEpoch),
                Number(admission.RuntimeGeneration), Number(admission.TerminalGeneration), Number(admission.LegGeneration),
                Number(admission.AdmissionGeneration), admission.Reason,
            ]);
            var survival = admission.FallbackSurvivalState;
            foreach (var value in new[]
            {
                survival.FallbackActive, survival.FallbackCurrent, survival.FallbackTerminalized, survival.CheckpointDeliveryRecoveryPending,
                survival.StateRefreshSendInFlight, survival.DeferredStateRefreshPending, survival.ProofReplayPending,
                survival.ControlPlanePressurePending, survival.BridgeRecoveryPending, survival.FallbackTailRepairPending,
                survival.PairedFallbackYieldGranted,
            })
            {
                fields.Add(value ? "1" : "0");
            }
            fields.AddRange([
                Number((int)commit.Route), commit.RouteToken, Number(commit.ProtocolVersion), Number(commit.LiveRouteEpoch),
                Number(commit.RuntimeGeneration), Number(commit.TerminalGeneration), Number(commit.LegGeneration),
                Number(commit.TransactionGeneration), Number(commit.OfferGeneration), Number(commit.TunaPathLeaseGeneration),
                commit.TunaPathLeaseListenerRunId is null ? "absent" : "present", commit.TunaPathLeaseListenerRunId ?? string.Empty,
                commit.SamePathTunaProbeAckId, Number((int)commit.TargetRoute), Number(commit.TargetProtocolVersion),
                Number((int)commit.TargetTransport), Number(commit.CommitGeneration), commit.Reason,
            ]);
        }
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
