using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferCoordinatorTests
{
    [Fact]
    public void StartTransfer_RegularV4CreatesActiveV4Leg()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = EmptyState(regular);

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                regular,
                state: FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 0,
                highestObservedChunk: -1),
            state);

        Assert.False(decision.TerminalMutationRejected);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.State.RouteSelection.Route);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, decision.State.RouteSelection.ProtocolVersion);
        Assert.True(decision.State.RouteRuntime.UsesV4SparsePump);
        Assert.NotNull(decision.StartedLeg);
        Assert.Equal(FileTransferLegState.Active, decision.State.CurrentLeg!.State);
        Assert.True(decision.State.CurrentLeg.CanSendData);
        Assert.Equal(1, decision.State.LastTransferLegGeneration);
        Assert.Equal(1, decision.State.LastRuntimeOwnerGeneration);
        Assert.NotNull(decision.State.CurrentRuntimeOwner);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.State.CurrentRuntimeOwner!.Value.Route);
        Assert.Equal("regular_v4", decision.State.CurrentRuntimeOwner.Value.RuntimeFamily);
    }

    [Fact]
    public void StartTransfer_ValidInitialTunaEvidenceStartsFileTunaV4()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var state = EmptyState(regular);

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                tuna,
                targetTransport: FileTransferTransportKind.Tuna,
                initialTunaReadinessEvidence: InitialTunaEvidence(state)),
            state);

        Assert.NotNull(decision.StartedLeg);
        Assert.Equal(FileTransferRoute.FileTunaV4, decision.State.RouteSelection.Route);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, decision.State.RouteSelection.ProtocolVersion);
        Assert.True(decision.InitialTunaRouteSelectionDecision?.Accepted);
        Assert.False(decision.InitialTunaRouteSelectionDecision?.FallbackApplied);
        Assert.Equal("accepted", decision.InitialTunaRouteSelectionDecision?.Reason);
    }

    [Fact]
    public void StartTransfer_InitialTunaActiveOnlyEvidenceFallsBackToRegularWhenAllowed()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var state = EmptyState(regular);

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                tuna,
                targetTransport: FileTransferTransportKind.Tuna,
                initialTunaReadinessEvidence: InitialTunaEvidence(
                    state,
                    leaseCurrent: false,
                    listenerReady: false,
                    peerVisibleProof: false,
                    answerReceived: false,
                    tunaNegotiatedObserved: true),
                allowInitialRouteFallback: true,
                initialRouteFallbackSelection: regular),
            state);

        Assert.NotNull(decision.StartedLeg);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.State.RouteSelection.Route);
        Assert.False(decision.InitialTunaRouteSelectionDecision?.Accepted);
        Assert.True(decision.InitialTunaRouteSelectionDecision?.FallbackApplied);
        Assert.Equal("initial_tuna_listener_or_lease_not_ready", decision.InitialTunaRouteSelectionDecision?.Reason);
    }

    [Fact]
    public void StartTransfer_InitialTunaStrictRejectsWhenEvidenceMissing()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var state = EmptyState(regular);

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                tuna,
                targetTransport: FileTransferTransportKind.Tuna),
            state);

        Assert.Null(decision.StartedLeg);
        Assert.Equal("initial_tuna_readiness_missing", decision.MutationRejectedReason);
        Assert.False(decision.InitialTunaRouteSelectionDecision?.Accepted);
        Assert.False(decision.InitialTunaRouteSelectionDecision?.FallbackApplied);
    }

    [Fact]
    public void StartTransfer_InitialTunaRejectsWrongTransferEvidence()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var state = EmptyState(regular);

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                tuna,
                targetTransport: FileTransferTransportKind.Tuna,
                initialTunaReadinessEvidence: InitialTunaEvidence(state, transferId: "other_transfer")),
            state);

        Assert.Null(decision.StartedLeg);
        Assert.Equal("initial_tuna_transfer_mismatch", decision.MutationRejectedReason);
    }

    [Fact]
    public void StartTransfer_InitialTunaRejectsStaleReadinessEvidence()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var state = EmptyState(regular);

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                tuna,
                targetTransport: FileTransferTransportKind.Tuna,
                initialTunaReadinessEvidence: InitialTunaEvidence(
                    state,
                    capturedUtc: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2))),
            state);

        Assert.Null(decision.StartedLeg);
        Assert.Equal("initial_tuna_evidence_not_current", decision.MutationRejectedReason);
        Assert.False(decision.InitialTunaRouteSelectionDecision?.Accepted);
    }

    [Fact]
    public void StartTransfer_SessionReactivationInitialTunaEvidenceAcceptsLongerCurrentWindow()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var state = EmptyState(regular);

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                tuna,
                targetTransport: FileTransferTransportKind.Tuna,
                initialTunaReadinessEvidence: InitialTunaEvidence(
                    state,
                    readinessScope: FileTransferInitialTunaReadinessScopes.SessionReactivation,
                    sourceTransferId: "first_transfer",
                    snapshotGeneration: 3,
                    sourceCapturedUtc: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
                    capturedUtc: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5))),
            state);

        Assert.NotNull(decision.StartedLeg);
        Assert.Equal(FileTransferRoute.FileTunaV4, decision.State.RouteSelection.Route);
        Assert.True(decision.InitialTunaRouteSelectionDecision?.Accepted);
        Assert.False(decision.InitialTunaRouteSelectionDecision?.FallbackApplied);
    }

    [Fact]
    public void StartTransfer_SessionReactivationInitialTunaEvidenceRejectsExpiredWindow()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var state = EmptyState(regular);

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                tuna,
                targetTransport: FileTransferTransportKind.Tuna,
                initialTunaReadinessEvidence: InitialTunaEvidence(
                    state,
                    readinessScope: FileTransferInitialTunaReadinessScopes.SessionReactivation,
                    sourceTransferId: "first_transfer",
                    snapshotGeneration: 3,
                    sourceCapturedUtc: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
                    capturedUtc: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(31))),
            state);

        Assert.Null(decision.StartedLeg);
        Assert.Equal("initial_tuna_evidence_not_current", decision.MutationRejectedReason);
        Assert.False(decision.InitialTunaRouteSelectionDecision?.Accepted);
    }

    [Fact]
    public void StartTransfer_SessionReactivationInitialTunaEvidenceRejectsMissingSourceSnapshot()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var state = EmptyState(regular);

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                tuna,
                targetTransport: FileTransferTransportKind.Tuna,
                initialTunaReadinessEvidence: InitialTunaEvidence(
                    state,
                    readinessScope: FileTransferInitialTunaReadinessScopes.SessionReactivation,
                    capturedUtc: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5))),
            state);

        Assert.Null(decision.StartedLeg);
        Assert.Equal("initial_tuna_session_reactivation_evidence_invalid", decision.MutationRejectedReason);
        Assert.False(decision.InitialTunaRouteSelectionDecision?.Accepted);
    }

    [Fact]
    public void RegularV4ToTunaV4_CreatesRecoveredLiveEpochAndActiveTunaLeg()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);

        var decision = ActivateTuna(started.State, tuna, committedChunk: 4);

        Assert.True(decision.RouteChanged);
        Assert.NotNull(decision.StartedLiveRouteEpoch);
        Assert.NotNull(decision.RecoveredLiveRouteEpoch);
        Assert.Equal(1, decision.StartedLiveRouteEpoch!.EpochId);
        Assert.Equal("recovered", decision.StartedLiveRouteEpoch.State);
        Assert.Equal(FileTransferRoute.FileTunaV4, decision.State.RouteSelection.Route);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, decision.State.CurrentLeg!.ProtocolVersion);
        Assert.True(decision.State.CurrentLeg.CanSendData);
        Assert.Equal(FileTransferLegState.Frozen, decision.FrozenLeg!.State);
        Assert.Equal(2, decision.State.LastRuntimeOwnerGeneration);
        Assert.NotEqual(started.State.CurrentRuntimeOwner, decision.State.CurrentRuntimeOwner);
        Assert.Equal(FileTransferRoute.FileTunaV4, decision.State.CurrentRuntimeOwner!.Value.Route);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FileTransferRouteTransition")]
    public void FileTransferRouteTransition_DirectActiveRouteEventsCannotBypassCoordinatorOperation()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = StartRegular(regular).State;
        foreach (var (kind, target, handoff, transport) in new[]
                 {
                     (
                         FileTransferCoordinatorEventKind.TunaActivated,
                         FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4),
                         FileTransferTransportHandoffKind.NormalToTunaActivation,
                         FileTransferTransportKind.Tuna),
                     (
                         FileTransferCoordinatorEventKind.TunaLost,
                         FileTransferRouteResolver.Resolve(
                             FileTransferRoute.PostTunaFallbackV6),
                         FileTransferTransportHandoffKind.TunaToNormalFallback,
                         FileTransferTransportKind.RegularNkn),
                 })
        {
            var rejected = FileTransferCoordinator.Apply(
                CoordinatorEvent(
                    kind,
                    target,
                    handoff,
                    transport,
                    "direct_route_transition",
                    FileTransferLegState.Active,
                    canSendData: true),
                state);

            Assert.Equal(
                "filetransfer_route_transition_commit_required",
                rejected.MutationRejectedReason);
            Assert.Same(state, rejected.State);
            Assert.False(rejected.RouteChanged);
        }
    }

    [Fact]
    public void RuntimeOwnerToken_OldOwnerInvalidAfterRouteTransition()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var oldOwner = started.State.CurrentRuntimeOwner!.Value;

        var activated = ActivateTuna(started.State, tuna, committedChunk: 0);

        Assert.False(FileTransferCoordinator.IsRuntimeOwnerCurrent(activated.State, oldOwner, out var reason));
        Assert.Equal("runtime_generation_mismatch", reason);
        Assert.True(FileTransferCoordinator.IsRuntimeOwnerCurrent(activated.State, activated.State.CurrentRuntimeOwner!.Value, out reason));
        Assert.Equal("current", reason);
    }

    [Fact]
    public void RuntimeOwnerToken_TerminalGenerationInvalidatesNonTerminalOwner()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;

        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "completed"),
            started.State);

        Assert.True(terminal.State.IsTerminal);
        Assert.Equal(1, terminal.State.TerminalGeneration);
        Assert.False(FileTransferCoordinator.IsRuntimeOwnerCurrent(terminal.State, owner, out var reason));
        Assert.Equal("terminal_generation_mismatch", reason);
    }

    [Fact]
    public void AuthorizeAction_AcceptsCurrentRuntimeOwner()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;

        var decision = FileTransferCoordinator.AuthorizeAction(
            started.State,
            ActionRequest(
                FileTransferCoordinatorActionKind.RuntimeOwnerValidation,
                owner,
                route: owner.Route,
                protocolVersion: owner.ProtocolVersion,
                liveRouteEpoch: owner.LiveRouteEpoch,
                legGeneration: started.State.CurrentLeg!.Generation));

        Assert.True(decision.Accepted);
        Assert.False(decision.TerminalMutationRejected);
        Assert.Equal("accepted", decision.RejectionReason);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.CurrentRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, decision.CurrentProtocolVersion);
        Assert.Equal(owner.RuntimeGeneration, decision.CurrentRuntimeGeneration);
        Assert.Equal(started.State.CurrentLeg.Generation, decision.CurrentLegGeneration);
    }

    [Fact]
    public void AuthorizeAction_RejectsMissingRuntimeOwner()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);

        var decision = FileTransferCoordinator.AuthorizeAction(
            EmptyState(regular),
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RuntimeOwnerValidation,
                Source: "test",
                Reason: "missing_owner"));

        Assert.False(decision.Accepted);
        Assert.False(decision.TerminalMutationRejected);
        Assert.Equal("runtime_owner_missing", decision.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RejectsOldOwnerAfterRouteTransition()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var oldOwner = started.State.CurrentRuntimeOwner!.Value;
        var activated = ActivateTuna(started.State, tuna, committedChunk: 4);

        var decision = FileTransferCoordinator.AuthorizeAction(
            activated.State,
            ActionRequest(FileTransferCoordinatorActionKind.LivenessDeferral, oldOwner));

        Assert.False(decision.Accepted);
        Assert.False(decision.TerminalMutationRejected);
        Assert.Equal("runtime_generation_mismatch", decision.RejectionReason);
        Assert.Equal(activated.State.CurrentRuntimeOwner!.Value.RuntimeGeneration, decision.CurrentRuntimeGeneration);
    }

    [Fact]
    public void AuthorizeAction_RejectsOwnerAfterTerminalGenerationChanges()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "completed"),
            started.State);

        var decision = FileTransferCoordinator.AuthorizeAction(
            terminal.State,
            ActionRequest(FileTransferCoordinatorActionKind.RepairReplay, owner));

        Assert.False(decision.Accepted);
        Assert.True(decision.TerminalMutationRejected);
        Assert.Equal("terminal_state", decision.RejectionReason);
        Assert.Equal(terminal.State.TerminalGeneration, decision.CurrentTerminalGeneration);
    }

    [Fact]
    public void AuthorizeAction_RejectsRouteProtocolAndLiveEpochMismatch()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;

        var routeMismatch = FileTransferCoordinator.AuthorizeAction(
            started.State,
            ActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                owner,
                route: FileTransferRoute.FileTunaV4));
        Assert.False(routeMismatch.Accepted);
        Assert.Equal("route_or_protocol_mismatch", routeMismatch.RejectionReason);

        var protocolMismatch = FileTransferCoordinator.AuthorizeAction(
            started.State,
            ActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSend,
                owner,
                protocolVersion: FileTransferProtocol.ProtocolVersionV6));
        Assert.False(protocolMismatch.Accepted);
        Assert.Equal("route_or_protocol_mismatch", protocolMismatch.RejectionReason);

        var liveEpochMismatch = FileTransferCoordinator.AuthorizeAction(
            started.State,
            ActionRequest(
                FileTransferCoordinatorActionKind.ProgressMutation,
                owner,
                liveRouteEpoch: owner.LiveRouteEpoch + 1));
        Assert.False(liveEpochMismatch.Accepted);
        Assert.Equal("live_route_epoch_mismatch", liveEpochMismatch.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RejectsLegGenerationMismatch()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;

        var decision = FileTransferCoordinator.AuthorizeAction(
            started.State,
            ActionRequest(
                FileTransferCoordinatorActionKind.FallbackCheckpointBegin,
                owner,
                legGeneration: started.State.CurrentLeg!.Generation + 1));

        Assert.False(decision.Accepted);
        Assert.Equal("leg_generation_mismatch", decision.RejectionReason);
        Assert.Equal(started.State.CurrentLeg.Generation, decision.CurrentLegGeneration);
    }

    [Fact]
    public void FallbackControlPlaneSend_AuthorizesAndCompletesWithMatchingToken()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var fallbackDecision = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4);
        var state = fallbackDecision.State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;

        var authorized = FileTransferCoordinator.AuthorizeAction(
            state,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSend,
                owner,
                leg,
                controlPlaneKind: FileTransferControlPlaneKind.FallbackCheckpointRequest,
                controlPlanePurpose: "checkpoint_request"));

        Assert.True(authorized.Accepted);
        Assert.NotNull(authorized.FallbackControlPlaneToken);
        Assert.Equal(FileTransferControlPlaneKind.FallbackCheckpointRequest, authorized.FallbackControlPlaneToken!.Value.ControlPlaneKind);
        Assert.False(authorized.FallbackControlPlaneToken.Value.AuthorityId.IsDefault);

        var stored = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackControlPlaneSendAuthorized,
                fallback,
                reason: "checkpoint_request",
                state: leg.State,
                canSendData: leg.CanSendData,
                transportEpoch: leg.TransportEpochId,
                bridgeRecoveryGeneration: leg.BridgeRecoveryGeneration,
                fallbackControlPlaneToken: authorized.FallbackControlPlaneToken,
                controlPlaneKind: FileTransferControlPlaneKind.FallbackCheckpointRequest,
                controlPlanePurpose: "checkpoint_request"),
            state);
        Assert.NotNull(stored.State.CurrentFallbackControlPlaneToken);
        Assert.True(FileTransferCoordinator.IsFallbackControlPlaneTokenIssued(
            stored.State,
            stored.State.CurrentFallbackControlPlaneToken.Value,
            out _));
        Assert.False(FileTransferCoordinator.IsFallbackControlPlaneTokenIssued(
            stored.State,
            stored.State.CurrentFallbackControlPlaneToken.Value with { AuthorityId = default },
            out _));

        var completed = FileTransferCoordinator.AuthorizeAction(
            stored.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSendCompletion,
                owner,
                leg,
                fallbackControlPlaneToken: stored.State.CurrentFallbackControlPlaneToken,
                controlPlaneKind: FileTransferControlPlaneKind.FallbackCheckpointRequest,
                peerVisible: true,
                acceptedAny: true));

        Assert.True(completed.Accepted);
        Assert.True(completed.ControlPlanePeerVisibleProofAccepted);

        var completion = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackControlPlaneSendCompleted,
                fallback,
                reason: "checkpoint_request_completed",
                state: leg.State,
                canSendData: leg.CanSendData,
                transportEpoch: leg.TransportEpochId,
                bridgeRecoveryGeneration: leg.BridgeRecoveryGeneration,
                fallbackControlPlaneToken: stored.State.CurrentFallbackControlPlaneToken,
                controlPlaneKind: FileTransferControlPlaneKind.FallbackCheckpointRequest,
                controlPlanePeerVisible: true,
                controlPlaneAcceptedAny: true),
            stored.State);
        Assert.True(completion.ControlPlanePeerVisibleProofAccepted);
        Assert.Null(completion.State.CurrentFallbackControlPlaneToken);
    }

    [Fact]
    public void FallbackControlPlaneSendCompletion_AcceptsIssuedTokenWithoutRetiringNewerCurrentToken()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var fallbackDecision = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4);
        var state = fallbackDecision.State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;

        var firstAuthorization = FileTransferCoordinator.AuthorizeAction(
            state,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSend,
                owner,
                leg,
                controlPlaneKind: FileTransferControlPlaneKind.FallbackTailYield,
                controlPlanePurpose: "fallback_tail_yield"));
        Assert.True(firstAuthorization.Accepted);
        var firstStored = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackControlPlaneSendAuthorized,
                fallback,
                reason: "fallback_tail_yield",
                state: leg.State,
                canSendData: leg.CanSendData,
                transportEpoch: leg.TransportEpochId,
                bridgeRecoveryGeneration: leg.BridgeRecoveryGeneration,
                fallbackControlPlaneToken: firstAuthorization.FallbackControlPlaneToken,
                controlPlaneKind: FileTransferControlPlaneKind.FallbackTailYield,
                controlPlanePurpose: "fallback_tail_yield"),
            state);
        var firstToken = firstStored.State.CurrentFallbackControlPlaneToken;
        Assert.NotNull(firstToken);

        var secondLeg = firstStored.State.CurrentLeg!;
        var secondAuthorization = FileTransferCoordinator.AuthorizeAction(
            firstStored.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSend,
                owner,
                secondLeg,
                controlPlaneKind: FileTransferControlPlaneKind.FallbackCheckpointRequest,
                controlPlanePurpose: "checkpoint_request"));
        Assert.True(secondAuthorization.Accepted);
        var secondStored = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackControlPlaneSendAuthorized,
                fallback,
                reason: "checkpoint_request",
                state: secondLeg.State,
                canSendData: secondLeg.CanSendData,
                transportEpoch: secondLeg.TransportEpochId,
                bridgeRecoveryGeneration: secondLeg.BridgeRecoveryGeneration,
                fallbackControlPlaneToken: secondAuthorization.FallbackControlPlaneToken,
                controlPlaneKind: FileTransferControlPlaneKind.FallbackCheckpointRequest,
                controlPlanePurpose: "checkpoint_request"),
            firstStored.State);
        var secondToken = secondStored.State.CurrentFallbackControlPlaneToken;
        Assert.NotNull(secondToken);
        Assert.NotEqual(firstToken!.Value.TokenGeneration, secondToken!.Value.TokenGeneration);

        var firstCompletionAuthorized = FileTransferCoordinator.AuthorizeAction(
            secondStored.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSendCompletion,
                owner,
                secondStored.State.CurrentLeg!,
                fallbackControlPlaneToken: firstToken,
                controlPlaneKind: FileTransferControlPlaneKind.FallbackTailYield,
                peerVisible: true,
                acceptedAny: true));

        Assert.True(firstCompletionAuthorized.Accepted);
        Assert.True(firstCompletionAuthorized.ControlPlanePeerVisibleProofAccepted);

        var firstCompleted = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackControlPlaneSendCompleted,
                fallback,
                reason: "fallback_tail_yield_completed",
                state: secondStored.State.CurrentLeg!.State,
                canSendData: secondStored.State.CurrentLeg.CanSendData,
                transportEpoch: secondStored.State.CurrentLeg.TransportEpochId,
                bridgeRecoveryGeneration: secondStored.State.CurrentLeg.BridgeRecoveryGeneration,
                fallbackControlPlaneToken: firstToken,
                controlPlaneKind: FileTransferControlPlaneKind.FallbackTailYield,
                controlPlanePeerVisible: true,
                controlPlaneAcceptedAny: true),
            secondStored.State);

        Assert.True(firstCompleted.ControlPlanePeerVisibleProofAccepted);
        Assert.Equal(secondToken.Value.TokenGeneration, firstCompleted.State.CurrentFallbackControlPlaneToken?.TokenGeneration);
    }

    [Fact]
    public void FallbackControlPlaneSendCompletion_AcceptedAnyWithoutPeerVisibleIsNotProof()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var state = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4).State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;
        var token = FileTransferCoordinator.AuthorizeAction(
            state,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSend,
                owner,
                leg,
                controlPlaneKind: FileTransferControlPlaneKind.FallbackCheckpointRequest)).FallbackControlPlaneToken;
        var stored = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackControlPlaneSendAuthorized,
                fallback,
                transportEpoch: leg.TransportEpochId,
                bridgeRecoveryGeneration: leg.BridgeRecoveryGeneration,
                fallbackControlPlaneToken: token),
            state);

        var diagnosticOnly = FileTransferCoordinator.AuthorizeAction(
            stored.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSendCompletion,
                owner,
                leg,
                fallbackControlPlaneToken: stored.State.CurrentFallbackControlPlaneToken,
                peerVisible: false,
                acceptedAny: true));

        Assert.False(diagnosticOnly.Accepted);
        Assert.False(diagnosticOnly.ControlPlanePeerVisibleProofAccepted);
        Assert.Equal("peer_visible_proof_missing", diagnosticOnly.RejectionReason);

        var completion = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackControlPlaneSendCompleted,
                fallback,
                transportEpoch: leg.TransportEpochId,
                bridgeRecoveryGeneration: leg.BridgeRecoveryGeneration,
                fallbackControlPlaneToken: stored.State.CurrentFallbackControlPlaneToken,
                controlPlanePeerVisible: false,
                controlPlaneAcceptedAny: true),
            stored.State);

        Assert.Equal("peer_visible_proof_missing", completion.MutationRejectedReason);
        Assert.False(completion.ControlPlanePeerVisibleProofAccepted);
        Assert.NotNull(completion.State.CurrentFallbackControlPlaneToken);
    }

    [Fact]
    public void FallbackCheckpointBegin_IssuesLeaseAndAcceptReissueRetireRequireCurrentLease()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var state = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4).State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;

        var begin = FileTransferCoordinator.AuthorizeAction(
            state,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.FallbackCheckpointBegin,
                owner,
                leg,
                checkpointRequestId: "checkpoint:lease",
                checkpointGeneration: leg.CheckpointGeneration));
        Assert.True(begin.Accepted);
        Assert.NotNull(begin.FallbackCheckpointLease);

        var requested = RequestFallbackCheckpoint(state, fallback, "checkpoint:lease", leg.TransportEpochId);
        var lease = requested.State.CurrentFallbackCheckpointLease;
        Assert.NotNull(lease);
        Assert.Equal("checkpoint:lease", lease!.Value.CheckpointRequestId);

        var missingLease = FileTransferCoordinator.AuthorizeAction(
            requested.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.FallbackCheckpointAccept,
                requested.State.CurrentRuntimeOwner!.Value,
                requested.State.CurrentLeg!,
                checkpointRequestId: "checkpoint:lease",
                checkpointGeneration: requested.State.CurrentLeg!.CheckpointGeneration));
        Assert.False(missingLease.Accepted);
        Assert.Equal("fallback_checkpoint_lease_missing", missingLease.RejectionReason);

        var staleLease = lease.Value with { CheckpointGeneration = lease.Value.CheckpointGeneration + 1 };
        var staleReissue = FileTransferCoordinator.AuthorizeAction(
            requested.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.FallbackCheckpointReissue,
                requested.State.CurrentRuntimeOwner!.Value,
                requested.State.CurrentLeg!,
                checkpointRequestId: "checkpoint:lease",
                checkpointGeneration: requested.State.CurrentLeg!.CheckpointGeneration,
                fallbackCheckpointLease: staleLease));
        Assert.False(staleReissue.Accepted);
        Assert.Equal("checkpoint_generation_mismatch", staleReissue.RejectionReason);

        var currentRetire = FileTransferCoordinator.AuthorizeAction(
            requested.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.FallbackCheckpointRetire,
                requested.State.CurrentRuntimeOwner!.Value,
                requested.State.CurrentLeg!,
                checkpointRequestId: "checkpoint:lease",
                checkpointGeneration: requested.State.CurrentLeg!.CheckpointGeneration,
                fallbackCheckpointLease: lease));
        Assert.True(currentRetire.Accepted);

        var accepted = AcceptFallbackCheckpoint(requested.State, fallback, "checkpoint:lease", leg.TransportEpochId, 12, 18);
        Assert.NotNull(accepted.FallbackCheckpointLease);
        Assert.Equal(FallbackCheckpointLeaseState.Accepted, accepted.FallbackCheckpointLease!.Value.State);
        Assert.Null(accepted.State.CurrentFallbackCheckpointLease);
    }

    [Fact]
    public void FallbackTailYield_AuthorizesOnlyCurrentFallbackAuthority()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var pendingRequested = RequestFallbackCheckpoint(
            FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4).State,
            fallback,
            "checkpoint:tail",
            4);
        var pendingOwner = pendingRequested.State.CurrentRuntimeOwner!.Value;
        var pendingLeg = pendingRequested.State.CurrentLeg!;

        var pendingQuiescent = FileTransferCoordinator.AuthorizeAction(
            pendingRequested.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.FallbackTailQuiescent,
                pendingOwner,
                pendingLeg,
                fallbackTailYieldKind: FallbackTailYieldKind.Quiescent));
        Assert.True(pendingQuiescent.Accepted);
        Assert.Equal("(none)", pendingQuiescent.FallbackTailYieldDecision!.Value.CheckpointRequestIdSnapshot);
        Assert.DoesNotContain("checkpoint:tail", pendingQuiescent.FallbackTailYieldDecision.Value.OwnerKey, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpoint:tail", pendingQuiescent.FallbackTailYieldDecision.Value.PairKey, StringComparison.Ordinal);

        var pendingQuiescentApplied = MarkFallbackTailQuiescent(pendingRequested.State, fallback, 4, 12, 18);
        Assert.Null(pendingQuiescentApplied.State.CurrentFallbackCheckpointLease);
        Assert.Equal("(none)", pendingQuiescentApplied.FallbackTailYieldDecision!.Value.CheckpointRequestIdSnapshot);
        Assert.DoesNotContain("checkpoint:tail", pendingQuiescentApplied.FallbackTailYieldDecision.Value.OwnerKey, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpoint:tail", pendingQuiescentApplied.FallbackTailYieldDecision.Value.PairKey, StringComparison.Ordinal);

        var requested = RequestFallbackCheckpoint(
            FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4).State,
            fallback,
            "checkpoint:tail",
            4);
        var accepted = AcceptFallbackCheckpoint(requested.State, fallback, "checkpoint:tail", 4, 12, 18);
        var owner = accepted.State.CurrentRuntimeOwner!.Value;
        var leg = accepted.State.CurrentLeg!;

        var quiescent = FileTransferCoordinator.AuthorizeAction(
            accepted.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.FallbackTailQuiescent,
                owner,
                leg,
                fallbackTailYieldKind: FallbackTailYieldKind.Quiescent));
        Assert.True(quiescent.Accepted);
        Assert.NotNull(quiescent.FallbackTailYieldDecision);
        Assert.True(quiescent.FallbackTailYieldDecision!.Value.Accepted);
        Assert.False(quiescent.FallbackTailYieldDecision.Value.AuthorityId.IsDefault);
        Assert.Equal(FallbackTailYieldKind.Quiescent, quiescent.FallbackTailYieldDecision.Value.YieldKind);
        Assert.Equal("(none)", quiescent.FallbackTailYieldDecision.Value.CheckpointRequestIdSnapshot);
        Assert.EndsWith("|(none)", quiescent.FallbackTailYieldDecision.Value.OwnerKey, StringComparison.Ordinal);
        Assert.EndsWith("|(none)", quiescent.FallbackTailYieldDecision.Value.PairKey, StringComparison.Ordinal);
        Assert.Equal(9, quiescent.FallbackTailYieldDecision.Value.OwnerKey.Split('|').Length);
        Assert.Equal(7, quiescent.FallbackTailYieldDecision.Value.PairKey.Split('|').Length);
        Assert.Equal(leg.BridgeRecoveryGeneration, quiescent.FallbackTailYieldDecision.Value.CurrentBridgeRecoveryGeneration);
        Assert.DoesNotContain("checkpoint:tail", quiescent.FallbackTailYieldDecision.Value.OwnerKey, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpoint:tail", quiescent.FallbackTailYieldDecision.Value.PairKey, StringComparison.Ordinal);

        var firstYield = MarkFallbackTailQuiescent(accepted.State, fallback, 4, 12, 18);
        var successor = FileTransferCoordinator.AuthorizeAction(
            firstYield.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.FallbackTailQuiescent,
                firstYield.State.CurrentRuntimeOwner!.Value,
                firstYield.State.CurrentLeg!,
                fallbackTailYieldKind: FallbackTailYieldKind.Quiescent));
        Assert.True(successor.Accepted);
        Assert.Equal(
            firstYield.FallbackTailYieldDecision!.Value.AuthorityId,
            successor.FallbackTailYieldDecision!.Value.PredecessorAuthorityId);

        var failed = FileTransferCoordinator.AuthorizeAction(
            accepted.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.FallbackTailFailed,
                owner,
                leg,
                fallbackTailYieldKind: FallbackTailYieldKind.Failed));
        Assert.True(failed.Accepted);
        Assert.Equal(FallbackTailYieldKind.Failed, failed.FallbackTailYieldDecision!.Value.YieldKind);
        Assert.Equal("(none)", failed.FallbackTailYieldDecision.Value.CheckpointRequestIdSnapshot);

        var staleTransport = FileTransferCoordinator.AuthorizeAction(
            accepted.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.FallbackTailQuiescent,
                owner,
                leg,
                transportEpoch: leg.TransportEpochId + 1,
                fallbackTailYieldKind: FallbackTailYieldKind.Quiescent));
        Assert.False(staleTransport.Accepted);
        Assert.Equal("transport_epoch_mismatch", staleTransport.RejectionReason);

        var currentYield = firstYield.FallbackTailYieldDecision!.Value;
        Assert.True(FileTransferCoordinator.IsFallbackTailYieldDecisionCurrent(firstYield.State, currentYield, out _));
        firstYield.State.CurrentLeg!.BridgeRecoveryGeneration++;
        Assert.False(FileTransferCoordinator.IsFallbackTailYieldDecisionCurrent(firstYield.State, currentYield, out var bridgeReason));
        Assert.Equal("fallback_tail_yield_authority_tuple_mismatch", bridgeReason);
    }

    [Fact]
    public void FallbackTailYield_PairScopeExcludesDirectionLocalBridgeGeneration()
    {
        const string sessionId = "session_pair_scope";
        const string transferId = "transfer_pair_scope";
        const string route = "post_tuna_fallback_v6";
        const int protocolVersion = FileTransferProtocol.ProtocolVersionV6;
        const int liveRouteEpoch = 4;
        const int legGeneration = 5;
        const long transportEpoch = 9;

        var ownerAtBridgeOne = FileTransferCoordinator.BuildFallbackTailYieldOwnerKey(
            sessionId,
            transferId,
            route,
            protocolVersion,
            liveRouteEpoch,
            legGeneration,
            bridgeRecoveryGeneration: 1,
            transportEpoch,
            "(none)");
        var ownerAtBridgeTwo = FileTransferCoordinator.BuildFallbackTailYieldOwnerKey(
            sessionId,
            transferId,
            route,
            protocolVersion,
            liveRouteEpoch,
            legGeneration,
            bridgeRecoveryGeneration: 2,
            transportEpoch,
            "(none)");
        var pairKey = FileTransferCoordinator.BuildFallbackTailYieldPairKey(
            sessionId,
            transferId,
            route,
            protocolVersion,
            liveRouteEpoch,
            transportEpoch,
            "(none)");

        Assert.NotEqual(ownerAtBridgeOne, ownerAtBridgeTwo);
        Assert.Equal(9, ownerAtBridgeOne.Split('|').Length);
        Assert.Equal(9, ownerAtBridgeTwo.Split('|').Length);
        Assert.Equal(7, pairKey.Split('|').Length);
        Assert.Equal("session_pair_scope|transfer_pair_scope|post_tuna_fallback_v6|6|4|9|(none)", pairKey);
    }

    [Fact]
    public void FallbackTailYield_TransportEpochReauthorizationPreservesExactPredecessorAcrossRestart()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var fallbackState = FallBack(
            StartRegular(regular).State,
            fallback,
            committedChunk: 8,
            transportEpoch: 4).State;
        var fallbackOwner = Assert.IsType<RuntimeOwnerToken>(fallbackState.CurrentRuntimeOwner);
        var fallbackLeg = Assert.IsType<FileTransferLeg>(fallbackState.CurrentLeg);
        var publication = FileTransferCoordinator.AuthorizeAction(
            fallbackState,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "fallback_route_publication",
                RuntimeOwner: fallbackOwner,
                Route: fallback.Route,
                ProtocolVersion: fallback.ProtocolVersion,
                LiveRouteEpoch: fallbackLeg.LiveRouteEpochId,
                LegGeneration: fallbackLeg.Generation,
                RuntimeGeneration: fallbackOwner.RuntimeGeneration,
                TerminalGeneration: fallbackState.TerminalGeneration,
                TargetRoute: FileTransferRoute.PostTunaFallbackV6,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
                TargetTransport: FileTransferTransportKind.RegularNkn));
        Assert.True(publication.Accepted);
        var accepted = AcceptFallbackCheckpoint(
            RequestFallbackCheckpoint(
                publication.State,
                fallback,
                "checkpoint:reauthorization",
                4).State,
            fallback,
            "checkpoint:reauthorization",
            4,
            12,
            18);
        var yielded = MarkFallbackTailQuiescent(accepted.State, fallback, 4, 12, 18);
        var predecessor = Assert.IsType<FallbackTailYieldDecision>(yielded.FallbackTailYieldDecision);
        var sourceOwner = Assert.IsType<RuntimeOwnerToken>(yielded.State.CurrentRuntimeOwner);
        var sourcePublication = Assert.IsType<RoutePublicationToken>(yielded.State.CurrentRoutePublicationToken);
        var sourceLeg = Assert.IsType<FileTransferLeg>(yielded.State.CurrentLeg);

        sourceLeg.TransportEpochId = 5;
        sourceLeg.BridgeRecoveryGeneration++;
        Assert.False(
            FileTransferCoordinator.IsFallbackTailYieldDecisionCurrent(
                yielded.State,
                predecessor,
                out var staleReason));
        Assert.Equal("fallback_tail_yield_authority_tuple_mismatch", staleReason);

        var request = new FallbackTailYieldTransportEpochReauthorizationRequest(
            predecessor,
            sourceOwner,
            sourcePublication,
            SourceTransportEpoch: 4,
            TargetTransportEpoch: 5,
            Reason: "post_yield_recovery_restart");
        var replacementBeforeCapture = FileTransferCoordinator.AuthorizeAction(
            yielded.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "replacement_before_reauthorization_capture",
                RuntimeOwner: sourceOwner,
                Route: fallback.Route,
                ProtocolVersion: fallback.ProtocolVersion,
                LiveRouteEpoch: sourceLeg.LiveRouteEpochId,
                LegGeneration: sourceLeg.Generation,
                RuntimeGeneration: sourceOwner.RuntimeGeneration,
                TerminalGeneration: yielded.State.TerminalGeneration,
                TargetRoute: FileTransferRoute.PostTunaFallbackV6,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
                TargetTransport: FileTransferTransportKind.RegularNkn));
        Assert.True(replacementBeforeCapture.Accepted);
        Assert.NotEqual(sourcePublication, replacementBeforeCapture.State.CurrentRoutePublicationToken);
        var replacementStart = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                fallback,
                targetTransport: FileTransferTransportKind.RegularNkn,
                reason: "replacement_before_reauthorization_capture",
                state: FileTransferLegState.RecoveryActive,
                canSendData: true,
                committedChunk: 12,
                highestObservedChunk: 18,
                transportEpoch: 5,
                bridgeRecoveryGeneration: sourceLeg.BridgeRecoveryGeneration,
                fallbackTailYieldTransportEpochReauthorizationRequest: request),
            replacementBeforeCapture.State);
        Assert.Equal(
            "fallback_tail_yield_transport_epoch_reauthorization_publication_missing",
            replacementStart.MutationRejectedReason);

        var restarted = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                fallback,
                targetTransport: FileTransferTransportKind.RegularNkn,
                reason: "post_yield_recovery_restart",
                state: FileTransferLegState.RecoveryActive,
                canSendData: true,
                committedChunk: 12,
                highestObservedChunk: 18,
                transportEpoch: 5,
                bridgeRecoveryGeneration: sourceLeg.BridgeRecoveryGeneration,
                fallbackTailYieldTransportEpochReauthorizationRequest: request),
            yielded.State);

        Assert.Null(restarted.MutationRejectedReason);
        Assert.Null(restarted.State.LastFallbackTailYieldDecision);
        var reauthorization = Assert.IsType<FallbackTailYieldTransportEpochReauthorization>(
            restarted.State.PendingFallbackTailYieldTransportEpochReauthorization);
        Assert.Equal(predecessor, reauthorization.PredecessorDecision);
        Assert.Equal(sourcePublication, reauthorization.SourceRoutePublicationToken);
        Assert.Equal(predecessor.OwnerKey, reauthorization.SourceOwnerKey);
        Assert.Equal(predecessor.PairKey, reauthorization.SourcePairKey);
        Assert.Equal(4, reauthorization.SourceTransportEpoch);
        Assert.Equal(5, reauthorization.TargetTransportEpoch);
        Assert.Equal(restarted.State.CurrentRuntimeOwner, reauthorization.TargetRuntimeOwner);
        var targetPublication = Assert.IsType<RoutePublicationToken>(
            reauthorization.TargetRoutePublicationToken);
        Assert.Equal(restarted.State.CurrentRoutePublicationToken, targetPublication);
        Assert.Null(reauthorization.TargetBridgeRecoveryDecision);
        Assert.True(
            FileTransferCoordinator.IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                restarted.State,
                reauthorization,
                out _));

        var targetOwner = Assert.IsType<RuntimeOwnerToken>(restarted.State.CurrentRuntimeOwner);
        var targetLeg = Assert.IsType<FileTransferLeg>(restarted.State.CurrentLeg);
        var missingDescriptor = FileTransferCoordinator.AuthorizeAction(
            restarted.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.FallbackTailQuiescent,
                targetOwner,
                targetLeg,
                fallbackTailYieldKind: FallbackTailYieldKind.Quiescent));
        Assert.False(missingDescriptor.Accepted);
        Assert.Equal(
            "fallback_tail_yield_transport_epoch_reauthorization_missing",
            missingDescriptor.RejectionReason);

        var authorized = FileTransferCoordinator.AuthorizeAction(
            restarted.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.FallbackTailQuiescent,
                targetOwner,
                targetLeg,
                fallbackTailYieldKind: FallbackTailYieldKind.Quiescent,
                fallbackTailYieldTransportEpochReauthorization: reauthorization));
        Assert.True(authorized.Accepted);
        Assert.Equal(
            predecessor.AuthorityId,
            authorized.FallbackTailYieldDecision!.Value.PredecessorAuthorityId);
        Assert.Null(authorized.State.PendingFallbackTailYieldTransportEpochReauthorization);

        var applied = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailQuiescent,
                fallback,
                reason: "fallback_tail_transport_epoch_reauthorized",
                state: FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 12,
                highestObservedChunk: 18,
                transportEpoch: 5,
                bridgeRecoveryGeneration: targetLeg.BridgeRecoveryGeneration,
                fallbackTailYieldKind: FallbackTailYieldKind.Quiescent,
                fallbackTailYieldTransportEpochReauthorization: reauthorization),
            restarted.State);
        var successor = Assert.IsType<FallbackTailYieldDecision>(applied.FallbackTailYieldDecision);
        Assert.Equal(predecessor.AuthorityId, successor.PredecessorAuthorityId);
        Assert.Equal(predecessor.YieldGeneration + 1, successor.YieldGeneration);
        Assert.Null(applied.State.PendingFallbackTailYieldTransportEpochReauthorization);
        Assert.True(FileTransferCoordinator.IsFallbackTailYieldDecisionCurrent(applied.State, successor, out _));

        var duplicate = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailQuiescent,
                fallback,
                transportEpoch: 5,
                bridgeRecoveryGeneration: targetLeg.BridgeRecoveryGeneration,
                fallbackTailYieldTransportEpochReauthorization: reauthorization),
            applied.State);
        Assert.Equal(
            "fallback_tail_yield_transport_epoch_reauthorization_stale",
            duplicate.MutationRejectedReason);
        Assert.Equal(successor, duplicate.State.LastFallbackTailYieldDecision);

        var replacementPublication = FileTransferCoordinator.AuthorizeAction(
            restarted.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "replacement_publication",
                RuntimeOwner: targetOwner,
                Route: fallback.Route,
                ProtocolVersion: fallback.ProtocolVersion,
                LiveRouteEpoch: targetLeg.LiveRouteEpochId,
                LegGeneration: targetLeg.Generation,
                RuntimeGeneration: targetOwner.RuntimeGeneration,
                TerminalGeneration: restarted.State.TerminalGeneration,
                TargetRoute: FileTransferRoute.PostTunaFallbackV6,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
                TargetTransport: FileTransferTransportKind.RegularNkn));
        Assert.True(replacementPublication.Accepted);
        Assert.Null(replacementPublication.State.PendingFallbackTailYieldTransportEpochReauthorization);

        var replacementBridge = FileTransferCoordinator.AuthorizeAction(
            restarted.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeRecovery,
                targetOwner,
                targetLeg,
                bridgeRecoveryKind: BridgeRecoveryKind.ReceiveRecoveryRequest));
        Assert.True(replacementBridge.Accepted);
        var firstTargetBridge = Assert.IsType<BridgeRecoveryDecision>(replacementBridge.BridgeRecoveryDecision);
        var bridgeBoundReauthorization = Assert.IsType<FallbackTailYieldTransportEpochReauthorization>(
            replacementBridge.State.PendingFallbackTailYieldTransportEpochReauthorization);
        Assert.Equal(firstTargetBridge, bridgeBoundReauthorization.TargetBridgeRecoveryDecision);
        Assert.True(
            FileTransferCoordinator.IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                replacementBridge.State,
                bridgeBoundReauthorization,
                out _));
        var sameEpochBridgeReplacement = FileTransferCoordinator.AuthorizeAction(
            replacementBridge.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeRecovery,
                targetOwner,
                targetLeg,
                bridgeRecoveryKind: BridgeRecoveryKind.ReceiveRecoveryRequest));
        Assert.False(sameEpochBridgeReplacement.Accepted);
        Assert.Equal(
            "bridge_recovery_execution_pending",
            sameEpochBridgeReplacement.RejectionReason);
        Assert.Equal(
            bridgeBoundReauthorization,
            sameEpochBridgeReplacement.State.PendingFallbackTailYieldTransportEpochReauthorization);

        targetLeg.TransportEpochId = 6;
        targetLeg.BridgeRecoveryGeneration++;
        var rebound = FileTransferCoordinator.RebindFallbackTailYieldTransportEpochReauthorization(
            replacementBridge.State,
            bridgeBoundReauthorization,
            targetTransportEpoch: 6,
            reason: "newer_transport_epoch_adopted");
        Assert.Null(rebound.MutationRejectedReason);
        var reboundReauthorization = Assert.IsType<FallbackTailYieldTransportEpochReauthorization>(
            rebound.State.PendingFallbackTailYieldTransportEpochReauthorization);
        Assert.Equal(predecessor, reboundReauthorization.PredecessorDecision);
        Assert.Equal(bridgeBoundReauthorization.SourceOwnerKey, reboundReauthorization.SourceOwnerKey);
        Assert.Equal(bridgeBoundReauthorization.SourcePairKey, reboundReauthorization.SourcePairKey);
        Assert.Equal(6, reboundReauthorization.TargetTransportEpoch);
        Assert.Equal(targetLeg.BridgeRecoveryGeneration, reboundReauthorization.TargetBridgeRecoveryGeneration);
        Assert.Equal(
            bridgeBoundReauthorization.ReauthorizationGeneration + 1,
            reboundReauthorization.ReauthorizationGeneration);
        Assert.Equal(
            bridgeBoundReauthorization.TargetRoutePublicationToken,
            reboundReauthorization.TargetRoutePublicationToken);
        Assert.Equal(firstTargetBridge, reboundReauthorization.TargetBridgeRecoveryDecision);
        Assert.False(
            FileTransferCoordinator.IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                rebound.State,
                reboundReauthorization,
                out var bridgeRebindPendingReason));
        Assert.Equal(
            "fallback_tail_yield_transport_epoch_reauthorization_tuple_mismatch",
            bridgeRebindPendingReason);
        var reboundBridge = FileTransferCoordinator.AuthorizeAction(
            rebound.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeRecovery,
                targetOwner,
                targetLeg,
                bridgeRecoveryKind: BridgeRecoveryKind.ReceiveRecoveryRequest));
        Assert.True(reboundBridge.Accepted);
        var reboundBridgeDecision = Assert.IsType<BridgeRecoveryDecision>(reboundBridge.BridgeRecoveryDecision);
        var reboundBridgeBound = Assert.IsType<FallbackTailYieldTransportEpochReauthorization>(
            reboundBridge.State.PendingFallbackTailYieldTransportEpochReauthorization);
        Assert.Equal(reboundBridgeDecision, reboundBridgeBound.TargetBridgeRecoveryDecision);
        Assert.Equal(predecessor, reboundBridgeBound.PredecessorDecision);
        Assert.True(
            FileTransferCoordinator.IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                reboundBridge.State,
                reboundBridgeBound,
                out _));

        var injectedConflict = restarted.State with
        {
            LastFallbackTailYieldDecision = predecessor,
        };
        Assert.False(
            FileTransferCoordinator.IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                injectedConflict,
                reauthorization,
                out var injectedConflictReason));
        Assert.Equal(
            "fallback_tail_yield_transport_epoch_reauthorization_lineage_conflict",
            injectedConflictReason);

        var conflictingLineage = MarkFallbackTailFailed(
            restarted.State,
            fallback,
            targetLeg.TransportEpochId);
        Assert.Null(conflictingLineage.MutationRejectedReason);
        Assert.Null(conflictingLineage.State.PendingFallbackTailYieldTransportEpochReauthorization);
        Assert.False(
            FileTransferCoordinator.IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                conflictingLineage.State,
                reauthorization,
                out var conflictingLineageReason));
        Assert.Equal(
            "fallback_tail_yield_transport_epoch_reauthorization_stale",
            conflictingLineageReason);
    }

    [Fact]
    public void FallbackOwnershipActions_RejectStaleAuthorityFields()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var state = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4).State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;

        Assert.Equal("live_route_epoch_mismatch", FileTransferCoordinator.AuthorizeAction(
            state,
            FallbackActionRequest(FileTransferCoordinatorActionKind.ControlPlaneSend, owner, leg, liveRouteEpoch: leg.LiveRouteEpochId + 1)).RejectionReason);
        Assert.Equal("leg_generation_mismatch", FileTransferCoordinator.AuthorizeAction(
            state,
            FallbackActionRequest(FileTransferCoordinatorActionKind.ControlPlaneSend, owner, leg, legGeneration: leg.Generation + 1)).RejectionReason);
        Assert.Equal("transport_epoch_mismatch", FileTransferCoordinator.AuthorizeAction(
            state,
            FallbackActionRequest(FileTransferCoordinatorActionKind.ControlPlaneSend, owner, leg, transportEpoch: leg.TransportEpochId + 1)).RejectionReason);
        Assert.Equal("bridge_recovery_generation_mismatch", FileTransferCoordinator.AuthorizeAction(
            state,
            FallbackActionRequest(FileTransferCoordinatorActionKind.FallbackControlPlanePressure, owner, leg, bridgeRecoveryGeneration: leg.BridgeRecoveryGeneration + 1)).RejectionReason);

        var requested = RequestFallbackCheckpoint(state, fallback, "checkpoint:stale", leg.TransportEpochId);
        var checkpointLeg = requested.State.CurrentLeg!;
        var checkpointOwner = requested.State.CurrentRuntimeOwner!.Value;
        Assert.Equal("checkpoint_request_mismatch", FileTransferCoordinator.AuthorizeAction(
            requested.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSend,
                checkpointOwner,
                checkpointLeg,
                checkpointRequestId: "checkpoint:old",
                checkpointGeneration: checkpointLeg.CheckpointGeneration)).RejectionReason);
        Assert.Equal("checkpoint_generation_mismatch", FileTransferCoordinator.AuthorizeAction(
            requested.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.FallbackCheckpointBegin,
                checkpointOwner,
                checkpointLeg,
                checkpointRequestId: "checkpoint:stale",
                checkpointGeneration: checkpointLeg.CheckpointGeneration + 1)).RejectionReason);
    }

    [Fact]
    public void Terminalization_InvalidatesFallbackOwnershipArtifacts()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var fallbackState = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4).State;
        var owner = fallbackState.CurrentRuntimeOwner!.Value;
        var leg = fallbackState.CurrentLeg!;
        var sendToken = FileTransferCoordinator.AuthorizeAction(
            fallbackState,
            FallbackActionRequest(FileTransferCoordinatorActionKind.ControlPlaneSend, owner, leg)).FallbackControlPlaneToken;
        var sendStored = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackControlPlaneSendAuthorized,
                fallback,
                transportEpoch: leg.TransportEpochId,
                bridgeRecoveryGeneration: leg.BridgeRecoveryGeneration,
                fallbackControlPlaneToken: sendToken),
            fallbackState);
        var pressure = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackControlPlanePressureUpdated,
                fallback,
                transportEpoch: leg.TransportEpochId,
                bridgeRecoveryGeneration: leg.BridgeRecoveryGeneration),
            sendStored.State);
        var requested = RequestFallbackCheckpoint(pressure.State, fallback, "checkpoint:terminal", leg.TransportEpochId);
        var lease = requested.State.CurrentFallbackCheckpointLease;

        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, fallback, reason: "completed"),
            requested.State);

        Assert.True(terminal.State.IsTerminal);
        Assert.Null(terminal.State.CurrentFallbackControlPlaneToken);
        Assert.Null(terminal.State.CurrentFallbackCheckpointLease);
        Assert.Null(terminal.State.CurrentFallbackControlPlanePressureToken);
        Assert.Null(terminal.State.LastFallbackTailYieldDecision);

        Assert.Equal("terminal_state", FileTransferCoordinator.AuthorizeAction(
            terminal.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSendCompletion,
                owner,
                leg,
                fallbackControlPlaneToken: sendToken,
                peerVisible: true,
                acceptedAny: true)).RejectionReason);
        Assert.Equal("terminal_state", FileTransferCoordinator.AuthorizeAction(
            terminal.State,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.FallbackCheckpointAccept,
                owner,
                leg,
                fallbackCheckpointLease: lease,
                checkpointRequestId: "checkpoint:terminal")).RejectionReason);
    }

    [Fact]
    public void RegularV4RepairAuthority_ForwardsCoordinatorIdentityAndFullTuple()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;
        var leg = started.State.CurrentLeg!;
        var publication = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "regular_v4_repair_publication",
                RuntimeOwner: owner,
                Route: regular.Route,
                ProtocolVersion: regular.ProtocolVersion,
                LiveRouteEpoch: leg.LiveRouteEpochId,
                LegGeneration: leg.Generation,
                RuntimeGeneration: owner.RuntimeGeneration,
                TerminalGeneration: started.State.TerminalGeneration));

        Assert.True(publication.Accepted);
        var repair = FileTransferCoordinator.AuthorizeAction(
            publication.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                owner,
                publication.State.CurrentLeg!,
                repairReplayKind: RepairReplayKind.V4Repair,
                predecessorAuthorityId: new FileTransferAuthorityId("fta2-repair-replay-predecessor")));

        Assert.True(repair.Accepted);
        Assert.NotNull(repair.RepairReplayDecision);
        var repairDecision = repair.RepairReplayDecision!.Value;
        Assert.False(repairDecision.AuthorityId.IsDefault);
        Assert.Equal(
            "fta2-repair-replay-predecessor",
            repairDecision.PredecessorAuthorityId!.Value.Value);

        Assert.True(FileTransferCoordinator.TryIssueRegularV4FrontierRepairAuthority(
            repairDecision,
            new RegularV4FrontierRepairAuthorityRequest(
                "regular-v4-test-repair",
                FrontierChunkIndex: 12,
                DurableReceivedHighestChunkIndex: 11,
                PendingRepairCount: 2,
                DecisionReason: "regular_v4_frontier_repair"),
            out var authority,
            out var rejectionReason));
        Assert.Equal("accepted", rejectionReason);
        Assert.NotNull(authority);
        Assert.Equal(repairDecision.AuthorityId, authority!.AuthorityId);
        Assert.Equal(repairDecision.PredecessorAuthorityId, authority.PredecessorAuthorityId);
        Assert.Equal(regular.TelemetryToken, authority.RouteToken);
        Assert.True(FileTransferCoordinator.IsRepairReplayDecisionCurrent(
            repair.State,
            repairDecision,
            out var currentReason));
        Assert.Equal("accepted", currentReason);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RegularV4FrontierRepairPressure")]
    public void RegularV4FrontierRepairPressure_RejectsSameAuthorityDecisionTupleMutation()
    {
        var (state, decision) = AuthorizeRegularV4FrontierRepair();
        var publication = Assert.IsType<RoutePublicationToken>(decision.RoutePublicationToken);
        var mutations = new (string Name, RepairReplayDecision Decision, string ExpectedReason)[]
        {
            (
                "accepted",
                decision with { Accepted = false },
                "repair_replay_decision_missing"),
            (
                "predecessor",
                decision with
                {
                    PredecessorAuthorityId = new FileTransferAuthorityId(
                        "fta2-regular-v4-pressure-mutated-predecessor"),
                },
                "repair_replay_decision_superseded"),
            (
                "repair_kind",
                decision with { RepairKind = RepairReplayKind.PeerSilenceReplay },
                "repair_replay_decision_superseded"),
            (
                "reason",
                decision with { Reason = decision.Reason + "_mutated" },
                "repair_replay_decision_superseded"),
            (
                "bridge_decision_generation",
                decision with { BridgeDecisionGeneration = decision.BridgeDecisionGeneration + 1 },
                "repair_replay_decision_superseded"),
            (
                "route_publication_token",
                decision with
                {
                    RoutePublicationToken = publication with
                    {
                        PublicationGeneration = publication.PublicationGeneration + 1,
                    },
                },
                "repair_replay_decision_superseded"),
        };

        foreach (var mutation in mutations)
        {
            Assert.Equal(decision.AuthorityId, mutation.Decision.AuthorityId);
            Assert.False(
                FileTransferCoordinator.IsRepairReplayDecisionCurrent(
                    state,
                    mutation.Decision,
                    out var currentReason),
                mutation.Name);
            Assert.Equal(mutation.ExpectedReason, currentReason);

            var staged = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
                state,
                mutation.Decision,
                RegularV4PressureAuthorityRequest($"regular-v4-pressure:tuple:{mutation.Name}"),
                "regular_v4_receiver_frontier_repair_due");
            Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected, staged.Status);
            Assert.Equal(mutation.ExpectedReason, staged.Reason);
            Assert.Null(staged.Binding);
            Assert.Null(PressureState(staged.State).PendingBinding);
            Assert.Null(PressureState(staged.State).InstalledBinding);
        }
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RegularV4FrontierRepairPressure")]
    public void RegularV4FrontierRepairPressure_CoordinatorOwnsInstallAndExactSettlement()
    {
        var (initialState, initialDecision) = AuthorizeRegularV4FrontierRepair();
        var request = RegularV4PressureAuthorityRequest("regular-v4-pressure:12:2");
        var staged = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
            initialState,
            initialDecision,
            request,
            "regular_v4_receiver_frontier_repair_due");

        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, staged.Status);
        var pending = Assert.IsType<RegularV4FrontierRepairPressureBinding>(staged.Binding);
        var installCommand =
            Assert.IsType<FileTransferRegularV4FrontierRepairPressureInstallCommand>(
                staged.InstallCommand);
        Assert.Equal(1, pending.InstallGeneration);
        Assert.Equal(RegularV4FrontierRepairPressureBindingState.Pending, pending.State);
        Assert.Equal(pending.Authority, installCommand.Pressure.RepairAuthority);
        Assert.Equal(pending.InstallGeneration, installCommand.InstallGeneration);
        Assert.Equal(7, installCommand.Pressure.FrontierLagChunks);
        Assert.Null(PressureState(staged.State).InstalledBinding);
        Assert.True(
            FileTransferCoordinator.TryGetPendingRegularV4FrontierRepairPressureInstall(
                staged.State,
                pending.Authority.AuthorityId,
                out var queriedPending,
                out var queriedPendingCommand));
        Assert.Equal(pending, queriedPending);
        Assert.Same(installCommand, queriedPendingCommand);

        var duplicateStage = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
            staged.State,
            initialDecision,
            request,
            "regular_v4_receiver_frontier_repair_due");
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent, duplicateStage.Status);
        Assert.Equal(pending, duplicateStage.Binding);
        Assert.Same(installCommand, duplicateStage.InstallCommand);

        var mutatedStage = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
            staged.State,
            initialDecision,
            request with { FrontierLagChunks = request.FrontierLagChunks + 1 },
            "regular_v4_receiver_frontier_repair_due");
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
            mutatedStage.Status);
        Assert.Equal(
            "regular_v4_frontier_repair_pressure_binding_pending_conflict",
            mutatedStage.Reason);
        Assert.Same(staged.State, mutatedStage.State);
        Assert.Same(installCommand, PressureState(mutatedStage.State).PendingInstallCommand);

        var pendingRepeat = FileTransferCoordinator.AuthorizeAction(
            staged.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                initialDecision.RuntimeOwner,
                staged.State.CurrentLeg!,
                reason: initialDecision.Reason,
                repairReplayKind: initialDecision.RepairKind,
                predecessorAuthorityId: initialDecision.PredecessorAuthorityId));
        Assert.True(pendingRepeat.Accepted);
        Assert.Same(staged.State, pendingRepeat.State);
        Assert.Equal(initialDecision, pendingRepeat.RepairReplayDecision);
        Assert.Equal(
            staged.State.LastRepairReplayGeneration,
            pendingRepeat.State.LastRepairReplayGeneration);

        var rejectedReceipt = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
            staged.State,
            new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                pending.Authority,
                FileTransferRegularV4FrontierRepairPressureInstallStatus.Rejected,
                "adapter_rejected",
                pending.InstallGeneration,
                pending.PressureReason));
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected, rejectedReceipt.Status);
        Assert.Equal(pending, PressureState(rejectedReceipt.State).PendingBinding);
        Assert.Same(installCommand, PressureState(rejectedReceipt.State).PendingInstallCommand);
        Assert.Null(PressureState(rejectedReceipt.State).InstalledBinding);

        var installedReceipt = new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
            pending.Authority,
            FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed,
            "installed",
            pending.InstallGeneration,
            pending.PressureReason);
        var committed = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
            rejectedReceipt.State,
            installedReceipt);
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, committed.Status);
        Assert.Null(PressureState(committed.State).PendingBinding);
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingState.Installed,
            PressureState(committed.State).InstalledBinding!.Value.State);
        Assert.Same(installCommand, PressureState(committed.State).InstalledInstallCommand);
        Assert.NotNull(PressureState(committed.State).InstalledInstallReceipt);
        Assert.True(
            FileTransferCoordinator.TryGetInstalledRegularV4FrontierRepairPressureInstall(
                committed.State,
                pending.Authority.AuthorityId,
                out var queriedInstalled,
                out var queriedInstalledCommand,
                out var queriedInstalledReceipt));
        Assert.Equal(PressureState(committed.State).InstalledBinding, queriedInstalled);
        Assert.Same(installCommand, queriedInstalledCommand);
        Assert.Equal(PressureState(committed.State).InstalledInstallReceipt, queriedInstalledReceipt);

        var exactDuplicateCommit = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
            committed.State,
            installedReceipt);
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent,
            exactDuplicateCommit.Status);
        Assert.Same(committed.State, exactDuplicateCommit.State);

        var duplicateCommit = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
            exactDuplicateCommit.State,
            new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                pending.Authority,
                FileTransferRegularV4FrontierRepairPressureInstallStatus.Idempotent,
                "regular_v4_frontier_repair_pressure_install_idempotent",
                pending.InstallGeneration,
                pending.PressureReason));
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent, duplicateCommit.Status);
        Assert.Same(exactDuplicateCommit.State, duplicateCommit.State);
        Assert.Equal(
            PressureState(committed.State).LastInstallGeneration,
            PressureState(duplicateCommit.State).LastInstallGeneration);

        var arbitraryReasonDuplicateCommit =
            FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
                duplicateCommit.State,
                new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                    pending.Authority,
                    FileTransferRegularV4FrontierRepairPressureInstallStatus.Idempotent,
                    "installed",
                    pending.InstallGeneration,
                    pending.PressureReason));
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
            arbitraryReasonDuplicateCommit.Status);
        Assert.Equal(
            "regular_v4_frontier_repair_pressure_binding_install_receipt_mismatch",
            arbitraryReasonDuplicateCommit.Reason);
        Assert.Same(duplicateCommit.State, arbitraryReasonDuplicateCommit.State);

        var arbitraryStatusDuplicateCommit =
            FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
                duplicateCommit.State,
                new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                    pending.Authority,
                    FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed,
                    "regular_v4_frontier_repair_pressure_install_idempotent",
                    pending.InstallGeneration,
                    pending.PressureReason));
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
            arbitraryStatusDuplicateCommit.Status);
        Assert.Same(duplicateCommit.State, arbitraryStatusDuplicateCommit.State);

        var rejectedDuplicateCommit = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
            duplicateCommit.State,
            new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                pending.Authority,
                FileTransferRegularV4FrontierRepairPressureInstallStatus.Rejected,
                "adapter_rejected_late",
                pending.InstallGeneration,
                pending.PressureReason));
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
            rejectedDuplicateCommit.Status);
        Assert.Equal(
            PressureState(duplicateCommit.State).InstalledBinding,
            PressureState(rejectedDuplicateCommit.State).InstalledBinding);

        var missingReasonDuplicateCommit = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
            duplicateCommit.State,
            new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                pending.Authority,
                FileTransferRegularV4FrontierRepairPressureInstallStatus.Idempotent,
                " ",
                pending.InstallGeneration,
                pending.PressureReason));
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
            missingReasonDuplicateCommit.Status);

        var conflict = FileTransferCoordinator.AuthorizeAction(
            committed.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                committed.State.CurrentRuntimeOwner!.Value,
                committed.State.CurrentLeg!,
                reason: "regular_v4_pressure_conflicting_no_predecessor",
                repairReplayKind: RepairReplayKind.V4Repair));
        Assert.False(conflict.Accepted);
        Assert.Equal(
            "regular_v4_frontier_repair_pressure_binding_predecessor_mismatch",
            conflict.RejectionReason);
        Assert.Same(committed.State, conflict.State);
        Assert.Equal(
            committed.State.CurrentRepairReplayDecision,
            conflict.State.CurrentRepairReplayDecision);
        Assert.Equal(
            PressureState(committed.State).InstalledBinding,
            PressureState(conflict.State).InstalledBinding);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RegularV4FrontierRepairPressure")]
    public void RegularV4FrontierRepairPressure_SuccessorRetiresOnlyExactPredecessor()
    {
        var (initialState, initialDecision) = AuthorizeRegularV4FrontierRepair();
        var initialStage = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
            initialState,
            initialDecision,
            RegularV4PressureAuthorityRequest("regular-v4-pressure:12:2"),
            "regular_v4_receiver_frontier_repair_due");
        var initial = initialStage.Binding!.Value;
        var installedInitial = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
            initialStage.State,
            new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                initial.Authority,
                FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed,
                "installed",
                initial.InstallGeneration,
                initial.PressureReason));

        var (successorState, successorDecision) = AuthorizeRegularV4FrontierRepair(
            installedInitial.State,
            initial.Authority.AuthorityId);
        Assert.Equal(initial.Authority.AuthorityId, successorDecision.PredecessorAuthorityId);
        Assert.Equal(initialDecision.RepairGeneration + 1, successorDecision.RepairGeneration);
        Assert.Equal(successorDecision, successorState.CurrentRepairReplayDecision);
        Assert.Equal(
            initial.Authority.AuthorityId,
            PressureState(installedInitial.State).InstalledBinding!.Value.Authority.AuthorityId);
        var successorStage = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
            successorState,
            successorDecision,
            RegularV4PressureAuthorityRequest("regular-v4-pressure:12:1"),
            "regular_v4_frontier_repair_successor_pressure");
        var successor = successorStage.Binding!.Value;
        Assert.Equal(2, successor.InstallGeneration);
        Assert.Null(PressureState(successorStage.State).RetainedPredecessorBinding);

        var installedSuccessor = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
            successorStage.State,
            new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                successor.Authority,
                FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed,
                "installed",
                successor.InstallGeneration,
                successor.PressureReason));
        Assert.Equal(
            initial.Authority.AuthorityId,
            PressureState(installedSuccessor.State).RetainedPredecessorBinding!.Value.Authority.AuthorityId);
        var successorSettlement = RegularV4PressureSettlement(
            successor.Authority,
            FileTransferRegularV4FrontierRepairSettlementOutcome.SettledStalled);
        var outOfOrderSuccessor = FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureSettlement(
            installedSuccessor.State,
            successorSettlement);
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected, outOfOrderSuccessor.Status);
        Assert.Equal(
            "regular_v4_frontier_repair_pressure_binding_predecessor_retirement_pending",
            outOfOrderSuccessor.Reason);
        Assert.Null(outOfOrderSuccessor.SettlementCommand);

        var predecessorSettlement = RegularV4PressureSettlement(
            initial.Authority,
            FileTransferRegularV4FrontierRepairSettlementOutcome.Superseded,
            successor.Authority.AuthorityId);
        var prepared = FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureSettlement(
            installedSuccessor.State,
            predecessorSettlement);
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, prepared.Status);
        Assert.Equal(successor.InstallGeneration, prepared.SettlementCommand!.InstallGeneration);
        Assert.Equal(successor.Authority.AuthorityId, prepared.SettlementCommand.CurrentAuthorityId);

        var predecessorWrongScope = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(
            installedSuccessor.State,
            new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                prepared.SettlementCommand!,
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                "predecessor_and_current_cleared",
                CurrentAuthorityCleared: true,
                RetainedPredecessorCleared: true));
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
            predecessorWrongScope.Status);
        Assert.Equal(
            "regular_v4_frontier_repair_pressure_predecessor_clear_scope_mismatch",
            predecessorWrongScope.Reason);
        Assert.Equal(
            PressureState(installedSuccessor.State).InstalledBinding,
            PressureState(predecessorWrongScope.State).InstalledBinding);
        Assert.Equal(
            PressureState(installedSuccessor.State).RetainedPredecessorBinding,
            PressureState(predecessorWrongScope.State).RetainedPredecessorBinding);

        var predecessorRetired = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(
            installedSuccessor.State,
            new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                prepared.SettlementCommand!,
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                "predecessor_retired",
                CurrentAuthorityCleared: false,
                RetainedPredecessorCleared: true));
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, predecessorRetired.Status);
        Assert.Null(PressureState(predecessorRetired.State).RetainedPredecessorBinding);
        Assert.Equal(
            successor.Authority.AuthorityId,
            PressureState(predecessorRetired.State).InstalledBinding!.Value.Authority.AuthorityId);

        var exactPredecessorDuplicate = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(
            predecessorRetired.State,
            new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                prepared.SettlementCommand!,
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent,
                "regular_v4_frontier_repair_pressure_settlement_apply_idempotent",
                CurrentAuthorityCleared: false,
                RetainedPredecessorCleared: true));
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent,
            exactPredecessorDuplicate.Status);

        var arbitraryReasonPredecessorDuplicate =
            FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(
                predecessorRetired.State,
                new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                    prepared.SettlementCommand!,
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent,
                    "predecessor_retired",
                    CurrentAuthorityCleared: false,
                    RetainedPredecessorCleared: true));
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
            arbitraryReasonPredecessorDuplicate.Status);
        Assert.Same(predecessorRetired.State, arbitraryReasonPredecessorDuplicate.State);

        var arbitraryStatusPredecessorDuplicate =
            FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(
                predecessorRetired.State,
                new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                    prepared.SettlementCommand!,
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                    "regular_v4_frontier_repair_pressure_settlement_apply_idempotent",
                    CurrentAuthorityCleared: false,
                    RetainedPredecessorCleared: true));
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
            arbitraryStatusPredecessorDuplicate.Status);
        Assert.Same(predecessorRetired.State, arbitraryStatusPredecessorDuplicate.State);

        var rejectedPredecessorDuplicate = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(
            predecessorRetired.State,
            new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                prepared.SettlementCommand!,
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Rejected,
                "regular_v4_frontier_repair_pressure_settlement_rejected_late",
                CurrentAuthorityCleared: false,
                RetainedPredecessorCleared: true));
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
            rejectedPredecessorDuplicate.Status);

        var mismatchedPredecessorDuplicate = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(
            predecessorRetired.State,
            new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                prepared.SettlementCommand! with { RetainedPredecessorAuthorityId = null },
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent,
                "regular_v4_frontier_repair_pressure_settlement_apply_idempotent",
                CurrentAuthorityCleared: false,
                RetainedPredecessorCleared: true));
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
            mismatchedPredecessorDuplicate.Status);

        var preparedSuccessor = FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureSettlement(
            predecessorRetired.State,
            successorSettlement);
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, preparedSuccessor.Status);
        var successorWrongScope = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(
            predecessorRetired.State,
            new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                preparedSuccessor.SettlementCommand!,
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                "current_and_predecessor_cleared",
                CurrentAuthorityCleared: true,
                RetainedPredecessorCleared: true));
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
            successorWrongScope.Status);
        Assert.Equal(
            "regular_v4_frontier_repair_pressure_current_clear_scope_mismatch",
            successorWrongScope.Reason);
        Assert.Equal(
            PressureState(predecessorRetired.State).InstalledBinding,
            PressureState(successorWrongScope.State).InstalledBinding);

        var retiredSuccessor = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(
            predecessorRetired.State,
            new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                preparedSuccessor.SettlementCommand!,
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                "successor_retired",
                CurrentAuthorityCleared: true,
                RetainedPredecessorCleared: false));
        Assert.Null(PressureState(retiredSuccessor.State).InstalledBinding);
        Assert.Null(PressureState(retiredSuccessor.State).RetainedPredecessorBinding);

        var exactSuccessorDuplicate = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(
            retiredSuccessor.State,
            new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                preparedSuccessor.SettlementCommand!,
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent,
                "regular_v4_frontier_repair_pressure_settlement_apply_idempotent",
                CurrentAuthorityCleared: true,
                RetainedPredecessorCleared: false));
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent,
            exactSuccessorDuplicate.Status);

        var mismatchedSuccessorDuplicate = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(
            retiredSuccessor.State,
            new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                preparedSuccessor.SettlementCommand! with
                {
                    RetainedPredecessorAuthorityId = initial.Authority.AuthorityId,
                },
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent,
                "regular_v4_frontier_repair_pressure_settlement_apply_idempotent",
                CurrentAuthorityCleared: true,
                RetainedPredecessorCleared: false));
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
            mismatchedSuccessorDuplicate.Status);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RegularV4FrontierRepairPressure")]
    public void RegularV4FrontierRepairPressure_SettlementRequiresCompleteAuthorityTuple()
    {
        var installed = StateWithInstalledRegularV4FrontierRepairPressure();
        var authority = PressureState(installed).InstalledBinding!.Value.Authority;
        var exact = RegularV4PressureSettlement(
            authority,
            FileTransferRegularV4FrontierRepairSettlementOutcome.SettledStalled);
        var mismatches = new[]
        {
            exact with { SessionId = exact.SessionId + "-stale" },
            exact with { RuntimeGeneration = exact.RuntimeGeneration + 1 },
            exact with { BridgeDecisionGeneration = exact.BridgeDecisionGeneration + 1 },
            exact with { RoutePublicationGeneration = exact.RoutePublicationGeneration + 1 },
            exact with { RepairKey = exact.RepairKey + ":stale" },
            exact with { DurableReceivedHighestChunkIndex = exact.DurableReceivedHighestChunkIndex + 1 },
            exact with { RequestedChunkCount = exact.RequestedChunkCount + 1 },
            exact with { TargetTransport = FileTransferTransportKind.Tuna },
        };

        foreach (var mismatch in mismatches)
        {
            var decision = FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureSettlement(
                installed,
                mismatch);
            Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected, decision.Status);
            Assert.Equal(
                "regular_v4_frontier_repair_pressure_settlement_authority_mismatch",
                decision.Reason);
            Assert.Null(decision.SettlementCommand);
            Assert.Equal(
                authority.AuthorityId,
                PressureState(decision.State).InstalledBinding!.Value.Authority.AuthorityId);
        }
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RegularV4FrontierRepairPressure")]
    public void RegularV4FrontierRepairPressure_ScopeChangingEventsRequireExactRetirement()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        foreach (var (bindingKind, state) in StatesWithRegularV4FrontierRepairPressure())
        {
            var start = FileTransferCoordinator.Apply(
                CoordinatorEvent(
                    FileTransferCoordinatorEventKind.StartTransfer,
                    regular,
                    reason: $"start_with_{bindingKind}_pressure"),
                state);
            AssertPressureRetirementRequired(state, start, $"start:{bindingKind}");

            var admission = RequestRuntimeUnlockAdmission(state, state.CurrentRuntimeOwner);
            var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
            var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State);
            var proof = WithCurrentTunaLease(
                RuntimeUnlockTransaction.CreateRouteCommitProof(
                    transaction,
                    "answer_received",
                    admissionToken));
            var routeCommit = FileTransferCoordinator.Apply(
                CoordinatorEvent(
                    FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                    tuna,
                    FileTransferTransportHandoffKind.NormalToTunaActivation,
                    FileTransferTransportKind.Tuna,
                    "runtime_unlock_commit_with_pressure",
                    FileTransferLegState.Active,
                    canSendData: true,
                    runtimeUnlockCommitProof: proof),
                admission.State);
            AssertPressureRetirementRequired(
                admission.State,
                routeCommit,
                $"route-commit:{bindingKind}");
            Assert.Equal(
                "regular_v4_frontier_repair_pressure_retirement_required",
                routeCommit.RuntimeUnlockCommitRejectedReason);

            var terminal = FileTransferCoordinator.Apply(
                CoordinatorEvent(
                    FileTransferCoordinatorEventKind.Terminalized,
                    regular,
                    reason: $"terminal_with_{bindingKind}_pressure"),
                state);
            AssertPressureRetirementRequired(state, terminal, $"terminal:{bindingKind}");

            var admissionRetirement = FileTransferCoordinator.Apply(
                CoordinatorEvent(
                    FileTransferCoordinatorEventKind.RuntimeUnlockAdmissionRetired,
                    regular,
                    reason: $"admission_retirement_with_{bindingKind}_pressure"),
                state);
            AssertPressureRetirementRequired(
                state,
                admissionRetirement,
                $"admission-retirement:{bindingKind}");
        }
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RegularV4FrontierRepairPressure")]
    public void RegularV4FrontierRepairPressure_ScopeChangingAuthorityIssuanceRequiresExactRetirement()
    {
        foreach (var (bindingKind, state) in StatesWithRegularV4FrontierRepairPressure())
        {
            var owner = state.CurrentRuntimeOwner!.Value;
            var leg = state.CurrentLeg!;
            var routePublication = FileTransferCoordinator.AuthorizeAction(
                state,
                new FileTransferCoordinatorActionRequest(
                    FileTransferCoordinatorActionKind.RoutePublication,
                    Source: "test",
                    Reason: $"publication_with_{bindingKind}_pressure",
                    RuntimeOwner: owner,
                    Route: state.RouteSelection.Route,
                    ProtocolVersion: state.RouteSelection.ProtocolVersion,
                    LiveRouteEpoch: leg.LiveRouteEpochId,
                    LegGeneration: leg.Generation,
                    RuntimeGeneration: owner.RuntimeGeneration,
                    TerminalGeneration: state.TerminalGeneration));
            AssertPressureRetirementRequired(state, routePublication, $"publication:{bindingKind}");

            var transportEpoch = FileTransferCoordinator.AuthorizeAction(
                state,
                RecoveryActionRequest(
                    FileTransferCoordinatorActionKind.TransportEpochAdoption,
                    owner,
                    leg,
                    transportEpochActionKind: TransportEpochActionKind.Adopt));
            AssertPressureRetirementRequired(state, transportEpoch, $"transport:{bindingKind}");

            var bridgeRecovery = FileTransferCoordinator.AuthorizeAction(
                state,
                RecoveryActionRequest(
                    FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                    owner,
                    leg,
                    bridgeRecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery));
            AssertPressureRetirementRequired(state, bridgeRecovery, $"bridge:{bindingKind}");
        }
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RegularV4FrontierRepairPressure")]
    public void RegularV4FrontierRepairPressure_RecoveryHandoffCommitsSettlementAndAuthorityAtomically()
    {
        var state = StateWithInstalledRegularV4FrontierRepairPressure();
        var binding = PressureState(state).InstalledBinding!.Value;
        var request = RecoveryActionRequest(
            FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
            state.CurrentRuntimeOwner!.Value,
            state.CurrentLeg!,
            reason: "session_liveness_timeout_pending",
            bridgeRecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery);

        var direct = FileTransferCoordinator.AuthorizeAction(state, request);
        Assert.False(direct.Accepted);
        Assert.Equal(
            "regular_v4_frontier_repair_pressure_retirement_required",
            direct.RejectionReason);
        Assert.Same(state, direct.State);

        var prepared = FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureRecoveryHandoff(
            state,
            request);
        Assert.Equal(RegularV4FrontierRepairPressureRecoveryHandoffStatus.Prepared, prepared.Status);
        Assert.Same(state, prepared.State);
        Assert.Equal(binding, PressureState(prepared.State).InstalledBinding);
        Assert.Null(prepared.State.CurrentBridgeRecoveryDecision);
        Assert.Null(prepared.State.CurrentRecoveryLivenessLease);

        var transition = Assert.IsType<RegularV4FrontierRepairPressureRecoveryHandoffTransition>(
            prepared.Transition);
        Assert.Equal(binding, transition.PressureBinding);
        Assert.Equal(binding.Authority.AuthorityId, transition.Settlement.AuthorityId);
        Assert.Equal(
            FileTransferRegularV4FrontierRepairSettlementOutcome.StaleRejected,
            transition.Settlement.Outcome);
        Assert.Equal(
            "bridge_receive_stall_recovery_pressure_handoff",
            transition.Settlement.DecisionReason);
        Assert.Equal(request, transition.RecoveryRequest);
        Assert.Equal(
            state.LastBridgeRecoveryDecisionGeneration + 1,
            transition.BridgeRecoveryDecision.BridgeDecisionGeneration);
        Assert.Equal(
            state.LastRecoveryLivenessLeaseGeneration + 1,
            transition.RecoveryLivenessLease.LeaseGeneration);
        Assert.Equal(RecoveryLivenessLeaseState.Active, transition.RecoveryLivenessLease.State);

        var duplicatePrepare =
            FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureRecoveryHandoff(state, request);
        Assert.Equal(RegularV4FrontierRepairPressureRecoveryHandoffStatus.Prepared, duplicatePrepare.Status);
        Assert.Equal(transition, duplicatePrepare.Transition);
        Assert.Same(state, duplicatePrepare.State);

        var receipt = new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
            transition.SettlementCommand,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
            "current_authority_cleared",
            CurrentAuthorityCleared: true,
            RetainedPredecessorCleared: false);
        var committed = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureRecoveryHandoff(
            state,
            transition,
            receipt);

        Assert.Equal(RegularV4FrontierRepairPressureRecoveryHandoffStatus.Committed, committed.Status);
        Assert.Null(PressureState(committed.State).PendingBinding);
        Assert.Null(PressureState(committed.State).InstalledBinding);
        Assert.Null(PressureState(committed.State).RetainedPredecessorBinding);
        Assert.Null(committed.State.CurrentRepairReplayDecision);
        Assert.Equal(state.LastRepairReplayGeneration, committed.State.LastRepairReplayGeneration);
        Assert.Equal(transition.Settlement, PressureState(committed.State).LastSettlement);
        Assert.Equal(receipt, PressureState(committed.State).LastSettlementApplyReceipt);
        Assert.Equal(transition, PressureState(committed.State).LastRecoveryHandoff);
        Assert.Equal(transition.BridgeRecoveryDecision, committed.State.CurrentBridgeRecoveryDecision);
        Assert.Equal(transition.RecoveryLivenessLease, committed.State.CurrentRecoveryLivenessLease);
        Assert.Equal(
            transition.BridgeRecoveryDecision.BridgeDecisionGeneration,
            committed.State.LastBridgeRecoveryDecisionGeneration);
        Assert.Equal(
            transition.RecoveryLivenessLease.LeaseGeneration,
            committed.State.LastRecoveryLivenessLeaseGeneration);

        var rejectedDuplicate =
            FileTransferCoordinator.CommitRegularV4FrontierRepairPressureRecoveryHandoff(
                committed.State,
                transition,
                receipt with
                {
                    Status = FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent,
                    Reason = "settlement_already_applied",
                });
        Assert.Equal(
            RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected,
            rejectedDuplicate.Status);
        Assert.Same(committed.State, rejectedDuplicate.State);

        var duplicate = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureRecoveryHandoff(
            committed.State,
            transition,
            receipt with
            {
                Status = FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent,
                Reason = "regular_v4_frontier_repair_pressure_settlement_apply_idempotent",
            });
        Assert.Equal(RegularV4FrontierRepairPressureRecoveryHandoffStatus.Idempotent, duplicate.Status);
        Assert.Same(committed.State, duplicate.State);
        Assert.Equal(
            committed.State.LastBridgeRecoveryDecisionGeneration,
            duplicate.State.LastBridgeRecoveryDecisionGeneration);
        Assert.Equal(
            committed.State.LastRecoveryLivenessLeaseGeneration,
            duplicate.State.LastRecoveryLivenessLeaseGeneration);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RegularV4FrontierRepairPressure")]
    public void RegularV4FrontierRepairPressure_RecoveryHandoffFailureRetainsBindingWithoutPartialAuthority()
    {
        var state = StateWithInstalledRegularV4FrontierRepairPressure();
        var request = RecoveryActionRequest(
            FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
            state.CurrentRuntimeOwner!.Value,
            state.CurrentLeg!,
            reason: "session_liveness_timeout_pending",
            bridgeRecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery);
        var prepared = FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureRecoveryHandoff(
            state,
            request);
        var transition = Assert.IsType<RegularV4FrontierRepairPressureRecoveryHandoffTransition>(
            prepared.Transition);

        var rejectedReceipt = new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
            transition.SettlementCommand,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Rejected,
            "pressure_apply_rejected",
            CurrentAuthorityCleared: false,
            RetainedPredecessorCleared: false);
        var rejected = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureRecoveryHandoff(
            state,
            transition,
            rejectedReceipt);
        Assert.Equal(RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected, rejected.Status);
        Assert.Same(state, rejected.State);
        Assert.Equal(
            PressureState(state).InstalledBinding,
            PressureState(rejected.State).InstalledBinding);
        Assert.Null(rejected.State.CurrentBridgeRecoveryDecision);
        Assert.Null(rejected.State.CurrentRecoveryLivenessLease);

        var mismatchedReceipt = rejectedReceipt with
        {
            Command = transition.SettlementCommand with
            {
                InstallGeneration = transition.SettlementCommand.InstallGeneration + 1,
            },
            Status = FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
            CurrentAuthorityCleared = true,
        };
        var mismatched = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureRecoveryHandoff(
            state,
            transition,
            mismatchedReceipt);
        Assert.Equal(RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected, mismatched.Status);
        Assert.Same(state, mismatched.State);

        var mismatchedTransition = transition with
        {
            RecoveryRequest = transition.RecoveryRequest with { Reason = "stale_reason" },
        };
        var mismatchedAuthority = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureRecoveryHandoff(
            state,
            mismatchedTransition,
            rejectedReceipt with
            {
                Status = FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                CurrentAuthorityCleared = true,
            });
        Assert.Equal(
            RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected,
            mismatchedAuthority.Status);
        Assert.Same(state, mismatchedAuthority.State);

        var mismatchedCheckpoint = FileTransferCoordinator
            .CommitRegularV4FrontierRepairPressureRecoveryHandoff(
                state,
                transition with
                {
                    RecoveryRequest = transition.RecoveryRequest with
                    {
                        CheckpointRequestId = "stale_checkpoint",
                    },
                },
                rejectedReceipt with
                {
                    Status = FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                    CurrentAuthorityCleared = true,
                });
        Assert.Equal(
            RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected,
            mismatchedCheckpoint.Status);
        Assert.Same(state, mismatchedCheckpoint.State);

        var terminalState = state with
        {
            IsTerminal = true,
            TerminalGeneration = state.TerminalGeneration + 1,
        };
        var late = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureRecoveryHandoff(
            terminalState,
            transition,
            rejectedReceipt with
            {
                Status = FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                CurrentAuthorityCleared = true,
            });
        Assert.Equal(RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected, late.Status);
        Assert.Same(terminalState, late.State);
        Assert.NotNull(PressureState(late.State).InstalledBinding);
        Assert.Null(late.State.CurrentBridgeRecoveryDecision);
        Assert.Null(late.State.CurrentRecoveryLivenessLease);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RegularV4FrontierRepairPressure")]
    public void RegularV4FrontierRepairPressure_RecoveryHandoffRejectsPendingAndPredecessorLineage()
    {
        foreach (var (bindingKind, state) in StatesWithRegularV4FrontierRepairPressure())
        {
            if (bindingKind == "installed")
            {
                continue;
            }

            var request = RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                state.CurrentRuntimeOwner!.Value,
                state.CurrentLeg!,
                reason: "session_liveness_timeout_pending",
                bridgeRecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery);
            var prepared = FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureRecoveryHandoff(
                state,
                request);

            Assert.Equal(
                RegularV4FrontierRepairPressureRecoveryHandoffStatus.Rejected,
                prepared.Status);
            Assert.Null(prepared.Transition);
            Assert.Same(state, prepared.State);
            Assert.Null(prepared.State.CurrentBridgeRecoveryDecision);
            Assert.Null(prepared.State.CurrentRecoveryLivenessLease);
        }
    }

    [Fact]
    public void FallbackFrontierRepairAuthority_ForwardsCoordinatorIdentityAndFullTuple()
    {
        var predecessorAuthorityId = new FileTransferAuthorityId("fta2-fallback-frontier-predecessor");
        var (state, repairDecision) = AuthorizeFallbackFrontierRepair(predecessorAuthorityId);

        Assert.True(FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
            repairDecision,
            new FallbackFrontierRepairAuthorityRequest(
                "fallback-frontier:12:3:frng-test",
                "frng-test",
                FrontierChunkIndex: 12,
                DurableReceivedHighestChunkIndex: 18,
                RangeCount: 2,
                RequestedChunkCount: 3,
                FallbackFrontierRepairSourceKind.PeerRequest,
                ProtocolRepairRequestId: "v6-frontier:9:12:1",
                RequestCorrelationId: "frq-test",
                CheckpointRequestId: "(none)",
                Priority: "frontier",
                RecoveryMode: "frontier_repair_only",
                DecisionReason: "fallback_frontier_request_received"),
            out var authority,
            out var rejectionReason));

        Assert.Equal("accepted", rejectionReason);
        Assert.NotNull(authority);
        var issuedAuthority = authority!.Value;
        Assert.Equal(repairDecision.AuthorityId, issuedAuthority.AuthorityId);
        Assert.Equal(predecessorAuthorityId, issuedAuthority.PredecessorAuthorityId);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, issuedAuthority.Route);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, issuedAuthority.ProtocolVersion);
        Assert.Equal(FileTransferTransportKind.RegularNkn, issuedAuthority.TargetTransport);
        Assert.Equal(repairDecision.RoutePublicationToken!.Value.PublicationGeneration, issuedAuthority.RoutePublicationGeneration);
        Assert.Equal("frng-test", issuedAuthority.RangeFingerprint);
        Assert.Equal("frq-test", issuedAuthority.RequestCorrelationId);
        Assert.True(FileTransferCoordinator.IsRepairReplayDecisionCurrent(state, repairDecision, out var currentReason));
        Assert.Equal("accepted", currentReason);
    }

    [Fact]
    public void FallbackFrontierRepairAuthority_RejectsNonFallbackAndIncompleteShapes()
    {
        var (_, repairDecision) = AuthorizeFallbackFrontierRepair();
        var validRequest = new FallbackFrontierRepairAuthorityRequest(
            "fallback-frontier:12:1:frng-test",
            "frng-test",
            FrontierChunkIndex: 12,
            DurableReceivedHighestChunkIndex: 11,
            RangeCount: 1,
            RequestedChunkCount: 1,
            FallbackFrontierRepairSourceKind.PeerRequest,
            ProtocolRepairRequestId: "v6-frontier:9:12:1",
            RequestCorrelationId: "frq-test",
            CheckpointRequestId: "(none)",
            Priority: "frontier",
            RecoveryMode: "frontier_repair_only",
            DecisionReason: "fallback_frontier_request_received");

        Assert.False(FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
            repairDecision with { RepairKind = RepairReplayKind.V6Repair },
            validRequest,
            out _,
            out var repairKindReason));
        Assert.Equal("fallback_frontier_repair_route_invariant_mismatch", repairKindReason);

        Assert.False(FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
            repairDecision with { CurrentProtocolVersion = FileTransferProtocol.ProtocolVersionV4 },
            validRequest,
            out _,
            out var protocolReason));
        Assert.Equal("repair_route_publication_authority_mismatch", protocolReason);

        Assert.False(FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
            repairDecision with { CurrentRuntimeGeneration = repairDecision.CurrentRuntimeGeneration + 1 },
            validRequest,
            out _,
            out var runtimeGenerationReason));
        Assert.Equal("repair_route_publication_authority_mismatch", runtimeGenerationReason);

        Assert.False(FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
            repairDecision with { CurrentTerminalGeneration = repairDecision.CurrentTerminalGeneration + 1 },
            validRequest,
            out _,
            out var terminalGenerationReason));
        Assert.Equal("repair_route_publication_authority_mismatch", terminalGenerationReason);

        var publication = repairDecision.RoutePublicationToken!.Value;
        Assert.False(FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
            repairDecision with
            {
                RoutePublicationToken = publication with { TargetTransport = FileTransferTransportKind.Tuna },
            },
            validRequest,
            out _,
            out var targetReason));
        Assert.Equal("fallback_frontier_repair_route_invariant_mismatch", targetReason);

        Assert.False(FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
            repairDecision with { RoutePublicationToken = null },
            validRequest,
            out _,
            out var publicationReason));
        Assert.Equal("route_publication_authority_missing", publicationReason);

        Assert.False(FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
            repairDecision,
            validRequest with { RequestCorrelationId = "" },
            out _,
            out var correlationReason));
        Assert.Equal("fallback_frontier_repair_identity_missing", correlationReason);

        Assert.False(FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
            repairDecision,
            validRequest with { CanonicalRepairKey = "fallback-frontier:12:2:frng-test" },
            out _,
            out var canonicalKeyReason));
        Assert.Equal("fallback_frontier_repair_identity_mismatch", canonicalKeyReason);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_GrantsExactRegularV4ReleaseAndIsIdempotent()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();

        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());

        Assert.True(granted.Granted);
        Assert.False(granted.State.RuntimeUnlockRegularV4RecoverySettlementAvailable);
        Assert.Equal(FileTransferRoute.FileTunaV4, granted.Contract!.Authority.TargetRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, granted.Contract.Authority.TargetProtocolVersion);
        Assert.Equal(FileTransferTransportKind.Tuna, granted.Contract.Authority.TargetTransport);
        var duplicate = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            granted.State,
            request,
            granted.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(duplicate.Granted);
        Assert.Same(granted.Contract, duplicate.Contract);
        Assert.Equal("idempotent", duplicate.Reason);
        Assert.False(granted.Contract.RequestActivePredecessorSupersession);

        var flippedSupersession = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            granted.State,
            request with { RequestActivePredecessorSupersession = true },
            granted.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, flippedSupersession.Status);
        Assert.Equal("retry_dispatch_attempt_identity_mismatch", flippedSupersession.Reason);
        Assert.Same(granted.State, flippedSupersession.State);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchInstallReceipt_IsFreshMonotonicAndIdempotent()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        var contract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(granted.Contract);
        var preinstallState = granted.State with
        {
            RuntimeUnlockRegularV4RecoverySettlementAvailable =
                state.RuntimeUnlockRegularV4RecoverySettlementAvailable,
        };

        Assert.True(FileTransferCoordinator.TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
            preinstallState,
            granted,
            request,
            contract,
            null,
            preinstallState.LastRuntimeUnlockRetryDispatchSettlement,
            currentReceipt: null,
            lastInstallGeneration: 0,
            out var firstReceipt,
            out var mismatchField));
        Assert.Equal("none", mismatchField);
        Assert.Equal(contract.Authority, firstReceipt.Authority);
        Assert.Equal(1, firstReceipt.InstallGeneration);
        Assert.Equal(request, firstReceipt.Request);

        Assert.True(FileTransferCoordinator.TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
            granted.State,
            granted,
            request,
            contract,
            null,
            granted.State.LastRuntimeUnlockRetryDispatchSettlement,
            firstReceipt,
            firstReceipt.InstallGeneration,
            out var duplicateReceipt,
            out mismatchField));
        Assert.Equal("none", mismatchField);
        Assert.Equal(firstReceipt, duplicateReceipt);

        Assert.False(FileTransferCoordinator.TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
            preinstallState with { IsTerminal = true },
            granted,
            request,
            contract,
            null,
            preinstallState.LastRuntimeUnlockRetryDispatchSettlement,
            currentReceipt: null,
            lastInstallGeneration: firstReceipt.InstallGeneration,
            out _,
            out mismatchField));
        Assert.Equal("terminal_state", mismatchField);

        Assert.False(FileTransferCoordinator.TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
            preinstallState with { CurrentRuntimeUnlockRetryDispatchAuthority = null },
            granted,
            request,
            contract,
            null,
            preinstallState.LastRuntimeUnlockRetryDispatchSettlement,
            currentReceipt: null,
            lastInstallGeneration: firstReceipt.InstallGeneration,
            out _,
            out mismatchField));
        Assert.Equal("coordinator_current_authority", mismatchField);

        Assert.False(FileTransferCoordinator.TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
            preinstallState with { RuntimeUnlockRegularV4RecoverySettlementAvailable = false },
            granted,
            request,
            contract,
            null,
            preinstallState.LastRuntimeUnlockRetryDispatchSettlement,
            currentReceipt: null,
            lastInstallGeneration: firstReceipt.InstallGeneration,
            out _,
            out mismatchField));
        Assert.Equal("release_currentness", mismatchField);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchInstallReceipt_RequiresExactPredecessorLineage()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var first = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        var predecessor = Assert.IsType<RuntimeUnlockRetryDispatchContract>(first.Contract);
        Assert.True(predecessor.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            "retry_dispatch_not_observed"));
        Assert.True(predecessor.TryCreateSettlementReport(out var predecessorReport));
        var settled = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            first.State,
            predecessor,
            null,
            predecessorReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, settled.Status);
        var successorRequest = request with
        {
            DispatchAttemptGeneration = 2,
            MissingDispatchSuccessorGeneration = 1,
            PredecessorAuthorityId = predecessor.Authority.AuthorityId,
        };
        var successor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            settled.State,
            successorRequest,
            predecessor,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        var successorContract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(successor.Contract);

        Assert.False(FileTransferCoordinator.TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
            successor.State,
            successor,
            successorRequest,
            successorContract,
            null,
            lastSettlement: null,
            currentReceipt: null,
            lastInstallGeneration: 1,
            out _,
            out var mismatchField));
        Assert.Equal("predecessor_settlement", mismatchField);

        Assert.True(FileTransferCoordinator.TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
            successor.State,
            successor,
            successorRequest,
            successorContract,
            null,
            predecessorReport,
            currentReceipt: null,
            lastInstallGeneration: 1,
            out var receipt,
            out mismatchField));
        Assert.Equal("none", mismatchField);
        Assert.Equal(2, receipt.InstallGeneration);
        Assert.Equal(successorContract.Authority, receipt.Authority);
        Assert.Equal(successorRequest, receipt.Request);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_RequiresAcceptedRegularV4RecoverySettlementConsumption()
    {
        var (correlatedState, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var report = Assert.IsType<RuntimeUnlockRegularV4RecoverySettlementReport>(
            correlatedState.LastRuntimeUnlockRegularV4RecoverySettlement);
        var rawSettlementState = correlatedState with
        {
            LastRuntimeUnlockRegularV4RecoverySettlement = null,
            RuntimeUnlockRegularV4RecoverySettlementAvailable = false,
        };

        var held = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            rawSettlementState,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Held, held.Status);
        Assert.Equal("regular_v4_recovery_settlement_consumption_pending", held.Reason);

        var accepted = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            rawSettlementState,
            null,
            report);
        Assert.Equal(
            RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Accepted,
            accepted.Status);
        Assert.Equal(report, accepted.State.LastRuntimeUnlockRegularV4RecoverySettlement);

        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            accepted.State,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(granted.Granted);
        Assert.Equal(0, request.RegularV4LivenessGeneration);
        Assert.False(granted.State.RuntimeUnlockRegularV4RecoverySettlementAvailable);
        Assert.Equal(report.RegularV4LivenessGeneration, request.RegularV4LivenessGeneration);
        Assert.Equal(report.BridgeDecisionGeneration, request.BridgeDecisionGeneration);

        var duplicate = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            granted.State,
            report,
            report);
        Assert.Equal(
            RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Idempotent,
            duplicate.Status);
        Assert.Equal(report, duplicate.State.LastRuntimeUnlockRegularV4RecoverySettlement);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_RejectsStaleOrNonConsumedRegularV4RecoverySettlement()
    {
        var (correlatedState, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var report = Assert.IsType<RuntimeUnlockRegularV4RecoverySettlementReport>(
            correlatedState.LastRuntimeUnlockRegularV4RecoverySettlement);
        var rawSettlementState = correlatedState with
        {
            LastRuntimeUnlockRegularV4RecoverySettlement = null,
            RuntimeUnlockRegularV4RecoverySettlementAvailable = false,
        };

        var staleTuple = report with
        {
            RegularV4LivenessGeneration = report.RegularV4LivenessGeneration + 1,
        };
        var staleAccepted = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            rawSettlementState,
            null,
            staleTuple);
        Assert.Equal(
            RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
            staleAccepted.Status);
        Assert.Equal(
            "regular_v4_recovery_settlement_generation_tuple_incomplete",
            staleAccepted.Reason);
        var stale = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            staleAccepted.State,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Held, stale.Status);
        Assert.Equal("regular_v4_recovery_settlement_consumption_pending", stale.Reason);

        var rejectedReport = report with
        {
            Disposition = RuntimeUnlockRegularV4RecoverySettlementDisposition.Rejected,
            Reason = "regular_v4_recovery_settlement_rejected",
        };
        var rejectedAccepted = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            rawSettlementState,
            null,
            rejectedReport);
        Assert.Equal(
            RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
            rejectedAccepted.Status);
        Assert.Equal(
            "regular_v4_recovery_settlement_disposition_diagnostic_only",
            rejectedAccepted.Reason);
        var rejected = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            rejectedAccepted.State,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Held, rejected.Status);
        Assert.Equal("regular_v4_recovery_settlement_consumption_pending", rejected.Reason);
    }

    [Fact]
    public void RuntimeUnlockRegularV4RecoverySettlement_LateTerminalAndMismatchedReportsAreDiagnosticOnly()
    {
        var (correlatedState, _, _) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var report = Assert.IsType<RuntimeUnlockRegularV4RecoverySettlementReport>(
            correlatedState.LastRuntimeUnlockRegularV4RecoverySettlement);
        var rawSettlementState = correlatedState with
        {
            LastRuntimeUnlockRegularV4RecoverySettlement = null,
            RuntimeUnlockRegularV4RecoverySettlementAvailable = false,
        };

        var mismatched = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            rawSettlementState,
            null,
            report with
            {
                Settlement = report.Settlement with
                {
                    AuthorityId = new FileTransferAuthorityId("fta2-stale-regular-v4-repair"),
                },
            });
        Assert.Equal(
            RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
            mismatched.Status);
        Assert.Null(mismatched.State.LastRuntimeUnlockRegularV4RecoverySettlement);

        var terminal = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            rawSettlementState with { IsTerminal = true },
            null,
            report);
        Assert.Equal(
            RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
            terminal.Status);
        Assert.Equal("terminal_state", terminal.Reason);
        Assert.Null(terminal.State.LastRuntimeUnlockRegularV4RecoverySettlement);
    }

    [Fact]
    public void RuntimeUnlockRegularV4RecoverySettlement_NewRecoveryLeaseInvalidatesPriorConsumption()
    {
        var (state, _, _) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        Assert.NotNull(state.LastRuntimeUnlockRegularV4RecoverySettlement);
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;

        var nextRecovery = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin,
                owner,
                leg));

        Assert.True(nextRecovery.Accepted);
        Assert.Null(nextRecovery.State.LastRuntimeUnlockRegularV4RecoverySettlement);
        Assert.False(nextRecovery.State.RuntimeUnlockRegularV4RecoverySettlementAvailable);
    }

    [Fact]
    public void RuntimeUnlockRegularV4RecoverySettlement_RequiresExactIssuedSettlementAndFinalDisposition()
    {
        var (correlatedState, _, _) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var report = Assert.IsType<RuntimeUnlockRegularV4RecoverySettlementReport>(
            correlatedState.LastRuntimeUnlockRegularV4RecoverySettlement);
        var rawState = correlatedState with
        {
            LastRuntimeUnlockRegularV4RecoverySettlement = null,
            RuntimeUnlockRegularV4RecoverySettlementAvailable = false,
        };
        var reconstructed = report with
        {
            Settlement = report.Settlement with { },
        };

        var missing = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            rawState,
            null,
            default);
        Assert.Equal(RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected, missing.Status);
        Assert.Equal("regular_v4_recovery_settlement_report_missing", missing.Reason);
        Assert.Same(rawState, missing.State);

        var reconstructedDecision = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            rawState,
            null,
            reconstructed);

        Assert.Equal(
            RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
            reconstructedDecision.Status);
        Assert.Equal(
            "regular_v4_recovery_settlement_not_coordinator_issued",
            reconstructedDecision.Reason);

        var accepted = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            rawState,
            null,
            report);
        Assert.Equal(RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Accepted, accepted.Status);

        var conflicting = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            accepted.State,
            report,
            report with { Reason = "different_final_reason" });
        Assert.Equal(
            RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
            conflicting.Status);
        Assert.Equal(
            "regular_v4_recovery_settlement_final_disposition_conflict",
            conflicting.Reason);
        Assert.Equal(report, conflicting.State.LastRuntimeUnlockRegularV4RecoverySettlement);
        Assert.True(conflicting.State.RuntimeUnlockRegularV4RecoverySettlementAvailable);
    }

    [Fact]
    public void RuntimeUnlockRegularV4RecoverySettlement_NewRecoverySupersedesPriorSettlement()
    {
        var (correlatedState, _, _) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var report = Assert.IsType<RuntimeUnlockRegularV4RecoverySettlementReport>(
            correlatedState.LastRuntimeUnlockRegularV4RecoverySettlement);
        var rawState = correlatedState with
        {
            LastRuntimeUnlockRegularV4RecoverySettlement = null,
            RuntimeUnlockRegularV4RecoverySettlementAvailable = false,
        };
        var owner = rawState.CurrentRuntimeOwner!.Value;
        var leg = rawState.CurrentLeg!;
        var recovery = FileTransferCoordinator.AuthorizeAction(
            rawState,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                owner,
                leg,
                bridgeRecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery));
        Assert.True(recovery.Accepted);
        Assert.NotNull(recovery.BridgeRecoveryDecision);
        Assert.NotNull(recovery.RecoveryLivenessLease);
        Assert.Null(recovery.State.LastCoordinatorIssuedRegularV4RecoverySettlement);
        Assert.Null(recovery.State.LastRuntimeUnlockRegularV4RecoverySettlement);
        Assert.False(recovery.State.RuntimeUnlockRegularV4RecoverySettlementAvailable);
        var lease = recovery.State.CurrentRecoveryLivenessLease!.Value;
        var infraLivenessGeneration = lease.LeaseGeneration;
        var activeReport = report with
        {
            RegularV4LivenessGeneration = infraLivenessGeneration,
            BridgeDecisionGeneration = recovery.BridgeRecoveryDecision!.Value.BridgeDecisionGeneration,
        };

        var zeroWhileActive = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            recovery.State,
            null,
            report);
        Assert.Equal(RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected, zeroWhileActive.Status);
        Assert.Equal(
            "regular_v4_recovery_settlement_not_coordinator_issued",
            zeroWhileActive.Reason);

        var beforeLifecycle = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            recovery.State,
            null,
            activeReport);
        Assert.Equal(RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected, beforeLifecycle.Status);
        Assert.Equal("regular_v4_recovery_settlement_not_coordinator_issued", beforeLifecycle.Reason);

        var lifecycleReport = CreateRegularV4RecoveryLifecycleReport(
            recovery.State,
            RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed);
        var lifecycle = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoveryLifecycle(
            recovery.State,
            null,
            lifecycleReport);
        Assert.Equal(RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted, lifecycle.Status);
        Assert.Null(lifecycle.State.CurrentRecoveryLivenessLease);
        Assert.Equal(lease.AuthorityId, lifecycle.State.LastRetiredRecoveryLivenessLease?.AuthorityId);
        Assert.True(lifecycle.State.RuntimeUnlockRecoveryLeaseClosureAvailable);

        var stale = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            lifecycle.State,
            null,
            activeReport with { RegularV4LivenessGeneration = infraLivenessGeneration + 1 });
        Assert.Equal(RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected, stale.Status);
        Assert.Equal("regular_v4_recovery_settlement_not_coordinator_issued", stale.Reason);

        var superseded = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            lifecycle.State,
            null,
            activeReport);
        Assert.Equal(RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected, superseded.Status);
        Assert.Equal("regular_v4_recovery_settlement_not_coordinator_issued", superseded.Reason);
        Assert.False(superseded.State.RuntimeUnlockRegularV4RecoverySettlementAvailable);
    }

    [Fact]
    public void RuntimeUnlockRegularV4RecoveryLifecycle_RetiresLeaseAndRejectsLateOrMismatchedReports()
    {
        var state = CreateActiveRegularV4RecoveryState();
        var lease = Assert.IsType<RecoveryLivenessLease>(state.CurrentRecoveryLivenessLease);
        var report = CreateRegularV4RecoveryLifecycleReport(
            state,
            RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Exhausted);

        var accepted = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoveryLifecycle(
            state,
            null,
            report);
        Assert.Equal(RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted, accepted.Status);
        Assert.Null(accepted.State.CurrentRecoveryLivenessLease);
        Assert.Equal(lease.AuthorityId, accepted.State.LastRetiredRecoveryLivenessLease?.AuthorityId);
        Assert.Equal(RecoveryLivenessLeaseState.Retired, accepted.State.LastRetiredRecoveryLivenessLease?.State);
        Assert.Equal(report, accepted.State.LastRuntimeUnlockRegularV4RecoveryLifecycle);
        Assert.False(accepted.State.RuntimeUnlockRecoveryLeaseClosureAvailable);

        var duplicate = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoveryLifecycle(
            accepted.State,
            report,
            report);
        Assert.Equal(RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Idempotent, duplicate.Status);

        var conflicting = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoveryLifecycle(
            accepted.State,
            report,
            report with { Outcome = RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed });
        Assert.Equal(RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Rejected, conflicting.Status);
        Assert.Equal(report, conflicting.State.LastRuntimeUnlockRegularV4RecoveryLifecycle);

        var staleState = CreateActiveRegularV4RecoveryState();
        var stale = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoveryLifecycle(
            staleState,
            null,
            report with
            {
                RecoveryAuthority = report.RecoveryAuthority with
                {
                    BridgeRecoveryAuthority = report.Authority with
                    {
                        BridgeDecisionGeneration = report.Authority.BridgeDecisionGeneration + 1,
                    },
                },
            });
        Assert.Equal(RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Rejected, stale.Status);
        Assert.NotNull(stale.State.CurrentRecoveryLivenessLease);
        Assert.Null(stale.State.LastRuntimeUnlockRegularV4RecoveryLifecycle);
    }

    [Fact]
    public void BridgeReceiveStallRecovery_AtomicallyIssuesBridgeDecisionAndRecoveryLease()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = StartRegular(regular).State;
        var owner = Assert.IsType<RuntimeOwnerToken>(state.CurrentRuntimeOwner);
        var leg = Assert.IsType<FileTransferLeg>(state.CurrentLeg);

        var recovery = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                owner,
                leg,
                bridgeRecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery,
                reason: " Receive Stall "));

        Assert.True(recovery.Accepted);
        var bridge = Assert.IsType<BridgeRecoveryDecision>(recovery.BridgeRecoveryDecision);
        var lease = Assert.IsType<RecoveryLivenessLease>(recovery.RecoveryLivenessLease);
        Assert.Equal(bridge, recovery.State.CurrentBridgeRecoveryDecision);
        Assert.Equal(lease, recovery.State.CurrentRecoveryLivenessLease);
        Assert.Equal(RecoveryLivenessLeaseState.Active, lease.State);
        Assert.Equal("Receive Stall", bridge.Reason);
        Assert.Equal("Receive Stall", lease.Reason);
        Assert.Equal(bridge.RuntimeOwner, lease.RuntimeOwner);
        Assert.Equal(bridge.CurrentRoute, lease.Route);
        Assert.Equal(bridge.CurrentProtocolVersion, lease.ProtocolVersion);
        Assert.Equal(bridge.CurrentLiveRouteEpoch, lease.LiveRouteEpoch);
        Assert.Equal(bridge.CurrentRuntimeGeneration, lease.RuntimeGeneration);
        Assert.Equal(bridge.CurrentTerminalGeneration, lease.TerminalGeneration);
        Assert.Equal(bridge.CurrentLegGeneration, lease.LegGeneration);
        Assert.Equal(bridge.TransportEpoch, lease.TransportEpoch);
        Assert.Equal(bridge.BridgeRecoveryGeneration, lease.BridgeRecoveryGeneration);

        var composite = CreateRegularV4RecoveryLifecycleReport(
            recovery.State,
            RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed).RecoveryAuthority;
        Assert.False(composite.IsDefault);
        Assert.True((composite with
        {
            BridgeRecoveryAuthority = composite.BridgeRecoveryAuthority with
            {
                RecoveryKind = "ReceiveRecoveryRequest",
            },
        }).IsDefault);
    }

    [Fact]
    public void BridgeRecoveryExecution_ExactStartAndReceiptAreOneShot()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = StartRegular(regular).State;
        var owner = Assert.IsType<RuntimeOwnerToken>(state.CurrentRuntimeOwner);
        var leg = Assert.IsType<FileTransferLeg>(state.CurrentLeg);
        var recovery = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                owner,
                leg,
                bridgeRecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery,
                reason: "bridge execution"));
        var command = Assert.IsType<FileTransferBridgeRecoveryExecutionCommand>(
            recovery.BridgeRecoveryExecutionCommand);
        Assert.Equal(recovery.BridgeRecoveryDecision, command.Decision);
        Assert.Equal(recovery.RecoveryLivenessLease, command.RecoveryLivenessLease);
        Assert.Equal(leg.CheckpointRequestId, command.CheckpointRequestId);
        Assert.Equal(command, recovery.State.CurrentBridgeRecoveryExecutionCommand);
        Assert.Equal(
            FileTransferBridgeRecoveryExecutionState.Issued,
            recovery.State.CurrentBridgeRecoveryExecutionState);

        var pendingReceipt = new FileTransferBridgeRecoveryExecutionReceipt(
            command,
            FileTransferBridgeRecoveryExecutionOutcome.Completed,
            "hard_restart",
            "bridge_recovery_completed");
        var completion = Task.FromResult(pendingReceipt);
        var started = FileTransferCoordinator.ObserveBridgeRecoveryExecutionStart(
            recovery.State,
            new FileTransferBridgeRecoveryExecutionStartResult(
                command,
                FileTransferBridgeRecoveryExecutionStartStatus.Started,
                "bridge_recovery_execution_started",
                completion));
        Assert.Equal(
            FileTransferBridgeRecoveryExecutionDecisionStatus.Accepted,
            started.Status);
        Assert.Equal(
            FileTransferBridgeRecoveryExecutionState.Running,
            started.State.CurrentBridgeRecoveryExecutionState);

        var duplicateStart = FileTransferCoordinator.ObserveBridgeRecoveryExecutionStart(
            started.State,
            new FileTransferBridgeRecoveryExecutionStartResult(
                command,
                FileTransferBridgeRecoveryExecutionStartStatus.Idempotent,
                "bridge_recovery_execution_already_started",
                completion));
        Assert.Equal(
            FileTransferBridgeRecoveryExecutionDecisionStatus.Idempotent,
            duplicateStart.Status);
        Assert.Equal(started.State, duplicateStart.State);

        var completed = FileTransferCoordinator.ObserveBridgeRecoveryExecutionReceipt(
            duplicateStart.State,
            pendingReceipt);
        Assert.Equal(
            FileTransferBridgeRecoveryExecutionDecisionStatus.Accepted,
            completed.Status);
        Assert.Null(completed.State.CurrentBridgeRecoveryExecutionCommand);
        Assert.Null(completed.State.CurrentBridgeRecoveryExecutionState);
        Assert.Equal(pendingReceipt, completed.State.LastBridgeRecoveryExecutionReceipt);

        var duplicateReceipt = FileTransferCoordinator.ObserveBridgeRecoveryExecutionReceipt(
            completed.State,
            pendingReceipt);
        Assert.Equal(
            FileTransferBridgeRecoveryExecutionDecisionStatus.Idempotent,
            duplicateReceipt.Status);
        Assert.Equal(completed.State, duplicateReceipt.State);
    }

    [Fact]
    public void BridgeRecoveryExecution_MutatedOrTerminalCommandCannotStartOrComplete()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = StartRegular(regular).State;
        var owner = Assert.IsType<RuntimeOwnerToken>(state.CurrentRuntimeOwner);
        var leg = Assert.IsType<FileTransferLeg>(state.CurrentLeg);
        var recovery = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                owner,
                leg,
                bridgeRecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery));
        var command = Assert.IsType<FileTransferBridgeRecoveryExecutionCommand>(
            recovery.BridgeRecoveryExecutionCommand);
        var mutated = command with
        {
            Decision = command.Decision with
            {
                BridgeDecisionGeneration =
                    command.Decision.BridgeDecisionGeneration + 1,
            },
        };
        var mutatedReceipt = new FileTransferBridgeRecoveryExecutionReceipt(
            mutated,
            FileTransferBridgeRecoveryExecutionOutcome.Completed,
            "hard_restart",
            "bridge_recovery_completed");

        var rejectedStart = FileTransferCoordinator.ObserveBridgeRecoveryExecutionStart(
            recovery.State,
            new FileTransferBridgeRecoveryExecutionStartResult(
                mutated,
                FileTransferBridgeRecoveryExecutionStartStatus.Started,
                "bridge_recovery_execution_started",
                Task.FromResult(mutatedReceipt)));
        Assert.Equal(
            FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
            rejectedStart.Status);
        Assert.Equal(command, rejectedStart.State.CurrentBridgeRecoveryExecutionCommand);

        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "completed"),
            recovery.State);
        Assert.Null(terminal.State.CurrentBridgeRecoveryExecutionCommand);
        Assert.Null(terminal.State.CurrentBridgeRecoveryExecutionState);

        var lateStart = FileTransferCoordinator.ObserveBridgeRecoveryExecutionStart(
            terminal.State,
            new FileTransferBridgeRecoveryExecutionStartResult(
                command,
                FileTransferBridgeRecoveryExecutionStartStatus.Started,
                "bridge_recovery_execution_started",
                Task.FromResult(mutatedReceipt with { Command = command })));
        Assert.Equal(
            FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
            lateStart.Status);
        var lateReceipt = FileTransferCoordinator.ObserveBridgeRecoveryExecutionReceipt(
            terminal.State,
            mutatedReceipt with { Command = command });
        Assert.Equal(
            FileTransferBridgeRecoveryExecutionDecisionStatus.Rejected,
            lateReceipt.Status);
        Assert.Null(lateReceipt.State.LastBridgeRecoveryExecutionReceipt);
    }

    [Fact]
    public void BridgeReceiveStallRecovery_RejectsWithoutPartialAuthority()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var active = StartRegular(regular).State;
        var owner = Assert.IsType<RuntimeOwnerToken>(active.CurrentRuntimeOwner);
        var state = active with { CurrentRuntimeOwner = null };

        var recovery = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                owner,
                Assert.IsType<FileTransferLeg>(state.CurrentLeg),
                bridgeRecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery));

        Assert.False(recovery.Accepted);
        Assert.Null(recovery.BridgeRecoveryDecision);
        Assert.Null(recovery.RecoveryLivenessLease);
        Assert.Null(recovery.State.CurrentBridgeRecoveryDecision);
        Assert.Null(recovery.State.CurrentRecoveryLivenessLease);
    }

    [Fact]
    public void RuntimeUnlockRegularV4RecoveryLifecycle_RequiresLeaseEvenWhenNoLeaseWasEverIssued()
    {
        var active = CreateActiveRegularV4RecoveryState();
        var report = CreateRegularV4RecoveryLifecycleReport(
            active,
            RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed);
        var missing = active with
        {
            CurrentRecoveryLivenessLease = null,
            LastRecoveryLivenessLeaseGeneration = 0,
        };

        var decision = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoveryLifecycle(
            missing,
            null,
            report);

        Assert.Equal(RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Rejected, decision.Status);
        Assert.Equal("regular_v4_recovery_lifecycle_lease_missing", decision.Reason);
        Assert.Null(decision.State.LastRuntimeUnlockRegularV4RecoveryLifecycle);
        Assert.False(decision.State.RuntimeUnlockRecoveryLeaseClosureAvailable);
    }

    [Fact]
    public void RuntimeUnlockRegularV4RecoveryLifecycle_RejectsArbitraryLivenessGeneration()
    {
        var state = CreateActiveRegularV4RecoveryState();
        var report = CreateRegularV4RecoveryLifecycleReport(
            state,
            RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed);
        var suppliedLease = report.RecoveryAuthority.RecoveryLivenessLease;
        var staleReport = report with
        {
            RecoveryAuthority = report.RecoveryAuthority with
            {
                RecoveryLivenessLease = suppliedLease with
                {
                    LeaseGeneration = suppliedLease.LeaseGeneration + 1,
                },
            },
        };

        var decision = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoveryLifecycle(
            state,
            null,
            staleReport);

        Assert.Equal(RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Rejected, decision.Status);
        Assert.Equal("regular_v4_recovery_lifecycle_authority_stale", decision.Reason);
        Assert.Equal(suppliedLease, decision.State.CurrentRecoveryLivenessLease);
        Assert.False(decision.State.RuntimeUnlockRecoveryLeaseClosureAvailable);
    }

    [Fact]
    public void RecoveryLivenessDeferral_ReusesAtomicLeaseAndCannotReconstructItAfterCompletion()
    {
        var state = CreateActiveRegularV4RecoveryState();
        var owner = Assert.IsType<RuntimeOwnerToken>(state.CurrentRuntimeOwner);
        var leg = Assert.IsType<FileTransferLeg>(state.CurrentLeg);
        var lease = Assert.IsType<RecoveryLivenessLease>(state.CurrentRecoveryLivenessLease);

        var reused = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.LivenessDeferral,
                owner,
                leg,
                recoveryLivenessLease: lease,
                recoveryDecisionGeneration: lease.LeaseGeneration));
        Assert.True(reused.Accepted);
        Assert.Equal(lease, reused.RecoveryLivenessLease);
        Assert.Equal(lease, reused.State.CurrentRecoveryLivenessLease);
        Assert.Equal(state.LastRecoveryLivenessLeaseGeneration, reused.State.LastRecoveryLivenessLeaseGeneration);

        var report = CreateRegularV4RecoveryLifecycleReport(
            reused.State,
            RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed);
        var completed = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoveryLifecycle(
            reused.State,
            null,
            report);
        Assert.Equal(RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted, completed.Status);

        var late = FileTransferCoordinator.AuthorizeAction(
            completed.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.LivenessDeferral,
                owner,
                leg,
                recoveryLivenessLease: lease,
                recoveryDecisionGeneration: lease.LeaseGeneration));
        Assert.False(late.Accepted);
        Assert.Null(late.State.CurrentRecoveryLivenessLease);
        Assert.True(late.State.RuntimeUnlockRecoveryLeaseClosureAvailable);

        var reconstructed = FileTransferCoordinator.AuthorizeAction(
            completed.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin,
                owner,
                leg));
        Assert.False(reconstructed.Accepted);
        Assert.Equal(
            "regular_v4_recovery_liveness_lease_must_be_issued_atomically",
            reconstructed.RejectionReason);
        Assert.Null(reconstructed.State.CurrentRecoveryLivenessLease);
        Assert.True(reconstructed.State.RuntimeUnlockRecoveryLeaseClosureAvailable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void RecoveryLeaseClosureLivenessDeferral_IsBoundedAndStatePreserving(
        int outcomeValue)
    {
        var outcome = (RuntimeUnlockRegularV4RecoveryLifecycleOutcome)outcomeValue;
        var state = CreateActiveRegularV4RecoveryState();
        var owner = Assert.IsType<RuntimeOwnerToken>(state.CurrentRuntimeOwner);
        var leg = Assert.IsType<FileTransferLeg>(state.CurrentLeg);
        var report = CreateRegularV4RecoveryLifecycleReport(state, outcome);
        var lifecycle = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoveryLifecycle(
            state,
            null,
            report);
        Assert.Equal(RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted, lifecycle.Status);
        var retiredLease = Assert.IsType<RecoveryLivenessLease>(
            lifecycle.State.LastRetiredRecoveryLivenessLease);
        var maximumDeadline = DateTimeOffset.UtcNow.AddSeconds(90);
        var proof = new RuntimeUnlockRegularV4RecoveryLeaseClosureContinuationProof(
            report,
            retiredLease,
            lifecycle.Status,
            lifecycle.Reason,
            maximumDeadline);

        var granted = FileTransferCoordinator.AuthorizeAction(
            lifecycle.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLeaseClosureLivenessDeferral,
                owner,
                leg,
                closureContinuationProof: proof,
                livenessDeferralDeadlineUtc: maximumDeadline));

        Assert.True(granted.Accepted);
        Assert.Equal(retiredLease, granted.RecoveryLivenessLease);
        Assert.Same(lifecycle.State, granted.State);
        Assert.Null(granted.State.CurrentRecoveryLivenessLease);
        Assert.Equal(retiredLease, granted.State.LastRetiredRecoveryLivenessLease);
        Assert.Equal(report, granted.State.LastRuntimeUnlockRegularV4RecoveryLifecycle);
        Assert.True(granted.State.RuntimeUnlockRecoveryLeaseClosureAvailable);
        Assert.Equal(
            lifecycle.State.LastRecoveryLivenessLeaseGeneration,
            granted.State.LastRecoveryLivenessLeaseGeneration);

        var duplicate = FileTransferCoordinator.AuthorizeAction(
            granted.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLeaseClosureLivenessDeferral,
                owner,
                leg,
                closureContinuationProof: proof,
                livenessDeferralDeadlineUtc: maximumDeadline));
        Assert.True(duplicate.Accepted);
        Assert.Same(granted.State, duplicate.State);
        Assert.Equal(retiredLease, duplicate.RecoveryLivenessLease);

        var excessive = FileTransferCoordinator.AuthorizeAction(
            granted.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLeaseClosureLivenessDeferral,
                owner,
                leg,
                closureContinuationProof: proof,
                livenessDeferralDeadlineUtc: maximumDeadline.AddTicks(1)));
        Assert.False(excessive.Accepted);
        Assert.Equal(
            "regular_v4_recovery_lease_closure_continuation_deadline_exceeded",
            excessive.RejectionReason);
        Assert.Same(granted.State, excessive.State);

        var expiredDeadline = DateTimeOffset.UtcNow.AddSeconds(-1);
        var expiredProof = proof with
        {
            MaximumLivenessDeferralDeadlineUtc = expiredDeadline,
        };
        var expired = FileTransferCoordinator.AuthorizeAction(
            granted.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLeaseClosureLivenessDeferral,
                owner,
                leg,
                closureContinuationProof: expiredProof,
                livenessDeferralDeadlineUtc: expiredDeadline));
        Assert.False(expired.Accepted);
        Assert.Equal(
            "regular_v4_recovery_lease_closure_continuation_deadline_expired",
            expired.RejectionReason);
        Assert.Same(granted.State, expired.State);

        var missingProof = FileTransferCoordinator.AuthorizeAction(
            granted.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLeaseClosureLivenessDeferral,
                owner,
                leg,
                livenessDeferralDeadlineUtc: maximumDeadline));
        Assert.False(missingProof.Accepted);
        Assert.Equal(
            "regular_v4_recovery_lease_closure_continuation_proof_missing",
            missingProof.RejectionReason);
        Assert.Null(missingProof.State.CurrentRecoveryLivenessLease);
        Assert.True(missingProof.State.RuntimeUnlockRecoveryLeaseClosureAvailable);
    }

    [Fact]
    public void NewRegularV4Recovery_SupersedesBothAuthoritiesAndClearsPriorClosure()
    {
        var first = CreateActiveRegularV4RecoveryState();
        var firstBridge = Assert.IsType<BridgeRecoveryDecision>(first.CurrentBridgeRecoveryDecision);
        var firstLease = Assert.IsType<RecoveryLivenessLease>(first.CurrentRecoveryLivenessLease);
        var completed = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoveryLifecycle(
            first,
            null,
            CreateRegularV4RecoveryLifecycleReport(
                first,
                RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed));
        Assert.True(completed.State.RuntimeUnlockRecoveryLeaseClosureAvailable);

        var second = FileTransferCoordinator.AuthorizeAction(
            completed.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                Assert.IsType<RuntimeOwnerToken>(completed.State.CurrentRuntimeOwner),
                Assert.IsType<FileTransferLeg>(completed.State.CurrentLeg),
                bridgeRecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery));

        Assert.True(second.Accepted);
        Assert.True(second.BridgeRecoveryDecision!.Value.BridgeDecisionGeneration > firstBridge.BridgeDecisionGeneration);
        Assert.True(second.RecoveryLivenessLease!.Value.LeaseGeneration > firstLease.LeaseGeneration);
        Assert.Null(second.State.LastRetiredRecoveryLivenessLease);
        Assert.Null(second.State.LastRuntimeUnlockRegularV4RecoveryLifecycle);
        Assert.Null(second.State.LastRuntimeUnlockRegularV4RecoverySettlement);
        Assert.Null(second.State.LastCoordinatorIssuedRegularV4RecoverySettlement);
        Assert.False(second.State.RuntimeUnlockRecoveryLeaseClosureAvailable);
        Assert.False(second.State.RuntimeUnlockRegularV4RecoverySettlementAvailable);
    }

    [Fact]
    public void BridgeDecisionReplacement_InvalidatesExistingRegularV4RecoveryLease()
    {
        var state = CreateActiveRegularV4RecoveryState();
        var oldLease = Assert.IsType<RecoveryLivenessLease>(state.CurrentRecoveryLivenessLease);
        var owner = Assert.IsType<RuntimeOwnerToken>(state.CurrentRuntimeOwner);
        var leg = Assert.IsType<FileTransferLeg>(state.CurrentLeg);

        var pendingReplacement = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeRecovery,
                owner,
                leg,
                bridgeRecoveryKind: BridgeRecoveryKind.ReceiveRecoveryRequest,
                reason: "replacement_bridge_recovery"));
        Assert.False(pendingReplacement.Accepted);
        Assert.Equal(
            "bridge_recovery_execution_pending",
            pendingReplacement.RejectionReason);

        var command = Assert.IsType<FileTransferBridgeRecoveryExecutionCommand>(
            state.CurrentBridgeRecoveryExecutionCommand);
        var discarded = FileTransferCoordinator.ObserveBridgeRecoveryExecutionReceipt(
            state,
            new FileTransferBridgeRecoveryExecutionReceipt(
                command,
                FileTransferBridgeRecoveryExecutionOutcome.Discarded,
                "not_started",
                "replacement_bridge_recovery"));
        Assert.Equal(
            FileTransferBridgeRecoveryExecutionDecisionStatus.Accepted,
            discarded.Status);

        var replacement = FileTransferCoordinator.AuthorizeAction(
            discarded.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeRecovery,
                owner,
                leg,
                bridgeRecoveryKind: BridgeRecoveryKind.ReceiveRecoveryRequest,
                reason: "replacement_bridge_recovery"));

        Assert.True(replacement.Accepted);
        Assert.NotNull(replacement.BridgeRecoveryDecision);
        Assert.Null(replacement.RecoveryLivenessLease);
        Assert.Null(replacement.State.CurrentRecoveryLivenessLease);
        Assert.Null(replacement.State.LastRetiredRecoveryLivenessLease);
        Assert.Null(replacement.State.LastRuntimeUnlockRegularV4RecoveryLifecycle);
        Assert.False(replacement.State.RuntimeUnlockRecoveryLeaseClosureAvailable);

        var staleDeferral = FileTransferCoordinator.AuthorizeAction(
            replacement.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.LivenessDeferral,
                owner,
                leg,
                recoveryLivenessLease: oldLease,
                recoveryDecisionGeneration: oldLease.LeaseGeneration));
        Assert.False(staleDeferral.Accepted);
        Assert.Equal("recovery_decision_generation_mismatch", staleDeferral.RejectionReason);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_ConsumedHandoffIsOneShotAndSuccessorTupleIsStable()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(granted.Granted);

        var mismatchedSuccessor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            granted.State,
            request with
            {
                DispatchAttemptGeneration = 2,
                PredecessorAuthorityId = granted.Contract!.Authority.AuthorityId,
                RequestActivePredecessorSupersession = true,
                BridgeDecisionGeneration = request.BridgeDecisionGeneration + 1,
            },
            granted.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, mismatchedSuccessor.Status);
        Assert.Equal("retry_dispatch_recovery_handoff_tuple_mismatch", mismatchedSuccessor.Reason);

        Assert.True(granted.Contract!.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.AuthorityRejected,
            "authority_rejected"));
        Assert.True(granted.Contract.TryCreateSettlementReport(out var settlementReport));
        var retired = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            granted.State,
            granted.Contract,
            null,
            settlementReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, retired.Status);
        Assert.Null(retired.State.CurrentRuntimeUnlockRetryDispatchAuthority);

        var reused = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            retired.State,
            request with { RecoveryContractGeneration = request.RecoveryContractGeneration + 1 },
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, reused.Status);
        Assert.Equal("regular_v4_recovery_settlement_consumption_already_used", reused.Reason);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_HeldRequestAcceptsCurrentCoordinatorBridgeGeneration()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        Assert.Equal(0, request.BridgeDecisionGeneration);
        var bridge = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeRecovery,
                state.CurrentRuntimeOwner!.Value,
                state.CurrentLeg!,
                bridgeRecoveryKind: BridgeRecoveryKind.ReceiveRecoveryRequest));
        Assert.True(bridge.Accepted);
        Assert.Equal(1, bridge.State.CurrentBridgeRecoveryDecision!.Value.BridgeDecisionGeneration);

        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            bridge.State,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());

        Assert.True(granted.Granted);
        Assert.Equal(request, granted.Request);
        Assert.Equal(1, granted.Contract!.Authority.BridgeDecisionGeneration);
        Assert.True(FileTransferCoordinator.TryValidateRuntimeUnlockRetryDispatchGrantedDecision(
            granted,
            request,
            out var mismatchField), mismatchField);

        var tamperedContract = new RuntimeUnlockRetryDispatchContract(
            granted.Contract.Authority,
            requestActivePredecessorSupersession: true);
        var tamperedDecision = granted with { Contract = tamperedContract };
        Assert.False(FileTransferCoordinator.TryValidateRuntimeUnlockRetryDispatchGrantedDecision(
            tamperedDecision,
            request,
            out mismatchField));
        Assert.Equal("contract_request_active_predecessor_supersession", mismatchField);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_IdempotentRecheckPreservesOriginalReleaseTuple()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(granted.Granted);
        var originalAuthority = granted.Contract!.Authority;
        var successorRelease = FileTransferCoordinator.AuthorizeAction(
            granted.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                granted.State.CurrentRuntimeOwner!.Value,
                granted.State.CurrentLeg!,
                repairReplayKind: RepairReplayKind.V4Repair));
        Assert.True(successorRelease.Accepted);
        var laterRelease = successorRelease.RepairReplayDecision!.Value;

        var duplicate = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            successorRelease.State,
            request,
            granted.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            laterRelease.AuthorityId,
            laterRelease.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());

        Assert.True(duplicate.Granted);
        Assert.Same(granted.Contract, duplicate.Contract);
        Assert.Equal(originalAuthority.ReleaseAuthorityId, duplicate.Contract!.Authority.ReleaseAuthorityId);
        Assert.Equal(originalAuthority.ReleaseGeneration, duplicate.Contract.Authority.ReleaseGeneration);
        Assert.Equal(originalAuthority.ReleaseOutcome, duplicate.Contract.Authority.ReleaseOutcome);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_FallbackPairRequiresExactAuthorityEvidence()
    {
        var (state, request, tailYield) =
            AuthorizeFallbackRuntimeUnlockRetryDispatchSource();
        Assert.True(
            state.RuntimeUnlockFallbackSurvivalState
                .PairedFallbackYieldGranted);

        Assert.False(
            FileTransferCoordinator
                .TryCreateFallbackPairedTailYieldRuntimeUnlockRetryDispatchRequest(
                    state,
                    tailYield,
                    default,
                    "runtime_unlock_offer_send_not_observed",
                    "post_tuna_fallback_tail_unblocked",
                    out _,
                    out var missingEvidenceReason));
        Assert.Equal(
            "fallback_paired_tail_yield_pending",
            missingEvidenceReason);

        var evidence = Assert.IsType<FallbackPairedTailYieldReleaseEvidence>(
            request.FallbackPairedTailYieldEvidence);
        var survivalProjectionWithoutPair =
            state.RuntimeUnlockFallbackSurvivalState with
            {
                PairedFallbackYieldGranted = false,
            };
        Assert.True(
            FileTransferCoordinator
                .TryCreateFallbackPairedTailYieldRuntimeUnlockRetryDispatchRequest(
                    state with
                    {
                        RuntimeUnlockFallbackSurvivalState =
                            survivalProjectionWithoutPair,
                    },
                    tailYield,
                    evidence,
                    "runtime_unlock_offer_send_not_observed",
                    "post_tuna_fallback_tail_unblocked",
                    out _,
                    out var exactEvidenceReason),
            exactEvidenceReason);

        var mutatedRequest = request with
        {
            FallbackPairedTailYieldEvidence = evidence with
            {
                PeerPairKey = $"{evidence.PeerPairKey}|mutated",
            },
        };
        var rejected =
            FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
                state,
                mutatedRequest,
                currentContract: null,
                RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield,
                tailYield.AuthorityId,
                tailYield.YieldGeneration,
                tailYield.YieldKind.ToString());
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
            rejected.Status);
        Assert.Equal(
            "retry_dispatch_predecessor_release_invalid",
            rejected.Reason);
        Assert.Null(rejected.Contract);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_ExactHeldFallbackRequestGrantsAfterTailReleaseSettles()
    {
        var (state, request, tailYield) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource();

        var held = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.Unknown,
            null,
            0,
            "pending");

        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Held, held.Status);
        Assert.Equal("fallback_paired_tail_yield_pending", held.Reason);
        Assert.Equal(request, held.Request);
        Assert.Null(held.Contract);
        Assert.Null(held.State.CurrentRuntimeUnlockRetryDispatchAuthority);

        var stillHeld = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            held.State,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.Unknown,
            null,
            0,
            "pending");

        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Held, stillHeld.Status);
        Assert.Equal(request, stillHeld.Request);
        Assert.Null(stillHeld.State.CurrentRuntimeUnlockRetryDispatchAuthority);

        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            stillHeld.State,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield,
            tailYield.AuthorityId,
            tailYield.YieldGeneration,
            tailYield.YieldKind.ToString());

        Assert.True(granted.Granted);
        Assert.Equal(request, granted.Request);
        Assert.Equal(
            RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield,
            granted.Contract!.Authority.ReleaseKind);
        Assert.Equal(tailYield.AuthorityId, granted.Contract.Authority.ReleaseAuthorityId);
        Assert.Equal(tailYield.YieldGeneration, granted.Contract.Authority.ReleaseGeneration);
        Assert.Equal(tailYield.YieldKind.ToString(), granted.Contract.Authority.ReleaseOutcome);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_FallbackMissingDispatchSuccessorPreservesCanonicalReleaseAcrossTransportEpoch()
    {
        var (state, request, tailYield) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource();
        var first = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield,
            tailYield.AuthorityId,
            tailYield.YieldGeneration,
            tailYield.YieldKind.ToString());
        var predecessor = Assert.IsType<RuntimeUnlockRetryDispatchContract>(first.Contract);
        Assert.True(predecessor.TryBindOfferAlias(2, "fallback-missing-dispatch"));
        Assert.True(predecessor.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            "fallback_dispatch_not_observed"));
        Assert.True(predecessor.TryCreateSettlementReport(out var predecessorReport));
        var settlement = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            first.State,
            predecessor,
            null,
            predecessorReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, settlement.Status);

        var sourceTransportEpoch = settlement.State.CurrentLeg!.TransportEpochId;
        settlement.State.CurrentLeg.TransportEpochId = sourceTransportEpoch + 1;
        var successorRequest = request with
        {
            RetiredOfferGeneration = 2,
            DispatchAttemptGeneration = 2,
            MissingDispatchSuccessorGeneration = 1,
            PredecessorAuthorityId = predecessor.Authority.AuthorityId,
            RequestActivePredecessorSupersession = false,
        };
        var successor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            settlement.State,
            successorRequest,
            predecessor,
            RuntimeUnlockRetryReleaseKind.Unknown,
            null,
            0,
            "pending");

        Assert.True(successor.Granted);
        var successorContract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(successor.Contract);
        Assert.Equal(2, successorContract.Authority.DispatchAttemptGeneration);
        Assert.Equal(1, successorContract.Authority.MissingDispatchSuccessorGeneration);
        Assert.Equal(predecessor.Authority.AuthorityId, successorContract.Authority.PredecessorAuthorityId);
        Assert.False(successorContract.RequestActivePredecessorSupersession);
        Assert.Equal(sourceTransportEpoch + 1, successorContract.Authority.TransportEpoch);
        Assert.Equal(predecessor.Authority.ReleaseKind, successorContract.Authority.ReleaseKind);
        Assert.Equal(predecessor.Authority.ReleaseAuthorityId, successorContract.Authority.ReleaseAuthorityId);
        Assert.Equal(predecessor.Authority.ReleaseGeneration, successorContract.Authority.ReleaseGeneration);
        Assert.Equal(nameof(FallbackTailYieldKind.Quiescent), successorContract.Authority.ReleaseOutcome);

        var mutatedAuthority = successorContract.Authority with
        {
            ReleaseAuthorityId = new FileTransferAuthorityId("fta2-mutated-successor-release"),
        };
        var mutatedContract = new RuntimeUnlockRetryDispatchContract(
            mutatedAuthority,
            successorContract.RequestActivePredecessorSupersession,
            successorContract.RegularV4LivenessGeneration,
            successorContract.RequestBridgeDecisionGeneration);
        var mutatedState = successor.State with
        {
            CurrentRuntimeUnlockRetryDispatchAuthority = mutatedAuthority,
        };
        var mutatedDecision = successor with
        {
            Contract = mutatedContract,
            State = mutatedState,
        };
        Assert.False(FileTransferCoordinator.TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
            mutatedState,
            mutatedDecision,
            successorRequest,
            mutatedContract,
            activePredecessorContract: null,
            lastSettlement: predecessorReport,
            currentReceipt: null,
            lastInstallGeneration: 1,
            out _,
            out var mutatedMismatchField));
        Assert.Equal("release_currentness", mutatedMismatchField);

        Assert.True(FileTransferCoordinator.TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
            successor.State,
            successor,
            successorRequest,
            successorContract,
            activePredecessorContract: null,
            lastSettlement: predecessorReport,
            currentReceipt: null,
            lastInstallGeneration: 1,
            out var receipt,
            out var mismatchField));
        Assert.Equal("none", mismatchField);
        Assert.Equal(2, receipt.InstallGeneration);
        Assert.Equal(successorContract.Authority, receipt.Authority);

        Assert.True(successorContract.TryBindOfferAlias(3, "fallback-successor-bounded"));
        Assert.True(successorContract.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            "fallback_successor_dispatch_not_observed"));
        Assert.True(successorContract.TryCreateSettlementReport(out var successorReport));
        var successorSettlement = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            successor.State,
            successorContract,
            predecessorReport,
            successorReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, successorSettlement.Status);
        var bounded = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            successorSettlement.State,
            successorRequest with
            {
                RetiredOfferGeneration = 3,
                DispatchAttemptGeneration = 3,
                MissingDispatchSuccessorGeneration = 2,
                PredecessorAuthorityId = successorContract.Authority.AuthorityId,
            },
            successorContract,
            RuntimeUnlockRetryReleaseKind.Unknown,
            null,
            0,
            "pending");
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, bounded.Status);
        Assert.Equal("retry_dispatch_missing_successor_limit_exhausted", bounded.Reason);
        Assert.Null(bounded.State.CurrentRuntimeUnlockRetryDispatchAuthority);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_FallbackReleaseTupleRejectsSyntheticAndMutatedValues()
    {
        var (state, request, tailYield) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource();

        RuntimeUnlockRetryDispatchDecision Authorize(
            FileTransferAuthorityId authorityId,
            int generation,
            string outcome)
            => FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
                state,
                request,
                null,
                RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield,
                authorityId,
                generation,
                outcome);

        foreach (var rejected in new[]
                 {
                     Authorize(tailYield.AuthorityId, tailYield.YieldGeneration, "PairedYieldGranted"),
                     Authorize(new FileTransferAuthorityId("fta2-mutated-fallback-yield"), tailYield.YieldGeneration, tailYield.YieldKind.ToString()),
                     Authorize(tailYield.AuthorityId, tailYield.YieldGeneration + 1, tailYield.YieldKind.ToString()),
                     Authorize(tailYield.AuthorityId, tailYield.YieldGeneration, FallbackTailYieldKind.Failed.ToString()),
                 })
        {
            Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, rejected.Status);
            Assert.Equal("retry_dispatch_predecessor_release_invalid", rejected.Reason);
            Assert.Null(rejected.Contract);
            Assert.Null(rejected.State.CurrentRuntimeUnlockRetryDispatchAuthority);
        }
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_FallbackActiveSupersessionPreservesCanonicalRelease()
    {
        var (state, request, tailYield) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource();
        var first = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield,
            tailYield.AuthorityId,
            tailYield.YieldGeneration,
            tailYield.YieldKind.ToString());
        var predecessor = Assert.IsType<RuntimeUnlockRetryDispatchContract>(first.Contract);
        var successorRequest = request with
        {
            DispatchAttemptGeneration = 2,
            PredecessorAuthorityId = predecessor.Authority.AuthorityId,
            RequestActivePredecessorSupersession = true,
        };

        var successor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            first.State,
            successorRequest,
            predecessor,
            RuntimeUnlockRetryReleaseKind.Unknown,
            null,
            0,
            "pending");

        Assert.True(successor.Granted);
        Assert.True(successor.Contract!.RequestActivePredecessorSupersession);
        Assert.Equal(predecessor.Authority.ReleaseKind, successor.Contract.Authority.ReleaseKind);
        Assert.Equal(predecessor.Authority.ReleaseAuthorityId, successor.Contract.Authority.ReleaseAuthorityId);
        Assert.Equal(predecessor.Authority.ReleaseGeneration, successor.Contract.Authority.ReleaseGeneration);
        Assert.Equal(predecessor.Authority.ReleaseOutcome, successor.Contract.Authority.ReleaseOutcome);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_HeldFallbackRequestUsesCurrentSameLegTransportEpoch()
    {
        var (state, request, tailYield) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource();
        var originalTransportEpoch = state.CurrentLeg!.TransportEpochId;
        var held = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.Unknown,
            null,
            0,
            "pending");
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Held, held.Status);

        held.State.CurrentLeg!.TransportEpochId = originalTransportEpoch + 1;
        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            held.State,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield,
            tailYield.AuthorityId,
            tailYield.YieldGeneration,
            tailYield.YieldKind.ToString());

        Assert.True(granted.Granted);
        Assert.Equal(request, granted.Request);
        Assert.Equal(originalTransportEpoch + 1, granted.Contract!.Authority.TransportEpoch);
        Assert.Equal(tailYield.AuthorityId, granted.Contract.Authority.ReleaseAuthorityId);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_HeldFallbackRequestRejectsStaleCurrentnessTuples()
    {
        var (state, request, tailYield) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource();

        RuntimeUnlockRetryDispatchDecision Recheck(
            FileTransferCoordinatorState candidateState,
            RuntimeUnlockRetryDispatchAuthorityRequest candidateRequest)
            => FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
                candidateState,
                candidateRequest,
                null,
                RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield,
                tailYield.AuthorityId,
                tailYield.YieldGeneration,
                tailYield.YieldKind.ToString());

        var stalePublication = Recheck(
            state,
            request with
            {
                SourcePublicationAuthority = request.SourcePublicationAuthority with
                {
                    RoutePublicationGeneration =
                        request.SourcePublicationAuthority.RoutePublicationGeneration + 1,
                },
            });
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, stalePublication.Status);
        Assert.Equal("retry_dispatch_source_publication_mismatch", stalePublication.Reason);

        var replacementPublication = FileTransferCoordinator.AuthorizeAction(
            state,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                "test",
                "fallback_retry_publication_replacement",
                RuntimeOwner: state.CurrentRuntimeOwner,
                Route: state.RouteSelection.Route,
                ProtocolVersion: state.RouteSelection.ProtocolVersion,
                LiveRouteEpoch: state.CurrentLeg!.LiveRouteEpochId,
                LegGeneration: state.CurrentLeg.Generation,
                RuntimeGeneration: state.CurrentRuntimeOwner!.Value.RuntimeGeneration,
                TerminalGeneration: state.TerminalGeneration,
                TargetRoute: state.RouteSelection.Route,
                TargetProtocolVersion: state.RouteSelection.ProtocolVersion,
                TargetTransport: FileTransferTransportKind.RegularNkn));
        Assert.True(replacementPublication.Accepted);
        var replacedPublication = Recheck(replacementPublication.State, request);
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, replacedPublication.Status);
        Assert.Equal("retry_dispatch_source_publication_mismatch", replacedPublication.Reason);

        var staleOwner = Recheck(
            state with
            {
                CurrentRuntimeOwner = state.CurrentRuntimeOwner!.Value with
                {
                    RuntimeGeneration = state.CurrentRuntimeOwner.Value.RuntimeGeneration + 1,
                },
            },
            request);
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, staleOwner.Status);

        var staleRoute = Recheck(
            state with
            {
                RouteSelection = FileTransferRouteResolver.Resolve(
                    FileTransferRoute.RegularNknV4Fast),
            },
            request);
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, staleRoute.Status);

        var terminal = Recheck(state with { IsTerminal = true }, request);
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, terminal.Status);
        Assert.Equal("terminal_state", terminal.Reason);

        var stalePredecessor = Recheck(
            state,
            request with
            {
                PredecessorAuthorityId = new FileTransferAuthorityId("fta2-stale-recheck-predecessor"),
            });
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, stalePredecessor.Status);
        Assert.Equal("retry_dispatch_predecessor_missing", stalePredecessor.Reason);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_SameGenerationStableRequestCollisionRejects()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(granted.Granted);

        var collision = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            granted.State,
            request with { RetryReason = "different_retry_reason" },
            granted.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());

        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, collision.Status);
        Assert.Equal("retry_dispatch_attempt_identity_mismatch", collision.Reason);

        var ordinalCollision = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            granted.State,
            request with { MissingDispatchSuccessorGeneration = 1 },
            granted.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, ordinalCollision.Status);
        Assert.Equal("retry_dispatch_attempt_identity_mismatch", ordinalCollision.Reason);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_InitialAttemptRequiresZeroMissingDispatchGeneration()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();

        var rejected = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request with { MissingDispatchSuccessorGeneration = 1 },
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());

        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, rejected.Status);
        Assert.Equal("retry_dispatch_initial_missing_successor_generation_mismatch", rejected.Reason);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_InitialRequestRequiresExactOrdinalAndNoPredecessorOrSupersession()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();

        foreach (var invalidAttempt in new[] { 0, 2 })
        {
            var rejected = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
                state,
                request with { DispatchAttemptGeneration = invalidAttempt },
                null,
                RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
                release.AuthorityId,
                release.RepairGeneration,
                FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
            Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, rejected.Status);
            Assert.Equal("retry_dispatch_initial_attempt_generation_mismatch", rejected.Reason);
        }

        var predecessorPresent = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request with
            {
                PredecessorAuthorityId = new FileTransferAuthorityId("fta2-runtime-unlock-unexpected-predecessor"),
            },
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal("retry_dispatch_predecessor_missing", predecessorPresent.Reason);

        var supersession = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request with { RequestActivePredecessorSupersession = true },
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal("retry_dispatch_initial_supersession_invalid", supersession.Reason);

        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(granted.Granted);
        Assert.Equal(1, granted.Contract!.Authority.DispatchAttemptGeneration);
        Assert.Equal(0, granted.Contract.Authority.MissingDispatchSuccessorGeneration);
        Assert.Null(granted.Contract.Authority.PredecessorAuthorityId);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_RequiresSettledPredecessorForSuccessor()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var first = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(first.Granted);
        Assert.True(first.Contract!.TryBindOfferAlias(81, "nonce-settled-successor"));
        var successorRequest = request with
        {
            RetiredOfferGeneration = 81,
            DispatchAttemptGeneration = request.DispatchAttemptGeneration + 1,
            PredecessorAuthorityId = first.Contract.Authority.AuthorityId,
            MissingDispatchSuccessorGeneration = 1,
        };

        var held = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            first.State,
            successorRequest,
            first.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Held, held.Status);
        Assert.True(first.Contract.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            "test_missing_dispatch_successor"));
        Assert.True(first.Contract.TryCreateSettlementReport(out var firstSettlementReport));
        var firstSettlement = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            first.State,
            first.Contract,
            null,
            firstSettlementReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, firstSettlement.Status);
        var successor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            firstSettlement.State,
            successorRequest,
            first.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration + 1,
            FileTransferRegularV4FrontierRepairSettlementOutcome.MissingDispatch.ToString());
        Assert.True(successor.Granted);
        Assert.Equal(first.Contract.Authority.AuthorityId, successor.Contract!.Authority.PredecessorAuthorityId);
        Assert.Equal(first.Contract.Authority.RecoveryContractGeneration, successor.Contract.Authority.RecoveryContractGeneration);
        Assert.Equal(first.Contract.Authority.ReleaseAuthorityId, successor.Contract.Authority.ReleaseAuthorityId);
        Assert.Equal(first.Contract.Authority.ReleaseGeneration, successor.Contract.Authority.ReleaseGeneration);
        Assert.Equal(first.Contract.Authority.ReleaseOutcome, successor.Contract.Authority.ReleaseOutcome);
        Assert.Equal(1, successor.Contract.Authority.MissingDispatchSuccessorGeneration);

        Assert.True(successor.Contract.TryBindOfferAlias(82, "nonce-successor-limit"));
        Assert.True(successor.Contract.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            "test_missing_dispatch_limit"));
        Assert.True(successor.Contract.TryCreateSettlementReport(out var successorSettlementReport));
        var successorSettlement = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            successor.State,
            successor.Contract,
            firstSettlementReport,
            successorSettlementReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, successorSettlement.Status);
        var exhausted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            successorSettlement.State,
            successorRequest with
            {
                RetiredOfferGeneration = 82,
                DispatchAttemptGeneration = successorRequest.DispatchAttemptGeneration + 1,
                MissingDispatchSuccessorGeneration = 2,
                PredecessorAuthorityId = successor.Contract.Authority.AuthorityId,
            },
            successor.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration + 2,
            FileTransferRegularV4FrontierRepairSettlementOutcome.MissingDispatch.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, exhausted.Status);
        Assert.Equal("retry_dispatch_missing_successor_limit_exhausted", exhausted.Reason);
        Assert.Null(exhausted.State.CurrentRuntimeUnlockRetryDispatchAuthority);
        Assert.Equal(successorSettlementReport, exhausted.State.LastRuntimeUnlockRetryDispatchSettlement);

        var staleIndependentRequest = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            exhausted.State,
            request with { RecoveryContractGeneration = request.RecoveryContractGeneration + 1 },
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, staleIndependentRequest.Status);
        Assert.Equal(
            "regular_v4_recovery_settlement_consumption_already_used",
            staleIndependentRequest.Reason);

        var nextRepair = FileTransferCoordinator.AuthorizeAction(
            exhausted.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                exhausted.State.CurrentRuntimeOwner!.Value,
                exhausted.State.CurrentLeg!,
                repairReplayKind: RepairReplayKind.V4Repair));
        Assert.True(nextRepair.Accepted);
        var nextRelease = nextRepair.RepairReplayDecision!.Value;
        var freshRequest = request with
        {
            RecoveryContractGeneration = request.RecoveryContractGeneration + 1,
        };
        var freshReport = CreateRegularV4RecoverySettlementReport(
            nextRepair.State,
            freshRequest,
            nextRelease,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved);
        var handoff = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoverySettlement(
            nextRepair.State with
            {
                LastRuntimeUnlockRegularV4RecoverySettlement = null,
                LastCoordinatorIssuedRegularV4RecoverySettlement = freshReport.Settlement,
                RuntimeUnlockRegularV4RecoverySettlementAvailable = false,
            },
            null,
            freshReport);
        Assert.Equal(RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Accepted, handoff.Status);

        var fresh = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            handoff.State,
            freshRequest,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            nextRelease.AuthorityId,
            nextRelease.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(fresh.Granted);
        Assert.Equal(1, fresh.Contract!.Authority.DispatchAttemptGeneration);
        Assert.Equal(0, fresh.Contract.Authority.MissingDispatchSuccessorGeneration);
        Assert.Null(fresh.Contract.Authority.PredecessorAuthorityId);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_ExplicitHandoffDefersPredecessorUntilInstall()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var first = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(first.Granted);
        var successorRequest = request with
        {
            DispatchAttemptGeneration = request.DispatchAttemptGeneration + 1,
            PredecessorAuthorityId = first.Contract!.Authority.AuthorityId,
            RequestActivePredecessorSupersession = true,
        };

        var skippedAttempt = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            first.State,
            successorRequest with { DispatchAttemptGeneration = successorRequest.DispatchAttemptGeneration + 1 },
            first.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal("retry_dispatch_successor_attempt_mismatch", skippedAttempt.Reason);
        Assert.Equal(first.Contract.Authority, skippedAttempt.State.CurrentRuntimeUnlockRetryDispatchAuthority);

        var consumedMissingAllowance = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            first.State,
            successorRequest with { MissingDispatchSuccessorGeneration = 1 },
            first.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal("retry_dispatch_active_successor_generation_mismatch", consumedMissingAllowance.Reason);
        Assert.Equal(first.Contract.Authority, consumedMissingAllowance.State.CurrentRuntimeUnlockRetryDispatchAuthority);

        var successor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            first.State,
            successorRequest,
            first.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());

        Assert.True(successor.Granted);
        Assert.Equal(first.Contract!.Authority.DispatchAttemptGeneration + 1, successor.Contract!.Authority.DispatchAttemptGeneration);
        Assert.Equal(0, successor.Contract!.Authority.MissingDispatchSuccessorGeneration);
        Assert.False(first.Contract.Lifecycle.IsSettled);
        Assert.Null(first.Contract.Lifecycle.SuccessorAuthorityId);
        Assert.Equal(first.Contract.Authority.AuthorityId, successor.Contract.Authority.PredecessorAuthorityId);
        Assert.Equal(
            first.State.LastRuntimeUnlockRetryDispatchSettlement,
            successor.State.LastRuntimeUnlockRetryDispatchSettlement);
        Assert.True(successor.Contract.RequestActivePredecessorSupersession);

        Assert.False(FileTransferCoordinator.TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
            successor.State,
            successor,
            successorRequest,
            successor.Contract,
            null,
            successor.State.LastRuntimeUnlockRetryDispatchSettlement,
            currentReceipt: null,
            lastInstallGeneration: 1,
            out _,
            out var missingPredecessorField));
        Assert.Equal("active_predecessor_lineage", missingPredecessorField);

        Assert.True(FileTransferCoordinator.TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
            successor.State,
            successor,
            successorRequest,
            successor.Contract,
            first.Contract,
            successor.State.LastRuntimeUnlockRetryDispatchSettlement,
            currentReceipt: null,
            lastInstallGeneration: 1,
            out var receipt,
            out var receiptMismatchField));
        Assert.Equal("none", receiptMismatchField);
        Assert.Equal(2, receipt.InstallGeneration);
        Assert.Equal(successorRequest, receipt.Request);
        Assert.True(receipt.Request.RequestActivePredecessorSupersession);
        Assert.False(first.Contract.Lifecycle.IsSettled);

        var idempotentSuccessor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            successor.State,
            successorRequest,
            successor.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(idempotentSuccessor.Granted);
        Assert.Equal("idempotent", idempotentSuccessor.Reason);
        Assert.Same(successor.Contract, idempotentSuccessor.Contract);

        var flippedSuccessor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            successor.State,
            successorRequest with { RequestActivePredecessorSupersession = false },
            successor.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, flippedSuccessor.Status);
        Assert.Equal("retry_dispatch_attempt_identity_mismatch", flippedSuccessor.Reason);
        Assert.Equal(successor.Contract.Authority, flippedSuccessor.State.CurrentRuntimeUnlockRetryDispatchAuthority);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_SettledSuccessorRequiresExactAttemptAndMissingDispatch()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var orphanedSuccessor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request with
            {
                DispatchAttemptGeneration = request.DispatchAttemptGeneration + 1,
                PredecessorAuthorityId = new FileTransferAuthorityId("fta2-runtime-unlock-orphaned-predecessor"),
            },
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal("retry_dispatch_predecessor_missing", orphanedSuccessor.Reason);

        var first = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(first.Granted);
        Assert.True(first.Contract!.TryBindOfferAlias(91, "nonce-successor-identity"));
        Assert.True(first.Contract.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            "test_missing_dispatch"));
        Assert.True(first.Contract.TryCreateSettlementReport(out var firstSettlementReport));
        var firstSettlement = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            first.State,
            first.Contract,
            null,
            firstSettlementReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, firstSettlement.Status);

        var successorRequest = request with
        {
            RetiredOfferGeneration = 91,
            DispatchAttemptGeneration = request.DispatchAttemptGeneration + 1,
            PredecessorAuthorityId = first.Contract.Authority.AuthorityId,
            MissingDispatchSuccessorGeneration = 1,
        };
        var invalidSupersession = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            firstSettlement.State,
            successorRequest with { RequestActivePredecessorSupersession = true },
            first.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal("retry_dispatch_missing_successor_supersession_invalid", invalidSupersession.Reason);
        var skippedAttempt = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            firstSettlement.State,
            successorRequest with { DispatchAttemptGeneration = successorRequest.DispatchAttemptGeneration + 1 },
            first.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal("retry_dispatch_successor_attempt_mismatch", skippedAttempt.Reason);

        var wrongRetiredOffer = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            firstSettlement.State,
            successorRequest with { RetiredOfferGeneration = 92 },
            first.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal("retry_dispatch_successor_retired_offer_mismatch", wrongRetiredOffer.Reason);

        var (secondState, secondRequest, secondRelease) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var nonMissing = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            secondState,
            secondRequest,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            secondRelease.AuthorityId,
            secondRelease.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(nonMissing.Contract!.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.AuthorityRejected,
            "test_rejected"));
        var rejectedSuccessor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            nonMissing.State,
            secondRequest with
            {
                DispatchAttemptGeneration = secondRequest.DispatchAttemptGeneration + 1,
                PredecessorAuthorityId = nonMissing.Contract.Authority.AuthorityId,
            },
            nonMissing.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            secondRelease.AuthorityId,
            secondRelease.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal("retry_dispatch_predecessor_settlement_invalid", rejectedSuccessor.Reason);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_PreOfferMissingDispatchUsesIssuedRetiredGeneration()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var predecessor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        var predecessorContract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(predecessor.Contract);
        Assert.Equal(0, predecessorContract.OfferGeneration);
        Assert.True(predecessorContract.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            "pre_offer_dispatch_missing"));
        Assert.True(predecessorContract.TryCreateSettlementReport(out var predecessorReport));
        var settlement = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            predecessor.State,
            predecessorContract,
            null,
            predecessorReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, settlement.Status);

        var successorRequest = request with
        {
            RetiredOfferGeneration = predecessorContract.Authority.RetiredOfferGeneration,
            DispatchAttemptGeneration = predecessorContract.Authority.DispatchAttemptGeneration + 1,
            MissingDispatchSuccessorGeneration =
                predecessorContract.Authority.MissingDispatchSuccessorGeneration + 1,
            PredecessorAuthorityId = predecessorContract.Authority.AuthorityId,
        };
        var mismatch = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            settlement.State,
            successorRequest with
            {
                RetiredOfferGeneration = successorRequest.RetiredOfferGeneration + 1,
            },
            predecessorContract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal("retry_dispatch_successor_retired_offer_mismatch", mismatch.Reason);
        Assert.Equal(predecessorContract.Authority, mismatch.State.CurrentRuntimeUnlockRetryDispatchAuthority);

        var successor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            settlement.State,
            successorRequest,
            predecessorContract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(successor.Granted);
        Assert.Equal(predecessorContract.Authority.AuthorityId, successor.Contract!.Authority.PredecessorAuthorityId);
        Assert.Equal(2, successor.Contract.Authority.DispatchAttemptGeneration);
        Assert.Equal(1, successor.Contract.Authority.MissingDispatchSuccessorGeneration);
        Assert.False(successor.Contract.RequestActivePredecessorSupersession);

        var idempotentSuccessor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            successor.State,
            successorRequest,
            successor.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(idempotentSuccessor.Granted);
        Assert.Equal("idempotent", idempotentSuccessor.Reason);
        Assert.Same(successor.Contract, idempotentSuccessor.Contract);

        var flippedSuccessor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            successor.State,
            successorRequest with { RequestActivePredecessorSupersession = true },
            successor.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, flippedSuccessor.Status);
        Assert.Equal("retry_dispatch_attempt_identity_mismatch", flippedSuccessor.Reason);
        Assert.Equal(successor.Contract.Authority, flippedSuccessor.State.CurrentRuntimeUnlockRetryDispatchAuthority);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_PreOfferMissingDispatchPreservesZeroOfferLineage()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource(
            retiredOfferGeneration: 0);
        var predecessor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        var predecessorContract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(predecessor.Contract);
        Assert.Equal(0, predecessorContract.Authority.RetiredOfferGeneration);
        Assert.Equal(0, predecessorContract.OfferGeneration);
        Assert.True(predecessorContract.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            "pre_offer_dispatch_missing"));
        Assert.True(predecessorContract.TryCreateSettlementReport(out var predecessorReport));
        var settlement = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            predecessor.State,
            predecessorContract,
            null,
            predecessorReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, settlement.Status);

        var successorRequest = request with
        {
            DispatchAttemptGeneration = 2,
            MissingDispatchSuccessorGeneration = 1,
            PredecessorAuthorityId = predecessorContract.Authority.AuthorityId,
        };
        var successor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            settlement.State,
            successorRequest,
            predecessorContract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(successor.Granted);
        Assert.Equal(0, successor.Contract!.Authority.RetiredOfferGeneration);
        Assert.Equal(2, successor.Contract.Authority.DispatchAttemptGeneration);
        Assert.Equal(1, successor.Contract.Authority.MissingDispatchSuccessorGeneration);
        Assert.Equal(predecessorContract.Authority.AuthorityId, successor.Contract.Authority.PredecessorAuthorityId);

        var duplicate = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            successor.State,
            successorRequest,
            successor.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(duplicate.Granted);
        Assert.Equal("idempotent", duplicate.Reason);
        Assert.Same(successor.Contract, duplicate.Contract);

        Assert.True(successor.Contract.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            "successor_pre_offer_dispatch_missing"));
        Assert.True(successor.Contract.TryCreateSettlementReport(out var successorReport));
        var successorSettlement = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            successor.State,
            successor.Contract,
            predecessorReport,
            successorReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, successorSettlement.Status);

        var boundedRequest = successorRequest with
        {
            DispatchAttemptGeneration = 3,
            MissingDispatchSuccessorGeneration = 2,
            PredecessorAuthorityId = successor.Contract.Authority.AuthorityId,
        };
        var bounded = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            successorSettlement.State,
            boundedRequest,
            successor.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, bounded.Status);
        Assert.Equal("retry_dispatch_missing_successor_limit_exhausted", bounded.Reason);
        Assert.Null(bounded.State.CurrentRuntimeUnlockRetryDispatchAuthority);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_NegativeRetiredOfferGenerationRejects()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var decision = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request with { RetiredOfferGeneration = -1 },
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());

        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, decision.Status);
        Assert.Equal("retry_dispatch_retired_offer_generation_invalid", decision.Reason);
        Assert.Null(decision.Contract);
        Assert.Null(decision.State.CurrentRuntimeUnlockRetryDispatchAuthority);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_DoesNotSupersedeSendInFlight()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var first = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(first.Contract!.TryBindOfferAlias(61, "nonce-in-flight"));
        Assert.True(first.Contract.TryBeginSend(out _));
        var successorRequest = request with
        {
            DispatchAttemptGeneration = request.DispatchAttemptGeneration + 1,
            PredecessorAuthorityId = first.Contract.Authority.AuthorityId,
            RequestActivePredecessorSupersession = true,
        };

        var held = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            first.State,
            successorRequest,
            first.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());

        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Held, held.Status);
        Assert.Equal("retry_dispatch_predecessor_send_in_flight", held.Reason);
        Assert.False(first.Contract.Lifecycle.IsSettled);
    }

    [Fact]
    public async Task RuntimeUnlockRetryDispatchAuthority_SendStartAndInstallHaveOneWinner()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
            var first = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
                state,
                request,
                null,
                RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
                release.AuthorityId,
                release.RepairGeneration,
                FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
            var predecessor = Assert.IsType<RuntimeUnlockRetryDispatchContract>(first.Contract);
            Assert.True(predecessor.TryBindOfferAlias(1000 + iteration, $"nonce-race-{iteration}"));
            var successorRequest = request with
            {
                DispatchAttemptGeneration = request.DispatchAttemptGeneration + 1,
                PredecessorAuthorityId = predecessor.Authority.AuthorityId,
                RequestActivePredecessorSupersession = true,
            };
            using var start = new ManualResetEventSlim(false);
            var sendStarted = false;
            var successorInstalled = false;
            RuntimeUnlockRetryDispatchDecision successor = default;

            var sendTask = Task.Run(() =>
            {
                start.Wait();
                sendStarted = predecessor.TryBeginSend(out _);
            });
            var successorTask = Task.Run(() =>
            {
                start.Wait();
                successor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
                    first.State,
                    successorRequest,
                    predecessor,
                    RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
                    release.AuthorityId,
                    release.RepairGeneration,
                    FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
                if (successor.Granted && successor.Contract is { } successorContract)
                {
                    successorInstalled = predecessor.TrySupersedeBeforeSend(
                        "coordinator_successor_installed",
                        successorContract.Authority.AuthorityId);
                }
            });

            start.Set();
            await Task.WhenAll(sendTask, successorTask);

            Assert.NotEqual(sendStarted, successorInstalled);
            Assert.True(sendStarted || successorInstalled);
        }
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchContract_AliasBoundAttemptSettlesExactlyOnce()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        var contract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(granted.Contract);

        Assert.Equal(RuntimeUnlockRetryDispatchAttemptPhase.Granted, contract.AttemptPhase);
        Assert.True(contract.TryBindOfferAlias(41, "nonce-attempt"));
        Assert.Equal(RuntimeUnlockRetryDispatchAttemptPhase.AliasBound, contract.AttemptPhase);
        Assert.True(contract.TryBeginSend(out var attempt));
        Assert.Equal(RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight, contract.AttemptPhase);
        Assert.False(contract.TryBeginSend(out _));
        Assert.True(contract.TryGetInFlightAttempt(out var retainedAttempt));
        Assert.Equal(attempt, retainedAttempt);
        Assert.True(contract.TryCompleteMissingDispatch(attempt, "peer_visible_send_missing"));
        Assert.Equal(RuntimeUnlockRetryDispatchAttemptPhase.Settled, contract.AttemptPhase);
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            contract.Lifecycle.SettlementOutcome);
        Assert.False(contract.TryCompleteObserved(attempt, "late_observed_send"));
        Assert.False(contract.TryBindOfferAlias(42, "nonce-successor"));
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchContract_ObservedSendWinsLateMissingDispatchCallback()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        var contract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(granted.Contract);

        Assert.True(contract.TryBindOfferAlias(51, "nonce-observed"));
        Assert.True(contract.TryBeginSend(out var attempt));
        Assert.True(contract.TryCompleteObserved(attempt, "peer_visible_send_observed"));
        Assert.False(contract.TryCompleteMissingDispatch(attempt, "late_send_timeout"));
        Assert.True(contract.Lifecycle.DispatchObserved);
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved,
            contract.Lifecycle.SettlementOutcome);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchSettlement_AcceptsMissingDispatchAndDispatchObservedAndIsIdempotent()
    {
        var (missingState, missingRequest, missingRelease) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var missingGrant = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            missingState,
            missingRequest,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            missingRelease.AuthorityId,
            missingRelease.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        var missingContract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(missingGrant.Contract);
        Assert.True(missingContract.TryBindOfferAlias(201, "missing-settlement"));
        Assert.True(missingContract.TryBeginSend(out var missingAttempt));
        Assert.True(missingContract.TryCompleteMissingDispatch(missingAttempt, "offer_send_not_observed"));
        Assert.True(missingContract.TryCreateSettlementReport(out var missingReport));
        Assert.Equal(missingContract.Authority, missingReport.Authority);

        var missingDecision = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            missingGrant.State,
            missingContract,
            null,
            missingReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, missingDecision.Status);
        Assert.Equal(missingContract.Authority, missingDecision.State.CurrentRuntimeUnlockRetryDispatchAuthority);
        Assert.Equal(missingReport, missingDecision.State.LastRuntimeUnlockRetryDispatchSettlement);

        var duplicate = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            missingDecision.State,
            missingContract,
            missingReport,
            missingReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent, duplicate.Status);
        Assert.Same(missingDecision.State, duplicate.State);

        var (observedState, observedRequest, observedRelease) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var observedGrant = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            observedState,
            observedRequest,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            observedRelease.AuthorityId,
            observedRelease.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        var observedContract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(observedGrant.Contract);
        Assert.True(observedContract.TryBindOfferAlias(202, "observed-settlement"));
        Assert.True(observedContract.TryBeginSend(out var observedAttempt));
        Assert.True(observedContract.TryCompleteObserved(observedAttempt, "peer_visible_send_observed"));
        Assert.True(observedContract.TryCreateSettlementReport(out var observedReport));

        var observedDecision = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            observedGrant.State,
            observedContract,
            null,
            observedReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, observedDecision.Status);
        Assert.Null(observedDecision.State.CurrentRuntimeUnlockRetryDispatchAuthority);
        Assert.Equal(observedReport, observedDecision.State.LastRuntimeUnlockRetryDispatchSettlement);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchSettlement_AcceptsTerminalRejectionAndRejectsUnsettledOrMismatchedReports()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        var contract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(granted.Contract);
        var syntheticRecoveryFailure = new RuntimeUnlockRetryDispatchSettlementReport(
            contract.Authority,
            RuntimeUnlockRetryDispatchSettlementOutcome.AuthorityRejected,
            "generic_recovery_failure");
        Assert.False(contract.TryCreateSettlementReport(out _));
        var unsettled = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            granted.State,
            contract,
            null,
            syntheticRecoveryFailure);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected, unsettled.Status);
        Assert.Equal("retry_dispatch_settlement_lifecycle_unsettled", unsettled.Reason);
        Assert.Equal(contract.Authority, unsettled.State.CurrentRuntimeUnlockRetryDispatchAuthority);

        Assert.True(contract.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.TransferTerminal,
            "transfer_terminal"));
        Assert.True(contract.TryCreateSettlementReport(out var terminalReport));
        var terminalState = granted.State with
        {
            IsTerminal = true,
            CurrentRuntimeUnlockRetryDispatchAuthority = null,
        };
        var terminal = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            terminalState,
            contract,
            null,
            terminalReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, terminal.Status);
        Assert.Null(terminal.State.CurrentRuntimeUnlockRetryDispatchAuthority);
        Assert.Equal(terminalReport, terminal.State.LastRuntimeUnlockRetryDispatchSettlement);

        var mismatched = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            terminal.State,
            contract,
            terminalReport,
            terminalReport with { Reason = "different_terminal_reason" });
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected, mismatched.Status);
        Assert.Same(terminal.State, mismatched.State);

        var (staleState, staleRequest, staleRelease) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var staleGrant = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            staleState,
            staleRequest,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            staleRelease.AuthorityId,
            staleRelease.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        var staleContract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(staleGrant.Contract);
        Assert.True(staleContract.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.StaleRejected,
            "coordinator_authority_stale"));
        Assert.True(staleContract.TryCreateSettlementReport(out var staleReport));
        var stale = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            staleGrant.State,
            staleContract,
            null,
            staleReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, stale.Status);
        Assert.Null(stale.State.CurrentRuntimeUnlockRetryDispatchAuthority);
        Assert.Equal(staleReport, stale.State.LastRuntimeUnlockRetryDispatchSettlement);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchSettlement_LatePredecessorCannotClearSuccessor()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var predecessor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        var predecessorContract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(predecessor.Contract);
        var successorRequest = request with
        {
            DispatchAttemptGeneration = 2,
            PredecessorAuthorityId = predecessorContract.Authority.AuthorityId,
            RequestActivePredecessorSupersession = true,
        };
        var successor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            predecessor.State,
            successorRequest,
            predecessorContract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(successor.Granted);
        Assert.False(predecessorContract.Lifecycle.IsSettled);
        Assert.True(predecessorContract.TrySupersedeBeforeSend(
            "coordinator_successor_installed",
            successor.Contract!.Authority.AuthorityId));
        Assert.True(predecessorContract.TryCreateSettlementReport(out var predecessorReport));

        var superseded = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            successor.State with { LastRuntimeUnlockRetryDispatchSettlement = null },
            successor.Contract,
            null,
            predecessorReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent, superseded.Status);
        Assert.Equal(successor.Contract!.Authority, superseded.State.CurrentRuntimeUnlockRetryDispatchAuthority);
        Assert.Equal(predecessorReport, superseded.State.LastRuntimeUnlockRetryDispatchSettlement);

        var duplicateWithoutSuccessorState = successor.State with
        {
            CurrentRuntimeUnlockRetryDispatchAuthority = null,
            LastRuntimeUnlockRetryDispatchSettlement = predecessorReport,
        };
        var duplicateWithoutSuccessor = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            duplicateWithoutSuccessorState,
            null,
            predecessorReport,
            predecessorReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected, duplicateWithoutSuccessor.Status);
        Assert.Equal("retry_dispatch_settlement_contract_missing", duplicateWithoutSuccessor.Reason);
        Assert.Same(duplicateWithoutSuccessorState, duplicateWithoutSuccessor.State);

        var mismatchedSuccessorAuthority = successor.Contract.Authority with
        {
            PredecessorAuthorityId = new FileTransferAuthorityId(
                "fta2-runtime-unlock-other-predecessor"),
        };
        var mismatchedSuccessorContract = new RuntimeUnlockRetryDispatchContract(
            mismatchedSuccessorAuthority,
            requestActivePredecessorSupersession: true);
        var duplicateWithMismatchedSuccessorState = successor.State with
        {
            CurrentRuntimeUnlockRetryDispatchAuthority = mismatchedSuccessorAuthority,
            LastRuntimeUnlockRetryDispatchSettlement = predecessorReport,
        };
        var duplicateWithMismatchedSuccessor = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            duplicateWithMismatchedSuccessorState,
            mismatchedSuccessorContract,
            predecessorReport,
            predecessorReport);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected, duplicateWithMismatchedSuccessor.Status);
        Assert.Equal("retry_dispatch_settlement_contract_mismatch", duplicateWithMismatchedSuccessor.Reason);
        Assert.Equal(
            mismatchedSuccessorAuthority,
            duplicateWithMismatchedSuccessor.State.CurrentRuntimeUnlockRetryDispatchAuthority);

        var incompletePredecessor = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            successor.State with { LastRuntimeUnlockRetryDispatchSettlement = null },
            successor.Contract,
            null,
            predecessorReport with
            {
                Authority = predecessorReport.Authority with { RetryReason = string.Empty },
            });
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected, incompletePredecessor.Status);
        Assert.Equal("retry_dispatch_settlement_authority_incomplete", incompletePredecessor.Reason);
        Assert.Equal(successor.Contract.Authority, incompletePredecessor.State.CurrentRuntimeUnlockRetryDispatchAuthority);

        var incompleteTarget = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            successor.State with { LastRuntimeUnlockRetryDispatchSettlement = null },
            successor.Contract,
            null,
            predecessorReport with
            {
                Authority = predecessorReport.Authority with
                {
                    TargetTransport = FileTransferTransportKind.RegularNkn,
                },
            });
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected, incompleteTarget.Status);
        Assert.Equal("retry_dispatch_settlement_authority_incomplete", incompleteTarget.Reason);
        Assert.Equal(successor.Contract.Authority, incompleteTarget.State.CurrentRuntimeUnlockRetryDispatchAuthority);

        var incompleteSourcePublication = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            successor.State with { LastRuntimeUnlockRetryDispatchSettlement = null },
            successor.Contract,
            null,
            predecessorReport with
            {
                Authority = predecessorReport.Authority with
                {
                    SourcePublicationAuthority = predecessorReport.Authority.SourcePublicationAuthority with
                    {
                        TargetProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                    },
                },
            });
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected, incompleteSourcePublication.Status);
        Assert.Equal("retry_dispatch_settlement_authority_incomplete", incompleteSourcePublication.Reason);
        Assert.Equal(successor.Contract.Authority, incompleteSourcePublication.State.CurrentRuntimeUnlockRetryDispatchAuthority);

        var lateMismatch = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(
            superseded.State,
            successor.Contract,
            predecessorReport,
            predecessorReport with
            {
                Outcome = RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
                Reason = "late_predecessor_missing_dispatch",
            });
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected, lateMismatch.Status);
        Assert.Equal(successor.Contract.Authority, lateMismatch.State.CurrentRuntimeUnlockRetryDispatchAuthority);
        Assert.Equal(predecessorReport, lateMismatch.State.LastRuntimeUnlockRetryDispatchSettlement);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_RejectsStalePublicationAndInvalidReleaseOutcome()
    {
        var (state, request, release) = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var stale = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request with
            {
                SourcePublicationAuthority = request.SourcePublicationAuthority with
                {
                    RuntimeGeneration = request.SourcePublicationAuthority.RuntimeGeneration + 1,
                },
            },
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, stale.Status);
        Assert.Equal("retry_dispatch_source_publication_mismatch", stale.Reason);

        var actionKindMismatch = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request with
            {
                SourcePublicationAuthority = request.SourcePublicationAuthority with
                {
                    RoutePublicationActionKind = "RouteCommitPublication",
                },
            },
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, actionKindMismatch.Status);
        Assert.Equal("retry_dispatch_source_publication_mismatch", actionKindMismatch.Reason);

        var invalidOutcome = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.AuthorityRejected.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, invalidOutcome.Status);
        Assert.Equal("regular_v4_repair_settlement_rejected", invalidOutcome.Reason);
    }

    [Fact]
    public void RuntimeUnlockRetryDispatchAuthority_RequiresExactRetiredReceiveRecoveryLease()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;
        var leg = started.State.CurrentLeg!;
        var publicationDecision = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "receive_recovery_retry_publication",
                RuntimeOwner: owner,
                Route: regular.Route,
                ProtocolVersion: regular.ProtocolVersion,
                LiveRouteEpoch: leg.LiveRouteEpochId,
                LegGeneration: leg.Generation,
                RuntimeGeneration: owner.RuntimeGeneration,
                TerminalGeneration: started.State.TerminalGeneration,
                TargetRoute: regular.Route,
                TargetProtocolVersion: regular.ProtocolVersion,
                TargetTransport: FileTransferTransportKind.RegularNkn));
        var recovery = FileTransferCoordinator.AuthorizeAction(
            publicationDecision.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                owner,
                publicationDecision.State.CurrentLeg!,
                bridgeRecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery));
        Assert.True(recovery.Accepted);
        var activeLease = Assert.IsType<RecoveryLivenessLease>(recovery.RecoveryLivenessLease);
        var bridgeDecision = Assert.IsType<BridgeRecoveryDecision>(recovery.BridgeRecoveryDecision);
        var lifecycle = FileTransferCoordinator.ObserveRuntimeUnlockRegularV4RecoveryLifecycle(
            recovery.State,
            null,
            CreateRegularV4RecoveryLifecycleReport(
                recovery.State,
                RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed));
        Assert.Equal(RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted, lifecycle.Status);
        Assert.True(lifecycle.State.RuntimeUnlockRecoveryLeaseClosureAvailable);
        var retiredLease = Assert.IsType<RecoveryLivenessLease>(
            lifecycle.State.LastRetiredRecoveryLivenessLease);
        Assert.Equal(activeLease.AuthorityId, retiredLease.AuthorityId);
        var publication = lifecycle.State.CurrentRoutePublicationToken!.Value;
        var request = new RuntimeUnlockRetryDispatchAuthorityRequest(
            lifecycle.State.SessionId,
            lifecycle.State.TransferId,
            lifecycle.State.Direction,
            new FileTransferRoutePublicationAuthority(
                publication.SessionId,
                publication.TransferId,
                publication.Direction,
                publication.RouteToken,
                publication.ProtocolVersion,
                publication.LiveRouteEpoch,
                publication.RuntimeGeneration,
                publication.TerminalGeneration,
                publication.LegGeneration,
                publication.PublicationGeneration,
                publication.RouteCommitGeneration,
                regular.TelemetryToken,
                publication.TargetProtocolVersion,
                publication.TargetTransport,
                publication.ActionKind.ToString(),
                publication.Reason),
            RecoveryContractGeneration: 51,
            RetiredOfferGeneration: 19,
            DispatchAttemptGeneration: 1,
            RetryReason: "runtime_unlock_offer_send_not_observed",
            RecoveryReason: "regular_v4_receive_recovery_closed",
            RegularV4LivenessGeneration: retiredLease.LeaseGeneration,
            BridgeDecisionGeneration: bridgeDecision.BridgeDecisionGeneration);

        var closureState = lifecycle.State;
        var stale = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            closureState,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure,
            new FileTransferAuthorityId("fta1-stale-recovery-lease"),
            retiredLease.LeaseGeneration,
            RecoveryLivenessLeaseState.Retired.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, stale.Status);
        Assert.Equal("receive_recovery_lease_stale", stale.Reason);

        var staleLivenessGeneration = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            closureState,
            request with { RegularV4LivenessGeneration = retiredLease.LeaseGeneration + 1 },
            null,
            RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure,
            retiredLease.AuthorityId,
            retiredLease.LeaseGeneration,
            RecoveryLivenessLeaseState.Retired.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, staleLivenessGeneration.Status);
        Assert.Equal("receive_recovery_lease_stale", staleLivenessGeneration.Reason);

        var staleBridgeGeneration = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            closureState,
            request with { BridgeDecisionGeneration = bridgeDecision.BridgeDecisionGeneration + 1 },
            null,
            RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure,
            retiredLease.AuthorityId,
            retiredLease.LeaseGeneration,
            RecoveryLivenessLeaseState.Retired.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, staleBridgeGeneration.Status);
        Assert.Equal("receive_recovery_lease_stale", staleBridgeGeneration.Reason);

        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            closureState,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure,
            retiredLease.AuthorityId,
            retiredLease.LeaseGeneration,
            RecoveryLivenessLeaseState.Retired.ToString());
        Assert.True(granted.Granted);
        Assert.Equal(retiredLease.AuthorityId, granted.Contract!.Authority.ReleaseAuthorityId);
        Assert.False(granted.State.RuntimeUnlockRecoveryLeaseClosureAvailable);

        var independentRequest = request with
        {
            RecoveryContractGeneration = request.RecoveryContractGeneration + 1,
        };
        var reused = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            granted.State with { CurrentRuntimeUnlockRetryDispatchAuthority = null },
            independentRequest,
            null,
            RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure,
            retiredLease.AuthorityId,
            retiredLease.LeaseGeneration,
            RecoveryLivenessLeaseState.Retired.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, reused.Status);
        Assert.Equal("receive_recovery_lease_stale", reused.Reason);
    }

    [Fact]
    public void AuthorityLifecycle_RejectsDuplicateDispatchAndSettlement()
    {
        var authorityId = new FileTransferAuthorityId("fta2-lifecycle-test");
        var successorId = new FileTransferAuthorityId("fta2-lifecycle-successor");
        var lifecycle = new FileTransferAuthorityLifecycle<FileTransferRegularV4FrontierRepairSettlementOutcome>(authorityId);

        Assert.True(lifecycle.TryMarkDispatched());
        Assert.False(lifecycle.TryMarkDispatched());
        Assert.True(lifecycle.TrySettle(
            FileTransferRegularV4FrontierRepairSettlementOutcome.Superseded,
            "send_timeout_successor_authorized",
            successorId));
        Assert.False(lifecycle.TrySettle(
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved,
            "late_progress"));
        Assert.True(lifecycle.IsSettled);
        Assert.Equal(FileTransferRegularV4FrontierRepairSettlementOutcome.Superseded, lifecycle.SettlementOutcome);
        Assert.Equal(successorId, lifecycle.SuccessorAuthorityId);
    }

    [Fact]
    public void AuthorityLifecycle_TerminalBeforeDispatchRejectsLateDispatch()
    {
        var lifecycle = new FileTransferAuthorityLifecycle<FileTransferRegularV4FrontierRepairSettlementOutcome>(
            new FileTransferAuthorityId("fta2-fallback-frontier-terminal"));

        Assert.True(lifecycle.TrySettle(
            FileTransferRegularV4FrontierRepairSettlementOutcome.TransferTerminal,
            "transfer_terminal"));
        Assert.False(lifecycle.TryMarkDispatched());
        Assert.False(lifecycle.DispatchObserved);
    }

    [Fact]
    public void AuthorityLifecycle_DispatchAndSettlementAreAtomic()
    {
        var lifecycle = new FileTransferAuthorityLifecycle<RuntimeUnlockRetryDispatchSettlementOutcome>(
            new FileTransferAuthorityId("fta2-runtime-unlock-atomic-dispatch"));

        Assert.True(lifecycle.TryMarkDispatchedAndSettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved,
            "peer_visible_offer_send_observed"));
        Assert.True(lifecycle.DispatchObserved);
        Assert.True(lifecycle.IsSettled);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved, lifecycle.SettlementOutcome);
        Assert.False(lifecycle.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.TransferTerminal,
            "late_terminal_cleanup"));
        Assert.False(lifecycle.TryMarkDispatchedAndSettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved,
            "duplicate_send_callback"));
    }

    [Fact]
    public void RegularV4RepairAuthority_SuccessorSupersedesPredecessor()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;
        var leg = started.State.CurrentLeg!;
        var publication = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "regular_v4_repair_publication",
                RuntimeOwner: owner,
                Route: regular.Route,
                ProtocolVersion: regular.ProtocolVersion,
                LiveRouteEpoch: leg.LiveRouteEpochId,
                LegGeneration: leg.Generation,
                RuntimeGeneration: owner.RuntimeGeneration,
                TerminalGeneration: started.State.TerminalGeneration));
        var predecessor = FileTransferCoordinator.AuthorizeAction(
            publication.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                owner,
                publication.State.CurrentLeg!,
                repairReplayKind: RepairReplayKind.V4Repair));
        var predecessorDecision = predecessor.RepairReplayDecision!.Value;
        var successor = FileTransferCoordinator.AuthorizeAction(
            predecessor.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                owner,
                predecessor.State.CurrentLeg!,
                repairReplayKind: RepairReplayKind.V4Repair,
                predecessorAuthorityId: predecessorDecision.AuthorityId));
        var successorDecision = successor.RepairReplayDecision!.Value;

        Assert.Equal(predecessorDecision.AuthorityId, successorDecision.PredecessorAuthorityId);
        Assert.True(FileTransferCoordinator.IsRepairReplayDecisionCurrent(
            successor.State,
            successorDecision,
            out var successorReason));
        Assert.Equal("accepted", successorReason);
        Assert.False(FileTransferCoordinator.IsRepairReplayDecisionCurrent(
            successor.State,
            predecessorDecision,
            out var predecessorReason));
        Assert.Equal("repair_replay_decision_superseded", predecessorReason);
    }

    [Fact]
    public void RecoveryAuthorityActions_AcceptCurrentRequests()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var state = StartRegular(regular).State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;

        var repair = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                owner,
                leg,
                repairReplayKind: RepairReplayKind.V4Repair));
        Assert.True(repair.Accepted);
        Assert.NotNull(repair.RepairReplayDecision);
        Assert.Equal(RepairReplayKind.V4Repair, repair.RepairReplayDecision!.Value.RepairKind);
        Assert.Equal(
            repair.RepairReplayDecision.Value.RepairGeneration,
            repair.State.CurrentRepairReplayDecision?.RepairGeneration);

        var transportEpoch = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.TransportEpochAdoption,
                owner,
                leg,
                transportEpochActionKind: TransportEpochActionKind.Adopt,
                targetRoute: tuna.Route,
                targetProtocolVersion: tuna.ProtocolVersion,
                targetTransport: FileTransferTransportKind.Tuna));
        Assert.True(transportEpoch.Accepted);
        Assert.NotNull(transportEpoch.TransportEpochDecision);
        Assert.Equal(TransportEpochActionKind.Adopt, transportEpoch.TransportEpochDecision!.Value.ActionKind);
        var promotionAuthority = transportEpoch.TransportEpochDecision.Value;
        Assert.Equal(
            promotionAuthority.TransportDecisionGeneration,
            transportEpoch.State.CurrentTransportEpochDecision?.TransportDecisionGeneration);

        var peerPromotion = FileTransferCoordinator.AuthorizeAction(
            transportEpoch.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.PeerEpochPromotion,
                owner,
                leg,
                transportEpochActionKind: TransportEpochActionKind.PromotePeerEpoch,
                recoveryDecisionGeneration: promotionAuthority.TransportDecisionGeneration,
                targetRoute: tuna.Route,
                targetProtocolVersion: tuna.ProtocolVersion,
                targetTransport: FileTransferTransportKind.Tuna));
        Assert.True(peerPromotion.Accepted);
        Assert.NotNull(peerPromotion.TransportEpochDecision);
        Assert.Equal(FileTransferRoute.FileTunaV4, peerPromotion.TransportEpochDecision!.Value.TargetRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, peerPromotion.TransportEpochDecision.Value.TargetProtocolVersion);

        var bridge = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                owner,
                leg,
                bridgeRecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery));
        Assert.True(bridge.Accepted);
        Assert.NotNull(bridge.BridgeRecoveryDecision);
        Assert.Equal(BridgeRecoveryKind.BridgeReceiveStallRecovery, bridge.BridgeRecoveryDecision!.Value.RecoveryKind);
        Assert.Equal(
            bridge.BridgeRecoveryDecision.Value.BridgeDecisionGeneration,
            bridge.State.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration);
        Assert.NotNull(bridge.RecoveryLivenessLease);
        Assert.Equal(
            bridge.RecoveryLivenessLease.Value.LeaseGeneration,
            bridge.State.CurrentRecoveryLivenessLease?.LeaseGeneration);

        var liveness = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin,
                owner,
                leg));
        Assert.True(liveness.Accepted);
        Assert.NotNull(liveness.RecoveryLivenessLease);
        Assert.Equal(RecoveryLivenessLeaseState.Active, liveness.RecoveryLivenessLease!.Value.State);
        Assert.Equal(
            liveness.RecoveryLivenessLease.Value.LeaseGeneration,
            liveness.State.CurrentRecoveryLivenessLease?.LeaseGeneration);
    }

    [Fact]
    public void RecoveryAuthorityActions_RejectStaleAuthorityFields()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var state = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4).State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;

        var staleOwner = owner with { RuntimeGeneration = owner.RuntimeGeneration - 1 };
        Assert.Equal("runtime_generation_mismatch", FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(FileTransferCoordinatorActionKind.RepairReplay, staleOwner, leg)).RejectionReason);
        Assert.Equal("live_route_epoch_mismatch", FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(FileTransferCoordinatorActionKind.TransportEpochAdoption, owner, leg, liveRouteEpoch: leg.LiveRouteEpochId + 1)).RejectionReason);
        Assert.Equal("leg_generation_mismatch", FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(FileTransferCoordinatorActionKind.ReceiveRecovery, owner, leg, legGeneration: leg.Generation + 1)).RejectionReason);
        Assert.Equal("transport_epoch_mismatch", FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery, owner, leg, transportEpoch: leg.TransportEpochId + 1)).RejectionReason);
        Assert.Equal("bridge_recovery_generation_mismatch", FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin, owner, leg, bridgeRecoveryGeneration: leg.BridgeRecoveryGeneration + 1)).RejectionReason);
        Assert.Equal("route_commit_generation_mismatch", FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(FileTransferCoordinatorActionKind.RepairReplay, owner, leg, routeCommitGeneration: 99)).RejectionReason);
    }

    [Fact]
    public void RecoveryLivenessLease_ValidateAndRetireRequireCurrentLease()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = StartRegular(regular).State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;

        var begin = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin,
                owner,
                leg));
        var lease = begin.RecoveryLivenessLease!.Value;
        var stored = begin.State;

        var missing = FileTransferCoordinator.AuthorizeAction(
            stored,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseValidate,
                owner,
                leg));
        Assert.False(missing.Accepted);
        Assert.Equal("recovery_liveness_lease_missing", missing.RejectionReason);

        var validate = FileTransferCoordinator.AuthorizeAction(
            stored,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseValidate,
                owner,
                leg,
                recoveryLivenessLease: lease));
        Assert.True(validate.Accepted);
        Assert.NotNull(validate.RecoveryLivenessLease);
        Assert.Equal(lease.LeaseGeneration, validate.RecoveryLivenessLease!.Value.LeaseGeneration);

        var staleLease = lease with { LegGeneration = lease.LegGeneration + 1 };
        var stale = FileTransferCoordinator.AuthorizeAction(
            stored,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseValidate,
                owner,
                leg,
                recoveryLivenessLease: staleLease));
        Assert.False(stale.Accepted);
        Assert.Equal("leg_generation_mismatch", stale.RejectionReason);

        var reconstructedLease = lease with
        {
            AuthorityId = new FileTransferAuthorityId("test-reconstructed-lease"),
        };
        var reconstructed = FileTransferCoordinator.AuthorizeAction(
            stored,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseValidate,
                owner,
                leg,
                recoveryLivenessLease: reconstructedLease));
        Assert.False(reconstructed.Accepted);
        Assert.Equal("recovery_liveness_lease_authority_mismatch", reconstructed.RejectionReason);

        var retire = FileTransferCoordinator.AuthorizeAction(
            stored,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseRetire,
                owner,
                leg,
                recoveryLivenessLease: lease));
        Assert.True(retire.Accepted);
        Assert.Equal(RecoveryLivenessLeaseState.Retired, retire.RecoveryLivenessLease!.Value.State);
        Assert.Null(retire.State.CurrentRecoveryLivenessLease);
        Assert.Equal(retire.RecoveryLivenessLease, retire.State.LastRetiredRecoveryLivenessLease);
        Assert.Equal(lease.AuthorityId, retire.State.LastRetiredRecoveryLivenessLease?.AuthorityId);
    }

    [Fact]
    public void TerminalizationAndRouteTransition_InvalidateRecoveryAuthority()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var state = StateWithRecoveryAuthority(StartRegular(regular).State);

        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, regular, reason: "completed"),
            state);
        Assert.Null(terminal.State.CurrentRepairReplayDecision);
        Assert.Null(terminal.State.CurrentTransportEpochDecision);
        Assert.Null(terminal.State.CurrentBridgeRecoveryDecision);
        Assert.Null(terminal.State.CurrentRecoveryLivenessLease);
        Assert.Null(terminal.State.LastRetiredRecoveryLivenessLease);

        var transitioned = ActivateTuna(state, tuna, committedChunk: 3);
        Assert.Null(transitioned.State.CurrentRepairReplayDecision);
        Assert.Null(transitioned.State.CurrentTransportEpochDecision);
        Assert.Null(transitioned.State.CurrentBridgeRecoveryDecision);
        Assert.Null(transitioned.State.CurrentRecoveryLivenessLease);
        Assert.Null(transitioned.State.LastRetiredRecoveryLivenessLease);

        var fallbackState = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4).State;
        var requested = RequestFallbackCheckpoint(fallbackState, fallback, "checkpoint:epoch", 4);
        var recoveryState = StateWithRecoveryAuthority(requested.State);
        var advancedEpoch = AcceptFallbackCheckpoint(recoveryState, fallback, "checkpoint:epoch", 5, 12, 18);

        Assert.Equal(5, advancedEpoch.State.CurrentLeg!.TransportEpochId);
        Assert.Null(advancedEpoch.State.CurrentRepairReplayDecision);
        Assert.Null(advancedEpoch.State.CurrentTransportEpochDecision);
        Assert.Null(advancedEpoch.State.CurrentBridgeRecoveryDecision);
        Assert.Null(advancedEpoch.State.CurrentRecoveryLivenessLease);
        Assert.Null(advancedEpoch.State.LastRetiredRecoveryLivenessLease);
    }

    [Fact]
    public void PeerEpochPromotion_RejectsMissingIllegalOrMismatchedTargetAuthority()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = StartRegular(regular).State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;

        var missingTarget = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.PeerEpochPromotion,
                owner,
                leg,
                transportEpochActionKind: TransportEpochActionKind.PromotePeerEpoch));
        Assert.False(missingTarget.Accepted);
        Assert.Equal("peer_epoch_target_missing", missingTarget.RejectionReason);

        var protocolMismatch = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.PeerEpochPromotion,
                owner,
                leg,
                transportEpochActionKind: TransportEpochActionKind.PromotePeerEpoch,
                targetRoute: FileTransferRoute.FileTunaV4,
                targetProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
                targetTransport: FileTransferTransportKind.Tuna));
        Assert.False(protocolMismatch.Accepted);
        Assert.Equal("file_tuna_v6_rejected", protocolMismatch.RejectionReason);

        var missingTransport = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.PeerEpochPromotion,
                owner,
                leg,
                transportEpochActionKind: TransportEpochActionKind.PromotePeerEpoch,
                targetRoute: FileTransferRoute.FileTunaV4,
                targetProtocolVersion: FileTransferProtocol.ProtocolVersionV4));
        Assert.False(missingTransport.Accepted);
        Assert.Equal("target_transport_missing", missingTransport.RejectionReason);

        var missingDecision = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.PeerEpochPromotion,
                owner,
                leg,
                transportEpochActionKind: TransportEpochActionKind.PromotePeerEpoch,
                targetRoute: FileTransferRoute.FileTunaV4,
                targetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                targetTransport: FileTransferTransportKind.Tuna));
        Assert.False(missingDecision.Accepted);
        Assert.Equal("transport_epoch_decision_missing", missingDecision.RejectionReason);
    }

    [Fact]
    public void ControlPlanePressureAuthorityRefresh_IsDiagnosticOnlyForRecoveryAuthority()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var state = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4).State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;

        var refreshRepair = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                owner,
                leg,
                reason: "control_plane_pressure_authority_refresh"));
        Assert.False(refreshRepair.Accepted);
        Assert.Equal("control_plane_pressure_authority_refresh_diagnostic_only", refreshRepair.RejectionReason);
        Assert.Null(refreshRepair.RepairReplayDecision);

        var pressure = FileTransferCoordinator.AuthorizeAction(
            state,
            FallbackActionRequest(
                FileTransferCoordinatorActionKind.FallbackControlPlanePressure,
                owner,
                leg,
                controlPlanePurpose: "control_plane_pressure_authority_refresh"));
        Assert.True(pressure.Accepted);
        Assert.NotNull(pressure.FallbackControlPlanePressureToken);
        Assert.Null(pressure.RepairReplayDecision);
        Assert.Null(pressure.TransportEpochDecision);
        Assert.Null(pressure.BridgeRecoveryDecision);
        Assert.Null(pressure.RecoveryLivenessLease);
    }

    [Fact]
    public void FallbackCheckpointAccepted_RemainsNonYieldableUntilTailQuiescent()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var started = StartRegular(regular);
        var fallbackDecision = FallBack(started.State, fallback, committedChunk: 32, transportEpoch: 7);
        var requested = RequestFallbackCheckpoint(fallbackDecision.State, fallback, "checkpoint:1", 7);
        var accepted = AcceptFallbackCheckpoint(requested.State, fallback, "checkpoint:1", 7, 64, 96);
        var leg = accepted.State.CurrentLeg!;

        Assert.Equal(FileTransferLegState.RecoveryActive, leg.State);
        Assert.False(FileTransferCoordinator.CanAdmitRuntimeUnlockWhilePostTunaFallback(
            routeUsesPostTunaFallbackRuntime: true,
            leg,
            checkpointDeliveryRecoveryPending: false,
            stateRefreshSendInFlight: false,
            deferredStateRefreshPending: false,
            proofReplayPending: false,
            out var rejectionReason));
        Assert.Equal("recovery_active", rejectionReason);
    }

    [Fact]
    public void FallbackTailQuiescent_MarksLegYieldableForRuntimeUnlock()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var started = StartRegular(regular);
        var fallbackDecision = FallBack(started.State, fallback, committedChunk: 32, transportEpoch: 7);
        var requested = RequestFallbackCheckpoint(fallbackDecision.State, fallback, "checkpoint:1", 7);
        var accepted = AcceptFallbackCheckpoint(requested.State, fallback, "checkpoint:1", 7, 64, 96);
        var quiescent = MarkFallbackTailQuiescent(accepted.State, fallback, 7, 64, 96);
        var leg = quiescent.State.CurrentLeg!;

        Assert.Equal(FileTransferLegState.Active, leg.State);
        Assert.True(leg.CanSendData);
        Assert.False(FileTransferCoordinator.CanAdmitRuntimeUnlockWhilePostTunaFallback(
            routeUsesPostTunaFallbackRuntime: true,
            leg,
            checkpointDeliveryRecoveryPending: false,
            stateRefreshSendInFlight: false,
            deferredStateRefreshPending: false,
            proofReplayPending: false,
            out var rejectionReason));
        Assert.Equal("paired_fallback_tail_yield_missing", rejectionReason);
    }

    [Fact]
    public void RuntimeUnlockTransaction_LocalObservedSendCannotCommitRoute()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 1,
                OfferGeneration: 10,
                Reason: "runtime_unlock"),
            RuntimeUnlockTransactionSnapshot.Idle).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.ObservedSend,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 1,
                OfferGeneration: 10,
                Reason: "control_priority",
                ObservedLane: "control_priority"),
            transaction).State;

        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "local_only", admissionToken));
        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("peer_visible_proof_missing", decision.RuntimeUnlockCommitRejectedReason);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.State.RouteSelection.Route);
    }

    [Fact]
    public void RuntimeUnlockTransaction_PeerProofWithoutPathProbeRejectsRouteCommit()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "runtime_unlock"),
            RuntimeUnlockTransactionSnapshot.Idle).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.PeerReceived,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "tuna_acceleration_offer_received_raw"),
            transaction).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.AnswerReceived,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "transport_acceleration_answer"),
            transaction).State;

        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken));
        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 3,
                highestObservedChunk: 3,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("runtime_unlock_probe_missing", decision.RuntimeUnlockCommitRejectedReason);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.State.RouteSelection.Route);
    }

    [Fact]
    public void RuntimeUnlockTransaction_PeerProofAndTunaPathProbePermitRouteCommit()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "runtime_unlock"),
            RuntimeUnlockTransactionSnapshot.Idle).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.PeerReceived,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "tuna_acceleration_offer_received_raw"),
            transaction).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.AnswerReceived,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "transport_acceleration_answer"),
            transaction).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.PathProbeStarted,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "transport_probe_sent",
                PathProbeId: "probe-22",
                PathProbeTransport: FileTransferTransportKind.Tuna),
            transaction).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.PathProbeAcked,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "transport_probe_ack",
                PathProbeId: "probe-22",
                PathProbeTransport: FileTransferTransportKind.Tuna),
            transaction).State;

        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken));
        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 3,
                highestObservedChunk: 3,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.True(decision.RuntimeUnlockCommitAccepted);
        Assert.True(decision.RouteChanged);
        Assert.Equal(FileTransferRoute.FileTunaV4, decision.State.RouteSelection.Route);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, decision.State.RouteSelection.ProtocolVersion);
        Assert.NotNull(decision.RecoveredLiveRouteEpoch);
        Assert.Equal(1, decision.RecoveredLiveRouteEpoch!.EpochId);
        Assert.NotNull(decision.RouteCommitToken);
        Assert.True(FileTransferCoordinator.IsRouteCommitTokenCurrent(
            decision.State,
            decision.RouteCommitToken!.Value,
            out var routeCommitReason));
        Assert.Equal("current", routeCommitReason);
    }

    [Fact]
    public void RuntimeUnlockTransaction_FailedTunaPathLeaseRejectsRouteCommit()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 3,
                OfferGeneration: 33,
                Reason: "runtime_unlock"),
            RuntimeUnlockTransactionSnapshot.Idle).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.AnswerReceived,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 3,
                OfferGeneration: 33,
                Reason: "transport_acceleration_answer"),
            transaction).State;

        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken) with
        {
            TunaPathLeaseRequired = true,
            TunaPathLeaseGeneration = 7,
            TunaPathLeaseState = RuntimeUnlockTunaPathLeaseState.Failed,
            TunaPathLeaseListenerRunId = "listener-start-7",
            TunaPathLeaseCurrent = false,
            TunaPathLeaseFailureReason = "runtime_unlock_answer_rejected_tuna_path_lease_unavailable",
        };
        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("tuna_path_lease_failed", decision.RuntimeUnlockCommitRejectedReason);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.State.RouteSelection.Route);
    }

    [Fact]
    public void RuntimeUnlockTunaPathLease_CurrentListenerProofAllowsCommit()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 4,
                OfferGeneration: 44,
                Reason: "runtime_unlock"),
            RuntimeUnlockTransactionSnapshot.Idle).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.AnswerReceived,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 4,
                OfferGeneration: 44,
                Reason: "transport_acceleration_answer"),
            transaction).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.PathProbeAcked,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 4,
                OfferGeneration: 44,
                Reason: "transport_probe_ack",
                PathProbeId: "probe-44",
                PathProbeTransport: FileTransferTransportKind.Tuna),
            transaction).State;
        var lease = RuntimeUnlockTunaPathLease.Start(
            started.State.SessionId,
            started.State.TransferId,
            leaseGeneration: 9,
            listenerRunId: "listener-start-9",
            payerDecisionId: 123,
            nowUtcMs: 100);
        lease = RuntimeUnlockTunaPathLease.BindOffer(
            lease,
            transaction.TransactionGeneration,
            transaction.OfferGeneration,
            payerDecisionId: 123,
            nowUtcMs: 101);
        Assert.True(RuntimeUnlockTunaPathLease.TryMarkListenerReady(
            lease,
            started.State.SessionId,
            leaseGeneration: 9,
            listenerRunId: "listener-start-9",
            nowUtcMs: 102,
            out lease));

        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken) with
        {
            TunaPathLeaseRequired = true,
            TunaPathLeaseGeneration = lease.LeaseGeneration,
            TunaPathLeaseState = lease.State,
            TunaPathLeaseListenerRunId = lease.ListenerRunId,
            TunaPathLeaseCurrent = lease.IsCurrent,
            TunaPathLeaseFailureReason = lease.FailureReason,
        };
        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.True(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal(FileTransferRoute.FileTunaV4, decision.State.RouteSelection.Route);
        Assert.NotNull(decision.RouteCommitToken);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_RejectsMissingAdmissionToken()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(started.State);
        var proof = WithCurrentTunaLease(RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received"));

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            started.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Null(decision.RouteCommitToken);
        Assert.Equal("runtime_unlock_admission_missing", decision.RuntimeUnlockCommitRejectedReason);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_RejectsStaleAdmissionTokenAfterRouteChange()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var changed = ActivateTuna(admission.State, tuna, committedChunk: 1);
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State);
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken));

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            changed.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("runtime_unlock_admission_generation_mismatch", decision.RuntimeUnlockCommitRejectedReason);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_RejectsMissingTunaLease()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State);
        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken);

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("tuna_path_lease_generation_missing", decision.RuntimeUnlockCommitRejectedReason);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_RejectsMismatchedSamePathAck()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State, probeId: "probe-current");
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken) with
            {
                SamePathTunaProbeAckId = "probe-old",
            });

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("same_path_tuna_probe_ack_mismatch", decision.RuntimeUnlockCommitRejectedReason);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_RejectsInvalidTargetRoute()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State);
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken) with
            {
                TargetRoute = fallback.Route,
                ProtocolVersion = fallback.ProtocolVersion,
                TargetTransport = FileTransferTransportKind.RegularNkn,
            });

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                fallback,
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("route_commit_metadata_invalid", decision.RuntimeUnlockCommitRejectedReason);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_RejectsEventTargetMismatch()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State);
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken));

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                fallback,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("route_commit_event_target_mismatch", decision.RuntimeUnlockCommitRejectedReason);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.State.RouteSelection.Route);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_RejectsFallbackActiveWithoutYield()
    {
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallbackState = ActiveFallbackStateWithoutCheckpoint();
        var owner = fallbackState.CurrentRuntimeOwner!.Value;
        var survivalState = new RuntimeUnlockFallbackSurvivalState(
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
            PairedFallbackYieldGranted: false);
        var forcedAdmissionToken = new RuntimeUnlockAdmissionToken(
            fallbackState.SessionId,
            fallbackState.TransferId,
            fallbackState.Direction,
            owner,
            fallbackState.RouteSelection.Route,
            fallbackState.RouteSelection.TelemetryToken,
            fallbackState.RouteSelection.ProtocolVersion,
            fallbackState.CurrentLiveRouteEpoch?.EpochId ?? 0,
            fallbackState.LastRuntimeOwnerGeneration,
            fallbackState.TerminalGeneration,
            fallbackState.CurrentLeg?.Generation ?? 0,
            AdmissionGeneration: 1,
            Reason: "forced_test_admission",
            survivalState);
        var forcedState = fallbackState with
        {
            RuntimeUnlockFallbackSurvivalState = survivalState,
            CurrentRuntimeUnlockAdmission = forcedAdmissionToken,
            LastRuntimeUnlockAdmissionGeneration = 1,
        };
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(forcedState);
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", forcedAdmissionToken));

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            forcedState);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("paired_fallback_tail_yield_missing", decision.RuntimeUnlockCommitRejectedReason);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_TokenBecomesStaleAfterOwnerChange()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State);
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken));
        var committed = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);
        var token = committed.RouteCommitToken!.Value;

        var nextStart = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                regular,
                reason: "new_owner",
                state: FileTransferLegState.Active,
                canSendData: true),
            committed.State);

        Assert.False(FileTransferCoordinator.IsRouteCommitTokenCurrent(nextStart.State, token, out var rejectionReason));
        Assert.Equal("route_commit_generation_mismatch", rejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RuntimeUnlockProbeStart_AcceptsCurrentAdmissionToken()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var token = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;

        var decision = FileTransferCoordinator.AuthorizeAction(
            admission.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RuntimeUnlockProbeStart,
                Source: "test",
                Reason: "start_probe",
                RuntimeOwner: token.RuntimeOwner,
                RuntimeUnlockAdmissionToken: token,
                RuntimeUnlockTransactionGeneration: 7,
                RuntimeUnlockOfferGeneration: 8,
                RuntimeUnlockTunaPathLeaseGeneration: 9,
                RuntimeUnlockProbeId: "probe-8",
                TargetRoute: FileTransferRoute.FileTunaV4,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna));

        Assert.True(decision.Accepted);
        Assert.Equal("accepted", decision.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RuntimeUnlockProbeAck_RequiresCurrentAdmissionToken()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var token = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;

        var accepted = FileTransferCoordinator.AuthorizeAction(
            admission.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RuntimeUnlockProbeAck,
                Source: "test",
                Reason: "probe_ack",
                RuntimeOwner: token.RuntimeOwner,
                RuntimeUnlockAdmissionToken: token,
                RuntimeUnlockTransactionGeneration: 7,
                RuntimeUnlockOfferGeneration: 8,
                RuntimeUnlockTunaPathLeaseGeneration: 9,
                RuntimeUnlockProbeId: "probe-8",
                RuntimeUnlockProbeAcked: true,
                RuntimeUnlockSamePathTunaProbeAckId: "probe-8",
                TargetRoute: FileTransferRoute.FileTunaV4,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna));

        var rejected = FileTransferCoordinator.AuthorizeAction(
            admission.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RuntimeUnlockProbeAck,
                Source: "test",
                Reason: "probe_ack",
                RuntimeOwner: token.RuntimeOwner,
                RuntimeUnlockTransactionGeneration: 7,
                RuntimeUnlockOfferGeneration: 8,
                RuntimeUnlockTunaPathLeaseGeneration: 9,
                RuntimeUnlockProbeId: "probe-8",
                RuntimeUnlockProbeAcked: true,
                RuntimeUnlockSamePathTunaProbeAckId: "probe-8",
                TargetRoute: FileTransferRoute.FileTunaV4,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna));

        Assert.True(accepted.Accepted);
        Assert.False(rejected.Accepted);
        Assert.Equal("runtime_unlock_admission_missing", rejected.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RuntimeUnlockTransactionCallback_RequiresCurrentAdmissionToken()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var token = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;

        var accepted = FileTransferCoordinator.AuthorizeAction(
            admission.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RuntimeUnlockTransactionCallback,
                Source: "test",
                Reason: "paired_commit_timeout",
                RuntimeOwner: token.RuntimeOwner,
                RuntimeUnlockAdmissionToken: token,
                RuntimeUnlockTransactionGeneration: 7,
                RuntimeUnlockOfferGeneration: 8,
                RuntimeUnlockTunaPathLeaseGeneration: 9,
                RuntimeUnlockProbeId: "probe-8",
                TargetRoute: FileTransferRoute.FileTunaV4,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna));

        var rejected = FileTransferCoordinator.AuthorizeAction(
            admission.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RuntimeUnlockTransactionCallback,
                Source: "test",
                Reason: "paired_commit_timeout",
                RuntimeOwner: token.RuntimeOwner,
                RuntimeUnlockTransactionGeneration: 7,
                RuntimeUnlockOfferGeneration: 8,
                RuntimeUnlockTunaPathLeaseGeneration: 9,
                RuntimeUnlockProbeId: "probe-8",
                TargetRoute: FileTransferRoute.FileTunaV4,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna));

        Assert.True(accepted.Accepted);
        Assert.False(rejected.Accepted);
        Assert.Equal("runtime_unlock_admission_missing", rejected.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RuntimeUnlockCallback_RejectsStaleAdmissionTokenAfterOwnerChange()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var token = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var changed = ActivateTuna(admission.State, tuna, committedChunk: 1);

        var decision = FileTransferCoordinator.AuthorizeAction(
            changed.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RuntimeUnlockProbeStart,
                Source: "test",
                Reason: "start_probe",
                RuntimeOwner: token.RuntimeOwner,
                RuntimeUnlockAdmissionToken: token,
                RuntimeUnlockTransactionGeneration: 7,
                RuntimeUnlockOfferGeneration: 8,
                RuntimeUnlockTunaPathLeaseGeneration: 9,
                RuntimeUnlockProbeId: "probe-8",
                TargetRoute: FileTransferRoute.FileTunaV4,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna));

        Assert.False(decision.Accepted);
        Assert.Equal("runtime_generation_mismatch", decision.RejectionReason);
    }

    [Fact]
    public void RuntimeUnlockPreCommitProbePending_AcceptsCurrentAuthorityAndReplays()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var token = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var pending = PendingPreCommitProbeRequest(admission.State, token);

        var recorded = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbePending,
                admission.State.RouteSelection,
                reason: "precommit_probe_pending",
                runtimeUnlockPendingPreCommitProbeRequest: pending),
            admission.State);

        Assert.True(recorded.RuntimeUnlockPendingPreCommitProbeDecision!.Value.Accepted);
        Assert.NotNull(recorded.State.CurrentRuntimeUnlockPendingPreCommitProbe);

        var current = recorded.State.CurrentRuntimeUnlockPendingPreCommitProbe!.Value;
        var replay = FileTransferCoordinator.AuthorizeAction(
            recorded.State,
            PendingPreCommitProbeReplayAction(current));

        Assert.True(replay.Accepted);
        Assert.True(replay.RuntimeUnlockPendingPreCommitProbeDecision!.Value.Accepted);

        var replayed = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbeReplayed,
                replay.State.RouteSelection,
                reason: "precommit_probe_replayed",
                runtimeUnlockPendingPreCommitProbeRequest: current),
            replay.State);
        var retired = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbeRetired,
                replayed.State.RouteSelection,
                reason: "precommit_probe_replayed",
                runtimeUnlockPendingPreCommitProbeRequest: current),
            replayed.State);

        Assert.Null(retired.State.CurrentRuntimeUnlockPendingPreCommitProbe);
    }

    [Fact]
    public void RuntimeUnlockPreCommitProbePending_RejectsInvalidTarget()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var token = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var pending = PendingPreCommitProbeRequest(
            admission.State,
            token,
            targetRoute: FileTransferRoute.FileTunaV4,
            targetProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
            targetTransport: FileTransferTransportKind.Tuna);

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbePending,
                admission.State.RouteSelection,
                reason: "precommit_probe_pending",
                runtimeUnlockPendingPreCommitProbeRequest: pending),
            admission.State);

        Assert.False(decision.RuntimeUnlockPendingPreCommitProbeDecision!.Value.Accepted);
        Assert.Equal("route_commit_metadata_invalid", decision.RuntimeUnlockPendingPreCommitProbeDecision!.Value.RejectionReason);
        Assert.Null(decision.State.CurrentRuntimeUnlockPendingPreCommitProbe);
    }

    [Fact]
    public void AuthorizeAction_RuntimeUnlockPendingPreCommitProbeReplay_RejectsStaleOwner()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var token = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var recorded = RecordPendingPreCommitProbe(admission.State, token);
        var pending = recorded.State.CurrentRuntimeUnlockPendingPreCommitProbe!.Value;
        var changed = ActivateTuna(recorded.State, tuna, committedChunk: 1);

        var decision = FileTransferCoordinator.AuthorizeAction(
            changed.State,
            PendingPreCommitProbeReplayAction(pending));

        Assert.False(decision.Accepted);
        Assert.Equal("runtime_generation_mismatch", decision.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RuntimeUnlockPendingPreCommitProbeReplay_RejectsMetadataMismatch()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var token = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var recorded = RecordPendingPreCommitProbe(admission.State, token);
        var pending = recorded.State.CurrentRuntimeUnlockPendingPreCommitProbe!.Value;
        var staleAttempt = pending with { OfferGeneration = pending.OfferGeneration + 1 };

        var decision = FileTransferCoordinator.AuthorizeAction(
            recorded.State,
            PendingPreCommitProbeReplayAction(staleAttempt));

        Assert.False(decision.Accepted);
        Assert.Equal("runtime_unlock_offer_generation_mismatch", decision.RejectionReason);
    }

    [Fact]
    public void RuntimeUnlockPreCommitProbePending_InvalidatedByTerminalAndRouteCommit()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var token = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var recorded = RecordPendingPreCommitProbe(admission.State, token);
        var pending = recorded.State.CurrentRuntimeUnlockPendingPreCommitProbe!.Value;

        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                recorded.State.RouteSelection,
                reason: "completed"),
            recorded.State);

        Assert.Null(terminal.State.CurrentRuntimeUnlockPendingPreCommitProbe);
        Assert.False(FileTransferCoordinator.IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
            terminal.State,
            pending,
            out var terminalReason));
        Assert.Equal("transfer_terminal", terminalReason);

        var committed = CommitRuntimeUnlockToTuna(recorded.State, tuna);

        Assert.Null(committed.State.CurrentRuntimeUnlockPendingPreCommitProbe);
        Assert.False(FileTransferCoordinator.IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
            committed.State,
            pending,
            out var commitReason));
        Assert.Equal("runtime_unlock_pending_precommit_probe_generation_mismatch", commitReason);
    }

    [Fact]
    public void AuthorizeAction_RoutePublication_IssuesTokenForCurrentRegularRoute()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;
        var leg = started.State.CurrentLeg!;

        var decision = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "route_publication",
                RuntimeOwner: owner,
                Route: regular.Route,
                ProtocolVersion: regular.ProtocolVersion,
                LiveRouteEpoch: leg.LiveRouteEpochId,
                LegGeneration: leg.Generation,
                RuntimeGeneration: owner.RuntimeGeneration,
                TerminalGeneration: started.State.TerminalGeneration));

        Assert.True(decision.Accepted);
        Assert.NotNull(decision.RoutePublicationToken);
        Assert.NotNull(decision.RoutePublicationDecision);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.RoutePublicationToken!.Value.TargetRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, decision.RoutePublicationToken.Value.TargetProtocolVersion);
        Assert.Equal(FileTransferTransportKind.RegularNkn, decision.RoutePublicationToken.Value.TargetTransport);
        Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(
            decision.State,
            decision.RoutePublicationToken.Value,
            out _));
    }

    [Fact]
    public void AuthorizeAction_RoutePublication_IssuesTokenForCurrentFallbackRoute()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var state = FallBack(
            ActivateTuna(StartRegular(regular).State, tuna, committedChunk: 4).State,
            fallback,
            committedChunk: 4,
            transportEpoch: 9).State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;

        var decision = FileTransferCoordinator.AuthorizeAction(
            state,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "fallback_route_publication",
                RuntimeOwner: owner,
                Route: fallback.Route,
                ProtocolVersion: fallback.ProtocolVersion,
                LiveRouteEpoch: leg.LiveRouteEpochId,
                LegGeneration: leg.Generation,
                RuntimeGeneration: owner.RuntimeGeneration,
                TerminalGeneration: state.TerminalGeneration,
                TargetRoute: FileTransferRoute.PostTunaFallbackV6,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
                TargetTransport: FileTransferTransportKind.RegularNkn));

        Assert.True(decision.Accepted);
        Assert.NotNull(decision.RoutePublicationToken);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, decision.RoutePublicationToken!.Value.TargetRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, decision.RoutePublicationToken.Value.TargetProtocolVersion);
        Assert.Equal(FileTransferTransportKind.RegularNkn, decision.RoutePublicationToken.Value.TargetTransport);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void AuthorizeAction_RoutePublication_DiagnosticRegularNknV6RequiresV6RegularNknTarget()
    {
        var diagnostic = FileTransferRouteResolver.Resolve(FileTransferRoute.DiagnosticRegularNknV6);
        var started = StartRegular(diagnostic);
        var owner = started.State.CurrentRuntimeOwner!.Value;
        var leg = started.State.CurrentLeg!;

        FileTransferCoordinatorActionRequest Request(int targetProtocol, FileTransferTransportKind targetTransport)
            => new(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "diagnostic_regular_nkn_v6_route_publication",
                RuntimeOwner: owner,
                Route: diagnostic.Route,
                ProtocolVersion: diagnostic.ProtocolVersion,
                LiveRouteEpoch: leg.LiveRouteEpochId,
                LegGeneration: leg.Generation,
                RuntimeGeneration: owner.RuntimeGeneration,
                TerminalGeneration: started.State.TerminalGeneration,
                TargetRoute: FileTransferRoute.DiagnosticRegularNknV6,
                TargetProtocolVersion: targetProtocol,
                TargetTransport: targetTransport);

        var accepted = FileTransferCoordinator.AuthorizeAction(
            started.State,
            Request(FileTransferProtocol.ProtocolVersionV6, FileTransferTransportKind.RegularNkn));
        var v4Target = FileTransferCoordinator.AuthorizeAction(
            started.State,
            Request(FileTransferProtocol.ProtocolVersionV4, FileTransferTransportKind.RegularNkn));
        var tunaTarget = FileTransferCoordinator.AuthorizeAction(
            started.State,
            Request(FileTransferProtocol.ProtocolVersionV6, FileTransferTransportKind.Tuna));

        Assert.True(accepted.Accepted);
        Assert.NotNull(accepted.RoutePublicationToken);
        Assert.Equal(FileTransferRoute.DiagnosticRegularNknV6, accepted.RoutePublicationToken!.Value.TargetRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, accepted.RoutePublicationToken.Value.TargetProtocolVersion);
        Assert.Equal(FileTransferTransportKind.RegularNkn, accepted.RoutePublicationToken.Value.TargetTransport);
        Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(
            accepted.State,
            accepted.RoutePublicationToken.Value,
            out _));
        Assert.False(v4Target.Accepted);
        Assert.Equal("target_route_protocol_mismatch", v4Target.RejectionReason);
        Assert.False(tunaTarget.Accepted);
        Assert.Equal("route_publication_target_mismatch", tunaTarget.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RoutePublication_RejectsTerminalState()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, regular, reason: "completed"),
            StartRegular(regular).State);

        var decision = FileTransferCoordinator.AuthorizeAction(
            terminal.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "route_publication",
                RuntimeOwner: terminal.State.CurrentRuntimeOwner));

        Assert.False(decision.Accepted);
        Assert.True(decision.TerminalMutationRejected);
        Assert.Equal("terminal_state", decision.RejectionReason);
        Assert.NotNull(decision.RoutePublicationDecision);
        Assert.False(decision.RoutePublicationDecision!.Value.Accepted);
    }

    [Fact]
    public void AuthorizeAction_RoutePublication_RejectsMissingRuntimeOwner()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);

        var decision = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "route_publication"));

        Assert.False(decision.Accepted);
        Assert.Equal("runtime_owner_missing", decision.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RoutePublication_RejectsStaleRuntimeOwner()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var oldOwner = started.State.CurrentRuntimeOwner!.Value;
        var changed = ActivateTuna(started.State, tuna, committedChunk: 3);

        var decision = FileTransferCoordinator.AuthorizeAction(
            changed.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "route_publication",
                RuntimeOwner: oldOwner));

        Assert.False(decision.Accepted);
        Assert.Equal("runtime_generation_mismatch", decision.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RoutePublication_RejectsRouteProtocolLiveEpochAndLegMismatch()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;
        var leg = started.State.CurrentLeg!;

        var routeMismatch = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "route_publication",
                RuntimeOwner: owner,
                Route: FileTransferRoute.FileTunaV4));
        var protocolMismatch = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "route_publication",
                RuntimeOwner: owner,
                ProtocolVersion: FileTransferProtocol.ProtocolVersionV6));
        var liveEpochMismatch = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "route_publication",
                RuntimeOwner: owner,
                LiveRouteEpoch: leg.LiveRouteEpochId + 1));
        var legMismatch = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "route_publication",
                RuntimeOwner: owner,
                LegGeneration: leg.Generation + 1));

        Assert.False(routeMismatch.Accepted);
        Assert.Equal("route_or_protocol_mismatch", routeMismatch.RejectionReason);
        Assert.False(protocolMismatch.Accepted);
        Assert.Equal("route_or_protocol_mismatch", protocolMismatch.RejectionReason);
        Assert.False(liveEpochMismatch.Accepted);
        Assert.Equal("live_route_epoch_mismatch", liveEpochMismatch.RejectionReason);
        Assert.False(legMismatch.Accepted);
        Assert.Equal("leg_generation_mismatch", legMismatch.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RoutePublication_RejectsFileTunaV6Target()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);

        var decision = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "route_publication",
                RuntimeOwner: started.State.CurrentRuntimeOwner,
                TargetRoute: FileTransferRoute.FileTunaV4,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
                TargetTransport: FileTransferTransportKind.Tuna));

        Assert.False(decision.Accepted);
        Assert.Equal("file_tuna_v6_rejected", decision.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RouteCommitPublication_RejectsMissingRouteCommitToken()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);

        var decision = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RouteCommitPublication,
                Source: "test",
                Reason: "route_publication",
                TargetRoute: FileTransferRoute.FileTunaV4,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna));

        Assert.False(decision.Accepted);
        Assert.Equal("route_commit_token_missing", decision.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RouteCommitPublication_AcceptsOnlyFileTunaV4Protocol4Token()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State);
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken));
        var committed = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);
        var token = committed.RouteCommitToken!.Value;

        var accepted = FileTransferCoordinator.AuthorizeAction(
            committed.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RouteCommitPublication,
                Source: "test",
                Reason: "route_publication",
                Route: token.Route,
                ProtocolVersion: token.ProtocolVersion,
                LiveRouteEpoch: token.LiveRouteEpoch,
                LegGeneration: token.LegGeneration,
                RouteCommitToken: token,
                RuntimeUnlockTransactionGeneration: token.TransactionGeneration,
                RuntimeUnlockOfferGeneration: token.OfferGeneration,
                RuntimeUnlockTunaPathLeaseGeneration: token.TunaPathLeaseGeneration,
                RuntimeUnlockProbeId: token.SamePathTunaProbeAckId,
                RuntimeUnlockSamePathTunaProbeAckId: token.SamePathTunaProbeAckId,
                RouteCommitGeneration: token.CommitGeneration,
                TargetRoute: FileTransferRoute.FileTunaV4,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna));
        var rejectedTarget = FileTransferCoordinator.AuthorizeAction(
            committed.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RouteCommitPublication,
                Source: "test",
                Reason: "route_publication",
                Route: token.Route,
                ProtocolVersion: token.ProtocolVersion,
                LiveRouteEpoch: token.LiveRouteEpoch,
                LegGeneration: token.LegGeneration,
                RouteCommitToken: token,
                TargetRoute: FileTransferRoute.RegularNknV4Fast,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna));

        Assert.True(accepted.Accepted);
        Assert.NotNull(accepted.RoutePublicationToken);
        Assert.NotNull(accepted.RoutePublicationDecision);
        Assert.Equal(token.CommitGeneration, accepted.RoutePublicationToken!.Value.RouteCommitGeneration);
        Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(
            accepted.State,
            accepted.RoutePublicationToken.Value,
            out _));
        Assert.False(rejectedTarget.Accepted);
        Assert.Equal("route_commit_metadata_invalid", rejectedTarget.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RouteCommitPublication_RejectsStaleRouteCommitTokenAfterOwnerTerminalOrLiveEpochChange()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var committed = CommitRuntimeUnlockToTuna(StartRegular(regular).State, tuna);
        var token = committed.RouteCommitToken!.Value;
        var ownerChanged = FallBack(committed.State, fallback, committedChunk: 4, transportEpoch: 9).State;
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, tuna, reason: "completed"),
            committed.State).State;
        var liveEpochChanged = committed.State with
        {
            CurrentLiveRouteEpoch = FileTransferCoordinator.StartLiveRouteEpoch(
                committed.State.CurrentLiveRouteEpoch?.EpochId ?? 0,
                committed.State.RouteSelection,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "test_live_epoch_change"),
        };

        var ownerRejected = FileTransferCoordinator.AuthorizeAction(
            ownerChanged,
            RouteCommitPublicationRequest(token));
        var terminalRejected = FileTransferCoordinator.AuthorizeAction(
            terminal,
            RouteCommitPublicationRequest(token));
        var liveEpochRejected = FileTransferCoordinator.AuthorizeAction(
            liveEpochChanged,
            RouteCommitPublicationRequest(token));

        Assert.False(ownerRejected.Accepted);
        Assert.Equal("runtime_generation_mismatch", ownerRejected.RejectionReason);
        Assert.False(terminalRejected.Accepted);
        Assert.Equal("terminal_state", terminalRejected.RejectionReason);
        Assert.False(liveEpochRejected.Accepted);
        Assert.Equal("live_route_epoch_mismatch", liveEpochRejected.RejectionReason);
    }

    [Fact]
    public void IsRoutePublicationTokenCurrent_RejectsAfterTerminalization()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var publication = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "route_publication",
                RuntimeOwner: started.State.CurrentRuntimeOwner));
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, regular, reason: "completed"),
            publication.State);

        var isCurrent = FileTransferCoordinator.IsRoutePublicationTokenCurrent(
            terminal.State,
            publication.RoutePublicationToken!.Value,
            out var rejectionReason);

        Assert.False(isCurrent);
        Assert.Equal("transfer_terminal", rejectionReason);
    }

    [Fact]
    public void IsRoutePublicationTokenCurrent_RejectsAfterRouteTransitionOrNewLeg()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var publication = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "route_publication",
                RuntimeOwner: started.State.CurrentRuntimeOwner));
        var transitioned = ActivateTuna(publication.State, tuna, committedChunk: 3);

        var isCurrent = FileTransferCoordinator.IsRoutePublicationTokenCurrent(
            transitioned.State,
            publication.RoutePublicationToken!.Value,
            out var rejectionReason);

        Assert.False(isCurrent);
        Assert.Equal("route_publication_generation_mismatch", rejectionReason);
    }

    [Fact]
    public void Apply_Terminalized_InvalidatesRoutePublicationToken()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var publication = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "route_publication",
                RuntimeOwner: started.State.CurrentRuntimeOwner));
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, regular, reason: "completed"),
            publication.State);

        Assert.Null(terminal.State.CurrentRoutePublicationToken);
        Assert.False(FileTransferCoordinator.IsRoutePublicationTokenCurrent(
            terminal.State,
            publication.RoutePublicationToken!.Value,
            out _));
    }

    [Fact]
    public void RuntimeUnlockTunaPathLease_StaleListenerReadyProofIsIgnored()
    {
        var lease = RuntimeUnlockTunaPathLease.Start(
            "session_a",
            transferId: "transfer_a",
            leaseGeneration: 5,
            listenerRunId: "listener-start-5",
            payerDecisionId: 1,
            nowUtcMs: 100);
        lease = RuntimeUnlockTunaPathLease.Fail(lease, "listener_sidecar_unavailable", nowUtcMs: 101);

        var marked = RuntimeUnlockTunaPathLease.TryMarkListenerReady(
            lease,
            "session_a",
            leaseGeneration: 4,
            listenerRunId: "listener-start-4",
            nowUtcMs: 102,
            out var updated);

        Assert.False(marked);
        Assert.Equal(RuntimeUnlockTunaPathLeaseState.Failed, updated.State);
        Assert.False(updated.IsCurrent);
    }

    [Fact]
    public void RuntimeUnlockTransaction_StaleSessionOrTerminalTransferRejectsCommit()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var staleProof = new RuntimeUnlockRouteCommitProof(
            "different_session",
            started.State.TransferId,
            TransactionGeneration: 1,
            OfferGeneration: 1,
            PeerVisibleProof: true,
            PeerReceived: true,
            AnswerReceived: false,
            FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            RuntimeUnlockTransactionState.PeerReceived,
            "test");

        var stale = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: staleProof),
            started.State);

        Assert.False(stale.RuntimeUnlockCommitAccepted);
        Assert.Equal("session_mismatch", stale.RuntimeUnlockCommitRejectedReason);

        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, regular, reason: "completed"),
            started.State);
        var proof = staleProof with { SessionId = started.State.SessionId };
        var terminalRejected = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            terminal.State);

        Assert.False(terminalRejected.TerminalMutationRejected);
        Assert.False(terminalRejected.RuntimeUnlockCommitAccepted);
        Assert.Equal("transfer_terminal", terminalRejected.RuntimeUnlockCommitRejectedReason);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, terminalRejected.State.RouteSelection.Route);
    }

    [Fact]
    public void FallbackV6Checkpoint_AcceptanceEnablesCurrentGenerationOnly()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var activated = ActivateTuna(
            StartRegular(regular).State,
            tuna,
            committedChunk: 9);
        var fallbackDecision = FallBack(
            activated.State,
            fallback,
            committedChunk: 9,
            transportEpoch: 3);

        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, fallbackDecision.State.RouteSelection.Route);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, fallbackDecision.State.CurrentLeg!.ProtocolVersion);
        Assert.False(fallbackDecision.State.CurrentLeg.CanSendData);
        Assert.Equal(FileTransferLegState.CheckpointPending, fallbackDecision.State.CurrentLeg.State);
        Assert.True(fallbackDecision.FallbackCheckpointRequired);

        var requested = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointRequested,
                fallback,
                reason: "checkpoint",
                state: FileTransferLegState.CheckpointPending,
                canSendData: false,
                transportEpoch: 3,
                checkpointRequestId: "checkpoint:1",
                checkpointPriority: "state_refresh"),
            fallbackDecision.State);
        Assert.True(FileTransferCoordinator.IsCurrentPostTunaFallbackLegCheckpointPending(requested.State.CurrentLeg));

        var accepted = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointAccepted,
                fallback,
                reason: "receiver_state",
                state: FileTransferLegState.RecoveryActive,
                canSendData: true,
                committedChunk: 12,
                highestObservedChunk: 18,
                transportEpoch: 3),
            requested.State);

        Assert.NotNull(accepted.AcceptedCheckpointLeg);
        Assert.True(accepted.State.CurrentLeg!.CanSendData);
        Assert.Equal(FileTransferLegState.RecoveryActive, accepted.State.CurrentLeg.State);
        Assert.Equal(12, accepted.State.CurrentLeg.ProvenCommittedChunkIndex);
        Assert.Equal(18, accepted.State.CurrentLeg.ProvenHighestObservedChunkIndex);
        Assert.Null(accepted.State.CurrentLeg.CheckpointRequestId);
    }

    [Fact]
    public void FallbackV6Checkpoint_RequiresCurrentRequestAndTransportEpoch()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var activated = ActivateTuna(
            StartRegular(regular).State,
            tuna,
            committedChunk: 9);
        var fallbackDecision = FallBack(activated.State, fallback, committedChunk: 9, transportEpoch: 4);
        var requested = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointRequested,
                fallback,
                reason: "checkpoint",
                state: FileTransferLegState.CheckpointPending,
                canSendData: false,
                transportEpoch: 4,
                checkpointRequestId: "checkpoint:4",
                checkpointPriority: "state_refresh"),
            fallbackDecision.State);
        var leg = requested.State.CurrentLeg;

        Assert.False(FileTransferCoordinator.TryValidateCurrentFallbackCheckpointProof(
            leg,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            requested.State.CurrentLiveRouteEpoch?.EpochId ?? 0,
            proofTransportEpochId: 4,
            proofCheckpointRequestId: null,
            out var missingRequestReason));
        Assert.Equal("checkpoint_request_missing_proof", missingRequestReason);

        Assert.False(FileTransferCoordinator.TryValidateCurrentFallbackCheckpointProof(
            leg,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            requested.State.CurrentLiveRouteEpoch?.EpochId ?? 0,
            proofTransportEpochId: 5,
            proofCheckpointRequestId: "checkpoint:4",
            out var transportMismatchReason));
        Assert.Equal("transport_epoch_mismatch", transportMismatchReason);

        Assert.False(FileTransferCoordinator.TryValidateCurrentFallbackCheckpointProof(
            leg,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            requested.State.CurrentLiveRouteEpoch?.EpochId ?? 0,
            proofTransportEpochId: 4,
            proofCheckpointRequestId: "checkpoint:old",
            out var checkpointMismatchReason));
        Assert.Equal("checkpoint_request_mismatch", checkpointMismatchReason);

        Assert.True(FileTransferCoordinator.TryValidateCurrentFallbackCheckpointProof(
            leg,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            requested.State.CurrentLiveRouteEpoch?.EpochId ?? 0,
            proofTransportEpochId: 4,
            proofCheckpointRequestId: "checkpoint:4",
            out var acceptedReason));
        Assert.Equal("ok", acceptedReason);
    }

    [Fact]
    public void OffOnOffRouteCycle_UsesStrictlyIncreasingEpochsAndNeverFileTunaV6()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);

        var state = StartRegular(regular).State;
        var firstOn = ActivateTuna(state, tuna, committedChunk: 5);
        var firstOff = FallBack(firstOn.State, fallback, committedChunk: 5, transportEpoch: 2);
        var secondOn = ActivateTuna(firstOff.State, tuna, committedChunk: 6);
        var secondOff = FallBack(secondOn.State, fallback, committedChunk: 6, transportEpoch: 3);

        Assert.Equal(1, firstOn.StartedLiveRouteEpoch!.EpochId);
        Assert.Equal(2, firstOff.StartedLiveRouteEpoch!.EpochId);
        Assert.Equal(3, secondOn.StartedLiveRouteEpoch!.EpochId);
        Assert.Equal(4, secondOff.StartedLiveRouteEpoch!.EpochId);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, secondOff.State.RouteSelection.Route);
        Assert.All(
            secondOff.State.LegHistory,
            leg => Assert.NotEqual("file_tuna_v6", leg.RouteSelection.TelemetryToken));
    }

    [Fact]
    public void PostTunaFallbackRecoveryActive_DeniesRuntimeUnlockAdmission()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);

        var state = FallBack(
            ActivateTuna(StartRegular(regular).State, tuna, committedChunk: 5).State,
            fallback,
            committedChunk: 5,
            transportEpoch: 2).State;
        state.CurrentLeg!.State = FileTransferLegState.RecoveryActive;
        state.CurrentLeg.CanSendData = true;
        state.CurrentLeg.CheckpointRequestId = null;

        var admitted = FileTransferCoordinator.CanAdmitRuntimeUnlockWhilePostTunaFallback(
            routeUsesPostTunaFallbackRuntime: true,
            state.CurrentLeg,
            checkpointDeliveryRecoveryPending: false,
            stateRefreshSendInFlight: false,
            deferredStateRefreshPending: false,
            proofReplayPending: false,
            out var rejectionReason);

        Assert.False(admitted);
        Assert.Equal("recovery_active", rejectionReason);
    }

    [Fact]
    public void RuntimeUnlockAdmissionDecision_ReportsFallbackOwnerAndTerminalReasons()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);

        var state = FallBack(
            ActivateTuna(StartRegular(regular).State, tuna, committedChunk: 5).State,
            fallback,
            committedChunk: 5,
            transportEpoch: 2).State;
        state.CurrentLeg!.State = FileTransferLegState.Active;
        state.CurrentLeg.CanSendData = true;

        var blocked = FileTransferCoordinator.EvaluateRuntimeUnlockAdmission(
            isTerminal: false,
            runtimeOwnerCurrent: true,
            routeUsesPostTunaFallbackRuntime: true,
            state.CurrentLeg,
            checkpointDeliveryRecoveryPending: false,
            stateRefreshSendInFlight: false,
            deferredStateRefreshPending: false,
            proofReplayPending: false,
            bridgeRecoveryPending: false,
            controlPlanePressurePending: false,
            fallbackTailRepairPending: true,
            transactionFailed: false);

        Assert.Equal(RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner, blocked.Status);
        Assert.Equal("fallback_tail_repair_pending", blocked.Reason);

        var missingPairedYield = FileTransferCoordinator.EvaluateRuntimeUnlockAdmission(
            isTerminal: false,
            runtimeOwnerCurrent: true,
            routeUsesPostTunaFallbackRuntime: true,
            state.CurrentLeg,
            checkpointDeliveryRecoveryPending: false,
            stateRefreshSendInFlight: false,
            deferredStateRefreshPending: false,
            proofReplayPending: false,
            bridgeRecoveryPending: false,
            controlPlanePressurePending: false,
            fallbackTailRepairPending: false,
            transactionFailed: false);

        Assert.Equal(RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner, missingPairedYield.Status);
        Assert.Equal("paired_fallback_tail_yield_missing", missingPairedYield.Reason);

        var pairedYield = FileTransferCoordinator.EvaluateRuntimeUnlockAdmission(
            isTerminal: false,
            runtimeOwnerCurrent: true,
            routeUsesPostTunaFallbackRuntime: true,
            state.CurrentLeg,
            checkpointDeliveryRecoveryPending: false,
            stateRefreshSendInFlight: false,
            deferredStateRefreshPending: false,
            proofReplayPending: false,
            bridgeRecoveryPending: false,
            controlPlanePressurePending: false,
            fallbackTailRepairPending: false,
            transactionFailed: false,
            pairedFallbackYieldGranted: true);

        Assert.Equal(RuntimeUnlockAdmissionStatus.Granted, pairedYield.Status);
        Assert.Equal("fallback_quiescent", pairedYield.Reason);

        var terminal = FileTransferCoordinator.EvaluateRuntimeUnlockAdmission(
            isTerminal: true,
            runtimeOwnerCurrent: true,
            routeUsesPostTunaFallbackRuntime: true,
            state.CurrentLeg,
            checkpointDeliveryRecoveryPending: false,
            stateRefreshSendInFlight: false,
            deferredStateRefreshPending: false,
            proofReplayPending: false,
            bridgeRecoveryPending: false,
            controlPlanePressurePending: false,
            fallbackTailRepairPending: false,
            transactionFailed: false);

        Assert.Equal(RuntimeUnlockAdmissionStatus.RejectedTerminal, terminal.Status);
        Assert.Equal("transfer_terminal", terminal.Reason);
    }

    [Fact]
    public void RuntimeUnlockAdmission_AcceptsCurrentOwnerWithoutFallbackBlocker()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = StartRegular(regular).State;

        var decision = RequestRuntimeUnlockAdmission(state, state.CurrentRuntimeOwner!);

        var admission = decision.RuntimeUnlockAdmissionDecision!.Value;
        Assert.True(admission.Granted);
        Assert.Equal("not_post_tuna_fallback", admission.Reason);
        Assert.NotNull(admission.AdmissionToken);
        Assert.Equal(state.CurrentRuntimeOwner!.Value.RuntimeGeneration, admission.AdmissionToken!.Value.RuntimeGeneration);
    }

    [Fact]
    public void RuntimeUnlockAdmission_RejectsTerminalState()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = StartRegular(regular).State;
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, regular, reason: "complete"),
            state).State;

        var decision = RequestRuntimeUnlockAdmission(terminal, terminal.CurrentRuntimeOwner);

        Assert.False(decision.RuntimeUnlockAdmissionDecision!.Value.Granted);
        Assert.Equal(RuntimeUnlockAdmissionStatus.RejectedTerminal, decision.RuntimeUnlockAdmissionDecision!.Value.Status);
        Assert.Equal("transfer_terminal", decision.RuntimeUnlockAdmissionDecision!.Value.Reason);
    }

    [Fact]
    public void RuntimeUnlockAdmission_RejectsStaleOwner()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var regularState = StartRegular(regular).State;
        var oldOwner = regularState.CurrentRuntimeOwner!.Value;
        var tunaState = ActivateTuna(regularState, tuna, committedChunk: 4).State;

        var decision = RequestRuntimeUnlockAdmission(tunaState, oldOwner);

        Assert.False(decision.RuntimeUnlockAdmissionDecision!.Value.Granted);
        Assert.Equal(RuntimeUnlockAdmissionStatus.RejectedStaleOwner, decision.RuntimeUnlockAdmissionDecision!.Value.Status);
        Assert.Equal("runtime_generation_mismatch", decision.RuntimeUnlockAdmissionDecision!.Value.Reason);
    }

    [Fact]
    public void RuntimeUnlockAdmission_RejectsActiveFallbackWithoutYield()
    {
        var fallbackState = ActiveFallbackStateWithoutCheckpoint();

        var decision = RequestRuntimeUnlockAdmission(
            fallbackState,
            fallbackState.CurrentRuntimeOwner!,
            new RuntimeUnlockFallbackSurvivalState(
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
                PairedFallbackYieldGranted: false));

        Assert.False(decision.RuntimeUnlockAdmissionDecision!.Value.Granted);
        Assert.Equal("paired_fallback_tail_yield_missing", decision.RuntimeUnlockAdmissionDecision!.Value.Reason);
    }

    [Fact]
    public void RuntimeUnlockAdmission_AcceptsAfterFallbackTerminalized()
    {
        var fallbackState = ActiveFallbackStateWithoutCheckpoint();
        fallbackState.CurrentLeg!.State = FileTransferLegState.Terminal;
        fallbackState.CurrentLeg.CanSendData = false;

        var decision = RequestRuntimeUnlockAdmission(
            fallbackState,
            fallbackState.CurrentRuntimeOwner!,
            new RuntimeUnlockFallbackSurvivalState(
                FallbackActive: true,
                FallbackCurrent: true,
                FallbackTerminalized: true,
                CheckpointDeliveryRecoveryPending: false,
                StateRefreshSendInFlight: false,
                DeferredStateRefreshPending: false,
                ProofReplayPending: false,
                ControlPlanePressurePending: false,
                BridgeRecoveryPending: false,
                FallbackTailRepairPending: false,
                PairedFallbackYieldGranted: false));

        Assert.True(decision.RuntimeUnlockAdmissionDecision!.Value.Granted);
        Assert.Equal("fallback_terminalized", decision.RuntimeUnlockAdmissionDecision!.Value.Reason);
    }

    [Fact]
    public void RuntimeUnlockAdmission_AcceptsAfterPairedYield()
    {
        var fallbackState = ActiveFallbackStateWithoutCheckpoint();

        var decision = RequestRuntimeUnlockAdmission(
            fallbackState,
            fallbackState.CurrentRuntimeOwner!,
            new RuntimeUnlockFallbackSurvivalState(
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
                PairedFallbackYieldGranted: true));

        Assert.True(decision.RuntimeUnlockAdmissionDecision!.Value.Granted);
        Assert.Equal("fallback_quiescent", decision.RuntimeUnlockAdmissionDecision!.Value.Reason);
    }

    [Fact]
    public void RuntimeUnlockAdmission_TokenCarriesCoordinatorAuthorityFields()
    {
        var state = ActiveFallbackStateWithoutCheckpoint();
        var survival = new RuntimeUnlockFallbackSurvivalState(
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
            PairedFallbackYieldGranted: true);

        var decision = RequestRuntimeUnlockAdmission(state, state.CurrentRuntimeOwner!, survival);
        var token = decision.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;

        Assert.Equal(state.CurrentRuntimeOwner, token.RuntimeOwner);
        Assert.Equal(state.TerminalGeneration, token.TerminalGeneration);
        Assert.Equal(state.CurrentLiveRouteEpoch!.EpochId, token.LiveRouteEpoch);
        Assert.Equal(state.CurrentLeg!.Generation, token.LegGeneration);
        Assert.Equal(state.CurrentRuntimeOwner!.Value.RuntimeGeneration, token.RuntimeGeneration);
        Assert.Equal(survival, token.FallbackSurvivalState);
    }

    [Fact]
    public void RuntimeUnlockAdmission_TokenBecomesStaleAfterRouteOwnerOrTerminalChange()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var state = StartRegular(regular).State;
        var admitted = RequestRuntimeUnlockAdmission(state, state.CurrentRuntimeOwner!).State;
        var token = admitted.CurrentRuntimeUnlockAdmission!.Value;

        var tunaState = ActivateTuna(admitted, tuna, committedChunk: 3).State;
        Assert.False(FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(tunaState, token, out var routeReason));
        Assert.Equal("runtime_unlock_admission_generation_mismatch", routeReason);

        var terminalState = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, regular, reason: "complete"),
            admitted).State;
        Assert.False(FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(terminalState, token, out var terminalReason));
        Assert.Equal("runtime_unlock_admission_generation_mismatch", terminalReason);
    }

    [Fact]
    public void TerminalStateRejectsLaterRouteMutation()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, regular, reason: "completed"),
            StartRegular(regular).State);

        var rejected = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaActivated,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "late_activation",
                FileTransferLegState.Active,
                canSendData: true),
            terminal.State);

        Assert.True(rejected.TerminalMutationRejected);
        Assert.Equal("terminal_state", rejected.MutationRejectedReason);
        Assert.True(rejected.State.IsTerminal);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, rejected.State.RouteSelection.Route);
        Assert.Equal(FileTransferLegState.Terminal, rejected.State.CurrentLeg!.State);
    }

    [Fact]
    public void Apply_Terminalized_IsFirstValidTerminalWins()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);

        var first = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "completed"),
            started.State);
        var second = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "late_failed"),
            first.State);

        Assert.False(first.TerminalMutationRejected);
        Assert.True(first.State.IsTerminal);
        Assert.Equal(1, first.State.TerminalGeneration);
        Assert.Equal(FileTransferLegState.Terminal, first.State.CurrentLeg!.State);
        Assert.True(second.TerminalMutationRejected);
        Assert.Equal("terminal_state", second.MutationRejectedReason);
        Assert.Equal(1, second.State.TerminalGeneration);
        Assert.Equal(FileTransferLegState.Terminal, second.State.CurrentLeg!.State);
    }

    [Fact]
    public void Apply_StartTransferAfterTerminal_DoesNotStartLeg()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "completed"),
            StartRegular(regular).State);
        var previousLegGeneration = terminal.State.LastTransferLegGeneration;
        var previousHistoryCount = terminal.State.LegHistory.Count;

        var rejected = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                regular,
                reason: "late_start",
                state: FileTransferLegState.Active,
                canSendData: true),
            terminal.State);

        Assert.True(rejected.TerminalMutationRejected);
        Assert.Equal("terminal_state", rejected.MutationRejectedReason);
        Assert.Null(rejected.StartedLeg);
        Assert.Equal(previousLegGeneration, rejected.State.LastTransferLegGeneration);
        Assert.Equal(previousHistoryCount, rejected.State.LegHistory.Count);
    }

    [Fact]
    public void Apply_SecondTerminalizedAfterTerminal_DoesNotIncrementTerminalGeneration()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var first = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "completed"),
            StartRegular(regular).State);

        var second = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "late_failed"),
            first.State);

        Assert.True(second.TerminalMutationRejected);
        Assert.Equal("terminal_state", second.MutationRejectedReason);
        Assert.Equal(first.State.TerminalGeneration, second.State.TerminalGeneration);
        Assert.Equal(1, second.State.TerminalGeneration);
    }

    [Fact]
    public void Apply_StartTransfer_FreezesPriorLegOnlyWhenAccepted()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var previousLeg = started.State.CurrentLeg!;

        var next = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                regular,
                reason: "restart_leg",
                state: FileTransferLegState.Active,
                canSendData: true),
            started.State);

        Assert.NotNull(next.StartedLeg);
        Assert.NotSame(previousLeg, next.FrozenLeg);
        Assert.Equal(FileTransferLegState.Active, previousLeg.State);
        Assert.True(previousLeg.CanSendData);
        Assert.Equal(FileTransferLegState.Frozen, next.FrozenLeg!.State);
        Assert.False(next.FrozenLeg.CanSendData);
        Assert.Same(next.FrozenLeg, next.State.LegHistory[0]);
        Assert.Equal(2, next.State.LegHistory.Count);
        Assert.Equal(2, next.State.LastTransferLegGeneration);
    }

    [Fact]
    public void Apply_FallbackCheckpointRequested_RejectedWhenLegNotCurrent()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);

        var rejected = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointRequested,
                regular,
                reason: "late_checkpoint",
                state: FileTransferLegState.CheckpointPending,
                canSendData: false,
                transportEpoch: 1,
                checkpointRequestId: "checkpoint:late"),
            started.State);

        Assert.False(rejected.TerminalMutationRejected);
        Assert.Equal("fallback_leg_not_current", rejected.MutationRejectedReason);
        Assert.Null(rejected.MutatedLeg);
        Assert.Equal(FileTransferLegState.Active, rejected.State.CurrentLeg!.State);
    }

    [Fact]
    public void Apply_FallbackCheckpointAccepted_RejectedWhenCheckpointMismatches()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var fallbackDecision = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4);
        var requested = RequestFallbackCheckpoint(fallbackDecision.State, fallback, "checkpoint:4", 4);

        var rejected = AcceptFallbackCheckpoint(requested.State, fallback, "checkpoint:old", 4, 12, 18);

        Assert.False(rejected.TerminalMutationRejected);
        Assert.Equal("checkpoint_request_mismatch", rejected.MutationRejectedReason);
        Assert.Null(rejected.AcceptedCheckpointLeg);
        Assert.Equal("checkpoint:4", rejected.State.CurrentLeg!.CheckpointRequestId);
        Assert.False(rejected.State.CurrentLeg.CanSendData);
    }

    [Fact]
    public void Apply_FallbackCheckpointRetired_MutatesOnlyCurrentFallbackLeg()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var fallbackDecision = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4);
        var requested = RequestFallbackCheckpoint(fallbackDecision.State, fallback, "checkpoint:4", 4);

        var retired = RetireFallbackCheckpoint(requested.State, fallback, "checkpoint:4", 4);

        Assert.Null(retired.MutationRejectedReason);
        Assert.Same(retired.State.CurrentLeg, retired.MutatedLeg);
        Assert.Null(retired.State.CurrentLeg!.CheckpointRequestId);
        Assert.True(retired.State.CurrentLeg.CanSendData);
        Assert.Equal(FileTransferLegState.RecoveryActive, retired.State.CurrentLeg.State);
    }

    [Fact]
    public void Apply_FallbackTailFailed_MutatesOnlyCurrentFallbackLeg()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var fallbackDecision = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4);
        var requested = RequestFallbackCheckpoint(fallbackDecision.State, fallback, "checkpoint:4", 4);
        var accepted = AcceptFallbackCheckpoint(requested.State, fallback, "checkpoint:4", 4, 12, 18);

        var failed = MarkFallbackTailFailed(accepted.State, fallback, 4);

        Assert.Null(failed.MutationRejectedReason);
        Assert.Same(failed.State.CurrentLeg, failed.MutatedLeg);
        Assert.Equal(FileTransferLegState.RecoveryActive, failed.State.CurrentLeg!.State);
        Assert.True(failed.State.CurrentLeg.CanSendData);
    }

    [Fact]
    public void Apply_TerminalStateRejectsFallbackLegMutations()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var fallbackDecision = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4);
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, fallback, reason: "completed"),
            fallbackDecision.State);

        var rejected = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailQuiescent,
                fallback,
                reason: "late_tail",
                state: FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 12,
                highestObservedChunk: 18,
                transportEpoch: 4),
            terminal.State);

        Assert.True(rejected.TerminalMutationRejected);
        Assert.Equal("terminal_state", rejected.MutationRejectedReason);
        Assert.Equal(terminal.State.TerminalGeneration, rejected.State.TerminalGeneration);
        Assert.Equal(FileTransferLegState.Terminal, rejected.State.CurrentLeg!.State);
    }

    [Fact]
    public void FileTransferCoordinatorSnapshot_CheckpointAndTerminalTransitionsDoNotMutatePriorGraphs()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var fallbackDecision = FallBack(
            StartRegular(regular).State,
            fallback,
            committedChunk: 8,
            transportEpoch: 4);
        var sourceState = fallbackDecision.State;
        var sourceLeg = Assert.IsType<FileTransferLeg>(sourceState.CurrentLeg);
        var sourceEpoch = Assert.IsType<LiveRouteEpoch>(sourceState.CurrentLiveRouteEpoch);
        var sourceLegState = sourceLeg.State;
        var sourceCheckpointGeneration = sourceLeg.CheckpointGeneration;

        var checkpoint = RequestFallbackCheckpoint(
            sourceState,
            fallback,
            "checkpoint:snapshot",
            transportEpoch: 4);

        Assert.Null(checkpoint.MutationRejectedReason);
        Assert.Equal(sourceLegState, sourceLeg.State);
        Assert.Equal(sourceCheckpointGeneration, sourceLeg.CheckpointGeneration);
        Assert.Null(sourceLeg.CheckpointRequestId);
        Assert.NotSame(sourceLeg, checkpoint.State.CurrentLeg);
        Assert.NotSame(sourceEpoch, checkpoint.State.CurrentLiveRouteEpoch);
        Assert.Equal(sourceEpoch.State, checkpoint.State.CurrentLiveRouteEpoch!.State);
        Assert.Same(checkpoint.State.CurrentLeg, checkpoint.State.LegHistory[^1]);
        Assert.Equal("checkpoint:snapshot", checkpoint.State.CurrentLeg!.CheckpointRequestId);

        var checkpointLeg = checkpoint.State.CurrentLeg;
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                fallback,
                reason: "snapshot_terminal"),
            checkpoint.State);

        Assert.True(terminal.State.IsTerminal);
        Assert.Equal(FileTransferLegState.CheckpointPending, checkpointLeg.State);
        Assert.NotSame(checkpointLeg, terminal.State.CurrentLeg);
        Assert.Equal(FileTransferLegState.Terminal, terminal.State.CurrentLeg!.State);
        Assert.Same(terminal.State.CurrentLeg, terminal.State.LegHistory[^1]);

        var rejected = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointAccepted,
                fallback,
                reason: "snapshot_stale",
                transportEpoch: 4,
                checkpointRequestId: "checkpoint:other"),
            checkpoint.State);
        Assert.NotNull(rejected.MutationRejectedReason);
        Assert.Same(checkpoint.State, rejected.State);
        Assert.Same(checkpointLeg, rejected.State.CurrentLeg);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FileTransferRouteTransition")]
    public void FileTransferRouteTransition_GenericRegularV4ProjectionHasOnePersistentOperation()
    {
        var setup = CreateRegularV4ToTunaRouteTransitionSetup();

        var prepared =
            FileTransferCoordinator.PrepareFileTransferRouteTransition(
                setup.State,
                setup.Request);

        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Prepared, prepared.Status);
        var generic = Assert.IsType<FileTransferRouteTransitionOperation>(
            FileTransferCoordinator.GetFileTransferRouteTransitionSubstate(prepared.State)
                .CurrentOperation);
        Assert.Equal(
            FileTransferRouteTransitionKind.RegularV4ToTunaV4,
            generic.Authority.Kind);

        var duplicate =
            FileTransferCoordinator.PrepareFileTransferRouteTransition(
                prepared.State,
                setup.Request);
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Idempotent, duplicate.Status);
        Assert.Same(prepared.State, duplicate.State);

        var draining =
            FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(
                prepared.State,
                generic.Authority,
                new FileTransferRouteTransitionPauseBindReceipt(
                    prepared.PauseBindCommand!.Value,
                    FileTransferRouteTransitionPauseBindStatus.Installed,
                    "filetransfer_route_transition_pause_bound"),
                capturedThroughPhysicalSendGeneration: 0,
                capturedPhysicalSendLeaseIds: [],
                FileTransferCoordinator
                    .CreateFileTransferRouteTransitionPhysicalDrainFingerprint([]));
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Draining, draining.Status);
        Assert.True(
            FileTransferCoordinator.IsFileTransferRouteTransitionDraining(
                draining.State,
                out var drainingAuthority));
        Assert.Equal(generic.Authority, drainingAuthority);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FileTransferRouteTransition")]
    public void FileTransferRouteTransition_TunaFallbackCommitsAndAppliesRetainedFrameOnce()
    {
        var setup = CreateRegularV4ToTunaRouteTransitionSetup();
        var activationPrepared =
            FileTransferCoordinator.PrepareFileTransferRouteTransition(
                setup.State,
                setup.Request);
        var activationAuthority = activationPrepared.Authority!.Value;
        var emptyFingerprint =
            FileTransferCoordinator
                .CreateFileTransferRouteTransitionPhysicalDrainFingerprint([]);
        var activationDraining =
            FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(
                activationPrepared.State,
                activationAuthority,
                new FileTransferRouteTransitionPauseBindReceipt(
                    activationPrepared.PauseBindCommand!.Value,
                    FileTransferRouteTransitionPauseBindStatus.Installed,
                    "filetransfer_route_transition_pause_bound"),
                capturedThroughPhysicalSendGeneration: 0,
                capturedPhysicalSendLeaseIds: [],
                emptyFingerprint);
        var activationDrain = activationDraining.DrainCommand!.Value;
        var activationCommitted =
            FileTransferCoordinator.CommitFileTransferRouteTransition(
                activationDraining.State,
                activationAuthority,
                new FileTransferRouteTransitionPhysicalDrainReceipt(
                    activationDrain,
                    activationDrain.CapturedPhysicalSendLeaseIds,
                    activationDrain.CapturedPhysicalSendCount,
                    activationDrain.CapturedPhysicalSendFingerprint,
                    FileTransferRouteTransitionPhysicalDrainOutcome.Completed,
                    "physical_drain_completed"),
                setup.FinalProof);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Committed,
            activationCommitted.Status);
        Assert.Equal(
            FileTransferRoute.FileTunaV4,
            activationCommitted.State.RouteSelection.Route);
        Assert.Equal(
            "recovered",
            activationCommitted.State.CurrentLiveRouteEpoch!.State);

        var activationDisposition =
            Assert.IsType<FileTransferRouteTransitionDispositionCommand>(
                activationCommitted.DispositionCommand);
        var tunaState = FileTransferCoordinator
            .ApplyFileTransferRouteTransitionDispositionReceipt(
                activationCommitted.State,
                new FileTransferRouteTransitionDispositionReceipt(
                    activationDisposition,
                    FileTransferRouteTransitionDispositionApplyStatus.Applied,
                    "filetransfer_route_transition_committed"))
            .State;
        var tunaOwner = tunaState.CurrentRuntimeOwner!.Value;
        var tunaPublication = tunaState.CurrentRoutePublicationToken!.Value;
        var tunaCommit = tunaState.CurrentRouteCommit!.Value;
        var tunaLeg = tunaState.CurrentLeg!;
        var targetEpoch = Math.Max(1, tunaLeg.TransportEpochId + 1);
        var frameIdentity = new PostTunaFallbackPeerProofFrameIdentity(
            "filetransfer_receiver_state_v6",
            targetEpoch,
            new string('a', 64));
        var peerProof = new PostTunaFallbackPeerProofEvidence(
            tunaState.SessionId,
            tunaState.TransferId,
            tunaState.Direction,
            ProofGeneration: 1,
            TransportEpoch: targetEpoch,
            ProofKind: "fallback_peer_state",
            Reason: "peer_fallback_proof",
            FrameIdentity: frameIdentity);
        var fallbackBoundary = new FileTransferFallbackTransitionProofBoundary(
            tunaCommit,
            peerProof,
            frameIdentity,
            targetEpoch,
            TargetBridgeRecoveryGeneration:
                Math.Max(1, tunaLeg.BridgeRecoveryGeneration + 1));
        var fallbackCheckpoint = new FileTransferRouteTransitionDurableCheckpoint(
            tunaOwner,
            tunaPublication,
            tunaState.TerminalGeneration,
            tunaLeg.Generation,
            tunaLeg.TransportEpochId,
            tunaLeg.BridgeRecoveryGeneration,
            DataSessionGeneration: 17,
            StateEpoch: 4,
            ContiguousCommittedChunkIndex: 4,
            DurableHighestChunkIndex: 6,
            ChunkCount: 128,
            CheckpointGeneration: 2);
        var fallbackRequest = new FileTransferRouteTransitionPrepareRequest(
            FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6,
            tunaOwner,
            tunaPublication,
            tunaCommit,
            FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportKind.Tuna,
            fallbackBoundary,
            fallbackCheckpoint,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            FileTransferTransportKind.RegularNkn,
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            PredecessorTransitionAuthorityId: null,
            "peer_fallback_proof");
        var fallbackPrepared =
            FileTransferCoordinator.PrepareFileTransferRouteTransition(
                tunaState,
                fallbackRequest);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Prepared,
            fallbackPrepared.Status);
        Assert.Equal(tunaOwner, fallbackPrepared.State.CurrentRuntimeOwner);
        Assert.Equal(tunaPublication, fallbackPrepared.State.CurrentRoutePublicationToken);

        var fallbackAuthority = fallbackPrepared.Authority!.Value;
        var fallbackDraining =
            FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(
                fallbackPrepared.State,
                fallbackAuthority,
                new FileTransferRouteTransitionPauseBindReceipt(
                    fallbackPrepared.PauseBindCommand!.Value,
                    FileTransferRouteTransitionPauseBindStatus.Installed,
                    "filetransfer_route_transition_pause_bound"),
                capturedThroughPhysicalSendGeneration: 0,
                capturedPhysicalSendLeaseIds: [],
                emptyFingerprint);
        var fallbackDrain = fallbackDraining.DrainCommand!.Value;
        var fallbackReceipt = new FileTransferRouteTransitionPhysicalDrainReceipt(
            fallbackDrain,
            fallbackDrain.CapturedPhysicalSendLeaseIds,
            fallbackDrain.CapturedPhysicalSendCount,
            fallbackDrain.CapturedPhysicalSendFingerprint,
            FileTransferRouteTransitionPhysicalDrainOutcome.Completed,
            "physical_drain_completed");
        var fallbackCommitted =
            FileTransferCoordinator.CommitFileTransferRouteTransition(
                fallbackDraining.State,
                fallbackAuthority,
                fallbackReceipt);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Committed,
            fallbackCommitted.Status);
        Assert.Equal(
            FileTransferRoute.PostTunaFallbackV6,
            fallbackCommitted.State.RouteSelection.Route);
        Assert.Equal(
            "started",
            fallbackCommitted.State.CurrentLiveRouteEpoch!.State);
        Assert.Null(fallbackCommitted.State.CurrentRouteCommit);
        Assert.True(
            FileTransferCoordinator.IsFileTransferRouteTransitionDispositionCommandValid(
                fallbackCommitted.DispositionCommand!.Value));
        var frameCommand = Assert.IsType<
            FileTransferRouteTransitionRetainedFrameApplyCommand>(
            fallbackCommitted.RetainedFrameApplyCommand);
        Assert.True(
            FileTransferCoordinator
                .IsFileTransferRouteTransitionRetainedFrameApplyCommandValid(
                    frameCommand));

        var applied = FileTransferCoordinator.ApplyFileTransferRouteTransitionRetainedFrameReceipt(
            fallbackCommitted.State,
            new FileTransferRouteTransitionRetainedFrameApplyReceipt(
                frameCommand,
                FileTransferRouteTransitionRetainedFrameApplyStatus.Applied,
                "filetransfer_route_transition_retained_frame_applied"));
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Committed, applied.Status);
        var duplicateCommit = FileTransferCoordinator.CommitFileTransferRouteTransition(
            applied.State,
            fallbackAuthority,
            fallbackReceipt);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Idempotent,
            duplicateCommit.Status);
        Assert.Same(applied.State, duplicateCommit.State);

        var duplicateFrame =
            FileTransferCoordinator.ApplyFileTransferRouteTransitionRetainedFrameReceipt(
                applied.State,
                new FileTransferRouteTransitionRetainedFrameApplyReceipt(
                    frameCommand,
                    FileTransferRouteTransitionRetainedFrameApplyStatus.Idempotent,
                    "filetransfer_route_transition_retained_frame_apply_idempotent"));
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Idempotent,
            duplicateFrame.Status);
        Assert.Same(applied.State, duplicateFrame.State);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FileTransferRouteTransition")]
    public void FileTransferRouteTransition_FallbackToTunaRequiresExactEffectivePhase2Receipt()
    {
        var setup = CreateFallbackV6ToTunaRouteTransitionSetup();
        var activationRequest = Assert.IsType<
            FileTransferTunaActivationTransitionProofBoundary>(
            setup.Request.ProofBoundary);
        var retryRequest =
            activationRequest.EffectiveRetryInstallReceipt!.Value.Request;
        Assert.Equal(1, retryRequest.DispatchAttemptGeneration);
        Assert.Equal(0, retryRequest.MissingDispatchSuccessorGeneration);
        Assert.Null(retryRequest.PredecessorAuthorityId);
        Assert.False(retryRequest.RequestActivePredecessorSupersession);
        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(
            setup.State,
            setup.Request);

        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Prepared,
            prepared.Status);
        var authority = prepared.Authority!.Value;
        Assert.Equal(
            FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4,
            authority.Kind);

        var activation = Assert.IsType<
            FileTransferTunaActivationTransitionProofBoundary>(
            setup.Request.ProofBoundary);
        var rawReceipt = activation.EffectiveRetryInstallReceipt!.Value with
        {
            StageStatus = default,
            StageDisposition = RuntimeUnlockRetryDispatchStageDisposition.Unknown,
            StageCleared = false,
            StagedUtcMs = 0,
            StageExpiresUtcMs = 0,
        };
        var rawRequest = setup.Request with
        {
            ProofBoundary = activation with
            {
                EffectiveRetryInstallReceipt = rawReceipt,
            },
        };
        var rawRejected =
            FileTransferCoordinator.PrepareFileTransferRouteTransition(
                setup.State,
                rawRequest);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Rejected,
            rawRejected.Status);
        Assert.Same(setup.State, rawRejected.State);

        var mutatedReceipt = activation.EffectiveRetryInstallReceipt.Value with
        {
            Authority = activation.EffectiveRetryInstallReceipt.Value.Authority with
            {
                AuthorityId = new FileTransferAuthorityId(
                    "fta2-mutated-fallback-transition-receipt"),
            },
        };
        var mutatedRequest = setup.Request with
        {
            ProofBoundary = activation with
            {
                EffectiveRetryInstallReceipt = mutatedReceipt,
            },
        };
        var mutatedRejected =
            FileTransferCoordinator.PrepareFileTransferRouteTransition(
                setup.State,
                mutatedRequest);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Rejected,
            mutatedRejected.Status);
        Assert.Same(setup.State, mutatedRejected.State);

        var emptyFingerprint =
            FileTransferCoordinator
                .CreateFileTransferRouteTransitionPhysicalDrainFingerprint([]);
        var draining =
            FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(
                prepared.State,
                authority,
                new FileTransferRouteTransitionPauseBindReceipt(
                    prepared.PauseBindCommand!.Value,
                    FileTransferRouteTransitionPauseBindStatus.Installed,
                    "filetransfer_route_transition_pause_bound"),
                capturedThroughPhysicalSendGeneration: 0,
                capturedPhysicalSendLeaseIds: [],
                emptyFingerprint);
        var drain = draining.DrainCommand!.Value;
        var committed = FileTransferCoordinator.CommitFileTransferRouteTransition(
            draining.State,
            authority,
            new FileTransferRouteTransitionPhysicalDrainReceipt(
                drain,
                drain.CapturedPhysicalSendLeaseIds,
                drain.CapturedPhysicalSendCount,
                drain.CapturedPhysicalSendFingerprint,
                FileTransferRouteTransitionPhysicalDrainOutcome.Completed,
                "physical_drain_completed"),
            setup.FinalProof);

        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Committed,
            committed.Status);
        Assert.Equal(
            FileTransferRoute.FileTunaV4,
            committed.State.RouteSelection.Route);
        Assert.Null(committed.State.CurrentRuntimeUnlockRetryDispatchAuthority);
        Assert.Null(committed.State.CurrentRuntimeUnlockAdmission);
        Assert.Null(committed.State.CurrentRuntimeUnlockPendingPreCommitProbe);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FileTransferRouteTransition")]
    public void FileTransferRouteTransition_InstalledPressureRequiresExactApplyReceipt()
    {
        var setup = CreateRegularV4ToTunaRouteTransitionSetup();
        var (repairState, repairDecision) =
            AuthorizeRegularV4FrontierRepair(setup.State);
        var stagedPressure =
            FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
                repairState,
                repairDecision,
                RegularV4PressureAuthorityRequest(
                    "regular-v4-pressure:route-transition"),
                "regular_v4_receiver_frontier_repair_due");
        var pressureBinding = stagedPressure.Binding!.Value;
        var installedPressure =
            FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
                stagedPressure.State,
                new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                    pressureBinding.Authority,
                    FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed,
                    "installed",
                    pressureBinding.InstallGeneration,
                    pressureBinding.PressureReason));
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted,
            installedPressure.Status);

        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(
            installedPressure.State,
            setup.Request);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Prepared,
            prepared.Status);
        var authority = prepared.Authority!.Value;
        var emptyFingerprint =
            FileTransferCoordinator
                .CreateFileTransferRouteTransitionPhysicalDrainFingerprint([]);
        var draining =
            FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(
                prepared.State,
                authority,
                new FileTransferRouteTransitionPauseBindReceipt(
                    prepared.PauseBindCommand!.Value,
                    FileTransferRouteTransitionPauseBindStatus.Installed,
                    "filetransfer_route_transition_pause_bound"),
                capturedThroughPhysicalSendGeneration: 0,
                capturedPhysicalSendLeaseIds: [],
                emptyFingerprint);
        var drainCommand = draining.DrainCommand!.Value;
        var drainReceipt = new FileTransferRouteTransitionPhysicalDrainReceipt(
            drainCommand,
            drainCommand.CapturedPhysicalSendLeaseIds,
            drainCommand.CapturedPhysicalSendCount,
            drainCommand.CapturedPhysicalSendFingerprint,
            FileTransferRouteTransitionPhysicalDrainOutcome.Completed,
            "physical_drain_completed");
        var preparedPressureSettlement =
            FileTransferCoordinator
                .PrepareFileTransferRouteTransitionPressureSettlement(
                    draining.State,
                    authority,
                    drainReceipt,
                    setup.FinalProof);
        Assert.Equal(
            RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted,
            preparedPressureSettlement.Status);
        var pressureCommand =
            Assert.IsType<FileTransferRegularV4FrontierRepairPressureSettlementCommand>(
                preparedPressureSettlement.SettlementCommand);

        var missingReceipt =
            FileTransferCoordinator.CommitFileTransferRouteTransition(
                draining.State,
                authority,
                drainReceipt,
                setup.FinalProof);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Rejected,
            missingReceipt.Status);
        Assert.Same(draining.State, missingReceipt.State);
        Assert.NotNull(PressureState(missingReceipt.State).InstalledBinding);

        var mutatedReceipt =
            FileTransferCoordinator.CommitFileTransferRouteTransition(
                draining.State,
                authority,
                drainReceipt,
                setup.FinalProof,
                new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                    pressureCommand,
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                    "pressure_applied_by_unrelated_path",
                    CurrentAuthorityCleared: true,
                    RetainedPredecessorCleared: false));
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Rejected,
            mutatedReceipt.Status);
        Assert.Same(draining.State, mutatedReceipt.State);

        var exactReceipt =
            new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
                pressureCommand,
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                "regular_v4_frontier_repair_pressure_settlement_applied",
                CurrentAuthorityCleared: true,
                RetainedPredecessorCleared: false);
        var committed = FileTransferCoordinator.CommitFileTransferRouteTransition(
            draining.State,
            authority,
            drainReceipt,
            setup.FinalProof,
            exactReceipt);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Committed,
            committed.Status);
        Assert.Equal(
            FileTransferRoute.FileTunaV4,
            committed.State.RouteSelection.Route);
        Assert.Null(PressureState(committed.State).InstalledBinding);

        var duplicate = FileTransferCoordinator.CommitFileTransferRouteTransition(
            committed.State,
            authority,
            drainReceipt,
            setup.FinalProof,
            exactReceipt with
            {
                Status =
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus
                        .Idempotent,
                Reason =
                    "regular_v4_frontier_repair_pressure_settlement_apply_idempotent",
            });
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Idempotent,
            duplicate.Status);
        Assert.Same(committed.State, duplicate.State);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FileTransferRouteTransition")]
    public void FileTransferRouteTransition_RollbackPreservesSourceScopeAndAppliesOnce()
    {
        var setup = CreateRegularV4ToTunaRouteTransitionSetup();
        var sourceOwner = setup.State.CurrentRuntimeOwner;
        var sourcePublication = setup.State.CurrentRoutePublicationToken;
        var sourceLeg = setup.State.CurrentLeg;
        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(
            setup.State,
            setup.Request);
        var authority = prepared.Authority!.Value;

        var invalidated = FileTransferCoordinator.InvalidateFileTransferRouteTransition(
            prepared.State,
            authority,
            terminalInvalidation: false,
            "precommit_probe_rejected");
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Invalidated,
            invalidated.Status);
        Assert.Equal(sourceOwner, invalidated.State.CurrentRuntimeOwner);
        Assert.Equal(sourcePublication, invalidated.State.CurrentRoutePublicationToken);
        Assert.Same(sourceLeg, invalidated.State.CurrentLeg);
        Assert.Equal(
            FileTransferRoute.RegularNknV4Fast,
            invalidated.State.RouteSelection.Route);

        var duplicate = FileTransferCoordinator.InvalidateFileTransferRouteTransition(
            invalidated.State,
            authority,
            terminalInvalidation: false,
            "precommit_probe_rejected");
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Idempotent,
            duplicate.Status);
        Assert.Same(invalidated.State, duplicate.State);

        var disposition = Assert.IsType<FileTransferRouteTransitionDispositionCommand>(
            invalidated.DispositionCommand);
        var mismatched = FileTransferCoordinator
            .ApplyFileTransferRouteTransitionDispositionReceipt(
                invalidated.State,
                new FileTransferRouteTransitionDispositionReceipt(
                    disposition,
                    FileTransferRouteTransitionDispositionApplyStatus.Applied,
                    "filetransfer_route_transition_committed"));
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Rejected,
            mismatched.Status);
        Assert.Same(invalidated.State, mismatched.State);

        var applied = FileTransferCoordinator
            .ApplyFileTransferRouteTransitionDispositionReceipt(
                invalidated.State,
                new FileTransferRouteTransitionDispositionReceipt(
                    disposition,
                    FileTransferRouteTransitionDispositionApplyStatus.Applied,
                    "filetransfer_route_transition_rolled_back"));
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Invalidated,
            applied.Status);

        var duplicateReceipt = FileTransferCoordinator
            .ApplyFileTransferRouteTransitionDispositionReceipt(
                applied.State,
                new FileTransferRouteTransitionDispositionReceipt(
                    disposition,
                    FileTransferRouteTransitionDispositionApplyStatus.Idempotent,
                    "filetransfer_route_transition_disposition_idempotent"));
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Idempotent,
            duplicateReceipt.Status);
        Assert.Same(applied.State, duplicateReceipt.State);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FileTransferRouteTransition")]
    public void FileTransferRouteTransition_UncommittedTunaFallbackSupersedesExactDrain()
    {
        var setup = CreateRegularV4ToTunaRouteTransitionSetup();
        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(
            setup.State,
            setup.Request);
        var predecessor = prepared.Authority!.Value;
        long[] capturedLeaseIds = [3, 7];
        var fingerprint =
            FileTransferCoordinator
                .CreateFileTransferRouteTransitionPhysicalDrainFingerprint(
                    capturedLeaseIds);
        var draining =
            FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(
                prepared.State,
                predecessor,
                new FileTransferRouteTransitionPauseBindReceipt(
                    prepared.PauseBindCommand!.Value,
                    FileTransferRouteTransitionPauseBindStatus.Installed,
                    "filetransfer_route_transition_pause_bound"),
                capturedThroughPhysicalSendGeneration: 7,
                capturedLeaseIds,
                fingerprint);

        var sourceLeg = draining.State.CurrentLeg!;
        var targetEpoch = Math.Max(1, sourceLeg.TransportEpochId + 1);
        var frameIdentity = new PostTunaFallbackPeerProofFrameIdentity(
            "filetransfer_frontier_request_v6",
            targetEpoch,
            new string('b', 64));
        var peerProof = new PostTunaFallbackPeerProofEvidence(
            draining.State.SessionId,
            draining.State.TransferId,
            draining.State.Direction,
            ProofGeneration: 2,
            TransportEpoch: targetEpoch,
            ProofKind: "fallback_peer_frontier",
            Reason: "uncommitted_tuna_fallback",
            FrameIdentity: frameIdentity);
        var successorRequest = new FileTransferRouteTransitionPrepareRequest(
            FileTransferRouteTransitionKind.UncommittedTunaToPostTunaFallbackV6,
            predecessor.SourceRuntimeOwner,
            predecessor.SourceRoutePublication,
            SourceRouteCommit: null,
            predecessor.SourceRoute,
            predecessor.SourceProtocolVersion,
            predecessor.SourceTransport,
            new FileTransferFallbackTransitionProofBoundary(
                SourceRouteCommit: null,
                peerProof,
                frameIdentity,
                targetEpoch,
                TargetBridgeRecoveryGeneration:
                    Math.Max(1, sourceLeg.BridgeRecoveryGeneration + 1),
                PredecessorTransitionAuthorityId: predecessor.AuthorityId),
            predecessor.DurableCheckpoint,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            FileTransferTransportKind.RegularNkn,
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            predecessor.AuthorityId,
            "uncommitted_tuna_fallback");
        var staged = FileTransferCoordinator.StageFileTransferRouteTransitionSupersession(
            draining.State,
            predecessor,
            successorRequest);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.SupersessionStaged,
            staged.Status);
        Assert.Equal(
            predecessor,
            staged.PauseBindCommand!.Value.PredecessorAuthority);

        var missingPredecessorReceipt = new FileTransferRouteTransitionPauseBindReceipt(
            new FileTransferRouteTransitionPauseBindCommand(
                staged.PauseBindCommand.Value.Authority),
            FileTransferRouteTransitionPauseBindStatus.Installed,
            "filetransfer_route_transition_pause_rebound");
        var missingPredecessor =
            FileTransferCoordinator.CommitFileTransferRouteTransitionSupersession(
                staged.State,
                missingPredecessorReceipt);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Rejected,
            missingPredecessor.Status);
        Assert.Same(staged.State, missingPredecessor.State);

        var reboundReceipt = new FileTransferRouteTransitionPauseBindReceipt(
            staged.PauseBindCommand.Value,
            FileTransferRouteTransitionPauseBindStatus.Installed,
            "filetransfer_route_transition_pause_rebound");
        var superseded =
            FileTransferCoordinator.CommitFileTransferRouteTransitionSupersession(
                staged.State,
                reboundReceipt);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Draining,
            superseded.Status);
        var transitionState =
            FileTransferCoordinator.GetFileTransferRouteTransitionSubstate(
                superseded.State);
        var successor = transitionState.CurrentOperation!.Value;
        Assert.Equal(
            FileTransferRouteTransitionKind
                .UncommittedTunaToPostTunaFallbackV6,
            successor.Authority.Kind);
        Assert.Equal(
            capturedLeaseIds,
            successor.DrainCommand!.Value.CapturedPhysicalSendLeaseIds);
        Assert.Equal(
            FileTransferRouteTransitionDispositionKind.Superseded,
            transitionState.LastTerminalOperation!.Value
                .DispositionCommand!.Value.Kind);

        var duplicate =
            FileTransferCoordinator.CommitFileTransferRouteTransitionSupersession(
                superseded.State,
                reboundReceipt with
                {
                    Status = FileTransferRouteTransitionPauseBindStatus.Idempotent,
                    Reason = "filetransfer_route_transition_pause_bind_idempotent",
                });
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Idempotent,
            duplicate.Status);
        Assert.Same(superseded.State, duplicate.State);

        var successorDrain = successor.DrainCommand.Value;
        var committed = FileTransferCoordinator.CommitFileTransferRouteTransition(
            superseded.State,
            successor.Authority,
            new FileTransferRouteTransitionPhysicalDrainReceipt(
                successorDrain,
                successorDrain.CapturedPhysicalSendLeaseIds,
                successorDrain.CapturedPhysicalSendCount,
                successorDrain.CapturedPhysicalSendFingerprint,
                FileTransferRouteTransitionPhysicalDrainOutcome.Completed,
                "physical_drain_completed"));
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Committed,
            committed.Status);
        Assert.Equal(
            FileTransferRoute.PostTunaFallbackV6,
            committed.State.RouteSelection.Route);

        var stalePredecessorReceipt = new FileTransferRouteTransitionPhysicalDrainReceipt(
            draining.DrainCommand!.Value,
            draining.DrainCommand.Value.CapturedPhysicalSendLeaseIds,
            draining.DrainCommand.Value.CapturedPhysicalSendCount,
            draining.DrainCommand.Value.CapturedPhysicalSendFingerprint,
            FileTransferRouteTransitionPhysicalDrainOutcome.Completed,
            "physical_drain_completed");
        var staleCommit = FileTransferCoordinator.CommitFileTransferRouteTransition(
            committed.State,
            predecessor,
            stalePredecessorReceipt,
            setup.FinalProof);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Rejected,
            staleCommit.Status);
        Assert.Same(committed.State, staleCommit.State);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FileTransferRouteTransition")]
    public void FileTransferRouteTransition_PreparedSupersessionBeginsDrainWithExactRebindReceipt()
    {
        var setup = CreateRegularV4ToTunaRouteTransitionSetup();
        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(
            setup.State,
            setup.Request);
        var predecessor = prepared.Authority!.Value;
        var sourceLeg = prepared.State.CurrentLeg!;
        var targetEpoch = Math.Max(1, sourceLeg.TransportEpochId + 1);
        var frameIdentity = new PostTunaFallbackPeerProofFrameIdentity(
            "filetransfer_frontier_request_v6",
            targetEpoch,
            new string('c', 64));
        var peerProof = new PostTunaFallbackPeerProofEvidence(
            prepared.State.SessionId,
            prepared.State.TransferId,
            prepared.State.Direction,
            ProofGeneration: 2,
            TransportEpoch: targetEpoch,
            ProofKind: "fallback_peer_frontier",
            Reason: "uncommitted_tuna_fallback",
            FrameIdentity: frameIdentity);
        var successorRequest = new FileTransferRouteTransitionPrepareRequest(
            FileTransferRouteTransitionKind.UncommittedTunaToPostTunaFallbackV6,
            predecessor.SourceRuntimeOwner,
            predecessor.SourceRoutePublication,
            SourceRouteCommit: null,
            predecessor.SourceRoute,
            predecessor.SourceProtocolVersion,
            predecessor.SourceTransport,
            new FileTransferFallbackTransitionProofBoundary(
                SourceRouteCommit: null,
                peerProof,
                frameIdentity,
                targetEpoch,
                TargetBridgeRecoveryGeneration:
                    Math.Max(1, sourceLeg.BridgeRecoveryGeneration + 1),
                PredecessorTransitionAuthorityId: predecessor.AuthorityId),
            predecessor.DurableCheckpoint,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            FileTransferTransportKind.RegularNkn,
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            predecessor.AuthorityId,
            "uncommitted_tuna_fallback");
        var staged = FileTransferCoordinator.StageFileTransferRouteTransitionSupersession(
            prepared.State,
            predecessor,
            successorRequest);
        var reboundReceipt = new FileTransferRouteTransitionPauseBindReceipt(
            staged.PauseBindCommand!.Value,
            FileTransferRouteTransitionPauseBindStatus.Installed,
            "filetransfer_route_transition_pause_rebound");
        var superseded =
            FileTransferCoordinator.CommitFileTransferRouteTransitionSupersession(
                staged.State,
                reboundReceipt);

        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Prepared,
            superseded.Status);
        var successor = Assert.IsType<FileTransferRouteTransitionAuthority>(
            superseded.Authority);
        Assert.Null(superseded.DrainCommand);

        long[] capturedLeaseIds = [5, 8];
        var draining =
            FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(
                superseded.State,
                successor,
                reboundReceipt,
                capturedThroughPhysicalSendGeneration: 8,
                capturedLeaseIds,
                FileTransferCoordinator
                    .CreateFileTransferRouteTransitionPhysicalDrainFingerprint(
                        capturedLeaseIds));

        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Draining,
            draining.Status);
        Assert.Equal(
            capturedLeaseIds,
            draining.DrainCommand!.Value.CapturedPhysicalSendLeaseIds);
        Assert.Equal(
            reboundReceipt,
            FileTransferCoordinator.GetFileTransferRouteTransitionSubstate(
                    draining.State)
                .CurrentOperation!.Value.PauseBindReceipt);
    }

    private static (
        FileTransferCoordinatorState State,
        FileTransferRouteTransitionPrepareRequest Request,
        RuntimeUnlockRouteCommitProof FinalProof)
        CreateFallbackV6ToTunaRouteTransitionSetup(
            FileTransferCoordinatorState? sourceState = null)
    {
        var (fallbackState, retryRequest, tailYield) =
            AuthorizeFallbackRuntimeUnlockRetryDispatchSource(sourceState);
        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            fallbackState,
            retryRequest,
            currentContract: null,
            RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield,
            tailYield.AuthorityId,
            tailYield.YieldGeneration,
            tailYield.YieldKind.ToString());
        Assert.True(granted.Granted);
        var retryContract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(
            granted.Contract);
        Assert.True(
            FileTransferCoordinator
                .TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
                    granted.State,
                    granted,
                    retryRequest,
                    retryContract,
                    activePredecessorContract: null,
                    lastSettlement: null,
                    currentReceipt: null,
                    lastInstallGeneration: 0,
                    out var installReceipt,
                    out var installMismatch),
            installMismatch);
        installReceipt = installReceipt with
        {
            Request = retryRequest,
            StageStatus =
                RuntimeUnlockRetryDispatchDecisionStageStatus.Staged,
            StageDisposition =
                RuntimeUnlockRetryDispatchStageDisposition.Retained,
            StageCleared = false,
            StagedUtcMs = 1,
            StageExpiresUtcMs = 2,
        };

        var admitted = RequestRuntimeUnlockAdmission(
            granted.State,
            granted.State.CurrentRuntimeOwner);
        var admission = Assert.IsType<RuntimeUnlockAdmissionToken>(
            admitted.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken);
        var pendingDecision = RecordPendingPreCommitProbe(
            admitted.State,
            admission);
        var pending = Assert.IsType<RuntimeUnlockPendingPreCommitProbeRequest>(
            pendingDecision.State.CurrentRuntimeUnlockPendingPreCommitProbe);
        var state = pendingDecision.State;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(
            state,
            pending.TransactionGeneration,
            pending.OfferGeneration,
            probeId: "probe-fallback-v6-to-tuna");
        var finalProof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(
                transaction,
                "fallback_v6_to_tuna_commit",
                admission),
            pending.TunaPathLeaseGeneration,
            pending.TunaPathLeaseListenerRunId!);
        var owner = state.CurrentRuntimeOwner!.Value;
        var publication = state.CurrentRoutePublicationToken!.Value;
        var leg = state.CurrentLeg!;
        var checkpoint = new FileTransferRouteTransitionDurableCheckpoint(
            owner,
            publication,
            state.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            DataSessionGeneration: 23,
            StateEpoch: 5,
            ContiguousCommittedChunkIndex: 6,
            DurableHighestChunkIndex: 7,
            ChunkCount: 128,
            CheckpointGeneration: 3);
        var request = new FileTransferRouteTransitionPrepareRequest(
            FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4,
            owner,
            publication,
            SourceRouteCommit: null,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            FileTransferTransportKind.RegularNkn,
            new FileTransferTunaActivationTransitionProofBoundary(
                admission,
                pending,
                finalProof,
                LaunchGeneration: 31,
                installReceipt,
                tailYield),
            checkpoint,
            FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportKind.Tuna,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            PredecessorTransitionAuthorityId: null,
            "fallback_v6_to_tuna_transition_test");
        return (state, request, finalProof);
    }

    private static (
        FileTransferCoordinatorState State,
        FileTransferRouteTransitionPrepareRequest Request,
        RuntimeUnlockRouteCommitProof FinalProof)
        CreateRegularV4ToTunaRouteTransitionSetup(
            FileTransferCoordinatorState? sourceState = null)
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = sourceState ?? StartRegular(regular).State;
        if (state.CurrentRoutePublicationToken is null)
        {
            var owner = state.CurrentRuntimeOwner!.Value;
            var leg = state.CurrentLeg!;
            var publication = FileTransferCoordinator.AuthorizeAction(
                state,
                new FileTransferCoordinatorActionRequest(
                    FileTransferCoordinatorActionKind.RoutePublication,
                    Source: "test",
                    Reason: "regular_v4_to_tuna_source_publication",
                    RuntimeOwner: owner,
                    Route: regular.Route,
                    ProtocolVersion: regular.ProtocolVersion,
                    LiveRouteEpoch: leg.LiveRouteEpochId,
                    LegGeneration: leg.Generation,
                    RuntimeGeneration: owner.RuntimeGeneration,
                    TerminalGeneration: state.TerminalGeneration,
                    TargetRoute: regular.Route,
                    TargetProtocolVersion: regular.ProtocolVersion,
                    TargetTransport: FileTransferTransportKind.RegularNkn));
            Assert.True(publication.Accepted);
            state = publication.State;
        }

        var admitted = RequestRuntimeUnlockAdmission(state, state.CurrentRuntimeOwner);
        var admission = Assert.IsType<RuntimeUnlockAdmissionToken>(
            admitted.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken);
        var pendingDecision = RecordPendingPreCommitProbe(admitted.State, admission);
        var pending = Assert.IsType<RuntimeUnlockPendingPreCommitProbeRequest>(
            pendingDecision.State.CurrentRuntimeUnlockPendingPreCommitProbe);
        state = pendingDecision.State;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(
            state,
            pending.TransactionGeneration,
            pending.OfferGeneration,
            probeId: "probe-regular-v4-to-tuna");
        var finalProof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(
                transaction,
                "regular_v4_to_tuna_commit",
                admission),
            pending.TunaPathLeaseGeneration,
            pending.TunaPathLeaseListenerRunId!);
        var launchBoundary = finalProof with
        {
            TransactionState = RuntimeUnlockTransactionState.AnswerReceived,
            PathProbeId = null,
            PathProbeState = RuntimeUnlockPathProbeState.None,
            PathProbeTransport = FileTransferTransportKind.Unknown,
            PathProbeAckedUtcMs = 0,
            SamePathTunaProbeAckId = null,
        };
        var ownerToken = state.CurrentRuntimeOwner!.Value;
        var sourcePublication = state.CurrentRoutePublicationToken!.Value;
        var sourceLeg = state.CurrentLeg!;
        var checkpoint = new FileTransferRouteTransitionDurableCheckpoint(
            ownerToken,
            sourcePublication,
            state.TerminalGeneration,
            sourceLeg.Generation,
            sourceLeg.TransportEpochId,
            sourceLeg.BridgeRecoveryGeneration,
            DataSessionGeneration: 17,
            StateEpoch: 3,
            ContiguousCommittedChunkIndex: 4,
            DurableHighestChunkIndex: 6,
            ChunkCount: 128,
            CheckpointGeneration: 1);
        var request = new FileTransferRouteTransitionPrepareRequest(
            FileTransferRouteTransitionKind.RegularV4ToTunaV4,
            ownerToken,
            sourcePublication,
            SourceRouteCommit: null,
            FileTransferRoute.RegularNknV4Fast,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportKind.RegularNkn,
            new FileTransferTunaActivationTransitionProofBoundary(
                admission,
                pending,
                launchBoundary,
                LaunchGeneration: 29),
            checkpoint,
            FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportKind.Tuna,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            PredecessorTransitionAuthorityId: null,
            "regular_v4_to_tuna_transition_test");
        return (state, request, finalProof);
    }

    private static FileTransferCoordinatorDecision StartRegular(FileTransferRouteSelection regular)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                regular,
                state: FileTransferLegState.Active,
                canSendData: true),
            EmptyState(regular));

    private static FileTransferCoordinatorDecision ActivateTuna(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection tuna,
        int committedChunk)
    {
        var setup = state.RouteSelection.Route switch
        {
            FileTransferRoute.RegularNknV4Fast =>
                CreateRegularV4ToTunaRouteTransitionSetup(state),
            FileTransferRoute.PostTunaFallbackV6 =>
                CreateFallbackV6ToTunaRouteTransitionSetup(state),
            _ => throw new InvalidOperationException(
                $"Cannot activate Tuna from {state.RouteSelection.Route}."),
        };
        return CommitEmptyFileTransferRouteTransitionForTest(
            setup.State,
            setup.Request,
            setup.FinalProof);
    }

    private static FileTransferCoordinatorDecision FallBack(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection fallback,
        int committedChunk,
        long transportEpoch)
    {
        if (state.RouteSelection.Route == FileTransferRoute.RegularNknV4Fast)
        {
            state = ActivateTuna(
                state,
                FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4),
                committedChunk).State;
        }

        Assert.Equal(FileTransferRoute.FileTunaV4, state.RouteSelection.Route);
        if (state.CurrentRoutePublicationToken is null)
        {
            var currentCommit = Assert.IsType<RouteCommitToken>(
                state.CurrentRouteCommit);
            var issuedPublication = FileTransferCoordinator.AuthorizeAction(
                state,
                RouteCommitPublicationRequest(currentCommit));
            Assert.True(issuedPublication.Accepted, issuedPublication.RejectionReason);
            state = issuedPublication.State;
        }

        var owner = state.CurrentRuntimeOwner!.Value;
        var publication = state.CurrentRoutePublicationToken!.Value;
        var routeCommit = state.CurrentRouteCommit!.Value;
        var leg = state.CurrentLeg!;
        var frameIdentity = new PostTunaFallbackPeerProofFrameIdentity(
            "filetransfer_receiver_state_v6",
            transportEpoch,
            new string('f', 64));
        var peerProof = new PostTunaFallbackPeerProofEvidence(
            state.SessionId,
            state.TransferId,
            state.Direction,
            ProofGeneration: Math.Max(1, transportEpoch),
            TransportEpoch: transportEpoch,
            ProofKind: "fallback_peer_state",
            Reason: "tuna_disabled",
            FrameIdentity: frameIdentity);
        var proofBoundary = new FileTransferFallbackTransitionProofBoundary(
            routeCommit,
            peerProof,
            frameIdentity,
            transportEpoch,
            TargetBridgeRecoveryGeneration: checked((int)Math.Max(1L, transportEpoch)));
        var checkpoint = new FileTransferRouteTransitionDurableCheckpoint(
            owner,
            publication,
            state.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            DataSessionGeneration: Math.Max(1, transportEpoch),
            StateEpoch: checked((int)Math.Max(1L, transportEpoch)),
            ContiguousCommittedChunkIndex: committedChunk,
            DurableHighestChunkIndex: committedChunk,
            ChunkCount: Math.Max(128, committedChunk + 2),
            CheckpointGeneration: checked((int)Math.Max(1L, transportEpoch)));
        var request = new FileTransferRouteTransitionPrepareRequest(
            FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6,
            owner,
            publication,
            routeCommit,
            FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportKind.Tuna,
            proofBoundary,
            checkpoint,
            fallback.Route,
            fallback.ProtocolVersion,
            FileTransferTransportKind.RegularNkn,
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            PredecessorTransitionAuthorityId: null,
            "tuna_disabled");
        return CommitEmptyFileTransferRouteTransitionForTest(
            state,
            request,
            finalProof: null);
    }

    private static FileTransferCoordinatorDecision
        CommitEmptyFileTransferRouteTransitionForTest(
            FileTransferCoordinatorState state,
            FileTransferRouteTransitionPrepareRequest request,
            RuntimeUnlockRouteCommitProof? finalProof)
    {
        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(
            state,
            request);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Prepared,
            prepared.Status);
        var authority = prepared.Authority!.Value;
        var emptyFingerprint =
            FileTransferCoordinator
                .CreateFileTransferRouteTransitionPhysicalDrainFingerprint([]);
        var draining =
            FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(
                prepared.State,
                authority,
                new FileTransferRouteTransitionPauseBindReceipt(
                    prepared.PauseBindCommand!.Value,
                    FileTransferRouteTransitionPauseBindStatus.Installed,
                    "filetransfer_route_transition_pause_bound"),
                capturedThroughPhysicalSendGeneration: 0,
                capturedPhysicalSendLeaseIds: [],
                emptyFingerprint);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Draining,
            draining.Status);
        var drain = draining.DrainCommand!.Value;
        var committed = FileTransferCoordinator.CommitFileTransferRouteTransition(
            draining.State,
            authority,
            new FileTransferRouteTransitionPhysicalDrainReceipt(
                drain,
                drain.CapturedPhysicalSendLeaseIds,
                drain.CapturedPhysicalSendCount,
                drain.CapturedPhysicalSendFingerprint,
                FileTransferRouteTransitionPhysicalDrainOutcome.Completed,
                "physical_drain_completed"),
            finalProof);
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Committed,
            committed.Status);

        var committedState = committed.State;
        if (committed.RetainedFrameApplyCommand is { } frameCommand)
        {
            committedState = FileTransferCoordinator
                .ApplyFileTransferRouteTransitionRetainedFrameReceipt(
                    committedState,
                    new FileTransferRouteTransitionRetainedFrameApplyReceipt(
                        frameCommand,
                        FileTransferRouteTransitionRetainedFrameApplyStatus.Applied,
                        "filetransfer_route_transition_retained_frame_applied"))
                .State;
        }

        var disposition = committed.DispositionCommand!.Value;
        committedState = FileTransferCoordinator
            .ApplyFileTransferRouteTransitionDispositionReceipt(
                committedState,
                new FileTransferRouteTransitionDispositionReceipt(
                    disposition,
                    FileTransferRouteTransitionDispositionApplyStatus.Applied,
                    "filetransfer_route_transition_committed"))
            .State;
        return Assert.IsType<FileTransferCoordinatorDecision>(
            committed.RouteDecision) with
        {
            State = committedState,
        };
    }

    private static FileTransferCoordinatorDecision RequestFallbackCheckpoint(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection fallback,
        string checkpointRequestId,
        long transportEpoch)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointRequested,
                fallback,
                reason: "checkpoint_requested",
                state: FileTransferLegState.CheckpointPending,
                canSendData: false,
                transportEpoch: transportEpoch,
                checkpointRequestId: checkpointRequestId,
                checkpointPriority: "state_refresh_tail_reconciliation"),
            state);

    private static FileTransferCoordinatorDecision AcceptFallbackCheckpoint(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection fallback,
        string checkpointRequestId,
        long transportEpoch,
        int committedChunk,
        int highestObservedChunk)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointAccepted,
                fallback,
                reason: "checkpoint_accepted",
                state: FileTransferLegState.RecoveryActive,
                canSendData: true,
                committedChunk: committedChunk,
                highestObservedChunk: highestObservedChunk,
                transportEpoch: transportEpoch,
                checkpointRequestId: checkpointRequestId),
            state);

    private static FileTransferCoordinatorDecision MarkFallbackTailQuiescent(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection fallback,
        long transportEpoch,
        int committedChunk,
        int highestObservedChunk)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailQuiescent,
                fallback,
                reason: "fallback_tail_quiescent",
                state: FileTransferLegState.Active,
                canSendData: true,
                committedChunk: committedChunk,
                highestObservedChunk: highestObservedChunk,
                transportEpoch: transportEpoch),
            state);

    private static FileTransferCoordinatorDecision RetireFallbackCheckpoint(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection fallback,
        string checkpointRequestId,
        long transportEpoch)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointRetired,
                fallback,
                reason: "checkpoint_retired",
                state: FileTransferLegState.RecoveryActive,
                canSendData: true,
                transportEpoch: transportEpoch,
                checkpointRequestId: checkpointRequestId),
            state);

    private static FileTransferCoordinatorDecision MarkFallbackTailFailed(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection fallback,
        long transportEpoch)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailFailed,
                fallback,
                reason: "fallback_tail_failed",
                state: FileTransferLegState.RecoveryActive,
                canSendData: false,
                transportEpoch: transportEpoch),
            state);

    private static FileTransferCoordinatorState ActiveFallbackStateWithoutCheckpoint()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var state = FallBack(
            ActivateTuna(StartRegular(regular).State, tuna, committedChunk: 5).State,
            fallback,
            committedChunk: 5,
            transportEpoch: 2).State;
        state.CurrentLeg!.State = FileTransferLegState.Active;
        state.CurrentLeg.CanSendData = true;
        state.CurrentLeg.CheckpointRequestId = null;
        return state;
    }

    private static FileTransferCoordinatorDecision RequestRuntimeUnlockAdmission(
        FileTransferCoordinatorState state,
        RuntimeOwnerToken? owner,
        RuntimeUnlockFallbackSurvivalState? survivalState = null)
    {
        var nextState = state;
        if (survivalState is { } survival)
        {
            nextState = FileTransferCoordinator.Apply(
                CoordinatorEvent(
                    FileTransferCoordinatorEventKind.RuntimeUnlockFallbackSurvivalUpdated,
                    state.RouteSelection,
                    reason: "fallback_survival_updated",
                    state: state.CurrentLeg?.State ?? FileTransferLegState.Active,
                    canSendData: state.CurrentLeg?.CanSendData ?? true,
                    transportEpoch: state.CurrentLeg?.TransportEpochId ?? 0,
                    bridgeRecoveryGeneration: state.CurrentLeg?.BridgeRecoveryGeneration ?? 0,
                    runtimeUnlockFallbackSurvivalState: survival),
                state).State;
        }

        return FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockAdmission,
                nextState.RouteSelection,
                reason: "runtime_unlock_admission",
                state: nextState.CurrentLeg?.State ?? FileTransferLegState.Active,
                canSendData: nextState.CurrentLeg?.CanSendData ?? true,
                transportEpoch: nextState.CurrentLeg?.TransportEpochId ?? 0,
                bridgeRecoveryGeneration: nextState.CurrentLeg?.BridgeRecoveryGeneration ?? 0,
                runtimeUnlockAdmissionRequest: new RuntimeUnlockAdmissionRequest(
                    "test",
                    "runtime_unlock_admission",
                    owner)),
            nextState);
    }

    private static FileTransferCoordinatorDecision CommitRuntimeUnlockToTuna(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection tuna)
    {
        var admission = RequestRuntimeUnlockAdmission(state, state.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State);
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken));
        return FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);
    }

    private static FileTransferCoordinatorDecision RecordPendingPreCommitProbe(
        FileTransferCoordinatorState state,
        RuntimeUnlockAdmissionToken token)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbePending,
                state.RouteSelection,
                reason: "precommit_probe_pending",
                runtimeUnlockPendingPreCommitProbeRequest: PendingPreCommitProbeRequest(state, token)),
            state);

    private static RuntimeUnlockPendingPreCommitProbeRequest PendingPreCommitProbeRequest(
        FileTransferCoordinatorState state,
        RuntimeUnlockAdmissionToken token,
        FileTransferRoute targetRoute = FileTransferRoute.FileTunaV4,
        int targetProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
        FileTransferTransportKind targetTransport = FileTransferTransportKind.Tuna)
    {
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;
        return new RuntimeUnlockPendingPreCommitProbeRequest(
            state.SessionId,
            state.TransferId,
            state.Direction,
            owner,
            token,
            state.RouteSelection.Route,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            leg.LiveRouteEpochId,
            owner.RuntimeGeneration,
            state.TerminalGeneration,
            leg.Generation,
            TransactionGeneration: 21,
            OfferGeneration: 22,
            TunaPathLeaseGeneration: 23,
            TunaPathLeaseListenerRunId: "listener-start-23",
            TargetRoute: targetRoute,
            TargetProtocolVersion: targetProtocolVersion,
            TargetTransport: targetTransport,
            HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
            PendingGeneration: 0,
            Reason: "precommit_probe_pending");
    }

    private static FileTransferCoordinatorActionRequest PendingPreCommitProbeReplayAction(
        RuntimeUnlockPendingPreCommitProbeRequest pending)
        => new(
            FileTransferCoordinatorActionKind.RuntimeUnlockPendingPreCommitProbeReplay,
            Source: "test",
            Reason: "precommit_probe_replay",
            RuntimeOwner: pending.RuntimeOwner,
            Route: pending.Route,
            ProtocolVersion: pending.ProtocolVersion,
            LiveRouteEpoch: pending.LiveRouteEpoch,
            LegGeneration: pending.LegGeneration,
            RuntimeGeneration: pending.RuntimeGeneration,
            TerminalGeneration: pending.TerminalGeneration,
            RuntimeUnlockAdmissionToken: pending.AdmissionToken,
            RuntimeUnlockTransactionGeneration: pending.TransactionGeneration,
            RuntimeUnlockOfferGeneration: pending.OfferGeneration,
            RuntimeUnlockTunaPathLeaseGeneration: pending.TunaPathLeaseGeneration,
            RuntimeUnlockTunaPathLeaseListenerRunId: pending.TunaPathLeaseListenerRunId,
            TargetRoute: pending.TargetRoute,
            TargetProtocolVersion: pending.TargetProtocolVersion,
            TargetTransport: pending.TargetTransport,
            RuntimeUnlockPendingPreCommitProbeRequest: pending);

    private static FileTransferCoordinatorActionRequest RouteCommitPublicationRequest(RouteCommitToken token)
        => new(
            FileTransferCoordinatorActionKind.RouteCommitPublication,
            Source: "test",
            Reason: "route_publication",
            Route: token.Route,
            ProtocolVersion: token.ProtocolVersion,
            LiveRouteEpoch: token.LiveRouteEpoch,
            LegGeneration: token.LegGeneration,
            RouteCommitToken: token,
            RuntimeUnlockTransactionGeneration: token.TransactionGeneration,
            RuntimeUnlockOfferGeneration: token.OfferGeneration,
            RuntimeUnlockTunaPathLeaseGeneration: token.TunaPathLeaseGeneration,
            RuntimeUnlockTunaPathLeaseListenerRunId: token.TunaPathLeaseListenerRunId,
            RuntimeUnlockProbeId: token.SamePathTunaProbeAckId,
            RuntimeUnlockSamePathTunaProbeAckId: token.SamePathTunaProbeAckId,
            RouteCommitGeneration: token.CommitGeneration,
            TargetRoute: FileTransferRoute.FileTunaV4,
            TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
            TargetTransport: FileTransferTransportKind.Tuna);

    private static RuntimeUnlockRouteCommitProof WithCurrentTunaLease(
        RuntimeUnlockRouteCommitProof proof,
        long leaseGeneration = 11,
        string listenerRunId = "listener-start-11")
        => proof with
        {
            TunaPathLeaseRequired = true,
            TunaPathLeaseGeneration = leaseGeneration,
            TunaPathLeaseState = RuntimeUnlockTunaPathLeaseState.ListenerReady,
            TunaPathLeaseListenerRunId = listenerRunId,
            TunaPathLeaseCurrent = true,
            TunaPathLeaseFailureReason = null,
        };

    private static RuntimeUnlockTransactionSnapshot BuildCommitReadyRuntimeUnlockTransaction(
        FileTransferCoordinatorState state,
        long transactionGeneration = 21,
        long offerGeneration = 22,
        string probeId = "probe-22")
    {
        var transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
                state.SessionId,
                state.TransferId,
                transactionGeneration,
                offerGeneration,
                Reason: "runtime_unlock"),
            RuntimeUnlockTransactionSnapshot.Idle).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.PeerReceived,
                state.SessionId,
                state.TransferId,
                transactionGeneration,
                offerGeneration,
                Reason: "tuna_acceleration_offer_received_raw"),
            transaction).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.AnswerReceived,
                state.SessionId,
                state.TransferId,
                transactionGeneration,
                offerGeneration,
                Reason: "transport_acceleration_answer"),
            transaction).State;
        return RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.PathProbeAcked,
                state.SessionId,
                state.TransferId,
                transactionGeneration,
                offerGeneration,
                Reason: "transport_probe_ack",
                PathProbeId: probeId,
                PathProbeTransport: FileTransferTransportKind.Tuna),
            transaction).State;
    }

    private static FileTransferCoordinatorState EmptyState(FileTransferRouteSelection routeSelection)
        => new(
            SessionId: "session_coordinator",
            TransferId: "transfer_coordinator",
            Direction: FileTransferDirection.Outbound,
            IsTerminal: false,
            RouteSelection: routeSelection,
            CurrentLiveRouteEpoch: null,
            CurrentLeg: null,
            LastLiveRouteEpochId: 0,
            LastTransferLegGeneration: 0,
            LastRuntimeOwnerGeneration: 0,
            TerminalGeneration: 0,
            CurrentRuntimeOwner: null,
            LegHistory: []);

    private static FileTransferInitialTunaReadinessEvidence InitialTunaEvidence(
        FileTransferCoordinatorState state,
        string? sessionId = null,
        string? transferId = null,
        FileTransferDirection? direction = null,
        bool leaseCurrent = true,
        bool listenerReady = true,
        bool peerVisibleProof = true,
        bool answerReceived = true,
        bool answerAckReceived = true,
        bool tunaNegotiatedObserved = true,
        DateTimeOffset? capturedUtc = null,
        string readinessScope = FileTransferInitialTunaReadinessScopes.Transfer,
        string? sourceTransferId = null,
        long snapshotGeneration = 0,
        DateTimeOffset? sourceCapturedUtc = null)
        => new(
            sessionId ?? state.SessionId,
            transferId ?? state.TransferId,
            direction ?? state.Direction,
            capturedUtc ?? DateTimeOffset.UtcNow,
            LeaseGeneration: 1,
            ListenerRunId: "listener-test-1",
            LeaseCurrent: leaseCurrent,
            ListenerReady: listenerReady,
            OfferGeneration: 1,
            PeerVisibleProof: peerVisibleProof,
            AnswerReceived: answerReceived,
            AnswerAckReceived: answerAckReceived,
            TunaNegotiatedObserved: tunaNegotiatedObserved,
            ReadinessScope: readinessScope,
            SourceTransferId: sourceTransferId,
            SnapshotGeneration: snapshotGeneration,
            SourceCapturedUtc: sourceCapturedUtc ?? default);

    private static FileTransferCoordinatorActionRequest ActionRequest(
        FileTransferCoordinatorActionKind kind,
        RuntimeOwnerToken owner,
        FileTransferRoute? route = null,
        int? protocolVersion = null,
        int? liveRouteEpoch = null,
        int? legGeneration = null,
        long? transportEpoch = null,
        int? bridgeRecoveryGeneration = null,
        string? checkpointRequestId = null)
        => new(
            kind,
            Source: "test",
            Reason: "test",
            RuntimeOwner: owner,
            Route: route,
            ProtocolVersion: protocolVersion,
            LiveRouteEpoch: liveRouteEpoch,
            LegGeneration: legGeneration,
            TransportEpochId: transportEpoch,
            BridgeRecoveryGeneration: bridgeRecoveryGeneration,
            CheckpointRequestId: checkpointRequestId);

    private static FileTransferCoordinatorActionRequest FallbackActionRequest(
        FileTransferCoordinatorActionKind kind,
        RuntimeOwnerToken owner,
        FileTransferLeg leg,
        FileTransferRoute? route = null,
        int? protocolVersion = null,
        int? liveRouteEpoch = null,
        int? legGeneration = null,
        long? transportEpoch = null,
        int? bridgeRecoveryGeneration = null,
        string? checkpointRequestId = null,
        int? checkpointGeneration = null,
        FallbackControlPlaneToken? fallbackControlPlaneToken = null,
        FallbackCheckpointLease? fallbackCheckpointLease = null,
        FallbackControlPlanePressureToken? fallbackControlPlanePressureToken = null,
        FileTransferControlPlaneKind? controlPlaneKind = null,
        string? controlPlanePurpose = null,
        bool? peerVisible = null,
        bool? acceptedAny = null,
        FallbackTailYieldKind? fallbackTailYieldKind = null,
        FallbackTailYieldTransportEpochReauthorization?
            fallbackTailYieldTransportEpochReauthorization = null)
        => new(
            kind,
            Source: "test",
            Reason: "test",
            RuntimeOwner: owner,
            Route: route ?? leg.RouteSelection.Route,
            ProtocolVersion: protocolVersion ?? leg.ProtocolVersion,
            LiveRouteEpoch: liveRouteEpoch ?? leg.LiveRouteEpochId,
            LegGeneration: legGeneration ?? leg.Generation,
            TransportEpochId: transportEpoch ?? leg.TransportEpochId,
            BridgeRecoveryGeneration: bridgeRecoveryGeneration ?? leg.BridgeRecoveryGeneration,
            CheckpointRequestId: checkpointRequestId ?? leg.CheckpointRequestId,
            FallbackControlPlaneToken: fallbackControlPlaneToken,
            FallbackCheckpointLease: fallbackCheckpointLease,
            FallbackControlPlanePressureToken: fallbackControlPlanePressureToken,
            ControlPlaneKind: controlPlaneKind,
            ControlPlanePurpose: controlPlanePurpose,
            ControlPlanePeerVisible: peerVisible,
            ControlPlaneAcceptedAny: acceptedAny,
            CheckpointGeneration: checkpointGeneration ?? leg.CheckpointGeneration,
            FallbackTailYieldKind: fallbackTailYieldKind,
            FallbackTailYieldTransportEpochReauthorization:
                fallbackTailYieldTransportEpochReauthorization);

    private static FileTransferCoordinatorActionRequest RecoveryActionRequest(
        FileTransferCoordinatorActionKind kind,
        RuntimeOwnerToken owner,
        FileTransferLeg leg,
        string reason = "test",
        FileTransferRoute? route = null,
        int? protocolVersion = null,
        int? liveRouteEpoch = null,
        int? legGeneration = null,
        long? transportEpoch = null,
        int? bridgeRecoveryGeneration = null,
        int? routeCommitGeneration = null,
        RepairReplayKind? repairReplayKind = null,
        TransportEpochActionKind? transportEpochActionKind = null,
        BridgeRecoveryKind? bridgeRecoveryKind = null,
        RecoveryLivenessLease? recoveryLivenessLease = null,
        RuntimeUnlockRegularV4RecoveryLeaseClosureContinuationProof? closureContinuationProof = null,
        DateTimeOffset? livenessDeferralDeadlineUtc = null,
        int? recoveryDecisionGeneration = null,
        FileTransferRoute? targetRoute = null,
        int? targetProtocolVersion = null,
        FileTransferTransportKind? targetTransport = null,
        FileTransferAuthorityId? predecessorAuthorityId = null)
        => new(
            kind,
            Source: "test",
            Reason: reason,
            RuntimeOwner: owner,
            Route: route ?? leg.RouteSelection.Route,
            ProtocolVersion: protocolVersion ?? leg.ProtocolVersion,
            LiveRouteEpoch: liveRouteEpoch ?? leg.LiveRouteEpochId,
            LegGeneration: legGeneration ?? leg.Generation,
            TransportEpochId: transportEpoch ?? leg.TransportEpochId,
            BridgeRecoveryGeneration: bridgeRecoveryGeneration ?? leg.BridgeRecoveryGeneration,
            RouteCommitGeneration: routeCommitGeneration,
            TargetRoute: targetRoute,
            TargetProtocolVersion: targetProtocolVersion,
            TargetTransport: targetTransport,
            RepairReplayKind: repairReplayKind,
            TransportEpochActionKind: transportEpochActionKind,
            BridgeRecoveryKind: bridgeRecoveryKind,
            RecoveryLivenessLease: recoveryLivenessLease,
            RegularV4RecoveryLeaseClosureContinuationProof: closureContinuationProof,
            LivenessDeferralDeadlineUtc: livenessDeferralDeadlineUtc,
            RecoveryDecisionGeneration: recoveryDecisionGeneration,
            PredecessorAuthorityId: predecessorAuthorityId);

    private static (FileTransferCoordinatorState State, RepairReplayDecision Decision) AuthorizeFallbackFrontierRepair(
        FileTransferAuthorityId? predecessorAuthorityId = null)
    {
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var state = ActiveFallbackStateWithoutCheckpoint();
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;
        var publication = FileTransferCoordinator.AuthorizeAction(
            state,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "fallback_frontier_publication",
                RuntimeOwner: owner,
                Route: fallback.Route,
                ProtocolVersion: fallback.ProtocolVersion,
                LiveRouteEpoch: leg.LiveRouteEpochId,
                LegGeneration: leg.Generation,
                RuntimeGeneration: owner.RuntimeGeneration,
                TerminalGeneration: state.TerminalGeneration,
                TargetRoute: FileTransferRoute.PostTunaFallbackV6,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
                TargetTransport: FileTransferTransportKind.RegularNkn));
        Assert.True(publication.Accepted);

        var repair = FileTransferCoordinator.AuthorizeAction(
            publication.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                owner,
                publication.State.CurrentLeg!,
                repairReplayKind: RepairReplayKind.FrontierStallRepair,
                predecessorAuthorityId: predecessorAuthorityId));
        Assert.True(repair.Accepted);
        Assert.NotNull(repair.RepairReplayDecision);
        return (repair.State, repair.RepairReplayDecision!.Value);
    }

    private static (FileTransferCoordinatorState State, RepairReplayDecision Decision)
        AuthorizeRegularV4FrontierRepair(
            FileTransferCoordinatorState? currentState = null,
            FileTransferAuthorityId? predecessorAuthorityId = null)
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = currentState ?? StartRegular(regular).State;
        if (state.CurrentRoutePublicationToken is null)
        {
            var owner = state.CurrentRuntimeOwner!.Value;
            var leg = state.CurrentLeg!;
            var publication = FileTransferCoordinator.AuthorizeAction(
                state,
                new FileTransferCoordinatorActionRequest(
                    FileTransferCoordinatorActionKind.RoutePublication,
                    Source: "test",
                    Reason: "regular_v4_pressure_publication",
                    RuntimeOwner: owner,
                    Route: regular.Route,
                    ProtocolVersion: regular.ProtocolVersion,
                    LiveRouteEpoch: leg.LiveRouteEpochId,
                    LegGeneration: leg.Generation,
                    RuntimeGeneration: owner.RuntimeGeneration,
                    TerminalGeneration: state.TerminalGeneration));
            Assert.True(publication.Accepted);
            state = publication.State;
        }

        var repair = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                state.CurrentRuntimeOwner!.Value,
                state.CurrentLeg!,
                repairReplayKind: RepairReplayKind.V4Repair,
                predecessorAuthorityId: predecessorAuthorityId));
        Assert.True(repair.Accepted);
        Assert.NotNull(repair.RepairReplayDecision);
        return (repair.State, repair.RepairReplayDecision!.Value);
    }

    private static RegularV4FrontierRepairAuthorityRequest RegularV4PressureAuthorityRequest(
        string repairKey)
        => new(
            repairKey,
            FrontierChunkIndex: 12,
            DurableReceivedHighestChunkIndex: 13,
            PendingRepairCount: 2,
            DecisionReason: "regular_v4_frontier_repair",
            FrontierLagChunks: 7);

    private static FileTransferRegularV4FrontierRepairSettlement RegularV4PressureSettlement(
        FileTransferRegularV4FrontierRepairAuthority authority,
        FileTransferRegularV4FrontierRepairSettlementOutcome outcome,
        FileTransferAuthorityId? successorAuthorityId = null)
        => new(
            authority.SessionId,
            authority.TransferId,
            authority.Direction,
            authority.RouteToken,
            authority.ProtocolVersion,
            authority.LiveRouteEpoch,
            authority.RuntimeGeneration,
            authority.TerminalGeneration,
            authority.LegGeneration,
            authority.TransportEpoch,
            authority.BridgeRecoveryGeneration,
            authority.BridgeDecisionGeneration,
            authority.RoutePublicationGeneration,
            authority.RouteCommitGeneration,
            authority.RepairKey,
            authority.FrontierChunkIndex,
            authority.DurableReceivedHighestChunkIndex,
            authority.PendingRepairCount,
            authority.DecisionGeneration,
            authority.DecisionReason,
            outcome,
            authority.TargetTransport)
        {
            AuthorityId = authority.AuthorityId,
            SuccessorAuthorityId = successorAuthorityId,
        };

    private static FileTransferCoordinatorState StateWithInstalledRegularV4FrontierRepairPressure()
    {
        var (state, decision) = AuthorizeRegularV4FrontierRepair();
        var staged = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
            state,
            decision,
            RegularV4PressureAuthorityRequest("regular-v4-pressure:scope-invalidation"),
            "regular_v4_receiver_frontier_repair_due");
        var binding = staged.Binding!.Value;
        var committed = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
            staged.State,
            new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                binding.Authority,
                FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed,
                "installed",
                binding.InstallGeneration,
                binding.PressureReason));
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, committed.Status);
        return committed.State;
    }

    private static IReadOnlyList<(string BindingKind, FileTransferCoordinatorState State)>
        StatesWithRegularV4FrontierRepairPressure()
    {
        var (pendingSource, pendingDecision) = AuthorizeRegularV4FrontierRepair();
        var pending = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
            pendingSource,
            pendingDecision,
            RegularV4PressureAuthorityRequest("regular-v4-pressure:scope-pending"),
            "regular_v4_receiver_frontier_repair_due");
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, pending.Status);

        var installed = StateWithInstalledRegularV4FrontierRepairPressure();

        var retainedSource = StateWithInstalledRegularV4FrontierRepairPressure();
        var predecessor = PressureState(retainedSource).InstalledBinding!.Value;
        var (successorSource, successorDecision) = AuthorizeRegularV4FrontierRepair(
            retainedSource,
            predecessor.Authority.AuthorityId);
        var successorStage = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
            successorSource,
            successorDecision,
            RegularV4PressureAuthorityRequest("regular-v4-pressure:scope-successor"),
            "regular_v4_frontier_repair_successor_pressure");
        var successor = Assert.IsType<RegularV4FrontierRepairPressureBinding>(successorStage.Binding);
        var retained = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
            successorStage.State,
            new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                successor.Authority,
                FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed,
                "installed",
                successor.InstallGeneration,
                successor.PressureReason));
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, retained.Status);
        Assert.NotNull(PressureState(retained.State).RetainedPredecessorBinding);

        return
        [
            ("pending", pending.State),
            ("installed", installed),
            ("retained", retained.State),
        ];
    }

    private static void AssertPressureRetirementRequired(
        FileTransferCoordinatorState expectedState,
        FileTransferCoordinatorDecision decision,
        string scope)
    {
        Assert.Equal(
            "regular_v4_frontier_repair_pressure_retirement_required",
            decision.MutationRejectedReason);
        Assert.Same(expectedState, decision.State);
        Assert.False(decision.RouteChanged, scope);
        Assert.False(decision.TerminalMutationRejected, scope);
    }

    private static void AssertPressureRetirementRequired(
        FileTransferCoordinatorState expectedState,
        FileTransferCoordinatorActionDecision decision,
        string scope)
    {
        Assert.False(decision.Accepted, scope);
        Assert.Equal(
            "regular_v4_frontier_repair_pressure_retirement_required",
            decision.RejectionReason);
        Assert.Same(expectedState, decision.State);
        Assert.False(decision.TerminalMutationRejected, scope);
    }

    private static FileTransferCoordinatorState StateWithRecoveryAuthority(FileTransferCoordinatorState state)
    {
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;
        var repair = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                owner,
                leg,
                repairReplayKind: RepairReplayKind.V4Repair));
        state = repair.State;
        var transportEpoch = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.TransportEpochAdoption,
                owner,
                leg,
                transportEpochActionKind: TransportEpochActionKind.Adopt));
        state = transportEpoch.State;
        var bridge = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeRecovery,
                owner,
                leg,
                bridgeRecoveryKind: BridgeRecoveryKind.ReceiveRecoveryRequest));
        state = bridge.State;
        var liveness = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin,
                owner,
                leg));

        return liveness.State;
    }

    private static FileTransferCoordinatorState CreateActiveRegularV4RecoveryState()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = StartRegular(regular).State;
        var recovery = FileTransferCoordinator.AuthorizeAction(
            state,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery,
                state.CurrentRuntimeOwner!.Value,
                state.CurrentLeg!,
                bridgeRecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery));
        Assert.True(recovery.Accepted);
        Assert.NotNull(recovery.BridgeRecoveryDecision);
        Assert.NotNull(recovery.RecoveryLivenessLease);
        return recovery.State;
    }

    private static RuntimeUnlockRegularV4RecoveryLifecycleReport
        CreateRegularV4RecoveryLifecycleReport(
            FileTransferCoordinatorState state,
            RuntimeUnlockRegularV4RecoveryLifecycleOutcome outcome)
    {
        var owner = Assert.IsType<RuntimeOwnerToken>(state.CurrentRuntimeOwner);
        var leg = Assert.IsType<FileTransferLeg>(state.CurrentLeg);
        var bridge = Assert.IsType<BridgeRecoveryDecision>(state.CurrentBridgeRecoveryDecision);
        var authority = new FileTransferBridgeRecoveryAuthority(
            state.SessionId,
            state.TransferId,
            state.Direction,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            state.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            bridge.BridgeDecisionGeneration,
            bridge.RecoveryKind.ToString(),
            bridge.Reason);
        return new RuntimeUnlockRegularV4RecoveryLifecycleReport(
            new RuntimeUnlockRegularV4RecoveryAuthority(
                authority,
                Assert.IsType<RecoveryLivenessLease>(state.CurrentRecoveryLivenessLease)),
            outcome,
            $"bridge_lifecycle_{outcome.ToString().ToLowerInvariant()}");
    }

    private static (
        FileTransferCoordinatorState State,
        RuntimeUnlockRetryDispatchAuthorityRequest Request,
        RepairReplayDecision Release) AuthorizeRegularRuntimeUnlockRetryDispatchSource(
            long retiredOfferGeneration = 17)
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = StartRegular(regular).State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;
        var publicationDecision = FileTransferCoordinator.AuthorizeAction(
            state,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "regular_v4_retry_publication",
                RuntimeOwner: owner,
                Route: regular.Route,
                ProtocolVersion: regular.ProtocolVersion,
                LiveRouteEpoch: leg.LiveRouteEpochId,
                LegGeneration: leg.Generation,
                RuntimeGeneration: owner.RuntimeGeneration,
                TerminalGeneration: state.TerminalGeneration,
                TargetRoute: FileTransferRoute.RegularNknV4Fast,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.RegularNkn));
        Assert.True(publicationDecision.Accepted);
        var repairDecision = FileTransferCoordinator.AuthorizeAction(
            publicationDecision.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RepairReplay,
                owner,
                publicationDecision.State.CurrentLeg!,
                repairReplayKind: RepairReplayKind.V4Repair));
        Assert.True(repairDecision.Accepted);
        var release = repairDecision.RepairReplayDecision!.Value;
        var publication = repairDecision.State.CurrentRoutePublicationToken!.Value;
        var publicPublication = new FileTransferRoutePublicationAuthority(
            publication.SessionId,
            publication.TransferId,
            publication.Direction,
            publication.RouteToken,
            publication.ProtocolVersion,
            publication.LiveRouteEpoch,
            publication.RuntimeGeneration,
            publication.TerminalGeneration,
            publication.LegGeneration,
            publication.PublicationGeneration,
            publication.RouteCommitGeneration,
            FileTransferRouteResolver.Resolve(publication.TargetRoute).TelemetryToken,
            publication.TargetProtocolVersion,
            publication.TargetTransport,
            publication.ActionKind.ToString(),
            publication.Reason);
        var request = new RuntimeUnlockRetryDispatchAuthorityRequest(
            repairDecision.State.SessionId,
            repairDecision.State.TransferId,
            repairDecision.State.Direction,
            publicPublication,
            RecoveryContractGeneration: 41,
            RetiredOfferGeneration: retiredOfferGeneration,
            DispatchAttemptGeneration: 1,
            RetryReason: "runtime_unlock_offer_send_not_observed",
            RecoveryReason: "regular_v4_repair_settled",
            RegularV4LivenessGeneration: 0,
            BridgeDecisionGeneration: release.BridgeDecisionGeneration);
        var handoff = CreateRegularV4RecoverySettlementReport(
            repairDecision.State,
            request,
            release,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved);
        return (
            repairDecision.State with
            {
                LastRuntimeUnlockRegularV4RecoverySettlement = handoff,
                LastCoordinatorIssuedRegularV4RecoverySettlement = handoff.Settlement,
                RuntimeUnlockRegularV4RecoverySettlementAvailable = true,
            },
            request,
            release);
    }

    private static (
        FileTransferCoordinatorState State,
        RuntimeUnlockRetryDispatchAuthorityRequest Request,
        FallbackTailYieldDecision TailYield) AuthorizeFallbackRuntimeUnlockRetryDispatchSource(
            FileTransferCoordinatorState? sourceState = null)
    {
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var state = sourceState ?? ActiveFallbackStateWithoutCheckpoint();
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;
        var publication = FileTransferCoordinator.AuthorizeAction(
            state,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "fallback_retry_publication",
                RuntimeOwner: owner,
                Route: fallback.Route,
                ProtocolVersion: fallback.ProtocolVersion,
                LiveRouteEpoch: leg.LiveRouteEpochId,
                LegGeneration: leg.Generation,
                RuntimeGeneration: owner.RuntimeGeneration,
                TerminalGeneration: state.TerminalGeneration,
                TargetRoute: fallback.Route,
                TargetProtocolVersion: fallback.ProtocolVersion,
                TargetTransport: FileTransferTransportKind.RegularNkn));
        Assert.True(publication.Accepted);

        var yielded = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailQuiescent,
                fallback,
                reason: "fallback_tail_quiescent",
                state: FileTransferLegState.Active,
                canSendData: true,
                committedChunk: Math.Max(0, leg.ProvenCommittedChunkIndex),
                highestObservedChunk: leg.ProvenHighestObservedChunkIndex,
                transportEpoch: leg.TransportEpochId,
                bridgeRecoveryGeneration: leg.BridgeRecoveryGeneration),
            publication.State);
        Assert.Null(yielded.MutationRejectedReason);
        var tailYield = Assert.IsType<FallbackTailYieldDecision>(yielded.FallbackTailYieldDecision);
        var survivalState = tailYield.FallbackSurvivalState with
        {
            PairedFallbackYieldGranted = true,
        };
        state = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockFallbackSurvivalUpdated,
                fallback,
                reason: "paired_fallback_tail_yield_granted",
                state: yielded.State.CurrentLeg!.State,
                canSendData: yielded.State.CurrentLeg.CanSendData,
                transportEpoch: yielded.State.CurrentLeg.TransportEpochId,
                bridgeRecoveryGeneration: yielded.State.CurrentLeg.BridgeRecoveryGeneration,
                runtimeUnlockFallbackSurvivalState: survivalState),
            yielded.State).State;
        var pairedYieldEvidence =
            CreateFallbackPairedTailYieldReleaseEvidence(state, tailYield);
        Assert.True(
            FileTransferCoordinator
                .TryCreateFallbackPairedTailYieldRuntimeUnlockRetryDispatchRequest(
                    state,
                    tailYield,
                    pairedYieldEvidence,
                    "runtime_unlock_offer_send_not_observed",
                    "post_tuna_fallback_tail_unblocked",
                    out var request,
                    out var requestRejectionReason),
            requestRejectionReason);
        return (state, request, tailYield);
    }

    private static FallbackPairedTailYieldReleaseEvidence
        CreateFallbackPairedTailYieldReleaseEvidence(
            FileTransferCoordinatorState state,
            FallbackTailYieldDecision tailYield)
    {
        var leg = Assert.IsType<FileTransferLeg>(state.CurrentLeg);
        const string peerAuthority =
            "fta2-fallback_tail_yield-0123456789abcdef01234567";
        var peerOwnerKey = FileTransferCoordinator.BuildFallbackTailYieldOwnerKey(
            state.SessionId,
            state.TransferId,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            tailYield.CurrentLiveRouteEpoch,
            leg.Generation,
            leg.BridgeRecoveryGeneration,
            leg.TransportEpochId,
            tailYield.CheckpointRequestIdSnapshot);
        return new FallbackPairedTailYieldReleaseEvidence(
            tailYield,
            new FileTransferAuthorityId(peerAuthority),
            PeerPredecessorAuthorityId: null,
            PeerYieldGeneration: 1,
            state.Direction == FileTransferDirection.Outbound
                ? FileTransferDirection.Inbound.ToString().ToLowerInvariant()
                : FileTransferDirection.Outbound.ToString().ToLowerInvariant(),
            peerOwnerKey,
            tailYield.PairKey,
            tailYield.CurrentLiveRouteEpoch,
            leg.Generation,
            leg.BridgeRecoveryGeneration,
            leg.TransportEpochId,
            tailYield.CheckpointRequestIdSnapshot);
    }

    private static RuntimeUnlockRegularV4RecoverySettlementReport
        CreateRegularV4RecoverySettlementReport(
            FileTransferCoordinatorState state,
            RuntimeUnlockRetryDispatchAuthorityRequest request,
            RepairReplayDecision release,
            FileTransferRegularV4FrontierRepairSettlementOutcome outcome,
            RuntimeUnlockRegularV4RecoverySettlementDisposition disposition =
                RuntimeUnlockRegularV4RecoverySettlementDisposition.Consumed,
            string reason = "regular_v4_recovery_settlement_consumed")
    {
        var leg = Assert.IsType<FileTransferLeg>(state.CurrentLeg);
        var settlement = new FileTransferRegularV4FrontierRepairSettlement(
            state.SessionId,
            state.TransferId,
            state.Direction,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0,
            state.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            release.BridgeDecisionGeneration,
            release.RoutePublicationToken?.PublicationGeneration ?? 0,
            release.RoutePublicationToken?.RouteCommitGeneration,
            "regular-v4-frontier:0:test",
            FrontierChunkIndex: 0,
            DurableReceivedHighestChunkIndex: -1,
            RequestedChunkCount: 1,
            release.RepairGeneration,
            "regular_v4_repair_settled",
            outcome,
            FileTransferTransportKind.RegularNkn)
        {
            AuthorityId = release.AuthorityId,
        };
        return new RuntimeUnlockRegularV4RecoverySettlementReport(
            settlement,
            request.RegularV4LivenessGeneration,
            request.BridgeDecisionGeneration,
            disposition,
            reason);
    }

    private static RegularV4FrontierRepairPressureSubstate PressureState(
        FileTransferCoordinatorState state)
        => FileTransferCoordinator.GetRegularV4FrontierRepairPressureSubstate(state);

    private static FileTransferCoordinatorEvent CoordinatorEvent(
        FileTransferCoordinatorEventKind kind,
        FileTransferRouteSelection routeSelection,
        FileTransferTransportHandoffKind handoffKind = FileTransferTransportHandoffKind.None,
        FileTransferTransportKind targetTransport = FileTransferTransportKind.Unknown,
        string reason = "test",
        FileTransferLegState state = FileTransferLegState.Active,
        bool canSendData = true,
        int committedChunk = 0,
        int highestObservedChunk = -1,
        long transportEpoch = 0,
        int bridgeRecoveryGeneration = 0,
        string? checkpointRequestId = null,
        string? checkpointPriority = null,
        RuntimeUnlockRouteCommitProof? runtimeUnlockCommitProof = null,
        RuntimeUnlockAdmissionRequest? runtimeUnlockAdmissionRequest = null,
        RuntimeUnlockFallbackSurvivalState? runtimeUnlockFallbackSurvivalState = null,
        FallbackControlPlaneToken? fallbackControlPlaneToken = null,
        FallbackCheckpointLease? fallbackCheckpointLease = null,
        FallbackControlPlanePressureToken? fallbackControlPlanePressureToken = null,
        FileTransferControlPlaneKind controlPlaneKind = FileTransferControlPlaneKind.Unknown,
        string? controlPlanePurpose = null,
        bool controlPlanePeerVisible = false,
        bool controlPlaneAcceptedAny = false,
        FallbackTailYieldKind fallbackTailYieldKind = FallbackTailYieldKind.Unknown,
        FileTransferInitialTunaReadinessEvidence? initialTunaReadinessEvidence = null,
        bool allowInitialRouteFallback = false,
        FileTransferRouteSelection? initialRouteFallbackSelection = null,
        RuntimeUnlockPendingPreCommitProbeRequest? runtimeUnlockPendingPreCommitProbeRequest = null,
        FallbackTailYieldTransportEpochReauthorizationRequest?
            fallbackTailYieldTransportEpochReauthorizationRequest = null,
        FallbackTailYieldTransportEpochReauthorization?
            fallbackTailYieldTransportEpochReauthorization = null)
        => new(
            kind,
            routeSelection,
            handoffKind,
            targetTransport,
            reason,
            state,
            canSendData,
            committedChunk,
            highestObservedChunk,
            transportEpoch,
            bridgeRecoveryGeneration,
            checkpointRequestId,
            checkpointPriority,
            RuntimeUnlockCommitProof: runtimeUnlockCommitProof,
            RuntimeUnlockAdmissionRequest: runtimeUnlockAdmissionRequest,
            RuntimeUnlockFallbackSurvivalState: runtimeUnlockFallbackSurvivalState,
            FallbackControlPlaneToken: fallbackControlPlaneToken,
            FallbackCheckpointLease: fallbackCheckpointLease,
            FallbackControlPlanePressureToken: fallbackControlPlanePressureToken,
            ControlPlaneKind: controlPlaneKind,
            ControlPlanePurpose: controlPlanePurpose,
            ControlPlanePeerVisible: controlPlanePeerVisible,
            ControlPlaneAcceptedAny: controlPlaneAcceptedAny,
            FallbackTailYieldKind: fallbackTailYieldKind,
            InitialTunaReadinessEvidence: initialTunaReadinessEvidence,
            AllowInitialRouteFallback: allowInitialRouteFallback,
            InitialRouteFallbackSelection: initialRouteFallbackSelection,
            RuntimeUnlockPendingPreCommitProbeRequest: runtimeUnlockPendingPreCommitProbeRequest,
            FallbackTailYieldTransportEpochReauthorizationRequest:
                fallbackTailYieldTransportEpochReauthorizationRequest,
            FallbackTailYieldTransportEpochReauthorization:
                fallbackTailYieldTransportEpochReauthorization);
}
