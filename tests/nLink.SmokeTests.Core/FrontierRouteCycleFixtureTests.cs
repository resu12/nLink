using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Tests;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
[Trait("RequiredConfiguration", "Debug")]
public sealed class FrontierRouteCycleFixtureTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", "default")]
    [InlineData("pwsh", "default")]
    [InlineData("powershell", "canonical")]
    [InlineData("pwsh", "canonical")]
    [InlineData("powershell", "release")]
    [InlineData("pwsh", "release")]
    [InlineData("powershell", "ineligible")]
    [InlineData("pwsh", "ineligible")]
    [InlineData("powershell", "non-gui")]
    [InlineData("pwsh", "non-gui")]
    public async Task GuiWorkBudget_IsFixedScopedCumulativeAndFailClosed(string host, string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "gui-work-budget", host + "-" + mode + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierGuiWorkBudgetControl.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, mode], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("gui-work-budget=" + (mode == "canonical" ? 30000 : 15000), result.Stdout);
        Assert.Contains("nested-expiry;exit-expiry;revocation;no-revival;one-clock;no-authority", result.Stdout);
    }

    [Theory]
    [InlineData("powershell", "Debug", AttemptRouteCycleProfile.Scenario, AttemptRouteCycleProfile.Name, 245000)]
    [InlineData("pwsh", "Debug", AttemptRouteCycleProfile.Scenario, AttemptRouteCycleProfile.Name, 245000)]
    [InlineData("powershell", "Debug", AttemptRouteCycleProfile.Scenario, "", 185000)]
    [InlineData("pwsh", "Debug", AttemptRouteCycleProfile.Scenario, "", 185000)]
    [InlineData("powershell", "Release", AttemptRouteCycleProfile.Scenario, AttemptRouteCycleProfile.Name, 185000)]
    [InlineData("pwsh", "Release", AttemptRouteCycleProfile.Scenario, AttemptRouteCycleProfile.Name, 185000)]
    [InlineData("powershell", "Debug", "writer-source-read-cross-process-carrier-full-valid", "", 185000)]
    [InlineData("pwsh", "Debug", "writer-source-read-cross-process-carrier-full-valid", "", 185000)]
    [InlineData("powershell", "Debug", "writer-source-read-cross-process-carrier-frontier-late-publication", AttemptRouteCycleProfile.Ineligible, 75000)]
    [InlineData("pwsh", "Debug", "writer-source-read-cross-process-carrier-frontier-late-publication", AttemptRouteCycleProfile.Ineligible, 75000)]
    [InlineData("powershell", "Debug", "writer-source-read-native-valid", "", 25000)]
    [InlineData("pwsh", "Debug", "writer-source-read-native-valid", "", 25000)]
    public async Task ReadHandoffBudget_IsScopedAndUsesOneCumulativeClock(string host, string configuration, string scenario, string profile, int expected)
    {
        var result = await RunPowerShellScriptTextAsync("""
            param($Root,$Configuration,$Scenario,$Profile,[int]$Expected)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            . (Join-Path $Root 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
            $env:NLINK_TEST_FRONTIER_WORKER_CONFIGURATION=$Configuration
            $env:NLINK_TEST_FRONTIER_ROUTE_CYCLE=$Profile;$attemptCase=$Scenario
            $ast=Read-AttemptAst (Join-Path $Root 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptSenderReadGui.ps1')
            $budget=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$readBudget'},$true))
            if($budget.Count -ne 1) {throw 'Read budget assignment ambiguous'}
            Invoke-Expression $budget[0].Extent.Text
            if($readBudget -ne $Expected) {throw 'Read budget escaped exact configuration/profile'}
            $clocks=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$readClock'},$true))
            $waits=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $n.Member.Extent.Text -ceq 'WaitOne'},$true))
            if($clocks.Count -ne 1 -or $waits.Count -ne 3) {throw 'Read clock reset or waits changed'}
            foreach($wait in $waits) {
                if($wait.Arguments.Count -ne 1 -or $wait.Arguments[0].Extent.Text -cne '[Math]::Max(0,$readBudget-[int]$readClock.ElapsedMilliseconds)') {throw 'Wait replaced cumulative remaining budget'}
            }
            'read-handoff-budget-control-ok'
            """, [FindRepoRoot(), configuration, scenario, profile, expected.ToString()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("read-handoff-budget-control-ok", result.Stdout);
        if (expected == 245000)
            Assert.Equal(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(expected) - NknAccelerationTransportTests.GetCarrierBudget(true, true));
    }

    [Theory]
    [InlineData("powershell", "primary-join-fail")]
    [InlineData("pwsh", "primary-join-fail")]
    [InlineData("powershell", "join-fail")]
    [InlineData("pwsh", "join-fail")]
    [InlineData("powershell", "primary-join-ok")]
    [InlineData("pwsh", "primary-join-ok")]
    [InlineData("powershell", "success")]
    [InlineData("pwsh", "success")]
    [InlineData("powershell", "primary-join-fail-diagnostic-fail")]
    [InlineData("pwsh", "primary-join-fail-diagnostic-fail")]
    public async Task WriterJoin_PreservesPrimaryErrorAndStillRejectsJoinOnlyFailure(string host, string mode)
    {
        var result = await RunPowerShellScriptTextAsync("""
            param($Root,$Mode)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            . (Join-Path $Root 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
            $ast=Read-AttemptAst (Join-Path $Root 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptWritersGui.ps1')
            $seam=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.StringConstantExpressionAst] -and
                $n.Value.StartsWith('finally {') -and $n.Value.Contains('FrontierAttemptWritersJoin.ps1')},$true))
            if($seam.Count -ne 1) {throw 'Writer finally seam ambiguous'}
            $tail=Replace-AttemptSeam $seam[0].Value ". (Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptWritersJoin.ps1')" 'Invoke-ControlJoin'
            if($Mode.EndsWith('diagnostic-fail')) {
                $write=@($tail -split "`n"|Where-Object {$_.Contains('[Console]::Error.WriteLine(')})
                if($write.Count -ne 1) {throw 'Diagnostic seam ambiguous'}
                $tail=Replace-AttemptSeam $tail $write[0] "throw 'diagnostic-write-failed'"
            }
            Add-Type 'public sealed class FixtureJoinOwner {public int Disposals; public void Dispose() {Disposals++;}}'
            $FrontierOwner=[FixtureJoinOwner]::new();$script:drained=$true;$script:joins=0
            $scenarioFailure=$null;$script:target=[object]::new()
            function Invoke-PrimaryError {
                [CmdletBinding()]param()
                $record=[Management.Automation.ErrorRecord]::new([IO.IOException]::new('primary-before-shutdown'),
                    'fixture-primary-id',[Management.Automation.ErrorCategory]::ReadError,$script:target)
                $record.ErrorDetails=[Management.Automation.ErrorDetails]::new('primary detailed failure')
                $PSCmdlet.ThrowTerminatingError($record)
            }
            if($Mode.StartsWith('primary-')) {try {Invoke-PrimaryError} catch {$scenarioFailure=$_}}
            $snapshot=if($null -ne $scenarioFailure) {[pscustomobject]@{exception=$scenarioFailure.Exception;id=$scenarioFailure.FullyQualifiedErrorId;
                target=$scenarioFailure.TargetObject;stack=$scenarioFailure.ScriptStackTrace;details=$scenarioFailure.ErrorDetails.Message}} else {$null}
            function Invoke-ControlJoin {$script:joins++;if($Mode.Contains('join-fail')) {throw 'secondary-shutdown-failure'}}
            # Execute only the fixture finally block with a scalar join and owner;
            # no app, native request, transport or coordinator is constructed.
            Invoke-Expression ('function Invoke-Control {try {} '+$tail+'; if($null -ne $scenarioFailure) {throw $scenarioFailure}}')
            $caught=$null;try {Invoke-Control} catch {$caught=$_}
            if($script:joins -ne 1 -or $FrontierOwner.Disposals -ne 1) {throw 'Join/disposal omitted or repeated'}
            if($null -ne $snapshot) {
                if($null -eq $caught -or -not [object]::ReferenceEquals($caught.Exception,$snapshot.exception) -or
                    $caught.FullyQualifiedErrorId -cne $snapshot.id -or -not [object]::ReferenceEquals($caught.TargetObject,$snapshot.target) -or
                    $caught.ScriptStackTrace -cne $snapshot.stack -or $caught.ErrorDetails.Message -cne $snapshot.details) {throw 'Primary ErrorRecord changed'}
            } elseif($Mode -ceq 'join-fail') {
                if($null -eq $caught -or $caught.Exception.Message -cne 'secondary-shutdown-failure') {throw 'Join-only failure was hidden'}
            } elseif($null -ne $caught) {throw 'Successful cleanup acquired a failure'}
            'writer-join-primary-control-ok'
            """, [FindRepoRoot(), mode], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("writer-join-primary-control-ok", result.Stdout);
    }

    [Theory]
    [InlineData(false, false, 70)]
    [InlineData(true, false, 180)]
    [InlineData(true, true, 240)]
    public void CarrierBudget_ChangesOnlyRouteCycle(bool fullSize, bool cycle, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), NknAccelerationTransportTests.GetCarrierBudget(fullSize, cycle));

    [Theory]
    [InlineData("powershell", AttemptRouteCycleProfile.Scenario, AttemptRouteCycleProfile.Name, 270)]
    [InlineData("pwsh", AttemptRouteCycleProfile.Scenario, AttemptRouteCycleProfile.Name, 270)]
    [InlineData("powershell", AttemptRouteCycleProfile.Scenario, "", 210)]
    [InlineData("pwsh", AttemptRouteCycleProfile.Scenario, "", 210)]
    [InlineData("powershell", "writer-source-read-cross-process-carrier-full-valid", "", 210)]
    [InlineData("pwsh", "writer-source-read-cross-process-carrier-full-valid", "", 210)]
    [InlineData("powershell", "writer-source-read-cross-process-carrier-frontier-late-publication", AttemptRouteCycleProfile.Ineligible, 100)]
    [InlineData("pwsh", "writer-source-read-cross-process-carrier-frontier-late-publication", AttemptRouteCycleProfile.Ineligible, 100)]
    public async Task DispatchBudget_PropagatesExactTimeoutAndRejectsChangedProjection(string host, string scenario, string profile, int expected)
    {
        var result = await RunPowerShellScriptTextAsync("""
            param($Root,$Scenario,$Profile,[int]$Expected)
            $ErrorActionPreference='Stop';Set-StrictMode -Version Latest
            . (Join-Path $Root 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
            $env:NLINK_TEST_FRONTIER_ROUTE_CYCLE=$Profile
            $Case=$Scenario;$RouteCycleProfile=$Profile
            $ast=Read-AttemptAst (Join-Path $Root 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1')
            foreach($name in @('$canonicalTailProfile','$TimeoutSeconds')) {
                $assignment=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq $name},$true))
                if($assignment.Count -ne 1) {throw 'Budget assignment ambiguous'}
                Invoke-Expression $assignment[0].Extent.Text
            }
            if($TimeoutSeconds -ne $Expected) {throw 'Profile budget differs'}
            $wrapper=Read-AttemptAst (Join-Path $Root 'tools/Run-FileTransferTunaGuiSmoke.ps1')
            Invoke-Expression (Get-AttemptFunction $wrapper 'Get-TunaGuiFinalizationGraceSeconds')
            $finalizationGraceSeconds=Get-TunaGuiFinalizationGraceSeconds
            $watchdog=@($wrapper.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $n.Name -ceq 'Invoke-TunaGuiSmokeWithHardDeadline'},$true))
            if($watchdog.Count -ne 1) {throw 'Original watchdog ambiguous'}
            foreach($name in @('$hardDeadlineSeconds','$maximumHardDeadlineSeconds')) {
                $assignment=@($watchdog[0].Body.EndBlock.Statements|Where-Object {$_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -ceq $name})
                if($assignment.Count -ne 1) {throw 'Original deadline assignment ambiguous'}
                Invoke-Expression $assignment[0].Extent.Text
            }
            if($finalizationGraceSeconds -ne 120 -or $hardDeadlineSeconds -ne $Expected+120 -or
                $maximumHardDeadlineSeconds -ne 2*$Expected+120) {throw 'Original deadline formula changed'}
            $gui=Read-AttemptAst (Join-Path $Root 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchGui.ps1')
            Invoke-Expression (Get-AttemptFunction $gui 'Invoke-FileTransferTunaHandoffFallbackCycle')
            # Value-only projection guard; no owner, app, transport or coordinator is created.
            function Assert-PrivateScenarioInputs {$script:checks++}
            $attemptCase=$Scenario;$script:checks=0;$script:cycleCalled=$false
            $parameters=@{PayloadSizeBytes=268435456;Direction='helpee-to-helper';PayerMode='helpee';FaultMode='switch-off';
                RouteMode='live-regular-activation-cycle';TimeoutMs=$Expected*1000;ProgressTimeoutMs=30000}
            Invoke-FileTransferTunaHandoffFallbackCycle @parameters
            if(-not $script:cycleCalled -or $script:checks -ne 1) {throw 'Exact projection was not consumed'}
            foreach($field in @('TimeoutMs','ProgressTimeoutMs')) {
                $changed=$parameters.Clone();$changed[$field]++;$script:cycleCalled=$false;$rejected=$false
                try {Invoke-FileTransferTunaHandoffFallbackCycle @changed}
                catch {if($_.Exception.Message -cne 'Actual scenario consumed another projection') {throw};$rejected=$true}
                if(-not $rejected -or $script:cycleCalled) {throw 'Changed timing projection admitted'}
            }
            'fixture-budget-projection-ok'
            """, [FindRepoRoot(), scenario, profile, expected.ToString()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("fixture-budget-projection-ok", result.Stdout);
    }

    [Fact]
    public async Task RouteCycleBudget_LeavesOriginalFinalizationAndOuterReserve()
    {
        var root = FindRepoRoot();
        var source = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/FrontierRouteCycleFixtureTests.cs"));
        var composition = source[source.LastIndexOf("public async Task OriginalApps_PreserveIneligibilityOrResolveAfterRealRouteCycle", StringComparison.Ordinal)..];
        Assert.Contains("timeout: positive ? TimeSpan.FromMinutes(72) : TimeSpan.FromMinutes(10)", composition);
        var wrapper = await File.ReadAllTextAsync(Path.Combine(root, "tools/Run-FileTransferTunaGuiSmoke.ps1"));
        Assert.Contains("$hardDeadlineSeconds = [long]$TimeoutSeconds + [long]$finalizationGraceSeconds", wrapper);
        Assert.Contains("$maximumHardDeadlineSeconds = (2L * [long]$TimeoutSeconds) + [long]$finalizationGraceSeconds", wrapper);
        var acceptance = await File.ReadAllTextAsync(Path.Combine(root, "tools/Run-FileTransferRouteAcceptance.ps1"));
        Assert.Contains("$analysisTimeoutSeconds = 3600", acceptance);
        Assert.Equal(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(72) - TimeSpan.FromSeconds(660) - TimeSpan.FromSeconds(3600));
    }

    [Theory]
    [InlineData(0, false, "local-edge-only")]
    [InlineData(0, true, "local-edge-only")]
    [InlineData(1, true, "carrier-requested-order-unproven")]
    [InlineData(-1, true, "callback-not-observed")]
    public void WaitCancellation_ReportsPassiveStatesWithoutInventingTimerOrder(int observed, bool carrier, string source)
    {
        using var output = new StringWriter();
        AttemptOfflineLatePublicationProgress.WriteWaitCancellation("original-recent-recheck", observed,
            carrier, true, 100, 170, 180, TimeSpan.FromSeconds(180), output);
        const string marker = "carrier-frontier-wait-cancellation=";
        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith(marker, line);
        using var json = JsonDocument.Parse(line[marker.Length..]);
        var snapshot = json.RootElement;
        Assert.Equal("original-recent-recheck", snapshot.GetProperty("Stage").GetString());
        Assert.Equal(source, snapshot.GetProperty("LinkedCancellation").GetString());
        Assert.Equal(carrier, snapshot.GetProperty("CarrierCancelled").GetBoolean());
        Assert.True(snapshot.GetProperty("EdgeCancelled").GetBoolean());
        Assert.Equal(System.Diagnostics.Stopwatch.Frequency, snapshot.GetProperty("Frequency").GetInt64());
        Assert.Equal(80, snapshot.GetProperty("CarrierElapsedTicks").GetInt64());
        Assert.Equal(10, snapshot.GetProperty("EdgeElapsedTicks").GetInt64());
        Assert.Equal(180000, snapshot.GetProperty("CarrierBudgetMs").GetDouble());
        Assert.Equal(8000, snapshot.GetProperty("EdgeBudgetMs").GetInt32());
    }

    [Fact]
    public void WaitCancellation_OutputFailureDoesNotEscape()
    {
        using var output = new FailingCancellationWriter();
        AttemptOfflineLatePublicationProgress.WriteWaitCancellation("original-recent-recheck", 1,
            true, true, 100, 170, 180, TimeSpan.FromSeconds(180), output);
        Assert.Equal(1, output.Attempts);
    }

    private sealed class FailingCancellationWriter : StringWriter
    {
        internal int Attempts;
        public override void WriteLine(string? value)
        {
            Attempts++;
            throw new IOException("diagnostic-output-failed");
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task TunaReader_WaitsForPeerPublicationAndHonorsCancellation(int open)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var read = new NknAccelerationTransportTests.RetainedRuntimeReadGate(source, ready.Task,
            (offset, bytes) => { Assert.Equal(0, offset); Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes); }, cycleOpen: open);
        var buffer = new byte[4];
        using var cancelled = new CancellationTokenSource();
        var pending = read.ReadAsync(buffer.AsMemory(), cancelled.Token).AsTask();
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, source.Position);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, source.Position);
        pending = read.ReadAsync(buffer.AsMemory()).AsTask();
        Assert.False(pending.IsCompleted);
        ready.SetResult();
        Assert.Equal(4, await pending);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer);
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 6)]
    public void ReaderProfile_HasExactBoundedRouteSequence(bool cycle, int count)
    {
        FileTransferRoute[] expected = [FileTransferRoute.RegularNknV4Fast, FileTransferRoute.RegularNknV4Fast,
            FileTransferRoute.FileTunaV4, FileTransferRoute.PostTunaFallbackV6,
            FileTransferRoute.FileTunaV4, FileTransferRoute.PostTunaFallbackV6];
        for (var i = 0; i < count; i++) Assert.Equal(expected[i], AttemptRouteCycleProfile.ExpectedRoute(i, cycle));
        Assert.Throws<IOException>(() => AttemptRouteCycleProfile.ExpectedRoute(-1, cycle));
        Assert.Throws<IOException>(() => AttemptRouteCycleProfile.ExpectedRoute(count, cycle));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task ReaderGate_CannotCrossPrefixOrSurviveCancellation(int open)
    {
        var limit = AttemptRouteCycleProfile.ReadLimit(open);
        using var ct = new CancellationTokenSource();
        await Assert.ThrowsAsync<IOException>(() => AttemptRouteCycleProfile.BeforeReadAsync(open, limit + 1, 1, ct.Token).AsTask());
        if (open is 3 or 4 or 5)
        {
            await AttemptRouteCycleProfile.BeforeReadAsync(open, limit - 1, 1, ct.Token);
            await Assert.ThrowsAsync<IOException>(() => AttemptRouteCycleProfile.BeforeReadAsync(open, limit - 1, 2, ct.Token).AsTask());
        }
        var pending = AttemptRouteCycleProfile.BeforeReadAsync(open, limit, 1, ct.Token).AsTask();
        Assert.False(pending.IsCompleted);
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task FirstFallbackReader_AllowsOnlyOneNonterminalRepairChunk()
    {
        var chunk = AttemptCanonicalTailProfile.ChunkBytes;
        var payload = new byte[5 * chunk];
        Array.Fill(payload, (byte)0x5a, 3 * chunk, chunk);
        using var source = new MemoryStream(payload);
        source.Position = 3L * chunk;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = 0;
        using var read = new NknAccelerationTransportTests.RetainedRuntimeReadGate(source, release.Task,
            (offset, bytes) => { Assert.Equal(3L * chunk, offset); Assert.All(bytes, b => Assert.Equal((byte)0x5a, b)); observations++; }, cycleOpen: 4);
        using var ct = new CancellationTokenSource();
        try
        {
            var buffer = new byte[chunk];
            var repair = read.ReadAsync(buffer.AsMemory(), ct.Token).AsTask();
            Assert.True(repair.IsCompleted, "The first fallback must not park its bounded repair read.");
            Assert.Equal(chunk, await repair);
            Assert.All(buffer, b => Assert.Equal((byte)0x5a, b));
            Assert.Equal(1, observations);
            Assert.False(release.Task.IsCompleted);
            Assert.Equal(4L * chunk, source.Position);
            Assert.True(source.Position < source.Length);
            var beyond = read.ReadAsync(buffer.AsMemory(0, 1), ct.Token).AsTask();
            Assert.False(beyond.IsCompleted);
            release.SetResult(); // A scheduling signal cannot expand the fixed window.
            Assert.False(beyond.IsCompleted);
            ct.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => beyond);
            Assert.Equal(4L * chunk, source.Position);
            Assert.Equal(1, observations);
        }
        finally { ct.Cancel(); }
    }

    [Fact]
    public async Task FirstFallbackReader_ReplaysPrefixBeforeOneNewNonterminalChunk()
    {
        var chunk = AttemptCanonicalTailProfile.ChunkBytes;
        var payload = new byte[5 * chunk];
        for (var i = 0; i < 5; i++) Array.Fill(payload, (byte)(i + 1), i * chunk, chunk);
        using var source = new MemoryStream(payload);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = 0;
        using var read = new NknAccelerationTransportTests.RetainedRuntimeReadGate(source, release.Task,
            (offset, bytes) =>
            {
                Assert.Equal((long)observations * chunk, offset);
                Assert.All(bytes, b => Assert.Equal((byte)(observations + 1), b));
                observations++;
            }, cycleOpen: 4);
        using var ct = new CancellationTokenSource();
        try
        {
            var buffer = new byte[chunk];
            // The original sender loads one chunk at a time, including prefix replay.
            for (var i = 0; i < 4; i++)
            {
                var pending = read.ReadAsync(buffer.AsMemory(), ct.Token).AsTask();
                Assert.True(pending.IsCompleted);
                Assert.Equal(chunk, await pending);
                Assert.All(buffer, b => Assert.Equal((byte)(i + 1), b));
                Assert.Equal((long)(i + 1) * chunk, source.Position);
            }
            Assert.Equal(4, observations);
            Assert.False(release.Task.IsCompleted);
            Assert.True(source.Position < source.Length);
            var beyond = read.ReadAsync(buffer.AsMemory(), ct.Token).AsTask();
            Assert.False(beyond.IsCompleted);
            release.SetResult();
            Assert.False(beyond.IsCompleted);
            ct.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => beyond);
            Assert.Equal(4L * chunk, source.Position);
            Assert.Equal(4, observations);
        }
        finally { ct.Cancel(); }
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    [InlineData(4 * AttemptCanonicalTailProfile.ChunkBytes - 1, 2)]
    [InlineData(4 * AttemptCanonicalTailProfile.ChunkBytes + 1, 1)]
    public async Task FirstFallbackReader_RejectsReadsOutsideBoundedWindow(long offset, int count)
        => await Assert.ThrowsAsync<IOException>(() => AttemptRouteCycleProfile.BeforeReadAsync(4, offset, count, CancellationToken.None).AsTask());

    [Theory]
    [InlineData(0)]
    [InlineData(3 * AttemptCanonicalTailProfile.ChunkBytes)]
    public async Task FirstFallbackReader_CancelledReplayOrRepairDoesNotRead(long offset)
    {
        using var source = new MemoryStream(new byte[5 * AttemptCanonicalTailProfile.ChunkBytes]);
        source.Position = offset;
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        using var read = new NknAccelerationTransportTests.RetainedRuntimeReadGate(source, Task.CompletedTask,
            (_, _) => throw new InvalidOperationException("Cancelled repair read was observed."), cycleOpen: 4);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.ReadAsync(new byte[1].AsMemory(), ct.Token).AsTask());
        Assert.Equal(offset, source.Position);
    }

    [Theory]
    [InlineData("Release", "writer-source-read-cross-process-carrier-full-canonical-tail-late-publication", "canonical-cycle-v1", "Route-cycle fixture requires Debug")]
    [InlineData("Debug", "writer-valid", "canonical-cycle-v1", "Route-cycle fixture requires the canonical tail")]
    [InlineData("Debug", "writer-valid", "late-ineligible-v1", "Ineligibility control requires the original small late fixture")]
    public async Task DispatchProfile_RejectsWrongConfigurationOrCompositionBeforeLaunch(string configuration, string scenario, string profile, string error)
    {
        var result = await RunPowerShellScriptTextAsync("""
            param($Root,$Configuration,$Scenario,$Profile)
            $env:NLINK_TEST_FRONTIER_WORKER_CONFIGURATION=$Configuration
            try {
                & (Join-Path $Root 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1') -RepoRoot $Root -Directory 'unused' -Case $Scenario -WorkerConfiguration $Configuration -RouteCycleProfile $Profile
                throw 'Profile was admitted'
            } catch { [Console]::WriteLine($_.Exception.Message) }
            """, [FindRepoRoot(), configuration, scenario, profile], "pwsh");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(error, result.Stdout);
        Assert.DoesNotContain("frontier-attempt-dispatch=", result.Stdout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalApps_PreserveIneligibilityOrResolveAfterRealRouteCycle(bool positive)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "route-cycle-late", (positive ? "pos-" : "neg-") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1"));
        var scenario = positive ? AttemptRouteCycleProfile.Scenario : "writer-source-read-cross-process-carrier-frontier-late-publication";
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, scenario, positive ? "writer-valid" : "", positive ? "valid" : "",
                positive ? AttemptCanonicalTailProfile.Name : "", positive ? "" : "frontier-late-publication", "Debug",
                positive ? AttemptRouteCycleProfile.Name : AttemptRouteCycleProfile.Ineligible], "pwsh", timeout: positive ? TimeSpan.FromMinutes(72) : TimeSpan.FromMinutes(10));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "late-publication-primary-observation.json")));
        var observation = json.RootElement;
        Assert.Equal(positive ? "filetransfer_fallback_frontier_recent_progress_grace_recheck_missing" :
            "filetransfer_tuna_activation_delivery_unproven", observation.GetProperty("SelectedFailure").GetString());
        Assert.Equal(positive ? 1 : 0, observation.GetProperty("ResolverCalls").GetInt32());
        Assert.Equal(positive, observation.GetProperty("OriginalOutputCommitted").GetBoolean());
        Assert.Equal(0, observation.GetProperty("OpenPinsAfterCleanup").GetInt32());
        Assert.True(observation.GetProperty("TextCleared").GetBoolean());
        foreach (var flag in new[] { "LiveProofEligible", "NativeWriterCustodyEstablished", "PublicationCompletionIssued" })
            Assert.False(observation.GetProperty(flag).GetBoolean());
        Assert.Equal(observation.GetProperty("ReportsBefore").GetString(), observation.GetProperty("ReportsAfter").GetString());
        if (!positive)
        {
            Assert.Equal(0, observation.GetProperty("WitnessCount").GetInt32());
            return;
        }
        Assert.InRange(observation.GetProperty("WitnessCount").GetInt32(), 1, 64);
        var report = await File.ReadAllTextAsync(Path.Combine(directory, "filetransfer-operator-verdict.txt"));
        foreach (var line in new[] { "file_tuna_route_cycle_delivery_verdict=pass", "file_tuna_activation_1_delivery_verdict=pass",
            "file_tuna_activation_2_delivery_verdict=pass", "file_tuna_route_cycle_full_compact_parity_verdict=pass" }) Assert.Contains(line, report);
        foreach (var role in new[] { "helpee", "helper" })
        {
            var output = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(Path.Combine(directory, "process-output"), role + "-*.stdout.log")));
            Assert.Contains("frontier-worker-configuration=Debug;", output);
            Assert.Contains("carrier-route-cycle=two-tuna-prefixes; role=" + role + "; chunks=3,6;", output);
        }
    }
}
