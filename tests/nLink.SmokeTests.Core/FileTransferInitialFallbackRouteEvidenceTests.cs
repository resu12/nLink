using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferInitialFallbackRouteEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(FileTransferDirection.Inbound, false, 1)]
    [InlineData(FileTransferDirection.Outbound, false, 1)]
    [InlineData(FileTransferDirection.Inbound, true, 1)]
    [InlineData(FileTransferDirection.Outbound, true, 1)]
    [InlineData(FileTransferDirection.Inbound, false, 2)]
    [InlineData(FileTransferDirection.Outbound, false, 2)]
    public async Task InitialFallbackRoute_ExactOwnerProofPreservesPublicationGuards(
        FileTransferDirection direction, bool independentGenerations, int publicationCount)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var envelopes = FileTransferCoordinatorTests.CreateInitialFallbackPublicationSnapshotFixture(direction, independentGenerations, publicationCount);
        var root = Path.Combine(FindRepoRoot(), "artifacts", "initial-fallback-owner-implementation-20260923",
            $"{direction}-{independentGenerations}-{publicationCount}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "actual-emitter.log");
        // Fresh Core decisions and the production emitter; forwarding is modeled,
        // not a claim of a service callback, peer delivery, or live acceptance.
        await File.WriteAllLinesAsync(path, FileTransferInitialRegularRouteEvidenceTests.Emit(envelopes));
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
            "Support", "InitialFallbackRouteSnapshotEvidence.ps1"));
        string? firstOutput = null;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path, publicationCount.ToString()], host);
            await File.WriteAllTextAsync(Path.Combine(root, host + ".txt"), result.Stdout + Environment.NewLine + result.Stderr);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("initial-fallback-owner-contract-ok", result.Stdout, StringComparison.Ordinal);
            var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith('{')));
            if (firstOutput is not null) { Assert.Equal(firstOutput, normalized); }
            firstOutput = normalized;
        }
    }
}

public sealed partial class FileTransferCoordinatorTests
{
    internal static FileTransferCoordinatorTransitionEnvelope[] CreateInitialFallbackPublicationSnapshotFixture(
        FileTransferDirection direction, bool independentGenerations, int publicationCount, bool startup = false, bool correlated = false)
    {
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var empty = EmptyState(fallback, direction) with
        {
            PersistentState = new() { CoordinatorInstanceId = "initial-fallback-" + direction },
            LastRuntimeOwnerGeneration = independentGenerations ? 6 : 0,
            LastTransferLegGeneration = independentGenerations ? 12 : 0,
        };
        var issuer = direction == FileTransferDirection.Outbound
            ? FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(empty)
            : FileTransferBootstrapCorrelation.CreateIssuer("initial-fallback-Outbound", empty.SessionId,
                empty.TransferId, FileTransferDirection.Outbound);
        var pinned = FileTransferCoordinator.PinFallbackBootstrapOffer(empty, new FileTransferOfferV2
        {
            SessionId = empty.SessionId, TransferId = empty.TransferId, FileName = "file.bin", FileSizeBytes = 4,
            PreferredDataProtocolVersion = 6, FileTransferRoute = fallback.TelemetryToken,
            FallbackBootstrapCorrelationVersion = 1, FallbackBootstrapIssuerId = issuer,
        });
        Assert.True(pinned.Accepted, pinned.Reason);
        var started = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer,
            fallback, targetTransport: FileTransferTransportKind.RegularNkn), pinned.State);
        var owner = Assert.IsType<RuntimeOwnerToken>(started.State.CurrentRuntimeOwner);
        var leg = Assert.IsType<FileTransferLeg>(started.State.CurrentLeg);
        Assert.Equal(independentGenerations ? 7 : 1, owner.RuntimeGeneration);
        Assert.Equal(independentGenerations ? 13 : 1, leg.Generation);
        Assert.Equal(0, owner.LiveRouteEpoch);
        Assert.Null(started.State.CurrentRoutePublicationToken);
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope> { pinned.SnapshotEnvelope!, started.SnapshotEnvelope! };
        var state = started.State;
        for (var index = 0; index < publicationCount; index++)
        {
            var publication = FileTransferCoordinator.AuthorizeAction(state,
                new(FileTransferCoordinatorActionKind.RoutePublication, Source: "initial_fallback_evidence",
                    Reason: "initial_publication", RuntimeOwner: owner, Route: fallback.Route, ProtocolVersion: 6,
                    LiveRouteEpoch: 0, LegGeneration: leg.Generation, RuntimeGeneration: owner.RuntimeGeneration,
                    TerminalGeneration: 0));
            Assert.True(publication.Accepted, publication.RejectionReason);
            var token = Assert.IsType<RoutePublicationToken>(publication.RoutePublicationToken);
            Assert.Equal(index + 1, token.PublicationGeneration);
            Assert.Null(token.RouteCommitGeneration);
            Assert.Equal(owner, publication.State.CurrentRuntimeOwner);
            Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(publication.State, token, out _));
            envelopes.Add(publication.SnapshotEnvelope!);
            state = publication.State;
        }
        if (startup)
        {
            Assert.Equal(1, publicationCount);
            if (correlated)
            {
                var receiverIssuer = direction == FileTransferDirection.Inbound
                    ? FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(state)
                    : FileTransferBootstrapCorrelation.CreateIssuer("initial-fallback-Inbound", state.SessionId,
                        state.TransferId, FileTransferDirection.Inbound);
                var accepted = FileTransferCoordinator.ApplyFallbackBootstrapAccept(state, new()
                {
                    SessionId = state.SessionId, TransferId = state.TransferId, AcceptedDataProtocolVersion = 6,
                    FileTransferRoute = fallback.TelemetryToken, FallbackBootstrapCorrelationVersion = 1,
                    FallbackBootstrapIssuerId = receiverIssuer,
                });
                Assert.True(accepted.Accepted, accepted.Reason);
                envelopes.Add(accepted.SnapshotEnvelope!);
                var opened = FileTransferCoordinator.ApplyFallbackBootstrapSessionOpen(accepted.State, new()
                {
                    SessionId = state.SessionId, TransferId = state.TransferId, ProtocolVersion = 6,
                    FileTransferRoute = fallback.TelemetryToken, SessionRole = FileTransferProtocol.SessionRoleSender,
                    ChunkSizeBytes = 21 * 1024, InitialPipelineDepth = 8,
                    FallbackBootstrapCorrelationVersion = 1, FallbackBootstrapIssuerId = issuer,
                });
                Assert.True(opened.Accepted, opened.Reason);
                envelopes.Add(opened.SnapshotEnvelope!);
                state = opened.State;
            }
            var binding = FileTransferCoordinator.BindInitialFallbackDataSession(state,
                InitialFallbackDataSessionRequestForTest(state));
            Assert.NotNull(binding.Binding);
            envelopes.Add(binding.SnapshotEnvelope!);
            var successor = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer,
                fallback, targetTransport: FileTransferTransportKind.RegularNkn, reason: "post_tuna_fallback_v6_route",
                state: FileTransferLegState.CheckpointPending, canSendData: false), binding.State);
            Assert.Equal(owner.RuntimeGeneration + 1, successor.State.CurrentRuntimeOwner!.Value.RuntimeGeneration);
            Assert.Equal(leg.Generation + 1, successor.State.CurrentLeg!.Generation);
            Assert.False(successor.State.CurrentLeg.CanSendData);
            Assert.Equal(2, successor.State.CurrentRoutePublicationToken!.Value.PublicationGeneration);
            envelopes.Add(successor.SnapshotEnvelope!);
        }
        return envelopes.ToArray();
    }
}
