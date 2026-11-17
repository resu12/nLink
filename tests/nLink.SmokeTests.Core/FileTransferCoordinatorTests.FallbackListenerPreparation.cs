using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FallbackListenerPreparation")]
    public void FallbackListenerPreparation_PendingRecoveryAllowsPhysicalWorkWithoutDispatchAuthority()
    {
        var (state, request, tail) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource();
        var now = DateTimeOffset.UtcNow;
        var security = Guid.NewGuid();
        var before = state.PersistentState;
        var permit = FileTransferCoordinator.CreateFallbackListenerPreparationPermit(state, security, now.AddMinutes(5), now);
        Assert.NotNull(permit);
        Assert.True(FileTransferCoordinator.IsFallbackListenerPreparationPermitCurrent(state, permit!, security, now));
        Assert.Equal(before, state.PersistentState);
        Assert.Null(state.CurrentRuntimeUnlockRetryDispatchAuthority);
        Assert.False(FileTransferCoordinator.TryCreateFallbackPairedTailYieldRuntimeUnlockRetryDispatchRequest(
            state, tail, default, "runtime_unlock", "fallback", out _, out _));
        Assert.NotNull(request.FallbackPairedTailYieldEvidence);
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData("publication")]
    [InlineData("security")]
    [InlineData("expired")]
    [InlineData("regular")]
    [InlineData("tuna")]
    [Trait("FileTransferCoordinatorSuite", "FallbackListenerPreparation")]
    public void FallbackListenerPreparation_RequiresExactCurrentFallbackAndSecurity(string change)
    {
        var (state, _, _) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource();
        var now = DateTimeOffset.UtcNow;
        var security = Guid.NewGuid();
        var permit = FileTransferCoordinator.CreateFallbackListenerPreparationPermit(state, security, now.AddMinutes(5), now)!;
        if (change == "terminal") state = state with { IsTerminal = true };
        if (change == "publication") state = state with { CurrentRoutePublicationToken = null };
        if (change == "regular") state = state with { RouteSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast) };
        if (change == "tuna") state = state with { RouteSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4) };
        if (change == "security") security = Guid.NewGuid();
        if (change == "expired") now = permit.ExpiresUtc;
        Assert.False(FileTransferCoordinator.IsFallbackListenerPreparationPermitCurrent(state, permit, security, now));
        if (change != "security")
            Assert.Null(FileTransferCoordinator.CreateFallbackListenerPreparationPermit(state, security, permit.ExpiresUtc, now));
    }

    internal static FileTransferCoordinatorState CreateFallbackPreparationStateForTests(string sessionId, string transferId)
    {
        var state = ActiveFallbackStateWithoutCheckpoint();
        var owner = state.CurrentRuntimeOwner!.Value with { SessionId = sessionId, TransferId = transferId };
        state = state with { SessionId = sessionId, TransferId = transferId, CurrentRuntimeOwner = owner,
            CurrentRoutePublicationToken = null };
        var leg = state.CurrentLeg!;
        var published = FileTransferCoordinator.AuthorizeAction(state, new(
            FileTransferCoordinatorActionKind.RoutePublication, "test", "fallback_preparation",
            RuntimeOwner: owner, Route: state.RouteSelection.Route, ProtocolVersion: 6,
            LiveRouteEpoch: leg.LiveRouteEpochId, LegGeneration: leg.Generation,
            RuntimeGeneration: owner.RuntimeGeneration, TerminalGeneration: state.TerminalGeneration,
            TargetRoute: state.RouteSelection.Route, TargetProtocolVersion: 6,
            TargetTransport: FileTransferTransportKind.RegularNkn));
        Assert.True(published.Accepted, published.RejectionReason);
        return published.State;
    }
}
