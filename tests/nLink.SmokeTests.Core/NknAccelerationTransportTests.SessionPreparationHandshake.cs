using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationHandshake")]
    public Task PreparationHandshake_BothPayerRolesCompleteOnlyReadinessAndCoalesce(bool helperPays)
        => WithPreparationHandshakePairAsync(helperPays, async pair =>
        {
            Assert.True(await pair.Start().WaitAsync(pair.Token));
            await pair.WaitReady();
            var payer = pair.State(pair.PayerService);
            var dialer = pair.State(pair.DialerService);
            Assert.True(payer.Readiness!.Proof.AnswerReceived);
            Assert.True(payer.Readiness.Proof.AnswerAckDispatched);
            Assert.False(payer.Readiness.Proof.AnswerAckReceived);
            Assert.True(dialer.Readiness!.Proof.AnswerAckReceived);
            Assert.False(dialer.Readiness.Proof.AnswerReceived);
            Assert.Equal(payer.Permit!.Nonce, dialer.Permit!.Nonce);
            Assert.True(await pair.Start().WaitAsync(pair.Token));
            Assert.Same(payer, pair.State(pair.PayerService));
            Assert.Same(dialer, pair.State(pair.DialerService));
            Assert.Equal(new[] { MsgType.SessionPreparationOffer, MsgType.SessionPreparationAnswer,
                MsgType.SessionPreparationAnswerAck }, pair.Sent.ToArray());
            Assert.Equal(1, pair.PayerLane.Starts);
            Assert.Equal(1, pair.DialerLane.Starts);
            pair.AssertNoFileEffects();
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationHandshake")]
    public Task PreparationHandshake_HeldAnswerOrAckIsNotReadiness(bool helperPays, bool ack)
        => WithPreparationHandshakePairAsync(helperPays, async pair =>
        {
            var barrier = pair.Hold(ack ? MsgType.SessionPreparationAnswerAck : MsgType.SessionPreparationAnswer);
            var start = pair.Start();
            await barrier.Entered.Task.WaitAsync(pair.Token);
            Assert.Null(pair.State(pair.PayerService).Readiness);
            Assert.Null(pair.State(pair.DialerService).Readiness);
            pair.AssertNoFileEffects();
            barrier.Release.TrySetResult();
            Assert.True(await start.WaitAsync(pair.Token));
            await pair.WaitReady();
            pair.AssertNoFileEffects();
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationHandshake")]
    public Task PreparationHandshake_AckDispatchDoesNotProveRemoteReceipt(bool helperPays)
        => WithPreparationHandshakePairAsync(helperPays, async pair =>
        {
            pair.PayerClient.ShouldDeliverSendAsync = (_, bytes, _) => Task.FromResult(
                !EnvelopeCodec.TryDeserialize(bytes, out var envelope) || envelope.Type != MsgType.SessionPreparationAnswerAck);
            Assert.True(await pair.Start().WaitAsync(pair.Token));
            Assert.NotNull(pair.State(pair.PayerService).Readiness);
            Assert.Null(pair.State(pair.DialerService).Readiness);
            pair.AssertNoFileEffects();
        });

    [Theory]
    [InlineData(false, "reset")]
    [InlineData(true, "reset")]
    [InlineData(false, "detach")]
    [InlineData(true, "detach")]
    [InlineData(false, "key_reset")]
    [InlineData(true, "key_reset")]
    [InlineData(false, "admission")]
    [InlineData(true, "admission")]
    [InlineData(false, "stop")]
    [InlineData(true, "stop")]
    [InlineData(false, "lease")]
    [InlineData(true, "lease")]
    [Trait("FileTransferInfraSuite", "SessionPreparationHandshake")]
    public Task PreparationHandshake_LateAnswerCannotCompleteRetiredPayer(bool helperPays, string change)
        => WithPreparationHandshakePairAsync(helperPays, async pair =>
        {
            var barrier = pair.Hold(MsgType.SessionPreparationAnswer);
            var start = pair.Start();
            await barrier.Entered.Task.WaitAsync(pair.Token);
            var original = pair.PayerLane.Current!;
            switch (change)
            {
                case "reset": pair.PayerService.ResetSessionState(); break;
                case "detach": pair.PayerService.DetachTransport(); break;
                case "key_reset": InvokePrivateMethod(pair.Payer, "ResetControlSecureState"); break;
                case "admission":
                    await pair.PayerService.TryStartSendAsync(new("first.bin", 4, "first-admission"),
                        _ => Task.FromResult<Stream>(new MemoryStream(new byte[4])), pair.Token);
                    break;
                case "stop": await pair.Payer.StopAccelerationAsync("user_locked", pair.Token); break;
                case "lease": pair.PayerLane.Current = original with { ClientGeneration = 2 }; break;
            }
            barrier.Release.TrySetResult();
            Assert.False(await start.WaitAsync(pair.Token));
            Assert.Null(pair.State(pair.PayerService).Readiness);
            Assert.DoesNotContain(MsgType.SessionPreparationAnswerAck, pair.Sent);
            if (change == "lease")
            {
                Assert.Equal(2, pair.PayerLane.Current!.ClientGeneration);
                Assert.Equal(0, pair.PayerLane.ExactStops);
            }
            if (change != "admission") pair.AssertNoFileEffects(expectedGlobalStops: change == "stop" ? 1 : 0);
            else Assert.NotEqual("file_tuna_v4", pair.PayerService.Snapshot.Outbound?.RouteToken);
        });

    [Theory]
    [InlineData(false, "reset")]
    [InlineData(true, "reset")]
    [InlineData(false, "admission")]
    [InlineData(true, "admission")]
    [Trait("FileTransferInfraSuite", "SessionPreparationHandshake")]
    public Task PreparationHandshake_LateAckCannotCompleteRetiredDialer(bool helperPays, string change)
        => WithPreparationHandshakePairAsync(helperPays, async pair =>
        {
            var barrier = pair.Hold(MsgType.SessionPreparationAnswerAck);
            var start = pair.Start();
            await barrier.Entered.Task.WaitAsync(pair.Token);
            if (change == "reset") pair.DialerService.ResetSessionState();
            else
            {
                await pair.DialerService.TryStartSendAsync(new("first.bin", 4, "dialer-admission"),
                    _ => Task.FromResult<Stream>(new MemoryStream(new byte[4])), pair.Token);
                // The real offer also reserves inbound admission at the payer.
                await WaitUntilAsync(() => pair.State(pair.PayerService).Admission is not null, TimeSpan.FromSeconds(3));
            }
            barrier.Release.TrySetResult();
            Assert.Equal(change == "reset", await start.WaitAsync(pair.Token));
            Assert.Null(pair.State(pair.DialerService).Readiness);
            if (change != "admission") pair.AssertNoFileEffects();
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationHandshake")]
    public Task PreparationHandshake_LaneEventsStayIsolatedAndStaleOwnerCannotRetireCurrent(bool helperPays, bool stale)
        => WithPreparationHandshakePairAsync(helperPays, async pair =>
        {
            Assert.True(await pair.Start().WaitAsync(pair.Token));
            await pair.WaitReady();
            var current = pair.State(pair.PayerService);
            pair.PayerLane.Emit(false, stale ? "old-preparation-owner" : pair.PayerLane.Request!.ExecutionIdentity);
            if (stale) Assert.Same(current, pair.State(pair.PayerService));
            else
            {
                await pair.PayerLane.Stopped.Task.WaitAsync(pair.Token);
                await WaitUntilAsync(() => pair.State(pair.PayerService).Readiness is null, TimeSpan.FromSeconds(3));
            }
            pair.AssertNoFileEffects();
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationHandshake")]
    public Task PreparationHandshake_UnsupportedPeerNeverDowngrades(bool helperPays)
        => WithPreparationHandshakePairAsync(helperPays, async pair =>
        {
            pair.DialerService.DetachTransport();
            var offerSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.PayerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) && envelope.Type == MsgType.SessionPreparationOffer)
                    offerSent.TrySetResult();
                return Task.FromResult(true);
            };
            using var cancel = new CancellationTokenSource();
            var start = pair.Start(cancel.Token);
            await offerSent.Task.WaitAsync(pair.Token);
            cancel.Cancel();
            Assert.False(await start.WaitAsync(pair.Token));
            Assert.Equal(new[] { MsgType.SessionPreparationOffer }, pair.Sent.ToArray());
            Assert.Equal(0, pair.DialerLane.Starts);
            pair.AssertNoFileEffects();
        });

    private static async Task WithPreparationHandshakePairAsync(bool helperPays, Func<PreparationHandshakePair, Task> test)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var pair = new PreparationHandshakePair(helperPays);
            await pair.Approve();
            await test(pair);
        }
        finally { FakeNknClient.ResetNetwork(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationHandshake")]
    public Task PreparationHandshake_QueuedOfferCannotDispatchAfterRetirement(bool helperPays)
        => WithPreparationHandshakePairAsync(helperPays, async pair =>
        {
            var sendGate = Assert.IsType<SemaphoreSlim>(GetPrivateField(pair.Payer, "outboundSendGate"));
            await sendGate.WaitAsync(pair.Token);
            try
            {
                var start = pair.Start();
                Assert.False(start.IsCompleted);
                pair.PayerService.ResetSessionState();
                Assert.False(await start.WaitAsync(pair.Token));
                Assert.Empty(pair.Sent);
                Assert.Equal(0, pair.DialerLane.Starts);
                pair.AssertNoFileEffects();
            }
            finally { sendGate.Release(); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationHandshake")]
    public Task PreparationHandshake_FailedAckDispatchCannotCompleteEitherPeer(bool helperPays)
        => WithPreparationHandshakePairAsync(helperPays, async pair =>
        {
            pair.PayerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
                EnvelopeCodec.TryDeserialize(bytes, out var envelope) && envelope.Type == MsgType.SessionPreparationAnswerAck
                    ? Task.FromException<bool>(new IOException("test_ack_send_failure")) : Task.FromResult(true);
            Assert.False(await pair.Start().WaitAsync(pair.Token));
            Assert.Null(pair.State(pair.PayerService).Readiness);
            Assert.Null(pair.State(pair.DialerService).Readiness);
            pair.AssertNoFileEffects();
        });

    private sealed class PreparationHandshakeBarrier
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "SessionPreparationHandshake")]
    public Task PreparationHandshake_SendCompletionCannotSupplyFallbackControlEvidence()
        => WithPreparationHandshakePairAsync(false, pair =>
        {
            var stateType = typeof(NknSignalingTransport).GetNestedType("TunaFallbackProofState", BindingFlags.NonPublic)!;
            var state = Activator.CreateInstance(stateType)!;
            stateType.GetProperty("SessionId")!.SetValue(state, pair.Permit.Scope.SessionId);
            typeof(NknSignalingTransport).GetField("tunaFallbackProofState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(pair.Payer, state);
            foreach (var type in new[] { MsgType.SessionPreparationOffer, MsgType.SessionPreparationAnswer, MsgType.SessionPreparationAnswerAck })
                InvokePrivateMethod(pair.Payer, "RecordTunaFallbackNknControlSent", type);
            Assert.Equal(0L, stateType.GetProperty("ControlNknMessagesSent")!.GetValue(state));
            InvokePrivateMethod(pair.Payer, "RecordTunaFallbackNknControlSent", MsgType.Chat);
            Assert.Equal(1L, stateType.GetProperty("ControlNknMessagesSent")!.GetValue(state));
            return Task.CompletedTask;
        });

    private sealed class PreparationHandshakePair : IDisposable
    {
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(15));
        private readonly NknSignalingTransport host, helper;
        private readonly List<PreparationHandshakeBarrier> barriers = [];
        public CancellationToken Token => lifetime.Token;
        public readonly SessionFileTransferService PayerService = new(), DialerService = new();
        public readonly PreparationHandshakeLane PayerLane = new(), DialerLane = new();
        public readonly FakeNknClient PayerClient, DialerClient;
        public readonly NknSignalingTransport Payer, Dialer;
        public readonly ConcurrentQueue<MsgType> Sent = new();
        public readonly ConcurrentQueue<string> Liveness = new();
        public FileTransferSessionPreparationPermit Permit = null!;

        public PreparationHandshakePair(bool helperPays)
        {
            var id = Guid.NewGuid().ToString("N");
            PayerClient = new($"payer.preparation-handshake.{id}.address");
            DialerClient = new($"dialer.preparation-handshake.{id}.address");
            var options = NknTransportOptions.Load();
            Payer = new(PayerClient, options, new NknIdentity("preparation-payer", PayerClient.Address),
                NknTunaAccelerationOptions.Disabled, PayerLane);
            Dialer = new(DialerClient, options, new NknIdentity("preparation-dialer", DialerClient.Address),
                NknTunaAccelerationOptions.Disabled, DialerLane);
            host = helperPays ? Dialer : Payer;
            helper = helperPays ? Payer : Dialer;
            PayerService.AttachTransport(Payer);
            DialerService.AttachTransport(Dialer);
            foreach (var transport in new[] { Payer, Dialer })
                transport.SessionLivenessProofReceived += (_, e) => Liveness.Enqueue(e.ProofKind);
            foreach (var client in new[] { PayerClient, DialerClient })
                client.BeforeSendAsync = (_, bytes, _) =>
                {
                    if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) && (int)envelope.Type >= 52)
                        Sent.Enqueue(envelope.Type);
                    return Task.CompletedTask;
                };
        }

        public async Task Approve(bool begin = true)
        {
            await ApproveNknSessionAsync(host, helper, Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
            if (!begin) return;
            var lifecycle = ((IFileTransferSessionPreparationTransport)Payer).SessionPreparationLifecycle;
            Permit = lifecycle.Attachment!.Authority.Begin(lifecycle.Attachment, lifecycle.Current!,
                Guid.NewGuid().ToString("N"), 1, true,
                DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds())).Permit!;
            Assert.NotNull(Permit);
        }
        public Task<bool> Start(CancellationToken ct = default) => Payer.StartSessionPreparationHandshakeAsync(Permit, null, ct);
        public FileTransferSessionPreparationState State(SessionFileTransferService service)
            => Assert.IsType<FileTransferSessionPreparationState>(GetPrivateField(service, "sessionPreparation"));
        public Task WaitReady() => WaitUntilAsync(() => State(PayerService).Readiness is not null &&
            State(DialerService).Readiness is not null, TimeSpan.FromSeconds(3));
        public PreparationHandshakeBarrier Hold(MsgType type)
        {
            var barrier = new PreparationHandshakeBarrier();
            barriers.Add(barrier);
            var client = type == MsgType.SessionPreparationAnswer ? DialerClient : PayerClient;
            client.ShouldDeliverSendAsync = async (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) && envelope.Type == type)
                {
                    barrier.Entered.TrySetResult();
                    await barrier.Release.Task; // Deliberately deliver even after cancellation.
                }
                return true;
            };
            return barrier;
        }
        public void AssertNoFileEffects(int expectedGlobalStops = 0)
        {
            foreach (var service in new[] { PayerService, DialerService })
            {
                Assert.Null(service.Snapshot.Inbound);
                Assert.Null(service.Snapshot.Outbound);
                Assert.Null(State(service).ClaimedEvidence);
            }
            foreach (var transport in new[] { Payer, Dialer })
            {
                Assert.False(transport.IsAccelerationAvailableForTests);
                Assert.Null(GetPrivateField(transport, "initialTunaReadinessState"));
                Assert.Equal(NknAccelerationLaneKind.None, GetPrivateField(transport, "accelerationNegotiatedLanes"));
                Assert.Null(GetPrivateField(transport, "outboundAccelerationOfferNonce"));
                Assert.Null(GetPrivateField(transport, "pendingAccelerationAnswerAckNonce"));
                Assert.Null(GetPrivateField(transport, "runtimeUnlockRecoveryRetryState"));
            }
            Assert.DoesNotContain(Liveness, proof => proof.StartsWith("session_preparation", StringComparison.Ordinal));
            Assert.Equal(expectedGlobalStops, PayerLane.GlobalStops + DialerLane.GlobalStops);
        }
        public void Dispose()
        {
            PayerService.Dispose();
            DialerService.Dispose();
            foreach (var barrier in barriers) barrier.Release.TrySetResult();
            Payer.Dispose();
            Dialer.Dispose();
            lifetime.Dispose();
        }
    }

    private sealed class PreparationHandshakeLane : INknTunaAccelerationSession
    {
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly NknTunaSidecarClient client = new(NknAccelerationLaneKind.File, 16);
        private readonly NknTunaSidecarProcessOwner owner = (NknTunaSidecarProcessOwner)typeof(NknTunaSidecarProcessOwner)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke(new object?[] { "dialer", new Process(), null });
        public NknTunaLaneLease? Current;
        public NknTunaLanePreparationRequest? Request;
        public int Starts, ExactStops, GlobalStops, OrdinaryStarts;
        public int Retains, Activations;
        public bool IsAvailable => Current is not null;
        public bool CanListen = true;
        public bool CanOfferListener => CanListen;
        public NknAccelerationLaneKind Configuration = NknAccelerationLaneKind.File;
        public NknAccelerationLaneKind ConfiguredLanes => Configuration;
        public NknAccelerationLaneKind SupportedLanes => NknAccelerationLaneKind.File;
        public string? LocalTunaAddress => "preparation-listener";
        public bool IsLocalPaidListenerActive => Request?.LocalIsPayer == true && IsAvailable;
        public event EventHandler<NknIncomingMessage>? MessageReceived;
        public PreparationHandshakeLane? FilePeer;
        public Func<NknBridgeChannel, byte[], CancellationToken, Task<bool>>? ForwardBytesForTests;
        public void ReceivePeerBytesForTests(NknBridgeChannel lane, byte[] bytes)
            => MessageReceived?.Invoke(this, new NknIncomingMessage(string.Empty, bytes.AsSpan().ToArray(),
                isTopic: false, topic: null, channel: lane,
                bridgeIngressObservedUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        public event EventHandler<AccelerationStateChangedEventArgs>? StateChanged;
        public void Emit(bool available, string identity) => StateChanged?.Invoke(this,
            new(available, "remote_closed", sessionPreparationExecutionIdentity: identity));
        private NknAccelerationFailureOrigin? failureOrigin;
        private long failureGeneration;
        public bool IsFailureOriginCurrent(NknAccelerationFailureOrigin origin)
            => !origin.IsDefault && Current is null && failureOrigin == origin;
        public void FailRuntime(string reason)
        {
            var lease = Assert.IsType<NknTunaLaneLease>(Current);
            failureOrigin = new(lease.LaneInstanceId, lease.ClientGeneration,
                lease.DialerOwner?.RunId ?? lease.Listener!.ProcessRunId, ++failureGeneration);
            Current = null;
            StateChanged?.Invoke(this, new(false, reason, failureOrigin));
        }
        public NknAccelerationLaneDiagnostics GetDiagnosticsSnapshot() => NknAccelerationLaneDiagnostics.Empty;
        public async Task<bool> TrySendAsync(NknBridgeChannel lane, byte[] bytes, CancellationToken ct)
        {
            if (!IsAvailable || FilePeer is not { IsAvailable: true } peer) { return false; }
            ct.ThrowIfCancellationRequested();
            if (ForwardBytesForTests is { } forward) { return await forward(lane, bytes, ct); }
            await Task.Yield();
            peer.MessageReceived?.Invoke(peer, new NknIncomingMessage(string.Empty, bytes.AsSpan().ToArray(),
                isTopic: false, topic: null, channel: lane,
                bridgeIngressObservedUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            return true;
        }
        public Task<bool> EnsureListenerSidecarConnectedAsync(string peer, CancellationToken ct)
        {
            Interlocked.Increment(ref OrdinaryStarts);
            throw new InvalidOperationException("ordinary_listener_forbidden");
        }
        public Task<bool> StartDialerSidecarAsync(string tuna, string peer, CancellationToken ct) => throw new InvalidOperationException("ordinary_dialer_forbidden");
        public Task StopAsync(string reason, CancellationToken ct) { GlobalStops++; Current = null; failureOrigin = null; return Task.CompletedTask; }
        public NknTunaLaneLease? CaptureCurrentLaneLease() => Current;
        public bool TryTransferPreparationLaneLease(NknTunaLaneLease lease, string identity, bool activate,
            out NknTunaLaneLease? successor)
        {
            successor = null;
            if (Current != lease) return false;
            failureOrigin = null;
            if (activate) Activations++; else Retains++;
            var listener = lease.Listener is { } prior ? prior with
            {
                CurrentRequest = prior.CurrentRequest with
                {
                    ExecutionIdentity = identity,
                    LifetimePolicy = activate ? NknTunaListenerLifetimePolicy.CoordinatorAdmittedTransfer : NknTunaListenerLifetimePolicy.SessionPreparation,
                },
                OwnershipGeneration = prior.OwnershipGeneration + 1,
            } : null;
            successor = Current = lease with { ClientGeneration = lease.ClientGeneration + 1, Listener = listener };
            return true;
        }
        public Task<NknTunaLaneLease?> StartPreparationLaneAsync(NknTunaLanePreparationRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Null(Current);
            Starts++;
            Request = request;
            failureOrigin = null;
            var listener = request.LocalIsPayer ? new NknTunaListenerAdoptionSnapshot(
                new(request.ExpectedRemotePeer, NknAccelerationLaneKind.File, NknTunaListenerLifetimePolicy.SessionPreparation, request.ExecutionIdentity),
                new("test-ipc", LocalTunaAddress!), 1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, 1) : null;
            Current = new(Guid.NewGuid(), 1, 1, client, request.LocalIsPayer ? null : owner, listener);
            Emit(true, request.ExecutionIdentity);
            return Task.FromResult<NknTunaLaneLease?>(Current);
        }
        public bool TryStopCurrentLaneLease(NknTunaLaneLease lease, string reason)
        {
            if (Current != lease) return false;
            Current = null;
            failureOrigin = null;
            Interlocked.Increment(ref ExactStops);
            Stopped.TrySetResult();
            return true;
        }
        public void Dispose() { client.Dispose(); owner.Stop("test_dispose"); }
    }
}
