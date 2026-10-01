using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InitialOutboundOffer_SelectsBeforeOwnerlessPinAndPreservesSnapshotChain(bool ready, bool correlated)
    {
        var state = InitialOfferState();
        var start = InitialOfferStart(state, ready);
        var draft = InitialOfferDraft(state, correlated);
        var result = FileTransferCoordinator.ApplyInitialOutboundOffer(start, state, draft, correlated);
        var expected = ready ? FileTransferRoute.FileTunaV4 : FileTransferRoute.RegularNknV4Fast;
        Assert.NotNull(result.Start.StartedLeg);
        Assert.Equal(expected, result.Start.State.RouteSelection.Route);
        Assert.Equal(expected, result.Start.State.CurrentRuntimeOwner!.Value.Route);
        Assert.Equal(4, result.Start.State.RouteSelection.ProtocolVersion);
        Assert.Equal(ready, result.Start.InitialTunaRouteSelectionDecision!.Value.Accepted);
        Assert.Equal(!ready, result.Start.InitialTunaRouteSelectionDecision.Value.FallbackApplied);
        Assert.Null(result.Start.State.CurrentRoutePublicationToken);
        if (correlated)
        {
            var pin = Assert.IsType<FileTransferBootstrapAgreementDecision>(result.Offer);
            Assert.True(pin.Accepted);
            Assert.Null(pin.State.CurrentRuntimeOwner);
            Assert.Null(pin.State.CurrentLeg);
            var agreement = pin.State.PersistentState.FileTransferRouteTransition.BootstrapAgreement!;
            Assert.Equal(draft with { FileTransferRoute = FileTransferRouteResolver.Resolve(expected).TelemetryToken }, agreement.Offer);
            Assert.Equal(0, agreement.SourceBoundary.RuntimeGeneration);
            Assert.Equal(agreement, result.Start.State.PersistentState.FileTransferRouteTransition.BootstrapAgreement);
            var first = pin.SnapshotEnvelope!;
            var second = result.Start.SnapshotEnvelope!;
            Assert.Equal("PinFallbackBootstrapOffer", first.DecisionKind);
            Assert.Equal("StartTransfer", second.DecisionKind);
            Assert.Equal(first.Aggregate.AfterStateGeneration, second.Aggregate.BeforeStateGeneration);
            Assert.Equal(first.Aggregate.AfterStateFingerprint, second.Aggregate.BeforeStateFingerprint);
        }
        else
        {
            Assert.Null(result.Offer);
            Assert.Null(result.Start.State.PersistentState.FileTransferRouteTransition.BootstrapAgreement);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialOutboundOffer_RejectsStaleScopeAndCannotRepinAfterStart(bool correlated)
    {
        var source = InitialOfferState();
        var draft = InitialOfferDraft(source, correlated);
        var start = InitialOfferStart(source, false);
        var started = FileTransferCoordinator.ApplyInitialOutboundOffer(start, source, draft, correlated).Start.State;
        foreach (var stale in new[]
        {
            source with { IsTerminal = true },
            source with { SessionId = "replacement" },
            source with { TransferId = "replacement" },
            source with { Direction = FileTransferDirection.Inbound },
            source with { LastRuntimeOwnerGeneration = 1 },
            source with { LastTransferLegGeneration = 1 },
            started,
        })
        {
            var rejected = FileTransferCoordinator.ApplyInitialOutboundOffer(start, stale, draft, correlated);
            Assert.Null(rejected.Offer);
            Assert.Null(rejected.Start.StartedLeg);
            Assert.Equal("initial_offer_source_not_fresh", rejected.Start.MutationRejectedReason);
            Assert.Same(stale, rejected.Start.State);
        }
        var pinned = FileTransferCoordinator.PinFallbackBootstrapOffer(source, draft).State;
        Assert.Same(pinned, FileTransferCoordinator.ApplyInitialOutboundOffer(start, pinned, draft, correlated).Start.State);
        if (!correlated)
        {
            var late = FileTransferCoordinator.PinFallbackBootstrapOffer(started, draft);
            Assert.False(late.Accepted);
            Assert.Same(started, late.State);
        }
    }

    [Fact]
    public void InitialOutboundOffer_RejectedReadinessOrInvalidPinCannotStartRuntime()
    {
        var source = InitialOfferState();
        var draft = InitialOfferDraft(source, true);
        var strict = InitialOfferStart(source, false) with { AllowInitialRouteFallback = false };
        var rejected = FileTransferCoordinator.ApplyInitialOutboundOffer(strict, source, draft, true);
        Assert.Null(rejected.Offer);
        Assert.Equal("initial_tuna_readiness_missing", rejected.Start.MutationRejectedReason);
        Assert.Same(source, rejected.Start.State);
        var invalid = FileTransferCoordinator.ApplyInitialOutboundOffer(InitialOfferStart(source, false), source,
            draft with { FallbackBootstrapIssuerId = new string('a', 64) }, true);
        Assert.False(invalid.Offer!.Accepted);
        Assert.Null(invalid.Start.StartedLeg);
        Assert.Same(source, invalid.Start.State);
    }

    private static FileTransferCoordinatorState InitialOfferState()
        => EmptyState(FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4)) with
        {
            PersistentState = new() { CoordinatorInstanceId = "initial-offer" },
        };

    private static FileTransferCoordinatorEvent InitialOfferStart(FileTransferCoordinatorState state, bool ready)
        => CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer, state.RouteSelection,
            targetTransport: FileTransferTransportKind.Tuna,
            initialTunaReadinessEvidence: ready ? InitialTunaEvidence(state) : null,
            allowInitialRouteFallback: true,
            initialRouteFallbackSelection: FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast));

    private static FileTransferOfferV2 InitialOfferDraft(FileTransferCoordinatorState state, bool correlated)
        => new()
        {
            SessionId = state.SessionId, TransferId = state.TransferId, FileName = "offer.bin", FileSizeBytes = 4096,
            PreferredDataProtocolVersion = 4, FileTransferRoute = FileTransferRouteResolver.FileTunaV4Token,
            FallbackBootstrapCorrelationVersion = correlated ? 1 : null,
            FallbackBootstrapIssuerId = correlated ? FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(state) : null,
        };
}
