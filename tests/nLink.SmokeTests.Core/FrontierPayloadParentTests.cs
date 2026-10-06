namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierPayloadParentTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task ParentAssociationDiagnostics_PreserveGuardOrderAndSingleEvaluations()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tools/FileTransferOps/LocalGuiWriterCanonicalPayloadParent.cs"));
        var start = source.IndexOf("internal bool MatchesParentPayload(", StringComparison.Ordinal);
        var end = source.IndexOf("public sealed partial class CanonicalPayloadWrapper", start, StringComparison.Ordinal);
        var body = System.Text.RegularExpressions.Regex.Replace(source[start..end], @"\s+", " ");
        var guards = System.Text.RegularExpressions.Regex.Matches(body, "if \\((.*?)\\) return RefuseParentMatch\\(\"([^\"]+)\"\\);")
            .Select(match => match.Groups[1].Value + " => " + match.Groups[2].Value).ToArray();
        Assert.Equal(new[]
        {
            "!Object.ReferenceEquals(original, this) => foreign-custody",
            "!Object.ReferenceEquals(owner, request) => foreign-owner",
            "!Object.ReferenceEquals(accepted, result) => accepted-result",
            "!IsCurrent => custody-not-current",
            "result.WriterContext.RequestId != gui => gui-request",
            "!CanonicalPayloadParent.SameCompletion(result.WriterContext, completionHash) => completion-hash",
            "!facts.MatchesCompletion(result.WriterContext) => completion-facts",
            "!facts.MatchesPayloadWriters(requests, processes, creations, writers, sequences) => payload-writer-facts"
        }, guards);
        foreach (var call in new[] { "IsCurrent", "SameCompletion(", "facts.MatchesCompletion(", "facts.MatchesPayloadWriters(" })
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(body, System.Text.RegularExpressions.Regex.Escape(call)).Count);
        Assert.DoesNotContain("Console.", body);
        Assert.DoesNotContain("Accept(", body);
        Assert.Contains("partial void ObserveParentMatch(string reason);", source);
        Assert.Contains("{ ObserveParentMatch(reason); return false; }", source);
        Assert.DoesNotContain("LastParentMatchDiagnosticForTest", source);
    }

    [Fact]
    public async Task ParentTakeDiagnostics_PreserveGuardOrderAndSingleEvaluations()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tools/FileTransferOps/LocalGuiWriterCanonicalPayloadParent.cs"));
        var start = source.IndexOf("public RelayedCompletion TryTakeAfterExit(", StringComparison.Ordinal);
        var end = source.IndexOf("public FrontierRelayCustody WriterCustody(", start, StringComparison.Ordinal);
        var body = System.Text.RegularExpressions.Regex.Replace(source[start..end], @"\s+", " ");
        var guards = System.Text.RegularExpressions.Regex.Matches(body, "if \\(([^{};]*?)\\) return RefuseTake\\(\"([^\"]+)\"\\);")
            .Select(match => match.Groups[1].Value + " => " + match.Groups[2].Value).ToArray();
        Assert.Equal(new[]
        {
            "!Object.ReferenceEquals(original, this) => foreign-instance",
            "!Object.ReferenceEquals(reports, candidate) => foreign-reports",
            "disposed => disposed-before-take",
            "takeAttempted => already-attempted",
            "!owner.workerExit.WaitOne(0) => worker-running",
            "!WrapperSucceeded() => wrapper-exit",
            "reports.Stdout.Status != TaskStatus.RanToCompletion => stdout-incomplete",
            "reports.Stderr.Status != TaskStatus.RanToCompletion => stderr-incomplete",
            "channel == null => channel-missing",
            "!channel.Current => channel-not-current-before-native",
            "channel.State != 3 => channel-not-committed",
            "!FilesCurrent() => files-before-native",
            "work.IsRunning => work-running",
            "work.ElapsedMilliseconds > 5000 => work-before-native",
            "disposed => disposed-before-native",
            "result == null => native-relay-result",
            "native == null => native-relay-custody",
            "!matchesParent => native-parent-match",
            "!FilesCurrent() => files-after-native",
            "!channel.Current => channel-not-current-after-native",
            "work.ElapsedMilliseconds > 5000 => work-after-native",
            "!preserveNative && !Object.ReferenceEquals(native, result.TryTakeFrontierCustody()) => native-custody-take",
            "disposed => disposed-before-bind",
            "disposed => disposed-after-bind",
            "writerLifetime.IsCancellationRequested => writer-lifetime-cancelled",
            "!FilesCurrent() => files-after-bind",
            "work.ElapsedMilliseconds > 5000 => work-after-bind"
        }, guards);
        foreach (var call in new[] { "owner.TakeRelayAfterExit()", "result.TryTakeFrontierCustody()", "native.MatchesParentPayload(", "work.Start()", "work.Stop()" })
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(body, System.Text.RegularExpressions.Regex.Escape(call)).Count);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(body, @"FilesCurrent\(\)").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(body, @"channel\.Current").Count);
        Assert.Contains("bool matchesParent = native.MatchesParentPayload(owner, result, gui, completionHash, requests, processes, creations, writers, sequences); ObserveParentPayloadMatch(native); if (!matchesParent)", body);
        Assert.Contains("BeginPayloadTakeDiagnostics();", body);
        Assert.Contains("completionAttempted = true; result = owner.TakeRelayAfterExit(); native = owner.frontierRelayCustody; ObserveNativePayloadTake(native); work.Start();", body);
        Assert.Contains("if (!success) { Dispose(); if (native != null && !preserveNative) native.Dispose(); }", body);
        Assert.Contains("return TryTakeAfterExitCore(candidate, false, out result, out completionAttempted);", body);
        Assert.DoesNotContain("Console.", body);
        Assert.Contains("partial void ObservePayloadTake(string reason);", source);
        Assert.Contains("{ ObservePayloadTake(reason); return null; }", source);
    }

    [Theory]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "valid")]
    [InlineData("pwsh", "cancel-before-commit")]
    [InlineData("pwsh", "foreign-writer")]
    [InlineData("pwsh", "native-accept-scope")]
    [InlineData("pwsh", "native-begin-disposed")]
    [InlineData("pwsh", "native-wrapper-state")]
    [InlineData("pwsh", "native-transfer-cancel")]
    [InlineData("pwsh", "parent-budget")]
    [InlineData("pwsh", "wrapper-nonzero")]
    [InlineData("pwsh", "wrapper-killed")]
    public async Task OriginalWrapper_PayloadPairRequiresMatchingNativeParentRelay(string host, string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "payload-parent-20260927", host + "-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "writer-source-read-cross-process-gui-valid", "writer-valid", fault], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("attempt-payload-parent-checked=" + fault, result.Stdout);
        if (fault is "wrapper-nonzero" or "wrapper-killed")
            Assert.DoesNotContain("parent-payload-take;", result.Stderr);
        else
        {
            Assert.Contains("parent-payload-take; call=first; result=" + (fault == "valid" ? "True; reason=accepted" : "False; reason="), result.Stderr);
            Assert.Contains("parent-payload-take; call=repeat; result=False; reason=" + (fault == "valid" ? "already-attempted" : "disposed-before-take"), result.Stderr);
            if (fault == "foreign-writer") Assert.Contains("call=first; result=False; reason=native-parent-match", result.Stderr);
            if (fault == "parent-budget") Assert.Contains("call=first; result=False; reason=work-before-native", result.Stderr);
            if (fault == "valid") Assert.Contains("call=first; result=True; reason=accepted; association=matched", result.Stderr);
            if (fault == "foreign-writer") Assert.Contains("call=first; result=False; reason=native-parent-match; association=payload-writer-facts", result.Stderr);
            if (fault == "valid") Assert.Contains("native_begin=ready; native_accept=accepted; native_close=open", result.Stderr);
            if (fault == "native-accept-scope") Assert.Contains("association=accepted-result; native_begin=ready; native_accept=scope-byte:0; native_close=begin:ready,accept:scope-byte:0,taking:True,accepted:False", result.Stderr);
            if (fault == "native-begin-disposed") Assert.Contains("association=accepted-result; native_begin=closed; native_accept=closed; native_close=begin:not-called,accept:not-called,taking:False,accepted:False", result.Stderr);
            if (fault == "valid") Assert.Contains("native_retirement=open", result.Stderr);
            if (fault == "native-accept-scope") Assert.Contains("native_retirement=retire:LocalGuiWriterRelay.cs:TakeRelayAfterExit,wrapper-exit:not-rejected,pull:pulled", result.Stderr);
            if (fault == "native-begin-disposed") Assert.Contains("native_retirement=Dispose,wrapper-exit:not-rejected,pull:pulled", result.Stderr);
            if (fault == "native-wrapper-state") Assert.Contains("native_retirement=WrapperExited,wrapper-exit:transfer-state,pull:pulled", result.Stderr);
            if (fault == "native-transfer-cancel") Assert.True(
                result.Stderr.Contains("native_retirement=TransferCancelled,wrapper-exit:not-rejected,pull:pulled", StringComparison.Ordinal) ||
                result.Stderr.Contains("native_retirement=WrapperExited,wrapper-exit:transfer-current,pull:pulled", StringComparison.Ordinal), result.Stderr);
            if (fault == "valid" || fault.StartsWith("native-", StringComparison.Ordinal))
                Assert.Contains("native-relay-wrapper; forward=forwarded; transfer_close=Close:release", result.Stdout);
        }
        var gui = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log"));
        Assert.Contains("attempt-payload-native-writer-handoff-ok", gui);
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
    public async Task PayloadControlChannel_RejectsTamperForeignBindingAndCancellation(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptPayloadParentChannel.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("payload-parent-channel-ok", result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task NormalLoader_ExposesParentPayloadHandoffWithoutAnalyzerAuthority(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            $flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
            $native=[NLink.Tools.LocalGuiWriterRequest+FrontierRelayCustody]
            foreach($name in @('NativeBeginDiagnosticForTest','NativeAcceptDiagnosticForTest','NativeCloseDiagnosticForTest','NativeRetirementDiagnosticForTest','NativeExitDiagnosticForTest')) {
                if($null -ne $native.GetField($name,$flags)) {throw 'Native acceptance diagnostic leaked into default loader'}
            }
            if($null -ne $native.GetMethod('NativeRejectForTest',$flags)) {throw 'Native acceptance trace helper leaked into default loader'}
            if($null -ne $native.GetMethod('NativeExitRejectForTest',$flags) -or
                $null -ne [NLink.Tools.LocalGuiWriterRequest].GetField('NativePullDiagnosticForTest',$flags) -or
                $null -ne [NLink.Tools.LocalGuiWriterRequest].GetMethod('NativePullRejectForTest',$flags)) {throw 'Native retirement trace leaked into default loader'}
            if($null -ne [NLink.Tools.LocalGuiWriterRequest+FrontierRelayRegistration].GetField('NativeAfterPullForTest',$flags) -or
                $null -ne [NLink.Tools.LocalGuiWriterRequest+FrontierRelayRegistration].GetMethod('ObserveNativePullForTest',$flags)) {throw 'Native pull ordering fixture leaked into default loader'}
            foreach($name in @('NativeForwardDiagnosticForTest','NativeTransferCloseDiagnosticForTest')) {
                if($null -ne [NLink.Tools.LocalGuiWriterRequest+FrontierRelayHandoff].GetField($name,$flags)) {throw 'Native wrapper trace leaked into default loader'}
            }
            $parent='NLink.Tools.LocalGuiWriterRequest+CanonicalPayloadParent' -as [type]
            $start=[NLink.Tools.LocalGuiWriterRequest].GetMethod('TryStartPayloadParent')
            $offer=[NLink.Tools.LocalGuiWriterRequest+FrontierRelayHandoff].GetMethod('TryOfferPayloadPair')
            if($null -eq $parent -or $null -eq $start -or $null -eq $offer) {throw 'Production parent payload handoff missing'}
            if($start.ReturnType -ne $parent -or $start.GetParameters().Count -ne 1 -or $offer.GetParameters().Count -ne 2 -or
                $null -eq $parent.GetMethod('TryPullIfOffered') -or $null -eq $parent.GetMethod('TryTakeAfterExit') -or
                $null -eq $parent.GetMethod('WriterCustody') -or $null -eq $parent.GetProperty('ProvisionalCustody')) {throw 'Production parent payload contract changed'}
            foreach($name in @('LastTakeDiagnosticForTest','LastParentMatchDiagnosticForTest','LastNativeBeginDiagnosticForTest','LastNativeAcceptDiagnosticForTest','LastNativeCloseDiagnosticForTest','LastNativeRetirementDiagnosticForTest')) {
                if($null -ne $parent.GetProperty($name,$flags)) {throw 'Parent diagnostics leaked into normal loader'}
            }
            if($null -ne $native.GetField('payloadParentForTest',$flags) -or
                $null -ne $native.GetMethod('CopyTransferPairToPrimaryForTest',$flags) -or
                $null -ne $parent.GetMethod('DuplicateForPrimaryForTest',$flags) -or
                $null -ne [NLink.Tools.LocalGuiWriterRequest+FrontierRelayHandoff].GetField('CancelPayloadCommitForTest',$flags) -or
                $null -ne [NLink.Tools.LocalGuiWriterRequest].Assembly.GetType('NLink.Tools.TransferPairForTest')) {throw 'Payload analyzer forwarding/fault controls leaked into normal loader'}
            if($null -ne ('NLink.Tools.LocalGuiWriterRequest+AttemptPayloadParentForTest' -as [type]) -or
                $null -ne [NLink.Tools.LocalGuiWriterRequest].GetMethod('TryStartPayloadParentForTest') -or
                $null -ne [NLink.Tools.LocalGuiWriterRequest+FrontierRelayHandoff].GetMethod('TryOfferPayloadPairForTest')) {throw 'Parent payload leaked into default loader'}
            'payload-parent-default-ok'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("payload-parent-default-ok", result.Stdout);
    }
}
