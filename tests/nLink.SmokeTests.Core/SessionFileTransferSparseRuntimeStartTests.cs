using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseRuntimeStart_RecordsEachCommittedPublicationOnce()
    {
        const string transferId = "sparse_runtime_start_publications";
        using var harness = new SparseWorkerHarness(transferId);
        SetPrivateProperty(harness.Context, "UserPaused", true);
        var logStart = GetOperationalLogLength();
        object? predecessor = null;
        var publications = new List<RoutePublicationToken>();
        foreach (var route in new[] { FileTransferRoute.FileTunaV4, FileTransferRoute.PostTunaFallbackV6, FileTransferRoute.FileTunaV4 })
        {
            var publication = ReplaceSparseRouteAuthority(harness.Context, route);
            publications.Add(publication);
            var pending = StageSparsePumpInstall(harness.Service, harness.Context, predecessor, "unit_test_runtime_start");
            Assert.True(pending.Accepted, pending.Reason);
            var committed = CommitSparsePumpInstall(harness.Service, harness.Context, pending.Install!, harness.DataSession);
            Assert.True(committed.Accepted, committed.Reason);
            Assert.Equal(publications.Count - 1, SparseRuntimeStartLines(logStart, transferId).Length);

            await RunPausedSparsePumpForRuntimeStartAsync(harness, committed.Install!);
            await RunPausedSparsePumpForRuntimeStartAsync(harness, committed.Install!);
            var lines = SparseRuntimeStartLines(logStart, transferId);
            Assert.Equal(publications.Count, lines.Length);
            Assert.Contains($"route={publication.RouteToken};", lines[^1], StringComparison.Ordinal);
            Assert.Contains($"route_publication_generation={publication.PublicationGeneration};", lines[^1], StringComparison.Ordinal);
            Assert.Equal(publication, GetPrivateProperty<RoutePublicationToken>(harness.Context, "CurrentRoutePublicationToken"));
            predecessor = committed.Install;
        }
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("unbound")]
    [InlineData("canceled")]
    [InlineData("route_publication")]
    [InlineData("runtime_owner")]
    [InlineData("leg")]
    [InlineData("data_session_generation")]
    [InlineData("terminal")]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseRuntimeStart_UncommittedOrStaleInstallCannotPublish(string invalidation)
    {
        var transferId = $"sparse_runtime_start_rejected_{invalidation}";
        using var harness = new SparseWorkerHarness(transferId);
        SetPrivateProperty(harness.Context, "UserPaused", true);
        ReplaceSparseRouteAuthority(harness.Context, FileTransferRoute.FileTunaV4);
        var pending = StageSparsePumpInstall(harness.Service, harness.Context, null, "unit_test_runtime_start");
        Assert.True(pending.Accepted, pending.Reason);
        if (invalidation != "pending")
        {
            Assert.True(CommitSparsePumpInstall(harness.Service, harness.Context, pending.Install!, harness.DataSession).Accepted);
            if (invalidation == "data_session_generation")
            {
                SetPrivateProperty(harness.Context, "DataSessionGeneration",
                    GetPrivateProperty<long>(harness.Context, "DataSessionGeneration") + 1);
            }
            else if (invalidation is not "unbound" and not "canceled")
            {
                InvalidateSparsePumpInstallTuple(harness, invalidation);
            }
        }

        var logStart = GetOperationalLogLength();
        await RunPausedSparsePumpForRuntimeStartAsync(harness,
            invalidation == "unbound" ? null : pending.Install!, invalidation == "canceled");
        Assert.Empty(SparseRuntimeStartLines(logStart, transferId));
        Assert.Empty(harness.DataSession.SentFrames);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseRuntimeStart_StartupDeduplicatesButStillValidatesAuthority()
    {
        const string transferId = "sparse_runtime_start_startup";
        using var harness = new SparseWorkerHarness(transferId);
        SetPrivateProperty(harness.Context, "UserPaused", true);
        var publication = ReplaceSparseRouteAuthority(harness.Context, FileTransferRoute.FileTunaV4);
        var publish = typeof(SessionFileTransferService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "TryPublishRuntimeStartedIfAuthorizedLocked" &&
                method.GetParameters()[0].ParameterType == harness.Context.GetType());
        var logStart = GetOperationalLogLength();
        lock (GetServiceGate(harness.Service))
        {
            Assert.True((bool)publish.Invoke(harness.Service,
                [harness.Context, "sender", "unit_test_startup", "runtime_started", null, publication])!);
        }
        var pending = StageSparsePumpInstall(harness.Service, harness.Context, null, "unit_test_runtime_start");
        Assert.True(pending.Accepted, pending.Reason);
        Assert.True(CommitSparsePumpInstall(harness.Service, harness.Context, pending.Install!, harness.DataSession).Accepted);
        await RunPausedSparsePumpForRuntimeStartAsync(harness, pending.Install!);
        Assert.Single(SparseRuntimeStartLines(logStart, transferId));

        lock (GetServiceGate(harness.Service))
        {
            ReplaceSparseAuthorityRetainingOwner(harness.Context, "route_publication");
            Assert.False((bool)publish.Invoke(harness.Service,
                [harness.Context, "sender", "unit_test_stale_startup", "runtime_started", null, publication])!);
        }
        Assert.Single(SparseRuntimeStartLines(logStart, transferId));
    }

    private static async Task RunPausedSparsePumpForRuntimeStartAsync(
        SparseWorkerHarness harness, object? install, bool cancelBeforeStart = false)
    {
        using var source = new MemoryStream(new byte[256 * 21 * 1024], writable: false);
        using var cancellation = new CancellationTokenSource();
        if (cancelBeforeStart) cancellation.Cancel();
        var pump = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
            .GetMethod("RunOutboundV4SenderPumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(harness.Service, [harness.Context, source, harness.DataSession, cancellation.Token, install]));
        cancellation.Cancel();
        try
        {
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private static string[] SparseRuntimeStartLines(int logStart, string transferId)
        => ReadOperationalLogTail(logStart).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("event=filetransfer_runtime_started; direction=outbound;", StringComparison.Ordinal) &&
                line.Contains($"transfer_id={transferId};", StringComparison.Ordinal))
            .ToArray();
}
