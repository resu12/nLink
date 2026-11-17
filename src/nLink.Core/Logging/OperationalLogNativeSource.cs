using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NLink.Core.Logging;

// These scalars are diagnostics. Only the pump's original receipt registry can
// bind them to a submission; only the retained drain handle can supply reads.
internal sealed record OperationalLogNativeSpan(string FileIdentity, long Offset, long ByteCount, string Sha256);

internal sealed class OperationalLogNativeAppend(string identity, long offset, OperationalLogNativeSource? source,
    OperationalLogNativeAnchor? provisional = null)
{
    internal string FileIdentity { get; } = identity;
    internal long Offset { get; } = offset;
    internal OperationalLogNativeSource? Source { get; } = source;
    internal OperationalLogNativeAnchor? Provisional { get; } = provisional;

    internal static OperationalLogNativeAppend? Write(string path, string text, bool freeze, bool retainProvisional = false)
    {
        SafeFileHandle? anchor = null;
        OperationalLogNativeAnchor? provisional = null;
        string? identity = null;
        long offset = 0, length = 0;
        try
        {
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                offset = stream.Position + (stream.Length == 0 ? Encoding.UTF8.Preamble.Length : 0);
                using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, leaveOpen: true))
                { writer.Write(text); writer.Write(Environment.NewLine); }
                // Native observation failure must not change a successful log write.
                try
                {
                    if (OperationalLogNativeSource.TryDescribe(stream.SafeFileHandle, out identity, out length) && freeze)
                    { anchor = OperationalLogNativeSource.Reopen(stream.SafeFileHandle, share: 3); }
                    if (identity is not null && retainProvisional && !freeze)
                    { provisional = OperationalLogNativeAnchor.TryRetain(stream.SafeFileHandle); }
                }
                catch { }
            }
            if (identity is null) { return null; }
            OperationalLogNativeSource? source = null;
            if (freeze && anchor is { IsInvalid: false })
            { source = OperationalLogNativeSource.TryFreeze(anchor, identity, length); }
            var result = new OperationalLogNativeAppend(identity, offset, source, provisional);
            provisional = null;
            return result;
        }
        finally { anchor?.Dispose(); provisional?.Dispose(); }
    }
}

internal sealed class OperationalLogNativeSource : IDisposable
{
    internal const long MaximumBytes = 268435456;
    internal static readonly TimeSpan MaximumVerificationTime = TimeSpan.FromSeconds(5);
    private readonly object gate = new();
    private readonly FileStream anchor;
    private bool attempted, verified, disposed;
    internal string FileIdentity { get; }
    internal long ByteCount { get; }
    internal string? Sha256 { get; private set; }
    private OperationalLogNativeSource(FileStream stream, string identity, long bytes)
    { anchor = stream; FileIdentity = identity; ByteCount = bytes; }

    internal static OperationalLogNativeSource? TryFreeze(SafeFileHandle original, string identity, long bytes)
    {
        SafeFileHandle? handle = null;
        try
        {
            if (bytes is <= 0 or > MaximumBytes) { return null; }
            // Reopen the actual append handle, never rediscover its path.
            handle = Reopen(original, share: 1);
            if (handle.IsInvalid || !TryDescribe(handle, out var current, out var length) || current != identity || length != bytes)
            { return null; }
            var stream = new FileStream(handle, FileAccess.Read);
            handle = null;
            return new(stream, identity, bytes);
        }
        catch { return null; }
        finally { handle?.Dispose(); }
    }

    internal bool IsVerified { get { lock (gate) { return !disposed && verified; } } }

    // One bounded source traversal verifies the complete file and the exact raw
    // byte ranges observed by the writer. It is not another authority evaluation.
    internal bool TryVerify(IReadOnlyList<OperationalLogNativeSpan> spans)
    {
        lock (gate)
        {
            if (attempted || disposed) { return false; }
            attempted = true;
            var hashes = new List<IncrementalHash>();
            try
            {
                if (spans.Count is < 1 or > 33 || spans.Any(s => s.FileIdentity != FileIdentity || s.Offset < 0 ||
                    s.ByteCount <= 0 || s.Offset > ByteCount - s.ByteCount)) { return false; }
                var ordered = spans.OrderBy(s => s.Offset).ToArray();
                for (var i = 1; i < ordered.Length; i++)
                { if (ordered[i].Offset < ordered[i - 1].Offset + ordered[i - 1].ByteCount) { return false; } }
                foreach (var span in ordered) { hashes.Add(IncrementalHash.CreateHash(HashAlgorithmName.SHA256)); }
                using var full = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var clock = Stopwatch.StartNew();
                var buffer = new byte[32768];
                long offset = 0;
                anchor.Position = 0;
                int read;
                while ((read = anchor.Read(buffer)) != 0)
                {
                    if (clock.Elapsed > MaximumVerificationTime || offset > ByteCount - read) { return false; }
                    full.AppendData(buffer, 0, read);
                    for (var i = 0; i < ordered.Length; i++)
                    {
                        var start = Math.Max(offset, ordered[i].Offset);
                        var end = Math.Min(offset + read, ordered[i].Offset + ordered[i].ByteCount);
                        if (end > start) { hashes[i].AppendData(buffer, (int)(start - offset), (int)(end - start)); }
                    }
                    offset += read;
                }
                if (offset != ByteCount || clock.Elapsed > MaximumVerificationTime ||
                    !TryDescribe(anchor.SafeFileHandle, out var identity, out var length) || identity != FileIdentity || length != ByteCount)
                { return false; }
                for (var i = 0; i < ordered.Length; i++)
                { if (Convert.ToHexString(hashes[i].GetHashAndReset()).ToLowerInvariant() != ordered[i].Sha256) { return false; } }
                Sha256 = Convert.ToHexString(full.GetHashAndReset()).ToLowerInvariant();
                return verified = true;
            }
            catch { return false; }
            finally
            {
                foreach (var hash in hashes) { hash.Dispose(); }
                if (!verified) { Dispose(); }
            }
        }
    }

    internal FileStream? TryOpenRead()
    {
        lock (gate)
        {
            if (!IsVerified) { return null; }
            SafeFileHandle? handle = null;
            try
            {
                handle = Reopen(anchor.SafeFileHandle, share: 1);
                if (handle.IsInvalid) { return null; }
                var stream = new FileStream(handle, FileAccess.Read);
                handle = null;
                return stream;
            }
            catch { return null; }
            finally { handle?.Dispose(); }
        }
    }

    public void Dispose() { lock (gate) { disposed = true; verified = false; anchor.Dispose(); } }

    internal static bool TryDescribe(SafeFileHandle handle, out string? identity, out long bytes)
        => TryDescribe(handle, out identity, out bytes, out _);

    internal static bool TryDescribe(SafeFileHandle handle, out string? identity, out long bytes, out FileAttributes attributes)
    {
        identity = null; bytes = 0; attributes = 0;
        if (!GetFileInformationByHandle(handle, out var info)) { return false; }
        identity = $"{info.Volume:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}";
        bytes = ((long)info.SizeHigh << 32) | info.SizeLow;
        attributes = (FileAttributes)info.Attributes;
        return true;
    }
    internal static SafeFileHandle Reopen(SafeFileHandle handle, uint share) => ReOpenFile(handle, 0x80000000, share, 0x08000000);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle ReOpenFile(SafeFileHandle handle, uint access, uint share, uint flags);
}
