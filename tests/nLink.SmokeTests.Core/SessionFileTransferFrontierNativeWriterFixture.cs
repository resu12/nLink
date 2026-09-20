using System.Collections;
using System.Diagnostics;
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
    public static async Task<object> RunNativeFrontierWriterFixtureAsync(
        LocalFrontierAppRequest.AppLease lease, string variant, EventWaitHandle ready, EventWaitHandle release)
    {
        var stage = variant == "cancel_before_writer" ? "before_writer" :
            variant == "cancel_before_write" ? "before_write" : "after_write";
        void Checkpoint(string current)
        {
            if (current != stage) { return; }
            ready.Set();
            if (variant == "parent_death")
            {
                Assert.True(SpinWait.SpinUntil(() => !lease.IsCurrent, TimeSpan.FromSeconds(15)));
            }
            else { Assert.True(release.WaitOne(TimeSpan.FromSeconds(15))); }
        }

        using var catalog = (IDisposable)FrontierCatalogType.GetMethod("TryCreateNative")!.Invoke(null, [lease])!;
        Assert.NotNull(catalog);
        var bind = FrontierCatalogType.GetMethod("TryBindOperationalWriter")!;
        if (variant == "late_bind") { Assert.NotNull(CatalogAdmit(catalog, out _)); }
        if (variant == "wrong_run") { Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_SCENARIO_RUN_ID", "wrong-run"); }
        Checkpoint("before_writer");
        var bound = (bool)bind.Invoke(catalog, null)!;
        if (variant is "cancel_before_writer" or "wrong_run" or "late_bind")
        {
            Assert.False(bound);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_SCENARIO_RUN_ID", "frontier-catalog-native");
            Assert.False((bool)bind.Invoke(catalog, null)!);
            Checkpoint("after_write");
            return new { Variant = variant, Passed = true, WriterBound = false, PayloadPublished = false };
        }
        Assert.True(bound);
        Assert.False((bool)bind.Invoke(catalog, null)!);
        var observer = CatalogAdmit(catalog, out var reason)!;
        Assert.NotNull(observer);
        object? emitter = null;
        var revoked = variant is "cancel_before_write" or "cancel_after_write" or "metadata_after_write" or
            "catalog_cancel" or "attachment_completed" or "lease_disposed" or "parent_death";
        var persisted = false;
        await RunFrontierBoundaryAsync(variant == "valid_v4" ? "v4" : "v6", "before_recheck", true,
            suppliedObserver: observer, beforeCapture: context =>
            {
                emitter = GetPrivatePropertyValue(context, "CoordinatorSnapshotEmitter")!;
                var scheduled = emitter.GetType().GetField("drainScheduled", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Assert.True(SpinWait.SpinUntil(() => (int)scheduled.GetValue(emitter)! == 0, TimeSpan.FromSeconds(5)));
                scheduled.SetValue(emitter, 1); // retain the original queue until this fixture drains it
            }, afterCaptureClosed: current =>
            {
                var timing = GetPrivatePropertyValue(current, "Timing");
                var sequence = BoundaryField(current, "sequence");
                var binding = BoundaryField(current, "completionWriter")!;
                var entries = ((IEnumerable)BoundaryField(current, "entries")!).Cast<object>();
                var completion = entries.Single(entry => GetPrivateProperty<string>(entry, "Kind") == "AcceptedCompletion");
                var envelope = GetPrivatePropertyValue(completion, "Binding")!;
                Assert.Same(envelope, BoundaryField(binding, "completion"));
                Assert.True((bool)BoundaryField(binding, "queued")!);
                Assert.Null(BoundaryField(binding, "receipt"));
                var associations = BoundaryField(emitter!, "frontierCompletionMessages")!;
                var exactMessage = Assert.Single(((IEnumerable)associations.GetType().GetProperty("Keys")!.GetValue(associations)!).Cast<string>());
                var copiedMessage = new string(exactMessage.ToCharArray());
                Assert.Equal(exactMessage, copiedMessage);
                Assert.NotSame(exactMessage, copiedMessage);
                Assert.True((bool)associations.GetType().GetMethod("ContainsKey")!.Invoke(associations, [exactMessage])!);
                Assert.False((bool)associations.GetType().GetMethod("ContainsKey")!.Invoke(associations, [copiedMessage])!);
                Assert.True(CatalogPrepare(catalog, current, out reason), reason);
                var snapshot = CatalogTake(catalog, out reason)!;
                Assert.NotNull(snapshot);
                object? ReadReceipt() => snapshot.GetType().GetMethod("GetCompletionWrite")!.Invoke(snapshot, [0]);
                Assert.Null(ReadReceipt());
                Checkpoint("before_write");
                var logger = (RollingFileLogger)typeof(LocalOperationalLog).GetField("Logger", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                using var mutex = variant == "deferred_write"
                    ? new OrderedOperationalLogExactBatchReceiptTests.MutexBlock(logger.CrossProcessMutexNameForTests) : null;
                using (variant == "writer_failure" ? new FileStream(LocalOperationalLog.LogFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
                {
                    emitter!.GetType().GetMethod("Drain", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(emitter, null);
                }
                if (mutex is not null)
                {
                    Assert.Null(ReadReceipt());
                    var pending = Assert.IsType<LocalOperationalLog.FrontierWriteReceipt>(BoundaryField(binding, "receipt"));
                    Assert.False(pending.WaitForPersistence(binding, TimeSpan.Zero));
                    var waiting = Task.Run(() => pending.WaitForPersistence(binding, TimeSpan.FromSeconds(5)));
                    Assert.False(waiting.Wait(100));
                    var logGate = typeof(LocalOperationalLog).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                    Assert.True(Monitor.TryEnter(logGate, 1000), "Persistence wait held the logging gate");
                    Monitor.Exit(logGate);
                    mutex.Release();
                    Assert.True(waiting.GetAwaiter().GetResult());
                }
                if (variant is "writer_failure" or "cancel_before_write")
                {
                    LocalOperationalLog.Info("NativeWriterFixture", "event=later_write_cannot_upgrade_failed_completion");
                    Assert.Null(ReadReceipt());
                    if (variant == "writer_failure")
                    {
                        var failed = Assert.IsType<LocalOperationalLog.FrontierWriteReceipt>(BoundaryField(binding, "receipt"));
                        Assert.False(failed.WaitForPersistence(binding, TimeSpan.Zero));
                    }
                }
                else
                {
                    Assert.True(SpinWait.SpinUntil(() => ReadReceipt() is not null, TimeSpan.FromSeconds(5)));
                    var receipt = Assert.IsType<LocalOperationalLog.FrontierWriteReceipt>(ReadReceipt());
                    Assert.True(receipt.IsPersistedFor(binding));
                    Assert.True(receipt.WaitForPersistence(binding, TimeSpan.Zero));
                    Assert.False(receipt.WaitForPersistence(new object(), TimeSpan.Zero));
                    Assert.False(receipt.IsPersistedFor(new object()));
                    var copy = envelope.GetType().GetMethod("<Clone>$")!.Invoke(envelope, null)!;
                    Assert.Null(binding.GetType().GetMethod("GetReceipt")!.Invoke(binding, [copy]));
                    Assert.Same(receipt, binding.GetType().GetMethod("GetReceipt")!.Invoke(binding, [envelope]));
                    var line = Assert.Single(File.ReadAllLines(LocalOperationalLog.LogFilePath), line =>
                        line.Contains($"log_writer_id={receipt.WriterIdentity}; log_writer_sequence={receipt.FirstSequence};"));
                    Assert.Equal(receipt.FirstSequence, receipt.LastSequence);
                    Assert.Equal(receipt.PayloadSha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line))).ToLowerInvariant());
                    Assert.Equal(receipt.Utf8ByteCount, Encoding.UTF8.GetByteCount(line));
                    using var payload = JsonDocument.Parse(CatalogCopy(snapshot, 0)!);
                    Assert.Contains("snapshot_sha256=" + payload.RootElement.GetProperty("Completion").GetProperty("SnapshotSha256").GetString(), line);
                    Assert.Contains("event=filetransfer_coordinator_transition_snapshot;", line);
                    persisted = true;
                    var registration = Assert.IsType<LocalOperationalLog.FrontierWriterRegistration>(BoundaryField(catalog, "writer"));
                    var forgedReceipt = Assert.IsType<LocalOperationalLog.FrontierWriteReceipt>(typeof(object)
                        .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(receipt, null));
                    Assert.False(forgedReceipt.IsPersistedFor(binding));
                    Assert.False(forgedReceipt.WaitForPersistence(binding, TimeSpan.Zero));
                    var foreignWriter = new LocalOperationalLog.FrontierWriterRegistration(lease, catalog);
                    Assert.Null(LocalOperationalLog.InfoWithFrontierReceipt("event=foreign_writer_rejected", foreignWriter, binding));
                    Assert.Null(LocalOperationalLog.InfoWithFrontierReceipt("event=repeated_association_rejected", registration, binding));
                    Assert.Same(receipt, ReadReceipt());
                    var copiedBinding = Activator.CreateInstance(binding.GetType(), registration)!;
                    Assert.True((bool)copiedBinding.GetType().GetMethod("TryStage")!.Invoke(copiedBinding, [copy, emitter])!);
                    Assert.False((bool)copiedBinding.GetType().GetMethod("TryEnqueue")!.Invoke(copiedBinding, [envelope])!);
                    copiedBinding.GetType().GetMethod("Write")!.Invoke(copiedBinding, ["event=copied_completion_rejected"]);
                    Assert.Null(copiedBinding.GetType().GetMethod("GetReceipt")!.Invoke(copiedBinding, [copy]));
                    if (variant == "receipt_capacity")
                    {
                        for (var index = 1; index < LocalOperationalLog.MaximumFrontierCompletionWrites; index++)
                        {
                            Assert.NotNull(LocalOperationalLog.InfoWithFrontierReceipt(
                                "event=bounded_writer_slot", registration, new object()));
                        }
                        Assert.Equal(16, registration.Writes.Count);
                        Assert.Null(LocalOperationalLog.InfoWithFrontierReceipt("event=writer_slot_overflow", registration, new object()));
                        Assert.True(registration.Revoked);
                        Assert.Empty(registration.Writes);
                        Assert.Null(ReadReceipt());
                        Assert.False(receipt.IsPersistedFor(binding));
                        Assert.Contains("event=writer_slot_overflow", File.ReadAllText(LocalOperationalLog.LogFilePath));
                    }
                }
                Checkpoint("after_write");
                if (variant == "catalog_cancel") { catalog.Dispose(); }
                if (variant == "attachment_completed") { Assert.True(lease.CompleteAttachment()); }
                if (variant == "lease_disposed") { lease.Dispose(); }
                if (revoked)
                {
                    Assert.Null(ReadReceipt());
                    if (BoundaryField(binding, "receipt") is LocalOperationalLog.FrontierWriteReceipt receipt)
                    {
                        Assert.False(receipt.IsPersistedFor(binding));
                        Assert.False(receipt.WaitForPersistence(binding, TimeSpan.Zero));
                    }
                }
                Assert.False(GetPrivateProperty<bool>(snapshot, "NativeWriterCustodyEstablished"));
                Assert.False(GetPrivateProperty<bool>(snapshot, "LiveProofEligible"));
                Assert.False(GetPrivateProperty<bool>(snapshot, "PublicationCompletionIssued"));
                Assert.Same(timing, GetPrivatePropertyValue(current, "Timing"));
                Assert.Equal(sequence, BoundaryField(current, "sequence"));
            });
        return new { Variant = variant, Passed = true, WriterBound = bound, CompletionPersisted = persisted,
            NativeRevoked = !lease.IsCurrent, WriterCustodyRevoked = revoked || variant == "receipt_capacity", PayloadPublished = false };
    }
}
