using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "ColdDialerPreparation")]
    public void ColdDialerPreparation_BindingDoesNotGrantRetryOrProbeAuthority()
    {
        var (state, retry, _) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource();
        var request = ColdDialerRequest(state, retry);
        var before = FileTransferCoordinator.CalculateSnapshotFingerprint(state);
        var bound = FileTransferCoordinator.BindOutboundFallbackDialerPreparation(state, request, null);
        Assert.True(bound.Accepted, bound.Reason);
        Assert.True(bound.Receipt.IsOutboundPreparation);
        Assert.False(bound.Receipt.IsDefault);
        Assert.True(bound.Receipt.AcceptanceScope.IsDefault);
        Assert.Equal(before, FileTransferCoordinator.CalculateSnapshotFingerprint(state));
        Assert.Null(FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(state).InstalledAttempt);
        Assert.Null(state.CurrentRuntimeUnlockPendingPreCommitProbe);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, state.CurrentRoutePublicationToken!.Value.Route);
        Assert.Equal(RuntimeUnlockPostFallbackPairedRetryOfferBindingStatus.Idempotent,
            FileTransferCoordinator.BindOutboundFallbackDialerPreparation(state, request, bound.Receipt).Status);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("transfer")]
    [InlineData("publication")]
    [InlineData("transport")]
    [InlineData("bridge")]
    [InlineData("checkpoint")]
    [InlineData("data_identity")]
    [InlineData("data_generation")]
    [InlineData("terminal")]
    [InlineData("missing_pair")]
    [InlineData("unpaired")]
    [Trait("FileTransferCoordinatorSuite", "ColdDialerPreparation")]
    public void ColdDialerPreparation_RejectsStaleScopeWithoutMutation(string change)
    {
        var (state, retry, _) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource();
        var request = ColdDialerRequest(state, retry);
        var previous = FileTransferCoordinator.BindOutboundFallbackDialerPreparation(state, request, null).Receipt;
        Assert.False(previous.IsDefault);
        var authority = request.PublicationAuthority;
        request = change switch
        {
            "session" => request with { PublicationAuthority = authority with { SessionId = "replaced" } },
            "transfer" => request with { PublicationAuthority = authority with { TransferId = "replaced" } },
            "publication" => request with { PublicationAuthority = authority with { RoutePublicationGeneration = authority.RoutePublicationGeneration + 1 } },
            "transport" => request with { TransportEpoch = request.TransportEpoch + 1 },
            "bridge" => request with { BridgeRecoveryGeneration = request.BridgeRecoveryGeneration + 1 },
            "checkpoint" => request with { CheckpointRequestId = "new-checkpoint" },
            "data_identity" => request with { DataSessionIdentity = "replaced" },
            "data_generation" => request with { DataSessionGeneration = request.DataSessionGeneration + 1 },
            _ => request,
        };
        if (change == "terminal") state = state with { IsTerminal = true };
        if (change is "missing_pair" or "unpaired")
        {
            var tail = state.PersistentState.FallbackTailYield;
            state = state with { PersistentState = state.PersistentState with { FallbackTailYield = tail with
            {
                CurrentOperation = change == "missing_pair" ? null : tail.CurrentOperation!.Value with
                { PairingState = FileTransferFallbackTailYieldPairingState.YieldIssued },
            } } };
        }
        var before = FileTransferCoordinator.CalculateSnapshotFingerprint(state);
        var result = FileTransferCoordinator.BindOutboundFallbackDialerPreparation(state, request, previous);
        Assert.False(result.Accepted);
        Assert.True(result.Receipt.IsDefault);
        Assert.Equal(before, FileTransferCoordinator.CalculateSnapshotFingerprint(state));
        Assert.Null(FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(state).InstalledAttempt);
        Assert.Null(state.CurrentRuntimeUnlockPendingPreCommitProbe);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "ColdDialerPreparation")]
    public void ColdDialerPreparation_ConsumedPairRequiresPreviousBindingAndExactCurrentInstall()
    {
        var (state, retry, yield) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource();
        var request = ColdDialerRequest(state, retry);
        var bound = FileTransferCoordinator.BindOutboundFallbackDialerPreparation(state, request, null);
        Assert.True(bound.Accepted, bound.Reason);
        var grant = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(state, retry,
            RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield, yield.AuthorityId,
            yield.YieldGeneration, yield.YieldKind.ToString());
        Assert.True(grant.Granted);
        var installed = StageAndInstallPersistentRetryLifecycle(grant.State, grant, retry, stagedUtcMs: 3_000_000);
        var after = FileTransferCoordinator.BindOutboundFallbackDialerPreparation(installed.State, request, bound.Receipt);
        Assert.True(after.Accepted, after.Reason);
        Assert.Equal(bound.Receipt, after.Receipt);
        Assert.False(FileTransferCoordinator.BindOutboundFallbackDialerPreparation(installed.State, request, null).Accepted);
        Assert.False(FileTransferCoordinator.BindOutboundFallbackDialerPreparation(installed.State,
            request with { MessageId = "different-offer" }, bound.Receipt).Accepted);
        var stale = installed.State with { PersistentState = installed.State.PersistentState with
        {
            RuntimeUnlockRetryLifecycle = FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(installed.State) with
            { InstalledAttempt = null },
        } };
        Assert.False(FileTransferCoordinator.BindOutboundFallbackDialerPreparation(stale, request, bound.Receipt).Accepted);
    }

    private static RuntimeUnlockPostFallbackPairedRetryOfferBindingRequest ColdDialerRequest(
        FileTransferCoordinatorState state, RuntimeUnlockRetryDispatchAuthorityRequest retry)
        => new(retry.SourcePublicationAuthority, state.CurrentLeg!.TransportEpochId,
            state.CurrentLeg.BridgeRecoveryGeneration, state.CurrentLeg.CheckpointRequestId ?? "(none)",
            state.CurrentDataSessionIdentity!, state.CurrentDataSessionGeneration, 7, "authenticated-offer", new string('a', 64));
}
