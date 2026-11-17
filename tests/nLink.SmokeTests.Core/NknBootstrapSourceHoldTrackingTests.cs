using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task BootstrapSourceHold_PhysicalFeedbackCompletionWaitsForSecondaryCopy(int variant)
    {
        FakeNknClient.ResetNetwork();
        var bulkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBulk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bulkDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.bootstrap.feedbacktracking.address");
            var helperClient = new FakeNknClient("helper.bootstrap.feedbacktracking.address");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-feedback-tracking", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-feedback-tracking", helperClient.Address));
            var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            using var session = await host.OpenFileTransferDataSessionAsync(sessionId, "source_hold_feedback_tracking", cts.Token);
            hostClient.BeforeSendCoreAsync = async (_, _, lane, _) =>
            {
                var blockedLane = variant == 1 ? NknBridgeChannel.Control : NknBridgeChannel.Bulk;
                if (lane != blockedLane)
                {
                    if (variant == 4) { throw new IOException("modeled_primary_failure"); }
                    return;
                }
                bulkEntered.TrySetResult();
                await releaseBulk.Task;
                if (variant is 2 or 4) { throw new IOException("modeled_secondary_failure"); }
            };
            helperClient.MessageReceived += (_, message) =>
            {
                if (!message.IsTopic && message.Channel == (variant == 1 ? NknBridgeChannel.Control : NknBridgeChannel.Bulk) &&
                    EnvelopeCodec.TryDeserialize(message.Payload, out Envelope envelope) && envelope.Type == MsgType.FileTransferDataFrame)
                {
                    bulkDelivered.TrySetResult();
                }
            };
            var frame = new FileTransferStateFrameV4
                {
                    SessionId = sessionId, TransferId = session.TransferId, Epoch = 1,
                    ContiguousCommittedChunkIndex = 10, DurableReceivedHighestChunkIndex = 12,
                    CreditUntilChunkIndexExclusive = 64, BytesCommitted = 21_504L * 10,
                };
            var physicalSession = Assert.IsAssignableFrom<IFileTransferPhysicalSendSubmissionSession>(session);
            if (variant == 5)
            {
                session.Dispose();
                Assert.Throws<ObjectDisposedException>(() => physicalSession.SubmitPhysicalSend(frame, authority: null, cts.Token));
                Assert.False(bulkEntered.Task.IsCompleted);
                return;
            }
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var submission = physicalSession.SubmitPhysicalSend(frame, authority: null, sendCts.Token);
            try
            {
                await bulkEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (variant != 4) { await submission.DeliveryTask.WaitAsync(TimeSpan.FromSeconds(2)); }
                else { Assert.False(submission.DeliveryTask.IsCompleted); }
                if (variant == 3) { sendCts.Cancel(); }
                Assert.Null(submission.BootstrapCopies);
                Assert.False(bulkDelivered.Task.IsCompleted);
                Assert.False(submission.PhysicalCompletionTask.IsCompleted,
                    "source_hold_feedback_physical_completion_early: delivery_completed=true; secondary_copy_still_blocked=true");
            }
            finally
            {
                releaseBulk.TrySetResult();
                if (variant is 0 or 1) { await bulkDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
                await submission.PhysicalCompletionTask.WaitAsync(TimeSpan.FromSeconds(2));
                if (variant == 4) { await Assert.ThrowsAnyAsync<Exception>(() => submission.DeliveryTask); }
                else { Assert.True(submission.DeliveryTask.IsCompletedSuccessfully); }
            }
        }
        finally
        {
            releaseBulk.TrySetResult();
            FakeNknClient.ResetNetwork();
        }
    }
}
