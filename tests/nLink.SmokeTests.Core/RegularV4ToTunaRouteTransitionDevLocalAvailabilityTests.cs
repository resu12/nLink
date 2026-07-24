using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Infra.DevLocal;

namespace NLink.SmokeTests;

public sealed class FileTransferRouteTransitionDevLocalAvailabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task PauseBindingRejectsMissingOrStaleExactDataSession(
        bool openStaleSession)
    {
        using var transport = new DevLocalTransport();
        using var dataSession = openStaleSession
            ? await transport.OpenFileTransferDataSessionAsync(
                "route-transition-session",
                "route-transition-transfer",
                CancellationToken.None)
            : null;
        var dataSessionGeneration = dataSession is null
            ? 1
            : GetGeneration(dataSession) + 1;
        var bridge =
            Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(
                transport);

        var receipt = bridge.BindFileTransferRouteTransitionPause(
            new FileTransferRouteTransitionPauseBindCommand(
                CreateAuthority(dataSessionGeneration)));

        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Rejected,
            receipt.Status);
        Assert.Equal(
            "filetransfer_route_transition_data_session_not_current",
            receipt.Reason);
        var transitionShadow = typeof(DevLocalTransport)
            .GetField(
                "fileTransferRouteTransitionExecutionShadow",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(transport);
        Assert.NotNull(transitionShadow);
        Assert.Null(
            transitionShadow!.GetType()
                .GetProperty("Current")!
                .GetValue(transitionShadow));
        if (dataSession is not null)
        {
            Assert.True(dataSession.IsAvailable);
        }
    }

    [Theory]
    [InlineData("Committed", true, FileTransferTransportKind.Tuna)]
    [InlineData("RolledBack", true, FileTransferTransportKind.RegularNkn)]
    [InlineData("TerminalInvalidated", false, FileTransferTransportKind.Unknown)]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task ExactDispositionControlsDevLocalDataSessionAvailability(
        string dispositionKind,
        bool releasesAvailability,
        FileTransferTransportKind expectedTransport)
    {
        using var transport = new DevLocalTransport();
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(
            "route-transition-session",
            "route-transition-transfer",
            CancellationToken.None);
        var authority = CreateAuthority(GetGeneration(dataSession));
        var bridge =
            Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(
                transport);
        var availability = new ConcurrentQueue<
            FileTransferDataSessionAvailabilityChangedEventArgs>();
        dataSession.AvailabilityChanged += (_, args) => availability.Enqueue(args);

        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(
                new FileTransferRouteTransitionPauseBindCommand(authority)).Status);
        Assert.False(dataSession.IsAvailable);
        Assert.Empty(availability);

        var kind =
            Enum.Parse<FileTransferRouteTransitionDispositionKind>(
                dispositionKind);
        var disposition = kind ==
                          FileTransferRouteTransitionDispositionKind.Committed
            ? CreateCommittedDisposition(authority)
            : new FileTransferRouteTransitionDispositionCommand(
                authority,
                kind,
                RouteCommitToken: null,
                RoutePublicationToken: null,
                Reason: "test_devlocal_disposition");
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(disposition)
                .Status);

        if (!releasesAvailability)
        {
            await Task.Delay(50);
            Assert.False(dataSession.IsAvailable);
            Assert.Empty(availability);
            return;
        }

        var observed = await WaitForAvailabilityAsync(availability);
        Assert.True(dataSession.IsAvailable);
        Assert.True(observed.IsAvailable);
        Assert.Equal(expectedTransport, observed.TargetTransport);
        Assert.False(observed.RequiresResumeRequest);
    }

    [Theory]
    [InlineData("RolledBack", true)]
    [InlineData("TerminalInvalidated", false)]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task SupersededDevLocalHandoffWaitsForFinalDisposition(
        string finalDispositionKind,
        bool releasesAvailability)
    {
        using var transport = new DevLocalTransport();
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(
            "route-transition-session",
            "route-transition-transfer",
            CancellationToken.None);
        var authority = CreateAuthority(GetGeneration(dataSession));
        var bridge =
            Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(
                transport);
        var availability = new ConcurrentQueue<
            FileTransferDataSessionAvailabilityChangedEventArgs>();
        dataSession.AvailabilityChanged += (_, args) => availability.Enqueue(args);

        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(
                new FileTransferRouteTransitionPauseBindCommand(authority)).Status);
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(
                CreateSupersededDisposition(authority)).Status);
        await Task.Delay(25);
        Assert.False(dataSession.IsAvailable);
        Assert.Empty(availability);

        var finalKind =
            Enum.Parse<FileTransferRouteTransitionDispositionKind>(
                finalDispositionKind);
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(
                new FileTransferRouteTransitionDispositionCommand(
                    authority,
                    finalKind,
                    RouteCommitToken: null,
                    RoutePublicationToken: null,
                    Reason: "test_devlocal_superseded_final")).Status);

        if (releasesAvailability)
        {
            var observed = await WaitForAvailabilityAsync(availability);
            Assert.True(dataSession.IsAvailable);
            Assert.Equal(
                FileTransferTransportKind.RegularNkn,
                observed.TargetTransport);
        }
        else
        {
            await Task.Delay(50);
            Assert.False(dataSession.IsAvailable);
            Assert.Empty(availability);
        }
    }

    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task ExactSuccessorRebindRetainsPredecessorUntilSuperseded()
    {
        using var transport = new DevLocalTransport();
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(
            "route-transition-session",
            "route-transition-transfer",
            CancellationToken.None);
        var predecessor = CreateAuthority(GetGeneration(dataSession));
        var successor = CreateFallbackSuccessor(predecessor);
        var bridge =
            Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(
                transport);

        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(
                new FileTransferRouteTransitionPauseBindCommand(predecessor))
                .Status);
        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(
                new FileTransferRouteTransitionPauseBindCommand(successor)
                {
                    PredecessorAuthority = predecessor,
                }).Status);
        Assert.False(dataSession.IsAvailable);

        var predecessorSuperseded =
            new FileTransferRouteTransitionDispositionCommand(
                predecessor,
                FileTransferRouteTransitionDispositionKind.Superseded,
                RouteCommitToken: null,
                RoutePublicationToken: null,
                Reason: "test_predecessor_superseded");
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(
                predecessorSuperseded).Status);
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Idempotent,
            bridge.ApplyFileTransferRouteTransitionDisposition(
                predecessorSuperseded).Status);
        Assert.False(dataSession.IsAvailable);

        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Rejected,
            bridge.ApplyFileTransferRouteTransitionDisposition(
                new FileTransferRouteTransitionDispositionCommand(
                    predecessor,
                    FileTransferRouteTransitionDispositionKind.RolledBack,
                    RouteCommitToken: null,
                    RoutePublicationToken: null,
                    Reason: "test_stale_predecessor_rollback")).Status);
        Assert.False(dataSession.IsAvailable);

        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(
                new FileTransferRouteTransitionDispositionCommand(
                    successor,
                    FileTransferRouteTransitionDispositionKind.RolledBack,
                    RouteCommitToken: null,
                    RoutePublicationToken: null,
                    Reason: "test_successor_rollback")).Status);
        await WaitUntilAsync(() => dataSession.IsAvailable);
    }

    private static long GetGeneration(IFileTransferDataSession dataSession)
        => Assert.IsType<long>(
            dataSession.GetType().GetProperty("Generation")!.GetValue(dataSession));

    private static FileTransferRouteTransitionAuthority CreateAuthority(
        long dataSessionGeneration)
        => InvokeFactory<FileTransferRouteTransitionAuthority>(
            "CreateAuthority",
            "devlocal-availability",
            dataSessionGeneration);

    private static FileTransferRouteTransitionDispositionCommand
        CreateCommittedDisposition(
            FileTransferRouteTransitionAuthority authority)
        => InvokeFactory<FileTransferRouteTransitionDispositionCommand>(
            "CreateCommittedDisposition",
            authority);

    private static FileTransferRouteTransitionDispositionCommand
        CreateSupersededDisposition(
            FileTransferRouteTransitionAuthority authority)
        => InvokeFactory<FileTransferRouteTransitionDispositionCommand>(
            "CreateSupersededDisposition",
            authority);

    private static FileTransferRouteTransitionAuthority CreateFallbackSuccessor(
        FileTransferRouteTransitionAuthority predecessor)
    {
        var frameIdentity = new PostTunaFallbackPeerProofFrameIdentity(
            FileTransferProtocol.ReceiverStateFrameTypeV6,
            TransportEpoch: 7,
            Sha256: new string('a', 64));
        var peerProof = new PostTunaFallbackPeerProofEvidence(
            predecessor.SessionId,
            predecessor.TransferId,
            predecessor.Direction,
            ProofGeneration: 1,
            TransportEpoch: frameIdentity.TransportEpoch,
            ProofKind: "filetransfer_v6_peer_receiver_state_received",
            Reason: "test_peer_fallback_proof",
            FrameIdentity: frameIdentity);
        return predecessor with
        {
            Kind =
                FileTransferRouteTransitionKind
                    .UncommittedTunaToPostTunaFallbackV6,
            ProofBoundary = new FileTransferFallbackTransitionProofBoundary(
                SourceRouteCommit: null,
                peerProof,
                frameIdentity,
                TargetTransportEpoch: frameIdentity.TransportEpoch,
                TargetBridgeRecoveryGeneration: 1,
                PredecessorTransitionAuthorityId: predecessor.AuthorityId),
            TargetRoute = FileTransferRoute.PostTunaFallbackV6,
            TargetProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            TargetTransport = FileTransferTransportKind.RegularNkn,
            HandoffKind =
                FileTransferTransportHandoffKind.TunaToNormalFallback,
            TransitionGeneration = predecessor.TransitionGeneration + 1,
            AuthorityId = new FileTransferAuthorityId(
                "fta4-devlocal-fallback-successor"),
            PredecessorTransitionAuthorityId = predecessor.AuthorityId,
            Reason = "test_uncommitted_tuna_fallback_successor",
        };
    }

    private static T InvokeFactory<T>(string methodName, params object[] args)
    {
        var method =
            typeof(FileTransferRouteTransitionInfraTests).GetMethod(
                methodName,
                BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<T>(method!.Invoke(null, args));
    }

    private static async Task<FileTransferDataSessionAvailabilityChangedEventArgs>
        WaitForAvailabilityAsync(
            ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>
                events)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (events.TryDequeue(out var observed))
            {
                return observed;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Expected an exact DevLocal transition availability event.");
        return null!;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(predicate());
    }
}
