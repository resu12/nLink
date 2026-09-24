using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferInitialFallbackDataSessionTests : SessionFileTransferServiceTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InitialFallbackDataSession_ServiceAppliesBeforeCallbacksAndRejectsWithoutEffects(bool outbound)
    {
        const string sessionId = "initial_binding_service";
        const string transferId = "initial_binding_transfer";
        using var senderTransport = new LoopbackFileTransferTransport(sessionId) { IsFileTunaActiveForRouteSelection = false, IsPostTunaFileFallbackActiveForRouteSelection = true };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId) { IsFileTunaActiveForRouteSelection = false, IsPostTunaFileFallbackActiveForRouteSelection = true };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new("binding.bin", 4, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[4])), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        var service = outbound ? sender : receiver;
        var type = typeof(SessionFileTransferService);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var context = type.GetField(outbound ? "outboundTransfer" : "inboundTransfer", flags)!.GetValue(service)!;
        var replace = type.GetMethod(outbound ? "ReplaceOutboundDataSessionLocked" : "ReplaceInboundDataSessionLocked", flags)!;
        var gate = type.GetField("gate", flags)!.GetValue(service)!;
        var capture = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(method =>
            method.Name == "CreateCoordinatorStateLocked" && method.GetParameters()[0].ParameterType == context.GetType());
        FileTransferCoordinatorState State() => Assert.IsType<FileTransferCoordinatorState>(capture.Invoke(null, [context]));
        bool Replace(IFileTransferDataSession session) { lock (gate) { return Assert.IsType<bool>(replace.Invoke(service, [context, session])); } }
        var before = State();
        var fingerprint = FileTransferCoordinator.CalculateSnapshotStateFingerprint(before);
        using var foreign = new BindingSession("foreign", transferId);
        Assert.False(Replace(foreign));
        Assert.Equal(fingerprint, FileTransferCoordinator.CalculateSnapshotStateFingerprint(State()));
        Assert.Equal(0, foreign.Subscriptions);
        Assert.Equal(0, foreign.Sends);
        Assert.Equal(0, foreign.Disposals);

        var transferState = context.GetType().GetProperty("State")!;
        var oldState = transferState.GetValue(context);
        transferState.SetValue(context, FileTransferTransferState.Canceled);
        using var rejected = new BindingSession(sessionId, transferId);
        var terminalFingerprint = FileTransferCoordinator.CalculateSnapshotStateFingerprint(State());
        Assert.False(Replace(rejected));
        Assert.Equal(terminalFingerprint, FileTransferCoordinator.CalculateSnapshotStateFingerprint(State()));
        Assert.Equal(0, rejected.Subscriptions);
        Assert.Equal(0, rejected.Sends);
        Assert.Equal(0, rejected.Disposals);
        transferState.SetValue(context, oldState);

        using var accepted = new BindingSession(sessionId, transferId);
        accepted.OnSubscribe = () =>
        {
            Assert.Same(accepted, context.GetType().GetProperty("DataSession")!.GetValue(context));
            Assert.Equal(1, State().CurrentDataSessionGeneration);
            Assert.Equal(before.PersistentState.SnapshotStateGeneration + 1, State().PersistentState.SnapshotStateGeneration);
        };
        Assert.True(Replace(accepted));
        var attached = State();
        Assert.Equal(before.CurrentRuntimeOwner, attached.CurrentRuntimeOwner);
        Assert.Equal(before.CurrentRoutePublicationToken, attached.CurrentRoutePublicationToken);
        Assert.Equal(transferId + ":1", attached.CurrentDataSessionIdentity);
        Assert.Equal(1, accepted.Subscriptions);
        Assert.True(Replace(accepted));
        Assert.Equal(1, accepted.Subscriptions);
        Assert.Equal(0, accepted.Sends);
        Assert.Equal(attached.PersistentState, State().PersistentState);
    }

    private sealed class BindingSession(string sessionId, string transferId) : IFileTransferDataSession
    {
        public string SessionId => sessionId;
        public string TransferId => transferId;
        public bool IsAvailable => true;
        public int Subscriptions { get; private set; }
        public int Sends { get; private set; }
        public int Disposals { get; private set; }
        public Action? OnSubscribe { get; set; }
        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add { Subscriptions++; OnSubscribe?.Invoke(); }
            remove { Subscriptions--; }
        }
        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct) => throw new InvalidOperationException("No receive pump is started by attachment.");
        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct) => throw new InvalidOperationException("No receive pump is started by attachment.");
        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct) { Sends++; return Task.CompletedTask; }
        public void Dispose() => Disposals++;
    }
}
