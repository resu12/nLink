using System.Collections.Concurrent;
using System.Text.Json;
using NLink.Core.FileTransfer;
using Xunit.Abstractions;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferRuntimeSuite", "CompletionBoundary")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferCompletionBoundaryReproTests : SessionFileTransferServiceTestBase
{
    private readonly ITestOutputHelper output;

    public SessionFileTransferCompletionBoundaryReproTests(ITestOutputHelper output) => this.output = output;

    [Theory]
    [InlineData("success")]
    [InlineData("collision")]
    [InlineData("complete_lost")]
    [InlineData("complete_send_failed")]
    [InlineData("terminal_state_stalled")]
    [InlineData("terminal_state_failed")]
    [InlineData("success", "tuna")]
    [InlineData("collision", "tuna")]
    [InlineData("complete_lost", "tuna")]
    [InlineData("success", "fallback")]
    [InlineData("collision", "fallback")]
    [InlineData("complete_lost", "fallback")]
    public async Task Sender_MustNotCompleteBeforeHeldDestinationFinalization(string outcome, string route = "regular")
    {
        var collideAtFinalPath = outcome == "collision";
        var completionSendFails = outcome == "complete_send_failed";
        var transferId = $"completion_boundary_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        var payload = Enumerable.Range(0, 2048).Select(static i => (byte)(i % 251)).ToArray();
        var root = Path.Combine(FindRepoRoot(), "artifacts", "completion-boundary-fix-20260926", transferId);
        Directory.CreateDirectory(root);
        var finalPath = Path.Combine(root, "boundary.bin");
        var tempPath = finalPath + ".part";
        var trace = new ConcurrentQueue<object>();
        var finalizerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalizer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var senderTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalStateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FileTransferReceiveDestination? destination = null;
        var earlyTerminalStates = 0;
        var fallbackCheckpointResponseObserved = 0;
        var fallbackPayloadBeforeCheckpoint = 0;
        var receiverTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var senderErrorDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        foreach (var transport in new[] { senderTransport, receiverTransport })
        {
            transport.IsFileTunaActiveForRouteSelection = route == "tuna";
            transport.IsPostTunaFileFallbackActiveForRouteSelection = route == "fallback";
        }
        if (route == "tuna")
        {
            senderTransport.SeedInitialTunaReadinessEvidenceForTests(transferId, FileTransferDirection.Outbound);
            receiverTransport.SeedInitialTunaReadinessEvidenceForTests(transferId, FileTransferDirection.Inbound);
        }
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        void ObserveFallback(string stage, SessionFileTransferService service, string direction, object? frame = null)
        {
            if (route != "fallback" || outcome != "complete_lost") { return; }
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            static object? Read(object? value, string name) => value?.GetType().GetProperty(name)!.GetValue(value);
            static object? Fields(object? value, params string[] names) => value is null ? null :
                names.ToDictionary(name => name, name => Read(value, name));
            lock (typeof(SessionFileTransferService).GetField("gate", flags)!.GetValue(service)!)
            {
                var context = typeof(SessionFileTransferService).GetField(direction + "Transfer", flags)!.GetValue(service);
                var leg = (FileTransferLeg?)Read(context, "CurrentTransferLeg");
                var contract = Read(context, "CurrentFallbackFrontierRepairContract");
                trace.Enqueue(new
                {
                    stage,
                    direction,
                    state = Read(context, "State")?.ToString(),
                    leg = Fields(leg, "State", "CanSendData", "CheckpointRequestId", "Generation", "TransportEpochId"),
                    checkpointGatePending = FileTransferCoordinator.IsCurrentPostTunaFallbackLegCheckpointPending(leg),
                    checkpointLease = Read(context, "CurrentFallbackCheckpointLease"),
                    owner = Read(context, "CurrentRuntimeOwner"),
                    repairDecision = Read(context, "CurrentRepairReplayDecision"),
                    repair = Fields(Read(contract, "Authority"), "AuthorityId", "ProtocolRepairRequestId", "CheckpointRequestId"),
                    repairLifecycle = Fields(Read(contract, "Lifecycle"), "IsSettled", "DispatchObserved", "SettlementOutcome"),
                    frame,
                });
            }
        }
        senderTransport.OutboundSessionOpenDeliveryOverrideAsync = (_, _, _) =>
        {
            ObserveFallback("session_open_send", sender, "outbound");
            return Task.FromResult(false);
        };
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV6 batch)
            {
                if (route == "fallback" && Volatile.Read(ref fallbackCheckpointResponseObserved) == 0)
                {
                    Interlocked.Increment(ref fallbackPayloadBeforeCheckpoint);
                }
                ObserveFallback("chunk_batch_send", sender, "outbound", new { batch.RepairRequestId, batch.TransportEpoch, batch.StartChunkIndex, batch.ChunkCount });
            }
            if (frame is FileTransferFrontierRequestFrameV6 request)
            {
                ObserveFallback("sender_frontier_request_send", sender, "outbound", new { request.RepairRequestId, request.RecoveryMode, request.TransportEpoch });
            }
            return Task.FromResult(false);
        };
        senderTransport.FileTransferErrorReceived += (_, e) =>
        {
            trace.Enqueue($"sender_error_delivered:{e.Message.ErrorCode}");
            senderErrorDelivered.TrySetResult();
        };
        sender.TransferChanged += (_, e) =>
        {
            if (e.Snapshot.OutboundState is FileTransferTransferState.Completed or FileTransferTransferState.Failed)
            {
                trace.Enqueue($"sender_terminal:{e.Snapshot.OutboundState}");
                senderTerminal.TrySetResult();
            }
        };
        receiver.TransferChanged += (_, e) =>
        {
            if (e.Snapshot.InboundState is FileTransferTransferState.Completed or FileTransferTransferState.Failed)
            {
                trace.Enqueue($"receiver_terminal:{e.Snapshot.InboundState}");
                receiverTerminal.TrySetResult();
            }
        };
        receiverTransport.OutboundCompleteDeliveryOverrideAsync = (_, _, _) =>
            completionSendFails ? Task.FromException<bool>(new IOException("injected completion send failure")) :
            Task.FromResult(outcome == "complete_lost");
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is FileTransferReceiverStateFrameV6 state)
            {
                if (route == "fallback" && state.RecoveryMode == "regular_nkn_state_refresh" &&
                    senderTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(request =>
                        request.RepairRequestId == state.RepairRequestId && request.TransportEpoch == state.TransportEpoch))
                {
                    Interlocked.Exchange(ref fallbackCheckpointResponseObserved, 1);
                }
                ObserveFallback("receiver_state_send", receiver, "inbound", new { state.Epoch, state.TerminalReady, state.ContiguousCommittedChunkIndex, state.RepairRequestId, state.RecoveryMode, state.TransportEpoch, state.MissingRanges });
            }
            if (frame is FileTransferFrontierRequestFrameV6 request)
            {
                ObserveFallback("frontier_request_send", receiver, "inbound", new { request.RepairRequestId, request.RecoveryMode, request.TransportEpoch });
            }
            if (outcome == "complete_lost" && frame is FileTransferCompleteFrameV4) { return true; }
            if (frame is FileTransferStateFrameV4 { TerminalReady: true })
            {
                trace.Enqueue("terminal_ready_sent");
                if (destination?.IsFinalized != true) { Interlocked.Increment(ref earlyTerminalStates); }
                terminalStateStarted.TrySetResult();
                if (outcome == "terminal_state_stalled") { await Task.Delay(Timeout.Infinite, ct); }
                if (outcome == "terminal_state_failed") { throw new IOException("injected terminal-state send failure"); }
            }
            return false;
        };

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("boundary.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            destination = CreateTempReceiveDestination(finalPath, async ct =>
            {
                trace.Enqueue("destination_finalizer_entered");
                finalizerEntered.TrySetResult();
                await releaseFinalizer.Task.WaitAsync(ct);
                trace.Enqueue("destination_finalizer_released");
            });
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult(destination), CancellationToken.None);
            await finalizerEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            ObserveFallback("finalizer_held", sender, "outbound");
            ObserveFallback("finalizer_held", receiver, "inbound");
            if (route == "fallback")
            {
                Assert.Equal(0, Volatile.Read(ref fallbackPayloadBeforeCheckpoint));
                var checkpoint = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>()
                    .Where(frame => frame.RecoveryMode == "regular_nkn_state_refresh"));
                Assert.Contains(receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>(), frame =>
                    frame.RepairRequestId == checkpoint.RepairRequestId &&
                    frame.TransportEpoch == checkpoint.TransportEpoch &&
                    frame.RecoveryMode == "regular_nkn_state_refresh" && !frame.TerminalReady);
            }

            Assert.Equal(route == "fallback" ? FileTransferProtocol.ProtocolVersionV6 : FileTransferProtocol.ProtocolVersionV4,
                Assert.Single(senderTransport.SentSessionOpens).ProtocolVersion);
            Assert.Equal(route switch { "tuna" => FileTransferRouteResolver.FileTunaV4Token,
                "fallback" => FileTransferRouteResolver.PostTunaFallbackV6Token, _ => FileTransferRouteResolver.RegularNknV4FastToken },
                sender.Snapshot.Outbound!.RouteToken);
            if (route != "fallback")
            {
                Assert.Contains(receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>(), frame =>
                    !frame.TerminalReady && frame.BytesCommitted == payload.Length && frame.ContiguousCommittedChunkIndex == 1);
            }
            Assert.NotEqual(FileTransferTransferState.Completed, sender.Snapshot.OutboundState);
            Assert.NotEqual(FileTransferTransferState.Completed, receiver.Snapshot.InboundState);
            Assert.True(File.Exists(tempPath));
            Assert.False(File.Exists(finalPath));
            Assert.False(destination.IsFinalized);
            Assert.Empty(receiverTransport.SentCompletes);

            Assert.DoesNotContain(receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>(),
                frame => frame.TerminalReady);
            trace.Enqueue($"while_finalizer_held:sender={sender.Snapshot.OutboundState},receiver={receiver.Snapshot.InboundState},complete_messages=0");

            byte[] collisionBytes = [91, 92, 93];
            if (collideAtFinalPath) { await File.WriteAllBytesAsync(finalPath, collisionBytes); }
            releaseFinalizer.TrySetResult();
            try { await Task.WhenAll(receiverTerminal.Task, senderTerminal.Task).WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (TimeoutException)
            {
                ObserveFallback("sender_completion_timeout", sender, "outbound");
                var context = typeof(SessionFileTransferService).GetField("outboundTransfer",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sender)!;
                var leg = context.GetType().GetProperty("CurrentTransferLeg")!.GetValue(context)!;
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    sender = new[] { "State", "RemoteNextExpectedChunkIndex", "ChunkCount", "V6LastReceiverStateEpoch" }
                        .ToDictionary(name => name, name => context.GetType().GetProperty(name)!.GetValue(context)),
                    leg = new[] { "State", "CanSendData", "CheckpointRequestId" }
                        .ToDictionary(name => name, name => leg.GetType().GetProperty(name)!.GetValue(leg)),
                    states = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>()
                        .Select(frame => new { frame.Epoch, frame.TerminalReady, frame.ContiguousCommittedChunkIndex }),
                }));
                throw;
            }
            var expectedState = collideAtFinalPath || completionSendFails
                ? FileTransferTransferState.Failed : FileTransferTransferState.Completed;
            Assert.Equal(expectedState, receiver.Snapshot.InboundState);
            Assert.Equal(expectedState, sender.Snapshot.OutboundState);
            Assert.Equal(0, Volatile.Read(ref earlyTerminalStates));
            Assert.Equal(!collideAtFinalPath && !completionSendFails, terminalStateStarted.Task.IsCompleted);
            Assert.Equal(!collideAtFinalPath, destination.IsFinalized);
            Assert.Equal(collideAtFinalPath ? collisionBytes : payload, await File.ReadAllBytesAsync(finalPath));
            if (collideAtFinalPath)
            {
                Assert.Empty(receiverTransport.SentCompletes);
                Assert.NotEmpty(receiverTransport.SentErrors);
                Assert.Null(receiver.Snapshot.Inbound!.SavedFilePath);
                await senderErrorDelivered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            }
            else { Assert.Single(receiverTransport.SentCompletes); }
            trace.Enqueue($"after_finalizer:sender={sender.Snapshot.OutboundState},receiver={receiver.Snapshot.InboundState},error={receiver.Snapshot.Inbound!.ErrorCode}");

            if (outcome == "complete_lost")
            {
                Assert.Contains(receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>(), frame => frame.TerminalReady);
            }
        }
        finally
        {
            releaseFinalizer.TrySetResult();
            output.WriteLine(JsonSerializer.Serialize(new { transferId, outcome, route, trace = trace.ToArray() }));
        }
    }

    [Fact]
    public async Task HashMismatch_NeverPublishesTerminalSuccess()
    {
        var transferId = $"hash_boundary_{Guid.NewGuid():N}";
        using var senderTransport = new LoopbackFileTransferTransport(transferId);
        using var receiverTransport = new LoopbackFileTransferTransport(transferId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var finalized = false;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV4 batch) { batch.DataSegments[0][0] ^= 1; }
            return Task.FromResult(false);
        };
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("hash.bin", 2048, transferId, ChunkSizeBytes: 2048),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[2048], writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult(new FileTransferReceiveDestination(
            new MemoryStream(), _ => { finalized = true; return Task.CompletedTask; })), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.Failed &&
            sender.Snapshot.OutboundState == FileTransferTransferState.Failed);
        Assert.Equal(FileTransferResultCodes.IntegrityMismatch, receiver.Snapshot.Inbound!.ErrorCode);
        Assert.False(finalized);
        Assert.Empty(receiverTransport.SentCompletes);
        Assert.DoesNotContain(receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>(), state => state.TerminalReady);
    }
}
