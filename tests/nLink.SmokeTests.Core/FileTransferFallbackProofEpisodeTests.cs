using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class FileTransferRouteTransitionInfraTests
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound, 1)]
    [InlineData(FileTransferDirection.Inbound, 1)]
    [InlineData(FileTransferDirection.Outbound, 2)]
    [InlineData(FileTransferDirection.Inbound, 2)]
    [Trait("FileTransferInfraSuite", "FallbackProofEpisode")]
    public async Task FallbackProofEpisode_CommittedDispositionRetiresCapturedLocalGeneration(
        FileTransferDirection direction, int episode)
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(direction, commitFallback: false);
        var transport = fixture.Transport;
        var command = PreparePendingProofEpisode(fixture, episode);
        var originalProjection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        originalProjection.GetType().GetProperty("ScreenFramesApplied")!.SetValue(originalProjection, 2L);
        originalProjection.GetType().GetProperty("ControlNknMessagesSent")!.SetValue(originalProjection, 5L);
        var screenState = originalProjection.GetType().GetProperty("ScreenState")!.GetValue(originalProjection);
        var bridge = (IFileTransferRouteTransitionBridge)transport;
        Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(new(command.Authority)).Status);
        var generation = (long)GetPrivateFieldValue(transport, "fileTransferFallbackProofGeneration")!;
        Assert.Equal(episode * 2 - 1, generation); // Characterize independent local counter; never an authority mapping.

        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(command).Status);
        Assert.Equal(false, GetPrivateFieldValue(transport, "fileTransferFallbackProofPending"));
        Assert.Equal(generation + 1, GetPrivateFieldValue(transport, "fileTransferFallbackProofGeneration"));
        var projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        Assert.Equal("Recovered", projection.GetType().GetProperty("FileState")!.GetValue(projection)!.ToString());
        Assert.Same(originalProjection, projection);
        Assert.Equal(screenState, projection.GetType().GetProperty("ScreenState")!.GetValue(projection));
        Assert.Equal(2L, projection.GetType().GetProperty("ScreenFramesApplied")!.GetValue(projection));
        Assert.Equal(5L, projection.GetType().GetProperty("ControlNknMessagesSent")!.GetValue(projection));
        Assert.True(IsFallbackProjectionActive(transport)); // Proof cleanup is not terminal projection retirement.
        Assert.False(transport.IsFileTunaActiveForRouteSelection);
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Idempotent,
            bridge.ApplyFileTransferRouteTransitionDisposition(command).Status);
        Assert.Equal(generation + 1, GetPrivateFieldValue(transport, "fileTransferFallbackProofGeneration"));

        Assert.True((bool)InvokePrivate(transport, "StartTunaFallbackProofIfNeeded", "header_switch_off",
            command.Authority.SessionId, NknAccelerationLaneKind.File)!);
        InvokePrivate(transport, "MarkFileTransferFallbackNknProofPending", "header_switch_off",
            command.Authority.SessionId, NknAccelerationLaneKind.File, null);
        var successor = GetPrivateFieldValue(transport, "tunaFallbackProofState");
        var successorGeneration = GetPrivateFieldValue(transport, "fileTransferFallbackProofGeneration");
        bridge.ApplyFileTransferRouteTransitionDisposition(command);
        Assert.Equal(true, GetPrivateFieldValue(transport, "fileTransferFallbackProofPending"));
        Assert.Equal(successorGeneration, GetPrivateFieldValue(transport, "fileTransferFallbackProofGeneration"));
        Assert.Same(successor, GetPrivateFieldValue(transport, "tunaFallbackProofState"));
    }

    [Theory]
    [InlineData("missing_pending")]
    [InlineData("wrong_session")]
    [InlineData("wrong_transfer")]
    [InlineData("wrong_direction")]
    [InlineData("wrong_transport_epoch")]
    [InlineData("newer_pending")]
    [InlineData("newer_projection")]
    [InlineData("missing_binding")]
    [InlineData("terminal_first")]
    [InlineData("rollback")]
    [InlineData("superseded")]
    [InlineData("replaced_data_session")]
    [InlineData("wrong_publication")]
    [Trait("FileTransferInfraSuite", "FallbackProofEpisode")]
    public async Task FallbackProofEpisode_StaleOrUnboundDispositionCannotCleanup(string control)
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(FileTransferDirection.Outbound, commitFallback: false);
        var transport = fixture.Transport;
        var command = PreparePendingProofEpisode(fixture, 2);
        var bridge = (IFileTransferRouteTransitionBridge)transport;
        if (control == "missing_pending") InvokePrivate(transport, "CompleteFileTransferFallbackProofUnsafe");
        Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(new(command.Authority)).Status);
        switch (control)
        {
            case "wrong_session": SetPendingField("fileTransferFallbackProofSessionId", "wrong-session"); break;
            case "wrong_transfer": SetPendingField("fileTransferFallbackProofTransferId", "wrong-transfer"); break;
            case "wrong_direction": SetPendingField("fileTransferFallbackProofDirection", FileTransferDirection.Inbound); break;
            case "wrong_transport_epoch": SetPendingField("fileTransferFallbackProofTransportEpoch", 99L); break;
            case "newer_pending":
                InvokePrivate(transport, "MarkFileTransferFallbackNknProofPending", "header_switch_off",
                    command.Authority.SessionId, NknAccelerationLaneKind.File, null);
                break;
            case "newer_projection":
                var old = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
                old.GetType().GetProperty("AccelerationUsedAfterFallback")!.SetValue(old, true);
                Assert.True((bool)InvokePrivate(transport, "StartTunaFallbackProofIfNeeded", "header_switch_off",
                    command.Authority.SessionId, NknAccelerationLaneKind.File)!);
                break;
            case "missing_binding":
                var shadow = GetPrivateFieldValue(transport, "fileTransferRouteTransitionExecutionShadow")!;
                var binding = shadow.GetType().GetProperty("Current")!.GetValue(shadow)!;
                binding.GetType().GetProperty("FallbackProofEpisode")!.SetValue(binding, null);
                break;
            case "terminal_first":
                bridge.ApplyFileTransferRouteTransitionDisposition(new(command.Authority,
                    FileTransferRouteTransitionDispositionKind.TerminalInvalidated, null, null, "terminal"));
                break;
            case "rollback":
                command = new(command.Authority, FileTransferRouteTransitionDispositionKind.RolledBack, null, null, "rollback");
                break;
            case "superseded":
                var successor = command.Authority with
                {
                    AuthorityId = new FileTransferAuthorityId("fta-new-pending-fallback"),
                    TransitionGeneration = command.Authority.TransitionGeneration + 1,
                    PredecessorTransitionAuthorityId = command.Authority.AuthorityId,
                };
                Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
                    bridge.BindFileTransferRouteTransitionPause(new(successor) { PredecessorAuthority = command.Authority }).Status);
                break;
            case "replaced_data_session": fixture.DataSession.Dispose(); break;
            case "wrong_publication":
                command = command with { RoutePublicationToken = command.RoutePublicationToken!.Value with { TransferId = "wrong" } };
                break;
        }
        var generation = GetPrivateFieldValue(transport, "fileTransferFallbackProofGeneration");
        var pending = GetPrivateFieldValue(transport, "fileTransferFallbackProofPending");
        var projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        var fileState = projection.GetType().GetProperty("FileState")!.GetValue(projection);
        bridge.ApplyFileTransferRouteTransitionDisposition(command);
        Assert.Equal(generation, GetPrivateFieldValue(transport, "fileTransferFallbackProofGeneration"));
        Assert.Equal(pending, GetPrivateFieldValue(transport, "fileTransferFallbackProofPending"));
        Assert.Same(projection, GetPrivateFieldValue(transport, "tunaFallbackProofState"));
        Assert.Equal(fileState, projection.GetType().GetProperty("FileState")!.GetValue(projection));

        void SetPendingField(string name, object value)
            => typeof(NknSignalingTransport).GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(transport, value);
    }

    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferInfraSuite", "FallbackProofEpisode")]
    public async Task FallbackProofEpisode_SessionDisposedWhileCleanupWaitsCannotRetireProof(FileTransferDirection direction)
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(direction, commitFallback: false);
        var transport = fixture.Transport;
        var command = PreparePendingProofEpisode(fixture, 2);
        var bridge = (IFileTransferRouteTransitionBridge)transport;
        Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(new(command.Authority)).Status);
        var shadow = GetPrivateFieldValue(transport, "fileTransferRouteTransitionExecutionShadow")!;
        var binding = shadow.GetType().GetProperty("Current")!.GetValue(shadow)!;
        var episodeProperty = binding.GetType().GetProperty("FallbackProofEpisode")!;
        var episode = episodeProperty.GetValue(binding);
        // Retain the real applied disposition while deferring only its cleanup.
        episodeProperty.SetValue(binding, null);
        try
        {
            Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied,
                bridge.ApplyFileTransferRouteTransitionDisposition(command).Status);
        }
        finally { episodeProperty.SetValue(binding, episode); }
        await WaitUntilAsync(() => GetPendingRouteTransitionAvailabilityDisposition(transport) is null);

        var generation = GetPrivateFieldValue(transport, "fileTransferFallbackProofGeneration");
        var projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        var fileState = projection.GetType().GetProperty("FileState")!.GetValue(projection);
        Assert.Equal(true, GetPrivateFieldValue(transport, "fileTransferFallbackProofPending"));
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                InvokePrivate(transport, "CompleteCommittedPostTunaFallbackFileTransition", command, binding);
                completed.TrySetResult();
            }
            catch (Exception error) { completed.TrySetException(error); }
        }) { IsBackground = true };
        Task dispose;
        lock (GetPrivateFieldValue(transport, "accelerationGate")!)
        {
            worker.Start();
            Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)), "Cleanup did not reach the held projection lock.");
            dispose = Task.Run(fixture.DataSession.Dispose);
            Assert.True(SpinWait.SpinUntil(() => (bool)fixture.DataSession.GetType().GetProperty("IsDisposed")!.GetValue(fixture.DataSession)!,
                TimeSpan.FromSeconds(5)), "Data session did not retire while cleanup was blocked.");
        }
        await Task.WhenAll(completed.Task, dispose).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(generation, GetPrivateFieldValue(transport, "fileTransferFallbackProofGeneration"));
        Assert.Equal(true, GetPrivateFieldValue(transport, "fileTransferFallbackProofPending"));
        Assert.Same(projection, GetPrivateFieldValue(transport, "tunaFallbackProofState"));
        Assert.Equal(fileState, projection.GetType().GetProperty("FileState")!.GetValue(projection));
    }

    [Theory]
    [InlineData("pending_generation")]
    [InlineData("fallback_epoch")]
    [InlineData("source_publication")]
    [InlineData("direction")]
    [InlineData("publication_runtime")]
    [InlineData("publication_terminal")]
    [InlineData("publication_generation")]
    [InlineData("peer_epoch")]
    [Trait("FileTransferInfraSuite", "FallbackProofEpisode")]
    public async Task FallbackProofEpisode_CoordinatorRejectsMismatchedCleanupScope(string control)
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(FileTransferDirection.Outbound, commitFallback: false);
        var command = PreparePendingProofEpisode(fixture, 2);
        var episode = new FileTransferFallbackProofEpisode(command.Authority, 3, 1);
        Assert.True(FileTransferCoordinator.AuthorizesFallbackProofEpisodeCleanup(command, episode));
        switch (control)
        {
            case "pending_generation": episode = episode with { PendingGeneration = 0 }; break;
            case "fallback_epoch": episode = episode with { FallbackEpoch = 0 }; break;
            case "source_publication":
                episode = episode with { Transition = episode.Transition with
                { SourceRoutePublication = episode.Transition.SourceRoutePublication with { PublicationGeneration = 99 } } };
                break;
            case "direction": episode = episode with { Transition = episode.Transition with { Direction = FileTransferDirection.Inbound } }; break;
            case "publication_runtime":
                command = command with { RoutePublicationToken = command.RoutePublicationToken!.Value with { RuntimeGeneration = 99 } }; break;
            case "publication_terminal":
                command = command with { RoutePublicationToken = command.RoutePublicationToken!.Value with { TerminalGeneration = 1 } }; break;
            case "publication_generation":
                command = command with { RoutePublicationToken = command.RoutePublicationToken!.Value with { PublicationGeneration = 1 } }; break;
            case "peer_epoch":
                var boundary = (FileTransferFallbackTransitionProofBoundary)command.Authority.ProofBoundary;
                command = command with { Authority = command.Authority with { ProofBoundary = boundary with
                { PeerProofEvidence = boundary.PeerProofEvidence with { TransportEpoch = 99 } } } };
                episode = episode with { Transition = command.Authority };
                break;
        }
        Assert.False(FileTransferCoordinator.AuthorizesFallbackProofEpisodeCleanup(command, episode));
    }

    private static FileTransferRouteTransitionDispositionCommand PreparePendingProofEpisode(
        FallbackProjectionFixture fixture, int episode)
    {
        var transport = fixture.Transport;
        for (var index = 0; index < episode; index++)
        {
            if (index != 0) InvokePrivate(transport, "CompleteFileTransferFallbackProofUnsafe");
            InvokePrivate(transport, "MarkFileTransferFallbackNknProofPending", "header_switch_off",
                fixture.FallbackEntry.Authority.SessionId, NknAccelerationLaneKind.File, null);
        }
        var projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        var stateType = projection.GetType().GetProperty("FileState")!.PropertyType;
        InvokePrivate(transport, "MarkTunaFallbackLaneState", fixture.FallbackEntry.Authority.SessionId,
            NknAccelerationLaneKind.File, Enum.Parse(stateType, "Pending"), "test_pending");
        var boundary = (FileTransferFallbackTransitionProofBoundary)fixture.FallbackEntry.Authority.ProofBoundary;
        return fixture.FallbackEntry with
        {
            Authority = fixture.FallbackEntry.Authority with
            {
                ProofBoundary = boundary with
                {
                    PeerProofEvidence = boundary.PeerProofEvidence with { ProofGeneration = episode },
                },
            },
        };
    }
}
