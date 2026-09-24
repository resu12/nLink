using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class FileTransferRouteTransitionInfraTests
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [InlineData(FileTransferDirection.Inbound, true)]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task FallbackProjection_FirstCommitBindsEpochWithoutObservation(
        FileTransferDirection direction, bool observeRecoveredEpoch)
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(direction, commitFallback: false);
        var transport = fixture.Transport;
        var bridge = (IFileTransferRouteTransitionBridge)transport;
        var projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        var type = projection.GetType();
        Assert.Equal(0L, type.GetProperty("FileV6TransportEpoch")!.GetValue(projection));
        Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(new(fixture.FallbackEntry.Authority)).Status);
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(fixture.FallbackEntry).Status);
        await WaitUntilAsync(() => GetPendingRouteTransitionAvailabilityDisposition(transport) is null);
        Assert.Equal(fixture.Recovered.TransportEpoch, type.GetProperty("FileV6TransportEpoch")!.GetValue(projection));
        Assert.Equal(fixture.FallbackEntry.RoutePublicationToken, type.GetProperty("FileProjectionPublication")!.GetValue(projection));
        Assert.Null(type.GetProperty("FileV6EpochState")!.GetValue(projection));
        Assert.Null(type.GetProperty("FileProjectionRetirement")!.GetValue(projection));
        Assert.False(transport.IsFileTunaActiveForRouteSelection);

        // The committed cleanup marks this diagnostic lane recovered; no epoch
        // observation is needed to supply the already-issued command's epoch.
        MarkFallbackProjectionRecovered(transport, fixture.Reactivation.SessionId);
        if (observeRecoveredEpoch) transport.ObserveFileTransferV6TransportEpoch(fixture.Recovered);
        Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(new(fixture.Reactivation)).Status);
        Assert.True(IsFallbackProjectionActive(transport));
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(CreateCommittedDisposition(fixture.Reactivation)).Status);
        Assert.False(IsFallbackProjectionActive(transport));
        Assert.NotNull(type.GetProperty("FileProjectionRetirement")!.GetValue(projection));
        Assert.False(transport.IsFileTunaActiveForRouteSelection);
    }

    [Theory]
    [InlineData(7L)]
    [InlineData(6L)]
    [InlineData(8L)]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task FallbackProjection_FirstCommitPreservesObservedEpoch(long observedEpoch)
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(FileTransferDirection.Outbound, commitFallback: false);
        var transport = fixture.Transport;
        var bridge = (IFileTransferRouteTransitionBridge)transport;
        InvokePrivate(transport, "MarkTunaFallbackFileV6EpochState", fixture.Reactivation.SessionId,
            observedEpoch, V6TransportEpochState.WaitingForTargetTransport, "observation_before_commit");
        Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(new(fixture.FallbackEntry.Authority)).Status);
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(fixture.FallbackEntry).Status);
        await WaitUntilAsync(() => GetPendingRouteTransitionAvailabilityDisposition(transport) is null);
        var projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        Assert.Equal(observedEpoch, projection.GetType().GetProperty("FileV6TransportEpoch")!.GetValue(projection));
        Assert.Equal(V6TransportEpochState.WaitingForTargetTransport,
            projection.GetType().GetProperty("FileV6EpochState")!.GetValue(projection));
        MarkFallbackProjectionRecovered(transport, fixture.Reactivation.SessionId);
        bridge.BindFileTransferRouteTransitionPause(new(fixture.Reactivation));
        bridge.ApplyFileTransferRouteTransitionDisposition(CreateCommittedDisposition(fixture.Reactivation));
        Assert.Equal(observedEpoch != fixture.Recovered.TransportEpoch, IsFallbackProjectionActive(transport));
    }

    [Theory]
    [InlineData("missing_proof")]
    [InlineData("wrong_proof")]
    [InlineData("zero_epoch")]
    [InlineData("negative_epoch")]
    [InlineData("wrong_protocol")]
    [InlineData("wrong_transport")]
    [InlineData("wrong_session")]
    [InlineData("wrong_transfer")]
    [InlineData("wrong_direction")]
    [InlineData("missing_binding")]
    [InlineData("terminal_first")]
    [InlineData("rollback")]
    [InlineData("retained_predecessor")]
    [InlineData("new_local_stop")]
    [InlineData("new_peer_stop")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task FallbackProjection_FirstCommitCannotBindInvalidOrStaleEpoch(string control)
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(FileTransferDirection.Outbound, commitFallback: false);
        var transport = fixture.Transport;
        var bridge = (IFileTransferRouteTransitionBridge)transport;
        var command = fixture.FallbackEntry;
        var authority = command.Authority;
        if (control == "missing_proof") authority = authority with { ProofBoundary = null! };
        if (control == "wrong_proof") authority = authority with { ProofBoundary = fixture.Reactivation.ProofBoundary };
        if (control is "zero_epoch" or "negative_epoch") authority = authority with
        { ProofBoundary = ((FileTransferFallbackTransitionProofBoundary)authority.ProofBoundary) with
            { TargetTransportEpoch = control == "zero_epoch" ? 0 : -1 } };
        if (control == "wrong_protocol") authority = authority with { TargetProtocolVersion = 4 };
        if (control == "wrong_transport") authority = authority with { TargetTransport = FileTransferTransportKind.Tuna };
        command = command with { Authority = authority };
        if (control is not "missing_binding" and not "missing_proof") bridge.BindFileTransferRouteTransitionPause(new(authority));
        if (control == "wrong_session") command = command with { RoutePublicationToken = command.RoutePublicationToken!.Value with { SessionId = "other" } };
        if (control == "wrong_transfer") command = command with { RoutePublicationToken = command.RoutePublicationToken!.Value with { TransferId = "other" } };
        if (control == "wrong_direction") command = command with { RoutePublicationToken = command.RoutePublicationToken!.Value with { Direction = FileTransferDirection.Inbound } };
        if (control == "terminal_first") bridge.ApplyFileTransferRouteTransitionDisposition(new(authority,
            FileTransferRouteTransitionDispositionKind.TerminalInvalidated, null, null, "terminal"));
        if (control == "rollback") command = new(authority, FileTransferRouteTransitionDispositionKind.RolledBack, null, null, "rollback");
        if (control == "retained_predecessor")
        {
            var successor = authority with { AuthorityId = new FileTransferAuthorityId("fta3-epoch-successor"),
                TransitionGeneration = authority.TransitionGeneration + 1, PredecessorTransitionAuthorityId = authority.AuthorityId };
            Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
                bridge.BindFileTransferRouteTransitionPause(new(successor) { PredecessorAuthority = authority }).Status);
        }
        if (control is "new_local_stop" or "new_peer_stop")
        {
            MarkFallbackProjectionRecovered(transport, authority.SessionId);
            Assert.True((bool)InvokePrivate(transport, "StartTunaFallbackProofIfNeeded",
                control == "new_local_stop" ? "header_switch_off" : "remote_header_switch_off",
                authority.SessionId, NknAccelerationLaneKind.File)!);
        }
        var projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        if (control == "missing_proof")
        {
            // A null proof is rejected by Core. Exercise the epoch reducer's
            // guard directly: the bridge's existing diagnostic formatter expects a non-null proof.
            Assert.False(FileTransferCoordinator.IsFileTransferRouteTransitionDispositionCommandValid(command));
            Assert.False((bool)InvokePrivate(transport, "ApplyFileFallbackProjectionDispositionUnsafe", command, projection)!);
        }
        else bridge.ApplyFileTransferRouteTransitionDisposition(command);
        Assert.Same(projection, GetPrivateFieldValue(transport, "tunaFallbackProofState"));
        Assert.Equal(0L, projection.GetType().GetProperty("FileV6TransportEpoch")!.GetValue(projection));
        Assert.Null(projection.GetType().GetProperty("FileProjectionRetirement")!.GetValue(projection));
        Assert.False(transport.IsFileTunaActiveForRouteSelection);
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task FallbackProjection_DuplicateFirstCommitCannotRebindNewerEpoch()
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(FileTransferDirection.Outbound);
        var bridge = (IFileTransferRouteTransitionBridge)fixture.Transport;
        InvokePrivate(fixture.Transport, "MarkTunaFallbackFileV6EpochState", fixture.Reactivation.SessionId,
            8L, V6TransportEpochState.Recovered, "newer_epoch");
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Idempotent,
            bridge.ApplyFileTransferRouteTransitionDisposition(fixture.FallbackEntry).Status);
        var projection = GetPrivateFieldValue(fixture.Transport, "tunaFallbackProofState")!;
        Assert.Equal(8L, projection.GetType().GetProperty("FileV6TransportEpoch")!.GetValue(projection));
        Assert.Null(projection.GetType().GetProperty("FileProjectionRetirement")!.GetValue(projection));
        Assert.True(IsFallbackProjectionActive(fixture.Transport));
    }

    private static void MarkFallbackProjectionRecovered(NknSignalingTransport transport, string sessionId)
    {
        var projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        var stateType = projection.GetType().GetProperty("FileState")!.PropertyType;
        InvokePrivate(transport, "MarkTunaFallbackLaneState", sessionId,
            NknAccelerationLaneKind.File, Enum.Parse(stateType, "Recovered"), "committed_cleanup");
    }
}
