using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using NLink.Core.Logging;
using static NLink.Core.Logging.OrderedOperationalLogWritePump;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
public sealed partial class OrderedOperationalLogExactBatchReceiptTests
{
    [Fact]
    public void HighWaterFlushCanSucceedAfterAnEarlierBatchFailed()
    {
        using var fixture = new WriterFixture();
        using (fixture.BlockFile())
        {
            Assert.False(fixture.Pump.Submit(CreateBatch(1, "failed")));
        }

        Assert.True(fixture.Pump.Submit(CreateBatch(2, "persisted")));
        Assert.True(fixture.Pump.FlushThroughSequence(1, TimeSpan.Zero));
        Assert.Equal(["persisted"], File.ReadAllLines(fixture.LogPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedReceiptCannotBeUpgradedByLaterSuccessOrAnEqualBatchCopy(bool equalCopy)
    {
        using var fixture = new WriterFixture();
        var batch = CreateBatch(1, "batch-text");
        ExactBatchReceipt? failed;
        using (fixture.BlockFile())
        {
            Assert.False(fixture.Pump.SubmitWithReceipt(batch, out failed));
        }

        Assert.NotNull(failed);
        Assert.Equal(ExactBatchPersistence.Failed, fixture.Pump.GetPersistence(failed));
        Assert.True(fixture.Pump.SubmitWithReceipt(
            equalCopy ? batch with { } : CreateBatch(2, "later"), out var succeeded));
        Assert.NotNull(succeeded);
        Assert.NotSame(failed, succeeded);
        Assert.True(fixture.Pump.FlushThroughSequence(batch.LastSequence, TimeSpan.Zero));
        Assert.Equal(ExactBatchPersistence.Persisted, fixture.Pump.GetPersistence(succeeded));
        Assert.Equal(ExactBatchPersistence.Failed, fixture.Pump.GetPersistence(failed));
        Assert.Single(File.ReadAllLines(fixture.LogPath));
    }

    [Fact]
    public void NullConstructedAndForeignPumpTokensAreUnowned()
    {
        using var owner = new WriterFixture();
        using var foreign = new WriterFixture();
        Assert.True(owner.Pump.SubmitWithReceipt(CreateBatch(1, "persisted"), out var receipt));

        Assert.NotNull(receipt);
        Assert.Equal(ExactBatchPersistence.Unowned, owner.Pump.GetPersistence(null));
        Assert.Equal(ExactBatchPersistence.Unowned, owner.Pump.GetPersistence(new ExactBatchReceipt()));
        Assert.Equal(ExactBatchPersistence.Unowned, foreign.Pump.GetPersistence(receipt));
        foreign.Pump.RevokeExactBatchReceipts();
        Assert.Equal(ExactBatchPersistence.Unowned, foreign.Pump.GetPersistence(receipt));
        Assert.Equal(ExactBatchPersistence.Persisted, owner.Pump.GetPersistence(receipt));
    }

    [Fact]
    public void DeferredReceiptsRemainPendingUntilTheirActualFifoWritesSucceed()
    {
        using var fixture = new WriterFixture();
        using var mutex = fixture.BlockMutex();
        Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(1, "first"), out var first));
        Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(2, "second"), out var second));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(ExactBatchPersistence.Pending, fixture.Pump.GetPersistence(first));
        Assert.Equal(ExactBatchPersistence.Pending, fixture.Pump.GetPersistence(second));
        Assert.False(fixture.Pump.FlushThroughSequence(2, TimeSpan.Zero));
        Assert.False(File.Exists(fixture.LogPath));

        mutex.Release();
        fixture.WaitForIdle();
        Assert.Equal(ExactBatchPersistence.Persisted, fixture.Pump.GetPersistence(first));
        Assert.Equal(ExactBatchPersistence.Persisted, fixture.Pump.GetPersistence(second));
        Assert.Equal(["first", "second"], File.ReadAllLines(fixture.LogPath));
        Assert.Equal(2, fixture.Markers.Count(marker => marker.Kind == OperationalLogWriteMarkerKind.Recovered));
    }

    [Fact]
    public void QueueCapacityFailureStaysFailedAfterLaterSequencePersists()
    {
        using var fixture = new WriterFixture(maximumQueuedBatches: 1);
        using var mutex = fixture.BlockMutex();
        Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(1, "first"), out var first));
        Assert.False(fixture.Pump.SubmitWithReceipt(CreateBatch(2, "rejected"), out var rejected));
        Assert.NotNull(rejected);
        Assert.Equal(ExactBatchPersistence.Failed, fixture.Pump.GetPersistence(rejected));
        Assert.Contains(fixture.Markers, marker => marker.FailureCode == "queue_capacity_exhausted");

        mutex.Release();
        fixture.WaitForIdle();
        Assert.True(fixture.Pump.Submit(CreateBatch(3, "later")));
        Assert.True(fixture.Pump.FlushThroughSequence(2, TimeSpan.Zero));
        Assert.Equal(ExactBatchPersistence.Persisted, fixture.Pump.GetPersistence(first));
        Assert.Equal(ExactBatchPersistence.Failed, fixture.Pump.GetPersistence(rejected));
        Assert.Equal(["first", "later"], File.ReadAllLines(fixture.LogPath));
    }

    [Fact]
    public void DeferredTerminalFileFailureCannotBecomePersisted()
    {
        using var fixture = new WriterFixture();
        using var mutex = fixture.BlockMutex();
        Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(1, "failed"), out var receipt));
        using (fixture.BlockFile())
        {
            mutex.Release();
            fixture.WaitForIdle();
            Assert.Equal(ExactBatchPersistence.Failed, fixture.Pump.GetPersistence(receipt));
        }

        Assert.True(fixture.Pump.Submit(CreateBatch(2, "later")));
        Assert.True(fixture.Pump.FlushThroughSequence(1, TimeSpan.Zero));
        Assert.Equal(ExactBatchPersistence.Failed, fixture.Pump.GetPersistence(receipt));
        Assert.Equal(["later"], File.ReadAllLines(fixture.LogPath));
    }

    [Fact]
    public void AbandonedInFlightWriteStaysAbandonedEvenWhenItsBytesArriveLater()
    {
        using var fixture = new WriterFixture();
        using var mutex = fixture.BlockMutex();
        ExactBatchReceipt? receipt;
        // The immediate attempt reenters this gate on the test thread; the queued
        // worker must stop here after capture until abandonment has completed.
        lock (fixture.LoggerGate)
        {
            Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(1, "late-physical-write"), out receipt));
            fixture.WaitForWorkerCapture();
            Assert.False(fixture.Pump.CompleteAndFlush(CreateBatch(2, "drain", isDrain: true), TimeSpan.Zero));
            Assert.Equal(ExactBatchPersistence.Abandoned, fixture.Pump.GetPersistence(receipt));
            mutex.Release();
        }

        fixture.WaitForIdle();
        Assert.Equal(["late-physical-write"], File.ReadAllLines(fixture.LogPath));
        Assert.Equal(ExactBatchPersistence.Abandoned, fixture.Pump.GetPersistence(receipt));
        Assert.False(fixture.Pump.FlushThroughSequence(1, TimeSpan.Zero));
        Assert.DoesNotContain(fixture.Markers, marker => marker.Kind == OperationalLogWriteMarkerKind.Recovered);
        _ = fixture.Pump.SubmitWithReceipt(CreateBatch(3, "after-abandon"), out var afterAbandon);
        Assert.Null(afterAbandon);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevocationIsPermanentAndDoesNotCancelOperationalWrites(bool deferred)
    {
        using var fixture = new WriterFixture();
        using var mutex = deferred ? fixture.BlockMutex() : null;
        Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(1, "first"), out var receipt));
        Assert.NotNull(receipt);
        Assert.Equal(deferred ? ExactBatchPersistence.Pending : ExactBatchPersistence.Persisted,
            fixture.Pump.GetPersistence(receipt));

        fixture.Pump.RevokeExactBatchReceipts();
        fixture.Pump.RevokeExactBatchReceipts();
        Assert.Equal(ExactBatchPersistence.Revoked, fixture.Pump.GetPersistence(receipt));
        mutex?.Release();
        fixture.WaitForIdle();
        Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(2, "later"), out var afterRevocation));
        Assert.Null(afterRevocation);
        Assert.Equal(ExactBatchPersistence.Revoked, fixture.Pump.GetPersistence(receipt));
        Assert.Equal(["first", "later"], File.ReadAllLines(fixture.LogPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LifetimeReceiptCapacityDoesNotRecycleOrRejectOperationalWrites(bool failWrites)
    {
        using var fixture = new WriterFixture();
        var receipts = new HashSet<ExactBatchReceipt>();
        using (failWrites ? fixture.BlockFile() : null)
        {
            for (var sequence = 1; sequence <= MaximumExactBatchReceipts; sequence++)
            {
                Assert.Equal(!failWrites, fixture.Pump.SubmitWithReceipt(
                    CreateBatch(sequence, $"batch-{sequence}"), out var receipt));
                Assert.NotNull(receipt);
                Assert.True(receipts.Add(receipt));
                Assert.Equal(failWrites ? ExactBatchPersistence.Failed : ExactBatchPersistence.Persisted,
                    fixture.Pump.GetPersistence(receipt));
            }
        }

        Assert.Equal(64, receipts.Count);
        Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(65, "untracked-65"), out var excess));
        Assert.Null(excess);
        Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(66, "untracked-66"), out excess));
        Assert.Null(excess);
        Assert.Equal(failWrites ? 2 : 66, File.ReadAllLines(fixture.LogPath).Length);
    }

    [Fact]
    public void OrdinarySubmissionsDoNotConsumeReceiptCapacity()
    {
        using var fixture = new WriterFixture();
        for (var sequence = 1; sequence <= MaximumExactBatchReceipts + 1; sequence++)
        {
            Assert.True(fixture.Pump.Submit(CreateBatch(sequence, "ordinary")));
        }

        Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(66, "tracked"), out var receipt));
        Assert.NotNull(receipt);
        Assert.Equal(ExactBatchPersistence.Persisted, fixture.Pump.GetPersistence(receipt));
    }

    [Fact]
    public void PendingReceiptsAlsoConsumeTheFixedLifetimeCapacity()
    {
        using var fixture = new WriterFixture();
        using var mutex = fixture.BlockMutex();
        var receipts = new List<ExactBatchReceipt>();
        for (var sequence = 1; sequence <= MaximumExactBatchReceipts; sequence++)
        {
            Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(sequence, $"batch-{sequence}"), out var receipt));
            Assert.NotNull(receipt);
            receipts.Add(receipt);
            Assert.Equal(ExactBatchPersistence.Pending, fixture.Pump.GetPersistence(receipt));
        }

        Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(65, "untracked"), out var excess));
        Assert.Null(excess);
        mutex.Release();
        fixture.WaitForIdle();
        Assert.All(receipts, receipt => Assert.Equal(ExactBatchPersistence.Persisted, fixture.Pump.GetPersistence(receipt)));
        Assert.Equal(65, File.ReadAllLines(fixture.LogPath).Length);
    }

    [Fact]
    public void ConcurrentSubmissionCannotExceedReceiptCapacity()
    {
        using var fixture = new WriterFixture();
        var receipts = new ConcurrentBag<ExactBatchReceipt>();
        Parallel.For(1, 97, sequence =>
        {
            Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(sequence, $"batch-{sequence}"), out var receipt));
            if (receipt is not null)
            {
                receipts.Add(receipt);
            }
        });

        Assert.Equal(MaximumExactBatchReceipts, receipts.Distinct().Count());
        Assert.All(receipts, receipt => Assert.Equal(ExactBatchPersistence.Persisted, fixture.Pump.GetPersistence(receipt)));
        Assert.Equal(96, File.ReadAllLines(fixture.LogPath).Length);
    }

    [Fact]
    public void SubmissionAfterWriterCompletionHasAnExactFailedReceipt()
    {
        using var fixture = new WriterFixture();
        Assert.True(fixture.Pump.CompleteAndFlush(CreateBatch(1, "drain", isDrain: true), TimeSpan.FromSeconds(5)));
        Assert.False(fixture.Pump.SubmitWithReceipt(CreateBatch(2, "too-late"), out var receipt));
        Assert.NotNull(receipt);
        Assert.Equal(ExactBatchPersistence.Failed, fixture.Pump.GetPersistence(receipt));
        Assert.Contains(fixture.Markers, marker => marker.FailureCode == "writer_completed");
        Assert.Equal(["drain"], File.ReadAllLines(fixture.LogPath));
    }

    [Fact]
    public void ThrowingMarkerSinkDoesNotChangePersistenceOutcomes()
    {
        using var fixture = new WriterFixture(throwFromMarkerSink: true);
        using var mutex = fixture.BlockMutex();
        Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(1, "deferred"), out var recovered));
        mutex.Release();
        fixture.WaitForIdle();
        Assert.Equal(ExactBatchPersistence.Persisted, fixture.Pump.GetPersistence(recovered));
        using (fixture.BlockFile())
        {
            Assert.False(fixture.Pump.SubmitWithReceipt(CreateBatch(2, "failed"), out var failed));
            Assert.Equal(ExactBatchPersistence.Failed, fixture.Pump.GetPersistence(failed));
        }
    }

    [Fact]
    public void ExactDrainReceiptCannotBeReissuedOrUpgradedByLaterDrainSuccess()
    {
        using var fixture = new WriterFixture();
        ExactBatchReceipt? failed;
        using (fixture.BlockFile())
        {
            Assert.False(fixture.Pump.CompleteAndFlushWithReceipt(CreateBatch(1, "failed-drain", isDrain: true), TimeSpan.Zero, out failed));
        }
        Assert.NotNull(failed);
        Assert.Equal(ExactBatchPersistence.Failed, fixture.Pump.GetPersistence(failed));
        Assert.True(fixture.Pump.Submit(CreateBatch(2, "later-drain", isDrain: true)));
        Assert.True(fixture.Pump.CompleteAndFlushWithReceipt(CreateBatch(1, "failed-drain", isDrain: true), TimeSpan.Zero, out var repeated));
        Assert.Null(repeated);
        Assert.Equal(ExactBatchPersistence.Failed, fixture.Pump.GetPersistence(failed));
        Assert.Equal(["later-drain"], File.ReadAllLines(fixture.LogPath));
    }

    [Fact]
    public void ExactDrainReceiptRequiresItsOwnAppendAndRetainsRevocation()
    {
        using var fixture = new WriterFixture();
        Assert.True(fixture.Pump.CompleteAndFlushWithReceipt(CreateBatch(1, "drain", isDrain: true), TimeSpan.FromSeconds(5), out var receipt));
        Assert.Equal(ExactBatchPersistence.Persisted, fixture.Pump.GetPersistence(receipt));
        Assert.True(fixture.Pump.CompleteAndFlushWithReceipt(CreateBatch(1, "drain", isDrain: true), TimeSpan.Zero, out var repeated));
        Assert.Null(repeated);
        fixture.Pump.RevokeExactBatchReceipts();
        Assert.Equal(ExactBatchPersistence.Revoked, fixture.Pump.GetPersistence(receipt));
        Assert.Single(File.ReadAllLines(fixture.LogPath));
    }

    [Fact]
    public void ExactDrainReceiptIsAbandonedWithItsDeferredWrite()
    {
        using var fixture = new WriterFixture();
        using var mutex = fixture.BlockMutex();
        Assert.False(fixture.Pump.CompleteAndFlushWithReceipt(CreateBatch(1, "drain", isDrain: true), TimeSpan.Zero, out var receipt));
        Assert.Equal(ExactBatchPersistence.Abandoned, fixture.Pump.GetPersistence(receipt));
        mutex.Release();
        fixture.WaitForIdle();
        Assert.Equal(ExactBatchPersistence.Abandoned, fixture.Pump.GetPersistence(receipt));
    }

    [Fact]
    public void ExhaustedExactReceiptCapacityDoesNotSuppressTheFinalDrain()
    {
        using var fixture = new WriterFixture();
        for (var index = 1; index <= MaximumExactBatchReceipts; index++)
        { Assert.True(fixture.Pump.SubmitWithReceipt(CreateBatch(index, "payload"), out _)); }
        Assert.True(fixture.Pump.CompleteAndFlushWithReceipt(CreateBatch(65, "drain", isDrain: true), TimeSpan.FromSeconds(5), out var receipt));
        Assert.Null(receipt);
        Assert.Equal("drain", File.ReadAllLines(fixture.LogPath)[^1]);
    }

    private static OperationalLogWriteBatch CreateBatch(long sequence, string text, bool isDrain = false)
        => new(
            "acceptance-run", "scenario-run", "writer-test", sequence, sequence, sequence,
            DateTime.UtcNow.Ticks,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))),
            text, Encoding.UTF8.GetByteCount(text), isDrain);

    private sealed class WriterFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(), "nlink-exact-batch-receipt", Guid.NewGuid().ToString("N"));

        public WriterFixture(int maximumQueuedBatches = DefaultMaximumQueuedBatches, bool throwFromMarkerSink = false)
        {
            Directory.CreateDirectory(directory);
            LogPath = Path.Combine(directory, "writer.log");
            Logger = new RollingFileLogger(LogPath);
            Pump = new OrderedOperationalLogWritePump(Logger, marker =>
            {
                Markers.Enqueue(marker);
                if (throwFromMarkerSink)
                {
                    throw new InvalidOperationException("Injected marker-sink failure.");
                }
            }, maximumQueuedBatches);
        }

        public string LogPath { get; }
        public RollingFileLogger Logger { get; }
        public OrderedOperationalLogWritePump Pump { get; }
        public ConcurrentQueue<OperationalLogWriteMarker> Markers { get; } = new();
        public object LoggerGate => typeof(RollingFileLogger)
            .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Logger)!;

        public FileStream BlockFile()
            => new(LogPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        public MutexBlock BlockMutex() => new(Logger.CrossProcessMutexNameForTests);

        public void WaitForWorkerCapture()
        {
            // Observe capture under the real pump gate; no timing sleep or production
            // test callback is needed to put the real I/O attempt behind the mutex.
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (ReadField<object>("gate"))
                {
                    return ReadField<IEnumerable>("pending").Cast<object>().Any(queued =>
                        (int)queued.GetType().GetProperty("AttemptCount")!.GetValue(queued)! > 1);
                }
            }, TimeSpan.FromSeconds(5)), "Writer did not capture the queued batch.");
        }

        public void WaitForIdle()
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (ReadField<object>("gate"))
                {
                    return !ReadField<bool>("workerRunning");
                }
            }, TimeSpan.FromSeconds(5)), "Writer did not finish.");
        }

        private T ReadField<T>(string name)
            => (T)typeof(OrderedOperationalLogWritePump)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Pump)!;

        public void Dispose()
        {
            WaitForIdle();
            Pump.RevokeExactBatchReceipts();
            Directory.Delete(directory, recursive: true);
        }
    }

    internal sealed class MutexBlock : IDisposable
    {
        private readonly ManualResetEventSlim held = new(false);
        private readonly ManualResetEventSlim release = new(false);
        private readonly Thread owner;
        private Exception? failure;

        public MutexBlock(string name)
        {
            owner = new Thread(() =>
            {
                try
                {
                    using var mutex = new Mutex(initiallyOwned: false, name);
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Could not acquire the writer mutex.");
                    }

                    try
                    {
                        held.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(15)))
                        {
                            throw new TimeoutException("Writer mutex was not released.");
                        }
                    }
                    finally
                    {
                        mutex.ReleaseMutex();
                    }
                }
                catch (Exception exception)
                {
                    failure = exception;
                    held.Set();
                }
            });
            owner.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
        }

        public void Release()
        {
            release.Set();
            Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
        }

        public void Dispose()
        {
            Release();
            held.Dispose();
            release.Dispose();
        }
    }
}
