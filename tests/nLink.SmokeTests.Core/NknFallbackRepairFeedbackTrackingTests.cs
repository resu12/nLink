using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    [Theory]
    [InlineData("success")] [InlineData("failure")] [InlineData("cancel")] [InlineData("rejected")] [InlineData("started")]
    public async Task FallbackRepairFeedback_PhysicalCompletionIncludesBoundedControlQueue(string variant)
    {
        FakeNknClient.ResetNetwork();
        var queuedSendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseQueuedSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.fallback.feedbacktracking.address");
            var helperClient = new FakeNknClient("helper.fallback.feedbacktracking.address");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-fallback-feedback", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-fallback-feedback", helperClient.Address));
            var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            using var session = await host.OpenFileTransferDataSessionAsync(sessionId, "fallback_feedback_tracking", cts.Token);
            var drainerFlag = typeof(NknSignalingTransport).GetField("controlOutboundDrainerActive", BindingFlags.Instance | BindingFlags.NonPublic)!;
            drainerFlag.SetValue(host, true); // Keep the real queue pending; peer copies can finish normally.
            if (variant == "failure")
            {
                hostClient.BeforeSendCoreAsync = (_, _, _, _) => throw new IOException("modeled_copy_failure");
            }
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var frame = new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId, TransferId = session.TransferId, Epoch = 1, TransportEpoch = 3,
                ContiguousCommittedChunkIndex = 100, DurableReceivedHighestChunkIndex = 255,
                CreditUntilChunkIndexExclusive = 256, BytesCommitted = 21_504L * 100,
            };
            var request = new FileTransferControlPlaneDeliveryRequest(FileTransferControlPlaneKind.ReceiverState, frame, "test")
            {
                PeerVisibleRequired = true, CopyTimeout = TimeSpan.FromMilliseconds(40),
                PreTransportDeliveryValidator = () => variant != "rejected",
            };
            var submission = Assert.IsAssignableFrom<IFileTransferControlPlanePhysicalSendSubmissionTransport>(host)
                .SubmitFileTransferControlPlanePhysicalSend(request, sendCts.Token);
            var result = await submission.DeliveryTask.WaitAsync(TimeSpan.FromSeconds(2));
            if (variant == "rejected")
            {
                await submission.PhysicalCompletionTask.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(result.AcceptedAny);
                return;
            }
            Assert.Equal(variant != "failure", result.AcceptedAny);
            Assert.False(result.ControlQueue);
            if (variant == "cancel") { sendCts.Cancel(); }
            Assert.False(submission.PhysicalCompletionTask.IsCompleted,
                "Delivery completion/cancellation must not settle the still-pending queue copy.");
            if (variant == "started")
            {
                hostClient.BeforeSendCoreAsync = async (_, _, _, _) =>
                {
                    queuedSendEntered.TrySetResult();
                    await releaseQueuedSend.Task;
                };
            }
            var queue = typeof(NknSignalingTransport).GetField("controlOutboundQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            var drain = Assert.IsAssignableFrom<Task>(queue.GetType().GetMethod("DrainAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(queue, null));
            if (variant == "started")
            {
                await queuedSendEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                sendCts.Cancel(); // Model a transport that does not settle an already-started send on cancellation.
                Assert.False(submission.PhysicalCompletionTask.IsCompleted);
                releaseQueuedSend.TrySetResult();
            }
            await drain.WaitAsync(TimeSpan.FromSeconds(2));
            await submission.PhysicalCompletionTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { releaseQueuedSend.TrySetResult(); FakeNknClient.ResetNetwork(); }
    }
}
