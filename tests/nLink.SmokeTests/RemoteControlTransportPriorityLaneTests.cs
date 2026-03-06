using System.Collections.Concurrent;
using NLink.Core;
using NLink.Core.RemoteControl;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class RemoteControlTransportPriorityLaneTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknControlLowLane_MouseMoveSpam_IsCoalescedToLatestWhenBackedUp()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.rc.lane.coalesce");
            var helperClient = new FakeNknClient("helper.rc.lane.coalesce");
            var hostIdentity = new NknIdentity("host-rc-lane-coalesce", "host.rc.lane.coalesce");
            var helperIdentity = new NknIdentity("helper-rc-lane-coalesce", "helper.rc.lane.coalesce");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            await ConnectAsync(host, helper, cts.Token);

            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlInputSendCount = 0;
            helperClient.BeforeSendAsync = async (_, payload, sendCt) =>
            {
                if (!EnvelopeCodec.TryDeserialize(payload, out var env) ||
                    env.Type != MsgType.ControlInput)
                {
                    return;
                }

                if (Interlocked.Increment(ref controlInputSendCount) == 1)
                {
                    firstSendStarted.TrySetResult();
                    await releaseFirstSend.Task.WaitAsync(sendCt);
                }
            };

            var receivedInputs = new ConcurrentQueue<ControlInputMessageV1>();
            hostClient.MessageReceived += (_, message) =>
            {
                if (!EnvelopeCodec.TryDeserialize(message.Payload, out var env) ||
                    env.Type != MsgType.ControlInput)
                {
                    return;
                }

                if (RemoteControlPayloadCodec.TryDeserializeControlInput(env.Payload, out var parsed))
                {
                    receivedInputs.Enqueue(parsed);
                }
            };

            const string requestId = "req-p61-coalesce";
            const int totalMoves = 1000;
            for (var i = 0; i < totalMoves; i++)
            {
                _ = helper.SendControlInputAsync(
                    new ControlInputMessageV1
                    {
                        RequestId = requestId,
                        Seq = i,
                        Kind = "mouse_move",
                        Nx = (i % 100) / 100d,
                        Ny = (i % 100) / 100d,
                    },
                    cts.Token);
            }

            await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            releaseFirstSend.TrySetResult();

            await WaitUntilAsync(() => receivedInputs.Count >= 2, TimeSpan.FromSeconds(2));
            await Task.Delay(100, cts.Token);

            var sent = receivedInputs.ToArray();
            Assert.Equal(2, sent.Length);
            Assert.True(sent[0].Seq >= 0 && sent[0].Seq < totalMoves - 1);
            Assert.Equal(totalMoves - 1, sent[1].Seq);
            Assert.Equal(totalMoves, helper.LowLaneEnqueuedMoves);
            Assert.True(helper.LowLaneDroppedMoves > 0);
            Assert.InRange(helper.LowLaneMaxDepthSeen, 1, 256);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknControlStop_HighLane_FlushesLowLaneAndWinsAfterInflightMove()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.rc.lane.stop");
            var helperClient = new FakeNknClient("helper.rc.lane.stop");
            var hostIdentity = new NknIdentity("host-rc-lane-stop", "host.rc.lane.stop");
            var helperIdentity = new NknIdentity("helper-rc-lane-stop", "helper.rc.lane.stop");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            await ConnectAsync(host, helper, cts.Token);

            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlInputSendCount = 0;
            helperClient.BeforeSendAsync = async (_, payload, sendCt) =>
            {
                if (!EnvelopeCodec.TryDeserialize(payload, out var env) ||
                    env.Type != MsgType.ControlInput)
                {
                    return;
                }

                if (Interlocked.Increment(ref controlInputSendCount) == 1)
                {
                    firstSendStarted.TrySetResult();
                    await releaseFirstSend.Task.WaitAsync(sendCt);
                }
            };

            var receivedTypes = new ConcurrentQueue<MsgType>();
            var receivedInputs = new ConcurrentQueue<ControlInputMessageV1>();
            var receivedStops = new ConcurrentQueue<ControlStopMessageV1>();
            hostClient.MessageReceived += (_, message) =>
            {
                if (!EnvelopeCodec.TryDeserialize(message.Payload, out var env))
                {
                    return;
                }

                if (env.Type is MsgType.ControlInput or MsgType.ControlStop)
                {
                    receivedTypes.Enqueue(env.Type);
                }

                if (env.Type == MsgType.ControlInput &&
                    RemoteControlPayloadCodec.TryDeserializeControlInput(env.Payload, out var input))
                {
                    receivedInputs.Enqueue(input);
                }

                if (env.Type == MsgType.ControlStop &&
                    RemoteControlPayloadCodec.TryDeserializeControlStop(env.Payload, out var stop))
                {
                    receivedStops.Enqueue(stop);
                }
            };

            const string requestId = "req-p61-stop";
            const int totalMoves = 1000;
            for (var i = 0; i < totalMoves; i++)
            {
                _ = helper.SendControlInputAsync(
                    new ControlInputMessageV1
                    {
                        RequestId = requestId,
                        Seq = i,
                        Kind = "mouse_move",
                        Nx = (i % 100) / 100d,
                        Ny = (i % 100) / 100d,
                    },
                    cts.Token);
            }

            await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            var stopTask = helper.SendControlStopAsync(
                new ControlStopMessageV1
                {
                    RequestId = requestId,
                    Reason = "p61_stop",
                },
                cts.Token);

            releaseFirstSend.TrySetResult();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await WaitUntilAsync(() => receivedStops.Count >= 1, TimeSpan.FromSeconds(2));
            await Task.Delay(100, cts.Token);

            var types = receivedTypes.ToArray();
            var stopIndex = Array.IndexOf(types, MsgType.ControlStop);
            Assert.True(stopIndex >= 1, "Expected control stop to arrive after one in-flight move.");
            Assert.DoesNotContain(types.Skip(stopIndex + 1), static t => t == MsgType.ControlInput);

            var inputs = receivedInputs.ToArray();
            Assert.Single(inputs);
            Assert.True(inputs[0].Seq >= 0 && inputs[0].Seq < totalMoves);

            var stops = receivedStops.ToArray();
            Assert.Single(stops);
            Assert.Equal("p61_stop", stops[0].Reason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknControlDisplayInfo_HighLane_IsProcessedBeforeQueuedLowLaneMouseMoves()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.rc.lane.display");
            var helperClient = new FakeNknClient("helper.rc.lane.display");
            var hostIdentity = new NknIdentity("host-rc-lane-display", "host.rc.lane.display");
            var helperIdentity = new NknIdentity("helper-rc-lane-display", "helper.rc.lane.display");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            await ConnectAsync(host, helper, cts.Token);

            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlInputSendCount = 0;
            helperClient.BeforeSendAsync = async (_, payload, sendCt) =>
            {
                if (!EnvelopeCodec.TryDeserialize(payload, out var env) ||
                    env.Type != MsgType.ControlInput)
                {
                    return;
                }

                if (Interlocked.Increment(ref controlInputSendCount) == 1)
                {
                    firstSendStarted.TrySetResult();
                    await releaseFirstSend.Task.WaitAsync(sendCt);
                }
            };

            var receivedTypes = new ConcurrentQueue<MsgType>();
            hostClient.MessageReceived += (_, message) =>
            {
                if (!EnvelopeCodec.TryDeserialize(message.Payload, out var env))
                {
                    return;
                }

                if (env.Type is MsgType.ControlInput or MsgType.ControlDisplayInfo)
                {
                    receivedTypes.Enqueue(env.Type);
                }
            };

            const string requestId = "req-p61-display";
            const int totalMoves = 1000;
            for (var i = 0; i < totalMoves; i++)
            {
                _ = helper.SendControlInputAsync(
                    new ControlInputMessageV1
                    {
                        RequestId = requestId,
                        Seq = i,
                        Kind = "mouse_move",
                        Nx = (i % 100) / 100d,
                        Ny = (i % 100) / 100d,
                    },
                    cts.Token);
            }

            await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            var displayTask = helper.SendControlDisplayInfoAsync(
                new ControlDisplayInfoMessageV1
                {
                    DisplayId = "primary",
                    Revision = 42,
                    VirtualDesktopX = 0,
                    VirtualDesktopY = 0,
                    VirtualDesktopWidth = 1920,
                    VirtualDesktopHeight = 1080,
                    CaptureRegionX = 0,
                    CaptureRegionY = 0,
                    CaptureRegionWidth = 1920,
                    CaptureRegionHeight = 1080,
                    FrameWidth = 1280,
                    FrameHeight = 720,
                },
                cts.Token);

            releaseFirstSend.TrySetResult();
            await displayTask.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await WaitUntilAsync(
                () =>
                {
                    var snapshot = receivedTypes.ToArray();
                    return snapshot.Contains(MsgType.ControlDisplayInfo) && snapshot.Count(t => t == MsgType.ControlInput) >= 1;
                },
                TimeSpan.FromSeconds(2));
            await Task.Delay(100, cts.Token);

            var types = receivedTypes.ToArray();
            var displayInfoIndex = Array.IndexOf(types, MsgType.ControlDisplayInfo);
            Assert.True(displayInfoIndex >= 1, "Expected display info after one in-flight move.");

            var controlInputsBeforeDisplay = types.Take(displayInfoIndex).Count(t => t == MsgType.ControlInput);
            Assert.Equal(1, controlInputsBeforeDisplay);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    private static async Task ConnectAsync(
        NknSignalingTransport host,
        NknSignalingTransport helper,
        CancellationToken ct)
    {
        var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
        host.Approved += (_, _) => hostApproved.TrySetResult();
        helper.Approved += (_, _) => helperApproved.TrySetResult();

        await host.HostByAddressAsync(ct);
        var (rawToken, invite) = InviteTestFactory.CreateValidatedInvite(
            new PeerAddress(host.LocalPeerAddress),
            InviteCapabilities.RemoteControl | InviteCapabilities.ScreenShare);
        await helper.JoinByInviteAsync(rawToken, invite, ct);

        var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), ct);
        await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var started = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - started < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), $"Condition was not met within {timeout}.");
    }
}
