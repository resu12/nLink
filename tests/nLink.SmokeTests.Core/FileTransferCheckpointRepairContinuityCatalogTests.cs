namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
public sealed class FileTransferCheckpointRepairContinuityCatalogTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task CheckpointRepairContinuityCatalog_PreservesFrozenProfilesAndDiagnosticGrammar()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var result = await RunPowerShellScriptTextAsync(Harness, [FindRepoRoot()]);
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        Assert.Equal("checkpoint-repair-continuity-catalog-ok", result.Stdout.Trim());
    }

    private const string Harness = """
        param([string]$RepoRoot)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
        $base = Get-FileTransferCoordinatorEvidenceCatalog
        $barrier = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion 'snapshot-authority-v1-receiver-barrier-v1'
        $version = 'snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1'
        $current = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $version
        if ($base.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2' -or
            $barrier.Hash -cne 'e507b186e037924e5785e563742fedd9111552684fc8cc2f46eef38225b0bb94' -or
            $current.ParentCatalogHash -cne $barrier.Hash -or
            $current.Hash -cne 'e6c3b5ff7cf8b44bcde0782ec325d2623cb96ba8ff362edca1902f9a1929059d' -or
            $current.Hash -ceq $barrier.Hash -or $current.Hash -cne (Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $version).Hash) {
            throw 'Catalog identity was not preserved or composition was not deterministic.'
        }
        if ((Test-FileTransferCoordinatorReceiverBarrierCatalogCapability $base) -or
            -not (Test-FileTransferCoordinatorReceiverBarrierCatalogCapability $barrier) -or
            -not (Test-FileTransferCoordinatorReceiverBarrierCatalogCapability $current)) { throw 'Receiver-barrier profile capability changed.' }
        if (($base.RequiredFamilies -join ',') -cne ($current.RequiredFamilies -join ',') -or
            ($base.Family1OperationKinds.Keys -join ',') -cne ($current.Family1OperationKinds.Keys -join ',') -or
            @($current.CheckpointRepairContinuityCatalog.effectKinds).Count -ne 0) { throw 'Frozen family coverage or physical authority changed.' }
        $unknownRejected = $false
        try { $null = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion ($version + '-unknown') }
        catch { $unknownRejected = $_.Exception.Message -cmatch '^coordinator_snapshot_catalog_version_unknown:' }
        if (-not $unknownRejected) { throw 'Unknown composite profile was accepted.' }
        $token = 'fte1-' + ('a' * 24)
        $fields = [ordered]@{}
        foreach ($name in $current.CheckpointRepairContinuityCatalog.diagnosticFields.Keys) {
            $kind = $current.CheckpointRepairContinuityCatalog.diagnosticFields[$name]
            $fields[$name] = if ($kind -ceq 'profile') { $version } elseif ($kind -ceq 'int64') { '1' } else { $token }
        }
        if ($null -eq (ConvertTo-FileTransferCheckpointRepairContinuityDiagnosticStamp $fields) -or
            $null -eq (ConvertTo-FileTransferCheckpointRepairContinuityDiagnosticStamp $fields -SourceOnly)) { throw 'Exact projected diagnostic stamp rejected.' }
        foreach ($name in @($fields.Keys)) {
            $copy = [ordered]@{}
            foreach ($key in $fields.Keys) { if ($key -cne $name) { $copy[$key] = $fields[$key] } }
            if ($null -ne (ConvertTo-FileTransferCheckpointRepairContinuityDiagnosticStamp $copy)) { throw "Missing stamp field accepted: $name" }
        }
        foreach ($invalid in @('[redacted]', '(none)', ('fte1-' + ('A' * 24)), 'raw-session-id', '')) {
            $fields.continuity_source_fingerprint = $invalid
            if ($null -ne (ConvertTo-FileTransferCheckpointRepairContinuityDiagnosticStamp $fields -SourceOnly)) { throw 'Unprojected source identity accepted.' }
        }
        $fields.continuity_source_fingerprint = $token
        foreach ($invalid in @('0', '-1', '01', '1.0', '9223372036854775808')) {
            $fields.continuity_repair_generation = $invalid
            if ($null -ne (ConvertTo-FileTransferCheckpointRepairContinuityDiagnosticStamp $fields)) { throw 'Invalid repair generation accepted.' }
        }
        $marker = [pscustomobject]@{ operationKind='FallbackCheckpointRepairScopeContinuity'; receiptValidated=$true }
        if (-not (Test-FileTransferCoordinatorCheckpointRepairContinuityMarker $marker) -or
            (Test-FileTransferCoordinatorSnapshotEffectReceipt $marker ([pscustomobject]@{})) -or
            (Test-FileTransferCoordinatorSnapshotEffectScope $marker ([pscustomobject]@{}))) { throw 'Evidence-only receipt exposed physical authority.' }
        Write-Output 'checkpoint-repair-continuity-catalog-ok'
        """;
}
