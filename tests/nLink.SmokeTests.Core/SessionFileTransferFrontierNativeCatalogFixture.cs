using System.Diagnostics;
using System.Text.Json;
using NLink.Tools;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    // Called only by the fresh PowerShell 7 app-role process, not by discovery.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task<object> RunNativeFrontierCatalogFixtureAsync(
        LocalFrontierAppRequest.AppLease lease, string variant, EventWaitHandle ready, EventWaitHandle release)
    {
        var stage = variant switch
        {
            "cancel_before_create" or "foreign_claim" => "before_create",
            "cancel_before_prepare" => "before_prepare",
            "cancel_during_prepare" => "inside_prepare",
            "cancel_after_prepare" or "metadata_after_prepare" => "after_prepare",
            _ => "after_take",
        };
        void Checkpoint(string current)
        {
            if (current != stage) { return; }
            ready.Set();
            if (variant == "parent_death")
            {
                var timer = Stopwatch.StartNew();
                while (lease.IsCurrent && timer.Elapsed < TimeSpan.FromSeconds(15)) { Thread.Sleep(10); }
                Assert.False(lease.IsCurrent);
            }
            else { Assert.True(release.WaitOne(TimeSpan.FromSeconds(15)), "Native catalog fixture release timeout"); }
        }

        Assert.True(lease.IsCurrent);
        Checkpoint("before_create");
        if (variant == "foreign_claim")
        {
            var other = new object();
            Assert.True(lease.TryBindCatalog(other));
            Assert.False(lease.TryBindCatalog(other));
            Assert.True(lease.OwnsCatalog(other));
        }
        var create = FrontierCatalogType.GetMethod("TryCreateNative")!;
        Assert.Null(create.Invoke(null, [null]));
        using var catalog = (IDisposable?)create.Invoke(null, [lease]);
        if (variant is "cancel_before_create" or "foreign_claim")
        {
            Assert.Null(catalog);
            Assert.Null(create.Invoke(null, [lease]));
            return new { Variant = variant, Passed = true, CatalogCreated = false, PayloadPublished = false };
        }
        Assert.NotNull(catalog);
        Assert.True(lease.OwnsCatalog(catalog!));
        Assert.False(lease.OwnsCatalog(new object()));
        Assert.Null(create.Invoke(null, [lease])); // no second catalog for this lease
        Assert.True(lease.OwnsCatalog(catalog!));
        var observer = CatalogAdmit(catalog!, out var reason)!;
        Assert.NotNull(observer);
        Assert.Equal(lease.AppProcessId, (int)BoundaryField(observer, "processId")!);
        Assert.Equal(lease.AppProcessCreationFileTime, (long)BoundaryField(observer, "processCreation")!);
        var path = variant == "valid_v4" ? "v4" : "v6";
        var prepared = false;
        var taken = false;
        var byteCount = 0;
        var revoked = variant is not ("valid_v6" or "valid_v4");
        await RunFrontierBoundaryAsync(path, "before_recheck", true, afterCaptureClosed: current =>
        {
            var sequence = BoundaryField(current, "sequence");
            var timing = GetPrivatePropertyValue(current, "Timing");
            Checkpoint("before_prepare");
            if (variant == "cancel_during_prepare")
            {
                var payloadGate = BoundaryField(current, "payloadGate")!;
                Task<bool> work;
                Monitor.Enter(payloadGate);
                try
                {
                    work = Task.Run(() => CatalogPrepare(catalog!, current, out _));
                    Assert.True(SpinWait.SpinUntil(() => GetPrivateProperty<int>(catalog!, "PreparationAttempts") == 1, TimeSpan.FromSeconds(5)));
                    Checkpoint("inside_prepare");
                }
                finally { Monitor.Exit(payloadGate); }
                prepared = work.GetAwaiter().GetResult();
            }
            else { prepared = CatalogPrepare(catalog!, current, out reason); }
            if (variant is "cancel_before_prepare" or "cancel_during_prepare")
            {
                Assert.False(prepared);
                Assert.Equal(variant == "cancel_before_prepare" ? 0 : 1, GetPrivateProperty<int>(catalog!, "PreparationAttempts"));
            }
            else { Assert.True(prepared, reason); }
            Checkpoint("after_prepare");
            var snapshot = CatalogTake(catalog!, out reason);
            taken = snapshot is not null;
            if (variant is "cancel_before_prepare" or "cancel_during_prepare" or "cancel_after_prepare" or "metadata_after_prepare")
            { Assert.Null(snapshot); }
            else
            {
                Assert.NotNull(snapshot);
                Assert.True(GetPrivateProperty<bool>(snapshot!, "IsCurrent"));
                var bytes = CatalogCopy(snapshot!, 0)!;
                byteCount = bytes.Length;
                Assert.Equal(CopyBoundaryPayload(BoundaryField(current, "preparedPayload")!), bytes);
                using var payload = JsonDocument.Parse(bytes);
                Assert.Equal(lease.AppProcessId, payload.RootElement.GetProperty("ProcessId").GetInt32());
                Assert.Equal(lease.AppProcessCreationFileTime, payload.RootElement.GetProperty("ProcessCreationFileTime").GetInt64());
                foreach (var flag in new[] { "LiveProofEligible", "NativeWriterCustodyEstablished", "PublicationCompletionIssued" })
                { Assert.False(GetPrivateProperty<bool>(snapshot!, flag)); }
                Checkpoint("after_take");
                if (variant == "lease_disposed") { lease.Dispose(); }
                if (variant == "attachment_completed") { Assert.True(lease.CompleteAttachment()); }
                if (revoked)
                {
                    Assert.False(GetPrivateProperty<bool>(snapshot!, "IsCurrent"));
                    Assert.Null(CatalogCopy(snapshot!, 0));
                    Assert.False(CatalogOwns(catalog!, snapshot));
                }
                else { Assert.Equal(bytes, CatalogCopy(snapshot!, 0)); }
            }
            if (revoked)
            {
                Assert.Null(CatalogAdmit(catalog!, out reason));
                Assert.Equal("native_request_revoked", reason);
                Assert.Equal(0, GetPrivateProperty<int>(catalog!, "RetainedByteCount"));
            }
            Assert.Equal(sequence, BoundaryField(current, "sequence"));
            Assert.Same(timing, GetPrivatePropertyValue(current, "Timing"));
        }, suppliedObserver: observer);
        return new { Variant = variant, Passed = true, CatalogCreated = true, Prepared = prepared, Taken = taken,
            Bytes = byteCount, NativeRevoked = revoked, PayloadPublished = false };
    }
}
