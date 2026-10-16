using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData("exact")]
    [InlineData("expired")]
    [InlineData("invalidated")]
    [InlineData("transaction_replaced")]
    [InlineData("offer_replaced")]
    [InlineData("lease_replaced")]
    [InlineData("missing_ack")]
    [InlineData("replaced_dialer")]
    [InlineData("publication_removed")]
    [InlineData("successor_launch")]
    [Trait("FileTransferInfraSuite", "AutomaticFileHandoff")]
    public async Task AutomaticFileHandoff_SenderDialerRevalidatesAtLaunchInstallation(string change)
    {
        using var lane = new RetryableTunaAccelerationSession(false, 0) { StrictPayerHandoffForTests = true };
        Assert.True(await lane.StartDialerSidecarAsync("test-listener", "peer.address", CancellationToken.None));
        var fixture = CreateRuntimeUnlockResponderWaitingFixtureForTests("install-" + change, accelerationLane: lane);
        using var transport = fixture.Transport;
        var handshake = fixture.Convergence!;
        handshake.GetType().GetProperty("PhysicalDialerLease")!.SetValue(handshake, lane.CaptureCurrentLaneLease());
        transport.ObserveFileTransferRouteHint(CreateAuthorizedRouteHintNotification(
            fixture.Transaction.SessionId, fixture.Transaction.TransferId!,
            FileTransferRouteResolver.RegularNknV4FastToken, 4, "launch_installation_test"));
        object?[] roleArguments = [fixture.Transaction, null, null];
        Assert.True((bool)typeof(NknSignalingTransport).GetMethod("TryResolveRuntimeUnlockPreCommitProbeLaunchRole",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(transport, roleArguments)!);
        Assert.Equal("Initiator", roleArguments[2]!.ToString());

        // Replace evidence after role selection, before the installation boundary.
        switch (change)
        {
            case "expired":
                handshake.GetType().GetProperty("AnswerDeadlineUtcMs")!.SetValue(handshake, 1L);
                break;
            case "invalidated":
                SetPrivateField(transport, "runtimeUnlockResponderAnswerProbeConvergence", null);
                break;
            case "transaction_replaced":
            case "successor_launch":
                SetPrivateField(transport, "runtimeUnlockTransactionState", fixture.Transaction with
                { TransactionGeneration = fixture.Transaction.TransactionGeneration + 1 });
                break;
            case "offer_replaced":
                SetPrivateField(transport, "runtimeUnlockTransactionState", fixture.Transaction with
                { OfferGeneration = fixture.Transaction.OfferGeneration + 1 });
                break;
            case "lease_replaced":
                SetPrivateField(transport, "runtimeUnlockTunaPathLeaseState", fixture.Lease with
                { LeaseGeneration = fixture.Lease.LeaseGeneration + 1 });
                break;
            case "missing_ack":
                handshake.GetType().GetProperty("AckMessageId")!.SetValue(handshake, null);
                break;
            case "replaced_dialer":
                var oldLease = lane.CaptureCurrentLaneLease()!;
                Assert.True(await lane.StartDialerSidecarAsync("replacement", "peer.address", CancellationToken.None));
                oldLease.Client.Dispose();
                oldLease.DialerOwner!.Dispose();
                break;
            case "publication_removed":
                ((System.Collections.IDictionary)GetPrivateField(transport, "fileTransferRoutePublicationAuthorities")!).Clear();
                break;
        }
        if (change == "successor_launch")
        {
            var successor = (RuntimeUnlockTransactionSnapshot)GetPrivateField(transport, "runtimeUnlockTransactionState")!;
            var launch = typeof(NknSignalingTransport).GetMethod("CreateRuntimeUnlockPreCommitProbeLaunchSnapshot",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null,
                [successor, successor.TransferId, "successor", 41L]);
            SetPrivateField(transport, "runtimeUnlockPreCommitProbeLaunchSessionId", successor.SessionId);
            SetPrivateField(transport, "runtimeUnlockPreCommitProbeLaunchTransferId", successor.TransferId);
            SetPrivateField(transport, "runtimeUnlockPreCommitProbeLaunchTransactionGeneration", successor.TransactionGeneration);
            SetPrivateField(transport, "runtimeUnlockPreCommitProbeLaunchOfferGeneration", successor.OfferGeneration);
            SetPrivateField(transport, "runtimeUnlockPreCommitProbeLaunchGeneration", 41L);
            SetPrivateField(transport, "runtimeUnlockPreCommitProbeLaunchSnapshot", launch);
            SetPrivateField(transport, "runtimeUnlockPreCommitProbeStartedSnapshot", launch);
        }
        string[] fields = ["runtimeUnlockTransactionState", "runtimeUnlockPreCommitProbeLaunchGeneration",
            "runtimeUnlockPreCommitProbeLaunchSnapshot", "runtimeUnlockPreCommitProbeStartedSnapshot",
            "runtimeUnlockPreCommitProbeLaunchTimeoutScheduled", "runtimeUnlockAcceptedPreCommitSnapshot"];
        var before = fields.Select(field => GetPrivateField(transport, field)).ToArray();
        var logStart = GetOperationalLogLength();
        InvokePrivateMethod(transport, "RequestRuntimeUnlockPreCommitProbeLaunchWithRole",
            fixture.Transaction.SessionId, NknAccelerationLaneKind.File, "test_installation_boundary",
            fixture.Transaction, roleArguments[2], null);
        if (change == "exact")
        {
            var launch = Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(GetPrivateField(transport, fields[2]));
            Assert.Equal(fixture.Transaction.TransactionGeneration, launch.TransactionGeneration);
            Assert.True(launch.LaunchGeneration > (long)before[1]!);
        }
        else
        {
            Assert.Equal(before, fields.Select(field => GetPrivateField(transport, field)).ToArray());
            var log = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain("event=runtime_unlock_precommit_probe_launch_requested;", log);
            Assert.DoesNotContain("event=filetransfer_data_session_handoff_", log);
        }
        Assert.False(transport.IsFileTunaActiveForRouteSelection);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("expired")]
    [InlineData("invalidated")]
    [InlineData("transaction_mutated")]
    [InlineData("offer_mutated")]
    [InlineData("lease_mutated")]
    [InlineData("missing_ack")]
    [InlineData("replaced_dialer")]
    [InlineData("wrong_transfer")]
    [InlineData("no_publication")]
    [InlineData("inbound_publication")]
    [Trait("FileTransferInfraSuite", "AutomaticFileHandoff")]
    public async Task AutomaticFileHandoff_SenderDialerRequiresExactHandshakeAndPublication(string control)
    {
        using var lane = new RetryableTunaAccelerationSession(false, 0) { StrictPayerHandoffForTests = true };
        Assert.True(await lane.StartDialerSidecarAsync("test-listener", "peer.address", CancellationToken.None));
        var fixture = CreateRuntimeUnlockResponderWaitingFixtureForTests(control, control, lane);
        using var transport = fixture.Transport;
        var handshake = fixture.Convergence!;
        handshake.GetType().GetProperty("PhysicalDialerLease")!.SetValue(handshake, lane.CaptureCurrentLaneLease());
        if (control == "missing_ack")
            handshake.GetType().GetProperty("AckMessageId")!.SetValue(handshake, null);
        if (control == "wrong_transfer")
            handshake.GetType().GetProperty("TransferId")!.SetValue(handshake, "wrong-transfer");
        if (control == "replaced_dialer")
        {
            var oldLease = lane.CaptureCurrentLaneLease()!;
            Assert.True(await lane.StartDialerSidecarAsync("replacement-listener", "peer.address", CancellationToken.None));
            oldLease.Client.Dispose();
            oldLease.DialerOwner!.Dispose();
        }
        if (control != "no_publication")
            transport.ObserveFileTransferRouteHint(CreateAuthorizedRouteHintNotification(
                fixture.Transaction.SessionId, fixture.Transaction.TransferId!,
                FileTransferRouteResolver.RegularNknV4FastToken, 4, "sender_dialer_role_test",
                direction: control == "inbound_publication" ? FileTransferDirection.Inbound : FileTransferDirection.Outbound));

        object?[] arguments = [fixture.Transaction, null, null];
        var accepted = Assert.IsType<bool>(typeof(NknSignalingTransport).GetMethod(
            "TryResolveRuntimeUnlockPreCommitProbeLaunchRole",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(transport, arguments));
        Assert.Equal(control is "exact" or "no_publication" or "inbound_publication", accepted);
        if (accepted)
            Assert.Equal(control == "exact" ? "Initiator" : "ResponderWaitingForPeerProbe", arguments[2]!.ToString());
        // Role selection cannot publish a route or start a probe itself.
        Assert.Equal(fixture.Transaction, GetPrivateField(transport, "runtimeUnlockTransactionState"));
        Assert.Null(GetPrivateField(transport, "runtimeUnlockPreCommitProbeLaunchSnapshot"));
        Assert.Null(GetPrivateField(transport, "runtimeUnlockAcceptedPreCommitSnapshot"));
        Assert.False(transport.IsFileTunaActiveForRouteSelection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "AutomaticFileHandoff")]
    public Task AutomaticFileHandoff_ActiveRegularV4UsesCoordinatorTransition(bool helperPays)
        => RunAutomaticFileHandoffAsync(helperPays, stopBeforeAnswer: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "AutomaticFileHandoff")]
    public Task AutomaticFileHandoff_BindsActivationAndPauseGuardBeforeAnswer(bool helperPays)
        => RunAutomaticFileHandoffAsync(helperPays, stopBeforeAnswer: true);

    private async Task RunAutomaticFileHandoffAsync(bool helperPays, bool stopBeforeAnswer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var previousPriority = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        var releaseAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var scope = Guid.NewGuid().ToString("N");
            var hostClient = new FakeNknClient($"host.automatic-file-handoff-{scope}.address");
            var helperClient = new FakeNknClient($"helper.automatic-file-handoff-{scope}.address");
            using var hostLane = new RetryableTunaAccelerationSession(!helperPays, 0) { StrictPayerHandoffForTests = true };
            using var helperLane = new RetryableTunaAccelerationSession(helperPays, 0) { StrictPayerHandoffForTests = true };
            var options = NknTransportOptions.Load();
            var hostIdentity = new NknIdentity("host-automatic-file-handoff", hostClient.Address);
            var helperIdentity = new NknIdentity("helper-automatic-file-handoff", helperClient.Address);
            // Keep byte transport fake, but exercise real pressure install/settlement receipts.
            using var hostPressure = new RealNknClientAdapter(hostIdentity, options);
            using var helperPressure = new RealNknClientAdapter(helperIdentity, options);
            using var host = new NknSignalingTransport(hostClient, options,
                hostIdentity, NknTunaAccelerationOptions.Disabled, hostLane, hostPressure);
            using var helper = new NknSignalingTransport(helperClient, options,
                helperIdentity, NknTunaAccelerationOptions.Disabled, helperLane, helperPressure);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(host);
            receiver.AttachTransport(helper);
            var sessionId = await ApproveNknSessionAsync(host, helper, timeout.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
            var transferId = $"automatic-file-handoff-{scope}";
            var releasePayload = 0;
            var tunaPayloads = 0;
            hostLane.ForwardBytesForTests = async (lane, bytes, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var frame = Decode(bytes, lane);
                if (frame is FileTransferChunkBatchFrameV4)
                    Interlocked.Increment(ref tunaPayloads);
                await Task.Yield();
                helperLane.ReceivePeerBytesForTests(lane, bytes);
                return true;
            };
            helperLane.ForwardBytesForTests = async (lane, bytes, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                hostLane.ReceivePeerBytesForTests(lane, bytes);
                return true;
            };
            var droppedRegular = new System.Collections.Concurrent.ConcurrentDictionary<byte[], byte>();
            hostClient.BeforeSendCoreAsync = (_, bytes, lane, _) =>
            {
                if (Volatile.Read(ref releasePayload) == 0 && Decode(bytes, lane) is
                    FileTransferChunkBatchFrameV4 { StartChunkIndex: >= 4 })
                    droppedRegular.TryAdd(bytes, 0);
                return Task.CompletedTask;
            };
            hostClient.ShouldDeliverSendAsync = (_, bytes, _) => Task.FromResult(!droppedRegular.TryRemove(bytes, out _));
            var answerClient = helperPays ? hostClient : helperClient;
            answerClient.BeforeSendAsync = async (_, bytes, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) && envelope.Type == MsgType.TransportAccelerationAnswer)
                {
                    answerHeld.TrySetResult();
                    await releaseAnswer.Task.WaitAsync(ct);
                }
            };
            var payload = Enumerable.Range(0, 512 * 1024).Select(i => (byte)(i % 251)).ToArray();
            using var destination = new MemoryStream();
            await sender.TryStartSendAsync(new("handoff.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), timeout.Token);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
                TimeSpan.FromSeconds(3));
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), timeout.Token);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                receiver.Snapshot.Inbound?.BytesTransferred > 0, TimeSpan.FromSeconds(3));
            var outbound = GetPrivateField(sender, "outboundTransfer")!;
            var inbound = GetPrivateField(receiver, "inboundTransfer")!;
            AssertPublication(sender, outbound, FileTransferRoute.RegularNknV4Fast);
            AssertPublication(receiver, inbound, FileTransferRoute.RegularNknV4Fast);

            await (helperPays ? helper : host).RequestAccelerationNegotiationAsync("session_security_state_ready", timeout.Token);
            await answerHeld.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
            await WaitUntilAsync(() => sender.Snapshot.Outbound!.IsPauseBlockedByTransport &&
                receiver.Snapshot.Inbound!.IsPauseBlockedByTransport, TimeSpan.FromSeconds(3));
            // Listener/answer evidence alone must not publish the Tuna route.
            AssertPublication(sender, outbound, FileTransferRoute.RegularNknV4Fast);
            AssertPublication(receiver, inbound, FileTransferRoute.RegularNknV4Fast);
            Assert.False(host.IsFileTunaActiveForRouteSelection);
            Assert.False(helper.IsFileTunaActiveForRouteSelection);
            Assert.True(sender.Snapshot.Outbound!.IsPauseBlockedByTransport);
            Assert.True(receiver.Snapshot.Inbound!.IsPauseBlockedByTransport);
            Assert.False(sender.Snapshot.Outbound.IsPaused);
            Assert.False(receiver.Snapshot.Inbound.IsPaused);
            Assert.Null(await sender.PauseTransferAsync(transferId, "ui_pause", timeout.Token));
            Assert.Null(await receiver.PauseTransferAsync(transferId, "ui_pause", timeout.Token));
            var transaction = Assert.IsType<RuntimeUnlockTransactionSnapshot>(
                GetPrivateField(helperPays ? helper : host, "runtimeUnlockTransactionState"));
            var boundary = GetPrivateField(helperPays ? helper : host, "outboundRuntimeUnlockOfferAuthorityBoundary");
            Assert.NotNull(boundary);
            Assert.Equal("InitialActivation", Value(boundary!, "Mode")!.ToString());
            Assert.Equal(sessionId, Value(boundary!, "SessionId"));
            Assert.Equal(transferId, Value(boundary!, "TransferId"));
            var expectedScope = Value(boundary!, "InitialActivationExpectedScope")!;
            var source = Assert.IsType<FileTransferRoutePublicationAuthority>(Value(expectedScope, "SourcePublicationAuthority"));
            Assert.Equal((sessionId, transferId, "regular_nkn_v4_fast", 4),
                (source.SessionId, source.TransferId, source.RouteToken, source.ProtocolVersion));
            Assert.Equal(helperPays ? FileTransferDirection.Inbound : FileTransferDirection.Outbound, source.Direction);
            Assert.Equal(sessionId, transaction.SessionId);
            Assert.True(transaction.TransactionGeneration > 0);
            Assert.True(transaction.OfferGeneration > 0);
            Assert.True(transaction.TunaPathLeaseCurrent);
            Assert.False(transaction.RouteCommitted);
            if (stopBeforeAnswer)
                return;
            releaseAnswer.TrySetResult();
            await WaitFor("coordinator Tuna publication", () => sender.Snapshot.Outbound?.RouteToken == "file_tuna_v4" &&
                receiver.Snapshot.Inbound?.RouteToken == "file_tuna_v4");
            AssertPublication(sender, outbound, FileTransferRoute.FileTunaV4);
            AssertPublication(receiver, inbound, FileTransferRoute.FileTunaV4);
            Assert.False(sender.Snapshot.Outbound!.IsPauseBlockedByTransport);
            Assert.False(receiver.Snapshot.Inbound!.IsPauseBlockedByTransport);
            Volatile.Write(ref releasePayload, 1);
            await WaitFor("same-transfer completion", () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed);
            Assert.True(Volatile.Read(ref tunaPayloads) > 0);
            Assert.Equal(payload, destination.ToArray());
            lock (GetPrivateField(receiver, "gate")!)
            {
                var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(receiver, inbound);
                Assert.True(state.IsTerminal);
                var pressure = FileTransferCoordinator.GetRegularV4FrontierRepairPressureSubstate(state);
                Assert.Null(pressure.PendingBinding);
                Assert.Null(pressure.InstalledBinding);
                Assert.Null(pressure.RetainedPredecessorBinding);
            }

            async Task WaitFor(string edge, Func<bool> condition)
            {
                try { await WaitUntilAsync(condition, TimeSpan.FromSeconds(8)); }
                catch (TimeoutException error)
                {
                    throw new TimeoutException($"Automatic handoff edge: {edge}; helperPays={helperPays}; " +
                        $"sender={sender.Snapshot.Outbound}; receiver={receiver.Snapshot.Inbound}", error);
                }
            }

            FileTransferDataFrame? Decode(byte[] bytes, NknBridgeChannel lane)
            {
                Assert.True(EnvelopeCodec.TryDeserialize(bytes, out var envelope));
                if (envelope.Type != MsgType.FileTransferDataFrame) return null;
                var secure = SessionSecureEnvelopeCodec.Decrypt(
                    Assert.IsType<byte[]>(GetPrivateField(helper, "fileTransferSessionSharedKey")), envelope.Payload,
                    new SessionSecureEnvelopeExpectation(SessionSecureMessageFamily.FileTransfer, "file_transfer_data_frame",
                        new SessionId(sessionId), new PeerAddress(lane == NknBridgeChannel.Bulk ? hostClient.BulkAddress : host.LocalPeerAddress), transferId));
                Assert.True(FileTransferDataFrameCodec.TryDeserialize(secure.Plaintext, out var frame) ||
                    FileTransferDataFrameCodec.TryDeserializeLegacyV4(secure.Plaintext, out frame));
                return frame;
            }
        }
        finally
        {
            releaseAnswer.TrySetResult();
            timeout.Cancel();
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousPriority;
        }

        static void AssertPublication(SessionFileTransferService service, object context, FileTransferRoute route)
        {
            lock (GetPrivateField(service, "gate")!)
            {
                var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
                var publication = Assert.IsType<RoutePublicationToken>(state.CurrentRoutePublicationToken);
                Assert.Equal((route, 4), (publication.Route, publication.ProtocolVersion));
                Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, publication, out _));
                if (route == FileTransferRoute.FileTunaV4)
                {
                    Assert.True(publication.RouteCommitGeneration > 0);
                    Assert.Equal(FileTransferCoordinatorActionKind.RouteCommitPublication, publication.ActionKind);
                }
            }
        }

        static object? Value(object value, string property)
            => value.GetType().GetProperty(property)!.GetValue(value);
    }
}
