using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLink.Core.Logging;
using NLink.Tools;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task<object> RunNativeFrontierMarkerFixtureAsync(
        LocalFrontierAppRequest.AppLease lease, string variant, EventWaitHandle ready, EventWaitHandle release,
        bool captureSource = false, bool writePayloadFiles = false)
    {
        var stage = variant == "cancel_before_marker" ? "before_marker" :
            variant == "cancel_after_marker" ? "after_marker" : "after_drain";
        void Checkpoint(string current)
        {
            if (stage != current) { return; }
            ready.Set();
            if (variant == "parent_death")
            { Assert.True(SpinWait.SpinUntil(() => !lease.IsCurrent, TimeSpan.FromSeconds(15))); }
            else { Assert.True(release.WaitOne(TimeSpan.FromSeconds(15))); }
        }

        using var catalog = (IDisposable)FrontierCatalogType.GetMethod("TryCreateNative")!.Invoke(null, [lease])!;
        Assert.NotNull(catalog);
        Assert.True((bool)FrontierCatalogType.GetMethod("TryBindOperationalWriter")!.Invoke(catalog, null)!);
        var registration = Assert.IsType<LocalOperationalLog.FrontierWriterRegistration>(BoundaryField(catalog, "writer"));
        var logger = (RollingFileLogger)typeof(LocalOperationalLog).GetField("Logger", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var pump = (OrderedOperationalLogWritePump)typeof(LocalOperationalLog).GetField("WritePump", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var count = variant is "two_payloads" or "payload_bytes_fault" ? 2 : 1;
        var observations = new List<object>();
        object? snapshot = null;
        bool Submit(object view) => (bool)view.GetType().GetMethod("TrySubmitMarkers")!.Invoke(view, null)!;
        object? Order(int index = 0) => snapshot!.GetType().GetMethod("GetOrderedWriterEvidence")!.Invoke(snapshot, [index]);
        for (var index = 0; index < count; index++)
        {
            var observer = CatalogAdmit(catalog, out var reason)!;
            observations.Add(observer);
            object? emitter = null;
            await RunFrontierBoundaryAsync(variant == "valid_v4" ? "v4" : "v6", "before_recheck", true,
                suppliedObserver: observer, beforeCapture: context =>
                {
                    emitter = GetPrivatePropertyValue(context, "CoordinatorSnapshotEmitter")!;
                    var field = emitter.GetType().GetField("drainScheduled", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    Assert.True(SpinWait.SpinUntil(() => (int)field.GetValue(emitter)! == 0, TimeSpan.FromSeconds(5)));
                    field.SetValue(emitter, 1);
                }, afterCaptureClosed: current =>
                {
                    Assert.True(CatalogPrepare(catalog, current, out reason), reason);
                    if (variant == "early_marker")
                    {
                        snapshot = CatalogTake(catalog, out reason)!;
                        Assert.False(Submit(snapshot));
                    }
                    using (variant == "completion_failure" ? new FileStream(LocalOperationalLog.LogFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
                    { emitter!.GetType().GetMethod("Drain", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(emitter, null); }
                });
        }
        snapshot ??= CatalogTake(catalog, out var takeReason);
        Assert.NotNull(snapshot);
        var timings = observations.Select(o => GetPrivatePropertyValue(o, "Timing")).ToArray();
        var sequences = observations.Select(o => BoundaryField(o, "sequence")).ToArray();
        Assert.Null(Order());
        var copiedSnapshot = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(snapshot, null)!;
        Assert.False(Submit(copiedSnapshot));
        Checkpoint("before_marker");
        if (variant == "late_marker") { Assert.True(LocalOperationalLog.CompleteAndFlush(TimeSpan.FromSeconds(5))); }

        var rejected = variant is "early_marker" or "completion_failure" or "cancel_before_marker" or "late_marker";
        var submitted = false;
        LocalOperationalLog.FrontierWriteReceipt? impostor = null, impersonatedCompletion = null;
        if (variant == "wrong_role")
        {
            impersonatedCompletion = Assert.IsType<LocalOperationalLog.FrontierWriteReceipt>(snapshot.GetType().GetMethod("GetCompletionWrite")!.Invoke(snapshot, [0]));
            impostor = LocalOperationalLog.InfoWithFrontierReceipt("event=not_a_payload_marker", registration, impersonatedCompletion);
            Assert.NotNull(impostor);
        }
        if (variant is not ("missing_marker" or "wrong_role"))
        {
            using var mutex = variant == "deferred_marker" ? new OrderedOperationalLogExactBatchReceiptTests.MutexBlock(logger.CrossProcessMutexNameForTests) : null;
            using (variant == "marker_failure" ? new FileStream(LocalOperationalLog.LogFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
            { submitted = Submit(snapshot); }
            Assert.Equal(!rejected, submitted);
            Assert.False(Submit(snapshot));
            Assert.Null(Order());
            if (mutex is not null)
            {
                var marker = Assert.Single(registration.Markers.Values)!;
                var completion = Assert.IsType<LocalOperationalLog.FrontierWriteReceipt>(Assert.Single(registration.Markers.Keys));
                Assert.False(marker.IsPersistedFor(completion));
                mutex.Release();
                Assert.True(SpinWait.SpinUntil(() => marker.IsPersistedFor(completion), TimeSpan.FromSeconds(5)));
            }
        }
        if (variant == "duplicate_marker")
        {
            var completion = Assert.IsType<LocalOperationalLog.FrontierWriteReceipt>(Assert.Single(registration.Markers.Keys));
            Assert.Null(LocalOperationalLog.TryWriteFrontierMarker(registration, completion, "event=duplicate_marker_attempt"));
            Assert.True(registration.Revoked);
        }
        Checkpoint("after_marker");
        if (variant is "source_substituted" or "source_mutated")
        {
            var bytes = File.ReadAllBytes(LocalOperationalLog.LogFilePath);
            if (variant == "source_substituted") { File.Move(LocalOperationalLog.LogFilePath, LocalOperationalLog.LogFilePath + ".old"); }
            else
            {
                var completion = registration.Writes.Values.Single()!;
                bytes[checked((int)completion.Exact.Span!.Offset)] = (byte)'X';
            }
            File.WriteAllBytes(LocalOperationalLog.LogFilePath, bytes);
        }
        if (variant is "deferred_drain" or "abandoned_drain")
        {
            using var mutex = new OrderedOperationalLogExactBatchReceiptTests.MutexBlock(logger.CrossProcessMutexNameForTests);
            if (variant == "abandoned_drain") { Assert.False(LocalOperationalLog.CompleteAndFlush(TimeSpan.Zero)); mutex.Release(); }
            else
            {
                var completion = Task.Run(() => LocalOperationalLog.CompleteAndFlush(TimeSpan.FromSeconds(5)));
                var gate = typeof(OrderedOperationalLogWritePump).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pump)!;
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    lock (gate)
                    { return ((IEnumerable)typeof(OrderedOperationalLogWritePump).GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pump)!).Cast<object>().Any(); }
                }, TimeSpan.FromSeconds(5)));
                mutex.Release();
                Assert.True(await completion);
            }
        }
        else
        {
            using (variant == "drain_failure" ? new FileStream(LocalOperationalLog.LogFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
            { Assert.Equal(variant != "drain_failure", LocalOperationalLog.CompleteAndFlush(variant == "drain_failure" ? TimeSpan.Zero : TimeSpan.FromSeconds(5))); }
        }

        var expectedOrder = !rejected && variant is not ("missing_marker" or "marker_failure" or "drain_failure" or
            "abandoned_drain" or "duplicate_marker" or "cancel_after_marker" or "wrong_role");
        if (variant == "wrong_role")
        {
            Assert.True(impostor!.IsPersistedFor(impersonatedCompletion!));
            Assert.Null(LocalOperationalLog.GetFrontierDrain(registration, impersonatedCompletion!, impostor));
        }
        var orders = Enumerable.Range(0, count).Select(i => Order(i)).ToArray();
        Assert.Equal(expectedOrder, orders.All(o => o is not null));
        if (expectedOrder)
        {
            var lines = File.ReadAllLines(LocalOperationalLog.LogFilePath);
            for (var index = 0; index < count; index++)
            {
                var order = orders[index]!;
                Assert.True(GetPrivateProperty<bool>(order, "IsCurrent"));
                Assert.Same(order, Order(index));
                var copy = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(order, null)!;
                Assert.False(GetPrivateProperty<bool>(copy, "IsCurrent"));
                var original = Assert.IsType<LocalOperationalLog.FrontierWriteReceipt>(GetPrivatePropertyValue(order, "Completion"));
                var marker = Assert.IsType<LocalOperationalLog.FrontierWriteReceipt>(GetPrivatePropertyValue(order, "Marker"));
                var drain = Assert.IsType<LocalOperationalLog.FrontierWriteReceipt>(GetPrivatePropertyValue(order, "Drain"));
                Assert.True(original.LastSequence < marker.FirstSequence && marker.LastSequence < drain.FirstSequence);
                var line = Assert.Single(lines, l => l.Contains($"log_writer_sequence={marker.FirstSequence};"));
                var bytes = CatalogCopy(snapshot, index)!;
                using var payload = JsonDocument.Parse(bytes);
                Assert.Contains("event=filetransfer_frontier_payload_marker;", line);
                Assert.Contains("frontier_payload_format=nlink-frontier-boundary-producer-v1;", line);
                Assert.Contains("frontier_payload_hash_domain=sha256-utf8-frontier-boundary-producer-v1;", line);
                Assert.Contains("frontier_payload_sha256=" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ";", line);
                Assert.Contains($"frontier_payload_byte_count={bytes.Length};", line);
                Assert.Contains("frontier_payload_observation_id=" + payload.RootElement.GetProperty("ObservationId").GetGuid().ToString("N") + ";", line);
                Assert.Contains($"frontier_completion_sha256={original.PayloadSha256};", line);
                Assert.Equal(marker.PayloadSha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line))).ToLowerInvariant());
                Assert.DoesNotContain("[redacted]", line);
                Assert.DoesNotContain("snapshot_payload=", line);
                Assert.Null(LocalOperationalLog.GetFrontierDrain(registration, original, original));
                var forged = Assert.IsType<LocalOperationalLog.FrontierWriteReceipt>(typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(marker, null));
                Assert.Null(LocalOperationalLog.GetFrontierDrain(registration, original, forged));
                foreach (var name in new[] { "LiveProofEligible", "NativeWriterCustodyEstablished", "PublicationCompletionIssued" })
                { Assert.False(GetPrivateProperty<bool>(order, name)); }
            }
            Assert.Equal(count, registration.Markers.Count);
            Assert.Equal(count, registration.Writes.Count);
            Assert.Single(lines, l => l.Contains("event=operational_log_writer_drained;"));
        }
        object? source = null;
        object? payloadFiles = null;
        var outputFailed = false;
        object? WriteFiles(object view) => view.GetType().GetMethod("TryWritePayloadFiles")!.Invoke(view, null);
        if (writePayloadFiles && variant == "early_output")
        {
            Assert.Null(WriteFiles(snapshot));
            outputFailed = true;
        }
        var expectedSource = captureSource && expectedOrder && !outputFailed && variant is not ("source_substituted" or "source_mutated");
        if (captureSource)
        {
            object? Acquire(object view) => view.GetType().GetMethod("TryAcquireSourceFile")!.Invoke(view, null);
            Assert.Null(Acquire(copiedSnapshot));
            source = Acquire(snapshot);
            Assert.Equal(expectedSource, source is not null);
            Assert.Null(Acquire(snapshot));
            if (source is not null)
            {
                Assert.True(GetPrivateProperty<bool>(source, "IsCurrent"));
                var copy = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source, null)!;
                Assert.False(GetPrivateProperty<bool>(copy, "IsCurrent"));
                Assert.Null(copy.GetType().GetMethod("TryOpenRead")!.Invoke(copy, null));
                var bytes = File.ReadAllBytes(LocalOperationalLog.LogFilePath);
                Assert.Equal(bytes.LongLength, GetPrivateProperty<long>(source, "ByteCount"));
                Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), GetPrivateProperty<string>(source, "Sha256"));
                using (var read = Assert.IsType<FileStream>(source.GetType().GetMethod("TryOpenRead")!.Invoke(source, null)))
                {
                    using var output = new MemoryStream();
                    read.CopyTo(output);
                    Assert.Equal(bytes, output.ToArray());
                }
                Assert.Throws<IOException>(() => File.AppendAllText(LocalOperationalLog.LogFilePath, "mutation"));
                Assert.Throws<IOException>(() => File.Move(LocalOperationalLog.LogFilePath, LocalOperationalLog.LogFilePath + ".replaced"));
                foreach (var name in new[] { "LiveProofEligible", "NativeWriterCustodyEstablished", "PublicationCompletionIssued" })
                { Assert.False(GetPrivateProperty<bool>(source, name)); }
            }
        }
        if (writePayloadFiles)
        {
            Assert.Null(WriteFiles(copiedSnapshot));
            if (variant == "payload_bytes_fault")
            {
                // Fault only the second retained byte array, after the real marker
                // and source were verified. The original digest must still win.
                var prepared = BoundaryField(observations[1], "preparedPayload")!;
                var bytes = (byte[])BoundaryField(prepared, "payload")!;
                bytes[0] ^= 1;
            }
            payloadFiles = WriteFiles(snapshot);
            outputFailed |= variant == "payload_bytes_fault" || !expectedSource;
            Assert.Equal(!outputFailed, payloadFiles is not null);
            Assert.Null(WriteFiles(snapshot));
            if (payloadFiles is not null)
            {
                Assert.True(GetPrivateProperty<bool>(payloadFiles, "IsCurrent"));
                Assert.Equal(count, GetPrivateProperty<int>(payloadFiles, "Count"));
                Assert.Equal(GetPrivateProperty<int>(snapshot, "ByteCount"), GetPrivateProperty<int>(payloadFiles, "ByteCount"));
                Assert.Same(source, GetPrivatePropertyValue(payloadFiles, "Source"));
                var copy = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(payloadFiles, null)!;
                Assert.False(GetPrivateProperty<bool>(copy, "IsCurrent"));
                Assert.Null(copy.GetType().GetMethod("TryOpenRead")!.Invoke(copy, [0]));
                var identities = new HashSet<string>();
                for (var index = 0; index < count; index++)
                {
                    var bytes = CatalogCopy(snapshot, index)!;
                    Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                        payloadFiles.GetType().GetMethod("GetSha256")!.Invoke(payloadFiles, [index]));
                    Assert.Equal((long)bytes.Length, payloadFiles.GetType().GetMethod("GetByteCount")!.Invoke(payloadFiles, [index]));
                    Assert.True(identities.Add((string)payloadFiles.GetType().GetMethod("GetFileIdentity")!.Invoke(payloadFiles, [index])!));
                    Assert.Same(orders[index], payloadFiles.GetType().GetMethod("GetWriterEvidence")!.Invoke(payloadFiles, [index]));
                    using var read = Assert.IsType<FileStream>(payloadFiles.GetType().GetMethod("TryOpenRead")!.Invoke(payloadFiles, [index]));
                    using var output = new MemoryStream();
                    read.CopyTo(output);
                    Assert.Equal(bytes, output.ToArray());
                }
                Assert.Null(payloadFiles.GetType().GetMethod("TryOpenRead")!.Invoke(payloadFiles, [-1]));
                Assert.Null(payloadFiles.GetType().GetMethod("TryOpenRead")!.Invoke(payloadFiles, [count]));
                foreach (var name in new[] { "LiveProofEligible", "NativeWriterCustodyEstablished", "PublicationCompletionIssued" })
                { Assert.False(GetPrivateProperty<bool>(payloadFiles, name)); }
                Assert.Equal(count, Directory.GetFiles(LocalOperationalLog.LogsDirectoryPath, "frontier-payload-*.json").Length);
            }
            else { Assert.Empty(Directory.GetFiles(LocalOperationalLog.LogsDirectoryPath, "frontier-payload-*.json")); }
        }
        Checkpoint("after_drain");
        if (variant == "catalog_cancel") { catalog.Dispose(); }
        if (variant == "lease_disposed") { lease.Dispose(); }
        if (variant == "attachment_completed") { Assert.True(lease.CompleteAttachment()); }
        if (variant == "after_drain_write") { LocalOperationalLog.Info("MarkerFixture", "event=late_write_attempt"); }
        var revoked = variant is "cancel_after_drain" or "metadata_after_drain" or "catalog_cancel" or "lease_disposed" or
            "attachment_completed" or "parent_death" or "after_drain_write";
        if (revoked)
        {
            Assert.Null(Order());
            foreach (var order in orders.Where(o => o is not null)) { Assert.False(GetPrivateProperty<bool>(order!, "IsCurrent")); }
        }
        if (source is not null)
        {
            Assert.Equal(!revoked && !outputFailed, GetPrivateProperty<bool>(source, "IsCurrent"));
            if (payloadFiles is not null) { Assert.Equal(!revoked, GetPrivateProperty<bool>(payloadFiles, "IsCurrent")); }
            catalog.Dispose();
            Assert.False(GetPrivateProperty<bool>(source, "IsCurrent"));
            Assert.Null(source.GetType().GetMethod("TryOpenRead")!.Invoke(source, null));
            using var released = new FileStream(LocalOperationalLog.LogFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        if (payloadFiles is not null)
        {
            Assert.False(GetPrivateProperty<bool>(payloadFiles, "IsCurrent"));
            Assert.Null(payloadFiles.GetType().GetMethod("TryOpenRead")!.Invoke(payloadFiles, [0]));
        }
        if (writePayloadFiles) { Assert.Empty(Directory.GetFiles(LocalOperationalLog.LogsDirectoryPath, "frontier-payload-*.json")); }
        for (var index = 0; index < count; index++)
        {
            Assert.Same(timings[index], GetPrivatePropertyValue(observations[index], "Timing"));
            Assert.Equal(sequences[index], BoundaryField(observations[index], "sequence"));
        }
        return new { Variant = variant, Passed = true, MarkersSubmitted = submitted, OrderedWritesObserved = expectedOrder,
            SourceCustodyObserved = expectedSource, PayloadFilesObserved = payloadFiles is not null,
            NativeRevoked = !lease.IsCurrent, PayloadPublished = false };
    }
}
