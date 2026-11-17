using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackTailChurn")]
    public async Task ReceiverState_LateDeliveryAfterRouteReplacementCannotPublishOrConsumeProof()
    {
        using var service = new SessionFileTransferService();
        const string transferId = "delayed_receiver_state_after_route_replacement";
        var sessionId = $"session_{transferId}";
        using var transport = new DeferredControlPlaneCompletionLoopbackTransport(sessionId);
        service.AttachTransport(transport);
        var context = CreatePostTunaFallbackV6InboundContext(transferId);
        var serviceType = typeof(SessionFileTransferService);
        serviceType.GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        var leg = GetCurrentContextLeg(context);
        leg.State = FileTransferLegState.Active;
        leg.CanSendData = true;
        leg.CheckpointRequestId = null;
        SetPrivateProperty(context, "CurrentFallbackCheckpointLease", null);
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", false);
        // No sparse gap is needed for this send-completion contract; avoid arming unrelated repair timers.
        SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 99);

        var logStart = GetOperationalLogLength();
        var send = (Task<bool>)serviceType.GetMethod("SendInboundV6ReceiverStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "chunk_batch_committed", true, false, null])!;
        await transport.RequestCaptured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(transport.LastValidationAccepted);

        SetPrivateProperty(context, "RouteSelection", FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4));
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV4);
        var authorityBeforeCompletion = GetPrivatePropertyValue(context, "CoordinatorPersistentState");
        var completionLogStart = GetOperationalLogLength();
        transport.ReleaseCompletion.TrySetResult();
        Assert.False(await send.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(authorityBeforeCompletion, GetPrivatePropertyValue(context, "CoordinatorPersistentState"));
        Assert.Equal(100, GetPrivateProperty<int>(context, "NextChunkIndex"));

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_runtime_owner_stale_update_rejected;", log, StringComparison.Ordinal);
        Assert.Contains(log.Split(Environment.NewLine), line =>
            line.Contains("event=filetransfer_control_plane_delivery_observed;", StringComparison.Ordinal) &&
            line.Contains("direction=inbound; kind=receiver_state;", StringComparison.Ordinal));
        var completion = ReadOperationalLogTail(completionLogStart);
        Assert.DoesNotContain("event=filetransfer_v6_receiver_state_sent;", completion, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_fallback_tail_terminal_proof_consumed;", completion, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_fallback_frontier_repair_progress_state_published;", completion, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_control_plane_pressure_marked;", completion, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_fallback_checkpoint_accepted;", completion, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_route_published;", completion, StringComparison.Ordinal);
    }
}
