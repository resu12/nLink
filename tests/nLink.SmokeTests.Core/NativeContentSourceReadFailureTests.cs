namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class NativeContentSourceReadFailureTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task FailureSelectionIsExactAndNormalLoaderOmitsDiagnostics()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            $path=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionWorker.ps1'
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
            if($errors.Count) {throw 'Worker parse failed'}
            $assignments=@($ast.FindAll({param($n)
                $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$sourceReadFailure'
            },$true))
            if($assignments.Count -ne 1) {throw 'Failure selection differs'}
            $selection=[scriptblock]::Create($assignments[0].Extent.Text+'; $sourceReadFailure')
            foreach($workerHost in @('pwsh','powershell','Pwsh','pwsh-extra')) {
                foreach($Case in @('content-native-scale','content-native-scale-profile','content-native-valid',
                    'content-native-read-profile-valid','content-native-scale-extra','Content-native-scale')) {
                    if((& $selection) -ne ($workerHost -ceq 'pwsh' -and $Case -ceq 'content-native-scale')) {throw 'Selection widened'}
                }
            }
            if([Diagnostics.Process]::GetCurrentProcess().ProcessName -cne 'pwsh') {throw 'Unexpected fixture host identity'}
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionLoader.ps1') $RepoRoot
            if($null -ne [NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs].GetMethod('ContentReadFailureForTest')) {
                throw 'Normal loader exposed failure diagnostics'
            }
            'source-read-failure-selection=exact'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], "pwsh");
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("source-read-failure-selection=exact", result.Stdout);
    }

    [Theory]
    [InlineData("pwsh", "content-native-scale", false, true)]
    [InlineData("powershell", "content-native-scale", false, false)]
    [InlineData("pwsh", "content-native-scale-profile", false, false)]
    [InlineData("pwsh", "content-native-valid", false, false)]
    [InlineData("pwsh", "content-native-read-profile-valid", true, false)]
    [InlineData("powershell", "content-native-read-profile-valid", true, false)]
    public async Task WorkerLoaderPreservesParserSamplingIntent(string host, string scenario, bool parserProfile, bool failureDiagnostic)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot,[string]$Case,[int]$ExpectedParser,[int]$ExpectedFailure)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            $path=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionWorker.ps1'
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
            $bodies=@($ast.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.TryStatementAst]})
            if($errors.Count -or $bodies.Count -ne 1) {throw 'Worker body differs'}
            # Execute the actual initialization, dot-sourced loader and parser gate together,
            # stopping before worker attachment, IPC, source acquisition or scale work.
            $prelude=@($ast.EndBlock.Statements | Where-Object {$_.Extent.EndOffset -le $bodies[0].Extent.StartOffset})
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
            $originalParser=(Get-Item Function:Read-FileTransferLogEvents).ScriptBlock.ToString()
            . ([scriptblock]::Create(($prelude.Extent.Text -join [Environment]::NewLine)))
            if($sourceReadFailure -ne [bool]$ExpectedFailure) {throw 'Failure selection changed after loader'}
            $actualParser=(Get-Item Function:Read-FileTransferLogEvents).ScriptBlock.ToString()
            if(($actualParser -cne $originalParser) -ne [bool]$ExpectedParser) {throw 'Loader changed parser sampling intent'}
            $type=[NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs]
            $hasSnapshot=$null -ne $type.GetMethod('ContentReadFailureForTest')
            if($hasSnapshot -ne [bool]($ExpectedParser -or $ExpectedFailure)) {throw 'Diagnostic compilation changed'}
            $hasFailureWriter=$null -ne (Get-Command Write-NativeContentSourceReadFailure -ErrorAction SilentlyContinue)
            if($hasFailureWriter -ne [bool]$ExpectedFailure) {throw 'Failure helper selection changed'}
            $begin=@($ast.FindAll({param($n)
                $n -is [Management.Automation.Language.IfStatementAst] -and
                $n.Extent.Text.Contains('[NLink.Tests.NativeContentReadProfileForTest]::Begin($owned)')
            },$true))
            $reads=@($ast.FindAll({param($n)
                $n -is [Management.Automation.Language.TryStatementAst] -and
                $n.Body.Extent.Text -ceq '{$events=[object[]]@(Read-FileTransferLogEvents -LogFiles @($SourcePath) -OwnedSource $reader)}'
            },$true))
            if($begin.Count -ne 1 -or $reads.Count -ne 1) {throw 'Read profiling gates differ'}
            # Passive dummy state only: the real Begin/finally gates must agree with
            # the parser gate even when failure diagnostics compiled the profile type.
            $flags=[Reflection.BindingFlags]'Instance,NonPublic'
            $owned=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($type)
            $watch=[Diagnostics.Stopwatch]::new()
            $type.GetField('work',$flags).SetValue($owned,$watch)
            if($hasSnapshot) {
                $type.GetField('sourceLines',$flags).SetValue($owned,[Collections.Generic.List[string]]::new())
                foreach($field in @('PrimaryCheckCallsForTest','PrimaryCheckTicksForTest')) {
                    $type.GetField($field,$flags).SetValue($owned,[long[]]::new(12))
                }
            }
            . ([scriptblock]::Create($begin[0].Extent.Text))
            . ([scriptblock]::Create(($reads[0].Finally.Statements.Extent.Text -join [Environment]::NewLine)))
            if($watch.IsRunning -or $watch.ElapsedTicks -ne 0 -or $type.GetField('closed',$flags).GetValue($owned)) {
                throw 'Profiling gates changed clock or custody'
            }
            if($hasSnapshot) {
                $profileType='NLink.Tests.NativeContentReadProfileForTest' -as [type]
                $inputs=$profileType.GetField('inputs',[Reflection.BindingFlags]'Static,NonPublic').GetValue($null)
                $probe=$type.GetField('SourceReadProbeForTest',$flags).GetValue($owned)
                if([object]::ReferenceEquals($inputs,$owned) -ne [bool]$ExpectedParser -or
                    ($null -ne $probe) -ne [bool]$ExpectedParser) {throw 'Loader enabled unexpected source sampling'}
            }
            'source-read-worker-gates=preserved'
            """;
        var result = await RunPowerShellScriptTextAsync(script,
            [FindRepoRoot(), scenario, parserProfile ? "1" : "0", failureDiagnostic ? "1" : "0"], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("source-read-worker-gates=preserved", result.Stdout);
        var profiles = result.Stdout.Split('\n').Where(row => row.StartsWith("content-native-read-profile=", StringComparison.Ordinal)).ToArray();
        if (parserProfile) Assert.Contains("phase=source_read_finally;", Assert.Single(profiles));
        else Assert.Empty(profiles);
        Assert.DoesNotContain("content-native-source-read-failure=", result.Stdout);
    }

    [Fact]
    public async Task FailureRecordIsBoundedCorrelatedAndPreservesTheOriginalException()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentSourceReadFailure.ps1')
            $script:reads=0
            $owned=[pscustomobject]@{}
            $owned | Add-Member ScriptProperty IsCurrent {throw 'Diagnostic evaluated custody'}
            $owned | Add-Member ScriptMethod ContentReadFailureForTest {
                $script:reads++
                if($script:reads -gt 1) {throw 'Diagnostic unavailable'}
                $text=([string][char]1)*1000
                return @{FirstRejectedPredicate=$text;RejectionSnapshot=$text;CloseReason=$text;CloseSnapshot=$text;
                    NativeException=$text;WorkMilliseconds=20001L;WorkRunning=$false;ReadCalls=99123L;
                    SourceEnded=$false;ProducerMilliseconds=20000L;PrivateContent='must-not-be-retained'}
            }
            $RequestId=[guid]'312e2aca-d904-4d3e-a494-63726b646280'
            $workerHost='pwsh';$Case='content-native-scale';$sourceReadFailure=$true;$parserReadProfile=$false
            $SourcePath='unused';$reader=$null
            function Read-FileTransferLogEvents {throw [IO.IOException]::new('original-source-failure')}
            $path=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionWorker.ps1'
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
            $blocks=@($ast.FindAll({param($n)
                $n -is [Management.Automation.Language.TryStatementAst] -and
                $n.Body.Extent.Text -ceq '{$events=[object[]]@(Read-FileTransferLogEvents -LogFiles @($SourcePath) -OwnedSource $reader)}'
            },$true))
            if($errors.Count -or $blocks.Count -ne 1) {throw 'Read wrapper differs'}
            $read=[scriptblock]::Create($blocks[0].Extent.Text)
            foreach($attempt in 1..2) {
                try {& $read;throw 'Read unexpectedly passed'} catch {
                    if($_.Exception.Message -cne 'original-source-failure') {throw 'Original exception replaced'}
                }
            }
            if($script:reads -ne 2) {throw 'Snapshot count differs'}
            $sourceReadFailure=$false
            try {& $read} catch {if($_.Exception.Message -cne 'original-source-failure') {throw}}
            function Read-FileTransferLogEvents {return @()}
            $sourceReadFailure=$true
            & $read
            Write-NativeContentSourceReadFailure $owned $RequestId 'powershell' $Case
            Write-NativeContentSourceReadFailure $owned $RequestId 'pwsh' 'content-native-scale-profile'
            if($script:reads -ne 2) {throw 'Success or excluded case captured failure'}
            'source-read-failure-exception=preserved'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], "pwsh");
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("source-read-failure-exception=preserved", result.Stdout);
        Assert.DoesNotContain("must-not-be-retained", result.Stdout);
        var line = Assert.Single(result.Stdout.Split('\n').Where(row => row.StartsWith("content-native-source-read-failure=", StringComparison.Ordinal)));
        Assert.InRange(line.TrimEnd('\r').Length, 1, 4096);
        using var json = System.Text.Json.JsonDocument.Parse(line["content-native-source-read-failure=".Length..]);
        var row = json.RootElement;
        Assert.Equal(16, row.EnumerateObject().Count());
        Assert.True(row.GetProperty("DiagnosticOnly").GetBoolean());
        Assert.True(row.GetProperty("StringsTruncated").GetBoolean());
        Assert.Equal(1, row.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal("pwsh", row.GetProperty("Host").GetString());
        Assert.Equal("content-native-scale", row.GetProperty("Case").GetString());
        Assert.Equal("312e2aca-d904-4d3e-a494-63726b646280", row.GetProperty("RequestId").GetString());
        Assert.Equal(20001, row.GetProperty("WorkMilliseconds").GetInt64());
        Assert.Equal(20000, row.GetProperty("ProducerMilliseconds").GetInt64());
        Assert.Equal(99123, row.GetProperty("ReadCalls").GetInt64());
        Assert.False(row.GetProperty("WorkRunning").GetBoolean());
        Assert.False(row.GetProperty("SourceEnded").GetBoolean());
        foreach (var name in new[] { "FirstRejectedPredicate", "CloseReason", "NativeException" })
            Assert.Equal(64, row.GetProperty(name).GetString()!.Length);
        foreach (var name in new[] { "RejectionSnapshot", "CloseSnapshot" })
            Assert.Equal(128, row.GetProperty(name).GetString()!.Length);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task FailureSnapshotReadsOnlyCapturedFieldsAndDoesNotChangeTheClock(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionLoader.ps1') $RepoRoot -ReadProfile
            $type=[NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs]
            $owned=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($type)
            $flags=[Reflection.BindingFlags]'Instance,NonPublic'
            $watch=[Diagnostics.Stopwatch]::StartNew()
            $type.GetField('work',$flags).SetValue($owned,$watch)
            $type.GetField('PrimarySourceRejectionForTest',$flags).SetValue($owned,'read:budget')
            $type.GetField('PrimarySourceSnapshotForTest',$flags).SetValue($owned,'work_ms=20001; lines=99122; units=200000000; started=True; ended=False')
            $type.GetField('PrimarySourceCloseForTest',$flags).SetValue($owned,'ReadSource')
            $type.GetField('PrimarySourceCloseSnapshotForTest',$flags).SetValue($owned,'work_ms=20001; lines=99122; units=200000000; started=True; ended=False')
            $type.GetField('PrimarySourceExceptionForTest',$flags).SetValue($owned,'System.IO.IOException:frontier_source_not_current')
            $type.GetField('PrimaryReadCallsForTest',$flags).SetValue($owned,99123L)
            foreach($running in @($true,$false)) {
                if(-not $running) {$watch.Stop()}
                $before=$watch.ElapsedTicks;$snapshot=$owned.ContentReadFailureForTest()
                if($snapshot.Count -ne 10 -or $snapshot.FirstRejectedPredicate -cne 'read:budget' -or
                    $snapshot.ReadCalls -ne 99123L -or $snapshot.ProducerMilliseconds -ne 20000 -or
                    $snapshot.WorkRunning -ne $running -or $watch.IsRunning -ne $running -or
                    $watch.ElapsedTicks -lt $before -or (-not $running -and $watch.ElapsedTicks -ne $before)) {throw 'Passive snapshot changed state'}
                if($snapshot.RejectionSnapshot -cne $type.GetField('PrimarySourceSnapshotForTest',$flags).GetValue($owned) -or
                    $snapshot.CloseSnapshot -cne $snapshot.RejectionSnapshot -or $snapshot.CloseReason -cne 'ReadSource' -or
                    $snapshot.NativeException -cne 'System.IO.IOException:frontier_source_not_current' -or
                    $type.GetField('closed',$flags).GetValue($owned)) {throw 'Snapshot lost captured fields or evaluated custody'}
            }
            'source-read-failure-snapshot=passive'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("source-read-failure-snapshot=passive", result.Stdout);
    }
}
