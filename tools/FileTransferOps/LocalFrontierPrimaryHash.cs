using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalInputReadLease
    {
        private bool frontierNative;
        private LocalInputReadLease frontierOriginalLease;
        private SafeFileHandle frontierHashHandle;
        private int frontierCountAttempted;
        // Replaces the parent's original pre-analysis count, not its hash pass.
        // A mapped read keeps the native file object's sharing and position.
        public long CountFrontierSourceLines(string path)
        {
            if (!Object.ReferenceEquals(frontierOriginalLease, this) || !frontierNative ||
                !String.Equals(System.IO.Path.GetFullPath(path), Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("frontier_primary_count_source");
            if (Interlocked.CompareExchange(ref frontierCountAttempted, 1, 0) != 0)
                throw new InvalidOperationException("frontier_primary_count_repeated");
            Action current = () =>
            {
                if (!IsVerified || anchor == null || !anchor.CanRead || anchor.CanWrite ||
                    frontierHashHandle == null || frontierHashHandle.IsClosed || frontierHashHandle.IsInvalid || anchor.Length != ByteCount)
                    throw new IOException("frontier_primary_count_retired");
            };
            current();
            if (ByteCount == 0) { current(); return 0; }
            // Do not fetch anchor.SafeFileHandle again: newer FileStream can
            // synchronize its cached position after another native owner moved it.
            using (var handle = DuplicateFrontierRead(frontierHashHandle))
            {
                if (handle == null) throw new IOException("frontier_primary_count_read_unavailable");
                using (var file = new FileStream(handle, FileAccess.Read))
                using (var map = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true))
                using (var stream = map.CreateViewStream(0, ByteCount, MemoryMappedFileAccess.Read))
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                {
                    long count = 0;
                    while (reader.ReadLine() != null) { current(); count++; }
                    current(); return count;
                }
            }
        }
        // Retain the original restrictive native file object. ReOpenFile with
        // looser sharing would lose that protection when prior custody closes.
        private static SafeFileHandle DuplicateFrontierRead(SafeFileHandle original)
        {
            SafeFileHandle result;
            if (DuplicateFrontierHandle(GetCurrentProcess(), original, GetCurrentProcess(), out result, 0x80000000, false, 0)) return result;
            if (result != null) result.Dispose(); return null;
        }
        internal static HashReadResult HashFrontier(SafeFileHandle original, LocalGuiWriterRequest.Output expected, Func<bool> current)
        {
            SafeFileHandle handle = null; FileStream stream = null; LocalInputReadLease lease = null; bool success = false;
            try
            {
                if (!current()) return null;
                handle = DuplicateFrontierRead(original); if (handle == null) return null;
                stream = new FileStream(handle, FileAccess.Read); handle = null;
                if (stream.Length != expected.ByteCount || stream.Length > MaximumWriterBytes) return null;
                using (var sha = SHA256.Create())
                {
                    if (stream.Length != 0)
                    using (var map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true))
                    {
                        var buffer = new byte[32768]; long position = 0;
                        while (position < stream.Length)
                        {
                            if (!current()) return null;
                            int count = (int)Math.Min(buffer.Length, stream.Length - position);
                            using (var view = map.CreateViewAccessor(position, count, MemoryMappedFileAccess.Read))
                                if (view.ReadArray(0, buffer, 0, count) != count) return null;
                            sha.TransformBlock(buffer, 0, count, buffer, 0); position += count;
                        }
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    string digest = Hex(sha.Hash);
                    if (digest != expected.Sha256 || !current()) return null;
                    lease = new LocalInputReadLease(stream, expected.Path, digest, expected.LineCount, true); stream = null;
                    lease.frontierNative = true; lease.frontierOriginalLease = lease;
                    lease.frontierHashHandle = lease.anchor.SafeFileHandle; success = current();
                    return success ? new HashReadResult(digest, lease) : null;
                }
            }
            catch { return null; }
            finally { if (handle != null) handle.Dispose(); if (stream != null) stream.Dispose(); if (!success && lease != null) lease.Dispose(); }
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle")] private static extern bool DuplicateFrontierHandle(IntPtr source,
            SafeFileHandle handle, IntPtr target, out SafeFileHandle duplicate, uint access, bool inherit, uint options);
    }
}
