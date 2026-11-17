using System.Reflection;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Fact]
    public Task JoinedBootstrapCorrelation_RealServicesNegotiateWithProductionCapability()
        => RunJoinedBootstrapCorrelationAsync(runExchange: false);

    [Fact]
    public Task JoinedBootstrapCorrelation_RealServicesPublishFallbackAfterTransportReceipts()
        => RunJoinedBootstrapCorrelationAsync(runExchange: true);

    [Fact]
    public Task JoinedBootstrapCorrelation_ReceiverOnlyStopPublishesFallbackAfterTransportReceipts()
        => RunJoinedBootstrapCorrelationAsync(runExchange: true, stopMode: 1);

    [Fact]
    public Task JoinedBootstrapCorrelation_DualStopPublishesFallbackAfterTransportReceipts()
        => RunJoinedBootstrapCorrelationAsync(runExchange: true, stopMode: 2);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public Task JoinedBootstrapCorrelation_HeaderStopPublishesFallbackAfterTransportReceipts(int stopMode)
        => RunJoinedBootstrapCorrelationAsync(runExchange: true, stopMode, stopReason: "header_switch_off");

    private async Task RunJoinedBootstrapCorrelationAsync(bool runExchange, int stopMode = 0, string stopReason = "runtime_disabled",
        JoinedFallbackFiles? files = null, RetainedRuntimeReadProbe? runtimeReads = null,
        NativeQueuedPumpProbe? queuedPump = null)
    {
        FakeNknClient.ResetNetwork();
        // The full-size file case is a correctness check, not a throughput gate.
        // Existing small route tests retain their forty-second deadline.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(files?.ByteCount > 64 * 1024 ? 180 : 40));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logStart = GetOperationalLogLength();
        var streamOpens = 0;
        var priorBarrier = SessionFileTransferService.OutboundSparseBootstrapBarrierForTests;
        var transferId = queuedPump?.Native.Descriptor.TransferId ?? runtimeReads?.NativeDescriptor?.TransferId ?? "joined-bootstrap-correlation";
        SessionFileTransferService.OutboundSparseBootstrapBarrierForTests = (id, _, stage) =>
        {
            if (id != transferId || stage != "session_open_completion") { return Task.CompletedTask; }
            opened.TrySetResult();
            return release.Task.WaitAsync(timeout.Token);
        };
        try
        {
            var hostClient = new FakeNknClient("host.joined-bootstrap.address");
            var helperClient = new FakeNknClient("helper.joined-bootstrap.address");
            var options = NknTransportOptions.Load();
            var hostIdentity = new NknIdentity("host-joined-bootstrap", hostClient.Address);
            var helperIdentity = new NknIdentity("helper-joined-bootstrap", helperClient.Address);
            // Never connected; transport lifecycle owns registration, Core authorizes pressure commands.
            using var hostPressure = new RealNknClientAdapter(hostIdentity, options);
            using var helperPressure = new RealNknClientAdapter(helperIdentity, options);
            var hostLane = new RetryableTunaAccelerationSession(true, 0, supportedLanes: NknAccelerationLaneKind.File);
            var helperLane = new RetryableTunaAccelerationSession(false, 0, supportedLanes: NknAccelerationLaneKind.File);
            hostLane.ForwardBytesForTests = (lane, bytes, ct) => Forward(helperLane, lane, bytes, ct);
            helperLane.ForwardBytesForTests = (lane, bytes, ct) => Forward(hostLane, lane, bytes, ct);
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity,
                NknTunaAccelerationOptions.Disabled, hostLane, hostPressure);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity,
                NknTunaAccelerationOptions.Disabled, helperLane, helperPressure);
            var sessionId = await ApproveNknSessionAsync(host, helper, timeout.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
            Assert.True(((IFileTransferRouteTransitionBridge)host).SupportsFallbackBootstrapCorrelation);
            Assert.True(((IFileTransferRouteTransitionBridge)helper).SupportsFallbackBootstrapCorrelation);
            using var sender = runtimeReads is null && queuedPump is null ? new SessionFileTransferService() : SessionFileTransferService.CreateForApp();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(host);
            receiver.AttachTransport(helper);
            queuedPump?.Attach(sender, hostClient, helperClient, timeout.Token);
            await sender.TryStartSendAsync(queuedPump?.Native.Descriptor ?? runtimeReads?.NativeDescriptor ?? new("joined-bootstrap.bin", runtimeReads is not null ? 268435456 : files?.ByteCount ?? 64 * 1024, transferId),
                queuedPump?.Native.OpenReadStreamAsync ?? (_ => runtimeReads is not null ? runtimeReads.OpenAsync(releaseReads.Task) : Task.FromResult<Stream>(files is not null
                    ? files.OpenSource(releaseReads.Task)
                    : runExchange && Interlocked.Increment(ref streamOpens) > 1
                        ? new JoinedBootstrapReadGate(new byte[64 * 1024], releaseReads.Task)
                        : new MemoryStream(new byte[64 * 1024], writable: false))), timeout.Token);
            runtimeReads?.Bind(sender);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
                TimeSpan.FromSeconds(3));
            var outbound = GetPrivateField(sender, "outboundTransfer")!;
            var inbound = GetPrivateField(receiver, "inboundTransfer")!;
            await receiver.AcceptIncomingTransferAsync(transferId,
                (_, _) => Task.FromResult<Stream>(files?.OpenDestination() ?? new MemoryStream()), timeout.Token);
            await opened.Task.WaitAsync(timeout.Token);
            await WaitUntilAsync(() => SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(receiver, inbound)
                .PersistentState.FileTransferRouteTransition.BootstrapAgreement?.IsEnabled == true, TimeSpan.FromSeconds(3));
            AssertJoinedBootstrapAgreement(sender, outbound, receiver, inbound, sessionId, host, helper);
            if (runExchange)
            {
                release.TrySetResult();
                await WaitFor("regular runtime startup", () =>
                    State(sender, outbound).CurrentRoutePublicationToken is not null &&
                    State(receiver, inbound).CurrentRoutePublicationToken is not null &&
                    Assert.IsType<bool>(ContextValue(inbound, "PullManifestReceived")));
                // This is the ordinary user-requested activation path. No authority or receipt is seeded.
                await host.RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token);
                await WaitFor("initial Tuna publication", () => Published(sender, outbound, FileTransferRoute.FileTunaV4) &&
                    Published(receiver, inbound, FileTransferRoute.FileTunaV4));
                if (queuedPump is not null) { await queuedPump.WaitForTunaSendAsync(); }
                // Only exact local reasons may capture a current source; remote/unknown observations cannot.
                foreach (var transport in new[] { host, helper })
                {
                    var capture = typeof(NknSignalingTransport)
                        .GetMethod("CaptureFallbackBootstrapLocalStopSources", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    foreach (var rejected in new[] { "remote_header_switch_off", "remote_runtime_disabled", "unknown", "HEADER_SWITCH_OFF" })
                    {
                        Assert.Empty((Array)capture.Invoke(transport, [rejected])!);
                    }
                    Assert.NotEmpty((Array)capture.Invoke(transport, [stopReason])!);
                }
                if (queuedPump is not null)
                {
                    var stopping = host.StopAccelerationAsync(stopReason, timeout.Token);
                    await queuedPump.RetireHeldCallbackAsync();
                    await stopping;
                }
                else if (stopMode != 1) { await host.StopAccelerationAsync(stopReason, timeout.Token); }
                if (stopMode != 0) { await helper.StopAccelerationAsync(stopReason, timeout.Token); }
                if (runtimeReads?.ExpectRejection == true)
                {
                    await runtimeReads.Rejected.Task.WaitAsync(timeout.Token);
                    // The factory rejects before Core finishes delivering its
                    // failure. Join that real terminal boundary before the app
                    // seals its writer; disposal is not a delivery receipt.
                    await WaitFor("retained source failure delivered", () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed &&
                        receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed);
                    Assert.Equal("read_failed", sender.Snapshot.Outbound!.ErrorCode);
                    Assert.Equal("read_failed", receiver.Snapshot.Inbound!.ErrorCode);
                    SessionFileTransferV4SenderTests.CancelJoinedNknContextForTest(outbound);
                    SessionFileTransferV4SenderTests.CancelJoinedNknContextForTest(inbound);
                    return;
                }
                // Local-stop correlation uses its control pumps, not ordinary data traffic.
                // Keep the source read held so synchronous mock send failures cannot starve peer receive.
                if (queuedPump is not null) { await queuedPump.VerifySuccessorAsync(); }
                await WaitFor("correlated fallback publication", () => Published(sender, outbound, FileTransferRoute.PostTunaFallbackV6) &&
                    Published(receiver, inbound, FileTransferRoute.PostTunaFallbackV6));
                AssertJoinedBootstrapFallbackPublished(sender, outbound, receiver, inbound);
                releaseReads.TrySetResult();
                if (runtimeReads is not null)
                {
                    await runtimeReads.ReadObserved.Task.WaitAsync(timeout.Token);
                    await WaitFor("held source fallback receive", () => receiver.Snapshot.Inbound?.BytesTransferred > 0);
                    runtimeReads.VerifyBeforeRetirement(sender.Snapshot.Outbound!, receiver.Snapshot.Inbound!,
                        Assert.IsType<MemoryStream>(ContextValue(inbound, "WriteStream")));
                }
                if (files is not null)
                {
                    // Route-edge waits stay at eight seconds. File completion
                    // uses only this fixture's overall cancellation deadline.
                    while (sender.Snapshot.Outbound?.IsTerminal != true || receiver.Snapshot.Inbound?.IsTerminal != true)
                        await Task.Delay(10, timeout.Token);
                    await files.VerifyAsync(sender.Snapshot.Outbound!, receiver.Snapshot.Inbound!, sessionId, transferId, timeout.Token);
                }
            }
            SessionFileTransferV4SenderTests.CancelJoinedNknContextForTest(outbound);
            SessionFileTransferV4SenderTests.CancelJoinedNknContextForTest(inbound);
            runtimeReads?.VerifyRetired();

            async Task WaitFor(string edge, Func<bool> predicate)
            {
                try { await WaitUntilAsync(predicate, TimeSpan.FromSeconds(8)); }
                catch (TimeoutException error)
                {
                    var summaries = new[] { Diagnostic(sender, outbound), Diagnostic(receiver, inbound) };
                    throw new TimeoutException($"Joined bootstrap edge: {edge}; " + System.Text.Json.JsonSerializer.Serialize(summaries) +
                        Environment.NewLine + string.Join(Environment.NewLine, ReadOperationalLogTail(logStart).Split('\n')
                            .Where(line => !line.Contains("snapshot_payload=", StringComparison.Ordinal) &&
                                (line.Contains("event=filetransfer_bootstrap", StringComparison.Ordinal) ||
                                 line.Contains("event=filetransfer_fallback", StringComparison.Ordinal) ||
                                 line.Contains("event=filetransfer_route_transition", StringComparison.Ordinal)))
                            .TakeLast(20)), error);
                }
            }
        }
        finally
        {
            timeout.Cancel();
            release.TrySetResult();
            releaseReads.TrySetResult();
            queuedPump?.Dispose();
            SessionFileTransferService.OutboundSparseBootstrapBarrierForTests = priorBarrier;
            FakeNknClient.ResetNetwork();
        }

        static FileTransferCoordinatorState State(SessionFileTransferService service, object context)
            => SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
        static object Diagnostic(SessionFileTransferService service, object context)
        {
            lock (GetPrivateField(service, "gate")!)
            {
                var state = State(service, context);
                var exchange = state.PersistentState.FileTransferRouteTransition.BootstrapExchange;
                var phase = exchange?.Final ?? exchange?.First;
                FileTransferFallbackV6ProofBootstrapPeerFrameBinding? current = null;
                if (phase is not null)
                {
                    object?[] args = [context, phase.Command.Frame, state.Direction, null];
                    typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                        .Single(method => method.Name == "TryCreateFallbackV6ProofBootstrapPeerFrameBinding" &&
                            method.GetParameters()[0].ParameterType == context.GetType()).Invoke(null, args);
                    current = args[3] as FileTransferFallbackV6ProofBootstrapPeerFrameBinding;
                }
                return new
                {
                    state.Direction, Route = state.CurrentRoutePublicationToken?.Route,
                    PublicationGeneration = state.CurrentRoutePublicationToken?.PublicationGeneration,
                    Transition = state.PersistentState.FileTransferRouteTransition.CurrentOperation?.State,
                    exchange?.Completed, exchange?.Invalidated,
                    Origin = exchange?.Bootstrap.Observation.InitiationKind, Phase = phase?.Command.Phase,
                    Delivery = phase?.Delivery?.Reason, Settlement = phase?.Settlement?.Reason, Failure = phase?.Failure?.Reason,
                    Copies = phase?.Settlement?.Copies.Select(copy => new { copy.Lane, copy.FrameIdentity.FrameType, copy.Outcome }),
                    IssuedCheckpoint = exchange?.Bootstrap.Observation.DurableCheckpoint,
                    CurrentCheckpoint = current?.DurableCheckpoint,
                    CurrentBindingPresent = current is not null,
                    DataSessionGeneration = state.CurrentDataSessionGeneration,
                    PublicationCurrent = state.CurrentRoutePublicationToken is { } publication &&
                        FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, publication, out _),
                };
            }
        }
        static object? ContextValue(object context, string name)
            => context.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(context);
        static bool Published(SessionFileTransferService service, object context, FileTransferRoute route)
            => State(service, context) is { CurrentRoutePublicationToken: { } publication } state && publication.Route == route &&
               state.PersistentState.FileTransferRouteTransition.LastTerminalOperation is
                   { DispositionReceipt.Status: FileTransferRouteTransitionDispositionApplyStatus.Applied };
        static async Task<bool> Forward(RetryableTunaAccelerationSession target, NknBridgeChannel lane, byte[] bytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!target.IsAvailable) { return false; }
            await Task.Yield();
            target.ReceivePeerBytesForTests(lane, bytes);
            return true;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertJoinedBootstrapAgreement(
        SessionFileTransferService sender, object outbound,
        SessionFileTransferService receiver, object inbound, string sessionId,
        NknSignalingTransport host, NknSignalingTransport helper)
    {
        var senderAgreement = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound)
            .PersistentState.FileTransferRouteTransition.BootstrapAgreement!;
        var receiverAgreement = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(receiver, inbound)
            .PersistentState.FileTransferRouteTransition.BootstrapAgreement!;
        Assert.True(senderAgreement.IsEnabled);
        Assert.True(receiverAgreement.IsEnabled);
        Assert.Equal(senderAgreement.Offer, receiverAgreement.Offer);
        Assert.Equal(senderAgreement.Accept, receiverAgreement.Accept);
        Assert.Equal(sessionId, senderAgreement.Offer.SessionId);
        Assert.False(senderAgreement.ReceiverSessionOpenValidated);
        Assert.True(receiverAgreement.ReceiverSessionOpenValidated);
        Assert.True(((IFileTransferRouteTransitionBridge)host).SupportsFallbackBootstrapCorrelation);
        Assert.True(((IFileTransferRouteTransitionBridge)helper).SupportsFallbackBootstrapCorrelation);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertJoinedBootstrapFallbackPublished(
        SessionFileTransferService sender, object outbound,
        SessionFileTransferService receiver, object inbound)
    {
        foreach (var state in new[] { State(sender, outbound), State(receiver, inbound) })
        {
            var exchange = Assert.IsType<FileTransferBootstrapExchange>(state.PersistentState.FileTransferRouteTransition.BootstrapExchange);
            Assert.True(exchange.Completed);
            Assert.True(exchange.First.IsReady);
            Assert.NotNull(state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold?.Receipt);
            Assert.Equal(exchange.SourceHoldCommand,
                state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold!.Command);
            if (state.Direction == FileTransferDirection.Outbound)
            {
                Assert.Equal(FileTransferBootstrapPhase.Confirm, exchange.Final?.Command.Phase);
                Assert.IsType<FileTransferFallbackBootstrapStateFrameV6>(exchange.RetainedPeerFrame);
            }
            if (exchange.Final is { } final) { Assert.True(final.IsReady); }
            var transition = state.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
            Assert.Equal(FileTransferRouteTransitionState.Committed, transition.State);
            Assert.Equal(transition.Authority, transition.DispositionReceipt!.Value.Command.Authority);
            Assert.Equal(state.CurrentRoutePublicationToken, transition.RoutePublicationToken);
            Assert.Equal(transition.RoutePublicationToken, transition.DispositionReceipt.Value.Command.RoutePublicationToken);
            var pressure = FileTransferCoordinator.GetRegularV4FrontierRepairPressureSubstate(state);
            Assert.False(pressure.HasBinding);
            if (state.Direction == FileTransferDirection.Inbound && pressure.LastInstallGeneration == 0)
            {
                // A source hold can precede frontier repair; no install means no settlement authority.
                Assert.Equal(RegularV4FrontierRepairPressureSubstate.Empty, pressure);
            }
            else if (state.Direction == FileTransferDirection.Inbound)
            {
                Assert.True(pressure.LastInstallGeneration > 0);
                var settlement = Assert.IsType<FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt>(
                    pressure.LastSettlementApplyReceipt);
                Assert.Equal(FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied, settlement.Status);
                Assert.True(settlement.CurrentAuthorityCleared);
                Assert.Equal(pressure.LastSettlement, settlement.Command.Settlement);
                Assert.Equal(FileTransferDirection.Inbound, settlement.Command.Settlement.Direction);
                Assert.Equal(state.SessionId, settlement.Command.Settlement.SessionId);
                Assert.Equal(state.TransferId, settlement.Command.Settlement.TransferId);
            }
            Assert.Equal(6, state.CurrentRoutePublicationToken!.Value.ProtocolVersion);
            Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, state.CurrentRoutePublicationToken.Value, out var reason), reason);
        }

        static FileTransferCoordinatorState State(SessionFileTransferService service, object context)
            => SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
    }

    private sealed class JoinedBootstrapReadGate(byte[] bytes, Task release) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await release.WaitAsync(ct);
            return await base.ReadAsync(buffer, ct);
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await release.WaitAsync(ct);
            return await base.ReadAsync(buffer, offset, count, ct);
        }
    }
}
