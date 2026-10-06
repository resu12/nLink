using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "CoordinatorSnapshots")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferInitialFallbackStartupEvidenceTests : FileTransferOpsScriptsTestBase
{
    private static readonly Lazy<Task<string[]>> Evidence = new(InitialFallbackStartupFixture.CreateAsync);
    private static readonly Lazy<Task<string[]>> CorrelatedEvidence = new(() => InitialFallbackStartupFixture.CreateCorrelatedAsync());

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task InitialFallbackStartup_ExactBindingAndOwnerSuccessorPassWithFailClosedControls(string host)
        => await RunServiceAsync(host, correlated: false);

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task InitialFallbackStartup_CorrelatedServiceAgreementPreservesExactSuccessor(string host)
        => await RunServiceAsync(host, correlated: true);

    private static async Task RunServiceAsync(string host, bool correlated)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = Path.Combine(FindRepoRoot(), "artifacts", correlated ? "initial-fallback-agreement-implementation-20260924" : "initial-fallback-successor-20260923", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "service-evidence.log");
        await File.WriteAllLinesAsync(path, await (correlated ? CorrelatedEvidence : Evidence).Value);
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
            "Support", "InitialFallbackStartupEvidence.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path, "False", correlated.ToString()], host);
        await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("initial-fallback-startup-evidence-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task InitialFallbackStartup_IndependentOwnerLegAndPublicationGenerationsRemainExact(string host)
        => await RunCoreAsync(host, correlated: false);

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task InitialFallbackStartup_CorrelatedCoreAgreementPreservesIndependentGenerations(string host)
        => await RunCoreAsync(host, correlated: true);

    private static async Task RunCoreAsync(string host, bool correlated)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = Path.Combine(FindRepoRoot(), "artifacts", correlated ? "initial-fallback-agreement-implementation-20260924" : "initial-fallback-successor-20260923",
            "independent-" + host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var lines = new List<string>();
        foreach (var direction in new[] { FileTransferDirection.Inbound, FileTransferDirection.Outbound })
        {
            var envelopes = FileTransferCoordinatorTests.CreateInitialFallbackPublicationSnapshotFixture(
                direction, independentGenerations: true, publicationCount: 1, startup: true, correlated: correlated);
            // Actual Core snapshots and production emitter; the forwarding call
            // here is modeled, separate from the real-service fixture above.
            lines.AddRange(FileTransferInitialRegularRouteEvidenceTests.Emit(envelopes, (type, emitter, envelope) =>
            {
                if (envelope.DecisionReason != "post_tuna_fallback_v6_route") { return; }
                var publication = Assert.Single(envelope.Aggregate.Operations, operation =>
                    operation.OperationRole == "route_publication" && operation.AfterState == "Published");
                type.GetMethod("EnqueueAuthorityEffect", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(emitter, [envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId, direction,
                        envelope.Aggregate.AfterStateGeneration, publication.Family, publication.AuthorityId,
                        publication.OperationGeneration, publication.ReceiptIdentity, publication.ReceiptGeneration,
                        publication.ReceiptStatus, publication.ReceiptFingerprint, "route_publication_forwarded",
                        publication.ReceiptIdentity, publication.ReceiptGeneration, null]);
            }).Select(line => line.Replace("initial-regular-writer", "initial-fallback-" + direction, StringComparison.Ordinal)));
        }
        var path = Path.Combine(root, "core-emitter-evidence.log");
        await File.WriteAllLinesAsync(path, lines);
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "nLink.SmokeTests.Core",
            "Support", "InitialFallbackStartupEvidence.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path, "True", correlated.ToString()], host);
        await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("initial-fallback-startup-evidence-ok", result.Stdout, StringComparison.Ordinal);
    }
}

internal sealed class InitialFallbackStartupFixture : SessionFileTransferServiceTestBase
{
    internal static Task<string[]> CreateAsync() => CreateAsync(correlated: false);
    internal static Task<string[]> CreateCorrelatedAsync() => CreateAsync(correlated: true);

    private sealed class CorrelatedLoopbackTransport(string sessionId, bool correlated)
        : LoopbackFileTransferTransport(sessionId), IFileTransferRouteTransitionBridge, IFileTransferSignalingTransport
    {
        private long sessionGeneration;
        bool IFileTransferRouteTransitionBridge.SupportsFallbackBootstrapCorrelation => correlated;

        async Task<IFileTransferDataSession> IFileTransferSignalingTransport.OpenFileTransferDataSessionAsync(
            string session, string transfer, CancellationToken ct)
            => new StartupDataSession(await OpenFileTransferDataSessionAsync(session, transfer, ct),
                Interlocked.Increment(ref sessionGeneration));
    }

    // The provider generation binds fallback writes to this opened session, not to transport observations.
    private sealed class StartupDataSession(IFileTransferDataSession inner, long generation)
        : IFileTransferDataSession, IFileTransferDataPathAuthoritySession, IFileTransferDataSessionGenerationProvider
    {
        public string SessionId => inner.SessionId;
        public string TransferId => inner.TransferId;
        public long Generation => generation;
        public bool IsAvailable => inner.IsAvailable;
        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add => inner.AvailabilityChanged += value;
            remove => inner.AvailabilityChanged -= value;
        }
        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct) => inner.ReceiveAsync(ct);
        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
            => inner.ReceiveWithMetadataAsync(ct);
        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct) => inner.SendAsync(frame, ct);
        public Task SendAsync(FileTransferDataFrame frame, FileTransferDataPathSendAuthority authority, CancellationToken ct)
            => ((IFileTransferDataPathAuthoritySession)inner).SendAsync(frame, authority, ct);
        public void Dispose() => inner.Dispose();
    }

    private static async Task<string[]> CreateAsync(bool correlated)
    {
        const string transferId = "initial_fallback_startup";
        const string sessionId = "session_initial_fallback_startup";
        var payload = Enumerable.Range(0, 128_000).Select(index => (byte)(index % 251)).ToArray();
        using var senderTransport = new CorrelatedLoopbackTransport(sessionId, correlated)
        {
            IsFileTunaActiveForRouteSelection = false,
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        using var receiverTransport = new CorrelatedLoopbackTransport(sessionId, correlated)
        {
            IsFileTunaActiveForRouteSelection = false,
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();
        var cursor = GetOperationalLogLength();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("startup.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20_000);
        }
        catch (TaskCanceledException)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(SessionFileTransferService);
            lock (type.GetField("gate", flags)!.GetValue(receiver)!)
            {
                var context = type.GetField("inboundTransfer", flags)!.GetValue(receiver)!;
                var session = context.GetType().GetProperty("DataSession")!.GetValue(context);
                var capture = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(method =>
                    method.Name == "CreateCoordinatorStateLocked" && method.GetParameters()[0].ParameterType == context.GetType());
                var state = Assert.IsType<FileTransferCoordinatorState>(capture.Invoke(null, [context]));
                throw new Xunit.Sdk.XunitException(System.Text.Json.JsonSerializer.Serialize(new
                {
                    correlated, sender = sender.Snapshot.Outbound, receiver = receiver.Snapshot.Inbound,
                    sessionType = session?.GetType().Name,
                    providerGeneration = (session as IFileTransferDataSessionGenerationProvider)?.Generation,
                    state.CurrentDataSessionIdentity, state.CurrentDataSessionGeneration,
                    state.CurrentRuntimeOwner, state.CurrentRoutePublicationToken, state.CurrentLeg,
                    writeScope = FileTransferCoordinator.CaptureInboundFallbackRepairWriteScope(state),
                    sentFrames = senderTransport.SentDataFrames.GroupBy(frame => frame.Type)
                        .ToDictionary(group => group.Key, group => group.Count()),
                }));
            }
        }
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        sender.Dispose();
        receiver.Dispose();
        await WaitUntilAsync(() => ReadOperationalLogTail(cursor).Split('\n').Count(line =>
            line.Contains("event=filetransfer_coordinator_snapshot_stream_completed;", StringComparison.Ordinal)) == 2);
        // Unmodified production service/emitter evidence. The loopback is not
        // a live-network or route-reactivation acceptance claim.
        return ReadOperationalLogTail(cursor).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }
}
