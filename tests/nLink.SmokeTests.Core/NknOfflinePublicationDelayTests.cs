using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;
using NLink.Tests;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData("absent")] [InlineData("unselected")] [InlineData("removed")]
    public Task OfflinePublicationDelay_DisabledPreservesThreeOriginalCopies(string mode)
        => WithPublicationPair(async (host, client, peer, request, ct) =>
        {
            OfflinePublicationMetadata? metadata = null;
            if (mode != "absent") host.OfflinePublicationDelayForTests = new(request.Frame.SessionId, request.Frame.TransferId,
                item => { metadata = item; return null; }, _ => { });
            if (mode == "removed") host.OfflinePublicationDelayForTests = null;
            await using var pipe = await PublicationTestPipe.OpenAsync(client.Address, peer, ct);
            client.BeforeSendCoreAsync = (_, bytes, lane, token) => pipe.WriteAsync(bytes, lane, token);
            var validations = 0;
            var result = await host.SendFileTransferControlPlaneFrameAsync(request with
            { PreTransportDeliveryValidator = () => { validations++; return true; } }, ct);
            Assert.True(result.ControlQueue && result.ControlCopy && result.BulkCopy && result.AcceptedAny);
            Assert.False(result.ControlAck); Assert.Equal(5, validations);
            var writes = pipe.Observations.Where(w => w.Completed).ToArray();
            Assert.Equal(3, writes.Length);
            Assert.Equal(writes.Where(w => w.Lane == NknBridgeChannel.Control).First().Sha256,
                writes.Where(w => w.Lane == NknBridgeChannel.Control).Last().Sha256);
            Assert.NotEqual(writes.First(w => w.Lane == NknBridgeChannel.Control).Sha256,
                Assert.Single(writes.Where(w => w.Lane == NknBridgeChannel.Bulk)).Sha256);
            if (mode == "unselected")
            {
                Assert.NotNull(metadata);
                Assert.All(writes, w => Assert.Equal(w.Lane == NknBridgeChannel.Control ? metadata.Control.Sha256 : metadata.Bulk!.Sha256, w.Sha256));
                Assert.NotSame(request.Frame, metadata.State);
                var ranges = Assert.IsType<FileTransferReceiverStateFrameV6>(request.Frame).MissingRanges;
                ((FileTransferRangeV4[])ranges)[0] = new() { StartChunkIndex = 99, ChunkCount = 1 };
                Assert.Equal(4, metadata.State.MissingRanges[0].StartChunkIndex);
            }
            else Assert.Null(metadata);
        });

    [Fact]
    public Task OfflinePublicationDelay_AllCopiesWaitWithoutBlockingUnrelatedLifecycle()
        => WithPublicationPair(async (host, client, peer, request, ct) =>
        {
            await using var pipe = await PublicationTestPipe.OpenAsync(client.Address, peer, ct);
            var release = At(550);
            await using var delay = NewPublicationDelay(request, client.Address, peer, release, At(1500), ct);
            host.OfflinePublicationDelayForTests = delay.Registration();
            pipe.Observer = delay.ObserveWrite;
            client.BeforeSendCoreAsync = (_, bytes, lane, token) => pipe.WriteAsync(bytes, lane, token);
            var selected = host.SendFileTransferControlPlaneFrameAsync(request, ct);
            Assert.Equal(3, delay.Pending); Assert.Empty(pipe.Observations);
            var unrelated = request with { Frame = request.Frame with { TransferId = "unrelated-publication" } };
            var other = await host.SendFileTransferControlPlaneFrameAsync(unrelated, ct);
            Assert.True(other.ControlQueue && other.ControlCopy && other.BulkCopy);
            Assert.False(selected.IsCompleted);
            Assert.All(pipe.Observations, write => Assert.True(write.Timestamp < release));
            var result = await selected;
            Assert.True(result.ControlQueue && result.ControlCopy && result.BulkCopy && result.AcceptedAny);
            Assert.False(result.ControlAck);
            delay.VerifyComplete(); Assert.Null(delay.Failure); Assert.Equal(0, delay.Pending);
            Assert.Equal(6, pipe.Observations.Count(w => w.Completed));
        });

    [Theory]
    [InlineData("caller_cancel")] [InlineData("peer_retired")] [InlineData("source_retired")]
    [InlineData("competing_publication")] [InlineData("stale_validator")] [InlineData("callback_failure")] [InlineData("oversize_ranges")]
    [InlineData("unsupported_frame")]
    public Task OfflinePublicationDelay_FailedOwnerRetiresEveryCopyWithoutLateWrites(string cause)
        => WithPublicationPair(async (host, client, peer, request, ct) =>
        {
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await using var pipe = await PublicationTestPipe.OpenAsync(client.Address, peer, ct);
            await using var delay = NewPublicationDelay(request, client.Address, peer, At(300), At(1000), ct);
            var current = true;
            host.OfflinePublicationDelayForTests = delay.Registration();
            pipe.Observer = write =>
            {
                if (cause == "callback_failure") { delay.Fail("callback_failure"); throw new IOException("observer_failure"); }
                delay.ObserveWrite(write);
            };
            client.BeforeSendCoreAsync = (_, bytes, lane, token) => pipe.WriteAsync(bytes, lane, token);
            var selected = host.SendFileTransferControlPlaneFrameAsync(request with { PreTransportDeliveryValidator = () => current }, caller.Token);
            Assert.Equal(3, delay.Pending);
            switch (cause)
            {
                case "caller_cancel": caller.Cancel(); break;
                case "peer_retired": case "source_retired": delay.Fail(cause); break;
                case "stale_validator": current = false; break;
                case "competing_publication":
                    await Assert.ThrowsAsync<InvalidOperationException>(() => host.SendFileTransferControlPlaneFrameAsync(request, ct)); break;
                case "oversize_ranges":
                    await Assert.ThrowsAsync<InvalidOperationException>(() => host.SendFileTransferControlPlaneFrameAsync(request with
                    { Frame = ((FileTransferStateFrameV4)request.Frame) with { MissingRanges = Enumerable.Range(0, 9)
                        .Select(i => new FileTransferRangeV4 { StartChunkIndex = i, ChunkCount = 1 }).ToArray() } }, ct)); break;
                case "unsupported_frame":
                    await Assert.ThrowsAsync<InvalidOperationException>(() => host.SendFileTransferControlPlaneFrameAsync(request with
                    { Frame = new MutablePublicationFrame { SessionId = request.Frame.SessionId, TransferId = request.Frame.TransferId } }, ct)); break;
            }
            var result = await selected;
            Assert.False(result.AcceptedAny); Assert.False(result.ControlQueue || result.ControlCopy || result.BulkCopy);
            await delay.DisposeAsync();
            Assert.Equal(0, delay.Pending); Assert.NotNull(delay.Failure);
            Assert.DoesNotContain(pipe.Observations, write => write.Completed);
            await Task.Delay(350, ct);
            Assert.DoesNotContain(pipe.Observations, write => write.Completed);
        });

    [Theory]
    [InlineData("copy_timeout")] [InlineData("profile_expiry")]
    public Task OfflinePublicationDelay_OriginalTimeoutAndExpiryIncludePostReleaseWork(string cause)
        => WithPublicationPair(async (host, client, peer, request, ct) =>
        {
            var clock = Stopwatch.StartNew();
            await using var delay = NewPublicationDelay(request, client.Address, peer, At(400),
                At(cause == "profile_expiry" ? 600 : 2500), ct);
            host.OfflinePublicationDelayForTests = delay.Registration();
            var entered = 0;
            client.BeforeSendCoreAsync = async (_, _, _, token) =>
            { Interlocked.Increment(ref entered); await Task.Delay(Timeout.Infinite, token); };
            var result = await host.SendFileTransferControlPlaneFrameAsync(request, ct);
            var elapsed = clock.ElapsedMilliseconds;
            Assert.False(result.AcceptedAny); Assert.InRange(entered, 1, 3);
            // Restarting the 750 ms clock after release would take >=1150 ms.
            Assert.InRange(elapsed, cause == "profile_expiry" ? 550 : 700, 1050);
            await delay.DisposeAsync(); Assert.Equal(0, delay.Pending);
            Assert.Throws<InvalidOperationException>(delay.VerifyComplete);
        });

    [Fact]
    public Task OfflinePublicationDelay_BoundedWaitDoesNotClaimQueuedChildWasDrained()
        => WithPublicationPair(async (host, client, peer, request, ct) =>
        {
            await using var delay = NewPublicationDelay(request, client.Address, peer, At(300), At(3000), ct);
            var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hashes = new ConcurrentDictionary<string, bool>();
            var selectedWrites = 0;
            host.OfflinePublicationDelayForTests = delay.Registration() with { Bind = item =>
            {
                hashes[item.Control.Sha256] = true; hashes[item.Bulk!.Sha256] = true;
                return delay.Bind(item);
            } };
            client.BeforeSendCoreAsync = async (_, bytes, _, token) =>
            {
                var selected = hashes.ContainsKey(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
                entered.TrySetResult();
                await unblock.Task.WaitAsync(token);
                if (selected) Interlocked.Increment(ref selectedWrites);
            };
            try
            {
                var unrelated = host.SendFileTransferControlPlaneFrameAsync(request with
                    { Frame = request.Frame with { TransferId = "blocked-unrelated" } }, ct);
                await entered.Task;
                var result = await host.SendFileTransferControlPlaneFrameAsync(request, ct);
                Assert.False(result.AcceptedAny);
                Assert.Equal(1, delay.Pending); // Original queue item is still behind unrelated work.
                var cleanup = delay.DisposeAsync().AsTask();
                Assert.False(cleanup.IsCompleted); Assert.False(delay.DisposeAsync().IsCompleted);
                unblock.TrySetResult();
                await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
                await unrelated;
                Assert.Equal(0, delay.Pending); Assert.Equal(0, selectedWrites);
            }
            finally { unblock.TrySetResult(); }
        });

    [Fact]
    public Task OfflinePublicationDelay_NonReceiverStateAndRejectedRequestDoNotInvokeBinder()
        => WithPublicationPair(async (host, _, _, request, ct) =>
        {
            host.OfflinePublicationDelayForTests = new(request.Frame.SessionId, request.Frame.TransferId,
                _ => throw new InvalidOperationException("must_not_bind"), _ => { });
            var rejected = await host.SendFileTransferControlPlaneFrameAsync(request with { PreTransportDeliveryValidator = () => false }, ct);
            Assert.False(rejected.AcceptedAny);
            // Deliberately use the same valid frame: selection is by actual request kind, not payload guessing.
            var other = await host.SendFileTransferControlPlaneFrameAsync(request with { Kind = FileTransferControlPlaneKind.FrontierRequest }, ct);
            Assert.True(other.AcceptedAny);
            var unselected = request with { Frame = ((FileTransferStateFrameV4)request.Frame) with
                { TransferId = "unselected", MissingRanges = Enumerable.Range(0, 9).Select(i => new FileTransferRangeV4
                    { StartChunkIndex = i, ChunkCount = 1 }).ToArray() } };
            Assert.True((await host.SendFileTransferControlPlaneFrameAsync(unselected, ct)).AcceptedAny);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.SendFileTransferControlPlaneFrameAsync(request, ct));
        });

    private static long At(int milliseconds) => Stopwatch.GetTimestamp() + Stopwatch.Frequency * milliseconds / 1000;
    private sealed record MutablePublicationFrame : FileTransferReceiverStateFrameV6 { public List<int> Values { get; } = []; }
    private static AttemptOfflinePublicationDelay NewPublicationDelay(FileTransferControlPlaneDeliveryRequest request,
        string source, string peer, long release, long expiry, CancellationToken ct)
        => new((FileTransferReceiverStateFrameV6)request.Frame, source, peer, release, expiry, 100000, ct);

    private async Task WithPublicationPair(Func<NknSignalingTransport, FakeNknClient, string,
        FileTransferControlPlaneDeliveryRequest, CancellationToken, Task> run)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var nonce = Guid.NewGuid().ToString("N");
            var hostClient = new FakeNknClient("host.publication." + nonce + ".address");
            var helperClient = new FakeNknClient("helper.publication." + nonce + ".address");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-publication", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-publication", helperClient.Address));
            var session = await ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(session, "publication", cts.Token);
            _ = await helper.OpenFileTransferDataSessionAsync(session, "publication", cts.Token);
            InvokePrivateMethod(host, "TrackAuthorizedFileTransferRouteHintForTests", "publication",
                FileTransferRouteResolver.PostTunaFallbackV6Token, 6, "test_publication");
            var frame = new FileTransferReceiverStateFrameV6
            {
                SessionId = session, TransferId = "publication", Epoch = 1, TransportEpoch = 2,
                ContiguousCommittedChunkIndex = 4, DurableReceivedHighestChunkIndex = 7,
                CreditUntilChunkIndexExclusive = 8, BytesCommitted = 4 * 21504,
                MissingRanges = new FileTransferRangeV4[] { new() { StartChunkIndex = 4, ChunkCount = 1 } },
            };
            var request = new FileTransferControlPlaneDeliveryRequest(FileTransferControlPlaneKind.ReceiverState, frame, "unit_publication_delay")
            { Direction = FileTransferDirection.Inbound, RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                ProtocolVersion = 6, TransportEpoch = 2, LiveRouteEpoch = 1, TransferLegGeneration = 1, BridgeRecoveryGeneration = 1 };
            await run(host, hostClient, helperClient.Address, request, cts.Token);
        }
        finally { FakeNknClient.ResetNetwork(); }
    }

    private sealed class PublicationTestPipe : IAsyncDisposable
    {
        private readonly NamedPipeServerStream writer;
        private readonly NamedPipeClientStream reader;
        private readonly SemaphoreSlim gate = new(1);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task drain;
        private readonly string source, peer;
        internal Action<AttemptPipeWriteObservation>? Observer;
        internal readonly ConcurrentQueue<AttemptPipeWriteObservation> Observations = new();
        private PublicationTestPipe(NamedPipeServerStream writer, NamedPipeClientStream reader, string source, string peer)
        {
            this.writer = writer; this.reader = reader; this.source = source; this.peer = peer;
            drain = reader.CopyToAsync(Stream.Null, lifetime.Token);
        }
        internal static async Task<PublicationTestPipe> OpenAsync(string source, string peer, CancellationToken ct)
        {
            var name = "nlink-publication-test-" + Guid.NewGuid().ToString("N");
            var writer = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var reader = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await Task.WhenAll(writer.WaitForConnectionAsync(ct), reader.ConnectAsync(ct));
                return new(writer, reader, source, peer);
            }
            catch { writer.Dispose(); reader.Dispose(); throw; }
        }
        internal async Task WriteAsync(byte[] bytes, NknBridgeChannel lane, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                await AttemptPipeFrameWriter.WriteAsync(writer, source, peer, 0, lane, bytes, null,
                    () => { if (!writer.IsConnected || !reader.IsConnected) throw new IOException("test_peer_closed"); },
                    observation => { Observer?.Invoke(observation); Observations.Enqueue(observation); }, ct);
            }
            finally { gate.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel(); writer.Dispose(); reader.Dispose();
            try { await drain; } catch (OperationCanceledException) { } catch (IOException) { } catch (ObjectDisposedException) { }
            lifetime.Dispose(); gate.Dispose();
        }
    }
}
