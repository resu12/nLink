using System.Text.RegularExpressions;

namespace NLink.SmokeTests;

public sealed class RuntimeUnlockRetryLifecycleStructuralTests
{
    [Fact]
    [Trait("FileTransferInfraSuite", "RuntimeUnlockRetryLifecycle")]
    public void RuntimeUnlockRetryLifecycle_PhysicalCompletionReconcilesOnlyAfterDrainInFinally()
    {
        var source = File.ReadAllText(Path.Combine(CoreSmokeTestsBase.FindRepoRoot(),
            "src", "nLink.Infra.Nkn", "NknSignalingTransport.Acceleration.cs"));
        var start = source.IndexOf("private async Task TrySendAccelerationOfferAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("private async Task TrySendAccelerationOfferCoreAsync(", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var wrapper = source[start..end];
        var finallyIndex = wrapper.IndexOf("finally", StringComparison.Ordinal);
        var drain = wrapper.IndexOf("await physicalSendScope.DrainAsync()", StringComparison.Ordinal);
        var capture = wrapper.IndexOf("TryBindMissingDispatchPredecessorOperationCompletionLatch(", StringComparison.Ordinal);
        var reconcile = wrapper.IndexOf("TryReconcileSettledMissingDispatchPredecessorAfterOperationCompletion(", StringComparison.Ordinal);
        Assert.True(finallyIndex >= 0 && drain > finallyIndex && capture > drain && reconcile > capture);
        Assert.Single(Regex.Matches(source, @"_ = TryBindMissingDispatchPredecessorOperationCompletionLatch\("));
        Assert.Contains("catch (OperationCanceledException)", source, StringComparison.Ordinal);
        Assert.Contains("negotiation_operation_cancelled", source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "RuntimeUnlockRetryLifecycle")]
    public void RuntimeUnlockRetryLifecycle_ReceiveResumedUsesExactCommandRendezvous()
    {
        var source = File.ReadAllText(Path.Combine(CoreSmokeTestsBase.FindRepoRoot(),
            "src", "nLink.Infra.Nkn", "NknSignalingTransport.cs"));
        Assert.Matches(
            @"if \(current && e.Kind == BridgeLifecycleEventKind.ReceiveStallRecoveryReceiveResumed\)\s*" +
            @"\{\s*ObserveRuntimeUnlockRetryPostFallbackReceiveResumed\(executionCommand\);\s*\}",
            source);
        var acceleration = File.ReadAllText(Path.Combine(CoreSmokeTestsBase.FindRepoRoot(),
            "src", "nLink.Infra.Nkn", "NknSignalingTransport.Acceleration.cs"));
        var observer = Regex.Match(acceleration,
            @"private void ObserveRuntimeUnlockRetryPostFallbackReceiveResumed\([\s\S]*?\n    \}");
        Assert.True(observer.Success);
        Assert.Contains("captured.RecoveryCommand == command", observer.Value, StringComparison.Ordinal);
        Assert.Contains("ReconcileRuntimeUnlockRetryPostFallbackReceiveRecovery(captured)", observer.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("SchedulePending", observer.Value, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferRuntimeSuite", "RuntimeUnlockRetryLifecycle")]
    public void RuntimeUnlockRetryLifecycle_CoreContextsRetainOnlyCoordinatorPersistentState()
    {
        var sourcePath = Path.Combine(
            CoreSmokeTestsBase.FindRepoRoot(),
            "src",
            "nLink.Core",
            "FileTransfer",
            "SessionFileTransferService.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotMatch(
            @"public\s+RuntimeUnlockRetryDispatchContract\?\s+" +
            @"CurrentRuntimeUnlockRetryDispatchContract\s*\{\s*get;\s*set;\s*\}",
            source);
        Assert.DoesNotMatch(
            @"public\s+RuntimeUnlockRetryDispatchContract\?\s+" +
            @"PendingRuntimeUnlockRetryDispatchPredecessorContract\s*\{\s*get;\s*set;\s*\}",
            source);
        Assert.DoesNotMatch(
            @"public\s+RuntimeUnlockRetryDispatchInstallReceipt\?\s+" +
            @"CurrentRuntimeUnlockRetryDispatchInstallReceipt\s*\{\s*get;\s*set;\s*\}",
            source);
        Assert.DoesNotMatch(
            @"public\s+int\s+LastRuntimeUnlockRetryDispatchAuthorityGeneration\s*" +
            @"\{\s*get;\s*set;\s*\}",
            source);
        Assert.DoesNotMatch(
            @"public\s+long\s+LastRuntimeUnlockRetryDispatchInstallGeneration\s*" +
            @"\{\s*get;\s*set;\s*\}",
            source);
        Assert.DoesNotMatch(
            @"public\s+RuntimeUnlockRetryDispatchSettlementReport\?\s+" +
            @"LastRuntimeUnlockRetryDispatchSettlement\s*\{\s*get;\s*set;\s*\}",
            source);
    }

    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "RuntimeUnlockRetryLifecycle")]
    public void RuntimeUnlockRetryLifecycle_InfraHasNoSuccessorOrDeadlineAuthority()
    {
        var sourcePath = Path.Combine(
            CoreSmokeTestsBase.FindRepoRoot(),
            "src",
            "nLink.Infra.Nkn",
            "NknSignalingTransport.Acceleration.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain(
            "RuntimeUnlockRetrySuccessorIntent",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PrepareRuntimeUnlockRetryMissingDispatchSuccessorUnsafe",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SettleRuntimeUnlockRetryDispatchAuthorityIfUnobserved",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RuntimeUnlockRetryDispatchBoundedClosureReceipt",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BoundedDispatchClosureReceipt",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PendingDispatchSuccessorIntent",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PendingDispatchSuccessorRequestIssued",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PendingDispatchSuccessorRequestHeld",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PendingDispatchSuccessorHeldLogged",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotMatch(
            @"new\s+RuntimeUnlockRetryDispatchAuthorityRequest\s*\(",
            source);
        Assert.Contains(
            "RuntimeUnlockRetryDispatchInitialEvaluation",
            source,
            StringComparison.Ordinal);
        var currentnessMethod = Regex.Match(
            source,
            @"private\s+bool\s+" +
            @"HasCurrentGrantedRuntimeUnlockRetryDispatchContractUnsafe" +
            @"\s*\([\s\S]*?\n\s{4}\}");
        Assert.True(currentnessMethod.Success);
        Assert.DoesNotContain(
            "state.DispatchAuthorityGranted",
            currentnessMethod.Value,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "state.DispatchAuthorityRejected",
            currentnessMethod.Value,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferRuntimeSuite", "RuntimeUnlockRetryLifecycle")]
    public void RuntimeUnlockRetryLifecycle_IsOwnedByCoordinatorPersistentState()
    {
        var sourcePath = Path.Combine(
            CoreSmokeTestsBase.FindRepoRoot(),
            "src",
            "nLink.Core",
            "FileTransfer",
            "FileTransferCoordinator.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains(
            "RuntimeUnlockRetryLifecycleSubstate",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RuntimeUnlockRetryLifecycle",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "EvaluateRuntimeUnlockRetryDispatchStageExpiry",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CloseRuntimeUnlockRetryDispatchChain",
            source,
            StringComparison.Ordinal);

        var stateDeclaration = Regex.Match(
            source,
            @"internal\s+sealed\s+record\s+FileTransferCoordinatorState\s*\(" +
            @"(?<parameters>[\s\S]*?)\)\s*\{");
        Assert.True(stateDeclaration.Success);
        var stateParameters = stateDeclaration.Groups["parameters"].Value;
        Assert.DoesNotContain(
            "CurrentRuntimeUnlockRetryDispatchAuthority",
            stateParameters,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LastRuntimeUnlockRetryDispatchAuthorityGeneration",
            stateParameters,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LastRuntimeUnlockRetryDispatchSettlement",
            stateParameters,
            StringComparison.Ordinal);
        Assert.Matches(
            @"CurrentRuntimeUnlockRetryDispatchAuthority\s*=>\s*" +
            @"PersistentState\.RuntimeUnlockRetryLifecycle\." +
            @"CurrentAuthorizedAuthority",
            source);
        Assert.Matches(
            @"LastRuntimeUnlockRetryDispatchAuthorityGeneration\s*=>\s*" +
            @"PersistentState\.RuntimeUnlockRetryLifecycle\." +
            @"LastAuthorityGeneration",
            source);
        Assert.Matches(
            @"LastRuntimeUnlockRetryDispatchSettlement\s*=>\s*" +
            @"PersistentState\.RuntimeUnlockRetryLifecycle\.LastSettlement",
            source);

        Assert.Matches(
            @"internal\s+static\s+RuntimeUnlockRetryDispatchSettlementDecision\s*" +
            @"ObserveRuntimeUnlockRetryDispatchSettlement\s*\(\s*" +
            @"FileTransferCoordinatorState\s+state\s*,\s*" +
            @"RuntimeUnlockRetryDispatchSettlementReport\s+report\s*\)",
            source);
        Assert.DoesNotMatch(
            @"ObserveRuntimeUnlockRetryDispatchSettlement\s*\(\s*" +
            @"FileTransferCoordinatorState\s+state\s*,\s*" +
            @"RuntimeUnlockRetryDispatchContract",
            source);
        Assert.Single(
            Regex.Matches(
                    source,
                    @"internal\s+static\s+RuntimeUnlockRetryDispatchSettlementDecision\s*" +
                    @"ObserveRuntimeUnlockRetryDispatchSettlement\s*\(")
                .OfType<Match>());
    }

    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "RuntimeUnlockRetryLifecycle")]
    public void RuntimeUnlockRetryLifecycle_InstalledAttemptPolicyIsTypedNotReasonDerived()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var contractSource = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "nLink.Core",
            "FileTransfer",
            "IFileTransferSignalingTransport.cs"));
        var infraSource = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "nLink.Infra.Nkn",
            "NknSignalingTransport.Acceleration.cs"));

        Assert.Contains(
            "RuntimeUnlockRetryDispatchListenerRearmRequirement",
            contractSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsRuntimeUnlockActivationRetryReason(state.RetryReason)",
            infraSource,
            StringComparison.Ordinal);
        Assert.DoesNotMatch(
            @"RejectRuntimeUnlockRecoveryRetryBeforeDispatchIfActive[\s\S]{0,1800}" +
            @"IsRuntimeUnlockActivationRetryReason\(state\.RetryReason\)",
            infraSource);
        Assert.DoesNotMatch(
            @"TryDeferRuntimeUnlockListenerRearmRetryForActiveRegularV4Recovery" +
            @"[\s\S]{0,1800}IsRuntimeUnlockActivationRetryReason\(state\.RetryReason\)",
            infraSource);
        Assert.DoesNotMatch(
            @"holdDispatchGrantForListenerRearm\s*=[\s\S]{0,400}" +
            @"RuntimeUnlockProbeSidecarClosedRetryReason",
            infraSource);
        Assert.DoesNotMatch(
            @"ScheduleAccelerationNegotiationRetry\s*\([\s\S]{0,2200}" +
            @"IsRuntimeUnlockActivationRetryReason",
            infraSource);
        Assert.DoesNotMatch(
            @"TryGrantRuntimeUnlockRecoveryRetryDispatchAuthorityForOfferSend\s*" +
            @"\([\s\S]{0,400}IsRuntimeUnlockActivationReason",
            infraSource);
        Assert.DoesNotMatch(
            @"public\s+int\s+AuthorityAttempt\s*\{\s*get;\s*set;",
            infraSource);
        Assert.Contains(
            "public int AuthorityAttempt => DispatchAttemptGeneration;",
            infraSource,
            StringComparison.Ordinal);
    }
}
