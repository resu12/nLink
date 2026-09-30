using System.Collections.Concurrent;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    internal static Task WithAuthenticatedPublicationPairAsync(
        Func<SessionFileTransferService, SessionFileTransferService, NknSignalingTransport,
            ConcurrentQueue<byte[]>, Action, CancellationToken, Task> test)
        => WithPublicPreparationPairAsync(false, async pair =>
        {
            pair.PayerLane.FilePeer = pair.DialerLane;
            pair.DialerLane.FilePeer = pair.PayerLane;
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            await pair.WaitReady();
            await WaitPreparationRequestIdle(pair);
            var gate = new object();
            var pending = new Queue<(NknBridgeChannel Lane, byte[] Bytes)>();
            var captured = new ConcurrentQueue<byte[]>();
            var released = false;
            pair.PayerLane.ForwardBytesForTests = (lane, bytes, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (!released)
                    {
                        Assert.True(pending.Sum(packet => packet.Bytes.Length) + bytes.Length <= 4 * 1024 * 1024);
                        var copy = bytes.ToArray();
                        pending.Enqueue((lane, copy));
                        captured.Enqueue(copy);
                    }
                    else { pair.DialerLane.ReceivePeerBytesForTests(lane, bytes); }
                }
                return Task.FromResult(true);
            };
            // Queue acceptance lets the original NKN send finish and log its
            // own authenticated capture. No receiver payload arrives yet.
            void Release()
            {
                lock (gate)
                {
                    Assert.False(released);
                    released = true;
                    while (pending.TryDequeue(out var packet))
                        pair.DialerLane.ReceivePeerBytesForTests(packet.Lane, packet.Bytes);
                }
            }
            await test(pair.PayerService, pair.DialerService, pair.Payer, captured, Release, pair.Token);
        });
}
