namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferReceiverBarrierCatalogProfileTests : FileTransferOpsScriptsTestBase
{
    private const string PreAdmissionProfile = "snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1-source-callback-v1-repair-owner-v1-activation-retry-v1-receiver-pre-admission-v1";

    [Theory]
    [InlineData("filetransfer_coordinator_transition_snapshot", PreAdmissionProfile, true)]
    [InlineData("filetransfer_coordinator_transition_snapshot", PreAdmissionProfile + "-unknown", false)]
    [InlineData("filetransfer_coordinator_transition_snapshot", "snapshot-authority-v1-receiver-barrier-v1", true)]
    [InlineData("filetransfer_coordinator_authority_effect", "snapshot-authority-v1-receiver-barrier-v1", true)]
    [InlineData("unrelated_event", "snapshot-authority-v1-receiver-barrier-v1", false)]
    [InlineData("filetransfer_coordinator_transition_snapshot", "snapshot-authority-v1-receiver-barrier-v1-unknown", false)]
    public void ReceiverBarrierCatalog_RedactorPreservesOnlyExactCoordinatorProfile(string eventName, string version, bool preserved)
    {
        var line = NLink.Core.Logging.SensitiveDataRedactor.Redact($"event={eventName}; snapshot_catalog_version={version}; reason=test");
        Assert.Equal(preserved, line.Contains($"snapshot_catalog_version={version};", StringComparison.Ordinal));
        Assert.Equal(!preserved, line.Contains("snapshot_catalog_version=[redacted];", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReceiverBarrierCatalog_FrozenDefaultAndExtendedHashRemainIndependent()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $before = $base | ConvertTo-Json -Depth 32 -Compress
            $extended = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $extendedVersion
            if ($base.Version -cne 'snapshot-authority-v1' -or
                $base.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2' -or
                $extended.Hash -cne 'e507b186e037924e5785e563742fedd9111552684fc8cc2f46eef38225b0bb94' -or
                $extended.BaseCatalogHash -cne $base.Hash -or
                $extended.SnapshotContractVersion -ne 1) { throw 'catalog profile identity changed' }
            if ((Get-FileTransferCoordinatorEvidenceCatalog | ConvertTo-Json -Depth 32 -Compress) -cne $before -or
                $base.PSObject.Properties.Name -contains 'ReceiverBarrierCatalog') { throw 'extended lookup mutated frozen default' }
            $originalType = $script:FileTransferCoordinatorReceiverBarrierCatalog.payloadFields.pauseGeneration
            try {
                $script:FileTransferCoordinatorReceiverBarrierCatalog.payloadFields.pauseGeneration = 'string'
                $rejected = $false
                try { $null = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $extendedVersion }
                catch { $rejected = $_.Exception.Message -like 'coordinator_snapshot_receiver_barrier_catalog_hash_invalid:*' }
                if (-not $rejected) { throw 'extension schema mutation did not invalidate its hash' }
            }
            finally { $script:FileTransferCoordinatorReceiverBarrierCatalog.payloadFields.pauseGeneration = $originalType }
            """);
    }

    [Theory]
    [InlineData(PreAdmissionProfile)]
    [InlineData("snapshot-authority-v1")]
    [InlineData("snapshot-authority-v1-receiver-barrier-v1")]
    public async Task ReceiverBarrierCatalog_ExactProfileEvaluatesBothInputPaths(string profileVersion)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $profile = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $Case
            [string[]]$lines = @(New-ProfileStream $profile)
            $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
            $records = @(for ($i = 0; $i -lt $lines.Count; $i++) {
                [pscustomobject]@{ Line = $lines[$i]; LineIndex = $i }
            })
            $compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
            foreach ($evaluation in @($full, $compact)) {
                if ($evaluation.ArtifactEvidenceVerdict -cne 'pass' -or
                    $evaluation.CatalogVersion -cne $profile.Version -or
                    $evaluation.CatalogHash -cne $profile.Hash -or
                    $evaluation.ContractVersion -ne 1) { throw ($evaluation | ConvertTo-Json -Depth 16 -Compress) }
            }
            $parity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact
            if ($parity.Verdict -cne 'pass') { throw $parity.FailureReason }
            if ((Get-FileTransferCoordinatorEvidenceCatalog).Version -cne $base.Version) { throw 'evaluation changed default catalog' }
            """, profileVersion);
    }

    [Fact]
    public async Task ReceiverBarrierCatalog_PreAdmissionExtensionPreservesParent()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $parent = Get-FileTransferCoordinatorActivationRetryCatalog
            $before = $parent | ConvertTo-Json -Depth 32 -Compress
            $extended = Get-FileTransferCoordinatorReceiverPreAdmissionCatalog
            if ($extended.Hash -cne '17196929ff74bd92c4866ccb8da33de05eb5b22d5436f56488f3a7fd2f4d086b' -or
                $extended.ParentCatalogHash -cne $parent.Hash -or
                $parent.Hash -cne '6f8667e92dae781e679bbe6f59fbd4f3d6284cd893ad2f643a9e47a21d451b00' -or
                (Get-FileTransferCoordinatorActivationRetryCatalog | ConvertTo-Json -Depth 32 -Compress) -cne $before) { throw 'parent profile changed' }
            $original = $script:FileTransferCoordinatorReceiverPreAdmissionCatalog.mode
            try {
                $script:FileTransferCoordinatorReceiverPreAdmissionCatalog.mode = 'Admitted'
                $rejected = $false
                try { $null = Get-FileTransferCoordinatorReceiverPreAdmissionCatalog }
                catch { $rejected = $_.Exception.Message -like 'receiver_pre_admission_catalog_hash_invalid:*' }
                if (-not $rejected) { throw 'extension schema mutation accepted' }
            } finally { $script:FileTransferCoordinatorReceiverPreAdmissionCatalog.mode = $original }
            """);
    }

    [Theory]
    [InlineData("unknown_version")]
    [InlineData("wrong_hash")]
    [InlineData("version_case")]
    [InlineData("hash_case")]
    [InlineData("midstream_change")]
    [InlineData("mixed_streams")]
    public async Task ReceiverBarrierCatalog_UnknownMutatedAndMixedProfilesFailClosed(string mutation)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $extended = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $extendedVersion
            [string[]]$lines = @(New-ProfileStream $extended)
            $expected = 'snapshot_catalog_mismatch:'
            switch -CaseSensitive ($Case) {
                'unknown_version' { $lines = @($lines | ForEach-Object { $_.Replace($extended.Version, 'unknown-profile') }) }
                'wrong_hash' { $lines = @($lines | ForEach-Object { $_.Replace($extended.Hash, ('0' * 64)) }) }
                'version_case' { $lines = @($lines | ForEach-Object { $_.Replace($extended.Version, $extended.Version.ToUpperInvariant()) }) }
                'hash_case' { $lines = @($lines | ForEach-Object { $_.Replace($extended.Hash, $extended.Hash.ToUpperInvariant()) }) }
                'midstream_change' {
                    $legacy = @(New-ProfileStream $base)
                    $lines[1] = $legacy[1]
                    $expected = 'snapshot_catalog_profile_conflict:'
                }
                'mixed_streams' {
                    $lines += @(New-ProfileStream $base 'other-coordinator')
                    $expected = 'snapshot_catalog_profile_conflict:'
                }
            }
            $evaluation = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
            if ($evaluation.ArtifactEvidenceVerdict -cne 'fail' -or
                -not $evaluation.ArtifactEvidenceFailureReason.Contains($expected)) {
                throw ($evaluation | ConvertTo-Json -Depth 16 -Compress)
            }
            """, mutation);
    }

    private static async Task RunHarnessAsync(string body, string argument = "")
    {
        var result = await RunPowerShellScriptTextAsync(
            HarnessPrefix + Environment.NewLine + body + Environment.NewLine + "Write-Output 'receiver-barrier-catalog-ok'",
            [FindRepoRoot(), argument]);
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        Assert.Equal("receiver-barrier-catalog-ok", result.Stdout.Trim());
    }

    private const string HarnessPrefix = """
        param([string]$RepoRoot, [string]$Case)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
        $base = Get-FileTransferCoordinatorEvidenceCatalog
        $extendedVersion = 'snapshot-authority-v1-receiver-barrier-v1'
        function New-ProfileStream($Profile, [string]$Instance = 'profile-coordinator') {
            $families = $base.RequiredFamilies -join ','
            $prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($Profile.Version); snapshot_catalog_hash=$($Profile.Hash); coordinator_instance_id=$Instance; session_id=profile-session; transfer_id=profile-transfer; direction=inbound"
            $coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
            "event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=0; initial_state_hash=initial; $coverage; authority_effect=none"
            "event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=0; final_state_hash=initial; transition_count=0; $coverage; authority_effect=none"
        }
        """;
}
