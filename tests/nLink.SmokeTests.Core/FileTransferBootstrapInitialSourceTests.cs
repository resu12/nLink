using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [InlineData(FileTransferDirection.Inbound, true)]
    [Trait("FileTransferCoordinatorSuite", "InitialTunaSource")]
    public void InitialTunaSource_OnlyExactInitialPublicationCanBindWithoutCommit(FileTransferDirection direction, bool independentGenerations)
    {
        var state = CreateInitialTunaBootstrapSourceFixture(direction, independentGenerations);
        var source = FileTransferCoordinator.CaptureFallbackBootstrapSourceScope(state);
        Assert.NotNull(source);
        Assert.Equal(("file_tuna_v4", 4, 0, 0, 1), (source.Source.RouteToken, source.Source.ProtocolVersion,
            source.Source.LiveRouteEpoch, source.Source.RouteCommitGeneration, source.Source.RoutePublicationGeneration));
        Assert.Equal(independentGenerations ? 7 : 1, source.Source.RuntimeGeneration);
        Assert.Equal(independentGenerations ? 13 : 1, source.Source.LegGeneration);
        Assert.True(FileTransferCoordinator.IsFallbackBootstrapLocalStopScopeCurrent(state, source));
        Assert.Null(state.CurrentRouteCommit);
        Assert.True(FileTransferCoordinator.CanStartFallbackBootstrapExchange(state)); // Eligibility is not a hold receipt or exchange authority.
        var publication = state.CurrentRoutePublicationToken!.Value;
        var owner = state.CurrentRuntimeOwner!.Value;
        foreach (var invalid in new[]
        {
            state with { IsTerminal = true }, state with { TerminalGeneration = 1 },
            state with { SessionId = "other" }, state with { TransferId = "other" },
            state with { Direction = direction == FileTransferDirection.Inbound ? FileTransferDirection.Outbound : FileTransferDirection.Inbound },
            state with { CurrentRoutePublicationToken = null }, state with { CurrentRuntimeOwner = null }, state with { CurrentLeg = null },
            state with { CurrentRuntimeOwner = owner with { RuntimeGeneration = owner.RuntimeGeneration + 1 } },
            state with { LastRuntimeOwnerGeneration = owner.RuntimeGeneration + 1 },
            state with { CurrentRoutePublicationToken = publication with { RuntimeOwner = owner with { TransferId = "other" } } },
            state with { CurrentRoutePublicationToken = publication with { LegGeneration = publication.LegGeneration + 1 } },
            state with { CurrentRoutePublicationToken = publication with { ActionKind = FileTransferCoordinatorActionKind.RouteCommitPublication } },
            state with { CurrentRoutePublicationToken = publication with { RouteCommitGeneration = 1 } },
            state with { CurrentRoutePublicationToken = publication with { PublicationGeneration = 2 }, LastRoutePublicationGeneration = 2 },
            state with { LastRoutePublicationGeneration = 2 }, state with { LastRouteCommitGeneration = 1 },
            state with { LastLiveRouteEpochId = 1 }, state with { CurrentRouteCommit = default(RouteCommitToken) },
            state with { RouteSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast) },
            state with { RouteSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6) },
            state with { CurrentRoutePublicationToken = publication with { ProtocolVersion = 6 } },
            state with { CurrentDataSessionIdentity = null }, state with { CurrentDataSessionGeneration = 0 },
            state with { PersistentState = state.PersistentState with { CoordinatorInstanceId = "replacement" } },
            state with { PersistentState = state.PersistentState with { FileTransferRouteTransition =
                state.PersistentState.FileTransferRouteTransition with { BootstrapAgreement = null } } },
        })
        {
            Assert.Null(FileTransferCoordinator.CaptureFallbackBootstrapSourceScope(invalid));
            Assert.False(FileTransferCoordinator.IsFallbackBootstrapLocalStopScopeCurrent(invalid, source));
            Assert.Null(FileTransferCoordinator.IssueFallbackBootstrapSourceCallback(invalid, 1, 1, false, 0));
        }
        foreach (var invalid in new[]
        {
            source with { LocalIssuer = new string('a', 64) },
            source with { Publication = source.Publication with { RoutePublicationGeneration = 2 } },
            source with { Source = source.Source with { RouteCommitGeneration = 1 } },
            source with { Source = source.Source with { LiveRouteEpoch = 1 } },
            source with { Source = source.Source with { LegGeneration = source.Source.LegGeneration + 1 } },
            source with { Source = source.Source with { TransportEpoch = source.Source.TransportEpoch + 1 } },
            source with { Source = source.Source with { BridgeRecoveryGeneration = 1 } },
            source with { Source = source.Source with { CheckpointGeneration = 1 } },
        }) { Assert.False(FileTransferCoordinator.IsFallbackBootstrapLocalStopScopeCurrent(state, invalid)); }
    }

    [Theory]
    [InlineData("session")] [InlineData("identity")] [InlineData("provider")]
    [InlineData("pause")] [InlineData("terminal")] [InlineData("publication")] [InlineData("copied_callback")]
    [Trait("FileTransferCoordinatorSuite", "InitialTunaSource")]
    public void InitialTunaSource_CallbackCannotOutliveItsExactDataLifetime(string change)
    {
        var state = CreateInitialTunaBootstrapSourceFixture(FileTransferDirection.Outbound, false);
        var callback = FileTransferCoordinator.IssueFallbackBootstrapSourceCallback(state, 1, 7, false, 0)!;
        Assert.NotNull(callback);
        Assert.Equal(FileTransferBootstrapSourceCallbackDisposition.Current,
            FileTransferCoordinator.ResolveFallbackBootstrapSourceCallback(state, callback, 7, false, 0));
        var changed = change == "identity" ? state with { CurrentDataSessionIdentity = "replacement" }
            : MutateBootstrapCallbackStateForTest(state, change);
        if (change == "copied_callback") callback = callback with { LeaseGeneration = 2 };
        Assert.Equal(FileTransferBootstrapSourceCallbackDisposition.Stale,
            FileTransferCoordinator.ResolveFallbackBootstrapSourceCallback(changed, callback,
                change == "provider" ? 8 : 7, change == "pause", change == "pause" ? 1 : 0));
    }

    internal static FileTransferCoordinatorTransitionEnvelope[] CreateInitialTunaSourceCallbackSnapshotFixture(bool terminal)
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        var state = CreateInitialTunaBootstrapSourceFixture(FileTransferDirection.Outbound, false, envelopes);
        var callback = FileTransferCoordinator.IssueFallbackBootstrapSourceCallback(state, 1, 7, false, 0)!;
        Assert.NotNull(callback);
        var admitted = FileTransferCoordinator.CreateBootstrapSourceCallbackSnapshot(state, callback, "Admitted",
            FileTransferBootstrapSourceCallbackDisposition.Current, "none");
        envelopes.Add(admitted.Envelope);
        state = admitted.State;
        if (terminal)
        {
            var stopped = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized,
                state.RouteSelection, reason: FileTransferResultCodes.CanceledLocal), state);
            envelopes.Add(stopped.SnapshotEnvelope!);
            state = stopped.State;
        }
        var disposition = FileTransferCoordinator.ResolveFallbackBootstrapSourceCallback(state, callback, 7, false, 0);
        Assert.Equal(terminal ? FileTransferBootstrapSourceCallbackDisposition.Stale : FileTransferBootstrapSourceCallbackDisposition.Current, disposition);
        envelopes.Add(FileTransferCoordinator.CreateBootstrapSourceCallbackSnapshot(state, callback, "Consumed", disposition, "none").Envelope);
        return envelopes.ToArray();
    }

    private static FileTransferCoordinatorState CreateInitialTunaBootstrapSourceFixture(FileTransferDirection direction, bool independentGenerations,
        List<FileTransferCoordinatorTransitionEnvelope>? envelopes = null, bool pairedIssuers = false)
    {
        var preparation = CreateSessionPreparationFixture(ready: true);
        var reserved = FileTransferCoordinator.ReserveSessionPreparationAdmission(preparation.State, preparation.Permit.Scope,
            "transfer_coordinator", direction, preparation.Now);
        var claimed = FileTransferCoordinator.ClaimSessionPreparationReadiness(reserved.State, reserved.Admission!, preparation.Lease, preparation.Now);
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, claimed.Status);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var empty = EmptyState(tuna, direction) with
        {
            PersistentState = new() { CoordinatorInstanceId = "initial-source-" + direction },
            CurrentDataSessionIdentity = "initial-source-data-session", CurrentDataSessionGeneration = 17,
            LastRuntimeOwnerGeneration = independentGenerations ? 6 : 0,
            LastTransferLegGeneration = independentGenerations ? 12 : 0,
        };
        var state = empty;
        string Issuer(FileTransferDirection role) => role == direction ? FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(state)
            : FileTransferBootstrapCorrelation.CreateIssuer(pairedIssuers ? "initial-source-" + role : "other-peer",
                state.SessionId, state.TransferId, role);
        var offer = new FileTransferOfferV2
        {
            SessionId = state.SessionId, TransferId = state.TransferId, FileName = "file.bin", FileSizeBytes = 4,
            PreferredDataProtocolVersion = 4, FileTransferRoute = tuna.TelemetryToken,
            FallbackBootstrapCorrelationVersion = 1, FallbackBootstrapIssuerId = Issuer(FileTransferDirection.Outbound),
        };
        Apply(FileTransferCoordinator.PinFallbackBootstrapOffer(state, offer));
        Apply(FileTransferCoordinator.ApplyFallbackBootstrapAccept(state, new()
        {
            SessionId = state.SessionId, TransferId = state.TransferId, AcceptedDataProtocolVersion = 4, FileTransferRoute = tuna.TelemetryToken,
            FallbackBootstrapCorrelationVersion = 1, FallbackBootstrapIssuerId = Issuer(FileTransferDirection.Inbound),
        }));
        if (direction == FileTransferDirection.Inbound) Apply(FileTransferCoordinator.ApplyFallbackBootstrapSessionOpen(state, new()
        {
            SessionId = state.SessionId, TransferId = state.TransferId, ProtocolVersion = 4, FileTransferRoute = tuna.TelemetryToken,
            SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = 1024, InitialPipelineDepth = 1,
            FallbackBootstrapCorrelationVersion = 1, FallbackBootstrapIssuerId = offer.FallbackBootstrapIssuerId,
        }));
        var start = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer,
            tuna, targetTransport: FileTransferTransportKind.Tuna, initialTunaReadinessEvidence: claimed.Evidence), state);
        Assert.True(start.InitialTunaRouteSelectionDecision?.Accepted);
        envelopes?.Add(start.SnapshotEnvelope!);
        var publication = FileTransferCoordinator.AuthorizeAction(start.State,
            new(FileTransferCoordinatorActionKind.RoutePublication, Source: "initial_source_test", Reason: "initial_publication",
                RuntimeOwner: start.State.CurrentRuntimeOwner!.Value, Route: tuna.Route, ProtocolVersion: 4,
                LiveRouteEpoch: 0, LegGeneration: start.State.CurrentLeg!.Generation,
                RuntimeGeneration: start.State.CurrentRuntimeOwner.Value.RuntimeGeneration, TerminalGeneration: 0));
        Assert.True(publication.Accepted, publication.RejectionReason);
        envelopes?.Add(publication.SnapshotEnvelope!);
        return publication.State;
        void Apply(FileTransferBootstrapAgreementDecision decision)
        {
            Assert.True(decision.Accepted, decision.Reason);
            envelopes?.Add(decision.SnapshotEnvelope!);
            state = decision.State;
        }
    }
}
