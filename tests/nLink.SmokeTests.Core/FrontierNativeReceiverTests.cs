using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierNativeReceiverTests : FileTransferOpsScriptsTestBase
{
    internal static bool Automatic => Environment.GetEnvironmentVariable("NLINK_TEST_NATIVE_RECEIVER") == "1";
    internal static object? Field(object owner, string name)
        => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner);
    internal static Task<bool>? Handoff(SessionFileTransferService.FrontierAppLifetime owner)
        => (Task<bool>?)Field(owner, "nativeDestinationHandoff");

    [Theory]
    [InlineData("powershell", "writer-destination-gui-valid")]
    [InlineData("pwsh", "writer-destination-gui-valid")]
    [InlineData("pwsh", "writer-destination-valid")]
    [InlineData("pwsh", "writer-destination-pending-dispose")]
    [InlineData("pwsh", "writer-destination-destination")]
    [InlineData("pwsh", "writer-destination-session")]
    [InlineData("pwsh", "writer-destination-canceled")]
    [InlineData("pwsh", "writer-source-read-cross-process-gui-valid")]
    [InlineData("pwsh", "writer-source-read-cross-process-carrier-full-gui-valid")]
    public async Task OriginalReceiver_CapturesAndHandsOffThroughAppLifetime(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "production-native-receiver-20260929",
            host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1"));
        script = script.Replace("$ErrorActionPreference='Stop';",
            "$env:NLINK_TEST_NATIVE_RECEIVER='1'; $ErrorActionPreference='Stop';", StringComparison.Ordinal);
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        var output = Assert.Single(Directory.GetFiles(Path.Combine(directory, "process-output"), "helper-*.stdout.log"));
        Assert.Contains("native-receiver-shutdown-joined", await File.ReadAllTextAsync(output));
        if (scenario.Contains("-gui-valid", StringComparison.Ordinal))
            Assert.Contains("native-receiver-handoff-ok", await File.ReadAllTextAsync(output));
    }
}
