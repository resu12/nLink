using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class FileTransferRouteTransitionInfraTests
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task TerminalFallbackProjection_ExactCommandRetiresOnlyFileProjection(FileTransferDirection direction)
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(direction);
        var transport = fixture.Transport;
        var bridge = (IFileTransferRouteTransitionBridge)transport;
        var projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        projection.GetType().GetProperty("ScreenFramesApplied")!.SetValue(projection, 2L);
        projection.GetType().GetProperty("ControlNknMessagesSent")!.SetValue(projection, 5L);
        var screen = projection.GetType().GetProperty("ScreenState")!.GetValue(projection);
        var pending = GetPrivateFieldValue(transport, "fileTransferFallbackProofPending");
        var generation = GetPrivateFieldValue(transport, "fileTransferFallbackProofGeneration");
        var command = TerminalProjectionCommand(fixture);

        Assert.True(bridge.ApplyTerminalFallbackProjectionRetirement(command));
        Assert.False(IsFallbackProjectionActive(transport));
        Assert.False(transport.IsFileTunaActiveForRouteSelection);
        Assert.Same(projection, GetPrivateFieldValue(transport, "tunaFallbackProofState"));
        Assert.Equal(screen, projection.GetType().GetProperty("ScreenState")!.GetValue(projection));
        Assert.Equal(2L, projection.GetType().GetProperty("ScreenFramesApplied")!.GetValue(projection));
        Assert.Equal(5L, projection.GetType().GetProperty("ControlNknMessagesSent")!.GetValue(projection));
        Assert.Equal(pending, GetPrivateFieldValue(transport, "fileTransferFallbackProofPending"));
        Assert.Equal(generation, GetPrivateFieldValue(transport, "fileTransferFallbackProofGeneration"));
        Assert.False(bridge.ApplyTerminalFallbackProjectionRetirement(command));
        transport.ObserveFileTransferV6TransportEpoch(fixture.Recovered);
        Assert.False(IsFallbackProjectionActive(transport));

        Assert.True((bool)InvokePrivate(transport, "StartTunaFallbackProofIfNeeded", "header_switch_off",
            command.Publication.SessionId, NknAccelerationLaneKind.File)!);
        var replacement = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        var stateType = replacement.GetType().GetProperty("FileState")!.PropertyType;
        InvokePrivate(transport, "MarkTunaFallbackLaneState", command.Publication.SessionId,
            NknAccelerationLaneKind.File, Enum.Parse(stateType, "Pending"), "new_stop");
        Assert.True(IsFallbackProjectionActive(transport));
        Assert.False(bridge.ApplyTerminalFallbackProjectionRetirement(command));
        Assert.True(IsFallbackProjectionActive(transport));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("transfer")]
    [InlineData("direction")]
    [InlineData("protocol")]
    [InlineData("publication")]
    [InlineData("source_terminal")]
    [InlineData("terminal_generation")]
    [InlineData("transport_epoch")]
    [InlineData("data_session_generation")]
    [InlineData("replaced_data_session")]
    [InlineData("newer_publication")]
    [InlineData("pending_proof")]
    [InlineData("active_transition")]
    [InlineData("newer_stop")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task TerminalFallbackProjection_StaleCommandHasNoEffects(string control)
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(FileTransferDirection.Outbound);
        var transport = fixture.Transport;
        var bridge = (IFileTransferRouteTransitionBridge)transport;
        var command = TerminalProjectionCommand(fixture);
        var publication = command.Publication;
        var projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        IFileTransferDataSession? replacement = null;
        switch (control)
        {
            case "session": command = command with { Publication = publication with { SessionId = "other" } }; break;
            case "transfer": command = command with { Publication = publication with { TransferId = "other" } }; break;
            case "direction": command = command with { Publication = publication with { Direction = FileTransferDirection.Inbound } }; break;
            case "protocol": command = command with { Publication = publication with { ProtocolVersion = 4 } }; break;
            case "publication": command = command with { Publication = publication with { PublicationGeneration = 99 } }; break;
            case "source_terminal": command = command with { Publication = publication with { TerminalGeneration = 1 } }; break;
            case "terminal_generation": command = command with { TerminalGeneration = 2 }; break;
            case "transport_epoch": command = command with { TransportEpoch = 99 }; break;
            case "data_session_generation": command = command with { DataSessionGeneration = 99 }; break;
            case "replaced_data_session":
                fixture.DataSession.Dispose();
                replacement = await transport.OpenFileTransferDataSessionAsync(publication.SessionId, publication.TransferId, CancellationToken.None);
                break;
            case "newer_publication":
                projection.GetType().GetProperty("FileProjectionPublication")!.SetValue(projection, publication with { PublicationGeneration = 99 });
                break;
            case "pending_proof":
                InvokePrivate(transport, "MarkFileTransferFallbackNknProofPending", "header_switch_off",
                    publication.SessionId, NknAccelerationLaneKind.File, null);
                break;
            case "active_transition":
                Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
                    bridge.BindFileTransferRouteTransitionPause(new(fixture.Reactivation)).Status);
                break;
            case "newer_stop":
                Assert.True((bool)InvokePrivate(transport, "StartTunaFallbackProofIfNeeded", "header_switch_off",
                    publication.SessionId, NknAccelerationLaneKind.File)!);
                break;
        }
        using (replacement)
        {
            projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
            var active = IsFallbackProjectionActive(transport);
            var state = projection.GetType().GetProperty("FileState")!.GetValue(projection);
            var pending = GetPrivateFieldValue(transport, "fileTransferFallbackProofPending");
            var generation = GetPrivateFieldValue(transport, "fileTransferFallbackProofGeneration");
            Assert.False(bridge.ApplyTerminalFallbackProjectionRetirement(command));
            Assert.Equal(active, IsFallbackProjectionActive(transport));
            Assert.Same(projection, GetPrivateFieldValue(transport, "tunaFallbackProofState"));
            Assert.Equal(state, projection.GetType().GetProperty("FileState")!.GetValue(projection));
            Assert.Null(projection.GetType().GetProperty("FileTerminalProjectionRetirement")!.GetValue(projection));
            Assert.Equal(pending, GetPrivateFieldValue(transport, "fileTransferFallbackProofPending"));
            Assert.Equal(generation, GetPrivateFieldValue(transport, "fileTransferFallbackProofGeneration"));
        }
    }

    [Theory]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [InlineData(FileTransferDirection.Inbound, true)]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task TerminalFallbackProjection_OnlyAnotherTransferCanReopen(FileTransferDirection direction, bool anotherTransfer)
    {
        using var fixture = await CreateFallbackProjectionFixtureAsync(direction, commitFallback: false);
        var transport = fixture.Transport;
        var bridge = (IFileTransferRouteTransitionBridge)transport;
        var projection = GetPrivateFieldValue(transport, "tunaFallbackProofState")!;
        var retiring = TerminalProjectionCommand(fixture);
        IFileTransferDataSession? previousSession = null;
        if (anotherTransfer)
        {
            const string previousTransferId = "previous-transfer";
            previousSession = await transport.OpenFileTransferDataSessionAsync(
                retiring.Publication.SessionId, previousTransferId, CancellationToken.None);
            retiring = retiring with
            {
                Publication = retiring.Publication with
                {
                    TransferId = previousTransferId,
                    RuntimeOwner = retiring.Publication.RuntimeOwner with { TransferId = previousTransferId },
                },
                DataSessionGeneration = GetDataSessionGeneration(previousSession),
            };
        }
        using (previousSession)
        {
            // Seed the exact predecessor projection; the successor still must
            // bind and apply its own committed coordinator disposition below.
            projection.GetType().GetProperty("FileProjectionPublication")!.SetValue(projection, retiring.Publication);
            projection.GetType().GetProperty("FileProjectionDataSessionGeneration")!.SetValue(projection, retiring.DataSessionGeneration);
            transport.ObserveFileTransferV6TransportEpoch(fixture.Recovered);
            var laneState = projection.GetType().GetProperty("FileState")!.PropertyType;
            InvokePrivate(transport, "MarkTunaFallbackLaneState", retiring.Publication.SessionId,
                NknAccelerationLaneKind.File, Enum.Parse(laneState, "Recovered"), "test_predecessor_recovered");
            Assert.True(bridge.ApplyTerminalFallbackProjectionRetirement(retiring));
            Assert.False(IsFallbackProjectionActive(transport));
            Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
                bridge.BindFileTransferRouteTransitionPause(new(fixture.FallbackEntry.Authority)).Status);
            Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied,
                bridge.ApplyFileTransferRouteTransitionDisposition(fixture.FallbackEntry).Status);
            await WaitUntilAsync(() => GetPendingRouteTransitionAvailabilityDisposition(transport) is null);
            Assert.Equal(anotherTransfer, IsFallbackProjectionActive(transport));
            Assert.False(bridge.ApplyTerminalFallbackProjectionRetirement(retiring));
            Assert.Equal(anotherTransfer, IsFallbackProjectionActive(transport));
        }
    }

    private static FileTransferTerminalFallbackProjectionRetirementCommand TerminalProjectionCommand(FallbackProjectionFixture fixture)
        => new(fixture.FallbackEntry.RoutePublicationToken!.Value, 1,
            fixture.Reactivation.SourceTransportEpoch, fixture.Reactivation.DataSessionGeneration);
}

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData("regular")]
    [InlineData("missing_publication")]
    [InlineData("missing_data_session")]
    [InlineData("stale_publication")]
    public void TerminalFallbackProjection_CoordinatorDoesNotIssueForUnownedProjection(string control)
    {
        var state = StartRegular(FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast)).State;
        if (control != "regular")
            state = FallBack(state, FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6),
                committedChunk: 8, transportEpoch: 4).State with { CurrentDataSessionGeneration = 1 };
        state = control switch
        {
            "missing_publication" => state with { CurrentRoutePublicationToken = null },
            "missing_data_session" => state with { CurrentDataSessionGeneration = 0 },
            "stale_publication" => state with { CurrentRoutePublicationToken = state.CurrentRoutePublicationToken!.Value with { TerminalGeneration = 99 } },
            _ => state,
        };
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, state.RouteSelection, reason: "completed"), state);
        Assert.Null(terminal.MutationRejectedReason);
        Assert.Null(terminal.TerminalFallbackProjectionRetirement);
    }

    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    public void TerminalFallbackProjection_CoordinatorIssuesExactCommandOnce(FileTransferDirection direction)
    {
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var started = StartRegular(FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast), direction).State;
        if (direction == FileTransferDirection.Inbound)
            started = ActivateInitialResponderTunaForFallbackFixture(started).State;
        var state = FallBack(started,
            fallback, committedChunk: 8, transportEpoch: 4).State with { CurrentDataSessionGeneration = 1 };
        var publication = Assert.IsType<RoutePublicationToken>(state.CurrentRoutePublicationToken);
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, fallback, reason: "completed"), state);
        Assert.Null(terminal.MutationRejectedReason);
        var command = Assert.IsType<FileTransferTerminalFallbackProjectionRetirementCommand>(terminal.TerminalFallbackProjectionRetirement);
        Assert.Equal(publication, command.Publication);
        Assert.Equal(terminal.State.TerminalGeneration, command.TerminalGeneration);
        Assert.Equal(4, command.TransportEpoch);
        Assert.Equal(1, command.DataSessionGeneration);
        Assert.Null(terminal.State.CurrentRoutePublicationToken);
        var duplicate = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, fallback, reason: "completed"), terminal.State);
        Assert.Null(duplicate.TerminalFallbackProjectionRetirement);
    }
}
