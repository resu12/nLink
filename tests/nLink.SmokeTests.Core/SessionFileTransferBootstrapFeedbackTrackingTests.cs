using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BootstrapFeedbackTracking_TerminalFailureRechecksExactOperationAtMutation(bool replace)
    {
        const string transferId = "bootstrap_feedback_terminal_binding";
        using var service = new SessionFileTransferService();
        using var session = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var replacement = new InitialBootstrapRecordingDataSession(session.SessionId, transferId);
        using var bridge = new BootstrapPhaseTestBridge(session);
        var issuer = FileTransferBootstrapCorrelation.CreateIssuer("peer", session.SessionId, transferId, FileTransferDirection.Outbound);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, session,
            beforeActivation: context => PinBootstrapServiceAgreement(context, issuer));
        var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var physical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PhysicalSubmissionForTest = (_, _) => new(delivery.Task, physical.Task);
        var sending = SendBootstrapFeedbackForTest(service, context);
        var operation = Assert.Single(BootstrapFeedbackOperationsForTest(context));
        var before = GetPrivateProperty<FileTransferTransferState>(context, "State");
        try
        {
            if (replace)
            {
                SetPrivateProperty(context, "DataSession", replacement);
                SetPrivateProperty(context, "DataSessionGeneration", GetPrivateProperty<long>(context, "DataSessionGeneration") + 1);
            }
            await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
                .GetMethod("TransitionInboundToTerminalCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, FileTransferTransferState.Failed, "invalid_state", "modeled feedback failure",
                    false, null, null, CancellationToken.None, operation, null]));
            Assert.Equal(replace ? before : FileTransferTransferState.Failed, GetPrivateProperty<FileTransferTransferState>(context, "State"));
            Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
        }
        finally
        {
            delivery.TrySetResult(); physical.TrySetResult();
            Assert.False(await sending.WaitAsync(TimeSpan.FromSeconds(3)));
            await BootstrapFeedbackSettledForTest(operation).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static object[] BootstrapFeedbackOperationsForTest(object context)
        => ((IDictionary)GetPrivatePropertyValue(context, "ActiveBootstrapFeedbackOperations")!).Values.Cast<object>().ToArray();

    private static Task BootstrapFeedbackSettledForTest(object operation)
        => GetPrivateProperty<TaskCompletionSource>(operation, "Settled").Task;

    private static Task<bool> SendBootstrapFeedbackForTest(SessionFileTransferService service, object context)
        => Assert.IsAssignableFrom<Task<bool>>(typeof(SessionFileTransferService)
            .GetMethod("SendInboundV4StateCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "bootstrap_feedback_tracking_test", false, false, false, true, null, null]));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public async Task BootstrapFeedbackTracking_ServiceOwnsAdmissionPhysicalAndLocalFinalization(int variant)
    {
        const string transferId = "bootstrap_feedback_tracking";
        using var service = new SessionFileTransferService();
        using var session = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var bridge = new BootstrapPhaseTestBridge(session);
        var issuer = FileTransferBootstrapCorrelation.CreateIssuer("peer", session.SessionId, transferId, FileTransferDirection.Outbound);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, session,
            beforeActivation: context => PinBootstrapServiceAgreement(context, issuer));
        var priorSubmissionBarrier = SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests;
        var priorFinalizationBarrier = SessionFileTransferService.BootstrapFeedbackBeforeFinalizationForTests;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var physical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var successorPhysical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = 0;
        session.PhysicalSubmissionForTest = (_, _) =>
        {
            submitted++;
            return submitted > 1 ? new(Task.CompletedTask, successorPhysical.Task)
                : new(delivery.Task, physical.Task);
        };
        if (variant is 0 or 1 or 2 or 3 or 5) { delivery.TrySetResult(); }
        if (variant is 0 or 2 or 3 or 5) { physical.TrySetResult(); }
        if (variant is 2 or 3)
        {
            SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests = id =>
            {
                Assert.Equal(transferId, id);
                entered.TrySetResult();
                return release.Task;
            };
        }
        if (variant == 5)
        {
            SessionFileTransferService.BootstrapFeedbackBeforeFinalizationForTests = id =>
            {
                Assert.Equal(transferId, id);
                entered.TrySetResult();
                return release.Task;
            };
        }
        Task<bool>? sending = null;
        try
        {
            if (variant == 6)
            {
                SetPrivateProperty(context, "DataSession", new RecordingDataSession(session.SessionId, transferId));
                var epoch = GetPrivateProperty<int>(context, "V4StateEpoch");
                Assert.False(await SendBootstrapFeedbackForTest(service, context));
                Assert.Equal(epoch, GetPrivateProperty<int>(context, "V4StateEpoch"));
                Assert.Empty(BootstrapFeedbackOperationsForTest(context));
                Assert.Equal(0, submitted);
                return;
            }
            var before = CreateCoordinatorStateForTest(context);
            var sentCount = GetPrivateProperty<int>(context, "PullV4StateSentCountTotal");
            sending = SendBootstrapFeedbackForTest(service, context);
            if (variant == 0)
            {
                Assert.True(await sending.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.Empty(BootstrapFeedbackOperationsForTest(context));
                return;
            }
            if (variant is 2 or 3 or 5) { await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
            var operation = Assert.Single(BootstrapFeedbackOperationsForTest(context));
            Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
            if (variant is 2 or 3)
            {
                Assert.Null(GetPrivatePropertyValue(operation, "Submission"));
                Assert.Equal(0, submitted);
            }
            if (variant == 1) { Assert.True(await sending.WaitAsync(TimeSpan.FromSeconds(3))); }
            if (variant == 5) { Assert.True(physical.Task.IsCompleted); Assert.False(sending.IsCompleted); }
            if (variant is 3 or 4)
            {
                SetPrivateProperty(context, "DataSession", new InitialBootstrapRecordingDataSession(session.SessionId, transferId));
                SetPrivateProperty(context, "DataSessionGeneration", 2L);
            }
            if (variant == 7)
            {
                SetPrivateProperty(context, "PullTransportPaused", true);
                SetPrivateProperty(context, "PullTransportPausedSinceUtc", DateTimeOffset.UtcNow);
            }
            object? successor = null;
            object? successorOperation = null;
            if (variant == 8)
            {
                successor = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, session,
                    beforeActivation: next => PinBootstrapServiceAgreement(next, issuer));
                Assert.True(await SendBootstrapFeedbackForTest(service, successor));
                successorOperation = Assert.Single(BootstrapFeedbackOperationsForTest(successor));
                Assert.Equal(GetPrivateProperty<long>(operation, "Generation"), GetPrivateProperty<long>(successorOperation, "Generation"));
            }
            release.TrySetResult();
            delivery.TrySetResult();
            physical.TrySetResult();
            Assert.Equal(variant is not (3 or 4 or 7 or 8), await sending.WaitAsync(TimeSpan.FromSeconds(3)));
            await BootstrapFeedbackSettledForTest(operation).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(BootstrapFeedbackOperationsForTest(context));
            if (variant is 3 or 4 or 7 or 8) { Assert.Equal(sentCount, GetPrivateProperty<int>(context, "PullV4StateSentCountTotal")); }
            if (variant == 3) { Assert.Equal(0, submitted); }
            if (successor is not null)
            {
                Assert.Same(successorOperation, Assert.Single(BootstrapFeedbackOperationsForTest(successor)));
                Assert.False(BootstrapFeedbackSettledForTest(successorOperation!).IsCompleted);
                successorPhysical.TrySetResult();
                await BootstrapFeedbackSettledForTest(successorOperation!).WaitAsync(TimeSpan.FromSeconds(3));
            }
            Assert.Equal(before.CurrentRoutePublicationToken, CreateCoordinatorStateForTest(context).CurrentRoutePublicationToken);
            Assert.Empty(bridge.Executions);
        }
        finally
        {
            release.TrySetResult(); delivery.TrySetResult(); physical.TrySetResult(); successorPhysical.TrySetResult();
            if (sending is not null) { await sending.WaitAsync(TimeSpan.FromSeconds(3)); }
            SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests = priorSubmissionBarrier;
            SessionFileTransferService.BootstrapFeedbackBeforeFinalizationForTests = priorFinalizationBarrier;
        }
    }
}
