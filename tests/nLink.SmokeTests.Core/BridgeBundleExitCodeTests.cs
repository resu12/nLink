namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("Category", "Smoke")]
public sealed class BridgeBundleExitCodeTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> VersionProbeCases()
    {
        foreach (var host in new[] { "powershell", "pwsh" })
        foreach (var caller in new[] { "nodeVersion", "npmVersion", "manifest" })
        {
            yield return [host, caller, 0, "version"];
            yield return [host, caller, 17, "version"];
            yield return [host, caller, 0, "empty"];
        }
    }

    [Theory]
    [MemberData(nameof(VersionProbeCases))]
    public async Task VersionProbe_WaitsForExitAndRejectsFailedOrEmptyOutput(
        string host, string caller, int exitCode, string output)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunProbeAsync(host, caller, exitCode, output);
        var diagnostics = result.Stdout + result.Stderr;
        if (exitCode == 0 && output == "version")
        {
            Assert.True(result.ExitCode == 0, diagnostics);
            Assert.Contains("PROBE_PASS", result.Stdout, StringComparison.Ordinal);
        }
        else
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain("PROBE_PASS", result.Stdout, StringComparison.Ordinal);
            Assert.Contains(
                exitCode != 0 ? "exit code 17" : "returned an empty version",
                diagnostics,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("powershell", 0)]
    [InlineData("powershell", 17)]
    [InlineData("pwsh", 0)]
    [InlineData("pwsh", 17)]
    public async Task HealthProbe_RequiresSuccessfulProcessExitAfterHelloAndPong(string host, int exitCode)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunProbeAsync(host, "health", exitCode, "version");
        if (exitCode == 0)
        {
            Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
            Assert.Contains("PROBE_PASS", result.Stdout, StringComparison.Ordinal);
        }
        else
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain("PROBE_PASS", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("shutdown exited with code 17", result.Stdout + result.Stderr, StringComparison.Ordinal);
        }
    }

    private static Task<ScriptResult> RunProbeAsync(string host, string caller, int exitCode, string output)
    {
        var repoRoot = FindRepoRoot();
        var nodePath = Path.Combine(repoRoot, "artifacts", "bridge", "win-x64", "node.exe");
        Assert.True(File.Exists(nodePath), $"Build the bridge bundle before these tests: {nodePath}");
        return RunPowerShellScriptTextAsync(
            ProbeHarness,
            [Path.Combine(repoRoot, "installer", "Build-BridgeBundle.ps1"), nodePath, caller, exitCode.ToString(), output],
            powerShellHost: host,
            timeout: TimeSpan.FromSeconds(30),
            initializeHostModulePath: true);
    }

    private const string ProbeHarness = """
param([string]$ScriptPath, [string]$NodePath, [string]$Caller, [int]$ExitCode, [string]$Output)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw ($errors | Out-String) }
# Load production functions only; never execute the build/download entry point.
foreach ($statement in $ast.EndBlock.Statements) {
    if ($statement -is [System.Management.Automation.Language.FunctionDefinitionAst]) {
        . ([scriptblock]::Create($statement.Extent.Text))
    }
}
$utf8 = [System.Text.UTF8Encoding]::new($false)
if ($Caller -eq 'health') {
    $bridgePath = Join-Path $PSScriptRoot 'health.js'
    $bridge = @'
const readline = require('readline');
readline.createInterface({ input: process.stdin }).on('line', line => {
  const command = JSON.parse(line).cmd;
  if (command === 'hello') console.log(JSON.stringify({ event: 'hello_ok' }));
  if (command === 'ping') console.log(JSON.stringify({ event: 'pong' }));
  if (command === 'shutdown') process.exit(EXIT_CODE);
});
'@
    [System.IO.File]::WriteAllText($bridgePath, $bridge.Replace('EXIT_CODE', [string]$ExitCode), $utf8)
    $global:LASTEXITCODE = 0
    Test-BridgeBundleHealth -NodePath $NodePath -BridgeScriptPath $bridgePath
}
else {
    $versionPath = Join-Path $PSScriptRoot 'version.js'
    $completedPath = Join-Path $PSScriptRoot 'version-completed.txt'
    $toolPath = Join-Path $PSScriptRoot 'version.cmd'
    $version = @'
if (process.argv[3] === 'version') process.stdout.write('v24.13.1\nextra output\n');
setTimeout(() => {
  require('fs').writeFileSync(process.argv[4], 'completed');
  process.exit(Number(process.argv[2]));
}, 250);
'@
    [System.IO.File]::WriteAllText($versionPath, $version, $utf8)
    $command = "@echo off`r`n`"$NodePath`" `"$versionPath`" $ExitCode $Output `"$completedPath`"`r`nexit /b %errorlevel%`r`n"
    [System.IO.File]::WriteAllText($toolPath, $command, $utf8)
    # A successful probe must replace a prior native failure, not merely avoid setting a new status.
    $global:LASTEXITCODE = 37
    if ($Caller -eq 'manifest') {
        [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'VERSION'), '0.7.0', $utf8)
        [System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'index.js'), '// fixture', $utf8)
        Write-BridgeBundleManifest -RepoRoot $PSScriptRoot -BridgeSourceDir $PSScriptRoot `
            -Runtime 'win-x64' -OutDir $PSScriptRoot -NodePath $toolPath `
            -NodeArchiveSha256 'fixture' -PackageLockSha256 'fixture' `
            -NpmVersion '1' -NccVersion '1' -BridgePackageVersion '1'
        $actualVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bridge-manifest.json') -Raw | ConvertFrom-Json).nodeVersion
    }
    else {
        $toolchain = [pscustomobject]@{ NodePath = $toolPath; NpmPath = $toolPath }
        $assignments = @($ast.EndBlock.Statements | Where-Object {
            $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $_.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $_.Left.VariablePath.UserPath -eq $Caller
        })
        if ($assignments.Count -ne 1) { throw "Expected one production assignment for $Caller" }
        . ([scriptblock]::Create($assignments[0].Extent.Text))
        $actualVersion = Get-Variable -Name $Caller -ValueOnly
    }
    if ($actualVersion -ne 'v24.13.1') { throw "Unexpected version: $actualVersion" }
    if (-not (Test-Path -LiteralPath $completedPath)) { throw 'Version producer did not finish.' }
}
Write-Host 'PROBE_PASS'
# Match the GitHub Actions PowerShell wrapper's native exit-status propagation.
if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }
""";
}
