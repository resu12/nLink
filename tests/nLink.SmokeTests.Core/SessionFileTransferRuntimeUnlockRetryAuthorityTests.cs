using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferRuntimeUnlockRetryAuthorityTests :
    SessionFileTransferServiceTestBase
{
    [Fact]
    public void AttachAndDetach_SubscribeAndUnsubscribeRetryAuthorityBridgeEvents()
    {
        using var bridge = new RetryAuthorityLoopbackTransport("session_retry_subscriptions");
        using var service = new SessionFileTransferService();

        service.AttachTransport(bridge);

        Assert.Equal(1, bridge.AuthorityRequestSubscriberCount);
        Assert.Equal(1, bridge.SettlementReportSubscriberCount);
        Assert.Equal(1, bridge.DeadlineEvaluationSubscriberCount);
        Assert.Equal(1, bridge.ExecutionObservationSubscriberCount);
        Assert.Equal(1, bridge.SuccessorEvaluationSubscriberCount);
        Assert.Equal(1, bridge.RegularV4SettlementReportSubscriberCount);
        Assert.Equal(1, bridge.RegularV4LifecycleReportSubscriberCount);

        service.DetachTransport();

        Assert.Equal(0, bridge.AuthorityRequestSubscriberCount);
        Assert.Equal(0, bridge.SettlementReportSubscriberCount);
        Assert.Equal(0, bridge.DeadlineEvaluationSubscriberCount);
        Assert.Equal(0, bridge.ExecutionObservationSubscriberCount);
        Assert.Equal(0, bridge.SuccessorEvaluationSubscriberCount);
        Assert.Equal(0, bridge.RegularV4SettlementReportSubscriberCount);
        Assert.Equal(0, bridge.RegularV4LifecycleReportSubscriberCount);
    }

    [Fact]
    public async Task GrantedDecision_IsStagedThenInstalledOnceUnderCoreGate()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_two_phase_install",
            "transfer_retry_two_phase_install");
        fixture.Bridge.CoreGateForInstallProbe = GetCoreGate(fixture.Service);
        var logStart = GetOperationalLogLength();
        Assert.True(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));

        fixture.Bridge.RaiseAuthorityRequest(fixture.InitialRequest);

        var decision = fixture.Bridge.DequeueAuthorityDecision();
        var stage = fixture.Bridge.DequeueStageResult();
        var install = fixture.Bridge.DequeueInstallResult();
        var contract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(decision.Contract);
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Granted, decision.Status);
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStageStatus.Staged, stage.Status);
        Assert.Equal(decision, stage.Decision);
        Assert.Equal(RuntimeUnlockRetryDispatchStageDisposition.Retained, stage.StageDisposition);
        Assert.False(stage.StageCleared);
        Assert.True(stage.StagedUtcMs > 0);
        Assert.True(stage.StageExpiresUtcMs > stage.StagedUtcMs);
        Assert.Equal(RuntimeUnlockRetryDispatchInstallStatus.Installed, install.Status);
        Assert.Equal(contract.Authority, install.Receipt.Authority);
        Assert.Equal(1, install.Receipt.InstallGeneration);
        Assert.Equal(fixture.InitialRequest, install.Receipt.Request);
        Assert.Equal(stage.Status, install.Receipt.StageStatus);
        Assert.Equal(stage.StageDisposition, install.Receipt.StageDisposition);
        Assert.Equal(stage.StageCleared, install.Receipt.StageCleared);
        Assert.Equal(stage.StagedUtcMs, install.Receipt.StagedUtcMs);
        Assert.Equal(stage.StageExpiresUtcMs, install.Receipt.StageExpiresUtcMs);
        Assert.True(fixture.Bridge.LastInstallObservedUnderCoreGate);
        Assert.True(fixture.Bridge.LastReceiptIssuedEvidenceObservedBeforeCommit);
        Assert.Equal(1, fixture.Bridge.InstallCount);
        Assert.Same(contract, fixture.Bridge.InfraCurrentContract);
        Assert.Equal(contract.Authority, GetCurrentAttempt(fixture.Context)?.Authority);
        Assert.Equal(install.Receipt, GetCurrentInstallReceipt(fixture.Context));
        Assert.False(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));

        var duplicate = fixture.Bridge.CommitRuntimeUnlockRetryDispatchInstall(install.Receipt);

        Assert.Equal(RuntimeUnlockRetryDispatchInstallStatus.Idempotent, duplicate.Status);
        Assert.Equal(1, fixture.Bridge.InstallCount);
        Assert.Same(contract, fixture.Bridge.InfraCurrentContract);
        Assert.Equal(install.Receipt, GetCurrentInstallReceipt(fixture.Context));

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains(
            "event=runtime_unlock_retry_dispatch_install_receipt_issued;",
            logTail,
            StringComparison.Ordinal);
        Assert.Contains(
            $"transfer_id={fixture.TransferId};",
            logTail,
            StringComparison.Ordinal);
        Assert.Contains(
            $"install_generation={install.Receipt.InstallGeneration};",
            logTail,
            StringComparison.Ordinal);
        Assert.Contains(
            $"request_regular_v4_liveness_generation={fixture.InitialRequest.RegularV4LivenessGeneration};",
            logTail,
            StringComparison.Ordinal);
        Assert.Contains(
            $"request_bridge_decision_generation={fixture.InitialRequest.BridgeDecisionGeneration};",
            logTail,
            StringComparison.Ordinal);
        Assert.Contains(
            $"request_active_predecessor_supersession={(fixture.InitialRequest.RequestActivePredecessorSupersession ? 1 : 0)};",
            logTail,
            StringComparison.Ordinal);
        Assert.Contains(
            $"current_authority_id={install.Receipt.Authority.AuthorityId};",
            logTail,
            StringComparison.Ordinal);
        Assert.Contains("stage_status=Staged;", logTail, StringComparison.Ordinal);
        Assert.Contains("stage_disposition=Retained;", logTail, StringComparison.Ordinal);
        Assert.Contains("stage_cleared=0;", logTail, StringComparison.Ordinal);
        Assert.Contains(
            $"stage_expires_utc_ms={install.Receipt.StageExpiresUtcMs};",
            logTail,
            StringComparison.Ordinal);
        Assert.Contains("runtime_owner_session_id=", logTail, StringComparison.Ordinal);
        Assert.Contains("source_publication_session_id=", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactDuplicateRequest_ReusesCommittedReceiptWithoutAdvancingGeneration()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_install_duplicate",
            "transfer_retry_install_duplicate");
        _ = Grant(fixture, fixture.InitialRequest);
        var firstReceipt = Assert.IsType<RuntimeUnlockRetryDispatchInstallReceipt>(
            fixture.Bridge.LastCommittedReceipt);

        fixture.Bridge.RaiseAuthorityRequest(fixture.InitialRequest);

        var duplicateDecision = fixture.Bridge.DequeueAuthorityDecision();
        var duplicateStage = fixture.Bridge.DequeueStageResult();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Granted, duplicateDecision.Status);
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStageStatus.Idempotent, duplicateStage.Status);
        Assert.Equal(
            RuntimeUnlockRetryDispatchStageDisposition.AlreadyInstalled,
            duplicateStage.StageDisposition);
        Assert.False(duplicateStage.StageCleared);
        Assert.Equal(0, duplicateStage.StageExpiresUtcMs);
        Assert.Equal(
            firstReceipt,
            GetCurrentInstallReceipt(fixture.Context));
        Assert.Equal(firstReceipt.InstallGeneration, GetLastInstallGeneration(fixture.Context));
        Assert.Equal(1, fixture.Bridge.InstallCount);
    }

    [Fact]
    public async Task TerminalizationBetweenStageAndRevalidation_PreventsInstall()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_stage_terminal",
            "transfer_retry_stage_terminal");
        using var allowStageReturn = new ManualResetEventSlim(initialState: false);
        fixture.Bridge.StageReturnBarrier = allowStageReturn;

        var request = Task.Run(() => fixture.Bridge.RaiseAuthorityRequest(fixture.InitialRequest));
        Assert.True(fixture.Bridge.StageObserved.Wait(TimeSpan.FromSeconds(3)));
        var stagedDecision = fixture.Bridge.DequeueAuthorityDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Granted, stagedDecision.Status);
        Assert.Null(fixture.Bridge.InfraCurrentContract);
        Assert.Null(GetCurrentInstallReceipt(fixture.Context));
        Assert.Equal(0, fixture.Bridge.InstallCount);
        Assert.True(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));

        var terminal = await fixture.Service.CancelTransferAsync(
            fixture.TransferId,
            "test_terminal_between_retry_stage_and_install",
            CancellationToken.None);
        Assert.NotNull(terminal);

        allowStageReturn.Set();
        await request.WaitAsync(TimeSpan.FromSeconds(3));
        var stage = fixture.Bridge.DequeueStageResult();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStageStatus.Staged, stage.Status);
        Assert.Equal(0, fixture.Bridge.InstallCount);
        Assert.Null(fixture.Bridge.InfraCurrentContract);
        Assert.Null(GetCurrentAttempt(fixture.Context));
        Assert.Null(GetCurrentInstallReceipt(fixture.Context));
        Assert.False(fixture.Bridge.HasPendingStage);
    }

    [Fact]
    public async Task DataSessionReplacementBetweenStageAndRevalidation_PreventsInstall()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_stage_data_session_replaced",
            "transfer_retry_stage_data_session_replaced");
        using var allowStageReturn = new ManualResetEventSlim(initialState: false);
        fixture.Bridge.StageReturnBarrier = allowStageReturn;

        var request = Task.Run(() => fixture.Bridge.RaiseAuthorityRequest(fixture.InitialRequest));
        Assert.True(fixture.Bridge.StageObserved.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStatus.Granted,
            fixture.Bridge.DequeueAuthorityDecision().Status);

        var coreGate = GetCoreGate(fixture.Service);
        lock (coreGate)
        {
            var currentGeneration = GetRequiredProperty<long>(
                fixture.Context,
                "DataSessionGeneration");
            SetProperty(
                fixture.Context,
                "DataSessionGeneration",
                currentGeneration + 1);
        }

        allowStageReturn.Set();
        await request.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStageStatus.Staged,
            fixture.Bridge.DequeueStageResult().Status);
        Assert.Equal(0, fixture.Bridge.InstallCount);
        Assert.Null(fixture.Bridge.InfraCurrentContract);
        Assert.Null(GetCurrentAttempt(fixture.Context));
        Assert.Null(GetCurrentInstallReceipt(fixture.Context));
        Assert.False(fixture.Bridge.HasPendingStage);

        var installRejection = fixture.Bridge.DequeueAuthorityDecision();
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
            installRejection.Status);
        Assert.Equal(
            "retry_dispatch_install_data_session_replaced",
            installRejection.Reason);

        fixture.Bridge.RaiseAuthorityRequest(fixture.InitialRequest);

        var replacementDecision = fixture.Bridge.DequeueAuthorityDecision();
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
            replacementDecision.Status);
        Assert.Equal(
            "retry_dispatch_attempt_already_settled",
            replacementDecision.Reason);
        Assert.Equal(0, fixture.Bridge.InstallCount);
        Assert.Null(fixture.Bridge.InfraCurrentContract);
        Assert.Null(GetCurrentAttempt(fixture.Context));
        Assert.Null(GetCurrentInstallReceipt(fixture.Context));
    }

    [Theory]
    [InlineData("route_publication")]
    [InlineData("runtime_owner")]
    [InlineData("terminal_generation")]
    [InlineData("transfer_leg")]
    [InlineData("bridge_decision")]
    [InlineData("recovery_contract")]
    public async Task CurrentnessReplacementBetweenStageAndRevalidation_PreventsInstall(
        string replacementKind)
    {
        using var fixture = await CreateFixtureAsync(
            $"session_retry_stage_{replacementKind}",
            $"transfer_retry_stage_{replacementKind}");
        using var allowStageReturn = new ManualResetEventSlim(initialState: false);
        fixture.Bridge.StageReturnBarrier = allowStageReturn;

        var request = Task.Run(() => fixture.Bridge.RaiseAuthorityRequest(fixture.InitialRequest));
        Assert.True(fixture.Bridge.StageObserved.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStatus.Granted,
            fixture.Bridge.DequeueAuthorityDecision().Status);

        lock (GetCoreGate(fixture.Service))
        {
            ReplaceRuntimeUnlockRetryDispatchInstallCurrentness(
                fixture.Context,
                replacementKind);
        }

        allowStageReturn.Set();
        await request.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStageStatus.Staged,
            fixture.Bridge.DequeueStageResult().Status);
        var rejection = fixture.Bridge.DequeueAuthorityDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, rejection.Status);
        if (replacementKind == "recovery_contract")
        {
            Assert.StartsWith(
                "retry_dispatch_stage_",
                rejection.Reason,
                StringComparison.Ordinal);
        }
        else if (replacementKind == "bridge_decision")
        {
            Assert.Equal(
                "retry_dispatch_install_receipt_bridge_decision_generation",
                rejection.Reason);
        }
        else
        {
            Assert.Equal(
                "retry_dispatch_runtime_scope_mismatch",
                rejection.Reason);
        }
        Assert.Equal(0, fixture.Bridge.InstallCount);
        Assert.False(fixture.Bridge.HasPendingStage);
        Assert.Null(fixture.Bridge.InfraCurrentContract);
        Assert.Null(GetCurrentAttempt(fixture.Context));
        Assert.Null(GetCurrentInstallReceipt(fixture.Context));
    }

    [Fact]
    public async Task PredecessorLineageReplacementBetweenStageAndRevalidation_PreventsInstall()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_stage_predecessor_replaced",
            "transfer_retry_stage_predecessor_replaced");
        var predecessor = Grant(
            fixture,
            fixture.InitialRequest with
            {
                ListenerRearmRequirement =
                    RuntimeUnlockRetryDispatchListenerRearmRequirement.Required,
            });
        var predecessorOfferGeneration =
            checked(fixture.InitialRequest.RetiredOfferGeneration + 1);
        BindActivePredecessorForSuccessor(
            fixture,
            predecessor,
            predecessorOfferGeneration);
        var successorEvaluation = new RuntimeUnlockRetryDispatchSuccessorEvaluation(
            predecessor.Authority,
            predecessorOfferGeneration);
        using var allowStageReturn = new ManualResetEventSlim(initialState: false);
        fixture.Bridge.StageReturnBarrier = allowStageReturn;
        fixture.Bridge.StageObserved.Reset();

        var request = Task.Run(
            () => fixture.Bridge.RaiseSuccessorEvaluation(successorEvaluation));
        Assert.True(fixture.Bridge.StageObserved.Wait(TimeSpan.FromSeconds(3)));
        var successorDecision = fixture.Bridge.DequeueSuccessorDecision();
        Assert.Equal(
            RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Requested,
            successorDecision.Status);
        Assert.Equal(
            predecessor.Authority.AuthorityId,
            successorDecision.Request?.PredecessorAuthorityId);
        Assert.True(
            successorDecision.Request?.RequestActivePredecessorSupersession);
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStatus.Granted,
            fixture.Bridge.DequeueAuthorityDecision().Status);
        Assert.False(predecessor.Lifecycle.IsSettled);
        Assert.Equal(
            predecessor.Authority,
            GetPendingPredecessorAttempt(fixture.Context)?.Authority);

        lock (GetCoreGate(fixture.Service))
        {
            var persistentState = GetCoordinatorPersistentState(fixture.Context);
            SetCoordinatorPersistentState(
                fixture.Context,
                persistentState with
                {
                    RuntimeUnlockRetryLifecycle =
                        persistentState.RuntimeUnlockRetryLifecycle with
                        {
                            InstalledAttempt = null,
                        },
                });
        }

        allowStageReturn.Set();
        await request.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStageStatus.Staged,
            fixture.Bridge.DequeueStageResult().Status);
        var rejection = fixture.Bridge.DequeueAuthorityDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, rejection.Status);
        Assert.StartsWith(
            "retry_dispatch_install_receipt_",
            rejection.Reason,
            StringComparison.Ordinal);
        Assert.Equal(1, fixture.Bridge.InstallCount);
        Assert.False(fixture.Bridge.HasPendingStage);
        Assert.Null(fixture.Bridge.InfraCurrentContract);
        Assert.Null(GetCurrentAttempt(fixture.Context));
        Assert.Null(GetCurrentInstallReceipt(fixture.Context));
        Assert.Null(GetPendingPredecessorAttempt(fixture.Context));
    }

    [Fact]
    public async Task CommitBarrier_HoldsCoreLockUntilInstallLinearizes()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_commit_barrier",
            "transfer_retry_commit_barrier");
        using var allowCommit = new ManualResetEventSlim(initialState: false);
        fixture.Bridge.CoreGateForInstallProbe = GetCoreGate(fixture.Service);
        fixture.Bridge.CommitReturnBarrier = allowCommit;

        var request = Task.Run(() => fixture.Bridge.RaiseAuthorityRequest(fixture.InitialRequest));
        Assert.True(fixture.Bridge.CommitObserved.Wait(TimeSpan.FromSeconds(3)));
        Assert.True(fixture.Bridge.LastInstallObservedUnderCoreGate);

        var terminal = Task.Run(async () => await fixture.Service.CancelTransferAsync(
            fixture.TransferId,
            "test_terminal_waits_for_retry_install_commit",
            CancellationToken.None));
        await Task.Delay(100);
        Assert.False(terminal.IsCompleted);
        Assert.Equal(0, fixture.Bridge.InstallCount);

        allowCommit.Set();
        await request.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(await terminal.WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.Equal(1, fixture.Bridge.InstallCount);
        Assert.Null(GetCurrentAttempt(fixture.Context));
        Assert.Null(GetCurrentInstallReceipt(fixture.Context));
    }

    [Fact]
    public async Task StageOrInstallRejection_FailsClosedWithoutInstallingInfraAuthority()
    {
        using (var stageFixture = await CreateFixtureAsync(
                   "session_retry_stage_rejected",
                   "transfer_retry_stage_rejected"))
        {
            stageFixture.Bridge.RejectNextStage = true;

            stageFixture.Bridge.RaiseAuthorityRequest(stageFixture.InitialRequest);

            Assert.Equal(
                RuntimeUnlockRetryDispatchDecisionStatus.Granted,
                stageFixture.Bridge.DequeueAuthorityDecision().Status);
            var stageRejection = stageFixture.Bridge.DequeueStageResult();
            Assert.Equal(
                RuntimeUnlockRetryDispatchDecisionStageStatus.Rejected,
                stageRejection.Status);
            Assert.Equal(
                RuntimeUnlockRetryDispatchStageDisposition.Rejected,
                stageRejection.StageDisposition);
            Assert.False(stageRejection.StageCleared);
            Assert.Equal(0, stageRejection.StageExpiresUtcMs);
            Assert.Equal(0, stageFixture.Bridge.InstallCount);
            Assert.Null(stageFixture.Bridge.InfraCurrentContract);
            Assert.Null(GetCurrentAttempt(stageFixture.Context));
            Assert.Null(GetCurrentInstallReceipt(stageFixture.Context));
        }

        using var installFixture = await CreateFixtureAsync(
            "session_retry_install_rejected",
            "transfer_retry_install_rejected");
        installFixture.Bridge.RejectNextInstall = true;

        installFixture.Bridge.RaiseAuthorityRequest(installFixture.InitialRequest);

        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStatus.Granted,
            installFixture.Bridge.DequeueAuthorityDecision().Status);
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStageStatus.Staged,
            installFixture.Bridge.DequeueStageResult().Status);
        Assert.Equal(
            RuntimeUnlockRetryDispatchInstallStatus.Rejected,
            installFixture.Bridge.DequeueInstallResult().Status);
        Assert.Equal(0, installFixture.Bridge.InstallCount);
        Assert.Null(installFixture.Bridge.InfraCurrentContract);
        Assert.Null(GetCurrentAttempt(installFixture.Context));
        Assert.Null(GetCurrentInstallReceipt(installFixture.Context));
        Assert.Equal(1, GetLastInstallGeneration(installFixture.Context));
    }

    [Fact]
    public async Task ConfirmationRejection_InvalidatesIssuedAttemptWithoutInstallingInfraAuthority()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_confirmation_rejected",
            "transfer_retry_confirmation_rejected");
        fixture.Bridge.RejectNextConfirmation = true;

        fixture.Bridge.RaiseAuthorityRequest(fixture.InitialRequest);

        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStatus.Granted,
            fixture.Bridge.DequeueAuthorityDecision().Status);
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStageStatus.Staged,
            fixture.Bridge.DequeueStageResult().Status);
        Assert.Equal(
            RuntimeUnlockRetryDispatchInstallStatus.Installed,
            fixture.Bridge.DequeueInstallResult().Status);
        var rejection = fixture.Bridge.DequeueAuthorityDecision();
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
            rejection.Status);
        Assert.Equal(
            "retry_dispatch_install_confirmation_rejected",
            rejection.Reason);
        Assert.Equal(0, fixture.Bridge.InstallCount);
        Assert.False(fixture.Bridge.HasPendingStage);
        Assert.Null(fixture.Bridge.InfraCurrentContract);
        Assert.Null(fixture.Bridge.LastCommittedReceipt);
        Assert.Null(GetCurrentAttempt(fixture.Context));
        Assert.Null(GetCurrentInstallReceipt(fixture.Context));
        Assert.Equal(1, GetLastInstallGeneration(fixture.Context));
    }

    [Fact]
    public async Task SuccessorConfirmationRejection_PreservesInstalledPredecessor()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_successor_confirmation_rejected",
            "transfer_retry_successor_confirmation_rejected");
        var predecessor = Grant(
            fixture,
            fixture.InitialRequest with
            {
                ListenerRearmRequirement =
                    RuntimeUnlockRetryDispatchListenerRearmRequirement.Required,
            });
        var predecessorReceipt = Assert.IsType<RuntimeUnlockRetryDispatchInstallReceipt>(
            fixture.Bridge.LastCommittedReceipt);
        fixture.Bridge.RejectNextConfirmation = true;
        var predecessorOfferGeneration =
            checked(fixture.InitialRequest.RetiredOfferGeneration + 1);
        BindActivePredecessorForSuccessor(
            fixture,
            predecessor,
            predecessorOfferGeneration);

        fixture.Bridge.RaiseSuccessorEvaluation(
            new RuntimeUnlockRetryDispatchSuccessorEvaluation(
                predecessor.Authority,
                predecessorOfferGeneration));

        Assert.Equal(
            RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Requested,
            fixture.Bridge.DequeueSuccessorDecision().Status);
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStatus.Granted,
            fixture.Bridge.DequeueAuthorityDecision().Status);
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStageStatus.Staged,
            fixture.Bridge.DequeueStageResult().Status);
        Assert.Equal(
            RuntimeUnlockRetryDispatchInstallStatus.Installed,
            fixture.Bridge.DequeueInstallResult().Status);
        var rejection = fixture.Bridge.DequeueAuthorityDecision();
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
            rejection.Status);
        Assert.Equal(
            "retry_dispatch_install_confirmation_rejected",
            rejection.Reason);
        Assert.False(predecessor.Lifecycle.IsSettled);
        Assert.Same(predecessor, fixture.Bridge.InfraCurrentContract);
        Assert.Equal(predecessorReceipt, fixture.Bridge.LastCommittedReceipt);
        Assert.Equal(predecessor.Authority, GetCurrentAttempt(fixture.Context)?.Authority);
        Assert.Equal(predecessorReceipt, GetCurrentInstallReceipt(fixture.Context));
        Assert.Null(GetPendingPredecessorAttempt(fixture.Context));
        Assert.Equal(2, GetLastInstallGeneration(fixture.Context));
        Assert.Equal(1, fixture.Bridge.InstallCount);
        Assert.False(fixture.Bridge.HasPendingStage);
    }

    [Fact]
    public async Task SuccessorInstall_UsesNextGenerationAndCannotBeClearedByOldReceipt()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_install_successor",
            "transfer_retry_install_successor");
        var predecessor = Grant(
            fixture,
            fixture.InitialRequest with
            {
                ListenerRearmRequirement =
                    RuntimeUnlockRetryDispatchListenerRearmRequirement.Required,
            });
        var predecessorReceipt = Assert.IsType<RuntimeUnlockRetryDispatchInstallReceipt>(
            fixture.Bridge.LastCommittedReceipt);
        var successorLogStart = GetOperationalLogLength();
        var predecessorOfferGeneration =
            checked(fixture.InitialRequest.RetiredOfferGeneration + 1);
        BindActivePredecessorForSuccessor(
            fixture,
            predecessor,
            predecessorOfferGeneration);
        var successorEvaluation = new RuntimeUnlockRetryDispatchSuccessorEvaluation(
            predecessor.Authority,
            predecessorOfferGeneration);

        using var allowStageReturn = new ManualResetEventSlim(initialState: false);
        fixture.Bridge.StageReturnBarrier = allowStageReturn;
        fixture.Bridge.StageObserved.Reset();
        var successorRequestTask = Task.Run(
            () => fixture.Bridge.RaiseSuccessorEvaluation(successorEvaluation));
        Assert.True(fixture.Bridge.StageObserved.Wait(TimeSpan.FromSeconds(3)));
        var successorLifecycleDecision = fixture.Bridge.DequeueSuccessorDecision();
        Assert.Equal(
            RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Requested,
            successorLifecycleDecision.Status);
        var successorDecision = fixture.Bridge.DequeueAuthorityDecision();
        var successor = Assert.IsType<RuntimeUnlockRetryDispatchContract>(
            successorDecision.Contract);
        Assert.False(predecessor.Lifecycle.IsSettled);
        Assert.Same(predecessor, fixture.Bridge.InfraCurrentContract);
        Assert.Equal(
            predecessor.Authority,
            GetPendingPredecessorAttempt(fixture.Context)?.Authority);

        allowStageReturn.Set();
        await successorRequestTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStageStatus.Staged,
            fixture.Bridge.DequeueStageResult().Status);
        Assert.Equal(
            RuntimeUnlockRetryDispatchInstallStatus.Installed,
            fixture.Bridge.DequeueInstallResult().Status);
        fixture.Bridge.FlushInstallFollowUp();
        var successorReceipt = Assert.IsType<RuntimeUnlockRetryDispatchInstallReceipt>(
            fixture.Bridge.LastCommittedReceipt);

        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementOutcome.Superseded,
            predecessor.Lifecycle.SettlementOutcome);
        Assert.Equal(
            "coordinator_successor_installed",
            predecessor.Lifecycle.SettlementReason);
        Assert.Equal(
            successor.Authority.AuthorityId,
            predecessor.Lifecycle.SuccessorAuthorityId);
        Assert.Null(GetPendingPredecessorAttempt(fixture.Context));
        Assert.Equal(predecessorReceipt.InstallGeneration + 1, successorReceipt.InstallGeneration);
        Assert.Equal(successor.Authority, successorReceipt.Authority);
        var late = fixture.Bridge.CommitRuntimeUnlockRetryDispatchInstall(predecessorReceipt);
        Assert.Equal(RuntimeUnlockRetryDispatchInstallStatus.Rejected, late.Status);
        Assert.Equal(2, fixture.Bridge.InstallCount);
        Assert.Same(successor, fixture.Bridge.InfraCurrentContract);
        Assert.Equal(successor.Authority, GetCurrentAttempt(fixture.Context)?.Authority);
        Assert.Equal(successorReceipt, GetCurrentInstallReceipt(fixture.Context));

        var successorLogTail = ReadOperationalLogTail(successorLogStart);
        Assert.Contains(
            $"predecessor_authority_id={predecessor.Authority.AuthorityId};",
            successorLogTail,
            StringComparison.Ordinal);
        Assert.Contains(
            "request_active_predecessor_supersession=1;",
            successorLogTail,
            StringComparison.Ordinal);
        Assert.Contains(
            $"install_generation={successorReceipt.InstallGeneration};",
            successorLogTail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettlementReport_IsProcessedUnderCoreGate_ReturnedAfterGate_AndDuplicateIsIdempotent()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_settlement",
            "transfer_retry_settlement");
        var contract = Grant(fixture, fixture.InitialRequest);
        var report = SettleMissingDispatch(fixture, contract, offerGeneration: 101);
        var coreGate = GetCoreGate(fixture.Service);
        fixture.Bridge.CoreGateForSettlementDecisionProbe = coreGate;

        Task delivery;
        Monitor.Enter(coreGate);
        try
        {
            delivery = Task.Run(() => fixture.Bridge.RaiseSettlementReport(report));
            Assert.False(
                fixture.Bridge.SettlementDecisionObserved.Wait(TimeSpan.FromMilliseconds(100)),
                "Settlement escaped the SessionFileTransferService transfer lock.");
        }
        finally
        {
            Monitor.Exit(coreGate);
        }

        await delivery.WaitAsync(TimeSpan.FromSeconds(3));
        var accepted = fixture.Bridge.DequeueSettlementDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, accepted.Status);
        Assert.Equal(report, GetLastSettlement(fixture.Context));
        Assert.Equal(contract.Authority, GetCurrentAttempt(fixture.Context)?.Authority);
        Assert.True(fixture.Bridge.LastSettlementDecisionObservedWithCoreGateAvailable);

        fixture.Bridge.RaiseSettlementReport(report);

        var duplicate = fixture.Bridge.DequeueSettlementDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent, duplicate.Status);
        Assert.Equal("retry_dispatch_settlement_duplicate", duplicate.Reason);
        Assert.Equal(contract.Authority, GetCurrentAttempt(fixture.Context)?.Authority);
        Assert.Equal(report, GetLastSettlement(fixture.Context));
    }

    [Fact]
    public async Task RegularV4RecoverySettlementReport_IsProcessedUnderCoreGate_AndAuthorizesExactRequest()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_regular_v4_handoff",
            "transfer_retry_regular_v4_handoff");
        var report = SeedRegularV4RecoverySettlement(fixture.Context);
        var coreGate = GetCoreGate(fixture.Service);

        Task delivery;
        Monitor.Enter(coreGate);
        try
        {
            delivery = Task.Run(() => fixture.Bridge.RaiseRegularV4SettlementReport(report));
            Assert.False(
                delivery.Wait(TimeSpan.FromMilliseconds(100)),
                "Regular-V4 recovery settlement escaped the Core transfer lock.");
        }
        finally
        {
            Monitor.Exit(coreGate);
        }

        await delivery.WaitAsync(TimeSpan.FromSeconds(3));
        var accepted = fixture.Bridge.DequeueRegularV4SettlementDecision();
        Assert.Equal(
            RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Accepted,
            accepted.Status);
        Assert.Equal(report, GetLastRegularV4Settlement(fixture.Context));

        fixture.Bridge.RaiseRegularV4SettlementReport(report);
        var duplicate = fixture.Bridge.DequeueRegularV4SettlementDecision();
        Assert.Equal(
            RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Idempotent,
            duplicate.Status);
        Assert.True(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRegularV4RecoverySettlementAvailable"));

        var settlementRequest = fixture.InitialRequest with
        {
            RegularV4LivenessGeneration = report.RegularV4LivenessGeneration,
            BridgeDecisionGeneration = report.BridgeDecisionGeneration,
        };
        var contract = Grant(fixture, settlementRequest);
        Assert.Equal(
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            contract.Authority.ReleaseKind);
        Assert.Equal(report.Settlement.AuthorityId, contract.Authority.ReleaseAuthorityId);
        Assert.Equal(report.Settlement.DecisionGeneration, contract.Authority.ReleaseGeneration);
        Assert.False(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRegularV4RecoverySettlementAvailable"));
    }

    [Fact]
    public async Task RegularV4RecoveryLifecycleReport_IsProcessedUnderCoreGate_AndRetiresLeaseOnce()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_regular_v4_lifecycle",
            "transfer_retry_regular_v4_lifecycle");
        var report = SeedRegularV4RecoveryLifecycle(fixture.Context);
        var coreGate = GetCoreGate(fixture.Service);

        Task delivery;
        Monitor.Enter(coreGate);
        try
        {
            delivery = Task.Run(() => fixture.Bridge.RaiseRegularV4LifecycleReport(report));
            Assert.False(
                delivery.Wait(TimeSpan.FromMilliseconds(100)),
                "Regular-V4 recovery lifecycle escaped the Core transfer lock.");
        }
        finally
        {
            Monitor.Exit(coreGate);
        }

        await delivery.WaitAsync(TimeSpan.FromSeconds(3));
        var accepted = fixture.Bridge.DequeueRegularV4LifecycleDecision();
        Assert.Equal(RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted, accepted.Status);
        Assert.Null(GetOptionalStructProperty<RecoveryLivenessLease>(fixture.Context, "CurrentRecoveryLivenessLease"));
        Assert.NotNull(GetOptionalStructProperty<RecoveryLivenessLease>(fixture.Context, "LastRetiredRecoveryLivenessLease"));
        Assert.Equal(
            report,
            GetOptionalStructProperty<RuntimeUnlockRegularV4RecoveryLifecycleReport>(
                fixture.Context,
                "LastRuntimeUnlockRegularV4RecoveryLifecycle"));
        Assert.True(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));

        fixture.Bridge.RaiseRegularV4LifecycleReport(report);
        var duplicate = fixture.Bridge.DequeueRegularV4LifecycleDecision();
        Assert.Equal(RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Idempotent, duplicate.Status);
        Assert.True(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));
    }

    [Fact]
    public async Task BridgeRecoveryExecutionCompletion_ReceiptPrecedesLifecycle_AndDuplicateIsIdempotent()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_bridge_execution_completion",
            "transfer_retry_bridge_execution_completion");
        var (request, receipt) = SeedRunningBridgeRecoveryExecution(
            fixture.Context,
            FileTransferBridgeRecoveryExecutionOutcome.Completed);

        await ObserveBridgeRecoveryExecutionCompletionAsync(fixture, request, receipt);

        Assert.Equal("receipt", fixture.Bridge.DequeueBridgeRecoveryCompletionObservation());
        Assert.Equal("lifecycle", fixture.Bridge.DequeueBridgeRecoveryCompletionObservation());
        var acceptedReceipt = fixture.Bridge.DequeueBridgeRecoveryExecutionReceiptDecision();
        Assert.Equal(receipt, acceptedReceipt.Receipt);
        Assert.True(acceptedReceipt.Accepted);
        Assert.Equal(
            "bridge_recovery_execution_receipt_accepted",
            acceptedReceipt.Reason);
        var acceptedLifecycle = fixture.Bridge.DequeueRegularV4LifecycleDecision();
        Assert.Equal(
            RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted,
            acceptedLifecycle.Status);
        Assert.Equal(receipt, acceptedLifecycle.ExecutionReceipt);
        Assert.True(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));
        var retainedLease = Assert.IsType<RecoveryLivenessLease>(
            GetOptionalStructProperty<RecoveryLivenessLease>(
                fixture.Context,
                "LastRetiredRecoveryLivenessLease"));

        await ObserveBridgeRecoveryExecutionCompletionAsync(fixture, request, receipt);

        Assert.Equal("receipt", fixture.Bridge.DequeueBridgeRecoveryCompletionObservation());
        Assert.Equal("lifecycle", fixture.Bridge.DequeueBridgeRecoveryCompletionObservation());
        var duplicateReceipt = fixture.Bridge.DequeueBridgeRecoveryExecutionReceiptDecision();
        Assert.Equal(receipt, duplicateReceipt.Receipt);
        Assert.True(duplicateReceipt.Accepted);
        Assert.Equal(
            "bridge_recovery_execution_receipt_duplicate",
            duplicateReceipt.Reason);
        var duplicateLifecycle = fixture.Bridge.DequeueRegularV4LifecycleDecision();
        Assert.Equal(
            RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Idempotent,
            duplicateLifecycle.Status);
        Assert.Equal(receipt, duplicateLifecycle.ExecutionReceipt);
        Assert.Equal(
            retainedLease,
            GetOptionalStructProperty<RecoveryLivenessLease>(
                fixture.Context,
                "LastRetiredRecoveryLivenessLease"));
        Assert.True(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));
    }

    [Theory]
    [InlineData("Exhausted")]
    [InlineData("Discarded")]
    public async Task BridgeRecoveryExecutionCompletion_NonCompletedReceiptRetiresLifecycleWithoutClosure(
        string outcomeName)
    {
        var outcome = Enum.Parse<FileTransferBridgeRecoveryExecutionOutcome>(outcomeName);
        using var fixture = await CreateFixtureAsync(
            $"session_retry_bridge_execution_{outcome.ToString().ToLowerInvariant()}",
            $"transfer_retry_bridge_execution_{outcome.ToString().ToLowerInvariant()}");
        var (request, receipt) = SeedRunningBridgeRecoveryExecution(
            fixture.Context,
            outcome);

        await ObserveBridgeRecoveryExecutionCompletionAsync(fixture, request, receipt);

        Assert.Equal("receipt", fixture.Bridge.DequeueBridgeRecoveryCompletionObservation());
        Assert.Equal("lifecycle", fixture.Bridge.DequeueBridgeRecoveryCompletionObservation());
        var observedReceipt = fixture.Bridge.DequeueBridgeRecoveryExecutionReceiptDecision();
        Assert.Equal(receipt, observedReceipt.Receipt);
        Assert.True(observedReceipt.Accepted);
        var lifecycle = fixture.Bridge.DequeueRegularV4LifecycleDecision();
        Assert.Equal(
            RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted,
            lifecycle.Status);
        Assert.Equal(receipt, lifecycle.ExecutionReceipt);
        Assert.Equal(
            outcome == FileTransferBridgeRecoveryExecutionOutcome.Exhausted
                ? RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Exhausted
                : RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Discarded,
            lifecycle.Report.Outcome);
        Assert.Null(GetOptionalStructProperty<RecoveryLivenessLease>(
            fixture.Context,
            "CurrentRecoveryLivenessLease"));
        Assert.NotNull(GetOptionalStructProperty<RecoveryLivenessLease>(
            fixture.Context,
            "LastRetiredRecoveryLivenessLease"));
        Assert.False(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));
    }

    [Fact]
    public async Task BridgeRecoveryExecutionCompletion_ReceiptNotificationFailureWithholdsLifecycle()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_bridge_execution_receipt_notification_failure",
            "transfer_retry_bridge_execution_receipt_notification_failure");
        var (request, receipt) = SeedRunningBridgeRecoveryExecution(
            fixture.Context,
            FileTransferBridgeRecoveryExecutionOutcome.Completed);
        fixture.Bridge.ThrowOnBridgeRecoveryExecutionReceiptDecision = true;

        await ObserveBridgeRecoveryExecutionCompletionAsync(fixture, request, receipt);

        Assert.Equal("receipt", fixture.Bridge.DequeueBridgeRecoveryCompletionObservation());
        Assert.Equal(0, fixture.Bridge.RegularV4LifecycleDecisionCount);
        Assert.Contains(
            "event=filetransfer_bridge_recovery_execution_receipt_notification_failed;",
            LocalOperationalLog.GetRecentLogText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BridgeRecoveryExecutionCompletion_InboundReceiptPrecedesLifecycle()
    {
        const string sessionId = "session_retry_bridge_execution_completion_inbound";
        const string transferId = "transfer_retry_bridge_execution_completion_inbound";
        using var bridge = new RetryAuthorityLoopbackTransport(sessionId);
        using var peerTransport = new LoopbackFileTransferTransport(sessionId);
        using var service = new SessionFileTransferService();
        using var peerService = new SessionFileTransferService();
        bridge.Connect(peerTransport);
        service.AttachTransport(bridge);
        peerService.AttachTransport(peerTransport);
        var started = await peerService.TryStartSendAsync(
            new FileTransferSendDescriptor("inbound-recovery.bin", 1, transferId, 4096),
            _ => Task.FromResult<Stream>(new MemoryStream([0x33], writable: false)),
            CancellationToken.None);
        Assert.NotNull(started);
        await WaitUntilAsync(() => service.Snapshot.Inbound?.TransferId == transferId);
        var context = GetInboundContext(service);
        var (request, receipt) = SeedRunningBridgeRecoveryExecution(
            context,
            FileTransferBridgeRecoveryExecutionOutcome.Completed,
            FileTransferDirection.Inbound);

        await ObserveBridgeRecoveryExecutionCompletionAsync(service, bridge, request, receipt);

        Assert.Equal("receipt", bridge.DequeueBridgeRecoveryCompletionObservation());
        Assert.Equal("lifecycle", bridge.DequeueBridgeRecoveryCompletionObservation());
        var receiptDecision = bridge.DequeueBridgeRecoveryExecutionReceiptDecision();
        Assert.True(receiptDecision.Accepted);
        Assert.Equal(receipt, receiptDecision.Receipt);
        var lifecycle = bridge.DequeueRegularV4LifecycleDecision();
        Assert.Equal(FileTransferDirection.Inbound, lifecycle.Report.Authority.Direction);
        Assert.Equal(receipt, lifecycle.ExecutionReceipt);
        Assert.Equal(
            RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted,
            lifecycle.Status);
    }

    [Fact]
    public async Task RecoveryLeaseClosureContinuation_DefersOnlyWithinExactRetainedProofWindow()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_closure_liveness",
            "transfer_retry_closure_liveness");
        var lifecycle = Assert.IsType<RuntimeUnlockRegularV4RecoveryLifecycleReport>(
            GetOptionalStructProperty<RuntimeUnlockRegularV4RecoveryLifecycleReport>(
                fixture.Context,
                "LastRuntimeUnlockRegularV4RecoveryLifecycle"));
        var retiredLease = Assert.IsType<RecoveryLivenessLease>(
            GetOptionalStructProperty<RecoveryLivenessLease>(
                fixture.Context,
                "LastRetiredRecoveryLivenessLease"));
        var leg = GetRequiredProperty<FileTransferLeg>(fixture.Context, "CurrentTransferLeg");
        var route = GetRequiredProperty<FileTransferRouteSelection>(fixture.Context, "RouteSelection");
        var maximumDeadline = DateTimeOffset.UtcNow.AddSeconds(90);
        var proof = new RuntimeUnlockRegularV4RecoveryLeaseClosureContinuationProof(
            lifecycle,
            retiredLease,
            RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted,
            "regular_v4_recovery_lifecycle_accepted",
            maximumDeadline);
        var snapshot = new FileTransferRecoveryLivenessSnapshot(
            fixture.InitialRequest.SessionId,
            fixture.TransferId,
            route.TelemetryToken,
            route.ProtocolVersion,
            leg.LiveRouteEpochId,
            leg.Generation,
            leg.BridgeRecoveryGeneration,
            leg.TransportEpochId,
            leg.CheckpointRequestId,
            "bridge_lifecycle_completed",
            FileTransferRecoveryLivenessState.BridgeRecoveryCompletedAwaitingProof,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            maximumDeadline,
            BridgeRecoveryRequested: true,
            BridgeRecoveryStarted: true,
            BridgeRecoveryCompleted: true,
            ReceiveProofObserved: false,
            RecoveryExhausted: false,
            AuthorityCompleted: false,
            TerminalRecommended: false)
        {
            RegularV4RecoveryLeaseClosureContinuationProof = proof,
        };

        Assert.True(fixture.Service.TryAuthorizeRecoveryLivenessDeferral(
            snapshot,
            "session_liveness_recovery_state",
            maximumDeadline,
            out var grant));
        Assert.NotNull(grant);
        Assert.Equal(retiredLease.LeaseGeneration, grant!.LeaseGeneration);
        Assert.Equal(RecoveryLivenessLeaseState.Retired.ToString(), grant.LeaseState);
        Assert.Null(GetOptionalStructProperty<RecoveryLivenessLease>(
            fixture.Context,
            "CurrentRecoveryLivenessLease"));
        Assert.Equal(retiredLease, GetOptionalStructProperty<RecoveryLivenessLease>(
            fixture.Context,
            "LastRetiredRecoveryLivenessLease"));
        Assert.True(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));

        Assert.True(fixture.Service.TryAuthorizeRecoveryLivenessDeferral(
            snapshot,
            "session_liveness_recovery_state",
            maximumDeadline,
            out var duplicateGrant));
        Assert.Equal(grant, duplicateGrant);

        Assert.False(fixture.Service.TryAuthorizeRecoveryLivenessDeferral(
            snapshot,
            "session_liveness_recovery_state",
            maximumDeadline.AddMinutes(7),
            out var excessiveGrant));
        Assert.Null(excessiveGrant);
        Assert.Null(GetOptionalStructProperty<RecoveryLivenessLease>(
            fixture.Context,
            "CurrentRecoveryLivenessLease"));

        Assert.False(fixture.Service.TryAuthorizeRecoveryLivenessDeferral(
            snapshot with { RegularV4RecoveryLeaseClosureContinuationProof = null },
            "session_liveness_recovery_state",
            maximumDeadline,
            out var syntheticGrant));
        Assert.Null(syntheticGrant);

        Assert.False(fixture.Service.TryAuthorizeRecoveryLivenessDeferral(
            snapshot with
            {
                State = FileTransferRecoveryLivenessState.BridgeRecoveryStarted,
            },
            "session_liveness_recovery_state",
            maximumDeadline,
            out var wrongStateGrant));
        Assert.Null(wrongStateGrant);
        Assert.True(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));
    }

    [Fact]
    public async Task RecoveryLeaseClosureContinuation_SameTransferIdInBothDirectionsTargetsProofDirection()
    {
        const string sessionId = "session_retry_closure_direction_collision";
        const string transferId = "transfer_retry_closure_direction_collision";
        using var localTransport = new LoopbackFileTransferTransport(sessionId);
        using var peerTransport = new LoopbackFileTransferTransport(sessionId);
        localTransport.Connect(peerTransport);
        using var localService = new SessionFileTransferService();
        using var peerService = new SessionFileTransferService();
        localService.AttachTransport(localTransport);
        peerService.AttachTransport(peerTransport);

        var localOutbound = await localService.TryStartSendAsync(
            new FileTransferSendDescriptor("local-outbound.bin", 1, transferId, 4096),
            _ => Task.FromResult<Stream>(new MemoryStream([0x11], writable: false)),
            CancellationToken.None);
        var peerOutbound = await peerService.TryStartSendAsync(
            new FileTransferSendDescriptor("peer-outbound.bin", 1, transferId, 4096),
            _ => Task.FromResult<Stream>(new MemoryStream([0x22], writable: false)),
            CancellationToken.None);
        Assert.NotNull(localOutbound);
        Assert.NotNull(peerOutbound);
        await WaitUntilAsync(() =>
            localService.Snapshot.Inbound?.TransferId == transferId);

        var outboundContext = GetOutboundContext(localService);
        var inboundContext = GetInboundContext(localService);
        SeedRetiredRecoveryLivenessLease(
            inboundContext,
            FileTransferDirection.Inbound);
        var lifecycle = Assert.IsType<RuntimeUnlockRegularV4RecoveryLifecycleReport>(
            GetOptionalStructProperty<RuntimeUnlockRegularV4RecoveryLifecycleReport>(
                inboundContext,
                "LastRuntimeUnlockRegularV4RecoveryLifecycle"));
        var retiredLease = Assert.IsType<RecoveryLivenessLease>(
            GetOptionalStructProperty<RecoveryLivenessLease>(
                inboundContext,
                "LastRetiredRecoveryLivenessLease"));
        var leg = GetRequiredProperty<FileTransferLeg>(inboundContext, "CurrentTransferLeg");
        var route = GetRequiredProperty<FileTransferRouteSelection>(inboundContext, "RouteSelection");
        var maximumDeadline = DateTimeOffset.UtcNow.AddSeconds(90);
        var proof = new RuntimeUnlockRegularV4RecoveryLeaseClosureContinuationProof(
            lifecycle,
            retiredLease,
            RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted,
            "regular_v4_recovery_lifecycle_accepted",
            maximumDeadline);
        var snapshot = new FileTransferRecoveryLivenessSnapshot(
            sessionId,
            transferId,
            route.TelemetryToken,
            route.ProtocolVersion,
            leg.LiveRouteEpochId,
            leg.Generation,
            leg.BridgeRecoveryGeneration,
            leg.TransportEpochId,
            leg.CheckpointRequestId,
            "bridge_lifecycle_completed",
            FileTransferRecoveryLivenessState.BridgeRecoveryCompletedAwaitingProof,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            maximumDeadline,
            BridgeRecoveryRequested: true,
            BridgeRecoveryStarted: true,
            BridgeRecoveryCompleted: true,
            ReceiveProofObserved: false,
            RecoveryExhausted: false,
            AuthorityCompleted: false,
            TerminalRecommended: false)
        {
            RegularV4RecoveryLeaseClosureContinuationProof = proof,
        };

        Assert.True(localService.TryAuthorizeRecoveryLivenessDeferral(
            snapshot,
            "session_liveness_recovery_state",
            maximumDeadline,
            out var grant));
        Assert.NotNull(grant);
        Assert.Equal(retiredLease.LeaseGeneration, grant!.LeaseGeneration);
        Assert.True(GetRequiredProperty<bool>(
            inboundContext,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));
        Assert.Null(GetOptionalStructProperty<RecoveryLivenessLease>(
            outboundContext,
            "LastRetiredRecoveryLivenessLease"));
    }

    [Fact]
    public async Task AuthorityRequest_FromStaleBridge_IsRejectedWithoutInstallingContract()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_stale_bridge",
            "transfer_retry_stale_bridge");
        using var staleBridge = new RetryAuthorityLoopbackTransport(
            "session_retry_stale_bridge");
        var handler = typeof(SessionFileTransferService).GetMethod(
            "OnRuntimeUnlockRetryDispatchAuthorityRequested",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);

        handler!.Invoke(
            fixture.Service,
            [
                staleBridge,
                new RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs(
                    fixture.InitialRequest),
            ]);

        var rejected = staleBridge.DequeueAuthorityDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, rejected.Status);
        Assert.Equal("retry_dispatch_transfer_context_missing", rejected.Reason);
        Assert.Null(GetCurrentAttempt(fixture.Context));
        Assert.Null(fixture.Bridge.InfraCurrentContract);
    }

    [Fact]
    public async Task ExactHeldFallbackRequest_IsGrantedAfterPairedTailReleaseBecomesCurrent()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_fallback_tail_recheck",
            "transfer_retry_fallback_tail_recheck");
        var (request, tailYield) = SeedPostTunaFallbackRetrySource(fixture.Context);

        fixture.Bridge.RaiseAuthorityRequest(request);

        var held = fixture.Bridge.DequeueAuthorityDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Held, held.Status);
        Assert.Equal("fallback_paired_tail_yield_pending", held.Reason);
        Assert.Equal(request, held.Request);
        Assert.Null(GetCurrentAttempt(fixture.Context));
        Assert.Null(fixture.Bridge.InfraCurrentContract);

        var leg = GetRequiredProperty<FileTransferLeg>(fixture.Context, "CurrentTransferLeg");
        var priorTransportEpoch = leg.TransportEpochId;
        leg.TransportEpochId++;
        SeedPairedFallbackTailYield(fixture.Context, tailYield);

        fixture.Bridge.RaiseAuthorityRequest(request);

        var granted = fixture.Bridge.DequeueAuthorityDecision();
        Assert.True(
            granted.Granted,
            $"status={granted.Status} reason={granted.Reason}");
        Assert.Equal(request, granted.Request);
        Assert.Equal(priorTransportEpoch + 1, granted.Contract!.Authority.TransportEpoch);
        Assert.Equal(tailYield.AuthorityId, granted.Contract.Authority.ReleaseAuthorityId);
        Assert.Equal(tailYield.YieldGeneration, granted.Contract.Authority.ReleaseGeneration);
        Assert.Equal(
            granted.Contract!.Authority,
            GetCurrentAttempt(fixture.Context)?.Authority);
        Assert.Same(granted.Contract, fixture.Bridge.InfraCurrentContract);
    }

    [Fact]
    public async Task LateSupersededPredecessorReport_IsDiagnosticOnlyAndCannotClearSuccessor()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_late_predecessor",
            "transfer_retry_late_predecessor");
        var predecessor = Grant(
            fixture,
            fixture.InitialRequest with
            {
                ListenerRearmRequirement =
                    RuntimeUnlockRetryDispatchListenerRearmRequirement.Required,
            });
        var predecessorOfferGeneration =
            checked(fixture.InitialRequest.RetiredOfferGeneration + 1);
        BindActivePredecessorForSuccessor(
            fixture,
            predecessor,
            predecessorOfferGeneration);
        var successor = GrantSuccessor(
            fixture,
            predecessor,
            predecessorOfferGeneration);
        Assert.True(predecessor.TryCreateSettlementReport(out var predecessorReport));
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementOutcome.Superseded,
            predecessorReport.Outcome);

        fixture.Bridge.RaiseSettlementReport(predecessorReport);

        var decision = fixture.Bridge.DequeueSettlementDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent, decision.Status);
        Assert.Equal(successor.Authority, GetCurrentAttempt(fixture.Context)?.Authority);
        Assert.Equal(predecessorReport, GetLastSettlement(fixture.Context));
        Assert.Same(successor, fixture.Bridge.InfraCurrentContract);

        var mismatchedLateReport = predecessorReport with
        {
            Reason = "late_predecessor_mismatch",
        };
        fixture.Bridge.RaiseSettlementReport(mismatchedLateReport);

        var rejected = fixture.Bridge.DequeueSettlementDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected, rejected.Status);
        Assert.Equal(successor.Authority, GetCurrentAttempt(fixture.Context)?.Authority);
        Assert.Equal(predecessorReport, GetLastSettlement(fixture.Context));
        Assert.Same(successor, fixture.Bridge.InfraCurrentContract);
    }

    [Fact]
    public async Task TerminalInvalidation_ClearsAggregateAndLateContractReportIsRejected()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_terminal",
            "transfer_retry_terminal");
        var contract = Grant(fixture, fixture.InitialRequest);

        var terminal = await fixture.Service.CancelTransferAsync(
            fixture.TransferId,
            "test_terminal_before_retry_settlement",
            CancellationToken.None);

        Assert.NotNull(terminal);
        Assert.Null(GetCurrentAttempt(fixture.Context));
        Assert.False(contract.TryCreateSettlementReport(out _));
        Assert.Null(GetLastSettlement(fixture.Context));

        var terminalReport = new RuntimeUnlockRetryDispatchSettlementReport(
            contract.Authority,
            RuntimeUnlockRetryDispatchSettlementOutcome.TransferTerminal,
            "test_terminal_before_retry_settlement");

        fixture.Bridge.RaiseSettlementReport(terminalReport);

        var lateDecision = fixture.Bridge.DequeueSettlementDecision();
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
            lateDecision.Status);
        Assert.Null(GetCurrentAttempt(fixture.Context));
        Assert.Null(GetLastSettlement(fixture.Context));
        Assert.Equal(1, fixture.Bridge.SettlementDecisionCount);
    }

    [Fact]
    public async Task BoundedMissingDispatchRejection_SynchronouslyClearsCoreAndBridgeState()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_bounded",
            "transfer_retry_bounded");
        var predecessor = Grant(fixture, fixture.InitialRequest);
        var predecessorOfferGeneration =
            checked(fixture.InitialRequest.RetiredOfferGeneration + 1);
        var predecessorReport = SettleMissingDispatch(
            fixture,
            predecessor,
            predecessorOfferGeneration);
        fixture.Bridge.RaiseSettlementReport(predecessorReport);
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted,
            fixture.Bridge.DequeueSettlementDecision().Status);

        var successor = GrantSuccessor(
            fixture,
            predecessor,
            predecessorOfferGeneration);
        Assert.Equal(2, successor.Authority.DispatchAttemptGeneration);
        Assert.Equal(1, successor.Authority.MissingDispatchSuccessorGeneration);
        Assert.Equal(
            predecessor.Authority.AuthorityId,
            successor.Authority.PredecessorAuthorityId);
        Assert.Equal(predecessorOfferGeneration, successor.Authority.RetiredOfferGeneration);
        var successorOfferGeneration = checked(predecessorOfferGeneration + 1);
        var successorReport = SettleMissingDispatch(
            fixture,
            successor,
            successorOfferGeneration);
        fixture.Bridge.RaiseSettlementReport(successorReport);
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted,
            fixture.Bridge.DequeueSettlementDecision().Status);

        fixture.Bridge.RaiseSuccessorEvaluation(
            new RuntimeUnlockRetryDispatchSuccessorEvaluation(
                successor.Authority,
                successorOfferGeneration));

        var exhausted = fixture.Bridge.DequeueSuccessorDecision();
        Assert.Equal(
            RuntimeUnlockRetryDispatchSuccessorDecisionStatus.ChainClosed,
            exhausted.Status);
        Assert.Equal("retry_dispatch_chain_closed", exhausted.Reason);
        Assert.Equal(
            "retry_dispatch_missing_successor_limit_exhausted",
            Assert.IsType<RuntimeUnlockRetryLifecycleBoundedClosure>(
                FileTransferCoordinator
                    .GetRuntimeUnlockRetryLifecycle(exhausted.State)
                    .BoundedClosure)
                .Reason);
        Assert.Null(exhausted.State.CurrentRuntimeUnlockRetryDispatchAuthority);
        Assert.Null(GetCurrentAttempt(fixture.Context));
        Assert.Null(fixture.Bridge.InfraCurrentContract);
        Assert.Equal(successorReport, GetLastSettlement(fixture.Context));
    }

    private static RuntimeUnlockRetryDispatchContract Grant(
        RetryFixture fixture,
        RuntimeUnlockRetryDispatchAuthorityRequest request)
    {
        fixture.Bridge.RaiseAuthorityRequest(request);
        return CompleteGrant(fixture, request);
    }

    private static RuntimeUnlockRetryDispatchContract GrantSuccessor(
        RetryFixture fixture,
        RuntimeUnlockRetryDispatchContract predecessor,
        long retiredOfferGeneration)
    {
        fixture.Bridge.RaiseSuccessorEvaluation(
            new RuntimeUnlockRetryDispatchSuccessorEvaluation(
                predecessor.Authority,
                retiredOfferGeneration));
        var successor = fixture.Bridge.DequeueSuccessorDecision();
        Assert.Equal(
            RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Requested,
            successor.Status);
        var request = Assert.IsType<RuntimeUnlockRetryDispatchAuthorityRequest>(
            successor.Request);
        Assert.Equal(
            predecessor.Authority.AuthorityId,
            request.PredecessorAuthorityId);
        return CompleteGrant(fixture, request);
    }

    private static RuntimeUnlockRetryDispatchContract CompleteGrant(
        RetryFixture fixture,
        RuntimeUnlockRetryDispatchAuthorityRequest request)
    {
        var decision = fixture.Bridge.DequeueAuthorityDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Granted, decision.Status);
        Assert.Equal(request, decision.Request);
        var contract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(decision.Contract);
        var staged = fixture.Bridge.DequeueStageResult();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStageStatus.Staged, staged.Status);
        Assert.Equal(decision, staged.Decision);
        var installed = fixture.Bridge.DequeueInstallResult();
        Assert.Equal(RuntimeUnlockRetryDispatchInstallStatus.Installed, installed.Status);
        Assert.Equal(contract.Authority, installed.Receipt.Authority);
        fixture.Bridge.FlushInstallFollowUp();
        Assert.True(installed.Receipt.InstallGeneration > 0);
        Assert.Equal(contract.Authority, GetCurrentAttempt(fixture.Context)?.Authority);
        Assert.Same(contract, fixture.Bridge.InfraCurrentContract);
        Assert.Equal(installed.Receipt, GetCurrentInstallReceipt(fixture.Context));
        Assert.Equal(
            installed.Receipt.InstallGeneration,
            GetLastInstallGeneration(fixture.Context));
        return contract;
    }

    private static void BindActivePredecessorForSuccessor(
        RetryFixture fixture,
        RuntimeUnlockRetryDispatchContract predecessor,
        long offerGeneration)
    {
        var receipt = Assert.IsType<RuntimeUnlockRetryDispatchInstallReceipt>(
            GetCurrentInstallReceipt(fixture.Context));
        var offerNonce = $"active-successor-{offerGeneration}";
        Assert.True(predecessor.TryBindOfferAlias(offerGeneration, offerNonce));
        fixture.Bridge.RaiseExecutionObservation(
            new RuntimeUnlockRetryDispatchExecutionObservation(
                predecessor.Authority,
                RuntimeUnlockRetryDispatchAttemptPhase.AliasBound,
                offerGeneration,
                offerNonce,
                DispatchObserved: false)
            {
                InstallReceipt = receipt,
            });
    }

    private static RuntimeUnlockRetryDispatchSettlementReport SettleMissingDispatch(
        RetryFixture fixture,
        RuntimeUnlockRetryDispatchContract contract,
        long offerGeneration)
    {
        var offerNonce = $"nonce-{offerGeneration}";
        var installReceipt = GetCurrentInstallReceipt(fixture.Context);
        Assert.True(installReceipt.HasValue);
        var exactInstallReceipt = installReceipt.Value;
        Assert.True(contract.TryBindOfferAlias(offerGeneration, offerNonce));
        fixture.Bridge.RaiseExecutionObservation(
            new RuntimeUnlockRetryDispatchExecutionObservation(
                contract.Authority,
                RuntimeUnlockRetryDispatchAttemptPhase.AliasBound,
                offerGeneration,
                offerNonce,
                DispatchObserved: false)
            {
                InstallReceipt = exactInstallReceipt,
            });
        Assert.True(contract.TryBeginSend(out var attempt));
        fixture.Bridge.RaiseExecutionObservation(
            new RuntimeUnlockRetryDispatchExecutionObservation(
                contract.Authority,
                RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight,
                offerGeneration,
                offerNonce,
                DispatchObserved: false)
            {
                InstallReceipt = exactInstallReceipt,
            });
        Assert.True(contract.TryCreateMissingDispatchSettlementProposal(
            attempt,
            "retry_dispatch_not_observed",
            out var report));
        Assert.False(contract.Lifecycle.IsSettled);
        return report;
    }

    private static (
        RuntimeUnlockRetryDispatchAuthorityRequest Request,
        FallbackTailYieldDecision TailYield) SeedPostTunaFallbackRetrySource(object context)
    {
        var regularState = CreateCoordinatorState(context);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var fallbackEpoch = FileTransferCoordinator.StartLiveRouteEpoch(
            regularState.LastLiveRouteEpochId,
            fallback,
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn,
            "test_post_tuna_fallback_retry_source");
        regularState = regularState with
        {
            CurrentLiveRouteEpoch = fallbackEpoch,
            LastLiveRouteEpochId = fallbackEpoch.EpochId,
        };
        var fallbackStarted = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                fallback,
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn,
                "test_post_tuna_fallback_retry_source",
                FileTransferLegState.Active,
                CanSendData: true,
                CommittedChunkIndex: 0,
                HighestObservedChunkIndex: 0,
                TransportEpochId: 2,
                BridgeRecoveryGeneration: 1),
            regularState);
        Assert.Null(fallbackStarted.MutationRejectedReason);
        ApplyCoordinatorDecision(context, fallbackStarted);

        var owner = Assert.IsType<RuntimeOwnerToken>(fallbackStarted.State.CurrentRuntimeOwner);
        var leg = Assert.IsType<FileTransferLeg>(fallbackStarted.State.CurrentLeg);
        var publication = FileTransferCoordinator.AuthorizeAction(
            fallbackStarted.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                "test",
                "fallback_retry_publication",
                RuntimeOwner: owner,
                Route: fallback.Route,
                ProtocolVersion: fallback.ProtocolVersion,
                LiveRouteEpoch: leg.LiveRouteEpochId,
                LegGeneration: leg.Generation,
                RuntimeGeneration: owner.RuntimeGeneration,
                TerminalGeneration: fallbackStarted.State.TerminalGeneration,
                TargetRoute: fallback.Route,
                TargetProtocolVersion: fallback.ProtocolVersion,
                TargetTransport: FileTransferTransportKind.RegularNkn));
        Assert.True(publication.Accepted);
        ApplyCoordinatorActionDecision(context, publication);

        var yielded = FileTransferCoordinator.AuthorizeAction(
            publication.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.FallbackTailQuiescent,
                "test",
                "fallback_tail_quiescent",
                RuntimeOwner: publication.State.CurrentRuntimeOwner,
                Route: fallback.Route,
                ProtocolVersion: fallback.ProtocolVersion,
                LiveRouteEpoch: publication.State.CurrentLeg!.LiveRouteEpochId,
                LegGeneration: publication.State.CurrentLeg.Generation,
                TransportEpochId: publication.State.CurrentLeg.TransportEpochId,
                BridgeRecoveryGeneration: publication.State.CurrentLeg.BridgeRecoveryGeneration,
                FallbackTailYieldKind: FallbackTailYieldKind.Quiescent));
        Assert.True(yielded.Accepted);
        ApplyCoordinatorActionDecision(context, yielded);

        var tailYield = Assert.IsType<FallbackTailYieldDecision>(yielded.FallbackTailYieldDecision);
        var sourcePublication = Assert.IsType<FileTransferRoutePublicationAuthority>(
            FileTransferCoordinator.CreateRoutePublicationAuthority(
                publication.State.CurrentRoutePublicationToken,
                "fallback_retry_publication"));
        var peerAuthorityId = new FileTransferAuthorityId(
            "fta2-fallback_tail_yield-0123456789abcdef01234567");
        var peerOwnerKey = FileTransferCoordinator.BuildFallbackTailYieldOwnerKey(
            publication.State.SessionId,
            publication.State.TransferId,
            publication.State.RouteSelection.TelemetryToken,
            publication.State.RouteSelection.ProtocolVersion,
            tailYield.CurrentLiveRouteEpoch,
            publication.State.CurrentLeg!.Generation,
            publication.State.CurrentLeg.BridgeRecoveryGeneration,
            publication.State.CurrentLeg.TransportEpochId,
            tailYield.CheckpointRequestIdSnapshot);
        var pairedYieldEvidence = new FallbackPairedTailYieldReleaseEvidence(
            tailYield,
            peerAuthorityId,
            PeerPredecessorAuthorityId: null,
            PeerYieldGeneration: 1,
            PeerDirection: "inbound",
            peerOwnerKey,
            tailYield.PairKey,
            tailYield.CurrentLiveRouteEpoch,
            publication.State.CurrentLeg.Generation,
            publication.State.CurrentLeg.BridgeRecoveryGeneration,
            publication.State.CurrentLeg.TransportEpochId,
            tailYield.CheckpointRequestIdSnapshot);
        return (
            new RuntimeUnlockRetryDispatchAuthorityRequest(
                regularState.SessionId,
                regularState.TransferId,
                FileTransferDirection.Outbound,
                sourcePublication,
                RecoveryContractGeneration: 71,
                RetiredOfferGeneration: 29,
                DispatchAttemptGeneration: 1,
                RetryReason: "runtime_unlock_offer_send_not_observed",
                RecoveryReason: "post_tuna_fallback_tail_unblocked",
                RegularV4LivenessGeneration: 0,
                BridgeDecisionGeneration: 0)
            {
                FallbackPairedTailYieldEvidence = pairedYieldEvidence,
            },
            tailYield);
    }

    private static void SeedPairedFallbackTailYield(
        object context,
        FallbackTailYieldDecision tailYield)
    {
        SetProperty(context, "PairedFallbackTailYieldGranted", true);
        SetProperty(context, "LocalFallbackTailYieldGranted", true);
        SetProperty(context, "PeerFallbackTailYieldGranted", true);
        SetProperty(context, "LocalFallbackTailYieldAuthorityId", tailYield.AuthorityId.ToString());
        SetProperty(
            context,
            "PeerFallbackTailYieldAuthorityId",
            "fta2-fallback_tail_yield-0123456789abcdef01234567");
        SetProperty(context, "PeerFallbackTailYieldGeneration", 1L);
        SetProperty(context, "LocalFallbackTailYieldOwnerKey", tailYield.OwnerKey);
        SetProperty(context, "PeerFallbackTailYieldOwnerKey", tailYield.OwnerKey);
        SetProperty(context, "LocalFallbackTailYieldPairKey", tailYield.PairKey);
        SetProperty(context, "PeerFallbackTailYieldPairKey", tailYield.PairKey);
        SetProperty(
            context,
            "RuntimeUnlockFallbackSurvivalState",
            tailYield.FallbackSurvivalState with { PairedFallbackYieldGranted = true });
    }

    private static FileTransferCoordinatorState CreateCoordinatorState(object context)
    {
        var method = typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate =>
                candidate.Name == "CreateCoordinatorStateLocked" &&
                candidate.GetParameters()[0].ParameterType == context.GetType());
        return Assert.IsType<FileTransferCoordinatorState>(method.Invoke(null, [context]));
    }

    private static void ApplyCoordinatorDecision(
        object context,
        FileTransferCoordinatorDecision decision)
    {
        var method = typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate =>
                candidate.Name == "ApplyCoordinatorDecisionToContextLocked" &&
                candidate.GetParameters()[0].ParameterType == context.GetType());
        method.Invoke(null, [context, decision, true]);
    }

    private static void ApplyCoordinatorActionDecision(
        object context,
        FileTransferCoordinatorActionDecision decision)
    {
        var method = typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate =>
                candidate.Name == "ApplyCoordinatorActionDecisionToContextLocked" &&
                candidate.GetParameters()[0].ParameterType == context.GetType());
        method.Invoke(null, [context, decision]);
    }

    private static async Task<RetryFixture> CreateFixtureAsync(
        string sessionId,
        string transferId)
    {
        var bridge = new RetryAuthorityLoopbackTransport(sessionId);
        var peer = new LoopbackFileTransferTransport(sessionId);
        var service = new SessionFileTransferService();
        try
        {
            bridge.Connect(peer);
            service.AttachTransport(bridge);
            var started = await service.TryStartSendAsync(
                new FileTransferSendDescriptor("runtime-unlock-retry.bin", 1, transferId, 4096),
                _ => Task.FromResult<Stream>(new MemoryStream([0x2a], writable: false)),
                CancellationToken.None);
            Assert.NotNull(started);

            var context = GetOutboundContext(service);
            var recoveryClosure = SeedRetiredRecoveryLivenessLease(context);
            var publicationToken = GetRequiredProperty<RoutePublicationToken>(
                context,
                "CurrentRoutePublicationToken");
            var sourcePublication = FileTransferCoordinator.CreateRoutePublicationAuthority(
                publicationToken,
                "session_retry_authority_test");
            Assert.NotNull(sourcePublication);
            var request = new RuntimeUnlockRetryDispatchAuthorityRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                sourcePublication!,
                RecoveryContractGeneration: 41,
                RetiredOfferGeneration: 17,
                DispatchAttemptGeneration: 1,
                RetryReason: "runtime_unlock_offer_send_not_observed",
                RecoveryReason: "receive_recovery_lease_retired",
                RegularV4LivenessGeneration: recoveryClosure.LeaseGeneration,
                BridgeDecisionGeneration: recoveryClosure.BridgeDecisionGeneration);
            return new RetryFixture(service, bridge, peer, context, transferId, request);
        }
        catch
        {
            service.Dispose();
            bridge.Dispose();
            peer.Dispose();
            throw;
        }
    }

    private static (int LeaseGeneration, int BridgeDecisionGeneration)
        SeedRetiredRecoveryLivenessLease(
            object context,
            FileTransferDirection direction = FileTransferDirection.Outbound)
    {
        var sessionId = GetRequiredProperty<string>(context, "SessionId");
        var transferId = GetRequiredProperty<string>(context, "TransferId");
        var route = GetRequiredProperty<FileTransferRouteSelection>(context, "RouteSelection");
        var epoch = GetOptionalProperty<LiveRouteEpoch>(context, "CurrentLiveRouteEpoch");
        var leg = GetRequiredProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        var owner = GetRequiredProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var terminalGeneration = GetRequiredProperty<int>(context, "TerminalGeneration");
        const int leaseGeneration = 1;
        const int bridgeDecisionGeneration = 1;
        const string bridgeReason = "test_recovery_bridge";
        const string lifecycleReason = "bridge_lifecycle_completed";
        var activeLease = new RecoveryLivenessLease(
            sessionId,
            transferId,
            direction,
            owner,
            route.Route,
            route.TelemetryToken,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            LeaseGeneration: leaseGeneration,
            new FileTransferAuthorityId(
                $"fta2-test-recovery-lease-{direction.ToString().ToLowerInvariant()}-{transferId}"),
            RecoveryLivenessLeaseState.Active,
            bridgeReason);
        var bridgeDecision = new BridgeRecoveryDecision(
            true,
            BridgeRecoveryKind.BridgeReceiveStallRecovery,
            bridgeReason,
            bridgeDecisionGeneration,
            route.Route,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            owner);
        var bridgeAuthority = new FileTransferBridgeRecoveryAuthority(
            sessionId,
            transferId,
            direction,
            route.TelemetryToken,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            bridgeDecisionGeneration,
            BridgeRecoveryKind.BridgeReceiveStallRecovery.ToString(),
            bridgeReason);
        var lifecycle = new RuntimeUnlockRegularV4RecoveryLifecycleReport(
            new RuntimeUnlockRegularV4RecoveryAuthority(bridgeAuthority, activeLease),
            RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed,
            lifecycleReason);
        var retiredLease = activeLease with
        {
            State = RecoveryLivenessLeaseState.Retired,
            Reason = lifecycleReason,
        };
        SetProperty(context, "CurrentRecoveryLivenessLease", null);
        SetProperty(context, "LastRetiredRecoveryLivenessLease", retiredLease);
        SetProperty(context, "LastRecoveryLivenessLeaseGeneration", leaseGeneration);
        SetProperty(context, "CurrentBridgeRecoveryDecision", bridgeDecision);
        SetProperty(context, "LastBridgeRecoveryDecisionGeneration", bridgeDecisionGeneration);
        SetProperty(context, "LastRuntimeUnlockRegularV4RecoveryLifecycle", lifecycle);
        SetProperty(context, "RuntimeUnlockRecoveryLeaseClosureAvailable", true);
        return (leaseGeneration, bridgeDecisionGeneration);
    }

    private static RuntimeUnlockRegularV4RecoverySettlementReport
        SeedRegularV4RecoverySettlement(object context)
    {
        var sessionId = GetRequiredProperty<string>(context, "SessionId");
        var transferId = GetRequiredProperty<string>(context, "TransferId");
        var route = GetRequiredProperty<FileTransferRouteSelection>(context, "RouteSelection");
        var epoch = GetOptionalProperty<LiveRouteEpoch>(context, "CurrentLiveRouteEpoch");
        var leg = GetRequiredProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        var owner = GetRequiredProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var terminalGeneration = GetRequiredProperty<int>(context, "TerminalGeneration");
        var publication = GetRequiredProperty<RoutePublicationToken>(
            context,
            "CurrentRoutePublicationToken");
        const int repairGeneration = 7;
        var authorityId = new FileTransferAuthorityId(
            $"fta2-test-regular-v4-handoff-{transferId}");
        var repairDecision = new RepairReplayDecision(
            Accepted: true,
            RepairReplayKind.V4Repair,
            "test_regular_v4_handoff",
            repairGeneration,
            route.Route,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            owner,
            authorityId,
            PredecessorAuthorityId: null,
            BridgeDecisionGeneration: 0,
            publication);
        SetProperty(context, "CurrentRepairReplayDecision", repairDecision);
        SetProperty(context, "LastRepairReplayGeneration", repairGeneration);

        var settlement = new FileTransferRegularV4FrontierRepairSettlement(
            sessionId,
            transferId,
            FileTransferDirection.Outbound,
            route.TelemetryToken,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            BridgeDecisionGeneration: 0,
            publication.PublicationGeneration,
            publication.RouteCommitGeneration,
            "regular-v4-frontier:0:test",
            FrontierChunkIndex: 0,
            DurableReceivedHighestChunkIndex: -1,
            RequestedChunkCount: 1,
            repairGeneration,
            "test_regular_v4_handoff_consumed",
            FileTransferRegularV4FrontierRepairSettlementOutcome.MissingDispatch,
            FileTransferTransportKind.RegularNkn)
        {
            AuthorityId = authorityId,
        };
        SetProperty(context, "LastCoordinatorIssuedRegularV4RecoverySettlement", settlement);
        SetProperty(context, "RuntimeUnlockRegularV4RecoverySettlementAvailable", false);
        SetProperty(context, "CurrentRecoveryLivenessLease", null);
        SetProperty(context, "LastRetiredRecoveryLivenessLease", null);
        SetProperty(context, "LastRuntimeUnlockRegularV4RecoveryLifecycle", null);
        SetProperty(context, "RuntimeUnlockRecoveryLeaseClosureAvailable", false);
        return new RuntimeUnlockRegularV4RecoverySettlementReport(
            settlement,
            RegularV4LivenessGeneration: 0,
            BridgeDecisionGeneration: 0,
            RuntimeUnlockRegularV4RecoverySettlementDisposition.Consumed,
            "test_regular_v4_handoff_consumed");
    }

    private static RuntimeUnlockRegularV4RecoveryLifecycleReport
        SeedRegularV4RecoveryLifecycle(
            object context,
            FileTransferDirection direction = FileTransferDirection.Outbound)
    {
        var sessionId = GetRequiredProperty<string>(context, "SessionId");
        var transferId = GetRequiredProperty<string>(context, "TransferId");
        var route = GetRequiredProperty<FileTransferRouteSelection>(context, "RouteSelection");
        var epoch = GetOptionalProperty<LiveRouteEpoch>(context, "CurrentLiveRouteEpoch");
        var leg = GetRequiredProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        var owner = GetRequiredProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var terminalGeneration = GetRequiredProperty<int>(context, "TerminalGeneration");
        const int bridgeDecisionGeneration = 1;
        var bridge = new BridgeRecoveryDecision(
            true,
            BridgeRecoveryKind.BridgeReceiveStallRecovery,
            "test_regular_v4_lifecycle",
            bridgeDecisionGeneration,
            route.Route,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            owner);
        SetProperty(context, "CurrentBridgeRecoveryDecision", bridge);
        SetProperty(context, "LastBridgeRecoveryDecisionGeneration", bridgeDecisionGeneration);
        var lease = new RecoveryLivenessLease(
            sessionId,
            transferId,
            direction,
            owner,
            route.Route,
            route.TelemetryToken,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            LeaseGeneration: 1,
            new FileTransferAuthorityId($"fta2-test-lifecycle-lease-{transferId}"),
            RecoveryLivenessLeaseState.Active,
            "test_regular_v4_lifecycle");
        SetProperty(context, "CurrentRecoveryLivenessLease", lease);
        SetProperty(context, "LastRecoveryLivenessLeaseGeneration", 1);
        SetProperty(context, "LastRetiredRecoveryLivenessLease", null);
        SetProperty(context, "LastRuntimeUnlockRegularV4RecoveryLifecycle", null);
        SetProperty(context, "RuntimeUnlockRecoveryLeaseClosureAvailable", false);
        var authority = new FileTransferBridgeRecoveryAuthority(
            sessionId,
            transferId,
            direction,
            route.TelemetryToken,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            bridgeDecisionGeneration,
            BridgeRecoveryKind.BridgeReceiveStallRecovery.ToString(),
            "test_regular_v4_lifecycle");
        return new RuntimeUnlockRegularV4RecoveryLifecycleReport(
            new RuntimeUnlockRegularV4RecoveryAuthority(authority, lease),
            RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed,
            "bridge_lifecycle_completed");
    }

    private static (
        FileTransferReceiveRecoveryRequest Request,
        FileTransferBridgeRecoveryExecutionReceipt Receipt)
        SeedRunningBridgeRecoveryExecution(
            object context,
            FileTransferBridgeRecoveryExecutionOutcome outcome,
            FileTransferDirection direction = FileTransferDirection.Outbound)
    {
        var lifecycle = SeedRegularV4RecoveryLifecycle(context, direction);
        var bridgeDecision = GetRequiredProperty<BridgeRecoveryDecision>(
            context,
            "CurrentBridgeRecoveryDecision");
        var leg = GetRequiredProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        var command = new FileTransferBridgeRecoveryExecutionCommand(
            bridgeDecision,
            lifecycle.RecoveryAuthority.RecoveryLivenessLease,
            leg.CheckpointRequestId);
        SetProperty(context, "CurrentBridgeRecoveryExecutionCommand", command);
        SetProperty(
            context,
            "CurrentBridgeRecoveryExecutionState",
            FileTransferBridgeRecoveryExecutionState.Running);
        SetProperty(context, "LastBridgeRecoveryExecutionReceipt", null);

        var receipt = new FileTransferBridgeRecoveryExecutionReceipt(
            command,
            outcome,
            "hard_restart",
            outcome switch
            {
                FileTransferBridgeRecoveryExecutionOutcome.Completed =>
                    "bridge_recovery_completed",
                FileTransferBridgeRecoveryExecutionOutcome.Exhausted =>
                    "bridge_recovery_exhausted",
                _ => "bridge_recovery_discarded",
            });
        var authority = lifecycle.Authority;
        var request = new FileTransferReceiveRecoveryRequest(
            authority.SessionId,
            authority.TransferId,
            authority.Direction,
            receipt.Reason)
        {
            RouteToken = authority.RouteToken,
            ProtocolVersion = authority.ProtocolVersion,
            LiveRouteEpoch = authority.LiveRouteEpoch,
            TransferLegGeneration = authority.TransferLegGeneration,
            BridgeRecoveryGeneration = authority.BridgeRecoveryGeneration,
            TransportEpoch = authority.TransportEpoch,
            CheckpointRequestId = leg.CheckpointRequestId,
            AuthorityReason = authority.Reason,
            BridgeRecoveryAuthority = authority,
            RegularV4RecoveryAuthority = lifecycle.RecoveryAuthority,
            BridgeRecoveryExecutionCommand = command,
        };
        return (request, receipt);
    }

    private static async Task ObserveBridgeRecoveryExecutionCompletionAsync(
        RetryFixture fixture,
        FileTransferReceiveRecoveryRequest request,
        FileTransferBridgeRecoveryExecutionReceipt receipt)
        => await ObserveBridgeRecoveryExecutionCompletionAsync(
            fixture.Service,
            fixture.Bridge,
            request,
            receipt);

    private static async Task ObserveBridgeRecoveryExecutionCompletionAsync(
        SessionFileTransferService service,
        RetryAuthorityLoopbackTransport bridge,
        FileTransferReceiveRecoveryRequest request,
        FileTransferBridgeRecoveryExecutionReceipt receipt)
    {
        var method = typeof(SessionFileTransferService).GetMethod(
            "ObserveBridgeRecoveryExecutionCompletionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var invocation = method!.Invoke(
            service,
            [request, Task.FromResult(receipt), bridge]);
        var task = Assert.IsAssignableFrom<Task>(invocation);
        await task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static object GetOutboundContext(SessionFileTransferService service)
    {
        var field = typeof(SessionFileTransferService).GetField(
            "outboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var context = field!.GetValue(service);
        Assert.NotNull(context);
        return context!;
    }

    private static object GetInboundContext(SessionFileTransferService service)
    {
        var field = typeof(SessionFileTransferService).GetField(
            "inboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var context = field!.GetValue(service);
        Assert.NotNull(context);
        return context!;
    }

    private static object GetCoreGate(SessionFileTransferService service)
    {
        var field = typeof(SessionFileTransferService).GetField(
            "gate",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<object>(field!.GetValue(service));
    }

    private static T GetRequiredProperty<T>(object context, string name)
    {
        var property = context.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        var value = property!.GetValue(context);
        Assert.NotNull(value);
        return Assert.IsType<T>(value);
    }

    private static T? GetOptionalProperty<T>(object context, string name)
        where T : class
    {
        var property = context.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return property!.GetValue(context) as T;
    }

    private static T? GetOptionalStructProperty<T>(object context, string name)
        where T : struct
    {
        var property = context.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        var value = property!.GetValue(context);
        return value is null ? null : Assert.IsType<T>(value);
    }

    private static void SetProperty(object context, string name, object? value)
    {
        var property = context.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        property!.SetValue(context, value);
    }

    private static void ReplaceRuntimeUnlockRetryDispatchInstallCurrentness(
        object context,
        string replacementKind)
    {
        switch (replacementKind)
        {
            case "route_publication":
            {
                var publication = GetRequiredProperty<RoutePublicationToken>(
                    context,
                    "CurrentRoutePublicationToken");
                SetProperty(
                    context,
                    "CurrentRoutePublicationToken",
                    publication with
                    {
                        PublicationGeneration = checked(publication.PublicationGeneration + 1),
                    });
                break;
            }
            case "runtime_owner":
            {
                var owner = GetRequiredProperty<RuntimeOwnerToken>(
                    context,
                    "CurrentRuntimeOwner");
                SetProperty(
                    context,
                    "CurrentRuntimeOwner",
                    owner with
                    {
                        RuntimeGeneration = checked(owner.RuntimeGeneration + 1),
                    });
                break;
            }
            case "terminal_generation":
            {
                var generation = GetRequiredProperty<int>(context, "TerminalGeneration");
                SetProperty(context, "TerminalGeneration", checked(generation + 1));
                break;
            }
            case "transfer_leg":
            {
                var leg = GetRequiredProperty<FileTransferLeg>(context, "CurrentTransferLeg");
                SetProperty(leg, "Generation", checked(leg.Generation + 1));
                break;
            }
            case "bridge_decision":
            {
                var bridgeDecision = GetOptionalStructProperty<BridgeRecoveryDecision>(
                    context,
                    "CurrentBridgeRecoveryDecision");
                Assert.NotNull(bridgeDecision);
                SetProperty(
                    context,
                    "CurrentBridgeRecoveryDecision",
                    bridgeDecision.Value with
                    {
                        BridgeDecisionGeneration = checked(
                            bridgeDecision.Value.BridgeDecisionGeneration + 1),
                    });
                break;
            }
            case "recovery_contract":
            {
                var persistentState = GetCoordinatorPersistentState(context);
                SetCoordinatorPersistentState(
                    context,
                    persistentState with
                    {
                        RuntimeUnlockRetryLifecycle =
                            persistentState.RuntimeUnlockRetryLifecycle with
                            {
                                PendingAttempt = null,
                            },
                    });
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(replacementKind),
                    replacementKind,
                    "Unknown install-currentness replacement.");
        }
    }

    private static FileTransferCoordinatorPersistentState
        GetCoordinatorPersistentState(object context)
        => GetRequiredProperty<FileTransferCoordinatorPersistentState>(
            context,
            "CoordinatorPersistentState");

    private static void SetCoordinatorPersistentState(
        object context,
        FileTransferCoordinatorPersistentState persistentState)
        => SetProperty(context, "CoordinatorPersistentState", persistentState);

    private static RuntimeUnlockRetryLifecycleAttemptSnapshot? GetCurrentAttempt(
        object context)
    {
        var lifecycle =
            GetCoordinatorPersistentState(context).RuntimeUnlockRetryLifecycle;
        return lifecycle.PendingAttempt ?? lifecycle.InstalledAttempt;
    }

    private static RuntimeUnlockRetryLifecycleAttemptSnapshot?
        GetPendingPredecessorAttempt(object context)
    {
        var lifecycle =
            GetCoordinatorPersistentState(context).RuntimeUnlockRetryLifecycle;
        if (lifecycle.RetainedPredecessor is { } retained)
        {
            return retained;
        }

        return lifecycle.PendingAttempt is
                   {
                       Authority.PredecessorAuthorityId: { IsDefault: false } predecessorId,
                   } &&
               lifecycle.InstalledAttempt is { } installed &&
               installed.Authority.AuthorityId == predecessorId
            ? installed
            : null;
    }

    private static RuntimeUnlockRetryDispatchSettlementReport? GetLastSettlement(object context)
    {
        return GetCoordinatorPersistentState(context)
            .RuntimeUnlockRetryLifecycle
            .LastSettlement;
    }

    private static RuntimeUnlockRetryDispatchInstallReceipt? GetCurrentInstallReceipt(
        object context)
        => GetCoordinatorPersistentState(context)
            .RuntimeUnlockRetryLifecycle
            .InstalledAttempt?
            .InstallReceipt;

    private static long GetLastInstallGeneration(object context)
        => GetCoordinatorPersistentState(context)
            .RuntimeUnlockRetryLifecycle
            .LastInstallGeneration;

    private static RuntimeUnlockRegularV4RecoverySettlementReport? GetLastRegularV4Settlement(
        object context)
    {
        var value = context.GetType()
            .GetProperty(
                "LastRuntimeUnlockRegularV4RecoverySettlement",
                BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(context);
        return value is RuntimeUnlockRegularV4RecoverySettlementReport report ? report : null;
    }

    private sealed class RetryFixture(
        SessionFileTransferService service,
        RetryAuthorityLoopbackTransport bridge,
        LoopbackFileTransferTransport peer,
        object context,
        string transferId,
        RuntimeUnlockRetryDispatchAuthorityRequest initialRequest) : IDisposable
    {
        public SessionFileTransferService Service { get; } = service;

        public RetryAuthorityLoopbackTransport Bridge { get; } = bridge;

        public object Context { get; } = context;

        public string TransferId { get; } = transferId;

        public RuntimeUnlockRetryDispatchAuthorityRequest InitialRequest { get; } = initialRequest;

        public void Dispose()
        {
            Service.Dispose();
            Bridge.Dispose();
            peer.Dispose();
        }
    }

    private sealed class RetryAuthorityLoopbackTransport(string sessionId) :
        LoopbackFileTransferTransport(sessionId),
        IRuntimeUnlockRetryDispatchAuthorityBridge,
        IFileTransferBridgeRecoveryExecutionBridge
    {
        private readonly ConcurrentQueue<RuntimeUnlockRetryDispatchDecision> authorityDecisions = [];
        private readonly ConcurrentQueue<RuntimeUnlockRetryDispatchDecisionStageResult> stageResults = [];
        private readonly ConcurrentQueue<RuntimeUnlockRetryDispatchInstallResult> installResults = [];
        private readonly ConcurrentQueue<RuntimeUnlockRetryDispatchSuccessorDecision>
            successorDecisions = [];
        private readonly ConcurrentQueue<RuntimeUnlockRetryDispatchSettlementDecision> settlementDecisions = [];
        private readonly ConcurrentQueue<RuntimeUnlockRegularV4RecoverySettlementDecision> regularV4SettlementDecisions = [];
        private readonly ConcurrentQueue<RuntimeUnlockRegularV4RecoveryLifecycleDecision> regularV4LifecycleDecisions = [];
        private readonly ConcurrentQueue<(
            FileTransferBridgeRecoveryExecutionReceipt Receipt,
            bool Accepted,
            string Reason)> bridgeRecoveryExecutionReceiptDecisions = [];
        private readonly ConcurrentQueue<string> bridgeRecoveryCompletionObservations = [];
        private EventHandler<RuntimeUnlockRetryDispatchInitialEvaluationRequestedEventArgs>?
            initialEvaluationRequested;
        private EventHandler<RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs>? authorityRequested;
        private EventHandler<RuntimeUnlockRetryDispatchSettlementReportedEventArgs>? settlementReported;
        private EventHandler<RuntimeUnlockRetryDispatchDeadlineEvaluationEventArgs>?
            deadlineEvaluationRequested;
        private EventHandler<RuntimeUnlockRetryDispatchExecutionObservedEventArgs>?
            executionObserved;
        private EventHandler<RuntimeUnlockRetryDispatchSuccessorEvaluationEventArgs>?
            successorEvaluationRequested;
        private EventHandler<RuntimeUnlockRegularV4RecoverySettlementReportedEventArgs>? regularV4SettlementReported;
        private EventHandler<RuntimeUnlockRegularV4RecoveryLifecycleReportedEventArgs>? regularV4LifecycleReported;
        private EventHandler<FileTransferBridgeRecoveryEvaluationRequestedEventArgs>? bridgeRecoveryEvaluationRequested;
        private RuntimeUnlockRetryDispatchAuthorityRequest? pendingRequest;
        private RuntimeUnlockRetryDispatchDecision? pendingStage;
        private long pendingStageStagedUtcMs;
        private long pendingStageExpiresUtcMs;
        private RuntimeUnlockRetryDispatchInstallReceipt? pendingInstallReceipt;
        private RuntimeUnlockRetryDispatchContract? pendingInstallContract;
        private RuntimeUnlockRetryDispatchInstallReceipt? lastCommittedReceipt;
        private RuntimeUnlockRetryDispatchScopeInvalidationCommand?
            pendingScopeInvalidationCommand;
        private RuntimeUnlockRetryDispatchScopeInvalidationReceipt?
            lastScopeInvalidationReceipt;
        private RuntimeUnlockRetryDispatchContract? pendingSupersededPredecessor;
        private RuntimeUnlockRetryDispatchContract? lastAppliedSettlementContract;
        private bool predecessorSettlementDecisionPendingDrain;
        private int settlementDecisionObservationCount;
        private int installObservationCount;

        public event EventHandler<RuntimeUnlockRetryDispatchInitialEvaluationRequestedEventArgs>?
            RuntimeUnlockRetryDispatchInitialEvaluationRequested
        {
            add => initialEvaluationRequested += value;
            remove => initialEvaluationRequested -= value;
        }

        public event EventHandler<RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs>?
            RuntimeUnlockRetryDispatchAuthorityRequested
        {
            add => authorityRequested += value;
            remove => authorityRequested -= value;
        }

        public event EventHandler<RuntimeUnlockRetryDispatchSettlementReportedEventArgs>?
            RuntimeUnlockRetryDispatchSettlementReported
        {
            add => settlementReported += value;
            remove => settlementReported -= value;
        }

        public event EventHandler<RuntimeUnlockRetryDispatchDeadlineEvaluationEventArgs>?
            RuntimeUnlockRetryDispatchDeadlineEvaluationRequested
        {
            add => deadlineEvaluationRequested += value;
            remove => deadlineEvaluationRequested -= value;
        }

        public event EventHandler<RuntimeUnlockRetryDispatchExecutionObservedEventArgs>?
            RuntimeUnlockRetryDispatchExecutionObserved
        {
            add => executionObserved += value;
            remove => executionObserved -= value;
        }

        public event EventHandler<RuntimeUnlockRetryDispatchSuccessorEvaluationEventArgs>?
            RuntimeUnlockRetryDispatchSuccessorEvaluationRequested
        {
            add => successorEvaluationRequested += value;
            remove => successorEvaluationRequested -= value;
        }

        public event EventHandler<RuntimeUnlockRegularV4RecoverySettlementReportedEventArgs>?
            RuntimeUnlockRegularV4RecoverySettlementReported
        {
            add => regularV4SettlementReported += value;
            remove => regularV4SettlementReported -= value;
        }

        public event EventHandler<RuntimeUnlockRegularV4RecoveryLifecycleReportedEventArgs>?
            RuntimeUnlockRegularV4RecoveryLifecycleReported
        {
            add => regularV4LifecycleReported += value;
            remove => regularV4LifecycleReported -= value;
        }

        public event EventHandler<FileTransferBridgeRecoveryEvaluationRequestedEventArgs>?
            BridgeRecoveryEvaluationRequested
        {
            add => bridgeRecoveryEvaluationRequested += value;
            remove => bridgeRecoveryEvaluationRequested -= value;
        }

        public int AuthorityRequestSubscriberCount =>
            authorityRequested?.GetInvocationList().Length ?? 0;

        public bool ThrowOnBridgeRecoveryExecutionReceiptDecision { get; set; }

        public int RegularV4LifecycleDecisionCount => regularV4LifecycleDecisions.Count;

        public int SettlementReportSubscriberCount =>
            settlementReported?.GetInvocationList().Length ?? 0;

        public int DeadlineEvaluationSubscriberCount =>
            deadlineEvaluationRequested?.GetInvocationList().Length ?? 0;

        public int ExecutionObservationSubscriberCount =>
            executionObserved?.GetInvocationList().Length ?? 0;

        public int SuccessorEvaluationSubscriberCount =>
            successorEvaluationRequested?.GetInvocationList().Length ?? 0;

        public int RegularV4SettlementReportSubscriberCount =>
            regularV4SettlementReported?.GetInvocationList().Length ?? 0;

        public int RegularV4LifecycleReportSubscriberCount =>
            regularV4LifecycleReported?.GetInvocationList().Length ?? 0;

        public RuntimeUnlockRetryDispatchContract? InfraCurrentContract { get; private set; }

        public ManualResetEventSlim? StageReturnBarrier { get; set; }

        public ManualResetEventSlim StageObserved { get; } = new(initialState: false);

        public ManualResetEventSlim? CommitReturnBarrier { get; set; }

        public ManualResetEventSlim CommitObserved { get; } = new(initialState: false);

        public object? CoreGateForInstallProbe { get; set; }

        public bool LastInstallObservedUnderCoreGate { get; private set; }

        public bool LastReceiptIssuedEvidenceObservedBeforeCommit { get; private set; }

        public bool RejectNextStage { get; set; }

        public bool RejectNextInstall { get; set; }

        public bool RejectNextConfirmation { get; set; }

        public int InstallCount => Volatile.Read(ref installObservationCount);

        public RuntimeUnlockRetryDispatchInstallReceipt? LastCommittedReceipt =>
            lastCommittedReceipt;

        public bool HasPendingStage => pendingStage.HasValue;

        public object? CoreGateForSettlementDecisionProbe { get; set; }

        public bool LastSettlementDecisionObservedWithCoreGateAvailable { get; private set; }

        public ManualResetEventSlim SettlementDecisionObserved { get; } = new(initialState: false);

        public int SettlementDecisionCount => Volatile.Read(ref settlementDecisionObservationCount);

        public void RaiseAuthorityRequest(RuntimeUnlockRetryDispatchAuthorityRequest request)
        {
            pendingRequest = request;
            authorityRequested?.Invoke(
                this,
                new RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs(request));
        }

        public void RaiseSettlementReport(RuntimeUnlockRetryDispatchSettlementReport report)
        {
            SettlementDecisionObserved.Reset();
            settlementReported?.Invoke(
                this,
                new RuntimeUnlockRetryDispatchSettlementReportedEventArgs(report));
        }

        public void RaiseDeadlineEvaluation(
            RuntimeUnlockRetryDispatchDeadlineEvaluation evaluation)
            => deadlineEvaluationRequested?.Invoke(
                this,
                new RuntimeUnlockRetryDispatchDeadlineEvaluationEventArgs(evaluation));

        public void RaiseExecutionObservation(
            RuntimeUnlockRetryDispatchExecutionObservation observation)
            => executionObserved?.Invoke(
                this,
                new RuntimeUnlockRetryDispatchExecutionObservedEventArgs(observation));

        public void RaiseSuccessorEvaluation(
            RuntimeUnlockRetryDispatchSuccessorEvaluation evaluation)
            => successorEvaluationRequested?.Invoke(
                this,
                new RuntimeUnlockRetryDispatchSuccessorEvaluationEventArgs(evaluation));

        public void RaiseRegularV4SettlementReport(
            RuntimeUnlockRegularV4RecoverySettlementReport report)
            => regularV4SettlementReported?.Invoke(
                this,
                new RuntimeUnlockRegularV4RecoverySettlementReportedEventArgs(report));

        public void RaiseRegularV4LifecycleReport(
            RuntimeUnlockRegularV4RecoveryLifecycleReport report)
            => regularV4LifecycleReported?.Invoke(
                this,
                new RuntimeUnlockRegularV4RecoveryLifecycleReportedEventArgs(report));

        public RuntimeUnlockRetryDispatchDecision DequeueAuthorityDecision()
        {
            Assert.True(authorityDecisions.TryDequeue(out var decision));
            return decision;
        }

        public RuntimeUnlockRetryDispatchDecisionStageResult DequeueStageResult()
        {
            Assert.True(stageResults.TryDequeue(out var result));
            return result;
        }

        public RuntimeUnlockRetryDispatchInstallResult DequeueInstallResult()
        {
            Assert.True(installResults.TryDequeue(out var result));
            return result;
        }

        public RuntimeUnlockRetryDispatchSuccessorDecision DequeueSuccessorDecision()
        {
            Assert.True(successorDecisions.TryDequeue(out var decision));
            return decision;
        }

        public RuntimeUnlockRetryDispatchSettlementDecision DequeueSettlementDecision()
        {
            Assert.True(settlementDecisions.TryDequeue(out var decision));
            return decision;
        }

        public RuntimeUnlockRegularV4RecoverySettlementDecision DequeueRegularV4SettlementDecision()
        {
            Assert.True(regularV4SettlementDecisions.TryDequeue(out var decision));
            return decision;
        }

        public RuntimeUnlockRegularV4RecoveryLifecycleDecision DequeueRegularV4LifecycleDecision()
        {
            Assert.True(regularV4LifecycleDecisions.TryDequeue(out var decision));
            return decision;
        }

        public (
            FileTransferBridgeRecoveryExecutionReceipt Receipt,
            bool Accepted,
            string Reason) DequeueBridgeRecoveryExecutionReceiptDecision()
        {
            Assert.True(bridgeRecoveryExecutionReceiptDecisions.TryDequeue(out var decision));
            return decision;
        }

        public string DequeueBridgeRecoveryCompletionObservation()
        {
            Assert.True(bridgeRecoveryCompletionObservations.TryDequeue(out var observation));
            return observation;
        }

        public bool ObserveRuntimeUnlockRetryDispatchNonGrantDecision(
            RuntimeUnlockRetryDispatchDecision decision)
        {
            if (decision.Status == RuntimeUnlockRetryDispatchDecisionStatus.Granted)
            {
                return false;
            }

            if (decision.Status == RuntimeUnlockRetryDispatchDecisionStatus.Rejected &&
                pendingStage is { } staged &&
                decision.Request == staged.Request &&
                ReferenceEquals(decision.Contract, staged.Contract))
            {
                var preserveInstalledContract =
                    InfraCurrentContract is { } installed &&
                    decision.State?
                        .CurrentRuntimeUnlockRetryDispatchAuthority ==
                    installed.Authority;
                pendingStage = null;
                pendingStageStagedUtcMs = 0;
                pendingStageExpiresUtcMs = 0;
                pendingInstallReceipt = null;
                pendingInstallContract = null;
                pendingSupersededPredecessor = null;
                if (!preserveInstalledContract)
                {
                    InfraCurrentContract = null;
                    lastCommittedReceipt = null;
                }
            }

            authorityDecisions.Enqueue(decision);
            pendingRequest = null;
            if (decision.Status == RuntimeUnlockRetryDispatchDecisionStatus.Rejected &&
                     string.Equals(
                         decision.Reason,
                         "retry_dispatch_missing_successor_limit_exhausted",
                         StringComparison.Ordinal))
            {
                InfraCurrentContract = null;
            }

            return true;
        }

        public bool ObserveRuntimeUnlockRetryDispatchSuccessorDecision(
            RuntimeUnlockRetryDispatchSuccessorDecision decision)
        {
            successorDecisions.Enqueue(decision);
            if (decision.Status !=
                RuntimeUnlockRetryDispatchSuccessorDecisionStatus.ChainClosed)
            {
                return true;
            }

            var predecessor = InfraCurrentContract;
            var closure = decision.State is null
                ? null
                : FileTransferCoordinator
                    .GetRuntimeUnlockRetryLifecycle(decision.State)
                    .BoundedClosure;
            if (predecessor is null ||
                predecessor.Lifecycle.SettlementOutcome !=
                    RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch ||
                decision.Request?.PredecessorAuthorityId !=
                    predecessor.Authority.AuthorityId ||
                closure is not { IsDefault: false } exactClosure ||
                exactClosure.FinalAttempt.Authority != predecessor.Authority)
            {
                return false;
            }

            pendingStage = null;
            pendingStageStagedUtcMs = 0;
            pendingStageExpiresUtcMs = 0;
            pendingInstallReceipt = null;
            pendingInstallContract = null;
            pendingSupersededPredecessor = null;
            InfraCurrentContract = null;
            lastCommittedReceipt = null;
            return true;
        }

        public RuntimeUnlockRetryDispatchDecisionStageResult StageRuntimeUnlockRetryDispatchDecision(
            RuntimeUnlockRetryDispatchDecision decision)
        {
            authorityDecisions.Enqueue(decision);
            RuntimeUnlockRetryDispatchDecisionStageResult result;
            if (RejectNextStage)
            {
                RejectNextStage = false;
                result = new RuntimeUnlockRetryDispatchDecisionStageResult(
                    RuntimeUnlockRetryDispatchDecisionStageStatus.Rejected,
                    "test_retry_dispatch_stage_rejected",
                    decision)
                {
                    StageDisposition = pendingStage.HasValue
                        ? RuntimeUnlockRetryDispatchStageDisposition.Retained
                        : RuntimeUnlockRetryDispatchStageDisposition.Rejected,
                    StageCleared = false,
                    StagedUtcMs = pendingStageStagedUtcMs,
                    StageExpiresUtcMs = pendingStageExpiresUtcMs,
                };
            }
            else if (!decision.Granted)
            {
                result = new RuntimeUnlockRetryDispatchDecisionStageResult(
                    RuntimeUnlockRetryDispatchDecisionStageStatus.Rejected,
                    "test_retry_dispatch_stage_non_grant",
                    decision)
                {
                    StageDisposition = pendingStage.HasValue
                        ? RuntimeUnlockRetryDispatchStageDisposition.Retained
                        : RuntimeUnlockRetryDispatchStageDisposition.Rejected,
                    StageCleared = false,
                    StagedUtcMs = pendingStageStagedUtcMs,
                    StageExpiresUtcMs = pendingStageExpiresUtcMs,
                };
            }
            else if (lastCommittedReceipt is { } committed &&
                     decision.Contract is { } committedContract &&
                     committed.Authority.Equals(committedContract.Authority) &&
                     committed.Request == decision.Request &&
                     InfraCurrentContract?.Authority == committedContract.Authority)
            {
                result = new RuntimeUnlockRetryDispatchDecisionStageResult(
                    RuntimeUnlockRetryDispatchDecisionStageStatus.Idempotent,
                    "retry_dispatch_decision_stage_duplicate",
                    decision)
                {
                    StageDisposition = RuntimeUnlockRetryDispatchStageDisposition.AlreadyInstalled,
                    StageCleared = false,
                    StagedUtcMs = 0,
                    StageExpiresUtcMs = 0,
                };
            }
            else if (pendingStage is { } staged && staged.Equals(decision))
            {
                result = new RuntimeUnlockRetryDispatchDecisionStageResult(
                    RuntimeUnlockRetryDispatchDecisionStageStatus.Idempotent,
                    "retry_dispatch_decision_stage_duplicate",
                    decision)
                {
                    StageDisposition = RuntimeUnlockRetryDispatchStageDisposition.Retained,
                    StageCleared = false,
                    StagedUtcMs = pendingStageStagedUtcMs,
                    StageExpiresUtcMs = pendingStageExpiresUtcMs,
                };
            }
            else if (pendingStage.HasValue)
            {
                result = new RuntimeUnlockRetryDispatchDecisionStageResult(
                    RuntimeUnlockRetryDispatchDecisionStageStatus.Rejected,
                    "test_retry_dispatch_stage_mismatch",
                    decision)
                {
                    StageDisposition = RuntimeUnlockRetryDispatchStageDisposition.Retained,
                    StageCleared = false,
                    StagedUtcMs = pendingStageStagedUtcMs,
                    StageExpiresUtcMs = pendingStageExpiresUtcMs,
                };
            }
            else
            {
                pendingStage = decision;
                pendingStageStagedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                pendingStageExpiresUtcMs = pendingStageStagedUtcMs + 15_000;
                result = new RuntimeUnlockRetryDispatchDecisionStageResult(
                    RuntimeUnlockRetryDispatchDecisionStageStatus.Staged,
                    "retry_dispatch_decision_staged",
                    decision)
                {
                    StageDisposition = RuntimeUnlockRetryDispatchStageDisposition.Retained,
                    StageCleared = false,
                    StagedUtcMs = pendingStageStagedUtcMs,
                    StageExpiresUtcMs = pendingStageExpiresUtcMs,
                };
            }

            stageResults.Enqueue(result);
            StageObserved.Set();
            StageReturnBarrier?.Wait(TimeSpan.FromSeconds(5));
            return result;
        }

        public RuntimeUnlockRetryDispatchInstallResult CommitRuntimeUnlockRetryDispatchInstall(
            RuntimeUnlockRetryDispatchInstallReceipt receipt)
        {
            var recentLog = LocalOperationalLog.GetRecentLogText();
            LastReceiptIssuedEvidenceObservedBeforeCommit =
                recentLog.Contains(
                    "event=runtime_unlock_retry_dispatch_install_receipt_issued;",
                    StringComparison.Ordinal) &&
                recentLog.Contains(
                    $"transfer_id={receipt.Authority.TransferId};",
                    StringComparison.Ordinal) &&
                recentLog.Contains(
                    $"install_generation={receipt.InstallGeneration};",
                    StringComparison.Ordinal);

            if (CoreGateForInstallProbe is { } coreGate)
            {
                LastInstallObservedUnderCoreGate = Monitor.IsEntered(coreGate);
            }

            CommitObserved.Set();
            CommitReturnBarrier?.Wait(TimeSpan.FromSeconds(5));

            RuntimeUnlockRetryDispatchInstallResult result;
            if (RejectNextInstall)
            {
                RejectNextInstall = false;
                result = new RuntimeUnlockRetryDispatchInstallResult(
                    RuntimeUnlockRetryDispatchInstallStatus.Rejected,
                    "test_retry_dispatch_install_rejected",
                    receipt);
            }
            else if ((lastCommittedReceipt is { } committed &&
                      committed.Equals(receipt)) ||
                     (pendingInstallReceipt is { } proposed &&
                      proposed.Equals(receipt)))
            {
                result = new RuntimeUnlockRetryDispatchInstallResult(
                    RuntimeUnlockRetryDispatchInstallStatus.Idempotent,
                    "retry_dispatch_install_duplicate",
                    receipt);
            }
            else if (pendingStage is not { } staged ||
                     !staged.Granted ||
                     !staged.Contract!.Authority.Equals(receipt.Authority) ||
                     staged.Request != receipt.Request ||
                     receipt.StageStatus is not (
                         RuntimeUnlockRetryDispatchDecisionStageStatus.Staged or
                         RuntimeUnlockRetryDispatchDecisionStageStatus.Idempotent) ||
                     receipt.StageDisposition !=
                         RuntimeUnlockRetryDispatchStageDisposition.Retained ||
                     receipt.StageCleared ||
                     receipt.StagedUtcMs != pendingStageStagedUtcMs ||
                     receipt.StageExpiresUtcMs != pendingStageExpiresUtcMs)
            {
                result = new RuntimeUnlockRetryDispatchInstallResult(
                    RuntimeUnlockRetryDispatchInstallStatus.Rejected,
                    "test_retry_dispatch_install_mismatch",
                    receipt);
            }
            else if (staged.Contract.RequestActivePredecessorSupersession &&
                     (InfraCurrentContract is not { } predecessor ||
                      predecessor.Authority.AuthorityId !=
                          staged.Contract.Authority.PredecessorAuthorityId))
            {
                result = new RuntimeUnlockRetryDispatchInstallResult(
                    RuntimeUnlockRetryDispatchInstallStatus.Rejected,
                    "test_retry_dispatch_install_predecessor_mismatch",
                    receipt);
            }
            else
            {
                pendingInstallReceipt = receipt;
                pendingInstallContract = staged.Contract;
                pendingSupersededPredecessor =
                    staged.Contract.RequestActivePredecessorSupersession
                        ? InfraCurrentContract
                        : null;
                result = new RuntimeUnlockRetryDispatchInstallResult(
                    RuntimeUnlockRetryDispatchInstallStatus.Installed,
                    "retry_dispatch_install_committed",
                    receipt);
            }

            installResults.Enqueue(result);
            return result;
        }

        public RuntimeUnlockRetryDispatchScopeInvalidationStageResult
            StageRuntimeUnlockRetryDispatchScopeInvalidation(
                RuntimeUnlockRetryDispatchScopeInvalidationCommand command)
        {
            if (command.IsDefault)
            {
                return new RuntimeUnlockRetryDispatchScopeInvalidationStageResult(
                    command,
                    RuntimeUnlockRetryDispatchScopeInvalidationStageStatus
                        .Rejected,
                    "test_retry_dispatch_scope_invalidation_command_missing");
            }

            if (pendingScopeInvalidationCommand is { } pending)
            {
                return new RuntimeUnlockRetryDispatchScopeInvalidationStageResult(
                    command,
                    pending == command
                        ? RuntimeUnlockRetryDispatchScopeInvalidationStageStatus
                            .Idempotent
                        : RuntimeUnlockRetryDispatchScopeInvalidationStageStatus
                            .Rejected,
                    pending == command
                        ? "test_retry_dispatch_scope_invalidation_duplicate"
                        : "test_retry_dispatch_scope_invalidation_pending_mismatch");
            }

            if (lastScopeInvalidationReceipt is { } completed &&
                completed.Command == command &&
                pendingStage is null &&
                (command.Kind ==
                     RuntimeUnlockRetryDispatchScopeInvalidationKind
                         .PendingStageExpiry
                    ? command.InstalledAttempt?.InstallReceipt ==
                      lastCommittedReceipt
                    : InfraCurrentContract is null))
            {
                return new RuntimeUnlockRetryDispatchScopeInvalidationStageResult(
                    command,
                    RuntimeUnlockRetryDispatchScopeInvalidationStageStatus
                        .Idempotent,
                    "test_retry_dispatch_scope_invalidation_already_applied");
            }

            var pendingMatches =
                command.PendingAttempt is not { } pendingAttempt ||
                pendingStage is { } staged &&
                staged.Request == pendingAttempt.Request &&
                staged.Contract?.Authority == pendingAttempt.Authority;
            var installedMatches =
                command.InstalledAttempt is not { } installedAttempt ||
                InfraCurrentContract?.Authority == installedAttempt.Authority &&
                installedAttempt.InstallReceipt == lastCommittedReceipt;
            var predecessorMatches =
                command.RetainedPredecessor is not { } predecessor ||
                pendingSupersededPredecessor?.Authority ==
                    predecessor.Authority;
            if (!pendingMatches || !installedMatches || !predecessorMatches)
            {
                return new RuntimeUnlockRetryDispatchScopeInvalidationStageResult(
                    command,
                    RuntimeUnlockRetryDispatchScopeInvalidationStageStatus
                        .Rejected,
                    "test_retry_dispatch_scope_invalidation_shadow_mismatch");
            }

            pendingScopeInvalidationCommand = command;
            return new RuntimeUnlockRetryDispatchScopeInvalidationStageResult(
                command,
                RuntimeUnlockRetryDispatchScopeInvalidationStageStatus.Staged,
                "test_retry_dispatch_scope_invalidation_staged");
        }

        public RuntimeUnlockRetryDispatchScopeInvalidationReceipt
            ApplyRuntimeUnlockRetryDispatchScopeInvalidation(
                RuntimeUnlockRetryDispatchScopeInvalidationCommand command)
        {
            if (lastScopeInvalidationReceipt is { } completed &&
                completed.Command == command &&
                pendingScopeInvalidationCommand is null)
            {
                return new RuntimeUnlockRetryDispatchScopeInvalidationReceipt(
                    command,
                    RuntimeUnlockRetryDispatchScopeInvalidationStatus
                        .Idempotent,
                    "test_retry_dispatch_scope_invalidation_duplicate");
            }

            if (pendingScopeInvalidationCommand != command)
            {
                return new RuntimeUnlockRetryDispatchScopeInvalidationReceipt(
                    command,
                    RuntimeUnlockRetryDispatchScopeInvalidationStatus.Rejected,
                    "test_retry_dispatch_scope_invalidation_not_staged");
            }

            pendingStage = null;
            pendingStageStagedUtcMs = 0;
            pendingStageExpiresUtcMs = 0;
            pendingInstallReceipt = null;
            pendingInstallContract = null;
            pendingRequest = null;
            pendingSupersededPredecessor = null;
            if (command.Kind ==
                RuntimeUnlockRetryDispatchScopeInvalidationKind.FullScope)
            {
                InfraCurrentContract = null;
                lastCommittedReceipt = null;
            }
            var receipt =
                new RuntimeUnlockRetryDispatchScopeInvalidationReceipt(
                    command,
                    RuntimeUnlockRetryDispatchScopeInvalidationStatus.Applied,
                    "test_retry_dispatch_scope_invalidation_applied");
            pendingScopeInvalidationCommand = null;
            lastScopeInvalidationReceipt = receipt;
            return receipt;
        }

        public bool ConfirmRuntimeUnlockRetryDispatchInstall(
            RuntimeUnlockRetryDispatchInstallReceipt receipt)
        {
            if (RejectNextConfirmation)
            {
                RejectNextConfirmation = false;
                return false;
            }

            if (lastCommittedReceipt == receipt &&
                InfraCurrentContract?.Authority == receipt.Authority)
            {
                return true;
            }

            if (pendingInstallReceipt != receipt ||
                pendingInstallContract is not { } contract ||
                contract.Authority != receipt.Authority)
            {
                return false;
            }

            InfraCurrentContract = contract;
            lastCommittedReceipt = receipt;
            pendingInstallReceipt = null;
            pendingInstallContract = null;
            pendingStage = null;
            pendingStageStagedUtcMs = 0;
            pendingStageExpiresUtcMs = 0;
            pendingRequest = null;
            Interlocked.Increment(ref installObservationCount);
            return true;
        }

        public void FlushInstallFollowUp()
        {
            if (!predecessorSettlementDecisionPendingDrain)
            {
                return;
            }

            predecessorSettlementDecisionPendingDrain = false;
            Assert.True(settlementDecisions.TryDequeue(out var decision));
            Assert.Equal(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted,
                decision.Status);
        }

        public bool ObserveRuntimeUnlockRetryDispatchSettlementDecision(
            RuntimeUnlockRetryDispatchSettlementDecision decision)
        {
            Interlocked.Increment(ref settlementDecisionObservationCount);
            if (CoreGateForSettlementDecisionProbe is { } coreGate)
            {
                LastSettlementDecisionObservedWithCoreGateAvailable = Task.Run(() =>
                {
                    if (!Monitor.TryEnter(coreGate, TimeSpan.FromMilliseconds(500)))
                    {
                        return false;
                    }

                    Monitor.Exit(coreGate);
                    return true;
                }).GetAwaiter().GetResult();
            }

            if (decision.Status is
                    RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted or
                    RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent)
            {
                RuntimeUnlockRetryDispatchContract? settlementContract;
                if (decision.Report.Outcome ==
                    RuntimeUnlockRetryDispatchSettlementOutcome.Superseded)
                {
                    var predecessor = pendingSupersededPredecessor is { } pending &&
                                      pending.Authority ==
                                      decision.Report.Authority
                        ? pending
                        : lastAppliedSettlementContract is { } applied &&
                          applied.Authority == decision.Report.Authority
                            ? applied
                            : null;
                    if (InfraCurrentContract is not { } successor ||
                        predecessor is null ||
                        decision.Report.Authority != predecessor.Authority ||
                        decision.Report.SuccessorAuthorityId !=
                            successor.Authority.AuthorityId)
                    {
                        return false;
                    }

                    settlementContract = predecessor;
                    if (ReferenceEquals(
                            pendingSupersededPredecessor,
                            predecessor))
                    {
                        pendingSupersededPredecessor = null;
                        predecessorSettlementDecisionPendingDrain = true;
                    }
                }
                else
                {
                    settlementContract = InfraCurrentContract is { } current &&
                                         current.Authority ==
                                         decision.Report.Authority
                        ? current
                        : null;
                }

                if (settlementContract is null ||
                    !settlementContract.TryApplySettlementReport(decision.Report) ||
                    !settlementContract.TryCreateSettlementReport(
                        out var appliedReport) ||
                    appliedReport != decision.Report)
                {
                    return false;
                }

                lastAppliedSettlementContract = settlementContract;
            }

            settlementDecisions.Enqueue(decision);
            SettlementDecisionObserved.Set();
            return true;
        }

        public bool ObserveRuntimeUnlockRegularV4RecoverySettlementDecision(
            RuntimeUnlockRegularV4RecoverySettlementDecision decision)
        {
            regularV4SettlementDecisions.Enqueue(decision);
            return true;
        }

        public bool ObserveRuntimeUnlockRegularV4RecoveryLifecycleDecision(
            RuntimeUnlockRegularV4RecoveryLifecycleDecision decision)
        {
            bridgeRecoveryCompletionObservations.Enqueue("lifecycle");
            regularV4LifecycleDecisions.Enqueue(decision);
            return true;
        }

        public FileTransferBridgeRecoveryExecutionStartResult BeginBridgeRecoveryExecution(
            FileTransferBridgeRecoveryExecutionCommand command)
            => new(
                command,
                FileTransferBridgeRecoveryExecutionStartStatus.Rejected,
                "test_bridge_recovery_execution_not_started",
                Completion: null);

        public void ObserveBridgeRecoveryExecutionReceiptDecision(
            FileTransferBridgeRecoveryExecutionReceipt receipt,
            bool accepted,
            string reason)
        {
            bridgeRecoveryCompletionObservations.Enqueue("receipt");
            if (ThrowOnBridgeRecoveryExecutionReceiptDecision)
            {
                throw new InvalidOperationException(
                    "test_bridge_recovery_execution_receipt_notification_failed");
            }

            bridgeRecoveryExecutionReceiptDecisions.Enqueue((receipt, accepted, reason));
        }

        public bool InvalidateBridgeRecoveryExecution(
            FileTransferBridgeRecoveryExecutionCommand command,
            string reason)
            => true;
    }
}
