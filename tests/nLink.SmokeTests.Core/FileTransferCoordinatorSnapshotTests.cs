using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
public sealed class FileTransferCoordinatorSnapshotTests
{
    [Theory]
    [InlineData("route_runtime", "runtime_owner", "RuntimeOwner", "authority-route")]
    [InlineData("route_transition", "current", "RegularV4ToTunaV4", "authority-transition")]
    [InlineData("runtime_unlock_retry_lifecycle", "attempted_decision", "RetryAttempt", "authority-retry")]
    [InlineData("receive_recovery", "operation_current", "BridgeReceiveStallRecovery", "authority-recovery")]
    [InlineData("fallback_tail_yield", "yield_current", "Quiescent", "authority-yield")]
    [InlineData("regular_v4_frontier_repair_pressure", "repair_authority_current", "V4Repair", "authority-pressure")]
    [InlineData("responder_probe_handoff", "handoff_current", "post_fallback_v6_paired", "authority-handoff")]
    [InlineData("route_runtime", "attempted_decision", "StartTransfer", "(none)")]
    public void EvidenceProjection_RebuildsOperationKeyFromSanitizedIdentity(
        string family,
        string role,
        string kind,
        string authorityId)
    {
        var state = EmptyState() with
        {
            PersistentState = EmptyState().PersistentState with
            {
                CoordinatorInstanceId = "coordinator-evidence-test",
            },
        };
        var decision = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                FileTransferRouteResolver.Resolve(
                    FileTransferRoute.RegularNknV4Fast),
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.RegularNkn,
                "snapshot_evidence_test",
                FileTransferLegState.Active,
                CanSendData: true,
                CommittedChunkIndex: 0,
                HighestObservedChunkIndex: -1,
                TransportEpochId: 0,
                BridgeRecoveryGeneration: 0),
            state);
        var envelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(
            decision.SnapshotEnvelope);
        var template = envelope.Aggregate.Operations[0];
        var operation = template with
        {
            Family = family,
            OperationRole = role,
            OperationKey = "raw-operation-key-must-not-be-projected",
            OperationKind = kind,
            AuthorityId = authorityId,
            OperationGeneration = 7,
        };
        var projected = SessionFileTransferService
            .CreateCoordinatorSnapshotEvidenceForTesting(
            envelope with
            {
                Aggregate = envelope.Aggregate with
                {
                    Operations = ImmutableArray.Create(operation),
                },
            });

        var projectedOperation = Assert.Single(projected.Aggregate.Operations);
        var projectedAuthority = authorityId == "(none)"
            ? authorityId
            : "fte1-" + Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(authorityId)))
                .ToLowerInvariant()[..24];
        Assert.Equal(projectedAuthority, projectedOperation.AuthorityId);
        Assert.Equal(
            $"{family}|{role}|{projectedAuthority}|7|{kind}",
            projectedOperation.OperationKey);
        Assert.DoesNotContain(
            "raw-operation-key-must-not-be-projected",
            projectedOperation.OperationKey,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceProjection_LongFamily5OperationKeySurvivesOperationalRedaction()
    {
        var state = EmptyState() with
        {
            PersistentState = EmptyState().PersistentState with
            {
                CoordinatorInstanceId = "coordinator-family5-redaction-test",
            },
        };
        var decision = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                FileTransferRouteResolver.Resolve(
                    FileTransferRoute.RegularNknV4Fast),
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.RegularNkn,
                "snapshot_family5_redaction_test",
                FileTransferLegState.Active,
                CanSendData: true,
                CommittedChunkIndex: 0,
                HighestObservedChunkIndex: -1,
                TransportEpochId: 0,
                BridgeRecoveryGeneration: 0),
            state);
        var envelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(
            decision.SnapshotEnvelope);
        var template = envelope.Aggregate.Operations[0];
        var projected = SessionFileTransferService
            .CreateCoordinatorSnapshotEvidenceForTesting(
                envelope with
                {
                    Aggregate = envelope.Aggregate with
                    {
                        Operations = ImmutableArray.Create(template with
                        {
                            Family =
                                "regular_v4_frontier_repair_pressure",
                            OperationRole = "attempted_decision",
                            OperationKind = nameof(
                                FileTransferCoordinator
                                    .StageRegularV4FrontierRepairPressureBinding),
                            AuthorityId = "family5-pressure-authority",
                            OperationGeneration = 2,
                        }),
                    },
                });
        var operation = Assert.Single(projected.Aggregate.Operations);
        var logLine = SensitiveDataRedactor.Redact(
            "event=filetransfer_coordinator_authority_effect; " +
            $"operation_key={operation.OperationKey}; " +
            $"operation_authority_id={operation.AuthorityId}; " +
            "authority_effect=applied");

        Assert.Contains(
            $"operation_key={operation.OperationKey}",
            logLine,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[redacted]", logLine, StringComparison.Ordinal);
    }

    [Fact]
    public void CoordinatorApply_AuthorsRouteRuntimeEnvelopeWhenInstanceIsCurrent()
    {
        var state = EmptyState() with
        {
            PersistentState = EmptyState().PersistentState with
            {
                CoordinatorInstanceId = "coordinator-test",
            },
        };

        var decision = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                FileTransferRouteResolver.Resolve(
                    FileTransferRoute.RegularNknV4Fast),
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.RegularNkn,
                "snapshot_test",
                FileTransferLegState.Active,
                CanSendData: true,
                CommittedChunkIndex: 0,
                HighestObservedChunkIndex: -1,
                TransportEpochId: 0,
                BridgeRecoveryGeneration: 0),
            state);

        var envelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(
            decision.SnapshotEnvelope);
        Assert.Equal(0, envelope.Aggregate.BeforeStateGeneration);
        Assert.Equal(1, envelope.Aggregate.AfterStateGeneration);
        Assert.Equal(
            1,
            decision.State.PersistentState.SnapshotStateGeneration);
        var operation = Assert.Single(
            envelope.Aggregate.Operations,
            operation => operation is
            {
                Family: "route_runtime",
                OperationRole: "runtime_owner",
                OperationKind: "RuntimeOwner",
            });
        Assert.Equal(
            $"route_runtime|runtime_owner|{operation.AuthorityId}|" +
            $"{operation.OperationGeneration}|RuntimeOwner",
            operation.OperationKey);
    }

    [Fact]
    public void RejectedRouteRuntimeDecisions_IncludeExactAttemptedOperations()
    {
        var terminal = EmptyState() with
        {
            IsTerminal = true,
            TerminalGeneration = 1,
            PersistentState = EmptyState().PersistentState with
            {
                CoordinatorInstanceId = "coordinator-test",
            },
        };
        var start = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                FileTransferRouteResolver.Resolve(
                    FileTransferRoute.RegularNknV4Fast),
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.RegularNkn,
                "snapshot_rejected_start",
                FileTransferLegState.Active,
                CanSendData: true,
                CommittedChunkIndex: 0,
                HighestObservedChunkIndex: -1,
                TransportEpochId: 0,
                BridgeRecoveryGeneration: 0),
            terminal);
        var startOperation = Assert.Single(
            Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(
                    start.SnapshotEnvelope)
                .Aggregate.Operations);
        Assert.Equal("Rejected", start.SnapshotEnvelope!.DecisionStatus);
        Assert.Equal("route_runtime", startOperation.Family);
        Assert.Equal("runtime_owner", startOperation.OperationRole);
        Assert.False(startOperation.StateApplied);

        var publication = FileTransferCoordinator.AuthorizeAction(
            terminal,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "snapshot_test",
                Reason: "snapshot_rejected_publication"));
        var publicationOperation = Assert.Single(
            Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(
                    publication.SnapshotEnvelope)
                .Aggregate.Operations);
        Assert.Equal("Rejected", publication.SnapshotEnvelope!.DecisionStatus);
        Assert.Equal("route_publication", publicationOperation.OperationRole);
        Assert.False(publicationOperation.StateApplied);
    }

    [Fact]
    public void AcceptedMutation_AdvancesGenerationOnceAndProducesStablePayload()
    {
        var state = EmptyState();
        var decision = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                FileTransferRouteResolver.Resolve(
                    FileTransferRoute.RegularNknV4Fast),
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.RegularNkn,
                "snapshot_test",
                FileTransferLegState.Active,
                CanSendData: true,
                CommittedChunkIndex: 0,
                HighestObservedChunkIndex: -1,
                TransportEpochId: 0,
                BridgeRecoveryGeneration: 0),
            state);

        var snapshot = FileTransferCoordinator.CreateTransitionSnapshot(
            state,
            decision,
            "coordinator-test");

        Assert.Equal(0, snapshot.Envelope.Aggregate.BeforeStateGeneration);
        Assert.Equal(1, snapshot.Envelope.Aggregate.AfterStateGeneration);
        Assert.Equal(
            "SnapshotV1Authoritative",
            snapshot.Envelope.SnapshotSelectionMode);
        Assert.Equal("Accepted", snapshot.Envelope.DecisionStatus);
        Assert.Equal(
            1,
            snapshot.State.PersistentState.SnapshotStateGeneration);
        Assert.Equal(
            "coordinator-test",
            snapshot.State.PersistentState.CoordinatorInstanceId);
        var routeOperation = Assert.Single(
            snapshot.Envelope.Aggregate.Operations,
            operation => operation.Family == "route_runtime" &&
                         operation.OperationRole == "runtime_owner");
        Assert.NotEqual(routeOperation.BeforeState, routeOperation.AfterState);
        Assert.Equal(
            $"route_runtime|runtime_owner|{routeOperation.AuthorityId}|" +
            $"{routeOperation.OperationGeneration}|RuntimeOwner",
            routeOperation.OperationKey);
        Assert.Equal(
            "regular_nkn_v4_fast",
            routeOperation.RouteRuntime?.Boundary.Route);
        Assert.Equal(
            snapshot.Envelope.CalculatePayloadSha256(),
            snapshot.Envelope.CalculatePayloadSha256());

        var payload = snapshot.Envelope.ToBase64UrlPayload()
            .Replace('-', '+')
            .Replace('_', '/');
        payload = payload.PadRight(
            payload.Length + ((4 - payload.Length % 4) % 4),
            '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        var reference = SerializeCanonicalReference(snapshot.Envelope);
        Assert.Equal(reference, json);
        Assert.Equal(reference, snapshot.Envelope.ToCanonicalJson());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference))).ToLowerInvariant(),
            snapshot.Envelope.CalculatePayloadSha256());
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            snapshot.Envelope.TransitionId,
            document.RootElement.GetProperty("transitionId").GetString());
    }

    [Fact]
    public void IdempotentBoundary_RetainsGeneration()
    {
        var state = EmptyState();

        var snapshot = FileTransferCoordinator.CreateTransitionSnapshot(
            state,
            state,
            "coordinator-test",
            beforeStateGeneration: 7,
            decisionKind: "Diagnostic",
            decisionStatus: "Accepted",
            decisionReason: "no_change");

        Assert.Equal(7, snapshot.Envelope.Aggregate.BeforeStateGeneration);
        Assert.Equal(7, snapshot.Envelope.Aggregate.AfterStateGeneration);
        Assert.Equal("Idempotent", snapshot.Envelope.DecisionStatus);
        Assert.Empty(snapshot.Envelope.Aggregate.ChangedFamilies);

        var next = FileTransferCoordinator.CreateTransitionSnapshot(
            snapshot.State,
            snapshot.State,
            "coordinator-test",
            beforeStateGeneration: 7,
            decisionKind: "Diagnostic",
            decisionStatus: "Idempotent",
            decisionReason: "no_change_duplicate");
        Assert.True(
            next.Envelope.DecisionUtcMilliseconds >
            snapshot.Envelope.DecisionUtcMilliseconds);
    }

    [Fact]
    public void AggregateFingerprint_AdvancesForAuthorityStateOutsideRouteBoundary()
    {
        var before = EmptyState();
        var after = before with
        {
            RuntimeUnlockFallbackSurvivalState = new(
                FallbackActive: true,
                FallbackCurrent: true,
                FallbackTerminalized: false,
                CheckpointDeliveryRecoveryPending: false,
                StateRefreshSendInFlight: false,
                DeferredStateRefreshPending: false,
                ProofReplayPending: false,
                ControlPlanePressurePending: false,
                BridgeRecoveryPending: false,
                FallbackTailRepairPending: false,
                PairedFallbackYieldGranted: false),
        };

        var snapshot = FileTransferCoordinator.CreateTransitionSnapshot(
            before,
            after,
            "coordinator-test",
            beforeStateGeneration: 11,
            decisionKind: "FallbackSurvival",
            decisionStatus: "Accepted",
            decisionReason: "test");

        Assert.Equal(11, snapshot.Envelope.Aggregate.BeforeStateGeneration);
        Assert.Equal(12, snapshot.Envelope.Aggregate.AfterStateGeneration);
        Assert.NotEqual(
            snapshot.Envelope.Aggregate.BeforeStateFingerprint,
            snapshot.Envelope.Aggregate.AfterStateFingerprint);
    }

    [Fact]
    public void RejectedDecision_CannotMutateSnapshotState()
    {
        var before = EmptyState();
        var changed = before with { IsTerminal = true, TerminalGeneration = 1 };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            FileTransferCoordinator.CreateTransitionSnapshot(
                before,
                changed,
                "coordinator-test",
                beforeStateGeneration: 0,
                decisionKind: "RejectedMutation",
                decisionStatus: "Rejected",
                decisionReason: "test"));

        Assert.Contains("Rejected decision mutated state", exception.Message);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void TransitionLocalDescriptors_PreserveFullStateFingerprintAndMutationChecks(
        bool hiddenMutation, bool suppliedOperationsAreComplete, bool storedFingerprint)
    {
        var initial = EmptyState();
        var started = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast),
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.RegularNkn,
                "snapshot_local_descriptors",
                FileTransferLegState.Active,
                CanSendData: true,
                CommittedChunkIndex: 0,
                HighestObservedChunkIndex: -1,
                TransportEpochId: 0,
                BridgeRecoveryGeneration: 0),
            initial);
        var before = FileTransferCoordinator.CreateTransitionSnapshot(
            initial, started, "coordinator-test").State;
        Assert.NotNull(before.CurrentRuntimeOwner);
        if (!storedFingerprint)
        {
            before = before with
            {
                PersistentState = before.PersistentState with { SnapshotStateFingerprint = null },
            };
        }
        var after = before with
        {
            RuntimeUnlockFallbackSurvivalState = hiddenMutation
                ? new(true, true, false, false, false, false, false, false, false, false, false)
                : before.RuntimeUnlockFallbackSurvivalState,
        };
        Assert.NotSame(before, after);
        // This public-internal helper freshly builds descriptors independently
        // of the transition-local reuse path under test.
        var freshBefore = FileTransferCoordinator.CalculateSnapshotStateFingerprint(before);
        var freshAfter = FileTransferCoordinator.CalculateSnapshotStateFingerprint(after);
        Assert.Equal(hiddenMutation, freshBefore != freshAfter);
        var expectedBefore = storedFingerprint
            ? before.PersistentState.SnapshotStateFingerprint
            : freshBefore;
        var result = FileTransferCoordinator.CreateTransitionSnapshot(
            before, after, "coordinator-test", 7, "DescriptorReuse", "Accepted", "test",
            suppliedOperationsAreComplete: suppliedOperationsAreComplete);
        var aggregate = result.Envelope.Aggregate;
        Assert.Equal(aggregate.BeforeBoundary, aggregate.AfterBoundary);
        Assert.Empty(aggregate.Operations);
        Assert.Equal(expectedBefore, aggregate.BeforeStateFingerprint);
        Assert.Equal(hiddenMutation ? 8 : 7, aggregate.AfterStateGeneration);
        Assert.Equal(hiddenMutation ? "Accepted" : "Idempotent", result.Envelope.DecisionStatus);
        var expectedAfter = hiddenMutation
            ? FileTransferCoordinator.CalculateSnapshotFingerprint(new
            {
                PreviousStateFingerprint = expectedBefore,
                Boundary = aggregate.AfterBoundary,
                Operations = ImmutableArray<FileTransferCoordinatorOperationTransitionSnapshot>.Empty,
            })
            : expectedBefore;
        Assert.Equal(expectedAfter, aggregate.AfterStateFingerprint);
        if (hiddenMutation)
        {
            foreach (var status in new[] { "Rejected", "Idempotent" })
            {
                Assert.Throws<InvalidOperationException>(() =>
                    FileTransferCoordinator.CreateTransitionSnapshot(
                        before, after, "coordinator-test", 7, "DescriptorReuse", status, "test",
                        suppliedOperationsAreComplete: suppliedOperationsAreComplete));
            }
        }
    }

    [Fact]
    public void CoordinatorInstanceIdentity_CannotChangeAcrossSnapshotBoundary()
    {
        var before = EmptyState() with
        {
            PersistentState = EmptyState().PersistentState with
            {
                CoordinatorInstanceId = "coordinator-a",
            },
        };

        Assert.Throws<InvalidOperationException>(() =>
            FileTransferCoordinator.CreateTransitionSnapshot(
                before,
                before,
                "coordinator-b",
                beforeStateGeneration: 0,
                decisionKind: "identity-test",
                decisionStatus: "Idempotent",
                decisionReason: "identity_changed"));
    }

    [Fact]
    public void RoutePublicationSnapshot_ContainsExactCommandAndReceiptFingerprint()
    {
        var started = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                FileTransferRouteResolver.Resolve(
                    FileTransferRoute.RegularNknV4Fast),
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.RegularNkn,
                "snapshot_test",
                FileTransferLegState.Active,
                CanSendData: true,
                CommittedChunkIndex: 0,
                HighestObservedChunkIndex: -1,
                TransportEpochId: 0,
                BridgeRecoveryGeneration: 0),
            EmptyState());
        var startedSnapshot = FileTransferCoordinator.CreateTransitionSnapshot(
            EmptyState(),
            started,
            "coordinator-test");
        var startedState = startedSnapshot.State;
        var owner = Assert.IsType<RuntimeOwnerToken>(
            startedState.CurrentRuntimeOwner);
        var leg = Assert.IsType<FileTransferLeg>(startedState.CurrentLeg);
        var publication = FileTransferCoordinator.AuthorizeAction(
            startedState,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "snapshot_test",
                Reason: "route_publication",
                RuntimeOwner: owner,
                Route: startedState.RouteSelection.Route,
                ProtocolVersion: startedState.RouteSelection.ProtocolVersion,
                LiveRouteEpoch: leg.LiveRouteEpochId,
                LegGeneration: leg.Generation,
                RuntimeGeneration: owner.RuntimeGeneration,
                TerminalGeneration: startedState.TerminalGeneration));

        var snapshot = FileTransferCoordinator.CreateTransitionSnapshot(
            startedState,
            publication,
            "coordinator-test");
        var operation = Assert.Single(
            snapshot.Envelope.Aggregate.Operations,
            candidate => candidate.Family == "route_runtime" &&
                         candidate.OperationRole == "route_publication");
        var receipt = Assert.IsType<RoutePublicationToken>(
            publication.RoutePublicationToken);

        Assert.True(operation.ReceiptValidated);
        Assert.Equal(
            $"publication:{receipt.PublicationGeneration}",
            operation.ReceiptIdentity);
        Assert.Equal(
            FileTransferCoordinator.CalculateSnapshotFingerprint(receipt),
            operation.ReceiptFingerprint);
        Assert.NotEqual("(none)", operation.CommandFingerprint);
    }

    [Fact]
    public void CanonicalFingerprint_IsIndependentOfObjectPropertyInsertionOrder()
    {
        var left = new Dictionary<string, object>
        {
            ["zeta"] = 7,
            ["alpha"] = new Dictionary<string, object>
            {
                ["second"] = true,
                ["first"] = "value",
            },
        };
        var right = new Dictionary<string, object>
        {
            ["alpha"] = new Dictionary<string, object>
            {
                ["first"] = "value",
                ["second"] = true,
            },
            ["zeta"] = 7,
        };

        Assert.Equal(
            FileTransferCoordinator.CalculateSnapshotFingerprint(left),
            FileTransferCoordinator.CalculateSnapshotFingerprint(right));
    }

    [Theory]
    [InlineData("""{"z":null,"a":[true,false,0,-1,1.2300,1e+3]}""")]
    [InlineData("""{"z":"<>&\"\\\n","a":"\u00e9\uD83D\uDE00"}""")]
    [InlineData("""{"z":{"y":2,"a":1},"a":[{"b":false,"a":null},[]]}""")]
    [InlineData("""{"b":1,"a":"first","a":"second"}""")]
    [InlineData("""[{"z":1,"a":2},null,"text",123456789012345678901234567890]""")]
    public void CanonicalUtf8_PreservesOriginalBytesAndFingerprint(string input)
    {
        using var document = JsonDocument.Parse(input);
        var value = document.RootElement;
        var reference = SerializeCanonicalReference(value);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Assert.Equal(reference, FileTransferCoordinatorTransitionEnvelope.SerializeCanonical(value, options));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference))).ToLowerInvariant(),
            FileTransferCoordinator.CalculateSnapshotFingerprint(value));
    }

    [Fact]
    public void CanonicalUtf8_RecomputesMutableInputsWithoutAuthorityCaching()
    {
        var value = new Dictionary<string, object> { ["source"] = "original", ["generation"] = 1 };
        var first = FileTransferCoordinator.CalculateSnapshotFingerprint(value);
        value["source"] = "replacement";
        value["generation"] = 2;
        var second = FileTransferCoordinator.CalculateSnapshotFingerprint(value);
        Assert.NotEqual(first, second);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SerializeCanonicalReference(value)))).ToLowerInvariant(),
            second);
        Assert.Equal("(none)", FileTransferCoordinator.CalculateSnapshotFingerprint(null));
    }

    // Frozen pre-optimization serializer: independently guards the exact bytes
    // consumed by existing snapshot hashes and retained evidence.
    private static string SerializeCanonicalReference(object value)
    {
        var element = JsonSerializer.SerializeToElement(value, value.GetType(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());

        static void Write(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        Write(writer, property.Value);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray()) Write(writer, item);
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String: writer.WriteStringValue(element.GetString()); break;
                case JsonValueKind.Number: writer.WriteRawValue(element.GetRawText(), skipInputValidation: true); break;
                case JsonValueKind.True: writer.WriteBooleanValue(true); break;
                case JsonValueKind.False: writer.WriteBooleanValue(false); break;
                default: writer.WriteNullValue(); break;
            }
        }
    }

    private static FileTransferCoordinatorState EmptyState()
        => new(
            "snapshot-session",
            "snapshot-transfer",
            FileTransferDirection.Outbound,
            IsTerminal: false,
            FileTransferRouteResolver.Resolve(
                FileTransferRoute.RegularNknV4Fast),
            CurrentLiveRouteEpoch: null,
            CurrentLeg: null,
            LastLiveRouteEpochId: 0,
            LastTransferLegGeneration: 0,
            LastRuntimeOwnerGeneration: 0,
            TerminalGeneration: 0,
            CurrentRuntimeOwner: null,
            LegHistory: Array.Empty<FileTransferLeg>());
}
