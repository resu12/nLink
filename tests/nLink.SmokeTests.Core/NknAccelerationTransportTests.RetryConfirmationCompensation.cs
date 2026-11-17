using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Smoke")]
    [Trait("FileTransferInfraSuite", "RuntimeUnlockRetryLifecycle")]
    public async Task RuntimeUnlockRetryDispatchInstall_CurrentStateCompensationClearsOnlyExactProposal(
        bool withPredecessor)
    {
        using var fixture = await CreateRuntimeUnlockRetryDispatchInstallFixtureAsync(
            $"confirmation-compensation-{withPredecessor}",
            requiresLocalListenerRetry: true);
        var decision = Assert.Single(fixture.Decisions);
        RuntimeUnlockRetryDispatchContract? predecessor = null;
        RuntimeUnlockRetryDispatchInstallReceipt? predecessorReceipt = null;
        if (withPredecessor)
        {
            predecessor = Assert.IsType<RuntimeUnlockRetryDispatchContract>(decision.Contract);
            var predecessorStage = fixture.Bridge.StageRuntimeUnlockRetryDispatchDecision(decision);
            predecessorReceipt = InstallRuntimeUnlockRetryDispatchThroughCoordinatorForTests(
                fixture, decision, predecessorStage).Receipt;
            await fixture.Host.DrainRuntimeUnlockRetryDispatchInstallFollowUpsForTestsAsync();
            decision = CreateCoordinatorActiveSuccessorDecisionForTests(fixture);
        }

        var contract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(decision.Contract);
        var stage = fixture.Bridge.StageRuntimeUnlockRetryDispatchDecision(decision);
        Assert.True(stage.Accepted, stage.Reason);
        var issued = IssueRuntimeUnlockRetryDispatchInstallReceiptForTests(decision, stage);
        var proposal = fixture.Bridge.CommitRuntimeUnlockRetryDispatchInstall(issued.Receipt);
        Assert.True(proposal.Accepted, proposal.Reason);
        var commit = FileTransferCoordinator.CommitRuntimeUnlockRetryDispatchInstall(
            issued.State, proposal, checked(issued.Receipt.StagedUtcMs + 1));
        Assert.True(commit.Accepted, commit.Reason);
        fixture.CoordinatorState = commit.State;

        var pendingStage = GetPendingRuntimeUnlockRetryDispatchDecisionStage(fixture.Host);
        var pendingFollowUp = GetPrivateField(
            fixture.Host, "runtimeUnlockRetryDispatchPendingInstallFollowUp");
        Assert.NotNull(pendingStage);
        Assert.NotNull(pendingFollowUp);
        var deadlines = new[]
        {
            "RetryDeadlineUtcMs", "DispatchDeadlineUtcMs",
            "ObservedSendDeadlineUtcMs", "LivenessDeferralDeadlineUtcMs",
        }.ToDictionary(name => name, name => GetPrivatePropertyValue<long>(fixture.State, name));
        var logStart = GetOperationalLogLength();

        // This mismatched callback is a separate negative control, not the cause
        // of the exact compensation below. Service tests inject confirmation
        // failure; this test verifies the real adapter's proposal cleanup.
        Assert.False(fixture.Bridge.ConfirmRuntimeUnlockRetryDispatchInstall(
            issued.Receipt with { InstallGeneration = issued.Receipt.InstallGeneration + 1 }));
        Assert.Same(pendingStage, GetPendingRuntimeUnlockRetryDispatchDecisionStage(fixture.Host));
        Assert.Same(pendingFollowUp, GetPrivateField(
            fixture.Host, "runtimeUnlockRetryDispatchPendingInstallFollowUp"));

        var compensation = FileTransferCoordinator.CompensateRejectedRuntimeUnlockRetryDispatchConfirmation(
            commit.State, Assert.IsType<RuntimeUnlockRetryConfirmationCompensationContext>(commit.CompensationContext));
        Assert.True(compensation.Accepted, compensation.Reason);
        fixture.CoordinatorState = compensation.State;
        var rejection = new RuntimeUnlockRetryDispatchDecision(
            RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
            "retry_dispatch_install_confirmation_rejected",
            contract)
        {
            Request = decision.Request,
            State = compensation.State,
        };
        foreach (var unrelated in new[]
        {
            rejection with { Contract = null },
            rejection with
            {
                Request = decision.Request with
                {
                    DispatchAttemptGeneration = decision.Request.DispatchAttemptGeneration + 1,
                },
            },
        })
        {
            Assert.False(fixture.Bridge.ObserveRuntimeUnlockRetryDispatchNonGrantDecision(unrelated));
            Assert.Same(pendingStage, GetPendingRuntimeUnlockRetryDispatchDecisionStage(fixture.Host));
            Assert.Same(pendingFollowUp, GetPrivateField(
                fixture.Host, "runtimeUnlockRetryDispatchPendingInstallFollowUp"));
            Assert.Same(predecessor, fixture.State.GetType().GetProperty("DispatchContract")!
                .GetValue(fixture.State));
            Assert.Equal(predecessorReceipt, GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(fixture.Host));
        }

        Assert.True(fixture.Bridge.ObserveRuntimeUnlockRetryDispatchNonGrantDecision(rejection));
        Assert.Null(GetPendingRuntimeUnlockRetryDispatchDecisionStage(fixture.Host));
        Assert.Null(GetPrivateField(fixture.Host, "runtimeUnlockRetryDispatchPendingInstallFollowUp"));
        Assert.Null(fixture.State.GetType().GetProperty("PendingDispatchAuthorityRequest")!
            .GetValue(fixture.State));
        Assert.Null(fixture.State.GetType().GetProperty("DispatchPredecessorContract")!
            .GetValue(fixture.State));
        Assert.Same(predecessor, fixture.State.GetType().GetProperty("DispatchContract")!
            .GetValue(fixture.State));
        Assert.Equal(predecessorReceipt, GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(fixture.Host));
        Assert.Equal(withPredecessor, GetPrivatePropertyValue<bool>(fixture.State, "DispatchAuthorityGranted"));
        Assert.Equal(withPredecessor, GetPrivatePropertyValue<bool>(fixture.State, "RetryAuthorityGranted"));
        if (predecessor is not null)
        {
            Assert.False(predecessor.Lifecycle.IsSettled);
            Assert.Equal(predecessor.Authority,
                FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(compensation.State).CurrentAuthorizedAuthority);
        }
        foreach (var deadline in deadlines)
        {
            Assert.Equal(withPredecessor ? deadline.Value : 0L,
                GetPrivatePropertyValue<long>(fixture.State, deadline.Key));
        }

        Assert.False(fixture.Bridge.ObserveRuntimeUnlockRetryDispatchNonGrantDecision(rejection));
        Assert.Same(predecessor, fixture.State.GetType().GetProperty("DispatchContract")!
            .GetValue(fixture.State));
        Assert.Equal(predecessorReceipt, GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(fixture.Host));
        await fixture.Host.DrainRuntimeUnlockRetryDispatchInstallFollowUpsForTestsAsync();
        Assert.DoesNotContain(
            $"event=runtime_unlock_retry_dispatch_authority_granted; authority_schema=2; authority_id={contract.Authority.AuthorityId};",
            ReadOperationalLogTail(logStart), StringComparison.Ordinal);
    }
}
