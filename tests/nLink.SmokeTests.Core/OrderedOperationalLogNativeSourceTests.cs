using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NLink.Core.Logging;
using static NLink.Core.Logging.OrderedOperationalLogWritePump;

namespace NLink.SmokeTests;

public sealed partial class OrderedOperationalLogExactBatchReceiptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSourceSharedLogCannotBeSealedAtOnlyOneAppDrain(bool sourceAcquired)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var first = new WriterFixture();
        var second = new OrderedOperationalLogWritePump(new RollingFileLogger(first.LogPath), _ => { });
        Assert.True(first.Pump.SubmitWithSourceReceipt(CreateBatch(1, "first-marker"), out var marker));
        Assert.True(second.Submit(CreateBatch(1, "second-app-running")));
        Assert.True(first.Pump.CompleteAndFlushWithSourceReceipt(CreateBatch(2, "first-drain", true), TimeSpan.FromSeconds(5), out var drain));
        if (sourceAcquired) { Assert.NotNull(first.Pump.TryAcquireSource(drain!, [marker!, drain!])); }
        // Deliberately captures the integration hazard, not a desired live policy:
        // retention already denies the other app's write before acquisition.
        Assert.False(second.SubmitWithReceipt(CreateBatch(2, "second-marker-blocked"), out var blocked));
        Assert.Equal(ExactBatchPersistence.Failed, second.GetPersistence(blocked));
        first.Pump.ReleaseSource(drain);
        Assert.True(second.Submit(CreateBatch(3, "second-can-write-after-release")));
        Assert.Equal(ExactBatchPersistence.Failed, second.GetPersistence(blocked));
        Assert.DoesNotContain("second-marker-blocked", File.ReadAllText(first.LogPath));
        second.RevokeExactBatchReceipts();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSourceCapturePreservesOrdinaryUtf8Bytes(bool existing)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var plain = new WriterFixture();
        using var captured = new WriterFixture();
        if (existing)
        {
            Assert.True(plain.Pump.Submit(CreateBatch(1, "earlier")));
            Assert.True(captured.Pump.Submit(CreateBatch(1, "earlier")));
        }
        var batch = CreateBatch(2, "first\r\nsecond λ\nthird");
        Assert.True(plain.Pump.Submit(batch));
        Assert.True(captured.Pump.SubmitWithSourceReceipt(batch, out var receipt));
        Assert.Equal(File.ReadAllBytes(plain.LogPath), File.ReadAllBytes(captured.LogPath));
        Assert.NotNull(receipt!.Span);
        Assert.Null(receipt.Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSourceOriginalDrainVerifiesActualImmediateOrDeferredRanges(bool deferred)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new WriterFixture();
        using var mutex = deferred ? fixture.BlockMutex() : null;
        Assert.True(fixture.Pump.SubmitWithSourceReceipt(CreateBatch(1, "completion\r\nλ"), out var completion));
        Assert.True(fixture.Pump.Submit(CreateBatch(2, "untracked\r\nλ")));
        Assert.True(fixture.Pump.SubmitWithSourceReceipt(CreateBatch(3, "marker"), out var marker));
        ExactBatchReceipt? drain = null;
        var flush = Task.Run(() => fixture.Pump.CompleteAndFlushWithSourceReceipt(CreateBatch(4, "drain", true), TimeSpan.FromSeconds(5), out drain));
        if (deferred)
        {
            Assert.True(SpinWait.SpinUntil(() => fixture.Markers.Any(m => m.Batch.IsDrain), TimeSpan.FromSeconds(5)));
            mutex!.Release();
        }
        Assert.True(await flush);
        using var source = fixture.Pump.TryAcquireSource(drain!, [completion!, marker!, drain!]);
        Assert.NotNull(source);
        var bytes = File.ReadAllBytes(fixture.LogPath);
        Assert.Equal(bytes.LongLength, source.ByteCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), source.Sha256);
        using (var read = source.TryOpenRead())
        {
            Assert.NotNull(read);
            using var copy = new MemoryStream();
            read.CopyTo(copy);
            Assert.Equal(bytes, copy.ToArray());
        }
        Assert.Null(fixture.Pump.TryAcquireSource(drain!, [completion!, marker!, drain!]));
        Assert.True(source.IsVerified);
        Assert.Throws<IOException>(() => File.AppendAllText(fixture.LogPath, "changed"));
        Assert.Throws<IOException>(() => File.Move(fixture.LogPath, fixture.LogPath + ".replacement"));
        fixture.Pump.RevokeExactBatchReceipts();
        Assert.False(source.IsVerified);
        Assert.Null(source.TryOpenRead());
        File.AppendAllText(fixture.LogPath, "released");
    }

    [Theory]
    [InlineData("copied_drain")]
    [InlineData("copied_completion")]
    [InlineData("foreign_completion")]
    [InlineData("duplicate")]
    [InlineData("missing_drain")]
    public void NativeSourceRejectsNonoriginalOrIncompleteReceiptSelections(string variant)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new WriterFixture();
        using var foreign = new WriterFixture();
        Assert.True(fixture.Pump.SubmitWithSourceReceipt(CreateBatch(1, "completion"), out var completion));
        Assert.True(foreign.Pump.SubmitWithSourceReceipt(CreateBatch(1, "completion"), out var other));
        Assert.True(fixture.Pump.CompleteAndFlushWithSourceReceipt(CreateBatch(2, "drain", true), TimeSpan.FromSeconds(5), out var drain));
        ExactBatchReceipt Copy(ExactBatchReceipt original) => (ExactBatchReceipt)typeof(object)
            .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
        var claimedDrain = variant == "copied_drain" ? Copy(drain!) : drain!;
        var selected = variant switch
        {
            "copied_completion" => new[] { Copy(completion!), drain! },
            "foreign_completion" => new[] { other!, drain! },
            "duplicate" => new[] { completion!, completion!, drain! },
            "missing_drain" => new[] { completion! },
            _ => new[] { completion!, drain! }
        };
        Assert.Null(fixture.Pump.TryAcquireSource(claimedDrain, selected));
        if (variant == "copied_drain")
        { Assert.NotNull(fixture.Pump.TryAcquireSource(drain!, [completion!, drain!])); }
        else
        {
            Assert.Null(fixture.Pump.TryAcquireSource(drain!, [completion!, drain!]));
            File.AppendAllText(fixture.LogPath, "released");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSourceRejectsPathReplacementAndOriginalByteMutation(bool replaceFile)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new WriterFixture();
        Assert.True(fixture.Pump.SubmitWithSourceReceipt(CreateBatch(1, "completion"), out var completion));
        var bytes = File.ReadAllBytes(fixture.LogPath);
        if (replaceFile) { File.Move(fixture.LogPath, fixture.LogPath + ".old"); }
        else { bytes[Encoding.UTF8.Preamble.Length] = (byte)'X'; }
        File.WriteAllBytes(fixture.LogPath, bytes);
        Assert.True(fixture.Pump.CompleteAndFlushWithSourceReceipt(CreateBatch(2, "drain", true), TimeSpan.FromSeconds(5), out var drain));
        Assert.Null(fixture.Pump.TryAcquireSource(drain!, [completion!, drain!]));
        Assert.Null(fixture.Pump.TryAcquireSource(drain!, [drain!]));
        File.AppendAllText(fixture.LogPath, "released");
    }

    [Fact]
    public void NativeSourceOrdinaryDrainFlagCannotAcquireOrReissueFrozenCustody()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new WriterFixture();
        Assert.True(fixture.Pump.SubmitWithSourceReceipt(CreateBatch(1, "claimed-drain", true), out var claimed));
        Assert.Null(claimed!.Source);
        Assert.Null(fixture.Pump.TryAcquireSource(claimed, [claimed]));
        Assert.True(fixture.Pump.CompleteAndFlushWithSourceReceipt(CreateBatch(2, "actual-drain", true), TimeSpan.FromSeconds(5), out var actual));
        Assert.True(fixture.Pump.CompleteAndFlushWithSourceReceipt(CreateBatch(2, "actual-drain", true), TimeSpan.Zero, out var repeated));
        Assert.Null(repeated);
        fixture.Pump.ReleaseSource(actual);
        Assert.Null(fixture.Pump.TryAcquireSource(actual!, [claimed, actual!]));
        File.AppendAllText(fixture.LogPath, "released");
    }

    [Fact]
    public void NativeSourceReceiptCapacityOmissionStillWritesAndDoesNotFreeze()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new WriterFixture();
        for (var sequence = 1; sequence <= MaximumExactBatchReceipts; sequence++)
        { Assert.True(fixture.Pump.SubmitWithSourceReceipt(CreateBatch(sequence, "tracked"), out _)); }
        Assert.True(fixture.Pump.CompleteAndFlushWithSourceReceipt(CreateBatch(65, "drain", true), TimeSpan.FromSeconds(5), out var drain));
        Assert.Null(drain);
        Assert.Equal("drain", File.ReadAllLines(fixture.LogPath)[^1]);
        File.AppendAllText(fixture.LogPath, "unfrozen");
    }

    [Theory]
    [InlineData(33, true)]
    [InlineData(34, false)]
    public void NativeSourceRangeLimitAndCrossBufferUtf8VerificationAreBounded(int count, bool expected)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new WriterFixture();
        var receipts = new List<ExactBatchReceipt>();
        for (var index = 1; index < count; index++)
        {
            Assert.True(fixture.Pump.SubmitWithSourceReceipt(CreateBatch(index, new string('λ', 3000) + "\r\n" + index), out var receipt));
            receipts.Add(receipt!);
        }
        Assert.True(fixture.Pump.CompleteAndFlushWithSourceReceipt(CreateBatch(count, "drain", true), TimeSpan.FromSeconds(5), out var drain));
        receipts.Add(drain!);
        using var source = fixture.Pump.TryAcquireSource(drain!, receipts);
        Assert.Equal(expected, source is not null);
        if (!expected) { File.AppendAllText(fixture.LogPath, "released"); }
        Assert.Equal(268435456, OperationalLogNativeSource.MaximumBytes);
        Assert.Equal(TimeSpan.FromSeconds(5), OperationalLogNativeSource.MaximumVerificationTime);
    }

    [Fact]
    public void NativeSourceFreezeRejectsInvalidSourceSizeWithoutPathReopening()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new WriterFixture();
        Assert.True(fixture.Pump.Submit(CreateBatch(1, "ordinary")));
        using var file = File.OpenRead(fixture.LogPath);
        Assert.True(OperationalLogNativeSource.TryDescribe(file.SafeFileHandle, out var identity, out var length));
        Assert.Null(OperationalLogNativeSource.TryFreeze(file.SafeFileHandle, identity!, 0));
        Assert.Null(OperationalLogNativeSource.TryFreeze(file.SafeFileHandle, identity!, OperationalLogNativeSource.MaximumBytes + 1));
        Assert.Null(OperationalLogNativeSource.TryFreeze(file.SafeFileHandle, "foreign", length));
        Assert.Null(OperationalLogNativeSource.TryFreeze(file.SafeFileHandle, identity!, length + 1));
    }

    [Fact]
    public void NativeSourceAbandonedLateDrainCannotRetainHandle()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new WriterFixture();
        using var mutex = fixture.BlockMutex();
        ExactBatchReceipt? drain;
        lock (fixture.LoggerGate)
        {
            // The immediate append reenters the logger on this thread and
            // times out on the mutex. The captured worker blocks on this gate
            // until the real completion wait abandons the pending drain.
            var captured = Task.Run(fixture.WaitForWorkerCapture);
            Assert.False(fixture.Pump.CompleteAndFlushWithSourceReceipt(CreateBatch(1, "drain", true), TimeSpan.FromSeconds(2), out drain));
            captured.GetAwaiter().GetResult();
            mutex.Release();
        }
        fixture.WaitForIdle();
        Assert.Equal(["drain"], File.ReadAllLines(fixture.LogPath));
        Assert.Equal(ExactBatchPersistence.Abandoned, fixture.Pump.GetPersistence(drain));
        Assert.Null(drain!.Source);
        Assert.Null(fixture.Pump.TryAcquireSource(drain, [drain]));
        File.AppendAllText(fixture.LogPath, "unfrozen");
    }
}
