using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("FileTransferInfraSuite", "SessionPreparationExecution")]
    public Task PreparationExecution_StartsOnlyExactCoreBoundWorkAndCoalescesDuplicates(bool payer)
        => WithPreparationExecutionPairAsync(payer, async pair =>
        {
            var start = pair.Start();
            await pair.Lane.Entered.Task.WaitAsync(pair.Token);
            Assert.Same(start, pair.Start());
            Assert.Equal(payer, pair.Lane.Request!.LocalIsPayer);
            Assert.Equal(pair.Peer.LocalPeerAddress, pair.Lane.Request.ExpectedRemotePeer);
            Assert.Equal(payer ? null : "preparation-remote", pair.Lane.Request.RemoteTunaAddress);
            pair.Lane.Release.TrySetResult();
            Assert.Same(pair.Lane.Original, await start.WaitAsync(pair.Token));
            Assert.Equal(1, pair.Lane.Starts);
            pair.AssertNoFileEffects();
            pair.Service.ResetSessionState();
            Assert.True(pair.Lane.StartToken.IsCancellationRequested);
            await pair.Lane.Stopped.Task.WaitAsync(pair.Token);
            Assert.Equal(1, pair.Lane.ExactStops);
            Assert.Equal(0, pair.Lane.GlobalStops);
        });

    [Theory]
    [InlineData(true, "reset")]
    [InlineData(false, "reset")]
    [InlineData(true, "detach")]
    [InlineData(false, "detach")]
    [InlineData(true, "key_reset")]
    [InlineData(false, "key_reset")]
    [InlineData(true, "admission")]
    [InlineData(false, "admission")]
    [InlineData(true, "caller_cancel")]
    [InlineData(false, "caller_cancel")]
    [Trait("FileTransferInfraSuite", "SessionPreparationExecution")]
    public Task PreparationExecution_RetirementDuringBlockedStartRejectsLateResource(bool payer, string change)
        => WithPreparationExecutionPairAsync(payer, async pair =>
        {
            using var request = new CancellationTokenSource();
            var start = pair.Start(request.Token);
            await pair.Lane.Entered.Task.WaitAsync(pair.Token);
            switch (change)
            {
                case "reset": pair.Service.ResetSessionState(); break;
                case "detach": pair.Service.DetachTransport(); break;
                case "key_reset": InvokePrivateMethod(pair.Host, "ResetControlSecureState"); break;
                case "admission":
                    await pair.Service.TryStartSendAsync(new("execution.bin", 4, "execution-admission"),
                        _ => Task.FromResult<Stream>(new MemoryStream(new byte[4])), pair.Token);
                    break;
                case "caller_cancel": request.Cancel(); break;
            }
            Assert.True(pair.Lane.StartToken.IsCancellationRequested);
            // Deliberately return a physical result after cancellation. The
            // adapter must still recapture and clean only this old receipt.
            pair.Lane.Release.TrySetResult();
            Assert.Null(await start.WaitAsync(pair.Token));
            await pair.Lane.Stopped.Task.WaitAsync(pair.Token);
            Assert.Equal(1, pair.Lane.ExactStops);
            Assert.Equal(0, pair.Lane.GlobalStops);
            if (change != "admission") pair.AssertNoFileEffects();
            else Assert.NotEqual("file_tuna_v4", pair.Service.Snapshot.Outbound?.RouteToken);
        });

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [Trait("FileTransferInfraSuite", "SessionPreparationExecution")]
    public Task PreparationExecution_LateCleanupCannotStopPhysicalSuccessor(bool payer, bool retire)
        => WithPreparationExecutionPairAsync(payer, async pair =>
        {
            var start = pair.Start();
            await pair.Lane.Entered.Task.WaitAsync(pair.Token);
            if (retire) pair.Service.ResetSessionState();
            var successor = pair.Lane.Original with { ClientGeneration = 2 };
            pair.Lane.Current = successor;
            pair.Lane.Release.TrySetResult();
            Assert.Null(await start.WaitAsync(pair.Token));
            Assert.Same(successor, pair.Lane.Current);
            Assert.Equal(0, pair.Lane.ExactStops);
            Assert.Equal(0, pair.Lane.GlobalStops);
            pair.AssertNoFileEffects();
        });

    [Theory]
    [InlineData(true, "reset")]
    [InlineData(false, "reset")]
    [InlineData(true, "key_reset")]
    [InlineData(false, "key_reset")]
    [InlineData(true, "admission")]
    [InlineData(false, "admission")]
    [Trait("FileTransferInfraSuite", "SessionPreparationExecution")]
    public Task PreparationExecution_RetirementAfterBindingButBeforeLaneCallRejectsQueuedWork(bool payer, string change)
        => WithPreparationExecutionPairAsync(payer, async pair =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Host.SessionPreparationBeforeLaneStartForTests = () => { entered.TrySetResult(); return release.Task; };
            var start = pair.Start();
            try
            {
                await entered.Task.WaitAsync(pair.Token);
                if (change == "reset") pair.Service.ResetSessionState();
                else if (change == "key_reset") InvokePrivateMethod(pair.Host, "ResetControlSecureState");
                else await pair.Service.TryStartSendAsync(new("queued.bin", 4, "queued-admission"),
                    _ => Task.FromResult<Stream>(new MemoryStream(new byte[4])), pair.Token);
            }
            finally { release.TrySetResult(); }
            Assert.Null(await start.WaitAsync(pair.Token));
            Assert.Equal(0, pair.Lane.Starts);
            Assert.Equal(0, pair.Lane.ExactStops);
            if (change != "admission") pair.AssertNoFileEffects();
        });

    [Theory]
    [InlineData(true, "copied_permit")]
    [InlineData(false, "copied_permit")]
    [InlineData(true, "retired")]
    [InlineData(false, "retired")]
    [InlineData(false, "missing_offer")]
    [InlineData(false, "other_episode")]
    [InlineData(false, "wrong_type")]
    [Trait("FileTransferInfraSuite", "SessionPreparationExecution")]
    public Task PreparationExecution_RejectsInvalidQueuedWorkBeforeLaneCall(bool payer, string change)
        => WithPreparationExecutionPairAsync(payer, async pair =>
        {
            var permit = pair.Permit;
            var offer = pair.Offer;
            switch (change)
            {
                case "copied_permit": permit = permit with { }; break;
                case "retired": pair.Service.ResetSessionState(); break;
                case "missing_offer": offer = null; break;
                case "other_episode": offer = offer! with { Nonce = "other" }; break;
                case "wrong_type": offer = offer! with { Type = MsgType.TransportAccelerationOffer }; break;
            }
            Assert.Null(await pair.Host.StartSessionPreparationLaneExecutionAsync(permit, offer, pair.Token));
            Assert.Equal(0, pair.Lane.Starts);
            Assert.Equal(0, pair.Lane.GlobalStops);
            pair.AssertNoFileEffects();
        });

    private static async Task WithPreparationExecutionPairAsync(bool payer, Func<PreparationExecutionPair, Task> test)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var pair = new PreparationExecutionPair(payer);
            await pair.ApproveAsync();
            await test(pair);
        }
        finally { FakeNknClient.ResetNetwork(); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationExecution")]
    public Task PreparationExecution_ExplicitStopOrTransportDisposalRetiresBoundStartup(bool payer, bool dispose)
        => WithPreparationExecutionPairAsync(payer, async pair =>
        {
            var start = pair.Start();
            await pair.Lane.Entered.Task.WaitAsync(pair.Token);
            if (dispose) pair.Host.Dispose();
            else await pair.Host.StopAccelerationAsync("user_locked", pair.Token);
            Assert.True(pair.Lane.StartToken.IsCancellationRequested);
            pair.Lane.Release.TrySetResult();
            Assert.Null(await start.WaitAsync(pair.Token));
            Assert.Null(pair.Service.Snapshot.Inbound);
            Assert.Null(pair.Service.Snapshot.Outbound);
        });

    private sealed class PreparationExecutionPair : IDisposable
    {
        private readonly bool payer;
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(15));
        public CancellationToken Token => lifetime.Token;
        public readonly SessionFileTransferService Service = new(), PeerService = new();
        public readonly PreparationExecutionLane Lane = new();
        public readonly NknSignalingTransport Host, Peer;
        public FileTransferSessionPreparationPermit Permit = null!;
        public SessionPreparationInbound? Offer;

        public PreparationExecutionPair(bool payer)
        {
            this.payer = payer;
            var id = Guid.NewGuid().ToString("N");
            var hostClient = new FakeNknClient($"host.preparation-execution.{id}.address");
            var peerClient = new FakeNknClient($"helper.preparation-execution.{id}.address");
            var options = NknTransportOptions.Load();
            Host = new(hostClient, options, new NknIdentity("prep-exec-host", hostClient.Address),
                NknTunaAccelerationOptions.Disabled, Lane);
            Peer = new(peerClient, options, new NknIdentity("prep-exec-helper", peerClient.Address),
                NknTunaAccelerationOptions.Disabled, accelerationLane: null);
            Service.AttachTransport(Host);
            PeerService.AttachTransport(Peer);
        }

        public async Task ApproveAsync()
        {
            var session = await ApproveNknSessionAsync(Host, Peer, Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
            var lifecycle = ((IFileTransferSessionPreparationTransport)Host).SessionPreparationLifecycle;
            var deadline = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds());
            Permit = lifecycle.Attachment!.Authority.Begin(lifecycle.Attachment, lifecycle.Current!,
                Guid.NewGuid().ToString("N"), 1, payer, deadline).Permit!;
            Assert.NotNull(Permit);
            if (!payer)
            {
                var payload = new TransportAccelerationOfferPayload
                {
                    SessionId = session, Nonce = Permit.Nonce, PayerDecisionId = 1,
                    SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ExpiresAtUnixMs = deadline.ToUnixTimeMilliseconds(),
                    SidecarProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion, SupportedLanes = ["file"],
                    SenderRole = "helper", Trigger = "session_preparation", TunaAddress = "preparation-remote",
                };
                var encrypted = Assert.IsType<byte[]>(InvokePrivateMethod(Peer, "CreateSecureControlPayload",
                    MsgType.SessionPreparationOffer, Permit.Nonce, JsonSerializer.SerializeToUtf8Bytes(payload)));
                var envelope = new Envelope(1, Assert.IsType<string>(GetPrivateField(Peer, "currentEnvelopeCode")),
                    Guid.NewGuid().ToString("N"), MsgType.SessionPreparationOffer, encrypted, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
                Assert.True(Host.TryReadSessionPreparationEnvelope(Peer.LocalPeerAddress!, envelope, out Offer));
            }
        }

        public Task<NknTunaLaneLease?> Start(CancellationToken ct = default)
            => Host.StartSessionPreparationLaneExecutionAsync(Permit, Offer, ct);

        public void AssertNoFileEffects()
        {
            Assert.Null(Service.Snapshot.Outbound);
            Assert.Null(Service.Snapshot.Inbound);
            Assert.False(Host.IsAccelerationAvailableForTests);
            Assert.Null(GetPrivateField(Host, "initialTunaReadinessState"));
            Assert.Null(GetPrivateField(Host, "runtimeUnlockRecoveryRetryState"));
            Assert.Null(GetPrivateField(Host, "outboundAccelerationOfferNonce"));
            Assert.Null(GetPrivateField(Host, "pendingAccelerationAnswerAckNonce"));
            Assert.Null(Assert.IsType<FileTransferSessionPreparationState>(GetPrivateField(Service, "sessionPreparation")).Readiness);
        }

        public void Dispose()
        {
            Service.Dispose();
            PeerService.Dispose();
            Lane.Release.TrySetResult();
            Host.Dispose();
            Peer.Dispose();
            lifetime.Dispose();
        }
    }

    private sealed class PreparationExecutionLane : INknTunaAccelerationSession
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly NknTunaLaneLease Original = new(Guid.NewGuid(), 1, 1,
            new NknTunaSidecarClient(NknAccelerationLaneKind.File, 16), null, null);
        public NknTunaLaneLease? Current;
        public NknTunaLanePreparationRequest? Request;
        public CancellationToken StartToken;
        public int Starts, ExactStops, GlobalStops;
        public bool IsAvailable => Current is not null;
        public bool CanOfferListener => true;
        public NknAccelerationLaneKind ConfiguredLanes => NknAccelerationLaneKind.File;
        public NknAccelerationLaneKind SupportedLanes => NknAccelerationLaneKind.File;
        public string? LocalTunaAddress => "preparation-local";
        public bool IsLocalPaidListenerActive => Current is not null;
        public event EventHandler<NknIncomingMessage>? MessageReceived { add { } remove { } }
        public event EventHandler<AccelerationStateChangedEventArgs>? StateChanged { add { } remove { } }
        public NknAccelerationLaneDiagnostics GetDiagnosticsSnapshot() => NknAccelerationLaneDiagnostics.Empty;
        public Task<bool> TrySendAsync(NknBridgeChannel lane, byte[] bytes, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> EnsureListenerSidecarConnectedAsync(string peer, CancellationToken ct) => throw new InvalidOperationException("legacy_listener_forbidden");
        public Task<bool> StartDialerSidecarAsync(string tuna, string peer, CancellationToken ct) => throw new InvalidOperationException("legacy_dialer_forbidden");
        public Task StopAsync(string reason, CancellationToken ct) { GlobalStops++; return Task.CompletedTask; }
        public NknTunaLaneLease? CaptureCurrentLaneLease() => Current;
        public async Task<NknTunaLaneLease?> StartPreparationLaneAsync(NknTunaLanePreparationRequest request, CancellationToken ct)
        {
            Starts++;
            Request = request;
            StartToken = ct;
            Current = Original;
            Entered.TrySetResult();
            await Release.Task;
            return Original; // Ignore cancellation to exercise the caller's recapture.
        }
        public bool TryStopCurrentLaneLease(NknTunaLaneLease lease, string reason)
        {
            if (Current != lease) return false;
            Current = null;
            Interlocked.Increment(ref ExactStops);
            Stopped.TrySetResult();
            return true;
        }
        public void Dispose() => Original.Client.Dispose();
    }
}
