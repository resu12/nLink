using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("current")] [InlineData("stream")] [InlineData("publication")]
    public async Task FallbackRepairFeedback_FrontierDispatchRetainsExactPhysicalOperation(string replacement)
    {
        var transport = new PhysicalFallbackFeedbackTransport("session_fallback_repair_write");
        using var fixture = new FallbackRepairWriteFixture(transport: transport);
        var logStart = GetOperationalLogLength();
        SetPrivateProperty(fixture.Context, "V6FrontierStallChunkIndex", 100);
        SetPrivateProperty(fixture.Context, "V6FrontierStallStartedUtc", DateTimeOffset.UtcNow.AddSeconds(-5));
        var send = Assert.IsAssignableFrom<Task<bool>>(typeof(SessionFileTransferService)
            .GetMethod("SendInboundV6FrontierRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Service, [fixture.Context, "frontier_stall_grace_elapsed", true]));
        await transport.RequestCaptured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var operation = Assert.Single(FallbackRepairFeedbackOperationsForTest(fixture.Context));
        if (replacement != "current") { fixture.Invalidate(replacement); }
        var before = GetPrivatePropertyValue(fixture.Context, "CoordinatorPersistentState");
        transport.ReleaseDelivery.TrySetResult();
        var sent = await send.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(sent == (replacement == "current"), ReadOperationalLogTail(logStart));
        if (replacement != "current") { Assert.Equal(before, GetPrivatePropertyValue(fixture.Context, "CoordinatorPersistentState")); }
        Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
        transport.ReleasePhysical.TrySetResult();
        await BootstrapFeedbackSettledForTest(operation).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(FallbackRepairFeedbackOperationsForTest(fixture.Context));
    }

    [Fact]
    public async Task FallbackRepairFeedback_PhysicalSettlementStillWaitsForLocalFinalization()
    {
        var transport = new PhysicalFallbackFeedbackTransport("session_fallback_repair_write");
        using var fixture = new FallbackRepairWriteFixture(transport: transport);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionFileTransferService.FallbackRepairFeedbackBeforeFinalizationForTests = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        try
        {
            var send = SendFallbackRepairStateForTest(fixture);
            await transport.RequestCaptured.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var operation = Assert.Single(FallbackRepairFeedbackOperationsForTest(fixture.Context));
            transport.ReleasePhysical.TrySetResult();
            transport.ReleaseDelivery.TrySetResult();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(send.IsCompleted);
            Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
            Assert.Single(FallbackRepairFeedbackOperationsForTest(fixture.Context));
            release.TrySetResult();
            Assert.True(await send.WaitAsync(TimeSpan.FromSeconds(2)));
            await BootstrapFeedbackSettledForTest(operation).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(FallbackRepairFeedbackOperationsForTest(fixture.Context));
        }
        finally
        {
            release.TrySetResult();
            SessionFileTransferService.FallbackRepairFeedbackBeforeFinalizationForTests = null;
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FallbackRepairFeedback_DataSessionSubmissionRetainsActualPhysicalSettlement(bool failDelivery)
    {
        using var fixture = new FallbackRepairWriteFixture();
        var releasePhysical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.PhysicalSubmissionForTest = (_, _) => new(
            failDelivery ? Task.FromException(new IOException("modeled_delivery_failure")) : Task.CompletedTask, releasePhysical.Task);
        try
        {
            await SendFallbackRepairStateForTest(fixture).WaitAsync(TimeSpan.FromSeconds(2));
            var operation = Assert.Single(FallbackRepairFeedbackOperationsForTest(fixture.Context));
            Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
            releasePhysical.TrySetResult();
            await BootstrapFeedbackSettledForTest(operation).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(FallbackRepairFeedbackOperationsForTest(fixture.Context));
        }
        finally { releasePhysical.TrySetResult(); }
    }

    [Theory]
    [InlineData("stream")] [InlineData("layout")] [InlineData("publication")]
    public async Task FallbackRepairFeedback_StaleFailureCannotScheduleRecovery(string replacement)
    {
        var transport = new PhysicalFallbackFeedbackTransport("session_fallback_repair_write") { FailDelivery = true };
        using var fixture = new FallbackRepairWriteFixture(transport: transport);
        var send = SendFallbackRepairStateForTest(fixture);
        await transport.RequestCaptured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var operation = Assert.Single(FallbackRepairFeedbackOperationsForTest(fixture.Context));
        fixture.Invalidate(replacement);
        var before = GetPrivatePropertyValue(fixture.Context, "CoordinatorPersistentState");
        transport.ReleaseDelivery.TrySetResult();
        Assert.False(await send.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(before, GetPrivatePropertyValue(fixture.Context, "CoordinatorPersistentState"));
        Assert.Equal(100, fixture.Frontier);
        Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
        transport.ReleasePhysical.TrySetResult();
        await BootstrapFeedbackSettledForTest(operation).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("current")] [InlineData("cancel")]
    public async Task FallbackRepairFeedback_DeliveryDoesNotRetirePhysicalOperation(string variant)
    {
        var transport = new PhysicalFallbackFeedbackTransport("session_fallback_repair_write");
        using var fixture = new FallbackRepairWriteFixture(transport: transport);
        var send = SendFallbackRepairStateForTest(fixture);
        await transport.RequestCaptured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var operation = Assert.Single(FallbackRepairFeedbackOperationsForTest(fixture.Context));
        transport.ReleaseDelivery.TrySetResult();
        Assert.True(await send.WaitAsync(TimeSpan.FromSeconds(2)));
        if (variant == "cancel") { GetPrivateProperty<CancellationTokenSource>(fixture.Context, "LifetimeCts").Cancel(); }
        Assert.Single(FallbackRepairFeedbackOperationsForTest(fixture.Context));
        Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
        transport.ReleasePhysical.TrySetResult();
        await BootstrapFeedbackSettledForTest(operation).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(FallbackRepairFeedbackOperationsForTest(fixture.Context));
        Assert.Null(CreateCoordinatorStateForTest(fixture.Context).PersistentState.FallbackTailYield.RepairHandoff);
    }

    [Theory]
    [InlineData("session")] [InlineData("provider")] [InlineData("service")] [InlineData("stream")]
    [InlineData("layout")] [InlineData("pause")] [InlineData("leg")] [InlineData("checkpoint")] [InlineData("publication")]
    public async Task FallbackRepairFeedback_StaleStandaloneCompletionCannotMutateSource(string replacement)
    {
        var transport = new PhysicalFallbackFeedbackTransport("session_fallback_repair_write");
        using var fixture = new FallbackRepairWriteFixture(transport: transport);
        var send = SendFallbackRepairStateForTest(fixture);
        await transport.RequestCaptured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var operation = Assert.Single(FallbackRepairFeedbackOperationsForTest(fixture.Context));
        fixture.Invalidate(replacement);
        var before = GetPrivatePropertyValue(fixture.Context, "CoordinatorPersistentState");
        transport.ReleaseDelivery.TrySetResult();
        Assert.False(await send.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(before, GetPrivatePropertyValue(fixture.Context, "CoordinatorPersistentState"));
        Assert.Equal(100, fixture.Frontier);
        Assert.False(BootstrapFeedbackSettledForTest(operation).IsCompleted);
        transport.ReleasePhysical.TrySetResult();
        await BootstrapFeedbackSettledForTest(operation).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(FallbackRepairFeedbackOperationsForTest(fixture.Context));
    }

    private static Task<bool> SendFallbackRepairStateForTest(FallbackRepairWriteFixture fixture)
        => Assert.IsAssignableFrom<Task<bool>>(typeof(SessionFileTransferService)
            .GetMethod("SendInboundV6ReceiverStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Service, [fixture.Context, "chunk_batch_committed", true, false, null]));

    private static object[] FallbackRepairFeedbackOperationsForTest(object context)
        => ((IDictionary)GetPrivatePropertyValue(context, "ActiveFallbackRepairFeedbackOperations")!).Values.Cast<object>().ToArray();

    private sealed class PhysicalFallbackFeedbackTransport(string sessionId) :
        PeerVisibleControlPlaneLoopbackTransport(sessionId, deliverFrames: false), IFileTransferControlPlanePhysicalSendSubmissionTransport
    {
        public TaskCompletionSource RequestCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDelivery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePhysical { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailDelivery { get; init; }
        public FileTransferControlPlanePhysicalSendSubmission SubmitFileTransferControlPlanePhysicalSend(
            FileTransferControlPlaneDeliveryRequest request, CancellationToken ct)
            => new(SendFileTransferControlPlaneFrameAsync(request, ct), ReleasePhysical.Task);
        public override async Task<FileTransferControlPlaneDeliveryResult> SendFileTransferControlPlaneFrameAsync(
            FileTransferControlPlaneDeliveryRequest request, CancellationToken ct)
        {
            RequestCaptured.TrySetResult();
            await ReleaseDelivery.Task;
            if (FailDelivery) { throw new IOException("modeled_delivery_failure"); }
            return await base.SendFileTransferControlPlaneFrameAsync(request, ct);
        }
    }
}
