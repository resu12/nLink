using System.Diagnostics;
using System.Reflection;
using NLink.Tools;

namespace NLink.SmokeTests;

public sealed partial class FrontierFileAdoptionTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("partial")]
    [InlineData("repeat-begin")]
    [InlineData("repeat-finish")]
    [InlineData("cpu-budget")]
    [InlineData("copy")]
    [InlineData("callback-throws")]
    public void Adoption_ProcessingWorkIncludesCpuAndRequiresOriginalCompleteRead(string fault)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var directory = new TestDirectory(); File.WriteAllBytes(directory.Log, new byte[100003]);
        var gui = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        using var a = new Child(directory.Path, gui, first, 1, 0);
        using var b = new Child(directory.Path, gui, second, 0, 1);
        using var owner = new LocalFrontierFileAdoption(gui, first, second, () => true);
        Assert.True(owner.TryAdopt(0, a.Handle, ExactRawRanges(a.Bytes, directory.Log)));
        Assert.True(owner.TryAdopt(1, b.Handle, ExactRawRanges(b.Bytes, directory.Log)));
        a.Exit(); b.Exit(); Assert.True(owner.TrySealSource(() => true));
        var token = new object(); Assert.True(owner.TryBeginSealedRead(token));
        Assert.True(owner.ProcessingLifetime(token).IsCancellationRequested);
        Assert.True(owner.FinalizationLifetime(token).IsCancellationRequested);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var work = (Stopwatch)typeof(LocalFrontierFileAdoption).GetField("work", flags)!.GetValue(owner)!;
        var alreadySpent = work.ElapsedTicks;
        Assert.True(owner.BeginProcessingWork(token)); Assert.True(work.IsRunning); Assert.True(work.ElapsedTicks >= alreadySpent);
        var lifetime = owner.ProcessingLifetime(token); Assert.False(lifetime.IsCancellationRequested);
        Assert.True(owner.FinalizationLifetime(token).IsCancellationRequested);
        Assert.True(owner.ProcessingLifetime(new object()).IsCancellationRequested);
        using var callback = lifetime.Register(() => { if (fault == "callback-throws") throw new InvalidOperationException("cleanup probe"); });
        if (fault == "copy")
        {
            var copy = (LocalFrontierFileAdoption)typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(owner, null)!;
            Assert.False(copy.BeginProcessingWork(token)); Assert.False(copy.FinishProcessingWork(token)); copy.Dispose();
            Assert.True(copy.ProcessingLifetime(token).IsCancellationRequested); Assert.False(lifetime.IsCancellationRequested);
            Assert.True(copy.FinalizationLifetime(token).IsCancellationRequested);
            Assert.True(owner.IsRetained); Assert.True(work.IsRunning);
        }
        if (fault == "repeat-begin")
        {
            Assert.False(owner.BeginProcessingWork(token)); Assert.False(work.IsRunning); Assert.False(owner.IsRetained); Assert.True(lifetime.IsCancellationRequested); return;
        }
        if (fault == "cpu-budget")
        {
            // No read occurs between these calls: CPU/control time is still charged.
            typeof(Stopwatch).GetField("_elapsed", flags)!.SetValue(work, Stopwatch.Frequency * 6);
            Assert.False(owner.FinishProcessingWork(token)); Assert.False(work.IsRunning); Assert.False(owner.IsRetained); Assert.True(lifetime.IsCancellationRequested); return;
        }
        var buffer = new byte[32768];
        do
        {
            Assert.True(owner.TryReadSealedSource(token, buffer, 0, buffer.Length, out _));
            Assert.True(work.IsRunning);
            if (fault == "partial") break;
        } while (!owner.SealedReadEnded(token));
        Assert.Equal(fault != "partial", owner.FinishProcessingWork(token)); Assert.False(work.IsRunning);
        Assert.True(owner.FinalizationLifetime(new object()).IsCancellationRequested);
        if (fault != "partial") Assert.Equal(lifetime, owner.FinalizationLifetime(token));
        else Assert.True(owner.FinalizationLifetime(token).IsCancellationRequested);
        if (fault == "repeat-finish") { Assert.False(owner.FinishProcessingWork(token)); Assert.False(owner.IsRetained); }
        if (fault == "partial") { Assert.False(owner.IsRetained); }
        owner.Dispose(); Assert.True(lifetime.IsCancellationRequested);
        Assert.True(owner.FinalizationLifetime(token).IsCancellationRequested);
        File.AppendAllText(directory.Log, "released");
    }
}
