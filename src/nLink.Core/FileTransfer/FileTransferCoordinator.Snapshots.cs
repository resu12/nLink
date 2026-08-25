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
        RuntimeUnlockRetry = null);

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
        var descriptors = CreateOperationDescriptors(state)
            .Where(descriptor => string.Equals(
                descriptor.Family,
                "runtime_unlock_retry_lifecycle",
                StringComparison.Ordinal));
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
            FileTransferCoordinatorSnapshotSelectionMode.Shadow)
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
        var beforeFingerprint = CalculateStateFingerprint(before);
        var afterFingerprint = CalculateStateFingerprint(after);
        var stateApplied = !string.Equals(
            beforeFingerprint,
            afterFingerprint,
            StringComparison.Ordinal);
        var operations = CreateOperationTransitionSnapshots(before, after);
        if (!stateApplied && !attemptedOperations.IsDefaultOrEmpty)
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
        var stampedState = after with
        {
            PersistentState = after.PersistentState with
            {
                CoordinatorInstanceId = coordinatorInstanceId,
                SnapshotStateGeneration = afterStateGeneration,
            },
        };
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
            FileTransferCoordinatorSnapshotSelectionMode.Shadow)
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
            FileTransferCoordinatorSnapshotSelectionMode.Shadow)
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
            FileTransferCoordinatorSnapshotSelectionMode.Shadow)
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
            FileTransferCoordinatorSnapshotSelectionMode.Shadow)
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
        var snapshot = CreateTransitionSnapshot(
            before,
            decision.State,
            coordinatorInstanceId,
            before.PersistentState.SnapshotStateGeneration,
            operationKind,
            decision.Status.ToString(),
            decision.Reason,
            ImmutableArray.Create(attemptedOperation));
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
        var operations = CreateOperationDescriptors(state);
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
            operation.RuntimeUnlockRetry);

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
        CreateOperationDescriptors(FileTransferCoordinatorState state)
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

        var recovery = persistent.FileTransferReceiveRecovery.CurrentOperation ??
                       persistent.FileTransferReceiveRecovery.LastTerminalOperation;
        if (recovery is { } recoveryOperation)
        {
            builder.Add(new CoordinatorOperationDescriptor(
                "receive_recovery",
                recoveryOperation.Authority.BridgeRecoveryDecision.RecoveryKind.ToString(),
                recoveryOperation.Authority.AuthorityId.ToString(),
                recoveryOperation.Authority.PredecessorAuthorityId?.ToString() ?? "(none)",
                recoveryOperation.Authority.OperationGeneration,
                $"{recoveryOperation.ExecutionState}:{recoveryOperation.ClosureState}",
                recoveryOperation.Authority.AuthorityId.ToString(),
                recoveryOperation.Authority.OperationGeneration,
                CalculateSnapshotFingerprint(new
                {
                    recoveryOperation.Authority.AuthorityId,
                    recoveryOperation.Authority.PredecessorAuthorityId,
                    recoveryOperation.Authority.OperationGeneration,
                    recoveryOperation.Authority.BridgeRecoveryDecision.BridgeDecisionGeneration,
                    recoveryOperation.Authority.RecoveryLivenessLease?.LeaseGeneration,
                    recoveryOperation.Authority.BridgeRecoveryDecision.Reason,
                }),
                recoveryOperation.ExecutionReceipt is not null
                    ? recoveryOperation.Authority.AuthorityId.ToString()
                    : "(none)",
                recoveryOperation.ExecutionReceipt is not null ||
                recoveryOperation.ReceiveProof is not null
                    ? Math.Max(
                        recoveryOperation.ClosureGeneration,
                        recoveryOperation.Authority.OperationGeneration)
                    : 0,
                recoveryOperation.ExecutionReceipt?.Outcome.ToString() ?? "(none)",
                CalculateSnapshotFingerprint(new
                {
                    ExecutionOutcome = recoveryOperation.ExecutionReceipt?.Outcome,
                    ExecutionReason = recoveryOperation.ExecutionReceipt?.Reason,
                    recoveryOperation.ClosureGeneration,
                    HasReceiveProof = recoveryOperation.ReceiveProof is not null,
                }),
                recoveryOperation.ExecutionReceipt is not null ||
                recoveryOperation.ReceiveProof is not null));
        }

        var yield = persistent.FallbackTailYield.CurrentOperation ??
                    persistent.FallbackTailYield.LastTerminalOperation;
        if (yield is { } yieldOperation)
        {
            builder.Add(new CoordinatorOperationDescriptor(
                "fallback_tail_yield",
                yieldOperation.Decision.YieldKind.ToString(),
                yieldOperation.Decision.AuthorityId.ToString(),
                yieldOperation.Decision.PredecessorAuthorityId?.ToString() ?? "(none)",
                yieldOperation.Decision.YieldGeneration,
                yieldOperation.PairingState.ToString(),
                yieldOperation.Decision.AuthorityId.ToString(),
                yieldOperation.Decision.YieldGeneration,
                CalculateSnapshotFingerprint(new
                {
                    yieldOperation.Decision.AuthorityId,
                    yieldOperation.Decision.PredecessorAuthorityId,
                    yieldOperation.Decision.YieldGeneration,
                    yieldOperation.Decision.OwnerKey,
                    yieldOperation.Decision.PairKey,
                    yieldOperation.Decision.CheckpointRequestIdSnapshot,
                    yieldOperation.Decision.Reason,
                }),
                yieldOperation.PairedRelease?.ReleaseAuthorityId.ToString() ?? "(none)",
                yieldOperation.PairedRelease?.ReleaseGeneration ?? 0,
                yieldOperation.PairedRelease?.ReleaseOutcome ?? "(none)",
                CalculateSnapshotFingerprint(new
                {
                    yieldOperation.PairedRelease?.ReleaseAuthorityId,
                    yieldOperation.PairedRelease?.ReleaseGeneration,
                    yieldOperation.PairedRelease?.ReleaseOutcome,
                    LocalAuthorityId = yieldOperation.PairedRelease?.LocalProof.Decision.AuthorityId,
                    PeerAuthorityId = yieldOperation.PairedRelease?.PeerProof.Proof.AuthorityId,
                }),
                yieldOperation.PairedRelease is not null));
        }

        var pressure = persistent.RegularV4FrontierRepairPressure.InstalledBinding ??
                       persistent.RegularV4FrontierRepairPressure.PendingBinding ??
                       persistent.RegularV4FrontierRepairPressure.RetainedPredecessorBinding;
        if (pressure is { } pressureBinding)
        {
            builder.Add(new CoordinatorOperationDescriptor(
                "regular_v4_frontier_repair_pressure",
                pressureBinding.Authority.DecisionReason,
                pressureBinding.Authority.AuthorityId.ToString(),
                pressureBinding.PredecessorAuthorityId?.ToString() ?? "(none)",
                pressureBinding.Authority.DecisionGeneration,
                pressureBinding.State.ToString(),
                pressureBinding.Authority.AuthorityId.ToString(),
                pressureBinding.InstallGeneration,
                CalculateSnapshotFingerprint(new
                {
                    pressureBinding.Authority.AuthorityId,
                    pressureBinding.Authority.DecisionGeneration,
                    pressureBinding.Authority.DecisionReason,
                    pressureBinding.Authority.RepairKey,
                    pressureBinding.InstallGeneration,
                }),
                pressureBinding.FinalSettlement?.AuthorityId.ToString() ?? "(none)",
                pressureBinding.FinalSettlement?.DecisionGeneration ?? 0,
                pressureBinding.FinalSettlement?.Outcome.ToString() ?? "(none)",
                CalculateSnapshotFingerprint(new
                {
                    pressureBinding.FinalSettlement?.AuthorityId,
                    pressureBinding.FinalSettlement?.DecisionGeneration,
                    pressureBinding.FinalSettlement?.Outcome,
                    pressureBinding.FinalSettlement?.DecisionReason,
                }),
                pressureBinding.FinalSettlement is not null));
        }

        var handoff = persistent.RuntimeUnlockResponderPeerProbeRouteTransitionHandoff;
        if (handoff.CurrentAuthority is { } handoffAuthority)
        {
            builder.Add(new CoordinatorOperationDescriptor(
                "responder_probe_handoff",
                "ResponderPeerProbeRouteTransitionHandoff",
                handoffAuthority.AuthorityId.ToString(),
                "(none)",
                handoffAuthority.AuthorityGeneration,
                handoff.LastStatus?.ToString() ?? "Accepted",
                handoffAuthority.Observation.ProbeId,
                handoffAuthority.AuthorityGeneration,
                CalculateSnapshotFingerprint(new
                {
                    handoffAuthority.Observation.ProbeId,
                    handoffAuthority.Observation.TransactionGeneration,
                    handoffAuthority.Observation.OfferGeneration,
                    handoffAuthority.Observation.LaunchGeneration,
                    handoffAuthority.Observation.TunaPathLeaseGeneration,
                }),
                handoffAuthority.AuthorityId.ToString(),
                handoffAuthority.AuthorityGeneration,
                handoff.LastStatus?.ToString() ?? "Accepted",
                CalculateSnapshotFingerprint(new
                {
                    handoffAuthority.AuthorityId,
                    handoffAuthority.AuthorityGeneration,
                    handoffAuthority.Observation.ProbeId,
                    handoff.LastStatus,
                    handoff.LastReason,
                }),
                true));
        }

        return builder
            .OrderBy(GetCoordinatorOperationKey, StringComparer.Ordinal)
            .ToImmutableArray();
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
            RuntimeUnlockRetry = null);
}
