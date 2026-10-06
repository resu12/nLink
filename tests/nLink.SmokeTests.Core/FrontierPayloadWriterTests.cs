namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierPayloadWriterTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NormalLoader_ExposesOriginalNativeWriterTakeWithoutForwardingAuthority(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            $type=[NLink.Tools.LocalGuiWriterRequest+CanonicalPayloadWrapper]
            $take=$type.GetMethod('TryTakeWithWriters')
            $bound=$type.GetProperty('NativeWritersBound')
            if($null -eq $take -or $null -eq $bound) {throw 'Production native-writer payload take missing'}
            $parameters=$take.GetParameters()
            if($parameters.Count -ne 2 -or
                $parameters[0].ParameterType -ne [NLink.Tools.LocalGuiWriterRequest+FrontierGuiReports] -or
                $parameters[1].ParameterType -ne [NLink.Tools.LocalGuiWriterRequest+FrontierParentCustody] -or
                $take.ReturnType -ne [NLink.Tools.LocalGuiWriterRequest+Completion] -or
                $null -ne $bound.GetSetMethod() -or $type.GetConstructors().Count -ne 0) {throw 'Native-writer take API changed'}
            $flags=[Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
            if(@($type.GetMethods($flags) | Where-Object Name -like '*ForTest').Count -or
                $null -ne $type.GetMethod('CreateParentPayloadOfferForTest',$flags) -or
                $null -ne ('NLink.Tools.LocalGuiWriterRequest+AttemptPayloadParentForTest' -as [type]) -or
                $null -ne ('NLink.Tools.TransferPairForTest' -as [type])) {throw 'Test or forwarding authority leaked'}
            'payload-writers-production-default-ok'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("payload-writers-production-default-ok", result.Stdout);
    }

    [Theory]
    [InlineData("powershell", "writer-valid")]
    [InlineData("pwsh", "writer-valid")]
    [InlineData("powershell", "writer-foreign")]
    [InlineData("pwsh", "writer-foreign")]
    [InlineData("pwsh", "writer-request")]
    [InlineData("pwsh", "writer-process")]
    [InlineData("pwsh", "writer-id")]
    [InlineData("pwsh", "writer-sequence")]
    public async Task OriginalPayloadPair_RequiresExactAcceptedNativeWriters(string host, string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "payload-writers-20260926", host + "-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "writer-source-read-cross-process-gui-valid", fault], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("attempt-payload-native-writers-checked=" + fault, result.Stdout);
        var gui = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log"));
        Assert.Contains("attempt-payload-native-writer-handoff-ok", gui);
        var error = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stderr.log"));
        FrontierAttemptWritersTests.AssertWorkProfile(error, "before_seal", "after_seal", "before_processing",
            "processing_open", "processing_after_final_capture", "processing_after_recovery_capture",
            "processing_before_parse", "processing_after_parse", "processing_after_matches", "after_processing",
            "before_role_probes", "after_role_probes", "after_role_bind", "after_role_checks",
            "before_source_bind_controls", "after_source_bind_controls", "after_source_protection_check",
            "before_destination_bind_controls", "after_destination_bind_controls", "after_coexistence",
            "after_payload_offer", "before_cleanup");
        var recoveryProfile = System.Text.RegularExpressions.Regex.Match(error,
            @"stage=processing_after_recovery_capture;[^\r\n]*; recovery_calls=(\d+); recovery_ticks=(\d+); recovery_failed_calls=(\d+)");
        Assert.True(recoveryProfile.Success);
        Assert.True(long.Parse(recoveryProfile.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.True(long.Parse(recoveryProfile.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal("0", recoveryProfile.Groups[3].Value);
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
    public async Task RecoveryCheckProfile_PreservesExceptionalExitWithoutStartingWork(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "recovery-check-cost-20260927", "exception-" + host + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchLoader.ps1') $RepoRoot
            $flags=[Reflection.BindingFlags]'Instance,NonPublic'
            # A fresh unowned binding: it cannot authorize anything. Exercise
            # the existing first-predicate rejection, not a genuine owner's check.
            $finalizer=[Activator]::CreateInstance([NLink.Tools.LocalGuiWriterRequest+Finalizer],$true)
            $type=[NLink.Tools.LocalGuiWriterRequest+FrontierFinalizerBinding]
            $binding=$type.GetConstructors($flags)[0].Invoke([object[]]@($finalizer,$null))
            $clock=[Diagnostics.Stopwatch]::new()
            [NLink.Tests.AttemptWorkProfileForTest]::Start($clock)
            $caught=$false
            try { [void]$type.GetMethod('CheckRecoveryWork',$flags).Invoke($binding,$null) }
            catch {
                $cause=$_.Exception.GetBaseException()
                if($cause -isnot [IO.InvalidDataException] -or $cause.Message -cne 'recovery_work_not_current') {throw}
                $caught=$true
            }
            if(-not $caught -or $clock.IsRunning -or $clock.ElapsedTicks -ne 0) {throw 'Recovery profiling swallowed failure or changed original clock'}
            [NLink.Tests.AttemptWorkProfileForTest]::Mark('failed')
            [NLink.Tests.AttemptWorkProfileForTest]::WriteAfterCleanup()
            [NLink.Tests.AttemptWorkProfileForTest]::Start($clock)
            [NLink.Tests.AttemptWorkProfileForTest]::Mark('reset')
            [NLink.Tests.AttemptWorkProfileForTest]::WriteAfterCleanup()
            'recovery-check-profile-exception-ok'
            """, [root], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("recovery-check-profile-exception-ok", result.Stdout);
        Assert.Matches(@"stage=failed; active_ticks=0;[^\r\n]*; recovery_calls=1; recovery_ticks=[1-9]\d*; recovery_failed_calls=1", result.Stderr);
        Assert.Matches(@"stage=reset; active_ticks=0;[^\r\n]*; recovery_calls=0; recovery_ticks=0; recovery_failed_calls=0", result.Stderr);
    }
}
