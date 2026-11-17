using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierServiceAdmissionTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell", "service-valid")]
    [InlineData("pwsh", "service-valid")]
    [InlineData("powershell", "service-both-positive")]
    [InlineData("pwsh", "service-both-positive")]
    [InlineData("powershell", "service-pause")]
    [InlineData("pwsh", "service-pause")]
    [InlineData("powershell", "service-source")]
    [InlineData("pwsh", "service-source")]
    [InlineData("powershell", "service-reset")]
    [InlineData("pwsh", "service-reset")]
    [InlineData("powershell", "service-unready")]
    [InlineData("pwsh", "service-unready")]
    [InlineData("powershell", "service-drained")]
    [InlineData("pwsh", "service-drained")]
    [InlineData("powershell", "service-context-race")]
    [InlineData("pwsh", "service-context-race")]
    [InlineData("powershell", "service-data-race")]
    [InlineData("pwsh", "service-data-race")]
    public async Task NativeServiceAdmission_UsesOriginalAppCatalog(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "frontier-service-admission-20260920", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierAppLifetime.ps1"));
        var result = await RunPowerShellScriptTextAsync(script, [root, directory, scenario], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("frontier-app-lifetime=" + scenario, result.Stdout);
    }

    [Fact]
    public async Task ServiceAdmission_DrainClosureUsesCaptureGateAndDoesNotReopen()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var type = typeof(SessionFileTransferService).GetNestedType("FrontierBoundaryObservation", BindingFlags.NonPublic)!;
        var gate = new object();
        var observation = Activator.CreateInstance(type, [null, gate])!;
        var close = type.GetMethod("CloseForDrain")!;
        var closed = type.GetProperty("IsClosed")!;
        using var started = new ManualResetEvent(false);
        Task pending;
        lock (gate)
        {
            pending = Task.Run(() => { started.Set(); close.Invoke(observation, null); });
            Assert.True(started.WaitOne(5000));
            Assert.False(pending.Wait(100), "Drain closure bypassed the actual capture gate");
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((bool)closed.GetValue(observation)!);
        Assert.Equal("EvidenceIncomplete", type.GetProperty("Status")!.GetValue(observation));
        close.Invoke(observation, null);
        Assert.Equal("EvidenceIncomplete", type.GetProperty("Status")!.GetValue(observation));
    }

    [Fact]
    public void ServiceAdmission_RequiresActualWriterEvenForOriginalCatalog()
    {
        var type = typeof(SessionFileTransferService).GetNestedType("FrontierPayloadCatalog", BindingFlags.NonPublic)!;
        using var catalog = (IDisposable)Activator.CreateInstance(type, nonPublic: true)!;
        object?[] args = [new object(), null];
        Assert.Null(type.GetMethod("TryAdmitService")!.Invoke(catalog, args));
        Assert.Equal("service_writer_unavailable", args[1]);
        Assert.Equal(0, type.GetProperty("AdmissionCount")!.GetValue(catalog));
    }
}

public sealed partial class SessionFileTransferV4SenderTests
{
    private sealed class AdmissionRaceFinished : Exception;

    internal static async Task ExerciseServiceAdmissionBindingRaceAsync(
        SessionFileTransferService.FrontierAppLifetime owner, object catalog, bool reset)
    {
        object? emitter = null;
        try
        {
            await CreateCheckpointRepairContinuityServiceEvidenceFixtureAsync(true, null, (_, _, _) => Task.CompletedTask,
                appOwnedService: true, beforeServiceRepairWrite: (service, context) =>
                {
                    emitter = GetPrivatePropertyValue(context, "CoordinatorSnapshotEmitter");
                    var ownerGate = BoundaryField(owner, "admissionGate")!;
                    var serviceGate = GetServiceGate(service);
                    var contract = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract");
                    var chunks = new List<(int ChunkIndex, byte[] ChunkBytes)>
                    { (GetPrivateProperty<int>(context, "NextChunkIndex"), [1]) };
                    Task admission;
                    lock (ownerGate)
                    {
                        admission = Task.Run(() => typeof(SessionFileTransferService)
                            .GetMethod("TryAdmitFrontierRepairObservation", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(service, [context, contract, chunks]));
                        Assert.True(SpinWait.SpinUntil(() =>
                        {
                            if (!Monitor.TryEnter(serviceGate, 50)) { return false; }
                            try { return GetPrivateProperty<bool>(context, "FrontierAdmissionAttempted"); }
                            finally { Monitor.Exit(serviceGate); }
                        }, TimeSpan.FromSeconds(5)));
                        Assert.True(Monitor.TryEnter(serviceGate, 1000), "Native admission held the service gate");
                        try
                        {
                            if (reset) { service.ResetSessionState(); }
                            else { SetPrivateProperty(context, "DataSessionGeneration", GetPrivateProperty<long>(context, "DataSessionGeneration") + 1); }
                        }
                        finally { Monitor.Exit(serviceGate); }
                    }
                    Assert.True(admission.Wait(5000));
                    Assert.Null(GetPrivatePropertyValue(context, "FrontierBoundaryObservation"));
                    typeof(SessionFileTransferService)
                        .GetMethod("TryAdmitFrontierRepairObservation", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(service, [context, contract, chunks]);
                    Assert.Equal(1, catalog.GetType().GetProperty("AdmissionCount")!.GetValue(catalog));
                    var slots = Assert.IsAssignableFrom<System.Collections.IList>(BoundaryField(catalog, "slots"));
                    var observation = slots[0]!.GetType().GetField("Observation")!.GetValue(slots[0])!;
                    Assert.True(GetPrivateProperty<bool>(observation, "IsClosed"));
                    Assert.Equal("EvidenceIncomplete", GetPrivateProperty<string>(observation, "Status"));
                    throw new AdmissionRaceFinished();
                });
            Assert.Fail("Admission race fixture did not reach its original repair boundary");
        }
        catch (AdmissionRaceFinished)
        {
            // The fixture intentionally ends before physical I/O. Let its real
            // service-disposal emitter finish before the app's ordinary drain.
            Assert.NotNull(emitter);
            Assert.True(SpinWait.SpinUntil(() => (int)BoundaryField(emitter, "drainScheduled")! == 0, TimeSpan.FromSeconds(5)));
        }
    }
}
