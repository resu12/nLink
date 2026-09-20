using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierPrimaryInputs
        {
            private readonly OwnedReader[] readers;
            private readonly bool[] opened;
            // Private primitive for later worker handoff. Mapped windows never
            // seek the shared duplicate-handle file position or reopen a path.
            internal OwnedReader TryOpenRead(int index)
            {
                lock (owner.frontierInputSync)
                {
                    if (!Object.ReferenceEquals(owner.frontierInputs, this)) return null;
                    lock (sync)
                    {
                        bool success = false; SafeFileHandle handle = null; FileStream stream = null;
                        work.Start();
                        try
                        {
                            if (!IsCurrent || workerOfferAttempted || index < 0 || index >= readers.Length || opened[index]) return null;
                            opened[index] = true;
                            if (!DuplicateReaderHandle(GetCurrentProcess(), pins.Handles[index], GetCurrentProcess(), out handle, 0x80000000, false, 0)) return null;
                            stream = new FileStream(handle, FileAccess.Read); handle = null;
                            readers[index] = new OwnedReader(this, index, stream); stream = null;
                            success = IsCurrent; return success ? readers[index] : null;
                        }
                        catch { return null; }
                        finally { work.Stop(); if (handle != null) handle.Dispose(); if (stream != null) stream.Dispose(); if (!success) Close(); }
                    }
                }
            }
            internal sealed class OwnedReader : IDisposable
            {
                private readonly FrontierPrimaryInputs owner;
                private readonly int index;
                private readonly FileStream stream;
                private readonly MemoryMappedFile map;
                private readonly long length;
                private long position;
                private bool closed;
                internal OwnedReader(FrontierPrimaryInputs owner, int index, FileStream stream)
                {
                    this.owner = owner; this.index = index; this.stream = stream; length = stream.Length;
                    if (length != 0) map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
                }
                internal int Read(byte[] buffer, int offset, int count)
                {
                    lock (owner.owner.frontierInputSync)
                    lock (owner.sync)
                    {
                        if (!Object.ReferenceEquals(owner.readers[index], this)) return -1;
                        bool success = false; int take = 0; owner.work.Start();
                        try
                        {
                            if (closed || !owner.IsCurrent || buffer == null || offset < 0 || count < 1 || count > 32768 || offset > buffer.Length - count) return -1;
                            take = (int)Math.Min(count, length - position);
                            if (take != 0)
                            {
                                using (var view = map.CreateViewAccessor(position, take, MemoryMappedFileAccess.Read))
                                    if (view.ReadArray(0, buffer, offset, take) != take) return -1;
                            }
                            if (!owner.IsCurrent) return -1;
                            position += take; success = true; return take;
                        }
                        catch { return -1; }
                        finally { owner.work.Stop(); if (!success) { if (take != 0) Array.Clear(buffer, offset, take); owner.Close(); } }
                    }
                }
                internal long Position { get { return position; } }
                internal void Close() { if (closed) return; closed = true; if (map != null) map.Dispose(); stream.Dispose(); }
                public void Dispose()
                {
                    lock (owner.owner.frontierInputSync)
                    lock (owner.sync)
                    { if (Object.ReferenceEquals(owner.readers[index], this)) Close(); }
                }
            }
            [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle")] private static extern bool DuplicateReaderHandle(IntPtr source,
                SafeFileHandle handle, IntPtr target, out SafeFileHandle duplicate, uint access, bool inherit, uint options);
        }
    }
}
