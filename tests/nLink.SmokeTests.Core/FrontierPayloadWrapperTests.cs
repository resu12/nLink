namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierPayloadWrapperTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> ListenerCases =>
        from host in new[] { "powershell", "pwsh" }
        from fault in new[] { "request-close", "request-dispose", "gui-normal", "gui-nonzero", "gui-killed",
            "adopter-dispose", "wrapper-finally", "watchdog-unwind", "startup-failure", "cancel-connect", "stall-packet", "wrong-pid",
            "connected-request-dispose", "connected-adopter-dispose", "connected-deadline-dispose" }
        select new object[] { host, fault };

    [Theory]
    [MemberData(nameof(ListenerCases))]
    public async Task OriginalListener_NoOfferRetiresOnOriginalLifetime(string host, string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "listener-lifetime-implementation-20260927",
            host + "-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "valid", "listener-" + fault], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("listener-cleanup-checked=" + fault, result.Stdout);
        if (fault is not "wrapper-finally" and not "watchdog-unwind")
            Assert.Contains("listener-pre-finally-retired=" + fault, result.Stdout);
        if (fault == "stall-packet")
            Assert.Contains("payload-wrapper-adoption-failed; stage=packet_length;", result.Stdout + result.Stderr);
        if (fault == "wrong-pid")
            Assert.Contains("payload-wrapper-adoption-failed; stage=client_scope;", result.Stdout + result.Stderr);
    }

    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from fault in new[] { "valid", "bad-ack", "foreign-identity", "cancel-before-ack", "gui-nonzero", "gui-killed", "spent-budget", "stall-commit" }
        select new object[] { host, fault };

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task OriginalListener_AuthenticatedOfferBeyondFormerWait(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "listener-lifetime-implementation-20260927",
            host + "-delayed-valid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "writer-source-read-cross-process-carrier-full-gui-valid", "delayed-valid"], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        var gui = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log"));
        var age = System.Text.RegularExpressions.Regex.Match(gui, @"listener-delayed-offer-age-ms=(\d+)");
        Assert.True(age.Success, gui);
        Assert.True(long.Parse(age.Groups[1].Value) >= 105000);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("attempt-payload-wrapper-offer=delayed-valid", gui);
        Assert.Contains("attempt-payload-wrapper-checked=delayed-valid", result.Stdout);
        Assert.DoesNotContain("payload-wrapper-adoption-failed;", result.Stdout + result.Stderr);
        var received = Assert.Single(Directory.GetFiles(Path.Combine(directory, "native-carrier-received"), "*.bin", SearchOption.AllDirectories));
        foreach (var path in new[] { received, Path.Combine(directory, "filetransfer-tuna-gui-payload.bin") })
        {
            using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(268435456, released.Length);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalGui_PayloadCustodyRequiresCommittedNormalExit(string host, string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "payload-wrapper-20260926", host + "-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "writer-source-read-cross-process-gui-valid", fault], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("attempt-payload-wrapper-checked=" + fault, result.Stdout);
        var gui = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log"));
        Assert.Contains("attempt-payload-wrapper-offer=" + fault, gui);
        var guiError = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stderr.log"));
        var wrapperOutput = result.Stdout + result.Stderr;
        if (fault == "valid")
        {
            Assert.DoesNotContain("payload-offer-failed;", guiError);
            Assert.DoesNotContain("payload-wrapper-adoption-failed;", wrapperOutput);
        }
        else if (fault == "bad-ack")
        {
            Assert.Contains("payload-offer-failed; stage=first_ack; exception_type=none;", guiError);
        }
        else if (fault == "spent-budget")
        {
            Assert.Contains("payload-wrapper-adoption-failed; stage=work_admission; exception_type=none;", wrapperOutput);
        }
        else if (fault == "stall-commit")
        {
            Assert.Contains("payload-offer-failed; stage=commit_stall;", guiError);
            Assert.Contains("payload-wrapper-adoption-failed; stage=commit_read;", wrapperOutput);
            var deadline = System.Text.RegularExpressions.Regex.Match(guiError, @"stage=commit_stall;[^\r\n]*; elapsed_ms=(\d+)");
            Assert.True(deadline.Success, guiError);
            Assert.InRange(long.Parse(deadline.Groups[1].Value), 9500, 15000);
        }
        Assert.DoesNotContain("payload-offer-contract-failed;", guiError);
        if (fault != "gui-killed")
        {
            Assert.Contains("attempt-source-custody-ok", gui);
            Assert.Contains("attempt-destination-closed-writers-ok", gui);
        }
        // Paths are used only after every owner has retired, to test release.
        var received = Assert.Single(Directory.GetFiles(Path.Combine(directory, "native-received"), "*.bin", SearchOption.AllDirectories));
        foreach (var path in new[] { received, Path.Combine(directory, "filetransfer-tuna-gui-payload.bin") })
        {
            using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(268435456, released.Length);
        }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task DefaultLoader_ExposesBoundPayloadHandoffWithoutTestOrForwardingAuthority(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            $type='NLink.Tools.LocalGuiWriterRequest+CanonicalPayloadWrapper' -as [type]
            $start=[NLink.Tools.LocalGuiWriterRequest].GetMethod('TryStartPayloadWrapper')
            $offer=[NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptDispatch].GetMethod('TryOfferPayloadsToWrapper')
            if($null -eq $type -or $null -eq $start -or $null -eq $offer) {throw 'Production payload handoff missing'}
            if($type.GetConstructors().Count -ne 0 -or $start.GetParameters().Count -ne 1 -or
                $offer.GetParameters().Count -ne 3 -or $null -eq $type.GetMethod('TryTakeAfterExit') -or
                $null -eq $type.GetMethod('TryVerifyFiles')) {throw 'Production payload API contract changed'}
            if(@($type.GetMethods([Reflection.BindingFlags]'Instance,Static,Public,NonPublic') |
                    Where-Object Name -like '*ForTest').Count -ne 0 -or
                @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count -ne 0 -or
                $null -ne $type.GetField('fault',[Reflection.BindingFlags]'Instance,NonPublic') -or
                $null -ne ('NLink.Tools.LocalGuiWriterRequest+AttemptPayloadParentForTest' -as [type])) {throw 'Downstream or fault API leaked'}
            if($null -ne ('NLink.Tools.LocalGuiWriterRequest+AttemptPayloadWrapperForTest' -as [type]) -or
                $null -ne [NLink.Tools.LocalGuiWriterRequest].GetMethod('TryStartPayloadWrapperForTest')) {throw 'Payload wrapper leaked into default loader'}
            'payload-wrapper-default-ok'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("payload-wrapper-default-ok", result.Stdout);
    }
}
