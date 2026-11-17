using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("FileTransferInfraSuite", "PreparationDiagnostics")]
    public Task PreparationDiagnostics_PublicPairedEpisodeSurvivesObservationAndRequiresActualAdmissions(bool helperPays, bool payerSends)
        => WithPublicPreparationPairAsync(helperPays, async pair =>
        {
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            await pair.WaitReady();
            await WaitPreparationRequestIdle(pair);
            var sender = payerSends ? pair.PayerService : pair.DialerService;
            var receiver = payerSends ? pair.DialerService : pair.PayerService;
            var sendState = pair.State(sender);
            var receiveState = pair.State(receiver);
            var session = sendState.Scope!.SessionId;
            var sent = pair.Sent.ToArray();
            for (var i = 0; i < 3; i++)
            {
                var outbound = sender.ObserveSessionPreparationForDiagnostics(session, FileTransferDirection.Outbound, DateTimeOffset.UtcNow);
                var inbound = receiver.ObserveSessionPreparationForDiagnostics(session, FileTransferDirection.Inbound, DateTimeOffset.UtcNow);
                Assert.True(outbound.Ready);
                Assert.True(inbound.Ready);
                Assert.NotEqual(outbound.Scope!.Incarnation, inbound.Scope!.Incarnation);
                Assert.Equal(outbound.EpisodeFingerprint, inbound.EpisodeFingerprint);
                Assert.Equal(sendState.Readiness!.Proof.ObservedUtc, outbound.SourceCapturedUtc);
                Assert.Equal(receiveState.Readiness!.Proof.ObservedUtc, inbound.SourceCapturedUtc);
                Assert.Same(sendState, pair.State(sender));
                Assert.Same(receiveState, pair.State(receiver));
                Assert.Equal(sent, pair.Sent.ToArray());
                pair.AssertNoFileEffects();
            }
            Assert.Equal("file_tuna_v4", (await StartPreparedAdmission(sender, pair.Token))!.RouteToken);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.RouteToken == "file_tuna_v4", TimeSpan.FromSeconds(3));
            Assert.True(AdmissionLease(sender).IsAdopted);
            Assert.True(AdmissionLease(receiver).IsAdopted);
            foreach (var service in new[] { sender, receiver })
            foreach (var direction in new[] { FileTransferDirection.Outbound, FileTransferDirection.Inbound })
                Assert.False(service.ObserveSessionPreparationForDiagnostics(session, direction, DateTimeOffset.UtcNow).Ready);
        });
}
