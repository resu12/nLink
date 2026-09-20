using System.Reflection;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Tools;

namespace NLink.SmokeTests;

// Original native requests, real prepared producer payloads and real persisted
// completion/marker/drain receipts. No caller-generated submission descriptors.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class FrontierGenuineSubmissionFixture : IDisposable
{
    private readonly object catalog;
    private readonly LocalFrontierAppRequest.FileHandoff handoff;
    private readonly string variant;
    private readonly int ordinal;
    private object? contribution;
    public bool IsCurrent => handoff.IsCurrent;
    public bool Acknowledged => handoff.Acknowledged;
    public static void WriteStartupPrefix()
    {
        LocalOperationalLog.Info("GenuineFixture", "event=cursor_startup_prefix");
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var pump = (OrderedOperationalLogWritePump)typeof(LocalOperationalLog).GetField("WritePump", flags)!.GetValue(null)!;
        var sequence = (long)typeof(LocalOperationalLog).GetField("writerSequence", flags)!.GetValue(null)!;
        Assert.True(pump.FlushThroughSequence(sequence, TimeSpan.FromSeconds(5)));
    }
    private FrontierGenuineSubmissionFixture(object catalog, LocalFrontierAppRequest.FileHandoff handoff, string variant, int ordinal)
    { this.catalog = catalog; this.handoff = handoff; this.variant = variant; this.ordinal = ordinal; }
    public static FrontierGenuineSubmissionFixture Create(LocalFrontierAppRequest.AppLease lease, string variant, int ordinal)
    {
        var type = typeof(SessionFileTransferService).GetNestedType("FrontierPayloadCatalog", BindingFlags.NonPublic)!;
        var catalog = type.GetMethod("TryCreateNative")!.Invoke(null, [lease])!;
        Assert.NotNull(catalog);
        var handoff = Assert.IsType<LocalFrontierAppRequest.FileHandoff>(type.GetMethod("TryAttachFileHandoff")!.Invoke(catalog, null));
        Assert.True((bool)type.GetMethod("TryBindOperationalWriter")!.Invoke(catalog, null)!);
        Assert.False((bool)type.GetMethod("TryBindOperationalWriter")!.Invoke(catalog, null)!);
        return new(catalog, handoff, variant, ordinal);
    }
    public async Task PrepareAsync()
    {
        contribution = await SessionFileTransferV4SenderTests.PrepareGenuineFrontierSubmissionAsync(catalog, variant, ordinal);
        Assert.NotNull(contribution);
        var registration = (LocalOperationalLog.FrontierWriterRegistration)catalog.GetType().GetField("writer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(catalog)!;
        if (variant != "early_submit")
        {
            Assert.NotNull(registration.Drain);
            Assert.Null(registration.Drain!.Exact.Source);
            Assert.NotNull(registration.Drain.Exact.Provisional);
            Assert.True(registration.Drain.Exact.Provisional!.IsRetained);
        }
    }
    public void CheckCopies()
    {
        static object Copy(object value) => typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(value, null)!;
        var copy = Copy(contribution!);
        Assert.False((bool)copy.GetType().GetMethod("TrySubmitFiles")!.Invoke(copy, null)!);
        var catalogCopy = Copy(catalog);
        Assert.Null(catalogCopy.GetType().GetMethod("TryTakeWriterOnly")!.Invoke(catalogCopy, null));
        ((IDisposable)catalogCopy).Dispose();
        var registration = (LocalOperationalLog.FrontierWriterRegistration)catalog.GetType().GetField("writer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(catalog)!;
        ((LocalOperationalLog.FrontierWriterRegistration)Copy(registration)).Revoke();
        Assert.False(registration.Revoked);
        Assert.True(IsCurrent);
    }
    public bool Submit()
    {
        var submitted = (bool)contribution!.GetType().GetMethod("TrySubmitFiles")!.Invoke(contribution, null)!;
        if (submitted) { CheckOriginalManifest(); }
        return submitted;
    }
    private void CheckOriginalManifest()
    {
        var channel = typeof(LocalFrontierAppRequest.FileHandoff).GetField("channel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(handoff)!;
        var encoded = (byte[])channel.GetType().GetField("body", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(channel)!;
        var manifest = Assert.IsType<LocalFrontierFileManifest>(LocalFrontierFileManifest.Decode(encoded));
        var registration = (LocalOperationalLog.FrontierWriterRegistration)catalog.GetType().GetField("writer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(catalog)!;
        Assert.Equal(registration.Drain!.WriterIdentity, manifest.Writer);
        Assert.Equal(registration.Writes.Count, manifest.Payloads.Length);
        Assert.Equal(registration.Markers.Count, manifest.Payloads.Length);
        var bytes = File.ReadAllBytes(LocalOperationalLog.LogFilePath);
        void VerifyRange(LocalFrontierFileManifest.Range range)
            => Assert.Equal(range.RawHash, SHA256.HashData(bytes.AsSpan(checked((int)range.Offset), checked((int)range.Length))));
        VerifyRange(manifest.Drain);
        for (var i = 0; i < manifest.Payloads.Length; i++)
        {
            var payload = manifest.Payloads[i];
            var original = (byte[])contribution!.GetType().GetMethod("CopyPayload")!.Invoke(contribution, [i])!;
            Assert.Equal(SHA256.HashData(original), payload.Hash);
            using var json = System.Text.Json.JsonDocument.Parse(original);
            Assert.Equal(json.RootElement.GetProperty("ObservationId").GetGuid(), payload.Observation);
            VerifyRange(payload.Completion); VerifyRange(payload.Marker);
        }
        foreach (var name in new[] { "LiveProofEligible", "NativeWriterCustodyEstablished", "PublicationCompletionIssued" })
        { Assert.False((bool)contribution!.GetType().GetProperty(name)!.GetValue(contribution)!); }
    }
    public bool TakeAck() => handoff.TryTakeAcknowledgment();
    public void WriteRecoveryMarkers(string mode)
    {
        // Controlled marker text from a genuine persisted drain tuple. This
        // fixture does not claim that the real writer experienced a retry.
        if (mode == "no-markers") { return; }
        var channel = typeof(LocalFrontierAppRequest.FileHandoff).GetField("channel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(handoff)!;
        var encoded = (byte[])channel.GetType().GetField("body", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(channel)!;
        var manifest = LocalFrontierFileManifest.Decode(encoded)!;
        var fault = ordinal == 0 ? mode : "valid";
        var range = manifest.Drain;
        var hash = fault == "hash" ? new string('0', 64) : Convert.ToHexString(range.BatchHash).ToLowerInvariant();
        var generation = range.Generation + (fault == "generation" ? 1 : 0);
        var writer = fault == "wrong-writer" ? "foreign-writer" : manifest.Writer;
        var run = fault == "wrong-run" ? "foreign-run" : "frontier-gui-run";
        string Marker(string kind, long ticks) => FormattableString.Invariant($"event=operational_log_write_{kind}; acceptance_run_id={run}; scenario_run_id=frontier-gui-scenario; log_writer_id={writer}; batch_generation={generation}; first_sequence={range.First}; last_sequence={range.Last}; line_count={range.Last - range.First + 1}; payload_sha256={hash}; attempt_utc_ticks={ticks}; attempt_count=1; failure_code=none");
        var deferred = Marker("deferred", range.AttemptTicks);
        var recovered = Marker("recovered", range.AttemptTicks + (fault == "ticks" ? 1 : 0));
        if (fault == "duplicate-field") { deferred += "; failure_code=other"; }
        if (fault == "terminal") { Console.Error.WriteLine("event=operational_log_write_failed; failure_code=io"); return; }
        if (fault == "invalid-utf8")
        { var output = Console.OpenStandardError(); output.Write(new byte[] { 0xc3, 0x28 }); output.Flush(); return; }
        if (fault != "orphan") { Console.Error.WriteLine(deferred); }
        if (fault == "duplicate") { Console.Error.WriteLine(deferred); }
        if (fault != "missing") { Console.Error.WriteLine(recovered); }
    }
    public void LateWrite() => LocalOperationalLog.Info("GenuineFixture", "event=late_write_attempt");
    public void CancelCatalog() => ((IDisposable)catalog).Dispose();
    public void Dispose() { ((IDisposable)catalog).Dispose(); handoff.Dispose(); }
}

public sealed partial class SessionFileTransferV4SenderTests
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task<object> PrepareGenuineFrontierSubmissionAsync(object catalog, string variant, int ordinal,
        bool deferFinalization = false, bool serviceAdmission = false, string serviceVariant = "before_recheck",
        bool productionShutdown = false, Action<object>? retainEmitter = null)
    {
        var writerOnly = ordinal == 1 && variant != "both_positive";
        if (writerOnly)
        {
            if (deferFinalization)
            {
                LocalOperationalLog.Info("GenuineFixture", "event=writer_only_contribution");
                return catalog;
            }
            var empty = catalog.GetType().GetMethod("TryTakeWriterOnly")!.Invoke(catalog, null);
            Assert.NotNull(empty);
            Assert.Null(catalog.GetType().GetMethod("TryTakeWriterOnly")!.Invoke(catalog, null));
            Assert.Null(CatalogAdmit(catalog, out _));
            Assert.Null(CatalogTake(catalog, out _));
            LocalOperationalLog.Info("GenuineFixture", "event=writer_only_contribution");
            if (variant != "early_submit") { Assert.True(LocalOperationalLog.CompleteAndFlush(TimeSpan.FromSeconds(5))); }
            return empty;
        }
        var observations = new List<object>();
        if (variant == "witness_rows")
        {
            for (var i = 0; i < 65; i++) { LocalOperationalLog.Info("GenuineFixture", $"event=frontier_witness_fixture; ordinal={i}"); }
        }
        var count = variant is "two_payloads" or "payload_fault" ? 2 : 1;
        for (var i = 0; i < count; i++)
        {
            string reason = "";
            var observation = serviceAdmission ? null : CatalogAdmit(catalog, out reason)!;
            if (!serviceAdmission)
            {
                Assert.NotNull(observation); observations.Add(observation);
                Assert.Null(catalog.GetType().GetMethod("TryTakeWriterOnly")!.Invoke(catalog, null));
            }
            object? emitter = null;
            CancellationTokenSource? fixtureLifetime = null;
            await RunFrontierBoundaryAsync(ordinal == 0 ? "v6" : "v4",
                serviceAdmission && serviceVariant != "reset" ? serviceVariant : "before_recheck", true,
                suppliedObserver: observation, beforeCapture: context =>
                {
                    fixtureLifetime = GetPrivateProperty<CancellationTokenSource>(context, "LifetimeCts");
                    emitter = GetPrivatePropertyValue(context, "CoordinatorSnapshotEmitter")!;
                    if (productionShutdown && retainEmitter is null) { return; }
                    var field = emitter.GetType().GetField("drainScheduled", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    Assert.True(SpinWait.SpinUntil(() => (int)field.GetValue(emitter)! == 0, TimeSpan.FromSeconds(5)));
                    field.SetValue(emitter, 1);
                    retainEmitter?.Invoke(emitter);
                }, afterCaptureClosed: current =>
                {
                    if (serviceAdmission)
                    {
                        Assert.Same(catalog, BoundaryField(current, "payloadCatalog"));
                        observations.Add(current);
                        Assert.Equal(i + 1, (int)catalog.GetType().GetProperty("AdmissionCount")!.GetValue(catalog)!);
                    }
                    if (!deferFinalization) { Assert.True(CatalogPrepare(catalog, current, out reason), reason); }
                    if (!productionShutdown)
                    { emitter!.GetType().GetMethod("Drain", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(emitter, null); }
                    // This manually constructed test context owns delayed grace
                    // wakes. Retire that test lifetime before declaring the
                    // process writer drained; do not let synthetic callbacks
                    // become post-drain writes during the other app's work.
                    fixtureLifetime!.Cancel();
                }, originalPrimaryFailure: variant is "primary_failure" or "primary_overflow",
                additionalWitnessWrites: variant == "primary_overflow" ? 42 : 0, appOwnedObservation: serviceAdmission,
                resetAfterCapture: serviceAdmission && serviceVariant == "reset", waitForLogEvidence: !productionShutdown);
        }
        // The app-lifetime fixture leaves preparation, markers and the only
        // final drain to the original production owner under test.
        if (deferFinalization) { return catalog; }
        var snapshot = CatalogTake(catalog, out var failure)!;
        Assert.NotNull(snapshot);
        if (variant != "missing_marker")
        { Assert.True((bool)snapshot.GetType().GetMethod("TrySubmitMarkers")!.Invoke(snapshot, null)!); }
        if (variant != "early_submit") { Assert.True(LocalOperationalLog.CompleteAndFlush(TimeSpan.FromSeconds(5))); }
        if (variant == "payload_fault")
        {
            var prepared = BoundaryField(observations[1], "preparedPayload")!;
            ((byte[])BoundaryField(prepared, "payload")!)[0] ^= 1;
        }
        return snapshot;
    }
}
