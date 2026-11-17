using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierLaunchOwner
        {
            private readonly Stopwatch frontierWork = new Stopwatch();
            private bool ledgerOriginAttempted;
            private FrontierLedgerOrigin ledgerOrigin;

            // Create the actual source before any app request/launch. No path,
            // snapshot, stream, handle, identity or success flag can be imported.
            public FrontierLedgerOrigin TryCreateLedgerOrigin()
            { bool created; return TryCreateLedgerOriginCore(out created); }

            // Output-only creation evidence for ordinary GUI startup. A later
            // optional rejection must not cause CREATE_NEW to run again. This
            // flag does not supply native custody; only the returned origin can.
            public FrontierLedgerOrigin TryCreateLedgerOriginForGui(string expectedPath, out bool sourceCreated)
            {
                sourceCreated = false;
                if (!String.Equals(expectedPath, Path.Combine(artifactRoot, "live-operational-log", "nlink.log"), StringComparison.OrdinalIgnoreCase)) return null;
                return TryCreateLedgerOriginCore(out sourceCreated);
            }

            private FrontierLedgerOrigin TryCreateLedgerOriginCore(out bool sourceCreated)
            {
                sourceCreated = false;
                lock (sync)
                {
                    if (!IsCurrent) return null;
                    bool success = false;
                    frontierWork.Start();
                    try
                    {
                        if (ledgerOriginAttempted || apps.Count != 0 || appRequests.Count != 0 || sealedLaunches) return null;
                        ledgerOriginAttempted = true;
                        if (frontierWork.ElapsedMilliseconds > LocalFrontierFileAdoption.MaximumActiveMilliseconds) return null;
                        string directory = Path.Combine(artifactRoot, "live-operational-log");
                        Directory.CreateDirectory(directory);
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
                        ledgerOrigin = new FrontierLedgerOrigin(this, Path.Combine(directory, "nlink.log"), out sourceCreated);
                        success = IsCurrent && frontierWork.ElapsedMilliseconds <= LocalFrontierFileAdoption.MaximumActiveMilliseconds;
                        return success ? ledgerOrigin : null;
                    }
                    catch { return null; }
                    finally { frontierWork.Stop(); if (!success) Dispose(); }
                }
            }
            // Resource checks never call back into this owner: adopter callbacks
            // may already hold their own lock. No ledger -> owner lock inversion.
            private bool LedgerOriginCurrent()
            { return frontierWork.ElapsedMilliseconds <= LocalFrontierFileAdoption.MaximumActiveMilliseconds && (ledgerOrigin == null || ledgerOrigin.ResourceCurrent); }
            internal bool OwnsLedgerOrigin(FrontierLedgerOrigin value)
            { lock (sync) { return value != null && Object.ReferenceEquals(ledgerOrigin, value) && IsCurrent; } }
            internal void DisposeLedgerOrigin(FrontierLedgerOrigin value)
            { lock (sync) { if (Object.ReferenceEquals(ledgerOrigin, value)) Dispose(); } }
            internal FrontierLedgerOrigin GetSealedLedgerOrigin(FrontierSealedFiles value)
            { lock (sync) { return OwnsSealedFiles(value) ? ledgerOrigin : null; } }
        }

        // Original creation/cursor custody, not validated scenario history.
        // Paths/IDs are diagnostic locators, not imports.
        public sealed partial class FrontierLedgerOrigin : IDisposable
        {
            private readonly FrontierLaunchOwner owner;
            private readonly FrontierLedgerOrigin original;
            private readonly object sync = new object();
            private FileStream file;
            private bool disposed;
            public string LedgerId { get; private set; }
            public string GenerationId { get; private set; }
            public string SourcePath { get; private set; }
            public bool WasCreatedEmpty { get { return true; } }
            public bool IsCurrent { get { return owner.OwnsLedgerOrigin(this); } }
            public bool CursorHistoryValidated { get { return false; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            internal FrontierLedgerOrigin(FrontierLaunchOwner owner, string path, out bool sourceCreated)
            {
                sourceCreated = false;
                original = this; this.owner = owner; SourcePath = path; LedgerId = Guid.NewGuid().ToString("N"); GenerationId = Guid.NewGuid().ToString("N");
                SafeFileHandle handle = null;
                try
                {
                    // GENERIC_READ + CREATE_NEW: retain no write grant, so the
                    // later restrictive source seal can coexist with this handle.
                    // Share append/read access, but exclude replacement/deletion.
                    handle = CreateLedgerFile(path, 0x80000000, 3, IntPtr.Zero, 1, 0x08000080, IntPtr.Zero);
                    if (handle.IsInvalid) throw new IOException("ledger_origin_create_failed");
                    sourceCreated = true;
                    if (GetLedgerFileType(handle) != 1) throw new IOException("ledger_origin_create_failed");
                    file = new FileStream(handle, FileAccess.Read); handle = null;
                    if (file.Length != 0 || !ResourceCurrent) throw new IOException("ledger_origin_not_empty");
                }
                catch { Close(); throw; }
                finally { if (handle != null) handle.Dispose(); }
            }
            internal bool ResourceCurrent
            {
                get
                {
                    lock (sync)
                    {
                        if (!Object.ReferenceEquals(original, this) || disposed || file == null) return false;
                        try { return file.CanRead && !file.CanWrite && file.Position == readPosition && file.Length >= readPosition && file.Length <= LocalFrontierFileManifest.MaximumSourceBytes; }
                        catch { return false; }
                    }
                }
            }
            internal SafeFileHandle OriginalHandle
            { get { lock (sync) { return ResourceCurrent ? file.SafeFileHandle : null; } } }
            internal void Close()
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || disposed) return;
                    disposed = true; if (file != null) file.Dispose(); cursorHash.Dispose();
                }
            }
            public void Dispose() { owner.DisposeLedgerOrigin(this); }
            [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern SafeFileHandle CreateLedgerFile(string path, uint access, uint share, IntPtr security,
                uint disposition, uint flags, IntPtr template);
            [DllImport("kernel32.dll", EntryPoint = "GetFileType")]
            private static extern uint GetLedgerFileType(SafeFileHandle handle);
        }
    }
}
