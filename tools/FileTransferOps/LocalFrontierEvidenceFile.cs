using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using NLink.Tools.FrontierLedger;

namespace NLink.Tools
{
    // Native persistence primitive, not a finalizer receipt. Only its original
    // binding can supply validated facts and the original cumulative work scope.
    internal sealed class LocalFrontierEvidenceFile : IDisposable
    {
        internal const int MaximumBytes = 65536;
        private readonly LocalFrontierEvidenceFile original;
        private readonly object sync = new object();
        private readonly CancellationTokenRegistration cancellation;
        private FileStream file;
        private bool disposed, attempted;
        internal GuiSmokeLedgerCapturedOutput Output { get; private set; }
        internal LocalFrontierEvidenceFile(CancellationToken lifetime)
        { original = this; cancellation = lifetime.Register(Close); }
        internal bool TryPersist(string destination, byte[] bytes, Func<bool> current)
        { return PersistCore(destination, bytes, current, MaximumBytes); }
        // The terminal diagnostic embeds the existing 4-MiB packet as base64.
        // Finalizer evidence retains its original, separate 64-KiB ceiling.
        internal bool TryPersistDiagnostic(string destination, byte[] bytes, Func<bool> current)
        { return PersistCore(destination, bytes, current, 6 * 1024 * 1024); }
        private bool PersistCore(string destination, byte[] bytes, Func<bool> current, int maximumBytes)
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this)) return false;
                bool success = false; SafeFileHandle created = null, duplicate = null;
                try
                {
                    if (disposed || attempted) return false; attempted = true;
                    if (bytes == null || bytes.Length < 1 || bytes.Length > maximumBytes || current == null || !current()) return false;
                    // CREATE_NEW, GENERIC_READ/WRITE + DELETE; deny other writes
                    // and deletion. Rename this exact open file, never a path reopen.
                    string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    created = CreateFile(temporary, 0xc0010000, 1, IntPtr.Zero, 1, 0x08000080, IntPtr.Zero);
                    if (created.IsInvalid) return false;
                    using (var output = new FileStream(created, FileAccess.ReadWrite, 32768))
                    using (var hash = SHA256.Create())
                    {
                        created = null;
                        for (int offset = 0; offset < bytes.Length;)
                        {
                            if (disposed || !current()) return false;
                            int count = Math.Min(32768, bytes.Length - offset);
                            output.Write(bytes, offset, count); hash.TransformBlock(bytes, offset, count, bytes, offset);
                            offset += count;
                        }
                        hash.TransformFinalBlock(new byte[0], 0, 0); output.Flush(true);
                        if (disposed || !current() || output.Length != bytes.Length || !Rename(output.SafeFileHandle, destination)) return false;
                        string digest = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                        Output = GuiSmokeLedgerCapturedOutput.Capture(output, destination, digest, 1, bytes.Length);
                        if (disposed || !current() || Output == null || !DuplicateHandle(GetCurrentProcess(), output.SafeFileHandle,
                            GetCurrentProcess(), out duplicate, 0x80000000, false, 0)) return false;
                        file = new FileStream(duplicate, FileAccess.Read); duplicate = null;
                    }
                    success = current() && IsCurrent; return success;
                }
                catch { return false; }
                finally
                {
                    if (created != null) created.Dispose(); if (duplicate != null) duplicate.Dispose();
                    if (!success) Close();
                }
            }
        }
        internal bool IsCurrent
        {
            get
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || disposed) return false;
                    try { if (file != null && file.CanRead && !file.CanWrite && Output != null && Output.Matches(file)) return true; }
                    catch { }
                    Close(); return false;
                }
            }
        }
        internal bool PinParentEvidence(LocalFrontierPreparedFiles target)
        { lock (sync) { return IsCurrent && target.Add(5, file.SafeFileHandle, Output.ByteCount, -1, null) && IsCurrent; } }
        private void Close()
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this) || disposed) return;
                disposed = true; if (file != null) file.Dispose();
            }
        }
        public void Dispose() { if (!Object.ReferenceEquals(original, this)) return; Close(); cancellation.Dispose(); }
        private static bool Rename(SafeFileHandle handle, string destination)
        {
            var name = new UnicodeEncoding(false, false, true).GetBytes(Path.GetFullPath(destination));
            if (name.Length > 8192) return false;
            int start = Marshal.OffsetOf(typeof(RenameInfo), "FirstCharacter").ToInt32();
            int lengthOffset = Marshal.OffsetOf(typeof(RenameInfo), "NameBytes").ToInt32();
            var body = new byte[start + name.Length + 2]; // zero flags: never replace
            Buffer.BlockCopy(BitConverter.GetBytes(name.Length), 0, body, lengthOffset, 4);
            Buffer.BlockCopy(name, 0, body, start, name.Length);
            return SetFileInformationByHandle(handle, 3, body, (uint)body.Length);
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RenameInfo { public uint Flags; public IntPtr Root; public uint NameBytes; public char FirstCharacter; }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int information, byte[] data, uint size);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr process, SafeFileHandle source, IntPtr target, out SafeFileHandle duplicate, uint access, bool inherit, uint options);
    }
}
