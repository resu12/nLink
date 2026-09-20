using System.Diagnostics;
using System.Reflection;
using NLink.Tools;

namespace NLink.SmokeTests;

public sealed partial class FrontierFileAdoptionTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("repeat")]
    [InlineData("foreign")]
    [InlineData("copy")]
    [InlineData("revoked")]
    [InlineData("budget")]
    [InlineData("closed")]
    [InlineData("position")]
    public void Adoption_SealedReadUsesOriginalHandleOnceWithBoundedReads(string fault)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var directory = new TestDirectory();
        var expected = Enumerable.Range(0, 100003).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(directory.Log, expected);
        var gui = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid(); var current = true;
        using var a = new Child(directory.Path, gui, first, 1, 0);
        using var b = new Child(directory.Path, gui, second, 0, 1);
        using var owner = new LocalFrontierFileAdoption(gui, first, second, () => current);
        Assert.True(owner.TryAdopt(0, a.Handle, ExactRawRanges(a.Bytes, directory.Log)));
        Assert.True(owner.TryAdopt(1, b.Handle, ExactRawRanges(b.Bytes, directory.Log)));
        a.Exit(); b.Exit();
        File.Move(directory.Log, directory.Log + ".original"); File.WriteAllText(directory.Log, "replacement");
        Assert.True(owner.TrySealSource(() => true));
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var stream = (FileStream)typeof(LocalFrontierFileAdoption).GetField("sealedSource", flags)!.GetValue(owner)!;
        var handle = stream.SafeFileHandle;
        var token = new object(); var buffer = new byte[70000];
        Assert.True(owner.TryBeginSealedRead(token));
        Assert.Same(handle, stream.SafeFileHandle);
        Assert.True(owner.TryReadSealedSource(token, buffer, 0, 0, out var empty)); Assert.Equal(0, empty);
        Assert.False(owner.SealedReadEnded(token));
        if (fault == "foreign")
        {
            Assert.False(owner.TryReadSealedSource(new object(), buffer, 0, buffer.Length, out _));
            Assert.True(owner.IsSourceVerified);
        }
        if (fault == "copy")
        {
            var copy = (LocalFrontierFileAdoption)typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(owner, null)!;
            Assert.False(copy.TryBeginSealedRead(token));
            Assert.False(copy.TryReadSealedSource(token, buffer, 0, buffer.Length, out _)); copy.Dispose();
            Assert.True(owner.IsSourceVerified);
        }
        if (fault == "repeat") { Assert.False(owner.TryBeginSealedRead(token)); }
        if (fault == "revoked") { current = false; }
        if (fault == "budget")
        {
            var work = (Stopwatch)typeof(LocalFrontierFileAdoption).GetField("work", flags)!.GetValue(owner)!;
            typeof(Stopwatch).GetField("_elapsed", flags)!.SetValue(work, Stopwatch.Frequency * 6);
        }
        if (fault == "closed") { handle.Dispose(); }
        if (fault == "position") { stream.Position = 1; }
        if (fault is "valid" or "foreign" or "copy")
        {
            using var bytes = new MemoryStream();
            while (true)
            {
                Assert.True(owner.TryReadSealedSource(token, buffer, 0, buffer.Length, out var read));
                Assert.InRange(read, 0, 32768);
                if (read == 0) { break; }
                bytes.Write(buffer, 0, read);
            }
            Assert.Equal(expected, bytes.ToArray()); Assert.True(owner.SealedReadEnded(token));
            Assert.Same(handle, stream.SafeFileHandle);
        }
        else
        {
            Assert.False(owner.TryReadSealedSource(token, buffer, 0, buffer.Length, out var read)); Assert.Equal(0, read);
            current = true;
            Assert.False(owner.IsSourceVerified); Assert.False(owner.SealedReadEnded(token));
        }
        owner.Dispose(); Assert.True(handle.IsClosed);
        Assert.Empty(Directory.GetFiles(directory.Path, "frontier-payload-*"));
        File.AppendAllText(directory.Log + ".original", "released");
        Assert.Equal("replacement", File.ReadAllText(directory.Log));
    }
}
