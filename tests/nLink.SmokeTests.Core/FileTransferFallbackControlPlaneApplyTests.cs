using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(false, "exact")]
    [InlineData(true, "exact")]
    [InlineData(false, "stale-epoch")]
    [InlineData(true, "stale-epoch")]
    [InlineData(false, "stale-owner")]
    [InlineData(true, "stale-owner")]
    [InlineData(true, "purpose")]
    [InlineData(true, "peer-invisible")]
    [InlineData(false, "fresh-terminal")]
    [InlineData(true, "fresh-terminal")]
    [InlineData(false, "fresh-transfer")]
    [InlineData(true, "fresh-transfer")]
    [InlineData(false, "fresh-provider")]
    [InlineData(true, "fresh-provider")]
    [InlineData(true, "newer-token")]
    [InlineData(false, "no-fingerprint")]
    [InlineData(true, "no-fingerprint")]
    [InlineData(false, "reconcile")]
    [InlineData(true, "reconcile")]
    public void CombinedFallbackApply_PreservesLegacyAuthorityAndSnapshot(bool completion, string scenario)
    {
        var before = CombinedFallbackState();
        if (completion) before = StoreCombinedFallbackToken(before);
        if (scenario == "no-fingerprint")
            before = before with { PersistentState = before.PersistentState with { SnapshotStateFingerprint = null } };
        if (scenario == "reconcile")
        {
            var origin = CreateCheckpointRepairContinuityFixture(5, 5).State.PersistentState.CheckpointRepairContinuityOrigin;
            Assert.NotNull(origin);
            before = before with { PersistentState = before.PersistentState with { CheckpointRepairContinuityOrigin = origin } };
        }
        var token = before.CurrentFallbackControlPlaneToken;
        var request = FallbackActionRequest(
            completion ? FileTransferCoordinatorActionKind.ControlPlaneSendCompletion : FileTransferCoordinatorActionKind.ControlPlaneSend,
            before.CurrentRuntimeOwner!.Value, before.CurrentLeg!,
            fallbackControlPlaneToken: token, controlPlaneKind: FileTransferControlPlaneKind.ReceiverState,
            controlPlanePurpose: "feedback", peerVisible: true, acceptedAny: true);
        request = scenario switch
        {
            "stale-epoch" => request with { LiveRouteEpoch = request.LiveRouteEpoch + 1 },
            "stale-owner" => request with { RuntimeOwner = request.RuntimeOwner!.Value with { RuntimeGeneration = 999 } },
            "purpose" => request with { ControlPlanePurpose = "replacement-purpose" },
            "peer-invisible" => request with { ControlPlanePeerVisible = false },
            _ => request,
        };
        var fresh = scenario switch
        {
            "fresh-terminal" => before with { IsTerminal = true, TerminalGeneration = before.TerminalGeneration + 1 },
            "fresh-transfer" => before with { TransferId = "replacement-transfer" },
            "fresh-provider" => before with { CurrentDataSessionProviderGeneration = 99 },
            "newer-token" => StoreCombinedFallbackToken(before),
            _ => before with { },
        };
        var legacyAuthorization = FileTransferCoordinator.AuthorizeAction(before, request);
        FileTransferCoordinatorDecision? legacyApplication = null;
        if (legacyAuthorization.Accepted && legacyAuthorization.FallbackControlPlaneToken is { } legacyToken)
            legacyApplication = FileTransferCoordinator.Apply(Event(legacyToken), fresh);

        var calls = new List<string>();
        var actual = FileTransferCoordinator.AuthorizeAndApplyFallbackControlPlane(before, request,
            issued => { calls.Add("event"); return Event(issued); },
            () => { calls.Add("state"); return fresh; });
        Assert.Equal(legacyAuthorization with { State = before, SnapshotEnvelope = null },
            actual.Authorization with { State = before, SnapshotEnvelope = null });
        if (!legacyAuthorization.Accepted)
        {
            Assert.Null(actual.Application);
            Assert.Empty(calls);
            Assert.NotNull(actual.Authorization.SnapshotEnvelope);
            Assert.Equal(CanonicalSnapshot(legacyAuthorization.SnapshotEnvelope!),
                CanonicalSnapshot(actual.Authorization.SnapshotEnvelope!));
            return;
        }
        Assert.Equal(new[] { "event", "state" }, calls);
        if (scenario == "reconcile" && !completion)
        {
            Assert.NotNull(actual.Authorization.SnapshotEnvelope);
            Assert.Null(actual.Authorization.State.PersistentState.CheckpointRepairContinuityOrigin);
            Assert.Equal(CanonicalSnapshot(legacyAuthorization.SnapshotEnvelope!),
                CanonicalSnapshot(actual.Authorization.SnapshotEnvelope!));
        }
        else Assert.Null(actual.Authorization.SnapshotEnvelope);
        if (scenario == "reconcile" && completion)
        {
            // Legacy idempotent completion does not reconcile the same state.
            Assert.Same(before.PersistentState.CheckpointRepairContinuityOrigin,
                actual.Authorization.State.PersistentState.CheckpointRepairContinuityOrigin);
        }
        var applied = Assert.IsType<FileTransferCoordinatorDecision>(actual.Application);
        Assert.NotNull(legacyApplication);
        Assert.Equal(legacyApplication.MutationRejectedReason, applied.MutationRejectedReason);
        Assert.Equal(legacyApplication.TerminalMutationRejected, applied.TerminalMutationRejected);
        Assert.Equal(legacyApplication.ControlPlanePeerVisibleProofAccepted, applied.ControlPlanePeerVisibleProofAccepted);
        Assert.Equal(legacyApplication.FallbackControlPlaneToken, applied.FallbackControlPlaneToken);
        Assert.Equal(legacyApplication.State.CurrentFallbackControlPlaneToken, applied.State.CurrentFallbackControlPlaneToken);
        Assert.Equal(legacyApplication.State.LastFallbackControlPlaneTokenGeneration, applied.State.LastFallbackControlPlaneTokenGeneration);
        Assert.Equal(FileTransferCoordinator.CalculateSnapshotStateFingerprint(legacyApplication.State),
            FileTransferCoordinator.CalculateSnapshotStateFingerprint(applied.State));
        Assert.Equal(legacyApplication.State.PersistentState.SnapshotStateGeneration, applied.State.PersistentState.SnapshotStateGeneration);
        Assert.Equal(legacyApplication.State.PersistentState.SnapshotStateFingerprint, applied.State.PersistentState.SnapshotStateFingerprint);
        Assert.Equal(CanonicalSnapshot(legacyApplication.SnapshotEnvelope!), CanonicalSnapshot(applied.SnapshotEnvelope!));
        if (scenario == "fresh-provider") Assert.Equal(99, applied.SnapshotEnvelope!.Aggregate.AfterBoundary.DataSessionProviderGeneration);
        if (scenario == "newer-token")
        {
            Assert.NotEqual(token, fresh.CurrentFallbackControlPlaneToken);
            Assert.Equal(fresh.CurrentFallbackControlPlaneToken, applied.State.CurrentFallbackControlPlaneToken);
        }
        if (scenario == "fresh-transfer") Assert.False(string.IsNullOrWhiteSpace(applied.MutationRejectedReason));

        FileTransferCoordinatorEvent Event(FallbackControlPlaneToken issued) => CombinedFallbackEvent(before, completion, issued);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("kind")]
    [InlineData("token")]
    public void CombinedFallbackApply_RejectsUnrelatedActionOrMismatchedEvent(string mismatch)
    {
        var state = CombinedFallbackState();
        var request = FallbackActionRequest(FileTransferCoordinatorActionKind.ControlPlaneSend,
            state.CurrentRuntimeOwner!.Value, state.CurrentLeg!, controlPlaneKind: FileTransferControlPlaneKind.ReceiverState,
            controlPlanePurpose: "feedback");
        if (mismatch == "action") request = request with { Kind = FileTransferCoordinatorActionKind.RepairReplay };
        var captures = 0;
        Assert.ThrowsAny<ArgumentException>(() => FileTransferCoordinator.AuthorizeAndApplyFallbackControlPlane(
            state, request, token =>
            {
                var e = CombinedFallbackEvent(state, false, token);
                return mismatch == "kind"
                    ? e with { Kind = FileTransferCoordinatorEventKind.Terminalized }
                    : e with { FallbackControlPlaneToken = token with { TokenGeneration = token.TokenGeneration + 1 } };
            }, () => { captures++; return state; }));
        Assert.Equal(0, captures);
        Assert.Null(state.CurrentFallbackControlPlaneToken);
    }

    private static FileTransferCoordinatorState CombinedFallbackState()
    {
        var state = ActiveFallbackStateWithoutCheckpoint(FileTransferDirection.Inbound);
        state = state with
        {
            CurrentDataSessionIdentity = "combined-data-session",
            CurrentDataSessionGeneration = 17,
            CurrentDataSessionProviderGeneration = 7,
            PersistentState = state.PersistentState with
            {
                CoordinatorInstanceId = "combined-fallback",
                SnapshotStateGeneration = 7,
            },
        };
        return state with { PersistentState = state.PersistentState with
            { SnapshotStateFingerprint = FileTransferCoordinator.CalculateSnapshotStateFingerprint(state) } };
    }

    private static FileTransferCoordinatorState StoreCombinedFallbackToken(FileTransferCoordinatorState state)
    {
        var authorization = FileTransferCoordinator.AuthorizeAction(state,
            FallbackActionRequest(FileTransferCoordinatorActionKind.ControlPlaneSend, state.CurrentRuntimeOwner!.Value,
                state.CurrentLeg!, controlPlaneKind: FileTransferControlPlaneKind.ReceiverState, controlPlanePurpose: "feedback"));
        Assert.True(authorization.Accepted, authorization.RejectionReason);
        return FileTransferCoordinator.Apply(CombinedFallbackEvent(state, false, authorization.FallbackControlPlaneToken!.Value), state).State;
    }

    private static FileTransferCoordinatorEvent CombinedFallbackEvent(
        FileTransferCoordinatorState state, bool completion, FallbackControlPlaneToken token)
        => CoordinatorEvent(completion ? FileTransferCoordinatorEventKind.FallbackControlPlaneSendCompleted
                : FileTransferCoordinatorEventKind.FallbackControlPlaneSendAuthorized,
            state.RouteSelection, reason: "feedback", transportEpoch: state.CurrentLeg!.TransportEpochId,
            bridgeRecoveryGeneration: state.CurrentLeg.BridgeRecoveryGeneration, fallbackControlPlaneToken: token,
            controlPlaneKind: FileTransferControlPlaneKind.ReceiverState, controlPlanePurpose: "feedback",
            controlPlanePeerVisible: true, controlPlaneAcceptedAny: true);

    private static string CanonicalSnapshot(FileTransferCoordinatorTransitionEnvelope envelope)
        // Only diagnostic clock/derived transition identity vary between independent calls.
        => (envelope with { DecisionUtcMilliseconds = 0, TransitionId = "(volatile)" }).ToCanonicalJson();
}
