using System;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    // An exact post-bind request snapshot, not an evaluator or publication token.
    internal sealed class LocalFrontierPrimaryScope : IDisposable
    {
        private readonly MemoryMappedViewAccessor view;
        private readonly byte[] header, body;
        internal readonly byte[] Binding;
        internal LocalFrontierPrimaryScope(MemoryMappedFile map, byte[] body)
        {
            this.body = (byte[])body.Clone(); header = new byte[64];
            view = map.CreateViewAccessor(0, 64 + body.Length, MemoryMappedFileAccess.Read);
            try
            {
                view.ReadArray(0, header, 0, header.Length);
                using (var hash = SHA256.Create())
                { hash.TransformBlock(header, 0, header.Length, header, 0); hash.TransformFinalBlock(body, 0, body.Length); Binding = hash.Hash; }
                if (!IsCurrent) throw new InvalidOperationException();
            }
            catch { view.Dispose(); throw; }
        }
        internal bool IsCurrent
        {
            get
            {
                try
                {
                    for (int i = 0; i < header.Length; i++) if (view.ReadByte(i) != header[i]) return false;
                    for (int i = 0; i < body.Length; i++) if (view.ReadByte(64 + i) != body[i]) return false;
                    return true;
                }
                catch { return false; }
            }
        }
        public void Dispose() { view.Dispose(); }
    }
    internal sealed partial class LocalFrontierParentFacts
    {
        internal byte[] CopyForPrimary() { return (byte[])bytes.Clone(); }
        internal static LocalFrontierParentFacts DecodeForPrimary(byte[] bytes, byte[] offered, string run, string scenario, Action check)
        {
            try
            {
                if (bytes == null || bytes.Length < 12 || bytes.Length > MaximumBytes) return null;
                int length = BitConverter.ToInt32(bytes, 8);
                if (length < 584 || length > LocalFrontierParentFilePacket.MaximumBytes || length > bytes.Length - 12) return null;
                var original = new byte[length]; Array.Copy(bytes, 12, original, 0, length);
                var old = LocalFrontierParentFilePacket.Decode(original); var next = LocalFrontierParentFilePacket.Decode(offered);
                if (old == null || next == null || old.Gui != next.Gui || old.First != next.First || old.Second != next.Second || old.Entries.Length != next.Entries.Length) return null;
                for (int i = 0; i < old.Entries.Length; i++)
                {
                    var a = old.Entries[i]; var b = next.Entries[i];
                    if (a.Role != b.Role || a.Slot != b.Slot || a.Observation != b.Observation || !a.File.SameFile(b.File) || a.File.Length != b.File.Length) return null;
                }
                // Historical GUI handle numbers remain immutable facts only.
                // Native pulls use the rebased parent offer, never those numbers.
                return Decode(bytes, original, run, scenario, check);
            }
            catch { return null; }
        }
    }
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierParentFilesPending
        {
            internal sealed partial class CommitPins
            {
                internal byte[] ExportPrimaryOffer()
                {
                    lock (sync)
                    {
                        if (!IsCurrent) return null;
                        var entries = new LocalFrontierParentFilePacket.Entry[packet.Entries.Length];
                        for (int i = 0; i < entries.Length; i++)
                        {
                            var entry = packet.Entries[i]; var file = entry.File;
                            entries[i] = new LocalFrontierParentFilePacket.Entry(entry.Role, entry.Slot, entry.Observation,
                                new LocalFrontierFileManifest.FileEntry(Handles[i].DangerousGetHandle().ToInt64(), file.Volume, file.IndexHigh, file.IndexLow, file.Length));
                        }
                        var bytes = new LocalFrontierParentFilePacket(packet.Gui, packet.First, packet.Second, entries).Encode();
                        return IsCurrent ? bytes : null;
                    }
                }
                internal static CommitPins PullPrimary(SafeWaitHandle parent, byte[] bytes, Func<bool> current)
                {
                    CommitPins result = null; bool success = false;
                    try
                    {
                        var packet = LocalFrontierParentFilePacket.Decode(bytes);
                        if (packet == null || !current()) return null;
                        result = new CommitPins(packet);
                        foreach (var entry in packet.Entries)
                        {
                            if (!current()) return null;
                            SafeFileHandle handle = null;
                            try
                            {
                                if (!DuplicateHandle(parent, new IntPtr(entry.File.Handle), GetCurrentProcess(), out handle, 0x80000000, false, 0) ||
                                    !Matches(handle, entry.File)) return null;
                                result.Handles.Add(handle); handle = null;
                            }
                            finally { if (handle != null) handle.Dispose(); }
                        }
                        success = current() && result.IsCurrent; return success ? result : null;
                    }
                    finally { if (!success && result != null) result.Dispose(); }
                }
            }
        }
    }
}
