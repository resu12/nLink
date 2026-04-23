using System.Diagnostics;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed class NknTransportDisposeTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void NknSignalingTransport_Dispose_ForcesClientDispose_WhenDisconnectHangs()
    {
        var previousTimeout = NknSignalingTransport.DisposeDisconnectTimeoutOverrideForTests;
        NknSignalingTransport.DisposeDisconnectTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);

        try
        {
            var client = new HangingDisconnectClient("dispose-timeout.addr");
            var transport = new NknSignalingTransport(
                client,
                NknTransportOptions.Load(),
                new NknIdentity("dispose-timeout", "dispose-timeout.addr"));

            var stopwatch = Stopwatch.StartNew();
            transport.Dispose();
            stopwatch.Stop();

            Assert.True(client.DisconnectCalled);
            Assert.True(client.DisposeCalled);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
        finally
        {
            NknSignalingTransport.DisposeDisconnectTimeoutOverrideForTests = previousTimeout;
        }
    }

    private sealed class HangingDisconnectClient : INknClient
    {
        private readonly TaskCompletionSource disconnectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HangingDisconnectClient(string address)
        {
            Address = address;
            MediaAddress = address + ".media";
            BulkAddress = address + ".bulk";
        }

        public string Address { get; }

        public string MediaAddress { get; }

        public string BulkAddress { get; }

        public bool DisconnectCalled { get; private set; }

        public bool DisposeCalled { get; private set; }

        public event EventHandler<NknIncomingMessage>? MessageReceived;

        public event EventHandler? Disconnected;

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public Task DisconnectAsync()
        {
            DisconnectCalled = true;
            return disconnectTcs.Task;
        }

        public Task SubscribeAsync(string topic, CancellationToken ct) => Task.CompletedTask;

        public Task UnsubscribeAsync(string topic) => Task.CompletedTask;

        public Task PublishAsync(string topic, byte[] payload, CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(string destination, byte[] payload, CancellationToken ct) => Task.CompletedTask;

        public Task SendMediaAsync(string destination, byte[] payload, CancellationToken ct) => Task.CompletedTask;

        public Task SendBulkAsync(string destination, byte[] payload, CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
            DisposeCalled = true;
            disconnectTcs.TrySetResult();
        }
    }
}
