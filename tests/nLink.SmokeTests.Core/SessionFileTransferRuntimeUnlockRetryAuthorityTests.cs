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
        Assert.Equal(1, bridge.RegularV4SettlementReportSubscriberCount);
        Assert.Equal(1, bridge.RegularV4LifecycleReportSubscriberCount);

        service.DetachTransport();

        Assert.Equal(0, bridge.AuthorityRequestSubscriberCount);
        Assert.Equal(0, bridge.SettlementReportSubscriberCount);
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
        Assert.Same(contract, GetCurrentContract(fixture.Context));
        Assert.Equal(install.Receipt, GetCurrentInstallReceipt(fixture.Context));

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
        var duplicateInstall = fixture.Bridge.DequeueInstallResult();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Granted, duplicateDecision.Status);
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStageStatus.Idempotent, duplicateStage.Status);
        Assert.Equal(
            RuntimeUnlockRetryDispatchStageDisposition.AlreadyInstalled,
            duplicateStage.StageDisposition);
        Assert.False(duplicateStage.StageCleared);
        Assert.Equal(0, duplicateStage.StageExpiresUtcMs);
        Assert.Equal(RuntimeUnlockRetryDispatchInstallStatus.Idempotent, duplicateInstall.Status);
        Assert.Equal(firstReceipt, duplicateInstall.Receipt);
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
        Assert.Null(GetCurrentContract(fixture.Context));
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
        Assert.Null(GetCurrentContract(fixture.Context));
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

        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStatus.Granted,
            fixture.Bridge.DequeueAuthorityDecision().Status);
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStageStatus.Staged,
            fixture.Bridge.DequeueStageResult().Status);
        Assert.Equal(
            RuntimeUnlockRetryDispatchInstallStatus.Installed,
            fixture.Bridge.DequeueInstallResult().Status);
        Assert.Equal(1, fixture.Bridge.InstallCount);
        Assert.NotNull(fixture.Bridge.InfraCurrentContract);
        Assert.NotNull(GetCurrentContract(fixture.Context));
        Assert.NotNull(GetCurrentInstallReceipt(fixture.Context));
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
        Assert.StartsWith("retry_dispatch_install_", rejection.Reason, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Bridge.InstallCount);
        Assert.False(fixture.Bridge.HasPendingStage);
        Assert.Null(fixture.Bridge.InfraCurrentContract);
        Assert.Null(GetCurrentContract(fixture.Context));
        Assert.Null(GetCurrentInstallReceipt(fixture.Context));
    }

    [Fact]
    public async Task PredecessorLineageReplacementBetweenStageAndRevalidation_PreventsInstall()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_stage_predecessor_replaced",
            "transfer_retry_stage_predecessor_replaced");
        var predecessor = Grant(fixture, fixture.InitialRequest);
        var successorRequest = fixture.InitialRequest with
        {
            DispatchAttemptGeneration = 2,
            PredecessorAuthorityId = predecessor.Authority.AuthorityId,
            RequestActivePredecessorSupersession = true,
        };
        using var allowStageReturn = new ManualResetEventSlim(initialState: false);
        fixture.Bridge.StageReturnBarrier = allowStageReturn;
        fixture.Bridge.StageObserved.Reset();

        var request = Task.Run(() => fixture.Bridge.RaiseAuthorityRequest(successorRequest));
        Assert.True(fixture.Bridge.StageObserved.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStatus.Granted,
            fixture.Bridge.DequeueAuthorityDecision().Status);
        Assert.False(predecessor.Lifecycle.IsSettled);
        Assert.Same(
            predecessor,
            GetPendingPredecessorContract(fixture.Context));

        lock (GetCoreGate(fixture.Service))
        {
            SetProperty(
                fixture.Context,
                "PendingRuntimeUnlockRetryDispatchPredecessorContract",
                null);
        }

        allowStageReturn.Set();
        await request.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStageStatus.Staged,
            fixture.Bridge.DequeueStageResult().Status);
        var rejection = fixture.Bridge.DequeueAuthorityDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, rejection.Status);
        Assert.Equal("retry_dispatch_install_revalidation_failed", rejection.Reason);
        Assert.Equal(1, fixture.Bridge.InstallCount);
        Assert.False(fixture.Bridge.HasPendingStage);
        Assert.Null(fixture.Bridge.InfraCurrentContract);
        Assert.Null(GetCurrentContract(fixture.Context));
        Assert.Null(GetCurrentInstallReceipt(fixture.Context));
        Assert.Null(GetPendingPredecessorContract(fixture.Context));
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
        Assert.Null(GetCurrentContract(fixture.Context));
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
            Assert.Null(GetCurrentContract(stageFixture.Context));
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
        Assert.Null(GetCurrentContract(installFixture.Context));
        Assert.Null(GetCurrentInstallReceipt(installFixture.Context));
        Assert.Equal(1, GetLastInstallGeneration(installFixture.Context));
    }

    [Fact]
    public async Task SuccessorInstall_UsesNextGenerationAndCannotBeClearedByOldReceipt()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_install_successor",
            "transfer_retry_install_successor");
        var predecessor = Grant(fixture, fixture.InitialRequest);
        var predecessorReceipt = Assert.IsType<RuntimeUnlockRetryDispatchInstallReceipt>(
            fixture.Bridge.LastCommittedReceipt);
        var successorLogStart = GetOperationalLogLength();
        var successorRequest = fixture.InitialRequest with
        {
            DispatchAttemptGeneration = 2,
            PredecessorAuthorityId = predecessor.Authority.AuthorityId,
            RequestActivePredecessorSupersession = true,
        };

        using var allowStageReturn = new ManualResetEventSlim(initialState: false);
        fixture.Bridge.StageReturnBarrier = allowStageReturn;
        fixture.Bridge.StageObserved.Reset();
        var successorRequestTask = Task.Run(
            () => fixture.Bridge.RaiseAuthorityRequest(successorRequest));
        Assert.True(fixture.Bridge.StageObserved.Wait(TimeSpan.FromSeconds(3)));
        var successorDecision = fixture.Bridge.DequeueAuthorityDecision();
        var successor = Assert.IsType<RuntimeUnlockRetryDispatchContract>(
            successorDecision.Contract);
        Assert.False(predecessor.Lifecycle.IsSettled);
        Assert.Same(predecessor, fixture.Bridge.InfraCurrentContract);
        Assert.Same(predecessor, GetPendingPredecessorContract(fixture.Context));

        allowStageReturn.Set();
        await successorRequestTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStageStatus.Staged,
            fixture.Bridge.DequeueStageResult().Status);
        Assert.Equal(
            RuntimeUnlockRetryDispatchInstallStatus.Installed,
            fixture.Bridge.DequeueInstallResult().Status);
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
        Assert.Null(GetPendingPredecessorContract(fixture.Context));
        Assert.Equal(predecessorReceipt.InstallGeneration + 1, successorReceipt.InstallGeneration);
        Assert.Equal(successor.Authority, successorReceipt.Authority);
        var late = fixture.Bridge.CommitRuntimeUnlockRetryDispatchInstall(predecessorReceipt);
        Assert.Equal(RuntimeUnlockRetryDispatchInstallStatus.Rejected, late.Status);
        Assert.Equal(2, fixture.Bridge.InstallCount);
        Assert.Same(successor, fixture.Bridge.InfraCurrentContract);
        Assert.Same(successor, GetCurrentContract(fixture.Context));
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
        var report = SettleMissingDispatch(contract, offerGeneration: 101);
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
        Assert.Same(contract, GetCurrentContract(fixture.Context));
        Assert.True(fixture.Bridge.LastSettlementDecisionObservedWithCoreGateAvailable);

        fixture.Bridge.RaiseSettlementReport(report);

        var duplicate = fixture.Bridge.DequeueSettlementDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent, duplicate.Status);
        Assert.Equal("retry_dispatch_settlement_duplicate", duplicate.Reason);
        Assert.Same(contract, GetCurrentContract(fixture.Context));
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
        Assert.Null(GetCurrentContract(fixture.Context));
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
        Assert.Null(GetCurrentContract(fixture.Context));
        Assert.Null(fixture.Bridge.InfraCurrentContract);

        var leg = GetRequiredProperty<FileTransferLeg>(fixture.Context, "CurrentTransferLeg");
        var priorTransportEpoch = leg.TransportEpochId;
        leg.TransportEpochId++;
        SeedPairedFallbackTailYield(fixture.Context, tailYield);

        fixture.Bridge.RaiseAuthorityRequest(request);

        var granted = fixture.Bridge.DequeueAuthorityDecision();
        Assert.True(granted.Granted);
        Assert.Equal(request, granted.Request);
        Assert.Equal(priorTransportEpoch + 1, granted.Contract!.Authority.TransportEpoch);
        Assert.Equal(tailYield.AuthorityId, granted.Contract.Authority.ReleaseAuthorityId);
        Assert.Equal(tailYield.YieldGeneration, granted.Contract.Authority.ReleaseGeneration);
        Assert.Same(granted.Contract, GetCurrentContract(fixture.Context));
        Assert.Same(granted.Contract, fixture.Bridge.InfraCurrentContract);
    }

    [Fact]
    public async Task LateSupersededPredecessorReport_IsDiagnosticOnlyAndCannotClearSuccessor()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_late_predecessor",
            "transfer_retry_late_predecessor");
        var predecessor = Grant(fixture, fixture.InitialRequest);
        var successorRequest = fixture.InitialRequest with
        {
            DispatchAttemptGeneration = 2,
            PredecessorAuthorityId = predecessor.Authority.AuthorityId,
            RequestActivePredecessorSupersession = true,
        };
        var successor = Grant(fixture, successorRequest);
        Assert.True(predecessor.TryCreateSettlementReport(out var predecessorReport));
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementOutcome.Superseded,
            predecessorReport.Outcome);

        fixture.Bridge.RaiseSettlementReport(predecessorReport);

        var decision = fixture.Bridge.DequeueSettlementDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent, decision.Status);
        Assert.Same(successor, GetCurrentContract(fixture.Context));
        Assert.Equal(predecessorReport, GetLastSettlement(fixture.Context));
        Assert.Same(successor, fixture.Bridge.InfraCurrentContract);

        var mismatchedLateReport = predecessorReport with
        {
            Reason = "late_predecessor_mismatch",
        };
        fixture.Bridge.RaiseSettlementReport(mismatchedLateReport);

        var rejected = fixture.Bridge.DequeueSettlementDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected, rejected.Status);
        Assert.Same(successor, GetCurrentContract(fixture.Context));
        Assert.Equal(predecessorReport, GetLastSettlement(fixture.Context));
        Assert.Same(successor, fixture.Bridge.InfraCurrentContract);
    }

    [Fact]
    public async Task TerminalBeforeSettlementReport_ClearsOnceAndLateReportIsIdempotent()
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
        Assert.Null(GetCurrentContract(fixture.Context));
        Assert.True(contract.TryCreateSettlementReport(out var terminalReport));
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementOutcome.TransferTerminal,
            terminalReport.Outcome);
        Assert.Equal(terminalReport, GetLastSettlement(fixture.Context));

        fixture.Bridge.RaiseSettlementReport(terminalReport);

        var lateDecision = fixture.Bridge.DequeueSettlementDecision();
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent,
            lateDecision.Status);
        Assert.Null(GetCurrentContract(fixture.Context));
        Assert.Equal(terminalReport, GetLastSettlement(fixture.Context));
        Assert.Equal(1, fixture.Bridge.SettlementDecisionCount);
    }

    [Fact]
    public async Task BoundedMissingDispatchRejection_SynchronouslyClearsCoreAndBridgeState()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_bounded",
            "transfer_retry_bounded");
        var zeroOfferInitialRequest = fixture.InitialRequest with { RetiredOfferGeneration = 0 };
        var predecessor = Grant(fixture, zeroOfferInitialRequest);
        var predecessorReport = SettleMissingDispatchBeforeOffer(predecessor);
        fixture.Bridge.RaiseSettlementReport(predecessorReport);
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted,
            fixture.Bridge.DequeueSettlementDecision().Status);

        var successorRequest = zeroOfferInitialRequest with
        {
            DispatchAttemptGeneration = 2,
            MissingDispatchSuccessorGeneration = 1,
            PredecessorAuthorityId = predecessor.Authority.AuthorityId,
        };
        var successor = Grant(fixture, successorRequest);
        Assert.Equal(0, successor.Authority.RetiredOfferGeneration);
        Assert.Equal(0, successor.OfferGeneration);
        var successorReport = SettleMissingDispatchBeforeOffer(successor);
        fixture.Bridge.RaiseSettlementReport(successorReport);
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted,
            fixture.Bridge.DequeueSettlementDecision().Status);

        var exhaustedRequest = successorRequest with
        {
            RetiredOfferGeneration = successor.OfferGeneration,
            DispatchAttemptGeneration = 3,
            MissingDispatchSuccessorGeneration = 2,
            PredecessorAuthorityId = successor.Authority.AuthorityId,
        };
        fixture.Bridge.RaiseAuthorityRequest(exhaustedRequest);

        var exhausted = fixture.Bridge.DequeueAuthorityDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, exhausted.Status);
        Assert.Equal("retry_dispatch_missing_successor_limit_exhausted", exhausted.Reason);
        Assert.Null(exhausted.State.CurrentRuntimeUnlockRetryDispatchAuthority);
        Assert.Null(GetCurrentContract(fixture.Context));
        Assert.Null(fixture.Bridge.InfraCurrentContract);
        Assert.Equal(successorReport, GetLastSettlement(fixture.Context));
    }

    private static RuntimeUnlockRetryDispatchContract Grant(
        RetryFixture fixture,
        RuntimeUnlockRetryDispatchAuthorityRequest request)
    {
        fixture.Bridge.RaiseAuthorityRequest(request);
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
        Assert.True(installed.Receipt.InstallGeneration > 0);
        Assert.Same(contract, GetCurrentContract(fixture.Context));
        Assert.Same(contract, fixture.Bridge.InfraCurrentContract);
        Assert.Equal(installed.Receipt, GetCurrentInstallReceipt(fixture.Context));
        Assert.Equal(
            installed.Receipt.InstallGeneration,
            GetLastInstallGeneration(fixture.Context));
        return contract;
    }

    private static RuntimeUnlockRetryDispatchSettlementReport SettleMissingDispatch(
        RuntimeUnlockRetryDispatchContract contract,
        long offerGeneration)
    {
        Assert.True(contract.TryBindOfferAlias(offerGeneration, $"nonce-{offerGeneration}"));
        Assert.True(contract.TryBeginSend(out var attempt));
        Assert.True(contract.TryCompleteMissingDispatch(attempt, "retry_dispatch_not_observed"));
        Assert.True(contract.TryCreateSettlementReport(out var report));
        return report;
    }

    private static RuntimeUnlockRetryDispatchSettlementReport SettleMissingDispatchBeforeOffer(
        RuntimeUnlockRetryDispatchContract contract)
    {
        Assert.Equal(0, contract.OfferGeneration);
        Assert.True(contract.TrySettle(
            RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch,
            "retry_dispatch_not_observed_before_offer"));
        Assert.True(contract.TryCreateSettlementReport(out var report));
        return report;
    }

    private static (
        RuntimeUnlockRetryDispatchAuthorityRequest Request,
        FallbackTailYieldDecision TailYield) SeedPostTunaFallbackRetrySource(object context)
    {
        var regularState = CreateCoordinatorState(context);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var tunaActivated = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaActivated,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "test_tuna_activation",
                FileTransferLegState.Active,
                CanSendData: true,
                CommittedChunkIndex: 0,
                HighestObservedChunkIndex: 0,
                TransportEpochId: 1,
                BridgeRecoveryGeneration: 1),
            regularState);
        var fallbackApplied = FileTransferCoordinator.Apply(
            new FileTransferCoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaLost,
                fallback,
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn,
                "test_post_tuna_fallback",
                FileTransferLegState.Active,
                CanSendData: true,
                CommittedChunkIndex: 0,
                HighestObservedChunkIndex: 0,
                TransportEpochId: 2,
                BridgeRecoveryGeneration: 1),
            tunaActivated.State);
        ApplyCoordinatorDecision(context, fallbackApplied);

        var owner = Assert.IsType<RuntimeOwnerToken>(fallbackApplied.State.CurrentRuntimeOwner);
        var leg = Assert.IsType<FileTransferLeg>(fallbackApplied.State.CurrentLeg);
        var publication = FileTransferCoordinator.AuthorizeAction(
            fallbackApplied.State,
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
                TerminalGeneration: fallbackApplied.State.TerminalGeneration,
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
                BridgeDecisionGeneration: 0),
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
        SetProperty(context, "PeerFallbackTailYieldAuthorityId", tailYield.AuthorityId.ToString());
        SetProperty(context, "PeerFallbackTailYieldGeneration", (long)tailYield.YieldGeneration);
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
        method.Invoke(null, [context, decision]);
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
        SeedRegularV4RecoveryLifecycle(object context)
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
            FileTransferDirection.Outbound,
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
            FileTransferDirection.Outbound,
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
                SetProperty(context, "CurrentRuntimeUnlockRetryDispatchContract", null);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(replacementKind),
                    replacementKind,
                    "Unknown install-currentness replacement.");
        }
    }

    private static RuntimeUnlockRetryDispatchContract? GetCurrentContract(object context)
        => context.GetType()
            .GetProperty(
                "CurrentRuntimeUnlockRetryDispatchContract",
                BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(context) as RuntimeUnlockRetryDispatchContract;

    private static RuntimeUnlockRetryDispatchContract? GetPendingPredecessorContract(
        object context)
        => context
            .GetType()
            .GetProperty(
                "PendingRuntimeUnlockRetryDispatchPredecessorContract",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context) as RuntimeUnlockRetryDispatchContract;

    private static RuntimeUnlockRetryDispatchSettlementReport? GetLastSettlement(object context)
    {
        var value = context.GetType()
            .GetProperty(
                "LastRuntimeUnlockRetryDispatchSettlement",
                BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(context);
        return value is RuntimeUnlockRetryDispatchSettlementReport report ? report : null;
    }

    private static RuntimeUnlockRetryDispatchInstallReceipt? GetCurrentInstallReceipt(
        object context)
        => GetOptionalStructProperty<RuntimeUnlockRetryDispatchInstallReceipt>(
            context,
            "CurrentRuntimeUnlockRetryDispatchInstallReceipt");

    private static long GetLastInstallGeneration(object context)
        => GetRequiredProperty<long>(
            context,
            "LastRuntimeUnlockRetryDispatchInstallGeneration");

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
        IRuntimeUnlockRetryDispatchAuthorityBridge
    {
        private readonly ConcurrentQueue<RuntimeUnlockRetryDispatchDecision> authorityDecisions = [];
        private readonly ConcurrentQueue<RuntimeUnlockRetryDispatchDecisionStageResult> stageResults = [];
        private readonly ConcurrentQueue<RuntimeUnlockRetryDispatchInstallResult> installResults = [];
        private readonly ConcurrentQueue<RuntimeUnlockRetryDispatchSettlementDecision> settlementDecisions = [];
        private readonly ConcurrentQueue<RuntimeUnlockRegularV4RecoverySettlementDecision> regularV4SettlementDecisions = [];
        private readonly ConcurrentQueue<RuntimeUnlockRegularV4RecoveryLifecycleDecision> regularV4LifecycleDecisions = [];
        private EventHandler<RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs>? authorityRequested;
        private EventHandler<RuntimeUnlockRetryDispatchSettlementReportedEventArgs>? settlementReported;
        private EventHandler<RuntimeUnlockRegularV4RecoverySettlementReportedEventArgs>? regularV4SettlementReported;
        private EventHandler<RuntimeUnlockRegularV4RecoveryLifecycleReportedEventArgs>? regularV4LifecycleReported;
        private RuntimeUnlockRetryDispatchAuthorityRequest? pendingRequest;
        private RuntimeUnlockRetryDispatchDecision? pendingStage;
        private long pendingStageStagedUtcMs;
        private long pendingStageExpiresUtcMs;
        private RuntimeUnlockRetryDispatchInstallReceipt? lastCommittedReceipt;
        private int settlementDecisionObservationCount;
        private int installObservationCount;

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

        public int AuthorityRequestSubscriberCount =>
            authorityRequested?.GetInvocationList().Length ?? 0;

        public int SettlementReportSubscriberCount =>
            settlementReported?.GetInvocationList().Length ?? 0;

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

        public bool TryGetPendingRuntimeUnlockRetryDispatchAuthorityRequest(
            string requestedSessionId,
            string transferId,
            FileTransferDirection direction,
            out RuntimeUnlockRetryDispatchAuthorityRequest request)
        {
            if (pendingRequest is { } pending &&
                string.Equals(pending.SessionId, requestedSessionId, StringComparison.Ordinal) &&
                string.Equals(pending.TransferId, transferId, StringComparison.Ordinal) &&
                pending.Direction == direction)
            {
                request = pending;
                return true;
            }

            request = default;
            return false;
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
                pendingStage = null;
                pendingStageStagedUtcMs = 0;
                pendingStageExpiresUtcMs = 0;
                InfraCurrentContract = null;
                lastCommittedReceipt = null;
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
                     ReferenceEquals(InfraCurrentContract, committedContract))
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
            else if (lastCommittedReceipt is { } committed && committed.Equals(receipt))
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
                          staged.Contract.Authority.PredecessorAuthorityId ||
                      !predecessor.TrySupersedeBeforeSend(
                          "coordinator_successor_installed",
                          staged.Contract.Authority.AuthorityId)))
            {
                result = new RuntimeUnlockRetryDispatchInstallResult(
                    RuntimeUnlockRetryDispatchInstallStatus.Rejected,
                    "test_retry_dispatch_install_predecessor_mismatch",
                    receipt);
            }
            else
            {
                InfraCurrentContract = staged.Contract;
                lastCommittedReceipt = receipt;
                pendingStage = null;
                pendingStageStagedUtcMs = 0;
                pendingStageExpiresUtcMs = 0;
                pendingRequest = null;
                Interlocked.Increment(ref installObservationCount);
                result = new RuntimeUnlockRetryDispatchInstallResult(
                    RuntimeUnlockRetryDispatchInstallStatus.Installed,
                    "retry_dispatch_install_committed",
                    receipt);
            }

            installResults.Enqueue(result);
            return result;
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
            regularV4LifecycleDecisions.Enqueue(decision);
            return true;
        }
    }
}
