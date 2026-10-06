using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierAttemptWritersTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "writer-valid", "writer-helper-first", "writer-app-mismatch", "writer-sequence-mismatch",
            "writer-parent-cancelled", "writer-closed-output", "writer-join-spent-budget" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OriginalAttempt_JoinsNamedCoreWritersWithoutLivePromotion(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-attempt-writers-20260926", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAttemptDispatch.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-attempt-dispatch=" + scenario, result.Stdout);
        Assert.Contains("attempt-role-writers-ok", await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log")));
        var error = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stderr.log"));
        string[] finalCheckpoints = scenario is "writer-app-mismatch" or "writer-sequence-mismatch"
            ? ["before_cleanup"] : ["after_role_checks", "before_cleanup"];
        AssertWorkProfile(error, ["before_seal", "after_seal", "before_processing", "after_processing",
            "before_role_probes", "after_role_probes", "after_role_bind", .. finalCheckpoints]);
        if (scenario == "writer-join-spent-budget")
        {
            Assert.Contains("role-writer-inputs-rejected; stage=attempt; gui_disposed=True; active_ms=", error);
            Assert.Contains("role-writer-copy-repeat-failed; original_current=False; repeat_evaluated=False; repeat_returned_join=False;", error);
            Assert.Contains("attempt-role-writer-spent-budget-rejected", await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log")));
        }
        else
        {
            Assert.DoesNotContain("role-writer-copy-repeat-failed;", error);
            if (scenario == "writer-parent-cancelled")
                Assert.Contains("role-writer-inputs-rejected; stage=attempt; gui_disposed=False;", error);
        }
    }

    internal static void AssertWorkProfile(string output, params string[] checkpoints)
    {
        var rows = output.Split('\n').Where(line => line.StartsWith("role-work-profile;", StringComparison.Ordinal))
            .Select(line => System.Text.RegularExpressions.Regex.Match(line,
                @"^role-work-profile; stage=([a-z_]+); active_ticks=(\d+); wall_ticks=(\d+); work_running=(True|False); frequency=(\d+)"))
            .ToArray();
        Assert.NotEmpty(rows);
        long previousActive = -1, previousWall = -1;
        foreach (var row in rows)
        {
            Assert.True(row.Success);
            var active = long.Parse(row.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var wall = long.Parse(row.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(active >= previousActive && wall >= previousWall);
            Assert.True(long.Parse(row.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture) > 0);
            previousActive = active; previousWall = wall;
        }
        var names = rows.Select(row => row.Groups[1].Value).ToArray();
        var previousIndex = -1;
        foreach (var checkpoint in checkpoints)
        {
            var index = Array.IndexOf(names, checkpoint);
            Assert.True(index > previousIndex, checkpoint);
            previousIndex = index;
        }
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task DefaultLoader_ExposesOnlyOriginalRoleWriterJoin(string host)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
            $type='NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptWriters' -as [type]
            $dispatch=[NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptDispatch]
            if($null -eq $type -or $null -eq $dispatch.GetMethod('TryBindRoleWriters')) {throw 'Production closed-writer join missing'}
            if($type.GetConstructors().Length -or
                @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count -or
                $type.GetFields().Length -or $null -ne $dispatch.GetMethod('TryBindRoleWritersForTest')) {throw 'Mutable/imported writer custody exposed'}
            if(($dispatch.GetMethod('TryBindRoleWriters').GetParameters().Name -join ',') -cne 'binding,match') {throw 'Writer join accepts caller-authored facts'}
            foreach($payload in @([NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptSource],
                [NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptDestination])) {
                $method=$payload.GetMethod('TryBindClosedWriters')
                if($null -eq $method -or $method.GetParameters().Length -ne 1 -or
                    $method.GetParameters()[0].ParameterType -ne $type -or
                    $null -ne $payload.GetMethod('TryBindClosedWritersForTest')) {throw 'Original payload/writer association missing'}
            }
            if($null -ne ('NLink.Tools.LocalGuiWriterRequest+AttemptRoleWritersForTest' -as [type]) -or
                $null -ne ('NLink.Tests.AttemptWorkProfileForTest' -as [type])) {throw 'Role writer API/profile leaked'}
            'role-writer-default-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("role-writer-default-ok", result.Stdout);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task RunAppAsync(string directory, string role, string signal, string scenario)
    {
        using var started = EventWaitHandle.OpenExisting(signal + "." + role + ".ready");
        using var ready = EventWaitHandle.OpenExisting(signal + "." + role + ".writer-ready");
        using var finish = EventWaitHandle.OpenExisting(signal + "." + role + ".exit");
        using var helperCompleted = scenario == "writer-helper-first"
            ? EventWaitHandle.OpenExisting(signal + ".helper.writer-completed") : null;
        File.AppendAllText(Path.Combine(directory, "launches.txt"), role + "\n");
        if (!FrontierNativeReceiverTests.Automatic)
            Environment.SetEnvironmentVariable("NLINK_APP_CANONICAL_DESTINATION_V1", null);
        else if (role == "helper")
            Assert.Equal("1", Environment.GetEnvironmentVariable("NLINK_APP_CANONICAL_DESTINATION_V1"));
        else
            Assert.NotEqual("1", Environment.GetEnvironmentVariable("NLINK_APP_CANONICAL_DESTINATION_V1"));
        var lifetime = SessionFileTransferService.FrontierAppLifetime.StartFromEnvironment();
        Assert.NotNull(lifetime);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Action? afterShutdown = null;
        try
        {
            LocalOperationalLog.Info("AttemptWriterFixture", "event=ordinary_app_startup");
            Console.Out.WriteLine("ordinary-" + role); Console.Error.WriteLine("error-" + role);
            started.Set();
            await ((Task)lifetime.GetType().GetField("setup", flags)!.GetValue(lifetime)!).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True((bool)lifetime.GetType().GetField("ready", flags)!.GetValue(lifetime)!);
            await SessionFileTransferV4SenderTests.PrepareProducerRegistryAsync(lifetime, "producer-writer", role == "helpee" ? 0 : 1);
            File.WriteAllText(Path.Combine(directory, role + ".attached"), Environment.GetEnvironmentVariable("NLINK_APP_FRONTIER_REQUEST_ID"));
            if (scenario.StartsWith("writer-destination-gui-", StringComparison.Ordinal)) ready.Set();
            if (role == "helper" && scenario.StartsWith("writer-destination-", StringComparison.Ordinal))
                afterShutdown = await FrontierAttemptDestinationReceiver.RunReceiverAsync(lifetime, directory, scenario);
            ready.Set();
            if (scenario.StartsWith("writer-source-read-cross-process-", StringComparison.Ordinal))
            {
                using var done = EventWaitHandle.OpenExisting(signal + (role == "helpee" ? ".sender-read-done" : ".receiver-read-done"));
                try { await SessionFileTransferV4SenderTests.RunCrossProcessAttemptAsync(lifetime, directory, role, signal, scenario); }
                finally { done.Set(); }
            }
            else if (role == "helpee" && scenario.StartsWith("writer-source-read-", StringComparison.Ordinal))
            {
                using var readDone = EventWaitHandle.OpenExisting(signal + ".sender-read-done");
                try
                {
                    if (scenario.StartsWith("writer-source-read-native-", StringComparison.Ordinal))
                        await FrontierNativePickerTests.RunNativePickerAsync(lifetime, directory, signal, scenario);
                    else await SessionFileTransferV4SenderTests.ReadAttemptSourceAsync(lifetime, scenario);
                }
                finally { readDone.Set(); }
            }
            Assert.True(finish.WaitOne(40000));
            if (helperCompleted is not null && role == "helpee") Assert.True(helperCompleted.WaitOne(5000));
        }
        finally
        {
            Assert.True(SessionFileTransferService.FrontierAppLifetime.CompleteAndFlush(lifetime, TimeSpan.FromSeconds(5)));
            if (FrontierNativeReceiverTests.Automatic && role == "helper")
            {
                var handoff = FrontierNativeReceiverTests.Handoff(lifetime);
                Assert.True(handoff is null || handoff.IsCompleted);
                Console.WriteLine("native-receiver-shutdown-joined");
            }
            afterShutdown?.Invoke();
        }
        Assert.True(lifetime.HandoffCompleted);
        if (helperCompleted is not null && role == "helper") helperCompleted.Set();
    }
}
