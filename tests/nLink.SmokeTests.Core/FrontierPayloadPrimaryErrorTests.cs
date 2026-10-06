namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierPayloadPrimaryErrorTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task BindingClassification_IncludesCanonicalFallbackWithoutPromotingInvalidOwners(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryProbe.ps1'),[ref]$tokens,[ref]$errors)
            if($errors.Count) {throw 'Primary probe parse failed'}
            $assignments=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$private:validPair'},$true))
            if($assignments.Count -ne 1) {throw 'Primary binding classification is ambiguous'}
            $classify=[scriptblock]::Create($assignments[0].Right.Extent.Text)
            foreach($primaryFault in @('valid','canonical-source','canonical-fallback','foreign-completion','pair-retired','owner-retired','diagnostic-control','CANONICAL-FALLBACK','unknown','')) {
                $expected=$primaryFault -cin @('valid','canonical-source','canonical-fallback')
                $actual=& $classify
                if($actual -isnot [bool] -or $actual -ne $expected) {throw ('Binding classification changed: '+$primaryFault+'; expected='+$expected+'; actual='+$actual)}
            }
            'primary-binding-classification-ok; cases=10'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("primary-binding-classification-ok; cases=10", result.Stdout);
    }

    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "bind-failure", "bind-dispose-failure", "cleanup-pass", "cleanup-open", "cleanup-current", "cleanup-absent" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalBindFailure_SurvivesCleanupWithoutRepeatedCurrentness(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "primary-binding-diagnostics-20260928", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierPayloadPrimaryErrorControls.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("primary-binding-error-control=" + scenario, result.Stdout);
    }
}
