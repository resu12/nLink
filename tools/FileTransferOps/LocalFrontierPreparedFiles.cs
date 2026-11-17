using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    // GUI-owned native pins. Bounded internal descriptor export never releases
    // these handles or transfers the original binding's semantic facts.
    internal sealed partial class LocalFrontierPreparedFiles : IDisposable
    {
        internal const int MaximumFiles = 42;
        private readonly LocalFrontierPreparedFiles original;
        private readonly object sync = new object();
        private readonly List<Entry> entries = new List<Entry>(MaximumFiles);
        private readonly CancellationTokenRegistration cancellation;
        private bool disposed, sealedFiles;
        internal LocalFrontierFileManifest[] Writers { get; private set; }
        internal int Count { get { lock (sync) { return entries.Count; } } }
        internal int PayloadCount { get { lock (sync) { return entries.FindAll(e => e.Role == 10).Count; } } }
        internal LocalFrontierPreparedFiles(CancellationToken lifetime)
        { original = this; cancellation = lifetime.Register(Close); }

        internal sealed class Entry
        {
            internal readonly int Role, Slot;
            internal readonly SafeFileHandle Handle;
            internal readonly FileInformation Identity;
            internal readonly LocalFrontierFileManifest.Payload Payload;
            internal Entry(int role, int slot, SafeFileHandle handle, FileInformation identity, LocalFrontierFileManifest.Payload payload)
            { Role = role; Slot = slot; Handle = handle; Identity = identity; Payload = payload; }
        }
        // Roles: raw source, retained/writer/semantic outputs, semantic copy,
        // evidence, four original pipe captures, then original private payloads.
        internal bool Add(int role, SafeFileHandle source, long bytes, int slot, LocalFrontierFileManifest.Payload payload)
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this)) return false;
                bool success = false; SafeFileHandle duplicate = null;
                try
                {
                    if (disposed || sealedFiles || entries.Count == MaximumFiles || role < 0 || role > 10 || bytes < 0) return false;
                    long maximum = role == 0 ? 268435456L : role <= 4 ? 268959744L : role == 5 ? 65536L : role <= 9 ? 67108864L : 262144L;
                    if (bytes > maximum || ((role == 0 || role == 5 || role == 10) && bytes == 0) ||
                        (role == 10 ? slot < 0 || slot > 1 || payload == null || payload.Observation == Guid.Empty : slot != -1 || payload != null)) return false;
                    FileInformation before, after;
                    if (!Describe(source, out before) || Length(before) != bytes ||
                        !DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), out duplicate, 0x80000000, false, 0) ||
                        !Describe(duplicate, out after) || !SameFile(before, after) || Length(after) != bytes) return false;
                    foreach (var entry in entries)
                        if (SameFile(entry.Identity, after) || (role != 10 && entry.Role == role) ||
                            (payload != null && entry.Payload != null && entry.Payload.Observation == payload.Observation)) return false;
                    if (payload != null && (payload.File.Length != bytes || !Matches(after, payload.File))) return false;
                    entries.Add(new Entry(role, slot, duplicate, after, payload)); duplicate = null;
                    success = true; return true;
                }
                catch { return false; }
                finally { if (duplicate != null) duplicate.Dispose(); if (!success) Close(); }
            }
        }
        internal bool Seal(LocalFrontierFileManifest first, LocalFrontierFileManifest second)
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this)) return false;
                bool success = false;
                try
                {
                    if (disposed || sealedFiles || first == null || second == null || first.AppRequest == second.AppRequest ||
                        first.Payloads.Length > 16 || second.Payloads.Length > 16 || entries.Count != 10 + first.Payloads.Length + second.Payloads.Length) return false;
                    for (int role = 0; role < 10; role++) if (entries.FindAll(e => e.Role == role).Count != 1) return false;
                    var source = entries.Find(e => e.Role == 0);
                    if (!Matches(source.Identity, first.Source) || !Matches(source.Identity, second.Source)) return false;
                    var writers = new[] { first, second };
                    for (int slot = 0; slot < 2; slot++)
                        foreach (var payload in writers[slot].Payloads)
                            if (entries.FindAll(e => e.Role == 10 && e.Slot == slot && Object.ReferenceEquals(e.Payload, payload)).Count != 1) return false;
                    Writers = writers; sealedFiles = true; success = IsCurrent; return success;
                }
                catch { return false; }
                finally { if (!success) Close(); }
            }
        }
        internal bool IsCurrent
        {
            get
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || disposed || !sealedFiles) return false;
                    foreach (var entry in entries)
                    {
                        FileInformation info;
                        if (!Describe(entry.Handle, out info) || !SameFile(info, entry.Identity) || Length(info) != Length(entry.Identity))
                        { Close(); return false; }
                    }
                    return true;
                }
            }
        }
        private void Close()
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this) || disposed) return;
                disposed = true; foreach (var entry in entries) entry.Handle.Dispose();
            }
        }
        public void Dispose() { if (!Object.ReferenceEquals(original, this)) return; Close(); cancellation.Dispose(); }
        private static bool Describe(SafeFileHandle handle, out FileInformation info)
        {
            info = new FileInformation();
            try { return handle != null && !handle.IsInvalid && !handle.IsClosed && GetFileType(handle) == 1 &&
                GetFileInformationByHandle(handle, out info) && (info.Attributes & 16) == 0; }
            catch { return false; }
        }
        private static long Length(FileInformation info) { return ((long)info.SizeHigh << 32) | info.SizeLow; }
        private static bool SameFile(FileInformation a, FileInformation b)
        { return a.Volume == b.Volume && a.IndexHigh == b.IndexHigh && a.IndexLow == b.IndexLow; }
        private static bool Matches(FileInformation info, LocalFrontierFileManifest.FileEntry expected)
        { return expected != null && info.Volume == expected.Volume && info.IndexHigh == expected.IndexHigh && info.IndexLow == expected.IndexLow; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern uint GetFileType(SafeFileHandle file);
        [DllImport("kernel32.dll")] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
        [DllImport("kernel32.dll")] private static extern bool DuplicateHandle(IntPtr source, SafeFileHandle handle, IntPtr target,
            out SafeFileHandle duplicate, uint access, bool inherit, uint options);
    }
}
