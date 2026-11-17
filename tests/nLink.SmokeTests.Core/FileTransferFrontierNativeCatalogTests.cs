using System.Diagnostics;
using System.Text.Json;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FileTransferFrontierNativeCatalogTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", false)]
    [InlineData("pwsh", false)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", true)]
    [Trait("FileTransferRuntimeSuite", "FrontierNativeCatalog")]
    public async Task FrontierNativeCatalog_RealCoreChildRetainsOnlyLiveNativeCustody(string host, bool parentDeath)
        => await RunNativeFixtureAsync(host, parentDeath, "catalog", 11);

    [Theory]
    [InlineData("powershell", false)]
    [InlineData("pwsh", false)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", true)]
    [Trait("FileTransferRuntimeSuite", "FrontierNativeWriter")]
    public async Task FrontierNativeWriter_OriginalCompletionOwnsActualWriterReceipt(string host, bool parentDeath)
        => await RunNativeFixtureAsync(host, parentDeath, "writer", 14);

    [Theory]
    [InlineData("powershell", false)]
    [InlineData("pwsh", false)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", true)]
    [Trait("FileTransferRuntimeSuite", "FrontierNativeMarker")]
    public async Task FrontierNativeMarker_OriginalPayloadRequiresExactCompletionMarkerAndDrain(string host, bool parentDeath)
        => await RunNativeFixtureAsync(host, parentDeath, "marker", 21);

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferRuntimeSuite", "FrontierNativeMarker")]
    public async Task FrontierNativeMarker_CompletionReceiptCannotImpersonatePayloadMarker(string host)
        => await RunNativeFixtureAsync(host, false, "marker", 1, "wrong_role");

    [Theory]
    [InlineData("powershell", false)]
    [InlineData("pwsh", false)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", true)]
    [Trait("FileTransferRuntimeSuite", "FrontierNativeSource")]
    public async Task FrontierNativeSource_OriginalWriterHandleOwnsBoundedReadCustody(string host, bool parentDeath)
        => await RunNativeFixtureAsync(host, parentDeath, "source", 23);

    [Theory]
    [InlineData("powershell", false)]
    [InlineData("pwsh", false)]
    [InlineData("powershell", true)]
    [InlineData("pwsh", true)]
    [Trait("FileTransferRuntimeSuite", "FrontierNativePayloadFiles")]
    public async Task FrontierNativePayloadFiles_OriginalCatalogOwnsExactTemporaryOutput(string host, bool parentDeath)
        => await RunNativeFixtureAsync(host, parentDeath, "payload", 14);

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferRuntimeSuite", "FrontierNativePayloadFiles")]
    public async Task FrontierNativePayloadFiles_RejectsPartialCatalogAndCleansOriginalFiles(string host)
        => await RunNativeFixtureAsync(host, false, "payload", 1, "payload_bytes_fault");

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferRuntimeSuite", "FrontierNativePayloadFiles")]
    public async Task FrontierNativePayloadFiles_FinalizationRetainsOriginalVerifiedOutput(string host)
        => await RunNativeFixtureAsync(host, false, "payload", 1, "valid_v6");

    private async Task RunNativeFixtureAsync(string host, bool parentDeath, string mode, int cases, string? onlyCase = null)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", mode == "payload" ? "frontier-native-payload-files-20260916" :
            mode == "source" ? "frontier-native-source-20260916" : mode == "marker" ? "frontier-native-marker-20260916" :
            mode == "writer" ? "frontier-native-writer-20260915" : "frontier-native-catalog-20260915",
            host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierNativeCatalogRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, typeof(FileTransferFrontierNativeCatalogTests).Assembly.Location,
            directory, parentDeath ? "parent_death" : onlyCase ?? "", mode], host, TimeSpan.FromMinutes(3));
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        var prefix = parentDeath ? "frontier-native-catalog-parent-death=" : "frontier-native-catalog=";
        var line = Assert.Single(result.Stdout.Split('\n').Select(l => l.Trim()), l => l.StartsWith(prefix));
        using var document = JsonDocument.Parse(line[prefix.Length..]);
        if (!parentDeath)
        {
            Assert.Equal(cases, document.RootElement.GetProperty("Cases").GetInt32());
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), line[prefix.Length..]);
            return;
        }
        var data = document.RootElement;
        var path = Path.Combine(directory, "parent_death", "result.json");
        Assert.Equal(path, data.GetProperty("Result").GetString());
        Process? child = null;
        try
        {
            try { child = Process.GetProcessById(data.GetProperty("ProcessId").GetInt32()); }
            catch (ArgumentException) { }
            if (child is not null)
            {
                Assert.Equal(data.GetProperty("Creation").GetInt64(), child.StartTime.ToUniversalTime().ToFileTimeUtc());
                Assert.True(child.WaitForExit(20000), "Core child did not exit after parent death");
            }
            using var output = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.True(output.RootElement.GetProperty("Passed").GetBoolean());
            Assert.True(output.RootElement.GetProperty("NativeRevoked").GetBoolean());
            Assert.False(output.RootElement.GetProperty("PayloadPublished").GetBoolean());
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited && child.StartTime.ToUniversalTime().ToFileTimeUtc() == data.GetProperty("Creation").GetInt64())
                { child.Kill(); child.WaitForExit(5000); }
                child.Dispose();
            }
        }
    }
}
