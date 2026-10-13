using System.Reflection;
using System.Text;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferBootstrapCorrelationEvidenceTests
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task BootstrapCorrelation_CompletedSecondExchangeOwnsOnlyItsExactLegacyDiagnostic(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "bootstrap-ownership-fix-20260912", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "modeled-second-exchange.log");
        await File.WriteAllLinesAsync(path, Emit(FileTransferCoordinatorTests.CreateBootstrapSuccessorHistory(outbound: false),
            modeledOwnershipEffects: true), new UTF8Encoding(false));
        const string script = """
            param([string]$RepoRoot, [string]$InputPath)
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/BootstrapCorrelationOwnership.ps1') -RepoRoot $RepoRoot -InputPath $InputPath
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path], host);
        Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("bootstrap-ownership-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("powershell", "hold_invalidated", false, true)]
    [InlineData("pwsh", "hold_invalidated", false, true)]
    [InlineData("powershell", "committed_cleanup", true, true)]
    [InlineData("pwsh", "committed_cleanup", true, true)]
    [InlineData("powershell", "committed", true, false)]
    [InlineData("pwsh", "committed", true, false)]
    [InlineData("powershell", "recovered", true, false)]
    [InlineData("pwsh", "recovered", true, false)]
    [InlineData("powershell", "recovered_cleanup", true, true)]
    [InlineData("pwsh", "recovered_cleanup", true, true)]
    public async Task BootstrapCorrelation_SourceHoldInvalidationRespectsCommittedConsumption(
        string host, string scenario, bool commitSuccessor, bool invalidateSourceHold)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(FindRepoRoot(), "artifacts", "bootstrap-ownership-fix-20260912", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, scenario + ".log");
        await File.WriteAllLinesAsync(path, Emit(FileTransferCoordinatorTests.CreateBootstrapSuccessorHistory(
            outbound: false, commitSuccessor: commitSuccessor, invalidateSourceHold: invalidateSourceHold),
            modeledOwnershipEffects: true, modeledFinalPublication: scenario.StartsWith("recovered", StringComparison.Ordinal)), new UTF8Encoding(false));
        const string script = """
            param([string]$RepoRoot, [string]$InputPath, [string]$Scenario)
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/BootstrapCorrelationOwnership.ps1') -RepoRoot $RepoRoot -InputPath $InputPath -Scenario $Scenario
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), path, scenario], host);
        Assert.True(result.ExitCode == 0, $"{host}/{scenario}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("bootstrap-ownership-ok", result.Stdout, StringComparison.Ordinal);
    }

    private static void EmitModeledOwnershipEffects(Type type, object emitter, FileTransferCoordinatorTransitionEnvelope envelope)
    {
        // Modeled adapter effects from real Coordinator receipts. This fixture is
        // analyzer input, not a claim that physical callbacks or a live transfer ran.
        foreach (var operation in envelope.Aggregate.Operations)
        {
            string? kind = null;
            var receipts = operation.ResponderProbeHandoff?.Receipts;
            if (receipts is not null)
            {
                kind = operation.OperationRole == "route_transition_handoff_pending" ? "route_transition_handoff_supplied"
                    : operation.ReceiptStatus == "Invalidated" ? "handoff_invalidated"
                    : envelope.DecisionKind == "BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff" ? "handoff_bound"
                    : envelope.DecisionKind == "HoldRuntimeUnlockResponderPeerProbeRouteTransitionHandoffForOwnerWork" ? "handoff_held"
                    : "convergence_observed";
            }
            else if (operation.OperationRole == "scope_invalidation" && operation.BeforeState != operation.AfterState && operation.AfterState == "Applied")
            {
                kind = "scope_invalidation_applied";
                receipts = operation.RuntimeUnlockRetry!.Receipts;
            }
            if (kind is null || receipts is null) { continue; }
            foreach (var receipt in receipts.Value.Where(receipt => receipt.ReceiptValidated))
            {
                type.GetMethod("EnqueueAuthorityEffect", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(emitter,
                    [envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId, FileTransferDirection.Inbound,
                        envelope.Aggregate.AfterStateGeneration, operation.Family, operation.AuthorityId,
                        operation.OperationGeneration, receipt.ReceiptIdentity, receipt.ReceiptGeneration,
                        receipt.ReceiptStatus, receipt.ReceiptFingerprint, kind,
                        $"{operation.OperationKey}:{kind}", Math.Max(1, receipt.ReceiptGeneration), null]);
            }
        }
    }
}
