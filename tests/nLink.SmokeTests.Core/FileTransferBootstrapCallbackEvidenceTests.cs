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
    [Theory]
    [InlineData("current")]
    [InlineData("held")]
    [InlineData("replacement")]
    public void BootstrapSourceCallback_SnapshotPreservesCapturedProviderWhenCurrentHeldOrReplaced(string lifetime)
    {
        var state = CreateCorrelatedBootstrapPair(providerGeneration: 7).Sender;
        var callback = FileTransferCoordinator.IssueFallbackBootstrapSourceCallback(state, 71, 7, false, 0)!;
        Assert.NotNull(callback);
        var identity = callback.Identity;
        var admitted = FileTransferCoordinator.CreateBootstrapSourceCallbackSnapshot(state, callback, "Admitted",
            FileTransferBootstrapSourceCallbackDisposition.Current, "none");
        var originalJson = admitted.Envelope.ToCanonicalJson();
        state = admitted.State;
        if (lifetime == "held")
        {
            var observation = StartCorrelated(state).Command!.Bootstrap.Observation;
            var prepared = FileTransferCoordinator.PrepareFallbackBootstrapSourceHold(state, observation, null);
            Assert.True(prepared.Accepted);
            var capture = new FileTransferBootstrapSourceHoldCapture(prepared.Command!, 99, [], [], []) { OutboundCallbacks = [callback] };
            var applied = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldCapture(prepared.State, capture);
            Assert.True(applied.Accepted);
            state = applied.State;
        }
        else if (lifetime == "replacement")
        {
            // Replacement state is evidence only; never patch the issued callback.
            state = state with { CurrentDataSessionProviderGeneration = 99 };
        }
        var disposition = FileTransferCoordinator.ResolveFallbackBootstrapSourceCallback(state, callback,
            lifetime == "replacement" ? 99 : 7, lifetime == "held", lifetime == "held" ? 99 : 0);
        Assert.Equal(lifetime == "current" ? FileTransferBootstrapSourceCallbackDisposition.Current
            : lifetime == "held" ? FileTransferBootstrapSourceCallbackDisposition.Held
            : FileTransferBootstrapSourceCallbackDisposition.Stale, disposition);
        var snapshot = lifetime == "current" ? admitted : FileTransferCoordinator.CreateBootstrapSourceCallbackSnapshot(
            state, callback, "Consumed", disposition, "none");
        var operation = Assert.Single(snapshot.Envelope.Aggregate.Operations);
        var route = operation.RouteTransition!;
        Assert.Equal(7, route.SourceBoundary.DataSessionProviderGeneration);
        Assert.Equal(route.SourceBoundary, route.TargetBoundary);
        Assert.Equal(17, route.SourceBoundary.DataSessionGeneration);
        Assert.Equal(callback.DataSessionIdentity, route.SourceBoundary.DataSessionIdentity);
        Assert.Equal(lifetime == "replacement" ? 99 : 7, snapshot.Envelope.Aggregate.BeforeBoundary.DataSessionProviderGeneration);
        Assert.Equal(7, route.BootstrapCallbacks!.ProviderGeneration);
        Assert.Equal("none", route.BootstrapCallbacks.AllowedEffect);
        Assert.False(operation.StateApplied);
        Assert.False(operation.ReceiptValidated);
        Assert.Equal(identity, callback.Identity);
        Assert.Equal(originalJson, admitted.Envelope.ToCanonicalJson());
        Assert.Contains("\"dataSessionProviderGeneration\":7", snapshot.Envelope.ToCanonicalJson(), StringComparison.Ordinal);
        Assert.Equal(state.SessionId, snapshot.State.SessionId);
        Assert.Equal(state.TransferId, snapshot.State.TransferId);
        Assert.Equal(state.CurrentRoutePublicationToken, snapshot.State.CurrentRoutePublicationToken);
    }

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
