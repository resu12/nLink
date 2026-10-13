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

    private async Task RunJoinedSenderDialerRetryAsync(string? control = null, bool senderCanListen = false)
    {
        // Only the already-open fallback baseline is seeded. The new payer's
        // handshake, paired yield, retry install, probe and payload are real
        // exchanges between the two Services and authenticated transports.
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var releaseYield = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInstall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousInstallBarrier = NknSignalingTransport.RuntimeUnlockRetryDispatchInstallFollowUpBarrierForTests;
        NknSignalingTransport.RuntimeUnlockRetryDispatchInstallFollowUpBarrierForTests = () => releaseInstall.Task;
        var payload = new TaskCompletionSource<FileTransferReceivedDataFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        object? outbound = null, inbound = null;
        Task[] pumps = [];
        var logStart = GetOperationalLogLength();
        var hostClient = new FakeNknClient("host.sender-dialer.address");
        var helperClient = new FakeNknClient("helper.sender-dialer.address");
        var listener = new RetryableTunaAccelerationSession(true, 0, NknAccelerationLaneKind.File);
        var dialer = new RetryableTunaAccelerationSession(senderCanListen, 0, NknAccelerationLaneKind.File)
        {
            StrictPayerHandoffForTests = true,
        };
        listener.ForwardBytesForTests = (lane, bytes, ct) => Forward(dialer, lane, bytes, ct);
        dialer.ForwardBytesForTests = (lane, bytes, ct) => Forward(listener, lane, bytes, ct);
        var options = NknTransportOptions.Load();
        using var host = new NknSignalingTransport(hostClient, options,
            new NknIdentity("host-sender-dialer", hostClient.Address), NknTunaAccelerationOptions.Disabled, listener);
        using var helper = new NknSignalingTransport(helperClient, options,
            new NknIdentity("helper-sender-dialer", helperClient.Address), NknTunaAccelerationOptions.Disabled, dialer);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        try
        {
            var sessionId = await ApproveNknSessionAsync(host, helper, timeout.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
            const string transferId = "transfer_sender_dialer_retry";
            var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += (_, _) => offered.TrySetResult();
            helper.FileTransferAcceptReceived += (_, _) => accepted.TrySetResult();
            host.FileTransferSessionOpenReceived += (_, _) => opened.TrySetResult();
            await helper.SendFileTransferOfferAsync(new FileTransferOfferV2
            {
                SessionId = sessionId, TransferId = transferId, FileName = transferId + ".bin",
                FileSizeBytes = 256L * 21 * 1024, PreferredDataProtocolVersion = 6,
                FileTransferRoute = FileTransferRouteResolver.PostTunaFallbackV6Token,
            }, timeout.Token);
            await offered.Task.WaitAsync(timeout.Token);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1
            {
                SessionId = sessionId, TransferId = transferId, AcceptedDataProtocolVersion = 6,
            }, timeout.Token);
            await accepted.Task.WaitAsync(timeout.Token);
            await helper.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2
            {
                SessionId = sessionId, TransferId = transferId, ProtocolVersion = 6,
                SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = 21 * 1024, InitialPipelineDepth = 8,
            }, timeout.Token);
            await opened.Task.WaitAsync(timeout.Token);
            sender.AttachTransport(helper);
            receiver.AttachTransport(host);
            var senderData = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, timeout.Token);
            var receiverData = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, timeout.Token);
            outbound = await SessionFileTransferV4SenderTests.CreateJoinedNknFallbackContextForTestAsync(
                sender, senderData, sessionId, transferId, FileTransferDirection.Outbound, withoutFrontierGap: true);
            inbound = await SessionFileTransferV4SenderTests.CreateJoinedNknFallbackContextForTestAsync(
                receiver, receiverData, sessionId, transferId, FileTransferDirection.Inbound, withoutFrontierGap: true);

            await host.RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token);
            await WaitUntilAsync(() => GetPrivateField(helper, "runtimeUnlockResponderAnswerProbeConvergence") is not null,
                TimeSpan.FromSeconds(6));
            Assert.NotNull(dialer.CaptureCurrentLaneLease()?.DialerOwner);
            Assert.Null(dialer.CaptureCurrentLaneLease()?.Listener);
            Assert.True(listener.IsLocalPaidListenerActive);

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
            await WaitUntilAsync(() => GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(helper).HasValue,
                TimeSpan.FromSeconds(3));
            if (control == "expired_handshake")
            {
                var handshake = GetPrivateField(helper, "runtimeUnlockResponderAnswerProbeConvergence")!;
                handshake.GetType().GetProperty("AnswerDeadlineUtcMs")!.SetValue(handshake,
                    DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds());
            }
            else if (control == "replaced_transaction")
            {
                var transaction = Assert.IsType<RuntimeUnlockTransactionSnapshot>(GetPrivateField(helper, "runtimeUnlockTransactionState"));
                SetPrivateField(helper, "runtimeUnlockTransactionState", transaction with { OfferGeneration = transaction.OfferGeneration + 1 });
            }
            else if (control == "replaced_dialer")
            {
                var oldLease = dialer.CaptureCurrentLaneLease()!;
                Assert.True(await dialer.StartDialerSidecarAsync(listener.LocalTunaAddress!, hostClient.Address, timeout.Token));
                oldLease.Client.Dispose();
                oldLease.DialerOwner!.Dispose();
            }
            releaseInstall.TrySetResult();
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
                line.Contains("precommit", StringComparison.Ordinal) ||
                line.Contains("answer_rejected", StringComparison.Ordinal)).TakeLast(15);
            throw new TimeoutException(string.Join("\n", events), error);
        }
        finally
        {
            if (outbound is not null) SessionFileTransferV4SenderTests.CancelJoinedNknContextForTest(outbound);
            if (inbound is not null) SessionFileTransferV4SenderTests.CancelJoinedNknContextForTest(inbound);
            releaseYield.TrySetResult();
            releaseInstall.TrySetResult();
            timeout.Cancel();
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
