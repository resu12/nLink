using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierProducerRegistryTests : FileTransferOpsScriptsTestBase
{
    public static IEnumerable<object[]> NativeCases =>
        from host in new[] { "powershell", "pwsh" }
        from scenario in new[] { "producer-writer", "producer-delayed", "producer-retired", "producer-rejected",
            "producer-timeout", "producer-late-service", "producer-dispose-race", "producer-positive-delayed" }
        select new object[] { host, scenario };

    [Theory]
    [MemberData(nameof(NativeCases))]
    public async Task NativeProducerRegistry_CoversOriginalAppStreams(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-producer-registry-20260920", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAppLifetime.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-app-lifetime=" + scenario, result.Stdout);
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Registry()
    {
        var type = typeof(SessionFileTransferService).GetNestedType("FrontierProducerRegistry", BindingFlags.NonPublic);
        Assert.NotNull(type);
        return Activator.CreateInstance(type, nonPublic: true)!;
    }
    private static object? Call(object target, string method, params object?[] args)
        => target.GetType().GetMethod(method, Flags)!.Invoke(target, args);
    private static bool Current(object registry) => (bool)registry.GetType().GetProperty("IsCurrent", Flags)!.GetValue(registry)!;
    private static object Copy(object value) => typeof(object).GetMethod("MemberwiseClone", Flags)!.Invoke(value, null)!;

    [Fact]
    public void ProducerRegistry_OpenServiceCannotBecomeWriterOnly()
    {
        var registry = Registry();
        var scope = Call(registry, "RegisterService")!;
        Assert.NotNull(scope);
        Assert.False((bool)Call(registry, "SealAndWait", TimeSpan.Zero)!);
        Call(scope, "Close");
        Assert.False(Current(registry));
        Assert.False((bool)Call(registry, "SealAndWait", TimeSpan.Zero)!);
    }

    [Fact]
    public void ProducerRegistry_ClosedScopeRetainsOriginalPendingStream()
    {
        var registry = Registry();
        var scope = Call(registry, "RegisterService")!;
        var emitter = Call(scope, "CreateEmitter")!;
        Assert.NotNull(emitter);
        Call(scope, "Close");
        Assert.False((bool)Call(registry, "SealAndWait", TimeSpan.FromMilliseconds(30))!);
        Assert.False(Current(registry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProducerRegistry_LifetimeCapacityCannotRecycle(bool streams)
    {
        var registry = Registry();
        if (streams)
        {
            var scope = Call(registry, "RegisterService")!;
            for (var i = 0; i < 64; i++) { Assert.NotNull(Call(scope, "CreateEmitter")); }
            Assert.True(Current(registry));
            Assert.NotNull(Call(scope, "CreateEmitter")); // ordinary runtime remains available
        }
        else
        {
            for (var i = 0; i < 16; i++) { Call(Call(registry, "RegisterService")!, "Close"); }
            Assert.True(Current(registry));
            Assert.Null(Call(registry, "RegisterService"));
        }
        Assert.False(Current(registry));
        Assert.False((bool)Call(registry, "SealAndWait", TimeSpan.Zero)!);
    }

    [Fact]
    public void ProducerRegistry_CopiesCannotCloseOriginalOrSealItsScope()
    {
        var registry = Registry();
        var scope = Call(registry, "RegisterService")!;
        Call(Copy(scope), "Close");
        Assert.False((bool)Call(Copy(registry), "SealAndWait", TimeSpan.Zero)!);
        Assert.True(Current(registry));
        Assert.False((bool)Call(registry, "SealAndWait", TimeSpan.Zero)!);
    }

    [Fact]
    public void ProducerRegistry_SealedScopeRejectsLateService()
    {
        var registry = Registry();
        var scope = Call(registry, "RegisterService")!;
        Call(scope, "Close");
        Assert.True((bool)Call(registry, "SealAndWait", TimeSpan.Zero)!);
        Assert.True(Current(registry));
        Assert.Null(Call(registry, "RegisterService"));
        Assert.False(Current(registry));
    }

    [Fact]
    public void ProducerRegistry_ClosedScopeCannotEnrollAnotherStream()
    {
        var registry = Registry();
        var scope = Call(registry, "RegisterService")!;
        Call(scope, "Close");
        Assert.NotNull(Call(scope, "CreateEmitter"));
        Assert.False(Current(registry));
    }

    [Fact]
    public async Task ProducerRegistry_StreamsShareOneBudgetAndLateCompletionCannotRecover()
    {
        var registry = Registry();
        var scope = Call(registry, "RegisterService")!;
        var emitters = Enumerable.Range(0, 2).Select(_ => Call(scope, "CreateEmitter")!).ToArray();
        foreach (var emitter in emitters)
        {
            emitter.GetType().GetField("drainScheduled", Flags)!.SetValue(emitter, 1);
            Assert.True((bool)Call(emitter, "Start", "budget-coordinator", "budget-session", "budget-transfer",
                FileTransferDirection.Inbound, 0L, new string('a', 64))!);
            Call(emitter, "Complete", "budget-coordinator", "budget-session", "budget-transfer", FileTransferDirection.Inbound);
        }
        Call(scope, "Close");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var waiting = Task.Run(() => (bool)Call(registry, "SealAndWait", TimeSpan.FromMilliseconds(250))!);
        await Task.Delay(75);
        Release(emitters[0]);
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.InRange(clock.ElapsedMilliseconds, 200, 2500);
        Release(emitters[1]);
        foreach (var emitter in emitters)
        { Assert.True((bool)Call(emitter, "WaitForFinalSubmission", TimeSpan.FromSeconds(3))!); }
        Assert.False(Current(registry));
        Assert.False((bool)Call(registry, "SealAndWait", TimeSpan.Zero)!);

        static void Release(object emitter)
        {
            emitter.GetType().GetField("drainScheduled", Flags)!.SetValue(emitter, 0);
            Call(emitter, "ScheduleDrain");
        }
    }
}

public sealed partial class SessionFileTransferV4SenderTests
{
    internal static async Task<Task?> PrepareProducerRegistryAsync(SessionFileTransferService.FrontierAppLifetime owner,
        string scenario, int ordinal)
    {
        using var transport = new RetirementCallbackTransport();
        using var peer = new LoopbackFileTransferTransport("retirement-session");
        transport.Connect(peer);
        using var service = SessionFileTransferService.CreateForApp();
        service.AttachTransport(transport);
        var registry = BoundaryField(owner, "producers")!;
        var tasks = (IReadOnlyCollection<Task>)BoundaryField(registry, "completions")!;
        var before = tasks.Count;
        if (ordinal == 0 && scenario is "producer-rejected" or "producer-dispose-race")
        {
            if (scenario == "producer-dispose-race") { transport.BeforeCorrelationCheck = service.Dispose; }
            await Offer(service, "producer-rejected", invalidCorrelation: scenario == "producer-rejected");
            Assert.Null(service.Snapshot.Inbound);
            Assert.Equal(before + 1, tasks.Count);
            return null;
        }

        await service.TryStartSendAsync(new FileTransferSendDescriptor("producer.bin", 1024, "producer-outbound"),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[1024])), CancellationToken.None);
        var context = BoundaryField(service, "outboundTransfer")!;
        Assert.Equal(FileTransferTransferState.AwaitingAcceptance, service.Snapshot.Outbound!.State);
        var emitter = GetPrivatePropertyValue(context, "CoordinatorSnapshotEmitter")!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var held = ordinal == 0 && scenario is "producer-delayed" or "producer-retired" or "producer-timeout" or
            "producer-late-service" or "producer-positive-delayed";
        if (held)
        {
            var field = emitter.GetType().GetField("drainScheduled", flags)!;
            Assert.True(SpinWait.SpinUntil(() => (int)field.GetValue(emitter)! == 0, TimeSpan.FromSeconds(5)));
            field.SetValue(emitter, 1);
        }
        service.ResetSessionState();
        Assert.Null(service.Snapshot.Outbound);
        if (held) { Assert.False(EmitterFinished(context, TimeSpan.Zero)); }
        if (ordinal == 0 && scenario == "producer-retired")
        {
            await Offer(service, "producer-successor");
            service.ResetSessionState();
        }
        Assert.Equal(before + (ordinal == 0 && scenario == "producer-retired" ? 2 : 1), tasks.Count);
        service.Dispose();
        if (!held || scenario == "producer-timeout") { return null; }
        return Task.Run(async () =>
        {
            Assert.True(SpinWait.SpinUntil(() => (bool)BoundaryField(registry, "sealedForDrain")!, TimeSpan.FromSeconds(15)));
            await Task.Delay(250);
            Assert.False((bool)typeof(NLink.Core.Logging.LocalOperationalLog).GetField("writerCompletionAttempted", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!);
            foreach (var waitGate in new[] { BoundaryField(registry, "gate")!, GetServiceGate(service) })
            {
                Assert.True(Monitor.TryEnter(waitGate, 1000), "Producer readiness held a registry/service gate");
                Monitor.Exit(waitGate);
            }
            if (scenario == "producer-late-service")
            {
                using var late = SessionFileTransferService.CreateForApp();
                Assert.Same(owner, BoundaryField(late, "frontierAppLifetime"));
                Assert.Null(BoundaryField(late, "frontierProducerScope"));
                Assert.False((bool)registry.GetType().GetProperty("IsCurrent", flags)!.GetValue(registry)!);
            }
            emitter.GetType().GetField("drainScheduled", flags)!.SetValue(emitter, 0);
            emitter.GetType().GetMethod("ScheduleDrain", flags)!.Invoke(emitter, null);
        });
    }
}
