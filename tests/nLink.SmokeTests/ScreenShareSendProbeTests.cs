using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

public sealed class ScreenShareSendProbeTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareSendProbe_TracksCounts_AndKeepsRecentPayloadHistoryBounded()
    {
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 3);

        await probe.SendPayloadAsync([1], CancellationToken.None);
        await probe.SendReadOnlyPayloadAsync(new byte[] { 2, 3 }, CancellationToken.None);
        await probe.SendPayloadAsync([4, 5, 6], CancellationToken.None);
        await probe.SendPayloadAsync([7, 8, 9, 10], CancellationToken.None);

        Assert.Equal(4, probe.PayloadsSent);
        Assert.Equal(10, probe.BytesSent);
        Assert.Equal([2, 3, 4], probe.GetRecentPayloadSizesSnapshot());

        var payloads = probe.GetRecentPayloadsSnapshot();
        Assert.Equal(3, payloads.Length);
        Assert.Equal([2, 3], payloads[0]);
        Assert.Equal([4, 5, 6], payloads[1]);
        Assert.Equal([7, 8, 9, 10], payloads[2]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareSendProbe_BlockAndCancellation_ReleaseInFlightSlotDeterministically()
    {
        var probe = new ScreenShareSendProbe(maxInFlight: 1, startBlocked: true, respectCancellation: true);
        using var cts = new CancellationTokenSource();

        var blockedSend = probe.SendPayloadAsync([1, 2, 3], cts.Token);
        await probe.FirstSendStarted.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, probe.CurrentInFlight);
        Assert.Equal(1, probe.MaxObservedInFlight);
        Assert.Equal(0, probe.PayloadsSent);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedSend);
        Assert.Equal(0, probe.CurrentInFlight);
        Assert.Equal(0, probe.PayloadsSent);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareSendProbe_SendChunkAsync_RecordsSerializedPayload()
    {
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 2);
        var chunk = new ScreenShareFrameChunkV1
        {
            SessionId = "session-probe",
            FrameId = 7,
            Width = 1280,
            Height = 720,
            TimestampUnixMilliseconds = 1234,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 1,
            DataBase64 = Convert.ToBase64String([1, 2, 3]),
        };

        await probe.SendChunkAsync(chunk, CancellationToken.None);

        Assert.Equal(1, probe.PayloadsSent);
        Assert.Equal(1, probe.ChunksSent);

        var payload = Assert.Single(probe.GetRecentPayloadsSnapshot());
        Assert.True(ScreenSharePayloadCodec.TryDeserialize(payload, out var deserialized));
        Assert.Equal("session-probe", deserialized.SessionId);
        Assert.Equal(7, deserialized.FrameId);
    }
}
