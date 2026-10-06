namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierProcessingCustodyTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "counts", "cursor-counts", "copies", "after-finish", "parent-attempt" }
            .Concat(from sample in new[] { "open", "close" }
                from mutation in new[] { "gui", "stage", "completion", "app", "digest", "body", "tail", "reserved", "ack", "state",
                    "channel-cancel", "closed", "parent-signal", "ledger", "output", "budget", "cancel", "dispose" }
                select sample + "-" + mutation)
            .Concat(new[] { "files-handle-0", "files-handle-1", "files-handle-2", "files-handle-3", "files-identity", "files-length",
                "files-cancel", "files-budget", "return-finish", "return-dispose", "return-cancel", "return-budget", "read-gui",
                "close-restored", "files-stopped", "read-budget", "extended-budget-window", "extended-copies",
                "extended-close-cancel", "extended-files-stopped", "extended-close-gui" })
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalCustody_ConfinesValidationToOneProcessingCheck(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "processing-custody-20260927", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "buffered-custody-" + scenario, "seal", typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location,
                typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("frontier-gui-launch=", result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task DefaultLoader_DoesNotExportProcessingValidationOrTestHooks(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            $assembly=[NLink.Tools.LocalGuiWriterRequest].Assembly
            if($null -ne $assembly.GetType('NLink.Tests.ProcessingCustodyProbe')) {throw 'Test hook exported'}
            if($null -ne $assembly.GetType('NLink.Tests.RecoveryBufferProbe')) {throw 'Recovery test hook exported'}
            foreach($type in @([NLink.Tools.LocalGuiWriterRequest+Finalizer],
                [NLink.Tools.LocalGuiWriterRequest+FrontierFinalizerBinding],
                [NLink.Tools.LocalGuiWriterRequest+FrontierLaunchOwner])) {
                if(@($type.GetMethods() | Where-Object {$_.Name -in @('ProcessingScopeCurrent','ProcessingReaderFor','CheckProcessingFiles')}).Count) {
                    throw 'Processing validation exported'
                }
            }
            'processing-default-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("processing-default-ok", result.Stdout);
    }
}
