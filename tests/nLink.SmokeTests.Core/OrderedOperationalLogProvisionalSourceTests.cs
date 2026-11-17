using System.Reflection;
using NLink.Core.Logging;
using static NLink.Core.Logging.OrderedOperationalLogWritePump;

namespace NLink.SmokeTests;

public sealed partial class OrderedOperationalLogExactBatchReceiptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProvisionalDrainRetainsActualImmediateOrDeferredSourceWithoutFreezingOtherWriter(bool deferred)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new WriterFixture();
        using var mutex = deferred ? fixture.BlockMutex() : null;
        Assert.True(fixture.Pump.SubmitWithSourceReceipt(CreateBatch(1, "completion λ\r\nsecond"), out var completion));
        Assert.True(fixture.Pump.SubmitWithSourceReceipt(CreateBatch(2, "marker"), out var marker));
        ExactBatchReceipt? drain = null;
        var flush = Task.Run(() => fixture.Pump.CompleteAndFlushWithProvisionalSourceReceipt(
            CreateBatch(3, "drain", true), TimeSpan.FromSeconds(5), out drain));
        if (deferred)
        {
            Assert.True(SpinWait.SpinUntil(() => fixture.Markers.Any(m => m.Batch.IsDrain), TimeSpan.FromSeconds(5)));
            mutex!.Release();
        }
        Assert.True(await flush);
        using var source = fixture.Pump.TryAcquireProvisionalSource(drain!, [completion!, marker!, drain!]);
        Assert.NotNull(source);
        Assert.Null(drain!.Source);
        Assert.Equal(new FileInfo(fixture.LogPath).Length, source.CapturedByteCount);
        var second = new OrderedOperationalLogWritePump(new RollingFileLogger(fixture.LogPath), _ => { });
        Assert.True(second.Submit(CreateBatch(1, "second-writer-still-running")));
        Assert.True(source.IsRetained);
        File.Move(fixture.LogPath, fixture.LogPath + ".old");
        File.WriteAllText(fixture.LogPath, "replacement");
        using (var read = source.TryOpenProvisionalRead())
        using (var reader = new StreamReader(Assert.IsType<FileStream>(read)))
        {
            var text = reader.ReadToEnd();
            Assert.Contains("completion λ", text);
            Assert.Contains("second-writer-still-running", text);
            Assert.DoesNotContain("replacement", text);
        }
        Assert.Null(fixture.Pump.TryAcquireProvisionalSource(drain, [completion!, marker!, drain]));
        Assert.Null(fixture.Pump.TryAcquireSource(drain, [completion!, marker!, drain]));
        Assert.True(source.IsRetained);
        fixture.Pump.RevokeExactBatchReceipts();
        Assert.False(source.IsRetained);
        Assert.Null(source.TryOpenProvisionalRead());
    }

    [Theory]
    [InlineData("copied_drain")]
    [InlineData("copied_completion")]
    [InlineData("foreign_completion")]
    [InlineData("duplicate")]
    [InlineData("missing_drain")]
    [InlineData("replacement")]
    [InlineData("released")]
    [InlineData("revoked")]
    public void ProvisionalSourceRequiresOriginalPersistedReceiptSet(string variant)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new WriterFixture();
        using var foreign = new WriterFixture();
        Assert.True(fixture.Pump.SubmitWithSourceReceipt(CreateBatch(1, "completion"), out var completion));
        Assert.True(foreign.Pump.SubmitWithSourceReceipt(CreateBatch(1, "completion"), out var other));
        if (variant == "replacement") { File.Move(fixture.LogPath, fixture.LogPath + ".old"); }
        Assert.True(fixture.Pump.CompleteAndFlushWithProvisionalSourceReceipt(CreateBatch(2, "drain", true), TimeSpan.FromSeconds(5), out var drain));
        static ExactBatchReceipt Copy(ExactBatchReceipt receipt) => (ExactBatchReceipt)typeof(object)
            .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(receipt, null)!;
        var claimed = variant == "copied_drain" ? Copy(drain!) : drain!;
        var selected = variant switch
        {
            "copied_completion" => new[] { Copy(completion!), drain! },
            "foreign_completion" => new[] { other!, drain! },
            "duplicate" => new[] { completion!, completion!, drain! },
            "missing_drain" => new[] { completion! },
            _ => new[] { completion!, drain! }
        };
        if (variant == "released") { fixture.Pump.ReleaseSource(drain); }
        if (variant == "revoked") { fixture.Pump.RevokeExactBatchReceipts(); }
        Assert.Null(fixture.Pump.TryAcquireProvisionalSource(claimed, selected));
        Assert.Equal(variant == "copied_drain", fixture.Pump.TryAcquireProvisionalSource(drain!, [completion!, drain!]) is not null);
        File.AppendAllText(fixture.LogPath, "still-writable");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(33, true)]
    [InlineData(34, false)]
    public void ProvisionalSourceRangeBoundsAndWriterOnlyDrain(int count, bool expected)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new WriterFixture();
        var receipts = new List<ExactBatchReceipt>();
        for (var i = 1; i < count; i++)
        {
            Assert.True(fixture.Pump.SubmitWithSourceReceipt(CreateBatch(i, "tracked"), out var receipt));
            receipts.Add(receipt!);
        }
        Assert.True(fixture.Pump.CompleteAndFlushWithProvisionalSourceReceipt(CreateBatch(count, "drain", true), TimeSpan.FromSeconds(5), out var drain));
        receipts.Add(drain!);
        using var source = fixture.Pump.TryAcquireProvisionalSource(drain!, receipts);
        Assert.Equal(expected, source is not null);
        File.AppendAllText(fixture.LogPath, "still-writable");
    }

    [Fact]
    public void ProvisionalSourceCannotBeInferredFromOrdinaryDrainMetadataOrReissued()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new WriterFixture();
        Assert.True(fixture.Pump.SubmitWithSourceReceipt(CreateBatch(1, "claimed-drain", true), out var claimed));
        Assert.Null(claimed!.Provisional);
        Assert.Null(fixture.Pump.TryAcquireProvisionalSource(claimed, [claimed]));
        Assert.True(fixture.Pump.CompleteAndFlushWithProvisionalSourceReceipt(CreateBatch(2, "actual-drain", true), TimeSpan.FromSeconds(5), out var actual));
        Assert.True(fixture.Pump.CompleteAndFlushWithProvisionalSourceReceipt(CreateBatch(2, "actual-drain", true), TimeSpan.Zero, out var repeated));
        Assert.Null(repeated);
        Assert.NotNull(fixture.Pump.TryAcquireProvisionalSource(actual!, [claimed, actual!]));
    }

    [Fact]
    public void ProvisionalSourceReceiptCapacityOmissionStillWritesWithoutRetainingAHandle()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new WriterFixture();
        for (var i = 1; i <= MaximumExactBatchReceipts; i++)
        { Assert.True(fixture.Pump.SubmitWithSourceReceipt(CreateBatch(i, "tracked"), out _)); }
        Assert.True(fixture.Pump.CompleteAndFlushWithProvisionalSourceReceipt(CreateBatch(65, "drain", true), TimeSpan.FromSeconds(5), out var drain));
        Assert.Null(drain);
        Assert.Equal("drain", File.ReadAllLines(fixture.LogPath)[^1]);
        using var exclusive = File.Open(fixture.LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void ProvisionalSourceAbandonedLateDrainDoesNotRetainHandle()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new WriterFixture();
        using var mutex = fixture.BlockMutex();
        ExactBatchReceipt? drain;
        lock (fixture.LoggerGate)
        {
            var captured = Task.Run(fixture.WaitForWorkerCapture);
            Assert.False(fixture.Pump.CompleteAndFlushWithProvisionalSourceReceipt(CreateBatch(1, "drain", true), TimeSpan.FromSeconds(2), out drain));
            captured.GetAwaiter().GetResult(); mutex.Release();
        }
        fixture.WaitForIdle();
        Assert.Equal(ExactBatchPersistence.Abandoned, fixture.Pump.GetPersistence(drain));
        Assert.Null(drain!.Provisional);
        Assert.Null(fixture.Pump.TryAcquireProvisionalSource(drain, [drain]));
        using var exclusive = File.Open(fixture.LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
