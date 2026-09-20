using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("busy")]
    [InlineData("correlation-rejected")]
    public async Task CoordinatorRetirement_UninstalledInboundStreamCompletes(string variant)
    {
        using var transport = new LoopbackFileTransferTransport("retirement-session");
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        if (variant == "busy")
        {
            var initial = GetOperationalLogLength();
            await Offer(service, "retirement-active");
            await WaitUntilAsync(() => ReadOperationalLogTail(initial).Contains("event=filetransfer_coordinator_snapshot_stream_started;"));
        }
        var active = variant == "busy" ? CurrentInbound(service) : null;
        var activeState = active is null ? null : GetPrivatePropertyValue(active, "CoordinatorPersistentState");
        var before = GetOperationalLogLength();
        await Offer(service, "retirement-discarded", variant == "correlation-rejected");
        await WaitUntilAsync(() => ReadOperationalLogTail(before).Contains("event=filetransfer_coordinator_snapshot_stream_started;"));
        var start = Assert.Single(ReadOperationalLogTail(before).Split('\n'), line => line.Contains("event=filetransfer_coordinator_snapshot_stream_started;"));
        var instance = GetDelimitedLogField(start, "coordinator_instance_id");
        Assert.True(SpinWait.SpinUntil(() => ReadOperationalLogTail(before).Split('\n').Any(line =>
            line.Contains("event=filetransfer_coordinator_snapshot_stream_completed;") &&
            GetDelimitedLogField(line, "coordinator_instance_id") == instance), TimeSpan.FromSeconds(2)),
            "The offer handler left its uninstalled coordinator stream open");
        if (variant == "busy")
        {
            Assert.Equal("retirement-active", service.Snapshot.Inbound!.TransferId);
            Assert.Single(transport.SentDeclines);
            Assert.Same(active, CurrentInbound(service));
            Assert.Equal(activeState, GetPrivatePropertyValue(active!, "CoordinatorPersistentState"));
            Assert.False(EmitterFinished(active!, TimeSpan.Zero));
            service.Dispose();
            Assert.True(EmitterFinished(active!, TimeSpan.FromSeconds(2)));
        }
        else { Assert.Null(service.Snapshot.Inbound); }
        Assert.Empty(transport.SentCancels);
        Assert.Empty(transport.SentErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatorRetirement_SetupExceptionClosesUninstalledStream(bool outbound)
    {
        using var transport = new RetirementThrowingTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var before = GetOperationalLogLength();
        if (outbound)
        {
            await Assert.ThrowsAsync<RetirementSetupFailure>(() => service.TryStartSendAsync(
                new FileTransferSendDescriptor("retirement.bin", 1024, "retirement-outbound"),
                _ => Task.FromResult<Stream>(new MemoryStream(new byte[1024])), CancellationToken.None));
        }
        else { await Assert.ThrowsAsync<RetirementSetupFailure>(() => Offer(service, "retirement-inbound")); }
        await WaitUntilAsync(() => ReadOperationalLogTail(before).Contains("event=filetransfer_coordinator_snapshot_stream_started;"));
        Assert.True(SpinWait.SpinUntil(() => ReadOperationalLogTail(before).Contains(
            "event=filetransfer_coordinator_snapshot_stream_completed;"), TimeSpan.FromSeconds(2)),
            "A setup exception orphaned its original stream");
        Assert.Null(service.Snapshot.Inbound);
        Assert.Null(service.Snapshot.Outbound);
    }

    private sealed class RetirementSetupFailure : Exception;
    private sealed class RetirementThrowingTransport() : LoopbackFileTransferTransport("retirement-session"), IFileTransferRouteTransitionBridge
    {
        bool IFileTransferRouteTransitionBridge.SupportsFallbackBootstrapCorrelation => throw new RetirementSetupFailure();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatorRetirement_OfferLosesInstallationRace(bool dispose)
    {
        using var transport = new RetirementCallbackTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.BeforeCorrelationCheck = () =>
        {
            if (dispose) { service.Dispose(); }
            else { Offer(service, "retirement-race").GetAwaiter().GetResult(); }
        };
        var before = GetOperationalLogLength();
        await Offer(service, "retirement-race");
        Assert.True(SpinWait.SpinUntil(() => ReadOperationalLogTail(before).Contains(
            "event=filetransfer_coordinator_snapshot_stream_completed;"), TimeSpan.FromSeconds(2)));
        var lines = ReadOperationalLogTail(before).Split('\n');
        Assert.Equal(dispose ? 1 : 2, lines.Count(l => l.Contains("event=filetransfer_coordinator_snapshot_stream_started;")));
        Assert.Single(lines, l => l.Contains("event=filetransfer_coordinator_snapshot_stream_completed;"));
        if (dispose) { Assert.Null(service.Snapshot.Inbound); }
        else
        {
            var surviving = CurrentInbound(service);
            Assert.False(EmitterFinished(surviving, TimeSpan.Zero));
            Assert.Equal("retirement-race", service.Snapshot.Inbound!.TransferId);
            service.Dispose();
            Assert.True(EmitterFinished(surviving, TimeSpan.FromSeconds(2)));
        }
        Assert.Empty(transport.SentDeclines);
        Assert.Empty(transport.SentCancels);
        Assert.Empty(transport.SentErrors);
    }

    private sealed class RetirementCallbackTransport() : LoopbackFileTransferTransport("retirement-session"), IFileTransferRouteTransitionBridge
    {
        public Action? BeforeCorrelationCheck { get; set; }
        bool IFileTransferRouteTransitionBridge.SupportsFallbackBootstrapCorrelation
        {
            get
            {
                var callback = BeforeCorrelationCheck;
                BeforeCorrelationCheck = null;
                callback?.Invoke();
                return false;
            }
        }
    }

    [Fact]
    public async Task CoordinatorRetirement_TerminalInboundReplacementClosesOnlyRetiredStream()
    {
        using var transport = new LoopbackFileTransferTransport("retirement-session");
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        await Offer(service, "retirement-old");
        var retired = CurrentInbound(service);
        await service.DeclineIncomingTransferAsync("retirement-old", "fixture", CancellationToken.None);
        await Offer(service, "retirement-new");
        Assert.True(EmitterFinished(retired, TimeSpan.FromSeconds(2)), "Replacing a terminal context lost its original stream");
        var current = CurrentInbound(service);
        Assert.NotSame(retired, current);
        Assert.Equal("retirement-new", service.Snapshot.Inbound!.TransferId);
        Assert.False(EmitterFinished(current, TimeSpan.Zero));
        service.ResetSessionState();
        Assert.True(EmitterFinished(current, TimeSpan.FromSeconds(2)));
    }

    private static object CurrentInbound(SessionFileTransferService service)
        => typeof(SessionFileTransferService).GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static bool EmitterFinished(object context, TimeSpan timeout)
    {
        var emitter = GetPrivatePropertyValue(context, "CoordinatorSnapshotEmitter")!;
        return (bool)emitter.GetType().GetMethod("WaitForFinalSubmission", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(emitter, [timeout])!;
    }

    private static Task Offer(SessionFileTransferService service, string transferId, bool invalidCorrelation = false)
        => (Task)typeof(SessionFileTransferService).GetMethod("HandleIncomingOfferAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [new FileTransferOfferV2
            {
                SessionId = "retirement-session", TransferId = transferId, FileName = "retirement.bin", FileSizeBytes = 1024,
                PreferredDataProtocolVersion = 4, FileTransferRoute = "regular_nkn_v4_fast",
                FallbackBootstrapCorrelationVersion = invalidCorrelation ? 1 : null,
                FallbackBootstrapIssuerId = invalidCorrelation ? "invalid" : null,
            }])!;
}
