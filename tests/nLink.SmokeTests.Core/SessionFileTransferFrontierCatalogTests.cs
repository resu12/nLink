using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("v6", "positive")]
    [InlineData("v4", "positive")]
    [InlineData("v6", "successors")]
    [InlineData("v4", "successors")]
    [InlineData("v6", "before_recheck")]
    [InlineData("v4", "before_recheck")]
    [Trait("FileTransferRuntimeSuite", "FrontierPayloadCatalog")]
    public async Task FrontierCatalog_AdmittedProducerRetainsExactPayloadAndRevocableOwnership(string path, string variant)
    {
        using var catalog = NewFrontierCatalog();
        var observer = CatalogAdmit(catalog, out var reason);
        Assert.NotNull(observer);
        Assert.Equal("", reason);
        Assert.Equal(0, GetPrivateProperty<int>(observer!, "EntryCount"));
        object? prepared = null;
        var fixture = await RunFrontierBoundaryAsync(path, variant, true, afterCaptureClosed: current =>
        {
            Assert.Same(observer, current);
            Assert.Null(PrepareBoundaryPayload(current, out reason));
            Assert.Equal("catalog_preparation_owner", reason);
            Assert.Null(BoundaryField(current, "preparedPayload"));
            using var wrongOwner = NewFrontierCatalog();
            object?[] wrongArgs = [wrongOwner, null];
            Assert.Null(current.GetType().GetMethod("TryPrepareCatalogPayload")!.Invoke(current, wrongArgs));
            Assert.Equal("catalog_preparation_owner", wrongArgs[1]);
            Assert.True(CatalogPrepare(catalog, current, out reason), reason);
            prepared = BoundaryField(current, "preparedPayload");
            Assert.NotNull(prepared);
        }, suppliedObserver: observer);
        Assert.False(CatalogPrepare(catalog, observer!, out reason));
        Assert.Equal("preparation_attempted", reason);
        Assert.Equal(1, GetPrivateProperty<int>(catalog, "PreparationAttempts"));
        var snapshot = CatalogTake(catalog, out reason);
        Assert.NotNull(snapshot);
        Assert.Equal("", reason);
        Assert.Equal(1, GetPrivateProperty<int>(snapshot!, "Count"));
        Assert.True(CatalogOwns(catalog, snapshot));
        Assert.True(GetPrivateProperty<bool>(snapshot!, "IsCurrent"));
        Assert.Empty(snapshot!.GetType().GetConstructors());
        Assert.All(snapshot.GetType().GetProperties(), p => Assert.Null(p.GetSetMethod()));
        var bytes = CatalogCopy(snapshot, 0)!;
        Assert.Equal(CopyBoundaryPayload(prepared!), bytes);
        Assert.Equal(bytes.Length, GetPrivateProperty<int>(snapshot, "ByteCount"));
        Assert.Equal(GetPrivateProperty<string>(prepared!, "Sha256"), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal((Guid)BoundaryField(observer!, "observationId")!, json.RootElement.GetProperty("ObservationId").GetGuid());
        Assert.All(new[] { "LiveProofEligible", "NativeWriterCustodyEstablished", "PublicationCompletionIssued" },
            flag => Assert.False(GetPrivateProperty<bool>(snapshot, flag)));
        bytes[0] ^= 1;
        Assert.Equal(CopyBoundaryPayload(prepared!), CatalogCopy(snapshot, 0));
        Assert.Null(CatalogCopy(snapshot, -1));
        Assert.Null(CatalogCopy(snapshot, 1));
        Assert.Null(CatalogTake(catalog, out reason));
        Assert.Equal("catalog_taken", reason);
        Assert.Null(CatalogAdmit(catalog, out reason));
        using var foreign = NewFrontierCatalog();
        Assert.False(CatalogOwns(foreign, snapshot));
        var forged = Activator.CreateInstance(snapshot.GetType(), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [catalog, 1, bytes.Length], culture: null)!;
        Assert.False(CatalogOwns(catalog, forged));
        Assert.Null(CatalogCopy(forged, 0));
        Assert.True(CatalogOwns(catalog, snapshot));
        var clockSequence = BoundaryField(observer!, "sequence");
        var timing = GetPrivatePropertyValue(observer!, "Timing");
        CatalogCancel(catalog);
        Assert.False(CatalogOwns(catalog, snapshot));
        Assert.False(GetPrivateProperty<bool>(snapshot, "IsCurrent"));
        Assert.Null(CatalogCopy(snapshot, 0));
        Assert.Equal(0, GetPrivateProperty<int>(catalog, "RetainedByteCount"));
        Assert.Equal(1, GetPrivateProperty<int>(catalog, "AdmissionCount"));
        Assert.Same(timing, GetPrivatePropertyValue(fixture.Observer, "Timing"));
        Assert.Equal(clockSequence, BoundaryField(fixture.Observer, "sequence"));
        Assert.True(OwnsBoundaryPayload(observer!, prepared)); // cancellation did not mutate the producer
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPayloadCatalog")]
    public void FrontierCatalog_LifetimeAdmissionCapCannotRecycleOrSelectLaterSuccess()
    {
        using var catalog = NewFrontierCatalog();
        var admitted = Enumerable.Range(0, 16).Select(index => CatalogAdmit(catalog, out _)).ToArray();
        Assert.All(admitted, Assert.NotNull);
        Assert.Equal(16, GetPrivateProperty<int>(catalog, "AdmissionCount"));
        Assert.Equal(16, admitted.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Null(CatalogAdmit(catalog, out var reason));
        Assert.Equal("admission_capacity", reason);
        Assert.Null(CatalogTake(catalog, out reason));
        Assert.False(CatalogPrepare(catalog, admitted[0]!, out reason));
        Assert.Equal(0, GetPrivateProperty<int>(catalog, "PreparationAttempts"));
        Assert.Equal(16, GetPrivateProperty<int>(catalog, "AdmissionCount"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPayloadCatalog")]
    public void FrontierCatalog_ForeignProducerCannotConsumeAnOwnedAttempt()
    {
        using var catalog = NewFrontierCatalog();
        using var foreign = NewFrontierCatalog();
        var owned = CatalogAdmit(catalog, out _)!;
        var other = CatalogAdmit(foreign, out _)!;
        Assert.False(CatalogPrepare(catalog, other, out var reason));
        Assert.Equal("foreign_producer", reason);
        Assert.Equal(0, GetPrivateProperty<int>(catalog, "PreparationAttempts"));
        Assert.Equal("", GetPrivateProperty<string>(catalog, "FailureReason"));
        Assert.False(CatalogPrepare(catalog, owned, out reason));
        Assert.Equal("producer_not_closed_positive", reason);
        Assert.Equal(1, GetPrivateProperty<int>(catalog, "PreparationAttempts"));
        Assert.Null(CatalogAdmit(catalog, out _));
        Assert.Null(CatalogTake(catalog, out _));
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("partial")]
    [InlineData("cancel")]
    [Trait("FileTransferRuntimeSuite", "FrontierPayloadCatalog")]
    public async Task FrontierCatalog_MultipleAdmissionsPreserveOrderAndNeverPublishAPartialCatalog(string variant)
    {
        using var catalog = NewFrontierCatalog();
        var first = CatalogAdmit(catalog, out _)!;
        var second = CatalogAdmit(catalog, out _)!;
        await RunFrontierBoundaryAsync("v6", "positive", true, suppliedObserver: first);
        Assert.True(CatalogPrepare(catalog, first, out var reason), reason);
        Assert.True(GetPrivateProperty<int>(catalog, "RetainedByteCount") > 0);
        if (variant == "complete")
        {
            await RunFrontierBoundaryAsync("v4", "before_recheck", true, suppliedObserver: second);
            Assert.True(CatalogPrepare(catalog, second, out reason), reason);
        }
        if (variant == "cancel") { CatalogCancel(catalog); }
        var snapshot = CatalogTake(catalog, out reason);
        if (variant != "complete")
        {
            Assert.Null(snapshot);
            Assert.Equal(variant == "cancel" ? "catalog_cancelled" : "catalog_incomplete", reason);
            Assert.Equal(0, GetPrivateProperty<int>(catalog, "RetainedByteCount"));
            Assert.Null(CatalogAdmit(catalog, out _));
            return;
        }
        Assert.NotNull(snapshot);
        Assert.Equal(2, GetPrivateProperty<int>(snapshot!, "Count"));
        var totalBytes = 0;
        foreach (var (observer, index) in new[] { (first, 0), (second, 1) })
        {
            var expected = CopyBoundaryPayload(BoundaryField(observer, "preparedPayload")!);
            var actual = CatalogCopy(snapshot!, index)!;
            Assert.Equal(expected, actual);
            using var json = JsonDocument.Parse(actual);
            Assert.Equal((Guid)BoundaryField(observer, "observationId")!, json.RootElement.GetProperty("ObservationId").GetGuid());
            totalBytes += actual.Length;
        }
        Assert.Equal(totalBytes, GetPrivateProperty<int>(snapshot!, "ByteCount"));
        Assert.Equal(totalBytes, GetPrivateProperty<int>(catalog, "RetainedByteCount"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPayloadCatalog")]
    public void FrontierCatalog_ExhaustedPreparationBudgetRejectsNewAdmission()
    {
        using var catalog = NewFrontierCatalog();
        FrontierCatalogType.GetField("preparationTime", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(catalog, TimeSpan.FromSeconds(2));
        Assert.Null(CatalogAdmit(catalog, out var reason));
        Assert.Equal("preparation_budget", reason);
        Assert.Equal(0, GetPrivateProperty<int>(catalog, "AdmissionCount"));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("rejected")]
    [Trait("FileTransferRuntimeSuite", "FrontierPayloadCatalog")]
    public async Task FrontierCatalog_NonPositiveAdmissionInvalidatesWholeCatalog(string variant)
    {
        using var catalog = NewFrontierCatalog();
        var observer = CatalogAdmit(catalog, out _)!;
        await RunFrontierBoundaryAsync("v6", variant, true, suppliedObserver: observer);
        Assert.False(CatalogPrepare(catalog, observer, out var reason));
        Assert.Equal("producer_not_closed_positive", reason);
        Assert.Null(CatalogTake(catalog, out _));
        Assert.Null(CatalogAdmit(catalog, out _));
        Assert.Equal(0, GetPrivateProperty<int>(catalog, "RetainedByteCount"));
    }

    [Theory]
    [InlineData("early_take")]
    [InlineData("cancel")]
    [InlineData("dispose")]
    [InlineData("budget_exhausted")]
    [Trait("FileTransferRuntimeSuite", "FrontierPayloadCatalog")]
    public void FrontierCatalog_ClosedOrExhaustedCatalogNeverStartsPreparation(string variant)
    {
        using var catalog = NewFrontierCatalog();
        var observer = CatalogAdmit(catalog, out _)!;
        if (variant == "early_take") { Assert.Null(CatalogTake(catalog, out _)); }
        if (variant == "cancel") { CatalogCancel(catalog); }
        if (variant == "dispose") { catalog.Dispose(); }
        if (variant == "budget_exhausted")
        { FrontierCatalogType.GetField("preparationTime", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(catalog, TimeSpan.FromSeconds(2)); }
        Assert.False(CatalogPrepare(catalog, observer, out var reason));
        Assert.Equal(variant switch { "early_take" => "catalog_incomplete", "budget_exhausted" => "preparation_budget", _ => "catalog_cancelled" }, reason);
        Assert.Equal(0, GetPrivateProperty<int>(catalog, "PreparationAttempts"));
        Assert.Null(BoundaryField(observer, "preparedPayload"));
        Assert.Null(CatalogTake(catalog, out _));
        Assert.Null(CatalogAdmit(catalog, out _));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("early_take")]
    [InlineData("overlap")]
    [Trait("FileTransferRuntimeSuite", "FrontierPayloadCatalog")]
    public async Task FrontierCatalog_InFlightPreparationCannotPublishAfterInvalidation(string variant)
    {
        using var catalog = NewFrontierCatalog();
        var observer = CatalogAdmit(catalog, out _)!;
        var other = variant == "overlap" ? CatalogAdmit(catalog, out _)! : null;
        await RunFrontierBoundaryAsync("v6", "positive", true, suppliedObserver: observer);
        var payloadGate = BoundaryField(observer, "payloadGate")!;
        Task<bool> work;
        Monitor.Enter(payloadGate);
        try
        {
            work = Task.Run(() => CatalogPrepare(catalog, observer, out _));
            Assert.True(SpinWait.SpinUntil(() => GetPrivateProperty<int>(catalog, "PreparationAttempts") == 1, TimeSpan.FromSeconds(5)));
            if (variant == "cancel") { CatalogCancel(catalog); }
            if (variant == "early_take") { Assert.Null(CatalogTake(catalog, out _)); }
            if (variant == "overlap")
            { Assert.False(CatalogPrepare(catalog, other!, out var reason)); Assert.Equal("preparation_busy", reason); }
        }
        finally { Monitor.Exit(payloadGate); }
        Assert.False(await work);
        Assert.Null(CatalogTake(catalog, out _));
        Assert.Equal(1, GetPrivateProperty<int>(catalog, "PreparationAttempts"));
        Assert.Equal(0, GetPrivateProperty<int>(catalog, "RetainedByteCount"));
    }

    [Theory]
    [InlineData(0, 262144, true)]
    [InlineData(3932160, 262144, true)]
    [InlineData(3932161, 262144, false)]
    [InlineData(0, 262145, false)]
    [InlineData(0, 0, false)]
    [Trait("FileTransferRuntimeSuite", "FrontierPayloadCatalog")]
    public void FrontierCatalog_PayloadAccountingEnforcesExactByteLimits(int retained, int next, bool expected)
        => Assert.Equal(expected, (bool)FrontierCatalogType.GetMethod("FitsPayloadBytes", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [retained, next])!);

    [Theory]
    [InlineData(0, 20000000, true)]
    [InlineData(10000000, 10000000, true)]
    [InlineData(10000000, 10000001, false)]
    [InlineData(0, -1, false)]
    [Trait("FileTransferRuntimeSuite", "FrontierPayloadCatalog")]
    public void FrontierCatalog_PreparationAccountingUsesExactCumulativeCutoff(long used, long elapsed, bool expected)
    {
        object?[] args = [TimeSpan.FromTicks(used), TimeSpan.FromTicks(elapsed), null];
        Assert.Equal(expected, (bool)FrontierCatalogType.GetMethod("TryChargePreparation", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!);
        if (expected) { Assert.Equal(TimeSpan.FromTicks(used + elapsed), args[2]); }
    }

    private static Type FrontierCatalogType => typeof(SessionFileTransferService).GetNestedType("FrontierPayloadCatalog", BindingFlags.NonPublic)!;
    private static IDisposable NewFrontierCatalog() => (IDisposable)Activator.CreateInstance(FrontierCatalogType, true)!;
    private static object? CatalogAdmit(object catalog, out string reason)
    {
        object?[] args = [null];
        var result = FrontierCatalogType.GetMethod("TryAdmit")!.Invoke(catalog, args);
        reason = (string)args[0]!;
        return result;
    }
    private static bool CatalogPrepare(object catalog, object observer, out string reason)
    {
        object?[] args = [observer, null];
        var result = (bool)FrontierCatalogType.GetMethod("TryPrepare")!.Invoke(catalog, args)!;
        reason = (string)args[1]!;
        return result;
    }
    private static object? CatalogTake(object catalog, out string reason)
    {
        object?[] args = [null];
        var result = FrontierCatalogType.GetMethod("TryTake")!.Invoke(catalog, args);
        reason = (string)args[0]!;
        return result;
    }
    private static void CatalogCancel(object catalog) => FrontierCatalogType.GetMethod("Cancel")!.Invoke(catalog, null);
    private static bool CatalogOwns(object catalog, object? snapshot) => (bool)FrontierCatalogType.GetMethod("Owns")!.Invoke(catalog, [snapshot])!;
    private static byte[]? CatalogCopy(object snapshot, int index) => (byte[]?)snapshot.GetType().GetMethod("CopyPayload")!.Invoke(snapshot, [index]);
}
