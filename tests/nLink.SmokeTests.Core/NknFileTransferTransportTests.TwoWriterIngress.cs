using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    // Two real test hosts/loggers, connected only by an offline packet pipe.
    // The pipe carries original encoded bytes, never log records or authority.
    internal static async Task ProduceTwoWriterIngressFixture()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ct = deadline.Token;
        var sender = Environment.GetEnvironmentVariable("NLINK_TWO_WRITER_PEER") == "sender";
        var directory = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR")!;
        var pipeName = Environment.GetEnvironmentVariable("NLINK_TWO_WRITER_PIPE") ?? "nlink-ingress-" + Guid.NewGuid().ToString("N");
        using var pipe = sender
            ? (PipeStream)new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)
            : new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var peer = sender ? null : new Process { StartInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        } };
        Task<string>? stdout = null, stderr = null;
        try
        {
            if (peer is not null)
            {
                peer.StartInfo.ArgumentList.Add("vstest");
                peer.StartInfo.ArgumentList.Add(typeof(FileTransferPublicationUseConsumerTests).Assembly.Location);
                peer.StartInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~PublicationUseConsumer_GeneratedRecordsAreBoundAndFailClosed");
                peer.StartInfo.Environment["NLINK_TWO_WRITER_PEER"] = "sender";
                peer.StartInfo.Environment["NLINK_TWO_WRITER_PIPE"] = pipeName;
                peer.StartInfo.Environment["NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH"] = Path.Combine(directory, "sender.log");
                peer.Start();
                stdout = peer.StandardOutput.ReadToEndAsync();
                stderr = peer.StandardError.ReadToEndAsync();
                await ((NamedPipeServerStream)pipe).WaitForConnectionAsync(ct);
            }
            else { await ((NamedPipeClientStream)pipe).ConnectAsync(ct); }

            await RunTwoWriterIngressPeerAsync(sender, pipe, directory, ct);
            if (peer is not null)
            {
                await peer.WaitForExitAsync(ct);
                Assert.True(peer.ExitCode == 0, await stdout! + Environment.NewLine + await stderr!);
            }
        }
        finally
        {
            if (peer is not null && !peer.HasExited) { peer.Kill(entireProcessTree: true); }
            if (stdout is not null && stderr is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "sender-test-stdout.log"), await stdout);
                await File.WriteAllTextAsync(Path.Combine(directory, "sender-test-stderr.log"), await stderr);
            }
        }
    }

    private static async Task RunTwoWriterIngressPeerAsync(bool sender, PipeStream pipe, string directory, CancellationToken ct)
    {
        FakeNknClient.ResetNetwork();
        const string senderAddress = "sender.two-writer.address", receiverAddress = "receiver.two-writer.address";
        const string transferId = "transfer_two_writer_ingress";
        var finalFallbackFixture = Environment.GetEnvironmentVariable("NLINK_PUBLICATION_FINAL_FALLBACK_FIXTURE") == "1";
        var serviceFixture = Environment.GetEnvironmentVariable("NLINK_PUBLICATION_SERVICE_INGRESS_FIXTURE") == "1";
        var serviceYieldFixture = Environment.GetEnvironmentVariable("NLINK_PUBLICATION_SERVICE_YIELD_FIXTURE") == "1";
        var yieldCase = Environment.GetEnvironmentVariable("NLINK_PUBLICATION_YIELD_CASE") ?? "none";
        Assert.Contains(yieldCase, new[] { "none", "both", "control_only", "bulk_only", "receiver_local_first" });
        var yieldCopies = yieldCase is "control_only" or "bulk_only" ? 1 : 2;
        var serviceFallback = finalFallbackFixture && serviceFixture;
        var startChunk = serviceFallback ? 100 : 12;
        var chunkSize = serviceFallback ? 21 * 1024 : 1024;
        var tunaPayloadMode = Environment.GetEnvironmentVariable("NLINK_PUBLICATION_TUNA_PAYLOAD_MODE") ?? "none";
        var rebatching = tunaPayloadMode is "rebatch_same" or "rebatch_changed" or "rebatch_middle";
        var middleRebatch = tunaPayloadMode == "rebatch_middle";
        var independentPairs = tunaPayloadMode is "independent_pairs" or "independent_missing_receive";
        var omitSecondReceive = tunaPayloadMode is "missing_receive" or "independent_missing_receive";
        var secondTunaStart = independentPairs ? 20 : middleRebatch ? 11 : tunaPayloadMode == "wrong_range" ? 13 : 12;
        if (independentPairs) { Assert.True(serviceFixture && serviceYieldFixture); }
        if (rebatching) { Assert.True(serviceFixture && !finalFallbackFixture && !serviceYieldFixture); }
        using var tunaLane = tunaPayloadMode == "none" ? null : new FakeNknAccelerationLane();
        var client = new FakeNknClient(sender ? senderAddress : receiverAddress);
        using var transport = new NknSignalingTransport(client, NknTransportOptions.Load(),
            new NknIdentity(sender ? "two-writer-sender" : "two-writer-receiver", client.Address),
            NknTunaAccelerationOptions.Disabled, tunaLane);
        await using var link = new IngressPacketPipe(pipe, client, ct, tunaLane, yieldCase);
        var approved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var join = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Approved += (_, _) => approved.TrySetResult();
        transport.IncomingJoinRequest += (_, e) => join.TrySetResult(e);
        var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.FileTransferOfferReceived += (_, _) => offered.TrySetResult();
        transport.FileTransferAcceptReceived += (_, _) => accepted.TrySetResult();
        transport.FileTransferSessionOpenReceived += (_, _) => opened.TrySetResult();

        if (sender)
        {
            var raw = await link.WaitAsync("invite");
            var validation = InviteTokenServiceFactory.CreateInviteTokenValidator().Validate(raw, DateTimeOffset.UtcNow);
            Assert.True(validation.IsSuccess, validation.Message);
            await transport.JoinByInviteAsync(raw, validation.Invite!, ct);
        }
        else
        {
            await transport.HostByAddressAsync(ct);
            _ = CreateValidatedInviteForTarget(new PeerAddress(receiverAddress), out var raw,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, boundHelperAddress: new PeerAddress(senderAddress));
            await link.SignalAsync("invite", raw);
            var pending = await join.Task.WaitAsync(ct);
            await pending.ApproveAsync(pending.CreateApprovalDecision(), ct);
        }
        await approved.Task.WaitAsync(ct);
        var sessionId = transport.CurrentSessionSecurityState.SessionId!.Value.Value;
        if (sender)
        {
            await transport.SendFileTransferOfferAsync(new FileTransferOfferV2
            {
                SessionId = sessionId, TransferId = transferId, FileName = "two-writer.bin", FileSizeBytes = serviceFallback ? 256L * chunkSize : 131072,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            }, ct);
            await accepted.Task.WaitAsync(ct);
            await transport.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2
            {
                SessionId = sessionId, TransferId = transferId, ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = chunkSize, InitialPipelineDepth = 8,
            }, ct);
        }
        else
        {
            await offered.Task.WaitAsync(ct);
            await transport.SendFileTransferAcceptAsync(new FileTransferAcceptV1
            {
                SessionId = sessionId, TransferId = transferId, AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            }, ct);
            await opened.Task.WaitAsync(ct);
        }
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(sessionId, transferId, ct);
        if (serviceFixture && !sender)
        {
            link.SendingYield = serviceYieldFixture;
            await link.SignalAsync("regular-ready");
            await link.WaitAsync("held");
            FileTransferChunkBatchFrameV4? heldChunk = null;
            string? heldPayloadFingerprint = null;
            object? rebatchingResult = null;
            if (rebatching)
            {
                // Decode the actual held wire privately; this does not enter the
                // adapter replay window or dispatch the stale packet.
                var heldWire = Convert.FromBase64String(await link.WaitAsync("rebatching-held-wire"));
                Assert.True(EnvelopeCodec.TryDeserialize(heldWire, out var heldEnvelope));
                var key = Assert.IsType<byte[]>(typeof(NknSignalingTransport)
                    .GetMethod("GetFileTransferSessionSharedKeyOrThrow", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(transport, null));
                var decoded = SessionSecureEnvelopeCodec.Decrypt(key, heldEnvelope.Payload,
                    new SessionSecureEnvelopeExpectation(Family: SessionSecureMessageFamily.FileTransfer,
                        SessionId: new SessionId(sessionId), RequestId: transferId));
                Assert.True(FileTransferDataFrameCodec.TryDeserializeLegacyV4(decoded.Plaintext, out var heldFrame));
                heldChunk = Assert.IsType<FileTransferChunkBatchFrameV4>(heldFrame);
                Assert.Equal(sessionId, heldChunk.SessionId);
                Assert.Equal(transferId, heldChunk.TransferId);
                Assert.Equal(startChunk, heldChunk.StartChunkIndex);
                Assert.Equal(1, heldChunk.ChunkCount);
                Assert.Equal(Enumerable.Repeat((byte)17, chunkSize), Assert.Single(heldChunk.DataSegments));
                heldPayloadFingerprint = SecurityFingerprint("payload", "ftpay1-", heldEnvelope.Payload);
            }
            await SessionFileTransferV4SenderTests.CreateSecondTunaPublicationEvidenceFixtureAsync(
                replayPostCommitCallbacks: false, sessionId: sessionId, suppliedTransferId: transferId,
                receivePeerYield: serviceYieldFixture ? null : async hints =>
                {
                    foreach (var hint in hints)
                    {
                        Assert.IsAssignableFrom<IFileTransferRouteHintObserver>(transport).ObserveFileTransferRouteHint(hint);
                    }
                    await link.SignalAsync("fallback-ready");
                    return Assert.IsType<FileTransferFallbackTailYieldFrameV6>(await dataSession.ReceiveAsync(ct));
                },
                afterSecondTuna: async (publication, hints) =>
                {
                    link.SendingYield = false;
                    var hint = Assert.Single(hints.Where(hint => hint.PublicationAuthority?.LiveRouteEpoch == 3));
                    Assert.IsAssignableFrom<IFileTransferRouteHintObserver>(transport).ObserveFileTransferRouteHint(hint);
                    var receivedTunaStart = -1;
                    if (tunaLane is not null)
                    {
                        transport.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
                        await link.SignalAsync("tuna-receiver-ready");
                        await link.WaitAsync("tuna-payload-sent");
                        if (!omitSecondReceive)
                        {
                            var received = Assert.IsType<FileTransferChunkBatchFrameV4>(await dataSession.ReceiveAsync(ct));
                            receivedTunaStart = secondTunaStart;
                            Assert.Equal(receivedTunaStart, received.StartChunkIndex);
                            Assert.Equal(rebatching ? 3 : 2, received.ChunkCount);
                            var expectedFirst = Enumerable.Repeat(middleRebatch ? (byte)34 : (byte)17, 1024).ToArray();
                            if (tunaPayloadMode == "rebatch_changed") { expectedFirst[0] ^= 1; }
                            Assert.Equal(expectedFirst, received.DataSegments[0]);
                            Assert.Equal(Enumerable.Repeat(middleRebatch ? (byte)17 : (byte)34, 1024), received.DataSegments[1]);
                            if (rebatching)
                            {
                                Assert.Equal(3, received.DataSegments.Count);
                                Assert.Equal(Enumerable.Repeat((byte)51, 1024), received.DataSegments[2]);
                                Assert.NotNull(heldChunk);
                                var selectedSlot = heldChunk.StartChunkIndex - received.StartChunkIndex;
                                Assert.Equal(middleRebatch ? 1 : 0, selectedSlot);
                                Assert.Equal(heldChunk.DataSegments[0].Length, received.DataSegments[selectedSlot].Length);
                                var contentEqual = heldChunk.DataSegments[0].AsSpan().SequenceEqual(received.DataSegments[selectedSlot]);
                                Assert.Equal(tunaPayloadMode != "rebatch_changed", contentEqual);
                                rebatchingResult = new
                                {
                                    Schema = "test-only-rebatching-byte-comparison-v1", Mode = tunaPayloadMode,
                                    Comparison = "decoded_held_wire_to_data_session_receive", ContentEqual = contentEqual,
                                    HeldPayloadFingerprint = heldPayloadFingerprint, ChunkIndex = heldChunk.StartChunkIndex,
                                    SegmentBytes = heldChunk.DataSegments[0].Length, HeldChunkCount = 1, ReceivedChunkCount = 3,
                                    ReceiverProcessId = Environment.ProcessId,
                                };
                            }
                        }
                        await link.SignalAsync("tuna-payload-checked");
                    }
                    await CheckDelayedIngressAsync(transport, dataSession, publication, link, peerYieldReceived: true, receivedTunaStart,
                        startChunk, chunkSize, peerYieldDispatches: serviceYieldFixture ? yieldCopies : 1,
                        firstTunaReceived: independentPairs, heldChunkCount: rebatching ? 1 : 2);
                    if (rebatchingResult is not null)
                    {
                        await File.WriteAllTextAsync(Path.Combine(directory, "rebatching-fixture.json"),
                            JsonSerializer.Serialize(rebatchingResult), ct);
                    }
                },
                sendLocalYield: serviceYieldFixture || (tunaLane is null && !serviceFallback) ? null : frame => dataSession.SendAsync(frame, ct),
                prepareFinalFallbackReceive: !serviceFallback ? null : async (hints, repairId) =>
                {
                    foreach (var hint in hints)
                    {
                        Assert.IsAssignableFrom<IFileTransferRouteHintObserver>(transport).ObserveFileTransferRouteHint(hint);
                    }
                    await link.SignalAsync("final-fallback-ready", repairId ?? "");
                    await link.WaitAsync("final-fallback-ready");
                },
                receiveData: !serviceFallback ? null : dataSession.ReceiveWithMetadataAsync,
                yieldTransport: serviceYieldFixture ? Assert.IsAssignableFrom<IFileTransferControlPlaneDeliveryTransport>(transport) : null,
                receivePeerYieldWithEvidence: !serviceYieldFixture ? null : async hints =>
                {
                    foreach (var hint in hints)
                    {
                        Assert.IsAssignableFrom<IFileTransferRouteHintObserver>(transport).ObserveFileTransferRouteHint(hint);
                    }
                    await link.SignalAsync("fallback-ready");
                    return await ReceiveYieldCopiesAsync();
                }, receiverLocalYieldFirst: yieldCase == "receiver_local_first",
                afterFirstTuna: !independentPairs ? null : async hints =>
                {
                    var hint = Assert.Single(hints.Where(hint => hint.PublicationAuthority?.LiveRouteEpoch == 1));
                    Assert.IsAssignableFrom<IFileTransferRouteHintObserver>(transport).ObserveFileTransferRouteHint(hint);
                    transport.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
                    await link.SignalAsync("first-tuna-ready");
                    var received = Assert.IsType<FileTransferChunkBatchFrameV4>(await dataSession.ReceiveAsync(ct));
                    Assert.Equal(16, received.StartChunkIndex);
                    Assert.Equal(2, received.ChunkCount);
                    Assert.Equal(Enumerable.Repeat((byte)17, 1024), received.DataSegments[0]);
                    Assert.Equal(Enumerable.Repeat((byte)34, 1024), received.DataSegments[1]);
                    transport.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.None, sessionId);
                    await link.SignalAsync("first-tuna-checked");
                });
            if (serviceFallback) { await link.SignalAsync("final-fallback-delivered"); }
            await WriteProcessAsync();
            return;
        }
        using var history = new FileTransferPublicationIngressHistory(sessionId, transferId,
            sender ? FileTransferDirection.Outbound : FileTransferDirection.Inbound, stopAtFallbackYield: serviceFixture,
            serviceYieldData: serviceYieldFixture ? dataSession : null,
            serviceYieldTransport: serviceYieldFixture ? Assert.IsAssignableFrom<IFileTransferControlPlaneDeliveryTransport>(transport) : null);
        var evidence = history.Publish(0);
        PublishDataPathFixtureAuthority(transport, history.Tokens[0], evidence);
        await link.SignalAsync("regular-ready");
        await link.WaitAsync("regular-ready");
        if (sender)
        {
            link.HoldDataFrame = true;
            var frame = new FileTransferChunkBatchFrameV4
            {
                SessionId = sessionId, TransferId = transferId, StartChunkIndex = startChunk, ChunkCount = rebatching ? 1 : 2,
                DataSegments = rebatching ? [Enumerable.Repeat((byte)17, chunkSize).ToArray()] :
                    [Enumerable.Repeat((byte)17, chunkSize).ToArray(), Enumerable.Repeat((byte)34, chunkSize).ToArray()],
                BatchProfile = "v4_default_21k",
            };
            var authority = Assert.IsType<FileTransferDataPathSendAuthority>(typeof(SessionFileTransferService)
                .GetMethod("CreateDataPathSendAuthority", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [history.Tokens[0], "two_writer_ingress_fixture"]));
            FileTransferPublicationUseEvidence.CaptureSend(authority, evidence, FileTransferPublicationUseKind.V4PhysicalSubmission);
            await Assert.IsAssignableFrom<IFileTransferDataPathAuthoritySession>(dataSession).SendAsync(frame, authority, ct);
            Assert.NotNull(link.HeldFrame);
            if (rebatching) { await link.SignalAsync("rebatching-held-wire", Convert.ToBase64String(link.HeldFrame.Payload!)); }
            await link.SignalAsync("held");
        }
        else { await link.WaitAsync("held"); }
        for (var i = 1; i < history.Tokens.Count; i++)
        {
            var published = history.Publish(i);
            PublishDataPathFixtureAuthority(transport, history.Tokens[i], published);
            if (independentPairs && i == 1)
            {
                transport.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
                await link.WaitAsync("first-tuna-ready");
                await SendTunaPayloadAsync(history.Tokens[i], published, 16, deliver: true);
                await link.WaitAsync("first-tuna-checked");
                transport.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.None, sessionId);
            }
        }
        if (serviceFixture)
        {
            if (!serviceYieldFixture) { history.PublishFallbackYield(); }
            await link.WaitAsync("fallback-ready");
            link.HoldDataFrame = false;
            if (serviceYieldFixture)
            {
                link.SendingYield = true;
                await history.SendServiceYieldAsync();
                link.SendingYield = false;
            }
            else { await dataSession.SendAsync(history.FallbackYieldFrame!, ct); }
            if (tunaLane is not null || serviceFallback)
            {
                if (serviceYieldFixture)
                {
                    evidence = history.ActivateFromServicePeerYield(await ReceiveYieldCopiesAsync());
                }
                else
                {
                    var peerFrame = Assert.IsType<FileTransferFallbackTailYieldFrameV6>(await dataSession.ReceiveAsync(ct));
                    evidence = history.ActivateFromPeerYield(peerFrame);
                }
                var publication = history.Tokens[3];
                PublishDataPathFixtureAuthority(transport, publication, evidence);
            }
            if (tunaLane is not null)
            {
                var publication = history.Tokens[3];
                transport.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
                await link.WaitAsync("tuna-receiver-ready");
                await SendTunaPayloadAsync(publication, evidence, secondTunaStart, deliver: !omitSecondReceive);
                await link.SignalAsync("tuna-payload-sent");
                await link.WaitAsync("tuna-payload-checked");
            }
        }
        if (sender)
        {
            await link.SignalAsync("second-tuna-ready");
            await link.WaitAsync("second-tuna-ready");
            // Replay the same original packet twice; the second must deduplicate.
            await link.SendAsync(link.HeldFrame!);
            await link.SendAsync(link.HeldFrame!);
            await link.SignalAsync("replayed");
            await link.WaitAsync("rejection-checked");
        }
        else { await CheckDelayedIngressAsync(transport, dataSession, history.Tokens[3], link); }
        if (finalFallbackFixture)
        {
            var beforeFallback = history.Tokens[3];
            var fallbackEvidence = history.PublishFinalFallback(serviceFallback ? 1 : 2);
            var fallback = history.Tokens[4];
            PublishDataPathFixtureAuthority(transport, fallback, fallbackEvidence);
            var states = Assert.IsAssignableFrom<IDictionary>(GetPrivateField(transport, "fileTransferStates"));
            var fallbackState = states[transferId];
            Assert.NotNull(fallbackState);
            // A stale publication cannot roll the adapter back to second Tuna.
            PublishDataPathFixtureAuthority(transport, beforeFallback);
            Assert.Equal(fallbackState, states[transferId]);
            await link.SignalAsync("final-fallback-ready");
            var repairId = await link.WaitAsync("final-fallback-ready");
            if (sender)
            {
                link.HoldDataFrame = false;
                var authority = Assert.IsType<FileTransferDataPathSendAuthority>(typeof(SessionFileTransferService)
                    .GetMethod("CreateDataPathSendAuthority", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [fallback, "independent_final_fallback_fixture"]));
                await Assert.IsAssignableFrom<IFileTransferDataPathAuthoritySession>(dataSession).SendAsync(new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId, TransferId = transferId, TransportEpoch = serviceFallback ? 1 : 2,
                    StartChunkIndex = startChunk, ChunkCount = 2, BatchProfile = "v4_default_21k",
                    RepairRequestId = string.IsNullOrEmpty(repairId) ? null : repairId,
                    DataSegments = [Enumerable.Repeat((byte)17, chunkSize).ToArray(), Enumerable.Repeat((byte)34, chunkSize).ToArray()],
                }, authority, ct);
                await link.WaitAsync("final-fallback-delivered");
            }
            else
            {
                var received = Assert.IsType<FileTransferChunkBatchFrameV6>(await dataSession.ReceiveAsync(ct));
                Assert.Equal(12, received.StartChunkIndex);
                Assert.Equal(2, received.ChunkCount);
                Assert.Equal(2, received.TransportEpoch);
                Assert.Equal(Enumerable.Repeat((byte)17, 1024), received.DataSegments[0]);
                Assert.Equal(Enumerable.Repeat((byte)34, 1024), received.DataSegments[1]);
                var receive = Assert.Single(FindOperationalLogLines(line => line.Contains("event=filetransfer_data_path_receive_result;", StringComparison.Ordinal) &&
                    line.Contains("frame_type=filetransfer.chunk_batch.v6;", StringComparison.Ordinal)));
                AssertDataPathPublication(receive, fallback);
                var dispatched = Assert.Single(FindOperationalLogLines(line => line.Contains("event=filetransfer_data_frame_dispatched;", StringComparison.Ordinal)));
                Assert.Contains("frame_type=filetransfer.chunk_batch.v6;", dispatched, StringComparison.Ordinal);
                await link.SignalAsync("final-fallback-delivered");
            }
        }
        await WriteProcessAsync();

        async Task SendTunaPayloadAsync(RoutePublicationToken publication, FileTransferRoutePublicationEvidence publicationEvidence,
            int firstChunk, bool deliver)
        {
            Assert.NotNull(tunaLane);
            var before = tunaLane.Sent.Count;
            var authority = Assert.IsType<FileTransferDataPathSendAuthority>(typeof(SessionFileTransferService)
                .GetMethod("CreateDataPathSendAuthority", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [publication, "two_writer_exact_tuna_payload_fixture"]));
            FileTransferPublicationUseEvidence.CaptureSend(authority, publicationEvidence, FileTransferPublicationUseKind.V4PhysicalSubmission);
            var segments = new List<byte[]> { Enumerable.Repeat((byte)17, 1024).ToArray(), Enumerable.Repeat((byte)34, 1024).ToArray() };
            if (rebatching)
            {
                segments.Add(Enumerable.Repeat((byte)51, 1024).ToArray());
                if (middleRebatch) { (segments[0], segments[1]) = (segments[1], segments[0]); }
                if (tunaPayloadMode == "rebatch_changed") { segments[0][0] ^= 1; }
            }
            await Assert.IsAssignableFrom<IFileTransferDataPathAuthoritySession>(dataSession).SendAsync(new FileTransferChunkBatchFrameV4
            {
                SessionId = sessionId, TransferId = transferId, StartChunkIndex = firstChunk,
                ChunkCount = segments.Count, BatchProfile = "v4_default_21k", DataSegments = segments,
            }, authority, ct);
            var sent = Assert.Single(tunaLane.Sent.Skip(before));
            Assert.Equal(NknBridgeChannel.Bulk, sent.Lane);
            if (deliver) { await link.SendAsync(new IngressPacketPipe.Packet(null, null, null, null, null, sent.Lane, sent.Payload, Tuna: true)); }
        }

        async Task<FileTransferReceivedDataFrame[]> ReceiveYieldCopiesAsync()
        {
            var first = await dataSession.ReceiveWithMetadataAsync(ct);
            Assert.IsType<FileTransferFallbackTailYieldFrameV6>(first.Frame);
            if (yieldCopies == 1) { return [first]; }
            if (yieldCase != "none") { await link.ReleaseSecondYieldCopyAsync(); }
            var second = await dataSession.ReceiveWithMetadataAsync(ct);
            Assert.IsType<FileTransferFallbackTailYieldFrameV6>(second.Frame);
            return [first, second];
        }

        async Task WriteProcessAsync()
        {
            await File.WriteAllTextAsync(Path.Combine(directory, sender ? "sender-process.json" : "receiver-process.json"),
                JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId, LogPath = LocalOperationalLog.LogFilePath, Sender = sender }), ct);
            link.ThrowIfFaulted();
        }
    }

    private static async Task CheckDelayedIngressAsync(NknSignalingTransport transport, IFileTransferDataSession dataSession,
        RoutePublicationToken publication, IngressPacketPipe link, bool peerYieldReceived = false, int receivedTunaStart = -1,
        int startChunk = 12, int chunkSize = 1024, int peerYieldDispatches = 1, bool firstTunaReceived = false, int heldChunkCount = 2)
    {
        var states = Assert.IsAssignableFrom<IDictionary>(GetPrivateField(transport, "fileTransferStates"));
        var before = states[publication.TransferId];
        Assert.NotNull(before);
        var dispatchedBefore = FindOperationalLogLines(line => line.Contains("event=filetransfer_data_frame_dispatched;", StringComparison.Ordinal));
        if (peerYieldReceived)
        {
            var firstCount = firstTunaReceived ? 1 : 0;
            if (firstTunaReceived) { Assert.Contains("frame_type=filetransfer.chunk_batch.v4;", dispatchedBefore[0], StringComparison.Ordinal); }
            Assert.All(dispatchedBefore.Skip(firstCount).Take(peerYieldDispatches), line =>
                Assert.Contains("frame_type=filetransfer.fallback_tail.yield.v6;", line, StringComparison.Ordinal));
            Assert.Equal(firstCount + peerYieldDispatches + (receivedTunaStart < 0 ? 0 : 1), dispatchedBefore.Count);
            if (receivedTunaStart >= 0)
            {
                Assert.Contains("frame_type=filetransfer.chunk_batch.v4;", dispatchedBefore[firstCount + peerYieldDispatches], StringComparison.Ordinal);
                var tunaReceive = Assert.Single(FindOperationalLogLines(line => line.Contains("event=filetransfer_data_path_receive_result;", StringComparison.Ordinal) &&
                    line.Contains("actual_transport=tuna;", StringComparison.Ordinal) && line.Contains("live_route_epoch=3;", StringComparison.Ordinal)));
                Assert.Contains($"start_chunk_index={receivedTunaStart};", tunaReceive, StringComparison.Ordinal);
            }
        }
        else { Assert.Empty(dispatchedBefore); }
        await link.SignalAsync("second-tuna-ready");
        await link.WaitAsync("second-tuna-ready");
        await link.WaitAsync("replayed");
        await WaitUntilAsync(() => FindOperationalLogLines(line => line.Contains("event=filetransfer_data_frame_ignored;", StringComparison.Ordinal)).Count == 1,
            TimeSpan.FromSeconds(3));
        var rows = FindOperationalLogLines(line => line.Contains("receive_disposition=rejected_before_dispatch;", StringComparison.Ordinal));
        Assert.Equal(3, rows.Count);
        Assert.All(rows, line =>
        {
            AssertDataPathPublication(line, publication);
            Assert.Contains("authority_effect=none;", line, StringComparison.Ordinal);
            AssertDataPathFrame(line, startChunk, chunkSize, heldChunkCount);
        });
        Assert.Equal(before, states[publication.TransferId]);
        Assert.Equal(dispatchedBefore, FindOperationalLogLines(line => line.Contains("event=filetransfer_data_frame_dispatched;", StringComparison.Ordinal)));
        using var noDelivery = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dataSession.ReceiveAsync(noDelivery.Token));
        await link.SignalAsync("rejection-checked");
    }

    private sealed class IngressPacketPipe : IAsyncDisposable
    {
        internal sealed record Packet(string? Signal, string? Value, string? Source, string? Destination, string? Topic, NknBridgeChannel Channel, byte[]? Payload, bool Tuna = false);
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private readonly SemaphoreSlim writes = new(1);
        private readonly CancellationTokenSource stop;
        private readonly CancellationToken ct;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> signals = new();
        private readonly Task pump;
        internal bool HoldDataFrame { get; set; }
        internal Packet? HeldFrame { get; private set; }
        internal bool SendingYield { get; set; }
        private readonly TaskCompletionSource secondYieldCopy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Packet> heldYieldPackets = [];
        private readonly Action<Packet> deliver;
        private bool yieldCopiesReleased;

        internal IngressPacketPipe(PipeStream pipe, FakeNknClient client, CancellationToken ct, FakeNknAccelerationLane? tunaLane = null,
            string yieldCase = "none")
        {
            stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            this.ct = ct = stop.Token;
            reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            client.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);
            client.BeforeSendCoreAsync = async (destination, payload, channel, _) =>
            {
                var source = channel == NknBridgeChannel.Bulk ? client.BulkAddress : channel == NknBridgeChannel.Media ? client.MediaAddress : client.Address;
                var packet = new Packet(null, null, source, destination, null, channel, payload.ToArray());
                if (HoldDataFrame && EnvelopeCodec.TryDeserialize(payload, out var envelope) && envelope.Type == MsgType.FileTransferDataFrame)
                { Assert.Null(HeldFrame); HeldFrame = packet; return; }
                if (SendingYield && (yieldCase == "control_only" && channel == NknBridgeChannel.Bulk ||
                    yieldCase == "bulk_only" && channel == NknBridgeChannel.Control) &&
                    EnvelopeCodec.TryDeserialize(payload, out var yieldEnvelope) && yieldEnvelope.Type == MsgType.FileTransferDataFrame)
                { throw new IOException("fixture_yield_copy_unavailable"); }
                await SendAsync(packet);
            };
            client.BeforePublishAsync = (topic, payload, _) => SendAsync(new Packet(null, null, client.Address, null, topic, NknBridgeChannel.Control, payload.ToArray()));
            deliver = packet =>
            {
                if (packet.Topic is null) { Assert.Contains(packet.Destination, new[] { client.Address, client.MediaAddress, client.BulkAddress }); }
                typeof(FakeNknClient).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(client, [new NknIncomingMessage(packet.Source!, packet.Payload!, packet.Topic is not null, packet.Topic, packet.Channel)]);
            };
            pump = Task.Run(async () =>
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    var packet = JsonSerializer.Deserialize<Packet>(line)!;
                    if (packet.Signal is not null) { Signal(packet.Signal).TrySetResult(packet.Value ?? ""); }
                    else if (packet.Tuna) { Assert.NotNull(tunaLane); tunaLane.InjectInbound(packet.Channel, packet.Payload!); }
                    else
                    {
                        var secondChannel = yieldCase == "receiver_local_first" ? NknBridgeChannel.Control : NknBridgeChannel.Bulk;
                        if (yieldCase is "both" or "receiver_local_first" && packet.Channel == secondChannel &&
                            EnvelopeCodec.TryDeserialize(packet.Payload!, out var envelope) && envelope.Type == MsgType.FileTransferDataFrame)
                        {
                            lock (heldYieldPackets)
                            {
                                if (!yieldCopiesReleased)
                                {
                                    // Queue and direct-copy paths can deliver the same
                                    // envelope twice. Preserve both for normal dedup.
                                    Assert.True(heldYieldPackets.Count < 2);
                                    heldYieldPackets.Add(packet);
                                    secondYieldCopy.TrySetResult();
                                    continue;
                                }
                            }
                        }
                        deliver(packet);
                    }
                }
            }, ct);
        }

        private TaskCompletionSource<string> Signal(string name) => signals.GetOrAdd(name, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        internal async Task ReleaseSecondYieldCopyAsync()
        {
            await secondYieldCopy.Task.WaitAsync(ct);
            lock (heldYieldPackets)
            {
                yieldCopiesReleased = true;
                foreach (var packet in heldYieldPackets) { deliver(packet); }
                heldYieldPackets.Clear();
            }
        }
        internal async Task<string> WaitAsync(string name)
        {
            var signal = Signal(name).Task;
            if (await Task.WhenAny(signal, pump).WaitAsync(ct) == pump)
            {
                await pump;
                throw new InvalidOperationException("Peer pipe closed before " + name);
            }
            return await signal.WaitAsync(ct);
        }
        internal Task SignalAsync(string name, string value = "") => SendAsync(new Packet(name, value, null, null, null, NknBridgeChannel.Control, null));
        internal async Task SendAsync(Packet packet)
        {
            await writes.WaitAsync(ct);
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(packet).AsMemory(), ct); }
            finally { writes.Release(); }
        }
        internal void ThrowIfFaulted() { if (pump.IsFaulted) { pump.GetAwaiter().GetResult(); } }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            try { await pump; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { reader.Dispose(); writer.Dispose(); writes.Dispose(); stop.Dispose(); }
        }
    }
}
