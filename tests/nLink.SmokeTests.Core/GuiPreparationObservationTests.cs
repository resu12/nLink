namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferGuiSuite", "PreparationObservation")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class GuiPreparationObservationTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task PairedPreparationValidator_RequiresTwoFreshScopedUnconsumedWitnesses(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync(Harness + """
            $responses = New-Responses
            if (-not (Test-GuiPreparationPair $responses $requests $identities $now)) { throw 'valid pair rejected' }
            # Peers independently allocate incarnation and every local generation.
            $responses[1].observation.preparationGeneration = 51
            $responses[1].observation.snapshotGeneration = 99
            $responses[1].observation.leaseGeneration = 123
            if (-not (Test-GuiPreparationPair $responses $requests $identities $now)) { throw 'local generations compared between peers' }
            $mutations = @(
                { param($r) $r[1] = $null },
                { param($r) $r[1] = $r[0] },
                { param($r) $r[1].requestId = 'old' },
                { param($r) $r[1].requestedUtcMs-- },
                { param($r) $r[1].capturedUtcMs = $requested - 1 },
                { param($r) $r[1].capturedUtcMs = $now + 1 },
                { param($r) $r[1].identity.processId = 77 },
                { param($r) $r[1].identity.bootId = 'other' },
                { param($r) $r[1].identity.processStartedUtcMs-- },
                { param($r) $r[1].identity.localRole = 'Helpee' },
                { param($r) $r[1].identity.scope.incarnation = [guid]::NewGuid().ToString() },
                { param($r) $r[1].identity.scope.sessionId = 'other' },
                { param($r) $r[1].observation.scope.incarnation = [guid]::NewGuid().ToString() },
                { param($r) $r[1].observation.scope.sessionId = 'other' },
                { param($r) $r[1].observation.scope.helpeeIdentity = 'other' },
                { param($r) $r[1].observation.scope.helperIdentity = 'other' },
                { param($r) $r[1].observation.direction = 'Outbound' },
                { param($r) $r[1].observation.ready = $false },
                { param($r) $r[1].observation.ready = 'true' },
                { param($r) $r[1].observation.consumed = $true },
                { param($r) $r[1].observation.revoked = $true },
                { param($r) $r[1].observation.authorityEffect = 'route' },
                { param($r) $r[1].observation.episodeFingerprint = ('B' * 64) },
                { param($r) $r[1].observation.episodeFingerprint = '' },
                { param($r) $r[1].observation.sourceCapturedUtc = [DateTimeOffset]::FromUnixTimeMilliseconds($requested + 1).ToString('o') },
                { param($r) $r[1].observation.expiresUtc = [DateTimeOffset]::FromUnixTimeMilliseconds($now).ToString('o') },
                { param($r) $r[1].observation.phase = 'AdmissionReserved' },
                { param($r) $r[1].observation.reason = 'preparation_lease_unavailable' },
                { param($r) $r[1].observation.leaseGeneration = 0 },
                { param($r) $r[1].observation.PSObject.Properties.Remove('consumed') }
            )
            for ($i = 0; $i -lt $mutations.Count; $i++) {
                $responses = New-Responses
                & $mutations[$i] $responses
                if (Test-GuiPreparationPair $responses $requests $identities $now) { throw "mutation accepted: $i" }
            }
            $requests[1].requestId = $requests[0].requestId
            if (Test-GuiPreparationPair (New-Responses) $requests $identities $now) { throw 'shared request accepted' }
            $requests[1].requestId = [guid]::NewGuid().ToString('N')
            $identities[1].processId = $identities[0].processId
            if (Test-GuiPreparationPair (New-Responses) $requests $identities $now) { throw 'same process accepted twice' }
            Write-Output 'paired-validator-ok'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Equal("paired-validator-ok", result.Stdout.Trim());
    }

    [Theory]
    [InlineData("powershell", "helper-to-helpee")]
    [InlineData("pwsh", "helpee-to-helper")]
    public async Task PairedPreparationWait_UsesKnownProcessesFreshRequestsAndRetainsResponses(string host, string direction)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync(Harness + """
            $scriptText = Get-Content -Raw -LiteralPath (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1')
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseInput($scriptText, [ref]$tokens, [ref]$errors)
            if ($errors.Count -ne 0) { throw 'harness parse error' }
            $write = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Write-GuiSmokeAtomicJson' }, $true)
            Invoke-Expression $write.Extent.Text
            $temp = Join-Path ([IO.Path]::GetTempPath()) ('nlink-paired-gui-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($temp)
            try {
                $roles = if ($Direction -eq 'helper-to-helpee') { @('Helper', 'Helpee') } else { @('Helpee', 'Helper') }
                $processes = @(0,1 | ForEach-Object {
                    $identities[$_].localRole = $roles[$_]
                    [pscustomobject]@{ Id = $identities[$_].processId; HasExited = $false
                        StartTime = [DateTimeOffset]::FromUnixTimeMilliseconds($identities[$_].processStartedUtcMs).UtcDateTime
                        PreparationObservationRoot = $temp; PreparationObservationBoot = $identities[$_].bootId }
                })
                $dirs = @(0,1 | ForEach-Object {
                    $dir = Join-Path $temp ('{0}-{1}' -f $processes[$_].Id, $processes[$_].PreparationObservationBoot)
                    Write-GuiSmokeAtomicJson -Path (Join-Path $dir 'identity.json') -Value $identities[$_]
                    $dir
                })
                $context = if ($Direction -eq 'helper-to-helpee') { [pscustomobject]@{ HelperProc=$processes[0]; HelpeeProc=$processes[1] } }
                    else { [pscustomobject]@{ HelpeeProc=$processes[0]; HelperProc=$processes[1] } }
                $script:rounds = 0
                function Wait-Until {
                    param($TimeoutMs, $PollMs, $Condition, $OnTimeoutMessage)
                    if ($TimeoutMs -ne 150000 -or $PollMs -ne 200) { throw 'wait budget changed' }
                    for ($round = 0; $round -lt 4; $round++) {
                        $script:rounds++
                        $value = & $Condition
                        if ($value) { return $value }
                        $script:requests = @((Read-GuiPreparationJson (Join-Path $dirs[0] 'request.json')), (Read-GuiPreparationJson (Join-Path $dirs[1] 'request.json')))
                        $r = New-Responses
                        for ($j=0; $j -lt 2; $j++) {
                            $r[$j].capturedUtcMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
                            if ($round -eq 0) { $r[$j].requestId = 'stale-response' }
                            Write-GuiSmokeAtomicJson -Path (Join-Path $dirs[$j] 'response.json') -Value $r[$j]
                        }
                    }
                    throw $OnTimeoutMessage
                }
                $line = Wait-GuiPairedPreparationReadiness -Context $context -Direction $Direction -ArtifactDir $temp
                if ($script:rounds -ne 3 -or $line -notlike '*paired_peer_count=2*authority_effect=none*') { throw 'stale files satisfied gate' }
                $saved = Read-GuiPreparationJson (Join-Path $temp 'paired-preparation-ready.json')
                if ($saved.requests[0].direction -ne 'Outbound' -or $saved.requests[1].direction -ne 'Inbound' -or
                    $saved.responses[0].identity.localRole -ne $roles[0]) { throw 'direction or artifacts lost' }
                $processes[0].HasExited = $true
                if (Test-GuiPreparationIdentity $identities[0] $processes[0] $roles[0]) { throw 'exited process accepted' }
                Write-Output 'paired-wait-ok'
            }
            finally { Remove-Item -LiteralPath $temp -Recurse -Force }
            """, [FindRepoRoot(), direction], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Equal("paired-wait-ok", result.Stdout.Trim());
    }

    [Fact]
    public void PairedPreparationGate_ReplacesOnlyFirstTransferWaitAndRetainsPublicationChecks()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), "tools", "GuiSmoke-Windows.ps1"));
        Assert.Contains("$diagnosticReadinessLine = [string](Wait-GuiPairedPreparationReadiness -Context $Context -Direction $Direction -ArtifactDir $ArtifactDir -TimeoutMs 150000)", text);
        Assert.Contains("$secondDiagnosticReadinessLine = [string](Wait-TunaGuiDiagnosticReadinessBeforeMeasuredTransfer -Bookmark $secondBookmark", text);
        Assert.Contains("function Wait-TunaGuiFileTunaV4PublicationAfterBookmark", text);
        Assert.Contains("Set-GuiPreparationProcessIdentity -Process $process -EnvironmentOverrides $launchEnvironment", text);
    }

    private const string Harness = """
        param([string]$RepoRoot, [string]$Direction)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$errors)
        if ($errors.Count -ne 0) { throw 'harness parse error' }
        foreach ($name in @('Read-GuiPreparationJson', 'Set-GuiPreparationProcessIdentity', 'Test-GuiPreparationIdentity',
            'Test-GuiPreparationResponse', 'Test-GuiPreparationPair', 'Wait-GuiPairedPreparationReadiness')) {
            $definition = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
            Invoke-Expression $definition.Extent.Text
        }
        $now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $requested = $now - 100
        $identities = @(0,1 | ForEach-Object {
            [pscustomobject]@{ processId = 100 + $_; bootId = [guid]::NewGuid().ToString('N'); processStartedUtcMs = $now - 5000
                localRole = @('Helpee','Helper')[$_]
                scope = [pscustomobject]@{ sessionId='session'; incarnation=[guid]::NewGuid().ToString(); helpeeIdentity='helpee'; helperIdentity='helper' } }
        })
        $requests = @(0,1 | ForEach-Object {
            [pscustomobject]@{ requestId=[guid]::NewGuid().ToString('N'); requestedUtcMs=$requested; expectedSessionId='session'; direction=@('Outbound','Inbound')[$_] }
        })
        function New-Responses {
            @(0,1 | ForEach-Object {
                [pscustomobject]@{ requestId=$requests[$_].requestId; requestedUtcMs=$requests[$_].requestedUtcMs; capturedUtcMs=$now
                    identity=($identities[$_] | ConvertTo-Json -Depth 8 | ConvertFrom-Json)
                    observation=[pscustomobject]@{ scope=($identities[$_].scope | ConvertTo-Json | ConvertFrom-Json)
                        ready=$true; consumed=$false; revoked=$false; direction=$requests[$_].direction
                        phase='Ready'; reason='preparation_ready'; authorityEffect='none'; episodeFingerprint=('A' * 64)
                        preparationGeneration=1; snapshotGeneration=2; leaseGeneration=3
                        sourceCapturedUtc=[DateTimeOffset]::FromUnixTimeMilliseconds($now - 1000).ToString('o')
                        expiresUtc=[DateTimeOffset]::FromUnixTimeMilliseconds($now + 60000).ToString('o') }
                }
            })
        }

        """;
}
