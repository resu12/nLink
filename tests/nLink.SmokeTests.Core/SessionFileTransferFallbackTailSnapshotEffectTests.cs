using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    [Trait("FileTransferRuntimeSuite", "FallbackTailChurn")]
    public async Task FallbackTailRecoveryOwnerAuthorityEffect_FailedSuccessorUsesEnteringQuiescentReceipt()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        const string transferId =
            "transfer_fallback_tail_failed_successor_effect";
        var context = CreatePostTunaFallbackV6InboundContext(transferId);
        serviceType
            .GetField(
                "inboundTransfer",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var leg = Assert.IsType<FileTransferLeg>(
            GetPrivatePropertyValue(context, "CurrentTransferLeg"));
        leg.State = FileTransferLegState.Active;
        leg.CanSendData = true;
        leg.CheckpointRequestId = null;
        leg.CheckpointPriority = null;
        leg.CheckpointRequestedUtc = null;

        var logStart = GetOperationalLogLength();
        var failed = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailFailed,
                leg.RouteSelection,
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown,
                "unit_test_fallback_tail_failed_successor_effect",
                FileTransferLegState.RecoveryActive,
                CanSendData: false,
                leg.ProvenCommittedChunkIndex,
                leg.ProvenHighestObservedChunkIndex,
                leg.TransportEpochId,
                leg.BridgeRecoveryGeneration,
                CheckpointGeneration: leg.CheckpointGeneration,
                FallbackTailYieldKind: FallbackTailYieldKind.Failed),
            CreateCoordinatorStateForTest(context));
        Assert.NotNull(failed.FallbackTailYieldDecision);
        AdoptFallbackTailCoordinatorDecisionForEffectTest(context, failed);

        var failedOperation = Assert.IsType<
            FileTransferFallbackTailYieldOperation>(
                GetCoordinatorFallbackTailYieldSubstate(context)
                    .CurrentOperation);
        var failedDecision = failedOperation.Decision;
        var peerProof = new FileTransferFallbackTailPeerProof(
            GetPrivateProperty<string>(context, "SessionId"),
            transferId,
            FileTransferDirection.Outbound,
            new FileTransferAuthorityId(
                "fta2-fallback_tail_yield-89abcdef0123456789abcdef"),
            PredecessorAuthorityId: null,
            YieldGeneration: 1,
            FileTransferCoordinator.BuildFallbackTailYieldOwnerKey(
                GetPrivateProperty<string>(context, "SessionId"),
                transferId,
                leg.RouteSelection.TelemetryToken,
                leg.ProtocolVersion,
                failedDecision.CurrentLiveRouteEpoch,
                leg.Generation,
                leg.BridgeRecoveryGeneration,
                leg.TransportEpochId,
                failedDecision.CheckpointRequestIdSnapshot),
            failedDecision.PairKey,
            failedDecision.CurrentLiveRouteEpoch,
            leg.Generation,
            leg.BridgeRecoveryGeneration,
            leg.TransportEpochId,
            failedDecision.CheckpointRequestIdSnapshot,
            leg.ProvenCommittedChunkIndex,
            leg.ProvenHighestObservedChunkIndex,
            "peer-frame-fingerprint-failed-successor");
        var peerReceipt =
            FileTransferCoordinator.CreateFallbackTailPeerProofReceipt(
                CreateCoordinatorStateForTest(context),
                peerProof,
                "peer-proof-failed-successor",
                4_000);
        var peerApplied =
            FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(
                CreateCoordinatorStateForTest(context),
                peerReceipt);
        Assert.True(peerApplied.Accepted, peerApplied.Reason);
        _ = AdoptFallbackTailYieldDecisionForTest(context, peerApplied);
        var peerEnvelope = Assert.IsType<
            FileTransferCoordinatorTransitionEnvelope>(
                peerApplied.SnapshotEnvelope);
        var peerEvidence =
            SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(
                peerEnvelope);
        var failedPeerOperation = Assert.Single(
            peerEvidence.Aggregate.Operations.Where(operation =>
                operation.Family == "fallback_tail_yield" &&
                operation.OperationRole == "yield_current" &&
                operation.OperationKind ==
                    FallbackTailYieldKind.Failed.ToString()));

        var successor = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailQuiescent,
                leg.RouteSelection,
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown,
                "unit_test_fallback_tail_failed_successor_effect",
                FileTransferLegState.Active,
                CanSendData: true,
                leg.ProvenCommittedChunkIndex,
                leg.ProvenHighestObservedChunkIndex,
                leg.TransportEpochId,
                leg.BridgeRecoveryGeneration,
                CheckpointGeneration: leg.CheckpointGeneration,
                FallbackTailYieldKind: FallbackTailYieldKind.Quiescent),
            CreateCoordinatorStateForTest(context));
        Assert.NotNull(successor.FallbackTailYieldDecision);
        AdoptFallbackTailCoordinatorDecisionForEffectTest(context, successor);
        var successorEnvelope = Assert.IsType<
            FileTransferCoordinatorTransitionEnvelope>(
                GetPrivatePropertyValue(
                    context,
                    "LastFallbackTailYieldSnapshotEnvelope"));
        var successorEvidence =
            SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(
                successorEnvelope);
        Assert.Equal(3, successorEnvelope.Aggregate.Operations.Count(
            operation => operation.Family == "fallback_tail_yield"));
        var successorOperationIndex = Array.FindIndex(
            successorEnvelope.Aggregate.Operations.ToArray(),
            operation =>
                operation.Family == "fallback_tail_yield" &&
                operation.OperationRole == "yield_current" &&
                operation.OperationKind ==
                    FallbackTailYieldKind.Quiescent.ToString() &&
                operation.BeforeState ==
                    FileTransferFallbackTailYieldPairingState.None.ToString() &&
                operation.AfterState ==
                    FileTransferFallbackTailYieldPairingState.PeerProofObserved
                        .ToString());
        Assert.True(successorOperationIndex >= 0);
        var successorEvidenceOperation =
            successorEvidence.Aggregate.Operations[successorOperationIndex];
        var successorYieldReceipt = Assert.Single(
            successorEvidenceOperation.FallbackTailYield!.Receipts.Where(
                receipt =>
                    receipt.Stage == "yield" &&
                    receipt.ReceiptValidated &&
                    receipt.ReceiptStatus == "Accepted"));

        var persistent = GetPrivateProperty<
            FileTransferCoordinatorPersistentState>(
                context,
                "CoordinatorPersistentState");
        SetPrivateProperty(
            context,
            "CoordinatorPersistentState",
            persistent with
            {
                SnapshotStateGeneration =
                    successorEnvelope.Aggregate.AfterStateGeneration + 1,
            });

        serviceType
            .GetMethod(
                "ClearInboundFallbackTailRecoveryOwnershipAfterYieldLocked",
                BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(
                null,
                [context, leg, "fallback_tail_quiescent"]);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart)
                .Split(
                    ["\r\n", "\n"],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(line =>
                    line.Contains(
                        "effect_kind=recovery_owner_retired_after_yield;",
                        StringComparison.Ordinal) &&
                    line.Contains(
                        $"transfer_id={successorEvidence.TransferId};",
                        StringComparison.Ordinal)),
            timeoutMs: 5_000);
        var authorityEffects = ReadOperationalLogTail(logStart)
            .Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(
                "event=filetransfer_coordinator_authority_effect;",
                StringComparison.Ordinal))
            .ToArray();
        var retirementEffect = Assert.Single(authorityEffects.Where(line =>
            line.Contains(
                "effect_kind=recovery_owner_retired_after_yield;",
                StringComparison.Ordinal)));
        Assert.Contains(
            $"transition_id={successorEvidence.TransitionId};",
            retirementEffect,
            StringComparison.Ordinal);
        Assert.Contains(
            $"coordinator_state_generation={successorEnvelope.Aggregate.AfterStateGeneration};",
            retirementEffect,
            StringComparison.Ordinal);
        Assert.Contains(
            $"operation_key={successorEvidenceOperation.OperationKey};",
            retirementEffect,
            StringComparison.Ordinal);
        Assert.Contains(
            $"receipt_identity={successorYieldReceipt.ReceiptIdentity};",
            retirementEffect,
            StringComparison.Ordinal);

        var peerEffects = authorityEffects.Where(line => line.Contains(
                "effect_kind=peer_proof_observed;",
                StringComparison.Ordinal))
            .ToArray();
        var peerEffect = Assert.Single(peerEffects);
        Assert.Contains(
            $"operation_key={failedPeerOperation.OperationKey};",
            peerEffect,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            peerEffects,
            line => line.Contains(
                $"operation_key={successorEvidenceOperation.OperationKey};",
                StringComparison.Ordinal));
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    [Trait("FileTransferRuntimeSuite", "FallbackTailChurn")]
    public async Task FallbackTailRecoveryOwnerAuthorityEffect_TerminalCleanupAfterLocalProofDoesNotReemit()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        const string transferId =
            "transfer_fallback_tail_recovery_owner_terminal_one_shot";
        var context = CreatePostTunaFallbackV6InboundContext(transferId);
        serviceType
            .GetField(
                "inboundTransfer",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var leg = Assert.IsType<FileTransferLeg>(
            GetPrivatePropertyValue(context, "CurrentTransferLeg"));
        leg.State = FileTransferLegState.Active;
        leg.CanSendData = true;
        leg.CheckpointRequestId = null;
        leg.CheckpointPriority = null;
        leg.CheckpointRequestedUtc = null;

        var logStart = GetOperationalLogLength();
        var yieldDecision = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailQuiescent,
                leg.RouteSelection,
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown,
                "unit_test_fallback_tail_terminal_effect_one_shot",
                FileTransferLegState.Active,
                CanSendData: true,
                leg.ProvenCommittedChunkIndex,
                leg.ProvenHighestObservedChunkIndex,
                leg.TransportEpochId,
                leg.BridgeRecoveryGeneration,
                CheckpointGeneration: leg.CheckpointGeneration,
                FallbackTailYieldKind: FallbackTailYieldKind.Quiescent),
            CreateCoordinatorStateForTest(context));
        Assert.NotNull(yieldDecision.FallbackTailYieldDecision);
        serviceType
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method =>
                method.Name == "AdoptCoordinatorStateLocked" &&
                method.GetParameters()[0].ParameterType == context.GetType() &&
                method.GetParameters()[1].ParameterType ==
                    typeof(FileTransferCoordinatorDecision))
            .Invoke(null, [context, yieldDecision]);
        var yieldEnvelope = Assert.IsType<
            FileTransferCoordinatorTransitionEnvelope>(
                GetPrivatePropertyValue(
                    context,
                    "LastFallbackTailYieldSnapshotEnvelope"));
        var evidenceYieldEnvelope =
            SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(
                yieldEnvelope);

        serviceType
            .GetMethod(
                "ClearInboundFallbackTailRecoveryOwnershipAfterYieldLocked",
                BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, leg, "fallback_tail_quiescent"]);
        var operation = Assert.IsType<FileTransferFallbackTailYieldOperation>(
            GetCoordinatorFallbackTailYieldSubstate(context).CurrentOperation);
        var frame = Assert.IsType<FileTransferFallbackTailYieldFrameV6>(
            serviceType
                .GetMethod(
                    "CreateFallbackTailYieldFrame",
                    BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(
                    null,
                    [
                        operation,
                        leg.ProvenCommittedChunkIndex,
                        leg.ProvenHighestObservedChunkIndex,
                    ]));
        var localProofApplied = Assert.IsType<bool>(serviceType
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method =>
                method.Name ==
                    "ApplyFallbackTailLocalProofReceiptLocked" &&
                method.GetParameters()[0].ParameterType == context.GetType())
            .Invoke(
                null,
                [
                    context,
                    frame,
                    "unit_test_fallback_tail_terminal_effect_one_shot",
                ]));
        Assert.True(localProofApplied);
        var localProofEnvelope = Assert.IsType<
            FileTransferCoordinatorTransitionEnvelope>(
                GetPrivatePropertyValue(
                    context,
                    "LastFallbackTailYieldSnapshotEnvelope"));
        Assert.NotEqual(
            yieldEnvelope.TransitionId,
            localProofEnvelope.TransitionId);

        var persistent = GetPrivateProperty<
            FileTransferCoordinatorPersistentState>(
                context,
                "CoordinatorPersistentState");
        var currentOperation = Assert.IsType<
            FileTransferFallbackTailYieldOperation>(
                persistent.FallbackTailYield.CurrentOperation);
        var olderTerminal = currentOperation with
        {
            Decision = currentOperation.Decision with
            {
                AuthorityId = new FileTransferAuthorityId(
                    CreatePeerFallbackTailYieldAuthorityId(
                        $"older:{transferId}")),
                YieldGeneration = currentOperation.Decision.YieldGeneration + 1,
            },
            Scope = currentOperation.Scope with
            {
                TransportEpoch = currentOperation.Scope.TransportEpoch + 1,
            },
            PairingState =
                FileTransferFallbackTailYieldPairingState.Invalidated,
            LocalProof = null,
            InvalidationReason = "unit_test_previous_fallback_tail_cycle",
        };
        SetPrivateProperty(
            context,
            "CoordinatorPersistentState",
            persistent with
            {
                FallbackTailYield = persistent.FallbackTailYield with
                {
                    LastTerminalOperation = olderTerminal,
                },
            });
        var disposition =
            FileTransferCoordinator.GetFallbackTailRetiredOwnerDisposition(
                CreateCoordinatorStateForTest(context));
        Assert.False(disposition.ShouldSuppress);
        Assert.Equal(
            FileTransferFallbackTailRetiredOwnerDispositionStatus.ScopeChanged,
            disposition.Status);

        serviceType
            .GetMethod(
                "ConsumeInboundFallbackTailTerminalProofBeforeTerminalLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    FileTransferTransferState.Completed,
                    "transfer_terminal",
                ]);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart)
                .Split(
                    ["\r\n", "\n"],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(line =>
                    line.Contains(
                        "event=filetransfer_fallback_tail_recovery_owner_retired_after_yield;",
                        StringComparison.Ordinal) &&
                    line.Contains(
                        $"transfer_id={transferId};",
                        StringComparison.Ordinal) &&
                    line.Contains(
                        "reason=fallback_tail_terminal_completed",
                        StringComparison.Ordinal)),
            timeoutMs: 5_000);
        var recoveryOwnerEffects = ReadOperationalLogTail(logStart)
            .Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(line =>
                line.Contains(
                    "event=filetransfer_coordinator_authority_effect;",
                    StringComparison.Ordinal) &&
                line.Contains(
                    $"transfer_id={evidenceYieldEnvelope.TransferId};",
                    StringComparison.Ordinal) &&
                line.Contains(
                    "effect_kind=recovery_owner_retired_after_yield;",
                    StringComparison.Ordinal))
            .ToArray();
        var recoveryOwnerEffect = Assert.Single(recoveryOwnerEffects);
        Assert.Contains(
            $"transition_id={evidenceYieldEnvelope.TransitionId};",
            recoveryOwnerEffect,
            StringComparison.Ordinal);
    }

    private static void AdoptFallbackTailCoordinatorDecisionForEffectTest(
        object context,
        FileTransferCoordinatorDecision decision)
        => typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method =>
                method.Name == "AdoptCoordinatorStateLocked" &&
                method.GetParameters()[0].ParameterType == context.GetType() &&
                method.GetParameters()[1].ParameterType ==
                    typeof(FileTransferCoordinatorDecision))
            .Invoke(null, [context, decision]);
}
