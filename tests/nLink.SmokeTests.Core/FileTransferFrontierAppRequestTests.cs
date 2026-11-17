using System.Diagnostics;
using System.Text.Json;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FileTransferFrontierAppRequestTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferRuntimeSuite", "FrontierNativeAppRequest")]
    public async Task FrontierAppRequest_ActualNativeProcessesEnforceOwnershipAndOneShotCompletion(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-native-app-20260915", host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAppRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        var line = Assert.Single(result.Stdout.Split('\n').Select(l => l.Trim()), l => l.StartsWith("frontier-app-request="));
        var json = line["frontier-app-request=".Length..];
        using var document = JsonDocument.Parse(json);
        Assert.Equal(23, document.RootElement.GetProperty("Cases").GetInt32());
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), json);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("FileTransferRuntimeSuite", "FrontierNativeAppRequest")]
    public async Task FrontierAppRequest_NativeParentDeathRevokesTheAppLease(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-native-app-20260915", "parent-death-" + host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAppRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "parent-death"], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        var line = Assert.Single(result.Stdout.Split('\n').Select(l => l.Trim()), l => l.StartsWith("frontier-app-parent-death="));
        using var document = JsonDocument.Parse(line["frontier-app-parent-death=".Length..]);
        var data = document.RootElement;
        var path = Path.Combine(directory, "parent-death-result.json");
        Assert.Equal(path, data.GetProperty("Result").GetString());
        Process? child = null;
        try
        {
            try { child = Process.GetProcessById(data.GetProperty("AppProcessId").GetInt32()); }
            catch (ArgumentException) { }
            if (child is not null)
            {
                Assert.Equal(data.GetProperty("AppCreation").GetInt64(), child.StartTime.ToUniversalTime().ToFileTimeUtc());
                Assert.True(child.WaitForExit(15000), "Native app fixture did not exit after parent death");
            }
            Assert.True(File.Exists(path), "App did not record native parent-death rejection");
            using var completion = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.True(completion.RootElement.GetProperty("ParentDeathRejected").GetBoolean());
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited && child.StartTime.ToUniversalTime().ToFileTimeUtc() == data.GetProperty("AppCreation").GetInt64()) { child.Kill(); child.WaitForExit(5000); }
                child.Dispose();
            }
        }
    }
}
