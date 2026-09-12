using System.Text;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferBootstrapCorrelationEvidenceTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BootstrapCallbacks_SourceHoldHasFullCompactGuiParity(bool nextCycle)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "validation", "consolidation-bounded-20260907",
            "bootstrap-correlation-implementation", "callback-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "source-callback.log");
        await File.WriteAllLinesAsync(path, Emit(nextCycle ? FileTransferCoordinatorTests.CreateBootstrapCallbackNextCycleEvidence()
            : FileTransferCoordinatorTests.CreateBootstrapInitiationSnapshotFixture(true, true, true)), new UTF8Encoding(false));
        const string script = """
            param([string]$RepoRoot, [string]$InputPath, [string]$NextCycle)
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/BootstrapCallbackEvidence.ps1') -RepoRoot $RepoRoot -InputPath $InputPath -ModeledSuccessor:($NextCycle -eq 'true')
            """;
        string? firstOutput = null;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path, nextCycle ? "true" : "false"], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            var normalized = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()).Where(line =>
                line.StartsWith("bootstrap-callback-normalized=", StringComparison.Ordinal)));
            if (firstOutput is not null) { Assert.Equal(firstOutput, normalized); }
            firstOutput = normalized;
        }
    }
}

public sealed partial class FileTransferCoordinatorTests
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState ApplyCallbackBootstrapSourceHoldForTests(FileTransferCoordinatorState state,
        FileTransferFallbackV6ProofBootstrapObservation observation, FileTransferFallbackBootstrapFrameV6? peerWake,
        Action<FileTransferCoordinatorTransitionEnvelope> captureEnvelope)
    {
        var callback = FileTransferCoordinator.IssueFallbackBootstrapSourceCallback(state, 71, 7, false, 0)!;
        Assert.NotNull(callback);
        var admitted = FileTransferCoordinator.CreateBootstrapSourceCallbackSnapshot(state, callback, "Admitted",
            FileTransferBootstrapSourceCallbackDisposition.Current, "none");
        captureEnvelope(admitted.Envelope); state = admitted.State;
        var prepared = FileTransferCoordinator.PrepareFallbackBootstrapSourceHold(state, observation, peerWake);
        Assert.True(prepared.Accepted); captureEnvelope(prepared.SnapshotEnvelope!);
        var capture = new FileTransferBootstrapSourceHoldCapture(prepared.Command!, 99, [], [], []) { OutboundCallbacks = [callback] };
        var applied = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldCapture(prepared.State, capture);
        Assert.True(applied.Accepted); captureEnvelope(applied.SnapshotEnvelope!);
        var drained = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldReceipt(applied.State, new(capture, [], [], [], observation.DurableCheckpoint));
        Assert.True(drained.Accepted); captureEnvelope(drained.SnapshotEnvelope!);
        var consumed = FileTransferCoordinator.CreateBootstrapSourceCallbackSnapshot(drained.State, callback, "Consumed",
            FileTransferBootstrapSourceCallbackDisposition.Held, "none");
        captureEnvelope(consumed.Envelope);
        return consumed.State;
    }
}
