namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierPayloadParentBoundaryTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> ProtectionCases()
    {
        foreach (var host in new[] { "powershell", "pwsh" })
        foreach (var layout in new[] { "ordinary", "full" })
        foreach (var fault in new[] { "protected", "missing-directory", "empty", "ambiguous", "wrong-layout", "missing-source", "source-unprotected", "receiver-unprotected" })
            yield return [host, layout, fault];
    }

    [Theory]
    [MemberData(nameof(ProtectionCases))]
    public async Task AnalysisBoundary_RequiresExactlyOneProtectedPairInDeclaredLayout(string host, string layout, string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "parent-fixture-layout-20260927", host + "-" + layout + "-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot,[string]$Directory,[string]$Layout,[string]$Fault)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadParentProbe.ps1'),[ref]$tokens,[ref]$errors)
            if($errors.Count) {throw 'Parent probe parse failed'}
            $boundary=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -ceq "$"+"Stage -ceq 'AnalysisBoundary'"},$true))
            if($boundary.Count -ne 1) {throw 'Expected one analysis boundary'}
            $protection=@($boundary[0].Clauses[0].Item2.Statements | Where-Object {$_ -is [Management.Automation.Language.IfStatementAst] -and $_.Clauses[0].Item1.Extent.Text -ceq "$"+"parentFault -ceq 'valid'"})
            if($protection.Count -ne 1) {throw 'Expected one filesystem protection probe'}
            # Execute only the filesystem probe, not a mocked custody/currentness gate.
            $probe=[scriptblock]::Create('& '+$protection[0].Clauses[0].Item2.Extent.Text)
            $Case=if($Layout -ceq 'full') {'writer-source-read-cross-process-carrier-full-gui-valid'} else {'writer-source-read-cross-process-gui-valid'}
            $receiveRoot=if($Layout -ceq 'full') {'native-carrier-received'} else {'native-received'}
            if($Fault -ceq 'wrong-layout') {$receiveRoot=if($Layout -ceq 'full') {'native-received'} else {'native-carrier-received'}}
            $source=Join-Path $Directory 'filetransfer-tuna-gui-payload.bin'
            $receiver=Join-Path (Join-Path $Directory $receiveRoot) 'nested/received.bin'
            $handles=[Collections.Generic.List[IO.FileStream]]::new()
            try {
                if($Fault -cne 'missing-source') {
                    [IO.File]::WriteAllBytes($source,[byte[]](1,2,3))
                    if($Fault -cne 'source-unprotected') {$handles.Add([IO.File]::Open($source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))}
                }
                if($Fault -cne 'missing-directory') {
                    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($receiver))
                    if($Fault -cne 'empty') {
                        [IO.File]::WriteAllBytes($receiver,[byte[]](1,2,3))
                        if($Fault -cne 'receiver-unprotected') {$handles.Add([IO.File]::Open($receiver,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))}
                    }
                    if($Fault -ceq 'ambiguous') {[IO.File]::WriteAllBytes((Join-Path ([IO.Path]::GetDirectoryName($receiver)) 'extra.bin'),[byte[]](4))}
                }
                $failure=''
                try {& $probe} catch {$failure=$_.Exception.Message}
                if($Fault -ceq 'protected') {if($failure) {throw $failure}}
                elseif($Fault -cin @('missing-directory','wrong-layout')) {
                    if($failure -notlike '*Cannot find path*') {throw ('Expected missing declared directory: '+$failure)}
                } elseif($Fault -cin @('empty','ambiguous')) {
                    if($failure -cne 'Unexpected received-file protection inventory') {throw ('Expected exact receiver inventory rejection: '+$failure)}
                } elseif($Fault -ceq 'missing-source') {
                    if($failure -cne 'Source payload missing at analysis boundary') {throw ('Expected missing source rejection: '+$failure)}
                } elseif($failure -cne 'Parent payload lost continuous native file protection') {throw ('Expected unprotected payload rejection: '+$failure)}
            } finally {foreach($handle in $handles) {$handle.Dispose()}}
            'parent-boundary-filesystem-ok'
            """, [root, directory, layout, fault], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("parent-boundary-filesystem-ok", result.Stdout);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task Dispatch_PreservesCaughtFailureWithoutRelaxingIncompleteProofGuard(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1'),[ref]$tokens,[ref]$errors)
            if($errors.Count) {throw 'Dispatcher parse failed'}
            $attempt=@($ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.TryStatementAst]})
            $guards=@($ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.IfStatementAst] -and $_.Clauses[0].Item1.Extent.Text -ceq "$"+"PrimaryPayloadCase -ceq 'canonical-source'"})
            if($attempt.Count -ne 1 -or $guards.Count -ne 1) {throw 'Dispatcher catch/guard selection changed'}
            $catch=[scriptblock]::Create('try {throw [IO.IOException]::new("original-boundary-failure")} catch '+$attempt[0].CatchClauses[0].Body.Extent.Text)
            $failure=''; . $catch
            if($failure -cne 'original-boundary-failure') {throw 'Caught original failure replaced'}
            $guard=[scriptblock]::Create($guards[0].Extent.Text)
            $PrimaryPayloadCase='canonical-fallback'; $rejection=''
            try {& $guard} catch {$rejection=$_.Exception.Message}
            if($rejection -cne 'Original incomplete-proof rejection changed') {throw ('Unexpected failure accepted: '+$rejection)}
            $failure='FileTransfer-Ops retained analysis exited successfully without complete proof artifacts for fixture'; & $guard
            $PrimaryPayloadCase='canonical-source'; $failure='original-boundary-failure'; $rejection=''
            try {& $guard} catch {$rejection=$_.Exception.Message}
            if($rejection -cne 'Canonical source verification failed: original-boundary-failure') {throw 'Canonical-source failure guard changed'}
            $failure='Offline canonical-source verification stopped before route-cycle acceptance'; & $guard
            'parent-dispatch-diagnostic-ok'
            """, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("attempt-dispatch-failure; type=System.IO.IOException; message=original-boundary-failure", result.Stderr);
        Assert.Contains("parent-dispatch-diagnostic-ok", result.Stdout);
    }
}
