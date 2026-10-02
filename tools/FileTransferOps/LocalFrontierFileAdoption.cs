#if NET8_0_OR_GREATER
#pragma warning disable 8600, 8601, 8602, 8603, 8604, 8618, 8625
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    // Bounded native transport primitive only. Scalar scope and a callback do
    // not create original GUI/app/writer custody. No production caller exists.
#if NET8_0_OR_GREATER
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
    internal sealed partial class LocalFrontierFileAdoption : IDisposable
    {
        internal const int MaximumActiveMilliseconds = 15000;
        private readonly LocalFrontierFileAdoption original;
        private readonly object sync = new object();
        private readonly Guid gui;
        private readonly Guid[] requests;
        private readonly Func<bool> current;
        private readonly Stopwatch work;
        private readonly bool[] attempted = new bool[2];
        private readonly List<Contribution> adopted = new List<Contribution>();
        private bool disposed;
        private int metadataBytes;
        private sealed class Contribution
        {
            internal LocalFrontierFileManifest Manifest;
            internal readonly List<FileStream> Files = new List<FileStream>();
            internal void Close() { foreach (var file in Files) file.Dispose(); Files.Clear(); }
        }
        internal LocalFrontierFileAdoption(Guid gui, Guid first, Guid second, Func<bool> current)
            : this(gui, first, second, current, new Stopwatch()) { }
        internal LocalFrontierFileAdoption(Guid gui, Guid first, Guid second, Func<bool> current, Stopwatch work)
        {
            this.work = work;
            original = this; this.gui = gui; requests = new[] { first, second }; this.current = current;
            disposed = gui == Guid.Empty || first == Guid.Empty || second == Guid.Empty || first == second || current == null;
        }
        internal long ActiveMilliseconds { get { return work.ElapsedMilliseconds; } }
        internal int ContributionCount { get { lock (sync) { return IsRetained ? adopted.Count : 0; } } }
        internal bool IsRetained
        {
            get
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || disposed) return false;
                    bool measuring = work.IsRunning;
                    if (!measuring) work.Start();
                    try
                    {
                        if (!Current()) { Dispose(); return false; }
                        if (!RetainedFilesCurrent()) { Dispose(); return false; }
                        if (Current()) return true;
                        Dispose(); return false;
                    }
                    catch { Dispose(); return false; }
                    finally { if (!measuring) work.Stop(); }
                }
            }
        }

        private bool RetainedFilesCurrent()
        {
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
            long sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
#endif
            foreach (var item in adopted)
                for (int i = 0; i < item.Files.Count; i++)
                    if (!Matches(item.Files[i].SafeFileHandle, i == 0 ? item.Manifest.Source : item.Manifest.Payloads[i - 1].File, i == 0)) return false;
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
            bool result = !sourceVerified || SealedSourceMatches();
            NLink.Tests.NativeContentParserProfileForTest.ReaderStage(17, sample); return result;
#else
            return !sourceVerified || SealedSourceMatches();
#endif
        }

        // One attempt for each fixed slot; failure retires BOTH contributions.
        // The process is an already-held original native handle, never OpenProcess
        // by descriptor PID. Every duplicate is owned locally before validation.
        internal bool TryAdopt(int slot, SafeWaitHandle appProcess, byte[] manifestBytes)
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this) || disposed) return false;
                var contribution = new Contribution(); bool success = false;
                bool measuring = work.IsRunning; work.Start();
                try
                {
                    if (!IsRetained || slot < 0 || slot >= 2 || attempted[slot]) return false;
                    attempted[slot] = true;
                    if (manifestBytes == null || manifestBytes.Length > LocalFrontierFileManifest.MaximumBytes) return false;
                    // Decode only a local immutable snapshot. Wire lengths never
                    // select an unbounded allocation or file-read limit.
                    var bytes = (byte[])manifestBytes.Clone();
                    var manifest = LocalFrontierFileManifest.Decode(bytes); contribution.Manifest = manifest;
                    if (manifest == null || manifest.GuiRequest != gui || manifest.AppRequest != requests[slot] ||
                        metadataBytes > 65536 - bytes.Length || !LiveApp(appProcess, manifest) || !Compatible(manifest)) return false;
                    for (int i = 0; i <= manifest.Payloads.Length; i++)
                    {
                        if (!Current() || !LiveApp(appProcess, manifest)) return false;
                        var expected = i == 0 ? manifest.Source : manifest.Payloads[i - 1].File;
                        var file = Pull(appProcess, expected, i == 0);
                        if (file == null) return false;
                        contribution.Files.Add(file);
                        if (i != 0 && !Verify(file, expected.Length, manifest.Payloads[i - 1].Hash)) return false;
                    }
                    if (!Current() || !LiveApp(appProcess, manifest)) return false;
                    for (int i = 0; i < contribution.Files.Count; i++)
                        if (!Matches(contribution.Files[i].SafeFileHandle, i == 0 ? manifest.Source : manifest.Payloads[i - 1].File, i == 0)) return false;
                    if (!IsRetained || !LiveApp(appProcess, manifest)) return false;
                    adopted.Add(contribution); metadataBytes += bytes.Length; success = true; return true;
                }
                catch { return false; }
                finally
                {
                    if (!measuring) work.Stop();
                    if (!success) { contribution.Close(); Dispose(); }
                }
            }
        }

        // The original GUI wrapper includes its descriptor/ack control work in
        // this SAME budget. Nested TryAdopt must not stop the outer measurement.
        internal bool TryRunControl(Func<bool> action)
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this) || disposed) return false;
                bool success = false; work.Start();
                try { success = Current() && action() && Current(); return success; }
                catch { return false; }
                finally { work.Stop(); if (!success) Dispose(); }
            }
        }

        private bool Compatible(LocalFrontierFileManifest manifest)
        {
            foreach (var old in adopted)
            {
                var previous = old.Manifest;
                if ((previous.AppId == manifest.AppId && previous.AppCreation == manifest.AppCreation) ||
                    previous.Writer == manifest.Writer || !previous.Source.SameFile(manifest.Source)) return false;
                foreach (var a in Ranges(previous)) foreach (var b in Ranges(manifest))
                    if (a.Offset < b.Offset + b.Length && b.Offset < a.Offset + a.Length) return false;
                foreach (var a in previous.Payloads) foreach (var b in manifest.Payloads)
                    if (a.Observation == b.Observation || a.File.SameFile(b.File)) return false;
            }
            return true;
        }
        private static IEnumerable<LocalFrontierFileManifest.Range> Ranges(LocalFrontierFileManifest manifest)
        {
            yield return manifest.Drain;
            foreach (var payload in manifest.Payloads) { yield return payload.Completion; yield return payload.Marker; }
        }
        private bool Current()
        {
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
            long sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
            bool result = !disposed && NLink.Tests.NativeSemanticCopyProbeForTest.CheckBudget("adoption_entry_budget", work.ElapsedMilliseconds, MaximumActiveMilliseconds) && current() &&
                (sealScope == null || sealScope()) && !disposed && NLink.Tests.NativeSemanticCopyProbeForTest.CheckBudget("adoption_exit_budget", work.ElapsedMilliseconds, MaximumActiveMilliseconds);
            NLink.Tests.NativeContentParserProfileForTest.ReaderStage(19, sample); return result;
#else
            return !disposed && work.ElapsedMilliseconds <= MaximumActiveMilliseconds && current() &&
                (sealScope == null || sealScope()) && !disposed && work.ElapsedMilliseconds <= MaximumActiveMilliseconds;
#endif
        }
        private bool Verify(FileStream file, long length, byte[] expected)
        {
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[32768]; long remaining = length;
                while (remaining > 0)
                {
                    if (!Current()) return false;
                    int read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) return false;
                    hash.TransformBlock(buffer, 0, read, buffer, 0); remaining -= read;
                }
                if (file.ReadByte() != -1) return false;
                hash.TransformFinalBlock(new byte[0], 0, 0);
                var actual = hash.Hash;
                for (int i = 0; i < 32; i++) if (actual[i] != expected[i]) return false;
                return Current();
            }
        }
        private static FileStream Pull(SafeWaitHandle process, LocalFrontierFileManifest.FileEntry expected, bool source)
        {
            SafeFileHandle duplicate = null, retained = null;
            try
            {
                // No DUPLICATE_CLOSE_SOURCE and no numeric-handle local wrapper.
                if (!DuplicateHandle(process, new IntPtr(expected.Handle), GetCurrentProcess(), out duplicate, 0x80000000, false, 0) ||
                    !Matches(duplicate, expected, source)) return null;
                // Independent file position; payload write exclusion, source
                // read/write/delete sharing. At most one temporary handle here.
                retained = ReOpenFile(duplicate, 0x80000000, source ? 7u : 5u, 0x08000000);
                if (!Matches(retained, expected, source)) return null;
                var file = new FileStream(retained, FileAccess.Read); retained = null; return file;
            }
            finally { if (retained != null) retained.Dispose(); if (duplicate != null) duplicate.Dispose(); }
        }
        private static bool LiveApp(SafeWaitHandle process, LocalFrontierFileManifest manifest)
        {
            long creation, exit, kernel, user;
            return process != null && !process.IsClosed && !process.IsInvalid && WaitForSingleObject(process, 0) == 258 &&
                GetProcessId(process) == manifest.AppId && manifest.AppId != GetCurrentProcessId() &&
                GetProcessTimes(process, out creation, out exit, out kernel, out user) && creation == manifest.AppCreation;
        }
        private static bool Matches(SafeFileHandle handle, LocalFrontierFileManifest.FileEntry expected, bool source)
        {
            FileInformation info;
            if (handle == null || handle.IsClosed || handle.IsInvalid || GetFileType(handle) != 1 ||
                !GetFileInformationByHandle(handle, out info) || (info.Attributes & 16) != 0 ||
                info.Volume != expected.Volume || info.IndexHigh != expected.IndexHigh || info.IndexLow != expected.IndexLow) return false;
            long bytes = ((long)info.SizeHigh << 32) | info.SizeLow;
            return source ? bytes >= expected.Length && bytes <= LocalFrontierFileManifest.MaximumSourceBytes : bytes == expected.Length;
        }
        public void Dispose()
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this) || disposed) return;
                disposed = true; sourceVerified = false; processingRunning = false; work.Stop();
                try { processingLifetime.Cancel(); } catch (AggregateException) { }
                finally { processingLifetime.Dispose(); }
                if (sealedSource != null) { sealedSource.Dispose(); sealedSource = null; }
                foreach (var item in adopted) item.Close(); adopted.Clear();
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern int GetCurrentProcessId();
        [DllImport("kernel32.dll")] private static extern int GetProcessId(SafeWaitHandle process);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeWaitHandle process, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeWaitHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern bool DuplicateHandle(SafeWaitHandle source, IntPtr handle, IntPtr target, out SafeFileHandle duplicate, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll")] private static extern SafeFileHandle ReOpenFile(SafeFileHandle file, uint access, uint share, uint flags);
        [DllImport("kernel32.dll")] private static extern uint GetFileType(SafeFileHandle file);
        [DllImport("kernel32.dll")] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
    }
}
