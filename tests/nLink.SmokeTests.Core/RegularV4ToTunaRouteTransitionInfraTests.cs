using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core.Configuration;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.SessionSecurity;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRouteTransitionInfraTests : IDisposable
{
    public FileTransferRouteTransitionInfraTests()
        => FakeNknClient.ResetNetwork();

    public void Dispose()
        => FakeNknClient.ResetNetwork();

    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task DevLocal_TransitionPauseShadow_IsExactAndIdempotent()
    {
        using var transport = new DevLocalTransport();
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(
            "route-transition-session",
            "route-transition-transfer",
            CancellationToken.None);
        var bridge = Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(
            transport);
        var authority = CreateAuthority(
            "devlocal-transition",
            GetDataSessionGeneration(dataSession));
        var bind = new FileTransferRouteTransitionPauseBindCommand(authority);

        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(bind).Status);
        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Idempotent,
            bridge.BindFileTransferRouteTransitionPause(bind).Status);
        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Rejected,
            bridge.BindFileTransferRouteTransitionPause(
                bind with
                {
                    Authority = authority with
                    {
                        SourceBridgeRecoveryGeneration =
                            authority.SourceBridgeRecoveryGeneration + 1,
                    },
                }).Status);

        var rollback = new FileTransferRouteTransitionDispositionCommand(
            authority,
            FileTransferRouteTransitionDispositionKind.RolledBack,
            RouteCommitToken: null,
            RoutePublicationToken: null,
            Reason: "test_rollback");
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(rollback).Status);
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Idempotent,
            bridge.ApplyFileTransferRouteTransitionDisposition(rollback).Status);
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Rejected,
            bridge.ApplyFileTransferRouteTransitionDisposition(
                rollback with
                {
                    Kind = FileTransferRouteTransitionDispositionKind.Committed,
                }).Status);
    }

    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task DevLocal_TransitionCommit_RequiresExactOwnerAndPublicationTuple()
    {
        using var transport = new DevLocalTransport();
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(
            "route-transition-session",
            "route-transition-transfer",
            CancellationToken.None);
        var bridge = Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(
            transport);
        var authority = CreateAuthority(
            "devlocal-commit",
            GetDataSessionGeneration(dataSession));
        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(
                new FileTransferRouteTransitionPauseBindCommand(authority)).Status);
        var committed = CreateCommittedDisposition(authority);
        var mismatched = committed with
        {
            RoutePublicationToken = committed.RoutePublicationToken!.Value with
            {
                RuntimeOwner =
                    committed.RoutePublicationToken.Value.RuntimeOwner with
                    {
                        RuntimeGeneration =
                            committed.RoutePublicationToken.Value.RuntimeGeneration + 1,
                    },
            },
        };

        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Rejected,
            bridge.ApplyFileTransferRouteTransitionDisposition(mismatched).Status);
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(committed).Status);
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Idempotent,
            bridge.ApplyFileTransferRouteTransitionDisposition(committed).Status);
    }

    [Theory]
    [InlineData("RolledBack", true)]
    [InlineData("Committed", true)]
    [InlineData("TerminalInvalidated", false)]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task Nkn_BoundTransitionPause_ReleasesOnlyFromExactDisposition(
        string dispositionKind,
        bool releasesAvailability)
    {
        var kind = Enum.Parse<FileTransferRouteTransitionDispositionKind>(
            dispositionKind);
        FakeNknClient.ResetNetwork();
        var client = new FakeNknClient("route-transition-infra.invalid");
        using var transport = new NknSignalingTransport(
            client,
            NknTransportOptions.Load(),
            new NknIdentity("route-transition-infra", client.Address),
            NknTunaAccelerationOptions.Disabled,
            accelerationLane: null);
        const string sessionId = "route-transition-session";
        const string transferId = "route-transition-transfer";
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(
            sessionId,
            transferId,
            CancellationToken.None);
        var generation = Assert.IsType<long>(
            dataSession.GetType().GetProperty("Generation")!.GetValue(dataSession));
        Assert.True(Assert.IsType<bool>(InvokePrivate(
            transport,
            "PauseFileTransferDataSessionsForTunaActivationNegotiation",
            "test_transition_pause",
            sessionId,
            "unit_test")));
        Assert.False(dataSession.IsAvailable);

        var bridge = Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(
            transport);
        var authority = CreateAuthority("nkn-transition", generation);
        InvokePrivate(
            dataSession,
            "RequestHandoff",
            "coordinator_transition_handoff",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            CreateLaunchSnapshot(authority));
        Assert.False(dataSession.IsAvailable);
        InvokePrivate(
            dataSession,
            "RequestHandoff",
            "legacy_null_snapshot_handoff",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            null);
        Assert.False(dataSession.IsAvailable);
        var bind = new FileTransferRouteTransitionPauseBindCommand(authority);
        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(bind).Status);

        InvokePrivate(
            dataSession,
            "RequestHandoff",
            "coordinator_transition_handoff",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            null);
        Assert.False(dataSession.IsAvailable);
        Assert.False(Assert.IsType<bool>(InvokePrivate(
            transport,
            "TryResumeFileTransferDataSessionsAfterTunaActivationNegotiation",
            "local_timeout",
            sessionId,
            "unit_test")));
        Assert.False(dataSession.IsAvailable);

        var disposition = kind ==
                          FileTransferRouteTransitionDispositionKind.Committed
            ? CreateCommittedDisposition(authority)
            : new FileTransferRouteTransitionDispositionCommand(
                authority,
                kind,
                RouteCommitToken: null,
                RoutePublicationToken: null,
                Reason: kind ==
                        FileTransferRouteTransitionDispositionKind.RolledBack
                    ? "test_rollback"
                    : "test_terminal_invalidated");
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(disposition).Status);
        if (releasesAvailability)
        {
            await WaitUntilAsync(() => dataSession.IsAvailable);
        }
        else
        {
            await Task.Delay(50);
            Assert.False(dataSession.IsAvailable);
        }
    }

    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task Nkn_BoundTransitionPause_ForwardsProofScopedFallbackWithoutResuming()
    {
        var client = new FakeNknClient(
            "route-transition-proof-scoped-fallback.invalid");
        using var transport = new NknSignalingTransport(
            client,
            NknTransportOptions.Load(),
            new NknIdentity(
                "route-transition-proof-scoped-fallback",
                client.Address),
            NknTunaAccelerationOptions.Disabled,
            accelerationLane: null);
        const string sessionId = "route-transition-session";
        const string transferId = "route-transition-transfer";
        using var dataSession =
            await transport.OpenFileTransferDataSessionAsync(
                sessionId,
                transferId,
                CancellationToken.None);
        var generation = GetDataSessionGeneration(dataSession);
        var availabilityEvents =
            new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
        dataSession.AvailabilityChanged += (_, e) =>
            availabilityEvents.Enqueue(e);
        InvokePrivate(
            transport,
            "TrackAuthorizedFileTransferRouteHintForTestsCore",
            sessionId,
            transferId,
            FileTransferRouteResolver.RegularNknV4FastToken,
            FileTransferProtocol.ProtocolVersionV4,
            "test_regular_v4_transition_fallback_boundary",
            false,
            "unknown",
            "unknown",
            false,
            0L,
            null,
            1,
            0L,
            0,
            FileTransferDirection.Outbound);
        Assert.True(Assert.IsType<bool>(InvokePrivate(
            transport,
            "PauseFileTransferDataSessionsForTunaActivationNegotiation",
            "test_transition_pause",
            sessionId,
            "unit_test")));
        var authority = CreateAuthority(
            "nkn-proof-scoped-fallback",
            generation);
        var bridge =
            Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(
                transport);
        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(
                    new FileTransferRouteTransitionPauseBindCommand(authority))
                .Status);

        var evidence = new PostTunaFallbackHandoffEvidence(
            sessionId,
            transferId,
            FileTransferDirection.Outbound,
            ProofGeneration: 1,
            FileTransferRouteResolver.PostTunaFallbackV6Token,
            FileTransferProtocol.ProtocolVersionV6,
            SourceLiveRouteEpoch: 3,
            SourceLegGeneration: 2,
            SourceBridgeRecoveryGeneration: 1,
            SourceTransportEpoch: 7,
            CheckpointRequestId: "(none)",
            ProofKind: "filetransfer_v6_peer_receiver_state_received",
            Reason: "peer_v6_fallback_proof");
        InvokePrivate(
            transport,
            "SetFileTransferDataSessionsAvailability",
            true,
            "transport_recovered",
            true,
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn,
            evidence,
            null);

        await WaitUntilAsync(
            () => availabilityEvents.Any(
                e => e.PostTunaFallbackHandoffEvidence == evidence));
        var proofBoundary = Assert.Single(
            availabilityEvents.Where(
                e => e.PostTunaFallbackHandoffEvidence == evidence));
        Assert.False(proofBoundary.IsAvailable);
        Assert.False(dataSession.IsAvailable);

        availabilityEvents.Clear();
        InvokePrivate(
            transport,
            "SetFileTransferDataSessionsAvailability",
            true,
            "transport_recovered",
            false,
            FileTransferTransportHandoffKind.None,
            FileTransferTransportKind.RegularNkn,
            null,
            null);
        await Task.Delay(50);

        Assert.Empty(availabilityEvents);
        Assert.False(dataSession.IsAvailable);
    }

    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task Nkn_SupersededTransition_LeavesSuccessorPauseUnavailable()
    {
        FakeNknClient.ResetNetwork();
        var client = new FakeNknClient("route-transition-superseded.invalid");
        using var transport = new NknSignalingTransport(
            client,
            NknTransportOptions.Load(),
            new NknIdentity("route-transition-superseded", client.Address),
            NknTunaAccelerationOptions.Disabled,
            accelerationLane: null);
        const string sessionId = "route-transition-session";
        const string transferId = "route-transition-transfer";
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(
            sessionId,
            transferId,
            CancellationToken.None);
        var generation = Assert.IsType<long>(
            dataSession.GetType().GetProperty("Generation")!.GetValue(dataSession));
        Assert.True(Assert.IsType<bool>(InvokePrivate(
            transport,
            "PauseFileTransferDataSessionsForTunaActivationNegotiation",
            "test_transition_pause",
            sessionId,
            "unit_test")));
        var bridge = Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(
            transport);
        var authority = CreateAuthority("nkn-transition-superseded", generation);
        var successor = CreateFallbackSuccessor(authority);
        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(
                new FileTransferRouteTransitionPauseBindCommand(authority)).Status);
        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(
                new FileTransferRouteTransitionPauseBindCommand(successor)
                {
                    PredecessorAuthority = authority,
                }).Status);

        var superseded = CreateSupersededDisposition(authority);
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(superseded).Status);
        Assert.False(Assert.IsType<bool>(InvokePrivate(
            transport,
            "TryResumeFileTransferDataSessionsAfterTunaActivationNegotiation",
            "local_timeout",
            sessionId,
            "unit_test")));
        await Task.Delay(50);
        Assert.False(dataSession.IsAvailable);
    }

    [Theory]
    [InlineData("RolledBack", true)]
    [InlineData("TerminalInvalidated", false)]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task Nkn_SupersededTransition_ExactRollbackOrTerminalRetiresPauseShadow(
        string dispositionKind,
        bool releasesAvailability)
    {
        var kind = Enum.Parse<FileTransferRouteTransitionDispositionKind>(
            dispositionKind);
        var client = new FakeNknClient(
            $"route-transition-superseded-{dispositionKind}.invalid");
        using var transport = new NknSignalingTransport(
            client,
            NknTransportOptions.Load(),
            new NknIdentity(
                $"route-transition-superseded-{dispositionKind}",
                client.Address),
            NknTunaAccelerationOptions.Disabled,
            accelerationLane: null);
        const string sessionId = "route-transition-session";
        const string transferId = "route-transition-transfer";
        using var dataSession =
            await transport.OpenFileTransferDataSessionAsync(
                sessionId,
                transferId,
                CancellationToken.None);
        var generation = Assert.IsType<long>(
            dataSession.GetType().GetProperty("Generation")!
                .GetValue(dataSession));
        Assert.True(Assert.IsType<bool>(InvokePrivate(
            transport,
            "PauseFileTransferDataSessionsForTunaActivationNegotiation",
            "test_transition_pause",
            sessionId,
            "unit_test")));
        var bridge =
            Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(
                transport);
        var authority = CreateAuthority(
            $"nkn-superseded-{dispositionKind}",
            generation);
        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(
                new FileTransferRouteTransitionPauseBindCommand(authority))
                .Status);
        var superseded = CreateSupersededDisposition(authority);
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(
                superseded).Status);

        var final = new FileTransferRouteTransitionDispositionCommand(
            authority,
            kind,
            RouteCommitToken: null,
            RoutePublicationToken: null,
            Reason: kind ==
                    FileTransferRouteTransitionDispositionKind.RolledBack
                ? "test_superseded_rollback"
                : "test_superseded_terminal");
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(final)
                .Status);
        if (releasesAvailability)
        {
            await WaitUntilAsync(() => dataSession.IsAvailable);
        }
        else
        {
            await Task.Delay(50);
            Assert.False(dataSession.IsAvailable);
        }

        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Rejected,
            bridge.ApplyFileTransferRouteTransitionDisposition(
                CreateCommittedDisposition(authority)).Status);
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Rejected,
            bridge.ApplyFileTransferRouteTransitionDisposition(
                superseded).Status);
    }

    [Theory]
    [InlineData("RolledBack")]
    [InlineData("TerminalInvalidated")]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task DevLocal_SupersededTransition_ExactRollbackOrTerminalRetiresPauseShadow(
        string dispositionKind)
    {
        var kind = Enum.Parse<FileTransferRouteTransitionDispositionKind>(
            dispositionKind);
        using var transport = new DevLocalTransport();
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(
            "route-transition-session",
            "route-transition-transfer",
            CancellationToken.None);
        var bridge =
            Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(
                transport);
        var authority = CreateAuthority(
            $"devlocal-superseded-{dispositionKind}",
            GetDataSessionGeneration(dataSession));
        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(
                new FileTransferRouteTransitionPauseBindCommand(authority))
                .Status);
        var superseded = CreateSupersededDisposition(authority);
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(
                superseded).Status);

        var final = new FileTransferRouteTransitionDispositionCommand(
            authority,
            kind,
            RouteCommitToken: null,
            RoutePublicationToken: null,
            Reason: "test_superseded_terminal_disposition");
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(final)
                .Status);
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Rejected,
            bridge.ApplyFileTransferRouteTransitionDisposition(
                CreateCommittedDisposition(authority)).Status);
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Rejected,
            bridge.ApplyFileTransferRouteTransitionDisposition(
                superseded).Status);
    }

    [Theory]
    [InlineData("Committed")]
    [InlineData("RolledBack")]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task Nkn_OldAvailabilityDispositionCannotCrossSuccessorPauseBinding(
        string dispositionKind)
    {
        var client = new FakeNknClient(
            $"route-transition-stale-availability-{dispositionKind}.invalid");
        using var transport = new NknSignalingTransport(
            client,
            NknTransportOptions.Load(),
            new NknIdentity(
                $"route-transition-stale-availability-{dispositionKind}",
                client.Address),
            NknTunaAccelerationOptions.Disabled,
            accelerationLane: null);
        const string sessionId = "route-transition-session";
        const string transferId = "route-transition-transfer";
        using var dataSession =
            await transport.OpenFileTransferDataSessionAsync(
                sessionId,
                transferId,
                CancellationToken.None);
        var generation = Assert.IsType<long>(
            dataSession.GetType().GetProperty("Generation")!
                .GetValue(dataSession));
        var bridge =
            Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(
                transport);
        Assert.True(Assert.IsType<bool>(InvokePrivate(
            transport,
            "PauseFileTransferDataSessionsForTunaActivationNegotiation",
            "test_first_transition_pause",
            sessionId,
            "unit_test")));
        var firstAuthority = CreateAuthority(
            $"nkn-first-{dispositionKind}",
            generation);
        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(
                new FileTransferRouteTransitionPauseBindCommand(firstAuthority))
                .Status);
        var firstDisposition = string.Equals(
            dispositionKind,
            "Committed",
            StringComparison.Ordinal)
            ? CreateCommittedDisposition(firstAuthority)
            : new FileTransferRouteTransitionDispositionCommand(
                firstAuthority,
                FileTransferRouteTransitionDispositionKind.RolledBack,
                RouteCommitToken: null,
                RoutePublicationToken: null,
                Reason: "test_first_rollback");
        Assert.Equal(
            FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(
                firstDisposition).Status);
        await WaitUntilAsync(() => dataSession.IsAvailable);
        await WaitUntilAsync(
            () => GetPendingRouteTransitionAvailabilityDisposition(transport)
                is null);

        Assert.True(Assert.IsType<bool>(InvokePrivate(
            transport,
            "PauseFileTransferDataSessionsForTunaActivationNegotiation",
            "test_successor_transition_pause",
            sessionId,
            "unit_test")));
        var successorAuthority = CreateAuthority(
            $"nkn-successor-{dispositionKind}",
            generation);
        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(
                    new FileTransferRouteTransitionPauseBindCommand(
                        successorAuthority))
                .Status);
        Assert.False(dataSession.IsAvailable);

        InvokePrivate(
            transport,
            "ApplyFileTransferRouteTransitionAvailability",
            firstDisposition);
        await Task.Delay(50);

        Assert.False(dataSession.IsAvailable);
    }

    private static object? GetPendingRouteTransitionAvailabilityDisposition(
        NknSignalingTransport transport)
    {
        var shadow = GetPrivateFieldValue(
            transport,
            "fileTransferRouteTransitionExecutionShadow");
        return shadow?.GetType()
            .GetProperty("PendingAvailabilityDisposition")!
            .GetValue(shadow);
    }

    private static FileTransferRouteTransitionAuthority CreateAuthority(
        string reason,
        long dataSessionGeneration)
    {
        const string sessionId = "route-transition-session";
        const string transferId = "route-transition-transfer";
        var sourceOwner = new RuntimeOwnerToken(
            sessionId,
            transferId,
            FileTransferDirection.Outbound,
            FileTransferRoute.RegularNknV4Fast,
            "regular_nkn_v4_fast",
            FileTransferProtocol.ProtocolVersionV4,
            LiveRouteEpoch: 1,
            RuntimeGeneration: 1,
            TerminalGeneration: 0,
            RuntimeFamily: "regular_nkn_v4",
            CreatedReason: reason);
        var sourcePublication = new RoutePublicationToken(
            sessionId,
            transferId,
            FileTransferDirection.Outbound,
            sourceOwner,
            FileTransferRoute.RegularNknV4Fast,
            "regular_nkn_v4_fast",
            FileTransferProtocol.ProtocolVersionV4,
            LiveRouteEpoch: 1,
            RuntimeGeneration: 1,
            TerminalGeneration: 0,
            LegGeneration: 1,
            RouteCommitGeneration: null,
            TargetRoute: FileTransferRoute.RegularNknV4Fast,
            TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
            TargetTransport: FileTransferTransportKind.RegularNkn,
            PublicationGeneration: 1,
            ActionKind: FileTransferCoordinatorActionKind.RoutePublication,
            Reason: reason);
        var admission = new RuntimeUnlockAdmissionToken(
            sessionId,
            transferId,
            FileTransferDirection.Outbound,
            sourceOwner,
            FileTransferRoute.RegularNknV4Fast,
            "regular_nkn_v4_fast",
            FileTransferProtocol.ProtocolVersionV4,
            LiveRouteEpoch: 1,
            RuntimeGeneration: 1,
            TerminalGeneration: 0,
            LegGeneration: 1,
            AdmissionGeneration: 1,
            Reason: reason,
            RuntimeUnlockFallbackSurvivalState.Empty);
        var pending = new RuntimeUnlockPendingPreCommitProbeRequest(
            sessionId,
            transferId,
            FileTransferDirection.Outbound,
            sourceOwner,
            admission,
            FileTransferRoute.RegularNknV4Fast,
            "regular_nkn_v4_fast",
            FileTransferProtocol.ProtocolVersionV4,
            LiveRouteEpoch: 1,
            RuntimeGeneration: 1,
            TerminalGeneration: 0,
            LegGeneration: 1,
            TransactionGeneration: 1,
            OfferGeneration: 1,
            TunaPathLeaseGeneration: 1,
            TunaPathLeaseListenerRunId: "listener-1",
            TargetRoute: FileTransferRoute.FileTunaV4,
            TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
            TargetTransport: FileTransferTransportKind.Tuna,
            HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
            PendingGeneration: 1,
            Reason: reason);
        var proof = new RuntimeUnlockRouteCommitProof(
            sessionId,
            transferId,
            TransactionGeneration: 1,
            OfferGeneration: 1,
            PeerVisibleProof: true,
            PeerReceived: true,
            AnswerReceived: true,
            TargetRoute: FileTransferRoute.FileTunaV4,
            ProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
            HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
            TargetTransport: FileTransferTransportKind.Tuna,
            TransactionState: RuntimeUnlockTransactionState.RouteCommitPending,
            Reason: reason,
            PathProbeId: "probe-1",
            PathProbeState: RuntimeUnlockPathProbeState.Acked,
            PathProbeTransport: FileTransferTransportKind.Tuna,
            PathProbeAckedUtcMs: 1,
            TunaPathLeaseRequired: true,
            TunaPathLeaseGeneration: 1,
            TunaPathLeaseState: RuntimeUnlockTunaPathLeaseState.ListenerReady,
            TunaPathLeaseListenerRunId: "listener-1",
            TunaPathLeaseCurrent: true,
            AdmissionToken: admission,
            SamePathTunaProbeAckId: "probe-1");
        var checkpoint = new FileTransferRouteTransitionDurableCheckpoint(
            sourceOwner,
            sourcePublication,
            SourceTerminalGeneration: 0,
            SourceLegGeneration: 1,
            SourceTransportEpoch: 1,
            SourceBridgeRecoveryGeneration: 0,
            DataSessionGeneration: dataSessionGeneration,
            StateEpoch: 1,
            ContiguousCommittedChunkIndex: 0,
            DurableHighestChunkIndex: -1,
            ChunkCount: 12,
            CheckpointGeneration: 1);
        var proofBoundary =
            new FileTransferTunaActivationTransitionProofBoundary(
                admission,
                pending,
                proof,
                LaunchGeneration: 1);
        return new FileTransferRouteTransitionAuthority(
            FileTransferRouteTransitionKind.RegularV4ToTunaV4,
            SessionId: sessionId,
            TransferId: transferId,
            Direction: FileTransferDirection.Outbound,
            SourceRuntimeOwner: sourceOwner,
            SourceRoutePublication: sourcePublication,
            SourceRouteCommit: null,
            SourceRoute: FileTransferRoute.RegularNknV4Fast,
            SourceProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
            SourceTransport: FileTransferTransportKind.RegularNkn,
            SourceTerminalGeneration: 0,
            SourceLiveRouteEpoch: 1,
            SourceLegGeneration: 1,
            SourceTransportEpoch: 1,
            SourceBridgeRecoveryGeneration: 0,
            DataSessionGeneration: dataSessionGeneration,
            ProofBoundary: proofBoundary,
            DurableCheckpoint: checkpoint,
            TargetRoute: FileTransferRoute.FileTunaV4,
            TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
            TargetTransport: FileTransferTransportKind.Tuna,
            HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
            TransitionGeneration: 1,
            AuthorityId: new FileTransferAuthorityId(
                $"fta3-{reason}-route-transition"),
            PredecessorTransitionAuthorityId: null,
            Reason: reason);
    }

    private static long GetDataSessionGeneration(
        IFileTransferDataSession dataSession)
        => Assert.IsType<long>(
            dataSession.GetType()
                .GetProperty("Generation", BindingFlags.Instance | BindingFlags.Public)!
                .GetValue(dataSession));

    private static FileTransferRouteTransitionDispositionCommand
        CreateCommittedDisposition(
            FileTransferRouteTransitionAuthority authority)
    {
        var activation =
            Assert.IsType<FileTransferTunaActivationTransitionProofBoundary>(
                authority.ProofBoundary);
        var targetOwner = new RuntimeOwnerToken(
            authority.SessionId,
            authority.TransferId,
            authority.Direction,
            FileTransferRoute.FileTunaV4,
            "file_tuna_v4",
            FileTransferProtocol.ProtocolVersionV4,
            LiveRouteEpoch: 2,
            RuntimeGeneration: 2,
            TerminalGeneration: authority.SourceTerminalGeneration,
            RuntimeFamily: "file_tuna_v4",
            CreatedReason: "test_commit");
        var commit = new RouteCommitToken(
            authority.SessionId,
            authority.TransferId,
            authority.Direction,
            targetOwner,
            activation.RuntimeUnlockAdmission,
            FileTransferRoute.FileTunaV4,
            "file_tuna_v4",
            FileTransferProtocol.ProtocolVersionV4,
            LiveRouteEpoch: 2,
            RuntimeGeneration: 2,
            TerminalGeneration: authority.SourceTerminalGeneration,
            LegGeneration: 2,
            TransactionGeneration:
                activation.AcceptedLaunchBoundary.TransactionGeneration,
            OfferGeneration:
                activation.AcceptedLaunchBoundary.OfferGeneration,
            TunaPathLeaseGeneration:
                activation.AcceptedLaunchBoundary.TunaPathLeaseGeneration,
            TunaPathLeaseListenerRunId:
                activation.AcceptedLaunchBoundary
                    .TunaPathLeaseListenerRunId,
            SamePathTunaProbeAckId:
                activation.AcceptedLaunchBoundary.SamePathTunaProbeAckId!,
            TargetRoute: FileTransferRoute.FileTunaV4,
            TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
            TargetTransport: FileTransferTransportKind.Tuna,
            CommitGeneration: 1,
            Reason: "test_commit");
        var publication = new RoutePublicationToken(
            authority.SessionId,
            authority.TransferId,
            authority.Direction,
            targetOwner,
            FileTransferRoute.FileTunaV4,
            "file_tuna_v4",
            FileTransferProtocol.ProtocolVersionV4,
            LiveRouteEpoch: 2,
            RuntimeGeneration: 2,
            TerminalGeneration: authority.SourceTerminalGeneration,
            LegGeneration: 2,
            RouteCommitGeneration: commit.CommitGeneration,
            TargetRoute: FileTransferRoute.FileTunaV4,
            TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
            TargetTransport: FileTransferTransportKind.Tuna,
            PublicationGeneration: 2,
            ActionKind: FileTransferCoordinatorActionKind.RouteCommitPublication,
            Reason: "test_commit");
        return new FileTransferRouteTransitionDispositionCommand(
            authority,
            FileTransferRouteTransitionDispositionKind.Committed,
            commit,
            publication,
            "test_commit")
        {
            FinalProof = activation.AcceptedLaunchBoundary,
        };
    }

    private static RuntimeUnlockRouteCommitSnapshot CreateLaunchSnapshot(
        FileTransferRouteTransitionAuthority authority)
    {
        var activation =
            Assert.IsType<FileTransferTunaActivationTransitionProofBoundary>(
                authority.ProofBoundary);
        var boundary = activation.AcceptedLaunchBoundary;
        return new RuntimeUnlockRouteCommitSnapshot(
            boundary.SessionId,
            boundary.TransferId,
            boundary.TransactionGeneration,
            boundary.OfferGeneration,
            boundary.PeerVisibleProof,
            boundary.PeerReceived,
            boundary.AnswerReceived,
            boundary.TargetRoute,
            boundary.ProtocolVersion,
            boundary.HandoffKind,
            boundary.TargetTransport,
            boundary.TransactionState.ToString(),
            boundary.Reason,
            boundary.PathProbeId,
            boundary.PathProbeState.ToString(),
            boundary.PathProbeTransport,
            boundary.PathProbeAckedUtcMs,
            PathProbeFailureReason: null,
            TunaPathLeaseRequired: boundary.TunaPathLeaseRequired,
            TunaPathLeaseGeneration: boundary.TunaPathLeaseGeneration,
            TunaPathLeaseState: boundary.TunaPathLeaseState.ToString(),
            TunaPathLeaseListenerRunId:
                boundary.TunaPathLeaseListenerRunId,
            TunaPathLeaseCurrent: boundary.TunaPathLeaseCurrent,
            TunaPathLeaseFailureReason: null,
            LaunchGeneration: activation.LaunchGeneration);
    }

    private static FileTransferRouteTransitionDispositionCommand
        CreateSupersededDisposition(
            FileTransferRouteTransitionAuthority authority)
        => new(
            authority,
            FileTransferRouteTransitionDispositionKind.Superseded,
            RouteCommitToken: null,
            RoutePublicationToken: null,
            Reason: "test_fallback_superseded");

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
            AuthorityId =
                new FileTransferAuthorityId("fta4-nkn-fallback-successor"),
            PredecessorTransitionAuthorityId = predecessor.AuthorityId,
            Reason = "test_uncommitted_tuna_fallback_successor",
        };
    }

    private static object? InvokePrivate(
        object target,
        string methodName,
        params object?[] args)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(method);
        return method!.Invoke(target, args);
    }

    private static object? GetPrivateFieldValue(
        object target,
        string fieldName)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target);
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
