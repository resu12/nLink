using System.Collections.Concurrent;
using System.Text.Json;
using NLink.App.Services;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    private sealed class WriterGatePeer(string address) : FakeNknClient(address), IOwnedHandshakeClient
    {
        public BridgeStdioWriter? Writer;
        public Action? AfterFrameWritten;
        public Func<MsgType, NknBridgeChannel, Task>? BeforeOwnedWrite;
        public Func<Envelope, Task>? AfterOwnedWrite;
        public Action<Envelope>? OwnedWriteRejected;
        public readonly TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> WriteCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> SendOwnedHandshakeAsync(string destination, byte[] bytes, NknBridgeChannel channel, bool priority,
            OwnedHandshakeWrite admit, CancellationToken ct)
        {
            if (BeforeOwnedWrite is { } before && EnvelopeCodec.TryDeserialize(bytes, out var envelope))
                await before(envelope.Type, channel);
            if (Writer is { } writer)
            {
                Arrived.TrySetResult();
                var written = await writer.WriteSendFrameAsync(destination, bytes, channel, ct, admit);
                if (written) AfterFrameWritten?.Invoke();
                WriteCompleted.TrySetResult(written);
                return written;
            }
            if (admit(() => new ValueTask(channel == NknBridgeChannel.Bulk ? SendBulkAsync(destination, bytes, ct)
                : SendAsync(destination, bytes, ct))) is not { } write)
            {
                if (OwnedWriteRejected is { } rejected && EnvelopeCodec.TryDeserialize(bytes, out var denied)) rejected(denied);
                return false;
            }
            await write;
            if (AfterOwnedWrite is { } after && EnvelopeCodec.TryDeserialize(bytes, out var sent)) await after(sent);
            return true;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardedOfferCopy_CountsOnlyCompletedWriterSubmissions(bool revoke)
    {
        // This diagnostic is process-wide. Use no connected fake peers, heartbeat, or automatic offer copies.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = timeout.Token;
        var client = new WriterGatePeer("isolated-counter.address");
        using var lane = new RetryableTunaAccelerationSession(true, 0);
        using var transport = new NknSignalingTransport(client, NknTransportOptions.Load(), new NknIdentity("counter", client.Address),
            NknTunaAccelerationOptions.CreateRuntimePilot("fake-sidecar.exe", NknAccelerationLaneKind.File, runtimeLaneLimit: () => NknAccelerationLaneKind.File), lane);
        var envelope = new Envelope(1, "counter", "single-offer", MsgType.TransportAccelerationOffer, [1], 1, null);
        var binding = typeof(NknSignalingTransport).GetNestedType("PreferenceBoundOffer", System.Reflection.BindingFlags.NonPublic)!;
        SetPrivateField(transport, "outboundPreferenceOffer", Activator.CreateInstance(binding, envelope, NknAccelerationLaneKind.File));
        using var bytes = new MemoryStream();
        using var writer = new BridgeStdioWriter(bytes, leaveOpen: true);
        client.Writer = writer;
        var gate = (SemaphoreSlim)GetPrivateField(writer, "gate")!;
        await gate.WaitAsync(ct);
        Task copy;
        var before = NknRuntimeDiagnostics.Snapshot().MessagesSent;
        try
        {
            copy = (Task)InvokePrivateMethod(transport, "SendBulkEnvelopeAsync", "peer.counter.address", envelope,
                EnvelopeCodec.Serialize(envelope), ct, false, false)!;
            await client.Arrived.Task.WaitAsync(ct);
            Assert.Equal(before, NknRuntimeDiagnostics.Snapshot().MessagesSent);
            if (revoke) SetPrivateField(transport, "outboundPreferenceOffer", null);
        }
        finally { gate.Release(); }
        await copy.WaitAsync(ct);
        Assert.Equal(!revoke, await client.WriteCompleted.Task.WaitAsync(ct));
        Assert.Equal(!revoke, bytes.Length != 0);
        Assert.Equal(before + (revoke ? 0 : 1), NknRuntimeDiagnostics.Snapshot().MessagesSent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task FixedLaneFinalAck_QueuedOrdinaryHandoffIsConsumedOnlyAfterCurrentAck(
        bool failAck, bool gateWrites = false, bool installSuccessor = false)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var options = NknTransportOptions.Load();
        var hostClient = new FakeNknClient("host.fixed-ack.address");
        var peerClient = new FakeNknClient("helper.fixed-ack.address");
        using var listener = new RetryableTunaAccelerationSession(true, 0) { StrictPayerHandoffForTests = true };
        using var dialer = new RetryableTunaAccelerationSession(false, 0) { StrictPayerHandoffForTests = true };
        using var payer = new NknSignalingTransport(hostClient, options, new NknIdentity("host", hostClient.Address),
            NknTunaAccelerationOptions.CreateRuntimePilot("fake-sidecar.exe", NknAccelerationLaneKind.File), listener);
        using var peer = new NknSignalingTransport(peerClient, options, new NknIdentity("helper", peerClient.Address),
            NknTunaAccelerationOptions.CreateRuntimePilot("fake-sidecar.exe", NknAccelerationLaneKind.File, false), dialer);
        using var files = new SessionFileTransferService();
        using var peerFiles = new SessionFileTransferService();
        files.AttachTransport(payer);
        peerFiles.AttachTransport(peer);
        var session = await ApproveNknSessionAsync(payer, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        using var data = await payer.OpenFileTransferDataSessionAsync(session, "before-fixed-ack", ct);
        var beforeAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new ConcurrentQueue<string>();
        string? exactAckId = null;
        Task<bool>? ackResult = null;
        Task? coreDispatch = null;
        Func<bool>? successorFence = null;
        using var consume = new ManualResetEventSlim();
        using var finishCleanup = new ManualResetEventSlim();
        if (failAck) payer.BeforeOrdinaryFinalAckCleanupForTests = () => { cleanup.TrySetResult(); finishCleanup.Wait(ct); };
        if (gateWrites)
        {
            hostClient.BeforeSendCoreAsync = async (_, bytes, _, _) =>
            {
                if (!EnvelopeCodec.TryDeserialize(bytes, out var env) || env.Type != MsgType.TransportAccelerationAnswerAck) return;
                Interlocked.CompareExchange(ref exactAckId, env.MessageId, null);
                firstWrite.TrySetResult();
                await finishWrites.Task.WaitAsync(ct);
                if (failAck) throw new IOException("fixture: final ACK not written");
            };
            hostClient.ShouldDeliverSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var env) && env.Type == MsgType.TransportAccelerationAnswerAck)
                    delivered.Enqueue(env.MessageId);
                return Task.FromResult(true);
            };
        }
        payer.IdleSessionBeforeAnswerAckForTests = async () => { beforeAck.TrySetResult(); await submit.Task.WaitAsync(ct); };
        data.AvailabilityChanged += (_, e) =>
        {
            if (e.Reason == "test_accept") { successorFence = e.OrdinaryTunaHandoffIsCurrent; return; }
            if (e.Reason != "tuna_activation_negotiated") return;
            var fence = Assert.IsType<Func<bool>>(e.OrdinaryTunaHandoffIsCurrent);
            ackResult = Assert.IsAssignableFrom<Task<bool>>(e.OrdinaryTunaFinalAck);
            coreDispatch = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(files, "HandleDataSessionAvailabilityChangedAsync", data, e));
            var reads = 0;
            var queued = new FileTransferDataSessionAvailabilityChangedEventArgs(e.IsAvailable, e.Reason,
                e.RequiresResumeRequest, e.HandoffKind, e.TargetTransport)
            {
                OrdinaryTunaFinalAck = ackResult,
                OrdinaryTunaHandoffIsCurrent = () =>
                {
                    if (Interlocked.Increment(ref reads) == 1) return fence(); // Core event entry
                    worker.TrySetResult();
                    consume.Wait(ct); // Core priority worker, before any transfer mutation
                    var current = fence();
                    consumed.TrySetResult(current);
                    return current;
                }
            };
            InvokePrivateMethod(files, "OnDataSessionAvailabilityChanged", data, queued);
        };
        try
        {
            await payer.RequestAccelerationNegotiationAsync("session_security_state_ready", ct);
            await Task.WhenAll(beforeAck.Task, worker.Task).WaitAsync(ct);
            Assert.Equal(NknAccelerationLaneKind.File, payer.AccelerationNegotiatedLanesForTests);
            Assert.False(coreDispatch!.IsCompleted);
            if (failAck && !gateWrites) payer.FinalAckUtcMs = () => long.MaxValue;
            submit.TrySetResult();
            var submission = Assert.IsAssignableFrom<Task<bool>>(payer.IdleSessionAnswerAckForTests);
            if (gateWrites)
            {
                await firstWrite.Task.WaitAsync(ct);
                Assert.False(submission.IsCompleted);
                Assert.False(ackResult!.IsCompleted);
                Assert.False(coreDispatch.IsCompleted);
                Assert.Empty(delivered);
                finishWrites.TrySetResult();
            }
            if (failAck)
            {
                await cleanup.Task.WaitAsync(ct);
                Assert.False(submission.IsCompleted);
                Assert.True(ackResult!.IsCompletedSuccessfully);
                Assert.False(await ackResult);
                consume.Set();
                Assert.False(await consumed.Task.WaitAsync(ct));
                await coreDispatch.WaitAsync(ct);
                if (installSuccessor)
                {
                    payer.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, session);
                    Assert.True(successorFence!());
                }
                finishCleanup.Set();
            }
            Assert.Equal(!failAck, await submission.WaitAsync(ct));
            Assert.Equal(!failAck || installSuccessor ? NknAccelerationLaneKind.File : NknAccelerationLaneKind.None, payer.AccelerationNegotiatedLanesForTests);
            if (installSuccessor) Assert.True(successorFence!());
            if (gateWrites && !failAck) { Assert.NotEmpty(delivered); Assert.All(delivered, id => Assert.Equal(exactAckId, id)); }
            if (failAck) Assert.Empty(delivered);
        }
        finally { submit.TrySetResult(); finishWrites.TrySetResult(); consume.Set(); finishCleanup.Set(); }
        Assert.Equal(!failAck, await consumed.Task.WaitAsync(ct));
        await coreDispatch!.WaitAsync(ct);
    }

    [Fact]
    public async Task LegacyFinalAck_DelayedBindCannotReplaceSuccessor()
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var options = NknTransportOptions.Load();
        var a = new FakeNknClient("host.legacy-bind.address");
        var b = new FakeNknClient("helper.legacy-bind.address");
        using var payer = new NknSignalingTransport(a, options, new NknIdentity("host", a.Address),
            NknTunaAccelerationOptions.Disabled, new RetryableTunaAccelerationSession(false, 0));
        using var peer = new NknSignalingTransport(b, options, new NknIdentity("helper", b.Address),
            NknTunaAccelerationOptions.Disabled, new RetryableTunaAccelerationSession(false, 0));
        var session = await ApproveNknSessionAsync(payer, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var beforeBind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCanBind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempted = new ConcurrentQueue<string>();
        var conflictingWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        payer.IdleSessionBeforeAnswerAckForTests = async () =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            beforeBind.TrySetResult();
            await oldCanBind.Task.WaitAsync(ct);
        };
        a.BeforeSendCoreAsync = async (_, bytes, _, _) =>
        {
            if (!EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type != MsgType.TransportAccelerationAnswerAck) return;
            attempted.Enqueue(e.MessageId);
            if (attempted.TryPeek(out var firstId) && e.MessageId != firstId) conflictingWrite.TrySetResult();
            beforeWrite.TrySetResult();
            await canWrite.Task.WaitAsync(ct);
        };
        Task<bool> SendLegacyAck(string nonce) =>
            Assert.IsAssignableFrom<Task<bool>>(InvokePrivateMethod(payer, "SendAccelerationAnswerAckAsync",
                new TransportAccelerationAnswerPayload { SessionId = session, Accepted = true, Nonce = nonce,
                    PayerDecisionId = 11, ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds() },
                NknAccelerationLaneKind.File, ct));
        try
        {
            payer.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, session);
            var old = SendLegacyAck("predecessor");
            await beforeBind.Task.WaitAsync(ct);
            payer.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, session);
            var successor = SendLegacyAck("successor");
            await beforeWrite.Task.WaitAsync(ct);
            var shadow = GetPrivateField(payer, "pendingAccelerationAnswerAckReplyShadow")!;
            Assert.Equal("successor", GetPrivatePropertyValue<string>(shadow, "Nonce"));
            var id = GetPrivatePropertyValue<Envelope>(shadow, "Envelope").MessageId;
            oldCanBind.TrySetResult();
            var first = await Task.WhenAny(old, conflictingWrite.Task).WaitAsync(ct);
            Assert.Same(old, first);
            Assert.False(await old);
            Assert.Same(shadow, GetPrivateField(payer, "pendingAccelerationAnswerAckReplyShadow"));
            canWrite.TrySetResult();
            Assert.True(await successor.WaitAsync(ct));
            Assert.All(attempted, x => Assert.Equal(id, x));
        }
        finally { oldCanBind.TrySetResult(); canWrite.TrySetResult(); }
    }

    [Fact]
    public async Task DialerSuccessor_WaitingForAckRetiresQueuedOrdinaryHandoff()
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = timeout.Token;
        var options = NknTransportOptions.Load();
        var a = new FakeNknClient("host.successor-ack.address");
        var b = new FakeNknClient("helper.successor-ack.address");
        var listener = new RetryableTunaAccelerationSession(false, 0);
        using var payer = new NknSignalingTransport(a, options, new NknIdentity("host", a.Address),
            NknTunaAccelerationOptions.Disabled, listener);
        using var dialer = new NknSignalingTransport(b, options, new NknIdentity("helper", b.Address),
            NknTunaAccelerationOptions.Disabled, new RetryableTunaAccelerationSession(false, 0));
        var session = await ApproveNknSessionAsync(payer, dialer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        using var payerData = await payer.OpenFileTransferDataSessionAsync(session, "payer-current", ct);
        using var data = await dialer.OpenFileTransferDataSessionAsync(session, "dialer-current", ct);
        TrackAuthorizedRegularV4RouteForTests(payer, payerData.TransferId);
        TrackAuthorizedRegularV4RouteForTests(dialer, data.TransferId);
        using var core = new SessionFileTransferService();
        using var consume = new ManualResetEventSlim();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.BeforeSendAsync = async (_, bytes, token) =>
        {
            if (!EnvelopeCodec.TryDeserialize(bytes, out var envelope) || envelope.Type != MsgType.TransportAccelerationAnswerAck) return;
            beforeAck.TrySetResult();
            await releaseAck.Task.WaitAsync(token);
        };
        data.AvailabilityChanged += (_, e) =>
        {
            if (e.Reason != "test_accept") return;
            var current = Assert.IsType<Func<bool>>(e.OrdinaryTunaHandoffIsCurrent);
            var reads = 0;
            var queued = new FileTransferDataSessionAvailabilityChangedEventArgs(e.IsAvailable, e.Reason, e.RequiresResumeRequest,
                e.HandoffKind, e.TargetTransport) { OrdinaryTunaHandoffIsCurrent = () =>
                {
                    if (Interlocked.Increment(ref reads) == 1) return current();
                    waiting.TrySetResult();
                    consume.Wait(ct);
                    var allowed = current();
                    admitted.TrySetResult(allowed);
                    return allowed;
                } };
            InvokePrivateMethod(core, "OnDataSessionAvailabilityChanged", data, queued);
        };
        try
        {
            dialer.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, session);
            await waiting.Task.WaitAsync(ct);
            listener.SetCanListen(true);
            await payer.RequestAccelerationNegotiationAsync("runtime_unlock", ct);
            await beforeAck.Task.WaitAsync(ct);
            Assert.NotNull(GetPrivateField(dialer, "pendingAccelerationAnswerAckNonce"));
            consume.Set();
            Assert.False(await admitted.Task.WaitAsync(ct));
        }
        finally { consume.Set(); releaseAck.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyFinalAck_WriterRejectsStaleGenerationOrReplacedEnvelope(bool replaceEnvelope)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = timeout.Token;
        var options = NknTransportOptions.Load();
        var a = new WriterGatePeer("host.legacy-write.address");
        var b = new FakeNknClient("helper.legacy-write.address");
        using var payer = new NknSignalingTransport(a, options, new NknIdentity("host", a.Address),
            NknTunaAccelerationOptions.Disabled, new RetryableTunaAccelerationSession(false, 0));
        using var peer = new NknSignalingTransport(b, options, new NknIdentity("helper", b.Address),
            NknTunaAccelerationOptions.Disabled, new RetryableTunaAccelerationSession(false, 0));
        var session = await ApproveNknSessionAsync(payer, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new ConcurrentQueue<string>();
        a.BeforeOwnedWrite = async (type, _) =>
        {
            if (type != MsgType.TransportAccelerationAnswerAck) return;
            arrived.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        a.BeforeSendCoreAsync = (_, bytes, _, _) =>
        {
            if (EnvelopeCodec.TryDeserialize(bytes, out var frame) && frame.Type == MsgType.TransportAccelerationAnswerAck)
            {
                written.Enqueue(frame.MessageId);
                arrived.TrySetResult(); // Also observe an incorrectly unguarded write.
            }
            return Task.CompletedTask;
        };
        Task<bool> Send(string nonce) => Assert.IsAssignableFrom<Task<bool>>(InvokePrivateMethod(payer,
            "SendAccelerationAnswerAckAsync", new TransportAccelerationAnswerPayload { SessionId = session, Accepted = true,
                Nonce = nonce, PayerDecisionId = 11, ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds() },
            NknAccelerationLaneKind.File, ct));
        object Shadow() => GetPrivateField(payer, "pendingAccelerationAnswerAckReplyShadow")!;
        try
        {
            payer.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, session);
            var old = Send("old");
            await arrived.Task.WaitAsync(ct);
            var oldId = GetPrivatePropertyValue<Envelope>(Shadow(), "Envelope").MessageId;
            Task<bool>? current = null;
            if (replaceEnvelope)
            {
                current = Send("new"); // Same answer generation, different exact envelope.
                await WaitUntilAsync(() => GetPrivatePropertyValue<string>(Shadow(), "Nonce") == "new", TimeSpan.FromSeconds(3));
            }
            else
            {
                payer.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, session);
                Assert.Equal(oldId, GetPrivatePropertyValue<Envelope>(Shadow(), "Envelope").MessageId);
            }
            release.TrySetResult();
            Assert.False(await old.WaitAsync(ct));
            Assert.True(await (current ?? Send("new")).WaitAsync(ct));
            var newId = GetPrivatePropertyValue<Envelope>(Shadow(), "Envelope").MessageId;
            Assert.NotEqual(oldId, newId);
            Assert.NotEmpty(written);
            Assert.All(written, id => Assert.Equal(newId, id));
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixedLaneRefreshedOffer_RejectsPredecessorAndKeepsListenerBinding(bool replaceListener)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = timeout.Token;
        var client = new WriterGatePeer("fixed.refresh.address");
        using var lane = new RetryableTunaAccelerationSession(true, 0) { StrictPayerHandoffForTests = true };
        using var transport = new NknSignalingTransport(client, NknTransportOptions.Load(), new NknIdentity("fixed", client.Address),
            NknTunaAccelerationOptions.Disabled, lane);
        Assert.True(await lane.EnsureListenerSidecarConnectedAsync("peer.address", ct));
        var lease = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
        var original = new Envelope(1, "fixed", "original", MsgType.TransportAccelerationOffer, [1], 1, null);
        var refreshed = original with { MessageId = "refreshed" };
        var type = typeof(NknSignalingTransport).GetNestedType("PreferenceBoundOffer", System.Reflection.BindingFlags.NonPublic)!;
        var owner = Activator.CreateInstance(type, original, NknAccelerationLaneKind.File)!;
        type.GetProperty("ListenerLease")!.SetValue(owner, lease);
        var bindings = GetPrivateField(transport, "writerBoundOffers")!;
        bindings.GetType().GetMethod("Add")!.Invoke(bindings, [original, owner]);
        SetPrivateField(transport, "outboundPreferenceOffer", owner);

        using var bytes = new MemoryStream();
        using var writer = new BridgeStdioWriter(bytes, leaveOpen: true);
        client.Writer = writer;
        var gate = (SemaphoreSlim)GetPrivateField(writer, "gate")!;
        Task Send(Envelope envelope) => (Task)InvokePrivateMethod(transport, "SendBulkEnvelopeAsync", "peer.address",
            envelope, EnvelopeCodec.Serialize(envelope), ct, false, false)!;
        await gate.WaitAsync(ct);
        Task pending;
        try
        {
            pending = Send(original);
            await client.Arrived.Task.WaitAsync(ct);
            lock (GetPrivateField(transport, "accelerationGate")!) InvokePrivateMethod(transport, "RebindOfferEnvelopeLocked", owner, refreshed);
        }
        finally { gate.Release(); }
        await pending.WaitAsync(ct);
        Assert.Equal(0, bytes.Length);
        if (replaceListener) Assert.True(await lane.EnsureListenerSidecarConnectedAsync("peer.address", ct));
        await Send(refreshed).WaitAsync(ct);
        Assert.Equal(!replaceListener, bytes.Length > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeUnlockExpiresBeforeAnswer_RetiresItsDialAndOnlyItsOwnPause(bool borrowedPause)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var opts = NknTransportOptions.Load();
        var a = new FakeNknClient("host.unbound-expiry.address");
        var b = new WriterGatePeer("peer.unbound-expiry.address");
        using var host = new NknSignalingTransport(a, opts, new NknIdentity("host", a.Address));
        using var lane = new RetryableTunaAccelerationSession(false, 0) { StrictPayerHandoffForTests = true };
        using var peer = new NknSignalingTransport(b, opts, new NknIdentity("peer", b.Address), NknTunaAccelerationOptions.Disabled, lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var data = await peer.OpenFileTransferDataSessionAsync(session, "transfer_unbound_expiry", ct);
        TrackAuthorizedRegularV4RouteForTests(peer, data.TransferId);
        if (borrowedPause)
            Assert.True((bool)InvokePrivateMethod(peer, "PauseFileTransferDataSessionsForTunaActivationNegotiation", "prior", session, "test")!);
        var writes = 0;
        b.ShouldDeliverSendAsync = (_, bytes, _) =>
        {
            if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.TransportAccelerationAnswer)
                Interlocked.Increment(ref writes);
            return Task.FromResult(false);
        };
        var beforeSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        peer.BeforeFirstAcceptedAnswerSendForTests = () => { beforeSend.TrySetResult(); release.Wait(ct); };
        var expiry = DateTimeOffset.UtcNow.AddSeconds(3);
        var original = CreateOfferPayload(session, "unbound-expiry", ["file"], expiresAtUnixMs: expiry.ToUnixTimeMilliseconds(), payerDecisionId: 7);
        original.Trigger = "runtime_unlock";
        Task Handle(TransportAccelerationOfferPayload offer) => (Task)InvokePrivateMethod(peer,
            "HandleTransportAccelerationOfferAsync", a.Address, offer, offer.Nonce, ct)!;
        var handling = Task.Run(() => Handle(original), ct);
        try
        {
            await beforeSend.Task.WaitAsync(ct);
            Assert.Equal(original.Nonce, GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
            Assert.Null(GetPrivateField(peer, "pendingAccelerationAnswerReplyShadow"));
            Assert.Equal(!borrowedPause, GetPrivateField(peer, "pendingAnswerOwnsPause"));
            Assert.True(lane.HasProvisionalDialForTests);
            var remaining = expiry - DateTimeOffset.UtcNow;
            if (remaining >= TimeSpan.Zero) await Task.Delay(remaining + TimeSpan.FromMilliseconds(20), ct);
        }
        finally { release.Set(); }
        await handling.WaitAsync(ct);
        Assert.Equal(0, Volatile.Read(ref writes));
        Assert.Null(GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
        Assert.False(lane.HasProvisionalDialForTests);
        Assert.Equal(1, lane.StopCalls);
        Assert.Equal(borrowedPause ? session : null, GetPrivateField(peer, "fileTransferTunaActivationPauseSessionId"));

        var next = CreateOfferPayload(session, "after-unbound-expiry", ["file"], payerDecisionId: 8);
        next.Trigger = "runtime_unlock";
        await Handle(next).WaitAsync(ct);
        Assert.Equal(next.Nonce, GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
        Assert.True(lane.HasProvisionalDialForTests);
        Assert.True(Volatile.Read(ref writes) > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositiveAnswerWaitingOnPhysicalDialLock_UsesDeadlineAtWriteStart(bool expire)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var opts = NknTransportOptions.Load();
        var a = new FakeNknClient("host.final-write-expiry.address");
        var b = new WriterGatePeer("peer.final-write-expiry.address");
        using var host = new NknSignalingTransport(a, opts, new NknIdentity("host", a.Address));
        const string endpoint = "test-writer-dial";
        using var lane = NknTunaLaneOwnershipTests.InstalledFakeDialForOffer(true, endpoint, a.Address);
        using var peer = new NknSignalingTransport(b, opts, new NknIdentity("peer", b.Address), NknTunaAccelerationOptions.Disabled, lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        b.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);
        var expiry = DateTimeOffset.UtcNow.AddSeconds(expire ? 3 : 30);
        var offer = CreateOfferPayload(session, "lock-wait", ["file"], expiresAtUnixMs: expiry.ToUnixTimeMilliseconds(), payerDecisionId: 7);
        offer.TunaAddress = endpoint;
        var previous = NknSignalingTransport.AccelerationAnswerReplayAttemptsOverrideForTests;
        NknSignalingTransport.AccelerationAnswerReplayAttemptsOverrideForTests = 0;
        try
        {
            await ((Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync", a.Address, offer, "original", ct)!).WaitAsync(ct);
            var shadow = GetPrivateField(peer, "pendingAccelerationAnswerReplyShadow")!;
            var envelope = Assert.IsType<Envelope>(shadow.GetType().GetProperty("Envelope")!.GetValue(shadow));
            var payload = EnvelopeCodec.Serialize(envelope);
            using var bytes = new MemoryStream();
            using var writer = new BridgeStdioWriter(bytes, leaveOpen: true);
            b.Writer = writer;
            var transportGate = GetPrivateField(peer, "accelerationGate")!;
            var laneGate = GetPrivateField(lane, "gate")!;
            bool WaitingWithTransportGate(Thread thread)
            {
                if ((thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0) return false;
                if (!Monitor.TryEnter(transportGate)) return true;
                Monitor.Exit(transportGate);
                return false;
            }

            Task<bool> write;
            var sender = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (laneGate)
            {
                write = Task.Factory.StartNew(() =>
                {
                    sender.SetResult(Thread.CurrentThread);
                    return (Task<bool>)InvokePrivateMethod(peer, "SendOwnedHandshakeIfBound", a.Address, envelope,
                        payload, NknBridgeChannel.Control, false, ct)!;
                }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
                b.Arrived.Task.Wait(ct);
                var thread = sender.Task.GetAwaiter().GetResult();
                Assert.True(SpinWait.SpinUntil(() => WaitingWithTransportGate(thread), TimeSpan.FromSeconds(2)));
                Assert.True(DateTimeOffset.UtcNow < expiry);
                if (expire) Assert.True(SpinWait.SpinUntil(() => DateTimeOffset.UtcNow >= expiry, TimeSpan.FromSeconds(4)));
            }
            Assert.Equal(!expire, await write.WaitAsync(ct));
            Assert.Equal(!expire, await b.WriteCompleted.Task.WaitAsync(ct));
            Assert.Equal(expire ? Array.Empty<byte>() : BridgeBinaryProtocol.BuildSendFrame(a.Address, payload, NknBridgeChannel.Control), bytes.ToArray());
        }
        finally { b.Writer = null; NknSignalingTransport.AccelerationAnswerReplayAttemptsOverrideForTests = previous; }
    }

    public enum OriginalAnswerExpiry { AfterReplay, BeforeBind, BeforeReplay }

    [Theory]
    [InlineData(false, 30, OriginalAnswerExpiry.AfterReplay)]
    [InlineData(true, 30, OriginalAnswerExpiry.AfterReplay)]
    [InlineData(false, -15, OriginalAnswerExpiry.AfterReplay)]
    [InlineData(false, 30, OriginalAnswerExpiry.BeforeBind)]
    [InlineData(false, 30, OriginalAnswerExpiry.BeforeReplay)]
    public async Task OrdinaryDuplicateBeforeOriginalAnswerBind_UsesEarliestDeadlineAndOneWorker(
        bool rejectDuplicateWrite, int duplicateExtensionSeconds, OriginalAnswerExpiry expiry)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = timeout.Token;
        var opts = NknTransportOptions.Load();
        var a = new FakeNknClient("host.early-duplicate.address");
        var b = new WriterGatePeer("peer.early-duplicate.address");
        using var host = new NknSignalingTransport(a, opts, new NknIdentity("host", a.Address));
        using var lane = new RetryableTunaAccelerationSession(false, 0) { StrictPayerHandoffForTests = true };
        using var peer = new NknSignalingTransport(b, opts, new NknIdentity("peer", b.Address), NknTunaAccelerationOptions.Disabled, lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var originalExpiry = DateTimeOffset.UtcNow.AddSeconds(expiry == OriginalAnswerExpiry.AfterReplay ? 45 : 4);
        var duplicateExpiry = originalExpiry.AddSeconds(duplicateExtensionSeconds);
        var offer = CreateOfferPayload(session, "early-duplicate", ["file"], expiresAtUnixMs: originalExpiry.ToUnixTimeMilliseconds(), payerDecisionId: 7);
        var duplicate = CreateOfferPayload(session, offer.Nonce, ["file"], expiresAtUnixMs: duplicateExpiry.ToUnixTimeMilliseconds(), payerDecisionId: 7);
        offer.Trigger = duplicate.Trigger = "session_ready";
        var expectedExpiry = Math.Min(offer.ExpiresAtUnixMs, duplicate.ExpiresAtUnixMs);
        Task Handle(TransportAccelerationOfferPayload incoming, string id) =>
            (Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync", a.Address, incoming, id, ct)!;
        var workers = new ConcurrentQueue<(long Generation, Task Worker)>();
        peer.AnswerReplayWorkerStartedForTests = (generation, worker) => workers.Enqueue((generation, worker));
        var copies = new ConcurrentQueue<byte[]>();
        var lateWrites = 0;
        var rejectWrite = rejectDuplicateWrite;
        b.BeforeSendAsync = (_, bytes, _) => Volatile.Read(ref rejectWrite) &&
            EnvelopeCodec.TryDeserialize(bytes, out var env) && env.Type == MsgType.TransportAccelerationAnswer
                ? Task.FromException(new IOException("fake_answer_write_failed"))
                : Task.CompletedTask;
        b.ShouldDeliverSendAsync = (_, bytes, _) =>
        {
            if (EnvelopeCodec.TryDeserialize(bytes, out var env) && env.Type == MsgType.TransportAccelerationAnswer)
            {
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expectedExpiry) Interlocked.Increment(ref lateWrites);
                copies.Enqueue(bytes.ToArray());
            }
            return Task.FromResult(false); // No remote delivery, ACK, or further offers.
        };

        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        peer.BeforeFirstAcceptedAnswerSendForTests = () =>
        {
            pending.TrySetResult();
            resume.Wait(ct);
        };
        var previousDelay = NknSignalingTransport.AccelerationAnswerReplayDelayOverrideForTests;
        var previousAttempts = NknSignalingTransport.AccelerationAnswerReplayAttemptsOverrideForTests;
        NknSignalingTransport.AccelerationAnswerReplayDelayOverrideForTests = expiry == OriginalAnswerExpiry.BeforeReplay
            ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(50);
        NknSignalingTransport.AccelerationAnswerReplayAttemptsOverrideForTests = 1;
        try
        {
            var logStart = GetOperationalLogLength();
            var original = Task.Run(() => Handle(offer, "original"), ct);
            long generation;
            try
            {
                await pending.Task.WaitAsync(ct);
                Assert.Equal(offer.Nonce, GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
                generation = Assert.IsType<long>(GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration"));
                Assert.True(generation > 0);
                Assert.Null(GetPrivateField(peer, "pendingAccelerationAnswerReplyShadow"));
                if (expiry == OriginalAnswerExpiry.BeforeBind)
                {
                    var remaining = originalExpiry - DateTimeOffset.UtcNow;
                    if (remaining >= TimeSpan.Zero) await Task.Delay(remaining + TimeSpan.FromMilliseconds(20), ct);
                }
                await Handle(duplicate, "early-copy").WaitAsync(ct);
                Assert.Equal(generation, GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration"));
                if (expiry != OriginalAnswerExpiry.BeforeBind)
                {
                    var shadow = GetPrivateField(peer, "pendingAccelerationAnswerReplyShadow")!;
                    Assert.NotNull(shadow);
                    Assert.Equal(expectedExpiry, Assert.IsType<long>(shadow.GetType().GetProperty("DeadlineUtcMs")!.GetValue(shadow)));
                    Assert.Equal(rejectDuplicateWrite, copies.IsEmpty);
                    Assert.Contains($"event=tuna_acceleration_answer_{(rejectDuplicateWrite ? "rejected" : "sent")}; accepted=1;", ReadOperationalLogTail(logStart));
                }
            }
            finally
            {
                Volatile.Write(ref rejectWrite, false);
                resume.Set();
                await original.WaitAsync(ct);
            }

            if (expiry == OriginalAnswerExpiry.BeforeBind)
            {
                Assert.Empty(copies);
                Assert.Empty(workers);
                Assert.Null(GetPrivateField(peer, "pendingAccelerationAnswerReplyShadow"));
                return;
            }

            var started = Assert.Single(workers);
            Assert.Equal(generation, started.Generation);
            await started.Worker.WaitAsync(ct);
            Assert.Equal(0, Volatile.Read(ref lateWrites));
            var replaySubmissions = ReadOperationalLogTail(logStart).Split('\n').Where(line =>
                line.Contains("event=tuna_acceleration_answer_replay_sent; attempt=1; max_attempts=1;") &&
                line.Contains($"generation={generation};") && line.Contains("inbound_msg_id=early-copy;"));
            if (expiry == OriginalAnswerExpiry.BeforeReplay) Assert.Empty(replaySubmissions);
            else Assert.Single(replaySubmissions);
            var expected = copies.First();
            Assert.True(EnvelopeCodec.TryDeserialize(expected, out var envelope));
            var key = (byte[])InvokePrivateMethod(host, "GetControlSessionSharedKeyOrThrow")!;
            var answer = JsonSerializer.Deserialize<TransportAccelerationAnswerPayload>(
                SessionSecureEnvelopeCodec.Decrypt(key, envelope.Payload).Plaintext)!;
            Assert.True(answer.Accepted);
            Assert.Equal(expectedExpiry, answer.ExpiresAtUnixMs);
            Assert.All(copies, bytes => Assert.Equal(expected, bytes));
            Assert.Equal(generation, GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration"));
            Assert.Equal(1, lane.StartDialerCalls);
        }
        finally
        {
            NknSignalingTransport.AccelerationAnswerReplayDelayOverrideForTests = previousDelay;
            NknSignalingTransport.AccelerationAnswerReplayAttemptsOverrideForTests = previousAttempts;
        }
    }

    [Theory]
    [InlineData("runtime_unlock")]
    [InlineData("session_ready")]
    public async Task ExactDuplicateOffer_WritesTheOriginalAnswerAfterTargetRefresh(string trigger)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var opts = NknTransportOptions.Load();
        var a = new FakeNknClient("host.refresh.address");
        var b = new WriterGatePeer("peer.refresh.address");
        using var host = new NknSignalingTransport(a, opts, new NknIdentity("host", a.Address));
        using var lane = new RetryableTunaAccelerationSession(false, 0) { StrictPayerHandoffForTests = true };
        using var peer = new NknSignalingTransport(b, opts, new NknIdentity("peer", b.Address), NknTunaAccelerationOptions.Disabled, lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare,
            suppressAutomaticAccelerationNegotiation: true);
        var data = await peer.OpenFileTransferDataSessionAsync(session, "transfer_duplicate_answer", ct);
        TrackAuthorizedRegularV4RouteForTests(peer, data.TransferId);
        var offer = CreateOfferPayload(session, "refresh-answer", ["file", "screen"], payerDecisionId: 7);
        offer.Trigger = trigger;
        Task Handle(string id) => (Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync", a.Address, offer, id, ct)!;
        byte[]? dropped = null;
        b.ShouldDeliverSendAsync = (_, bytes, _) =>
        {
            if (EnvelopeCodec.TryDeserialize(bytes, out var env) && env.Type == MsgType.TransportAccelerationAnswer) dropped = bytes.ToArray();
            return Task.FromResult(false);
        };
        var previousDelay = NknSignalingTransport.AccelerationAnswerReplayDelayOverrideForTests;
        NknSignalingTransport.AccelerationAnswerReplayDelayOverrideForTests = TimeSpan.FromMinutes(1);
        try
        {
            var lanesAtReservation = offer.SupportedLanes;
            using (Assert.IsAssignableFrom<IDisposable>(InvokePrivateMethod(peer, "ReserveDialEndpoint", offer, a.Address)))
            {
                // No pending ACK yet: the reservation itself must retain the incoming offer's semantics.
                offer.SupportedLanes = ["file"];
                Assert.Null(InvokePrivateMethod(peer, "ReserveDialEndpoint", offer, a.Address));
                Assert.Equal(0L, InvokePrivateMethod(peer, "BeginPendingAccelerationAnswerAck", offer, NknAccelerationLaneKind.File));
                offer.SupportedLanes = lanesAtReservation;
            }
            await Handle("original").WaitAsync(ct);
            Assert.NotNull(dropped);
            var generation = GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration");
            using var bytes = new MemoryStream();
            using var writer = new BridgeStdioWriter(bytes, leaveOpen: true);
            b.Writer = writer;
            var originalLanes = offer.SupportedLanes;
            offer.SupportedLanes = ["file"];
            // Another handler passed its first check before the original was installed.
            Assert.Null(InvokePrivateMethod(peer, "ReserveDialEndpoint", offer, a.Address));
            Assert.Equal(0L, InvokePrivateMethod(peer, "BeginPendingAccelerationAnswerAck", offer, NknAccelerationLaneKind.File));
            await Handle("same-nonce-changed-lanes").WaitAsync(ct);
            offer.SupportedLanes = ["file,screen"]; // Not two selectable lanes, even though a comma join is identical.
            await Handle("same-nonce-invalid-lane").WaitAsync(ct);
            offer.SupportedLanes = originalLanes;
            var endpoint = offer.TunaAddress;
            offer.TunaAddress = "different-tuna-endpoint";
            await Handle("same-nonce-changed-endpoint").WaitAsync(ct);
            offer.TunaAddress = endpoint;
            Assert.Equal(0, bytes.Length);
            Assert.False(b.Arrived.Task.IsCompleted);
            Assert.Equal(generation, GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration"));
            Assert.Equal(1, lane.StartDialerCalls);
            offer.SupportedLanes = originalLanes;
            await Handle("redelivery").WaitAsync(ct);
            Assert.True(await b.WriteCompleted.Task.WaitAsync(ct));
            var frames = bytes.ToArray();
            // The existing send policy may submit more than one copy. Each must be the same cached answer.
            for (var offset = 0; offset < frames.Length;)
            {
                var header = BridgeBinaryProtocol.ParseHeader(frames.AsSpan(offset, BridgeBinaryProtocol.HeaderSize));
                offset += BridgeBinaryProtocol.HeaderSize;
                var frame = BridgeBinaryProtocol.DecodeFrame(header, frames.AsSpan(offset, header.BodyLength));
                Assert.Equal(dropped, frame.Payload);
                offset += header.BodyLength;
            }
            Assert.NotEmpty(frames);
            Assert.Equal(generation, GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration"));
            Assert.Equal(1, lane.StartDialerCalls);
            b.Writer = null;
        }
        finally { NknSignalingTransport.AccelerationAnswerReplayDelayOverrideForTests = previousDelay; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NegativeAnswerAtWriter_RechecksSupersession(bool supersede)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        var opts = NknTransportOptions.Load();
        var a = new FakeNknClient("host.writer-negative.address");
        var b = new WriterGatePeer("peer.writer-negative.address");
        using var host = new NknSignalingTransport(a, opts, new NknIdentity("host", a.Address));
        using var lane = new RetryableTunaAccelerationSession(false, 0);
        using var peer = new NknSignalingTransport(b, opts, new NknIdentity("peer", b.Address), NknTunaAccelerationOptions.Disabled, lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        using var bytes = new MemoryStream();
        using var writer = new BridgeStdioWriter(bytes, leaveOpen: true);
        b.Writer = writer;
        var gate = (SemaphoreSlim)GetPrivateField(writer, "gate")!;
        var invalid = CreateOfferPayload(session, "writer-negative-nonce", ["unknown"], payerDecisionId: 7);
        await gate.WaitAsync(ct);
        Task handling;
        try
        {
            handling = (Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync", a.Address, invalid, "negative", ct)!;
            await b.Arrived.Task.WaitAsync(ct);
            if (supersede)
            {
                var valid = CreateOfferPayload(session, invalid.Nonce, ["file"], payerDecisionId: 7);
                valid.TunaAddress += "-replacement";
                Assert.True((long)InvokePrivateMethod(peer, "BeginPendingAccelerationAnswerAck", valid, NknAccelerationLaneKind.File)! > 0);
            }
        }
        finally { gate.Release(); }
        await handling.WaitAsync(ct);
        Assert.Equal(!supersede, await b.WriteCompleted.Task.WaitAsync(ct));
        Assert.Equal(!supersede, bytes.Length > 0);
        b.Writer = null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false, true)]
    public async Task InitialTimeoutRetry_SubmitsFreshOfferOnTheSameListener(bool replaceAtWriter, bool peerReceived = false, bool failFirstRearm = false,
        bool refuseInsideRearm = false)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var opts = NknTransportOptions.Load();
        var a = new WriterGatePeer("host.retry-existing.address");
        var b = new FakeNknClient("peer.retry-existing.address");
        using var listener = new RetryableTunaAccelerationSession(false, 0) { StrictPayerHandoffForTests = true };
        using var host = new NknSignalingTransport(a, opts, new NknIdentity("host", a.Address), NknTunaAccelerationOptions.Disabled, listener);
        using var peer = new NknSignalingTransport(b, opts, new NknIdentity("peer", b.Address));
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var data = await host.OpenFileTransferDataSessionAsync(session, "transfer_listener_reuse", ct);
        TrackAuthorizedRegularV4RouteForTests(host, data.TransferId);
        var offers = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        a.ShouldDeliverSendAsync = (_, bytes, _) =>
        {
            if (!EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type != MsgType.TransportAccelerationOffer) return Task.FromResult(true);
            offers.TryAdd(e.MessageId, 0);
            return Task.FromResult(false);
        };
        listener.SetCanListen(true);
        await host.RequestAccelerationNegotiationAsync("runtime_unlock", ct);
        await WaitUntilAsync(() => offers.Count == 1 && Equals(0, GetPrivateField(host, "accelerationNegotiationScheduled")), TimeSpan.FromSeconds(5));
        var lease = Assert.IsType<NknTunaLaneLease>(listener.CaptureCurrentLaneLease());
        var starts = listener.EnsureListenerCalls;
        var nonce = Assert.IsType<string>(GetPrivateField(host, "outboundAccelerationOfferNonce"));
        var payer = (long)GetPrivateField(host, "outboundAccelerationOfferPayerDecisionId")!;
        var generation = (long)GetPrivateField(host, "outboundAccelerationOfferGeneration")!;
        var proof = GetPrivateField(host, "runtimeUnlockOfferProofState")!;
        proof.GetType().GetProperty("RetiredAnswerExpiresUtcMs")!.SetValue(proof, 1L);
        if (peerReceived)
        {
            proof.GetType().GetProperty("PeerReceived")!.SetValue(proof, true);
            var retry = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failureRetry = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failures = new ConcurrentQueue<string>();
            host.TransportAccelerationStateChanged += (_, e) => { if (e.Reason == "listener_sidecar_unavailable") failures.Enqueue(e.Reason); };
            if (failFirstRearm) listener.BeforeListenerReadyForTests = (_, _) => Task.FromResult(listener.EnsureListenerCalls != starts + 1);
            NknTunaLaneLease? successor = null;
            object? leaseBeforeRetry = null, leaseAtRearm = null;
            var rearmReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (refuseInsideRearm) listener.BeforeRearmOwnerCheckForTests = () =>
            {
                leaseAtRearm = GetPrivateField(host, "runtimeUnlockTunaPathLeaseState");
                Assert.True(listener.EnsureListenerSidecarConnectedAsync(b.Address, ct).GetAwaiter().GetResult());
                successor = listener.CaptureCurrentLaneLease();
                rearmReached.SetResult();
            };
            host.NegotiationRetryScheduledForTests = task =>
            {
                leaseBeforeRetry = GetPrivateField(host, "runtimeUnlockTunaPathLeaseState");
                var afterFailure = failFirstRearm && listener.EnsureListenerCalls == starts + 1;
                if (replaceAtWriter && (!failFirstRearm || afterFailure))
                {
                    Assert.True(listener.EnsureListenerSidecarConnectedAsync(b.Address, ct).GetAwaiter().GetResult());
                    successor = listener.CaptureCurrentLaneLease();
                }
                (afterFailure ? failureRetry : retry).TrySetResult(task);
            };
            await ((Task)InvokePrivateMethod(host, "ScheduleAccelerationOfferAnswerTimeout", nonce, null, false, 1L)!).WaitAsync(ct);
            await retry.Task.Unwrap().WaitAsync(ct);
            if (refuseInsideRearm)
            {
                await rearmReached.Task.WaitAsync(ct);
                await WaitUntilAsync(() => Equals(0, GetPrivateField(host, "accelerationNegotiationScheduled")), TimeSpan.FromSeconds(4));
                Assert.NotNull(leaseBeforeRetry);
                Assert.Equal(leaseBeforeRetry, leaseAtRearm);
                Assert.Equal(leaseBeforeRetry, GetPrivateField(host, "runtimeUnlockTunaPathLeaseState"));
                Assert.Equal(Assert.IsType<NknTunaLaneLease>(successor), listener.CaptureCurrentLaneLease());
                Assert.Equal(starts + 1, listener.EnsureListenerCalls); // Only the successor start.
                Assert.Single(offers);
                Assert.Empty(failures);
                return;
            }
            if (failFirstRearm) await failureRetry.Task.Unwrap().WaitAsync(ct);
            if (!replaceAtWriter) await WaitUntilAsync(() => offers.Count == 2, TimeSpan.FromSeconds(4));
            Assert.Equal(failFirstRearm ? 1 : 0, failures.Count);
            Assert.Equal(starts + (failFirstRearm ? 2 : 1), listener.EnsureListenerCalls);
            Assert.Equal(replaceAtWriter ? 1 : 2, offers.Count);
            Assert.NotEqual(lease, listener.CaptureCurrentLaneLease());
            if (replaceAtWriter) Assert.Equal(Assert.IsType<NknTunaLaneLease>(successor), listener.CaptureCurrentLaneLease());
            return;
        }
        using var bytes = new MemoryStream();
        using var writer = new BridgeStdioWriter(bytes, leaveOpen: true);
        var gate = (SemaphoreSlim)GetPrivateField(writer, "gate")!;
        await gate.WaitAsync(ct);
        try
        {
            a.Writer = writer;
            var previous = NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests;
            try
            {
                NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = TimeSpan.FromMilliseconds(1);
                await ((Task)InvokePrivateMethod(host, "ScheduleRuntimeUnlockOfferPeerResponseTimeout", nonce, payer, generation,
                    session, "control_to_bulk_endpoint")!).WaitAsync(ct);
            }
            finally { NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = previous; }
            await a.Arrived.Task.WaitAsync(ct);
            Assert.Equal(starts, listener.EnsureListenerCalls);
            Assert.Equal(lease, listener.CaptureCurrentLaneLease());
            if (replaceAtWriter) Assert.True(await listener.EnsureListenerSidecarConnectedAsync(b.Address, ct));
        }
        finally { gate.Release(); }
        Assert.Equal(!replaceAtWriter, await a.WriteCompleted.Task.WaitAsync(ct));
        Assert.Equal(!replaceAtWriter, bytes.Length > 0);
        if (!replaceAtWriter)
        {
            var frames = bytes.ToArray();
            var header = BridgeBinaryProtocol.ParseHeader(frames.AsSpan(0, BridgeBinaryProtocol.HeaderSize));
            var frame = BridgeBinaryProtocol.DecodeFrame(header, frames.AsSpan(BridgeBinaryProtocol.HeaderSize, header.BodyLength));
            Assert.True(EnvelopeCodec.TryDeserialize(frame.Payload, out var retry));
            Assert.Equal(MsgType.TransportAccelerationOffer, retry.Type);
            Assert.False(offers.ContainsKey(retry.MessageId));
            var key = (byte[])InvokePrivateMethod(host, "GetControlSessionSharedKeyOrThrow")!;
            var offer = JsonSerializer.Deserialize<TransportAccelerationOfferPayload>(SessionSecureEnvelopeCodec.Decrypt(key, retry.Payload).Plaintext)!;
            Assert.NotEqual(nonce, offer.Nonce);
            Assert.Equal(offer.Nonce, GetPrivateField(host, "outboundAccelerationOfferNonce"));
        }
        a.Writer = null;
    }

    [Fact]
    public async Task RetryQueue_StaleScopeCannotQueueBehindSuccessor()
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var options = NknTransportOptions.Load();
        var hostClient = new FakeNknClient("host.retry-scope.address");
        var peerClient = new FakeNknClient("peer.retry-scope.address");
        using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host", hostClient.Address));
        using var peer = new NknSignalingTransport(peerClient, options, new NknIdentity("peer", peerClient.Address));
        var session = await ApproveNknSessionAsync(host, peer, timeout.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        object Scope() => InvokePrivateMethod(peer, "CaptureRetryPauseScopeLocked", session, false)!;
        var stale = Scope();
        var offer = CreateOfferPayload(session, "retry-scope-successor", ["file"], payerDecisionId: 7);
        Assert.True((long)InvokePrivateMethod(peer, "BeginPendingAccelerationAnswerAck", offer, NknAccelerationLaneKind.File)! > 0);
        var initial = Enum.Parse(typeof(NknSignalingTransport).GetNestedType("RuntimeUnlockOfferAuthorityMode",
            System.Reflection.BindingFlags.NonPublic)!, "InitialActivation");
        bool Queue(object scope, string reason) => (bool)InvokePrivateMethod(peer, "TryQueueRuntimeUnlockNegotiationBehindPendingGeneration",
            reason, "delayed_pending_answer_ack", null, initial, null, scope)!;
        Assert.False(Queue(stale, "stale"));
        Assert.Equal(0, GetPrivateField(peer, "pendingRuntimeUnlockAccelerationNegotiation"));
        Assert.True(Queue(Scope(), "current"));
        Assert.False(Queue(stale, "stale"));
        Assert.Equal("current", GetPrivateField(peer, "pendingRuntimeUnlockAccelerationNegotiationReason"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedInitialRetry_QueuesOnlyBehindItsOriginalGeneration(bool replace)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var options = NknTransportOptions.Load();
        var hostClient = new FakeNknClient("host.delayed-retry.address");
        var peerClient = new FakeNknClient("peer.delayed-retry.address");
        using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host", hostClient.Address));
        using var lane = new RetryableTunaAccelerationSession(true, 0);
        using var peer = new NknSignalingTransport(peerClient, options, new NknIdentity("peer", peerClient.Address),
            NknTunaAccelerationOptions.Disabled, lane);
        var session = await ApproveNknSessionAsync(host, peer, timeout.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var offer = CreateOfferPayload(session, "delayed-queue-owner", ["file"], payerDecisionId: 7);
        void Install() => Assert.True((long)InvokePrivateMethod(peer, "BeginPendingAccelerationAnswerAck", offer, NknAccelerationLaneKind.File)! > 0);
        Task? retry = null;
        peer.NegotiationRetryScheduledForTests = task => retry = task;
        lock (GetPrivateField(peer, "accelerationGate")!)
        {
            if (!replace) Install();
            Assert.True((bool)InvokePrivateMethod(peer, "ScheduleInitialRuntimeUnlockAccelerationNegotiationRetry", "runtime_unlock")!);
            if (replace)
            {
                Install();
                SetPrivateField(peer, "accelerationNegotiationRetryAttempts", 4);
            }
        }
        await Assert.IsAssignableFrom<Task>(retry).WaitAsync(timeout.Token);
        Assert.Equal(replace ? 0 : 1, GetPrivateField(peer, "pendingRuntimeUnlockAccelerationNegotiation"));
        if (replace) Assert.Equal(4, GetPrivateField(peer, "accelerationNegotiationRetryAttempts"));
        Assert.Equal(offer.Nonce, GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, true, true)]
    public async Task PeerResponseTimeout_RearmsOnlyTheInstalledOffersPhysicalListener(bool replace, bool healthy = false, bool replaceDuringRecovery = false)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var options = NknTransportOptions.Load();
        var a = new FakeNknClient("host.installed-timeout.address");
        var b = new FakeNknClient("peer.installed-timeout.address");
        using var listener = new RetryableTunaAccelerationSession(true, 0) { StrictPayerHandoffForTests = true };
        using var host = new NknSignalingTransport(a, options, new NknIdentity("host", a.Address), NknTunaAccelerationOptions.Disabled, listener);
        using var peer = new NknSignalingTransport(b, options, new NknIdentity("peer", b.Address));
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        a.ShouldDeliverSendAsync = (_, bytes, _) => Task.FromResult(!EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type != MsgType.TransportAccelerationOffer);
        Assert.True(await listener.EnsureListenerSidecarConnectedAsync(b.Address, ct));
        InstallRuntimeUnlockRetryDispatchCoordinatorForTests(host);
        var data = await host.OpenFileTransferDataSessionAsync(session, "transfer_installed_timeout", ct);
        TrackAuthorizedRegularV4RouteForTests(host, data.TransferId);
        var previous = NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests;
        var priorRecovery = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        var requests = 0;
        try
        {
            NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = TimeSpan.FromMinutes(1);
            InvokePrivateMethod(host, "ArmRuntimeUnlockRetryAfterRecovery", 6L, session, "runtime_unlock_offer_send_not_observed", "regular_v4_repair_settled", false);
            await host.DrainRuntimeUnlockRetryDispatchInstallFollowUpsForTestsAsync();
            await WaitUntilAsync(() => Equals(session, GetPrivateField(host, "fileTransferTunaActivationPauseSessionId")) &&
                GetPrivateField(host, "runtimeUnlockOfferProofState") is { } state && Equals(true, state.GetType().GetProperty("AnswerTimeoutScheduled")!.GetValue(state)), TimeSpan.FromSeconds(4));
            var receipt = Assert.IsType<RuntimeUnlockRetryDispatchInstallReceipt>(GetPrivateField(host, "outboundAccelerationOfferInstallReceipt"));
            var nonce = Assert.IsType<string>(GetPrivateField(host, "outboundAccelerationOfferNonce"));
            var gen = (long)GetPrivateField(host, "outboundAccelerationOfferGeneration")!;
            var payerId = (long)GetPrivateField(host, "outboundAccelerationOfferPayerDecisionId")!;
            var lease = Assert.IsType<NknTunaLaneLease>(listener.CaptureCurrentLaneLease());
            var stops = listener.StopCalls;
            var expired = false;
            host.TransportAccelerationStateChanged += (_, args) =>
            {
                if (args.Reason != "activation_offer_not_observed") return;
                expired = true;
                if (replace) Assert.True(listener.EnsureListenerSidecarConnectedAsync(b.Address, ct).GetAwaiter().GetResult());
                if (!healthy) listener.SetCanListen(false);
            };
            var proof = GetPrivateField(host, "runtimeUnlockOfferProofState")!;
            proof.GetType().GetProperty("RetiredAnswerExpiresUtcMs")!.SetValue(proof, 1L);
            if (healthy) NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (transport, _, _) =>
            {
                if (ReferenceEquals(transport, host))
                {
                    Interlocked.Increment(ref requests);
                    if (replaceDuringRecovery) Assert.True(listener.EnsureListenerSidecarConnectedAsync(b.Address, ct).GetAwaiter().GetResult());
                }
                return false;
            };
            var originalAttempts = GetPrivateField(host, "accelerationNegotiationRetryAttempts");
            var logStart = GetOperationalLogLength();
            NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = TimeSpan.FromMilliseconds(1);
            await ((Task)InvokePrivateMethod(host, "ScheduleRuntimeUnlockOfferPeerResponseTimeout", nonce, payerId, gen,
                session, "control_to_bulk_endpoint", receipt)!).WaitAsync(ct);
            Assert.True(expired);
            var log = ReadOperationalLogTail(logStart);
            if (!healthy) Assert.Contains("event=session_recovery_contract_listener_rearm_required;", log, StringComparison.Ordinal);
            else
            {
                Assert.Equal(replace ? 0 : 1, requests);
                Assert.Equal(!replace && !replaceDuringRecovery, log.Contains("event=session_recovery_contract_listener_rearm_skipped;", StringComparison.Ordinal));
                if (replace || replaceDuringRecovery) Assert.Equal(originalAttempts, GetPrivateField(host, "accelerationNegotiationRetryAttempts"));
            }
            Assert.True(data.IsAvailable);
            Assert.Equal(stops + (replace || healthy ? 0 : 1), listener.StopCalls);
            if (replace || replaceDuringRecovery)
            {
                Assert.True(listener.IsLocalPaidListenerActive);
                Assert.NotEqual(lease, listener.CaptureCurrentLaneLease());
            }
            else if (healthy) Assert.Equal(lease, listener.CaptureCurrentLaneLease());
            else Assert.Null(listener.CaptureCurrentLaneLease());
        }
        finally
        {
            NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = previous;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = priorRecovery;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementAnswer_InheritsOnlyThePredecessorsOwnPause(bool borrowed)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var opts = NknTransportOptions.Load();
        var a = new FakeNknClient("host.inherited-pause.address");
        var b = new FakeNknClient("peer.inherited-pause.address");
        using var host = new NknSignalingTransport(a, opts, new NknIdentity("host", a.Address));
        using var lane = new RetryableTunaAccelerationSession(false, 0);
        using var peer = new NknSignalingTransport(b, opts, new NknIdentity("peer", b.Address), NknTunaAccelerationOptions.Disabled, lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var data = await peer.OpenFileTransferDataSessionAsync(session, "transfer_inherit_pause", ct);
        TrackAuthorizedRegularV4RouteForTests(peer, data.TransferId);
        b.ShouldDeliverSendAsync = (_, bytes, _) => Task.FromResult(!EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type != MsgType.TransportAccelerationAnswer);
        if (borrowed) Assert.True((bool)InvokePrivateMethod(peer, "PauseFileTransferDataSessionsForTunaActivationNegotiation", "prior", session, "test")!);
        async Task<long> AcceptOffer(string nonce)
        {
            var offer = CreateOfferPayload(session, nonce, ["file"], payerDecisionId: 7);
            await ((Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync", a.Address, offer, nonce, ct)!).WaitAsync(ct);
            Assert.Equal(nonce, GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
            return (long)GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration")!;
        }
        var first = await AcceptOffer("first-inherited-pause");
        var generation = GetPrivateField(peer, "fileTransferTunaActivationPauseGeneration");
        var second = await AcceptOffer("second-inherited-pause");
        Assert.True(second > first);
        Assert.Equal(generation, GetPrivateField(peer, "fileTransferTunaActivationPauseGeneration"));
        Task Expire(string nonce, long gen) => (Task)InvokePrivateMethod(peer, "ScheduleAccelerationAnswerAckTimeoutForReply", session, nonce, 7L, gen, null)!;
        var previous = NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests;
        try
        {
            NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests = TimeSpan.FromMilliseconds(1);
            await Expire("first-inherited-pause", first).WaitAsync(ct);
            Assert.False(data.IsAvailable);
            Assert.Equal("second-inherited-pause", GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
            await Expire("second-inherited-pause", second).WaitAsync(ct);
            Assert.Null(GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
            Assert.Equal(!borrowed, data.IsAvailable);
        }
        finally { NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests = previous; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OverlappingDials_OnlyWinningAnswerInheritsTheOwnedPause(bool firstReturnsFirst, bool borrowed)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        var opts = NknTransportOptions.Load();
        var a = new FakeNknClient("host.overlap.address");
        var b = new FakeNknClient("peer.overlap.address");
        using var host = new NknSignalingTransport(a, opts, new NknIdentity("host", a.Address));
        using var lane = new RetryableTunaAccelerationSession(false, 0);
        using var peer = new NknSignalingTransport(b, opts, new NknIdentity("peer", b.Address), NknTunaAccelerationOptions.Disabled, lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var data = await peer.OpenFileTransferDataSessionAsync(session, "transfer_overlap", ct);
        TrackAuthorizedRegularV4RouteForTests(peer, data.TransferId);
        b.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);
        if (borrowed) Assert.True((bool)InvokePrivateMethod(peer, "PauseFileTransferDataSessionsForTunaActivationNegotiation", "prior", session, "test")!);
        (Task Handling, Task Ready, TaskCompletionSource Release) Start(string nonce)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lane.AfterOfferDialReadyForTests = () => { ready.SetResult(); return release.Task.WaitAsync(ct); };
            var payload = CreateOfferPayload(session, nonce, ["file"], payerDecisionId: 7);
            return ((Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync", a.Address, payload, nonce, ct)!, ready.Task, release);
        }
        var first = Start("overlapping-first");
        await first.Ready.WaitAsync(ct);
        var second = Start("overlapping-second");
        await second.Ready.WaitAsync(ct);
        var pause = GetPrivateField(peer, "fileTransferTunaActivationPauseGeneration");
        var early = firstReturnsFirst ? first : second;
        var late = firstReturnsFirst ? second : first;
        early.Release.SetResult();
        try
        {
            await early.Handling.WaitAsync(ct);
            Assert.False(data.IsAvailable);
            Assert.Equal(pause, GetPrivateField(peer, "fileTransferTunaActivationPauseGeneration"));
        }
        finally { late.Release.SetResult(); }
        await late.Handling.WaitAsync(ct);
        Assert.Equal("overlapping-second", GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
        var gen = (long)GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration")!;
        var prior = NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests;
        try
        {
            NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests = TimeSpan.FromMilliseconds(1);
            await ((Task)InvokePrivateMethod(peer, "ScheduleAccelerationAnswerAckTimeoutForReply", session, "overlapping-second", 7L, gen, null)!).WaitAsync(ct);
            Assert.Null(GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
            Assert.Equal(!borrowed, data.IsAvailable);
        }
        finally { NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests = prior; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task QueuedPositiveAnswer_RequiresItsPhysicalDialAtSend(bool replace, bool atWriter = false)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        var opts = NknTransportOptions.Load();
        var a = new FakeNknClient("host.queued-positive.address");
        var b = new WriterGatePeer("peer.queued-positive.address");
        using var host = new NknSignalingTransport(a, opts, new NknIdentity("host", a.Address));
        using var lane = new RetryableTunaAccelerationSession(false, 0);
        using var peer = new NknSignalingTransport(b, opts, new NknIdentity("peer", b.Address), NknTunaAccelerationOptions.Disabled, lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        b.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);
        Task Handle(string nonce) => (Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync", a.Address,
            CreateOfferPayload(session, nonce, ["file"], payerDecisionId: 7), nonce, ct)!;
        await Handle("queued-positive-first").WaitAsync(ct);
        var shadow = GetPrivateField(peer, "pendingAccelerationAnswerReplyShadow")!;
        var envelope = Assert.IsType<Envelope>(shadow.GetType().GetProperty("Envelope")!.GetValue(shadow));
        var wire = 0;
        b.ShouldDeliverSendAsync = (_, bytes, _) =>
        {
            if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.TransportAccelerationAnswer) Interlocked.Increment(ref wire);
            return Task.FromResult(false);
        };
        var gate = (SemaphoreSlim)GetPrivateField(peer, "outboundSendGate")!;
        var high = Enum.Parse(typeof(NknSignalingTransport).GetNestedType("ControlOutboundLane", System.Reflection.BindingFlags.NonPublic)!, "High");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bytes = new MemoryStream();
        using var writer = new BridgeStdioWriter(bytes, leaveOpen: true);
        if (atWriter) b.Writer = writer;
        var blockedGate = atWriter ? (SemaphoreSlim)GetPrivateField(writer, "gate")! : gate;
        await blockedGate.WaitAsync(ct);
        var queued = (Task<bool>)InvokePrivateMethod(peer, "QueueControlEnvelopeAsync", a.Address, envelope, high, ct)!;
        Task? replacement = null;
        try
        {
            if (atWriter) await b.Arrived.Task.WaitAsync(ct);
            if (replace)
            {
                lane.AfterOfferDialReadyForTests = () => { ready.SetResult(); return release.Task.WaitAsync(ct); };
                replacement = Handle("queued-positive-second");
                await ready.Task.WaitAsync(ct);
            }
            Assert.Same(shadow, GetPrivateField(peer, "pendingAccelerationAnswerReplyShadow"));
        }
        finally { blockedGate.Release(); }
        try
        {
            Assert.Equal(!replace, await queued.WaitAsync(ct));
            if (atWriter) Assert.Equal(replace ? Array.Empty<byte>() : BridgeBinaryProtocol.BuildSendFrame(a.Address,
                EnvelopeCodec.Serialize(envelope), NknBridgeChannel.Control), bytes.ToArray());
            else Assert.Equal(replace ? 0 : 1, Volatile.Read(ref wire));
        }
        finally { release.TrySetResult(); }
        if (replacement is not null) await replacement.WaitAsync(ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreflightRetryExhaustion_DoesNotOwnAnUnrelatedPause(bool pauseDuringNotification)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var opts = NknTransportOptions.Load();
        var a = new FakeNknClient("host.exhaustion.address");
        var b = new FakeNknClient("peer.exhaustion.address");
        using var lane = new RetryableTunaAccelerationSession(true, 0);
        using var host = new NknSignalingTransport(a, opts, new NknIdentity("host", a.Address), NknTunaAccelerationOptions.Disabled, lane);
        using var peer = new NknSignalingTransport(b, opts, new NknIdentity("peer", b.Address));
        var session = await ApproveNknSessionAsync(host, peer, timeout.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var data = await host.OpenFileTransferDataSessionAsync(session, "transfer_unrelated_pause", timeout.Token);
        TrackAuthorizedRegularV4RouteForTests(host, data.TransferId);
        void Pause() => Assert.True((bool)InvokePrivateMethod(host, "PauseFileTransferDataSessionsForTunaActivationNegotiation", "owner", session, "test")!);
        if (!pauseDuringNotification) Pause();
        var notified = false;
        host.TransportAccelerationStateChanged += (_, e) =>
        {
            if (e.Reason != "preflight_listener_unavailable") return;
            notified = true;
            if (pauseDuringNotification) Pause();
        };
        SetPrivateField(host, "accelerationNegotiationRetryAttempts", 99);
        InvokePrivateMethod(host, "RejectAccelerationOfferPreflight", "session_security_state_ready", "listener_unavailable", true);
        Assert.True(notified);
        Assert.False(data.IsAvailable);
        Assert.Equal(session, GetPrivateField(host, "fileTransferTunaActivationPauseSessionId"));
        Assert.Equal(pauseDuringNotification ? 99 : 100, GetPrivateField(host, "accelerationNegotiationRetryAttempts"));
    }

    [Theory]
    [InlineData("current")]
    [InlineData("scheduled")]
    [InlineData("registering")]
    public async Task RetryOfferRegistration_RequiresTheSchedulingOwner(string replaceAt)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var opts = NknTransportOptions.Load();
        var a = new FakeNknClient("host.registration.address");
        var b = new FakeNknClient("peer.registration.address");
        using var lane = new RetryableTunaAccelerationSession(true, 0) { StrictPayerHandoffForTests = true };
        using var host = new NknSignalingTransport(a, opts, new NknIdentity("host", a.Address), NknTunaAccelerationOptions.Disabled, lane);
        using var peer = new NknSignalingTransport(b, opts, new NknIdentity("peer", b.Address));
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var successor = CreateOfferPayload(session, "registration-successor", ["file"], payerDecisionId: 7);
        void Install() => Assert.True((long)InvokePrivateMethod(host, "BeginPendingAccelerationAnswerAck", successor, NknAccelerationLaneKind.File)! > 0);
        var scheduled = false;
        host.TransportAccelerationStateChanged += (_, e) =>
        {
            if (e.Reason != "negotiation_scheduled_session_security_state_ready") return;
            scheduled = true;
            if (replaceAt == "scheduled") Install();
        };
        if (replaceAt == "registering")
            lane.AfterListenerOfferCapture = () => { lane.AfterListenerOfferCapture = null; Install(); };
        var offers = 0;
        a.ShouldDeliverSendAsync = (_, bytes, _) =>
        {
            if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.TransportAccelerationOffer) Interlocked.Increment(ref offers);
            return Task.FromResult(false);
        };
        var scope = InvokePrivateMethod(host, "CaptureRetryPauseScopeLocked", session, false);
        InvokePrivateMethod(host, "ScheduleAccelerationNegotiationIfEligible", "session_security_state_ready", null, false, false, 0L, null, null, null, scope);
        Assert.True(scheduled);
        await WaitUntilAsync(() => Equals(0, GetPrivateField(host, "accelerationNegotiationScheduled")), TimeSpan.FromSeconds(6));
        if (replaceAt == "current")
        {
            Assert.NotNull(GetPrivateField(host, "outboundAccelerationOfferNonce"));
            Assert.True(Volatile.Read(ref offers) > 0);
        }
        else
        {
            Assert.Equal(successor.Nonce, GetPrivateField(host, "pendingAccelerationAnswerAckNonce"));
            Assert.Null(GetPrivateField(host, "outboundAccelerationOfferNonce"));
            Assert.Equal(0, Volatile.Read(ref offers));
        }
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("expired-borrowed")]
    [InlineData("rejected")]
    [InlineData("rejected-borrowed")]
    [InlineData("deselected")]
    [InlineData("deselected-borrowed")]
    public async Task RetiringAnswer_HandsOffOrReleasesOnlyItsOwnPause(string scenario)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        var opts = NknTransportOptions.Load();
        var a = new FakeNknClient("host.retiring-answer.address");
        var b = new FakeNknClient("peer.retiring-answer.address");
        using var host = new NknSignalingTransport(a, opts, new NknIdentity("host", a.Address));
        using var lane = new RetryableTunaAccelerationSession(false, 0) { StrictPayerHandoffForTests = true };
        const NknAccelerationLaneKind both = NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen;
        var allowed = both;
        var deselect = scenario.StartsWith("deselected", StringComparison.Ordinal);
        using var peer = new NknSignalingTransport(b, opts, new NknIdentity("peer", b.Address), deselect
            ? NknTunaAccelerationOptions.CreateRuntimePilot("fake-sidecar.exe", both, false, () => allowed)
            : NknTunaAccelerationOptions.Disabled, lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare,
            suppressAutomaticAccelerationNegotiation: true);
        var data = await peer.OpenFileTransferDataSessionAsync(session, "transfer_retiring_answer", ct);
        TrackAuthorizedRegularV4RouteForTests(peer, data.TransferId);
        var borrowed = scenario.EndsWith("borrowed", StringComparison.Ordinal);
        if (borrowed) Assert.True((bool)InvokePrivateMethod(peer, "PauseFileTransferDataSessionsForTunaActivationNegotiation", "prior", session, "test")!);
        var rejected = scenario.StartsWith("rejected", StringComparison.Ordinal);
        var offersAnswered = 0;
        b.ShouldDeliverSendAsync = (_, bytes, _) =>
        {
            if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.TransportAccelerationAnswer) Interlocked.Increment(ref offersAnswered);
            return Task.FromResult(false);
        };
        Task Handle(string nonce)
        {
            var offer = CreateOfferPayload(session, nonce, deselect && nonce == "first-retiring" ? ["screen", "file"] : ["file"], payerDecisionId: 7);
            if (rejected) offer.Trigger = "runtime_unlock";
            return (Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync", a.Address, offer, nonce, ct)!;
        }
        if (rejected)
        {
            var before = (long)GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration")!;
            var logAtOffer = GetOperationalLogLength();
            lane.AfterOfferDialReadyForTests = () =>
            {
                Assert.False(data.IsAvailable);
                ((System.Collections.IDictionary)GetPrivateField(peer, "fileTransferRoutePublicationAuthorities")!).Clear();
                return Task.CompletedTask;
            };
            await Handle("first-retiring").WaitAsync(ct);
            Assert.Contains("event=runtime_unlock_filetransfer_owner_missing_before_path_lease", ReadOperationalLogTail(logAtOffer));
            Assert.Equal(before + 2, GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration")); // installed, then cleared
            Assert.Null(GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
            Assert.Equal(0, offersAnswered);
            Assert.Equal(!borrowed, data.IsAvailable);
            return;
        }
        await Handle("first-retiring").WaitAsync(ct);
        var first = (long)GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration")!;
        var acquired = GetPrivateField(peer, "fileTransferTunaActivationPauseGeneration");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lane.AfterOfferDialReadyForTests = () => { entered.SetResult(); return release.Task.WaitAsync(ct); };
        var next = Handle("second-retiring");
        var prior = NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests;
        Task Expire(string nonce, long gen) => (Task)InvokePrivateMethod(peer, "ScheduleAccelerationAnswerAckTimeoutForReply", session, nonce, 7L, gen, null)!;
        try
        {
            await entered.Task.WaitAsync(ct);
            NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests = TimeSpan.FromMilliseconds(1);
            if (deselect)
            {
                Assert.Equal(both, GetPrivateField(peer, "pendingAccelerationAnswerAckLanes"));
                allowed = NknAccelerationLaneKind.File;
                peer.RetireDeselectedAccelerationOffers();
                Assert.Null(GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
            }
            else await Expire("first-retiring", first).WaitAsync(ct);
            Assert.False(data.IsAvailable);
            Assert.Equal(acquired, GetPrivateField(peer, "fileTransferTunaActivationPauseGeneration"));
            NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests = TimeSpan.FromMinutes(1);
            release.SetResult();
            await next.WaitAsync(ct);
            Assert.Equal("second-retiring", GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
            var second = (long)GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration")!;
            NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests = TimeSpan.FromMilliseconds(1);
            await Expire("second-retiring", second).WaitAsync(ct);
            Assert.Equal(!borrowed, data.IsAvailable);
        }
        finally { release.TrySetResult(); NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests = prior; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredRetryWake_RequiresOriginalPauseAfterAckClears(bool replace)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var opts = NknTransportOptions.Load();
        var a = new FakeNknClient("host.deferred-wake.address");
        var b = new FakeNknClient("peer.deferred-wake.address");
        using var lane = new RetryableTunaAccelerationSession(true, 0);
        using var host = new NknSignalingTransport(a, opts, new NknIdentity("host", a.Address), NknTunaAccelerationOptions.Disabled, lane);
        using var peer = new NknSignalingTransport(b, opts, new NknIdentity("peer", b.Address));
        var session = await ApproveNknSessionAsync(host, peer, timeout.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var offer = CreateOfferPayload(session, "deferred-owner", ["file"], payerDecisionId: 7);
        Assert.True((long)InvokePrivateMethod(host, "BeginPendingAccelerationAnswerAck", offer, NknAccelerationLaneKind.File)! > 0);
        var retry = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wake = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.NegotiationRetryScheduledForTests = t => retry.TrySetResult(t);
        host.ScopedRetryWakeScheduledForTests = t => wake.TrySetResult(t);
        Assert.True((bool)InvokePrivateMethod(host, "ScheduleInitialRuntimeUnlockAccelerationNegotiationRetry", "runtime_unlock")!);
        await retry.Task.Unwrap().WaitAsync(timeout.Token);
        Assert.Equal(1, GetPrivateField(host, "pendingRuntimeUnlockAccelerationNegotiation"));
        var scheduled = 0;
        host.TransportAccelerationStateChanged += (_, e) => { if (e.Reason.StartsWith("negotiation_scheduled_", StringComparison.Ordinal)) Interlocked.Increment(ref scheduled); };
        if (replace) Assert.True((bool)InvokePrivateMethod(host, "PauseFileTransferDataSessionsForTunaActivationNegotiation", "replacement", session, "test")!);
        lock (GetPrivateField(host, "accelerationGate")!)
            InvokePrivateMethod(host, "ClearPendingAccelerationAnswerAckLocked");
        await wake.Task.Unwrap().WaitAsync(timeout.Token);
        Assert.Equal(replace ? 0 : 1, scheduled);
        if (replace) Assert.Null(GetPrivateField(host, "outboundAccelerationOfferNonce"));
    }

    [Fact]
    public async Task InstalledRetry_AbandonedAcceptedAliasSettlesItsExactNonceWithoutSending()
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var opts = NknTransportOptions.Load();
        var a = new FakeNknClient("host.alias-retired.address");
        var b = new FakeNknClient("peer.alias-retired.address");
        using var lane = new RetryableTunaAccelerationSession(true, 0) { StrictPayerHandoffForTests = true };
        using var host = new NknSignalingTransport(a, opts, new NknIdentity("host", a.Address), NknTunaAccelerationOptions.Disabled, lane);
        using var peer = new NknSignalingTransport(b, opts, new NknIdentity("peer", b.Address));
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var sent = 0;
        a.ShouldDeliverSendAsync = (_, bytes, _) =>
        {
            if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.TransportAccelerationOffer) Interlocked.Increment(ref sent);
            return Task.FromResult(false);
        };
        Assert.True(await lane.EnsureListenerSidecarConnectedAsync(b.Address, ct));
        var settled = new TaskCompletionSource<RuntimeUnlockRetryDispatchSettlementDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        InstallRuntimeUnlockRetryDispatchCoordinatorForTests(host, settlementDecisionTransform: decision =>
        {
            if (decision.Report.Reason == "offer_registration_superseded") settled.TrySetResult(decision);
            return decision;
        });
        RuntimeUnlockRetryDispatchExecutionObservation? accepted = null;
        NknTunaLaneLease? replacement = null;
        ((IRuntimeUnlockRetryDispatchAuthorityBridge)host).RuntimeUnlockRetryDispatchExecutionObserved += (_, args) =>
        {
            if (args.Observation.AttemptPhase != RuntimeUnlockRetryDispatchAttemptPhase.AliasBound || args.Decision?.Accepted != true) return;
            accepted = args.Observation;
            Assert.True(lane.EnsureListenerSidecarConnectedAsync(b.Address, ct).GetAwaiter().GetResult());
            replacement = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
        };
        var data = await host.OpenFileTransferDataSessionAsync(session, "transfer_alias_retired", ct);
        TrackAuthorizedRegularV4RouteForTests(host, data.TransferId);
        InvokePrivateMethod(host, "ArmRuntimeUnlockRetryAfterRecovery", 6L, session, "runtime_unlock_offer_send_not_observed", "regular_v4_repair_settled", false);
        await host.DrainRuntimeUnlockRetryDispatchInstallFollowUpsForTestsAsync();
        var decision = await settled.Task.WaitAsync(ct);
        Assert.True(decision.Accepted, decision.Reason);
        var alias = Assert.IsType<RuntimeUnlockRetryDispatchExecutionObservation>(accepted);
        Assert.Equal(alias.Authority, decision.Report.Authority);
        Assert.Equal(alias.OfferGeneration, decision.Report.OfferGeneration);
        Assert.Equal(alias.OfferNonce, decision.Report.OfferNonce);
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch, decision.Report.Outcome);
        Assert.False(decision.Report.DispatchObserved);
        Assert.NotNull(replacement);
        Assert.Equal(replacement, lane.CaptureCurrentLaneLease());
        Assert.Null(GetPrivateField(host, "outboundAccelerationOfferNonce"));
        Assert.Equal(0, Volatile.Read(ref sent));
    }

    [Fact]
    public async Task ActivationPause_DelayedBroadcastCannotUndoRelease()
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        var hostClient = new FakeNknClient("host.reverse-pause.address");
        var peerClient = new FakeNknClient("peer.reverse-pause.address");
        var options = NknTransportOptions.Load();
        using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host", hostClient.Address));
        using var peer = new NknSignalingTransport(peerClient, options, new NknIdentity("peer", peerClient.Address));
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var first = await peer.OpenFileTransferDataSessionAsync(session, "transfer_reverse_a", ct);
        var second = await peer.OpenFileTransferDataSessionAsync(session, "transfer_reverse_b", ct);
        var pauses = 0;
        void OnAvailability(object? sender, FileTransferDataSessionAvailabilityChangedEventArgs e)
        {
            if (!e.IsAvailable && ++pauses == 1)
                Assert.True((bool)InvokePrivateMethod(peer, "TryResumeMatchingActivationPause", "release", session, "test", null)!);
        }
        first.AvailabilityChanged += OnAvailability;
        second.AvailabilityChanged += OnAvailability;
        Assert.True((bool)InvokePrivateMethod(peer, "PauseFileTransferDataSessionsForTunaActivationNegotiation",
            "offer", session, "peer_offer")!);
        Assert.Equal(1, pauses);
        Assert.Null(GetPrivateField(peer, "fileTransferTunaActivationPauseSessionId"));
        foreach (var data in new[] { first, second })
        {
            Assert.True(data.IsAvailable);
            var field = data.GetType().GetField("tunaActivationPauseCts",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.False(Assert.IsType<CancellationTokenSource>(field?.GetValue(data)).IsCancellationRequested);
        }
    }

    [Fact]
    public async Task AnswerAckTimeout_ReleaseWriteAndSuccessorInstallAreMutuallyExclusive()
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        var hostClient = new FakeNknClient("host.atomic-pause.address");
        var peerClient = new FakeNknClient("peer.atomic-pause.address");
        var options = NknTransportOptions.Load();
        using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host", hostClient.Address));
        using var peer = new NknSignalingTransport(peerClient, options, new NknIdentity("peer", peerClient.Address));
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var data = await peer.OpenFileTransferDataSessionAsync(session, "transfer_atomic_pause", ct);
        bool Pause() => (bool)InvokePrivateMethod(peer, "PauseFileTransferDataSessionsForTunaActivationNegotiation",
            "offer", session, "peer_offer")!;
        Assert.True(Pause());
        var generations = ((long)GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration")!,
            (long)GetPrivateField(peer, "fileTransferTunaActivationPauseGeneration")!);
        var writeGate = GetPrivateField(peer, "activationPausePublicationGate")!;
        var contended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? successor = null;
        var sawInstall = false;
        peer.ActivationPausePublishingForTests = installing =>
        {
            Assert.True(Monitor.IsEntered(writeGate));
            if (installing)
            {
                sawInstall = true;
                Assert.True(data.IsAvailable); // The old write finished before successor installation.
                return;
            }
            Assert.False(data.IsAvailable);
            successor = Task.Run(() =>
            {
                var entered = Monitor.TryEnter(writeGate);
                if (entered) Monitor.Exit(writeGate);
                contended.SetResult(!entered);
                return Pause();
            }, ct);
            Assert.True(contended.Task.WaitAsync(ct).GetAwaiter().GetResult());
        };
        Assert.True((bool)InvokePrivateMethod(peer, "TryResumeMatchingActivationPause",
            "answer_ack_timeout", session, "answer_ack_timeout", generations)!);
        Assert.NotNull(successor);
        Assert.True(await successor!.WaitAsync(ct));
        Assert.True(sawInstall);
        Assert.False(data.IsAvailable);
        var sendTokenField = data.GetType().GetField("tunaActivationPauseCts",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.True(Assert.IsType<CancellationTokenSource>(sendTokenField?.GetValue(data)).IsCancellationRequested);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false, true)]
    public async Task QueuedNegativeAnswer_RechecksPendingEndpointBeforeSending(bool conflict, bool whileDialing, bool unrelated = false, bool retireBeforeDrain = false)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = timeout.Token;
        var options = NknTransportOptions.Load();
        var hostClient = new FakeNknClient("host.queued-negative.address");
        var peerClient = new FakeNknClient("peer.queued-negative.address");
        using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host", hostClient.Address));
        using var lane = new RetryableTunaAccelerationSession(false, 0);
        using var peer = new NknSignalingTransport(peerClient, options, new NknIdentity("peer", peerClient.Address),
            NknTunaAccelerationOptions.Disabled, lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var invalid = CreateOfferPayload(session, "queued-negative-endpoint-nonce", ["unknown"], payerDecisionId: 7);
        var valid = CreateOfferPayload(session, unrelated ? "independent-offer-nonce" : invalid.Nonce, ["file"], payerDecisionId: 7);
        if (conflict) valid.TunaAddress += "-replacement";
        var bulkFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var directFailures = 0;
        var sends = 0;
        peerClient.BeforeSendCoreAsync = (_, bytes, channel, _) =>
        {
            if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.TransportAccelerationAnswer && !bulkFailed.Task.IsCompleted)
            {
                Interlocked.Increment(ref directFailures);
                if (channel == NknBridgeChannel.Bulk) bulkFailed.TrySetResult();
                throw new IOException("direct copy unavailable");
            }
            return Task.CompletedTask;
        };
        peerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
        {
            if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.TransportAccelerationAnswer)
                Interlocked.Increment(ref sends);
            return Task.FromResult(false);
        };
        var gate = (SemaphoreSlim)GetPrivateField(peer, "outboundSendGate")!;
        await gate.WaitAsync(ct);
        var dialReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishDial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? dialing = null;
        Task handling;
        Task<bool> drain;
        try
        {
            handling = (Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync", hostClient.Address, invalid, "negative-copy", ct)!;
            await bulkFailed.Task.WaitAsync(ct);
            Assert.Equal(0, Volatile.Read(ref sends));
            if (whileDialing)
            {
                lane.AfterOfferDialReadyForTests = async () => { dialReady.SetResult(); await finishDial.Task.WaitAsync(ct); };
                dialing = (Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync", hostClient.Address, valid, "valid-copy", ct)!;
                await dialReady.Task.WaitAsync(ct);
                Assert.Null(GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
            }
            else
            {
                Assert.True((long)InvokePrivateMethod(peer, "BeginPendingAccelerationAnswerAck", valid, NknAccelerationLaneKind.File)! > 0);
                if (retireBeforeDrain) InvokePrivateMethod(peer, "ClearPendingAccelerationAnswerAckLocked", false);
            }
            var high = Enum.Parse(typeof(NknSignalingTransport).GetNestedType("ControlOutboundLane", System.Reflection.BindingFlags.NonPublic)!, "High");
            drain = (Task<bool>)InvokePrivateMethod(peer, "QueueControlEnvelopeAsync", hostClient.Address,
                new Envelope(1, "000000", "queue-drain", MsgType.Ack, [], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null), high, ct)!;
        }
        finally { gate.Release(); }
        await handling.WaitAsync(ct);
        Assert.True(await drain.WaitAsync(ct));
        Assert.True(directFailures > 0);
        try { Assert.Equal(unrelated ? 1 : 0, Volatile.Read(ref sends)); }
        finally { finishDial.TrySetResult(); }
        if (dialing is not null) await dialing.WaitAsync(ct);
        Assert.Equal(retireBeforeDrain ? null : valid.TunaAddress, GetPrivateField(peer, "pendingAnswerTunaEndpoint"));
    }

    [Theory]
    [InlineData("owned")]
    [InlineData("preexisting")]
    [InlineData("replaced")]
    [InlineData("successor-ack")]
    public async Task RejectedOffer_ReleasesOnlyThePauseItAcquired(string scenario)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        var hostClient = new FakeNknClient("host.owned-pause.address");
        var peerClient = new FakeNknClient("peer.owned-pause.address");
        var dial = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lane = new RetryableTunaAccelerationSession(false, 0, dialAttemptOutcomes: [dial]);
        var options = NknTransportOptions.Load();
        using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host", hostClient.Address));
        using var peer = new NknSignalingTransport(peerClient, options, new NknIdentity("peer", peerClient.Address),
            NknTunaAccelerationOptions.Disabled, lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        const string transfer = "transfer_rejected_offer_pause";
        var data = await peer.OpenFileTransferDataSessionAsync(session, transfer, ct);
        TrackAuthorizedRegularV4RouteForTests(peer, transfer);
        bool Pause() => (bool)InvokePrivateMethod(peer, "PauseFileTransferDataSessionsForTunaActivationNegotiation",
            "offer", session, "peer_offer")!;
        if (scenario == "preexisting") Assert.True(Pause());
        var offer = CreateOfferPayload(session, "ordinary-owned-pause-nonce", ["file"], payerDecisionId: 7);
        var pending = (Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync",
            hostClient.Address, offer, "ordinary-offer", ct)!;
        Assert.Equal(1, lane.StartDialerCalls);
        Assert.False(data.IsAvailable);
        if (scenario == "replaced")
        {
            Assert.True((bool)InvokePrivateMethod(peer, "TryResumeMatchingActivationPause",
                "old_offer_retired", session, "test", null)!);
            Assert.True(Pause());
        }
        if (scenario == "successor-ack")
            InvokePrivateMethod(peer, "BeginPendingAccelerationAnswerAck",
                CreateOfferPayload(session, "successor-ack-nonce", ["file"], payerDecisionId: 8), NknAccelerationLaneKind.File);
        var generation = GetPrivateField(peer, "fileTransferTunaActivationPauseGeneration");
        dial.SetResult(false);
        await pending.WaitAsync(ct);
        Assert.Equal(scenario == "owned", data.IsAvailable);
        Assert.Equal(scenario == "owned" ? null : session, GetPrivateField(peer, "fileTransferTunaActivationPauseSessionId"));
        if (scenario != "owned") Assert.Equal(generation, GetPrivateField(peer, "fileTransferTunaActivationPauseGeneration"));
    }

    [Fact]
    public async Task AnswerAckTimeout_ReleaseCannotOverwriteSuccessorPauseDuringBroadcast()
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = timeout.Token;
        var hostClient = new FakeNknClient("host.pause-write.address");
        var peerClient = new FakeNknClient("peer.pause-write.address");
        var options = NknTransportOptions.Load();
        using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host", hostClient.Address));
        using var peer = new NknSignalingTransport(peerClient, options, new NknIdentity("peer", peerClient.Address));
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var first = await peer.OpenFileTransferDataSessionAsync(session, "transfer_pause_a", ct);
        var second = await peer.OpenFileTransferDataSessionAsync(session, "transfer_pause_b", ct);
        bool Pause() => (bool)InvokePrivateMethod(peer, "PauseFileTransferDataSessionsForTunaActivationNegotiation",
            "offer", session, "peer_offer")!;
        Assert.True(Pause());
        Assert.False(first.IsAvailable);
        Assert.False(second.IsAvailable);
        var generations = ((long)GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration")!,
            (long)GetPrivateField(peer, "fileTransferTunaActivationPauseGeneration")!);
        var resumes = 0;
        first.AvailabilityChanged += OnAvailability;
        second.AvailabilityChanged += OnAvailability;
        void OnAvailability(object? sender, FileTransferDataSessionAvailabilityChangedEventArgs e)
        {
            if (e.IsAvailable && e.Reason == "answer_ack_timeout" && ++resumes == 1)
                Assert.True(Pause()); // Successor pauses both sessions before the old broadcast reaches its second write.
        }
        Assert.True((bool)InvokePrivateMethod(peer, "TryResumeMatchingActivationPause",
            "answer_ack_timeout", session, "answer_ack_timeout", generations)!);
        Assert.Equal(1, resumes);
        Assert.False(first.IsAvailable);
        Assert.False(second.IsAvailable);
        Assert.Equal(session, GetPrivateField(peer, "fileTransferTunaActivationPauseSessionId"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(true, true)]
    [InlineData(false, false, true)]
    public async Task FailedDialAnswerReplay_ReportsOnlyCompletedPhysicalSubmission(
        bool queueSucceeds, bool afterDeadline = false, bool directStillPending = false)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = timeout.Token;
        var options = NknTransportOptions.Load();
        var hostClient = new FakeNknClient("host.replay-result.address");
        var peerClient = new FakeNknClient("helper.replay-result.address");
        using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host", hostClient.Address));
        using var lane = new RetryableTunaAccelerationSession(false, 1);
        using var peer = new NknSignalingTransport(peerClient, options, new NknIdentity("peer", peerClient.Address),
            NknTunaAccelerationOptions.Disabled, lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var offer = CreateOfferPayload(session, "negative-replay-proof-nonce", ["file"]);
        offer.Trigger = "runtime_unlock";
        Task Handle(string messageId) => (Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync",
            hostClient.Address, offer, messageId, ct)!;
        var originalCopies = 0;
        var submitted = 0;
        peerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
        {
            if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.TransportAccelerationAnswer)
                Interlocked.Increment(ref originalCopies);
            return Task.FromResult(false);
        };
        await Handle("initial-failed-dial").WaitAsync(ct);
        await WaitUntilAsync(() => Volatile.Read(ref originalCopies) == 3, TimeSpan.FromSeconds(3));
        var gate = (SemaphoreSlim)GetPrivateField(peer, "outboundSendGate")!;
        await gate.WaitAsync(ct);
        var directFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDirect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peerClient.BeforeSendCoreAsync = (_, bytes, channel, _) =>
        {
            if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.TransportAccelerationAnswer)
            {
                if (!directFailed.Task.IsCompleted)
                {
                    if (directStillPending && channel != NknBridgeChannel.Bulk) return releaseDirect.Task;
                    if (channel == NknBridgeChannel.Bulk) directFailed.TrySetResult();
                    throw new IOException("unavailable direct copy");
                }
                if (!queueSucceeds) throw new IOException("failed queued copy");
                Interlocked.Increment(ref submitted);
            }
            return Task.CompletedTask;
        };
        var logStart = GetOperationalLogLength();
        Task replay;
        var previousWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousDirectWait = NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests;
        try
        {
            if (afterDeadline) NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(300);
            if (directStillPending) NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = TimeSpan.FromMilliseconds(100);
            replay = Handle("duplicate-failed-dial");
            await directFailed.Task.WaitAsync(ct);
            if (afterDeadline)
            {
                await replay.WaitAsync(ct);
                Assert.Equal(0, Volatile.Read(ref submitted));
                Assert.Contains("event=runtime_unlock_failed_dial_answer_replay_unobserved;", ReadOperationalLogTail(logStart));
            }
            else Assert.False(replay.IsCompleted);
            Assert.DoesNotContain("event=runtime_unlock_failed_dial_answer_replay_sent;", ReadOperationalLogTail(logStart));
        }
        finally
        {
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousWait;
            NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = previousDirectWait;
            gate.Release();
        }
        try { await replay.WaitAsync(ct); }
        finally { releaseDirect.TrySetResult(); }
        if (afterDeadline) await WaitUntilAsync(() => Volatile.Read(ref submitted) == 1, TimeSpan.FromSeconds(3));
        var log = ReadOperationalLogTail(logStart);
        var expected = afterDeadline || directStillPending ? "unobserved" : queueSucceeds ? "sent" : "rejected";
        foreach (var result in new[] { "unobserved", "sent", "rejected" })
            Assert.Equal(result == expected, log.Contains($"event=runtime_unlock_failed_dial_answer_replay_{result};"));
        Assert.Equal(queueSucceeds ? 1 : 0, submitted);
        Assert.Equal(1, lane.StartDialerCalls);
    }

    [Theory]
    [InlineData("owned")]
    [InlineData("borrowed")]
    [InlineData("borrowed-by-successor")]
    [InlineData("borrowed-pause")]
    [InlineData("successor-pause")]
    [InlineData("new-pause-generation")]
    [InlineData("successor-at-retry-limit")]
    [InlineData("replaced-before-ack")]
    [InlineData("reclaimed-before-ack")]
    [InlineData("missing-answer-proof")]
    public async Task FinalAck_BindsPhysicalDialAndTimeoutOwnsOnlyItsDialAndPause(string scenario)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = timeout.Token;
        var hostClient = new FakeNknClient("host.bound-dial.address");
        var peerClient = new FakeNknClient("peer.bound-dial.address");
        var options = NknTransportOptions.Load();
        using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host", hostClient.Address));
        const string endpoint = "test-final-ack-endpoint";
        using var lane = NknTunaLaneOwnershipTests.InstalledFakeDialForOffer(scenario != "borrowed", endpoint, hostClient.Address);
        using var peer = new NknSignalingTransport(peerClient, options, new NknIdentity("peer", peerClient.Address),
            NknTunaAccelerationOptions.Disabled, lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        peerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
            Task.FromResult(!EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type != MsgType.TransportAccelerationAnswer);
        var offer = CreateOfferPayload(session, "bound-final-ack-nonce", ["file"], payerDecisionId: 7);
        offer.TunaAddress = endpoint;
        offer.Trigger = "runtime_unlock";
        var data = await peer.OpenFileTransferDataSessionAsync(session, "transfer_bound_final_ack", ct);
        TrackAuthorizedRegularV4RouteForTests(peer, data.TransferId);
        if (scenario == "borrowed-pause")
            Assert.True((bool)InvokePrivateMethod(peer, "PauseFileTransferDataSessionsForTunaActivationNegotiation", "prior", session, "test")!);
        await ((Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync",
            hostClient.Address, offer, "bound-offer", ct)!).WaitAsync(ct);
        Assert.Equal(offer.Nonce, GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
        var generation = (long)GetPrivateField(peer, "pendingAccelerationAnswerAckGeneration")!;
        var shadow = GetPrivateField(peer, "pendingAccelerationAnswerReplyShadow");
        Assert.NotNull(shadow);
        var lease = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
        var successor = new object();
        if (scenario == "replaced-before-ack")
            Assert.True(lane.TryRetainCurrentLaneLease(lease, out _));
        if (scenario == "reclaimed-before-ack")
            Assert.True(await lane.StartOfferDialerAsync(endpoint, hostClient.Address, successor, ct));
        if (scenario == "missing-answer-proof") SetPrivateField(peer, "pendingAccelerationAnswerReplyShadow", null);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ack = new TransportAccelerationAnswerAckPayload { SessionId = session, Nonce = offer.Nonce,
            Accepted = true, SupportedLanes = ["file"], SidecarProtocolVersion = 1, PayerDecisionId = 7,
            SentAtUnixMs = now, ExpiresAtUnixMs = now + 10_000 };
        var frame = BuildSecureAccelerationEnvelope(host, MsgType.TransportAccelerationAnswerAck, ack,
            "transport_acceleration_answer_ack", offer.Nonce, sequence: 951);
        InvokePrivateMethod(peer, "HandleTransportAccelerationAnswerAck", hostClient.Address, frame);
        if (scenario is "replaced-before-ack" or "reclaimed-before-ack" or "missing-answer-proof")
        {
            Assert.Equal(NknAccelerationLaneKind.None, peer.AccelerationNegotiatedLanesForTests);
            Assert.Null(GetPrivateField(peer, "runtimeUnlockResponderAnswerProbeConvergence"));
            if (scenario != "missing-answer-proof")
            {
                Assert.NotNull(lane.CaptureCurrentLaneLease());
                Assert.Equal(offer.Nonce, GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
            }
            if (scenario == "reclaimed-before-ack") Assert.True(lane.IsCurrentOfferDial(successor));
            return;
        }
        Assert.Equal(NknAccelerationLaneKind.File, peer.AccelerationNegotiatedLanesForTests);
        Assert.NotNull(GetPrivateField(peer, "runtimeUnlockResponderAnswerProbeConvergence"));
        if (scenario == "borrowed-by-successor")
            Assert.True(await lane.StartOfferDialerAsync(endpoint, hostClient.Address, successor, ct));
        var nextOffer = CreateOfferPayload(session, "successor-pause-nonce", ["file"], payerDecisionId: 8);
        if (scenario == "new-pause-generation") SetPrivateField(peer, "fileTransferTunaActivationPauseGeneration",
            1L + (long)GetPrivateField(peer, "fileTransferTunaActivationPauseGeneration")!);
        var timeoutEntered = false;
        peer.BeforeAnswerAckTimeoutRecoveryForTests = () =>
        {
            timeoutEntered = true;
            if (scenario is "successor-pause" or "successor-at-retry-limit")
                InvokePrivateMethod(peer, "BeginPendingAccelerationAnswerAck", nextOffer, NknAccelerationLaneKind.File);
            if (scenario == "successor-at-retry-limit")
                SetPrivateField(peer, "accelerationNegotiationRetryAttempts", 8);
        };
        var previous = NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests;
        try
        {
            NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests = TimeSpan.FromMilliseconds(1);
            await ((Task)InvokePrivateMethod(peer, "ScheduleAccelerationAnswerAckTimeoutForReply",
                session, offer.Nonce, 7L, generation, shadow)!).WaitAsync(ct);
        }
        finally { NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests = previous; }
        Assert.True(timeoutEntered);
        if (scenario == "successor-at-retry-limit")
        {
            Assert.Equal(8, GetPrivateField(peer, "accelerationNegotiationRetryAttempts"));
            Assert.Equal(nextOffer.Nonce, GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
        }
        if (scenario == "borrowed-pause") Assert.Equal(0, GetPrivateField(peer, "accelerationNegotiationRetryAttempts"));
        Assert.Equal(scenario is "borrowed-pause" or "successor-pause" or "new-pause-generation" or "successor-at-retry-limit" ? session : null,
            GetPrivateField(peer, "fileTransferTunaActivationPauseSessionId"));
        Assert.Equal(scenario is "borrowed" or "borrowed-by-successor" ? lease : null, lane.CaptureCurrentLaneLease());
        if (scenario == "borrowed-by-successor") Assert.True(lane.IsCurrentOfferDial(successor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task LaneSelection_RevokedDialStopsOnlyItsOwnPhysicalClient(bool replacePhysicalDial, bool awaitingFinalAck = false,
        bool replacePause = false)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var selected = NknAccelerationLaneKind.File;
        var hostClient = new FakeNknClient("host.dial-owner.address");
        var peerClient = new FakeNknClient("helper.dial-owner.address");
        var options = NknTransportOptions.Load();
        using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host", hostClient.Address));
        using var lane = new RetryableTunaAccelerationSession(false, 0) { StrictPayerHandoffForTests = true };
        using var peer = new NknSignalingTransport(peerClient, options, new NknIdentity("helper", peerClient.Address),
            NknTunaAccelerationOptions.CreateRuntimePilot("fake-sidecar.exe", NknAccelerationLaneKind.File, false, () => selected), lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var offer = CreateOfferPayload(session, "revoked-dial-owner-nonce", ["file"], payerDecisionId: 7);
        var data = await peer.OpenFileTransferDataSessionAsync(session, "transfer_lane_revoke", ct);
        TrackAuthorizedRegularV4RouteForTests(peer, data.TransferId);
        peerClient.ShouldDeliverSendAsync = (_, bytes, _) => Task.FromResult(
            !EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type != MsgType.TransportAccelerationAnswer);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!awaitingFinalAck)
            lane.AfterOfferDialReadyForTests = async () => { ready.TrySetResult(); await release.Task.WaitAsync(ct); };
        var handling = (Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync",
            hostClient.Address, offer, "revoked-dial-offer", ct)!;
        NknTunaLaneLease? successor = null;
        try
        {
            if (awaitingFinalAck)
            {
                await handling.WaitAsync(ct);
                Assert.Equal(offer.Nonce, GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
            }
            else await ready.Task.WaitAsync(ct);
            Assert.False(data.IsAvailable);
            if (replacePause)
            {
                Assert.True((bool)InvokePrivateMethod(peer, "TryResumeMatchingActivationPause", "release", session, "test", null)!);
                Assert.True((bool)InvokePrivateMethod(peer, "PauseFileTransferDataSessionsForTunaActivationNegotiation",
                    "successor", session, "test")!);
            }
            var original = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
            if (replacePhysicalDial)
            {
                Assert.True(await lane.StartDialerSidecarAsync("replacement", hostClient.Address, ct));
                successor = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
                Assert.NotEqual(original, successor);
            }
            selected = NknAccelerationLaneKind.None;
            peer.RetireDeselectedAccelerationOffers();
            selected = NknAccelerationLaneKind.File;
            peer.RetireDeselectedAccelerationOffers();
        }
        finally { release.TrySetResult(); }
        await handling.WaitAsync(ct);
        Assert.Equal(replacePhysicalDial ? 0 : 1, lane.StopCalls);
        Assert.Equal(!replacePause, data.IsAvailable);
        Assert.Equal(replacePause ? session : null, GetPrivateField(peer, "fileTransferTunaActivationPauseSessionId"));
        Assert.Equal(successor, lane.CaptureCurrentLaneLease());
        Assert.Null(GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
        Assert.Equal(NknAccelerationLaneKind.None, peer.AccelerationNegotiatedLanesForTests);
    }

    [Theory]
    [InlineData(0, 7, false, true)]
    [InlineData(7, 0, false, true)]
    [InlineData(7, 7, true, true)]
    [InlineData(7, 7, true, false)]
    public async Task FinalAck_StaleFrameLeavesPendingDialAndPauseForTheExactFreshAck(
        long payer, long stalePayer, bool expired, bool accepted)
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var selected = NknAccelerationLaneKind.File;
        var hostClient = new FakeNknClient("host.inbound-ack.address");
        var peerClient = new FakeNknClient("helper.inbound-ack.address");
        var options = NknTransportOptions.Load();
        using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host", hostClient.Address));
        using var lane = new RetryableTunaAccelerationSession(false, 0) { StrictPayerHandoffForTests = true };
        using var peer = new NknSignalingTransport(peerClient, options, new NknIdentity("peer", peerClient.Address),
            NknTunaAccelerationOptions.CreateRuntimePilot("fake-sidecar.exe", selected, false, () => selected), lane);
        var session = await ApproveNknSessionAsync(host, peer, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        peerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
            Task.FromResult(!EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type != MsgType.TransportAccelerationAnswer);
        var offer = CreateOfferPayload(session, "final-ack-exact-payer-nonce", ["file"], payerDecisionId: payer);
        await ((Task)InvokePrivateMethod(peer, "HandleTransportAccelerationOfferAsync",
            hostClient.Address, offer, "final-ack-test-offer", ct)!).WaitAsync(ct);
        var installed = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
        SetPrivateField(peer, "fileTransferTunaActivationPauseSessionId", session);
        void Deliver(long id, bool stale, bool positive, long sequence)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var ack = new TransportAccelerationAnswerAckPayload { SessionId = session, Nonce = offer.Nonce,
                Accepted = positive, SupportedLanes = ["file"], SidecarProtocolVersion = 1, PayerDecisionId = id,
                SentAtUnixMs = now - 1000, ExpiresAtUnixMs = stale ? now - 1 : now + 10_000 };
            var envelope = BuildSecureAccelerationEnvelope(host, MsgType.TransportAccelerationAnswerAck, ack,
                "transport_acceleration_answer_ack", offer.Nonce, sequence: sequence);
            InvokePrivateMethod(peer, "HandleTransportAccelerationAnswerAck", hostClient.Address, envelope);
        }
        Deliver(stalePayer, expired, accepted, 951);
        Assert.True(lane.HasProvisionalDialForTests);
        Assert.Equal(session, GetPrivateField(peer, "fileTransferTunaActivationPauseSessionId"));
        Assert.Equal(offer.Nonce, GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
        Assert.Equal(NknAccelerationLaneKind.None, peer.AccelerationNegotiatedLanesForTests);
        Assert.Equal(installed, lane.CaptureCurrentLaneLease());
        Deliver(payer, false, true, 952);
        Assert.False(lane.HasProvisionalDialForTests);
        Assert.Equal(NknAccelerationLaneKind.File, peer.AccelerationNegotiatedLanesForTests);
        Assert.Null(GetPrivateField(peer, "pendingAccelerationAnswerAckNonce"));
        selected = NknAccelerationLaneKind.None;
        peer.RetireDeselectedAccelerationOffers();
        Assert.Equal(installed, lane.CaptureCurrentLaneLease());
        Assert.Equal(0, lane.StopCalls);
    }

    [Theory]
    [InlineData("payer-before-ack")]
    [InlineData("payer-before-ack-successor-pause")]
    [InlineData("responder-before-ack")]
    [InlineData("successor-before-ack")]
    [InlineData("completed-before-edit")]
    [InlineData("expired-before-ack")]
    [InlineData("expired-replacement-pause")]
    [InlineData("send-rejected")]
    [InlineData("listener-replaced-before-registration")]
    [InlineData("healthy-before-payer-yield")]
    [InlineData("expired-during-ack-send")]
    [InlineData("queue-only-ack")]
    [InlineData("expired-queued-ack")]
    [InlineData("unmatched-invalid-ack")]
    [InlineData("replace-before-accept")]
    [InlineData("replace-before-reject")]
    [InlineData("negative-owned-pause")]
    [InlineData("negative-successor-pause")]
    [InlineData("negative-yield-owned")]
    [InlineData("negative-yield-replaced-listener")]
    [InlineData("negative-yield-new-payer")]
    [InlineData("invalid-ack-borrowed-pause")]
    [InlineData("invalid-ack-replaced-during-dial")]
    [InlineData("lease-before-ack")]
    [InlineData("lease-during-ack")]
    [InlineData("budget-successor")]
    [InlineData("yield-owned")]
    [InlineData("yield-borrowed")]
    [InlineData("yield-replaced")]
    [InlineData("timeout-preserved")]
    [InlineData("timeout-successor")]
    [InlineData("timeout-expired-successor")]
    [InlineData("timeout-listener-replaced")]
    [InlineData("timeout-listener-current")]
    [InlineData("writer-offer-revoked")]
    [InlineData("writer-offer-current")]
    [InlineData("writer-offer-replaced")]
    [InlineData("writer-ack-current")]
    [InlineData("writer-ack-revoked")]
    public async Task LaneSelection_OrdinaryHandshakeKeepsExactOfferThroughFinalAck(string scenario)
    {
        FakeNknClient.ResetNetwork();
        using var profile = NknTransportOptions.OverrideLocalAppDataPathForTests(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        const NknAccelerationLaneKind both = NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen;
        var payerLanes = both;
        var responderLanes = both;
        var payerClient = scenario.StartsWith("writer-", StringComparison.Ordinal)
            ? new WriterGatePeer($"host.{scenario}.final-ack.address") : new FakeNknClient($"host.{scenario}.final-ack.address");
        var responderClient = new FakeNknClient($"helper.{scenario}.final-ack.address");
        var options = NknTransportOptions.Load();
        using var listener = new RetryableTunaAccelerationSession(true, 0) { StrictPayerHandoffForTests = true };
        using var dialer = new RetryableTunaAccelerationSession(false, 0) { StrictPayerHandoffForTests = true };
        using var payer = new NknSignalingTransport(payerClient, options, new NknIdentity("host", payerClient.Address),
            NknTunaAccelerationOptions.CreateRuntimePilot("fake-sidecar.exe", both, runtimeLaneLimit: () => payerLanes), listener);
        using var responder = new NknSignalingTransport(responderClient, options, new NknIdentity("helper", responderClient.Address),
            NknTunaAccelerationOptions.CreateRuntimePilot("fake-sidecar.exe", both, false, () => responderLanes), dialer);
        using var payerFiles = new SessionFileTransferService();
        using var responderFiles = new SessionFileTransferService();
        payerFiles.AttachTransport(payer);
        responderFiles.AttachTransport(responder);
        var session = await ApproveNknSessionAsync(payer, responder, ct,
            InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        var answer = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ack = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        responderClient.ShouldDeliverSendAsync = (_, bytes, _) =>
        {
            if (!EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type != MsgType.TransportAccelerationAnswer)
                return Task.FromResult(true);
            answer.TrySetResult(e);
            return Task.FromResult(false);
        };
        payerClient.BeforeSendAsync = (_, bytes, _) =>
        {
            if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.TransportAccelerationAnswerAck)
                ack.TrySetResult(e);
            return Task.CompletedTask;
        };
        var testNegative = scenario.StartsWith("negative-", StringComparison.Ordinal);
        var revokeFinalAck = scenario.StartsWith("payer-before-ack", StringComparison.Ordinal);
        var abandonAck = scenario.StartsWith("expired-", StringComparison.Ordinal) || scenario == "send-rejected";
        var yieldOffer = scenario.StartsWith("yield-", StringComparison.Ordinal);
        var peerTimeout = scenario.StartsWith("timeout-", StringComparison.Ordinal);
        var leaseAck = scenario.StartsWith("lease-", StringComparison.Ordinal);
        var data = testNegative || revokeFinalAck || abandonAck || yieldOffer || peerTimeout || leaseAck || scenario == "budget-successor"
            ? await payer.OpenFileTransferDataSessionAsync(session, "transfer_negative_answer", ct) : null;
        void ReplacePause()
        {
            Assert.True((bool)InvokePrivateMethod(payer, "TryResumeMatchingActivationPause", "retired", session, "test", null)!);
            Assert.True((bool)InvokePrivateMethod(payer, "PauseFileTransferDataSessionsForTunaActivationNegotiation", "successor", session, "test")!);
        }
        if (scenario == "yield-borrowed")
            Assert.True((bool)InvokePrivateMethod(payer, "PauseFileTransferDataSessionsForTunaActivationNegotiation", "prior", session, "test")!);
        var invalidAck = scenario.Contains("invalid-ack", StringComparison.Ordinal);
        if (invalidAck)
        {
            var receiver = await responder.OpenFileTransferDataSessionAsync(session, "transfer_invalid_ack", ct);
            TrackAuthorizedRegularV4RouteForTests(responder, receiver.TransferId);
            if (scenario == "invalid-ack-borrowed-pause")
                Assert.True((bool)InvokePrivateMethod(responder, "PauseFileTransferDataSessionsForTunaActivationNegotiation", "prior", session, "test")!);
            if (scenario == "invalid-ack-replaced-during-dial")
                dialer.AfterOfferDialReadyForTests = () =>
                {
                    Assert.False(receiver.IsAvailable);
                    Assert.True((bool)InvokePrivateMethod(responder, "TryResumeMatchingActivationPause", "release", session, "test", null)!);
                    Assert.True((bool)InvokePrivateMethod(responder, "PauseFileTransferDataSessionsForTunaActivationNegotiation", "replacement", session, "test")!);
                    return Task.CompletedTask;
                };
        }
        if (scenario == "listener-replaced-before-registration")
        {
            NknTunaLaneLease? successor = null;
            listener.AfterListenerOfferCapture = () =>
            {
                listener.AfterListenerOfferCapture = null;
                var originalLease = Assert.IsType<NknTunaLaneLease>(listener.CaptureCurrentLaneLease());
                var address = listener.LocalTunaAddress;
                Assert.True(listener.EnsureListenerSidecarConnectedAsync(responderClient.Address, ct).GetAwaiter().GetResult());
                Assert.Equal(address, listener.LocalTunaAddress);
                successor = Assert.IsType<NknTunaLaneLease>(listener.CaptureCurrentLaneLease());
                Assert.NotEqual(originalLease, successor);
            };
            await payer.RequestAccelerationNegotiationAsync("session_security_state_ready", ct);
            await WaitUntilAsync(() => successor is not null && Equals(0, GetPrivateField(payer, "accelerationNegotiationScheduled")), TimeSpan.FromSeconds(4));
            Assert.Null(GetPrivateField(payer, "outboundPreferenceOffer"));
            Assert.Equal(0, dialer.StartDialerCalls);
            Assert.Equal(successor, listener.CaptureCurrentLaneLease());
            return;
        }
        await payer.RequestAccelerationNegotiationAsync("session_security_state_ready", ct);
        var held = await answer.Task.WaitAsync(ct);
        var original = Assert.IsType<string>(GetPrivateField(payer, "outboundAccelerationOfferNonce"));
        Assert.Null(GetPrivateField(payer, "outboundAccelerationOfferIdleUnlock"));
        if (scenario.StartsWith("writer-offer-", StringComparison.Ordinal))
        {
            await WaitUntilAsync(() => Equals(0, GetPrivateField(payer, "accelerationNegotiationScheduled")), TimeSpan.FromSeconds(4));
            var selected = GetPrivateField(payer, "outboundPreferenceOffer")!;
            var envelope = Assert.IsType<Envelope>(selected.GetType().GetProperty("Envelope")!.GetValue(selected));
            var writerPeer = Assert.IsType<WriterGatePeer>(payerClient);
            using var bytes = new MemoryStream();
            using var writer = new BridgeStdioWriter(bytes, leaveOpen: true);
            writerPeer.Writer = writer;
            var gate = (SemaphoreSlim)GetPrivateField(writer, "gate")!;
            await gate.WaitAsync(ct);
            Task copy;
            try
            {
                copy = (Task)InvokePrivateMethod(payer, "SendBulkEnvelopeAsync", responderClient.Address, envelope,
                    EnvelopeCodec.Serialize(envelope), ct, false, false)!;
                await writerPeer.Arrived.Task.WaitAsync(ct);
                if (scenario == "writer-offer-revoked")
                {
                    payerLanes = NknAccelerationLaneKind.File;
                    payer.RetireDeselectedAccelerationOffers();
                    payerLanes = both;
                }
                if (scenario == "writer-offer-replaced")
                    Assert.True(await listener.EnsureListenerSidecarConnectedAsync(responderClient.Address, ct));
            }
            finally { gate.Release(); }
            await copy.WaitAsync(ct);
            var sent = scenario == "writer-offer-current";
            Assert.Equal(sent, await writerPeer.WriteCompleted.Task.WaitAsync(ct));
            Assert.Equal(sent, bytes.Length > 0);
            writerPeer.Writer = null;
            return;
        }
        Assert.Equal(original, GetPrivateField(responder, "pendingAccelerationAnswerAckNonce"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (yieldOffer)
            {
                Assert.False(data!.IsAvailable);
                if (scenario == "yield-replaced") ReplacePause();
                var stops = listener.StopCalls;
                InvokePrivateMethod(payer, "YieldLocalPaidListenerToRemoteHelpee", "remote_helpee_priority", 0L);
                Assert.Null(GetPrivateField(payer, "outboundAccelerationOfferNonce"));
                Assert.Equal(scenario == "yield-owned", data.IsAvailable);
                Assert.Equal(stops + 1, listener.StopCalls);
                return;
            }
            if (peerTimeout)
            {
                // Observing the peer's answer does not mean the ordinary offer's send continuation has finished.
                await WaitUntilAsync(() => Equals(0, GetPrivateField(payer, "accelerationNegotiationScheduled")), TimeSpan.FromSeconds(4));
                var payerId = (long)GetPrivateField(payer, "outboundAccelerationOfferPayerDecisionId")!;
                var gen = (long)GetPrivateField(payer, "outboundAccelerationOfferGeneration")!;
                InstallRuntimeUnlockRetryDispatchCoordinatorForTests(payer);
                TrackAuthorizedRegularV4RouteForTests(payer, data!.TransferId);
                payer.SeedRuntimeUnlockOfferCriticalSectionForTests(session, original, payerId, gen,
                    observedSend: true, observedLane: "control_to_bulk_endpoint", answerTimeoutScheduled: true);
                var listenerTest = scenario.StartsWith("timeout-listener-", StringComparison.Ordinal);
                var oldListener = listener.CaptureCurrentLaneLease();
                var stopCount = listener.StopCalls;
                if (listenerTest) Assert.NotNull(oldListener);
                if (listenerTest || scenario == "timeout-expired-successor")
                    GetPrivateField(payer, "runtimeUnlockOfferProofState")!.GetType().GetProperty("RetiredAnswerExpiresUtcMs")!
                        .SetValue(GetPrivateField(payer, "runtimeUnlockOfferProofState"), 1L);
                var observed = false;
                payer.TransportAccelerationStateChanged += (_, args) =>
                {
                    if (args.Reason != (listenerTest || scenario == "timeout-expired-successor" ? "activation_offer_not_observed" : "runtime_unlock_pending_answer_preserved")) return;
                    observed = true;
                    if (scenario == "timeout-listener-replaced")
                        Assert.True(listener.EnsureListenerSidecarConnectedAsync(responderClient.Address, ct).GetAwaiter().GetResult());
                    else if (!listenerTest && scenario != "timeout-preserved") ReplacePause();
                };
                var prev = NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests;
                try
                {
                    NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = TimeSpan.FromMilliseconds(1);
                    await ((Task)InvokePrivateMethod(payer, "ScheduleRuntimeUnlockOfferPeerResponseTimeout", original, payerId, gen,
                        session, "control_to_bulk_endpoint")!).WaitAsync(ct);
                }
                finally { NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = prev; }
                Assert.True(observed);
                Assert.Equal(listenerTest || scenario == "timeout-preserved", data.IsAvailable);
                if (listenerTest)
                {
                    Assert.Equal(scenario == "timeout-listener-current", listener.CaptureCurrentLaneLease() == oldListener);
                    Assert.Equal(stopCount, listener.StopCalls);
                }
                Assert.Equal(scenario == "timeout-listener-current" ? 1 : 0, GetPrivateField(payer, "accelerationNegotiationRetryAttempts"));
                return;
            }
            if (testNegative)
            {
                Assert.False(data!.IsAvailable);
                var yield = scenario.StartsWith("negative-yield-", StringComparison.Ordinal);
                var reason = yield ? "helpee_payer_preferred" : "rejected";
                var payerEpoch = (long)GetPrivateField(payer, "accelerationPayerDecisionId")!;
                var originalLease = listener.CaptureCurrentLaneLease();
                var stops = listener.StopCalls;
                var observedRejection = false;
                payer.TransportAccelerationStateChanged += (_, e) =>
                {
                    if (e.Reason != "answer_rejected_" + reason) return;
                    observedRejection = true;
                    Assert.Null(GetPrivateField(payer, "outboundAccelerationOfferNonce"));
                    if (scenario == "negative-yield-replaced-listener")
                        Assert.True(listener.EnsureListenerSidecarConnectedAsync(responderClient.Address, ct).GetAwaiter().GetResult());
                    if (scenario == "negative-yield-new-payer")
                        InvokePrivateMethod(payer, "AdvancePayerDecisionEpoch", "successor");
                    if (scenario != "negative-successor-pause") return;
                    Assert.True((bool)InvokePrivateMethod(payer, "TryResumeMatchingActivationPause", "old_retired", session, "test", null)!);
                    Assert.True((bool)InvokePrivateMethod(payer, "PauseFileTransferDataSessionsForTunaActivationNegotiation",
                        "successor", session, "test")!);
                };
                var key = (byte[])InvokePrivateMethod(responder, "GetControlSessionSharedKeyOrThrow")!;
                var negative = JsonSerializer.Deserialize<TransportAccelerationAnswerPayload>(
                    SessionSecureEnvelopeCodec.Decrypt(key, held.Payload).Plaintext)!;
                negative.Accepted = false;
                negative.RejectReason = reason;
                var envelope = BuildSecureAccelerationEnvelope(responder, MsgType.TransportAccelerationAnswer,
                    negative, "transport_acceleration_answer", negative.Nonce, sequence: 954);
                InvokePrivateMethod(payer, "HandleTransportAccelerationAnswer", responderClient.Address, envelope);
                Assert.True(observedRejection);
                Assert.Equal(scenario != "negative-successor-pause", data.IsAvailable);
                Assert.Equal(scenario != "negative-successor-pause" ? null : session, GetPrivateField(payer, "fileTransferTunaActivationPauseSessionId"));
                if (yield)
                {
                    Assert.NotNull(originalLease);
                    Assert.Equal(stops + (scenario == "negative-yield-owned" ? 1 : 0), listener.StopCalls);
                    Assert.Equal(payerEpoch + (scenario == "negative-yield-replaced-listener" ? 0 : 1),
                        GetPrivateField(payer, "accelerationPayerDecisionId"));
                    if (scenario == "negative-yield-owned") Assert.Null(listener.CaptureCurrentLaneLease());
                    else
                    {
                        Assert.True(listener.IsLocalPaidListenerActive);
                        Assert.Equal(scenario == "negative-yield-new-payer", originalLease == listener.CaptureCurrentLaneLease());
                    }
                }
                return;
            }
            if (invalidAck)
            {
                Assert.Equal(session, GetPrivateField(responder, "fileTransferTunaActivationPauseSessionId"));
                var pendingPayer = (long)GetPrivateField(responder, "pendingAccelerationAnswerAckPayerDecisionId")!;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                void Deliver(string nonce, long payerId, long sequence)
                {
                    var payload = new TransportAccelerationAnswerAckPayload { SessionId = session, Accepted = true,
                        SupportedLanes = [], SentAtUnixMs = now, ExpiresAtUnixMs = now + 10_000, Nonce = nonce,
                        PayerDecisionId = payerId, SidecarProtocolVersion = 1 };
                    var envelope = BuildSecureAccelerationEnvelope(payer, MsgType.TransportAccelerationAnswerAck,
                        payload, "transport_acceleration_answer_ack", nonce, sequence: sequence);
                    InvokePrivateMethod(responder, "HandleTransportAccelerationAnswerAck", payerClient.Address, envelope);
                }
                Deliver("stale-final-ack-nonce", pendingPayer, 951);
                Assert.Equal(session, GetPrivateField(responder, "fileTransferTunaActivationPauseSessionId"));
                Deliver(original, pendingPayer + 1, 952);
                Assert.Equal(session, GetPrivateField(responder, "fileTransferTunaActivationPauseSessionId"));
                Deliver(original, pendingPayer, 953);
                Assert.Equal(scenario == "unmatched-invalid-ack" ? null : session,
                    GetPrivateField(responder, "fileTransferTunaActivationPauseSessionId"));
                return;
            }
            if (scenario.StartsWith("replace-", StringComparison.Ordinal))
            {
                if (scenario == "replace-before-reject")
                {
                    var key = (byte[])InvokePrivateMethod(responder, "GetControlSessionSharedKeyOrThrow")!;
                    var payload = JsonSerializer.Deserialize<TransportAccelerationAnswerPayload>(
                        SessionSecureEnvelopeCodec.Decrypt(key, held.Payload).Plaintext)!;
                    payload.Accepted = false;
                    payload.RejectReason = "rejected";
                    held = held with { Payload = (byte[])InvokePrivateMethod(responder, "CreateSecureControlPayload",
                        held.Type, payload.Nonce, JsonSerializer.SerializeToUtf8Bytes(payload))! };
                }
                payer.IdleSessionBeforeAnswerCommitForTests = () => { entered.TrySetResult(); release.Task.Wait(ct); };
                var handling = Task.Run(() => InvokePrivateMethod(payer, "HandleTransportAccelerationAnswer", responderClient.Address, held));
                await entered.Task.WaitAsync(ct);
                lock (GetPrivateField(payer, "accelerationGate")!)
                    InvokePrivateMethod(payer, "RetireOutboundAccelerationOfferLocked", session, "offer_answer_timeout", 0L);
                await payer.RequestAccelerationNegotiationAsync("session_security_state_ready", ct);
                await WaitUntilAsync(() => GetPrivateField(payer, "outboundAccelerationOfferNonce") is string n && n != original, TimeSpan.FromSeconds(4));
                var successor = GetPrivateField(payer, "outboundPreferenceOffer");
                release.TrySetResult();
                await handling.WaitAsync(ct);
                Assert.NotNull(successor);
                Assert.Same(successor, GetPrivateField(payer, "outboundPreferenceOffer"));
                Assert.Equal(NknAccelerationLaneKind.None, payer.AccelerationNegotiatedLanesForTests);
                Assert.False(ack.Task.IsCompleted);
                return;
            }
            payer.IdleSessionBeforeAnswerAckForTests = async () => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
            InvokePrivateMethod(payer, "HandleTransportAccelerationAnswer", responderClient.Address, held);
            await entered.Task.WaitAsync(ct);
            Assert.Equal(NknAccelerationLaneKind.None, payer.AccelerationNegotiatedLanesForTests);
            Assert.False(ack.Task.IsCompleted);
            Assert.Null(GetPrivateField(payer, "initialTunaReadinessState"));
            SetPrivateField(payer, "accelerationNegotiationRetryAttempts", 3);
            if (payerClient is WriterGatePeer writerPeer)
            {
                using var bytes = new MemoryStream();
                using var writer = new BridgeStdioWriter(bytes, leaveOpen: true);
                var gate = (SemaphoreSlim)GetPrivateField(writer, "gate")!;
                await gate.WaitAsync(ct);
                try
                {
                    writerPeer.Writer = writer;
                    release.TrySetResult();
                    await writerPeer.Arrived.Task.WaitAsync(ct);
                    if (scenario == "writer-ack-revoked")
                    {
                        payerLanes = NknAccelerationLaneKind.File;
                        payer.RetireDeselectedAccelerationOffers();
                    }
                }
                finally { gate.Release(); }
                var sent = await Assert.IsAssignableFrom<Task<bool>>(payer.IdleSessionAnswerAckForTests).WaitAsync(ct);
                Assert.Equal(scenario == "writer-ack-current", sent);
                Assert.Equal(sent, bytes.Length > 0);
                Assert.Equal(sent ? both : NknAccelerationLaneKind.None, payer.AccelerationNegotiatedLanesForTests);
                writerPeer.Writer = null;
                return;
            }
            if (leaseAck || scenario == "budget-successor")
            {
                var originalLease = Assert.IsType<NknTunaLaneLease>(listener.CaptureCurrentLaneLease());
                void ReplaceListener() => Assert.True(listener.EnsureListenerSidecarConnectedAsync(responderClient.Address, ct).GetAwaiter().GetResult());
                if (scenario == "lease-before-ack") ReplaceListener();
                else if (scenario == "budget-successor") ReplacePause();
                else payerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
                {
                    if (!EnvelopeCodec.TryDeserialize(bytes, out var env) || env.Type != MsgType.TransportAccelerationAnswerAck)
                        return Task.FromResult(true);
                    if (listener.CaptureCurrentLaneLease() == originalLease) ReplaceListener();
                    return Task.FromResult(false);
                };
                release.TrySetResult();
                var published = await Assert.IsAssignableFrom<Task<bool>>(payer.IdleSessionAnswerAckForTests).WaitAsync(ct);
                Assert.Equal(!leaseAck, published);
                Assert.Equal(published ? both : NknAccelerationLaneKind.None, payer.AccelerationNegotiatedLanesForTests);
                if (!published) Assert.Null(GetPrivateField(payer, "initialTunaReadinessState"));
                Assert.Equal(scenario != "lease-before-ack", ack.Task.IsCompleted);
                Assert.Equal(!leaseAck, originalLease == listener.CaptureCurrentLaneLease());
                Assert.True(listener.IsLocalPaidListenerActive);
                Assert.Equal(3, GetPrivateField(payer, "accelerationNegotiationRetryAttempts"));
                return;
            }
            if (abandonAck) Assert.False(data!.IsAvailable);
            if (scenario == "expired-replacement-pause")
            {
                Assert.True((bool)InvokePrivateMethod(payer, "TryResumeMatchingActivationPause", "retire", session, "test", null)!);
                Assert.True((bool)InvokePrivateMethod(payer, "PauseFileTransferDataSessionsForTunaActivationNegotiation", "successor", session, "test")!);
                payer.FinalAckUtcMs = () => long.MaxValue;
            }
            if (scenario == "send-rejected")
                payerClient.BeforeSendCoreAsync = (_, bytes, _, _) =>
                    EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.TransportAccelerationAnswerAck
                        ? Task.FromException(new IOException("unavailable")) : Task.CompletedTask;
            if (scenario is "queue-only-ack" or "expired-queued-ack")
            {
                var gate = (SemaphoreSlim)GetPrivateField(payer, "outboundSendGate")!;
                await gate.WaitAsync(ct);
                var bulkFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var directFailures = 0;
                var delivered = 0;
                payerClient.BeforeSendCoreAsync = (_, bytes, channel, _) =>
                {
                    if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.TransportAccelerationAnswerAck &&
                        !bulkFailed.Task.IsCompleted)
                    {
                        Interlocked.Increment(ref directFailures);
                        if (channel == NknBridgeChannel.Bulk) bulkFailed.TrySetResult();
                        throw new IOException("fixture: direct ACK copy unavailable");
                    }
                    return Task.CompletedTask;
                };
                payerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
                {
                    if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.TransportAccelerationAnswerAck)
                        Interlocked.Increment(ref delivered);
                    return Task.FromResult(true);
                };
                try
                {
                    release.TrySetResult();
                    await bulkFailed.Task.WaitAsync(ct);
                    Assert.Equal(0, Volatile.Read(ref delivered));
                    Assert.Equal(NknAccelerationLaneKind.None, payer.AccelerationNegotiatedLanesForTests);
                    if (scenario == "expired-queued-ack") payer.FinalAckUtcMs = () => long.MaxValue;
                }
                finally { gate.Release(); }
                var submitted = await Assert.IsAssignableFrom<Task<bool>>(payer.IdleSessionAnswerAckForTests).WaitAsync(ct);
                Assert.Equal(3, Volatile.Read(ref directFailures));
                Assert.Equal(scenario == "queue-only-ack", submitted);
                Assert.Equal(submitted ? 1 : 0, Volatile.Read(ref delivered));
                Assert.Equal(submitted ? both : NknAccelerationLaneKind.None, payer.AccelerationNegotiatedLanesForTests);
                if (!submitted) Assert.True(data!.IsAvailable);
                Assert.Equal(submitted ? 0 : 3, GetPrivateField(payer, "accelerationNegotiationRetryAttempts"));
                Assert.Equal(payer.AccelerationNegotiatedLanesForTests, responder.AccelerationNegotiatedLanesForTests);
                return;
            }
            if (scenario == "expired-before-ack") payer.FinalAckUtcMs = () => long.MaxValue;
            if (scenario == "expired-during-ack-send")
            {
                payerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
                {
                    if (!EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type != MsgType.TransportAccelerationAnswerAck)
                        return Task.FromResult(true);
                    payer.FinalAckUtcMs = () => long.MaxValue;
                    return Task.FromResult(false); // Local send completes; no peer delivery.
                };
            }
            if (revokeFinalAck)
            {
                Assert.False(data!.IsAvailable);
                if (scenario == "payer-before-ack-successor-pause")
                {
                    Assert.True((bool)InvokePrivateMethod(payer, "TryResumeMatchingActivationPause", "release", session, "test", null)!);
                    Assert.True((bool)InvokePrivateMethod(payer, "PauseFileTransferDataSessionsForTunaActivationNegotiation", "successor", session, "test")!);
                }
                payerLanes = NknAccelerationLaneKind.Screen;
                payer.RetireDeselectedAccelerationOffers();
                payerLanes = both;
                payer.RetireDeselectedAccelerationOffers();
                Assert.Equal(scenario == "payer-before-ack", data.IsAvailable);
            }
            else if (scenario == "responder-before-ack")
            {
                responderLanes = NknAccelerationLaneKind.Screen;
                responder.RetireDeselectedAccelerationOffers();
                responderLanes = both;
                responder.RetireDeselectedAccelerationOffers();
                Assert.Null(GetPrivateField(responder, "pendingAccelerationAnswerAckNonce"));
            }
            else if (scenario == "successor-before-ack")
            {
                // The held reply can arrive before the original send task releases its negotiation slot.
                await WaitUntilAsync(() => Equals(0, GetPrivateField(payer, "accelerationNegotiationScheduled")),
                    TimeSpan.FromSeconds(4));
                await payer.RequestAccelerationNegotiationAsync("session_security_state_ready", ct);
                await WaitUntilAsync(() => GetPrivateField(payer, "outboundAccelerationOfferNonce") is string n && n != original,
                    TimeSpan.FromSeconds(4));
            }
            if (scenario == "healthy-before-payer-yield")
            {
                SetPrivateField(responder, "accelerationNegotiationRetryAttempts", 4);
                dialer.AfterOfferDialCommit = () =>
                {
                    Assert.Equal(0, GetPrivateField(responder, "accelerationNegotiationRetryAttempts"));
                    SetPrivateField(responder, "accelerationNegotiationRetryAttempts", 7);
                };
            }
            release.TrySetResult();
            await Assert.IsAssignableFrom<Task>(payer.IdleSessionAnswerAckForTests).WaitAsync(ct);
            if (abandonAck)
            {
                Assert.False(await Assert.IsAssignableFrom<Task<bool>>(payer.IdleSessionAnswerAckForTests));
                Assert.Equal(scenario != "expired-replacement-pause", data!.IsAvailable);
                Assert.Null(GetPrivateField(payer, "initialTunaReadinessState"));
                Assert.Null(GetPrivateField(payer, "finalAckPreferenceOffer"));
                Assert.Equal(3, GetPrivateField(payer, "accelerationNegotiationRetryAttempts"));
                Assert.Equal(NknAccelerationLaneKind.None, payer.AccelerationNegotiatedLanesForTests);
                if (scenario is "expired-replacement-pause" or "send-rejected") return;
            }
            if (scenario == "expired-during-ack-send")
            {
                Assert.True(ack.Task.IsCompleted);
                Assert.False(await Assert.IsAssignableFrom<Task<bool>>(payer.IdleSessionAnswerAckForTests));
                Assert.Equal(NknAccelerationLaneKind.None, payer.AccelerationNegotiatedLanesForTests);
            }
            else if (revokeFinalAck || scenario is "successor-before-ack" or "expired-before-ack")
            {
                Assert.False(ack.Task.IsCompleted);
                Assert.Equal(NknAccelerationLaneKind.None, payer.AccelerationNegotiatedLanesForTests);
                Assert.Equal(NknAccelerationLaneKind.None, responder.AccelerationNegotiatedLanesForTests);
            }
            else if (scenario == "responder-before-ack")
            {
                var lateAck = await ack.Task.WaitAsync(ct);
                InvokePrivateMethod(responder, "HandleTransportAccelerationAnswerAck", payerClient.Address, lateAck);
                Assert.Equal(NknAccelerationLaneKind.None, responder.AccelerationNegotiatedLanesForTests);
            }
            else
            {
                await WaitUntilAsync(() => responder.AccelerationNegotiatedLanesForTests == both, TimeSpan.FromSeconds(4));
                if (scenario == "healthy-before-payer-yield")
                {
                    Assert.Equal(7, GetPrivateField(responder, "accelerationNegotiationRetryAttempts"));
                    Assert.Equal(0, GetPrivateField(payer, "accelerationNegotiationRetryAttempts"));
                    var lease = Assert.IsType<NknTunaLaneLease>(listener.CaptureCurrentLaneLease());
                    var epoch = (long)GetPrivateField(payer, "accelerationPayerDecisionId")!;
                    var scope = InvokePrivateMethod(payer, "CaptureRetryPauseScopeLocked", session, false);
                    SetPrivateField(payer, "outboundAccelerationOfferNonce", "successor-offer");
                    var remote = (long)GetPrivateField(payer, "remoteAccelerationPayerDecisionId")!;
                    InvokePrivateMethod(payer, "YieldLocalPaidListenerToRemoteHelpee", "payer_intent_will_listen", remote);
                    Assert.Equal("successor-offer", GetPrivateField(payer, "outboundAccelerationOfferNonce"));
                    Assert.False((bool)InvokePrivateMethod(payer, "StopOfferListenerIfCurrent", lease, epoch, scope, "payer_yield_to_helpee", true, remote)!);
                    Assert.Equal(lease, listener.CaptureCurrentLaneLease());
                    Assert.Equal(epoch, GetPrivateField(payer, "accelerationPayerDecisionId"));
                }
                payerLanes = responderLanes = NknAccelerationLaneKind.Screen;
                payer.RetireDeselectedAccelerationOffers();
                responder.RetireDeselectedAccelerationOffers();
                Assert.Equal(both, payer.AccelerationNegotiatedLanesForTests);
                Assert.Equal(both, responder.AccelerationNegotiatedLanesForTests);
            }
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false, true)]
    public async Task IdleSession_LaneEditsApplyToOriginalTransportAndPendingOffer(
        bool initiallyEnabled, bool withdrawLane, bool inLateAnswerGrace, bool removeScreen, bool onlyUnoffered = false)
    {
        var root = Directory.CreateTempSubdirectory("nlink-fake-lanes-");
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var (runtime, wallet) = await TunaWalletDiagnosticsTests.CreateFundedFakePeerRuntimeAsync(root.FullName);
            void Select(bool enabled, bool file, bool screen = true) => runtime.SavePreferences(new TunaRuntimePreferenceState
                { Enabled = enabled, FileLaneEnabled = file, ScreenLaneEnabled = screen });
            // Start with the opposite file selection: test widening as well as narrowing.
            Select(initiallyEnabled, !withdrawLane);
            var hostClient = new FakeNknClient("host.lane-edit.address");
            var peerClient = new FakeNknClient("helper.lane-edit.address");
            var options = NknTransportOptions.Load();
            using var hostLane = new RetryableTunaAccelerationSession(true, 0) { StrictPayerHandoffForTests = true };
            using var peerLane = new RetryableTunaAccelerationSession(false, 0) { StrictPayerHandoffForTests = true };
            using var host = Assert.IsType<NknSignalingTransport>(runtime.CreateNknTransport((tuna, supervisor) =>
            {
                hostLane.ListenerEligibilityForTests = () => supervisor!.CanOfferListener;
                return new NknSignalingTransport(hostClient, options, new NknIdentity("host", hostClient.Address), tuna, hostLane);
            }));
            using var peer = new NknSignalingTransport(peerClient, options, new NknIdentity("helper", peerClient.Address),
                NknTunaAccelerationOptions.Disabled, peerLane);
            using var hostFiles = new SessionFileTransferService();
            using var peerFiles = new SessionFileTransferService();
            hostFiles.AttachTransport(host);
            peerFiles.AttachTransport(peer);
            var session = await ApproveNknSessionAsync(host, peer, timeout.Token,
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.FileTransfer,
                suppressAutomaticAccelerationNegotiation: true);
            var offers = new ConcurrentQueue<TransportAccelerationOfferPayload>();
            var answer = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acks = 0;
            var key = (byte[])InvokePrivateMethod(peer, "GetControlSessionSharedKeyOrThrow")!;
            hostClient.BeforeSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var e))
                {
                    if (e.Type == MsgType.TransportAccelerationOffer)
                        offers.Enqueue(JsonSerializer.Deserialize<TransportAccelerationOfferPayload>(
                            SessionSecureEnvelopeCodec.Decrypt(key, e.Payload).Plaintext)!);
                    if (e.Type == MsgType.TransportAccelerationAnswerAck) Interlocked.Increment(ref acks);
                }
                return Task.CompletedTask;
            };
            peerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
            {
                if (!EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type != MsgType.TransportAccelerationAnswer)
                    return Task.FromResult(true);
                answer.TrySetResult(e);
                return Task.FromResult(false);
            };
            Select(true, withdrawLane);
            Assert.False(host.AccelerationCanOfferListenerForTests);
            Assert.Empty(offers);
            var sendGate = (SemaphoreSlim)GetPrivateField(host, "outboundSendGate")!;
            await sendGate.WaitAsync(timeout.Token);
            Envelope held;
            Envelope originalOffer;
            int beforeRelease;
            try
            {
                Assert.True((await runtime.UnlockForSessionAsync("fixture".ToCharArray(), TunaRuntimeUnlockSource.Header, wallet, timeout.Token)).Success);
                held = await answer.Task.WaitAsync(timeout.Token);
                var expected = NknAccelerationLaneKind.Screen | (withdrawLane ? NknAccelerationLaneKind.File : NknAccelerationLaneKind.None);
                Assert.NotEmpty(offers);
                Assert.All(offers, offer => Assert.Equal(expected, NknAccelerationLaneCodec.FromNames(offer.SupportedLanes)));
                var nonce = offers.First().Nonce;
                var binding = GetPrivateField(host, "outboundPreferenceOffer")!;
                originalOffer = (Envelope)binding.GetType().GetProperty("Envelope")!.GetValue(binding)!;
                Assert.Equal(nonce, GetPrivateField(host, "outboundAccelerationOfferNonce"));
                if (inLateAnswerGrace)
                    lock (GetPrivateField(host, "accelerationGate")!)
                        InvokePrivateMethod(host, "RetireOutboundAccelerationOfferLocked", session, "offer_answer_timeout", 0L);
                Select(true, !withdrawLane || removeScreen, !removeScreen);
                if (withdrawLane)
                {
                    Assert.Null(GetPrivateField(host, "outboundAccelerationOfferNonce"));
                    Assert.Null(GetPrivateField(host, "retiredAccelerationOfferNonce"));
                    Select(true, true); // Re-selecting cannot revive the old offer or its queued copy.
                }
                else Assert.Equal(nonce, GetPrivateField(host, "outboundAccelerationOfferNonce"));
                beforeRelease = offers.Count;
            }
            finally { sendGate.Release(); }
            await WaitUntilAsync(() =>
            {
                lock (GetPrivateField(host, "controlOutboundQueueGate")!)
                    return !(bool)GetPrivateField(host, "controlOutboundDrainerActive")!;
            }, TimeSpan.FromSeconds(5));
            if (withdrawLane) Assert.Equal(beforeRelease, offers.Count);
            else Assert.True(offers.Count > beforeRelease);
            // Exercise the lifecycle replay copies themselves, not just their enqueue result.
            var copyOptions = InvokePrivateMethod(host, "CreateRemoteControlLifecycleDeliveryOptions",
                originalOffer.Type, originalOffer.MessageId, peerClient.Address);
            foreach (var bulk in new[] { false, true })
            {
                var copy = (Task)InvokePrivateMethod(host, "SendLifecycleCopyAsync", peerClient.Address,
                    originalOffer, bulk, bulk ? "bulk_copy" : "control_copy", copyOptions, timeout.Token)!;
                await copy;
                var result = copy.GetType().GetProperty("Result")!.GetValue(copy)!;
                Assert.Equal(!withdrawLane, (bool)result.GetType().GetProperty("Succeeded")!.GetValue(result)!);
            }
            if (withdrawLane)
            {
                var retry = (Task)InvokePrivateMethod(host, "SendLifecycleControlAckCopyAsync",
                    copyOptions, originalOffer, timeout.Token, null)!;
                await retry;
                var result = retry.GetType().GetProperty("Result")!.GetValue(retry)!;
                Assert.False((bool)result.GetType().GetProperty("Succeeded")!.GetValue(result)!);
                Assert.Null(result.GetType().GetProperty("Error")!.GetValue(result));
                Assert.Equal(beforeRelease, offers.Count);
            }
            else
            {
                // A genuine peer can name a newly enabled lane, but this nonce never offered it.
                var decoded = JsonSerializer.Deserialize<TransportAccelerationAnswerPayload>(
                    SessionSecureEnvelopeCodec.Decrypt(key, held.Payload).Plaintext)!;
                decoded.SupportedLanes = NknAccelerationLaneCodec.ToNames(onlyUnoffered
                    ? NknAccelerationLaneKind.File : NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen);
                held = held with { Payload = (byte[])InvokePrivateMethod(peer, "CreateSecureControlPayload",
                    held.Type, decoded.Nonce, JsonSerializer.SerializeToUtf8Bytes(decoded))! };
            }
            InvokePrivateMethod(host, "HandleTransportAccelerationAnswer", peerClient.Address, held);
            if (withdrawLane || onlyUnoffered)
            {
                Assert.False(host.IsAccelerationAvailableForTests);
                Assert.False(peer.IsAccelerationAvailableForTests);
                Assert.Equal(0, Volatile.Read(ref acks));
            }
            else
            {
                await WaitUntilAsync(() => host.IsAccelerationAvailableForTests && peer.IsAccelerationAvailableForTests, TimeSpan.FromSeconds(5));
                Assert.Equal(NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
                Assert.Equal(NknAccelerationLaneKind.Screen, peer.AccelerationNegotiatedLanesForTests);
            }
            Assert.Equal(session, host.CurrentSessionSecurityState.SessionId!.Value.Value);
            Assert.Equal(1, hostLane.EnsureListenerCalls);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    [Trait("FileTransferInfraSuite", "IdleSessionReestablishment")]
    public Task IdleSession_ReestablishesAfterTerminalTransfer(
        bool completed, bool helperPays, bool switchPayer, bool stopBeforeAnswer)
        => RunIdleSessionReestablishmentAsync(completed, helperPays, switchPayer, stopBeforeAnswer);

    [Theory]
    [InlineData(false, false, "delayed_stop")]
    [InlineData(false, true, "delayed_stop")]
    [InlineData(true, false, "delayed_stop")]
    [InlineData(true, true, "delayed_stop")]
    [InlineData(false, false, "first_send_failure")]
    [InlineData(false, true, "first_send_failure")]
    [InlineData(true, false, "first_send_failure")]
    [InlineData(true, true, "first_send_failure")]
    [InlineData(false, false, "delayed_stop_new_stop")]
    [InlineData(true, true, "delayed_stop_key_reset")]
    [InlineData(false, true, "delayed_stop_new_transfer")]
    [InlineData(true, false, "first_send_failure_new_stop")]
    [InlineData(false, false, "first_send_failure_peer_stop")]
    [InlineData(false, false, "delayed_stop_late_marker")]
    [InlineData(true, true, "delayed_stop_late_marker")]
    [Trait("FileTransferInfraSuite", "IdleUnlockRaces")]
    public Task IdleSession_UnlockSurvivesOnlyItsOwnStopAndSendRetry(bool completed, bool helperPays, string change)
        => RunIdleSessionReestablishmentAsync(completed, helperPays, switchPayer: true, stopBeforeAnswer: false, change);

    [Theory]
    [InlineData(false, false, "before_publication_stop")]
    [InlineData(true, true, "before_publication_stop")]
    [InlineData(false, false, "offer_failure")]
    [InlineData(true, true, "offer_failure")]
    [InlineData(false, false, "offer_failure_cancel")]
    [InlineData(true, true, "offer_failure_cancel")]
    [InlineData(false, true, "offer_failure_new_transfer")]
    [InlineData(false, false, "answer_timeout")]
    [InlineData(true, true, "answer_timeout")]
    [InlineData(false, false, "answer_timeout_cancel")]
    [InlineData(true, true, "answer_timeout_cancel")]
    [InlineData(true, false, "answer_timeout_key_reset")]
    [Trait("FileTransferInfraSuite", "IdleUnlockRemainingRaces")]
    public Task IdleSession_UnlockPublicationAndOfferRetriesStayBound(bool completed, bool helperPays, string change)
        => RunIdleSessionReestablishmentAsync(completed, helperPays, switchPayer: true, stopBeforeAnswer: false, change);

    [Theory]
    [InlineData(false, false, "answer_commit_stop")]
    [InlineData(true, true, "answer_commit_stop")]
    [InlineData(false, false, "answer_commit_cancel")]
    [InlineData(true, true, "answer_commit_cancel")]
    [InlineData(false, false, "answer_ack_stop")]
    [InlineData(true, true, "answer_ack_stop")]
    [InlineData(false, false, "answer_ack_cancel")]
    [InlineData(true, true, "answer_ack_cancel")]
    [InlineData(false, true, "answer_ack_key_reset")]
    [InlineData(false, false, "retired_answer_current")]
    [InlineData(true, true, "retired_answer_current")]
    [InlineData(false, false, "retired_answer_cancel")]
    [InlineData(true, true, "retired_answer_cancel")]
    [InlineData(false, true, "retired_answer_new_transfer")]
    [InlineData(true, false, "retired_answer_key_reset")]
    [InlineData(false, false, "retired_answer_rejected_cancel")]
    [Trait("FileTransferInfraSuite", "IdleAnswerRaces")]
    public Task IdleSession_AnswersCannotOutliveTheirUnlock(bool completed, bool helperPays, string change)
        => RunIdleSessionReestablishmentAsync(completed, helperPays, switchPayer: true, stopBeforeAnswer: false, change);

    [Theory]
    [InlineData(false, false, "answer_ack_new_transfer")]
    [InlineData(false, true, "answer_ack_new_transfer")]
    [InlineData(true, false, "answer_ack_new_transfer")]
    [InlineData(true, true, "answer_ack_new_transfer")]
    [InlineData(false, false, "answer_physical_stop")]
    [InlineData(false, true, "answer_physical_stop")]
    [InlineData(false, false, "answer_physical_cancel")]
    [InlineData(false, true, "answer_physical_cancel")]
    [InlineData(false, false, "answer_physical_key_reset")]
    [InlineData(false, true, "answer_physical_replace")]
    [InlineData(false, false, "answer_physical_expire")]
    [InlineData(false, true, "answer_physical_current")]
    [InlineData(false, false, "answer_ack_writer_stop_before")]
    [InlineData(false, false, "answer_ack_writer_stop_after")]
    [InlineData(false, false, "answer_ack_writer_current")]
    [InlineData(false, false, "answer_ack_writer_fixed_current")]
    [InlineData(false, false, "answer_ack_writer_fixed_replaced")]
    [Trait("FileTransferInfraSuite", "IdleAckCompletionRaces")]
    public Task IdleSession_AcceptedAckCompletesOnlyWithinItsHandshake(bool completed, bool helperPays, string change)
        => RunIdleSessionReestablishmentAsync(completed, helperPays, switchPayer: true, stopBeforeAnswer: false, change);

    [Theory]
    [InlineData("offer_writer_current")]
    [InlineData("offer_writer_stop")]
    [InlineData("offer_writer_new_transfer")]
    public Task IdleOfferAtWriter_RechecksOriginalUnlock(string change) =>
        RunIdleSessionReestablishmentAsync(false, false, switchPayer: true, stopBeforeAnswer: false, change);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExplicitUnlock_WithUnfinishedFilesInBothDirections_ReconnectsWithoutChangingEitherFile(bool fileOnly)
        => CheckDuplexUnlockAsync(fileOnly, deselectScreen: false);

    [Fact]
    public Task DuplexUnlock_ScreenDeselectionDoesNotGiveLateFileSubscribersAHandoff()
        => CheckDuplexUnlockAsync(fileOnly: false, deselectScreen: true);

    private static async Task OfferPausedFileAsync(SessionFileTransferService from, SessionFileTransferService to, string id, CancellationToken ct)
    {
        Assert.NotNull(await from.TryStartSendAsync(new(id + ".bin", 1024, id),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[1024], writable: false)), ct));
        await WaitUntilAsync(() => to.Snapshot.Inbound?.TransferId == id, TimeSpan.FromSeconds(3));
        Assert.True((await from.PauseTransferAsync(id, "user_requested", ct))?.IsPaused);
        Assert.NotNull(await to.AcceptIncomingTransferAsync(id, (_, _) => Task.FromResult<Stream>(new MemoryStream()), ct));
    }

    [Fact]
    public async Task DuplexUnlock_OrdinaryOfferDeliveredAfterBothAdmissionsCannotPauseEitherFile()
    {
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ct = timeout.Token;
        var a = new FakeNknClient("host.delayed-duplex.address");
        var b = new FakeNknClient("helper.delayed-duplex.address");
        var options = NknTransportOptions.Load();
        using var lane = new RetryableTunaAccelerationSession(false, 0);
        using var host = new NknSignalingTransport(a, options, new NknIdentity("host", a.Address), NknTunaAccelerationOptions.Disabled, lane);
        using var helper = new NknSignalingTransport(b, options, new NknIdentity("helper", b.Address));
        using var hostFiles = new SessionFileTransferService();
        using var helperFiles = new SessionFileTransferService();
        hostFiles.AttachTransport(host);
        helperFiles.AttachTransport(helper);
        var session = await ApproveNknSessionAsync(host, helper, ct, InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        // The authenticated ordinary offer already exists before either file is admitted.
        var offer = CreateOfferPayload(session, "delayed-ordinary-offer", ["file"], payerDecisionId: 7);
        offer.Trigger = "session_ready";
        var delayed = BuildSecureAccelerationEnvelope(helper, MsgType.TransportAccelerationOffer, offer,
            "transport_acceleration_offer", offer.Nonce, sequence: 730);
        await OfferPausedFileAsync(hostFiles, helperFiles, "outgoing", ct);
        await OfferPausedFileAsync(helperFiles, hostFiles, "incoming", ct);
        var pauses = new ConcurrentQueue<string>();
        foreach (var id in new[] { "outgoing", "incoming" })
        {
            var channel = await host.OpenFileTransferDataSessionAsync(session, id, ct);
            channel.AvailabilityChanged += (_, e) => { if (!e.IsAvailable && e.Reason == "tuna_activation_negotiating") pauses.Enqueue(id); };
        }
        var answer = new TaskCompletionSource<TransportAccelerationAnswerPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.ShouldDeliverSendAsync = (_, bytes, _) =>
        {
            if (!EnvelopeCodec.TryDeserialize(bytes, out var env) || env.Type != MsgType.TransportAccelerationAnswer)
                return Task.FromResult(true);
            var plain = SessionSecureEnvelopeCodec.Decrypt((byte[])InvokePrivateMethod(host, "GetControlSessionSharedKeyOrThrow")!, env.Payload);
            answer.TrySetResult(JsonSerializer.Deserialize<TransportAccelerationAnswerPayload>(plain.Plaintext)!);
            return Task.FromResult(false);
        };
        InvokePrivateMethod(host, "HandleTransportAccelerationOffer", b.Address, delayed);
        var response = await answer.Task.WaitAsync(ct);
        Assert.Equal(offer.Nonce, response.Nonce);
        Assert.Empty(pauses);
        Assert.False(response.Accepted);
        Assert.Equal("duplex_session_unlock_required", response.RejectReason);
        Assert.Equal(0, lane.StartDialerCalls);
        Assert.Null(GetPrivateField(host, "fileTransferTunaActivationPauseSessionId"));
        Assert.All(new[] { hostFiles.Snapshot.Outbound!, hostFiles.Snapshot.Inbound! },
            file => Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, file.RouteToken));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public Task DuplexUnlock_ReverseFileJoinsWhileOrdinaryFinalAckWaits(bool checkPause, bool fixedLanes)
        => CheckReverseAdmissionAsync(checkPause, fixedLanes);

    [Fact]
    public Task DuplexUnlock_ReverseAdmissionAtDispatchVetoesBothPeersHandoffs()
        => CheckReverseAdmissionAsync(false, false, holdDispatch: true);

    [Theory]
    [InlineData(false, false)] // Recovery overlaps the handshake and finishes last.
    [InlineData(false, true)]  // Recovery finishes first; the handshake still owns its pause.
    [InlineData(true, false)]  // The channel was already held before the handshake.
    [InlineData(true, true)]   // The pre-existing recovery ends while the final ACK is still gated.
    public Task DuplexUnlock_FinalAckReleasesOnlyItsOwnChannels(bool recoveryPrecedesHandshake, bool recoveryCompletesBeforeAck)
        => CheckReverseAdmissionAsync(false, false, independentHold: true,
            recoveryPrecedesHandshake: recoveryPrecedesHandshake, recoveryCompletesBeforeAck: recoveryCompletesBeforeAck);

    [Fact]
    public Task DuplexUnlock_NewChannelHeldByRecoveryWaitsForFinalAck()
        => CheckReverseAdmissionAsync(false, false, independentHold: true,
            recoveryCompletesBeforeAck: true, recoveryDuringOpen: true);

    [Fact]
    public Task DuplexUnlock_RecoveryFinishingInsideChannelOpenWaitsForFinalAck()
        => CheckReverseAdmissionAsync(false, false, independentHold: true,
            recoveryCompletesBeforeAck: true, recoveryDuringOpen: true, finishInsideOpen: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DuplexUnlock_ReverseOfferDuringReopenRetainsOnlyFirstFilesPause(bool finishInsideOpen)
        => CheckReverseAdmissionAsync(false, false, independentHold: true, recoveryCompletesBeforeAck: true,
            recoveryDuringOpen: true, finishInsideOpen: finishInsideOpen, admitReverseDuringOpen: true);

    [Fact]
    public Task DuplexUnlock_ConcurrentReopenNeverExposesFirstFileBeforeAck()
        => CheckReverseAdmissionAsync(false, false, independentHold: true, recoveryCompletesBeforeAck: true,
            recoveryDuringOpen: true, admitReverseDuringOpen: true, concurrentOrder: ConcurrentOpenOrder.OpenBeforeAck);

    [Fact]
    public Task DuplexUnlock_FinalAckBeforeConcurrentReopenFinishesDoesNotReinstallPause()
        => CheckReverseAdmissionAsync(false, false, independentHold: true, recoveryCompletesBeforeAck: true,
            recoveryDuringOpen: true, admitReverseDuringOpen: true, concurrentOrder: ConcurrentOpenOrder.AckBeforeOpen);

    private enum ConcurrentOpenOrder { None, OpenBeforeAck, AckBeforeOpen }

    private async Task CheckReverseAdmissionAsync(bool checkPause, bool fixedLanes, bool holdDispatch = false,
        bool independentHold = false, bool recoveryPrecedesHandshake = false, bool recoveryCompletesBeforeAck = false,
        bool recoveryDuringOpen = false, bool finishInsideOpen = false, bool admitReverseDuringOpen = false,
        ConcurrentOpenOrder concurrentOrder = ConcurrentOpenOrder.None)
    {
        var concurrentReopen = concurrentOrder != ConcurrentOpenOrder.None;
        var ackBeforeOpen = concurrentOrder == ConcurrentOpenOrder.AckBeforeOpen;
        FakeNknClient.ResetNetwork();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var payerClient = new WriterGatePeer("host.duplex-ack.address");
        var dialerClient = new FakeNknClient("helper.duplex-ack.address");
        var both = NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen;
        var laneOptions = NknTunaAccelerationOptions.CreateRuntimePilot("fake-sidecar.exe", both, runtimeLaneLimit: fixedLanes ? null : () => both);
        using var listener = new RetryableTunaAccelerationSession(true, 0) { StrictPayerHandoffForTests = true };
        using var dial = new RetryableTunaAccelerationSession(false, 0) { StrictPayerHandoffForTests = true };
        using var payer = new NknSignalingTransport(payerClient, NknTransportOptions.Load(), new NknIdentity("host", payerClient.Address), laneOptions, listener);
        using var responder = new NknSignalingTransport(dialerClient, NknTransportOptions.Load(), new NknIdentity("helper", dialerClient.Address), laneOptions, dial);
        using var payerFiles = new SessionFileTransferService();
        using var responderFiles = new SessionFileTransferService();
        payerFiles.AttachTransport(payer);
        responderFiles.AttachTransport(responder);
        var session = await ApproveNknSessionAsync(payer, responder, ct,
            InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
        var offer = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var atAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ack = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        payerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
        {
            if (!EnvelopeCodec.TryDeserialize(bytes, out var env)) return Task.FromResult(true);
            if ((holdDispatch || independentHold) && env.Type == MsgType.TransportAccelerationAnswerAck)
            {
                ack.TrySetResult(env);
                return Task.FromResult(false);
            }
            if (env.Type != MsgType.TransportAccelerationOffer)
                return Task.FromResult(true);
            offer.TrySetResult(env);
            return Task.FromResult(false);
        };
        payer.IdleSessionBeforeAnswerAckForTests = async () =>
        {
            atAck.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        await payer.RequestAccelerationNegotiationAsync("session_ready", ct);
        var sent = await offer.Task.WaitAsync(ct);
        await OfferPausedFileAsync(responderFiles, payerFiles, "first", ct);
        var original = await responder.OpenFileTransferDataSessionAsync(session, "first", ct);
        Assert.True(original.IsAvailable);
        void HoldForRecovery() => InvokePrivateMethod(responder, "SetFileTransferDataSessionsAvailability",
            false, "receive_stall_recovery", false, FileTransferTransportHandoffKind.None, FileTransferTransportKind.RegularNkn);
        void FinishRecovery() => InvokePrivateMethod(responder, "SetFileTransferDataSessionsAvailability",
            true, "transport_recovered", false, FileTransferTransportHandoffKind.None, FileTransferTransportKind.RegularNkn);
        int originalReopened = 0;
        string? lastHoldReason = null;
        void ObserveAvailability(object? sender, FileTransferDataSessionAvailabilityChangedEventArgs e)
        {
            if (e.IsAvailable) Interlocked.Increment(ref originalReopened);
            else Volatile.Write(ref lastHoldReason, e.Reason);
        }
        if (independentHold && !recoveryDuringOpen)
            original.AvailabilityChanged += ObserveAvailability;
        if (recoveryPrecedesHandshake)
        {
            HoldForRecovery();
            Assert.False(original.IsAvailable);
        }
        InvokePrivateMethod(responder, "HandleTransportAccelerationOffer", payerClient.Address, sent);
        await atAck.Task.WaitAsync(ct);
        using var unblockDispatch = new ManualResetEventSlim();
        var observed = new ConcurrentQueue<string>();
        Task? responderAck = null;
        IFileTransferDataSession? heldReverse = null;
        int reversePauses = 0;
        async Task AdmitReverseAsync()
        {
            // Deliver the ordinary reverse offer without waiting on the optional initial Tuna readiness.
            await payer.SendFileTransferOfferAsync(new FileTransferOfferV2
            {
                SessionId = session, TransferId = "reverse", FileName = "reverse.bin", FileSizeBytes = 1024,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            }, ct);
            await WaitUntilAsync(() => responderFiles.Snapshot.Inbound?.TransferId == "reverse", TimeSpan.FromSeconds(3));
            Assert.All(new[] { payer, responder }, peer => Assert.True((bool)InvokePrivateMethod(peer, "HasDuplexFileOffers", session)!));
        }
        try
        {
            Assert.False(original.IsAvailable);
            if (recoveryDuringOpen)
            {
                // Reopen file A's channel while its ordinary handshake is waiting for the ACK.
                original.Dispose();
                IFileTransferDataSession? registered = null;
                IFileTransferDataSession? registeredReverse = null;
                var atOpenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var continueOpen = new ManualResetEventSlim();
                if (ackBeforeOpen)
                {
                    // Let A capture its retained pause, but hold the actual write until its ACK has retired that generation.
                    responder.NewChannelPauseCapturedForTests = () =>
                    {
                        if (atOpenGate.TrySetResult()) continueOpen.Wait(ct);
                    };
                }
                responder.NewChannelRegisteredForTests = channel =>
                {
                    if (admitReverseDuringOpen && channel.TransferId == "reverse")
                    {
                        registeredReverse = channel;
                        channel.AvailabilityChanged += (_, e) => { if (!e.IsAvailable) Interlocked.Increment(ref reversePauses); };
                        return;
                    }
                    Assert.Equal("first", channel.TransferId);
                    Assert.Null(registered);
                    registered = channel;
                    channel.AvailabilityChanged += ObserveAvailability;
                    if (!concurrentReopen) // A separate recovery hold would mask exposure of the uninitialized channel.
                    {
                        HoldForRecovery();
                        Assert.False(channel.IsAvailable);
                        Assert.Equal("receive_stall_recovery", Volatile.Read(ref lastHoldReason));
                    }
                    if (admitReverseDuringOpen && !ackBeforeOpen)
                    {
                        atOpenGate.TrySetResult();
                        continueOpen.Wait(ct);
                    }
                    if (finishInsideOpen)
                    {
                        FinishRecovery();
                        Assert.Equal(0, Volatile.Read(ref originalReopened));
                        Assert.False(channel.IsAvailable);
                    }
                };
                if (admitReverseDuringOpen)
                {
                    Task<IFileTransferDataSession>? secondOpen = null;
                    var reopening = Task.Run(() => responder.OpenFileTransferDataSessionAsync(session, "first", ct), ct);
                    try
                    {
                        await atOpenGate.Task.WaitAsync(ct);
                        await AdmitReverseAsync();
                        heldReverse = await responder.OpenFileTransferDataSessionAsync(session, "reverse", ct);
                        Assert.Same(registeredReverse, heldReverse);
                        Assert.True(heldReverse.IsAvailable);
                        if (concurrentReopen)
                        {
                            secondOpen = responder.OpenFileTransferDataSessionAsync(session, "first", ct);
                            Assert.False(secondOpen.IsCompleted);
                            if (ackBeforeOpen)
                            {
                                Assert.False(responder.IsAccelerationAvailableForTests);
                                Assert.False(ack.Task.IsCompleted);
                                Assert.True(release.TrySetResult());
                                var writtenAck = await ack.Task.WaitAsync(ct);
                                Assert.True(await Assert.IsAssignableFrom<Task<bool>>(payer.IdleSessionAnswerAckForTests).WaitAsync(ct));
                                InvokePrivateMethod(responder, "HandleTransportAccelerationAnswerAck", payerClient.Address, writtenAck);
                                Assert.True(responder.IsAccelerationAvailableForTests);
                                Assert.False(reopening.IsCompleted);
                                Assert.True(heldReverse.IsAvailable);
                            }
                            else
                            {
                                using var cancelWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                var canceledOpen = responder.OpenFileTransferDataSessionAsync(session, "first", cancelWait.Token);
                                cancelWait.Cancel();
                                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledOpen);
                            }
                            Assert.False(secondOpen.IsCompleted);
                        }
                    }
                    finally
                    {
                        continueOpen.Set();
                        try { original = await reopening; }
                        finally { if (ackBeforeOpen) responder.NewChannelPauseCapturedForTests = null; }
                    }
                    if (secondOpen is not null)
                    {
                        var second = await secondOpen.WaitAsync(ct);
                        Assert.Same(original, second);
                        Assert.Equal(ackBeforeOpen, second.IsAvailable);
                    }
                }
                else original = await responder.OpenFileTransferDataSessionAsync(session, "first", ct);
                responder.NewChannelRegisteredForTests = null;
                Assert.Same(registered, original);
            }
            if (holdDispatch)
            {
                var atPayer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var atResponder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                foreach (var (peer, at) in new[] { (payer, atPayer), (responder, atResponder) })
                {
                    var channel = await peer.OpenFileTransferDataSessionAsync(session, "first", ct);
                    channel.AvailabilityChanged += (_, e) =>
                    {
                        if (e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation) observed.Enqueue("first");
                    };
                    peer.OrdinaryFileHandoffBeforeDispatchForTests = () =>
                    {
                        if (at.TrySetResult()) unblockDispatch.Wait(ct);
                    };
                }
                release.TrySetResult();
                var writtenAck = await ack.Task.WaitAsync(ct);
                responderAck = Task.Run(() => InvokePrivateMethod(responder, "HandleTransportAccelerationAnswerAck", payerClient.Address, writtenAck));
                await Task.WhenAll(atPayer.Task, atResponder.Task).WaitAsync(ct);
            }
            if (!admitReverseDuringOpen) await AdmitReverseAsync();
            if (independentHold && !recoveryPrecedesHandshake && !recoveryDuringOpen)
            {
                heldReverse = await responder.OpenFileTransferDataSessionAsync(session, "reverse", ct);
                Assert.True(heldReverse.IsAvailable);
                HoldForRecovery();
                Assert.False(heldReverse.IsAvailable);
            }
            if (recoveryCompletesBeforeAck)
            {
                if (!finishInsideOpen && !concurrentReopen) FinishRecovery();
                if (heldReverse is not null) Assert.True(heldReverse.IsAvailable);
                Assert.Equal(0, Volatile.Read(ref originalReopened));
                Assert.Equal(ackBeforeOpen, original.IsAvailable);
                if (admitReverseDuringOpen)
                {
                    Assert.True(responderFiles.Snapshot.Outbound!.IsPaused);
                    Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, responderFiles.Snapshot.Outbound.RouteToken);
                }
            }
            if (fixedLanes)
            {
                var early = new ConcurrentQueue<string>();
                var reverse = await payer.OpenFileTransferDataSessionAsync(session, "reverse", ct);
                reverse.AvailabilityChanged += (_, e) =>
                {
                    if (e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation) early.Enqueue("reverse");
                };
                Assert.Empty(early);
            }
        }
        finally { unblockDispatch.Set(); release.TrySetResult(); }
        if (independentHold && !ackBeforeOpen)
            InvokePrivateMethod(responder, "HandleTransportAccelerationAnswerAck", payerClient.Address, await ack.Task.WaitAsync(ct));
        if (responderAck is not null) await responderAck.WaitAsync(ct);
        if (holdDispatch || independentHold)
        {
            await payer.IdleSessionAnswerAckForTests!.WaitAsync(ct);
            Assert.Empty(observed);
            if (independentHold && !recoveryCompletesBeforeAck)
            {
                Assert.False(original.IsAvailable);
                Assert.Equal(0, Volatile.Read(ref originalReopened));
                if (heldReverse is not null) Assert.False(heldReverse.IsAvailable);
                Assert.Equal("receive_stall_recovery", Volatile.Read(ref lastHoldReason));
                FinishRecovery();
            }
            Assert.True(original.IsAvailable);
            if (independentHold)
            {
                Assert.Equal(ackBeforeOpen ? 0 : 1, Volatile.Read(ref originalReopened));
                if (ackBeforeOpen) Assert.Null(Volatile.Read(ref lastHoldReason));
                Assert.True(responderFiles.Snapshot.Outbound!.IsPaused);
            }
            if (heldReverse is not null) Assert.True(heldReverse.IsAvailable);
            if (admitReverseDuringOpen) Assert.Equal(0, Volatile.Read(ref reversePauses));
            Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, responderFiles.Snapshot.Outbound!.RouteToken);
        }
        await WaitUntilAsync(() => payer.IsAccelerationAvailableForTests && responder.IsAccelerationAvailableForTests, TimeSpan.FromSeconds(4));
        if (checkPause)
        {
            await WaitUntilAsync(() => original.IsAvailable, TimeSpan.FromSeconds(2));
            Assert.True(responderFiles.Snapshot.Outbound!.IsPaused); // Only the transport pause was released.
            Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, responderFiles.Snapshot.Outbound.RouteToken);
            var regularData = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dialerClient.BeforeSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var env) && env.Type == MsgType.FileTransferDataFrame)
                    regularData.TrySetResult();
                return Task.CompletedTask;
            };
            Assert.NotNull(await responderFiles.ResumeTransferAsync("first", "user_requested", ct));
            await regularData.Task.WaitAsync(ct);
            await WaitUntilAsync(() => payerFiles.Snapshot.Inbound?.State == FileTransferTransferState.Completed &&
                responderFiles.Snapshot.Outbound?.State == FileTransferTransferState.Completed, TimeSpan.FromSeconds(5));
        }
        else
        {
            var received = new ConcurrentQueue<string>();
            foreach (var peer in new[] { payer, responder })
                foreach (var id in new[] { "first", "reverse" })
                {
                    var channel = await peer.OpenFileTransferDataSessionAsync(session, id, ct);
                    channel.AvailabilityChanged += (_, e) =>
                    {
                        if (e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation) received.Enqueue(id);
                    };
                }
            Assert.Empty(received);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DuplexUnlock_EligibleHelpeePaysWhenOnlyHelperReenables(bool dropHelperIntent)
        => CheckDuplexUnlockAsync(false, false, helpeeEligible: true, dropHelperIntent: dropHelperIntent);

    [Fact]
    public Task DuplexUnlock_EligibleHelpeeWithoutAnAddressPaysWhenOnlyHelperReenables()
        => CheckDuplexUnlockAsync(false, false, helpeeEligible: true, dropHelperIntent: true, coldHelpee: true);

    private async Task CheckDuplexUnlockAsync(bool fileOnly, bool deselectScreen, bool helpeeEligible = false, bool dropHelperIntent = false, bool coldHelpee = false)
    {
        FakeNknClient.ResetNetwork();
        var scope = Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var hostClient = new FakeNknClient($"host.duplex-{scope}.address");
        var helperClient = new FakeNknClient($"helper.duplex-{scope}.address");
        using var hostLane = new RetryableTunaAccelerationSession(false, 0) { StrictPayerHandoffForTests = true, ClearLocalAddressOnStop = coldHelpee };
        using var helperLane = new RetryableTunaAccelerationSession(true, 0) { StrictPayerHandoffForTests = true };
        var options = NknTransportOptions.Load();
        var selectedLanes = NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen;
        var laneOptions = NknTunaAccelerationOptions.CreateRuntimePilot("fake-sidecar.exe", selectedLanes,
            runtimeLaneLimit: () => selectedLanes);
        using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("duplex-host", hostClient.Address),
            laneOptions, hostLane);
        using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("duplex-helper", helperClient.Address),
            laneOptions, helperLane);
        using var hostFiles = new SessionFileTransferService();
        using var helperFiles = new SessionFileTransferService();
        hostFiles.AttachTransport(host);
        helperFiles.AttachTransport(helper);
        var session = await ApproveNknSessionAsync(host, helper, ct, InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        bool Ready() => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests;
        await helper.RequestAccelerationNegotiationAsync("runtime_unlock", ct);
        await WaitUntilAsync(Ready, TimeSpan.FromSeconds(5));
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hostLane.StateChanged += (_, e) => { if (e.Reason == "remote_header_switch_off") stopped.TrySetResult(); };
        await helper.StopAccelerationAsync("header_switch_off", ct);
        await stopped.Task.WaitAsync(ct);
        Assert.False(host.IsAccelerationAvailableForTests);
        Assert.False(helper.IsAccelerationAvailableForTests);

        await OfferPausedFileAsync(helperFiles, hostFiles, "helper-file", ct);
        await OfferPausedFileAsync(hostFiles, helperFiles, "helpee-file", ct);
        await WaitUntilAsync(() => helperFiles.Snapshot.Inbound?.IsPeerPaused == true &&
            hostFiles.Snapshot.Inbound?.IsPeerPaused == true, TimeSpan.FromSeconds(3));
        FileTransferTransferSnapshot[] Files() => [helperFiles.Snapshot.Outbound!, hostFiles.Snapshot.Inbound!,
            hostFiles.Snapshot.Outbound!, helperFiles.Snapshot.Inbound!];
        var before = Files();
        Assert.All(before, file => { Assert.False(file.IsTerminal); Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, file.RouteToken); });

        int handoffs = 0;
        host.OrdinaryFileHandoffBeforeDispatchForTests = helper.OrdinaryFileHandoffBeforeDispatchForTests =
            () => Interlocked.Increment(ref handoffs);
        if (fileOnly) selectedLanes = NknAccelerationLaneKind.File;
        if (helpeeEligible)
        {
            hostLane.SetCanListen(true);
            if (coldHelpee)
            {
                Assert.Null(hostLane.LocalTunaAddress);
                Assert.True(hostLane.CanOfferListener);
            }
            if (dropHelperIntent)
                helperClient.ShouldDeliverSendAsync = (_, bytes, _) => Task.FromResult(
                    !EnvelopeCodec.TryDeserialize(bytes, out var env) || env.Type != MsgType.TransportAccelerationPayerIntent);
        }
        await helper.RequestAccelerationNegotiationAsync("runtime_unlock", ct);
        await WaitUntilAsync(Ready, TimeSpan.FromSeconds(5));
        if (helpeeEligible)
        {
            Assert.True(hostLane.IsLocalPaidListenerActive);
            Assert.False(helperLane.IsLocalPaidListenerActive);
        }
        Assert.Equal(selectedLanes, helper.AccelerationNegotiatedLanesForTests);
        Assert.Equal(helper.AccelerationNegotiatedLanesForTests, host.AccelerationNegotiatedLanesForTests);
        if (deselectScreen)
        {
            selectedLanes = NknAccelerationLaneKind.File;
            helper.RetireDeselectedAccelerationOffers();
            host.RetireDeselectedAccelerationOffers();
            var received = new ConcurrentQueue<string>();
            foreach (var peer in new[] { helper, host })
            {
                Assert.True(peer.IsAccelerationAvailableForTests);
                Assert.True(peer.AccelerationNegotiatedLanesForTests.HasFlag(NknAccelerationLaneKind.File));
                foreach (var id in new[] { "helper-file", "helpee-file" })
                {
                    var channel = await peer.OpenFileTransferDataSessionAsync(session, id, ct);
                    channel.AvailabilityChanged += (_, e) =>
                    {
                        if (e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation) received.Enqueue(id);
                    };
                }
            }
            Assert.Empty(received);
        }
        Assert.Equal(0, Volatile.Read(ref handoffs));
        foreach (var (old, current) in before.Zip(Files()))
        {
            Assert.False(current.IsTerminal);
            Assert.Equal((old.TransferId, old.RouteToken, old.ProtocolVersion), (current.TransferId, current.RouteToken, current.ProtocolVersion));
            Assert.False(current.IsPauseBlockedByTransport);
            Assert.True(current.Direction == FileTransferDirection.Outbound ? current.IsPaused : current.IsPeerPaused);
        }
    }

    private async Task RunIdleSessionReestablishmentAsync(
        bool completed, bool helperPays, bool switchPayer, bool stopBeforeAnswer, string? change = null)
    {
        FakeNknClient.ResetNetwork();
        var previousPriority = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var releaseAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePublication = new ManualResetEventSlim();
        var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerCommitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseAnswerCommit = new ManualResetEventSlim();
        var answerAckEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAnswerAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unlockCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var delayStop = change?.StartsWith("delayed_stop", StringComparison.Ordinal) == true;
        var failSend = change?.StartsWith("first_send_failure", StringComparison.Ordinal) == true;
        var failOffer = change?.StartsWith("offer_failure", StringComparison.Ordinal) == true;
        var answerTimeout = change?.StartsWith("answer_timeout", StringComparison.Ordinal) == true;
        var offerRetry = failOffer || answerTimeout;
        try
        {
            var scope = Guid.NewGuid().ToString("N");
            var writerAckRace = change?.StartsWith("answer_ack_writer_", StringComparison.Ordinal) == true;
            var fixedWriter = change?.StartsWith("answer_ack_writer_fixed_", StringComparison.Ordinal) == true;
            var hostClient = new FakeNknClient($"host.idle-restart-{scope}.address");
            var writerOfferRace = change?.StartsWith("offer_writer_", StringComparison.Ordinal) == true;
            var helperClient = writerAckRace || writerOfferRace ? new WriterGatePeer($"helper.idle-restart-{scope}.address") : new FakeNknClient($"helper.idle-restart-{scope}.address");
            using var hostLane = new RetryableTunaAccelerationSession(!helperPays, 0) { StrictPayerHandoffForTests = true };
            using var helperLane = new RetryableTunaAccelerationSession(helperPays, 0) { StrictPayerHandoffForTests = true };
            hostLane.ForwardBytesForTests = async (lane, bytes, _) =>
            {
                await Task.Yield();
                helperLane.ReceivePeerBytesForTests(lane, bytes);
                return true;
            };
            helperLane.ForwardBytesForTests = async (lane, bytes, _) =>
            {
                await Task.Yield();
                hostLane.ReceivePeerBytesForTests(lane, bytes);
                return true;
            };
            var options = NknTransportOptions.Load();
            using var host = new NknSignalingTransport(hostClient, options,
                new NknIdentity("host-idle-restart", hostClient.Address), NknTunaAccelerationOptions.Disabled, hostLane);
            using var helper = new NknSignalingTransport(helperClient, options,
                new NknIdentity("helper-idle-restart", helperClient.Address),
                writerAckRace && !fixedWriter ? NknTunaAccelerationOptions.CreateRuntimePilot("fake-sidecar.exe", NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen,
                    runtimeLaneLimit: () => NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen) : NknTunaAccelerationOptions.Disabled, helperLane);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(host);
            receiver.AttachTransport(helper);
            await ApproveNknSessionAsync(host, helper, timeout.Token,
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.FileTransfer,
                suppressAutomaticAccelerationNegotiation: true);
            var originalPayer = helperPays ? helper : host;
            await originalPayer.RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token);
            await WaitUntilAsync(() => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(5));

            const string transferId = "idle-restart-transfer";
            var payload = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i % 251)).ToArray();
            using var destination = new MemoryStream();
            await sender.TryStartSendAsync(new("memory.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), timeout.Token);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
                TimeSpan.FromSeconds(3));
            if (completed)
                await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), timeout.Token);
            else
                await sender.CancelTransferAsync(transferId, "test_cancel", timeout.Token);
            var terminalState = completed ? FileTransferTransferState.Completed : FileTransferTransferState.Canceled;
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == terminalState && receiver.Snapshot.Inbound?.State == terminalState,
                TimeSpan.FromSeconds(5));
            if (completed) Assert.Equal(payload, destination.ToArray());

            var remoteStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (delayStop)
                (helperPays ? hostLane : helperLane).BeforeStopForTests = async reason =>
                {
                    if (reason != "remote_header_switch_off") return;
                    stopEntered.TrySetResult();
                    await releaseStop.Task.WaitAsync(timeout.Token);
                };
            // Observe completion of the peer's asynchronous stop callback, not just
            // the earlier clearing of its negotiated/available state.
            (helperPays ? hostLane : helperLane).StateChanged += (_, e) =>
            {
                if (!e.IsAvailable && e.Reason == "remote_header_switch_off") remoteStopped.TrySetResult();
            };
            await originalPayer.StopAccelerationAsync("header_switch_off", timeout.Token);
            await (delayStop ? stopEntered.Task : remoteStopped.Task).WaitAsync(TimeSpan.FromSeconds(3), timeout.Token);
            await WaitUntilAsync(() => !host.IsAccelerationAvailableForTests && !helper.IsAccelerationAvailableForTests &&
                (int)GetPrivateField(host, "accelerationNegotiationScheduled")! == 0 &&
                (int)GetPrivateField(helper, "accelerationNegotiationScheduled")! == 0, TimeSpan.FromSeconds(3));
            var hostPreparation = GetPrivateField(sender, "sessionPreparation");
            var helperPreparation = GetPrivateField(receiver, "sessionPreparation");
            Assert.Equal(FileTransferSessionPreparationPhase.AdmissionReserved,
                Assert.IsType<FileTransferSessionPreparationState>(hostPreparation).Phase);
            var retainedPublications = new[] { host, helper }.Select(transport =>
            {
                var map = (System.Collections.IDictionary)GetPrivateField(transport, "fileTransferRoutePublicationAuthorities")!;
                return map.Keys.Cast<object>().ToDictionary(key => key, key => map[key]);
            }).ToArray();
            var restartHelperPays = helperPays != switchPayer;
            hostLane.SetCanListen(!restartHelperPays);
            helperLane.SetCanListen(restartHelperPays);
            var payer = restartHelperPays ? helper : host;
            var payerClient = restartHelperPays ? helperClient : hostClient;
            string? rejectedIntentId = null;
            string? firstOfferId = null;
            var sentIntents = new ConcurrentDictionary<string, TransportAccelerationPayerIntentPayload>();
            var sentOffers = new ConcurrentDictionary<string, TransportAccelerationOfferPayload>();
            if (failSend || offerRetry)
                payerClient.BeforeSendCoreAsync = (_, bytes, _, _) =>
                {
                    if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) && envelope.Type == MsgType.TransportAccelerationPayerIntent)
                    {
                        var decoded = SessionSecureEnvelopeCodec.Decrypt(
                            (byte[])InvokePrivateMethod(payer, "GetControlSessionSharedKeyOrThrow")!, envelope.Payload);
                        sentIntents[envelope.MessageId] = JsonSerializer.Deserialize<TransportAccelerationPayerIntentPayload>(decoded.Plaintext)!;
                        Interlocked.CompareExchange(ref rejectedIntentId, envelope.MessageId, null);
                        if (failSend && envelope.MessageId == rejectedIntentId)
                        {
                            failedSend.TrySetResult();
                            throw new InvalidOperationException("test_first_intent_rejected");
                        }
                    }
                    if (offerRetry && EnvelopeCodec.TryDeserialize(bytes, out var offerEnvelope) &&
                        offerEnvelope.Type == MsgType.TransportAccelerationOffer)
                    {
                        var decoded = SessionSecureEnvelopeCodec.Decrypt(
                            (byte[])InvokePrivateMethod(payer, "GetControlSessionSharedKeyOrThrow")!, offerEnvelope.Payload);
                        sentOffers[offerEnvelope.MessageId] = JsonSerializer.Deserialize<TransportAccelerationOfferPayload>(decoded.Plaintext)!;
                        Interlocked.CompareExchange(ref firstOfferId, offerEnvelope.MessageId, null);
                        if (failOffer && offerEnvelope.MessageId == firstOfferId)
                            throw new InvalidOperationException("test_first_offer_rejected");
                    }
                    return Task.CompletedTask;
                };
            if (answerTimeout)
            {
                NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(200);
                payerClient.ShouldDeliverSendAsync = (_, bytes, _) => Task.FromResult(
                    !EnvelopeCodec.TryDeserialize(bytes, out var envelope) ||
                    envelope.Type != MsgType.TransportAccelerationOffer || envelope.MessageId != firstOfferId);
            }
            if (offerRetry)
                payer.IdleSessionUnlockBeforeRetryForTests = async () =>
                {
                    retryEntered.TrySetResult();
                    await releaseRetry.Task.WaitAsync(timeout.Token);
                };
            var messages = new ConcurrentQueue<MsgType>();
            Envelope? heldAnswer = null;
            foreach (var client in new[] { hostClient, helperClient })
                client.BeforeSendAsync = async (_, bytes, ct) =>
                {
                    if (!EnvelopeCodec.TryDeserialize(bytes, out var envelope)) return;
                    messages.Enqueue(envelope.Type);
                    if (envelope.Type != MsgType.TransportAccelerationAnswer) return;
                    heldAnswer = envelope;
                    answerHeld.TrySetResult();
                    await releaseAnswer.Task.WaitAsync(ct);
                };
            if (change == "before_publication_stop")
            {
                payer.IdleSessionUnlockBeforePublicationForTests = () =>
                {
                    publicationEntered.TrySetResult();
                    releasePublication.Wait(timeout.Token);
                };
                var unlock = Task.Run(() => payer.RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token));
                try
                {
                    await publicationEntered.Task.WaitAsync(timeout.Token);
                    var stopGeneration = (long)GetPrivateField(payer, "accelerationExplicitStopGeneration")!;
                    await payer.StopAccelerationAsync("header_switch_off", timeout.Token);
                    Assert.Equal(stopGeneration + 1, GetPrivateField(payer, "accelerationExplicitStopGeneration"));
                }
                finally { releasePublication.Set(); }
                await unlock.WaitAsync(timeout.Token);
                Assert.Null(GetPrivateField(payer, "idleSessionUnlock"));
                Assert.False((bool)GetPrivateField(payer, "idleSessionUnlockWaiting")!);
                Assert.Equal(0, GetPrivateField(payer, "accelerationNegotiationScheduled"));
                Assert.Empty(messages);
                Assert.False(host.IsAccelerationAvailableForTests);
                Assert.False(helper.IsAccelerationAvailableForTests);
                return;
            }
            if (writerOfferRace)
            {
                var writerPeer = Assert.IsType<WriterGatePeer>(payerClient);
                using var bytes = new MemoryStream();
                using var writer = new BridgeStdioWriter(bytes, leaveOpen: true);
                var gate = (SemaphoreSlim)GetPrivateField(writer, "gate")!;
                await gate.WaitAsync(timeout.Token);
                try
                {
                    writerPeer.Writer = writer;
                    await payer.RequestAccelerationNegotiationAsync("runtime_unlock", unlockCancellation.Token);
                    await writerPeer.Arrived.Task.WaitAsync(timeout.Token);
                    Assert.NotNull(GetPrivateField(payer, "outboundAccelerationOfferIdleUnlock"));
                    if (change == "offer_writer_new_transfer")
                    {
                        var stopStamp = GetPrivateField(payer, "accelerationExplicitStopGeneration");
                        Assert.Equal("regular_nkn_v4_fast", (await StartPreparedAdmission(restartHelperPays ? receiver : sender, timeout.Token))!.RouteToken);
                        Assert.Equal(FileTransferAccelerationRequestPurpose.TransferActivation,
                            Assert.IsType<FileTransferAccelerationRequestSelection>(InvokePrivateMethod(payer, "SelectSessionPreparationRequest")).Purpose);
                        Assert.Equal(stopStamp, GetPrivateField(payer, "accelerationExplicitStopGeneration"));
                    }
                    if (change == "offer_writer_stop")
                    {
                        using var drain = (IDisposable)InvokePrivateMethod(payer, "BeginAccelerationLaneStopDrain", true)!;
                        Assert.NotNull(GetPrivateField(payer, "outboundAccelerationOfferNonce"));
                    }
                }
                finally { gate.Release(); }
                Assert.Equal(change == "offer_writer_current", await writerPeer.WriteCompleted.Task.WaitAsync(timeout.Token));
                Assert.Equal(change == "offer_writer_current", bytes.Length > 0);
                writerPeer.Writer = null;
                return;
            }
            await payer.RequestAccelerationNegotiationAsync("runtime_unlock", unlockCancellation.Token);
            var request = GetPrivateField(payer, "idleSessionUnlock")!;
            var requestedAt = (long)request.GetType().GetProperty("RequestedAtUnixMs")!.GetValue(request)!;
            if (offerRetry)
            {
                await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(8), timeout.Token);
                var retry = Assert.IsAssignableFrom<Task>(payer.IdleSessionUnlockRetryForTests);
                Assert.Single(sentOffers);
                Assert.Single(sentIntents);
                Assert.Null(GetPrivateField(payer, "outboundAccelerationOfferNonce"));
                Assert.Null(GetPrivateField(payer, "outboundAccelerationOfferIdleUnlock"));
                if (change!.EndsWith("_cancel", StringComparison.Ordinal)) unlockCancellation.Cancel();
                if (change.EndsWith("_new_transfer", StringComparison.Ordinal))
                    Assert.Equal("regular_nkn_v4_fast", (await StartPreparedAdmission(sender, timeout.Token))!.RouteToken);
                if (change.EndsWith("_key_reset", StringComparison.Ordinal)) InvokePrivateMethod(payer, "ResetControlSecureState");
                NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousAnswerTimeout;
                releaseRetry.TrySetResult();
                await retry.WaitAsync(timeout.Token);
                if (change.EndsWith("_cancel", StringComparison.Ordinal) ||
                    change.EndsWith("_new_transfer", StringComparison.Ordinal) ||
                    change.EndsWith("_key_reset", StringComparison.Ordinal))
                {
                    Assert.Single(sentOffers);
                    Assert.Single(sentIntents);
                    Assert.DoesNotContain(MsgType.TransportAccelerationAnswerAck, messages);
                    Assert.False(host.IsAccelerationAvailableForTests);
                    Assert.False(helper.IsAccelerationAvailableForTests);
                    Assert.Null(GetPrivateField(payer, "outboundRuntimeUnlockOfferAuthorityBoundary"));
                    return;
                }
            }
            if (delayStop) Assert.DoesNotContain(MsgType.TransportAccelerationPayerIntent, messages);
            if (failSend) await failedSend.Task.WaitAsync(TimeSpan.FromSeconds(3), timeout.Token);
            if (change?.EndsWith("new_stop", StringComparison.Ordinal) == true)
                await payer.StopAccelerationAsync("header_switch_off", timeout.Token);
            if (change == "delayed_stop_key_reset") InvokePrivateMethod(payer, "ResetControlSecureState");
            if (change == "delayed_stop_new_transfer")
                Assert.Equal("regular_nkn_v4_fast", (await StartPreparedAdmission(sender, timeout.Token))!.RouteToken);
            if (change == "delayed_stop_late_marker")
                InvokePrivateMethod(payer, "MarkAccelerationPeerUserStoppedForCurrentSession", new object?[] { null });
            if (change == "first_send_failure_peer_stop")
            {
                await WaitUntilAsync(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > requestedAt, TimeSpan.FromSeconds(1));
                InvokePrivateMethod(restartHelperPays ? host : helper,
                    "MarkAccelerationUserStoppedForCurrentSession", new object?[] { null });
            }
            releaseStop.TrySetResult();
            if (delayStop)
                await WaitUntilAsync(() => (int)GetPrivateField(payer, "accelerationLaneStopDrains")! == 0,
                    TimeSpan.FromSeconds(3));
            if (change is "delayed_stop_new_stop" or "delayed_stop_key_reset" or "delayed_stop_new_transfer" or "first_send_failure_new_stop")
            {
                await WaitUntilAsync(() => (int)GetPrivateField(payer, "accelerationNegotiationScheduled")! == 0,
                    TimeSpan.FromSeconds(3));
                Assert.False(host.IsAccelerationAvailableForTests);
                Assert.False(helper.IsAccelerationAvailableForTests);
                Assert.DoesNotContain(MsgType.TransportAccelerationOffer, messages);
                Assert.DoesNotContain(MsgType.TransportAccelerationAnswerAck, messages);
                Assert.Null(GetPrivateField(payer, "outboundRuntimeUnlockOfferAuthorityBoundary"));
                return;
            }
            await answerHeld.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
            if (failSend || offerRetry)
            {
                Assert.True(sentIntents.Count >= 2);
                foreach (var intent in sentIntents.Values)
                {
                    Assert.Equal("session_ready_after_user_unlock", intent.Trigger);
                    Assert.Equal(requestedAt, intent.SentAtUnixMs);
                    Assert.Equal(sentIntents.Values.First().ExpiresAtUnixMs, intent.ExpiresAtUnixMs);
                }
            }
            if (offerRetry)
            {
                Assert.Equal(2, sentOffers.Count);
                var binding = GetPrivateField(payer, "outboundAccelerationOfferIdleUnlock")!;
                Assert.Same(request, binding.GetType().GetProperty("Request")!.GetValue(binding));
                foreach (var offer in sentOffers.Values)
                {
                    Assert.Equal("session_ready_after_user_unlock", offer.Trigger);
                    Assert.Equal(requestedAt, offer.SentAtUnixMs);
                    Assert.Equal(sentOffers.Values.First().ExpiresAtUnixMs, offer.ExpiresAtUnixMs);
                }
            }
            var commitRace = change?.StartsWith("answer_commit_", StringComparison.Ordinal) == true;
            var physicalAckRace = change?.StartsWith("answer_physical_", StringComparison.Ordinal) == true;
            var ackRace = physicalAckRace || change?.StartsWith("answer_ack_", StringComparison.Ordinal) == true;
            var retiredAnswer = change?.StartsWith("retired_answer_", StringComparison.Ordinal) == true;
            if (commitRace || ackRace || retiredAnswer)
            {
                var binding = GetPrivateField(payer, "outboundAccelerationOfferIdleUnlock")!;
                var peerClient = restartHelperPays ? hostClient : helperClient;
                if (retiredAnswer)
                {
                    // Exercise the existing retirement transition without starting a
                    // second handshake: its delayed answer must retain the exact intent.
                    lock (GetPrivateField(payer, "accelerationGate")!)
                        InvokePrivateMethod(payer, "RetireOutboundAccelerationOfferLocked",
                            ((FileTransferSessionPreparationSecurityEpoch)request.GetType().GetProperty("Epoch")!.GetValue(request)!).SessionId,
                            "accepted_peer_offer", 0L);
                    Assert.Null(GetPrivateField(payer, "outboundAccelerationOfferIdleUnlock"));
                    Assert.Same(binding, GetPrivateField(payer, "retiredAccelerationOfferIdleUnlock"));
                }
                if (commitRace)
                    payer.IdleSessionBeforeAnswerCommitForTests = () =>
                    {
                        answerCommitEntered.TrySetResult();
                        releaseAnswerCommit.Wait(timeout.Token);
                    };
                if (ackRace)
                    payer.IdleSessionBeforeAnswerAckForTests = async () =>
                    {
                        answerAckEntered.TrySetResult();
                        await releaseAnswerAck.Task.WaitAsync(timeout.Token);
                    };

                async Task InvalidateAnswerAsync()
                {
                    if (change!.EndsWith("_stop", StringComparison.Ordinal))
                        await payer.StopAccelerationAsync("header_switch_off", timeout.Token);
                    if (change.EndsWith("_cancel", StringComparison.Ordinal)) unlockCancellation.Cancel();
                    if (change.EndsWith("_key_reset", StringComparison.Ordinal)) InvokePrivateMethod(payer, "ResetControlSecureState");
                    if (change.EndsWith("_new_transfer", StringComparison.Ordinal))
                        Assert.Equal("regular_nkn_v4_fast", (await StartPreparedAdmission(sender, timeout.Token))!.RouteToken);
                }

                if (retiredAnswer) await InvalidateAnswerAsync();
                var handling = Task.Run(() =>
                {
                    if (change == "retired_answer_rejected_cancel")
                    {
                        var decoded = SessionSecureEnvelopeCodec.Decrypt(
                            (byte[])InvokePrivateMethod(payer, "GetControlSessionSharedKeyOrThrow")!, heldAnswer!.Payload);
                        var answer = JsonSerializer.Deserialize<TransportAccelerationAnswerPayload>(decoded.Plaintext)!;
                        answer.Accepted = false;
                        answer.RejectReason = "sidecar_unavailable";
                        InvokePrivateMethod(payer, "HandleValidatedTransportAccelerationAnswer",
                            peerClient.Address, heldAnswer, answer, null);
                    }
                    else InvokePrivateMethod(payer, "HandleTransportAccelerationAnswer", peerClient.Address, heldAnswer!);
                });
                if (writerAckRace)
                {
                    await answerAckEntered.Task.WaitAsync(timeout.Token);
                    await handling.WaitAsync(timeout.Token);
                    Assert.NotNull(GetPrivateField(payer, "acceptedIdleSessionAnswer"));
                    var writerPeer = Assert.IsType<WriterGatePeer>(payerClient);
                    using var buffer = new MemoryStream();
                    using var writer = new BridgeStdioWriter(buffer, leaveOpen: true);
                    var gate = (SemaphoreSlim)GetPrivateField(writer, "gate")!;
                    void StopIdle()
                    {
                        using var drain = (IDisposable)InvokePrivateMethod(payer, "BeginAccelerationLaneStopDrain", true)!;
                    }
                    await gate.WaitAsync(timeout.Token);
                    try
                    {
                        writerPeer.Writer = writer;
                        if (change == "answer_ack_writer_stop_after") writerPeer.AfterFrameWritten = StopIdle;
                        releaseAnswerAck.TrySetResult();
                        await writerPeer.Arrived.Task.WaitAsync(timeout.Token);
                        Assert.Equal(fixedWriter, payer.AccelerationNegotiatedLanesForTests != NknAccelerationLaneKind.None);
                        if (change == "answer_ack_writer_stop_before") StopIdle();
                        if (change == "answer_ack_writer_fixed_replaced")
                        {
                            var old = helperLane.CaptureCurrentLaneLease();
                            Assert.True(await helperLane.EnsureListenerSidecarConnectedAsync(hostClient.Address, timeout.Token));
                            Assert.NotEqual(old, helperLane.CaptureCurrentLaneLease());
                            Assert.False(payer.IsAccelerationAvailableForTests);
                        }
                    }
                    finally { gate.Release(); }
                    var published = await Assert.IsAssignableFrom<Task<bool>>(payer.IdleSessionAnswerAckForTests).WaitAsync(timeout.Token);
                    var expectedWrite = change is not ("answer_ack_writer_stop_before" or "answer_ack_writer_fixed_replaced");
                    Assert.Equal(expectedWrite, await writerPeer.WriteCompleted.Task.WaitAsync(timeout.Token));
                    Assert.Equal(expectedWrite, buffer.Length > 0);
                    if (fixedWriter)
                    {
                        // Fixed lanes may admit the queue before the write. Only the actual frame proves a write.
                        Assert.True(helperLane.IsLocalPaidListenerActive);
                        Assert.Equal(expectedWrite, payer.IsAccelerationAvailableForTests);
                    }
                    else
                    {
                        Assert.Equal(change == "answer_ack_writer_current", published);
                        Assert.Equal(published, payer.AccelerationNegotiatedLanesForTests != NknAccelerationLaneKind.None);
                    }
                    writerPeer.Writer = null;
                    return;
                }
                if (physicalAckRace)
                {
                    await answerAckEntered.Task.WaitAsync(timeout.Token);
                    await handling.WaitAsync(timeout.Token);
                    var sendGate = Assert.IsType<SemaphoreSlim>(GetPrivateField(payer, "outboundSendGate"));
                    await sendGate.WaitAsync(timeout.Token);
                    var directCopiesEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var releaseDirectCopies = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var directCopies = 0;
                    var bulkCopies = 0;
                    payerClient.BeforeSendCoreAsync = async (_, bytes, lane, ct) =>
                    {
                        if (!EnvelopeCodec.TryDeserialize(bytes, out var envelope) || envelope.Type != MsgType.TransportAccelerationAnswerAck) return;
                        if (lane == NknBridgeChannel.Bulk) Interlocked.Increment(ref bulkCopies);
                        if (lane == NknBridgeChannel.Control && Interlocked.Increment(ref directCopies) <= 2)
                        {
                            if (Volatile.Read(ref directCopies) == 2) directCopiesEntered.TrySetResult();
                            await releaseDirectCopies.Task.WaitAsync(ct);
                            throw new InvalidOperationException("test_direct_ack_copy_failed");
                        }
                    };
                    try
                    {
                        releaseAnswerAck.TrySetResult();
                        await directCopiesEntered.Task.WaitAsync(timeout.Token);
                        await Assert.IsAssignableFrom<Task>(payer.IdleSessionAnswerAckForTests).WaitAsync(timeout.Token);
                        Assert.Equal(2, messages.Count(type => type == MsgType.TransportAccelerationAnswerAck));
                        await InvalidateAnswerAsync();
                        if (change == "answer_physical_replace")
                            lock (GetPrivateField(payer, "accelerationGate")!) SetPrivateField(payer, "acceptedIdleSessionAnswer", null);
                        if (change == "answer_physical_expire")
                            request.GetType().GetProperty("RequestedAtUnixMs")!.SetValue(request, DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeMilliseconds());
                    }
                    finally
                    {
                        sendGate.Release();
                        releaseDirectCopies.TrySetResult();
                    }
                    var sends = Assert.IsType<Task[]>(payer.IdleSessionAnswerAckSendsForTests);
                    try { await Task.WhenAll(sends).WaitAsync(timeout.Token); }
                    catch (OperationCanceledException) when (change == "answer_physical_cancel" && !timeout.IsCancellationRequested) { }
                    Assert.All(sends, task => Assert.False(task.IsFaulted));
                    if (change == "answer_physical_current")
                    {
                        Assert.True(Volatile.Read(ref bulkCopies) > 0);
                        await WaitUntilAsync(() => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                            TimeSpan.FromSeconds(3));
                    }
                    else
                    {
                        Assert.Equal(2, messages.Count(type => type == MsgType.TransportAccelerationAnswerAck));
                        Assert.Equal(0, Volatile.Read(ref bulkCopies));
                        Assert.False((restartHelperPays ? host : helper).IsAccelerationAvailableForTests);
                    }
                    foreach (var (transport, retained) in new[] { host, helper }.Zip(retainedPublications))
                    {
                        var map = (System.Collections.IDictionary)GetPrivateField(transport, "fileTransferRoutePublicationAuthorities")!;
                        Assert.Equal(retained.Count, map.Count);
                        foreach (var (key, value) in retained) Assert.Equal(value, map[key]);
                    }
                    await handling.WaitAsync(timeout.Token);
                    return;
                }
                try
                {
                    if (commitRace)
                    {
                        await answerCommitEntered.Task.WaitAsync(timeout.Token);
                        await InvalidateAnswerAsync();
                    }
                    if (ackRace)
                    {
                        await answerAckEntered.Task.WaitAsync(timeout.Token);
                        if (change == "answer_ack_new_transfer")
                        {
                            // Admit on the already-ready payer before its ACK task can run.
                            Assert.True(payer.IsAccelerationAvailableForTests);
                            Assert.Equal("file_tuna_v4", (await StartPreparedAdmission(restartHelperPays ? receiver : sender, timeout.Token))!.RouteToken);
                            Assert.Equal(FileTransferAccelerationRequestPurpose.TransferActivation,
                                Assert.IsType<FileTransferAccelerationRequestSelection>(InvokePrivateMethod(payer, "SelectSessionPreparationRequest")).Purpose);
                        }
                        else await InvalidateAnswerAsync();
                    }
                }
                finally
                {
                    releaseAnswerCommit.Set();
                    releaseAnswerAck.TrySetResult();
                }
                await handling.WaitAsync(timeout.Token);
                if (ackRace) await Assert.IsAssignableFrom<Task>(payer.IdleSessionAnswerAckForTests).WaitAsync(timeout.Token);
                if (change == "answer_ack_new_transfer")
                {
                    await WaitUntilAsync(() => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                        TimeSpan.FromSeconds(3));
                    Assert.Contains(MsgType.TransportAccelerationAnswerAck, messages);
                    var admitted = (restartHelperPays ? receiver : sender).Snapshot.Outbound!;
                    Assert.Equal("prepared-admission", admitted.TransferId);
                    Assert.Equal("file_tuna_v4", admitted.RouteToken);
                    Assert.Null(GetPrivateField(payer, "outboundRuntimeUnlockOfferAuthorityBoundary"));
                    Assert.Null(GetPrivateField(payer, "runtimeUnlockAcceptedPreCommitSnapshot"));
                    return;
                }
                if (change != "retired_answer_current")
                {
                    Assert.DoesNotContain(MsgType.TransportAccelerationAnswerAck, messages);
                    Assert.Null(GetPrivateField(payer, "pendingAccelerationAnswerAckReplyShadow"));
                    Assert.Null(payer.IdleSessionUnlockRetryForTests);
                    if (!ackRace)
                    {
                        Assert.False(host.IsAccelerationAvailableForTests);
                        Assert.False(helper.IsAccelerationAvailableForTests);
                    }
                    if (change!.EndsWith("_stop", StringComparison.Ordinal))
                        Assert.False(payer.IsAccelerationAvailableForTests);
                    foreach (var (transport, retained) in new[] { host, helper }.Zip(retainedPublications))
                    {
                        Assert.Null(GetPrivateField(transport, "outboundRuntimeUnlockOfferAuthorityBoundary"));
                        Assert.Null(GetPrivateField(transport, "runtimeUnlockAcceptedPreCommitSnapshot"));
                        if (!change.EndsWith("_new_transfer", StringComparison.Ordinal))
                        {
                            var map = (System.Collections.IDictionary)GetPrivateField(transport, "fileTransferRoutePublicationAuthorities")!;
                            Assert.Equal(retained.Count, map.Count);
                            foreach (var (key, value) in retained) Assert.Equal(value, map[key]);
                        }
                    }
                    return;
                }
            }
            if (change == "first_send_failure_peer_stop")
            {
                Assert.NotNull(GetPrivateField(payer, "outboundAccelerationOfferNonce"));
                releaseAnswer.TrySetResult();
                await WaitUntilAsync(() => GetPrivateField(payer, "outboundAccelerationOfferNonce") is null,
                    TimeSpan.FromSeconds(3));
                Assert.False(host.IsAccelerationAvailableForTests);
                Assert.False(helper.IsAccelerationAvailableForTests);
                Assert.DoesNotContain(MsgType.TransportAccelerationAnswerAck, messages);
                return;
            }
            if (change != "retired_answer_current")
            {
                Assert.False(host.IsAccelerationAvailableForTests);
                Assert.False(helper.IsAccelerationAvailableForTests);
            }
            if (stopBeforeAnswer)
            {
                await payer.StopAccelerationAsync("header_switch_off", timeout.Token);
                InvokePrivateMethod(payer, "HandleTransportAccelerationAnswer",
                    (restartHelperPays ? hostClient : helperClient).Address, heldAnswer!);
                Assert.False(host.IsAccelerationAvailableForTests);
                Assert.False(helper.IsAccelerationAvailableForTests);
                Assert.DoesNotContain(MsgType.TransportAccelerationAnswerAck, messages);
            }
            else
            {
                releaseAnswer.TrySetResult();
                await WaitUntilAsync(() => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                    TimeSpan.FromSeconds(5));
                Assert.Contains(MsgType.TransportAccelerationAnswerAck, messages);
                Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
                Assert.Equal(host.AccelerationNegotiatedLanesForTests, helper.AccelerationNegotiatedLanesForTests);
            }
            Assert.Contains(MsgType.TransportAccelerationOffer, messages);
            Assert.DoesNotContain(MsgType.SessionPreparationOffer, messages);
            Assert.Same(hostPreparation, GetPrivateField(sender, "sessionPreparation"));
            Assert.Same(helperPreparation, GetPrivateField(receiver, "sessionPreparation"));
            Assert.Equal(terminalState, sender.Snapshot.Outbound!.State);
            Assert.Equal(terminalState, receiver.Snapshot.Inbound!.State);
            foreach (var (transport, retained) in new[] { host, helper }.Zip(retainedPublications))
            {
                var map = (System.Collections.IDictionary)GetPrivateField(transport, "fileTransferRoutePublicationAuthorities")!;
                Assert.Equal(retained.Count, map.Count);
                foreach (var (key, value) in retained) Assert.Equal(value, map[key]);
                Assert.Null(GetPrivateField(transport, "outboundRuntimeUnlockOfferAuthorityBoundary"));
                Assert.Null(GetPrivateField(transport, "runtimeUnlockAcceptedPreCommitSnapshot"));
            }
            if (!stopBeforeAnswer)
            {
                // A new file still needs its own explicit Coordinator admission.
                Assert.Equal("file_tuna_v4", (await StartPreparedAdmission(sender, timeout.Token))!.RouteToken);
                await WaitUntilAsync(() => receiver.Snapshot.Inbound?.TransferId == "prepared-admission" &&
                    receiver.Snapshot.Inbound.RouteToken == "file_tuna_v4", TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            releaseAnswerCommit.Set();
            releaseAnswerAck.TrySetResult();
            releasePublication.Set();
            releaseRetry.TrySetResult();
            releaseStop.TrySetResult();
            releaseAnswer.TrySetResult();
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousPriority;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousAnswerTimeout;
            FakeNknClient.ResetNetwork();
        }
    }

    [Theory]
    [InlineData("session_ready_after_user_unlock", 0, true)]
    [InlineData("session_ready_after_user_unlock", 1, true)]
    [InlineData("session_ready_after_user_unlock", -1, false)]
    [InlineData("session_ready_after_user_unlock", long.MinValue, false)]
    [InlineData("session_security_state_ready", 1, false)]
    [InlineData("runtime_unlock", 1, true)]
    [Trait("FileTransferInfraSuite", "IdleSessionReestablishment")]
    public Task IdleSession_ExplicitPeerUnlockPreservesStopFreshness(string trigger, long offset, bool allowed)
        => WithPublicPreparationPairAsync(false, pair =>
        {
            InvokePrivateMethod(pair.Payer, "MarkAccelerationUserStoppedForCurrentSession", new object?[] { null });
            var stoppedAt = (long)GetPrivateField(pair.Payer, "accelerationUserStoppedUtcMs")!;
            var cleared = Assert.IsType<bool>(InvokePrivateMethod(pair.Payer,
                "ClearAccelerationUserStoppedForFreshPeerMessage", "offer", trigger,
                offset == long.MinValue ? 0L : stoppedAt + offset, null, null));
            Assert.Equal(allowed, cleared);
            Assert.Equal(!allowed, pair.Payer.IsAccelerationUserStoppedForCurrentSessionForTests);
            Assert.Empty(pair.Sent);
            Assert.Null(GetPrivateField(pair.Payer, "outboundRuntimeUnlockOfferAuthorityBoundary"));
            Assert.Null(GetPrivateField(pair.Payer, "runtimeUnlockAcceptedPreCommitSnapshot"));
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [Trait("FileTransferInfraSuite", "IdleSession")]
    public async Task IdleSession_UnlockNegotiatesBeforeFileOrScreenStarts(bool helperPays, bool allowFiles, bool stopBeforeAnswer)
    {
        FakeNknClient.ResetNetwork();
        var previousPriority = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var releaseAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Envelope? heldAnswer = null;
        try
        {
            var hostClient = new FakeNknClient("host.idle-tuna.address");
            var helperClient = new FakeNknClient("helper.idle-tuna.address");
            using var hostLane = new RetryableTunaAccelerationSession(!helperPays, 0) { StrictPayerHandoffForTests = true };
            using var helperLane = new RetryableTunaAccelerationSession(helperPays, 0) { StrictPayerHandoffForTests = true };
            var tunaSends = 0;
            foreach (var lane in new[] { hostLane, helperLane })
                lane.ForwardBytesForTests = (_, _, _) =>
                {
                    Interlocked.Increment(ref tunaSends);
                    return Task.FromResult(true);
                };
            var options = NknTransportOptions.Load();
            using var host = new NknSignalingTransport(hostClient, options,
                new NknIdentity("host-idle-tuna", hostClient.Address), NknTunaAccelerationOptions.Disabled, hostLane);
            using var helper = new NknSignalingTransport(helperClient, options,
                new NknIdentity("helper-idle-tuna", helperClient.Address), NknTunaAccelerationOptions.Disabled, helperLane);
            using var hostFiles = new SessionFileTransferService();
            using var helperFiles = new SessionFileTransferService();
            hostFiles.AttachTransport(host);
            helperFiles.AttachTransport(helper);
            await ApproveNknSessionAsync(host, helper, timeout.Token,
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare |
                (allowFiles ? InviteCapabilities.FileTransfer : InviteCapabilities.None),
                suppressAutomaticAccelerationNegotiation: true);
            var messages = new ConcurrentQueue<MsgType>();
            foreach (var client in new[] { hostClient, helperClient })
                client.BeforeSendAsync = async (_, bytes, ct) =>
                {
                    if (!EnvelopeCodec.TryDeserialize(bytes, out var envelope)) return;
                    messages.Enqueue(envelope.Type);
                    if (envelope.Type == MsgType.TransportAccelerationAnswer)
                    {
                        heldAnswer = envelope;
                        answerHeld.TrySetResult();
                        await releaseAnswer.Task.WaitAsync(ct);
                    }
                };

            await (helperPays ? helper : host).RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token);
            await answerHeld.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
            Assert.False(host.IsAccelerationAvailableForTests);
            Assert.False(helper.IsAccelerationAvailableForTests);
            if (stopBeforeAnswer)
            {
                await (helperPays ? helper : host).StopAccelerationAsync("header_switch_off", timeout.Token);
                // Deliver the authenticated late answer directly at its handler
                // boundary; do not infer processing from a redacted log string.
                InvokePrivateMethod(helperPays ? helper : host, "HandleTransportAccelerationAnswer",
                    (helperPays ? hostClient : helperClient).Address, heldAnswer!);
            }
            if (stopBeforeAnswer)
            {
                Assert.False(host.IsAccelerationAvailableForTests);
                Assert.False(helper.IsAccelerationAvailableForTests);
                Assert.DoesNotContain(MsgType.TransportAccelerationAnswerAck, messages);
            }
            else
            {
                releaseAnswer.TrySetResult();
                await WaitUntilAsync(() => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                    TimeSpan.FromSeconds(5));
                var lanes = NknAccelerationLaneKind.Screen | (allowFiles ? NknAccelerationLaneKind.File : NknAccelerationLaneKind.None);
                Assert.Equal(lanes, host.AccelerationNegotiatedLanesForTests);
                Assert.Equal(lanes, helper.AccelerationNegotiatedLanesForTests);
                Assert.Contains(MsgType.TransportAccelerationAnswerAck, messages);
            }

            Assert.Contains(MsgType.TransportAccelerationOffer, messages);
            Assert.Contains(MsgType.TransportAccelerationAnswer, messages);
            Assert.DoesNotContain(MsgType.SessionPreparationOffer, messages);
            Assert.Equal(1, (helperPays ? helperLane : hostLane).EnsureListenerCalls);
            Assert.Equal(1, (helperPays ? hostLane : helperLane).StartDialerCalls);
            foreach (var service in new[] { hostFiles, helperFiles })
            {
                Assert.Null(service.Snapshot.Inbound);
                Assert.Null(service.Snapshot.Outbound);
            }
            foreach (var transport in new[] { host, helper })
            {
                Assert.Empty((System.Collections.IDictionary)GetPrivateField(transport, "fileTransferRoutePublicationAuthorities")!);
                Assert.Null(GetPrivateField(transport, "outboundRuntimeUnlockOfferAuthorityBoundary"));
                Assert.Null(GetPrivateField(transport, "runtimeUnlockAcceptedPreCommitSnapshot"));
            }
            Assert.Equal(0, Volatile.Read(ref tunaSends));
            if (allowFiles && !stopBeforeAnswer)
            {
                // Only a subsequent explicit admission may publish a file route.
                Assert.Equal("file_tuna_v4", (await StartPreparedAdmission(hostFiles, timeout.Token))!.RouteToken);
                await WaitUntilAsync(() => helperFiles.Snapshot.Inbound?.RouteToken == "file_tuna_v4", TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            releaseAnswer.TrySetResult();
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousPriority;
            FakeNknClient.ResetNetwork();
        }
    }
}
