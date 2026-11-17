using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

// A file primitive, not a producer/custody capability. Only the original catalog
// can associate one of these files with its privately owned prepared payload.
internal sealed class FrontierNativePayloadFile : IDisposable
{
    internal const int MaximumBytes = 262144;
    private const uint Read = 0x80000000, Write = 0x40000000, Delete = 0x00010000;
    private readonly object gate = new();
    private readonly FileStream anchor;
    private bool disposed;
    internal string FileIdentity { get; }
    internal int ByteCount { get; }
    internal string Sha256 { get; }

    private FrontierNativePayloadFile(FileStream anchor, string identity, int count, string hash)
    { this.anchor = anchor; FileIdentity = identity; ByteCount = count; Sha256 = hash; }

    internal static FrontierNativePayloadFile? TryCreate(string directory, byte[] bytes)
    {
        if (!OperatingSystem.IsWindows() || bytes.Length is < 1 or > MaximumBytes) { return null; }
        SafeFileHandle? created = null, bridge = null, frozen = null;
        FileStream? writer = null, reader = null;
        try
        {
            var path = Path.Combine(directory, "frontier-payload-" + Guid.NewGuid().ToString("N") + ".json");
            // CREATE_NEW never overwrites an existing path. DELETE_ON_CLOSE is
            // present from creation, including failures and abrupt process exit.
            created = CreateFile(path, Read | Write | Delete, 5, IntPtr.Zero, 1, 0x0c000100, IntPtr.Zero);
            if (created.IsInvalid) { return null; }
            writer = new FileStream(created, FileAccess.ReadWrite);
            writer.Write(bytes);
            writer.Flush();
            if (!OperationalLogNativeSource.TryDescribe(created, out var identity, out var length) || length != bytes.Length)
            { return null; }
            bridge = ReOpenFile(created, Read | Delete, 7, 0x08000000);
            if (bridge.IsInvalid) { return null; }
            writer.Dispose(); writer = null;
            // DELETE_ON_CLOSE also retires the owned file on abrupt app exit.
            frozen = ReOpenFile(bridge, Read | Delete, 5, 0x0c000000);
            if (frozen.IsInvalid || !OperationalLogNativeSource.TryDescribe(frozen, out var current, out length) ||
                current != identity || length != bytes.Length) { return null; }
            reader = new FileStream(frozen, FileAccess.Read);
            var expected = SHA256.HashData(bytes);
            if (!SHA256.HashData(reader).AsSpan().SequenceEqual(expected)) { return null; }
            var result = new FrontierNativePayloadFile(reader, identity!, bytes.Length, Convert.ToHexString(expected).ToLowerInvariant());
            reader = null; frozen = null;
            return result;
        }
        catch { return null; }
        finally
        {
            reader?.Dispose(); frozen?.Dispose(); bridge?.Dispose(); writer?.Dispose(); created?.Dispose();
        }
    }

    internal bool IsCurrent
    {
        get
        {
            lock (gate)
            {
                return !disposed && OperationalLogNativeSource.TryDescribe(anchor.SafeFileHandle, out var identity, out var count) &&
                    identity == FileIdentity && count == ByteCount;
            }
        }
    }

    internal FileStream? TryOpenRead()
    {
        lock (gate)
        {
            if (!IsCurrent) { return null; }
            SafeFileHandle? read = null;
            try
            {
                read = ReOpenFile(anchor.SafeFileHandle, Read, 5, 0x08000000);
                if (read.IsInvalid) { return null; }
                var result = new FileStream(read, FileAccess.Read);
                read = null; return result;
            }
            catch { return null; }
            finally { read?.Dispose(); }
        }
    }

    internal SafeFileHandle? TryGetSubmissionHandle()
    { lock (gate) { return IsCurrent ? anchor.SafeFileHandle : null; } }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) { return; }
            disposed = true;
            anchor.Dispose();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle ReOpenFile(SafeFileHandle handle, uint access, uint share, uint flags);
}
