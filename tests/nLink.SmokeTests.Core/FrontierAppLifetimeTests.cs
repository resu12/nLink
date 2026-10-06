using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierAppLifetimeTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> Cases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "valid", "both-positive", "writer-only", "disabled", "setup-rejected", "early-exit", "early-start-exit", "delayed-channel", "delayed-adoption",
            "cancel-adoption", "incomplete", "catalog-cancel", "scope-change", "early-drain", "copies-repeat" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeAppLifetime_OwnsSetupDrainAndHandoff(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-app-lifetime-20260919", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAppLifetime.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-app-lifetime=" + scenario, result.Stdout);
    }

    // Runs only in a fresh app-role process. The real owner performs attachment,
    // preparation, final drain, submission, wait and acknowledgment. Only the
    // transfer observations use the existing deterministic producer fixture.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task RunAppAsync(string directory, string scenario, int ordinal)
    {
        var name = Environment.GetEnvironmentVariable("NLINK_APP_FRONTIER_REQUEST")!;
        using var started = EventWaitHandle.OpenExisting(name + ".test.lifetime.started");
        using var ready = EventWaitHandle.OpenExisting(name + ".test.lifetime.ready");
        using var capture = EventWaitHandle.OpenExisting(name + ".test.lifetime.capture");
        using var finish = EventWaitHandle.OpenExisting(name + ".test.lifetime.finish");
        using var finished = EventWaitHandle.OpenExisting(name + ".test.lifetime.finished");
        if (scenario == "disabled")
        {
            Environment.SetEnvironmentVariable("NLINK_APP_FRONTIER_START_V1", null);
            Assert.Null(SessionFileTransferService.FrontierAppLifetime.StartFromEnvironment());
            LocalOperationalLog.Info("AppLifetimeFixture", "event=ordinary_disabled_startup");
            started.Set();
            Assert.True(finish.WaitOne(15000));
            var ordinaryFlushed = SessionFileTransferService.FrontierAppLifetime.CompleteAndFlush(null, TimeSpan.FromSeconds(5));
            Assert.True(ordinaryFlushed);
            File.WriteAllText(Path.Combine(directory, "app-" + ordinal + ".json"), JsonSerializer.Serialize(new
            { Completed = false, Flushed = ordinaryFlushed }));
            finished.Set();
            return;
        }
        Environment.SetEnvironmentVariable("NLINK_APP_FRONTIER_START_V1", "true");
        Assert.Null(SessionFileTransferService.FrontierAppLifetime.StartFromEnvironment());
        Environment.SetEnvironmentVariable("NLINK_APP_FRONTIER_START_V1", "1");
        if (scenario == "setup-rejected") { Environment.SetEnvironmentVariable("NLINK_APP_FRONTIER_REQUEST_ID", Guid.Empty.ToString("D")); }
        var clock = Stopwatch.StartNew();
        var owner = SessionFileTransferService.FrontierAppLifetime.StartFromEnvironment();
        Assert.NotNull(owner);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), "App startup blocked on the second app");
        Assert.Null(SessionFileTransferService.FrontierAppLifetime.StartFromEnvironment());
        var setup = (Task)Field(owner, "setup")!;
        var productionShutdown = scenario.StartsWith("service-shutdown-", StringComparison.Ordinal);
        object? retainedEmitter = null;
        Task? releaseEmitter = null;
        bool flushed = false;
        try
        {
            LocalOperationalLog.Info("AppLifetimeFixture", "event=ordinary_app_startup");
            started.Set();
            if (scenario == "service-unready")
            {
                Assert.True(capture.WaitOne(15000), "GUI launch reports did not settle before early admission");
                using var service = SessionFileTransferService.CreateForApp();
                Assert.Same(owner, Field(service, "frontierAppLifetime"));
                Assert.Null(owner.TryAdmitService(Field(service, "gate")!));
                await setup.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False((bool)Field(owner, "ready")!);
                ready.Set();
            }
            if (scenario == "setup-rejected")
            {
                await setup.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False((bool)Field(owner, "ready")!);
                Assert.Null(Field(owner, "catalog"));
            }
            if (scenario is not ("early-exit" or "early-start-exit" or "setup-rejected" or "service-unready"))
            {
                await setup.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True((bool)Field(owner, "ready")!);
                var catalog = Field(owner, "catalog")!;
                if (scenario.StartsWith("service-", StringComparison.Ordinal))
                {
                    using var ordinary = new SessionFileTransferService();
                    using var appService = SessionFileTransferService.CreateForApp();
                    Assert.Null(Field(ordinary, "frontierAppLifetime"));
                    Assert.Same(owner, Field(appService, "frontierAppLifetime"));
                    var copy = (SessionFileTransferService.FrontierAppLifetime)typeof(object).GetMethod("MemberwiseClone", Flags)!.Invoke(owner, null)!;
                    Assert.Null(copy.TryAdmitService(Field(appService, "gate")!));
                    Assert.Same(owner, SessionFileTransferService.FrontierAppLifetime.Current);
                }
                if (scenario is "both-positive" or "service-both-positive" || scenario.StartsWith("producer-", StringComparison.Ordinal))
                {
                    // The reused producer helper asserts one stream start in
                    // its shared-log tail. Serialize only synthetic capture,
                    // not setup, shutdown, native submission or adoption.
                    Assert.True(capture.WaitOne(15000));
                }
                if (scenario.StartsWith("producer-", StringComparison.Ordinal))
                {
                    if (scenario == "producer-positive-delayed" && ordinal == 0)
                    {
                        await SessionFileTransferV4SenderTests.PrepareGenuineFrontierSubmissionAsync(catalog, "valid", ordinal,
                            deferFinalization: true, serviceAdmission: true, productionShutdown: true);
                    }
                    releaseEmitter = await SessionFileTransferV4SenderTests.PrepareProducerRegistryAsync(owner, scenario, ordinal);
                }
                else if (scenario is "service-context-race" or "service-data-race" && ordinal == 0)
                {
                    await SessionFileTransferV4SenderTests.ExerciseServiceAdmissionBindingRaceAsync(owner, catalog,
                        reset: scenario == "service-context-race");
                }
                else if (scenario == "service-drained" && ordinal == 0)
                {
                    Assert.True(LocalOperationalLog.CompleteAndFlush(TimeSpan.FromSeconds(5)));
                    using var appService = SessionFileTransferService.CreateForApp();
                    Assert.Null(owner.TryAdmitService(Field(appService, "gate")!));
                    Assert.Equal(0, catalog.GetType().GetProperty("AdmissionCount")!.GetValue(catalog));
                    Assert.Equal("service_writer_unavailable", catalog.GetType().GetProperty("FailureReason")!.GetValue(catalog));
                }
                else if (scenario == "incomplete" && ordinal == 0)
                {
                    object?[] args = [null];
                    Assert.NotNull(catalog.GetType().GetMethod("TryAdmit")!.Invoke(catalog, args));
                }
                else if (scenario != "writer-only")
                {
                    await SessionFileTransferV4SenderTests.PrepareGenuineFrontierSubmissionAsync(catalog,
                        scenario is "both-positive" or "service-both-positive" ? "both_positive" : "valid", ordinal,
                        deferFinalization: true, serviceAdmission: scenario.StartsWith("service-", StringComparison.Ordinal),
                        serviceVariant: scenario switch { "service-pause" => "pause", "service-source" => "source",
                            "service-reset" => "reset", _ => "before_recheck" }, productionShutdown: productionShutdown,
                        retainEmitter: productionShutdown && scenario != "service-shutdown-natural" ? emitter => retainedEmitter = emitter : null);
                    if (retainedEmitter is not null)
                    {
                        Assert.False((bool)retainedEmitter.GetType().GetMethod("WaitForFinalSubmission", Flags)!
                            .Invoke(retainedEmitter, [TimeSpan.Zero])!);
                        if (scenario == "service-shutdown-delayed")
                        {
                            releaseEmitter = Task.Run(async () =>
                            {
                                Assert.True(SpinWait.SpinUntil(() => (int)Field(owner, "finishAttempted")! != 0, TimeSpan.FromSeconds(15)));
                                await Task.Delay(250);
                                Assert.False((bool)Field(catalog, "markersAttempted")!);
                                var slots = (System.Collections.IList)Field(catalog, "slots")!;
                                var observation = slots[0]!.GetType().GetField("Observation")!.GetValue(slots[0])!;
                                foreach (var waitGate in new[] { Field(catalog, "gate")!, Field(observation, "payloadGate")!, Field(observation, "serviceGate")! })
                                {
                                    Assert.True(Monitor.TryEnter(waitGate, 1000), "Readiness wait held an ownership/capture gate");
                                    Monitor.Exit(waitGate);
                                }
                                // Release a fixture scheduling hold; only the real worker drains.
                                retainedEmitter.GetType().GetField("drainScheduled", Flags)!.SetValue(retainedEmitter, 0);
                                retainedEmitter.GetType().GetMethod("ScheduleDrain", Flags)!.Invoke(retainedEmitter, null);
                            });
                        }
                    }
                }
                if (ordinal == 0)
                {
                    if (scenario == "catalog-cancel") { ((IDisposable)catalog).Dispose(); }
                    if (scenario == "scope-change") { Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_SCENARIO_RUN_ID", "changed"); }
                    if (scenario == "early-drain") { Assert.True(LocalOperationalLog.CompleteAndFlush(TimeSpan.FromSeconds(5))); }
                }
                if (scenario == "copies-repeat")
                {
                    var copy = (SessionFileTransferService.FrontierAppLifetime)typeof(object).GetMethod("MemberwiseClone", Flags)!.Invoke(owner, null)!;
                    Assert.False(SessionFileTransferService.FrontierAppLifetime.CompleteAndFlush(copy, TimeSpan.FromSeconds(5)));
                    Assert.Null(Field(Field(catalog, "writer")!, "Drain"));
                }
                ready.Set();
            }
            Assert.True(finish.WaitOne(15000), "Fixture exit boundary not reached");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "app-" + ordinal + "-error.txt"), error.ToString());
            throw;
        }
        finally
        {
            clock.Restart();
            flushed = SessionFileTransferService.FrontierAppLifetime.CompleteAndFlush(owner, TimeSpan.FromSeconds(5));
        }
        if (scenario is "early-exit" or "early-start-exit" or "service-unready")
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "Early shutdown waited for the full setup deadline");
            Assert.True(setup.IsCompleted);
        }
        Assert.True(flushed, "Optional failure prevented ordinary log drain");
        if (releaseEmitter is not null) { await releaseEmitter.WaitAsync(TimeSpan.FromSeconds(5)); }
        if (scenario == "producer-timeout" && ordinal == 0)
        {
            Assert.False(owner.HandoffCompleted);
            Assert.InRange(clock.ElapsedMilliseconds, 4500, 12000);
        }
        if (scenario == "service-shutdown-timeout" && ordinal == 0)
        {
            Assert.False(owner.HandoffCompleted);
            Assert.InRange(clock.ElapsedMilliseconds, 4500, 12000);
            Assert.Equal("completion_write_not_ready", Field(owner, "catalog")!.GetType().GetProperty("FailureReason")!.GetValue(Field(owner, "catalog")));
        }
        Assert.Null(SessionFileTransferService.FrontierAppLifetime.Current);
        var sequence = LocalOperationalLog.WriterSequenceForTests;
        Assert.False(SessionFileTransferService.FrontierAppLifetime.CompleteAndFlush(owner, TimeSpan.FromSeconds(5)));
        Assert.Equal(sequence, LocalOperationalLog.WriterSequenceForTests);
        File.WriteAllText(Path.Combine(directory, "app-" + ordinal + ".json"), JsonSerializer.Serialize(new
        { Completed = owner.HandoffCompleted, Flushed = flushed, Elapsed = clock.ElapsedMilliseconds }));
        finished.Set();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppLifetime_DrainPreparationClosesOriginalAdmission(bool incomplete)
    {
        var type = typeof(SessionFileTransferService).GetNestedType("FrontierPayloadCatalog", BindingFlags.NonPublic)!;
        using var catalog = (IDisposable)Activator.CreateInstance(type, nonPublic: true)!;
        object?[] args = [null];
        if (incomplete) { Assert.NotNull(type.GetMethod("TryAdmit")!.Invoke(catalog, args)); }
        var copy = typeof(object).GetMethod("MemberwiseClone", Flags)!.Invoke(catalog, null)!;
        Assert.False((bool)type.GetMethod("PrepareForDrain")!.Invoke(copy, null)!);
        ((IDisposable)copy).Dispose();
        Assert.Equal(!incomplete, (bool)type.GetMethod("PrepareForDrain")!.Invoke(catalog, null)!);
        Assert.Null(type.GetMethod("TryAdmit")!.Invoke(catalog, args));
        Assert.False((bool)type.GetMethod("PrepareForDrain")!.Invoke(catalog, null)!);
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(object value, string name) => value.GetType().GetField(name, Flags)!.GetValue(value);
}
