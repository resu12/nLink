using System.Diagnostics;
using System.Reflection;
using NLink.Tools;

namespace NLink.SmokeTests;

public sealed partial class FrontierFileAdoptionTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("swapped")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("substitute")]
    [InlineData("case")]
    [InlineData("sequence")]
    [InlineData("duplicate")]
    [InlineData("foreign-reader")]
    [InlineData("copy")]
    [InlineData("partial")]
    [InlineData("after-finish")]
    [InlineData("repeat")]
    [InlineData("cancel")]
    [InlineData("budget")]
    public void Adoption_WriterMatchRequiresExactOriginalPairAndActiveProcessing(string fault)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var directory = new TestDirectory(); File.WriteAllBytes(directory.Log, new byte[100003]);
        var gui = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid(); var current = true;
        using var a = new Child(directory.Path, gui, first, 1, 0);
        using var b = new Child(directory.Path, gui, second, 0, 1);
        using var owner = new LocalFrontierFileAdoption(gui, first, second, () => current);
        var firstBytes = ExactRawRanges(a.Bytes, directory.Log); var secondBytes = ExactRawRanges(b.Bytes, directory.Log);
        Assert.True(owner.TryAdopt(0, a.Handle, firstBytes)); Assert.True(owner.TryAdopt(1, b.Handle, secondBytes));
        a.Exit(); b.Exit(); Assert.True(owner.TrySealSource(() => true));
        var reader = new object(); Assert.True(owner.TryBeginSealedRead(reader)); Assert.True(owner.BeginProcessingWork(reader));
        var lifetime = owner.ProcessingLifetime(reader); var buffer = new byte[32768];
        do { Assert.True(owner.TryReadSealedSource(reader, buffer, 0, buffer.Length, out _)); if (fault == "partial") break; }
        while (!owner.SealedReadEnded(reader));
        var manifests = new[] { LocalFrontierFileManifest.Decode(firstBytes)!, LocalFrontierFileManifest.Decode(secondBytes)! };
        var writers = manifests.Select(m => m.Writer).ToArray(); var sequences = manifests.Select(m => m.Drain.Last).ToArray();
        if (fault == "swapped") { Array.Reverse(writers); Array.Reverse(sequences); }
        if (fault == "missing") { writers = writers.Take(1).ToArray(); sequences = sequences.Take(1).ToArray(); }
        if (fault == "extra") { writers = [.. writers, "extra"]; sequences = [.. sequences, 1]; }
        if (fault == "substitute") { writers[0] = "substituted"; }
        if (fault == "case") { writers[0] = writers[0].ToUpperInvariant(); Assert.NotEqual(writers[0], manifests[0].Writer); }
        if (fault == "sequence") { sequences[0]++; }
        if (fault == "duplicate") { writers[1] = writers[0]; }
        if (fault == "after-finish") { Assert.True(owner.FinishProcessingWork(reader)); }
        if (fault == "cancel") { current = false; }
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var work = (Stopwatch)typeof(LocalFrontierFileAdoption).GetField("work", flags)!.GetValue(owner)!;
        if (fault == "budget") { typeof(Stopwatch).GetField("_elapsed", flags)!.SetValue(work, Stopwatch.Frequency * 11); }
        if (fault == "foreign-reader") { Assert.False(owner.TryMatchWriters(new object(), writers, sequences)); Assert.True(owner.IsSourceVerified); }
        if (fault == "copy")
        {
            var copy = (LocalFrontierFileAdoption)typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(owner, null)!;
            Assert.False(copy.TryMatchWriters(reader, writers, sequences)); copy.Dispose(); Assert.True(owner.IsSourceVerified);
        }
        var accepted = fault is "valid" or "swapped" or "foreign-reader" or "copy" or "repeat";
        Assert.Equal(accepted, owner.TryMatchWriters(reader, writers, sequences));
        if (accepted && fault != "repeat") { Assert.True(work.IsRunning); Assert.True(owner.FinishProcessingWork(reader)); }
        if (fault == "repeat") { Assert.False(owner.TryMatchWriters(reader, writers, sequences)); }
        if (!accepted || fault == "repeat") { Assert.False(owner.IsSourceVerified); Assert.True(lifetime.IsCancellationRequested); }
        owner.Dispose(); Assert.False(work.IsRunning); File.AppendAllText(directory.Log, "released");
    }
}
