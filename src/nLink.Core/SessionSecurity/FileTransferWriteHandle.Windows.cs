using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NLink.Core.Logging;

namespace NLink.Core.SessionSecurity;

public sealed partial class FileTransferWriteHandle
{
    private readonly SemaphoreSlim? windowsLifetime;
    private SafeFileHandle? windowsRetainedRead;
    private bool windowsDisposed, windowsRenamed, windowsFinalizeAttempted;

    // Per-instance scheduling/fault seam; not a destination/transfer capability.
    internal Func<Task>? WindowsAfterRenameForTests { get; set; }

    private FileTransferWriteHandle(FileTransferWritePlan plan, FileStream stream, SemaphoreSlim lifetime)
        : this(plan, stream) => windowsLifetime = lifetime;

    internal static FileTransferWriteHandle CreateWindows(FileTransferWritePlan plan, int bufferSize)
    {
        // CREATE_NEW, read/write + DELETE, exclusive, asynchronous/random access.
        // DELETE access permits rename of this open file; no delete sharing is added.
        var native = CreateFile(NativePath(plan.TempPath), 0xc0010000, 0, IntPtr.Zero, 1, 0x50000080, IntPtr.Zero);
        try
        {
            if (native.IsInvalid) throw NativeFailure("Could not create the file-transfer destination.");
            var stream = new FileStream(native, FileAccess.ReadWrite, bufferSize, isAsync: true);
            return new FileTransferWriteHandle(plan, stream, new SemaphoreSlim(1, 1));
        }
        catch { native.Dispose(); throw; }
    }

    private async Task FinalizeWindowsAsync(CancellationToken ct)
    {
        await windowsLifetime!.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(windowsDisposed, this);
            if (finalized) return;
            if (windowsFinalizeAttempted) throw new InvalidOperationException("Destination finalization was already attempted.");
            windowsFinalizeAttempted = true;
            await Stream.FlushAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            preserveTempArtifact = true;
            try
            {
                if (!Plan.AllowOverwrite && (File.Exists(Plan.FinalPath) || Directory.Exists(Plan.FinalPath)))
                    throw new IOException("File-transfer target already exists and overwrite is disabled.");
                RenameWindows(Stream.SafeFileHandle, Plan.FinalPath, Plan.AllowOverwrite);
                windowsRenamed = true; // Physical rename cannot be rolled back by later cleanup.
                if (WindowsAfterRenameForTests is { } afterRename) await afterRename().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!DuplicateHandle(GetCurrentProcess(), Stream.SafeFileHandle, GetCurrentProcess(),
                        out var read, 0x80000000, false, 0))
                {
                    var error = NativeFailure("Could not retain the finalized destination.");
                    read.Dispose();
                    throw error;
                }
                windowsRetainedRead = read;
                // Only the read-only duplicate survives; it has neither WRITE nor DELETE rights.
                await Stream.DisposeAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                finalized = true;
            }
            catch
            {
                windowsRetainedRead?.Dispose();
                windowsRetainedRead = null;
                await Stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { windowsLifetime.Release(); }
    }

    private void DisposeWindows()
    {
        windowsLifetime!.Wait();
        try { if (BeginWindowsDispose()) Stream.Dispose(); }
        finally { windowsLifetime.Release(); }
    }

    // Moves the private read-only hold; never reopens a pathname or duplicates
    // WRITE/DELETE rights. Called only by the exact Service evidence handoff.
    internal SafeFileHandle? TryTakeFinalizedWindowsRead()
    {
        if (!ReferenceEquals(original, this) || windowsLifetime is null || !windowsLifetime.Wait(0)) return null;
        try
        {
            if (windowsDisposed || !finalized) return null;
            return Interlocked.Exchange(ref windowsRetainedRead, null);
        }
        finally { windowsLifetime.Release(); }
    }

    internal void ReleaseFinalizedWindowsRead()
    {
        if (ReferenceEquals(original, this) && finalized)
            Interlocked.Exchange(ref windowsRetainedRead, null)?.Dispose();
    }

    private async ValueTask DisposeWindowsAsync()
    {
        await windowsLifetime!.WaitAsync().ConfigureAwait(false);
        try { if (BeginWindowsDispose()) await Stream.DisposeAsync().ConfigureAwait(false); }
        finally { windowsLifetime.Release(); }
    }

    private bool BeginWindowsDispose()
    {
        if (windowsDisposed) return false;
        windowsDisposed = true;
        windowsRetainedRead?.Dispose();
        windowsRetainedRead = null;
        if (!windowsRenamed && !preserveTempArtifact)
        {
            try
            {
                // Delete only the still-owned temporary file. If its handle was
                // externally closed, never fall back to deleting a possibly reused path.
                if (!SetFileInformationByHandle(Stream.SafeFileHandle, 4, [1], 1))
                    throw NativeFailure("Could not remove the unfinished destination.");
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn("FileTransferGuard", $"event=temp_cleanup_failed; path={Plan.TempPath}; ex={ex.GetType().Name}");
            }
        }
        return true;
    }

    private static void RenameWindows(SafeFileHandle file, string path, bool replace)
    {
        var name = Encoding.Unicode.GetBytes(NativePath(path));
        var start = Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.FirstCharacter)).ToInt32();
        var lengthOffset = Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.NameBytes)).ToInt32();
        var body = new byte[checked(start + name.Length + 2)];
        body[0] = replace ? (byte)1 : (byte)0;
        BitConverter.GetBytes(name.Length).CopyTo(body, lengthOffset);
        name.CopyTo(body, start);
        if (!SetFileInformationByHandle(file, 3, body, (uint)body.Length))
            throw NativeFailure("Could not rename the file-transfer destination.");
    }

    private static string NativePath(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private static IOException NativeFailure(string message)
        => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

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
