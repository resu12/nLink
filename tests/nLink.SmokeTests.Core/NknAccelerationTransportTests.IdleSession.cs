using System.Collections.Concurrent;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
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
