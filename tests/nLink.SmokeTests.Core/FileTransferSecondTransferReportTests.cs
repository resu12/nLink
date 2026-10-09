using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferSecondTransferReportTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", false)]
    [InlineData("pwsh", false)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", true)]
    public async Task SecondTransferReport_FreshCoordinatorStreamsAreReused(string host, bool delayedTerminalSnapshot)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var dir = Path.Combine(FindRepoRoot(), "artifacts", "second-transfer-report-implementation-20260924", "fresh", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var transfer in new[] { "first", "second" })
        foreach (var direction in new[] { FileTransferDirection.Outbound, FileTransferDirection.Inbound })
        {
            var lines = FileTransferCoordinatorTests.CreateSecondTransferReportStream(transfer, direction);
            await File.WriteAllLinesAsync(Path.Combine(dir, $"{transfer}-{direction}.log"), lines, new UTF8Encoding(false));
        }
        const string script = """
            param([string]$RepoRoot, [string]$Directory, [string]$DelayedTerminalSnapshot)
            $ErrorActionPreference = 'Stop'
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/SecondTransferReportFresh.ps1') -RepoRoot $RepoRoot -Directory $Directory -DelayedTerminalSnapshot ($DelayedTerminalSnapshot -ceq 'true')
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), dir, delayedTerminalSnapshot ? "true" : "false"], host, initializeHostModulePath: true);
        Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("second-transfer-fresh-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task SecondTransferReport_DelegatedOwnerRequestsOnePrimaryAnalysis(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference = 'Stop'
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/SecondTransferReportOwnership.ps1') -RepoRoot $RepoRoot
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host, initializeHostModulePath: true);
        Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("second-transfer-owner-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task SecondTransferReport_UsesPrimaryViewsAndFailsClosed(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference = 'Stop'
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/SecondTransferReport.ps1') -RepoRoot $RepoRoot
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host, initializeHostModulePath: true);
        Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("second-transfer-report-ok", result.Stdout, StringComparison.Ordinal);
    }
}

public sealed partial class FileTransferCoordinatorTests
{
    internal static string[] CreateSecondTransferReportStream(string transfer, FileTransferDirection direction)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = typeof(SessionFileTransferService).GetNestedType("CoordinatorSnapshotStreamEmitter", BindingFlags.NonPublic)!;
        var emitter = Activator.CreateInstance(type, nonPublic: true)!;
        type.GetField("drainScheduled", flags)!.SetValue(emitter, 1);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var initial = EmptyState(tuna) with { SessionId = "second-report-session", TransferId = transfer, Direction = direction };
        var id = $"report-{transfer}-{direction}";
        initial = initial with { PersistentState = initial.PersistentState with { CoordinatorInstanceId = id } };
        var started = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer, tuna,
            targetTransport: FileTransferTransportKind.Tuna, initialTunaReadinessEvidence: InitialTunaEvidence(initial)), initial);
        var start = (State: started.State, Envelope: started.SnapshotEnvelope!);
        var state = start.State;
        Assert.Equal(FileTransferRoute.FileTunaV4, state.RouteSelection.Route);
        var owner = state.CurrentRuntimeOwner!.Value;
        var publication = FileTransferCoordinator.AuthorizeAction(state, new FileTransferCoordinatorActionRequest(
            FileTransferCoordinatorActionKind.RoutePublication, Source: "report_fixture", Reason: "route_publication",
            RuntimeOwner: owner, Route: state.RouteSelection.Route, ProtocolVersion: state.RouteSelection.ProtocolVersion,
            LiveRouteEpoch: state.CurrentLeg!.LiveRouteEpochId, LegGeneration: state.CurrentLeg.Generation,
            RuntimeGeneration: owner.RuntimeGeneration, TerminalGeneration: state.TerminalGeneration));
        var published = (State: publication.State, Envelope: publication.SnapshotEnvelope!);
        var token = publication.RoutePublicationToken!.Value;
        var stopped = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, tuna, reason: "transfer_terminal"), published.State);
        var terminal = (State: stopped.State, Envelope: stopped.SnapshotEnvelope!);
        Assert.Equal(true, type.GetMethod("Start", flags)!.Invoke(emitter,
            [id, initial.SessionId, transfer, direction, start.Envelope.Aggregate.BeforeStateGeneration, start.Envelope.Aggregate.BeforeStateFingerprint]));
        type.GetMethod("Enqueue", flags)!.Invoke(emitter, [start.Envelope]);
        type.GetMethod("Enqueue", flags)!.Invoke(emitter, [published.Envelope]);
        // Modeled physical publication receipt, emitted by the production projector.
        // This is fresh analyzer input, not a live network or adapter claim.
        type.GetMethod("EnqueueAuthorityEffect", flags)!.Invoke(emitter,
            [id, initial.SessionId, transfer, direction, published.Envelope.Aggregate.AfterStateGeneration,
                "route_runtime", $"publication:{token.PublicationGeneration}", token.PublicationGeneration,
                $"publication:{token.PublicationGeneration}", token.PublicationGeneration, "Accepted",
                FileTransferCoordinator.CalculateSnapshotFingerprint(token), "route_publication_forwarded",
                $"publication:{token.PublicationGeneration}", token.PublicationGeneration, null]);
        type.GetMethod("Enqueue", flags)!.Invoke(emitter, [terminal.Envelope]);
        type.GetMethod("Complete", flags)!.Invoke(emitter, [id, initial.SessionId, transfer, direction]);
        return ((ConcurrentQueue<string>)type.GetField("pending", flags)!.GetValue(emitter)!)
            .Select(SensitiveDataRedactor.Redact).ToArray();
    }
}
