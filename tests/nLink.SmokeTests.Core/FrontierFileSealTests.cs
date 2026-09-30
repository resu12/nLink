using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using NLink.Tools;
using Manifest = NLink.Tools.LocalFrontierFileManifest;

namespace NLink.SmokeTests;

public sealed partial class FrontierFileAdoptionTests
{
    // Synthetic exact raw ranges exercise the transport primitive only. Genuine
    // writer ownership and original exit/EOF gates are tested by the GUI fixture.
    private static byte[] ExactRawRanges(byte[] encoded, string source)
    {
        var manifest = Manifest.Decode(encoded)!;
        var bytes = File.ReadAllBytes(source);
        Manifest.Range Exact(Manifest.Range range)
        {
            var offset = 32760 + range.Offset * 64;
            const int length = 40;
            return new(range.Generation, range.First, range.Last, range.AttemptTicks, range.BatchBytes,
                offset, length, range.BatchHash, SHA256.HashData(bytes.AsSpan(checked((int)offset), length)));
        }
        return new Manifest(manifest.GuiRequest, manifest.AppRequest, manifest.AppId, manifest.AppCreation,
            manifest.Writer, manifest.Source, Exact(manifest.Drain), manifest.Payloads.Select(p =>
                new Manifest.Payload(p.Observation, p.File, p.Hash, Exact(p.Completion), Exact(p.Marker))).ToArray()).Encode();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    public void Adoption_SealVerifiesOneWholeTraversalAndAllRangesThenExcludesWritesAndDeletion(int count)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var directory = new TestDirectory(); File.WriteAllBytes(directory.Log, new byte[100003]);
        var gui = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        using var a = new Child(directory.Path, gui, first, count, 0);
        using var b = new Child(directory.Path, gui, second, count, 1);
        using var owner = new LocalFrontierFileAdoption(gui, first, second, () => true);
        Assert.True(owner.TryAdopt(0, a.Handle, ExactRawRanges(a.Bytes, directory.Log)));
        Assert.True(owner.TryAdopt(1, b.Handle, ExactRawRanges(b.Bytes, directory.Log)));
        a.Exit(); b.Exit();
        using (var writer = new FileStream(directory.Log, FileMode.Append, FileAccess.Write, FileShare.Read)) { writer.WriteByte(42); }
        File.Move(directory.Log, directory.Log + ".original"); File.WriteAllText(directory.Log, "unrelated replacement");
        var original = directory.Log + ".original";
        var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))).ToLowerInvariant();
        Assert.True(owner.TrySealSource(() => true));
        Assert.True(owner.IsSourceVerified); Assert.Equal(100004, owner.SealedLength);
        Assert.Equal(expectedHash, owner.SealedHash); Assert.Equal(2 * (2 * count + 1), owner.SealedRangeCount);
        Assert.Throws<IOException>(() => { using var write = new FileStream(original, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
        Assert.Throws<IOException>(() => File.Move(original, original + ".moved"));
        var copy = (LocalFrontierFileAdoption)typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, null)!;
        Assert.False(copy.TrySealSource(() => true)); Assert.False(copy.IsSourceVerified); copy.Dispose();
        Assert.True(owner.IsSourceVerified);
        var sealedStream = (FileStream)typeof(LocalFrontierFileAdoption).GetField("sealedSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
        Assert.Equal(owner.SealedLength, sealedStream.Position); // No per-range rescans.
        owner.Dispose(); Assert.False(owner.IsSourceVerified); Assert.True(sealedStream.SafeFileHandle.IsClosed);
        Assert.Empty(Directory.GetFiles(directory.Path, "frontier-payload-*"));
        File.AppendAllText(original, "released"); File.Move(original, original + ".moved");
        Assert.Equal("unrelated replacement", File.ReadAllText(directory.Log));
    }

    [Theory]
    [InlineData("completion")]
    [InlineData("marker")]
    [InlineData("drain")]
    [InlineData("writer-open")]
    [InlineData("missing-second")]
    [InlineData("closure-false")]
    [InlineData("closure-throws")]
    [InlineData("cancel-during")]
    [InlineData("budget-before")]
    [InlineData("budget-during")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("repeat")]
    [InlineData("revoke-after")]
    [InlineData("closure-revoked-after")]
    [InlineData("handle-closed")]
    public void Adoption_SealFailureRevokesEveryFileAndCannotBeRetried(string fault)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var directory = new TestDirectory(); File.WriteAllBytes(directory.Log, new byte[100003]);
        var gui = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid(); var current = true;
        using var a = new Child(directory.Path, gui, first, 1, 0);
        using var b = new Child(directory.Path, gui, second, 0, 1);
        using var owner = new LocalFrontierFileAdoption(gui, first, second, () => current);
        var firstBytes = ExactRawRanges(a.Bytes, directory.Log);
        Assert.True(owner.TryAdopt(0, a.Handle, firstBytes));
        if (fault != "missing-second") { Assert.True(owner.TryAdopt(1, b.Handle, ExactRawRanges(b.Bytes, directory.Log))); }
        a.Exit(); b.Exit();
        var work = (Stopwatch)typeof(LocalFrontierFileAdoption).GetField("work", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
        void Expire() => typeof(Stopwatch).GetField("_elapsed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(work, Stopwatch.Frequency * 11);
        if (fault == "budget-before") { Expire(); }
        if (fault is "completion" or "marker" or "drain" or "truncated" or "oversized")
        {
            using var write = new FileStream(directory.Log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var manifest = Manifest.Decode(firstBytes)!;
            var range = fault == "completion" ? manifest.Payloads[0].Completion : fault == "marker" ? manifest.Payloads[0].Marker : manifest.Drain;
            if (fault == "truncated") { write.SetLength(1); }
            else if (fault == "oversized") { write.SetLength(Manifest.MaximumSourceBytes + 1); }
            else { write.Position = range.Offset; write.WriteByte(1); }
        }
        using var heldWriter = fault == "writer-open" ? new FileStream(directory.Log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete) : null;
        var calls = 0; var closureCurrent = true;
        bool Closed()
        {
            Assert.True(work.IsRunning);
            calls++;
            if (fault == "closure-throws") { throw new IOException("fixture closure failure"); }
            if (fault == "budget-during" && calls == 3) { Expire(); }
            return closureCurrent && fault != "closure-false" && (fault != "cancel-during" || calls < 3);
        }
        if (fault is "repeat" or "revoke-after" or "closure-revoked-after" or "handle-closed")
        {
            Assert.True(owner.TrySealSource(Closed)); Assert.True(owner.IsSourceVerified);
            if (fault == "repeat") { Assert.False(owner.TrySealSource(Closed)); }
            else if (fault == "revoke-after") { current = false; Assert.False(owner.IsSourceVerified); }
            else if (fault == "closure-revoked-after") { closureCurrent = false; Assert.False(owner.IsSourceVerified); }
            else
            {
                ((FileStream)typeof(LocalFrontierFileAdoption).GetField("sealedSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!).Dispose();
                Assert.False(owner.IsSourceVerified);
            }
        }
        else { Assert.False(owner.TrySealSource(Closed)); }
        Assert.False(work.IsRunning); Assert.False(owner.IsRetained); Assert.False(owner.IsSourceVerified);
        Assert.Equal(0, owner.ContributionCount); Assert.False(owner.TrySealSource(() => true));
        Assert.Empty(Directory.GetFiles(directory.Path, "frontier-payload-*"));
    }
}
