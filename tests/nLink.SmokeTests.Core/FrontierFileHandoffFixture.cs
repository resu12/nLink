using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Tools;

namespace NLink.SmokeTests;

// Original Core catalog/lease binding, but deliberately synthetic file/receipt
// content. Only protocol ownership is tested here, never producer proof.
public sealed class FrontierFileHandoffFixture : IDisposable
{
    private readonly object catalog;
    private readonly LocalFrontierAppRequest.AppLease lease;
    private readonly LocalFrontierAppRequest.FileHandoff? handoff;
    private readonly FrontierFileAdoptionFixture? files;
    public bool Attached => handoff is not null;
    public bool IsCurrent => handoff?.IsCurrent == true;
    public bool FilesCurrent => files?.IsCurrent == true;
    public bool Acknowledged => handoff?.Acknowledged == true;
    private FrontierFileHandoffFixture(LocalFrontierAppRequest.AppLease lease, object catalog,
        LocalFrontierAppRequest.FileHandoff? handoff, FrontierFileAdoptionFixture? files)
    { this.lease = lease; this.catalog = catalog; this.handoff = handoff; this.files = files; }
    public static FrontierFileHandoffFixture Create(LocalFrontierAppRequest.AppLease lease, string directory, int ordinal, int count)
    {
        var type = typeof(SessionFileTransferService).GetNestedType("FrontierPayloadCatalog", BindingFlags.NonPublic)!;
        var catalog = type.GetMethod("TryCreateNative")!.Invoke(null, [lease])!;
        if (catalog is null) { throw new InvalidOperationException("Original native catalog unavailable"); }
        var handoff = (LocalFrontierAppRequest.FileHandoff?)type.GetMethod("TryAttachFileHandoff")!.Invoke(catalog, null);
        var files = handoff is null ? null : FrontierFileAdoptionFixture.Create(directory, handoff.GuiRequest, lease.RequestId,
            count, ordinal, count == 16 ? LocalFrontierFileManifest.MaximumPayloadBytes : 0);
        return new(lease, catalog, handoff, files);
    }
    public bool RetryAttach() => catalog.GetType().GetMethod("TryAttachFileHandoff")!.Invoke(catalog, null) is not null;
    public bool TryBindWriter() => (bool)catalog.GetType().GetMethod("TryBindOperationalWriter")!.Invoke(catalog, null)!;
    public bool Submit() => handoff?.TrySubmit(Convert.FromBase64String(files!.Encoded), files.NativeHandles) == true;
    public bool TakeAck() => handoff?.TryTakeAcknowledgment() == true;
    public void CancelCatalog() => ((IDisposable)catalog).Dispose();
    public bool CancelWhileAcknowledgmentChecksCatalog()
    {
        var catalogGate = catalog.GetType().GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(catalog)!;
        var handoffGate = typeof(LocalFrontierAppRequest.FileHandoff).GetField("sync", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(handoff)!;
        Task<bool> pending;
        Monitor.Enter(catalogGate);
        try
        {
            pending = Task.Run(() => handoff!.TryTakeAcknowledgment());
            var blocked = SpinWait.SpinUntil(() =>
            {
                if (!Monitor.TryEnter(handoffGate)) { return true; }
                Monitor.Exit(handoffGate); return false;
            }, TimeSpan.FromSeconds(5));
            if (!blocked) { throw new InvalidOperationException("Acknowledgment did not enter its catalog check"); }
            // The current thread owns the catalog gate; cancellation must not
            // wait for the handoff gate held by the blocked acknowledgment.
            CancelCatalog();
        }
        finally { Monitor.Exit(catalogGate); }
        return pending.Wait(TimeSpan.FromSeconds(5)) && !pending.Result && !IsCurrent;
    }
    public void CheckCopies()
    {
        static object Copy(object value) => typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(value, null)!;
        var copiedLease = (LocalFrontierAppRequest.AppLease)Copy(lease);
        if (copiedLease.IsCurrent || copiedLease.CompleteAttachment()) { throw new InvalidOperationException("Copied app lease accepted"); }
        copiedLease.Dispose();
        var copiedHandoff = (LocalFrontierAppRequest.FileHandoff)Copy(handoff!);
        if (copiedHandoff.IsCurrent || copiedHandoff.TrySubmit(Convert.FromBase64String(files!.Encoded), files.NativeHandles) || copiedHandoff.TryTakeAcknowledgment())
        { throw new InvalidOperationException("Copied handoff accepted"); }
        copiedHandoff.Cancel(); copiedHandoff.Dispose();
        var copiedCatalog = Copy(catalog);
        if (copiedCatalog.GetType().GetMethod("TryAttachFileHandoff")!.Invoke(copiedCatalog, null) is not null)
        { throw new InvalidOperationException("Copied catalog imported a channel"); }
        ((IDisposable)copiedCatalog).Dispose();
        if (!lease.IsCurrent || !IsCurrent) { throw new InvalidOperationException("Copied owner retired original"); }
    }
    public void Dispose()
    {
        ((IDisposable)catalog).Dispose(); handoff?.Dispose(); files?.Dispose();
    }
}
