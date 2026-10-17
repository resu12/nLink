using System.Text.Json;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
public sealed class FileTransferBoundaryProviderSchemaTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task BoundaryProviderSchema_PreservesLegacyShapeAndExactProviderCorrelation(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var boundary = new FileTransferCoordinatorStateBoundarySnapshot(
            "post_tuna_fallback_v6", 6, 2, 2, 2, 2, 1, 2, 1, 0, 1, "checkpoint:1", "data-session", 1)
        {
            DataSessionProviderGeneration = 7,
        };
        var directory = Path.Combine(Path.GetTempPath(), "nlink-boundary-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "producer-boundaries.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(
                new[] { boundary with { DataSessionProviderGeneration = 0 }, boundary },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            var result = await RunPowerShellScriptTextAsync(Harness, [FindRepoRoot(), path], host);
            Assert.True(result.ExitCode == 0, $"{host} STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
            Assert.Equal("boundary-provider-schema-ok", result.Stdout.Trim());
        }
        finally { TryDeleteDirectory(directory); }
    }

    private const string Harness = """
        param([string]$RepoRoot, [string]$InputPath)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
        function Copy-Value($Value) { ConvertFrom-Json (ConvertTo-Json -InputObject $Value -Depth 32 -Compress) }
        $parsed = Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json
        $values = @($parsed)
        $legacy = $values[0]; $current = $values[1]
        if (@($legacy.PSObject.Properties).Count -ne 14 -or @($current.PSObject.Properties).Count -ne 15 -or
            $current.dataSessionGeneration -ne 1 -or $current.dataSessionProviderGeneration -ne 7) { throw 'Producer shape changed' }
        foreach ($boundary in $values) {
            if (-not (Test-FileTransferCoordinatorSnapshotBoundarySchema $boundary) -or
                -not (Test-FileTransferCoordinatorSnapshotBoundaryProviderEqual $boundary $boundary)) { throw 'Valid boundary rejected' }
        }
        if (Test-FileTransferCoordinatorSnapshotBoundaryProviderEqual $legacy $current) { throw 'Missing provider matched nonzero provider' }
        $zero = Copy-Value $legacy
        $zero | Add-Member dataSessionProviderGeneration 0
        if (-not (Test-FileTransferCoordinatorSnapshotBoundaryProviderEqual $legacy $zero)) { throw 'Default omission changed meaning' }
        foreach ($invalid in @(-1, '7', 7.5, $true, $null)) {
            $bad = Copy-Value $current
            $bad.dataSessionProviderGeneration = $invalid
            if ((Test-FileTransferCoordinatorSnapshotBoundarySchema $bad) -or
                (Test-FileTransferCoordinatorSnapshotBoundaryProviderEqual $bad $bad)) { throw 'Malformed provider accepted' }
        }
        foreach ($mutation in @('unknown', 'missing_base', 'wrong_case', 'untyped_local')) {
            $bad = Copy-Value $current
            switch ($mutation) {
                'unknown' { $bad | Add-Member unexpectedGeneration 7 }
                'missing_base' { $bad.PSObject.Properties.Remove('dataSessionIdentity') }
                'wrong_case' { $bad.PSObject.Properties.Remove('dataSessionProviderGeneration'); $bad | Add-Member DataSessionProviderGeneration 7 }
                'untyped_local' { $bad.dataSessionGeneration = '1' }
            }
            if (Test-FileTransferCoordinatorSnapshotBoundarySchema $bad) { throw "Malformed boundary accepted: $mutation" }
        }
        # Scope validation controls, not fabricated runtime authority or a live receipt.
        foreach ($boundary in $values) {
            $scope = [pscustomobject]@{
                boundary=$boundary; runtimeOwnerFingerprint=('a' * 64); publicationFingerprint=('b' * 64)
                commitFingerprint='(none)'; bridgeDecisionGeneration=0; aggregateBridgeDecisionGeneration=0
                aggregateTransportEpoch=1; retainedLegCheckpointGeneration=1; retainedLegCheckpointRequestId='checkpoint:1'
                leaseGeneration=1; scopeFingerprint=('c' * 64)
            }
            $cleared = Copy-Value $scope
            $cleared.boundary.checkpointGeneration=0; $cleared.boundary.checkpointRequestId='(none)'
            $cleared.leaseGeneration=0; $cleared.retainedLegCheckpointRequestId='(none)'
            $payload = [pscustomobject]@{
                sourceScope=$scope; clearedScope=$cleared
                acceptedCheckpoint=[pscustomobject]@{
                    leaseGeneration=1; checkpointGeneration=1; checkpointRequestId='checkpoint:1'
                    status='Accepted'; reason='fixture'; leaseFingerprint=('d' * 64)
                }
            }
            if (-not (Test-FileTransferCheckpointRepairContinuityScope $scope) -or
                -not (Test-FileTransferCheckpointRepairContinuityScopeEqual $scope (Copy-Value $scope)) -or
                -not (Test-FileTransferCheckpointRepairContinuityAggregateScope $scope $boundary) -or
                -not (Test-FileTransferCheckpointRepairContinuityOriginScope $payload)) { throw 'Valid continuity scope rejected' }
            foreach ($provider in @(1, 8)) {
                $bad = Copy-Value $scope
                $bad.boundary | Add-Member dataSessionProviderGeneration $provider -Force
                if ((Test-FileTransferCheckpointRepairContinuityScopeEqual $scope $bad) -or
                    (Test-FileTransferCheckpointRepairContinuityAggregateScope $scope $bad.boundary)) { throw 'Provider mismatch accepted' }
                $badPayload = Copy-Value $payload
                $badPayload.clearedScope.boundary | Add-Member dataSessionProviderGeneration $provider -Force
                if (Test-FileTransferCheckpointRepairContinuityOriginScope $badPayload) { throw 'Cleared provider mismatch accepted' }
            }
        }
        # The barrier's historical source is intentionally provider-less; a
        # captured provider, when supplied, must correlate with the aggregate.
        if (-not (Test-FileTransferCoordinatorReceiverBarrierSourceProjection $legacy $current -StructureOnly)) { throw 'Historical source rejected' }
        $bad = Copy-Value $current; $bad.dataSessionProviderGeneration = 1
        if (Test-FileTransferCoordinatorReceiverBarrierSourceProjection $bad $current -StructureOnly) { throw 'Provider/local conflation accepted' }
        Write-Output 'boundary-provider-schema-ok'
        """;
}
