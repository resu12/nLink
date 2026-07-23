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
public sealed class RegularV4ToTunaRouteTransitionInfraTests : IDisposable
{
    public RegularV4ToTunaRouteTransitionInfraTests()
        => FakeNknClient.ResetNetwork();

    public void Dispose()
        => FakeNknClient.ResetNetwork();

    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "RegularV4ToTunaRouteTransition")]
    public async Task DevLocal_TransitionPauseShadow_IsExactAndIdempotent()
    {
        using var transport = new DevLocalTransport();
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(
            "route-transition-session",
            "route-transition-transfer",
            CancellationToken.None);
        var bridge = Assert.IsAssignableFrom<IRegularV4ToTunaRouteTransitionBridge>(
            transport);
        var authority = CreateAuthority(
            "devlocal-transition",
            GetDataSessionGeneration(dataSession));
        var bind = new RegularV4ToTunaActivationPauseBindCommand(authority);

        Assert.Equal(
            RegularV4ToTunaActivationPauseBindStatus.Installed,
            bridge.BindRegularV4ToTunaActivationPause(bind).Status);
        Assert.Equal(
            RegularV4ToTunaActivationPauseBindStatus.Idempotent,
            bridge.BindRegularV4ToTunaActivationPause(bind).Status);
        Assert.Equal(
            RegularV4ToTunaActivationPauseBindStatus.Rejected,
            bridge.BindRegularV4ToTunaActivationPause(
                bind with
                {
                    Authority = authority with
                    {
                        SourceBridgeRecoveryGeneration =
                            authority.SourceBridgeRecoveryGeneration + 1,
                    },
                }).Status);

        var rollback = new RegularV4ToTunaRouteTransitionDispositionCommand(
            authority,
            RegularV4ToTunaRouteTransitionDispositionKind.RolledBack,
            RouteCommitToken: null,
            RoutePublicationToken: null,
            Reason: "test_rollback");
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(rollback).Status);
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Idempotent,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(rollback).Status);
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Rejected,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(
                rollback with
                {
                    Kind = RegularV4ToTunaRouteTransitionDispositionKind.Committed,
                }).Status);
    }

    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "RegularV4ToTunaRouteTransition")]
    public async Task DevLocal_TransitionCommit_RequiresExactOwnerAndPublicationTuple()
    {
        using var transport = new DevLocalTransport();
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(
            "route-transition-session",
            "route-transition-transfer",
            CancellationToken.None);
        var bridge = Assert.IsAssignableFrom<IRegularV4ToTunaRouteTransitionBridge>(
            transport);
        var authority = CreateAuthority(
            "devlocal-commit",
            GetDataSessionGeneration(dataSession));
        Assert.Equal(
            RegularV4ToTunaActivationPauseBindStatus.Installed,
            bridge.BindRegularV4ToTunaActivationPause(
                new RegularV4ToTunaActivationPauseBindCommand(authority)).Status);
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
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Rejected,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(mismatched).Status);
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(committed).Status);
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Idempotent,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(committed).Status);
    }

    [Theory]
    [InlineData("RolledBack", true)]
    [InlineData("Committed", true)]
    [InlineData("TerminalInvalidated", false)]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "RegularV4ToTunaRouteTransition")]
    public async Task Nkn_BoundTransitionPause_ReleasesOnlyFromExactDisposition(
        string dispositionKind,
        bool releasesAvailability)
    {
        var kind = Enum.Parse<RegularV4ToTunaRouteTransitionDispositionKind>(
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

        var bridge = Assert.IsAssignableFrom<IRegularV4ToTunaRouteTransitionBridge>(
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
        var bind = new RegularV4ToTunaActivationPauseBindCommand(authority);
        Assert.Equal(
            RegularV4ToTunaActivationPauseBindStatus.Installed,
            bridge.BindRegularV4ToTunaActivationPause(bind).Status);

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
                          RegularV4ToTunaRouteTransitionDispositionKind.Committed
            ? CreateCommittedDisposition(authority)
            : new RegularV4ToTunaRouteTransitionDispositionCommand(
                authority,
                kind,
                RouteCommitToken: null,
                RoutePublicationToken: null,
                Reason: kind ==
                        RegularV4ToTunaRouteTransitionDispositionKind.RolledBack
                    ? "test_rollback"
                    : "test_terminal_invalidated");
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(disposition).Status);
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
    [Trait("FileTransferInfraSuite", "RegularV4ToTunaRouteTransition")]
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
            Assert.IsAssignableFrom<IRegularV4ToTunaRouteTransitionBridge>(
                transport);
        Assert.Equal(
            RegularV4ToTunaActivationPauseBindStatus.Installed,
            bridge.BindRegularV4ToTunaActivationPause(
                    new RegularV4ToTunaActivationPauseBindCommand(authority))
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
    [Trait("FileTransferInfraSuite", "RegularV4ToTunaRouteTransition")]
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
        var bridge = Assert.IsAssignableFrom<IRegularV4ToTunaRouteTransitionBridge>(
            transport);
        var authority = CreateAuthority("nkn-transition-superseded", generation);
        Assert.Equal(
            RegularV4ToTunaActivationPauseBindStatus.Installed,
            bridge.BindRegularV4ToTunaActivationPause(
                new RegularV4ToTunaActivationPauseBindCommand(authority)).Status);

        var superseded = CreateSupersededDisposition(authority);
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(superseded).Status);
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
    [Trait("FileTransferInfraSuite", "RegularV4ToTunaRouteTransition")]
    public async Task Nkn_SupersededTransition_ExactRollbackOrTerminalRetiresPauseShadow(
        string dispositionKind,
        bool releasesAvailability)
    {
        var kind = Enum.Parse<RegularV4ToTunaRouteTransitionDispositionKind>(
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
            Assert.IsAssignableFrom<IRegularV4ToTunaRouteTransitionBridge>(
                transport);
        var authority = CreateAuthority(
            $"nkn-superseded-{dispositionKind}",
            generation);
        Assert.Equal(
            RegularV4ToTunaActivationPauseBindStatus.Installed,
            bridge.BindRegularV4ToTunaActivationPause(
                new RegularV4ToTunaActivationPauseBindCommand(authority))
                .Status);
        var superseded = CreateSupersededDisposition(authority);
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(
                superseded).Status);

        var final = new RegularV4ToTunaRouteTransitionDispositionCommand(
            authority,
            kind,
            RouteCommitToken: null,
            RoutePublicationToken: null,
            Reason: kind ==
                    RegularV4ToTunaRouteTransitionDispositionKind.RolledBack
                ? "test_superseded_rollback"
                : "test_superseded_terminal");
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(final)
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
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Rejected,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(
                CreateCommittedDisposition(authority)).Status);
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Rejected,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(
                superseded).Status);
    }

    [Theory]
    [InlineData("RolledBack")]
    [InlineData("TerminalInvalidated")]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "RegularV4ToTunaRouteTransition")]
    public async Task DevLocal_SupersededTransition_ExactRollbackOrTerminalRetiresPauseShadow(
        string dispositionKind)
    {
        var kind = Enum.Parse<RegularV4ToTunaRouteTransitionDispositionKind>(
            dispositionKind);
        using var transport = new DevLocalTransport();
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(
            "route-transition-session",
            "route-transition-transfer",
            CancellationToken.None);
        var bridge =
            Assert.IsAssignableFrom<IRegularV4ToTunaRouteTransitionBridge>(
                transport);
        var authority = CreateAuthority(
            $"devlocal-superseded-{dispositionKind}",
            GetDataSessionGeneration(dataSession));
        Assert.Equal(
            RegularV4ToTunaActivationPauseBindStatus.Installed,
            bridge.BindRegularV4ToTunaActivationPause(
                new RegularV4ToTunaActivationPauseBindCommand(authority))
                .Status);
        var superseded = CreateSupersededDisposition(authority);
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(
                superseded).Status);

        var final = new RegularV4ToTunaRouteTransitionDispositionCommand(
            authority,
            kind,
            RouteCommitToken: null,
            RoutePublicationToken: null,
            Reason: "test_superseded_terminal_disposition");
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(final)
                .Status);
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Rejected,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(
                CreateCommittedDisposition(authority)).Status);
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Rejected,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(
                superseded).Status);
    }

    [Theory]
    [InlineData("Committed")]
    [InlineData("RolledBack")]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "RegularV4ToTunaRouteTransition")]
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
            Assert.IsAssignableFrom<IRegularV4ToTunaRouteTransitionBridge>(
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
            RegularV4ToTunaActivationPauseBindStatus.Installed,
            bridge.BindRegularV4ToTunaActivationPause(
                new RegularV4ToTunaActivationPauseBindCommand(firstAuthority))
                .Status);
        var firstDisposition = string.Equals(
            dispositionKind,
            "Committed",
            StringComparison.Ordinal)
            ? CreateCommittedDisposition(firstAuthority)
            : new RegularV4ToTunaRouteTransitionDispositionCommand(
                firstAuthority,
                RegularV4ToTunaRouteTransitionDispositionKind.RolledBack,
                RouteCommitToken: null,
                RoutePublicationToken: null,
                Reason: "test_first_rollback");
        Assert.Equal(
            RegularV4ToTunaRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyRegularV4ToTunaRouteTransitionDisposition(
                firstDisposition).Status);
        await WaitUntilAsync(() => dataSession.IsAvailable);
        await WaitUntilAsync(
            () => GetPrivateFieldValue(
                transport,
                "pendingRegularV4ToTunaRouteTransitionAvailabilityDisposition")
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
            RegularV4ToTunaActivationPauseBindStatus.Installed,
            bridge.BindRegularV4ToTunaActivationPause(
                    new RegularV4ToTunaActivationPauseBindCommand(
                        successorAuthority))
                .Status);
        Assert.False(dataSession.IsAvailable);

        InvokePrivate(
            transport,
            "ApplyRegularV4ToTunaRouteTransitionAvailability",
            firstDisposition);
        await Task.Delay(50);

        Assert.False(dataSession.IsAvailable);
    }

    private static RegularV4ToTunaRouteTransitionAuthority CreateAuthority(
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
        var checkpoint = new RegularV4ToTunaDurableCheckpoint(
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
        return new RegularV4ToTunaRouteTransitionAuthority(
            sessionId,
            transferId,
            FileTransferDirection.Outbound,
            sourceOwner,
            sourcePublication,
            SourceTerminalGeneration: 0,
            SourceLiveRouteEpoch: 1,
            SourceLegGeneration: 1,
            SourceTransportEpoch: 1,
            SourceBridgeRecoveryGeneration: 0,
            DataSessionGeneration: dataSessionGeneration,
            admission,
            pending,
            proof,
            LaunchGeneration: 1,
            checkpoint,
            TargetRoute: FileTransferRoute.FileTunaV4,
            TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
            TargetTransport: FileTransferTransportKind.Tuna,
            HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
            TransitionGeneration: 1,
            AuthorityId: new FileTransferAuthorityId(
                $"fta3-{reason}-route-transition"),
            Reason: reason);
    }

    private static long GetDataSessionGeneration(
        IFileTransferDataSession dataSession)
        => Assert.IsType<long>(
            dataSession.GetType()
                .GetProperty("Generation", BindingFlags.Instance | BindingFlags.Public)!
                .GetValue(dataSession));

    private static RegularV4ToTunaRouteTransitionDispositionCommand
        CreateCommittedDisposition(
            RegularV4ToTunaRouteTransitionAuthority authority)
    {
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
            authority.RuntimeUnlockAdmission,
            FileTransferRoute.FileTunaV4,
            "file_tuna_v4",
            FileTransferProtocol.ProtocolVersionV4,
            LiveRouteEpoch: 2,
            RuntimeGeneration: 2,
            TerminalGeneration: authority.SourceTerminalGeneration,
            LegGeneration: 2,
            TransactionGeneration:
                authority.AcceptedLaunchBoundary.TransactionGeneration,
            OfferGeneration: authority.AcceptedLaunchBoundary.OfferGeneration,
            TunaPathLeaseGeneration:
                authority.AcceptedLaunchBoundary.TunaPathLeaseGeneration,
            TunaPathLeaseListenerRunId:
                authority.AcceptedLaunchBoundary.TunaPathLeaseListenerRunId,
            SamePathTunaProbeAckId:
                authority.AcceptedLaunchBoundary.SamePathTunaProbeAckId!,
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
        return new RegularV4ToTunaRouteTransitionDispositionCommand(
            authority,
            RegularV4ToTunaRouteTransitionDispositionKind.Committed,
            commit,
            publication,
            "test_commit")
        {
            FinalProof = authority.AcceptedLaunchBoundary,
        };
    }

    private static RuntimeUnlockRouteCommitSnapshot CreateLaunchSnapshot(
        RegularV4ToTunaRouteTransitionAuthority authority)
    {
        var boundary = authority.AcceptedLaunchBoundary;
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
            LaunchGeneration: authority.LaunchGeneration);
    }

    private static RegularV4ToTunaRouteTransitionDispositionCommand
        CreateSupersededDisposition(
            RegularV4ToTunaRouteTransitionAuthority authority)
    {
        var fallbackOwner = new RuntimeOwnerToken(
            authority.SessionId,
            authority.TransferId,
            authority.Direction,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferRouteResolver.PostTunaFallbackV6Token,
            FileTransferProtocol.ProtocolVersionV6,
            LiveRouteEpoch: 2,
            RuntimeGeneration: 2,
            TerminalGeneration: authority.SourceTerminalGeneration,
            RuntimeFamily: "post_tuna_fallback_v6",
            CreatedReason: "test_fallback_superseded");
        var publication = new RoutePublicationToken(
            authority.SessionId,
            authority.TransferId,
            authority.Direction,
            fallbackOwner,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferRouteResolver.PostTunaFallbackV6Token,
            FileTransferProtocol.ProtocolVersionV6,
            LiveRouteEpoch: 2,
            RuntimeGeneration: 2,
            TerminalGeneration: authority.SourceTerminalGeneration,
            LegGeneration: 2,
            RouteCommitGeneration: null,
            TargetRoute: FileTransferRoute.PostTunaFallbackV6,
            TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
            TargetTransport: FileTransferTransportKind.RegularNkn,
            PublicationGeneration: 2,
            ActionKind: FileTransferCoordinatorActionKind.RoutePublication,
            Reason: "test_fallback_superseded");
        return new RegularV4ToTunaRouteTransitionDispositionCommand(
            authority,
            RegularV4ToTunaRouteTransitionDispositionKind.Superseded,
            RouteCommitToken: null,
            publication,
            Reason: "test_fallback_superseded");
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
