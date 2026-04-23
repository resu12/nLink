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

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "ScreenShare")]
public sealed class ScreenShareAndChatCoexistenceTests : CoreSmokeTestsBase
{
[Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task ScreenShareAndChat_Coexist_WithoutStarvingChatProcessing()
    {
        ChatRuntimeCounters.ResetForTests();

        var network = new FakeSessionTransportNetwork();
        using var hostTransport = network.CreateTransport("helpee");
        using var helperTransport = network.CreateTransport("helper");
        using var helpeeChat = new SessionChatService(() => new DateTimeOffset(2026, 3, 3, 18, 0, 0, TimeSpan.Zero));
        using var helperChat = new SessionChatService(() => new DateTimeOffset(2026, 3, 3, 18, 0, 5, TimeSpan.Zero));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var screenShareClock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 18, 0, 0, TimeSpan.Zero));
        var reassembler = new ScreenShareVideoFrameReassembler();
        var receivedChatTexts = new ConcurrentQueue<string>();
        var allChatReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedFrameCount = 0;

        helpeeChat.AttachTransport(hostTransport);
        helperChat.AttachTransport(helperTransport);
        helpeeChat.MessageReceived += (_, e) =>
        {
            receivedChatTexts.Enqueue(e.Message.Text);
            if (receivedChatTexts.Count >= 50)
            {
                allChatReceived.TrySetResult();
            }
        };
        reassembler.FrameReady += (_, _) => Interlocked.Increment(ref completedFrameCount);

        IncomingJoinRequestEventArgs? pendingJoin = null;
        var joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hostTransport.IncomingJoinRequest += (_, e) =>
        {
            pendingJoin = e;
            joinRaised.TrySetResult();
        };

        var hostAddress = hostTransport.LocalPeerAddress;
        _ = hostTransport.HostByAddressAsync(cts.Token);
        await helperTransport.JoinByAddressAsync(hostAddress, cts.Token).WaitAsync(TimeSpan.FromSeconds(2));
        await joinRaised.Task.WaitAsync(cts.Token);
        await pendingJoin!.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
        await WaitUntilAsync(() => helperChat.IsApproved && helpeeChat.IsApproved, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => helperChat.HasSessionKey && helpeeChat.HasSessionKey, TimeSpan.FromSeconds(2));

        await using var pipeline = new ScreenShareFrameSendPipeline(
            sendFrameAsync: async (frame, _) =>
            {
                if (frame.StreamConfig is not null)
                {
                    reassembler.OnStreamConfig(frame.StreamConfig);
                }

                var fragments = ScreenShareVideoFragmenter.FragmentAccessUnit(
                    frame.SessionId,
                    frame.StreamEpoch,
                    frame.FrameId,
                    frame.TimestampUnixMilliseconds,
                    frame.Width,
                    frame.Height,
                    frame.Encoding,
                    frame.IsKeyFrame,
                    frame.EncodedFrameBytes);
                foreach (var fragment in fragments)
                {
                    reassembler.OnFragment(fragment);
                }

                await Task.Yield();
                return fragments.Count;
            },
            capacity: ScreenShareFrameSendPipeline.MaxBufferedFrames,
            clock: screenShareClock);

        var chatTask = Task.WhenAll(
            Enumerable.Range(0, 50).Select(i => helperChat.TrySendTextAsync($"chat-{i:D2}", cts.Token)));
        var screenShareTask = Task.Run(async () =>
        {
            for (var i = 0; i < 5; i++)
            {
                screenShareClock.Advance(TimeSpan.FromMilliseconds(125));
                await pipeline.EnqueueFrameAsync(
                    sessionId: "session-chat-share",
                    width: 640 + i,
                    height: 360 + i,
                    encoding: "h264",
                    encodedFrameBytes: new byte[] { (byte)(i + 1), (byte)(i + 2), (byte)(i + 3) },
                    timestampUnixMilliseconds: 1000 + i,
                    isKeyFrame: i == 0,
                    streamEpoch: 1,
                    streamConfig: i == 0
                        ? new ScreenShareVideoStreamConfigV1
                        {
                            SessionId = "session-chat-share",
                            StreamEpoch = 1,
                            Encoding = "h264",
                            CodecProfile = "baseline",
                            DecoderConfigData = new byte[] { 1, 2, 3 },
                        }
                        : null,
                    cancellationToken: cts.Token);
                await Task.Yield();
            }
        }, cts.Token);

        await Task.WhenAll(chatTask, screenShareTask);
        var chatRecords = await chatTask;
        await allChatReceived.Task.WaitAsync(cts.Token);
        await WaitUntilAsync(() => Volatile.Read(ref completedFrameCount) >= 1, TimeSpan.FromSeconds(2));

        Assert.Equal(50, receivedChatTexts.Count);
        Assert.True(Volatile.Read(ref completedFrameCount) >= 1);
        Assert.DoesNotContain(chatRecords, record => record is null);

        var senderMetrics = pipeline.GetMetricsSnapshot();
        Assert.Equal(50, ChatRuntimeCounters.Snapshot().ChatReceived);
        Assert.Equal(5, senderMetrics.FramesCaptured);
        Assert.True(senderMetrics.FramesQueued >= 1);
        Assert.True(senderMetrics.ChunksSent >= 1);
        Assert.True(reassembler.GetMetricsSnapshot().FramesCompleted >= 1);
    }

}
