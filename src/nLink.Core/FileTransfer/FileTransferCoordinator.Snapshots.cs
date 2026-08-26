using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NLink.Core.FileTransfer;

internal enum FileTransferCoordinatorSnapshotSelectionMode
{
    Shadow = 1,
    Authoritative = 2,
}

internal sealed record FileTransferCoordinatorStateBoundarySnapshot(
    string Route,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RoutePublicationGeneration,
    int RouteCommitGeneration,
    int RuntimeGeneration,
    int TerminalGeneration,
    int LegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    int CheckpointGeneration,
    string CheckpointRequestId,
    string DataSessionIdentity,
    long DataSessionGeneration);

internal sealed record FileTransferCoordinatorReceiptTransitionSnapshot(
    string Stage,
    string CommandIdentity,
    long CommandGeneration,
    string CommandFingerprint,
    string ReceiptIdentity,
    long ReceiptGeneration,
    string ReceiptStatus,
    string ReceiptReason,
    string ReceiptFingerprint,
    bool ReceiptValidated);

internal sealed record FileTransferCoordinatorRouteRuntimeOperationSnapshot(
    string Role,
    FileTransferCoordinatorStateBoundarySnapshot Boundary,
    string TokenFingerprint);

internal sealed record FileTransferCoordinatorRouteTransitionOperationSnapshot(
    string Role,
    string TransitionKind,
    FileTransferCoordinatorStateBoundarySnapshot SourceBoundary,
    FileTransferCoordinatorStateBoundarySnapshot TargetBoundary,
    string SourceTransport,
    string ProofBoundaryKind,
    string ProofAdmissionMode,
    string ProofBoundaryFingerprint,
    string DurableCheckpointFingerprint,
    string TargetRoute,
    int TargetProtocolVersion,
    string TargetTransport,
    string HandoffKind,
    string DispositionKind,
    string RetainedFrameType,
    string RetainedFrameFingerprint,
    ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot> Receipts);

internal sealed record FileTransferCoordinatorRuntimeUnlockRetryOperationSnapshot(
    string Role,
    FileTransferCoordinatorStateBoundarySnapshot SourceBoundary,
    int AuthorityGeneration,
    long RecoveryContractGeneration,
    int DispatchAttemptGeneration,
    int MissingDispatchSuccessorGeneration,
    long RetiredOfferGeneration,
    long OfferGeneration,
    string OfferNonceFingerprint,
    long DispatchDeadlineUtcMs,
    bool DispatchObserved,
    string ReleaseKind,
    string ReleaseAuthorityId,
    int ReleaseGeneration,
    string ReleaseOutcome,
    string ListenerRearmRequirement,
    string ListenerExecutionMode,
    bool RequestActivePredecessorSupersession,
    string SuccessorAuthorityId,
    ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot> Receipts);

internal sealed record FileTransferCoordinatorReceiveRecoveryOperationSnapshot(
    string Role,
    FileTransferCoordinatorStateBoundarySnapshot SourceBoundary,
    FileTransferCoordinatorStateBoundarySnapshot EffectiveBoundary,
    string RecoveryKind,
    long BridgeDecisionGeneration,
    long RecoveryLivenessLeaseGeneration,
    string ExecutionState,
    string ClosureState,
    long ClosureGeneration,
    string ProofIdentity,
    long ProofObservedUtcMs,
    string HeldRetryAuthorityId,
    string ReservedRetryAuthorityId,
    long InitialActivationHoldGeneration,
    long EnvelopeRefreshGeneration,
    long CheckpointBindingGeneration,
    long CheckpointDeliveryGeneration,
    long CheckpointResumeGeneration,
    long CheckpointFrontierHoldGeneration,
    long CheckpointFrontierRecheckGeneration,
    ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot> Receipts);

internal sealed record FileTransferCoordinatorFallbackTailYieldOperationSnapshot(
    string Role,
    FileTransferCoordinatorStateBoundarySnapshot SourceBoundary,
    FileTransferCoordinatorStateBoundarySnapshot EffectiveBoundary,
    FileTransferCoordinatorStateBoundarySnapshot TargetBoundary,
    string YieldKind,
    string PairingState,
    string OwnerKey,
    string PairKey,
    string LocalProofIdentity,
    string LocalProofFingerprint,
    string PeerProofAuthorityId,
    int PeerProofYieldGeneration,
    string PeerProofIdentity,
    string PeerProofFingerprint,
    long PairingGeneration,
    string PairedReleaseAuthorityId,
    int PairedReleaseGeneration,
    string PairedReleaseOutcome,
    string FailedPredecessorAuthorityId,
    bool FailedSupersessionCarriedPeerProof,
    int ReauthorizationGeneration,
    long ReauthorizationSourceTransportEpoch,
    long ReauthorizationTargetTransportEpoch,
    long ReservationGeneration,
    string ReservedRetryAuthorityId,
    long ConsumingInstallGeneration,
    int RetryHoldGeneration,
    int RetryRecheckGeneration,
    string InvalidationReason,
    ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot> Receipts);

internal sealed record
    FileTransferCoordinatorRegularV4FrontierRepairPressureOperationSnapshot(
        string Role,
        FileTransferCoordinatorStateBoundarySnapshot SourceBoundary,
        FileTransferCoordinatorStateBoundarySnapshot EffectiveBoundary,
        string RepairKind,
        string RepairKey,
        int FrontierChunkIndex,
        int DurableReceivedHighestChunkIndex,
        int RequestedChunkCount,
        string TargetTransport,
        string PressureReason,
        int DecisionGeneration,
        long PressureInstallGeneration,
        string BindingState,
        string SettlementOutcome,
        string SettlementReason,
        bool CurrentAuthorityCleared,
        bool RetainedPredecessorCleared,
        string SuccessorAuthorityId,
        string RecoveryAuthorityId,
        long RecoveryOperationGeneration,
        string RecoveryKind,
        ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot>
            Receipts);

internal sealed record
    FileTransferCoordinatorResponderProbeHandoffOperationSnapshot(
        string Role,
        string TransitionMode,
        FileTransferCoordinatorStateBoundarySnapshot SourceBoundary,
        FileTransferCoordinatorStateBoundarySnapshot EffectiveBoundary,
        FileTransferCoordinatorStateBoundarySnapshot TargetBoundary,
        long TransactionGeneration,
        long OfferGeneration,
        long PendingAnswerAckGeneration,
        long PayerDecisionId,
        long LaunchGeneration,
        long TunaPathLeaseGeneration,
        string TunaPathLeaseListenerRunId,
        string Lanes,
        string AckMessageId,
        string AckSourceFingerprint,
        string PeerProbeFrameFingerprint,
        string ProbeId,
        string ProbeState,
        string ProbeTransport,
        long ProbeAckedUtcMs,
        int AdmissionGeneration,
        int PendingPreCommitGeneration,
        string OwnerKey,
        string PairKey,
        string LocalProofIdentity,
        string LocalProofFingerprint,
        string PeerProofIdentity,
        string PeerProofFingerprint,
        string PairedReleaseAuthorityId,
        int PairedReleaseGeneration,
        long PairingGeneration,
        string HandoffAuthorityId,
        int HandoffGeneration,
        string LinkedTransitionAuthorityId,
        long LinkedTransitionGeneration,
        string DecisionStatus,
        string DecisionReason,
        ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot>
            Receipts);

internal sealed record FileTransferCoordinatorOperationTransitionSnapshot(
    string Family,
    string OperationRole,
    string OperationKey,
    string OperationKind,
    string AuthorityId,
    string PredecessorAuthorityId,
    long OperationGeneration,
    string BeforeState,
    string AfterState,
    string CommandIdentity,
    long CommandGeneration,
    string CommandFingerprint,
    string ReceiptIdentity,
    long ReceiptGeneration,
    string ReceiptStatus,
    string ReceiptFingerprint,
    bool ReceiptValidated,
    bool StateApplied,
    FileTransferCoordinatorRouteRuntimeOperationSnapshot? RouteRuntime,
    FileTransferCoordinatorRouteTransitionOperationSnapshot? RouteTransition,
    FileTransferCoordinatorRuntimeUnlockRetryOperationSnapshot?
        RuntimeUnlockRetry = null,
    FileTransferCoordinatorReceiveRecoveryOperationSnapshot?
        ReceiveRecovery = null,
    FileTransferCoordinatorFallbackTailYieldOperationSnapshot?
        FallbackTailYield = null,
    FileTransferCoordinatorRegularV4FrontierRepairPressureOperationSnapshot?
        RegularV4FrontierRepairPressure = null,
    FileTransferCoordinatorResponderProbeHandoffOperationSnapshot?
        ResponderProbeHandoff = null);

internal sealed record FileTransferCoordinatorAggregateTransitionSnapshot(
    long BeforeStateGeneration,
    long AfterStateGeneration,
    string BeforeStateFingerprint,
    string AfterStateFingerprint,
    FileTransferCoordinatorStateBoundarySnapshot BeforeBoundary,
    FileTransferCoordinatorStateBoundarySnapshot AfterBoundary,
    ImmutableArray<string> ChangedFamilies,
    ImmutableArray<FileTransferCoordinatorOperationTransitionSnapshot> Operations);

internal sealed record FileTransferCoordinatorTransitionEnvelope(
    int SnapshotContractVersion,
    string SnapshotSelectionMode,
    string CoordinatorInstanceId,
    string SessionId,
    string TransferId,
    string Direction,
    long DecisionUtcMilliseconds,
    string TransitionId,
    string DecisionKind,
    string DecisionStatus,
    string DecisionReason,
    FileTransferCoordinatorAggregateTransitionSnapshot Aggregate)
{
    internal const int CurrentContractVersion = 1;

    internal string ToCanonicalJson()
        => SerializeCanonical(this, SnapshotJsonOptions);

    internal string ToBase64UrlPayload()
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(ToCanonicalJson()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal string CalculatePayloadSha256()
        => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson())))
            .ToLowerInvariant();

    internal static string SerializeCanonical(
        object value,
        JsonSerializerOptions options)
    {
        var element = JsonSerializer.SerializeToElement(
            value,
            value.GetType(),
            options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalElement(writer, element);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalElement(
        Utf8JsonWriter writer,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element
                             .EnumerateObject()
                             .OrderBy(
                                 property => property.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(
                    element.GetRawText(),
                    skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported snapshot JSON value kind {element.ValueKind}.");
        }
    }

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };
}

internal readonly record struct FileTransferCoordinatorSnapshotResult(
    FileTransferCoordinatorState State,
    FileTransferCoordinatorTransitionEnvelope Envelope);

internal readonly record struct
    FileTransferCoordinatorRouteTransitionCommitEvidence(
        FileTransferRouteTransitionAuthority Authority,
        FileTransferRouteTransitionPhysicalDrainReceipt DrainReceipt,
        RuntimeUnlockRouteCommitProof? FinalProof,
        FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt?
            PressureSettlementReceipt,
        RouteCommitToken? RouteCommitToken,
        FileTransferRouteTransitionDispositionCommand? DispositionCommand);

internal static partial class FileTransferCoordinator
{
    private static long lastSnapshotDecisionUtcMilliseconds;

    internal static
        RuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision
        FinalizeResponderProbeHandoffSnapshot(
            FileTransferCoordinatorState before,
            RuntimeUnlockResponderPeerProbeRouteTransitionHandoffDecision
                decision,
            string decisionKind,
            RuntimeUnlockResponderPeerProbeRouteTransitionObservation
                observation)
    {
        var coordinatorInstanceId =
            before.PersistentState.CoordinatorInstanceId;
        if (string.IsNullOrWhiteSpace(coordinatorInstanceId))
        {
            return decision;
        }

        var attempted = CreateResponderProbeHandoffAttemptSnapshot(
            before,
            decisionKind,
            decision.Status.ToString(),
            decision.Reason,
            observation,
            decision.Authority);
        var beforeHandoff = before.PersistentState
            .RuntimeUnlockResponderPeerProbeRouteTransitionHandoff;
        var afterHandoff = decision.State.PersistentState
            .RuntimeUnlockResponderPeerProbeRouteTransitionHandoff;
        var handoffStateApplied = beforeHandoff != afterHandoff;
        var operations = ImmutableArray.CreateBuilder<
            FileTransferCoordinatorOperationTransitionSnapshot>();
        operations.Add(attempted);
        var resultingAuthority = decision.State.PersistentState
            .RuntimeUnlockResponderPeerProbeRouteTransitionHandoff
            .CurrentAuthority ??
            decision.State.PersistentState
                .RuntimeUnlockResponderPeerProbeRouteTransitionHandoff
                .LastTerminalAuthority;
        if (resultingAuthority is { } exactAuthority &&
            decision.Status is not
                RuntimeUnlockResponderPeerProbeRouteTransitionHandoffStatus
                    .Idempotent and not
                RuntimeUnlockResponderPeerProbeRouteTransitionHandoffStatus
                    .Rejected)
        {
            var role = decision.Status switch
            {
                RuntimeUnlockResponderPeerProbeRouteTransitionHandoffStatus
                        .Held =>
                    "owner_work_hold",
                RuntimeUnlockResponderPeerProbeRouteTransitionHandoffStatus
                        .Invalidated =>
                    "handoff_terminal",
                _ => "handoff_current",
            };
            var descriptor = CreateResponderProbeHandoffDescriptor(
                decision.State,
                exactAuthority,
                role,
                decision.Status.ToString(),
                decision.Reason);
            operations.Add(CreateOperationTransitionSnapshot(
                descriptor,
                before.PersistentState
                    .RuntimeUnlockResponderPeerProbeRouteTransitionHandoff
                    .LastStatus?.ToString() ?? "None",
                descriptor.State,
                stateApplied: handoffStateApplied));
        }
        var snapshot = CreateTransitionSnapshot(
            before,
            decision.State,
            coordinatorInstanceId,
            before.PersistentState.SnapshotStateGeneration,
            decisionKind,
            decision.Status.ToString(),
            decision.Reason,
            operations.ToImmutable(),
            FileTransferCoordinatorSnapshotSelectionMode.Authoritative,
            suppliedOperationsAreComplete: true);
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    internal static FileTransferCoordinatorSnapshotResult
        CreateResponderProbeHandoffAttemptSnapshot(
            FileTransferCoordinatorState state,
            RuntimeUnlockResponderPeerProbeRouteTransitionObservation
                observation,
            string decisionKind,
            string decisionStatus,
            string decisionReason)
    {
        var operation = CreateResponderProbeHandoffAttemptSnapshot(
            state,
            decisionKind,
            decisionStatus,
            decisionReason,
            observation,
            state.PersistentState
                .RuntimeUnlockResponderPeerProbeRouteTransitionHandoff
                .CurrentAuthority);
        return CreateTransitionSnapshot(
            state,
            state,
            state.PersistentState.CoordinatorInstanceId!,
            state.PersistentState.SnapshotStateGeneration,
            decisionKind,
            decisionStatus,
            decisionReason,
            ImmutableArray.Create(operation),
            FileTransferCoordinatorSnapshotSelectionMode.Authoritative);
    }

    private static FileTransferCoordinatorOperationTransitionSnapshot
        CreateResponderProbeHandoffAttemptSnapshot(
            FileTransferCoordinatorState state,
            string decisionKind,
            string decisionStatus,
            string decisionReason,
            RuntimeUnlockResponderPeerProbeRouteTransitionObservation
                observation,
            RuntimeUnlockResponderPeerProbeRouteTransitionHandoffAuthority?
                authority)
    {
        var authorityId = authority?.AuthorityId.ToString() ??
            $"attempt:{CalculateSnapshotFingerprint(observation)[..16]}";
        var generation = Math.Max(
            1,
            authority?.AuthorityGeneration ??
            state.PersistentState
                .RuntimeUnlockResponderPeerProbeRouteTransitionHandoff
                .LastAuthorityGeneration + 1);
        var role = decisionStatus switch
        {
            "Held" => "owner_work_hold",
            "Invalidated" => "handoff_terminal",
            "Accepted" or "Idempotent" => "peer_acceptance_attempt",
            _ => "attempted_decision",
        };
        var receipt = CreateResponderProbeHandoffReceipt(
            authorityId,
            generation,
            decisionKind,
            decisionStatus,
            decisionReason,
            observation);
        return new FileTransferCoordinatorOperationTransitionSnapshot(
            "responder_probe_handoff",
            role,
            CreateCoordinatorOperationKey(
                "responder_probe_handoff",
                role,
                authorityId,
                generation,
                decisionKind),
            decisionKind,
            authorityId,
            "(none)",
            generation,
            "None",
            decisionStatus,
            receipt.CommandIdentity,
            receipt.CommandGeneration,
            receipt.CommandFingerprint,
            receipt.ReceiptIdentity,
            receipt.ReceiptGeneration,
            receipt.ReceiptStatus,
            receipt.ReceiptFingerprint,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            CreateResponderProbeHandoffSnapshot(
                state,
                role,
                observation,
                authority,
                decisionStatus,
                decisionReason,
                ImmutableArray.Create(receipt)));
    }

    internal static RegularV4FrontierRepairPressureBindingDecision
        FinalizeRegularV4FrontierRepairPressureSnapshot(
            FileTransferCoordinatorState before,
            RegularV4FrontierRepairPressureBindingDecision decision,
            string decisionKind,
            object? command = null)
    {
        var coordinatorInstanceId =
            before.PersistentState.CoordinatorInstanceId;
        if (string.IsNullOrWhiteSpace(coordinatorInstanceId))
        {
            return decision;
        }

        var snapshot = CreateTransitionSnapshot(
            before,
            decision.State,
            coordinatorInstanceId,
            before.PersistentState.SnapshotStateGeneration,
            decisionKind,
            decision.Status.ToString(),
            decision.Reason,
            CreateRegularV4PressureAttemptedDecisionSnapshots(
                before,
                decisionKind,
                decision.Status.ToString(),
                decision.Reason,
                command ??
                    decision.SettlementCommand ??
                    (object?)decision.InstallCommand,
                decision.Binding?.Authority.AuthorityId));
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    internal static RegularV4FrontierRepairPressureRecoveryHandoffDecision
        FinalizeRegularV4FrontierRepairPressureSnapshot(
            FileTransferCoordinatorState before,
            RegularV4FrontierRepairPressureRecoveryHandoffDecision decision,
            string decisionKind,
            object? command = null)
    {
        var coordinatorInstanceId =
            before.PersistentState.CoordinatorInstanceId;
        if (string.IsNullOrWhiteSpace(coordinatorInstanceId))
        {
            return decision;
        }

        var authorityId = decision.Transition?.PressureBinding.Authority.AuthorityId;
        var snapshot = CreateTransitionSnapshot(
            before,
            decision.State,
            coordinatorInstanceId,
            before.PersistentState.SnapshotStateGeneration,
            decisionKind,
            decision.Status.ToString(),
            decision.Reason,
            CreateRegularV4PressureAttemptedDecisionSnapshots(
                before,
                decisionKind,
                decision.Status.ToString(),
                decision.Reason,
                command ?? (object?)decision.Transition,
                authorityId));
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    private static ImmutableArray<
        FileTransferCoordinatorOperationTransitionSnapshot>
        CreateRegularV4PressureAttemptedDecisionSnapshots(
            FileTransferCoordinatorState state,
            string decisionKind,
            string decisionStatus,
            string decisionReason,
            object? command,
            FileTransferAuthorityId? authorityId)
    {
        var attemptedRole = decisionKind switch
        {
            "PrepareRegularV4FrontierRepairPressureSettlement" =>
                "pressure_settlement_prepared",
            "PrepareRegularV4FrontierRepairPressureRecoveryHandoff" =>
                "recovery_handoff_prepared",
            _ => "attempted_decision",
        };
        var descriptorBuilder =
            ImmutableArray.CreateBuilder<CoordinatorOperationDescriptor>();
        AddRegularV4FrontierRepairPressureOperationDescriptors(
            state,
            descriptorBuilder);
        IEnumerable<CoordinatorOperationDescriptor> descriptors =
            descriptorBuilder;
        if (authorityId is { IsDefault: false } exactAuthority)
        {
            descriptors = descriptors.Where(descriptor => string.Equals(
                descriptor.AuthorityId,
                exactAuthority.ToString(),
                StringComparison.Ordinal));
        }
        var descriptor = descriptors
            .OrderBy(descriptor => descriptor.OperationRole, StringComparer.Ordinal)
            .FirstOrDefault();
        var commandFingerprint = CalculateSnapshotFingerprint(command);
        var boundary = CreateStateBoundarySnapshot(state);
        var authority = authorityId is { IsDefault: false } exact
            ? exact.ToString()
            : descriptor?.AuthorityId ?? $"attempt:{commandFingerprint}";
        var generation = Math.Max(
            1,
            descriptor?.OperationGeneration ??
            state.LastRepairReplayGeneration + 1);
        var receipt = new FileTransferCoordinatorReceiptTransitionSnapshot(
            "attempted_decision",
            $"{authority}:{decisionKind}",
            generation,
            commandFingerprint,
            $"{authority}:{decisionKind}:decision",
            generation,
            decisionStatus,
            decisionReason,
            CalculateSnapshotFingerprint(new
            {
                decisionKind,
                decisionStatus,
                decisionReason,
                command,
            }),
            true);
        if (descriptor is not null)
        {
            var operation = CreateOperationTransitionSnapshot(
                descriptor,
                descriptor.State,
                descriptor.State,
                stateApplied: false);
            return ImmutableArray.Create(operation with
            {
                OperationRole = attemptedRole,
                OperationKey = CreateCoordinatorOperationKey(
                    "regular_v4_frontier_repair_pressure",
                    attemptedRole,
                    authority,
                    generation,
                    decisionKind),
                OperationKind = decisionKind,
                CommandIdentity = receipt.CommandIdentity,
                CommandGeneration = receipt.CommandGeneration,
                CommandFingerprint = receipt.CommandFingerprint,
                ReceiptIdentity = receipt.ReceiptIdentity,
                ReceiptGeneration = receipt.ReceiptGeneration,
                ReceiptStatus = receipt.ReceiptStatus,
                ReceiptFingerprint = receipt.ReceiptFingerprint,
                ReceiptValidated = true,
                StateApplied = false,
                RegularV4FrontierRepairPressure =
                    operation.RegularV4FrontierRepairPressure is null
                        ? null
                        : operation.RegularV4FrontierRepairPressure with
                        {
                            Role = attemptedRole,
                            Receipts = ImmutableArray.Create(receipt),
                        },
            });
        }

        return ImmutableArray.Create(
            new FileTransferCoordinatorOperationTransitionSnapshot(
                Family: "regular_v4_frontier_repair_pressure",
                OperationRole: attemptedRole,
                OperationKey: CreateCoordinatorOperationKey(
                    "regular_v4_frontier_repair_pressure",
                    attemptedRole,
                    authority,
                    generation,
                    decisionKind),
                OperationKind: decisionKind,
                AuthorityId: authority,
                PredecessorAuthorityId: "(none)",
                OperationGeneration: generation,
                BeforeState: "None",
                AfterState: "None",
                CommandIdentity: receipt.CommandIdentity,
                CommandGeneration: receipt.CommandGeneration,
                CommandFingerprint: receipt.CommandFingerprint,
                ReceiptIdentity: receipt.ReceiptIdentity,
                ReceiptGeneration: receipt.ReceiptGeneration,
                ReceiptStatus: receipt.ReceiptStatus,
                ReceiptFingerprint: receipt.ReceiptFingerprint,
                ReceiptValidated: true,
                StateApplied: false,
                RouteRuntime: null,
                RouteTransition: null,
                RuntimeUnlockRetry: null,
                ReceiveRecovery: null,
                FallbackTailYield: null,
                RegularV4FrontierRepairPressure: new(
                    attemptedRole,
                    boundary,
                    boundary,
                    "RegularV4FrontierRepairPressure",
                    "(none)",
                    0,
                    -1,
                    0,
                    "Unknown",
                    decisionReason,
                    checked((int)generation),
                    0,
                    decisionStatus,
                    "(none)",
                    decisionReason,
                    false,
                    false,
                    "(none)",
                    "(none)",
                    0,
                    "(none)",
                    ImmutableArray.Create(receipt))));
    }

    internal static FileTransferFallbackTailYieldTransitionDecision
        FinalizeFallbackTailYieldSnapshot(
            FileTransferCoordinatorState before,
            FileTransferFallbackTailYieldTransitionDecision decision,
            string decisionKind,
            object? command = null,
            FileTransferAuthorityId? authorityId = null)
    {
        var coordinatorInstanceId =
            before.PersistentState.CoordinatorInstanceId;
        if (string.IsNullOrWhiteSpace(coordinatorInstanceId))
        {
            return decision;
        }

        var snapshot = CreateTransitionSnapshot(
            before,
            decision.State,
            coordinatorInstanceId,
            before.PersistentState.SnapshotStateGeneration,
            decisionKind,
            decision.Status.ToString(),
            decision.Reason,
            CreateFallbackTailYieldAttemptedDecisionSnapshots(
                before,
                decisionKind,
                decision.Status.ToString(),
                decision.Reason,
                command,
                authorityId));
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    internal static FileTransferCoordinatorDecision
        FinalizeFallbackTailYieldSnapshot(
            FileTransferCoordinatorState before,
            FileTransferCoordinatorDecision decision,
            string decisionKind,
            object? command = null,
            FileTransferAuthorityId? authorityId = null)
    {
        var coordinatorInstanceId =
            before.PersistentState.CoordinatorInstanceId;
        if (string.IsNullOrWhiteSpace(coordinatorInstanceId))
        {
            return decision;
        }

        var status = !string.IsNullOrWhiteSpace(decision.MutationRejectedReason)
            ? "Rejected"
            : before.PersistentState == decision.State.PersistentState
                ? "Idempotent"
                : "Accepted";
        var reason = decision.MutationRejectedReason ?? decision.Reason;
        var snapshot = CreateTransitionSnapshot(
            before,
            decision.State,
            coordinatorInstanceId,
            before.PersistentState.SnapshotStateGeneration,
            decisionKind,
            status,
            reason,
            CreateFallbackTailYieldAttemptedDecisionSnapshots(
                before,
                decisionKind,
                status,
                reason,
                command,
                authorityId));
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    private static ImmutableArray<
        FileTransferCoordinatorOperationTransitionSnapshot>
        CreateFallbackTailYieldAttemptedDecisionSnapshots(
            FileTransferCoordinatorState state,
            string decisionKind,
            string decisionStatus,
            string decisionReason,
            object? command,
            FileTransferAuthorityId? authorityId)
    {
        var descriptorBuilder =
            ImmutableArray.CreateBuilder<CoordinatorOperationDescriptor>();
        AddFallbackTailYieldOperationDescriptors(state, descriptorBuilder);
        IEnumerable<CoordinatorOperationDescriptor> descriptors =
            descriptorBuilder;
        if (authorityId is { IsDefault: false } exactAuthority)
        {
            descriptors = descriptors.Where(descriptor => string.Equals(
                descriptor.AuthorityId,
                exactAuthority.ToString(),
                StringComparison.Ordinal));
        }
        var descriptor = descriptors
            .OrderBy(descriptor => descriptor.OperationRole, StringComparer.Ordinal)
            .FirstOrDefault();
        var commandFingerprint = CalculateSnapshotFingerprint(command);
        if (descriptor is not null)
        {
            var operation = CreateOperationTransitionSnapshot(
                descriptor,
                descriptor.State,
                descriptor.State,
                stateApplied: false);
            var generation = Math.Max(1, descriptor.OperationGeneration);
            var receipt = new FileTransferCoordinatorReceiptTransitionSnapshot(
                "attempted_decision",
                $"{descriptor.AuthorityId}:{decisionKind}",
                generation,
                commandFingerprint,
                $"{descriptor.AuthorityId}:{decisionKind}:decision",
                generation,
                decisionStatus,
                decisionReason,
                CalculateSnapshotFingerprint(new
                {
                    decisionKind,
                    decisionStatus,
                    decisionReason,
                    command,
                }),
                true);
            return ImmutableArray.Create(operation with
            {
                OperationRole = "attempted_decision",
                OperationKey = CreateCoordinatorOperationKey(
                    "fallback_tail_yield",
                    "attempted_decision",
                    descriptor.AuthorityId,
                    generation,
                    decisionKind),
                OperationKind = decisionKind,
                CommandIdentity = receipt.CommandIdentity,
                CommandGeneration = receipt.CommandGeneration,
                CommandFingerprint = receipt.CommandFingerprint,
                ReceiptIdentity = receipt.ReceiptIdentity,
                ReceiptGeneration = receipt.ReceiptGeneration,
                ReceiptStatus = receipt.ReceiptStatus,
                ReceiptFingerprint = receipt.ReceiptFingerprint,
                ReceiptValidated = true,
                FallbackTailYield = operation.FallbackTailYield is null
                    ? null
                    : operation.FallbackTailYield with
                    {
                        Role = "attempted_decision",
                        Receipts = ImmutableArray.Create(receipt),
                    },
            });
        }

        var fallback = state.PersistentState.FallbackTailYield;
        var generationFallback = Math.Max(
            1,
            fallback.LastYieldGeneration + 1);
        var syntheticAuthority = authorityId is { IsDefault: false } exact
            ? exact.ToString()
            : $"attempt:{commandFingerprint}";
        var boundary = CreateStateBoundarySnapshot(state);
        var receiptFallback =
            new FileTransferCoordinatorReceiptTransitionSnapshot(
                "attempted_decision",
                $"{syntheticAuthority}:{decisionKind}",
                generationFallback,
                commandFingerprint,
                $"{syntheticAuthority}:{decisionKind}:decision",
                generationFallback,
                decisionStatus,
                decisionReason,
                CalculateSnapshotFingerprint(new
                {
                    decisionKind,
                    decisionStatus,
                    decisionReason,
                    command,
                }),
                true);
        return ImmutableArray.Create(
            new FileTransferCoordinatorOperationTransitionSnapshot(
                "fallback_tail_yield",
                "attempted_decision",
                CreateCoordinatorOperationKey(
                    "fallback_tail_yield",
                    "attempted_decision",
                    syntheticAuthority,
                    generationFallback,
                    decisionKind),
                decisionKind,
                syntheticAuthority,
                "(none)",
                generationFallback,
                "None",
                "None",
                receiptFallback.CommandIdentity,
                receiptFallback.CommandGeneration,
                receiptFallback.CommandFingerprint,
                receiptFallback.ReceiptIdentity,
                receiptFallback.ReceiptGeneration,
                receiptFallback.ReceiptStatus,
                receiptFallback.ReceiptFingerprint,
                true,
                false,
                null,
                null,
                null,
                null,
                new FileTransferCoordinatorFallbackTailYieldOperationSnapshot(
                    "attempted_decision",
                    boundary,
                    boundary,
                    boundary,
                    "Attempt",
                    decisionStatus,
                    "(none)",
                    "(none)",
                    "(none)",
                    "(none)",
                    "(none)",
                    0,
                    "(none)",
                    "(none)",
                    0,
                    "(none)",
                    0,
                    "(none)",
                    "(none)",
                    false,
                    0,
                    boundary.TransportEpoch,
                    boundary.TransportEpoch,
                    0,
                    "(none)",
                    0,
                    0,
                    0,
                    decisionReason,
                    ImmutableArray.Create(receiptFallback))));
    }

    internal static FileTransferReceiveRecoveryDecision
        FinalizeReceiveRecoverySnapshot(
            FileTransferCoordinatorState before,
            FileTransferReceiveRecoveryDecision decision,
            string decisionKind,
            object? command = null,
            FileTransferReceiveRecoveryOperationAuthority? authority = null)
    {
        var snapshot = CreateReceiveRecoveryTransitionSnapshot(
            before,
            decision.State,
            decisionKind,
            decision.Status.ToString(),
            decision.Reason,
            command,
            authority ?? decision.Authority);
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    internal static
        RuntimeUnlockInitialActivationReceiveRecoveryHoldCoordinatorDecision
        FinalizeReceiveRecoverySnapshot(
            FileTransferCoordinatorState before,
            RuntimeUnlockInitialActivationReceiveRecoveryHoldCoordinatorDecision
                decision,
            string decisionKind,
            object? command = null)
    {
        var authority = decision.Decision.ReceiveRecoveryOperation?.Authority;
        var snapshot = CreateReceiveRecoveryTransitionSnapshot(
            before,
            decision.State,
            decisionKind,
            decision.Decision.Status.ToString(),
            decision.Decision.Reason,
            command ?? decision.Decision.Request,
            authority);
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    internal static
        RuntimeUnlockInitialActivationOfferEnvelopeRefreshCoordinatorDecision
        FinalizeReceiveRecoverySnapshot(
            FileTransferCoordinatorState before,
            RuntimeUnlockInitialActivationOfferEnvelopeRefreshCoordinatorDecision
                decision,
            string decisionKind,
            object? command = null)
    {
        var snapshot = CreateReceiveRecoveryTransitionSnapshot(
            before,
            decision.State,
            decisionKind,
            decision.Decision.Status.ToString(),
            decision.Decision.Reason,
            command ?? decision.Decision.Receipt.Command,
            decision.Decision.Receipt.Command.ReceiveRecoveryAuthority);
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    private static FileTransferCoordinatorSnapshotResult
        CreateReceiveRecoveryTransitionSnapshot(
            FileTransferCoordinatorState before,
            FileTransferCoordinatorState after,
            string decisionKind,
            string decisionStatus,
            string decisionReason,
            object? command,
            FileTransferReceiveRecoveryOperationAuthority? authority)
    {
        var coordinatorInstanceId =
            before.PersistentState.CoordinatorInstanceId;
        if (string.IsNullOrWhiteSpace(coordinatorInstanceId))
        {
            return new FileTransferCoordinatorSnapshotResult(after, null!);
        }

        return CreateTransitionSnapshot(
            before,
            after,
            coordinatorInstanceId,
            before.PersistentState.SnapshotStateGeneration,
            decisionKind,
            decisionStatus,
            decisionReason,
            CreateReceiveRecoveryAttemptedDecisionSnapshots(
                before,
                decisionKind,
                decisionStatus,
                decisionReason,
                command,
                authority));
    }

    internal static FileTransferCoordinatorSnapshotResult
        CreateReceiveRecoveryAttemptSnapshot(
            FileTransferCoordinatorState state,
            string decisionKind,
            string decisionStatus,
            string decisionReason,
            object? command,
            FileTransferReceiveRecoveryOperationAuthority? authority)
        => CreateReceiveRecoveryTransitionSnapshot(
            state,
            state,
            decisionKind,
            decisionStatus,
            decisionReason,
            command,
            authority);

    private static ImmutableArray<
        FileTransferCoordinatorOperationTransitionSnapshot>
        CreateReceiveRecoveryAttemptedDecisionSnapshots(
            FileTransferCoordinatorState state,
            string decisionKind,
            string decisionStatus,
            string decisionReason,
            object? command,
            FileTransferReceiveRecoveryOperationAuthority? authority)
    {
        var descriptorBuilder =
            ImmutableArray.CreateBuilder<CoordinatorOperationDescriptor>();
        AddReceiveRecoveryOperationDescriptors(state, descriptorBuilder);
        IEnumerable<CoordinatorOperationDescriptor> descriptors =
            descriptorBuilder;
        if (authority is { IsDefault: false } exactAuthority)
        {
            descriptors = descriptors.Where(descriptor => string.Equals(
                descriptor.AuthorityId,
                exactAuthority.AuthorityId.ToString(),
                StringComparison.Ordinal));
        }
        var descriptor = descriptors
            .OrderBy(descriptor => descriptor.OperationRole, StringComparer.Ordinal)
            .FirstOrDefault();
        var commandFingerprint = CalculateSnapshotFingerprint(command);
        if (descriptor is not null)
        {
            var operation = CreateOperationTransitionSnapshot(
                descriptor,
                descriptor.State,
                descriptor.State,
                stateApplied: false);
            var decisionReceipt =
                new FileTransferCoordinatorReceiptTransitionSnapshot(
                    "attempted_decision",
                    $"{descriptor.AuthorityId}:{decisionKind}",
                    Math.Max(1, descriptor.OperationGeneration),
                    commandFingerprint,
                    $"{descriptor.AuthorityId}:{decisionKind}:decision",
                    Math.Max(1, descriptor.OperationGeneration),
                    decisionStatus,
                    decisionReason,
                    CalculateSnapshotFingerprint(new
                    {
                        decisionKind,
                        decisionStatus,
                        decisionReason,
                        command,
                    }),
                    true);
            return ImmutableArray.Create(operation with
            {
                CommandIdentity = decisionReceipt.CommandIdentity,
                CommandGeneration = decisionReceipt.CommandGeneration,
                CommandFingerprint = decisionReceipt.CommandFingerprint,
                ReceiptIdentity = decisionReceipt.ReceiptIdentity,
                ReceiptGeneration = decisionReceipt.ReceiptGeneration,
                ReceiptStatus = decisionReceipt.ReceiptStatus,
                ReceiptFingerprint = decisionReceipt.ReceiptFingerprint,
                ReceiptValidated = true,
                ReceiveRecovery = operation.ReceiveRecovery is null
                    ? null
                    : operation.ReceiveRecovery with
                    {
                        Receipts = ImmutableArray.Create(decisionReceipt),
                    },
            });
        }

        var generation = Math.Max(
            1,
            authority?.OperationGeneration ??
            state.PersistentState.FileTransferReceiveRecovery
                .LastOperationGeneration + 1);
        var syntheticAuthority = authority is { IsDefault: false } exact
            ? exact.AuthorityId.ToString()
            : $"attempt:{commandFingerprint}";
        var boundary = authority is { IsDefault: false } scoped
            ? CreateReceiveRecoverySourceBoundary(state, scoped.Observation)
            : CreateStateBoundarySnapshot(state);
        var receipt = new FileTransferCoordinatorReceiptTransitionSnapshot(
            "attempted_decision",
            $"{syntheticAuthority}:{decisionKind}",
            generation,
            commandFingerprint,
            $"{syntheticAuthority}:{decisionKind}:decision",
            generation,
            decisionStatus,
            decisionReason,
            CalculateSnapshotFingerprint(new
            {
                decisionKind,
                decisionStatus,
                decisionReason,
                command,
            }),
            true);
        return ImmutableArray.Create(
            new FileTransferCoordinatorOperationTransitionSnapshot(
                "receive_recovery",
                "attempted_decision",
                CreateCoordinatorOperationKey(
                    "receive_recovery",
                    "attempted_decision",
                    syntheticAuthority,
                    generation,
                    decisionKind),
                decisionKind,
                syntheticAuthority,
                authority?.PredecessorAuthorityId?.ToString() ?? "(none)",
                generation,
                "None",
                "None",
                receipt.CommandIdentity,
                receipt.CommandGeneration,
                receipt.CommandFingerprint,
                receipt.ReceiptIdentity,
                receipt.ReceiptGeneration,
                receipt.ReceiptStatus,
                receipt.ReceiptFingerprint,
                true,
                false,
                null,
                null,
                null,
                new FileTransferCoordinatorReceiveRecoveryOperationSnapshot(
                    "attempted_decision",
                    boundary,
                    boundary,
                    authority?.BridgeRecoveryDecision.RecoveryKind.ToString() ??
                        "Unknown",
                    authority?.BridgeRecoveryDecision.BridgeDecisionGeneration ?? 0,
                    authority?.RecoveryLivenessLease?.LeaseGeneration ?? 0,
                    "None",
                    "None",
                    0,
                    "(none)",
                    0,
                    "(none)",
                    "(none)",
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    ImmutableArray.Create(receipt))));
    }

    internal static RuntimeUnlockRetryLifecycleTransitionDecision
        FinalizeRuntimeUnlockRetrySnapshot(
            FileTransferCoordinatorState before,
            RuntimeUnlockRetryLifecycleTransitionDecision decision,
            string decisionKind,
            object? command = null,
            FileTransferAuthorityId? authorityId = null)
    {
        var snapshot = CreateRuntimeUnlockRetryTransitionSnapshot(
            before,
            decision.State,
            decisionKind,
            decision.Status.ToString(),
            decision.Reason,
            command,
            authorityId);
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    internal static RuntimeUnlockRetryLifecycleInstallReceiptDecision
        FinalizeRuntimeUnlockRetrySnapshot(
            FileTransferCoordinatorState before,
            RuntimeUnlockRetryLifecycleInstallReceiptDecision decision,
            string decisionKind,
            object? command = null,
            FileTransferAuthorityId? authorityId = null)
    {
        var snapshot = CreateRuntimeUnlockRetryTransitionSnapshot(
            before,
            decision.State,
            decisionKind,
            decision.Status.ToString(),
            decision.Reason,
            command,
            authorityId ?? decision.Receipt?.Authority.AuthorityId);
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    internal static RuntimeUnlockRetryLifecycleDeadlineDecision
        FinalizeRuntimeUnlockRetrySnapshot(
            FileTransferCoordinatorState before,
            RuntimeUnlockRetryLifecycleDeadlineDecision decision,
            string decisionKind,
            object? command = null,
            FileTransferAuthorityId? authorityId = null)
    {
        var snapshot = CreateRuntimeUnlockRetryTransitionSnapshot(
            before,
            decision.State,
            decisionKind,
            decision.Status.ToString(),
            decision.Reason,
            command,
            authorityId ?? decision.Settlement?.Authority.AuthorityId);
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    internal static RuntimeUnlockRetryLifecycleScopeInvalidationDecision
        FinalizeRuntimeUnlockRetrySnapshot(
            FileTransferCoordinatorState before,
            RuntimeUnlockRetryLifecycleScopeInvalidationDecision decision,
            string decisionKind,
            object? command = null,
            FileTransferAuthorityId? authorityId = null)
    {
        var snapshot = CreateRuntimeUnlockRetryTransitionSnapshot(
            before,
            decision.State,
            decisionKind,
            decision.Status.ToString(),
            decision.Reason,
            command ?? decision.Command,
            authorityId);
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    internal static RuntimeUnlockRetryDispatchSuccessorDecision
        FinalizeRuntimeUnlockRetrySnapshot(
            FileTransferCoordinatorState before,
            RuntimeUnlockRetryDispatchSuccessorDecision decision,
            string decisionKind)
    {
        var snapshot = CreateRuntimeUnlockRetryTransitionSnapshot(
            before,
            decision.State,
            decisionKind,
            decision.Status.ToString(),
            decision.Reason,
            decision.Evaluation,
            decision.Evaluation.PredecessorAuthority.AuthorityId);
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    internal static RuntimeUnlockRetryDispatchDecision
        FinalizeRuntimeUnlockRetrySnapshot(
            FileTransferCoordinatorState before,
            RuntimeUnlockRetryDispatchDecision decision,
            string decisionKind,
            object? command = null)
    {
        var snapshot = CreateRuntimeUnlockRetryTransitionSnapshot(
            before,
            decision.State,
            decisionKind,
            decision.Status.ToString(),
            decision.Reason,
            command ?? decision.Request,
            decision.Contract?.Authority.AuthorityId ??
            decision.Request.PredecessorAuthorityId);
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    internal static RuntimeUnlockRetryDispatchSettlementDecision
        FinalizeRuntimeUnlockRetrySnapshot(
            FileTransferCoordinatorState before,
            RuntimeUnlockRetryDispatchSettlementDecision decision,
            string decisionKind)
    {
        var snapshot = CreateRuntimeUnlockRetryTransitionSnapshot(
            before,
            decision.State,
            decisionKind,
            decision.Status.ToString(),
            decision.Reason,
            decision.Report,
            decision.Report.Authority.AuthorityId);
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    private static FileTransferCoordinatorSnapshotResult
        CreateRuntimeUnlockRetryTransitionSnapshot(
            FileTransferCoordinatorState before,
            FileTransferCoordinatorState after,
            string decisionKind,
            string decisionStatus,
            string decisionReason,
            object? command,
            FileTransferAuthorityId? authorityId)
    {
        var coordinatorInstanceId =
            before.PersistentState.CoordinatorInstanceId;
        if (string.IsNullOrWhiteSpace(coordinatorInstanceId))
        {
            return new FileTransferCoordinatorSnapshotResult(
                after,
                null!);
        }

        return CreateTransitionSnapshot(
            before,
            after,
            coordinatorInstanceId,
            before.PersistentState.SnapshotStateGeneration,
            decisionKind,
            decisionStatus,
            decisionReason,
            CreateRuntimeUnlockRetryAttemptedDecisionSnapshots(
                before,
                decisionKind,
                decisionStatus,
                decisionReason,
                command,
                authorityId));
    }

    private static ImmutableArray<
        FileTransferCoordinatorOperationTransitionSnapshot>
        CreateRuntimeUnlockRetryAttemptedDecisionSnapshots(
            FileTransferCoordinatorState state,
            string decisionKind,
            string decisionStatus,
            string decisionReason,
            object? command,
            FileTransferAuthorityId? authorityId)
    {
        var descriptorBuilder =
            ImmutableArray.CreateBuilder<CoordinatorOperationDescriptor>();
        AddRuntimeUnlockRetryOperationDescriptors(state, descriptorBuilder);
        IEnumerable<CoordinatorOperationDescriptor> descriptors =
            descriptorBuilder;
        var invalidationCommand = command switch
        {
            RuntimeUnlockRetryDispatchScopeInvalidationCommand exactCommand =>
                exactCommand,
            RuntimeUnlockRetryDispatchScopeInvalidationReceipt exactReceipt =>
                exactReceipt.Command,
            _ => null,
        };
        if (invalidationCommand is { IsDefault: false } exactInvalidation)
        {
            var invalidationOperationKey = CreateCoordinatorOperationKey(
                "runtime_unlock_retry_lifecycle",
                "scope_invalidation",
                exactInvalidation.CommandId.ToString(),
                exactInvalidation.InvalidationGeneration,
                "ScopeInvalidation");
            descriptors = descriptors.Where(descriptor => string.Equals(
                descriptor.OperationKey,
                invalidationOperationKey,
                StringComparison.Ordinal));
        }
        else if (authorityId is { IsDefault: false } exactAuthorityId)
        {
            descriptors = descriptors.Where(descriptor => string.Equals(
                descriptor.AuthorityId,
                exactAuthorityId.ToString(),
                StringComparison.Ordinal));
        }
        else
        {
            descriptors = Enumerable.Empty<CoordinatorOperationDescriptor>();
        }

        var descriptor = descriptors
            .OrderBy(descriptor => descriptor.OperationRole, StringComparer.Ordinal)
            .FirstOrDefault();
        var commandFingerprint = CalculateSnapshotFingerprint(command);
        if (descriptor is not null)
        {
            var operation = CreateOperationTransitionSnapshot(
                descriptor,
                descriptor.State,
                descriptor.State,
                stateApplied: false);
            return ImmutableArray.Create(operation with
            {
                CommandIdentity =
                    $"{descriptor.AuthorityId}:{decisionKind}",
                CommandFingerprint = commandFingerprint,
                ReceiptIdentity =
                    $"{descriptor.AuthorityId}:{decisionKind}:decision",
                ReceiptStatus = decisionStatus,
                ReceiptFingerprint = CalculateSnapshotFingerprint(new
                {
                    decisionKind,
                    decisionStatus,
                    decisionReason,
                    command,
                }),
                ReceiptValidated = true,
            });
        }

        var commandGeneration = command switch
        {
            RuntimeUnlockRetryDispatchAuthorityRequest request =>
                request.DispatchAttemptGeneration,
            RuntimeUnlockRetryDispatchSuccessorEvaluation evaluation =>
                evaluation.PredecessorAuthority.DispatchAttemptGeneration,
            RuntimeUnlockRetryDispatchScopeInvalidationCommand invalidation =>
                invalidation.InvalidationGeneration,
            RuntimeUnlockRetryDispatchScopeInvalidationReceipt invalidation =>
                invalidation.Command?.InvalidationGeneration ?? 0,
            _ => Math.Max(
                1,
                state.PersistentState.RuntimeUnlockRetryLifecycle
                    .LastAuthorityGeneration + 1),
        };
        commandGeneration = Math.Max(1, commandGeneration);
        var syntheticAuthority =
            invalidationCommand is { IsDefault: false } exactInvalidationCommand
                ? exactInvalidationCommand.CommandId.ToString()
                : authorityId is { IsDefault: false } exact
                ? exact.ToString()
                : $"attempt:{commandFingerprint}";
        var decisionReceipt =
            new FileTransferCoordinatorReceiptTransitionSnapshot(
                "decision",
                $"{syntheticAuthority}:{decisionKind}",
                commandGeneration,
                commandFingerprint,
                $"{syntheticAuthority}:{decisionKind}:decision",
                commandGeneration,
                decisionStatus,
                decisionReason,
                CalculateSnapshotFingerprint(new
                {
                    decisionKind,
                    decisionStatus,
                    decisionReason,
                    command,
                }),
                true);
        return ImmutableArray.Create(
            new FileTransferCoordinatorOperationTransitionSnapshot(
                "runtime_unlock_retry_lifecycle",
                "attempted_decision",
                CreateCoordinatorOperationKey(
                    "runtime_unlock_retry_lifecycle",
                    "attempted_decision",
                    syntheticAuthority,
                    commandGeneration,
                    decisionKind),
                decisionKind,
                syntheticAuthority,
                "(none)",
                commandGeneration,
                "None",
                "None",
                decisionReceipt.CommandIdentity,
                decisionReceipt.CommandGeneration,
                decisionReceipt.CommandFingerprint,
                decisionReceipt.ReceiptIdentity,
                decisionReceipt.ReceiptGeneration,
                decisionReceipt.ReceiptStatus,
                decisionReceipt.ReceiptFingerprint,
                true,
                false,
                null,
                null,
                new FileTransferCoordinatorRuntimeUnlockRetryOperationSnapshot(
                    "attempted_decision",
                    CreateStateBoundarySnapshot(state),
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    CalculateSnapshotFingerprint(null),
                    0,
                    false,
                    RuntimeUnlockRetryReleaseKind.Unknown.ToString(),
                    "(none)",
                    0,
                    "(none)",
                    RuntimeUnlockRetryDispatchListenerRearmRequirement.None
                        .ToString(),
                    RuntimeUnlockRetryDispatchListenerExecutionMode.Default
                        .ToString(),
                    false,
                    "(none)",
                    ImmutableArray.Create(decisionReceipt))));
    }

    internal static FileTransferCoordinatorSnapshotResult CreateTransitionSnapshot(
        FileTransferCoordinatorState before,
        FileTransferCoordinatorState after,
        string coordinatorInstanceId,
        long beforeStateGeneration,
        string decisionKind,
        string decisionStatus,
        string decisionReason,
        ImmutableArray<FileTransferCoordinatorOperationTransitionSnapshot>
            attemptedOperations = default,
        FileTransferCoordinatorSnapshotSelectionMode selectionMode =
            FileTransferCoordinatorSnapshotSelectionMode.Authoritative,
        bool suppliedOperationsAreComplete = false)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (string.IsNullOrWhiteSpace(coordinatorInstanceId))
        {
            throw new ArgumentException(
                "Coordinator instance identity is required.",
                nameof(coordinatorInstanceId));
        }
        if ((!string.IsNullOrWhiteSpace(
                 before.PersistentState.CoordinatorInstanceId) &&
             !string.Equals(
                 before.PersistentState.CoordinatorInstanceId,
                 coordinatorInstanceId,
                 StringComparison.Ordinal)) ||
            (!string.IsNullOrWhiteSpace(
                 after.PersistentState.CoordinatorInstanceId) &&
             !string.Equals(
                 after.PersistentState.CoordinatorInstanceId,
                 coordinatorInstanceId,
                 StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Coordinator snapshot instance identity changed.");
        }

        var beforeBoundary = CreateStateBoundarySnapshot(before);
        var afterBoundary = CreateStateBoundarySnapshot(after);
        var operations = suppliedOperationsAreComplete
            ? (attemptedOperations.IsDefault
                ? ImmutableArray<
                    FileTransferCoordinatorOperationTransitionSnapshot>.Empty
                : attemptedOperations)
            : ReferenceEquals(before, after)
            ? ImmutableArray<
                FileTransferCoordinatorOperationTransitionSnapshot>.Empty
            : CreateOperationTransitionSnapshots(before, after);
        var beforeFingerprint = string.IsNullOrWhiteSpace(
            before.PersistentState.SnapshotStateFingerprint)
            ? CalculateStateFingerprint(before)
            : before.PersistentState.SnapshotStateFingerprint!;
        var stateApplied = operations.Any(operation => operation.StateApplied) ||
            beforeBoundary != afterBoundary ||
            (!ReferenceEquals(before, after) &&
             !string.Equals(
                 CalculateStateFingerprint(before),
                 CalculateStateFingerprint(after),
                 StringComparison.Ordinal));
        var afterFingerprint = stateApplied
            ? CalculateSnapshotFingerprint(new
            {
                PreviousStateFingerprint = beforeFingerprint,
                Boundary = afterBoundary,
                Operations = operations.Where(operation =>
                    operation.StateApplied).ToImmutableArray(),
            })
            : beforeFingerprint;
        if (!suppliedOperationsAreComplete &&
            !attemptedOperations.IsDefaultOrEmpty)
        {
            var changedKeys = operations
                .Select(operation => operation.OperationKey)
                .ToHashSet(StringComparer.Ordinal);
            operations = operations.AddRange(
                attemptedOperations.Where(operation =>
                    !changedKeys.Contains(operation.OperationKey)));
        }
        var changedFamilies = operations
            .Select(operation => operation.Family)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var normalizedStatus = NormalizeSnapshotToken(decisionStatus, "Unknown");
        if (stateApplied &&
            normalizedStatus is "Rejected" or "Idempotent")
        {
            throw new InvalidOperationException(
                $"Coordinator snapshot {normalizedStatus} decision mutated state.");
        }
        var afterStateGeneration = stateApplied
            ? checked(beforeStateGeneration + 1)
            : beforeStateGeneration;
        var stampedState = stateApplied
            ? after with
            {
                PersistentState = after.PersistentState with
                {
                    CoordinatorInstanceId = coordinatorInstanceId,
                    SnapshotStateGeneration = afterStateGeneration,
                    SnapshotStateFingerprint = afterFingerprint,
                },
            }
            : after;
        if (!stateApplied &&
            string.Equals(normalizedStatus, "Accepted", StringComparison.Ordinal))
        {
            normalizedStatus = "Idempotent";
        }
        var normalizedReason = NormalizeSnapshotToken(
            decisionReason,
            "file_transfer_coordinator");
        var decisionUtcMilliseconds = GetNextSnapshotDecisionUtcMilliseconds();
        var transitionIdentity = string.Join(
            "|",
            coordinatorInstanceId,
            beforeStateGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            afterStateGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            NormalizeSnapshotToken(decisionKind, "Unknown"),
            normalizedStatus,
            normalizedReason,
            decisionUtcMilliseconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            beforeFingerprint,
            afterFingerprint);
        var transitionId = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(transitionIdentity)))
            .ToLowerInvariant();
        var aggregate = new FileTransferCoordinatorAggregateTransitionSnapshot(
            beforeStateGeneration,
            afterStateGeneration,
            beforeFingerprint,
            afterFingerprint,
            beforeBoundary,
            afterBoundary,
            changedFamilies,
            operations);
        var envelope = new FileTransferCoordinatorTransitionEnvelope(
            FileTransferCoordinatorTransitionEnvelope.CurrentContractVersion,
            selectionMode == FileTransferCoordinatorSnapshotSelectionMode.Authoritative
                ? "SnapshotV1Authoritative"
                : "SnapshotV1Shadow",
            coordinatorInstanceId,
            stampedState.SessionId,
            stampedState.TransferId,
            stampedState.Direction.ToString().ToLowerInvariant(),
            decisionUtcMilliseconds,
            transitionId,
            NormalizeSnapshotToken(decisionKind, "Unknown"),
            normalizedStatus,
            normalizedReason,
            aggregate);

        return new FileTransferCoordinatorSnapshotResult(stampedState, envelope);
    }

    private static long GetNextSnapshotDecisionUtcMilliseconds()
    {
        while (true)
        {
            var current = Volatile.Read(
                ref lastSnapshotDecisionUtcMilliseconds);
            var candidate = Math.Max(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                current + 1L);
            if (Interlocked.CompareExchange(
                    ref lastSnapshotDecisionUtcMilliseconds,
                    candidate,
                    current) == current)
            {
                return candidate;
            }
        }
    }

    internal static FileTransferCoordinatorSnapshotResult CreateTransitionSnapshot(
        FileTransferCoordinatorState before,
        FileTransferCoordinatorDecision decision,
        string coordinatorInstanceId,
        FileTransferCoordinatorSnapshotSelectionMode selectionMode =
            FileTransferCoordinatorSnapshotSelectionMode.Authoritative)
        => CreateTransitionSnapshot(
            before,
            decision.State,
            coordinatorInstanceId,
            before.PersistentState.SnapshotStateGeneration,
            "CoordinatorDecision",
            decision.TerminalMutationRejected ||
            !string.IsNullOrWhiteSpace(decision.MutationRejectedReason) ||
            !string.IsNullOrWhiteSpace(
                decision.RuntimeUnlockCommitRejectedReason)
                ? "Rejected"
                : "Accepted",
            decision.MutationRejectedReason ??
            decision.RuntimeUnlockCommitRejectedReason ??
            decision.Reason,
            selectionMode: selectionMode);

    internal static FileTransferCoordinatorSnapshotResult CreateTransitionSnapshot(
        FileTransferCoordinatorState before,
        FileTransferCoordinatorDecision decision,
        FileTransferCoordinatorEvent coordinatorEvent,
        string coordinatorInstanceId,
        FileTransferCoordinatorSnapshotSelectionMode selectionMode =
            FileTransferCoordinatorSnapshotSelectionMode.Authoritative)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var rejected = decision.TerminalMutationRejected ||
                       !string.IsNullOrWhiteSpace(decision.MutationRejectedReason) ||
                       !string.IsNullOrWhiteSpace(
                           decision.RuntimeUnlockCommitRejectedReason);
        return CreateTransitionSnapshot(
            before,
            decision.State,
            coordinatorInstanceId,
            before.PersistentState.SnapshotStateGeneration,
            "CoordinatorDecision",
            rejected ? "Rejected" : "Accepted",
            decision.MutationRejectedReason ??
            decision.RuntimeUnlockCommitRejectedReason ??
            decision.Reason,
            CreateRouteRuntimeAttemptSnapshots(
                before,
                decision.State,
                coordinatorEvent),
            selectionMode: selectionMode);
    }

    internal static FileTransferCoordinatorSnapshotResult CreateTransitionSnapshot(
        FileTransferCoordinatorState before,
        FileTransferCoordinatorActionDecision decision,
        string coordinatorInstanceId,
        FileTransferCoordinatorSnapshotSelectionMode selectionMode =
            FileTransferCoordinatorSnapshotSelectionMode.Authoritative)
        => CreateTransitionSnapshot(
            before,
            decision.State,
            coordinatorInstanceId,
            before.PersistentState.SnapshotStateGeneration,
            decision.Kind.ToString(),
            decision.Accepted ? "Accepted" : "Rejected",
            decision.RejectionReason,
            selectionMode: selectionMode);

    internal static FileTransferCoordinatorSnapshotResult CreateTransitionSnapshot(
        FileTransferCoordinatorState before,
        FileTransferCoordinatorActionDecision decision,
        FileTransferCoordinatorActionRequest request,
        string coordinatorInstanceId,
        FileTransferCoordinatorSnapshotSelectionMode selectionMode =
            FileTransferCoordinatorSnapshotSelectionMode.Authoritative)
        => CreateTransitionSnapshot(
            before,
            decision.State,
            coordinatorInstanceId,
            before.PersistentState.SnapshotStateGeneration,
            decision.Kind.ToString(),
            decision.Accepted ? "Accepted" : "Rejected",
            decision.RejectionReason,
            CreateRouteRuntimeAttemptSnapshots(before, decision, request),
            selectionMode: selectionMode);

    private static ImmutableArray<
        FileTransferCoordinatorOperationTransitionSnapshot>
        CreateRouteRuntimeAttemptSnapshots(
            FileTransferCoordinatorState before,
            FileTransferCoordinatorState after,
            FileTransferCoordinatorEvent coordinatorEvent)
    {
        string role;
        long generation;
        string authorityId;
        switch (coordinatorEvent.Kind)
        {
            case FileTransferCoordinatorEventKind.StartTransfer:
                role = "runtime_owner";
                generation = after.CurrentRuntimeOwner?.RuntimeGeneration ??
                    Math.Max(1, before.LastRuntimeOwnerGeneration + 1);
                authorityId = $"runtime:{generation}";
                break;
            case FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested:
                role = "route_commit";
                generation = after.CurrentRouteCommit?.CommitGeneration ??
                    Math.Max(1, before.LastRouteCommitGeneration + 1);
                authorityId = $"commit:{generation}";
                break;
            default:
                return ImmutableArray<
                    FileTransferCoordinatorOperationTransitionSnapshot>.Empty;
        }

        return ImmutableArray.Create(CreateRouteRuntimeAttemptSnapshot(
            before,
            coordinatorEvent.Kind.ToString(),
            role,
            authorityId,
            generation,
            CalculateSnapshotFingerprint(coordinatorEvent)));
    }

    private static ImmutableArray<
        FileTransferCoordinatorOperationTransitionSnapshot>
        CreateRouteRuntimeAttemptSnapshots(
            FileTransferCoordinatorState before,
            FileTransferCoordinatorActionDecision decision,
            FileTransferCoordinatorActionRequest request)
    {
        if (!IsRoutePublicationAuthorityAction(request.Kind))
        {
            return ImmutableArray<
                FileTransferCoordinatorOperationTransitionSnapshot>.Empty;
        }

        var generation = decision.RoutePublicationToken?.PublicationGeneration ??
            Math.Max(1, before.LastRoutePublicationGeneration + 1);
        return ImmutableArray.Create(CreateRouteRuntimeAttemptSnapshot(
            before,
            request.Kind.ToString(),
            "route_publication",
            $"publication:{generation}",
            generation,
            CalculateSnapshotFingerprint(request)));
    }

    private static FileTransferCoordinatorOperationTransitionSnapshot
        CreateRouteRuntimeAttemptSnapshot(
            FileTransferCoordinatorState state,
            string operationKind,
            string role,
            string authorityId,
            long generation,
            string commandFingerprint)
    {
        var boundary = CreateStateBoundarySnapshot(state);
        return new FileTransferCoordinatorOperationTransitionSnapshot(
            "route_runtime",
            role,
            CreateCoordinatorOperationKey(
                "route_runtime",
                role,
                authorityId,
                generation,
                operationKind),
            operationKind,
            authorityId,
            "(none)",
            generation,
            "Attempted",
            "Attempted",
            authorityId,
            generation,
            commandFingerprint,
            "(none)",
            0,
            "(none)",
            "(none)",
            false,
            false,
            new(role, boundary, commandFingerprint),
            null);
    }

    internal static FileTransferRouteTransitionDecision
        FinalizeRouteTransitionDecisionSnapshot(
            FileTransferCoordinatorState before,
            FileTransferRouteTransitionDecision decision,
            string operationKind,
            object attemptedCommand,
            FileTransferRouteTransitionAuthority? attemptedAuthority = null,
            object? attemptedReceipt = null,
            string receiptStatus = "(none)",
            string receiptReason = "(none)",
            string operationRole = "attempt")
    {
        operationRole = NormalizeSnapshotToken(operationRole, "attempt")
            .ToLowerInvariant();
        var coordinatorInstanceId = before.PersistentState.CoordinatorInstanceId;
        if (string.IsNullOrWhiteSpace(coordinatorInstanceId))
        {
            return decision;
        }

        var commandFingerprint = CalculateSnapshotFingerprint(attemptedCommand);
        var authority = attemptedAuthority ?? decision.Authority;
        var authorityId = authority?.AuthorityId.ToString() ??
                          $"attempt:{commandFingerprint[..16]}";
        var operationGeneration = authority?.TransitionGeneration ??
            Math.Max(
                1,
                before.PersistentState.FileTransferRouteTransition
                    .LastTransitionGeneration + 1);
        var operationKey = CreateCoordinatorOperationKey(
            "route_transition",
            operationRole,
            authorityId,
            operationGeneration,
            authority?.Kind.ToString() ?? operationKind);
        var sourceBoundary = authority is { } exactAuthority
            ? CreateRouteTransitionSourceBoundary(before, exactAuthority)
            : CreateStateBoundarySnapshot(before);
        var targetRoute = authority is { } targetAuthority
            ? FileTransferRouteResolver.Resolve(targetAuthority.TargetRoute)
                .RuntimeDescriptor.TelemetryToken
            : before.RouteSelection.RuntimeDescriptor.TelemetryToken;
        var targetProtocol = authority?.TargetProtocolVersion ??
                             before.RouteSelection.ProtocolVersion;
        var targetTransport = authority?.TargetTransport.ToString() ??
                              GetSnapshotTransport(before.RouteSelection.Route)
                                  .ToString();
        var receiptIdentity = attemptedReceipt is null
            ? "(none)"
            : $"{authorityId}:{operationKind}";
        var attemptedOperation =
            new FileTransferCoordinatorOperationTransitionSnapshot(
                "route_transition",
                operationRole,
                operationKey,
                operationKind,
                authorityId,
                authority?.PredecessorTransitionAuthorityId?.ToString() ??
                    "(none)",
                operationGeneration,
                authority is null ? "Unknown" : "Current",
                decision.State.PersistentState.FileTransferRouteTransition
                    .CurrentOperation?.State.ToString() ??
                    decision.State.PersistentState.FileTransferRouteTransition
                        .LastTerminalOperation?.State.ToString() ??
                    "None",
                $"{authorityId}:{operationKind}",
                operationGeneration,
                commandFingerprint,
                receiptIdentity,
                attemptedReceipt is null ? 0 : operationGeneration,
                attemptedReceipt is null ? "(none)" : receiptStatus,
                CalculateSnapshotFingerprint(attemptedReceipt),
                attemptedReceipt is not null &&
                receiptStatus is not "Rejected" and not "(none)",
                false,
                null,
                new(
                    operationRole,
                    authority?.Kind.ToString() ?? operationKind,
                    sourceBoundary,
                    CreateRouteTransitionTargetBoundary(
                        decision.State,
                        authority),
                    authority?.SourceTransport.ToString() ??
                        GetSnapshotTransport(before.RouteSelection.Route)
                            .ToString(),
                    authority?.ProofBoundary.GetType().Name ?? "Attempt",
                    GetRouteTransitionProofAdmissionMode(
                        authority,
                        decision.State.PersistentState.FileTransferRouteTransition
                            .CurrentOperation),
                    CalculateSnapshotFingerprint(authority?.ProofBoundary),
                    CalculateSnapshotFingerprint(
                        authority?.DurableCheckpoint),
                    targetRoute,
                    targetProtocol,
                    targetTransport,
                    authority?.HandoffKind.ToString() ?? "(none)",
                    decision.DispositionCommand?.Kind.ToString() ?? "(none)",
                    decision.RetainedFrameApplyCommand?.FrameIdentity.FrameType ??
                        "(none)",
                    CalculateSnapshotFingerprint(
                        decision.RetainedFrameApplyCommand?.FrameIdentity),
                    attemptedReceipt is null
                        ? ImmutableArray<
                            FileTransferCoordinatorReceiptTransitionSnapshot>
                            .Empty
                        : ImmutableArray.Create(
                            CreateRouteTransitionReceiptSnapshot(
                                operationKind,
                                $"{authorityId}:{operationKind}",
                                operationGeneration,
                                attemptedCommand,
                                receiptIdentity,
                                operationGeneration,
                                receiptStatus,
                                receiptReason,
                                attemptedReceipt,
                                receiptStatus is not "Rejected" and not
                                    "(none)"))));
        var attemptedOperations = ImmutableArray.Create(attemptedOperation);
        if (authority?.ProofBoundary is
            FileTransferTunaActivationTransitionProofBoundary activation)
        {
            var responderObservation =
                activation.InitialResponderPeerProbeObservation ??
                activation.ResponderPeerProbeHandoffAuthority?.Observation;
            if (responderObservation is not null)
            {
                var responderOperation =
                    CreateResponderProbeHandoffAttemptSnapshot(
                        decision.State,
                        operationKind,
                        decision.Status.ToString(),
                        decision.Reason,
                        responderObservation,
                        activation.ResponderPeerProbeHandoffAuthority);
                const string responderRole =
                    "route_transition_handoff_pending";
                attemptedOperations = attemptedOperations.Add(
                    responderOperation with
                    {
                        OperationRole = responderRole,
                        OperationKey = CreateCoordinatorOperationKey(
                            "responder_probe_handoff",
                            responderRole,
                            responderOperation.AuthorityId,
                            responderOperation.OperationGeneration,
                            operationKind),
                        ResponderProbeHandoff =
                            responderOperation.ResponderProbeHandoff! with
                            {
                                Role = responderRole,
                            },
                    });
            }
        }
        var snapshot = CreateTransitionSnapshot(
            before,
            decision.State,
            coordinatorInstanceId,
            before.PersistentState.SnapshotStateGeneration,
            operationKind,
            decision.Status.ToString(),
            decision.Reason,
            attemptedOperations,
            FileTransferCoordinatorSnapshotSelectionMode.Authoritative);
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    private static FileTransferFallbackV6ProofBootstrapDecision
        FinalizeFallbackV6ProofBootstrapDecisionSnapshot(
            FileTransferCoordinatorState before,
            FileTransferFallbackV6ProofBootstrapDecision decision,
            string decisionKind,
            FileTransferFallbackV6ProofBootstrapObservation observation,
            FileTransferFallbackV6ProofBootstrapReceipt? attemptedReceipt = null)
    {
        var coordinatorInstanceId = before.PersistentState.CoordinatorInstanceId;
        if (string.IsNullOrWhiteSpace(coordinatorInstanceId))
        {
            return decision;
        }

        var transitionState = before.PersistentState.FileTransferRouteTransition;
        var command = decision.Command ??
                      decision.Receipt?.Command ??
                      attemptedReceipt?.Command ??
                      transitionState.PendingFallbackV6ProofBootstrap ??
                      transitionState.LastFallbackV6ProofBootstrapReceipt?.Command;
        FileTransferCoordinatorOperationTransitionSnapshot attemptedOperation;
        if (command is { } exactCommand)
        {
            var descriptor = CreateFallbackV6ProofBootstrapDescriptor(
                decision.State,
                exactCommand,
                decision.Receipt ?? attemptedReceipt);
            attemptedOperation = CreateOperationTransitionSnapshot(
                descriptor,
                descriptor.State,
                descriptor.State,
                stateApplied: false);
        }
        else
        {
            var generation = Math.Max(
                1,
                transitionState.LastFallbackV6ProofBootstrapGeneration + 1);
            var fingerprint = CalculateSnapshotFingerprint(observation);
            var authorityId = $"attempt:{fingerprint[..16]}";
            var boundary = CreateStateBoundarySnapshot(before);
            attemptedOperation =
                new FileTransferCoordinatorOperationTransitionSnapshot(
                    "route_transition",
                    "fallback_v6_bootstrap",
                    CreateCoordinatorOperationKey(
                        "route_transition",
                        "fallback_v6_bootstrap",
                        authorityId,
                        generation,
                        "FallbackV6ProofBootstrap"),
                    "FallbackV6ProofBootstrap",
                    authorityId,
                    "(none)",
                    generation,
                    "None",
                    "Rejected",
                    authorityId,
                    generation,
                    fingerprint,
                    "(none)",
                    0,
                    "(none)",
                    "(none)",
                    false,
                    false,
                    null,
                    new(
                        "fallback_v6_bootstrap",
                        "TunaV4ToPostTunaFallbackV6",
                        boundary,
                        CreateFallbackV6ProofBootstrapTargetBoundary(
                            before,
                            observation),
                        FileTransferTransportKind.Tuna.ToString(),
                        observation.ProofKind,
                        "fallback_v6_proof_bootstrap",
                        fingerprint,
                        CalculateSnapshotFingerprint(
                            observation.DurableCheckpoint),
                        FileTransferRouteResolver.Resolve(
                                FileTransferRoute.PostTunaFallbackV6)
                            .RuntimeDescriptor.TelemetryToken,
                        FileTransferProtocol.ProtocolVersionV6,
                        FileTransferTransportKind.RegularNkn.ToString(),
                        FileTransferTransportHandoffKind.TunaToNormalFallback
                            .ToString(),
                        "(none)",
                        "(none)",
                        "(none)",
                        ImmutableArray<
                            FileTransferCoordinatorReceiptTransitionSnapshot>
                            .Empty));
        }

        var status = decision.Status switch
        {
            FileTransferFallbackV6ProofBootstrapDecisionStatus.Issued or
            FileTransferFallbackV6ProofBootstrapDecisionStatus.Applied =>
                "Accepted",
            FileTransferFallbackV6ProofBootstrapDecisionStatus.Idempotent =>
                "Idempotent",
            _ => "Rejected",
        };
        var snapshot = CreateTransitionSnapshot(
            before,
            decision.State,
            coordinatorInstanceId,
            before.PersistentState.SnapshotStateGeneration,
            decisionKind,
            status,
            decision.Reason,
            ImmutableArray.Create(attemptedOperation));
        return decision with
        {
            State = snapshot.State,
            SnapshotEnvelope = snapshot.Envelope,
        };
    }

    private static FileTransferCoordinatorStateBoundarySnapshot
        CreateStateBoundarySnapshot(FileTransferCoordinatorState state)
    {
        var checkpoint = state.CurrentFallbackCheckpointLease;
        return new FileTransferCoordinatorStateBoundarySnapshot(
            state.RouteSelection.RuntimeDescriptor.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            state.CurrentRoutePublicationToken?.PublicationGeneration ?? 0,
            state.CurrentRouteCommit?.CommitGeneration ?? 0,
            state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0,
            state.TerminalGeneration,
            state.CurrentLeg?.Generation ?? 0,
            state.CurrentTransportEpochDecision?.TransportEpoch ?? 0,
            state.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ?? 0,
            checkpoint?.CheckpointGeneration ?? 0,
            checkpoint?.CheckpointRequestId ?? "(none)",
            state.CurrentDataSessionIdentity ?? "(none)",
            state.CurrentDataSessionGeneration);
    }

    private static string CalculateStateFingerprint(
        FileTransferCoordinatorState state)
    {
        var operations = CreateOperationDescriptors(
            state,
            includeResponderProbeHandoff: false);
        var responderHandoff = state.PersistentState
            .RuntimeUnlockResponderPeerProbeRouteTransitionHandoff;
        var retryLifecycle = state.PersistentState.RuntimeUnlockRetryLifecycle;
        var normalizedRetryLifecycle = retryLifecycle with
        {
            AttemptHistory = retryLifecycle.AttemptHistory.IsDefault
                ? ImmutableArray<RuntimeUnlockRetryLifecycleAttemptSnapshot>.Empty
                : retryLifecycle.AttemptHistory,
        };
        var payload = FileTransferCoordinatorTransitionEnvelope
            .SerializeCanonical(
                new
                {
                    Boundary = CreateStateBoundarySnapshot(state),
                    state.IsTerminal,
                    state.RouteSelection,
                    state.CurrentLiveRouteEpoch,
                    state.CurrentLeg,
                    state.LegHistory,
                    state.LastLiveRouteEpochId,
                    state.LastTransferLegGeneration,
                    state.LastRuntimeOwnerGeneration,
                    state.TerminalGeneration,
                    state.CurrentRuntimeOwner,
                    state.RuntimeUnlockFallbackSurvivalState,
                    state.CurrentRuntimeUnlockAdmission,
                    state.LastRuntimeUnlockAdmissionGeneration,
                    state.CurrentRouteCommit,
                    state.LastRouteCommitGeneration,
                    state.CurrentFallbackControlPlaneToken,
                    state.LastFallbackControlPlaneTokenGeneration,
                    state.CurrentFallbackCheckpointLease,
                    state.LastFallbackCheckpointLeaseGeneration,
                    state.CurrentFallbackControlPlanePressureToken,
                    state.LastFallbackControlPlanePressureGeneration,
                    state.CurrentRepairReplayDecision,
                    state.LastRepairReplayGeneration,
                    state.CurrentTransportEpochDecision,
                    state.LastTransportEpochDecisionGeneration,
                    state.CurrentRoutePublicationToken,
                    state.LastRoutePublicationGeneration,
                    state.CurrentRuntimeUnlockPendingPreCommitProbe,
                    state.LastRuntimeUnlockPendingPreCommitProbeGeneration,
                    state.CurrentDataSessionIdentity,
                    state.CurrentDataSessionGeneration,
                    RuntimeUnlockRetryLifecycle = normalizedRetryLifecycle,
                    ResponderProbeHandoff = new
                    {
                        CurrentAuthorityId = responderHandoff.CurrentAuthority
                            ?.AuthorityId.ToString() ?? "(none)",
                        CurrentAuthorityGeneration = responderHandoff
                            .CurrentAuthority?.AuthorityGeneration ?? 0,
                        CurrentAckFingerprint = responderHandoff
                            .CurrentAuthority?.Observation
                            .AckSourceFingerprint ?? "(none)",
                        CurrentProbeFingerprint = responderHandoff
                            .CurrentAuthority?.Observation
                            .PeerProbeFrameFingerprint ?? "(none)",
                        TerminalAuthorityId = responderHandoff
                            .LastTerminalAuthority?.AuthorityId.ToString() ??
                            "(none)",
                        TerminalAuthorityGeneration = responderHandoff
                            .LastTerminalAuthority?.AuthorityGeneration ?? 0,
                        Status = responderHandoff.LastStatus?.ToString() ??
                            "(none)",
                        Reason = responderHandoff.LastReason ?? "(none)",
                    },
                    Operations = operations,
                },
                SnapshotFingerprintJsonOptions);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
    }

    private static ImmutableArray<FileTransferCoordinatorOperationTransitionSnapshot>
        CreateOperationTransitionSnapshots(
            FileTransferCoordinatorState before,
            FileTransferCoordinatorState after)
    {
        var beforeOperations = CreateOperationDescriptors(before)
            .ToDictionary(GetCoordinatorOperationKey, StringComparer.Ordinal);
        var afterOperations = CreateOperationDescriptors(after)
            .ToDictionary(GetCoordinatorOperationKey, StringComparer.Ordinal);
        var operationKeys = beforeOperations.Keys
            .Concat(afterOperations.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<
            FileTransferCoordinatorOperationTransitionSnapshot>();
        foreach (var operationKey in operationKeys)
        {
            beforeOperations.TryGetValue(operationKey, out var prior);
            afterOperations.TryGetValue(operationKey, out var current);
            if (prior is not null &&
                current is not null &&
                AreOperationDescriptorsEquivalent(prior, current))
            {
                continue;
            }

            var selected = current ?? prior!;
            builder.Add(CreateOperationTransitionSnapshot(
                selected,
                prior?.State ?? "None",
                current?.State ?? "None",
                stateApplied: true));
        }
        return builder.ToImmutable();
    }

    private static bool AreOperationDescriptorsEquivalent(
        CoordinatorOperationDescriptor prior,
        CoordinatorOperationDescriptor current)
    {
        if (string.Equals(
                prior.Family,
                "route_runtime",
                StringComparison.Ordinal) &&
            prior.RouteRuntime is { } priorRuntime &&
            current.RouteRuntime is { } currentRuntime)
        {
            prior = prior with
            {
                RouteRuntime = priorRuntime with
                {
                    Boundary = currentRuntime.Boundary,
                },
            };
        }

        return string.Equals(
            FileTransferCoordinatorTransitionEnvelope.SerializeCanonical(
                prior,
                SnapshotFingerprintJsonOptions),
            FileTransferCoordinatorTransitionEnvelope.SerializeCanonical(
                current,
                SnapshotFingerprintJsonOptions),
            StringComparison.Ordinal);
    }

    private static string GetCoordinatorOperationKey(
        CoordinatorOperationDescriptor operation)
        => string.IsNullOrWhiteSpace(operation.OperationKey)
            ? $"{operation.Family}|{operation.AuthorityId}|{operation.OperationGeneration}"
            : operation.OperationKey;

    private static FileTransferCoordinatorOperationTransitionSnapshot
        CreateOperationTransitionSnapshot(
            CoordinatorOperationDescriptor operation,
            string beforeState,
            string afterState,
            bool stateApplied)
        => new(
            operation.Family,
            operation.OperationRole,
            GetCoordinatorOperationKey(operation),
            operation.OperationKind,
            operation.AuthorityId,
            operation.PredecessorAuthorityId,
            operation.OperationGeneration,
            beforeState,
            afterState,
            operation.CommandIdentity,
            operation.CommandGeneration,
            operation.CommandFingerprint,
            operation.ReceiptIdentity,
            operation.ReceiptGeneration,
            operation.ReceiptStatus,
            operation.ReceiptFingerprint,
            operation.ReceiptValidated,
            stateApplied,
            operation.RouteRuntime,
            operation.RouteTransition,
            operation.RuntimeUnlockRetry,
            operation.ReceiveRecovery,
            operation.FallbackTailYield,
            operation.RegularV4FrontierRepairPressure,
            operation.ResponderProbeHandoff);

    internal static ImmutableArray<
        FileTransferCoordinatorOperationTransitionSnapshot>
        CreateRouteTransitionTerminalizationAttemptSnapshots(
            FileTransferCoordinatorState state)
    {
        if (state.PersistentState.FileTransferRouteTransition.CurrentOperation is
            not { State: FileTransferRouteTransitionState.Draining } operation)
        {
            return ImmutableArray<
                FileTransferCoordinatorOperationTransitionSnapshot>.Empty;
        }

        var descriptor = CreateRouteTransitionOperationDescriptor(
            state,
            operation,
            "current");
        return ImmutableArray.Create(CreateOperationTransitionSnapshot(
            descriptor,
            descriptor.State,
            descriptor.State,
            stateApplied: false));
    }

    private static ImmutableArray<CoordinatorOperationDescriptor>
        CreateOperationDescriptors(
            FileTransferCoordinatorState state,
            bool includeResponderProbeHandoff = true)
    {
        var persistent = state.PersistentState;
        var builder = ImmutableArray.CreateBuilder<CoordinatorOperationDescriptor>();

        AddRouteRuntimeOperationDescriptors(state, builder);

        var transitionState = persistent.FileTransferRouteTransition;
        if (transitionState.CurrentOperation is { } currentTransition)
        {
            builder.Add(CreateRouteTransitionOperationDescriptor(
                state,
                currentTransition,
                "current"));
        }
        if (transitionState.LastTerminalOperation is { } terminalTransition &&
            (transitionState.CurrentOperation is null ||
             terminalTransition.Authority !=
                 transitionState.CurrentOperation.Value.Authority))
        {
            builder.Add(CreateRouteTransitionOperationDescriptor(
                state,
                terminalTransition,
                "terminal"));
        }
        if (transitionState.PendingSupersession is { } supersession)
        {
            builder.Add(CreateRouteTransitionSupersessionDescriptor(
                state,
                supersession));
        }
        if (transitionState.PendingFallbackV6ProofBootstrap is { } bootstrap)
        {
            builder.Add(CreateFallbackV6ProofBootstrapDescriptor(
                state,
                bootstrap,
                transitionState.LastFallbackV6ProofBootstrapReceipt));
        }
        else if (transitionState.LastFallbackV6ProofBootstrapReceipt is
                 { } bootstrapReceipt)
        {
            builder.Add(CreateFallbackV6ProofBootstrapDescriptor(
                state,
                bootstrapReceipt.Command,
                bootstrapReceipt));
        }

        AddRuntimeUnlockRetryOperationDescriptors(state, builder);
        AddReceiveRecoveryOperationDescriptors(state, builder);

        AddFallbackTailYieldOperationDescriptors(state, builder);

        AddRegularV4FrontierRepairPressureOperationDescriptors(state, builder);

        var handoff = persistent.RuntimeUnlockResponderPeerProbeRouteTransitionHandoff;
        if (includeResponderProbeHandoff &&
            handoff.CurrentAuthority is { } handoffAuthority)
        {
            builder.Add(CreateResponderProbeHandoffDescriptor(
                state,
                handoffAuthority,
                handoff.LastStatus ==
                    RuntimeUnlockResponderPeerProbeRouteTransitionHandoffStatus
                        .Held
                    ? "owner_work_hold"
                    : "handoff_current",
                handoff.LastStatus?.ToString() ?? "Accepted",
                handoff.LastReason ??
                    "runtime_unlock_responder_peer_probe_handoff_bound"));
        }
        if (includeResponderProbeHandoff &&
            handoff.LastTerminalAuthority is { } terminalAuthority &&
            (handoff.CurrentAuthority is null ||
             handoff.CurrentAuthority.Value.AuthorityId !=
                 terminalAuthority.AuthorityId))
        {
            builder.Add(CreateResponderProbeHandoffDescriptor(
                state,
                terminalAuthority,
                "handoff_terminal",
                handoff.LastStatus?.ToString() ?? "Invalidated",
                handoff.LastReason ??
                    "runtime_unlock_responder_peer_probe_handoff_invalidated"));
        }

        return builder
            .OrderBy(GetCoordinatorOperationKey, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static CoordinatorOperationDescriptor
        CreateResponderProbeHandoffDescriptor(
            FileTransferCoordinatorState state,
            RuntimeUnlockResponderPeerProbeRouteTransitionHandoffAuthority
                authority,
            string role,
            string status,
            string reason)
    {
        var observation = authority.Observation;
        var receipt = CreateResponderProbeHandoffReceipt(
            authority.AuthorityId.ToString(),
            authority.AuthorityGeneration,
            "ResponderPeerProbeRouteTransitionHandoff",
            status,
            reason,
            observation);
        return new CoordinatorOperationDescriptor(
            "responder_probe_handoff",
            "ResponderPeerProbeRouteTransitionHandoff",
            authority.AuthorityId.ToString(),
            "(none)",
            authority.AuthorityGeneration,
            status,
            receipt.CommandIdentity,
            receipt.CommandGeneration,
            receipt.CommandFingerprint,
            receipt.ReceiptIdentity,
            receipt.ReceiptGeneration,
            receipt.ReceiptStatus,
            receipt.ReceiptFingerprint,
            receipt.ReceiptValidated,
            OperationRole: role,
            OperationKey: CreateCoordinatorOperationKey(
                "responder_probe_handoff",
                role,
                authority.AuthorityId.ToString(),
                authority.AuthorityGeneration,
                "ResponderPeerProbeRouteTransitionHandoff"),
            ResponderProbeHandoff: CreateResponderProbeHandoffSnapshot(
                state,
                role,
                observation,
                authority,
                status,
                reason,
                ImmutableArray.Create(receipt)));
    }

    private static FileTransferCoordinatorReceiptTransitionSnapshot
        CreateResponderProbeHandoffReceipt(
            string authorityId,
            long generation,
            string decisionKind,
            string status,
            string reason,
            RuntimeUnlockResponderPeerProbeRouteTransitionObservation
                observation)
    {
        var stage = status switch
        {
            "Held" => "handoff_hold",
            "Invalidated" => "handoff_invalidation",
            "Accepted" or "Idempotent" => "handoff_bind",
            _ => "attempted_decision",
        };
        var commandFingerprint = CalculateSnapshotFingerprint(observation);
        return new FileTransferCoordinatorReceiptTransitionSnapshot(
            stage,
            $"{authorityId}:{decisionKind}",
            generation,
            commandFingerprint,
            $"{authorityId}:{decisionKind}:decision",
            generation,
            status,
            reason,
            CalculateSnapshotFingerprint(new
            {
                decisionKind,
                status,
                reason,
                observation,
            }),
            true);
    }

    private static FileTransferCoordinatorResponderProbeHandoffOperationSnapshot
        CreateResponderProbeHandoffSnapshot(
            FileTransferCoordinatorState state,
            string role,
            RuntimeUnlockResponderPeerProbeRouteTransitionObservation
                observation,
            RuntimeUnlockResponderPeerProbeRouteTransitionHandoffAuthority?
                authority,
            string status,
            string reason,
            ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot>
                receipts)
    {
        var source = CreateResponderProbeHandoffBoundary(
            observation.PeerReceiveScope);
        var effective = CreateStateBoundarySnapshot(state);
        var target = effective with
        {
            Route = FileTransferRouteResolver.FileTunaV4Token,
            ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
        };
        var release = observation.ResponderTransitionMode ==
            RuntimeUnlockResponderPeerProbeTransitionMode.PostFallbackV6Paired
                ? authority?.PairedRelease ??
                  state.PersistentState.FallbackTailYield.CurrentOperation
                      ?.PairedRelease
                : null;
        var pendingPreCommitGeneration =
            authority?.PendingPreCommitProbe.PendingGeneration ??
            state.CurrentRuntimeUnlockPendingPreCommitProbe
                ?.PendingGeneration ??
            0;
        var linkedTransition = state.PersistentState.FileTransferRouteTransition
            .CurrentOperation?.Authority;
        return new(
            role,
            observation.ResponderTransitionMode switch
            {
                RuntimeUnlockResponderPeerProbeTransitionMode.InitialRegularV4 =>
                    "initial_regular_v4",
                RuntimeUnlockResponderPeerProbeTransitionMode
                        .PostFallbackV6Paired =>
                    "post_fallback_v6_paired",
                _ => "none",
            },
            source,
            effective,
            target,
            observation.TransactionGeneration,
            observation.OfferGeneration,
            observation.PendingAnswerAckGeneration,
            observation.PayerDecisionId,
            observation.LaunchGeneration,
            observation.TunaPathLeaseGeneration,
            observation.TunaPathLeaseListenerRunId ?? "(none)",
            observation.Lanes,
            observation.AckMessageId ?? "(none)",
            observation.AckSourceFingerprint ?? "(none)",
            observation.PeerProbeFrameFingerprint ?? "(none)",
            observation.ProbeId,
            observation.ProbeState.ToString(),
            observation.ProbeTransport.ToString(),
            observation.ProbeAckedUtcMs,
            observation.PeerReceiveScope.Admission.AdmissionGeneration,
            pendingPreCommitGeneration,
            release?.LocalProof.Decision.OwnerKey ?? "(none)",
            release?.LocalProof.Decision.PairKey ?? "(none)",
            release?.LocalProof.ProofIdentity ?? "(none)",
            release?.LocalProof.FrameFingerprint ?? "(none)",
            release?.PeerProof.ObservationIdentity ?? "(none)",
            release?.PeerProof.Proof.FrameFingerprint ?? "(none)",
            release?.ReleaseAuthorityId.ToString() ?? "(none)",
            release?.ReleaseGeneration ?? 0,
            release?.PairingGeneration ?? 0,
            authority?.AuthorityId.ToString() ?? "(none)",
            authority?.AuthorityGeneration ?? 0,
            linkedTransition?.AuthorityId.ToString() ?? "(none)",
            linkedTransition?.TransitionGeneration ?? 0,
            status,
            reason,
            receipts);
    }

    private static FileTransferCoordinatorStateBoundarySnapshot
        CreateResponderProbeHandoffBoundary(
            RuntimeUnlockPeerPreCommitAnswerAcceptanceScope scope)
        => new(
            scope.RuntimeOwner.RouteToken,
            scope.RuntimeOwner.ProtocolVersion,
            scope.RuntimeOwner.LiveRouteEpoch,
            scope.RoutePublication.PublicationGeneration,
            scope.RoutePublication.RouteCommitGeneration ?? 0,
            scope.RuntimeOwner.RuntimeGeneration,
            scope.TerminalGeneration,
            scope.LegGeneration,
            scope.TransportEpoch,
            scope.BridgeRecoveryGeneration,
            scope.CheckpointGeneration,
            scope.CheckpointRequestId,
            scope.DataSessionIdentity,
            scope.DataSessionGeneration);

    private static void
        AddRegularV4FrontierRepairPressureOperationDescriptors(
            FileTransferCoordinatorState state,
            ImmutableArray<CoordinatorOperationDescriptor>.Builder builder)
    {
        var pressure = state.PersistentState.RegularV4FrontierRepairPressure;
        var pressureAuthorityIds = new[]
        {
            pressure.PendingBinding?.Authority.AuthorityId,
            pressure.InstalledBinding?.Authority.AuthorityId,
            pressure.RetainedPredecessorBinding?.Authority.AuthorityId,
        };
        if (state.CurrentRepairReplayDecision is { } currentRepair &&
            pressureAuthorityIds.Any(candidate =>
                candidate is { IsDefault: false } exact &&
                exact == currentRepair.AuthorityId))
        {
            builder.Add(CreateRegularV4PressureRepairAuthorityDescriptor(
                state,
                currentRepair,
                "repair_authority_current"));
        }

        AddBinding(
            "pressure_binding_pending",
            pressure.PendingBinding,
            pressure.PendingInstallCommand,
            null);
        AddBinding(
            "pressure_binding_installed",
            pressure.InstalledBinding,
            pressure.InstalledInstallCommand,
            pressure.InstalledInstallReceipt);
        AddBinding(
            "pressure_binding_retained_predecessor",
            pressure.RetainedPredecessorBinding,
            pressure.RetainedPredecessorInstallCommand,
            pressure.RetainedPredecessorInstallReceipt);

        if (pressure.LastSettlement is { } settlement)
        {
            builder.Add(CreateRegularV4PressureSettlementDescriptor(
                state,
                "repair_authority_terminal",
                settlement,
                pressure.LastSettlementApplyReceipt));
            builder.Add(CreateRegularV4PressureSettlementDescriptor(
                state,
                "pressure_settlement_terminal",
                settlement,
                pressure.LastSettlementApplyReceipt));
        }
        if (pressure.LastRecoveryHandoff is { } handoff)
        {
            builder.Add(CreateRegularV4PressureRecoveryHandoffDescriptor(
                state,
                "recovery_handoff_terminal",
                handoff,
                pressure.LastSettlementApplyReceipt,
                "Committed"));
        }

        return;

        void AddBinding(
            string role,
            RegularV4FrontierRepairPressureBinding? binding,
            FileTransferRegularV4FrontierRepairPressureInstallCommand? command,
            FileTransferRegularV4FrontierRepairPressureInstallReceipt? receipt)
        {
            if (binding is { } exact)
            {
                builder.Add(CreateRegularV4PressureBindingDescriptor(
                    state,
                    role,
                    exact,
                    command,
                    receipt));
            }
        }
    }

    private static CoordinatorOperationDescriptor
        CreateRegularV4PressureRepairAuthorityDescriptor(
            FileTransferCoordinatorState state,
            RepairReplayDecision repair,
            string role)
    {
        var boundary = CreateStateBoundarySnapshot(state);
        var authorityId = repair.AuthorityId.ToString();
        return new CoordinatorOperationDescriptor(
            "regular_v4_frontier_repair_pressure",
            repair.RepairKind.ToString(),
            authorityId,
            repair.PredecessorAuthorityId?.ToString() ?? "(none)",
            repair.RepairGeneration,
            "Authorized",
            authorityId,
            repair.RepairGeneration,
            CalculateSnapshotFingerprint(repair),
            "(none)",
            0,
            "(none)",
            "(none)",
            false,
            OperationRole: role,
            OperationKey: CreateCoordinatorOperationKey(
                "regular_v4_frontier_repair_pressure",
                role,
                authorityId,
                repair.RepairGeneration,
                repair.RepairKind.ToString()),
            RegularV4FrontierRepairPressure: new(
                role,
                boundary,
                boundary,
                repair.RepairKind.ToString(),
                "(none)",
                0,
                -1,
                0,
                repair.RoutePublicationToken?.TargetTransport.ToString() ??
                    "Unknown",
                repair.Reason,
                repair.RepairGeneration,
                0,
                "Authorized",
                "(none)",
                "(none)",
                false,
                false,
                "(none)",
                "(none)",
                0,
                "(none)",
                ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot>
                    .Empty));
    }

    private static CoordinatorOperationDescriptor
        CreateRegularV4PressureBindingDescriptor(
            FileTransferCoordinatorState state,
            string role,
            RegularV4FrontierRepairPressureBinding binding,
            FileTransferRegularV4FrontierRepairPressureInstallCommand? command,
            FileTransferRegularV4FrontierRepairPressureInstallReceipt? receipt)
    {
        var authority = binding.Authority;
        var authorityId = authority.AuthorityId.ToString();
        var receipts = ImmutableArray.CreateBuilder<
            FileTransferCoordinatorReceiptTransitionSnapshot>();
        if (command is not null)
        {
            var exactReceipt = receipt is not null &&
                               receipt.Authority == authority &&
                               receipt.InstallGeneration ==
                               binding.InstallGeneration
                ? receipt
                : null;
            receipts.Add(new FileTransferCoordinatorReceiptTransitionSnapshot(
                "pressure_install",
                $"{authorityId}:pressure_install",
                binding.InstallGeneration,
                CalculateSnapshotFingerprint(command),
                exactReceipt is null
                    ? $"{authorityId}:pressure_install:pending"
                    : $"{authorityId}:pressure_install",
                exactReceipt?.InstallGeneration ?? binding.InstallGeneration,
                exactReceipt?.Status.ToString() ?? "Pending",
                exactReceipt?.Reason ?? "pressure_install_pending",
                CalculateSnapshotFingerprint(exactReceipt),
                exactReceipt?.Status is
                    FileTransferRegularV4FrontierRepairPressureInstallStatus
                        .Installed or
                    FileTransferRegularV4FrontierRepairPressureInstallStatus
                        .Idempotent));
        }
        if (binding.FinalSettlement is { } settlement)
        {
            receipts.Add(CreateRegularV4PressureSettlementReceiptSnapshot(
                settlement,
                state.PersistentState.RegularV4FrontierRepairPressure
                    .LastSettlementApplyReceipt));
        }

        var source = CreateRegularV4PressureSourceBoundary(state, authority);
        var effective = CreateStateBoundarySnapshot(state);
        var selectedReceipt = receipts.LastOrDefault();
        return new CoordinatorOperationDescriptor(
            "regular_v4_frontier_repair_pressure",
            authority.DecisionReason,
            authorityId,
            binding.PredecessorAuthorityId?.ToString() ?? "(none)",
            authority.DecisionGeneration,
            binding.State.ToString(),
            command is null ? authorityId : $"{authorityId}:pressure_install",
            command?.InstallGeneration ?? authority.DecisionGeneration,
            CalculateSnapshotFingerprint(command ?? (object)authority),
            selectedReceipt?.ReceiptIdentity ?? "(none)",
            selectedReceipt?.ReceiptGeneration ?? 0,
            selectedReceipt?.ReceiptStatus ?? "(none)",
            selectedReceipt?.ReceiptFingerprint ?? "(none)",
            selectedReceipt?.ReceiptValidated ?? false,
            OperationRole: role,
            OperationKey: CreateCoordinatorOperationKey(
                "regular_v4_frontier_repair_pressure",
                role,
                authorityId,
                binding.InstallGeneration,
                authority.DecisionReason),
            RegularV4FrontierRepairPressure: new(
                role,
                source,
                effective,
                "RegularV4FrontierRepairPressure",
                authority.RepairKey,
                authority.FrontierChunkIndex,
                authority.DurableReceivedHighestChunkIndex,
                authority.PendingRepairCount,
                authority.TargetTransport.ToString(),
                binding.PressureReason,
                authority.DecisionGeneration,
                binding.InstallGeneration,
                binding.State.ToString(),
                binding.FinalSettlement?.Outcome.ToString() ?? "(none)",
                binding.FinalSettlement?.DecisionReason ?? "(none)",
                false,
                false,
                binding.FinalSettlement?.SuccessorAuthorityId?.ToString() ??
                    "(none)",
                "(none)",
                0,
                "(none)",
                receipts.ToImmutable()));
    }

    private static CoordinatorOperationDescriptor
        CreateRegularV4PressureSettlementDescriptor(
            FileTransferCoordinatorState state,
            string role,
            FileTransferRegularV4FrontierRepairSettlement settlement,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt?
                receipt)
    {
        var authorityId = settlement.AuthorityId.ToString();
        var receiptSnapshot =
            CreateRegularV4PressureSettlementReceiptSnapshot(
                settlement,
                receipt);
        var boundary = CreateStateBoundarySnapshot(state);
        return new CoordinatorOperationDescriptor(
            "regular_v4_frontier_repair_pressure",
            "PressureSettlement",
            authorityId,
            "(none)",
            settlement.DecisionGeneration,
            receiptSnapshot.ReceiptValidated ? "Applied" : "Prepared",
            receiptSnapshot.CommandIdentity,
            receiptSnapshot.CommandGeneration,
            receiptSnapshot.CommandFingerprint,
            receiptSnapshot.ReceiptIdentity,
            receiptSnapshot.ReceiptGeneration,
            receiptSnapshot.ReceiptStatus,
            receiptSnapshot.ReceiptFingerprint,
            receiptSnapshot.ReceiptValidated,
            OperationRole: role,
            OperationKey: CreateCoordinatorOperationKey(
                "regular_v4_frontier_repair_pressure",
                role,
                authorityId,
                settlement.DecisionGeneration,
                settlement.Outcome.ToString()),
            RegularV4FrontierRepairPressure: new(
                role,
                CreateRegularV4PressureSourceBoundary(state, settlement),
                boundary,
                "RegularV4FrontierRepairPressure",
                settlement.RepairKey,
                settlement.FrontierChunkIndex,
                settlement.DurableReceivedHighestChunkIndex,
                settlement.RequestedChunkCount,
                settlement.TargetTransport.ToString(),
                settlement.DecisionReason,
                settlement.DecisionGeneration,
                receipt?.Command.InstallGeneration ?? 0,
                receiptSnapshot.ReceiptValidated ? "Settled" : "Prepared",
                settlement.Outcome.ToString(),
                settlement.DecisionReason,
                receipt?.CurrentAuthorityCleared ?? false,
                receipt?.RetainedPredecessorCleared ?? false,
                settlement.SuccessorAuthorityId?.ToString() ?? "(none)",
                "(none)",
                0,
                "(none)",
                ImmutableArray.Create(receiptSnapshot)));
    }

    private static CoordinatorOperationDescriptor
        CreateRegularV4PressureRecoveryHandoffDescriptor(
            FileTransferCoordinatorState state,
            string role,
            RegularV4FrontierRepairPressureRecoveryHandoffTransition handoff,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt?
                receipt,
            string lifecycle)
    {
        var binding = handoff.PressureBinding;
        var authority = binding.Authority;
        var recovery = handoff.ReceiveRecoveryOperation;
        var authorityId = authority.AuthorityId.ToString();
        var receiptSnapshot =
            CreateRegularV4PressureSettlementReceiptSnapshot(
                handoff.Settlement,
                receipt);
        return new CoordinatorOperationDescriptor(
            "regular_v4_frontier_repair_pressure",
            "PressureRecoveryHandoff",
            authorityId,
            binding.PredecessorAuthorityId?.ToString() ?? "(none)",
            recovery.OperationGeneration,
            lifecycle,
            $"{authorityId}:recovery_handoff",
            recovery.OperationGeneration,
            CalculateSnapshotFingerprint(handoff),
            receiptSnapshot.ReceiptIdentity,
            receiptSnapshot.ReceiptGeneration,
            receiptSnapshot.ReceiptStatus,
            receiptSnapshot.ReceiptFingerprint,
            receiptSnapshot.ReceiptValidated,
            OperationRole: role,
            OperationKey: CreateCoordinatorOperationKey(
                "regular_v4_frontier_repair_pressure",
                role,
                authorityId,
                recovery.OperationGeneration,
                "PressureRecoveryHandoff"),
            RegularV4FrontierRepairPressure: new(
                role,
                CreateRegularV4PressureSourceBoundary(state, authority),
                CreateStateBoundarySnapshot(state),
                "RegularV4FrontierRepairPressure",
                authority.RepairKey,
                authority.FrontierChunkIndex,
                authority.DurableReceivedHighestChunkIndex,
                authority.PendingRepairCount,
                authority.TargetTransport.ToString(),
                binding.PressureReason,
                authority.DecisionGeneration,
                binding.InstallGeneration,
                lifecycle,
                handoff.Settlement.Outcome.ToString(),
                handoff.Settlement.DecisionReason,
                receipt?.CurrentAuthorityCleared ?? false,
                receipt?.RetainedPredecessorCleared ?? false,
                handoff.Settlement.SuccessorAuthorityId?.ToString() ??
                    "(none)",
                recovery.AuthorityId.ToString(),
                recovery.OperationGeneration,
                recovery.BridgeRecoveryDecision.RecoveryKind.ToString(),
                ImmutableArray.Create(receiptSnapshot)));
    }

    private static FileTransferCoordinatorReceiptTransitionSnapshot
        CreateRegularV4PressureSettlementReceiptSnapshot(
            FileTransferRegularV4FrontierRepairSettlement settlement,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt?
                receipt)
    {
        var exactReceipt = receipt is not null &&
                           receipt.Command.Settlement == settlement
            ? receipt
            : null;
        var authorityId = settlement.AuthorityId.ToString();
        return new FileTransferCoordinatorReceiptTransitionSnapshot(
            "pressure_settlement",
            $"{authorityId}:pressure_settlement",
            settlement.DecisionGeneration,
            CalculateSnapshotFingerprint(
                exactReceipt?.Command ?? (object)settlement),
            exactReceipt is null
                ? $"{authorityId}:pressure_settlement:pending"
                : $"{authorityId}:pressure_settlement",
            exactReceipt?.Command.InstallGeneration ??
                settlement.DecisionGeneration,
            exactReceipt?.Status.ToString() ?? "Prepared",
            exactReceipt?.Reason ?? settlement.DecisionReason,
            CalculateSnapshotFingerprint(exactReceipt),
            exactReceipt?.Status is
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus
                    .Applied or
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus
                    .Idempotent);
    }

    private static FileTransferCoordinatorStateBoundarySnapshot
        CreateRegularV4PressureSourceBoundary(
            FileTransferCoordinatorState state,
            FileTransferRegularV4FrontierRepairAuthority authority)
    {
        var current = CreateStateBoundarySnapshot(state);
        return current with
        {
            Route = FileTransferRouteResolver.Resolve(
                    authority.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
                    authority.TargetTransport == FileTransferTransportKind.Tuna
                        ? FileTransferRoute.FileTunaV4
                        : FileTransferRoute.RegularNknV4Fast)
                .RuntimeDescriptor.TelemetryToken,
            ProtocolVersion = authority.ProtocolVersion,
            LiveRouteEpoch = authority.LiveRouteEpoch,
            RoutePublicationGeneration = authority.RoutePublicationGeneration,
            RouteCommitGeneration = authority.RouteCommitGeneration ?? 0,
            RuntimeGeneration = authority.RuntimeGeneration,
            TerminalGeneration = authority.TerminalGeneration,
            LegGeneration = checked((int)authority.LegGeneration),
            TransportEpoch = authority.TransportEpoch,
            BridgeRecoveryGeneration = authority.BridgeRecoveryGeneration,
        };
    }

    private static FileTransferCoordinatorStateBoundarySnapshot
        CreateRegularV4PressureSourceBoundary(
            FileTransferCoordinatorState state,
            FileTransferRegularV4FrontierRepairSettlement settlement)
    {
        var current = CreateStateBoundarySnapshot(state);
        return current with
        {
            Route = FileTransferRouteResolver.Resolve(
                    settlement.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
                    settlement.TargetTransport == FileTransferTransportKind.Tuna
                        ? FileTransferRoute.FileTunaV4
                        : FileTransferRoute.RegularNknV4Fast)
                .RuntimeDescriptor.TelemetryToken,
            ProtocolVersion = settlement.ProtocolVersion,
            LiveRouteEpoch = settlement.LiveRouteEpoch,
            RoutePublicationGeneration = settlement.RoutePublicationGeneration,
            RouteCommitGeneration = settlement.RouteCommitGeneration ?? 0,
            RuntimeGeneration = settlement.RuntimeGeneration,
            TerminalGeneration = settlement.TerminalGeneration,
            LegGeneration = checked((int)settlement.LegGeneration),
            TransportEpoch = settlement.TransportEpoch,
            BridgeRecoveryGeneration = settlement.BridgeRecoveryGeneration,
        };
    }

    private static void AddRuntimeUnlockRetryOperationDescriptors(
        FileTransferCoordinatorState state,
        ImmutableArray<CoordinatorOperationDescriptor>.Builder builder)
    {
        var lifecycle = state.PersistentState.RuntimeUnlockRetryLifecycle;
        AddAttempt("attempt_pending", lifecycle.PendingAttempt);
        AddAttempt("attempt_installed", lifecycle.InstalledAttempt);
        AddAttempt("attempt_retained_predecessor", lifecycle.RetainedPredecessor);
        AddAttempt("attempt_terminal", lifecycle.LastTerminalAttempt);

        if (lifecycle.BoundedClosure is { } closure)
        {
            builder.Add(CreateRuntimeUnlockRetryClosureDescriptor(
                state,
                closure));
        }

        var invalidationCommand =
            lifecycle.PendingScopeInvalidationCommand ??
            lifecycle.LastScopeInvalidationReceipt?.Command;
        if (invalidationCommand is not null)
        {
            builder.Add(CreateRuntimeUnlockRetryInvalidationDescriptor(
                state,
                invalidationCommand,
                lifecycle.LastScopeInvalidationReceipt));
        }

        return;

        void AddAttempt(
            string role,
            RuntimeUnlockRetryLifecycleAttemptSnapshot? attempt)
        {
            if (attempt is { } exact)
            {
                builder.Add(CreateRuntimeUnlockRetryAttemptDescriptor(
                    state,
                    role,
                    exact));
            }
        }
    }

    private static void AddFallbackTailYieldOperationDescriptors(
        FileTransferCoordinatorState state,
        ImmutableArray<CoordinatorOperationDescriptor>.Builder builder)
    {
        var fallback = state.PersistentState.FallbackTailYield;
        if (fallback.CurrentOperation is { } current)
        {
            builder.Add(CreateFallbackTailYieldOperationDescriptor(
                state,
                current,
                "yield_current"));
        }
        if (fallback.LastTerminalOperation is { } terminal &&
            (fallback.CurrentOperation is null ||
             terminal.Decision.AuthorityId !=
                 fallback.CurrentOperation.Value.Decision.AuthorityId))
        {
            builder.Add(CreateFallbackTailYieldOperationDescriptor(
                state,
                terminal,
                "yield_terminal"));
        }
        if (fallback.RetainedPredecessor is { } retained &&
            fallback.LastTerminalOperation is { } retainedOperation &&
            retainedOperation.Decision == retained)
        {
            builder.Add(CreateFallbackTailYieldOperationDescriptor(
                state,
                retainedOperation,
                "yield_retained_predecessor"));
        }

        AddPeerProof(
            "peer_proof_pending_initial",
            fallback.PendingPeerProofBeforeLocalYield);
        AddPeerProof(
            "peer_proof_pending_reauthorization",
            fallback.PendingReauthorizationPeerProof);
        AddPeerProof(
            "peer_proof_pending_retry_successor",
            fallback.PendingRetryCycleSuccessorPeerProof);

        if (fallback.PendingReauthorization is { } pendingReauthorization)
        {
            builder.Add(CreateFallbackTailYieldReauthorizationDescriptor(
                state,
                pendingReauthorization,
                "reauthorization_pending",
                "Pending",
                fallback.PendingReauthorizationPeerProof));
        }
        if (fallback.LastInvalidatedReauthorization is { } terminalReauthorization)
        {
            builder.Add(CreateFallbackTailYieldReauthorizationDescriptor(
                state,
                terminalReauthorization,
                "reauthorization_terminal",
                "Invalidated",
                null,
                fallback.LastReauthorizationInvalidationReason));
        }

        if (fallback.CurrentOperation is { } retryOperation)
        {
            if (fallback.PendingRetryRecheckRequest is { } retryRequest)
            {
                builder.Add(CreateFallbackTailYieldChildDescriptor(
                    state,
                    retryOperation,
                    "retry_request_pending",
                    "RetryRequest",
                    retryRequest.DispatchAttemptGeneration,
                    "Pending",
                    retryRequest,
                    null,
                    "Pending"));
            }
            if (fallback.CurrentRetryDispatchHold is { } retryHold)
            {
                builder.Add(CreateFallbackTailYieldChildDescriptor(
                    state,
                    retryOperation,
                    "retry_hold_current",
                    "RetryDispatchHold",
                    retryHold.HoldGeneration,
                    "Held",
                    retryHold,
                    retryHold,
                    "Held"));
            }
            if (fallback.PendingRetryDispatchRecheckCommand is { } recheck)
            {
                builder.Add(CreateFallbackTailYieldChildDescriptor(
                    state,
                    retryOperation,
                    "retry_recheck_pending",
                    "RetryDispatchRecheck",
                    recheck.RecheckGeneration,
                    "Pending",
                    recheck,
                    null,
                    "Pending"));
            }
            if (fallback.LastRetryDispatchRecheckReceipt is { } recheckReceipt)
            {
                builder.Add(CreateFallbackTailYieldChildDescriptor(
                    state,
                    retryOperation,
                    "retry_recheck_terminal",
                    "RetryDispatchRecheck",
                    recheckReceipt.Command.RecheckGeneration,
                    recheckReceipt.Status.ToString(),
                    recheckReceipt.Command,
                    recheckReceipt,
                    recheckReceipt.Status.ToString(),
                    recheckReceipt.Status !=
                        FileTransferFallbackTailYieldRetryDispatchRecheckReceiptStatus.Rejected));
            }
            if (fallback.CurrentReservation is { } reservation)
            {
                builder.Add(CreateFallbackTailYieldReservationDescriptor(
                    state,
                    retryOperation,
                    reservation,
                    "release_reservation_current",
                    fallback.LastConsumingInstallReceipt));
            }
            if (fallback.LastConsumedReservation is { } consumed)
            {
                builder.Add(CreateFallbackTailYieldReservationDescriptor(
                    state,
                    retryOperation,
                    consumed,
                    "release_reservation_consumed",
                    fallback.LastConsumingInstallReceipt));
            }
        }

        return;

        void AddPeerProof(
            string role,
            FileTransferFallbackTailPeerProofReceipt? receipt)
        {
            if (receipt is { } exact)
            {
                builder.Add(CreateFallbackTailYieldPeerProofDescriptor(
                    state,
                    role,
                    exact));
            }
        }
    }

    private static CoordinatorOperationDescriptor
        CreateFallbackTailYieldOperationDescriptor(
            FileTransferCoordinatorState state,
            FileTransferFallbackTailYieldOperation operation,
            string role)
    {
        var receipts = CreateFallbackTailYieldReceiptSnapshots(operation);
        var latest = receipts.IsDefaultOrEmpty ? null : receipts[^1];
        var decision = operation.Decision;
        var authorityId = decision.AuthorityId.ToString();
        return new CoordinatorOperationDescriptor(
            "fallback_tail_yield",
            decision.YieldKind.ToString(),
            authorityId,
            decision.PredecessorAuthorityId?.ToString() ?? "(none)",
            decision.YieldGeneration,
            operation.PairingState.ToString(),
            latest?.CommandIdentity ?? $"{authorityId}:yield",
            latest?.CommandGeneration ?? decision.YieldGeneration,
            latest?.CommandFingerprint ?? CalculateSnapshotFingerprint(decision),
            latest?.ReceiptIdentity ?? "(none)",
            latest?.ReceiptGeneration ?? 0,
            latest?.ReceiptStatus ?? "(none)",
            latest?.ReceiptFingerprint ?? "(none)",
            latest?.ReceiptValidated ?? false,
            OperationRole: role,
            OperationKey: CreateCoordinatorOperationKey(
                "fallback_tail_yield",
                role,
                authorityId,
                decision.YieldGeneration,
                decision.YieldKind.ToString()),
            FallbackTailYield: CreateFallbackTailYieldSnapshot(
                state,
                role,
                operation,
                receipts));
    }

    private static CoordinatorOperationDescriptor
        CreateFallbackTailYieldPeerProofDescriptor(
            FileTransferCoordinatorState state,
            string role,
            FileTransferFallbackTailPeerProofReceipt receipt)
    {
        var proof = receipt.Proof;
        var authorityId = proof.AuthorityId.ToString();
        var generation = proof.YieldGeneration;
        var exactReceipt = new FileTransferCoordinatorReceiptTransitionSnapshot(
            "peer_proof",
            $"{authorityId}:peer_proof",
            generation,
            CalculateSnapshotFingerprint(proof),
            receipt.ObservationIdentity,
            generation,
            "Observed",
            "fallback_tail_peer_proof_retained",
            CalculateSnapshotFingerprint(receipt),
            !receipt.IsDefault);
        var boundary = CreateFallbackTailYieldScopeBoundary(
            receipt.ObservationScope);
        return new CoordinatorOperationDescriptor(
            "fallback_tail_yield",
            "PeerProof",
            authorityId,
            proof.PredecessorAuthorityId?.ToString() ?? "(none)",
            generation,
            "Pending",
            exactReceipt.CommandIdentity,
            exactReceipt.CommandGeneration,
            exactReceipt.CommandFingerprint,
            exactReceipt.ReceiptIdentity,
            exactReceipt.ReceiptGeneration,
            exactReceipt.ReceiptStatus,
            exactReceipt.ReceiptFingerprint,
            exactReceipt.ReceiptValidated,
            OperationRole: role,
            OperationKey: CreateCoordinatorOperationKey(
                "fallback_tail_yield",
                role,
                authorityId,
                generation,
                "PeerProof"),
            FallbackTailYield:
                new FileTransferCoordinatorFallbackTailYieldOperationSnapshot(
                    role,
                    boundary,
                    boundary,
                    boundary,
                    "PeerProof",
                    "Pending",
                    receipt.ObservationScope.OwnerKey,
                    receipt.ObservationScope.PairKey,
                    "(none)",
                    "(none)",
                    authorityId,
                    generation,
                    receipt.ObservationIdentity,
                    proof.FrameFingerprint,
                    0,
                    "(none)",
                    0,
                    "(none)",
                    proof.PredecessorAuthorityId?.ToString() ?? "(none)",
                    false,
                    0,
                    proof.TransportEpoch,
                    proof.TransportEpoch,
                    0,
                    "(none)",
                    0,
                    0,
                    0,
                    "(none)",
                    ImmutableArray.Create(exactReceipt)));
    }

    private static CoordinatorOperationDescriptor
        CreateFallbackTailYieldReauthorizationDescriptor(
            FileTransferCoordinatorState state,
            FallbackTailYieldTransportEpochReauthorization reauthorization,
            string role,
            string status,
            FileTransferFallbackTailPeerProofReceipt? peerProof,
            string? reason = null)
    {
        var authorityId = reauthorization.PredecessorDecision.AuthorityId.ToString();
        var generation = reauthorization.ReauthorizationGeneration;
        var receipt = new FileTransferCoordinatorReceiptTransitionSnapshot(
            "transport_reauthorization",
            $"{authorityId}:reauthorization:{generation}",
            generation,
            CalculateSnapshotFingerprint(reauthorization),
            $"{authorityId}:reauthorization:{generation}:{status}",
            generation,
            status,
            reason ?? reauthorization.Reason,
            CalculateSnapshotFingerprint(new
            {
                reauthorization,
                peerProof,
                status,
                reason,
            }),
            !reauthorization.IsDefault);
        var source = CreateFallbackTailYieldReauthorizationSourceBoundary(
            reauthorization);
        var target = CreateFallbackTailYieldReauthorizationTargetBoundary(
            reauthorization);
        return new CoordinatorOperationDescriptor(
            "fallback_tail_yield",
            "TransportEpochReauthorization",
            authorityId,
            reauthorization.PredecessorDecision.PredecessorAuthorityId?.ToString() ??
                "(none)",
            generation,
            status,
            receipt.CommandIdentity,
            receipt.CommandGeneration,
            receipt.CommandFingerprint,
            receipt.ReceiptIdentity,
            receipt.ReceiptGeneration,
            receipt.ReceiptStatus,
            receipt.ReceiptFingerprint,
            receipt.ReceiptValidated,
            OperationRole: role,
            OperationKey: CreateCoordinatorOperationKey(
                "fallback_tail_yield",
                role,
                authorityId,
                generation,
                "TransportEpochReauthorization"),
            FallbackTailYield:
                new FileTransferCoordinatorFallbackTailYieldOperationSnapshot(
                    role,
                    source,
                    target,
                    target,
                    reauthorization.PredecessorDecision.YieldKind.ToString(),
                    status,
                    reauthorization.SourceOwnerKey,
                    reauthorization.SourcePairKey,
                    "(none)",
                    "(none)",
                    peerProof?.Proof.AuthorityId.ToString() ?? "(none)",
                    peerProof?.Proof.YieldGeneration ?? 0,
                    peerProof?.ObservationIdentity ?? "(none)",
                    peerProof?.Proof.FrameFingerprint ?? "(none)",
                    0,
                    "(none)",
                    0,
                    "(none)",
                    reauthorization.PredecessorDecision.AuthorityId.ToString(),
                    false,
                    generation,
                    reauthorization.SourceTransportEpoch,
                    reauthorization.TargetTransportEpoch,
                    0,
                    "(none)",
                    0,
                    0,
                    0,
                    reason ?? "(none)",
                    ImmutableArray.Create(receipt)));
    }

    private static CoordinatorOperationDescriptor
        CreateFallbackTailYieldReservationDescriptor(
            FileTransferCoordinatorState state,
            FileTransferFallbackTailYieldOperation operation,
            FileTransferFallbackTailPairedReleaseReservation reservation,
            string role,
            RuntimeUnlockRetryDispatchInstallReceipt? installReceipt)
    {
        var authorityId = operation.Decision.AuthorityId.ToString();
        var consumed = string.Equals(
            role,
            "release_reservation_consumed",
            StringComparison.Ordinal);
        var receipt = new FileTransferCoordinatorReceiptTransitionSnapshot(
            consumed ? "release_consumption" : "release_reservation",
            $"{authorityId}:reservation:{reservation.ReservationGeneration}",
            reservation.ReservationGeneration,
            CalculateSnapshotFingerprint(reservation),
            consumed
                ? $"{reservation.RetryAuthority.AuthorityId}:install:{installReceipt?.InstallGeneration ?? 0}"
                : $"{authorityId}:reservation:{reservation.ReservationGeneration}",
            consumed
                ? installReceipt?.InstallGeneration ?? 0
                : reservation.ReservationGeneration,
            consumed ? "Consumed" : "Reserved",
            consumed
                ? "fallback_tail_paired_release_consumed"
                : "fallback_tail_paired_release_reserved",
            CalculateSnapshotFingerprint(consumed ? installReceipt : reservation),
            !reservation.IsDefault && (!consumed ||
                installReceipt is { IsDefault: false }));
        return CreateFallbackTailYieldChildDescriptor(
            state,
            operation,
            role,
            "PairedReleaseReservation",
            reservation.ReservationGeneration,
            consumed ? "Consumed" : "Reserved",
            reservation,
            consumed ? installReceipt : reservation,
            receipt.ReceiptStatus,
            receipt.ReceiptValidated,
            receipt);
    }

    private static CoordinatorOperationDescriptor
        CreateFallbackTailYieldChildDescriptor(
            FileTransferCoordinatorState state,
            FileTransferFallbackTailYieldOperation operation,
            string role,
            string kind,
            long generation,
            string status,
            object command,
            object? receipt,
            string receiptStatus,
            bool receiptValidated = false,
            FileTransferCoordinatorReceiptTransitionSnapshot?
                exactReceipt = null)
    {
        var authorityId = operation.Decision.AuthorityId.ToString();
        var receiptSnapshot = exactReceipt ??
            new FileTransferCoordinatorReceiptTransitionSnapshot(
                kind,
                $"{authorityId}:{role}:{generation}",
                generation,
                CalculateSnapshotFingerprint(command),
                receipt is null
                    ? "(none)"
                    : $"{authorityId}:{role}:{generation}:receipt",
                receipt is null ? 0 : generation,
                receiptStatus,
                status,
                receipt is null
                    ? "(none)"
                    : CalculateSnapshotFingerprint(receipt),
                receiptValidated);
        var snapshot = CreateFallbackTailYieldSnapshot(
            state,
            role,
            operation,
            ImmutableArray.Create(receiptSnapshot));
        return new CoordinatorOperationDescriptor(
            "fallback_tail_yield",
            kind,
            authorityId,
            operation.Decision.PredecessorAuthorityId?.ToString() ?? "(none)",
            generation,
            status,
            receiptSnapshot.CommandIdentity,
            receiptSnapshot.CommandGeneration,
            receiptSnapshot.CommandFingerprint,
            receiptSnapshot.ReceiptIdentity,
            receiptSnapshot.ReceiptGeneration,
            receiptSnapshot.ReceiptStatus,
            receiptSnapshot.ReceiptFingerprint,
            receiptSnapshot.ReceiptValidated,
            OperationRole: role,
            OperationKey: CreateCoordinatorOperationKey(
                "fallback_tail_yield",
                role,
                authorityId,
                generation,
                kind),
            FallbackTailYield: snapshot with
            {
                ReservationGeneration = role.StartsWith(
                    "release_reservation_",
                    StringComparison.Ordinal)
                    ? generation
                    : snapshot.ReservationGeneration,
                ReservedRetryAuthorityId = command is
                    FileTransferFallbackTailPairedReleaseReservation reservation
                    ? reservation.RetryAuthority.AuthorityId.ToString()
                    : snapshot.ReservedRetryAuthorityId,
                ConsumingInstallGeneration = receipt is
                    RuntimeUnlockRetryDispatchInstallReceipt install
                    ? install.InstallGeneration
                    : snapshot.ConsumingInstallGeneration,
                RetryHoldGeneration = command is
                    FileTransferFallbackTailYieldRetryDispatchHold hold
                    ? hold.HoldGeneration
                    : snapshot.RetryHoldGeneration,
                RetryRecheckGeneration = command switch
                {
                    FileTransferFallbackTailYieldRetryDispatchRecheckCommand recheck =>
                        recheck.RecheckGeneration,
                    FileTransferFallbackTailYieldRetryDispatchRecheckReceipt recheckReceipt =>
                        recheckReceipt.Command.RecheckGeneration,
                    _ => snapshot.RetryRecheckGeneration,
                },
            });
    }

    private static FileTransferCoordinatorFallbackTailYieldOperationSnapshot
        CreateFallbackTailYieldSnapshot(
            FileTransferCoordinatorState state,
            string role,
            FileTransferFallbackTailYieldOperation operation,
            ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot>
                receipts)
    {
        var fallback = state.PersistentState.FallbackTailYield;
        var source = CreateFallbackTailYieldScopeBoundary(operation.Scope);
        var effectiveScope = GetEffectiveFallbackTailYieldScope(operation);
        var effective = CreateFallbackTailYieldScopeBoundary(effectiveScope);
        var reauthorization = operation.SourceReauthorization?.Reauthorization;
        var reservation = fallback.CurrentReservation ??
                          fallback.LastConsumedReservation;
        return new(
            role,
            source,
            effective,
            effective,
            operation.Decision.YieldKind.ToString(),
            operation.PairingState.ToString(),
            effectiveScope.OwnerKey,
            effectiveScope.PairKey,
            operation.LocalProof?.ProofIdentity ?? "(none)",
            operation.LocalProof?.FrameFingerprint ?? "(none)",
            operation.PeerProof?.Proof.AuthorityId.ToString() ?? "(none)",
            operation.PeerProof?.Proof.YieldGeneration ?? 0,
            operation.PeerProof?.ObservationIdentity ?? "(none)",
            operation.PeerProof?.Proof.FrameFingerprint ?? "(none)",
            operation.PairedRelease?.PairingGeneration ?? 0,
            operation.PairedRelease?.ReleaseAuthorityId.ToString() ?? "(none)",
            operation.PairedRelease?.ReleaseGeneration ?? 0,
            operation.PairedRelease?.ReleaseOutcome ?? "(none)",
            operation.FailedYieldSupersession?.FailedDecision.AuthorityId
                .ToString() ?? "(none)",
            operation.FailedYieldSupersession?.CarriedPeerProof ?? false,
            reauthorization?.ReauthorizationGeneration ?? 0,
            reauthorization?.SourceTransportEpoch ?? source.TransportEpoch,
            reauthorization?.TargetTransportEpoch ?? effective.TransportEpoch,
            reservation?.ReservationGeneration ?? 0,
            reservation?.RetryAuthority.AuthorityId.ToString() ?? "(none)",
            fallback.LastConsumingInstallReceipt?.InstallGeneration ?? 0,
            fallback.CurrentRetryDispatchHold?.HoldGeneration ?? 0,
            fallback.PendingRetryDispatchRecheckCommand?.RecheckGeneration ??
                fallback.LastRetryDispatchRecheckReceipt?.Command
                    .RecheckGeneration ?? 0,
            operation.InvalidationReason ?? "(none)",
            receipts);
    }

    private static ImmutableArray<
        FileTransferCoordinatorReceiptTransitionSnapshot>
        CreateFallbackTailYieldReceiptSnapshots(
            FileTransferFallbackTailYieldOperation operation)
    {
        var builder = ImmutableArray.CreateBuilder<
            FileTransferCoordinatorReceiptTransitionSnapshot>();
        var decision = operation.Decision;
        var authorityId = decision.AuthorityId.ToString();
        builder.Add(new FileTransferCoordinatorReceiptTransitionSnapshot(
            "yield",
            $"{authorityId}:yield",
            decision.YieldGeneration,
            CalculateSnapshotFingerprint(decision),
            $"{authorityId}:yield:decision",
            decision.YieldGeneration,
            decision.Accepted ? "Accepted" : "Rejected",
            decision.Reason,
            CalculateSnapshotFingerprint(new
            {
                decision.Accepted,
                decision.Reason,
                decision.YieldKind,
                decision.OwnerKey,
                decision.PairKey,
            }),
            decision.Accepted && !decision.IsDefault));
        if (operation.LocalProof is { } local)
        {
            builder.Add(new FileTransferCoordinatorReceiptTransitionSnapshot(
                "local_proof",
                $"{authorityId}:local_proof",
                decision.YieldGeneration,
                CalculateSnapshotFingerprint(local.Decision),
                local.ProofIdentity,
                decision.YieldGeneration,
                "Observed",
                "fallback_tail_local_proof_observed",
                CalculateSnapshotFingerprint(local),
                !local.IsDefault && local.Decision == decision));
        }
        if (operation.PeerProof is { } peer)
        {
            builder.Add(new FileTransferCoordinatorReceiptTransitionSnapshot(
                "peer_proof",
                $"{peer.Proof.AuthorityId}:peer_proof",
                peer.Proof.YieldGeneration,
                CalculateSnapshotFingerprint(peer.Proof),
                peer.ObservationIdentity,
                peer.Proof.YieldGeneration,
                "Observed",
                "fallback_tail_peer_proof_observed",
                CalculateSnapshotFingerprint(peer),
                !peer.IsDefault));
        }
        if (operation.PairedRelease is { } release)
        {
            builder.Add(new FileTransferCoordinatorReceiptTransitionSnapshot(
                "paired_release",
                $"{authorityId}:pair:{release.PairingGeneration}",
                release.PairingGeneration,
                CalculateSnapshotFingerprint(new
                {
                    release.LocalProof,
                    release.PeerProof,
                }),
                $"{authorityId}:pair:{release.PairingGeneration}:receipt",
                release.PairingGeneration,
                "Paired",
                release.ReleaseOutcome,
                CalculateSnapshotFingerprint(release),
                !release.IsDefault));
        }
        if (operation.PairedReleaseCarryForward is { } carryForward)
        {
            builder.Add(new FileTransferCoordinatorReceiptTransitionSnapshot(
                "carry_forward",
                $"{authorityId}:carry:{carryForward.TargetScope.TransportEpoch}",
                carryForward.TargetScope.TransportEpoch,
                CalculateSnapshotFingerprint(carryForward.SourceScope),
                $"{authorityId}:carry:{carryForward.TargetScope.TransportEpoch}:receipt",
                carryForward.TargetScope.TransportEpoch,
                "Carried",
                carryForward.Reason,
                CalculateSnapshotFingerprint(carryForward),
                !carryForward.IsDefault));
        }
        if (operation.SourceReauthorization is { } sourceReauthorization)
        {
            var exact = sourceReauthorization.Reauthorization;
            builder.Add(new FileTransferCoordinatorReceiptTransitionSnapshot(
                "transport_reauthorization",
                $"{authorityId}:reauthorization:{exact.ReauthorizationGeneration}",
                exact.ReauthorizationGeneration,
                CalculateSnapshotFingerprint(exact),
                $"{authorityId}:reauthorization:{exact.ReauthorizationGeneration}:receipt",
                exact.ReauthorizationGeneration,
                "Applied",
                exact.Reason,
                CalculateSnapshotFingerprint(sourceReauthorization),
                !sourceReauthorization.IsDefault));
        }
        if (operation.FailedYieldSupersession is { } supersession)
        {
            builder.Add(new FileTransferCoordinatorReceiptTransitionSnapshot(
                "failed_supersession",
                $"{supersession.FailedDecision.AuthorityId}:supersede",
                supersession.FailedDecision.YieldGeneration,
                CalculateSnapshotFingerprint(supersession.FailedDecision),
                $"{authorityId}:supersession:receipt",
                decision.YieldGeneration,
                "Superseded",
                supersession.Reason,
                CalculateSnapshotFingerprint(supersession),
                !supersession.IsDefault));
        }
        if (!string.IsNullOrWhiteSpace(operation.InvalidationReason))
        {
            builder.Add(new FileTransferCoordinatorReceiptTransitionSnapshot(
                "invalidation",
                $"{authorityId}:invalidate",
                decision.YieldGeneration,
                CalculateSnapshotFingerprint(operation),
                $"{authorityId}:invalidate:receipt",
                decision.YieldGeneration,
                "Invalidated",
                operation.InvalidationReason,
                CalculateSnapshotFingerprint(new
                {
                    operation.Decision,
                    operation.InvalidationReason,
                }),
                true));
        }
        return builder.ToImmutable();
    }

    private static FileTransferCoordinatorStateBoundarySnapshot
        CreateFallbackTailYieldScopeBoundary(
            FileTransferFallbackTailYieldScope scope)
        => new(
            scope.RouteToken,
            scope.ProtocolVersion,
            scope.LiveRouteEpoch,
            scope.RoutePublication?.PublicationGeneration ?? 0,
            scope.RoutePublication?.RouteCommitGeneration ?? 0,
            scope.RuntimeOwner.RuntimeGeneration,
            scope.TerminalGeneration,
            scope.LegGeneration,
            scope.TransportEpoch,
            scope.BridgeRecoveryGeneration,
            scope.CheckpointGeneration,
            scope.CheckpointRequestIdSnapshot,
            scope.DataSessionIdentity ?? "(none)",
            scope.DataSessionGeneration);

    private static FileTransferCoordinatorStateBoundarySnapshot
        CreateFallbackTailYieldReauthorizationSourceBoundary(
            FallbackTailYieldTransportEpochReauthorization reauthorization)
        => new(
            reauthorization.SourceRuntimeOwner.RouteToken,
            reauthorization.SourceRuntimeOwner.ProtocolVersion,
            reauthorization.SourceRuntimeOwner.LiveRouteEpoch,
            reauthorization.SourceRoutePublicationToken.PublicationGeneration,
            reauthorization.SourceRoutePublicationToken.RouteCommitGeneration ?? 0,
            reauthorization.SourceRuntimeOwner.RuntimeGeneration,
            reauthorization.SourceRuntimeOwner.TerminalGeneration,
            reauthorization.PredecessorDecision.CurrentLegGeneration,
            reauthorization.SourceTransportEpoch,
            reauthorization.PredecessorDecision.CurrentBridgeRecoveryGeneration,
            reauthorization.PredecessorDecision.CheckpointLease?
                .CheckpointGeneration ?? 0,
            reauthorization.PredecessorDecision.CheckpointRequestIdSnapshot,
            reauthorization.TargetDataSessionIdentity ?? "(none)",
            reauthorization.TargetDataSessionGeneration);

    private static FileTransferCoordinatorStateBoundarySnapshot
        CreateFallbackTailYieldReauthorizationTargetBoundary(
            FallbackTailYieldTransportEpochReauthorization reauthorization)
        => new(
            reauthorization.TargetRuntimeOwner.RouteToken,
            reauthorization.TargetRuntimeOwner.ProtocolVersion,
            reauthorization.TargetRuntimeOwner.LiveRouteEpoch,
            reauthorization.TargetRoutePublicationToken?.PublicationGeneration ?? 0,
            reauthorization.TargetRoutePublicationToken?.RouteCommitGeneration ?? 0,
            reauthorization.TargetRuntimeOwner.RuntimeGeneration,
            reauthorization.TargetTerminalGeneration,
            reauthorization.TargetLegGeneration,
            reauthorization.TargetTransportEpoch,
            reauthorization.TargetBridgeRecoveryGeneration,
            reauthorization.TargetCheckpointGeneration,
            reauthorization.TargetCheckpointRequestIdSnapshot,
            reauthorization.TargetDataSessionIdentity ?? "(none)",
            reauthorization.TargetDataSessionGeneration);

    private static CoordinatorOperationDescriptor
        CreateRuntimeUnlockRetryAttemptDescriptor(
            FileTransferCoordinatorState state,
            string role,
            RuntimeUnlockRetryLifecycleAttemptSnapshot attempt)
    {
        var receipts = CreateRuntimeUnlockRetryReceiptSnapshots(attempt);
        var latest = receipts.IsDefaultOrEmpty ? null : receipts[^1];
        var authority = attempt.Authority;
        var authorityId = authority.AuthorityId.ToString();
        return new CoordinatorOperationDescriptor(
            "runtime_unlock_retry_lifecycle",
            "RetryAttempt",
            authorityId,
            authority.PredecessorAuthorityId?.ToString() ?? "(none)",
            authority.AuthorityGeneration,
            attempt.State.ToString(),
            latest?.CommandIdentity ?? $"{authorityId}:authorize",
            latest?.CommandGeneration ?? authority.AuthorityGeneration,
            latest?.CommandFingerprint ??
                CalculateRuntimeUnlockRetryCommandFingerprint(attempt),
            latest?.ReceiptIdentity ?? "(none)",
            latest?.ReceiptGeneration ?? 0,
            latest?.ReceiptStatus ?? "(none)",
            latest?.ReceiptFingerprint ?? "(none)",
            latest?.ReceiptValidated ?? false,
            OperationRole: role,
            OperationKey: CreateCoordinatorOperationKey(
                "runtime_unlock_retry_lifecycle",
                role,
                authorityId,
                authority.AuthorityGeneration,
                "RetryAttempt"),
            RuntimeUnlockRetry: CreateRuntimeUnlockRetrySnapshot(
                state,
                role,
                attempt,
                receipts));
    }

    private static CoordinatorOperationDescriptor
        CreateRuntimeUnlockRetryClosureDescriptor(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryLifecycleBoundedClosure closure)
    {
        var attempt = closure.FinalAttempt;
        var authority = attempt.Authority;
        var authorityId = authority.AuthorityId.ToString();
        var receipt = new FileTransferCoordinatorReceiptTransitionSnapshot(
            "bounded_closure",
            $"{authorityId}:successor:{closure.RejectedSuccessorRequest.DispatchAttemptGeneration}",
            closure.RejectedSuccessorRequest.DispatchAttemptGeneration,
            CalculateSnapshotFingerprint(closure.RejectedSuccessorRequest),
            $"{authorityId}:closure:{closure.ClosureGeneration}",
            closure.ClosureGeneration,
            "Closed",
            closure.Reason,
            CalculateSnapshotFingerprint(closure),
            !closure.IsDefault &&
            closure.FinalAttempt.State ==
                RuntimeUnlockRetryLifecycleAttemptState.Closed);
        return new CoordinatorOperationDescriptor(
            "runtime_unlock_retry_lifecycle",
            "BoundedClosure",
            authorityId,
            authority.PredecessorAuthorityId?.ToString() ?? "(none)",
            closure.ClosureGeneration,
            "Closed",
            receipt.CommandIdentity,
            receipt.CommandGeneration,
            receipt.CommandFingerprint,
            receipt.ReceiptIdentity,
            receipt.ReceiptGeneration,
            receipt.ReceiptStatus,
            receipt.ReceiptFingerprint,
            receipt.ReceiptValidated,
            OperationRole: "bounded_closure",
            OperationKey: CreateCoordinatorOperationKey(
                "runtime_unlock_retry_lifecycle",
                "bounded_closure",
                authorityId,
                closure.ClosureGeneration,
                "BoundedClosure"),
            RuntimeUnlockRetry: CreateRuntimeUnlockRetrySnapshot(
                state,
                "bounded_closure",
                attempt,
                ImmutableArray.Create(receipt)));
    }

    private static CoordinatorOperationDescriptor
        CreateRuntimeUnlockRetryInvalidationDescriptor(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchScopeInvalidationCommand command,
            RuntimeUnlockRetryDispatchScopeInvalidationReceipt? receipt)
    {
        var attempt = command.PendingAttempt ??
                      command.InstalledAttempt ??
                      command.RetainedPredecessor ??
                      command.BoundedClosure?.FinalAttempt;
        var authorityId = attempt?.Authority.AuthorityId.ToString() ??
                          command.CommandId.ToString();
        RuntimeUnlockRetryDispatchScopeInvalidationReceipt? exactReceipt =
            receipt is { } candidate && candidate.Command == command
                ? candidate
                : null;
        var receiptSnapshot = new FileTransferCoordinatorReceiptTransitionSnapshot(
            "scope_invalidation",
            command.CommandId.ToString(),
            command.InvalidationGeneration,
            CalculateSnapshotFingerprint(command),
            exactReceipt?.Command.CommandId.ToString() ?? "(none)",
            exactReceipt?.Command.InvalidationGeneration ?? 0,
            exactReceipt?.Status.ToString() ?? "Pending",
            exactReceipt?.Reason ?? command.Reason,
            CalculateSnapshotFingerprint(exactReceipt),
            exactReceipt?.Accepted == true);
        return new CoordinatorOperationDescriptor(
            "runtime_unlock_retry_lifecycle",
            "ScopeInvalidation",
            authorityId,
            attempt?.Authority.PredecessorAuthorityId?.ToString() ?? "(none)",
            command.InvalidationGeneration,
            exactReceipt is null ? "Pending" : exactReceipt.Value.Status.ToString(),
            receiptSnapshot.CommandIdentity,
            receiptSnapshot.CommandGeneration,
            receiptSnapshot.CommandFingerprint,
            receiptSnapshot.ReceiptIdentity,
            receiptSnapshot.ReceiptGeneration,
            receiptSnapshot.ReceiptStatus,
            receiptSnapshot.ReceiptFingerprint,
            receiptSnapshot.ReceiptValidated,
            OperationRole: "scope_invalidation",
            OperationKey: CreateCoordinatorOperationKey(
                "runtime_unlock_retry_lifecycle",
                "scope_invalidation",
                command.CommandId.ToString(),
                command.InvalidationGeneration,
                "ScopeInvalidation"),
            RuntimeUnlockRetry: attempt is { } exactAttempt
                ? CreateRuntimeUnlockRetrySnapshot(
                    state,
                    "scope_invalidation",
                    exactAttempt,
                    ImmutableArray.Create(receiptSnapshot))
                : null);
    }

    private static FileTransferCoordinatorRuntimeUnlockRetryOperationSnapshot
        CreateRuntimeUnlockRetrySnapshot(
            FileTransferCoordinatorState state,
            string role,
            RuntimeUnlockRetryLifecycleAttemptSnapshot attempt,
            ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot>
                receipts)
    {
        var authority = attempt.Authority;
        return new(
            role,
            CreateRuntimeUnlockRetrySourceBoundary(state, authority),
            authority.AuthorityGeneration,
            authority.RecoveryContractGeneration,
            authority.DispatchAttemptGeneration,
            authority.MissingDispatchSuccessorGeneration,
            authority.RetiredOfferGeneration,
            attempt.OfferGeneration,
            CalculateSnapshotFingerprint(attempt.OfferNonce),
            attempt.DispatchDeadlineUtcMs,
            attempt.DispatchObserved,
            authority.ReleaseKind.ToString(),
            authority.ReleaseAuthorityId?.ToString() ?? "(none)",
            authority.ReleaseGeneration,
            authority.ReleaseOutcome,
            authority.ListenerRearmRequirement.ToString(),
            authority.ListenerExecutionMode.ToString(),
            attempt.Request.RequestActivePredecessorSupersession,
            attempt.SuccessorAuthorityId?.ToString() ?? "(none)",
            receipts);
    }

    private static FileTransferCoordinatorStateBoundarySnapshot
        CreateRuntimeUnlockRetrySourceBoundary(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchAuthority authority)
        => new(
            authority.RouteToken,
            authority.ProtocolVersion,
            authority.LiveRouteEpoch,
            authority.RoutePublicationGeneration,
            authority.RouteCommitGeneration ?? 0,
            authority.RuntimeGeneration,
            authority.TerminalGeneration,
            authority.LegGeneration,
            authority.TransportEpoch,
            authority.BridgeRecoveryGeneration,
            authority.CheckpointGeneration,
            authority.CheckpointRequestId,
            authority.DataSessionIdentity,
            authority.DataSessionGeneration);

    private static ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot>
        CreateRuntimeUnlockRetryReceiptSnapshots(
            RuntimeUnlockRetryLifecycleAttemptSnapshot attempt)
    {
        var builder = ImmutableArray.CreateBuilder<
            FileTransferCoordinatorReceiptTransitionSnapshot>();
        var authority = attempt.Authority;
        var authorityId = authority.AuthorityId.ToString();
        if (attempt.StageStatus is { } stageStatus)
        {
            builder.Add(new FileTransferCoordinatorReceiptTransitionSnapshot(
                "stage",
                $"{authorityId}:stage",
                authority.DispatchAttemptGeneration,
                CalculateSnapshotFingerprint(attempt.Request),
                $"{authorityId}:stage",
                authority.DispatchAttemptGeneration,
                stageStatus.ToString(),
                attempt.StageDisposition.ToString(),
                CalculateSnapshotFingerprint(new
                {
                    stageStatus,
                    attempt.StageDisposition,
                    attempt.StagedUtcMs,
                    attempt.StageExpiresUtcMs,
                }),
                stageStatus is
                    RuntimeUnlockRetryDispatchDecisionStageStatus.Staged or
                    RuntimeUnlockRetryDispatchDecisionStageStatus.Idempotent));
        }
        if (attempt.InstallReceipt is { IsDefault: false } install)
        {
            builder.Add(new FileTransferCoordinatorReceiptTransitionSnapshot(
                "install",
                $"{authorityId}:install",
                install.InstallGeneration,
                CalculateSnapshotFingerprint(install.Request),
                $"{authorityId}:install",
                install.InstallGeneration,
                attempt.State.ToString(),
                "retry_dispatch_install_receipt",
                CalculateSnapshotFingerprint(install),
                install.Authority == authority &&
                install.Request == attempt.Request));
        }
        if (attempt.State is
                RuntimeUnlockRetryLifecycleAttemptState.AliasBound or
                RuntimeUnlockRetryLifecycleAttemptState.SendInFlight or
                RuntimeUnlockRetryLifecycleAttemptState.Settled &&
            attempt.OfferGeneration > 0)
        {
            builder.Add(new FileTransferCoordinatorReceiptTransitionSnapshot(
                "execution",
                $"{authorityId}:execution",
                attempt.OfferGeneration,
                CalculateSnapshotFingerprint(attempt.InstallReceipt),
                $"{authorityId}:offer:{attempt.OfferGeneration}",
                attempt.OfferGeneration,
                attempt.State.ToString(),
                attempt.DispatchObserved ? "dispatch_observed" : "offer_bound",
                CalculateSnapshotFingerprint(new
                {
                    attempt.OfferGeneration,
                    attempt.OfferNonce,
                    attempt.DispatchObserved,
                    attempt.State,
                }),
                !string.IsNullOrWhiteSpace(attempt.OfferNonce)));
        }
        if (attempt.Settlement is { IsDefault: false } settlement)
        {
            builder.Add(new FileTransferCoordinatorReceiptTransitionSnapshot(
                "settlement",
                $"{authorityId}:settlement",
                Math.Max(1, attempt.OfferGeneration),
                CalculateSnapshotFingerprint(new
                {
                    settlement.Authority,
                    settlement.OfferGeneration,
                    settlement.OfferNonce,
                }),
                $"{authorityId}:settlement",
                Math.Max(1, attempt.OfferGeneration),
                settlement.Outcome.ToString(),
                settlement.Reason,
                CalculateSnapshotFingerprint(settlement),
                settlement.Authority == authority &&
                settlement.OfferGeneration == attempt.OfferGeneration &&
                string.Equals(
                    settlement.OfferNonce,
                    attempt.OfferNonce,
                    StringComparison.Ordinal) &&
                settlement.DispatchObserved == attempt.DispatchObserved));
        }
        return builder.ToImmutable();
    }

    private static string CalculateRuntimeUnlockRetryCommandFingerprint(
        RuntimeUnlockRetryLifecycleAttemptSnapshot attempt)
        => CalculateSnapshotFingerprint(new
        {
            attempt.Request,
            attempt.Authority,
            attempt.StageStatus,
            attempt.StageDisposition,
            attempt.StagedUtcMs,
            attempt.StageExpiresUtcMs,
            attempt.DispatchDeadlineUtcMs,
            attempt.OfferGeneration,
            attempt.OfferNonce,
            attempt.DispatchObserved,
            attempt.SuccessorAuthorityId,
        });

    private static void AddReceiveRecoveryOperationDescriptors(
        FileTransferCoordinatorState state,
        ImmutableArray<CoordinatorOperationDescriptor>.Builder builder)
    {
        var recovery = state.PersistentState.FileTransferReceiveRecovery;
        if (recovery.CurrentOperation is { } current)
        {
            builder.Add(CreateReceiveRecoveryOperationDescriptor(
                state,
                current,
                "operation_current"));
        }
        if (recovery.LastTerminalOperation is { } terminal &&
            (recovery.CurrentOperation is null ||
             terminal.Authority != recovery.CurrentOperation.Value.Authority))
        {
            builder.Add(CreateReceiveRecoveryOperationDescriptor(
                state,
                terminal,
                "operation_terminal"));
        }

        var proof = recovery.ProofStatePublication;
        if (proof?.PendingCommand is { } pendingProof)
        {
            AddReceiveRecoveryChildDescriptor(
                state,
                builder,
                pendingProof.Authority,
                "proof_publication_pending",
                "ProofPublication",
                pendingProof.PublicationGeneration,
                "Pending",
                pendingProof,
                null,
                "Pending");
        }
        if (proof?.LastReceipt is { } proofReceipt)
        {
            AddReceiveRecoveryChildDescriptor(
                state,
                builder,
                proofReceipt.Command.Authority,
                "proof_publication_terminal",
                "ProofPublication",
                proofReceipt.Command.PublicationGeneration,
                proofReceipt.Outcome.ToString(),
                proofReceipt.Command,
                proofReceipt,
                proofReceipt.Outcome.ToString(),
                proofReceipt.Outcome !=
                    FileTransferReceiveRecoveryProofStatePublicationOutcome.Rejected);
        }
        if (proof?.PendingRecheckArm is { } recheckArm)
        {
            AddReceiveRecoveryChildDescriptor(
                state,
                builder,
                recheckArm.PredecessorReceipt.Command.Authority,
                "proof_publication_recheck",
                "ProofPublicationRecheck",
                recheckArm.PredecessorReceipt.Command.PublicationGeneration,
                "Armed",
                recheckArm,
                proof.LastRecheckEvidence,
                proof.LastRecheckEvidence is null ? "Pending" : "Observed",
                proof.LastRecheckEvidence is not null);
        }

        AddReceiveRecoveryInitialActivationDescriptors(state, recovery, builder);
        AddReceiveRecoveryCheckpointDescriptors(state, recovery, builder);
    }

    private static void AddReceiveRecoveryInitialActivationDescriptors(
        FileTransferCoordinatorState state,
        FileTransferReceiveRecoverySubstate recovery,
        ImmutableArray<CoordinatorOperationDescriptor>.Builder builder)
    {
        if (recovery.CurrentInitialActivationHold is { } currentHold)
        {
            AddReceiveRecoveryChildDescriptor(
                state,
                builder,
                currentHold.ReceiveRecoveryAuthority,
                "initial_activation_hold_current",
                "InitialActivationHold",
                currentHold.HoldGeneration,
                currentHold.Status.ToString(),
                currentHold.Request,
                currentHold,
                currentHold.Status.ToString(),
                true);
        }
        if (recovery.LastInitialActivationHold is { } terminalHold &&
            (recovery.CurrentInitialActivationHold is null ||
             terminalHold.HoldGeneration !=
                 recovery.CurrentInitialActivationHold.HoldGeneration))
        {
            AddReceiveRecoveryChildDescriptor(
                state,
                builder,
                terminalHold.ReceiveRecoveryAuthority,
                "initial_activation_hold_terminal",
                "InitialActivationHold",
                terminalHold.HoldGeneration,
                terminalHold.Status.ToString(),
                terminalHold.Request,
                terminalHold,
                terminalHold.Status.ToString(),
                true);
        }

        var refreshCommand =
            recovery.PendingInitialActivationEnvelopeRefresh ??
            recovery.LastInitialActivationEnvelopeRefreshReceipt?.Command;
        if (refreshCommand is not null)
        {
            var refreshReceipt =
                recovery.LastInitialActivationEnvelopeRefreshReceipt is
                    { } candidate && candidate.Command == refreshCommand
                    ? candidate
                    : null;
            AddReceiveRecoveryChildDescriptor(
                state,
                builder,
                refreshCommand.ReceiveRecoveryAuthority,
                "initial_activation_envelope_refresh",
                "InitialActivationEnvelopeRefresh",
                refreshCommand.RefreshGeneration,
                refreshReceipt is null ? "Pending" : "Committed",
                refreshCommand,
                refreshReceipt,
                refreshReceipt is null ? "Pending" : "Accepted",
                refreshReceipt is not null);
        }
    }

    private static void AddReceiveRecoveryCheckpointDescriptors(
        FileTransferCoordinatorState state,
        FileTransferReceiveRecoverySubstate recovery,
        ImmutableArray<CoordinatorOperationDescriptor>.Builder builder)
    {
        var checkpoint = recovery.ProofStatePublication?.FallbackCheckpoint;
        if (checkpoint is null)
        {
            return;
        }

        if (checkpoint.CurrentBinding is { } currentBinding)
        {
            AddReceiveRecoveryCheckpointChildDescriptor(
                state, builder, currentBinding,
                "checkpoint_binding_current",
                "FallbackCheckpointBinding",
                currentBinding.BindingGeneration,
                currentBinding.State.ToString(),
                currentBinding,
                null,
                "Pending");
        }
        if (checkpoint.LastTerminalBinding is { } terminalBinding &&
            (checkpoint.CurrentBinding is null ||
             terminalBinding.BindingGeneration !=
                 checkpoint.CurrentBinding.BindingGeneration))
        {
            AddReceiveRecoveryCheckpointChildDescriptor(
                state, builder, terminalBinding,
                "checkpoint_binding_terminal",
                "FallbackCheckpointBinding",
                terminalBinding.BindingGeneration,
                terminalBinding.State.ToString(),
                terminalBinding,
                terminalBinding,
                terminalBinding.State.ToString(),
                terminalBinding.State is
                    FileTransferReceiveRecoveryFallbackCheckpointBindingState.Accepted or
                    FileTransferReceiveRecoveryFallbackCheckpointBindingState.Retired);
        }
        if (checkpoint.PendingDeliveryCommand is { } delivery)
        {
            AddReceiveRecoveryCheckpointChildDescriptor(
                state, builder, delivery.Binding,
                "checkpoint_delivery_pending",
                "FallbackCheckpointDelivery",
                delivery.DeliveryGeneration,
                "Pending",
                delivery,
                null,
                "Pending");
        }
        if (checkpoint.LastDeliveryReceipt is { } deliveryReceipt)
        {
            AddReceiveRecoveryCheckpointChildDescriptor(
                state, builder, deliveryReceipt.Command.Binding,
                "checkpoint_delivery_terminal",
                "FallbackCheckpointDelivery",
                deliveryReceipt.Command.DeliveryGeneration,
                deliveryReceipt.State.ToString(),
                deliveryReceipt.Command,
                deliveryReceipt,
                deliveryReceipt.State.ToString(),
                deliveryReceipt.State !=
                    FileTransferReceiveRecoveryFallbackCheckpointDeliveryState.Rejected);
        }
        var resume = checkpoint.PendingResumeReservation ??
                     checkpoint.LastResumeReservation;
        if (resume is not null)
        {
            var resumeCompleted = checkpoint.LastResumeReservation == resume;
            AddReceiveRecoveryCheckpointChildDescriptor(
                state, builder, resume.Command.Binding,
                "checkpoint_resume",
                "FallbackCheckpointResume",
                resume.Command.DeliveryGeneration,
                resumeCompleted ? "Completed" : "Reserved",
                resume,
                resumeCompleted ? resume : null,
                resumeCompleted ? "Accepted" : "Pending",
                resumeCompleted);
        }
        if (checkpoint.PendingFrontierRequestHold is { } frontierHold)
        {
            AddReceiveRecoveryCheckpointChildDescriptor(
                state, builder, frontierHold.Observation.Binding,
                "checkpoint_frontier_hold",
                "FallbackCheckpointFrontierHold",
                frontierHold.HoldGeneration,
                "Held",
                frontierHold.Observation,
                frontierHold,
                "Held",
                true);
        }
        var frontierRecheck = checkpoint.PendingFrontierRequestRecheckCommand ??
                              checkpoint.LastFrontierRequestRecheckReceipt?.Command;
        if (frontierRecheck is not null)
        {
            var recheckReceipt =
                checkpoint.LastFrontierRequestRecheckReceipt is { } candidate &&
                candidate.Command == frontierRecheck
                    ? candidate
                    : null;
            AddReceiveRecoveryCheckpointChildDescriptor(
                state, builder, frontierRecheck.AcceptedBinding,
                "checkpoint_frontier_recheck",
                "FallbackCheckpointFrontierRecheck",
                frontierRecheck.RecheckGeneration,
                recheckReceipt?.Status.ToString() ?? "Pending",
                frontierRecheck,
                recheckReceipt,
                recheckReceipt?.Status.ToString() ?? "Pending",
                recheckReceipt?.Status is
                    FileTransferReceiveRecoveryFallbackCheckpointFrontierRequestRecheckStatus.Applied or
                    FileTransferReceiveRecoveryFallbackCheckpointFrontierRequestRecheckStatus.Idempotent);
        }
    }

    private static void AddReceiveRecoveryCheckpointChildDescriptor(
        FileTransferCoordinatorState state,
        ImmutableArray<CoordinatorOperationDescriptor>.Builder builder,
        FileTransferReceiveRecoveryFallbackCheckpointBinding binding,
        string role,
        string kind,
        long generation,
        string lifecycleState,
        object command,
        object? receipt,
        string receiptStatus,
        bool receiptValidated = false)
        => AddReceiveRecoveryChildDescriptor(
            state,
            builder,
            binding.ParentAuthority,
            role,
            kind,
            generation,
            lifecycleState,
            command,
            receipt,
            receiptStatus,
            receiptValidated,
            CreateReceiveRecoveryEffectiveBoundary(binding));

    private static void AddReceiveRecoveryChildDescriptor(
        FileTransferCoordinatorState state,
        ImmutableArray<CoordinatorOperationDescriptor>.Builder builder,
        FileTransferReceiveRecoveryOperationAuthority authority,
        string role,
        string kind,
        long generation,
        string lifecycleState,
        object command,
        object? receipt,
        string receiptStatus,
        bool receiptValidated = false,
        FileTransferCoordinatorStateBoundarySnapshot? effectiveBoundary = null)
    {
        var operation = FindReceiveRecoveryOperation(state, authority);
        var receiptSnapshot = new FileTransferCoordinatorReceiptTransitionSnapshot(
            role,
            $"{authority.AuthorityId}:{role}",
            generation,
            CalculateSnapshotFingerprint(command),
            receipt is null ? "(none)" : $"{authority.AuthorityId}:{role}:receipt",
            receipt is null ? 0 : generation,
            receiptStatus,
            receiptStatus,
            receipt is null ? "(none)" : CalculateSnapshotFingerprint(receipt),
            receiptValidated);
        builder.Add(CreateReceiveRecoveryDescriptor(
            state,
            authority,
            operation,
            role,
            kind,
            generation,
            lifecycleState,
            receiptSnapshot,
            effectiveBoundary));
    }

    private static CoordinatorOperationDescriptor
        CreateReceiveRecoveryOperationDescriptor(
            FileTransferCoordinatorState state,
            FileTransferReceiveRecoveryOperation operation,
            string role)
    {
        var authority = operation.Authority;
        return CreateReceiveRecoveryDescriptor(
            state,
            authority,
            operation,
            role,
            authority.BridgeRecoveryDecision.RecoveryKind.ToString(),
            authority.OperationGeneration,
            $"{operation.ExecutionState}:{operation.ClosureState}",
            CreateReceiveRecoveryOperationReceipt(operation),
            null);
    }

    private static CoordinatorOperationDescriptor CreateReceiveRecoveryDescriptor(
        FileTransferCoordinatorState state,
        FileTransferReceiveRecoveryOperationAuthority authority,
        FileTransferReceiveRecoveryOperation? operation,
        string role,
        string kind,
        long generation,
        string lifecycleState,
        FileTransferCoordinatorReceiptTransitionSnapshot receipt,
        FileTransferCoordinatorStateBoundarySnapshot? effectiveBoundary)
    {
        var recovery = state.PersistentState.FileTransferReceiveRecovery;
        var checkpoint = recovery.ProofStatePublication?.FallbackCheckpoint;
        var heldRetryAuthority = operation?.HeldRetryRequest?.PredecessorAuthorityId;
        var reservedRetryAuthority = operation?.ReservedRetryAuthority?.AuthorityId;
        var receipts = receipt.ReceiptIdentity == "(none)"
            ? ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot>.Empty
            : ImmutableArray.Create(receipt);
        return new CoordinatorOperationDescriptor(
            "receive_recovery",
            kind,
            authority.AuthorityId.ToString(),
            authority.PredecessorAuthorityId?.ToString() ?? "(none)",
            generation,
            lifecycleState,
            receipt.CommandIdentity,
            receipt.CommandGeneration,
            receipt.CommandFingerprint,
            receipt.ReceiptIdentity,
            receipt.ReceiptGeneration,
            receipt.ReceiptStatus,
            receipt.ReceiptFingerprint,
            receipt.ReceiptValidated,
            OperationRole: role,
            OperationKey: CreateCoordinatorOperationKey(
                "receive_recovery",
                role,
                authority.AuthorityId.ToString(),
                generation,
                kind),
            ReceiveRecovery: new(
                role,
                CreateReceiveRecoverySourceBoundary(state, authority.Observation),
                effectiveBoundary ??
                    CreateReceiveRecoverySourceBoundary(state, authority.Observation),
                authority.BridgeRecoveryDecision.RecoveryKind.ToString(),
                authority.BridgeRecoveryDecision.BridgeDecisionGeneration,
                authority.RecoveryLivenessLease?.LeaseGeneration ?? 0,
                operation?.ExecutionState.ToString() ?? "None",
                operation?.ClosureState.ToString() ?? "None",
                operation?.ClosureGeneration ?? 0,
                operation?.ReceiveProof?.ProofIdentity ?? "(none)",
                operation?.ReceiveProof?.ObservedUtcMs ?? 0,
                heldRetryAuthority?.ToString() ?? "(none)",
                reservedRetryAuthority?.ToString() ?? "(none)",
                recovery.CurrentInitialActivationHold?.HoldGeneration ??
                    recovery.LastInitialActivationHold?.HoldGeneration ?? 0,
                recovery.PendingInitialActivationEnvelopeRefresh?.RefreshGeneration ??
                    recovery.LastInitialActivationEnvelopeRefreshReceipt?.Command.RefreshGeneration ?? 0,
                checkpoint?.CurrentBinding?.BindingGeneration ??
                    checkpoint?.LastTerminalBinding?.BindingGeneration ?? 0,
                checkpoint?.PendingDeliveryCommand?.DeliveryGeneration ??
                    checkpoint?.LastDeliveryReceipt?.Command.DeliveryGeneration ?? 0,
                checkpoint?.PendingResumeReservation?.Command.DeliveryGeneration ??
                    checkpoint?.LastResumeReservation?.Command.DeliveryGeneration ?? 0,
                checkpoint?.PendingFrontierRequestHold?.HoldGeneration ?? 0,
                checkpoint?.PendingFrontierRequestRecheckCommand?.RecheckGeneration ??
                    checkpoint?.LastFrontierRequestRecheckReceipt?.Command.RecheckGeneration ?? 0,
                receipts));
    }

    private static FileTransferCoordinatorReceiptTransitionSnapshot
        CreateReceiveRecoveryOperationReceipt(
            FileTransferReceiveRecoveryOperation operation)
    {
        var authority = operation.Authority;
        object? receipt = operation.ConsumingInstallReceipt ??
                          (object?)operation.ReceiveProof ??
                          (object?)operation.ExecutionReceipt ??
                          (operation.ExecutionStartResult is { } start
                              ? new
                              {
                                  start.Command,
                                  start.Status,
                                  start.Reason,
                              }
                              : null);
        var status = operation.ConsumingInstallReceipt is not null
            ? "Consumed"
            : operation.ReceiveProof is not null
                ? "Adopted"
                : operation.ExecutionReceipt?.Outcome.ToString() ??
                  operation.ExecutionStartStatus?.ToString() ?? "Pending";
        return new FileTransferCoordinatorReceiptTransitionSnapshot(
            "operation",
            authority.AuthorityId.ToString(),
            authority.OperationGeneration,
            CalculateSnapshotFingerprint(authority.ExecutionCommand),
            receipt is null ? "(none)" : $"{authority.AuthorityId}:operation:receipt",
            receipt is null ? 0 : Math.Max(
                authority.OperationGeneration,
                operation.ClosureGeneration),
            status,
            operation.InvalidationReason ??
                operation.ExecutionReceipt?.Reason ??
                operation.ExecutionStartReason ?? "(none)",
            receipt is null ? "(none)" : CalculateSnapshotFingerprint(receipt),
            receipt is not null);
    }

    private static FileTransferReceiveRecoveryOperation?
        FindReceiveRecoveryOperation(
            FileTransferCoordinatorState state,
            FileTransferReceiveRecoveryOperationAuthority authority)
    {
        var recovery = state.PersistentState.FileTransferReceiveRecovery;
        if (recovery.CurrentOperation is { } current &&
            current.Authority == authority)
        {
            return current;
        }
        if (recovery.LastTerminalOperation is { } terminal &&
            terminal.Authority == authority)
        {
            return terminal;
        }
        return null;
    }

    private static FileTransferCoordinatorStateBoundarySnapshot
        CreateReceiveRecoverySourceBoundary(
            FileTransferCoordinatorState state,
            FileTransferReceiveRecoveryObservation observation)
    {
        var checkpointGeneration =
            string.Equals(
                state.CurrentLeg?.CheckpointRequestId,
                observation.CheckpointRequestId,
                StringComparison.Ordinal)
                ? state.CurrentLeg?.CheckpointGeneration ?? 0
                : 0;
        return new(
            observation.RouteToken,
            observation.ProtocolVersion,
            observation.LiveRouteEpoch,
            observation.RoutePublication.PublicationGeneration,
            observation.RouteCommit?.CommitGeneration ?? 0,
            observation.RuntimeOwner.RuntimeGeneration,
            observation.TerminalGeneration,
            observation.LegGeneration,
            observation.TransportEpoch,
            observation.BridgeRecoveryGeneration,
            checkpointGeneration,
            observation.CheckpointRequestId ?? "(none)",
            observation.DataSessionIdentity,
            observation.DataSessionGeneration);
    }

    private static FileTransferCoordinatorStateBoundarySnapshot
        CreateReceiveRecoveryEffectiveBoundary(
            FileTransferReceiveRecoveryFallbackCheckpointBinding binding)
    {
        var source = binding.OriginalObservation;
        return new(
            source.RouteToken,
            source.ProtocolVersion,
            source.LiveRouteEpoch,
            source.RoutePublication.PublicationGeneration,
            source.RouteCommit?.CommitGeneration ?? 0,
            source.RuntimeOwner.RuntimeGeneration,
            source.TerminalGeneration,
            source.LegGeneration,
            binding.TransportEpoch,
            source.BridgeRecoveryGeneration,
            binding.EffectiveCheckpointGeneration,
            binding.EffectiveCheckpointRequestId,
            source.DataSessionIdentity,
            source.DataSessionGeneration);
    }

    private static void AddRouteRuntimeOperationDescriptors(
        FileTransferCoordinatorState state,
        ImmutableArray<CoordinatorOperationDescriptor>.Builder builder)
    {
        var boundary = CreateStateBoundarySnapshot(state);
        if (state.CurrentRuntimeOwner is { } runtimeOwner)
        {
            builder.Add(new CoordinatorOperationDescriptor(
                "route_runtime",
                "RuntimeOwner",
                $"runtime:{runtimeOwner.RuntimeGeneration}",
                "(none)",
                runtimeOwner.RuntimeGeneration,
                state.IsTerminal ? "Terminal" : "Current",
                $"runtime:{runtimeOwner.RuntimeGeneration}",
                runtimeOwner.RuntimeGeneration,
                CalculateSnapshotFingerprint(runtimeOwner),
                "(none)",
                0,
                "(none)",
                "(none)",
                false,
                OperationRole: "runtime_owner",
                OperationKey: CreateCoordinatorOperationKey(
                    "route_runtime",
                    "runtime_owner",
                    $"runtime:{runtimeOwner.RuntimeGeneration}",
                    runtimeOwner.RuntimeGeneration,
                    "RuntimeOwner"),
                RouteRuntime: new(
                    "runtime_owner",
                    boundary,
                    CalculateSnapshotFingerprint(runtimeOwner))));
        }

        if (state.CurrentRouteCommit is { } routeCommit)
        {
            builder.Add(new CoordinatorOperationDescriptor(
                "route_runtime",
                "RouteCommit",
                $"commit:{routeCommit.CommitGeneration}",
                "(none)",
                routeCommit.CommitGeneration,
                "Committed",
                $"commit:{routeCommit.CommitGeneration}",
                routeCommit.CommitGeneration,
                CalculateSnapshotFingerprint(routeCommit),
                $"commit:{routeCommit.CommitGeneration}",
                routeCommit.CommitGeneration,
                "Committed",
                CalculateSnapshotFingerprint(routeCommit),
                true,
                OperationRole: "route_commit",
                OperationKey: CreateCoordinatorOperationKey(
                    "route_runtime",
                    "route_commit",
                    $"commit:{routeCommit.CommitGeneration}",
                    routeCommit.CommitGeneration,
                    "RouteCommit"),
                RouteRuntime: new(
                    "route_commit",
                    boundary,
                    CalculateSnapshotFingerprint(routeCommit))));
        }

        if (state.CurrentRoutePublicationToken is { } publication)
        {
            builder.Add(new CoordinatorOperationDescriptor(
                "route_runtime",
                publication.ActionKind.ToString(),
                $"publication:{publication.PublicationGeneration}",
                publication.RouteCommitGeneration is { } predecessor
                    ? $"commit:{predecessor}"
                    : "(none)",
                publication.PublicationGeneration,
                "Published",
                $"publication:{publication.PublicationGeneration}",
                publication.PublicationGeneration,
                CalculateSnapshotFingerprint(publication),
                $"publication:{publication.PublicationGeneration}",
                publication.PublicationGeneration,
                "Accepted",
                CalculateSnapshotFingerprint(publication),
                true,
                OperationRole: "route_publication",
                OperationKey: CreateCoordinatorOperationKey(
                    "route_runtime",
                    "route_publication",
                    $"publication:{publication.PublicationGeneration}",
                    publication.PublicationGeneration,
                    publication.ActionKind.ToString()),
                RouteRuntime: new(
                    "route_publication",
                    boundary,
                    CalculateSnapshotFingerprint(publication))));
        }
    }

    private static CoordinatorOperationDescriptor
        CreateRouteTransitionOperationDescriptor(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionOperation operation,
            string role)
    {
        var receipts = CreateRouteTransitionReceiptSnapshots(operation);
        var latest = receipts.IsDefaultOrEmpty ? null : receipts[^1];
        var authority = operation.Authority;
        var operationKey = CreateCoordinatorOperationKey(
            "route_transition",
            role,
            authority.AuthorityId.ToString(),
            authority.TransitionGeneration,
            authority.Kind.ToString());
        return new CoordinatorOperationDescriptor(
            "route_transition",
            authority.Kind.ToString(),
            authority.AuthorityId.ToString(),
            authority.PredecessorTransitionAuthorityId?.ToString() ?? "(none)",
            authority.TransitionGeneration,
            operation.State.ToString(),
            latest?.CommandIdentity ?? authority.AuthorityId.ToString(),
            latest?.CommandGeneration ?? authority.TransitionGeneration,
            latest?.CommandFingerprint ??
                CalculateRouteTransitionCommandFingerprint(operation),
            latest?.ReceiptIdentity ?? "(none)",
            latest?.ReceiptGeneration ?? 0,
            latest?.ReceiptStatus ?? "(none)",
            latest?.ReceiptFingerprint ?? "(none)",
            latest?.ReceiptValidated ?? false,
            OperationRole: role,
            OperationKey: operationKey,
            RouteTransition: new(
                role,
                authority.Kind.ToString(),
                CreateRouteTransitionSourceBoundary(state, authority),
                CreateRouteTransitionTargetBoundary(state, authority),
                authority.SourceTransport.ToString(),
                authority.ProofBoundary.GetType().Name,
                GetRouteTransitionProofAdmissionMode(authority, operation),
                CalculateSnapshotFingerprint(authority.ProofBoundary),
                CalculateSnapshotFingerprint(authority.DurableCheckpoint),
                FileTransferRouteResolver.Resolve(authority.TargetRoute)
                    .RuntimeDescriptor.TelemetryToken,
                authority.TargetProtocolVersion,
                authority.TargetTransport.ToString(),
                authority.HandoffKind.ToString(),
                operation.DispositionCommand?.Kind.ToString() ?? "(none)",
                operation.RetainedFrameApplyCommand?.FrameIdentity.FrameType ??
                    "(none)",
                CalculateSnapshotFingerprint(
                    operation.RetainedFrameApplyCommand?.FrameIdentity),
                receipts));
    }

    private static CoordinatorOperationDescriptor
        CreateRouteTransitionSupersessionDescriptor(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionSupersession supersession)
    {
        var authority = supersession.SuccessorAuthority;
        var receipt = supersession.PauseRebindReceipt;
        var receiptSnapshot = receipt is { } exactReceipt
            ? CreateRouteTransitionReceiptSnapshot(
                "pause_rebind",
                $"{authority.AuthorityId}:pause_rebind",
                authority.TransitionGeneration,
                supersession.PauseRebindCommand,
                $"{authority.AuthorityId}:pause_rebind",
                authority.TransitionGeneration,
                exactReceipt.Status.ToString(),
                exactReceipt.Reason,
                exactReceipt,
                exactReceipt.Status is
                    FileTransferRouteTransitionPauseBindStatus.Installed or
                    FileTransferRouteTransitionPauseBindStatus.Idempotent)
            : null;
        var receipts = receiptSnapshot is null
            ? ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot>
                .Empty
            : ImmutableArray.Create(receiptSnapshot);
        return new CoordinatorOperationDescriptor(
            "route_transition",
            "Supersession",
            authority.AuthorityId.ToString(),
            supersession.PredecessorAuthority.AuthorityId.ToString(),
            authority.TransitionGeneration,
            receipt is null ? "Staged" : "Rebound",
            $"{authority.AuthorityId}:pause_rebind",
            authority.TransitionGeneration,
            CalculateSnapshotFingerprint(supersession.PauseRebindCommand),
            receiptSnapshot?.ReceiptIdentity ?? "(none)",
            receiptSnapshot?.ReceiptGeneration ?? 0,
            receiptSnapshot?.ReceiptStatus ?? "(none)",
            receiptSnapshot?.ReceiptFingerprint ?? "(none)",
            receiptSnapshot?.ReceiptValidated ?? false,
            OperationRole: "supersession",
            OperationKey: CreateCoordinatorOperationKey(
                "route_transition",
                "supersession",
                authority.AuthorityId.ToString(),
                authority.TransitionGeneration,
                "Supersession"),
            RouteTransition: new(
                "supersession",
                authority.Kind.ToString(),
                CreateRouteTransitionSourceBoundary(state, authority),
                CreateRouteTransitionTargetBoundary(state, authority),
                authority.SourceTransport.ToString(),
                authority.ProofBoundary.GetType().Name,
                GetRouteTransitionProofAdmissionMode(authority, null),
                CalculateSnapshotFingerprint(authority.ProofBoundary),
                CalculateSnapshotFingerprint(authority.DurableCheckpoint),
                FileTransferRouteResolver.Resolve(authority.TargetRoute)
                    .RuntimeDescriptor.TelemetryToken,
                authority.TargetProtocolVersion,
                authority.TargetTransport.ToString(),
                authority.HandoffKind.ToString(),
                "Superseded",
                "(none)",
                "(none)",
                receipts));
    }

    private static CoordinatorOperationDescriptor
        CreateFallbackV6ProofBootstrapDescriptor(
            FileTransferCoordinatorState state,
            FileTransferFallbackV6ProofBootstrapCommand command,
            FileTransferFallbackV6ProofBootstrapReceipt? receipt)
    {
        var exactReceipt = receipt is { } candidate &&
                           candidate.Command == command
            ? candidate
            : null;
        var receiptSnapshot = exactReceipt is null
            ? null
            : CreateRouteTransitionReceiptSnapshot(
                "fallback_v6_bootstrap",
                command.AuthorityId.ToString(),
                command.BootstrapGeneration,
                command,
                command.AuthorityId.ToString(),
                command.BootstrapGeneration,
                exactReceipt.Status.ToString(),
                exactReceipt.Reason,
                exactReceipt,
                exactReceipt.Status ==
                    FileTransferFallbackV6ProofBootstrapReceiptStatus.Delivered);
        var boundary = CreateStateBoundarySnapshot(state);
        return new CoordinatorOperationDescriptor(
            "route_transition",
            "FallbackV6ProofBootstrap",
            command.AuthorityId.ToString(),
            "(none)",
            command.BootstrapGeneration,
            exactReceipt is null ? "Pending" : exactReceipt.Status.ToString(),
            command.AuthorityId.ToString(),
            command.BootstrapGeneration,
            CalculateSnapshotFingerprint(command),
            receiptSnapshot?.ReceiptIdentity ?? "(none)",
            receiptSnapshot?.ReceiptGeneration ?? 0,
            receiptSnapshot?.ReceiptStatus ?? "(none)",
            receiptSnapshot?.ReceiptFingerprint ?? "(none)",
            receiptSnapshot?.ReceiptValidated ?? false,
            OperationRole: "fallback_v6_bootstrap",
            OperationKey: CreateCoordinatorOperationKey(
                "route_transition",
                "fallback_v6_bootstrap",
                command.AuthorityId.ToString(),
                command.BootstrapGeneration,
                "FallbackV6ProofBootstrap"),
            RouteTransition: new(
                "fallback_v6_bootstrap",
                "TunaV4ToPostTunaFallbackV6",
                boundary,
                CreateFallbackV6ProofBootstrapTargetBoundary(
                    state,
                    command.Observation),
                FileTransferTransportKind.Tuna.ToString(),
                command.Observation.ProofKind,
                "fallback_v6_proof_bootstrap",
                CalculateSnapshotFingerprint(command.Observation),
                CalculateSnapshotFingerprint(command.Observation.DurableCheckpoint),
                FileTransferRouteResolver.Resolve(
                        FileTransferRoute.PostTunaFallbackV6)
                    .RuntimeDescriptor.TelemetryToken,
                FileTransferProtocol.ProtocolVersionV6,
                FileTransferTransportKind.RegularNkn.ToString(),
                FileTransferTransportHandoffKind.TunaToNormalFallback.ToString(),
                "(none)",
                exactReceipt?.FrameIdentity?.FrameType ?? "(none)",
                CalculateSnapshotFingerprint(exactReceipt?.FrameIdentity),
                receiptSnapshot is null
                    ? ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot>
                        .Empty
                    : ImmutableArray.Create(receiptSnapshot)));
    }

    private static FileTransferCoordinatorStateBoundarySnapshot
        CreateRouteTransitionSourceBoundary(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionAuthority authority)
        => new(
            FileTransferRouteResolver.Resolve(authority.SourceRoute)
                .RuntimeDescriptor.TelemetryToken,
            authority.SourceProtocolVersion,
            authority.SourceLiveRouteEpoch,
            authority.SourceRoutePublication.PublicationGeneration,
            authority.SourceRouteCommit?.CommitGeneration ?? 0,
            authority.SourceRuntimeOwner.RuntimeGeneration,
            authority.SourceTerminalGeneration,
            authority.SourceLegGeneration,
            authority.SourceTransportEpoch,
            authority.SourceBridgeRecoveryGeneration,
            authority.DurableCheckpoint.CheckpointGeneration,
            $"checkpoint:{authority.DurableCheckpoint.CheckpointGeneration}",
            state.CurrentDataSessionGeneration == authority.DataSessionGeneration
                ? state.CurrentDataSessionIdentity ?? "(none)"
                : "(captured-by-generation)",
            authority.DataSessionGeneration);

    private static FileTransferCoordinatorStateBoundarySnapshot
        CreateRouteTransitionTargetBoundary(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionAuthority? authority)
    {
        if (authority is null)
        {
            return CreateStateBoundarySnapshot(state);
        }

        var targetRoute = FileTransferRouteResolver.Resolve(
            authority.Value.TargetRoute).RuntimeDescriptor.TelemetryToken;
        var current = CreateStateBoundarySnapshot(state);
        if (string.Equals(current.Route, targetRoute, StringComparison.Ordinal) &&
            current.ProtocolVersion == authority.Value.TargetProtocolVersion)
        {
            return current;
        }

        return new(
            targetRoute,
            authority.Value.TargetProtocolVersion,
            0,
            0,
            0,
            0,
            authority.Value.SourceTerminalGeneration,
            0,
            authority.Value.SourceTransportEpoch,
            authority.Value.SourceBridgeRecoveryGeneration,
            authority.Value.DurableCheckpoint.CheckpointGeneration,
            $"checkpoint:{authority.Value.DurableCheckpoint.CheckpointGeneration}",
            state.CurrentDataSessionGeneration == authority.Value.DataSessionGeneration
                ? state.CurrentDataSessionIdentity ?? "(none)"
                : "(captured-by-generation)",
            authority.Value.DataSessionGeneration);
    }

    private static FileTransferCoordinatorStateBoundarySnapshot
        CreateFallbackV6ProofBootstrapTargetBoundary(
            FileTransferCoordinatorState state,
            FileTransferFallbackV6ProofBootstrapObservation observation)
        => new(
            FileTransferRouteResolver.Resolve(
                    FileTransferRoute.PostTunaFallbackV6)
                .RuntimeDescriptor.TelemetryToken,
            FileTransferProtocol.ProtocolVersionV6,
            checked((int)observation.FallbackEpoch),
            0,
            0,
            0,
            state.TerminalGeneration,
            0,
            state.CurrentTransportEpochDecision?.TransportEpoch ?? 0,
            state.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ?? 0,
            observation.DurableCheckpoint.CheckpointGeneration,
            $"checkpoint:{observation.DurableCheckpoint.CheckpointGeneration}",
            state.CurrentDataSessionIdentity ?? "(none)",
            state.CurrentDataSessionGeneration);

    private static string GetRouteTransitionProofAdmissionMode(
        FileTransferRouteTransitionAuthority? authority,
        FileTransferRouteTransitionOperation? operation)
    {
        if (operation?.FinalProof is not null)
        {
            return "runtime_unlock_final_proof";
        }
        if (authority is null)
        {
            return "attempt";
        }
        return authority.Value.Kind switch
        {
            FileTransferRouteTransitionKind.RegularV4ToTunaV4 =>
                "runtime_unlock_precommit",
            FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4 =>
                "paired_retry_precommit",
            FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6 or
            FileTransferRouteTransitionKind.UncommittedTunaToPostTunaFallbackV6 =>
                "fallback_v6_peer_proof",
            _ => "coordinator_route_transition",
        };
    }

    private static string CreateCoordinatorOperationKey(
        string family,
        string role,
        string authorityId,
        long generation,
        string kind)
        => string.Join(
            "|",
            family,
            role,
            authorityId,
            generation.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            kind);

    private static ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot>
        CreateRouteTransitionReceiptSnapshots(
            FileTransferRouteTransitionOperation operation)
    {
        var builder = ImmutableArray.CreateBuilder<
            FileTransferCoordinatorReceiptTransitionSnapshot>();
        var authority = operation.Authority;
        if (operation.PauseBindReceipt is { } pause)
        {
            builder.Add(CreateRouteTransitionReceiptSnapshot(
                "pause_bind",
                $"{authority.AuthorityId}:pause_bind",
                authority.TransitionGeneration,
                pause.Command,
                $"{authority.AuthorityId}:pause_bind",
                authority.TransitionGeneration,
                pause.Status.ToString(),
                pause.Reason,
                pause,
                pause.Status is
                    FileTransferRouteTransitionPauseBindStatus.Installed or
                    FileTransferRouteTransitionPauseBindStatus.Idempotent));
        }
        if (operation.DrainReceipt is { } drain)
        {
            builder.Add(CreateRouteTransitionReceiptSnapshot(
                "drain",
                $"{authority.AuthorityId}:drain",
                authority.TransitionGeneration,
                drain.Command,
                $"{authority.AuthorityId}:drain",
                authority.TransitionGeneration,
                drain.Outcome.ToString(),
                drain.Reason,
                drain,
                drain.Outcome ==
                    FileTransferRouteTransitionPhysicalDrainOutcome.Completed));
        }
        if (operation.State == FileTransferRouteTransitionState.Committed &&
            operation.DrainReceipt is { } committedDrain)
        {
            var commitEvidence =
                CreateRouteTransitionCommitEvidence(operation);
            builder.Add(CreateRouteTransitionReceiptSnapshot(
                "commit",
                $"{authority.AuthorityId}:commit",
                authority.TransitionGeneration,
                committedDrain.Command,
                $"{authority.AuthorityId}:commit",
                authority.TransitionGeneration,
                "Committed",
                operation.DispositionCommand?.Reason ?? authority.Reason,
                commitEvidence,
                true));
        }
        if (operation.PressureSettlementReceipt is { } pressureSettlement)
        {
            builder.Add(CreateRouteTransitionReceiptSnapshot(
                "pressure_settlement",
                $"{authority.AuthorityId}:pressure_settlement",
                authority.TransitionGeneration,
                pressureSettlement.Command,
                $"{authority.AuthorityId}:pressure_settlement",
                authority.TransitionGeneration,
                pressureSettlement.Status.ToString(),
                pressureSettlement.Reason,
                pressureSettlement,
                pressureSettlement.Status is
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied or
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent));
        }
        if (operation.RetainedFrameApplyReceipt is { } retained)
        {
            builder.Add(CreateRouteTransitionReceiptSnapshot(
                "retained_frame",
                $"{authority.AuthorityId}:retained_frame",
                authority.TransitionGeneration,
                retained.Command,
                $"{authority.AuthorityId}:retained_frame",
                authority.TransitionGeneration,
                retained.Status.ToString(),
                retained.Reason,
                retained,
                retained.Status is
                    FileTransferRouteTransitionRetainedFrameApplyStatus.Applied or
                    FileTransferRouteTransitionRetainedFrameApplyStatus.Idempotent));
        }
        if (operation.DispositionReceipt is { } disposition)
        {
            builder.Add(CreateRouteTransitionReceiptSnapshot(
                "disposition",
                $"{authority.AuthorityId}:{disposition.Command.Kind}",
                authority.TransitionGeneration,
                disposition.Command,
                $"{authority.AuthorityId}:{disposition.Command.Kind}",
                authority.TransitionGeneration,
                disposition.Status.ToString(),
                disposition.Reason,
                disposition,
                disposition.Status is
                    FileTransferRouteTransitionDispositionApplyStatus.Applied or
                    FileTransferRouteTransitionDispositionApplyStatus.Idempotent));
        }
        return builder.ToImmutable();
    }

    internal static FileTransferCoordinatorRouteTransitionCommitEvidence
        CreateRouteTransitionCommitEvidence(
            FileTransferRouteTransitionOperation operation)
    {
        if (operation.State != FileTransferRouteTransitionState.Committed ||
            operation.DrainReceipt is not { } drainReceipt)
        {
            throw new InvalidOperationException(
                "A committed route-transition operation with an exact drain receipt is required.");
        }

        return new(
            operation.Authority,
            drainReceipt,
            operation.FinalProof,
            operation.PressureSettlementReceipt,
            operation.RouteCommitToken,
            operation.DispositionCommand);
    }

    private static FileTransferCoordinatorReceiptTransitionSnapshot
        CreateRouteTransitionReceiptSnapshot(
            string stage,
            string commandIdentity,
            long commandGeneration,
            object command,
            string receiptIdentity,
            long receiptGeneration,
            string receiptStatus,
            string receiptReason,
            object receipt,
            bool validated)
        => new(
            stage,
            commandIdentity,
            commandGeneration,
            CalculateSnapshotFingerprint(command),
            receiptIdentity,
            receiptGeneration,
            receiptStatus,
            NormalizeSnapshotToken(receiptReason, "file_transfer_coordinator"),
            CalculateSnapshotFingerprint(receipt),
            validated);

    private static string CalculateRouteTransitionCommandFingerprint(
        FileTransferRouteTransitionOperation operation)
    {
        var authority = operation.Authority;
        return CalculateSnapshotFingerprint(new
        {
            authority.AuthorityId,
            authority.PredecessorTransitionAuthorityId,
            authority.TransitionGeneration,
            authority.SessionId,
            authority.TransferId,
            authority.Direction,
            authority.SourceRoute,
            authority.SourceProtocolVersion,
            authority.SourceTerminalGeneration,
            authority.SourceLiveRouteEpoch,
            authority.SourceLegGeneration,
            authority.SourceTransportEpoch,
            authority.SourceBridgeRecoveryGeneration,
            authority.DataSessionGeneration,
            authority.TargetRoute,
            authority.TargetProtocolVersion,
            authority.TargetTransport,
            RetainedFrame = operation.RetainedFrameApplyCommand?.FrameIdentity,
            DispositionKind = operation.DispositionCommand?.Kind,
            DispositionCommitGeneration =
                operation.DispositionCommand?.RouteCommitToken?.CommitGeneration,
            DispositionPublicationGeneration =
                operation.DispositionCommand?.RoutePublicationToken?.PublicationGeneration,
            DrainThroughGeneration =
                operation.DrainCommand?.CapturedThroughPhysicalSendGeneration,
            DrainFingerprint = operation.DrainCommand?.CapturedPhysicalSendFingerprint,
            PauseBind = operation.PauseBindReceipt is not null,
        });
    }

    internal static string CalculateSnapshotStateFingerprint(
        FileTransferCoordinatorState state)
        => CalculateStateFingerprint(state);

    private static string CalculateRouteTransitionReceiptFingerprint(
        FileTransferRouteTransitionOperation operation)
    {
        if (operation.RetainedFrameApplyReceipt is { } retained)
        {
            return CalculateSnapshotFingerprint(new
            {
                retained.Command.Authority.AuthorityId,
                retained.Command.Authority.TransitionGeneration,
                retained.Command.FrameIdentity,
                retained.Status,
                retained.Reason,
            });
        }
        if (operation.DispositionReceipt is { } disposition)
        {
            return CalculateRouteTransitionDispositionReceiptFingerprint(
                disposition);
        }
        if (operation.DrainReceipt is { } drain)
        {
            return CalculateSnapshotFingerprint(new
            {
                drain.Command.Authority.AuthorityId,
                drain.Command.Authority.TransitionGeneration,
                drain.Command.CapturedThroughPhysicalSendGeneration,
                drain.Command.CapturedPhysicalSendFingerprint,
                drain.CompletedPhysicalSendCount,
                drain.CompletedPhysicalSendFingerprint,
                drain.Outcome,
                drain.Reason,
            });
        }
        if (operation.PauseBindReceipt is { } pause)
        {
            return CalculateSnapshotFingerprint(new
            {
                pause.Command.Authority.AuthorityId,
                pause.Command.Authority.TransitionGeneration,
                PredecessorAuthorityId =
                    pause.Command.PredecessorAuthority?.AuthorityId,
                pause.Status,
                pause.Reason,
            });
        }
        return "(none)";
    }

    internal static string
        CalculateRouteTransitionDispositionReceiptFingerprint(
            FileTransferRouteTransitionDispositionReceipt receipt)
        => CalculateSnapshotFingerprint(new
        {
            receipt.Command.Authority.AuthorityId,
            receipt.Command.Authority.PredecessorTransitionAuthorityId,
            receipt.Command.Authority.TransitionGeneration,
            receipt.Command.Kind,
            CommitGeneration =
                receipt.Command.RouteCommitToken?.CommitGeneration,
            PublicationGeneration =
                receipt.Command.RoutePublicationToken?.PublicationGeneration,
            CommandReason = receipt.Command.Reason,
            receipt.Status,
            receipt.Reason,
        });

    private static FileTransferTransportKind GetSnapshotTransport(
        FileTransferRoute route)
        => route == FileTransferRoute.FileTunaV4
            ? FileTransferTransportKind.Tuna
            : route is FileTransferRoute.RegularNknV4Fast or
                FileTransferRoute.PostTunaFallbackV6
                ? FileTransferTransportKind.RegularNkn
                : FileTransferTransportKind.Unknown;

    private static string NormalizeSnapshotToken(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    internal static string CalculateSnapshotFingerprint(object? value)
    {
        if (value is null)
        {
            return "(none)";
        }

        var payload = FileTransferCoordinatorTransitionEnvelope
            .SerializeCanonical(value, SnapshotFingerprintJsonOptions);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions SnapshotFingerprintJsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
        };

    private sealed record CoordinatorOperationDescriptor(
        string Family,
        string OperationKind,
        string AuthorityId,
        string PredecessorAuthorityId,
        long OperationGeneration,
        string State,
        string CommandIdentity,
        long CommandGeneration,
        string CommandFingerprint,
        string ReceiptIdentity,
        long ReceiptGeneration,
        string ReceiptStatus,
        string ReceiptFingerprint,
        bool ReceiptValidated,
        string OperationRole = "primary",
        string OperationKey = "",
        FileTransferCoordinatorRouteRuntimeOperationSnapshot? RouteRuntime = null,
        FileTransferCoordinatorRouteTransitionOperationSnapshot?
            RouteTransition = null,
        FileTransferCoordinatorRuntimeUnlockRetryOperationSnapshot?
            RuntimeUnlockRetry = null,
        FileTransferCoordinatorReceiveRecoveryOperationSnapshot?
            ReceiveRecovery = null,
        FileTransferCoordinatorFallbackTailYieldOperationSnapshot?
            FallbackTailYield = null,
        FileTransferCoordinatorRegularV4FrontierRepairPressureOperationSnapshot?
            RegularV4FrontierRepairPressure = null,
        FileTransferCoordinatorResponderProbeHandoffOperationSnapshot?
            ResponderProbeHandoff = null);
}
