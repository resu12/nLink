using System.Text.RegularExpressions;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierPrimaryReadValidationTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task NativeRead_ValidatesBeforePublishingWithoutChangingSharedReader()
    {
        var root = FindRepoRoot();
        var source = Regex.Replace(await File.ReadAllTextAsync(Path.Combine(root, "tools/FileTransferOps/LocalFrontierPrimarySource.cs")), @"\s+", " ");
        Assert.Contains("new GuiSmokeLedgerLfRead(nativeStream, CheckSourceFragment, true)", source);
        Assert.Contains("CheckSourcePublication(); sourceUnits += line.Length; sourceLines.Add(line); return line;", source);
        Assert.Contains("CheckSourcePublication(); sourceEnded = true; return null;", source);
        Assert.Contains("CheckSource();", source);
        Assert.Contains("ProbeReadForTest(\"verified\"); #endif CheckSourceContinuation();", source);
        var shared = await File.ReadAllTextAsync(Path.Combine(root, "tools/GuiSmokeLogLedgerStreamingProcessor.cs"));
        Assert.Contains("LineStarts.Add(lineStart); lineStart = position; current(); return line;", shared);
    }

    public static IEnumerable<object[]> Cases =>
        (from host in new[] { "powershell", "pwsh" }
         from scenario in new[] { "valid", "transient-facts" }
         select new object[] { host, scenario })
        .Concat(from phase in new[] { "entry", "fragment", "commit" }
                from fault in new[] { "scope", "facts", "pin", "cancel", "dispose", "budget" }
                select new object[] { "pwsh", phase + "-" + fault })
        .Concat(from fault in new[] { "scope", "cancel", "dispose", "budget" }
                select new object[] { "pwsh", "verified-" + fault })
        .Concat(from fault in new[] { "facts", "pin", "budget" }
                select new object[] { "pwsh", "eof-" + fault });

    [Theory]
    [InlineData("files-cancel")]
    [InlineData("facts-cancel")]
    public Task NativeFragment_RejectsChannelCancellation(string fault) =>
        NativeRead_RejectsUnvalidatedLinesAndRetainsApprovedCadence("pwsh", "fragment-" + fault);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeRead_RejectsUnvalidatedLinesAndRetainsApprovedCadence(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "primary-read-validation-20260928", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-read-validation-" + scenario, "seal",
                typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("frontier-primary-read-validation=" + scenario, result.Stdout);
    }
}
