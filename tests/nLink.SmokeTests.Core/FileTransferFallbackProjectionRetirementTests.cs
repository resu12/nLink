using NLink.Core.Configuration;
using NLink.Core.FileTransfer;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class FileTransferRouteTransitionInfraTests
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task FallbackProjection_CommittedReactivationRetiresOnlyFileProjection(FileTransferDirection direction)
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(direction);
        var transport = fixture.Transport;
        var bridge = (IFileTransferRouteTransitionBridge)transport;
        Assert.True(IsFallbackProjectionActive(transport));
        var projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        var laneStateType = projection.GetType().GetProperty("ScreenState")!.PropertyType;
        InvokePrivate(transport, "MarkTunaFallbackLaneState", fixture.Reactivation.SessionId,
            NknAccelerationLaneKind.Screen, Enum.Parse(laneStateType, "WaitingForRegularNkn"), "screen_pending");
        projection.GetType().GetProperty("ScreenFramesApplied")!.SetValue(projection, 2L);
        projection.GetType().GetProperty("ControlNknMessagesSent")!.SetValue(projection, 5L);
        var screenState = projection.GetType().GetProperty("ScreenState")!.GetValue(projection);
        var epoch = projection.GetType().GetProperty("Epoch")!.GetValue(projection);

        Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(new(fixture.Reactivation)).Status);
        Assert.True(IsFallbackProjectionActive(transport)); // A prepared transition cannot retire it.
        var command = CreateCommittedDisposition(fixture.Reactivation);
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(command).Status);
        Assert.False(IsFallbackProjectionActive(transport));
        Assert.Same(projection, GetPrivateFieldValue(transport, "tunaFallbackProofState"));
        Assert.Equal(screenState, projection.GetType().GetProperty("ScreenState")!.GetValue(projection));
        Assert.Equal(epoch, projection.GetType().GetProperty("Epoch")!.GetValue(projection));
        Assert.Equal(2L, projection.GetType().GetProperty("ScreenFramesApplied")!.GetValue(projection));
        Assert.Equal(5L, projection.GetType().GetProperty("ControlNknMessagesSent")!.GetValue(projection));
        Assert.False(transport.IsFileTunaActiveForRouteSelection); // Retirement is not Tuna readiness.

        transport.ObserveFileTransferV6TransportEpoch(fixture.Recovered);
        Assert.False(IsFallbackProjectionActive(transport));
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Idempotent,
            bridge.ApplyFileTransferRouteTransitionDisposition(command).Status);
        Assert.False(IsFallbackProjectionActive(transport));
        Assert.False((bool)InvokePrivate(transport, "StartTunaFallbackProofIfNeeded", "tuna_bulk_send_failed",
            fixture.Reactivation.SessionId, NknAccelerationLaneKind.File)!);
        Assert.False(IsFallbackProjectionActive(transport)); // A stale observation cannot reopen it.

        Assert.True((bool)InvokePrivate(transport, "StartTunaFallbackProofIfNeeded", "header_switch_off",
            fixture.Reactivation.SessionId, NknAccelerationLaneKind.File)!);
        InvokePrivate(transport, "MarkTunaFallbackLaneState", fixture.Reactivation.SessionId,
            NknAccelerationLaneKind.File, Enum.Parse(screenState!.GetType(), "Pending"), "new_stop");
        Assert.True(IsFallbackProjectionActive(transport));
        bridge.ApplyFileTransferRouteTransitionDisposition(command);
        Assert.True(IsFallbackProjectionActive(transport)); // Duplicate cannot clear the newer stop.
    }

    private static bool IsFallbackProjectionActive(NknSignalingTransport transport)
        => (bool)InvokePrivate(transport, "IsFileTransferUsingRegularNknFallbackForCurrentSession",
            "route-transition-session")!;

    [Theory]
    [InlineData("newer_stop")]
    [InlineData("newer_transport_epoch")]
    [InlineData("terminal_first")]
    [InlineData("rollback")]
    [InlineData("superseded")]
    [InlineData("unbound_source_publication")]
    [InlineData("source_terminal")]
    [InlineData("wrong_session")]
    [InlineData("wrong_transfer")]
    [InlineData("wrong_direction")]
    [InlineData("wrong_transition_generation")]
    [InlineData("missing_final_proof")]
    [InlineData("target_session")]
    [InlineData("target_transfer")]
    [InlineData("target_direction")]
    [InlineData("target_terminal")]
    [InlineData("target_leg")]
    [InlineData("target_generation")]
    [InlineData("target_protocol")]
    [InlineData("user_stop_without_new_epoch")]
    [InlineData("peer_stop_without_new_epoch")]
    [InlineData("missing_binding")]
    [InlineData("source_transport_epoch")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task FallbackProjection_RejectedOrStaleBoundaryHasNoRetirementEffect(string control)
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(FileTransferDirection.Outbound);
        var transport = fixture.Transport;
        var bridge = (IFileTransferRouteTransitionBridge)transport;
        var authority = fixture.Reactivation;
        if (control == "unbound_source_publication")
            authority = authority with { SourceRoutePublication = authority.SourceRoutePublication with { PublicationGeneration = 99 } };
        if (control == "source_terminal")
            authority = authority with { SourceTerminalGeneration = 1 };
        if (control == "source_transport_epoch")
            authority = authority with { SourceTransportEpoch = 8 };
        if (control == "missing_binding")
            GetPrivateFieldValue(transport, "tunaFallbackProofState")!.GetType()
                .GetProperty("FileProjectionPublication")!.SetValue(GetPrivateFieldValue(transport, "tunaFallbackProofState"), null);
        Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(new(authority)).Status);
        var command = CreateCommittedDisposition(authority);
        var projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        switch (control)
        {
            case "newer_stop":
                Assert.True((bool)InvokePrivate(transport, "StartTunaFallbackProofIfNeeded", "header_switch_off",
                    authority.SessionId, NknAccelerationLaneKind.File)!);
                var state = projection.GetType().GetProperty("FileState")!.PropertyType;
                InvokePrivate(transport, "MarkTunaFallbackLaneState", authority.SessionId,
                    NknAccelerationLaneKind.File, Enum.Parse(state, "Pending"), "new_stop");
                projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
                break;
            case "newer_transport_epoch":
                InvokePrivate(transport, "MarkTunaFallbackFileV6EpochState", authority.SessionId,
                    8L, V6TransportEpochState.Recovered, "newer_epoch");
                break;
            case "user_stop_without_new_epoch":
            case "peer_stop_without_new_epoch":
                typeof(NknSignalingTransport).GetField(control == "user_stop_without_new_epoch"
                    ? "accelerationUserStoppedSessionId" : "accelerationPeerUserStoppedSessionId",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(transport, authority.SessionId);
                break;
            case "terminal_first":
                bridge.ApplyFileTransferRouteTransitionDisposition(new(authority,
                    FileTransferRouteTransitionDispositionKind.TerminalInvalidated, null, null, "terminal"));
                break;
            case "rollback":
                command = new(authority, FileTransferRouteTransitionDispositionKind.RolledBack, null, null, "rollback");
                break;
            case "superseded":
                var successor = authority with
                {
                    AuthorityId = new FileTransferAuthorityId("fta3-newer-transition"),
                    TransitionGeneration = authority.TransitionGeneration + 1,
                    PredecessorTransitionAuthorityId = authority.AuthorityId,
                };
                Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
                    bridge.BindFileTransferRouteTransitionPause(new(successor) { PredecessorAuthority = authority }).Status);
                break;
            case "wrong_session": command = command with { Authority = authority with { SessionId = "other" } }; break;
            case "wrong_transfer": command = command with { Authority = authority with { TransferId = "other" } }; break;
            case "wrong_direction": command = command with { Authority = authority with { Direction = FileTransferDirection.Inbound } }; break;
            case "wrong_transition_generation": command = command with { Authority = authority with { TransitionGeneration = 99 } }; break;
            case "missing_final_proof": command = command with { FinalProof = null }; break;
            case "target_session": command = command with { RoutePublicationToken = command.RoutePublicationToken!.Value with { SessionId = "other" } }; break;
            case "target_transfer": command = command with { RoutePublicationToken = command.RoutePublicationToken!.Value with { TransferId = "other" } }; break;
            case "target_direction": command = command with { RoutePublicationToken = command.RoutePublicationToken!.Value with { Direction = FileTransferDirection.Inbound } }; break;
            case "target_terminal": command = command with { RoutePublicationToken = command.RoutePublicationToken!.Value with { TerminalGeneration = 1 } }; break;
            case "target_leg": command = command with { RoutePublicationToken = command.RoutePublicationToken!.Value with { LegGeneration = authority.SourceLegGeneration } }; break;
            case "target_generation": command = command with { RoutePublicationToken = command.RoutePublicationToken!.Value with { PublicationGeneration = 1 } }; break;
            case "target_protocol": command = command with { RoutePublicationToken = command.RoutePublicationToken!.Value with { ProtocolVersion = 6 } }; break;
        }

        bridge.ApplyFileTransferRouteTransitionDisposition(command);
        Assert.True(IsFallbackProjectionActive(transport));
        Assert.Same(projection, GetPrivateFieldValue(transport, "tunaFallbackProofState"));
        Assert.Null(projection.GetType().GetProperty("FileProjectionRetirement")!.GetValue(projection));
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task FallbackProjection_PublicationObservationAloneCannotRetire()
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(FileTransferDirection.Outbound);
        var publication = CreateCommittedDisposition(fixture.Reactivation).RoutePublicationToken!.Value;
        ((IFileTransferRouteHintObserver)fixture.Transport).ObserveFileTransferRouteHint(new(
            publication.SessionId, publication.TransferId, publication.RouteToken, publication.ProtocolVersion,
            "observation_only", PublicationAuthority: FileTransferCoordinator.CreateRoutePublicationAuthority(publication)));
        Assert.True(IsFallbackProjectionActive(fixture.Transport));
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task FallbackProjection_NewCommittedFallbackReopensRetiredFileProjection()
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(FileTransferDirection.Outbound);
        var bridge = (IFileTransferRouteTransitionBridge)fixture.Transport;
        bridge.BindFileTransferRouteTransitionPause(new(fixture.Reactivation));
        var tuna = CreateCommittedDisposition(fixture.Reactivation);
        bridge.ApplyFileTransferRouteTransitionDisposition(tuna);
        Assert.False(IsFallbackProjectionActive(fixture.Transport));
        await WaitUntilAsync(() => GetPendingRouteTransitionAvailabilityDisposition(fixture.Transport) is null);
        var source = tuna.RoutePublicationToken!.Value;
        var authority = fixture.FallbackEntry.Authority with
        {
            SourceRuntimeOwner = source.RuntimeOwner, SourceRoutePublication = source, SourceRouteCommit = tuna.RouteCommitToken,
            SourceLiveRouteEpoch = source.LiveRouteEpoch, SourceLegGeneration = (int)source.LegGeneration,
            AuthorityId = new FileTransferAuthorityId("fta3-new-committed-fallback"),
            TransitionGeneration = fixture.Reactivation.TransitionGeneration + 1,
            DurableCheckpoint = fixture.FallbackEntry.Authority.DurableCheckpoint with
            { SourceRuntimeOwner = source.RuntimeOwner, SourceRoutePublication = source, SourceLegGeneration = (int)source.LegGeneration },
            ProofBoundary = ((FileTransferFallbackTransitionProofBoundary)fixture.FallbackEntry.Authority.ProofBoundary) with
            { SourceRouteCommit = tuna.RouteCommitToken, TargetTransportEpoch = 8 },
        };
        var fallback = fixture.Reactivation.SourceRoutePublication;
        var owner = fallback.RuntimeOwner with
        { RuntimeGeneration = source.RuntimeGeneration + 1, LiveRouteEpoch = source.LiveRouteEpoch + 1 };
        fallback = fallback with
        {
            RuntimeOwner = owner, RuntimeGeneration = owner.RuntimeGeneration, LiveRouteEpoch = owner.LiveRouteEpoch,
            LegGeneration = source.LegGeneration + 1, PublicationGeneration = source.PublicationGeneration + 1,
        };
        Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(new(authority)).Status);
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(new(authority,
                FileTransferRouteTransitionDispositionKind.Committed, null, fallback, "new_fallback")).Status);
        Assert.True(IsFallbackProjectionActive(fixture.Transport));
        bridge.ApplyFileTransferRouteTransitionDisposition(tuna);
        Assert.True(IsFallbackProjectionActive(fixture.Transport));
    }

    private sealed record FallbackProjectionFixture(
        NknSignalingTransport Transport,
        IFileTransferDataSession DataSession,
        FileTransferRouteTransitionAuthority Reactivation,
        FileTransferRouteTransitionDispositionCommand FallbackEntry,
        FileTransferV6TransportEpochSnapshot Recovered) : IDisposable
    {
        public void Dispose() { DataSession.Dispose(); Transport.Dispose(); }
    }

    private static async Task<FallbackProjectionFixture> CreateFallbackProjectionFixtureAsync(FileTransferDirection direction)
    {
        var client = new FakeNknClient("fallback-projection.invalid");
        var transport = new NknSignalingTransport(client, NknTransportOptions.Load(),
            new NknIdentity("fallback-projection", client.Address), NknTunaAccelerationOptions.Disabled, null);
        var data = await transport.OpenFileTransferDataSessionAsync("route-transition-session",
            "route-transition-transfer", CancellationToken.None);
        var reactivation = CreateAuthorityForRoute("fallback-projection-reactivation", GetDataSessionGeneration(data),
            FileTransferRoute.PostTunaFallbackV6, direction);
        var entry = CreateFallbackSuccessor(CreateAuthorityForRoute("fallback-projection-entry",
            GetDataSessionGeneration(data), direction: direction));
        var priorTuna = CreateCommittedDisposition(CreateAuthorityForRoute("fallback-projection-prior-tuna",
            GetDataSessionGeneration(data), direction: direction));
        entry = entry with
        {
            Kind = FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6,
            SourceRuntimeOwner = priorTuna.RouteCommitToken!.Value.RuntimeOwner,
            SourceRoutePublication = priorTuna.RoutePublicationToken!.Value,
            SourceRouteCommit = priorTuna.RouteCommitToken,
            SourceRoute = FileTransferRoute.FileTunaV4,
            SourceTransport = FileTransferTransportKind.Tuna,
            SourceLiveRouteEpoch = 2,
            SourceLegGeneration = 2,
            SourceTransportEpoch = 0,
            DurableCheckpoint = entry.DurableCheckpoint with
            {
                SourceRuntimeOwner = priorTuna.RouteCommitToken.Value.RuntimeOwner,
                SourceRoutePublication = priorTuna.RoutePublicationToken.Value,
                SourceLegGeneration = 2,
                SourceTransportEpoch = 0,
            },
            PredecessorTransitionAuthorityId = null,
            ProofBoundary = ((FileTransferFallbackTransitionProofBoundary)entry.ProofBoundary) with
            { SourceRouteCommit = priorTuna.RouteCommitToken, PredecessorTransitionAuthorityId = null },
        };
        var bridge = (IFileTransferRouteTransitionBridge)transport;
        Assert.True((bool)InvokePrivate(transport, "StartTunaFallbackProofIfNeeded", "header_switch_off",
            reactivation.SessionId, NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen)!);
        var entryBind = bridge.BindFileTransferRouteTransitionPause(new(entry));
        Assert.True(entryBind.Status == FileTransferRouteTransitionPauseBindStatus.Installed, entryBind.Reason);
        var fallbackEntry = new FileTransferRouteTransitionDispositionCommand(entry,
            FileTransferRouteTransitionDispositionKind.Committed, null,
            reactivation.SourceRoutePublication, "test_fallback_commit");
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(fallbackEntry).Status);
        await WaitUntilAsync(() => GetPendingRouteTransitionAvailabilityDisposition(transport) is null);
        var recovered = new FileTransferV6TransportEpochSnapshot(reactivation.SessionId, reactivation.TransferId,
            direction, 7, FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.Tuna, FileTransferTransportKind.RegularNkn,
            V6TransportEpochState.Recovered, "test_fallback_recovered", false);
        transport.ObserveFileTransferV6TransportEpoch(recovered);
        return new(transport, data, reactivation with { TransitionGeneration = entry.TransitionGeneration + 1 }, fallbackEntry, recovered);
    }
}
