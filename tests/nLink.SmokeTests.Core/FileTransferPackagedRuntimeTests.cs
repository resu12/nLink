namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferPackagedRuntimeTests : FileTransferOpsScriptsTestBase
{
    private readonly Xunit.Abstractions.ITestOutputHelper output;

    public FileTransferPackagedRuntimeTests(Xunit.Abstractions.ITestOutputHelper output) => this.output = output;

    [Theory]
    [InlineData("powershell", "Preparation")]
    [InlineData("pwsh", "Preparation")]
    [InlineData("powershell", "Paths")]
    [InlineData("pwsh", "Paths")]
    [InlineData("powershell", "ShortPaths")]
    [InlineData("pwsh", "ShortPaths")]
    [InlineData("powershell", "Entry")]
    [InlineData("pwsh", "Entry")]
    [InlineData("powershell", "Development")]
    [InlineData("pwsh", "Development")]
    [InlineData("powershell", "Dispatch")]
    [InlineData("pwsh", "Dispatch")]
    public async Task PackagedRuntime_PreservesSuppliedPackage(string host, string lane)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var outputRoot = Path.Combine(Path.GetTempPath(), $"nlink-packaged-runtime-{Guid.NewGuid():N}");
        try
        {
            const string script = """
                param([string]$RepoRoot, [string]$OutputRoot, [string]$Lane)
                & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/PackagedRuntime.ps1') -RepoRoot $RepoRoot -OutputRoot $OutputRoot -Lane $Lane
                """;
            var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), outputRoot, lane], host, initializeHostModulePath: true);
            output.WriteLine(result.Stdout);
            Assert.True(result.ExitCode == 0, $"{host}/{lane}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains($"packaged-runtime-{lane}-ok", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(outputRoot);
        }
    }
}
