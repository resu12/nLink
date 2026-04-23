using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Linq;
using Avalonia.Headless;
using NLink.App.Services;
using NLink.App.Services.ScreenCapture;
using NLink.App.Services.RemoteControl;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class RemoteControlP6RuntimeTests
{
    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControlStop_HighPriority_DeliversEvenWhenMouseMoveSpamIsBlocked()
    {
        var transport = new P6TestTransport
        {
            BlockControlInputSends = true,
        };
        using var runtime = new SessionRuntime(() => transport);
        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helper);
        SetRemoteControlState(runtime, SessionRuntimeRole.Helper, "req-p6-priority", "local-helper");
        SetDisplayInfo(runtime, "primary", revision: 1);

        for (var i = 0; i < 500; i++)
        {
            _ = runtime.SendRemoteControlInputAsync(
                new ControlInputMessageV1
                {
                    Kind = "mouse_move",
                    Nx = (i % 100) / 100d,
                    Ny = (i % 100) / 100d,
                },
                CancellationToken.None);
        }

        await WaitUntilAsync(() => transport.SentControlInputCount >= 1, TimeSpan.FromSeconds(1));

        var stopTask = runtime.StopRemoteControlAsync("helper_stop", CancellationToken.None);

        await WaitUntilAsync(() => transport.SentControlStopCount >= 1, TimeSpan.FromSeconds(1));
        Assert.True(await stopTask);
        Assert.Equal(ControlState.Off, runtime.ControlState);

        transport.ReleaseControlInputSends();
        await Task.Delay(100);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControlInjectionQueue_IsBounded_AndFlushedOnStop()
    {
        var transport = new P6TestTransport();
        var injector = new BlockingRemoteInputInjector();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: null,
            watchdogDelayAsync: null,
            telemetrySink: null,
            bridgeReusePolicy: null,
            bridgeIdleDelayAsync: null,
            remoteInputInjector: injector,
            remoteCoordinateMapper: new FixedRemoteCoordinateMapper());

        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, SessionRuntimeRole.Helpee, "req-p6-queue", "controller-peer");
        SetDisplayInfo(runtime, "primary", revision: 1);

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-p6-queue",
                Kind = "mouse_move",
                Nx = 0.2d,
                Ny = 0.2d,
                DisplayId = "primary",
                DisplayInfoRevision = 1,
                Seq = 1,
            },
            peerId: "controller-peer");

        await WaitUntilAsync(() => injector.InFlightCalls > 0, TimeSpan.FromSeconds(1));

        for (var i = 0; i < 2000; i++)
        {
            transport.InjectIncomingControlInput(
                new ControlInputMessageV1
                {
                    RequestId = "req-p6-queue",
                    Kind = "mouse_move",
                    Nx = (i % 100) / 100d,
                    Ny = (i % 100) / 100d,
                    DisplayId = "primary",
                    DisplayInfoRevision = 1,
                    Seq = i + 2,
                },
                peerId: "controller-peer");
        }

        await Task.Delay(100);
        var queuedBeforeStop = GetInjectionQueueCount(runtime);
        Assert.InRange(queuedBeforeStop, 1, 256);

        Assert.True(await runtime.StopRemoteControlAsync("UserStop", CancellationToken.None));
        Assert.Equal(ControlState.Off, runtime.ControlState);

        injector.Release();
        await WaitUntilAsync(() => GetInjectionQueueCount(runtime) == 0, TimeSpan.FromSeconds(1));
        Assert.Equal(1, injector.TotalMouseMoveCalls);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControlInjectionQueue_SnapshotBurst_IsCoalesced_AndQueuedKeyStillExecutes()
    {
        using var snapshotFlag = new EnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_STATE_SNAPSHOT", "1");
        var transport = new P6TestTransport();
        var injector = new BlockingRemoteInputInjector();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: null,
            watchdogDelayAsync: null,
            telemetrySink: null,
            bridgeReusePolicy: null,
            bridgeIdleDelayAsync: null,
            remoteInputInjector: injector,
            remoteCoordinateMapper: new FixedRemoteCoordinateMapper());

        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, SessionRuntimeRole.Helpee, "req-p6-snapshot-burst", "controller-peer");
        SetDisplayInfo(runtime, "primary", revision: 1);

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-p6-snapshot-burst",
                Kind = "mouse_move",
                Nx = 0.2d,
                Ny = 0.2d,
                DisplayId = "primary",
                DisplayInfoRevision = 1,
                Seq = 1,
            },
            peerId: "controller-peer");

        await WaitUntilAsync(() => injector.InFlightCalls > 0, TimeSpan.FromSeconds(1));

        for (var i = 1; i <= 1000; i++)
        {
            transport.InjectIncomingControlStateSnapshot(
                new ControlStateSnapshotV1
                {
                    RequestId = "req-p6-snapshot-burst",
                    Seq = i,
                    TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ModifiersMask = 0,
                    MouseButtonsMask = 0,
                },
                peerId: "controller-peer");
        }

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-p6-snapshot-burst",
                Kind = "key",
                Action = "down",
                Key = "A",
                DisplayId = "primary",
                DisplayInfoRevision = 1,
                Seq = 2,
            },
            peerId: "controller-peer");

        await WaitUntilAsync(
            () =>
            {
                if (GetPrivateLongField(runtime, "remoteControlSnapshotLastReceivedSeq") != 1000)
                {
                    return false;
                }

                if (GetInjectionQueueCount(runtime) != 2)
                {
                    return false;
                }

                var queuedItems = GetQueuedInjectionItems(runtime);
                return queuedItems.SequenceEqual(new[] { "state_snapshot:1000", "key:2" });
            },
            TimeSpan.FromSeconds(1));
        var queuedItems = GetQueuedInjectionItems(runtime);
        Assert.Equal(new[] { "state_snapshot:1000", "key:2" }, queuedItems);

        injector.Release();
        await WaitUntilAsync(
            () => injector.TotalKeyCalls == 1 &&
                  GetPrivateLongField(runtime, "remoteControlSnapshotAppliedCount") == 1 &&
                  GetPrivateLongField(runtime, "remoteControlSnapshotLastAppliedSeq") == 1000,
            TimeSpan.FromSeconds(3));

        Assert.Equal(1, injector.TotalMouseMoveCalls);
        Assert.Equal(1, injector.TotalKeyCalls);
        Assert.Equal(1L, GetPrivateLongField(runtime, "remoteControlSnapshotAppliedCount"));
        Assert.Equal(1000L, GetPrivateLongField(runtime, "remoteControlSnapshotLastAppliedSeq"));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControlInjectionQueue_WhenFull_EvictsPendingSnapshot_BeforeDroppingOlderKeyInput()
    {
        using var snapshotFlag = new EnvironmentOverride("NLINK_FEATURE_REMOTE_CONTROL_STATE_SNAPSHOT", "1");
        var transport = new P6TestTransport();
        var injector = new BlockingRemoteInputInjector();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: null,
            watchdogDelayAsync: null,
            telemetrySink: null,
            bridgeReusePolicy: null,
            bridgeIdleDelayAsync: null,
            remoteInputInjector: injector,
            remoteCoordinateMapper: new FixedRemoteCoordinateMapper());

        AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
        SetRemoteControlState(runtime, SessionRuntimeRole.Helpee, "req-p6-snapshot-overflow", "controller-peer");
        SetDisplayInfo(runtime, "primary", revision: 1);

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-p6-snapshot-overflow",
                Kind = "mouse_move",
                Nx = 0.2d,
                Ny = 0.2d,
                DisplayId = "primary",
                DisplayInfoRevision = 1,
                Seq = 1,
            },
            peerId: "controller-peer");

        await WaitUntilAsync(() => injector.InFlightCalls > 0, TimeSpan.FromSeconds(1));

        for (var i = 0; i < 255; i++)
        {
            transport.InjectIncomingControlInput(
                new ControlInputMessageV1
                {
                    RequestId = "req-p6-snapshot-overflow",
                    Kind = "key",
                    Action = "down",
                    Key = "A",
                    DisplayId = "primary",
                    DisplayInfoRevision = 1,
                    Seq = i + 2,
                },
                peerId: "controller-peer");
        }

        transport.InjectIncomingControlStateSnapshot(
            new ControlStateSnapshotV1
            {
                RequestId = "req-p6-snapshot-overflow",
                Seq = 1,
                TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ModifiersMask = 0,
                MouseButtonsMask = 0,
            },
            peerId: "controller-peer");

        await WaitUntilAsync(() => GetInjectionQueueCount(runtime) == 256, TimeSpan.FromSeconds(1));

        transport.InjectIncomingControlInput(
            new ControlInputMessageV1
            {
                RequestId = "req-p6-snapshot-overflow",
                Kind = "key",
                Action = "down",
                Key = "A",
                DisplayId = "primary",
                DisplayInfoRevision = 1,
                Seq = 257,
            },
            peerId: "controller-peer");

        await WaitUntilAsync(
            () =>
            {
                var queued = GetQueuedInjectionItems(runtime);
                return queued.Count == 256 &&
                       queued.Contains("key:257") &&
                       !queued.Any(item => item.StartsWith("state_snapshot:", StringComparison.Ordinal));
            },
            TimeSpan.FromSeconds(1));

        injector.Release();
        await WaitUntilAsync(() => injector.TotalKeyCalls > 0, TimeSpan.FromSeconds(1));

        Assert.Equal(1, injector.TotalMouseMoveCalls);
        Assert.Equal(0L, GetPrivateLongField(runtime, "remoteControlSnapshotAppliedCount"));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControlLogRateLimit_SuppressesRepeatedKeyWithinWindow()
    {
        var transport = new P6TestTransport();
        using var runtime = new SessionRuntime(() => transport);

        var method = typeof(SessionRuntime).GetMethod(
            "ShouldEmitRemoteControlRateLimitedLog",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var window = TimeSpan.FromMilliseconds(40);
        var first = Assert.IsType<bool>(method!.Invoke(runtime, new object?[] { "p6-rate-limit", window }));
        var second = Assert.IsType<bool>(method!.Invoke(runtime, new object?[] { "p6-rate-limit", window }));
        Assert.True(first);
        Assert.False(second);

        await Task.Delay(60);

        var third = Assert.IsType<bool>(method!.Invoke(runtime, new object?[] { "p6-rate-limit", window }));
        Assert.True(third);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControlAndTransportScreenShare_RepeatedChurn_LeavesNoLingeringWork()
    {
        const int cycleCount = 20;

        ActiveRuntimeCounters.ResetForTests();
        var transport = new P6TestTransport();
        var injector = new SlowRemoteInputInjector(TimeSpan.FromMilliseconds(20));
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: null,
            watchdogDelayAsync: null,
            telemetrySink: null,
            bridgeReusePolicy: null,
            bridgeIdleDelayAsync: null,
            remoteInputInjector: injector,
            remoteCoordinateMapper: new FixedRemoteCoordinateMapper());

        var fakeSource = new FakeScreenCaptureSource();
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 8, startBlocked: true);
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync);

        for (var cycle = 1; cycle <= cycleCount; cycle++)
        {
            var sessionId = $"session-cycle-{cycle}";

            AttachConnectedRuntime(runtime, transport, SessionRuntimeRole.Helpee);
            SetRemoteControlState(runtime, SessionRuntimeRole.Helpee, $"req-p6-cycle-{cycle}", "controller-peer");
            SetDisplayInfo(runtime, "primary", revision: cycle);

            await coordinator.StartAsync(sessionId, CancellationToken.None);
            Assert.True(coordinator.IsActive, $"Expected screen-share coordinator to be active in cycle {cycle}.");
            Assert.True(fakeSource.IsStarted, $"Expected capture source to be started in cycle {cycle}.");

            fakeSource.RaiseFrame(
                640,
                360,
                new byte[] { (byte)cycle, 1, 2 },
                "h264",
                isKeyFrame: true,
                streamEpoch: cycle,
                streamConfig: new ScreenShareVideoStreamConfigV1
                {
                    SessionId = sessionId,
                    StreamEpoch = cycle,
                    Encoding = "h264",
                    CodecProfile = "baseline",
                    DecoderConfigData = new byte[] { 1, 2, 3 },
                });
            await WaitUntilAsync(() => probe.CurrentInFlight > 0, TimeSpan.FromSeconds(1));

            for (var i = 0; i < 64; i++)
            {
                transport.InjectIncomingControlInput(
                    new ControlInputMessageV1
                    {
                        RequestId = $"req-p6-cycle-{cycle}",
                        Kind = "mouse_move",
                        Nx = (i % 32) / 31d,
                        Ny = (i % 16) / 15d,
                        DisplayId = "primary",
                        DisplayInfoRevision = cycle,
                        Seq = (cycle * 1000L) + i + 1,
                    },
                    peerId: "controller-peer");
            }

            await WaitUntilAsync(
                () => runtime.RemoteControlInjectionQueueDepth > 0 || injector.TotalMouseMoveCalls > 0,
                TimeSpan.FromSeconds(1));

            if ((cycle % 2) == 0)
            {
                await coordinator.HandleDisconnectedAsync();
                await runtime.DisconnectAsync();
            }
            else
            {
                await coordinator.StopAsync(sendStopMessage: false, reason: "cycle_stop", CancellationToken.None);
                Assert.True(await runtime.StopRemoteControlAsync("cycle_stop", CancellationToken.None));
                await WaitUntilAsync(() => runtime.ControlState == ControlState.Off, TimeSpan.FromSeconds(1));
                await runtime.DisconnectAsync();
            }

            await WaitUntilAsync(
                () => runtime.ControlState == ControlState.Off &&
                      runtime.RemoteControlInjectionQueueDepth == 0 &&
                      runtime.RemoteControlOutgoingMouseMoveQueueDepth == 0 &&
                      injector.InFlightCalls == 0,
                TimeSpan.FromSeconds(2));

            await WaitUntilAsync(
                () => !coordinator.IsActive &&
                      !fakeSource.IsStarted &&
                      fakeSource.FrameSubscriberCount == 0 &&
                      probe.CurrentInFlight == 0,
                TimeSpan.FromSeconds(2));

            await WaitUntilAsync(
                () => ActiveRuntimeCounters.Snapshot().ActiveTransportTasks == 0,
                TimeSpan.FromSeconds(2));

            var payloadsAfterStop = probe.PayloadsSent;
            fakeSource.RaiseFrame(
                640,
                360,
                new byte[] { (byte)(cycle + 100), 9, 9 },
                "h264",
                isKeyFrame: true,
                streamEpoch: cycle,
                streamConfig: new ScreenShareVideoStreamConfigV1
                {
                    SessionId = sessionId,
                    StreamEpoch = cycle,
                    Encoding = "h264",
                    CodecProfile = "baseline",
                    DecoderConfigData = new byte[] { 1, 2, 3 },
                });
            await Task.Delay(50);
            Assert.Equal(payloadsAfterStop, probe.PayloadsSent);
            Assert.True(
                probe.CanceledSendCount >= cycle,
                $"Expected blocked screen-share send cancellation by cycle {cycle}. Canceled={probe.CanceledSendCount}.");
        }

        Assert.Equal(cycleCount, fakeSource.StartCallCount);
        Assert.Equal(cycleCount, fakeSource.StopCallCount);
        Assert.Equal(cycleCount, fakeSource.DisposeCallCount);

        var counters = ActiveRuntimeCounters.Snapshot();
        Assert.Equal(0, counters.ActiveSessions);
        Assert.Equal(0, counters.ActiveConnectAttempts);
        Assert.Equal(0, counters.ActiveRetryTimers);
        Assert.Equal(0, counters.ActiveWatchdogs);
        Assert.Equal(0, counters.ActiveTransportTasks);
        Assert.Equal(0, counters.ActiveBridgeIoReaders);
    }

    private static void AttachConnectedRuntime(
        SessionRuntime runtime,
        P6TestTransport transport,
        SessionRuntimeRole role)
    {
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        runtime.SetRoleForTests(role);
        _ = InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                helperIdentity: role == SessionRuntimeRole.Helpee ? "controller-peer" : "helper-peer",
                helpeeIdentity: "helpee-peer"));
        _ = InvokePrivateMethod(runtime, "RefreshRemoteControlCapabilitiesFromTransport");
    }

    private static SessionSecurityState CreateApprovedSecurityState(
        string helperIdentity,
        string helpeeIdentity,
        CapabilityGrant capabilities = CapabilityGrant.RemoteControl)
    {
        var sessionId = new SessionId(
            $"rc_p6_{NormalizeSessionToken(helpeeIdentity)}_{NormalizeSessionToken(helperIdentity)}");
        var helpeeAddress = new PeerAddress(helpeeIdentity);
        var helperAddress = new PeerAddress(helperIdentity);
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = true,
        }).WithHandshakeVerified(helperAddress)
          .WithApproval(new SessionGrant(
              helperAddress,
              capabilities,
              sessionId,
              DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    private static string NormalizeSessionToken(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        foreach (var ch in value)
        {
            buffer[length++] = char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_';
        }

        return new string(buffer[..length]);
    }

    private static void SetRemoteControlState(
        SessionRuntime runtime,
        SessionRuntimeRole role,
        string requestId,
        string controllerPeerId)
    {
        SetPrivateField(
            runtime,
            "remoteControlSessionState",
            new RemoteControlSessionState(
                ControlState: ControlState.Active,
                ControllerPeerId: role == SessionRuntimeRole.Helper ? controllerPeerId : "controller-peer",
                CurrentControlRequestId: requestId,
                ConsentToken: null,
                SupportsRemoteControl: true,
                PeerSupportsRemoteControl: true));
    }

    private static void SetDisplayInfo(SessionRuntime runtime, string displayId, long revision)
    {
        SetPrivateField(
            runtime,
            "latestRemoteControlDisplayInfo",
            new ControlDisplayInfoMessageV1
            {
                DisplayId = displayId,
                VirtualDesktopX = 0,
                VirtualDesktopY = 0,
                VirtualDesktopWidth = 1920,
                VirtualDesktopHeight = 1080,
                CaptureRegionX = 0,
                CaptureRegionY = 0,
                CaptureRegionWidth = 1920,
                CaptureRegionHeight = 1080,
                FrameWidth = 1920,
                FrameHeight = 1080,
                DpiScale = 1.0d,
                Revision = revision,
                TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
    }

    private static int GetInjectionQueueCount(SessionRuntime runtime)
    {
        return WithInjectionQueueLock(
            runtime,
            () =>
            {
                var field = typeof(SessionRuntime).GetField("remoteControlInjectionQueue", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(field);
                var queue = field!.GetValue(runtime) as System.Collections.ICollection;
                Assert.NotNull(queue);
                return queue!.Count;
            });
    }

    private static long GetPrivateLongField(SessionRuntime runtime, string fieldName)
    {
        var field = typeof(SessionRuntime).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (long)(field!.GetValue(runtime) ?? 0L);
    }

    private static IReadOnlyList<string> GetQueuedInjectionItems(SessionRuntime runtime)
    {
        return WithInjectionQueueLock(
            runtime,
            () =>
            {
                var field = typeof(SessionRuntime).GetField("remoteControlInjectionQueue", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(field);
                var queue = Assert.IsAssignableFrom<System.Collections.IEnumerable>(field!.GetValue(runtime));
                var items = new List<string>();

                foreach (var entry in queue)
                {
                    Assert.NotNull(entry);
                    var entryType = entry!.GetType();
                    var message = entryType.GetProperty("Message", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(entry) as ControlInputMessageV1;
                    if (message is not null)
                    {
                        items.Add($"{message.Kind}:{message.Seq}");
                        continue;
                    }

                    var snapshotObject = entryType.GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(entry);
                    if (snapshotObject is ControlStateSnapshotV1 snapshot)
                    {
                        items.Add($"state_snapshot:{snapshot.Seq}");
                    }
                }

                return items;
            });
    }

    private static T WithInjectionQueueLock<T>(SessionRuntime runtime, Func<T> action)
    {
        var gateField = typeof(SessionRuntime).GetField("remoteControlInjectionQueueGate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(gateField);
        var gate = gateField!.GetValue(runtime);
        Assert.NotNull(gate);

        Monitor.Enter(gate!);
        try
        {
            return action();
        }
        finally
        {
            Monitor.Exit(gate!);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        while ((DateTimeOffset.UtcNow - start) < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), $"Condition was not met within {timeout}.");
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static object? InvokePrivateMethod(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(target, args);
    }

    private sealed class FixedRemoteCoordinateMapper : IRemoteCoordinateMapper
    {
        public bool IsMappingAvailable => true;

        public (int xPx, int yPx) MapNormalizedToVirtualDesktop(double nx, double ny)
        {
            return (100, 200);
        }
    }

    private sealed class SlowRemoteInputInjector : IRemoteInputInjector
    {
        private readonly TimeSpan delay;
        private int inFlightCalls;
        private int totalMouseMoveCalls;

        public SlowRemoteInputInjector(TimeSpan delay)
        {
            this.delay = delay;
        }

        public bool IsSupported => true;
        public int InFlightCalls => Volatile.Read(ref inFlightCalls);
        public int TotalMouseMoveCalls => Volatile.Read(ref totalMouseMoveCalls);

        public void InjectMouseMoveAbsolute(int xPx, int yPx)
        {
            Interlocked.Increment(ref inFlightCalls);
            Interlocked.Increment(ref totalMouseMoveCalls);
            try
            {
                Thread.Sleep(delay);
            }
            finally
            {
                Interlocked.Decrement(ref inFlightCalls);
            }
        }

        public void InjectMouseButton(RemoteMouseButton button, RemoteButtonAction action)
        {
        }

        public void InjectMouseWheel(int deltaX, int deltaY)
        {
        }

        public void InjectKey(RemoteKey key, RemoteKeyAction action, RemoteKeyModifiers mods)
        {
        }
    }

    private sealed class BlockingRemoteInputInjector : IRemoteInputInjector
    {
        private readonly ManualResetEventSlim gate = new(false);
        private int inFlightCalls;
        private int totalMouseMoveCalls;
        private int totalKeyCalls;

        public bool IsSupported => true;
        public int InFlightCalls => Volatile.Read(ref inFlightCalls);
        public int TotalMouseMoveCalls => Volatile.Read(ref totalMouseMoveCalls);
        public int TotalKeyCalls => Volatile.Read(ref totalKeyCalls);

        public void InjectMouseMoveAbsolute(int xPx, int yPx)
        {
            Interlocked.Increment(ref inFlightCalls);
            Interlocked.Increment(ref totalMouseMoveCalls);
            try
            {
                gate.Wait(TimeSpan.FromSeconds(3));
            }
            finally
            {
                Interlocked.Decrement(ref inFlightCalls);
            }
        }

        public void InjectMouseButton(RemoteMouseButton button, RemoteButtonAction action)
        {
        }

        public void InjectMouseWheel(int deltaX, int deltaY)
        {
        }

        public void InjectKey(RemoteKey key, RemoteKeyAction action, RemoteKeyModifiers mods)
        {
            Interlocked.Increment(ref totalKeyCalls);
        }

        public void Release()
        {
            gate.Set();
        }
    }

#pragma warning disable CS0067
    private sealed class P6TestTransport :
        ISignalingTransport,
        IAddressTargetSignalingTransport,
        IAddressHostSignalingTransport,
        ISessionSecuritySignalingTransport,
        IRemoteControlCapabilityProvider,
        IRemoteControlSignalingTransport
    {
        private readonly object gate = new();
        private readonly List<ControlStopMessageV1> sentControlStops = new();
        private int sentControlInputCount;
        private readonly ManualResetEventSlim controlInputGate = new(false);
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public bool BlockControlInputSends { get; set; }

        public bool LocalSupportsRemoteControl => true;
        public bool RemoteSupportsRemoteControl => true;
        public bool SessionSupportsRemoteControl => true;
        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public int SentControlInputCount => Volatile.Read(ref sentControlInputCount);
        public int SentControlStopCount
        {
            get
            {
                lock (gate)
                {
                    return sentControlStops.Count;
                }
            }
        }

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
        public event EventHandler<RemoteControlRequestReceivedEventArgs>? RemoteControlRequestReceived;
        public event EventHandler<RemoteControlResponseReceivedEventArgs>? RemoteControlResponseReceived;
        public event EventHandler<RemoteControlStartReceivedEventArgs>? RemoteControlStartReceived;
        public event EventHandler<RemoteControlStopReceivedEventArgs>? RemoteControlStopReceived;
        public event EventHandler<RemoteControlInputReceivedEventArgs>? RemoteControlInputReceived;
        public event EventHandler<RemoteControlAckReceivedEventArgs>? RemoteControlAckReceived;
        public event EventHandler<RemoteControlStateSnapshotReceivedEventArgs>? RemoteControlStateSnapshotReceived;
        public event EventHandler<RemoteControlDisplayInfoReceivedEventArgs>? RemoteControlDisplayInfoReceived;

        public void Dispose()
        {
            controlInputGate.Set();
        }

        public Task HostByAddressAsync(CancellationToken ct) => Task.CompletedTask;
        public Task JoinByAddressAsync(string peerAddress, CancellationToken ct) => Task.CompletedTask;
        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlRequestAsync(ControlRequestMessageV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlResponseAsync(ControlResponseMessageV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlStartAsync(ControlStartMessageV1 message, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlStopAsync(ControlStopMessageV1 message, CancellationToken ct)
        {
            lock (gate)
            {
                sentControlStops.Add(message);
            }

            return Task.CompletedTask;
        }

        public Task SendControlInputAsync(ControlInputMessageV1 message, CancellationToken ct)
        {
            Interlocked.Increment(ref sentControlInputCount);
            if (!BlockControlInputSends)
            {
                return Task.CompletedTask;
            }

            if (!controlInputGate.Wait(TimeSpan.FromSeconds(3), ct))
            {
                throw new TimeoutException("Blocked control input send timed out.");
            }

            return Task.CompletedTask;
        }

        public Task SendControlAckAsync(ControlInputAckV1 message, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlStateSnapshotAsync(ControlStateSnapshotV1 message, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlDisplayInfoAsync(ControlDisplayInfoMessageV1 message, CancellationToken ct) => Task.CompletedTask;

        public void SetSessionSecurityStateForTests(SessionSecurityState nextState)
        {
            if (Equals(currentSessionSecurityState, nextState))
            {
                return;
            }

            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        }

        public void InjectIncomingControlInput(ControlInputMessageV1 message, string? peerId)
        {
            RemoteControlInputReceived?.Invoke(this, new RemoteControlInputReceivedEventArgs(message, peerId));
        }

        public void ReleaseControlInputSends()
        {
            controlInputGate.Set();
        }

        public void InjectIncomingControlStateSnapshot(ControlStateSnapshotV1 snapshot, string? peerId)
        {
            RemoteControlStateSnapshotReceived?.Invoke(this, new RemoteControlStateSnapshotReceivedEventArgs(snapshot, peerId));
        }
    }
#pragma warning restore CS0067
}

[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class RemoteControlP6SurfaceViewTests : IClassFixture<RemoteControlP6SurfaceFixture>
{
    private readonly RemoteControlP6SurfaceFixture fixture;

    public RemoteControlP6SurfaceViewTests(RemoteControlP6SurfaceFixture fixture)
    {
        this.fixture = fixture;
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task ScreenShareSurface_MouseMoveCoalescing_EmitsAtMostRatePerSecondUnderHeavyUpdates()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var view = new ScreenShareSurfaceView
            {
                CaptureEnabled = true,
                MouseMoveRateHz = 90,
            };

            var tickMethod = typeof(ScreenShareSurfaceView).GetMethod(
                "OnMouseMoveThrottleTick",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var hasPendingField = typeof(ScreenShareSurfaceView).GetField(
                "hasPendingMouseMove",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var pendingNxField = typeof(ScreenShareSurfaceView).GetField(
                "pendingMouseMoveNx",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var pendingNyField = typeof(ScreenShareSurfaceView).GetField(
                "pendingMouseMoveNy",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var intervalMethod = typeof(ScreenShareSurfaceView).GetMethod(
                "GetMouseMoveThrottleInterval",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(tickMethod);
            Assert.NotNull(hasPendingField);
            Assert.NotNull(pendingNxField);
            Assert.NotNull(pendingNyField);
            Assert.NotNull(intervalMethod);

            var interval = Assert.IsType<TimeSpan>(intervalMethod!.Invoke(null, new object[] { 90 }));
            var maxPerSecond = (int)Math.Floor(1d / interval.TotalSeconds);
            Assert.Equal(90, maxPerSecond);

            var emitted = new List<ControlInputMessageV1>();
            view.RemoteControlInputProduced += (_, e) =>
            {
                emitted.Add(e.Message);
            };

            var expectedLastNx = new List<double>();
            var expectedLastNy = new List<double>();
            for (var tick = 0; tick < maxPerSecond; tick++)
            {
                // Simulate heavy pointer-move bursts between timer ticks.
                for (var burst = 0; burst < 100; burst++)
                {
                    var nx = tick + (burst / 1000d);
                    var ny = tick + (burst / 2000d);
                    pendingNxField!.SetValue(view, nx);
                    pendingNyField!.SetValue(view, ny);
                    hasPendingField!.SetValue(view, true);
                    if (burst == 99)
                    {
                        expectedLastNx.Add(nx);
                        expectedLastNy.Add(ny);
                    }
                }

                tickMethod!.Invoke(view, new object?[] { null, EventArgs.Empty });
            }

            var mouseMoves = emitted.Where(m => string.Equals(m.Kind, "mouse_move", StringComparison.Ordinal)).ToList();
            Assert.Equal(maxPerSecond, mouseMoves.Count);
            Assert.True(mouseMoves.Count <= maxPerSecond);

            for (var i = 0; i < mouseMoves.Count; i++)
            {
                Assert.Equal(expectedLastNx[i], mouseMoves[i].Nx.GetValueOrDefault());
                Assert.Equal(expectedLastNy[i], mouseMoves[i].Ny.GetValueOrDefault());
            }

            await Task.CompletedTask;
            return true;
        }, CancellationToken.None);
    }
}

public sealed class RemoteControlP6SurfaceFixture : IDisposable
{
    public RemoteControlP6SurfaceFixture()
    {
        Session = HeadlessUnitTestSession.StartNew(typeof(AvaloniaHeadlessUiAppBootstrap));
    }

    public HeadlessUnitTestSession Session { get; }

    public void Dispose()
    {
        Session.Dispose();
    }
}

internal sealed class EnvironmentOverride : IDisposable
{
    private readonly string key;
    private readonly string? previousValue;

    public EnvironmentOverride(string key, string value)
    {
        this.key = key;
        previousValue = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(key, previousValue);
    }
}
