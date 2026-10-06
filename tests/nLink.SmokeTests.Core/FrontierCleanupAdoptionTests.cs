using System.Reflection;
using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierCleanupAdoptionTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "writer-only", "positive", "second-first", "delayed-emitter", "app-rejected",
            "cancel-before", "timeout", "copies-repeat", "zero-budget", "premature-signal" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeCleanup_AdoptsOriginalSubmissionsBeforeWaitingForExit(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-cleanup-adoption-20260920", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierCleanupAdoption.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-cleanup-adoption=" + scenario, result.Stdout);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task RunAppAsync(string directory, string scenario, int ordinal)
    {
        var name = Environment.GetEnvironmentVariable("NLINK_APP_FRONTIER_REQUEST")!;
        using var started = EventWaitHandle.OpenExisting(name + ".test.cleanup.started");
        using var ready = EventWaitHandle.OpenExisting(name + ".test.cleanup.ready");
        using var finish = EventWaitHandle.OpenExisting(name + ".test.cleanup.finish");
        var owner = SessionFileTransferService.FrontierAppLifetime.StartFromEnvironment();
        Assert.NotNull(owner);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Field(string field) => owner.GetType().GetField(field, flags)!.GetValue(owner);
        Task? releaseEmitter = null;
        bool flushed;
        try
        {
            LocalOperationalLog.Info("CleanupFixture", "event=ordinary_app_startup");
            started.Set();
            await ((Task)Field("setup")!).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True((bool)Field("ready")!);
            if (scenario == "positive" && ordinal == 0)
            {
                await SessionFileTransferV4SenderTests.PrepareGenuineFrontierSubmissionAsync(Field("catalog")!, "valid", ordinal,
                    deferFinalization: true, serviceAdmission: true, productionShutdown: true);
            }
            else
            {
                releaseEmitter = await SessionFileTransferV4SenderTests.PrepareProducerRegistryAsync(owner,
                    scenario == "delayed-emitter" ? "producer-delayed" : "producer-writer", ordinal);
            }
            ready.Set();
            Assert.True(finish.WaitOne(20000));
            if (ordinal == 0 && scenario is "second-first" or "premature-signal") { await Task.Delay(500); }
            if (ordinal == 1 && scenario == "timeout") { await Task.Delay(10000); }
            if (ordinal == 0 && scenario == "app-rejected")
            { await Task.Delay(100); ((IDisposable)Field("catalog")!).Dispose(); }
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "app-" + ordinal + "-error.txt"), error.ToString());
            throw;
        }
        finally { flushed = SessionFileTransferService.FrontierAppLifetime.CompleteAndFlush(owner, TimeSpan.FromSeconds(5)); }
        if (releaseEmitter is not null) { await releaseEmitter; }
        Assert.True(flushed);
        File.WriteAllText(Path.Combine(directory, "app-" + ordinal + ".json"), JsonSerializer.Serialize(new
        { Completed = owner.HandoffCompleted, Flushed = flushed }));
        // Model exit-code observation before the original kernel process object
        // is signaled. The GUI fixture skips its ordinary wait for this child.
        if (scenario == "exit-rundown" && ordinal == 1) { await Task.Delay(1000); }
    }
}
