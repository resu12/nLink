using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class InitialActivationReceiverBarrierExecutionFenceTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public void ReceiverBarrierFence_InvalidationPreventsLateApplyAndFinalization()
    {
        var fence = new InitialActivationReceiverBarrierExecutionFence();
        var effects = 0;
        Assert.True(fence.TryApply(() => { effects++; return true; }));
        Assert.True(fence.TryFinalize(() => effects++));
        Assert.Equal(2, effects);
        fence.Invalidate();
        fence.Invalidate();
        Assert.False(fence.IsValid);
        Assert.False(fence.TryApply(() => { effects++; return true; }));
        Assert.False(fence.TryFinalize(() => effects++));
        Assert.Equal(2, effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public async Task ReceiverBarrierFence_InFlightActionCompletesBeforeInvalidation(bool finalize)
    {
        var fence = new InitialActivationReceiverBarrierExecutionFence();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var invalidationStarted = new ManualResetEventSlim();
        var ordering = new List<string>();
        var action = Task.Run(() =>
        {
            void Work()
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                ordering.Add("action_completed");
            }
            return finalize ? fence.TryFinalize(Work) : fence.TryApply(() => { Work(); return true; });
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var invalidation = Task.Run(() =>
        {
            invalidationStarted.Set();
            fence.Invalidate();
            ordering.Add("invalidated");
        });
        try
        {
            Assert.True(invalidationStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(invalidation.IsCompleted);
        }
        finally
        {
            release.Set();
        }
        Assert.True(await action.WaitAsync(TimeSpan.FromSeconds(5)));
        await invalidation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "action_completed", "invalidated" }, ordering);
        Assert.False(fence.IsValid);
    }
}
