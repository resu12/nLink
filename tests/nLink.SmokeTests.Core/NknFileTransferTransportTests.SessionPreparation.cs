using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    [Theory]
    [InlineData("reset")]
    [InlineData("unbound_install")]
    [Trait("FileTransferInfraSuite", "SessionPreparationLifetime")]
    public async Task PreparationLifetime_UsesActualVerifiedKeyInstallationAndRetirement(string change)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.preparation-lifetime.address");
            var helperClient = new FakeNknClient("helper.preparation-lifetime.address");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("prep-host", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("prep-helper", helperClient.Address));
            using var hostService = new SessionFileTransferService();
            using var helperService = new SessionFileTransferService();
            hostService.AttachTransport(host);
            helperService.AttachTransport(helper);
            var hostLifecycle = ((IFileTransferSessionPreparationTransport)host).SessionPreparationLifecycle;
            var helperLifecycle = ((IFileTransferSessionPreparationTransport)helper).SessionPreparationLifecycle;
            Assert.Null(hostLifecycle.Current);
            Assert.Null(helperLifecycle.Current);
            var sessionId = await ApproveNknSessionAsync(host, helper, ct.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
            Assert.NotEqual(hostLifecycle.Current!.Id, helperLifecycle.Current!.Id);
            foreach (var transport in new[] { host, helper })
            {
                var lifecycle = ((IFileTransferSessionPreparationTransport)transport).SessionPreparationLifecycle;
                var epoch = lifecycle.Current!;
                var attachment = lifecycle.Attachment!;
                Assert.True(epoch.IsCurrent);
                Assert.Equal(sessionId, epoch.SessionId);
                Assert.Equal(transport.CurrentSessionSecurityState, epoch.Security);
                var begin = attachment.Authority.Begin(attachment, epoch, "lifetime-episode", 1,
                    ReferenceEquals(transport, host), DateTimeOffset.UtcNow.AddSeconds(30));
                Assert.Equal(FileTransferSessionPreparationStatus.Accepted, begin.Status);
                var permit = begin.Permit!;
                var update = typeof(NknSignalingTransport).GetMethod("UpdateSessionSecurityState", BindingFlags.Instance | BindingFlags.NonPublic)!;
                update.Invoke(transport, [transport.CurrentSessionSecurityState with { }]);
                Assert.Same(epoch, lifecycle.Current);
                Assert.True(attachment.Authority.IsCurrent(attachment, epoch, permit));

                // Optional diagnostic retirement is not this runtime lifetime.
                var diagnostic = typeof(NknSignalingTransport).GetField("fileTransferSecurityContext", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
                diagnostic.GetType().GetField("Retired")!.SetValue(diagnostic, true);
                Assert.True(attachment.Authority.IsCurrent(attachment, epoch, permit));

                var method = change == "reset" ? "ResetControlSecureState" : "SetControlSessionSharedKey";
                typeof(NknSignalingTransport).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(transport, change == "reset" ? null : [new byte[32]]);
                Assert.False(epoch.IsCurrent);
                Assert.Null(lifecycle.Current);
                Assert.False(attachment.Authority.IsCurrent(attachment, epoch, permit));
                update.Invoke(transport, [transport.CurrentSessionSecurityState]);
                Assert.Null(lifecycle.Current); // A repeated approved snapshot is not a key install.
            }
            Assert.Null(hostService.Snapshot.Outbound);
            Assert.Null(helperService.Snapshot.Inbound);
        }
        finally { FakeNknClient.ResetNetwork(); }
    }
}
