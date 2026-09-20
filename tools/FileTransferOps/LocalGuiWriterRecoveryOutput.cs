using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        // Original pipe-capture file only. No caller path or replacement handle.
        // The four-stream registry bounds retained input to 256 MiB in total;
        // ordinary raw capture still drains to EOF under the outer deadline.
        internal sealed class FrontierRecoveryOutput
        {
            internal const long MaximumBytes = 67108864;
            private readonly FrontierRecoveryOutput original;
            private readonly object sync = new object();
            private FileStream file;
            private object reader;
            private long length, position;
            private bool disposed, ended;
            internal FrontierRecoveryOutput() { original = this; }
            internal long Length { get { return length; } }
            internal bool Seal(FileStream target)
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || disposed || file != null) return false;
                    SafeFileHandle handle = null;
                    try
                    {
                        length = target.Length;
                        if (length < 0 || length > MaximumBytes || !DuplicateRecoveryOutput(GetCurrentProcess(), target.SafeFileHandle,
                            GetCurrentProcess(), out handle, 0x80000000, false, 0)) return false;
                        file = new FileStream(handle, FileAccess.Read); handle = null;
                        return IsCurrent;
                    }
                    catch { return false; }
                    finally { if (handle != null) handle.Dispose(); }
                }
            }
            internal bool IsCurrent
            {
                get
                {
                    lock (sync)
                    {
                        if (!Object.ReferenceEquals(original, this) || disposed || file == null) return false;
                        try { if (file.CanRead && !file.CanWrite && file.Length == length) return true; }
                        catch { }
                        Revoke(); return false;
                    }
                }
            }
            internal bool TryOpen(object value)
            {
                lock (sync)
                {
                    if (!IsCurrent || value == null || reader != null) return false;
                    try { file.Position = 0; reader = value; return true; }
                    catch { Revoke(); return false; }
                }
            }
            internal long Position(object value)
            { lock (sync) { return Object.ReferenceEquals(reader, value) && value != null && IsCurrent ? position : -1; } }
            internal bool ReachedEnd(object value)
            { lock (sync) { return Object.ReferenceEquals(reader, value) && value != null && IsCurrent && ended; } }
            internal bool TryRead(object value, byte[] buffer, int offset, int count, out int read)
            {
                read = 0;
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || value == null || !Object.ReferenceEquals(reader, value)) return false;
                    try
                    {
                        if (!IsCurrent || file.Position != position) return false;
                        if (count != 0)
                        {
                            int requested = (int)Math.Min(Math.Min(count, 32768), length - position);
                            if (requested != 0)
                            {
                                read = file.Read(buffer, offset, requested);
                                if (read == 0) return false;
                                position += read;
                            }
                            if (position == length)
                            {
                                if (file.ReadByte() != -1) return false;
                                ended = true;
                            }
                        }
                        return IsCurrent;
                    }
                    catch { read = 0; return false; }
                }
            }
            internal bool PinParentOutput(LocalFrontierPreparedFiles target, int role)
            { lock (sync) { return IsCurrent && ended && target.Add(role, file.SafeFileHandle, length, -1, null) && IsCurrent; } }
            internal void Revoke()
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || disposed) return;
                    disposed = true;
                    if (file != null) file.Dispose();
                }
            }
            [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)]
            private static extern bool DuplicateRecoveryOutput(IntPtr process, SafeFileHandle source, IntPtr target,
                out SafeFileHandle duplicate, uint access, bool inherit, uint options);
        }
    }
}
