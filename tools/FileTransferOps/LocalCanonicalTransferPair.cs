using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    // Two binary transfer files, never entries in the UTF-8 diagnostic catalog.
    // Metadata can describe the pair but cannot instantiate native ownership.
    internal sealed partial class CanonicalTransferPair : IDisposable
    {
        internal readonly SafeFileHandle[] Files = new SafeFileHandle[2];
        private readonly string[] identities = new string[2], writers = new string[2];
        private readonly Guid[] requests = new Guid[2];
        private readonly int[] processes = new int[2], handleOffsets = new int[2];
        private readonly long[] creations = new long[2], sequences = new long[2];
        private byte[] packet;
        private bool disposed;
        internal string Session, Transfer, Sha256;
        private CanonicalTransferPair() { }
        private bool Decode(byte[] bytes, LocalGuiWriterRequest.Completion completion)
        {
            if (bytes == null || bytes.Length > LocalPayloadParentChannel.Capacity || completion == null) return false;
            packet = (byte[])bytes.Clone();
            using (var stream = new MemoryStream(packet, false))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                if (new Guid(reader.ReadBytes(16)) != completion.RequestId) return false;
                int length = reader.ReadInt32();
                if (length < 32 || length > 4088 || reader.ReadBytes(length).Length != length ||
                    !LocalGuiWriterRequest.CanonicalPayloadParent.SameCompletion(completion, reader.ReadBytes(32))) return false;
                Session = reader.ReadString(); Transfer = reader.ReadString();
                if (String.IsNullOrEmpty(Session) || Session.Length > 1024 || String.IsNullOrEmpty(Transfer) || Transfer.Length > 1024) return false;
                for (int i = 0; i < 2; i++)
                {
                    requests[i] = new Guid(reader.ReadBytes(16)); processes[i] = reader.ReadInt32(); creations[i] = reader.ReadInt64();
                    writers[i] = reader.ReadString(); sequences[i] = reader.ReadInt64();
                    handleOffsets[i] = checked((int)stream.Position); long handle = reader.ReadInt64();
                    identities[i] = reader.ReadString(); long count = reader.ReadInt64(); string hash = reader.ReadString();
                    if (requests[i] == Guid.Empty || processes[i] <= 0 || creations[i] <= 0 || String.IsNullOrEmpty(writers[i]) || writers[i].Length > 1024 ||
                        sequences[i] <= 0 || handle <= 0 || count != 268435456 ||
                        !System.Text.RegularExpressions.Regex.IsMatch(hash, @"\A[0-9a-f]{64}\z") || (Sha256 != null && Sha256 != hash)) return false;
                    Sha256 = hash;
                }
                return stream.Position == stream.Length && requests[0] != requests[1] && processes[0] != processes[1] &&
                    writers[0] != writers[1] && identities[0] != identities[1];
            }
        }
        internal static CanonicalTransferPair DuplicateLocal(byte[] packet, SafeFileHandle[] source, string[] ids, LocalGuiWriterRequest.Completion completion)
        {
            var pair = new CanonicalTransferPair(); bool success = false;
            try
            {
                if (!LocalGuiWriterRequest.CanonicalPayloadWrapper.PayloadFilesMatch(source, ids) || !pair.Decode(packet, completion)) return null;
                for (int i = 0; i < 2; i++)
                    if (ids[i] != pair.identities[i] || !DuplicateLocalHandle(GetCurrentProcess(), source[i], GetCurrentProcess(), out pair.Files[i], 0x80000000, false, 0)) return null;
                success = pair.IsCurrent; return success ? pair : null;
            }
            catch { return null; }
            finally { if (!success) pair.Dispose(); }
        }
        internal static CanonicalTransferPair Pull(byte[] packet, SafeWaitHandle parent, LocalGuiWriterRequest.Completion completion, LocalFrontierParentFacts facts)
        {
            var pair = new CanonicalTransferPair(); bool success = false;
            try
            {
                if (!pair.Decode(packet, completion) || !facts.MatchesCompletion(completion) ||
                    !facts.MatchesPayloadWriters(pair.requests, pair.processes, pair.creations, pair.writers, pair.sequences)) return null;
                for (int i = 0; i < 2; i++)
                    if (!LocalGuiWriterRequest.CanonicalAttemptDestination.DuplicateHandle(parent, new IntPtr(BitConverter.ToInt64(pair.packet, pair.handleOffsets[i])),
                        GetCurrentProcess(), out pair.Files[i], 0x80000000, false, 0)) return null;
                success = pair.IsCurrent; return success ? pair : null;
            }
            catch { return null; }
            finally { if (!success) pair.Dispose(); }
        }
        internal byte[] Export()
        {
            if (!IsCurrent) return null;
            var bytes = (byte[])packet.Clone();
            for (int i = 0; i < 2; i++) Array.Copy(BitConverter.GetBytes(Files[i].DangerousGetHandle().ToInt64()), 0, bytes, handleOffsets[i], 8);
            return IsCurrent ? bytes : null;
        }
        internal bool IsCurrent { get { return !disposed && LocalGuiWriterRequest.CanonicalPayloadWrapper.PayloadFilesMatch(Files, identities); } }
        public void Dispose() { if (disposed) return; disposed = true; foreach (var file in Files) if (file != null) file.Dispose(); }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", EntryPoint="DuplicateHandle")] private static extern bool DuplicateLocalHandle(IntPtr process, SafeFileHandle handle,
            IntPtr target, out SafeFileHandle duplicate, uint access, bool inherit, uint options);
    }

}
