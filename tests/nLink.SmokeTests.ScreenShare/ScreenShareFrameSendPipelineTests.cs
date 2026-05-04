using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using NLink.App;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Metrics;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.Retry;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Core.Logging;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareFrameSendPipelineTests : CoreSmokeTestsBase
{
[Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task ScreenShareFrameSendPipeline_DefaultConstructor_UsesSingleBufferCapacityWithoutThrowing()
    {
        await using var pipeline = new ScreenShareFrameSendPipeline(
            sendFrameAsync: (_, _) => Task.FromResult(1));

        await pipeline.EnqueueFrameAsync(
            sessionId: "session-default-capacity",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 1, 2, 3 },
            timestampUnixMilliseconds: 1000,
            cancellationToken: CancellationToken.None);

        var metrics = pipeline.GetMetricsSnapshot();
        Assert.Equal(1, metrics.FramesCaptured);
        Assert.True(metrics.FramesQueued >= 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSendPipeline_AllowsHighQualityPresetTransportFps()
    {
        await using var pipeline = new ScreenShareFrameSendPipeline(
            sendFrameAsync: (_, _) => Task.FromResult(1),
            maxFramesPerSecond: ScreenShareQualitySettings.HighQualityPreset.TransportFramesPerSecond);

        pipeline.SetMaxFramesPerSecond(ScreenShareQualitySettings.HighQualityPreset.TransportFramesPerSecond);

        Assert.Equal(12, ScreenShareFrameSendPipeline.MaxFramesPerSecond);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task ScreenShareFrameSendPipeline_DeferredFrame_SendsAtNextSlotWithoutNewArrival()
    {
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 18, 0, 0, TimeSpan.Zero));
        var delayScheduler = new ControlledDelayScheduler();
        var sentPackets = new ConcurrentQueue<ScreenShareEncodedFramePacket>();

        await using var pipeline = CreateControlledScreenShareFrameSendPipeline(
            sendFrameAsync: (packet, _) =>
            {
                sentPackets.Enqueue(packet);
                return Task.FromResult(1);
            },
            clock,
            delayScheduler,
            maxFramesPerSecond: 5);

        await pipeline.EnqueueFrameAsync(
            sessionId: "session-slot-send",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 1, 2, 3 },
            timestampUnixMilliseconds: 1000,
            isKeyFrame: true,
            streamEpoch: 1,
            streamConfig: new ScreenShareVideoStreamConfigV1
            {
                SessionId = "session-slot-send",
                StreamEpoch = 1,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            },
            cancellationToken: CancellationToken.None);

        await WaitUntilAsync(() => sentPackets.Count >= 1, TimeSpan.FromSeconds(1));

        await pipeline.EnqueueFrameAsync(
            sessionId: "session-slot-send",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 4, 5, 6 },
            timestampUnixMilliseconds: 1010,
            isKeyFrame: false,
            streamEpoch: 1,
            streamConfig: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(1, delayScheduler.PendingCount);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        delayScheduler.CompleteLatest();

        await WaitUntilAsync(() => sentPackets.Count >= 2, TimeSpan.FromSeconds(1));

        var packets = sentPackets.ToArray();
        Assert.Equal(2, packets.Length);
        Assert.Equal(0, packets[0].FrameId);
        Assert.Equal(1, packets[1].FrameId);
        Assert.Equal(new byte[] { 4, 5, 6 }, packets[1].EncodedFrameBytes);

        var metrics = pipeline.GetMetricsSnapshot();
        Assert.Equal(1, metrics.FramesDeferredToSendSlot);
        Assert.Equal(0, metrics.FramesReplacedBeforeSendSlot);
        Assert.Equal(0, metrics.FramesDroppedByRateGate);
        Assert.True(metrics.SlotCoalescingActive);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task ScreenShareFrameSendPipeline_CoalescesBurst_AndSendsFreshestFrameAtNextSlot()
    {
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 18, 5, 0, TimeSpan.Zero));
        var delayScheduler = new ControlledDelayScheduler();
        var sentPackets = new ConcurrentQueue<ScreenShareEncodedFramePacket>();

        await using var pipeline = CreateControlledScreenShareFrameSendPipeline(
            sendFrameAsync: (packet, _) =>
            {
                sentPackets.Enqueue(packet);
                return Task.FromResult(1);
            },
            clock,
            delayScheduler,
            maxFramesPerSecond: 5);

        await pipeline.EnqueueFrameAsync(
            sessionId: "session-slot-coalesce",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 1, 1, 1 },
            timestampUnixMilliseconds: 2000,
            isKeyFrame: true,
            streamEpoch: 1,
            streamConfig: new ScreenShareVideoStreamConfigV1
            {
                SessionId = "session-slot-coalesce",
                StreamEpoch = 1,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            },
            cancellationToken: CancellationToken.None);

        await WaitUntilAsync(() => sentPackets.Count >= 1, TimeSpan.FromSeconds(1));

        await pipeline.EnqueueFrameAsync(
            sessionId: "session-slot-coalesce",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 2, 2, 2 },
            timestampUnixMilliseconds: 2010,
            isKeyFrame: false,
            streamEpoch: 1,
            streamConfig: null,
            cancellationToken: CancellationToken.None);
        await pipeline.EnqueueFrameAsync(
            sessionId: "session-slot-coalesce",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 3, 3, 3 },
            timestampUnixMilliseconds: 2020,
            isKeyFrame: false,
            streamEpoch: 1,
            streamConfig: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(1, delayScheduler.PendingCount);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        delayScheduler.CompleteLatest();

        await WaitUntilAsync(() => sentPackets.Count >= 2, TimeSpan.FromSeconds(1));

        var packets = sentPackets.ToArray();
        Assert.Equal(2, packets.Length);
        Assert.Equal(new byte[] { 3, 3, 3 }, packets[1].EncodedFrameBytes);

        var metrics = pipeline.GetMetricsSnapshot();
        Assert.Equal(2, metrics.FramesDeferredToSendSlot);
        Assert.Equal(1, metrics.FramesReplacedBeforeSendSlot);
        Assert.Equal(0, metrics.FramesDroppedByRateGate);
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task ScreenShareFrameSendPipeline_PrefersSameEpochKeyframe_AndThenNewerEpoch_FrameForNextSlot()
    {
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 18, 10, 0, TimeSpan.Zero));
        var delayScheduler = new ControlledDelayScheduler();
        var sentPackets = new ConcurrentQueue<ScreenShareEncodedFramePacket>();

        await using var pipeline = CreateControlledScreenShareFrameSendPipeline(
            sendFrameAsync: (packet, _) =>
            {
                sentPackets.Enqueue(packet);
                return Task.FromResult(1);
            },
            clock,
            delayScheduler,
            maxFramesPerSecond: 5);

        await pipeline.EnqueueFrameAsync(
            sessionId: "session-slot-priority",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 9, 9, 9 },
            timestampUnixMilliseconds: 3000,
            isKeyFrame: true,
            streamEpoch: 1,
            streamConfig: new ScreenShareVideoStreamConfigV1
            {
                SessionId = "session-slot-priority",
                StreamEpoch = 1,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            },
            cancellationToken: CancellationToken.None);

        await WaitUntilAsync(() => sentPackets.Count >= 1, TimeSpan.FromSeconds(1));

        await pipeline.EnqueueFrameAsync(
            sessionId: "session-slot-priority",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 1, 0, 0 },
            timestampUnixMilliseconds: 3010,
            isKeyFrame: false,
            streamEpoch: 1,
            streamConfig: null,
            cancellationToken: CancellationToken.None);
        await pipeline.EnqueueFrameAsync(
            sessionId: "session-slot-priority",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 1, 1, 1 },
            timestampUnixMilliseconds: 3020,
            isKeyFrame: true,
            streamEpoch: 1,
            streamConfig: new ScreenShareVideoStreamConfigV1
            {
                SessionId = "session-slot-priority",
                StreamEpoch = 1,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 7, 8, 9 },
            },
            cancellationToken: CancellationToken.None);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        delayScheduler.CompleteLatest();
        await WaitUntilAsync(() => sentPackets.Count >= 2, TimeSpan.FromSeconds(1));

        await pipeline.EnqueueFrameAsync(
            sessionId: "session-slot-priority",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 2, 0, 0 },
            timestampUnixMilliseconds: 3030,
            isKeyFrame: false,
            streamEpoch: 1,
            streamConfig: null,
            cancellationToken: CancellationToken.None);
        await pipeline.EnqueueFrameAsync(
            sessionId: "session-slot-priority",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 2, 2, 2 },
            timestampUnixMilliseconds: 3040,
            isKeyFrame: false,
            streamEpoch: 2,
            streamConfig: null,
            cancellationToken: CancellationToken.None);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        delayScheduler.CompleteLatest();
        await WaitUntilAsync(() => sentPackets.Count >= 3, TimeSpan.FromSeconds(1));

        var packets = sentPackets.ToArray();
        Assert.Equal(3, packets.Length);
        Assert.Equal(0, packets[0].FrameId);
        Assert.True(packets[1].IsKeyFrame);
        Assert.Equal(1, packets[1].StreamEpoch);
        Assert.Equal(1, packets[1].FrameId);
        Assert.Equal(new byte[] { 1, 1, 1 }, packets[1].EncodedFrameBytes);
        Assert.False(packets[2].IsKeyFrame);
        Assert.Equal(2, packets[2].StreamEpoch);
        Assert.Equal(0, packets[2].FrameId);
        Assert.Equal(new byte[] { 2, 2, 2 }, packets[2].EncodedFrameBytes);

        var metrics = pipeline.GetMetricsSnapshot();
        Assert.Equal(4, metrics.FramesDeferredToSendSlot);
        Assert.Equal(2, metrics.FramesReplacedBeforeSendSlot);
        Assert.Equal(0, metrics.FramesDroppedByRateGate);
    }

[Trait("Category", "Smoke")]
    [Fact]
    public async Task ScreenShareFrameSendPipeline_PrunesFrameIdKeysForOlderEpochsWhenStreamEpochAdvances()
    {
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 18, 12, 0, TimeSpan.Zero));
        var delayScheduler = new ControlledDelayScheduler();
        var sentPackets = new ConcurrentQueue<ScreenShareEncodedFramePacket>();

        await using var pipeline = CreateControlledScreenShareFrameSendPipeline(
            sendFrameAsync: (packet, _) =>
            {
                sentPackets.Enqueue(packet);
                return Task.FromResult(1);
            },
            clock,
            delayScheduler,
            maxFramesPerSecond: 5);

        await EnqueueEpochKeyframeAsync(pipeline, "session-epoch-prune", streamEpoch: 1, timestampUnixMilliseconds: 5000);
        await WaitUntilAsync(() => sentPackets.Count >= 1, TimeSpan.FromSeconds(1));
        Assert.Equal(1, pipeline.FrameSequenceKeyCount);

        await EnqueueEpochKeyframeAsync(pipeline, "session-epoch-prune", streamEpoch: 2, timestampUnixMilliseconds: 5100);
        await WaitUntilAsync(() => delayScheduler.PendingCount >= 1, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromMilliseconds(200));
        delayScheduler.CompleteLatest();
        await WaitUntilAsync(() => sentPackets.Count >= 2, TimeSpan.FromSeconds(1));
        Assert.Equal(1, pipeline.FrameSequenceKeyCount);

        await EnqueueEpochKeyframeAsync(pipeline, "session-epoch-prune", streamEpoch: 3, timestampUnixMilliseconds: 5200);
        await WaitUntilAsync(() => delayScheduler.PendingCount >= 1, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromMilliseconds(200));
        delayScheduler.CompleteLatest();
        await WaitUntilAsync(() => sentPackets.Count >= 3, TimeSpan.FromSeconds(1));

        Assert.Equal(1, pipeline.FrameSequenceKeyCount);
        Assert.Equal(new long[] { 1, 2, 3 }, sentPackets.Select(packet => packet.StreamEpoch));
        Assert.Equal(new long[] { 0, 0, 0 }, sentPackets.Select(packet => packet.FrameId));
    }

[Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task ScreenShareFrameSendPipeline_OrdinaryFrame_CannotEvictOrDelayQueuedRecoveryOwnerAndFollowers()
    {
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 18, 15, 0, TimeSpan.Zero));
        var delayScheduler = new ControlledDelayScheduler();
        var sentPackets = new ConcurrentQueue<ScreenShareEncodedFramePacket>();

        await using var pipeline = CreateControlledScreenShareFrameSendPipeline(
            sendFrameAsync: (packet, _) =>
            {
                sentPackets.Enqueue(packet);
                return Task.FromResult(1);
            },
            clock,
            delayScheduler,
            maxFramesPerSecond: 5);

        await pipeline.EnqueueFrameAsync(
            sessionId: "session-recovery-protected",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 1, 1, 1 },
            timestampUnixMilliseconds: 4000,
            isKeyFrame: true,
            streamEpoch: 1,
            streamConfig: new ScreenShareVideoStreamConfigV1
            {
                SessionId = "session-recovery-protected",
                StreamEpoch = 1,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            },
            cancellationToken: CancellationToken.None);

        await WaitUntilAsync(() => sentPackets.Count >= 1, TimeSpan.FromSeconds(1));

        await pipeline.EnqueueFrameAsync(
            sessionId: "session-recovery-protected",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 9, 8, 7 },
            timestampUnixMilliseconds: 4005,
            isKeyFrame: false,
            streamEpoch: 2,
            streamConfig: null,
            cancellationToken: CancellationToken.None);
        await pipeline.EnqueueFrameAsync(
            sessionId: "session-recovery-protected",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 2, 2, 2 },
            timestampUnixMilliseconds: 4010,
            isKeyFrame: true,
            streamEpoch: 2,
            streamConfig: new ScreenShareVideoStreamConfigV1
            {
                SessionId = "session-recovery-protected",
                StreamEpoch = 2,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 4, 5, 6 },
            },
            cancellationToken: CancellationToken.None,
            preserveOrdering: true);
        await pipeline.EnqueueFrameAsync(
            sessionId: "session-recovery-protected",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 3, 3, 3 },
            timestampUnixMilliseconds: 4020,
            isKeyFrame: false,
            streamEpoch: 2,
            streamConfig: null,
            cancellationToken: CancellationToken.None,
            preserveOrdering: true);
        await pipeline.EnqueueFrameAsync(
            sessionId: "session-recovery-protected",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 4, 4, 4 },
            timestampUnixMilliseconds: 4030,
            isKeyFrame: false,
            streamEpoch: 2,
            streamConfig: null,
            cancellationToken: CancellationToken.None,
            preserveOrdering: true);
        await pipeline.EnqueueFrameAsync(
            sessionId: "session-recovery-protected",
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { 9, 9, 9 },
            timestampUnixMilliseconds: 4040,
            isKeyFrame: false,
            streamEpoch: 2,
            streamConfig: null,
            cancellationToken: CancellationToken.None);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        await WaitUntilAsync(() => delayScheduler.PendingCount >= 1, TimeSpan.FromSeconds(1));
        delayScheduler.CompleteLatest();
        await WaitUntilAsync(() => sentPackets.Count >= 2, TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromMilliseconds(200));
        await WaitUntilAsync(() => delayScheduler.PendingCount >= 1, TimeSpan.FromSeconds(1));
        delayScheduler.CompleteLatest();
        await WaitUntilAsync(() => sentPackets.Count >= 3, TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromMilliseconds(200));
        await WaitUntilAsync(() => delayScheduler.PendingCount >= 1, TimeSpan.FromSeconds(1));
        delayScheduler.CompleteLatest();
        await WaitUntilAsync(() => sentPackets.Count >= 4, TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromMilliseconds(200));
        await WaitUntilAsync(() => delayScheduler.PendingCount >= 1, TimeSpan.FromSeconds(1));
        delayScheduler.CompleteLatest();
        await WaitUntilAsync(() => sentPackets.Count >= 5, TimeSpan.FromSeconds(1));

        var packets = sentPackets.ToArray();
        Assert.Equal(5, packets.Length);
        Assert.True(packets[1].IsKeyFrame);
        Assert.Equal(2, packets[1].StreamEpoch);
        Assert.Equal(new byte[] { 2, 2, 2 }, packets[1].EncodedFrameBytes);
        Assert.Equal(new byte[] { 3, 3, 3 }, packets[2].EncodedFrameBytes);
        Assert.Equal(new byte[] { 4, 4, 4 }, packets[3].EncodedFrameBytes);
        Assert.Equal(new byte[] { 9, 8, 7 }, packets[4].EncodedFrameBytes);
        Assert.DoesNotContain(packets, static packet => packet.EncodedFrameBytes.SequenceEqual(new byte[] { 9, 9, 9 }));

        var metrics = pipeline.GetMetricsSnapshot();
        Assert.Equal(4, metrics.FramesDeferredToSendSlot);
        Assert.Equal(0, metrics.FramesReplacedBeforeSendSlot);
        Assert.Equal(3, metrics.ProtectedRecoveryFramesDispatched);
        Assert.Equal(3, metrics.RecoveryProtectedFrameBlockedByOrdinaryCount);
        Assert.True(metrics.FramesDropped >= 1);
    }

    private static Task EnqueueEpochKeyframeAsync(
        ScreenShareFrameSendPipeline pipeline,
        string sessionId,
        long streamEpoch,
        long timestampUnixMilliseconds)
        => pipeline.EnqueueFrameAsync(
            sessionId,
            width: 1280,
            height: 720,
            encoding: "h264",
            encodedFrameBytes: new byte[] { checked((byte)streamEpoch), 1, 1 },
            timestampUnixMilliseconds,
            isKeyFrame: true,
            streamEpoch,
            streamConfig: new ScreenShareVideoStreamConfigV1
            {
                SessionId = sessionId,
                StreamEpoch = streamEpoch,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            },
            cancellationToken: CancellationToken.None);
}
