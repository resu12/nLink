using System.Reflection;
using NLink.App.ViewModels;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    public async Task TunaActivationPauseEligibility_NotifiesWithoutPayloadProgress(FileTransferDirection direction)
    {
        var transferId = "pause_notification_" + direction;
        using var service = new SessionFileTransferService();
        var context = direction == FileTransferDirection.Outbound
            ? CreateRegularNknV4OutboundContext(transferId, 0, 0, 0, DateTimeOffset.UtcNow)
            : CreateRegularNknV4InboundContext(transferId, nextChunkIndex: 0, durableHighestChunkIndex: -1);
        using var data = new PauseNotificationDataSession("session_" + transferId, transferId);
        SetPrivateProperty(context, "DataSession", data);
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
        if (direction == FileTransferDirection.Inbound)
            SeedCurrentRuntimeOwnerForTest(context, direction, "pause_notification");
        typeof(SessionFileTransferService).GetField(direction == FileTransferDirection.Outbound ? "outboundTransfer" : "inboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        var serviceGate = typeof(SessionFileTransferService).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var events = new List<(bool Blocked, bool ShowPause, long Bytes, bool UnderLock)>();
        service.TransferChanged += (_, args) =>
        {
            var snapshot = direction == FileTransferDirection.Outbound ? args.Snapshot.Outbound! : args.Snapshot.Inbound!;
            events.Add((snapshot.IsPauseBlockedByTransport, FileTransferPanelItemViewModel.FromSnapshot(snapshot)!.ShowPause,
                snapshot.BytesTransferred, Monitor.IsEntered(serviceGate)));
        };
        try
        {
            await Notify(false, "tuna_activation_negotiating");
            Assert.Equal((true, false, 0L, false), Assert.Single(events));
            Assert.Null(await service.PauseTransferAsync(transferId, "ui_pause", CancellationToken.None));
            await Notify(false, "tuna_activation_negotiating");
            Assert.Single(events);
            await Notify(true, "tuna_activation_failed_regular_v4_resumed");
            Assert.Equal((false, true, 0L, false), events[1]);
            Assert.Equal(2, events.Count);
            await Notify(true, "tuna_activation_failed_regular_v4_resumed");
            Assert.Equal(2, events.Count);
        }
        finally
        {
            TryCancelContextLifetime(context);
        }

        async Task Notify(bool available, string reason)
        {
            data.IsAvailable = available;
            await (Task)typeof(SessionFileTransferService).GetMethod("HandleDataSessionAvailabilityChangedAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
                [data, new FileTransferDataSessionAvailabilityChangedEventArgs(available, reason, false)])!;
        }
    }

    private sealed class PauseNotificationDataSession(string sessionId, string transferId) : IFileTransferDataSession
    {
        public string SessionId => sessionId;
        public string TransferId => transferId;
        public bool IsAvailable { get; set; } = true;
        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged { add { } remove { } }
        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct) => Task.CompletedTask;
        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
