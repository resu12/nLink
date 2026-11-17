using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("current")]
    [InlineData("retired_before_completion")]
    [InlineData("retired_after_completion")]
    [InlineData("settled_before_completion")]
    [InlineData("data_session_replaced")]
    [InlineData("delivery_not_accepted")]
    [InlineData("delivery_not_peer_visible")]
    [Trait("FileTransferRuntimeSuite", "FallbackFrontierDispatchCompletion")]
    public async Task FallbackFrontierDispatch_CompletionIsAppliedBeforePostDeliveryObservation(string boundary)
    {
        var transferId = "frontier_dispatch_" + Guid.NewGuid().ToString("N");
        var context = CreatePostTunaFallbackV6InboundContext(transferId);
        var sessionId = GetPrivateProperty<string>(context, "SessionId");
        using var service = new SessionFileTransferService();
        using var transport = new FrontierDispatchCompletionTransport(sessionId);
        transport.Accepted = boundary != "delivery_not_accepted";
        transport.PeerVisible = boundary != "delivery_not_peer_visible";
        service.AttachTransport(transport);
        var serviceType = typeof(SessionFileTransferService);
        SetPrivateProperty(context, "V6DestinationMode", Enum.Parse(
            serviceType.GetNestedType("V6ReceiveDestinationMode", BindingFlags.NonPublic)!, "SparseSeekable"));
        SetPrivateProperty(context, "V6FrontierStallChunkIndex", 100);
        SetPrivateProperty(context, "V6FrontierStallStartedUtc", DateTimeOffset.UtcNow.AddSeconds(-5));
        serviceType.GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        var logStart = GetOperationalLogLength();
        object? contract = null;
        bool? dispatchedAtObserver = null;
        string? logAtObserver = null;
        var observations = 0;
        transport.BeforeCompletion = () =>
        {
            contract = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract")!;
            Assert.NotNull(contract);
            if (boundary == "retired_before_completion") { Retire(); }
            if (boundary == "settled_before_completion")
            {
                var settle = serviceType.GetMethod("TrySettleFallbackFrontierRepairContract", BindingFlags.Static | BindingFlags.NonPublic)!;
                settle.Invoke(null, [contract, Enum.Parse(settle.GetParameters()[1].ParameterType, "StaleRejected"), "unit_source_retirement", null]);
            }
            if (boundary == "data_session_replaced")
            {
                SetPrivateProperty(context, "DataSession", new CompletingDataSession(sessionId, transferId));
            }
        };
        transport.AfterCompletion = () =>
        {
            observations++;
            dispatchedAtObserver = GetPrivateProperty<bool>(GetPrivatePropertyValue(contract!, "Lifecycle")!, "DispatchObserved");
            logAtObserver = ReadOperationalLogTail(logStart);
            if (boundary == "retired_after_completion") { Retire(); }
        };

        var sent = await Assert.IsAssignableFrom<Task<bool>>(serviceType.GetMethod("SendInboundV6FrontierRequestAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, "frontier_stall_grace_elapsed", true]));
        var expectedDispatch = boundary is "current" or "retired_after_completion";
        Assert.NotNull(contract);
        var lifecycle = GetPrivatePropertyValue(contract!, "Lifecycle")!;
        Assert.Equal(expectedDispatch, sent);
        Assert.Equal(expectedDispatch, GetPrivateProperty<bool>(lifecycle, "DispatchObserved"));
        if (expectedDispatch)
        {
            Assert.Equal(1, observations);
            Assert.True(dispatchedAtObserver, "Accepted dispatch was still pending when post-delivery observation began.");
            Assert.Contains("event=filetransfer_fallback_frontier_repair_dispatched;", logAtObserver!, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_frontier_request_sent;", logAtObserver!, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("event=filetransfer_fallback_frontier_repair_dispatched;", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_frontier_request_sent;", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
        }
        if (boundary is "retired_before_completion" or "retired_after_completion")
        {
            Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
            Assert.Equal("StaleRejected", GetPrivatePropertyValue(lifecycle, "SettlementOutcome")!.ToString());
            Assert.Equal("unit_source_retirement", GetPrivatePropertyValue(lifecycle, "SettlementReason"));
        }
        Assert.Equal(1, transport.SendCount);
        Assert.Equal(100, GetPrivateProperty<int>(context, "NextChunkIndex"));
        Assert.Equal(255, GetPrivateProperty<int>(context, "PullHighestReceivedChunkIndex"));

        void Retire()
        {
            // The fixture retires the exact contract at the selected callback
            // boundary; it does not claim this is a real Tuna handoff receipt.
            lock (GetServiceGate(service))
            {
                var retire = serviceType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(method =>
                    method.Name == "SettleAndClearFallbackFrontierRepairContractLocked" &&
                    method.GetParameters()[0].ParameterType == context.GetType());
                var outcome = Enum.Parse(retire.GetParameters()[1].ParameterType, "StaleRejected");
                retire.Invoke(null, [context, outcome, "unit_source_retirement"]);
            }
        }
    }

    private sealed class FrontierDispatchCompletionTransport(string sessionId) :
        LoopbackFileTransferTransport(sessionId), IFileTransferControlPlaneDeliveryTransport,
        IFileTransferPostTunaFallbackControlPlanePressureObserver
    {
        public Action? BeforeCompletion { get; set; }
        public Action? AfterCompletion { get; set; }
        public bool Accepted { get; set; } = true;
        public bool PeerVisible { get; set; } = true;
        public int SendCount { get; private set; }

        public Task<FileTransferControlPlaneDeliveryResult> SendFileTransferControlPlaneFrameAsync(
            FileTransferControlPlaneDeliveryRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.True(request.PreTransportDeliveryValidator?.Invoke());
            SendCount++;
            BeforeCompletion?.Invoke();
            return Task.FromResult(new FileTransferControlPlaneDeliveryResult(request.Kind, request.Frame.TransferId,
                request.Frame.SessionId, "unit-frontier-completion", ControlQueue: true, ControlAck: false,
                ControlCopy: Accepted, BulkCopy: false, PeerVisibleAny: PeerVisible, AcceptedAny: Accepted,
                ControlQueueErrorName: "", ControlAckErrorName: "", ControlCopyErrorName: "", BulkCopyErrorName: ""));
        }

        public void ObservePostTunaFallbackControlPlanePressure(FileTransferPostTunaFallbackControlPlanePressure pressure)
            => AfterCompletion?.Invoke();

        public void ClearPostTunaFallbackControlPlanePressure(string transferId, string reason)
            => AfterCompletion?.Invoke();
    }
}
