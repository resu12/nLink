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

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaActivated,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock",
                FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 4,
                highestObservedChunk: 7),
            started.State);

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
    public void RuntimeOwnerToken_OldOwnerInvalidAfterRouteTransition()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var oldOwner = started.State.CurrentRuntimeOwner!.Value;

        var activated = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaActivated,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock",
                FileTransferLegState.Active,
                canSendData: true),
            started.State);

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
        Assert.Equal(8, quiescent.FallbackTailYieldDecision.Value.PairKey.Split('|').Length);
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
        var successorRequest = request with
        {
            RecoveryContractGeneration = request.RecoveryContractGeneration + 1,
            DispatchAttemptGeneration = request.DispatchAttemptGeneration + 1,
            PredecessorAuthorityId = first.Contract!.Authority.AuthorityId,
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
        Assert.True(first.Contract.Lifecycle.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.Superseded,
            "test_successor"));
        var successor = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            first.State,
            successorRequest,
            first.Contract,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            release.AuthorityId,
            release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(successor.Granted);
        Assert.Equal(first.Contract.Authority.AuthorityId, successor.Contract!.Authority.PredecessorAuthorityId);
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
        var firstBegin = FileTransferCoordinator.AuthorizeAction(
            publicationDecision.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin,
                owner,
                publicationDecision.State.CurrentLeg!));
        var firstLease = firstBegin.RecoveryLivenessLease!.Value;
        var firstRetire = FileTransferCoordinator.AuthorizeAction(
            firstBegin.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseRetire,
                owner,
                firstBegin.State.CurrentLeg!,
                recoveryLivenessLease: firstLease));
        var secondBegin = FileTransferCoordinator.AuthorizeAction(
            firstRetire.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin,
                owner,
                firstRetire.State.CurrentLeg!));
        var secondLease = secondBegin.RecoveryLivenessLease!.Value;
        var secondRetire = FileTransferCoordinator.AuthorizeAction(
            secondBegin.State,
            RecoveryActionRequest(
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseRetire,
                owner,
                secondBegin.State.CurrentLeg!,
                recoveryLivenessLease: secondLease));
        var publication = secondRetire.State.CurrentRoutePublicationToken!.Value;
        var request = new RuntimeUnlockRetryDispatchAuthorityRequest(
            secondRetire.State.SessionId,
            secondRetire.State.TransferId,
            secondRetire.State.Direction,
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
            RegularV4LivenessGeneration: secondLease.LeaseGeneration,
            BridgeDecisionGeneration: 0);

        var stale = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            secondRetire.State,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure,
            firstLease.AuthorityId,
            firstLease.LeaseGeneration,
            RecoveryLivenessLeaseState.Retired.ToString());
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, stale.Status);
        Assert.Equal("receive_recovery_lease_stale", stale.Reason);

        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            secondRetire.State,
            request,
            null,
            RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure,
            secondLease.AuthorityId,
            secondLease.LeaseGeneration,
            RecoveryLivenessLeaseState.Retired.ToString());
        Assert.True(granted.Granted);
        Assert.Equal(secondLease.AuthorityId, granted.Contract!.Authority.ReleaseAuthorityId);
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
        var activated = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaActivated,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock",
                FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 9,
                highestObservedChunk: 11),
            StartRegular(regular).State);

        var fallbackDecision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaLost,
                fallback,
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn,
                "tuna_disabled",
                FileTransferLegState.CheckpointPending,
                canSendData: false,
                committedChunk: 9,
                highestObservedChunk: 11,
                transportEpoch: 3,
                bridgeRecoveryGeneration: 3),
            activated.State);

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
        var activated = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaActivated,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock",
                FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 9,
                highestObservedChunk: 11),
            StartRegular(regular).State);
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
        Assert.Same(previousLeg, next.FrozenLeg);
        Assert.Equal(FileTransferLegState.Frozen, previousLeg.State);
        Assert.False(previousLeg.CanSendData);
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
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaActivated,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock",
                FileTransferLegState.Active,
                canSendData: true,
                committedChunk: committedChunk,
                highestObservedChunk: committedChunk),
            state);

    private static FileTransferCoordinatorDecision FallBack(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection fallback,
        int committedChunk,
        long transportEpoch)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaLost,
                fallback,
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn,
                "tuna_disabled",
                FileTransferLegState.CheckpointPending,
                canSendData: false,
                committedChunk: committedChunk,
                highestObservedChunk: committedChunk,
                transportEpoch: transportEpoch,
                bridgeRecoveryGeneration: (int)transportEpoch),
            state);

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
        FallbackTailYieldKind? fallbackTailYieldKind = null)
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
            FallbackTailYieldKind: fallbackTailYieldKind);

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

    private static (
        FileTransferCoordinatorState State,
        RuntimeUnlockRetryDispatchAuthorityRequest Request,
        RepairReplayDecision Release) AuthorizeRegularRuntimeUnlockRetryDispatchSource()
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
            RetiredOfferGeneration: 17,
            DispatchAttemptGeneration: 1,
            RetryReason: "runtime_unlock_offer_send_not_observed",
            RecoveryReason: "regular_v4_repair_settled",
            RegularV4LivenessGeneration: 0,
            BridgeDecisionGeneration: release.BridgeDecisionGeneration);
        return (repairDecision.State, request, release);
    }

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
        RuntimeUnlockPendingPreCommitProbeRequest? runtimeUnlockPendingPreCommitProbeRequest = null)
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
            RuntimeUnlockPendingPreCommitProbeRequest: runtimeUnlockPendingPreCommitProbeRequest);
}
