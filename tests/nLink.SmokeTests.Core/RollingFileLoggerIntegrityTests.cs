using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class RollingFileLoggerIntegrityTests
{
    [Fact]
    [Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
    public async Task RollingFileLogger_IndependentInstancesSerializeRotationAndAppend()
    {
        var directory = CreateTemporaryDirectory("concurrent");
        var logPath = Path.Combine(directory, "nlink.log");
        const int writerCount = 4;
        const int linesPerWriter = 75;

        try
        {
            var loggers = Enumerable.Range(0, writerCount)
                .Select(_ => new RollingFileLogger(logPath, maxFileBytes: 128, retainedFileCount: 200))
                .ToArray();
            var expected = Enumerable.Range(0, writerCount)
                .SelectMany(writer => Enumerable.Range(0, linesPerWriter)
                    .Select(line => $"writer-{writer:D2}-line-{line:D3}"))
                .ToHashSet(StringComparer.Ordinal);

            await Task.WhenAll(loggers.Select((logger, writer) => Task.Run(() =>
            {
                for (var line = 0; line < linesPerWriter; line++)
                {
                    var result = logger.TryWriteLine($"writer-{writer:D2}-line-{line:D3}");
                    Assert.True(result.Success, result.FailureCode);
                }
            })));

            var actual = Directory.GetFiles(directory, "nlink*.log", SearchOption.TopDirectoryOnly)
                .SelectMany(File.ReadAllLines)
                .ToArray();

            Assert.Equal(expected.Count, actual.Length);
            Assert.Equal(expected.Count, actual.Distinct(StringComparer.Ordinal).Count());
            Assert.All(actual, line => Assert.Contains(line, expected));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    [Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
    public void RollingFileLogger_NormalizedEquivalentPathsShareOneMutexIdentity()
    {
        var directory = CreateTemporaryDirectory("mutex-identity");

        try
        {
            var direct = new RollingFileLogger(Path.Combine(directory, "nlink.log"));
            var normalized = new RollingFileLogger(Path.Combine(directory, ".", "nlink.log"));

            Assert.Equal(
                direct.CrossProcessMutexNameForTests,
                normalized.CrossProcessMutexNameForTests);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    [Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
    public void RollingFileLogger_TryWriteLineReturnsMutexTimeoutWithoutThrowing()
    {
        var directory = CreateTemporaryDirectory("mutex-timeout");
        var logPath = Path.Combine(directory, "nlink.log");

        try
        {
            var logger = new RollingFileLogger(logPath);
            using var externalMutex = new Mutex(initiallyOwned: false, logger.CrossProcessMutexNameForTests);
            Assert.True(externalMutex.WaitOne(TimeSpan.FromSeconds(1)));

            RollingFileWriteResult? observedResult = null;
            var writer = new Thread(() =>
            {
                observedResult = logger.TryWriteLine("must-not-be-written");
            });
            writer.Start();
            Assert.True(writer.Join(TimeSpan.FromSeconds(2)));

            Assert.True(observedResult.HasValue);
            var result = observedResult.Value;
            Assert.False(result.Success);
            Assert.Equal("mutex_timeout", result.FailureCode);
            Assert.False(File.Exists(logPath));
            externalMutex.ReleaseMutex();
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    [Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
    public void OrderedOperationalLogWritePump_RecoversTimeoutInFifoOrderAndDrainsLast()
    {
        var directory = CreateTemporaryDirectory("ordered-recovery");
        var logPath = Path.Combine(directory, "nlink.log");
        var logger = new RollingFileLogger(logPath);
        var markers = new ConcurrentQueue<OperationalLogWriteMarker>();
        var pump = new OrderedOperationalLogWritePump(logger, markers.Enqueue);
        using var mutexHeld = new ManualResetEventSlim(initialState: false);
        using var releaseMutex = new ManualResetEventSlim(initialState: false);
        var mutexOwner = new Thread(() =>
        {
            using var externalMutex = new Mutex(
                initiallyOwned: false,
                logger.CrossProcessMutexNameForTests);
            Assert.True(externalMutex.WaitOne(TimeSpan.FromSeconds(1)));
            mutexHeld.Set();
            Assert.True(releaseMutex.Wait(TimeSpan.FromSeconds(5)));
            externalMutex.ReleaseMutex();
        });

        try
        {
            mutexOwner.Start();
            Assert.True(mutexHeld.Wait(TimeSpan.FromSeconds(2)));

            Assert.True(pump.Submit(CreateBatch(1, "event=first")));
            Assert.True(pump.Submit(CreateBatch(2, "event=second")));
            Assert.False(File.Exists(logPath));

            releaseMutex.Set();
            Assert.True(pump.CompleteAndFlush(CreateBatch(3, "event=drained", isDrain: true), TimeSpan.FromSeconds(5)));
            Assert.True(mutexOwner.Join(TimeSpan.FromSeconds(2)));

            Assert.Equal(
                ["event=first", "event=second", "event=drained"],
                File.ReadAllLines(logPath));
            Assert.Contains(
                markers,
                marker => marker.Kind == OperationalLogWriteMarkerKind.Deferred &&
                    marker.Batch.FirstSequence == 1 &&
                    marker.FailureCode == "mutex_timeout");
            Assert.Contains(
                markers,
                marker => marker.Kind == OperationalLogWriteMarkerKind.Recovered &&
                    marker.Batch.FirstSequence == 1);
            Assert.Contains(
                markers,
                marker => marker.Kind == OperationalLogWriteMarkerKind.Recovered &&
                    marker.Batch.FirstSequence == 2);
            Assert.DoesNotContain(markers, marker => marker.Kind == OperationalLogWriteMarkerKind.Failed);
        }
        finally
        {
            releaseMutex.Set();
            _ = mutexOwner.Join(TimeSpan.FromSeconds(2));
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    [Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
    public void OrderedOperationalLogWritePump_QueueCapacityFailureIsTerminalAndDoesNotOvertake()
    {
        var directory = CreateTemporaryDirectory("ordered-capacity");
        var logPath = Path.Combine(directory, "nlink.log");
        var logger = new RollingFileLogger(logPath);
        var markers = new ConcurrentQueue<OperationalLogWriteMarker>();
        var pump = new OrderedOperationalLogWritePump(
            logger,
            markers.Enqueue,
            maximumQueuedBatches: 1,
            maximumQueuedBytes: 1024);
        using var mutexHeld = new ManualResetEventSlim(initialState: false);
        using var releaseMutex = new ManualResetEventSlim(initialState: false);
        var mutexOwner = new Thread(() =>
        {
            using var externalMutex = new Mutex(
                initiallyOwned: false,
                logger.CrossProcessMutexNameForTests);
            Assert.True(externalMutex.WaitOne(TimeSpan.FromSeconds(1)));
            mutexHeld.Set();
            Assert.True(releaseMutex.Wait(TimeSpan.FromSeconds(5)));
            externalMutex.ReleaseMutex();
        });

        try
        {
            mutexOwner.Start();
            Assert.True(mutexHeld.Wait(TimeSpan.FromSeconds(2)));

            Assert.True(pump.Submit(CreateBatch(1, "event=first")));
            Assert.False(pump.Submit(CreateBatch(2, "event=capacity-rejected")));
            Assert.Contains(
                markers,
                marker => marker.Kind == OperationalLogWriteMarkerKind.Failed &&
                    marker.Batch.FirstSequence == 2 &&
                    marker.FailureCode == "queue_capacity_exhausted");

            releaseMutex.Set();
            Assert.True(pump.CompleteAndFlush(CreateBatch(3, "event=drained", isDrain: true), TimeSpan.FromSeconds(5)));
            Assert.Equal(["event=first", "event=drained"], File.ReadAllLines(logPath));
        }
        finally
        {
            releaseMutex.Set();
            _ = mutexOwner.Join(TimeSpan.FromSeconds(2));
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    [Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
    public void RollingFileLogger_AbandonedMutexOwnershipIsAccepted()
    {
        var directory = CreateTemporaryDirectory("mutex-abandoned");
        var logPath = Path.Combine(directory, "nlink.log");

        try
        {
            var logger = new RollingFileLogger(logPath);
            using var acquired = new ManualResetEventSlim(initialState: false);
            var owner = new Thread(() =>
            {
                using var externalMutex = new Mutex(
                    initiallyOwned: false,
                    logger.CrossProcessMutexNameForTests);
                if (externalMutex.WaitOne(TimeSpan.FromSeconds(1)))
                {
                    acquired.Set();
                }
            });
            owner.Start();
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(owner.Join(TimeSpan.FromSeconds(2)));

            var result = logger.TryWriteLine("written-after-abandoned-owner");

            Assert.True(result.Success, result.FailureCode);
            Assert.Contains(
                "written-after-abandoned-owner",
                File.ReadAllText(logPath),
                StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void RollingFileLogger_TryWriteLineNormalizesLockedFileFailure()
    {
        var directory = CreateTemporaryDirectory("locked-file");
        var logPath = Path.Combine(directory, "nlink.log");

        try
        {
            File.WriteAllText(logPath, "locked", Encoding.UTF8);
            var logger = new RollingFileLogger(logPath, maxFileBytes: 1);
            using var locked = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            var result = logger.TryWriteLine("must-not-be-written");

            Assert.False(result.Success);
            Assert.Equal("io_error", result.FailureCode);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    [Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
    public async Task RollingFileLogger_RetainedSnapshotSurvivesIndependentRotationAfterHandlesOpen()
    {
        var directory = CreateTemporaryDirectory("retained-snapshot");
        var logPath = Path.Combine(directory, "nlink.log");
        var rotatedPath = Path.Combine(directory, "nlink.1.log");
        const string cursor = "event=writer_snapshot_cursor; cursor_id=test-cursor";
        using var releaseRead = new ManualResetEventSlim(initialState: false);

        try
        {
            File.WriteAllLines(rotatedPath, [cursor, "rotated-before-snapshot"], Encoding.UTF8);
            File.WriteAllLines(logPath, ["active-before-snapshot"], Encoding.UTF8);
            var reader = new RollingFileLogger(logPath, maxFileBytes: 1, retainedFileCount: 3);
            var writer = new RollingFileLogger(logPath, maxFileBytes: 1, retainedFileCount: 3);
            var barrierReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var barrierCount = 0;
            reader.RetainedSliceReadBarrierForTests = _ =>
            {
                if (Interlocked.CompareExchange(ref barrierCount, 1, 0) != 0)
                {
                    return;
                }

                barrierReached.TrySetResult();
                Assert.True(releaseRead.Wait(TimeSpan.FromSeconds(5)));
            };

            var readTask = Task.Run(() =>
            {
                var found = reader.TryReadRetainedTail(cursor, out var tail);
                return (Found: found, Tail: tail);
            });
            await barrierReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var writeTask = Task.Run(() => writer.TryWriteLine("rotation-after-handles-open"));
            var writeResult = await writeTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(writeResult.Success, writeResult.FailureCode);

            releaseRead.Set();
            var readResult = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(readResult.Found);
            Assert.Contains("rotated-before-snapshot", readResult.Tail, StringComparison.Ordinal);
            Assert.Contains("active-before-snapshot", readResult.Tail, StringComparison.Ordinal);
            Assert.DoesNotContain("rotation-after-handles-open", readResult.Tail, StringComparison.Ordinal);
        }
        finally
        {
            releaseRead.Set();
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void LocalOperationalLog_PersistsWriterIdentityAndContiguousPhysicalLineSequences()
    {
        var marker = Guid.NewGuid().ToString("D");
        var startToken = $"event=writer_evidence_slice_started; marker={marker}";
        var endToken = $"event=writer_evidence_slice_ended; marker={marker}";

        LocalOperationalLog.Info("WriterEvidenceTests", startToken);
        LocalOperationalLog.Info(
            "WriterEvidenceTests",
            $"event=writer_evidence_first; marker={marker}{Environment.NewLine}" +
            $"event=writer_evidence_second; marker={marker}");
        LocalOperationalLog.Info("WriterEvidenceTests", endToken);

        Assert.True(LocalOperationalLog.TryReadRetainedSlice(startToken, endToken, out var slice));
        var evidenceLines = slice.Split(
            ["\r\n", "\n"],
            StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, evidenceLines.Length);
        Assert.All(
            evidenceLines,
            line => Assert.Contains(
                $"log_writer_id={LocalOperationalLog.WriterIdForTests}",
                line,
                StringComparison.Ordinal));
        var sequences = evidenceLines
            .Select(line => long.Parse(
                Regex.Match(line, @"\blog_writer_sequence=(\d+)").Groups[1].Value))
            .ToArray();
        Assert.Equal(sequences[0] + 1, sequences[1]);
        Assert.DoesNotContain("log_writer_id=", LocalOperationalLog.GetRecentLogText(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
    public async Task LocalOperationalLog_InfoErrorWarnPreservesPerEntryMetadataUnderContention()
    {
        var marker = Guid.NewGuid().ToString("D");
        var start = $"event=log_group_started; marker={marker}";
        var end = $"event=log_group_ended; marker={marker}";
        const string secret = "0123456789abcdef0123456789abcdef0123456789abcdef";
        LocalOperationalLog.Info("GroupEvidenceTests", start);
        using var ready = new Barrier(2);
        await Task.WhenAll(Task.Run(() =>
        {
            Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < 32; i++)
            {
                var suffix = $"; marker={marker}; group_index={i}; sharedKey={secret}";
                LocalOperationalLog.InfoErrorWarn("GroupEvidenceTests",
                    "event=log_group_info" + suffix, "event=log_group_error" + suffix, "event=log_group_warn" + suffix);
            }
        }), Task.Run(() =>
        {
            Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < 32; i++) { LocalOperationalLog.Info("GroupEvidenceTests", $"event=log_group_competitor; marker={marker}"); }
        }));
        LocalOperationalLog.Info("GroupEvidenceTests", end);
        Assert.True(LocalOperationalLog.TryReadRetainedSlice(start, end, out var slice));
        var lines = slice.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var groups = lines.Where(line => line.Contains("; group_index=", StringComparison.Ordinal))
            .GroupBy(line => Field(line, "group_index")).ToArray();
        Assert.Equal(32, groups.Length);
        Assert.Equal(32, lines.Count(line => line.Contains("event=log_group_competitor;", StringComparison.Ordinal)));
        Assert.DoesNotContain(secret, slice, StringComparison.Ordinal);
        foreach (var group in groups)
        {
            var rows = group.ToArray();
            Assert.Equal(3, rows.Length);
            var first = Field(rows[0], "log_writer_sequence");
            var batch = Field(rows[0], "log_writer_batch_generation");
            for (var i = 0; i < rows.Length; i++)
            {
                var level = new[] { "INFO", "ERROR", "WARN" }[i];
                Assert.Contains($"[{level}] [GroupEvidenceTests]", rows[i], StringComparison.Ordinal);
                Assert.Contains("; [redacted];", rows[i], StringComparison.Ordinal);
                Assert.Contains($"log_writer_id={LocalOperationalLog.WriterIdForTests};", rows[i], StringComparison.Ordinal);
                Assert.Equal(first + i, Field(rows[i], "log_writer_sequence"));
                Assert.Equal(first + i, Field(rows[i], "log_writer_batch_first_sequence"));
                Assert.Equal(first + i, Field(rows[i], "log_writer_batch_last_sequence"));
                Assert.Equal(batch + i, Field(rows[i], "log_writer_batch_generation"));
                if (i > 0) { Assert.True(Field(rows[i], "log_writer_attempt_utc_ticks") > Field(rows[i - 1], "log_writer_attempt_utc_ticks")); }
            }
        }
        static long Field(string line, string name) => long.Parse(Regex.Match(line, @"\b" + name + @"=(\d+)").Groups[1].Value);
    }

    [Fact]
    [Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
    public void LocalOperationalLog_WriteFailureMarkerCarriesExactAttemptIdentity()
    {
        var marker = LocalOperationalLog.FormatWriteFailureMarker(
            "p123r0123456789abcdef",
            42,
            "mutex_timeout");

        Assert.Equal(
            "event=operational_log_write_failed; log_writer_id=p123r0123456789abcdef; " +
            "log_writer_sequence=42; failure_code=mutex_timeout",
            marker);
    }

    private static string CreateTemporaryDirectory(string suffix)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "nlink-rolling-file-integrity",
            suffix,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static OperationalLogWriteBatch CreateBatch(long sequence, string text, bool isDrain = false)
    {
        return new OperationalLogWriteBatch(
            "acceptance-run",
            "scenario-run",
            "writer-test",
            sequence,
            sequence,
            sequence,
            DateTime.UtcNow.Ticks,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
            text,
            Encoding.UTF8.GetByteCount(text),
            isDrain);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
