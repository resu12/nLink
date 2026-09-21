using System.Collections;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    private static void AssertUnboundUnlockRejected(int logStart, params NknSignalingTransport[] transports)
    {
        foreach (var transport in transports)
        {
            Assert.Null(((IFileTransferSessionPreparationTransport)transport).SessionPreparationLifecycle.Attachment);
            Assert.False(transport.IsAccelerationAvailableForTests);
            Assert.Equal(NknAccelerationLaneKind.None, transport.AccelerationNegotiatedLanesForTests);
            Assert.Null(GetPrivateField(transport, "initialTunaReadinessState"));
            Assert.Null(GetPrivateField(transport, "outboundAccelerationOfferNonce"));
            Assert.Null(GetPrivateField(transport, "pendingAccelerationAnswerAckNonce"));
            Assert.Null(GetPrivateField(transport, "runtimeUnlockRecoveryRetryState"));
            Assert.Equal(0, GetPrivateField(transport, "accelerationNegotiationScheduled"));
            Assert.Empty((IDictionary)GetPrivateField(transport, "fileTransferRoutePublicationAuthorities")!);
        }
        var tail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=session_preparation_request_rejected; reason=preparation_binding_unavailable; authority_effect=none", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=tuna_acceleration_payer_intent_queued;", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=tuna_acceleration_user_stop_cleared; trigger=peer_payer_intent", tail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationRequest")]
    public Task TransportAccelerationStop_BoundUnlockPreparesBeforeActualAdmission(bool helperPays, bool stopPayer)
        => WithPublicPreparationPairAsync(helperPays, async pair =>
        {
            var sessionId = ((IFileTransferSessionPreparationTransport)pair.Payer).SessionPreparationLifecycle.Current!.SessionId;
            // Only the old transport observation is seeded. No transfer, publication or owner exists.
            pair.Payer.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            pair.Dialer.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            var stopped = stopPayer ? pair.Payer : pair.Dialer;
            var peer = stopPayer ? pair.Dialer : pair.Payer;
            await stopped.StopAccelerationAsync("header_switch_off", pair.Token);
            await WaitUntilAsync(() => Equals(sessionId, GetPrivateField(peer, "accelerationPeerUserStoppedSessionId")) &&
                pair.PayerLane.GlobalStops + pair.DialerLane.GlobalStops == 2,
                TimeSpan.FromSeconds(3));
            Assert.True(stopped.IsAccelerationUserStoppedForCurrentSessionForTests);
            Assert.False(peer.IsAccelerationUserStoppedForCurrentSessionForTests);

            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            Assert.False(pair.Payer.IsAccelerationUserStoppedForCurrentSessionForTests);
            await pair.WaitReady();
            await WaitPreparationRequestIdle(pair);
            pair.AssertNoFileEffects(expectedGlobalStops: 2);
            Assert.Equal(1, pair.PayerLane.Starts);
            Assert.Equal(1, pair.DialerLane.Starts);

            Assert.Equal("file_tuna_v4", (await StartPreparedAdmission(pair.PayerService, pair.Token))!.RouteToken);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.RouteToken == "file_tuna_v4", TimeSpan.FromSeconds(3));
            Assert.True(AdmissionLease(pair.PayerService).IsAdopted);
            Assert.True(AdmissionLease(pair.DialerService).IsAdopted);
            foreach (var transport in new[] { pair.Payer, pair.Dialer })
            {
                var publication = Assert.Single(((IDictionary)GetPrivateField(transport, "fileTransferRoutePublicationAuthorities")!)
                    .Values.Cast<FileTransferRoutePublicationAuthority>());
                Assert.Equal("prepared-admission", publication.TransferId);
                Assert.Equal("file_tuna_v4", publication.RouteToken);
                Assert.Equal(FileTransferProtocol.ProtocolVersionV4, publication.ProtocolVersion);
                Assert.Equal(1, publication.RoutePublicationGeneration);
                Assert.Null(publication.RouteCommitGeneration);
            }
        });
}
