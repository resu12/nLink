using System.Reflection;
using System.Security.Cryptography;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SenderDialerRetry")]
    public Task JoinedFallbackRetry_HelperSenderUsesAuthenticatedHelpeeListener(bool senderCanListen)
        => RunJoinedSenderDialerRetryAsync(senderCanListen: senderCanListen);

    [Theory]
    [InlineData("expired_handshake")]
    [InlineData("replaced_transaction")]
    [InlineData("replaced_dialer")]
    [Trait("FileTransferInfraSuite", "SenderDialerRetry")]
    public Task JoinedFallbackRetry_SenderDialerRejectsStaleHandshake(string control)
        => RunJoinedSenderDialerRetryAsync(control);

    [Fact]
    [Trait("FileTransferInfraSuite", "SenderDialerRetry")]
    public Task JoinedFallbackRetry_HelperOnlyReenable_PreparesColdHelpeeDialer()
        => RunJoinedSenderDialerRetryAsync(helperOnlyCold: true);

    [Fact]
    [Trait("FileTransferInfraSuite", "SenderDialerRetry")]
    public Task JoinedFallbackRetry_SecondTransferKeepsProviderAndLocalGenerationsDistinct()
        => RunJoinedSenderDialerRetryAsync(helperOnlyCold: true, verifyInstalledListenerAnswer: true,
            completeConsumedResponderHandoff: true, secondTransfer: true);

    [Fact]
    [Trait("FileTransferInfraSuite", "SenderDialerRetry")]
    public Task JoinedFallbackRetry_InstalledListenerAnswerConvergesWithPeerProbe()
        => RunJoinedSenderDialerRetryAsync(helperOnlyCold: true, verifyInstalledListenerAnswer: true);

    [Fact]
    [Trait("FileTransferInfraSuite", "SenderDialerRetry")]
    public Task JoinedFallbackRetry_ConsumedResponderReleasePublishesBothTunaRoutesAndPayload()
        => RunJoinedSenderDialerRetryAsync(helperOnlyCold: true, verifyInstalledListenerAnswer: true,
            completeConsumedResponderHandoff: true);

    [Theory]
    [InlineData("cold_data_session_replaced")]
    [InlineData("cold_terminal")]
    [Trait("FileTransferInfraSuite", "SenderDialerRetry")]
    public Task JoinedFallbackRetry_ColdDialerRevalidatesAfterPreparation(string control)
        => RunJoinedSenderDialerRetryAsync(control, helperOnlyCold: true);

    private async Task RunJoinedSenderDialerRetryAsync(string? control = null, bool senderCanListen = false,
        bool helperOnlyCold = false, bool verifyInstalledListenerAnswer = false, string? listenerAnswerControl = null,
        bool completeConsumedResponderHandoff = false, bool secondTransfer = false)
    {
        // Only the already-open fallback baseline is seeded. The new payer's
        // handshake, paired yield, retry install, probe and payload are real
        // exchanges between the two Services and authenticated transports.
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var releaseYield = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInstall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDial = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var installFollowUps = 0;
        var previousInstallBarrier = NknSignalingTransport.RuntimeUnlockRetryDispatchInstallFollowUpBarrierForTests;
        NknSignalingTransport.RuntimeUnlockRetryDispatchInstallFollowUpBarrierForTests = () =>
            helperOnlyCold && Interlocked.Increment(ref installFollowUps) == 1
                ? Task.CompletedTask : releaseInstall.Task;
        var payload = new TaskCompletionSource<FileTransferReceivedDataFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        object? outbound = null, inbound = null;
        Task[] pumps = [];
        var logStart = GetOperationalLogLength();
        var hostClient = new FakeNknClient("host.sender-dialer.address");
        var helperClient = new FakeNknClient("helper.sender-dialer.address");
        var listener = new RetryableTunaAccelerationSession(true, 0, NknAccelerationLaneKind.File);
        var dialer = new RetryableTunaAccelerationSession(senderCanListen, 0, NknAccelerationLaneKind.File,
            dialAttemptOutcomes: helperOnlyCold && control is not null ? [releaseDial] : null)
        {
            StrictPayerHandoffForTests = true,
        };
        listener.ForwardBytesForTests = (lane, bytes, ct) => Forward(dialer, lane, bytes, ct);
        dialer.ForwardBytesForTests = (lane, bytes, ct) => Forward(listener, lane, bytes, ct);
        var options = NknTransportOptions.Load();
        using var host = new NknSignalingTransport(hostClient, options,
            new NknIdentity("host-sender-dialer", hostClient.Address), NknTunaAccelerationOptions.Disabled,
            helperOnlyCold ? dialer : listener);
        using var helper = new NknSignalingTransport(helperClient, options,
            new NknIdentity("helper-sender-dialer", helperClient.Address), NknTunaAccelerationOptions.Disabled,
            helperOnlyCold ? listener : dialer);
        var senderTransport = helperOnlyCold ? host : helper;
        var receiverTransport = helperOnlyCold ? helper : host;
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        try
        {
            var sessionId = await ApproveNknSessionAsync(host, helper, timeout.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
            if (secondTransfer)
            {
                // A preceding transfer advances the transport-wide provider counter,
                // not the new transfer's service-local lifetime.
                (await host.OpenFileTransferDataSessionAsync(sessionId, "previous-transfer", timeout.Token)).Dispose();
                (await helper.OpenFileTransferDataSessionAsync(sessionId, "previous-transfer", timeout.Token)).Dispose();
            }
            const string transferId = "transfer_sender_dialer_retry";
            var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            receiverTransport.FileTransferOfferReceived += (_, _) => offered.TrySetResult();
            senderTransport.FileTransferAcceptReceived += (_, _) => accepted.TrySetResult();
            receiverTransport.FileTransferSessionOpenReceived += (_, _) => opened.TrySetResult();
            await senderTransport.SendFileTransferOfferAsync(new FileTransferOfferV2
            {
                SessionId = sessionId, TransferId = transferId, FileName = transferId + ".bin",
                FileSizeBytes = 256L * 21 * 1024, PreferredDataProtocolVersion = 6,
                FileTransferRoute = FileTransferRouteResolver.PostTunaFallbackV6Token,
            }, timeout.Token);
            await offered.Task.WaitAsync(timeout.Token);
            await receiverTransport.SendFileTransferAcceptAsync(new FileTransferAcceptV1
            {
                SessionId = sessionId, TransferId = transferId, AcceptedDataProtocolVersion = 6,
            }, timeout.Token);
            await accepted.Task.WaitAsync(timeout.Token);
            await senderTransport.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2
            {
                SessionId = sessionId, TransferId = transferId, ProtocolVersion = 6,
                SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = 21 * 1024, InitialPipelineDepth = 8,
            }, timeout.Token);
            await opened.Task.WaitAsync(timeout.Token);
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            var senderData = await senderTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, timeout.Token);
            var receiverData = await receiverTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, timeout.Token);
            outbound = await SessionFileTransferV4SenderTests.CreateJoinedNknFallbackContextForTestAsync(
                sender, senderData, sessionId, transferId, FileTransferDirection.Outbound, withoutFrontierGap: true);
            inbound = await SessionFileTransferV4SenderTests.CreateJoinedNknFallbackContextForTestAsync(
                receiver, receiverData, sessionId, transferId, FileTransferDirection.Inbound, withoutFrontierGap: true);
            if (secondTransfer)
            {
                foreach (var side in new[] { (sender, outbound), (receiver, inbound) })
                {
                    var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(side.Item1, side.Item2);
                    Assert.Equal(1, state.CurrentDataSessionGeneration);
                    Assert.Equal(2, state.CurrentDataSessionProviderGeneration);
                }
            }

            if (!helperOnlyCold)
            {
                await receiverTransport.RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token);
                await WaitUntilAsync(() => GetPrivateField(senderTransport, "runtimeUnlockResponderAnswerProbeConvergence") is not null,
                    TimeSpan.FromSeconds(6));
                Assert.NotNull(dialer.CaptureCurrentLaneLease()?.DialerOwner);
                Assert.Null(dialer.CaptureCurrentLaneLease()?.Listener);
                Assert.True(listener.IsLocalPaidListenerActive);
            }
            else
            {
                InvokePrivateMethod(senderTransport, "MarkAccelerationUserStoppedForCurrentSession", sessionId);
                Assert.Null(dialer.CaptureCurrentLaneLease());
            }

            foreach (var client in new[] { hostClient, helperClient })
                client.BeforeSendAsync = async (_, bytes, ct) =>
                {
                    if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) && envelope.Type == MsgType.FileTransferDataFrame)
                        await releaseYield.Task.WaitAsync(ct);
                };
            SessionFileTransferV4SenderTests.IssueJoinedNknYieldForTest(sender, outbound, FileTransferDirection.Outbound, 99);
            SessionFileTransferV4SenderTests.IssueJoinedNknYieldForTest(receiver, inbound, FileTransferDirection.Inbound, 99);
            releaseYield.TrySetResult();
            pumps = [Pump(sender, senderData, outbound, FileTransferDirection.Outbound),
                Pump(receiver, receiverData, inbound, FileTransferDirection.Inbound)];
            if (helperOnlyCold)
            {
                // Let the receiver's installed retry send its offer, but hold
                // the sender's install follow-up at the preparation boundary.
                await WaitUntilAsync(() => GetPrivateField(senderTransport, "pendingRuntimeUnlockRetryNegotiationIntent") is not null,
                    TimeSpan.FromSeconds(3));
                Assert.Null(GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(senderTransport));
                if (verifyInstalledListenerAnswer)
                {
                    // Both yield frames must be applied before enabling the
                    // listener; otherwise this tests InitialActivation instead.
                    await WaitUntilAsync(() => new[] { (sender, outbound), (receiver, inbound) }.All(side =>
                        SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(side.Item1, side.Item2)
                            .PersistentState.FallbackTailYield.CurrentOperation?.PairedRelease is not null),
                        TimeSpan.FromSeconds(3));
                    // Pairing publication precedes the queued coordinator retry request.
                    // Finish that phase before asking the listener to negotiate.
                    await WaitUntilAsync(() =>
                        GetPrivateField(receiverTransport, "pendingRuntimeUnlockRetryNegotiationIntent") is not null ||
                        GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(receiverTransport).HasValue,
                        TimeSpan.FromSeconds(3));
                    await receiverTransport.DrainCoordinatorQueuedRuntimeUnlockRetryDispatchAuthorityRequestsForTestsAsync()
                        .WaitAsync(timeout.Token);
                }
                await receiverTransport.RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token);
                if (control is not null)
                {
                    await WaitUntilAsync(() => dialer.StartDialerCalls == 1, TimeSpan.FromSeconds(8));
                    lock (GetPrivateField(sender, "gate")!)
                    {
                        var property = outbound.GetType().GetProperty(control == "cold_terminal" ? "State" : "DataSessionGeneration")!;
                        property.SetValue(outbound, control == "cold_terminal"
                            ? (object)FileTransferTransferState.Failed
                            : (long)property.GetValue(outbound)! + 1);
                    }
                    releaseDial.TrySetResult(true);
                    await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains(
                        "outbound_dialer_preparation_source_not_current", StringComparison.Ordinal), TimeSpan.FromSeconds(3));
                    Assert.Null(GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(senderTransport));
                    Assert.Null(GetPrivateField(senderTransport, "runtimeUnlockResponderAnswerProbeConvergence"));
                    Assert.Equal(FileTransferRoute.PostTunaFallbackV6,
                        SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound)
                            .CurrentRoutePublicationToken!.Value.Route);
                    Assert.False(payload.Task.IsCompleted);
                    return;
                }
                await WaitUntilAsync(() => GetPrivateField(senderTransport, "runtimeUnlockResponderAnswerProbeConvergence") is not null,
                    TimeSpan.FromSeconds(8));
            }
            // Installation includes the existing retry queue/backoff. Bound it by the
            // fixture's overall deadline instead of a competing three-second poll.
            while (!GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(senderTransport).HasValue)
                await Task.Delay(10, timeout.Token);
            if (helperOnlyCold)
            {
                var installed = GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(senderTransport)!.Value;
                var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound);
                Assert.Equal(installed, FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(state).InstalledAttempt?.InstallReceipt);
                Assert.Equal(FileTransferDirection.Outbound, installed.Authority.Direction);
                Assert.Equal(RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield, installed.Authority.ReleaseKind);
                Assert.NotNull(dialer.CaptureCurrentLaneLease()?.DialerOwner);
                Assert.Equal(1, dialer.StartDialerCalls);
                Assert.Equal(0, dialer.EnsureListenerCalls);
                Assert.Equal(0, dialer.ListenerAdoptionCalls);
                Assert.False(dialer.IsLocalPaidListenerActive);
                // Preparation and even the installed grant cannot publish Tuna.
                // The separate authenticated probe/publication gates remain ahead.
                Assert.Equal(FileTransferRoute.PostTunaFallbackV6, state.CurrentRoutePublicationToken!.Value.Route);
                Assert.Null(state.CurrentRuntimeUnlockPendingPreCommitProbe);
                Assert.False(payload.Task.IsCompleted);
                if (!verifyInstalledListenerAnswer)
                    return;
                var answer = GetPrivateField(receiverTransport, "runtimeUnlockResponderAnswerProbeConvergence")!;
                Assert.NotNull(answer);
                Assert.Equal("InstalledRetry", answer.GetType().GetProperty("AuthorityMode")!.GetValue(answer)!.ToString());
                Assert.Equal("AuthenticatedAnswer", answer.GetType().GetProperty("AcceptanceSource")!.GetValue(answer)!.ToString());
                Assert.NotNull(answer.GetType().GetProperty("ListenerAnswerBarrier")!.GetValue(answer));
                if (listenerAnswerControl is not null)
                {
                    await VerifyInstalledListenerAnswerRejectionAsync(receiverTransport, receiver, inbound,
                        listener, answer, listenerAnswerControl);
                    Assert.False(payload.Task.IsCompleted);
                    return;
                }
            }
            if (control == "expired_handshake")
            {
                var handshake = GetPrivateField(senderTransport, "runtimeUnlockResponderAnswerProbeConvergence")!;
                handshake.GetType().GetProperty("AnswerDeadlineUtcMs")!.SetValue(handshake,
                    DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());
            }
            else if (control == "replaced_transaction")
            {
                var transaction = Assert.IsType<RuntimeUnlockTransactionSnapshot>(GetPrivateField(senderTransport, "runtimeUnlockTransactionState"));
                SetPrivateField(senderTransport, "runtimeUnlockTransactionState", transaction with { OfferGeneration = transaction.OfferGeneration + 1 });
            }
            else if (control == "replaced_dialer")
            {
                var oldLease = dialer.CaptureCurrentLaneLease()!;
                Assert.True(await dialer.StartDialerSidecarAsync(listener.LocalTunaAddress!, hostClient.Address, timeout.Token));
                oldLease.Client.Dispose();
                oldLease.DialerOwner!.Dispose();
            }
            releaseInstall.TrySetResult();
            if (verifyInstalledListenerAnswer)
            {
                // Answer/probe convergence is evidence; Core must separately
                // authorize both publications before authenticated payload.
                await WaitUntilAsync(() =>
                    GetPrivateField(receiverTransport, "runtimeUnlockResponderAnswerProbeConvergence") is { } evidence &&
                    evidence.GetType().GetProperty("State")!.GetValue(evidence)!.ToString() == "Complete" &&
                    ReadOperationalLogTail(logStart).Contains(
                        "event=filetransfer_runtime_unlock_responder_peer_probe_route_transition_handoff_decided;",
                        StringComparison.Ordinal), TimeSpan.FromSeconds(6));
                var evidence = GetPrivateField(receiverTransport, "runtimeUnlockResponderAnswerProbeConvergence")!;
                Assert.Equal("InstalledRetry", evidence.GetType().GetProperty("AuthorityMode")!.GetValue(evidence)!.ToString());
                Assert.Equal("AuthenticatedAnswer", evidence.GetType().GetProperty("AcceptanceSource")!.GetValue(evidence)!.ToString());
                Assert.NotNull(evidence.GetType().GetProperty("PeerReceipt")!.GetValue(evidence));
                var transaction = Assert.IsType<RuntimeUnlockTransactionSnapshot>(
                    GetPrivateField(receiverTransport, "runtimeUnlockTransactionState"));
                Assert.Equal(RuntimeUnlockPathProbeState.Acked, transaction.PathProbeState);
                Assert.Equal(FileTransferTransportKind.Tuna, transaction.PathProbeTransport);
                Assert.False(payload.Task.IsCompleted);
                if (!completeConsumedResponderHandoff)
                    return;
            }
            if (control is not null)
            {
                await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains(
                    "event=runtime_unlock_sender_dialer_continuation; decision_status=Held;", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(3));
                Assert.DoesNotContain("event=runtime_unlock_sender_dialer_continuation; decision_status=Requested;",
                    ReadOperationalLogTail(logStart), StringComparison.Ordinal);
                foreach (var side in new[] { (sender, outbound), (receiver, inbound) })
                    Assert.Equal(FileTransferRoute.PostTunaFallbackV6,
                        SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(side.Item1, side.Item2)
                            .CurrentRoutePublicationToken!.Value.Route);
                Assert.False(payload.Task.IsCompleted);
                Assert.Equal(0, dialer.EnsureListenerCalls);
                return;
            }
            await WaitUntilAsync(() => new[] { (sender, outbound), (receiver, inbound) }.All(side =>
                SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(side.Item1, side.Item2)
                    .CurrentRoutePublicationToken is { Route: FileTransferRoute.FileTunaV4, ProtocolVersion: 4 }),
                TimeSpan.FromSeconds(12));

            var publication = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound)
                .CurrentRoutePublicationToken!.Value;
            var authority = Assert.IsType<FileTransferDataPathSendAuthority>(typeof(SessionFileTransferService)
                .GetMethod("CreateDataPathSendAuthority", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [publication, "sender_dialer_retry_regression"]));
            var bytes = Enumerable.Range(0, 21 * 1024).Select(index => (byte)(index * 17)).ToArray();
            await Assert.IsAssignableFrom<IFileTransferDataPathAuthoritySession>(senderData).SendAsync(
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId, TransferId = transferId, StartChunkIndex = 100, ChunkCount = 1,
                    DataSegments = [bytes], BatchProfile = "v4_default_21k",
                }, authority, timeout.Token);
            var delivered = await payload.Task.WaitAsync(timeout.Token);
            Assert.Equal(FileTransferTransportKind.Tuna, delivered.TransportKind);
            Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(
                Assert.Single(Assert.IsType<FileTransferChunkBatchFrameV4>(delivered.Frame).DataSegments)));
            Assert.False(dialer.IsLocalPaidListenerActive);
            Assert.Equal(0, dialer.EnsureListenerCalls);
            Assert.Equal(0, dialer.ListenerAdoptionCalls);

            async Task Pump(SessionFileTransferService service, IFileTransferDataSession data,
                object context, FileTransferDirection direction)
            {
                while (!timeout.IsCancellationRequested)
                {
                    var received = await data.ReceiveWithMetadataAsync(timeout.Token);
                    if (received.Frame is FileTransferFallbackTailYieldFrameV6 yield)
                        SessionFileTransferV4SenderTests.ApplyJoinedNknYieldForTest(service, context, direction, yield);
                    else if (received.Frame is FileTransferRuntimeUnlockPreCommitProbeFrame)
                        Assert.True(await Assert.IsAssignableFrom<Task<bool>>(typeof(SessionFileTransferService)
                            .GetMethod("TryHandleRuntimeUnlockPreCommitProbeDataFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(service, [sessionId, transferId, direction, received.Frame, received.TransportKind, data])));
                    else if (direction == FileTransferDirection.Inbound && received.Frame is FileTransferChunkBatchFrameV4)
                        payload.TrySetResult(received);
                }
            }
        }
        catch (TimeoutException error)
        {
            var events = ReadOperationalLogTail(logStart).Split('\n').Where(line =>
                line.Contains("listener_unavailable", StringComparison.Ordinal) ||
                line.Contains("preparation", StringComparison.Ordinal) ||
                line.Contains("offer_", StringComparison.Ordinal) ||
                line.Contains("payer_intent", StringComparison.Ordinal) ||
                line.Contains("precommit", StringComparison.Ordinal) ||
                line.Contains("answer_rejected", StringComparison.Ordinal)).TakeLast(25);
            throw new TimeoutException(string.Join("\n", events), error);
        }
        finally
        {
            if (outbound is not null) SessionFileTransferV4SenderTests.CancelJoinedNknContextForTest(outbound);
            if (inbound is not null) SessionFileTransferV4SenderTests.CancelJoinedNknContextForTest(inbound);
            releaseYield.TrySetResult();
            timeout.Cancel();
            releaseDial.TrySetCanceled();
            releaseInstall.TrySetResult();
            try { await Task.WhenAll(pumps); }
            catch (OperationCanceledException) { }
            finally
            {
                NknSignalingTransport.RuntimeUnlockRetryDispatchInstallFollowUpBarrierForTests = previousInstallBarrier;
                sender.Dispose();
                receiver.Dispose();
                host.Dispose();
                helper.Dispose();
                FakeNknClient.ResetNetwork();
            }
        }

        static async Task<bool> Forward(RetryableTunaAccelerationSession target,
            NknBridgeChannel lane, byte[] bytes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!target.IsAvailable) return false;
            await Task.Yield();
            target.ReceivePeerBytesForTests(lane, bytes);
            return true;
        }
    }
}
