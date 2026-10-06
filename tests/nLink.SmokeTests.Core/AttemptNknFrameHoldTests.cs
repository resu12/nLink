using System.Security.Cryptography;
using NLink.Infra.Nkn;
using NLink.Tests;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class AttemptNknFrameHoldTests
{
    [Theory]
    [InlineData(1, (int)NknBridgeChannel.Bulk, 4096)]
    [InlineData(0, (int)NknBridgeChannel.Control, 4096)]
    [InlineData(0, (int)NknBridgeChannel.Bulk, 4095)]
    public async Task UnselectedFramePassesUnchanged(byte kind, int lane, int length)
    {
        var hold = new AttemptNknFrameHold();
        var bytes = new byte[length];
        Assert.Same(bytes, await hold.BeforeSendAsync(kind, (NknBridgeChannel)lane, bytes, default));
        Assert.False(hold.Selected.IsCompleted); Assert.False(hold.Release());
        Assert.Equal(0, hold.PendingBytes);
    }

    [Fact]
    public async Task HoldsOriginalBytesOnceWithoutBlockingLaterFramesOrCompletingEarly()
    {
        var hold = new AttemptNknFrameHold();
        Assert.False(hold.Release());
        var bytes = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        var expected = (byte[])bytes.Clone();
        var first = hold.BeforeSendAsync(0, NknBridgeChannel.Bulk, bytes, default);
        var selected = await hold.Selected.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(bytes.Length, selected.ByteCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), selected.Sha256);
        bytes[0] ^= 0xff;
        var later = new byte[1048576];
        Assert.Same(later, await hold.BeforeSendAsync(0, NknBridgeChannel.Bulk, later, default));
        Assert.False(first.IsCompleted); Assert.Equal(expected.Length, hold.PendingBytes);
        Assert.True(hold.Release()); Assert.False(hold.Release());
        Assert.Equal(expected, await first); Assert.Equal(0, hold.PendingBytes);
        Assert.Same(bytes, await hold.BeforeSendAsync(0, NknBridgeChannel.Bulk, bytes, default));
    }

    [Fact]
    public async Task CancellationRetiresHeldBytesWithoutDeliveryOrRearming()
    {
        var hold = new AttemptNknFrameHold();
        using var cancellation = new CancellationTokenSource();
        var first = hold.BeforeSendAsync(0, NknBridgeChannel.Bulk, new byte[4096], cancellation.Token);
        await hold.Selected; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(0, hold.PendingBytes); Assert.False(hold.Release());
        var bytes = new byte[4096];
        Assert.Same(bytes, await hold.BeforeSendAsync(0, NknBridgeChannel.Bulk, bytes, default));
    }

    [Fact]
    public async Task UnreleasedFrameTimesOutWithoutSuccessfulSend()
    {
        var hold = new AttemptNknFrameHold();
        await Assert.ThrowsAsync<TimeoutException>(() => hold.BeforeSendAsync(0, NknBridgeChannel.Bulk, new byte[4096], default));
        Assert.Equal(0, hold.PendingBytes); Assert.False(hold.Release());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1048577)]
    public async Task InvalidCapacityDoesNotConsumeTheHold(int length)
    {
        var hold = new AttemptNknFrameHold();
        await Assert.ThrowsAsync<IOException>(() => hold.BeforeSendAsync(0, NknBridgeChannel.Bulk, new byte[length], default));
        Assert.False(hold.Selected.IsCompleted); Assert.Equal(0, hold.PendingBytes);
    }
}
