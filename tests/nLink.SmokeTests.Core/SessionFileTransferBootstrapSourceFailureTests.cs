using System.Reflection;
using System.Threading.Channels;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false, "throw")]
    [InlineData(true, "throw")]
    [InlineData(false, "supervisor")]
    [InlineData(true, "supervisor")]
    [InlineData(false, "terminal")]
    [InlineData(true, "terminal")]
    [InlineData(true, "unheld")]
    [InlineData(false, "cancel")]
    [InlineData(true, "success")]
    [InlineData(false, "success")]
    [InlineData(true, "unheld_success")]
    [InlineData(true, "physical_failed")]
    [InlineData(false, "physical_failed")]
    [InlineData(true, "terminal-provider")]
    [InlineData(true, "terminal-session")]
    [InlineData(true, "terminal-pause")]
    [InlineData(true, "terminal-agreement")]
    [InlineData(true, "terminal-owner")]
    [InlineData(true, "terminal-publication")]
    [InlineData(true, "usercancel")]
    [InlineData(true, "throw-publication")]
    [InlineData(true, "retired-handoff")]
    [InlineData(true, "retired-next-stop")]
    [InlineData(true, "retired-recovery-before")]
    [InlineData(true, "retired-recovery-after")]
    [InlineData(true, "receive-gap")]
    [InlineData(true, "install-commit")]
    [InlineData(true, "install-reopen")]
    public async Task BootstrapSourceFailure_HoldAfterEligibilityCannotTerminalize(bool physicalAlreadySettled, string boundary)
    {
        var transferId = "bootstrap_source_failure_" + physicalAlreadySettled + "_" + boundary;
        var callbackLogStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var session = new SourceFailureDataSession("session_" + transferId, transferId);
        using var bridge = new BootstrapPhaseTestBridge(session);
        using var peer = new LoopbackFileTransferTransport(session.SessionId);
        bridge.Connect(peer);
        service.AttachTransport(bridge);
        var sourceOpenCount = 0;
        var context = CreateRegularNknV4OutboundContext(transferId, 4, 4, 5,
            DateTimeOffset.UtcNow, seedRuntimeOwner: false, chunkCount: 128, sourceFactory: _ =>
            {
                Interlocked.Increment(ref sourceOpenCount);
                return Task.FromResult<Stream>(new MemoryStream(new byte[128 * 21 * 1024], writable: false));
            });
        SetPrivateProperty(context, "DataSession", session);
        SetPrivateProperty(context, "DataSessionGeneration", 17L);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        SetPrivateProperty(context, "V4LastStateEpoch", 3);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 6);
        AdoptSourceFailureTunaForTest(service, context);
        var peerWake = PeerWakeForSourceFailureTest(context);
        var physical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PhysicalSubmissionForTest = (frame, ct) =>
        {
            if (frame is FileTransferChunkBatchFrameV4)
            { return new(boundary is "success" or "unheld_success" || boundary.StartsWith("retired-", StringComparison.Ordinal) && sourceOpenCount > 1 ? Task.CompletedTask :
                Task.FromException(new IOException("controlled source send failure")), physical.Task); }
            var sent = session.SendAsync(frame, ct);
            return new(sent, sent);
        };
        var previous = SessionFileTransferService.BootstrapSourceSendFailureBeforeThrowForTests;
        var previousSupervisor = SessionFileTransferService.OutboundSparseFailureDeferralBarrierForTests;
        var previousTerminal = SessionFileTransferService.OutboundSparseFailureAuthorizationBarrierForTests;
        var previousCompletion = SessionFileTransferService.OutboundSparseSendCompletionBarrierForTests;
        var previousReopen = SessionFileTransferService.OutboundSparseSourceReopenBarrierForTests;
        var reopenReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reopenRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (boundary == "install-reopen")
        {
            SessionFileTransferService.OutboundSparseSourceReopenBarrierForTests = (id, _) =>
            {
                if (id != transferId) { return Task.CompletedTask; }
                reopenReached.TrySetResult();
                return reopenRelease.Task;
            };
        }
        SessionFileTransferService.BootstrapSourceSendFailureBeforeThrowForTests = (id, lease) =>
        {
            if (id != transferId) { return Task.CompletedTask; }
            reached.TrySetResult(lease);
            return boundary == "supervisor" || boundary.StartsWith("terminal", StringComparison.Ordinal) ? Task.CompletedTask : release.Task;
        };
        Task FinalBarrier(string id, string _)
        {
            if (id != transferId) { return Task.CompletedTask; }
            finalReached.TrySetResult();
            return release.Task;
        }
        if (boundary == "supervisor") { SessionFileTransferService.OutboundSparseFailureDeferralBarrierForTests = FinalBarrier; }
        if (boundary.StartsWith("terminal", StringComparison.Ordinal)) { SessionFileTransferService.OutboundSparseFailureAuthorizationBarrierForTests = FinalBarrier; }
        if (boundary is "success" or "unheld_success")
        {
            SessionFileTransferService.OutboundSparseSendCompletionBarrierForTests = (id, _) =>
            {
                if (id != transferId) { return Task.CompletedTask; }
                reached.TrySetResult(GetPrivateProperty<long>(context, "LastOutboundPhysicalSendLeaseGeneration"));
                return release.Task;
            };
        }
        Task? sending = null;
        object? stagedCallbackInstall = null;
        Task? stagedCallbackReopen = null;
        try
        {
            if (physicalAlreadySettled)
            {
                if (boundary == "physical_failed") { physical.SetException(new IOException("controlled physical failure")); }
                else { physical.SetResult(); }
            }
            sending = StartSparseReceiveSupervisor(service, context);
            var leaseId = await reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(leaseId > 0);
            var callback = Assert.Single(((System.Collections.IDictionary)GetPrivatePropertyValue(context, "ActiveBootstrapSourceCallbacks")!).Values.Cast<object>());
            if (boundary == "install-commit")
            {
                lock (GetServiceGate(service))
                {
                    var staged = StageSparsePumpInstall(service, context, GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"), "callback_capture_commit_race");
                    Assert.True(staged.Accepted, staged.Reason);
                    stagedCallbackInstall = staged.Install;
                }
            }
            if (boundary == "install-reopen")
            {
                stagedCallbackReopen = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
                    .GetMethod("TryInstallOutboundSparsePumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [context, GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"), null,
                        true, "callback_capture_reopen_race", CancellationToken.None]));
                await reopenReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            if (boundary == "supervisor" || boundary.StartsWith("terminal", StringComparison.Ordinal))
            {
                await finalReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod("TryHandleBootstrapCorrelationFrame",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
                    [FileTransferDirection.Outbound, session, new FileTransferReceivedDataFrame(PeerWakeForSourceFailureTest(context),
                        FileTransferTransportKind.RegularNkn, "control", DateTimeOffset.UtcNow)])));
            }
            else if (boundary is not ("unheld" or "unheld_success" or "cancel" or "usercancel")) { session.Receive(PeerWakeForSourceFailureTest(context)); }
            if (boundary is "unheld" or "unheld_success" or "cancel" or "usercancel")
            {
                if (boundary == "cancel") { TryCancelContextLifetime(context); }
                if (boundary == "usercancel")
                {
                    var canceled = await service.CancelTransferAsync(transferId, "test user cancel", CancellationToken.None);
                    Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
                }
                physical.TrySetResult(); release.TrySetResult();
                if (boundary == "unheld_success")
                {
                    await CoreSmokeTestsBase.WaitUntilAsync(() => GetPrivateProperty<int>(context, "ChunksAcceptedForTransport") == 5, TimeSpan.FromSeconds(3));
                    Assert.Empty((System.Collections.IDictionary)GetPrivatePropertyValue(context, "ActiveBootstrapSourceCallbacks")!);
                }
                else { await sending.WaitAsync(TimeSpan.FromSeconds(3)); }
                Assert.Equal(boundary is "unheld" or "usercancel", GetPrivateProperty<bool>(context, "IsTerminal"));
                Assert.Equal(1, sourceOpenCount);
                return;
            }
            await CoreSmokeTestsBase.WaitUntilAsync(() => SourceFailureHoldCapturedForTest(service, context),
                TimeSpan.FromSeconds(3));
            AssertSourceFailureHoldCaptureForTest(context, leaseId, physicalAlreadySettled);
            if (boundary == "physical_failed") { physical.TrySetException(new IOException("controlled physical failure")); }
            else { physical.TrySetResult(); }
            await CoreSmokeTestsBase.WaitUntilAsync(() => bridge.Executions.Count == 1, TimeSpan.FromSeconds(3));
            if (boundary.StartsWith("retired-", StringComparison.Ordinal))
            {
                await AssertCallbackRetiredAfterSupervisorHandoffAsync(service, context, session, bridge, peerWake, callback, release,
                    () => sourceOpenCount, boundary == "retired-next-stop", callbackLogStart,
                    boundary.StartsWith("retired-recovery-", StringComparison.Ordinal) ? boundary : null);
                Assert.False(sending.IsCompleted);
                return;
            }
            if (boundary == "receive-gap")
            {
                await AssertCallbackReceiveGapCannotInstallAsync(service, context, session, release, () => sourceOpenCount);
                Assert.False(sending.IsCompleted);
                return;
            }
            if (boundary == "install-commit")
            {
                AssertCallbackInstallBoundariesForTest(service, context, session, stagedCallbackInstall!);
            }
            if (boundary == "install-reopen")
            {
                reopenRelease.SetResult();
                await stagedCallbackReopen!.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Null(stagedCallbackReopen.GetType().GetProperty("Result")!.GetValue(stagedCallbackReopen));
                Assert.Equal(1, sourceOpenCount);
            }
            lock (GetServiceGate(service)) { MutateSourceFailureScopeForTest(context, session, boundary); }
            var before = SourceFailureEffectsForTest(context);
            release.TrySetResult();
            await CoreSmokeTestsBase.WaitUntilAsync(() => GetPrivateProperty<bool>(context, "IsTerminal") ||
                GetPrivatePropertyValue(context, "ActiveBootstrapSourceCallbacks") is System.Collections.IDictionary { Count: 0 },
                TimeSpan.FromSeconds(3));
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
            Assert.Equal(before, SourceFailureEffectsForTest(context));
            Assert.Equal("Stale", typeof(SessionFileTransferService).GetMethod("ResolveBootstrapSourceCallbackLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, callback])!.ToString());
            Assert.Equal(before, SourceFailureEffectsForTest(context));
            Assert.Equal(1, sourceOpenCount);
            Assert.False(sending.IsCompleted);
            Assert.DoesNotContain(session.SentFrames, frame => frame is FileTransferErrorFrameV4);
        }
        finally
        {
            physical.TrySetResult();
            release.TrySetResult();
            reopenRelease.TrySetResult();
            SessionFileTransferService.BootstrapSourceSendFailureBeforeThrowForTests = previous;
            SessionFileTransferService.OutboundSparseFailureDeferralBarrierForTests = previousSupervisor;
            SessionFileTransferService.OutboundSparseFailureAuthorizationBarrierForTests = previousTerminal;
            SessionFileTransferService.OutboundSparseSendCompletionBarrierForTests = previousCompletion;
            SessionFileTransferService.OutboundSparseSourceReopenBarrierForTests = previousReopen;
            TryCancelContextLifetime(context);
            if (stagedCallbackReopen is not null) { await stagedCallbackReopen.WaitAsync(TimeSpan.FromSeconds(3)); }
            if (sending is not null) { await sending.WaitAsync(TimeSpan.FromSeconds(3)); }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void MutateSourceFailureScopeForTest(object context, SourceFailureDataSession session, string boundary)
    {
        if (boundary == "terminal-provider") { session.Generation++; }
        if (boundary == "terminal-session") { SetPrivateProperty(context, "DataSessionGeneration", 18L); }
        if (boundary == "terminal-pause")
        { SetPrivateProperty(context, "PullTransportPausedSinceUtc", GetPrivateProperty<DateTimeOffset>(context, "PullTransportPausedSinceUtc").AddTicks(1)); }
        if (boundary == "terminal-owner")
        {
            var owner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
            SetPrivateProperty(context, "CurrentRuntimeOwner", owner with { RuntimeGeneration = owner.RuntimeGeneration + 1 });
        }
        if (boundary is "terminal-publication" or "throw-publication")
        {
            var publication = GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken");
            SetPrivateProperty(context, "CurrentRoutePublicationToken", publication with { PublicationGeneration = publication.PublicationGeneration + 1 });
        }
        if (boundary == "terminal-agreement")
        {
            var state = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState");
            SetPrivateProperty(context, "CoordinatorPersistentState", state with
            { FileTransferRouteTransition = state.FileTransferRouteTransition with { BootstrapAgreement = null } });
        }
    }

    private static object?[] SourceFailureEffectsForTest(object context)
        => new[] { "CurrentRuntimeOwner", "CurrentRoutePublicationToken", "RuntimeUnlockRouteCommitToken", "TerminalGeneration",
            "ChunksAcceptedForTransport", "BytesTransferred", "PullTransportPaused", "PullTransportPausedSinceUtc",
            "PullTransportPauseReason", "PullTransportGraceDeadlineUtc", "PullTransportRebindGeneration",
            "PullTransportResumeRequestPending", "InstalledOutboundSparsePump", "PendingOutboundSparsePumpInstall",
            "V6LastReceiveRecoveryRequestedUtc", "V6EpochLivenessDeferralCount", "V6PeerLivenessRecoveryDeferralCount",
            "V4LastStateEpoch", "StatusMessage" }.Select(name => GetPrivatePropertyValue(context, name)).ToArray();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferFallbackBootstrapStateFrameV6 PeerWakeForSourceFailureTest(object context)
    {
        var transition = CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition;
        var prior = Assert.IsType<FileTransferFallbackBootstrapStateFrameV6>(transition.BootstrapExchange!.RetainedPeerFrame);
        return prior with { SenderRequestId = null,
            ReceiverChallengeId = $"fbc1:{transition.BootstrapAgreement!.Accept!.FallbackBootstrapIssuerId}:2:{new string('c', 64)}" };
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AdoptSourceFailureTunaForTest(SessionFileTransferService service, object context)
    {
        var history = FileTransferCoordinatorTests.CreateCorrelatedOutboundServiceSuccessorHistory(CreateCoordinatorStateForTest(context));
        SetPrivateProperty(context, "CoordinatorPersistentState", history.State.PersistentState);
        typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "ApplyCoordinatorDecisionToContextLocked" && method.GetParameters().Length == 4 &&
                method.GetParameters()[0].ParameterType == context.GetType()).Invoke(null, [context, history, false, false]);
        typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        StartCoordinatorSnapshotStreamForTest(context);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool SourceFailureHoldCapturedForTest(SessionFileTransferService service, object context)
    {
        lock (GetServiceGate(service))
        {
            var state = CreateCoordinatorStateForTest(context);
            return state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold is { Capture: not null } hold &&
                FileTransferCoordinator.IsFallbackBootstrapSourceHoldCurrent(state, hold.Command);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertSourceFailureHoldCaptureForTest(object context, long leaseId, bool physicalAlreadySettled)
    {
        var hold = CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapSourceHold!;
        Assert.NotNull(hold.Capture);
        Assert.Equal(leaseId, Assert.Single(hold.Capture.OutboundCallbacks).LeaseGeneration);
        if (physicalAlreadySettled) { Assert.Empty(hold.Capture.OutboundPhysicalLeases); }
        else { Assert.Equal(leaseId, Assert.Single(hold.Capture.OutboundPhysicalLeases)); }
    }

    private sealed class SourceFailureDataSession(string sessionId, string transferId)
        : InitialBootstrapRecordingDataSession(sessionId, transferId), IFileTransferDataPathAuthoritySession, IFileTransferDataSession
    {
        private readonly Channel<FileTransferReceivedDataFrame> incoming = Channel.CreateUnbounded<FileTransferReceivedDataFrame>();
        public void Receive(FileTransferDataFrame frame) => Assert.True(incoming.Writer.TryWrite(
            new(frame, FileTransferTransportKind.RegularNkn, "control", DateTimeOffset.UtcNow)));
        ValueTask<FileTransferReceivedDataFrame> IFileTransferDataSession.ReceiveWithMetadataAsync(CancellationToken ct)
            => incoming.Reader.ReadAsync(ct);
        async ValueTask<FileTransferDataFrame> IFileTransferDataSession.ReceiveAsync(CancellationToken ct)
            => (await incoming.Reader.ReadAsync(ct)).Frame;
        public Task SendAsync(FileTransferDataFrame frame, FileTransferDataPathSendAuthority authority, CancellationToken ct)
            => SubmitPhysicalSend(frame, authority, ct).DeliveryTask;
    }
}
