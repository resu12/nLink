using System.Reflection;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;
using Xunit.Abstractions;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FrontierWorkerConfigurationTests(ITestOutputHelper output) : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", false)] [InlineData("powershell", true)]
    [InlineData("pwsh", false)] [InlineData("pwsh", true)]
    public async Task Dispatcher_PropagatesCallerConfigurationToNestedWorker(string host, bool explicitConfiguration)
    {
        if (!OperatingSystem.IsWindows()) return;
        var configuration = typeof(FrontierWorkerConfigurationTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var inherited = Environment.GetEnvironmentVariable("NLINK_TEST_FRONTIER_WORKER_CONFIGURATION");
        const string script = """
            param([string]$RepoRoot,[string]$Expected,[string]$Explicit)
            $ErrorActionPreference='Stop'
            if($env:NLINK_TEST_FRONTIER_WORKER_CONFIGURATION -cne $Expected) {throw 'Caller configuration not propagated'}
            $dispatch=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1'))
            $end=$dispatch.IndexOf('# Only the reviewed actual-pair composite')
            if($end -lt 0) {throw 'Dispatch configuration boundary changed'}
            # Execute the real configuration preamble only: no fixture/app launch.
            $bind=[scriptblock]::Create($dispatch.Substring(0,$end))
            if($Explicit -ceq 'True') {& $bind -RepoRoot $RepoRoot -WorkerConfiguration $Expected}
            else {& $bind -RepoRoot $RepoRoot}
            if($env:NLINK_TEST_FRONTIER_WORKER_CONFIGURATION -cne $Expected) {throw 'Dispatcher changed caller configuration'}
            $worker=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptWritersApp.ps1'))
            $end=$worker.IndexOf('[void][NLink.SmokeTests.FrontierAttemptWritersTests]::RunAppAsync')
            if($end -lt 0) {throw 'Worker configuration boundary changed'}
            # Load/check the actual worker assemblies, but never call RunAppAsync.
            $command='& { '+$worker.Substring(0,$end)+' } -RepoRoot '+"'"+$RepoRoot.Replace("'","''")+"'"
            $start=[Diagnostics.ProcessStartInfo]::new()
            $start.FileName=(Get-Command pwsh -ErrorAction Stop).Source
            $start.Arguments='-NoProfile -NonInteractive -EncodedCommand '+[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
            $start.UseShellExecute=$false; $start.CreateNoWindow=$true
            $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
            $process=[Diagnostics.Process]::Start($start)
            try {
                $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
                $process.WaitForExit()
                $out=$stdout.GetAwaiter().GetResult(); $err=$stderr.GetAwaiter().GetResult()
                if($process.ExitCode -ne 0) {throw ($out+$err)}
                $out
            } finally {$process.Dispose()}
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), configuration, explicitConfiguration.ToString()], host);
        output.WriteLine(result.Stdout + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var coreHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(SessionFileTransferService).Assembly.Location)));
        var testsHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(FrontierWorkerConfigurationTests).Assembly.Location)));
        Assert.Equal($"frontier-worker-configuration={configuration}; core_sha256={coreHash}; tests_sha256={testsHash}",
            Assert.Single(result.Stdout.Split('\n').Where(line => line.StartsWith("frontier-worker-configuration=", StringComparison.Ordinal))).Trim());
        Assert.Equal(inherited, Environment.GetEnvironmentVariable("NLINK_TEST_FRONTIER_WORKER_CONFIGURATION"));
    }

    [Theory]
    [InlineData("powershell")] [InlineData("pwsh")]
    public async Task Dispatcher_RejectsConflictingExplicitConfigurationBeforeLoading(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            $caller=$env:NLINK_TEST_FRONTIER_WORKER_CONFIGURATION
            $other=if($caller -ceq 'Release') {'Debug'} else {'Release'}
            try {
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1') -RepoRoot 'must-not-load' -WorkerConfiguration $other
            } finally {
                if($env:NLINK_TEST_FRONTIER_WORKER_CONFIGURATION -cne $caller) {throw 'Rejected configuration changed caller'}
            }
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        output.WriteLine(result.Stdout + result.Stderr);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Frontier worker configuration differs from caller", result.Stderr);
        Assert.DoesNotContain("frontier-worker-configuration=", result.Stdout);
    }

    [Theory]
    [InlineData("powershell", "FrontierAttemptDispatch.ps1")]
    [InlineData("pwsh", "FrontierAttemptDispatch.ps1")]
    [InlineData("powershell", "FrontierAttemptWritersApp.ps1")]
    [InlineData("pwsh", "FrontierAttemptWritersApp.ps1")]
    public async Task EntryPoint_RejectsMissingConfigurationBeforeLoading(string host, string entryPoint)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot,[string]$EntryPoint)
            $ErrorActionPreference='Stop'
            $env:NLINK_TEST_FRONTIER_WORKER_CONFIGURATION=$null
            & (Join-Path $RepoRoot ('tests/nLink.SmokeTests.Core/Support/'+$EntryPoint)) -RepoRoot 'must-not-load'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), entryPoint], host);
        output.WriteLine(result.Stdout + result.Stderr);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unsupported frontier worker configuration", result.Stderr);
        Assert.DoesNotContain("frontier-worker-configuration=", result.Stdout);
    }
}
