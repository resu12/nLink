namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierPrimaryJoinTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    public Task PrimaryJoin_BindsOriginalRedactedDataSessionWithoutChangingPrimary(string host)
        => PrimaryJoin_UsesOriginalFailureAndNativeReceipt(host, "data-session-valid");

    [Theory]
    [InlineData("pwsh", "wake-token")]
    [InlineData("powershell", "wake-token")]
    [InlineData("pwsh", "wake-generation")]
    [InlineData("pwsh", "watchdog-token")]
    [InlineData("powershell", "watchdog-token")]
    [InlineData("pwsh", "watchdog-generation")]
    [InlineData("pwsh", "missing-both")]
    [InlineData("pwsh", "unknown-token")]
    [InlineData("pwsh", "opaque-duplicate")]
    [InlineData("pwsh", "opaque-conflict")]
    [InlineData("pwsh", "terminal")]
    [InlineData("pwsh", "pause")]
    public Task PrimaryJoin_DataSessionRejectsContradictionsWithoutChangingPrimary(string host, string fault)
        => PrimaryJoin_UsesOriginalFailureAndNativeReceipt(host, "data-session-" + fault);

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public Task PrimaryJoin_BindsGenuineWatchdogRootWithoutRecoveringFailure(string host)
        => PrimaryJoin_UsesOriginalFailureAndNativeReceipt(host, "watchdog-valid");

    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    public Task PrimaryJoin_BindsOriginalRedactedSessionToProjectedScope(string host)
        => PrimaryJoin_UsesOriginalFailureAndNativeReceipt(host, "scope-session-redacted");

    [Theory]
    [InlineData("pwsh", "scope-two-transfers")]
    [InlineData("powershell", "scope-two-transfers")]
    [InlineData("pwsh", "scope-duplicate-receipt")]
    public Task PrimaryJoin_SeparatesOriginalTransfersWithinOneWriter(string host, string scenario)
        => PrimaryJoin_UsesOriginalFailureAndNativeReceipt(host, scenario);

    public static IEnumerable<object[]> ScopeFaultCases =>
        new[] { "foreign-session", "foreign-transfer", "foreign-both", "conflict", "unscoped-competing", "unscoped-future", "unscoped-link" }
            .Concat(new[] { "file_transfer_inbound_terminal", "filetransfer_live_route_epoch_terminal", "filetransfer_terminalized_by_session_end",
                "filetransfer_data_session_recreated", "filetransfer_data_session_removed" }.SelectMany(name => new[] { "terminal-" + name, "unscoped-terminal-" + name }))
            .Concat(new[] { "filetransfer_v4_pause_state_sent", "filetransfer_v4_pause_state_send_failed", "filetransfer_v4_peer_state_received",
                "filetransfer_v4_state_sent", "future_pause_event" }.SelectMany(name => new[] { "pause-" + name, "unscoped-pause-" + name }))
            .Select(fault => new object[] { "pwsh", "scope-fault-" + fault });

    [Theory]
    [MemberData(nameof(ScopeFaultCases))]
    public Task PrimaryJoin_ScopeRejectsLostIdentityBeforeFilteringEvidence(string host, string scenario)
        => PrimaryJoin_UsesOriginalFailureAndNativeReceipt(host, scenario);

    public static IEnumerable<object[]> WatchdogCases =>
        from host in new[] { "powershell", "pwsh" }
        from fault in new[] { "rearm", "successor", "unknown-root", "missing-root", "root-highest", "direction", "authority",
            "missing-elapsed", "grace", "missing-clock", "malformed-clock", "overflow-clock", "root-clock-before", "root-clock-after",
            "recheck-highest", "recheck-at-deadline", "recheck-after-deadline", "recheck-progress", "recheck-deadline", "stale-recheck",
            "wrong-recheck-generation", "competing-root", "terminal", "pause", "scope", "duplicate-fields",
            "reordered-root", "missing-recheck", "duplicate-root", "conflicting-root" }
        select new object[] { host, "watchdog-" + fault };

    [Theory]
    [MemberData(nameof(WatchdogCases))]
    public Task PrimaryJoin_WatchdogRejectsContradictionsWithoutChangingPrimary(string host, string scenario)
        => PrimaryJoin_UsesOriginalFailureAndNativeReceipt(host, scenario);

    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "failure-copy", "failure-reason", "failure-line", "candidate-duplicate", "recovered",
            "view-verdict", "completion-hash", "completion-projection", "index-line-copy", "receipt-profile", "receipt-root",
            "receipt-repair", "receipt-frame", "receipt-lifecycle", "receipt-completion", "receipt-observation", "witness-overflow",
            "foreign-context", "repeat", "post-mutation", "parent-cancel", "snapshot-overflow" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PrimaryJoin_UsesOriginalFailureAndNativeReceipt(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directoryScenario = scenario.StartsWith("scope-fault-", StringComparison.Ordinal) ? "scope-fault" :
            scenario.StartsWith("data-session-", StringComparison.Ordinal) ? "data-session" : scenario;
        var directory = Path.Combine(root, "artifacts", "frontier-primary-join-20260917", host + "-" + directoryScenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-join-" + scenario, "seal",
            typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-primary-join=" + scenario, result.Stdout);
    }
}
