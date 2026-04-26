using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core;
using NLink.Core.RemoteControl;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "RemoteControl")]
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
            host.RemoteControlInputReceived += (_, e) => receivedInputs.Enqueue(e.Message);

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
            host.RemoteControlInputReceived += (_, e) => receivedInputs.Enqueue(e.Message);
            host.RemoteControlStopReceived += (_, e) => receivedStops.Enqueue(e.Message);
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

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknControlStop_HighLane_StillWinsAfterDisconnectReconnect_WithRenewedLowLaneBacklog()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.rc.lane.reconnect");
            var helperClient = new FakeNknClient("helper.rc.lane.reconnect");
            var hostIdentity = new NknIdentity("host-rc-lane-reconnect", "host.rc.lane.reconnect");
            var helperIdentity = new NknIdentity("helper-rc-lane-reconnect", "helper.rc.lane.reconnect");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            await ConnectAsync(host, helper, cts.Token);

            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gateFirstControlInput = 1;
            var controlInputSendCount = 0;
            helperClient.BeforeSendAsync = async (_, payload, sendCt) =>
            {
                if (!EnvelopeCodec.TryDeserialize(payload, out var env) ||
                    env.Type != MsgType.ControlInput)
                {
                    return;
                }

                if (Volatile.Read(ref gateFirstControlInput) == 1 &&
                    Interlocked.Increment(ref controlInputSendCount) == 1)
                {
                    firstSendStarted.TrySetResult();
                    await releaseFirstSend.Task.WaitAsync(sendCt);
                }
            };

            var receivedInputs = new ConcurrentQueue<ControlInputMessageV1>();
            var receivedStops = new ConcurrentQueue<ControlStopMessageV1>();
            var receivedTypes = new ConcurrentQueue<MsgType>();
            host.RemoteControlInputReceived += (_, e) => receivedInputs.Enqueue(e.Message);
            host.RemoteControlStopReceived += (_, e) => receivedStops.Enqueue(e.Message);
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
            };

            const string oldRequestId = "req-p61-reconnect-old";
            for (var i = 0; i < 1000; i++)
            {
                _ = helper.SendControlInputAsync(
                    new ControlInputMessageV1
                    {
                        RequestId = oldRequestId,
                        Seq = i,
                        Kind = "mouse_move",
                        Nx = (i % 100) / 100d,
                        Ny = (i % 100) / 100d,
                    },
                    cts.Token);
            }

            await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await helperClient.DisconnectAsync();
            releaseFirstSend.TrySetResult();
            await Task.Delay(100, cts.Token);

            while (receivedInputs.TryDequeue(out _))
            {
            }

            while (receivedStops.TryDequeue(out _))
            {
            }

            while (receivedTypes.TryDequeue(out _))
            {
            }

            controlInputSendCount = 0;
            firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref gateFirstControlInput, 1);

            await ConnectAsync(host, helper, cts.Token);

            const string newRequestId = "req-p61-reconnect-new";
            for (var i = 0; i < 1000; i++)
            {
                _ = helper.SendControlInputAsync(
                    new ControlInputMessageV1
                    {
                        RequestId = newRequestId,
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
                    RequestId = newRequestId,
                    Reason = "p61_reconnect_stop",
                },
                cts.Token);

            releaseFirstSend.TrySetResult();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await WaitUntilAsync(() => receivedStops.Count >= 1, TimeSpan.FromSeconds(2));
            await Task.Delay(100, cts.Token);

            var types = receivedTypes.ToArray();
            var stopIndex = Array.IndexOf(types, MsgType.ControlStop);
            Assert.True(stopIndex >= 1, "Expected control stop to arrive after one in-flight move following reconnect.");
            Assert.DoesNotContain(types.Skip(stopIndex + 1), static t => t == MsgType.ControlInput);

            var inputs = receivedInputs.ToArray();
            Assert.Single(inputs);
            Assert.All(inputs, input => Assert.Equal(newRequestId, input.RequestId));

            var stops = receivedStops.ToArray();
            Assert.Single(stops);
            Assert.Equal("p61_reconnect_stop", stops[0].Reason);
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
    public async Task NknControlHighLane_Backlog_IsCapped_WhenSendIsBlocked()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.rc.lane.highcap");
            var helperClient = new FakeNknClient("helper.rc.lane.highcap");
            var hostIdentity = new NknIdentity("host-rc-lane-highcap", "host.rc.lane.highcap");
            var helperIdentity = new NknIdentity("helper-rc-lane-highcap", "helper.rc.lane.highcap");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            await ConnectAsync(host, helper, cts.Token);

            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlRequestSendCount = 0;
            helperClient.BeforeSendAsync = async (_, payload, sendCt) =>
            {
                if (!EnvelopeCodec.TryDeserialize(payload, out var env) ||
                    env.Type != MsgType.ControlRequest)
                {
                    return;
                }

                if (Interlocked.Increment(ref controlRequestSendCount) == 1)
                {
                    firstSendStarted.TrySetResult();
                    await releaseFirstSend.Task.WaitAsync(sendCt);
                }
            };

            var receivedRequests = new ConcurrentQueue<ControlRequestMessageV1>();
            host.RemoteControlRequestReceived += (_, e) => receivedRequests.Enqueue(e.Message);

            const int totalRequests = 400;
            for (var i = 0; i < totalRequests; i++)
            {
                _ = helper.SendControlRequestAsync(
                    new ControlRequestMessageV1
                    {
                        RequestId = $"req-p61-highcap-{i:D4}",
                        Caps = new[] { "remote_control" },
                    },
                    cts.Token);
            }

            await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await WaitUntilAsync(() => GetHighPriorityQueueCount(helper) == 256, TimeSpan.FromSeconds(2));
            var cappedDepth = GetHighPriorityQueueCount(helper);

            releaseFirstSend.TrySetResult();

            await WaitUntilAsync(() => receivedRequests.Count >= 257, TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => GetHighPriorityQueueCount(helper) == 0, TimeSpan.FromSeconds(2));
            await Task.Delay(100, cts.Token);

            Assert.Equal(256, cappedDepth);
            Assert.Equal(257, receivedRequests.Count);
            Assert.True(helper.HighPriorityControlQueueOverflowCount > 0);
            Assert.True(helper.HighPriorityControlRejectedCount > 0);
            Assert.Equal(0, helper.HighPriorityControlCoalescedCount);
            Assert.Equal(0, helper.HighPriorityControlDroppedForStopCount);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknControlDisplayInfo_HighLane_CoalescesLatestRevision_WhenQueueIsFull()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.rc.lane.displaycap");
            var helperClient = new FakeNknClient("helper.rc.lane.displaycap");
            var hostIdentity = new NknIdentity("host-rc-lane-displaycap", "host.rc.lane.displaycap");
            var helperIdentity = new NknIdentity("helper-rc-lane-displaycap", "helper.rc.lane.displaycap");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            await ConnectAsync(host, helper, cts.Token);

            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlDisplayInfoSendCount = 0;
            helperClient.BeforeSendAsync = async (_, payload, sendCt) =>
            {
                if (!EnvelopeCodec.TryDeserialize(payload, out var env) ||
                    env.Type != MsgType.ControlDisplayInfo)
                {
                    return;
                }
                var sendIndex = Interlocked.Increment(ref controlDisplayInfoSendCount);
                if (sendIndex == 1)
                {
                    firstSendStarted.TrySetResult();
                    await releaseFirstSend.Task.WaitAsync(sendCt);
                }
            };

            const int totalRevisions = 400;
            for (var i = 0; i < totalRevisions; i++)
            {
                _ = helper.SendControlDisplayInfoAsync(
                    new ControlDisplayInfoMessageV1
                    {
                        DisplayId = "primary",
                        Revision = i,
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
                        TsUtcMs = i + 1,
                    },
                    cts.Token);
            }

            await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await WaitUntilAsync(() => GetHighPriorityQueueCount(helper) == 256, TimeSpan.FromSeconds(2));
            var cappedDepth = GetHighPriorityQueueCount(helper);
            var queuedRevisions = GetQueuedHighPriorityDisplayInfoRevisions(helper);

            releaseFirstSend.TrySetResult();
            await WaitUntilAsync(() => GetHighPriorityQueueCount(helper) == 0, TimeSpan.FromSeconds(5));
            await Task.Delay(100, cts.Token);

            Assert.Equal(256, cappedDepth);
            Assert.Equal(256, queuedRevisions.Length);
            Assert.Equal(totalRevisions - 1, queuedRevisions[^1]);
            Assert.Contains(totalRevisions - 1, queuedRevisions);
            Assert.DoesNotContain(totalRevisions - 2, queuedRevisions);
            Assert.True(helper.HighPriorityControlQueueOverflowCount > 0);
            Assert.Equal(0, helper.HighPriorityControlRejectedCount);
            Assert.True(helper.HighPriorityControlCoalescedCount > 0);
            Assert.Equal(0, helper.HighPriorityControlDroppedForStopCount);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknControlStop_HighLane_InsertsAtHead_WhenHighLaneIsAlreadyFull()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.rc.lane.stopfull");
            var helperClient = new FakeNknClient("helper.rc.lane.stopfull");
            var hostIdentity = new NknIdentity("host-rc-lane-stopfull", "host.rc.lane.stopfull");
            var helperIdentity = new NknIdentity("helper-rc-lane-stopfull", "helper.rc.lane.stopfull");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            await ConnectAsync(host, helper, cts.Token);

            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlDisplayInfoSendCount = 0;
            helperClient.BeforeSendAsync = async (_, payload, sendCt) =>
            {
                if (!EnvelopeCodec.TryDeserialize(payload, out var env) ||
                    env.Type != MsgType.ControlDisplayInfo)
                {
                    return;
                }

                var sendIndex = Interlocked.Increment(ref controlDisplayInfoSendCount);
                if (sendIndex == 1)
                {
                    firstSendStarted.TrySetResult();
                    await releaseFirstSend.Task.WaitAsync(sendCt);
                }
            };

            const int totalRevisions = 257;
            for (var i = 0; i < totalRevisions; i++)
            {
                _ = helper.SendControlDisplayInfoAsync(
                    new ControlDisplayInfoMessageV1
                    {
                        DisplayId = "primary",
                        Revision = i,
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
                        TsUtcMs = i + 1,
                    },
                    cts.Token);
            }

            await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await WaitUntilAsync(() => GetHighPriorityQueueCount(helper) == 256, TimeSpan.FromSeconds(2));

            var stopTask = helper.SendControlStopAsync(
                new ControlStopMessageV1
                {
                    RequestId = "stop-priority",
                    SessionId = helper.CurrentSessionSecurityState.SessionId!.Value.Value,
                    Reason = "test",
                },
                cts.Token);

            await WaitUntilAsync(
                () => helper.HighPriorityControlDroppedForStopCount > 0,
                TimeSpan.FromSeconds(2));

            var queuedTypes = GetQueuedHighPriorityTypes(helper);
            Assert.Equal(256, queuedTypes.Length);
            Assert.Equal(MsgType.ControlStop, queuedTypes[0]);

            releaseFirstSend.TrySetResult();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);
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

        EventHandler<IncomingJoinRequestEventArgs>? onJoin = null;
        EventHandler? onHostApproved = null;
        EventHandler? onHelperApproved = null;

        onJoin = (_, e) => joinRequestRaised.TrySetResult(e);
        onHostApproved = (_, _) => hostApproved.TrySetResult();
        onHelperApproved = (_, _) => helperApproved.TrySetResult();

        host.IncomingJoinRequest += onJoin;
        host.Approved += onHostApproved;
        helper.Approved += onHelperApproved;

        try
        {
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
        finally
        {
            host.IncomingJoinRequest -= onJoin;
            host.Approved -= onHostApproved;
            helper.Approved -= onHelperApproved;
        }
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

    private static int GetHighPriorityQueueCount(NknSignalingTransport transport)
    {
        var field = typeof(NknSignalingTransport).GetField(
            "highPriorityControlOutboundQueue",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var queue = field!.GetValue(transport);
        Assert.NotNull(queue);

        var countProperty = queue!.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(countProperty);
        return (int)(countProperty!.GetValue(queue) ?? 0);
    }

    private static T? GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T?)field!.GetValue(instance);
    }

    private static int[] GetQueuedHighPriorityDisplayInfoRevisions(NknSignalingTransport transport)
    {
        var queue = GetPrivateField<object>(transport, "highPriorityControlOutboundQueue");
        Assert.NotNull(queue);

        var sharedKey = GetPrivateField<byte[]?>(transport, "controlSessionSharedKey");
        Assert.NotNull(sharedKey);

        var sessionId = Assert.IsType<SessionId>(transport.CurrentSessionSecurityState.SessionId);
        var revisions = new List<int>();

        foreach (var queued in (System.Collections.IEnumerable)queue!)
        {
            var envelope = GetPrivateProperty<Envelope>(queued, "Envelope");
            if (envelope.Type != MsgType.ControlDisplayInfo)
            {
                continue;
            }

            var securePayload = SessionSecureEnvelopeCodec.Decrypt(
                sharedKey!,
                envelope.Payload,
                new SessionSecureEnvelopeExpectation(
                    Family: SessionSecureMessageFamily.RemoteControl,
                    MessageType: "control_display_info",
                    SessionId: sessionId));
            Assert.True(
                RemoteControlPayloadCodec.TryDeserializeControlDisplayInfo(securePayload.Plaintext, out var displayInfo),
                "Expected queued high-priority payload to deserialize as ControlDisplayInfo.");
            revisions.Add(checked((int)displayInfo.Revision));
        }

        return revisions.ToArray();
    }

    private static MsgType[] GetQueuedHighPriorityTypes(NknSignalingTransport transport)
    {
        var queue = GetPrivateField<object>(transport, "highPriorityControlOutboundQueue");
        Assert.NotNull(queue);

        var types = new List<MsgType>();
        foreach (var queued in (System.Collections.IEnumerable)queue!)
        {
            var envelope = GetPrivateProperty<Envelope>(queued, "Envelope");
            types.Add(envelope.Type);
        }

        return types.ToArray();
    }

    private static T GetPrivateProperty<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return Assert.IsType<T>(property!.GetValue(instance));
    }
}
