using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NLink.Core.Logging;

// Provisional file primitive only. It preserves a native source while other
// writers append; it proves neither original writer ownership nor immutable bytes.
// The catalog/writer issuer binds it to exact receipts; GUI final sealing remains
// separate and must wait for every original writer and output-completion gate.
internal sealed class OperationalLogNativeAnchor : IDisposable
{
    private readonly object gate = new();
    private readonly OperationalLogNativeAnchor original;
    private readonly SafeFileHandle anchor;
    private bool disposed;
    internal string FileIdentity { get; }
    internal long CapturedByteCount { get; }

    private OperationalLogNativeAnchor(SafeFileHandle anchor, string identity, long bytes)
    { original = this; this.anchor = anchor; FileIdentity = identity; CapturedByteCount = bytes; }

    internal static OperationalLogNativeAnchor? TryRetain(SafeFileHandle source)
    {
        if (!OperatingSystem.IsWindows()) { return null; }
        SafeFileHandle? retained = null;
        try
        {
            if (!Describe(source, out var identity, out var bytes)) { return null; }
            // Native handle reopen, not pathname discovery. Read-only access with
            // read/write/delete sharing permits remaining appends and rotation.
            retained = OperationalLogNativeSource.Reopen(source, share: 7);
            if (!Describe(retained, out var current, out var count) || current != identity || count < bytes) { return null; }
            var result = new OperationalLogNativeAnchor(retained, identity!, bytes);
            retained = null;
            return result;
        }
        catch { return null; }
        finally { retained?.Dispose(); }
    }

    internal bool IsRetained
    {
        get
        {
            lock (gate)
            {
                if (!ReferenceEquals(original, this) || disposed) { return false; }
                if (Matches(anchor)) { return true; }
                Dispose(); return false;
            }
        }
    }

    internal SafeFileHandle? TryGetSubmissionHandle()
    { lock (gate) { return IsRetained ? anchor : null; } }

    // These bytes remain mutable. An already-open reader is not continuing
    // receipt/catalog custody and must not be used as a final source snapshot.
    internal FileStream? TryOpenProvisionalRead()
    {
        lock (gate)
        {
            if (!IsRetained) { return null; }
            SafeFileHandle? read = null;
            FileStream? stream = null;
            bool opened = false;
            try
            {
                read = OperationalLogNativeSource.Reopen(anchor, share: 7);
                if (!Matches(read)) { return null; }
                stream = new FileStream(read, FileAccess.Read);
                read = null;
                if (!IsRetained) { return null; }
                var result = stream; stream = null; opened = true;
                return result;
            }
            catch { return null; }
            finally { stream?.Dispose(); read?.Dispose(); if (!opened) { Dispose(); } }
        }
    }

    private bool Matches(SafeFileHandle handle)
        => Describe(handle, out var identity, out var bytes) && identity == FileIdentity && bytes >= CapturedByteCount;

    private static bool Describe(SafeFileHandle handle, out string? identity, out long bytes)
    {
        identity = null; bytes = 0;
        try
        {
            return !handle.IsClosed && !handle.IsInvalid && GetFileType(handle) == 1 &&
                OperationalLogNativeSource.TryDescribe(handle, out identity, out bytes, out var attributes) &&
                (attributes & FileAttributes.Directory) == 0 && bytes is > 0 and <= OperationalLogNativeSource.MaximumBytes;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (!ReferenceEquals(original, this) || disposed) { return; }
            disposed = true;
            anchor.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle handle);
}
