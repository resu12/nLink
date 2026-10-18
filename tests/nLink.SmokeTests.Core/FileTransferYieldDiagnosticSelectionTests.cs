namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
public sealed class FileTransferYieldDiagnosticSelectionTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task YieldDiagnosticSelection_RequiresUniqueOriginalUnrecoveredReceive(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var result = await RunPowerShellScriptTextAsync(Harness, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Equal("yield-diagnostic-selection-ok", result.Stdout.Trim());
    }

    private const string Harness = """
        param([string]$RepoRoot)
        Set-StrictMode -Version Latest
        $ErrorActionPreference='Stop'
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PublicationServiceYieldMatcher.ps1')
        # Exercise only the pure selector, not a fabricated custody/evaluation.
        $tokens=$null; $errors=$null
        $ast=[Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PublicationServiceYieldProbe.ps1'),[ref]$tokens,[ref]$errors)
        if($errors.Count -ne 0) { throw ($errors | Out-String) }
        $function=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $n.Name -ceq 'YSelectReceivePosition'},$true))
        if($function.Count -ne 1) { throw 'Selector seam changed' }
        . ([scriptblock]::Create($function[0].Extent.Text))
        foreach($case in @('valid','missing','ambiguous','cloned_event','wrong_path','wrong_line','wrong_sequence',
            'wrong_raw_line','wrong_original','recovered','diagnostic_recovered','unmatched','wrong_reason')) {
            $line=[string]::new([char[]]'original rejected receive')
            $event=[pscustomobject]@{EventName='filetransfer_data_path_receive_result';LineNumber=2;Sequence=2;FilePath='owned';RawLine=$line}
            $diagnostic=[pscustomobject]@{Recovered=$false;RejectionChainMatched=$true;Reason='missing_exact_tuna_receive'}
            $candidate=[pscustomobject]@{Event=$event;Recovered=$false;RejectionDiagnostic=$diagnostic}
            $proof=[pscustomobject]@{CandidateRecords=@($candidate)}
            $context=[pscustomobject]@{SourceOwner=[pscustomobject]@{SourcePath='owned';SourceLines=2};
                FullOrigins=[int[]]@(2);FullLines=[string[]]@($line);FullOriginal=[string[]]@($line)}
            $index=[pscustomobject]@{AllEvents=@($event)}
            $expected='diagnostic_origin'
            switch($case) {
                'missing' {$proof.CandidateRecords=@();$expected='diagnostic_candidate'}
                'ambiguous' {$proof.CandidateRecords=@($candidate,$candidate);$expected='diagnostic_candidate'}
                'cloned_event' {$candidate.Event=$event.PSObject.Copy()}
                'wrong_path' {$event.FilePath='foreign'}
                'wrong_line' {$event.LineNumber=1}
                'wrong_sequence' {$event.Sequence=1}
                'wrong_raw_line' {$event.RawLine=[string]::new($line.ToCharArray())}
                'wrong_original' {$context.FullOriginal=[string[]]@([string]::new($line.ToCharArray()))}
                'recovered' {$candidate.Recovered=$true;$expected='diagnostic_rejection'}
                'diagnostic_recovered' {$diagnostic.Recovered=$true;$expected='diagnostic_rejection'}
                'unmatched' {$diagnostic.RejectionChainMatched=$false;$expected='diagnostic_rejection'}
                'wrong_reason' {$diagnostic.Reason='recovered';$expected='diagnostic_rejection'}
            }
            $actual='';$position=-1
            try {$position=YSelectReceivePosition $proof $context $index} catch {$actual=$_.Exception.Message}
            if($case -ceq 'valid') {
                if($actual -or $position -ne 0) {throw "Valid original candidate rejected: $actual"}
            } elseif($actual -cne $expected) {throw "Unexpected $case outcome: $actual"}
        }
        'yield-diagnostic-selection-ok'
        """;
}
