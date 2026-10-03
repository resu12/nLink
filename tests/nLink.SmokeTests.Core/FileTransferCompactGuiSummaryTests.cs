namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
public sealed class FileTransferCompactGuiSummaryTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task CompactGuiSummary_ReadsExplicitRegularCycleIntegrityWithoutAuthority(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "compact-gui-intake-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var script = """
                param($Root, $Directory)
                $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
                . (Join-Path $Root 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
                function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'GUI intake cannot evaluate authority' }
                $valid='{"completed":true,"integrity_ok":true,"goodput_bytes_per_second":1000000}'
                $cases=@(
                    @{name='valid';lines=@($valid);completed=$true;integrity=$true;speed=1000000},
                    @{name='multiple';lines=@($valid,$valid.Replace('1000000','2000000'));completed=$true;integrity=$true;speed=1500000},
                    @{name='missing_integrity';lines=@('{"completed":true}');completed=$true;integrity=$false;speed=0},
                    @{name='false_integrity';lines=@($valid.Replace('"integrity_ok":true','"integrity_ok":false'));completed=$true;integrity=$false;speed=1000000},
                    @{name='false_completed';lines=@($valid.Replace('"completed":true','"completed":false'));completed=$false;integrity=$true;speed=1000000},
                    @{name='mixed_integrity';lines=@($valid,$valid.Replace('"integrity_ok":true','"integrity_ok":false'));completed=$true;integrity=$false;speed=1000000},
                    @{name='string_flags';lines=@('{"completed":"true","integrity_ok":"true"}');completed=$false;integrity=$false;speed=0},
                    @{name='empty';lines=@();absent=$true},
                    @{name='missing';absent=$true},
                    @{name='malformed';lines=@('{');absent=$true},
                    @{name='partial_append';lines=@($valid,'{');absent=$true},
                    @{name='array';lines=@('['+$valid+']');absent=$true},
                    @{name='regular_hard_failure';lines=@($valid);regular='{"verdict":"FAIL_PROTOCOL_OR_INTEGRITY"}';completed=$true;integrity=$true;speed=1000000;hard=1},
                    @{name='regular_malformed';lines=@($valid);regular='{';absent=$true},
                    @{name='tuna_false';lines=@($valid);tuna='{"completed":true,"integrityOk":false,"goodputBytesPerSecond":7}';completed=$true;integrity=$false;speed=7},
                    @{name='tuna_malformed';lines=@($valid);tuna='{';absent=$true}
                )
                foreach($case in $cases) {
                    $dir=Join-Path $Directory $case.name; [void][IO.Directory]::CreateDirectory($dir)
                    if($case.ContainsKey('lines')) {
                        [IO.File]::WriteAllLines((Join-Path $dir 'filetransfer-live-nkn-cycles.jsonl'),[string[]]$case.lines)
                    }
                    if($case.ContainsKey('tuna')) {
                        [IO.File]::WriteAllText((Join-Path $dir 'filetransfer-tuna-gui-summary.json'),$case.tuna)
                    }
                    if($case.ContainsKey('regular')) {
                        [IO.File]::WriteAllText((Join-Path $dir 'filetransfer-live-nkn-summary.json'),$case.regular)
                    }
                    $result=Read-FileTransferCompactGuiSummary $dir
                    if($case.ContainsKey('absent')) {
                        if($null -ne $result) { throw ('Unusable input was accepted: '+$case.name) }
                        continue
                    }
                    if($null -eq $result -or $result.completed -ne $case.completed -or
                        $result.integrityOk -ne $case.integrity -or $result.goodputBytesPerSecond -ne $case.speed) {
                        throw ('GUI observation projection failed: '+$case.name)
                    }
                    if($case.ContainsKey('hard') -and $result.hardFailureCount -ne $case.hard) {
                        throw 'Regular protocol/integrity failure was lost'
                    }
                }
                'regular-gui-intake=pass'
                """;
            var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
            Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains("regular-gui-intake=pass", result.Stdout, StringComparison.Ordinal);
        }
        finally { TryDeleteDirectory(directory); }
    }
}
