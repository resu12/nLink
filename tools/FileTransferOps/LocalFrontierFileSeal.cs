#if NET8_0_OR_GREATER
#pragma warning disable 8600, 8601, 8602, 8603, 8604, 8618, 8625
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    internal sealed partial class LocalFrontierFileAdoption
    {
        private bool sealAttempted, sourceVerified;
        private FileStream sealedSource;
        private Func<bool> sealScope;
        private long sealedLength;
        private string sealedHash;
        private int sealedRangeCount;
        private object sealedReader;
        private bool sourceReadAttempted, sourceReadEnded;
        private long sourceReadPosition;
        private bool processingAttempted, processingRunning;
        private bool writerMatchAttempted;
        private readonly CancellationTokenSource processingLifetime = new CancellationTokenSource();
        internal CancellationToken ProcessingLifetime(object reader)
        {
            lock (sync)
            {
                return Object.ReferenceEquals(original, this) && Object.ReferenceEquals(sealedReader, reader) &&
                    reader != null && processingRunning && IsSourceVerified ? processingLifetime.Token : new CancellationToken(true);
            }
        }
        internal bool IsSourceVerified { get { lock (sync) { return IsRetained && sourceVerified; } } }
        internal long SealedLength { get { return sealedLength; } }
        internal string SealedHash { get { return sealedHash; } }
        internal int SealedRangeCount { get { return sealedRangeCount; } }

        // Transport only: the original GUI/finalizer supplies the reader identity.
        // Reuse the sealed handle and cumulative budget; never reopen a pathname.
        internal bool TryBeginSealedRead(object reader)
        {
            return TryRunControl(() =>
            {
                if (!IsSourceVerified || sourceReadAttempted || reader == null) return false;
                sourceReadAttempted = true; sealedReader = reader;
                sealedSource.Position = 0; sourceReadPosition = 0;
                return true;
            });
        }
        internal bool TryReadSealedSource(object reader, byte[] buffer, int offset, int count, out int read)
        {
            read = 0;
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this) || !Object.ReferenceEquals(sealedReader, reader) || reader == null) return false;
                bool success = false, measuring = work.IsRunning;
                work.Start();
                try
                {
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
                    long sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
#endif
                    if (!IsSourceVerified || buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count ||
                        sealedSource.Position != sourceReadPosition) return false;
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
                    NLink.Tests.NativeContentParserProfileForTest.ReaderStage(7, sample);
                    sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
#endif
                    if (count != 0)
                    {
                        int requested = (int)Math.Min(Math.Min(count, 32768), sealedLength - sourceReadPosition);
                        if (requested != 0)
                        {
                            read = sealedSource.Read(buffer, offset, requested);
                            if (read == 0) return false;
                            sourceReadPosition += read;
                        }
                        if (sourceReadPosition == sealedLength)
                        {
                            if (sealedSource.ReadByte() != -1) return false;
                            sourceReadEnded = true;
                        }
                    }
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
                    NLink.Tests.NativeContentParserProfileForTest.ReaderStage(8, sample);
                    sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
#endif
                    success = IsSourceVerified;
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
                    NLink.Tests.NativeContentParserProfileForTest.ReaderStage(9, sample);
#endif
                    return success;
                }
                catch { return false; }
                finally
                {
                    if (!measuring) work.Stop();
                    if (!success) { read = 0; Dispose(); }
                }
            }
        }
        internal bool SealedReadEnded(object reader)
        { lock (sync) { return Object.ReferenceEquals(sealedReader, reader) && reader != null && IsSourceVerified && sourceReadEnded; } }

        internal long SealedReadPosition(object reader)
        { lock (sync) { return Object.ReferenceEquals(sealedReader, reader) && reader != null && IsSourceVerified ? sourceReadPosition : -1; } }
        internal bool BeginProcessingWork(object reader)
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this) || !Object.ReferenceEquals(sealedReader, reader) || reader == null) return false;
                if (!IsSourceVerified || processingAttempted || sourceReadPosition != 0) { Dispose(); return false; }
                processingAttempted = true; processingRunning = true; work.Start(); return true;
            }
        }
        internal bool ProcessingWorkActive(object reader)
        { lock (sync) { return Object.ReferenceEquals(original, this) && reader != null && Object.ReferenceEquals(sealedReader, reader) && processingRunning && IsSourceVerified; } }
        // Rejection-only guard for already-buffered CPU work. This does not
        // validate native custody and must never authorize a read or capture.
        internal bool BufferedWorkCurrent(object reader)
        {
            lock (sync)
            {
                return Object.ReferenceEquals(original, this) && !disposed && reader != null &&
                    Object.ReferenceEquals(sealedReader, reader) && sourceVerified && processingAttempted && processingRunning &&
                    work.IsRunning && work.ElapsedMilliseconds <= MaximumActiveMilliseconds && !processingLifetime.IsCancellationRequested;
            }
        }
        // Phase selection and resource validation only. Neither replaces the
        // original finalizer/GUI scope checks or exports a custody receipt.
        internal bool ProcessingRunning { get { lock (sync) { return processingRunning; } } }
        internal bool ProcessingFilesCurrent(object reader)
        {
            lock (sync)
            {
                return BufferedWorkCurrent(reader) && adopted.Count == 2 &&
                    RetainedFilesCurrent() && BufferedWorkCurrent(reader);
            }
        }
        internal CancellationToken FinalizationLifetime(object reader)
        {
            lock (sync)
            {
                return Object.ReferenceEquals(original, this) && reader != null && Object.ReferenceEquals(sealedReader, reader) &&
                    processingAttempted && !processingRunning && sourceReadEnded && IsSourceVerified ?
                    processingLifetime.Token : new CancellationToken(true);
            }
        }
        internal bool MatchesRecoveryWriter(int process, long creation, string writer)
        {
            lock (sync)
            {
                if (!IsSourceVerified || !processingRunning) return false;
                foreach (var item in adopted)
                    if (item.Manifest.AppId == process && item.Manifest.AppCreation == creation && item.Manifest.Writer == writer) return true;
                return false;
            }
        }
        internal bool FinishProcessingWork(object reader)
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this) || !Object.ReferenceEquals(sealedReader, reader) || reader == null) return false;
                bool success = processingRunning && IsSourceVerified && sourceReadEnded;
                processingRunning = false; work.Stop();
                if (!success) Dispose();
                return success;
            }
        }

        // Internal bounded comparison only. The original GUI binding supplies
        // facts from its exact typed processor snapshot, never caller approval.
        internal bool TryMatchWriters(object reader, string[] writers, long[] lastSequences)
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this) || reader == null || !Object.ReferenceEquals(sealedReader, reader)) return false;
                bool success = false;
                try
                {
                    if (!IsSourceVerified || !processingRunning || !sourceReadEnded || writerMatchAttempted) return false;
                    writerMatchAttempted = true;
                    if (adopted.Count != 2 || writers == null || writers.Length != 2 || lastSequences == null || lastSequences.Length != 2 ||
                        String.Equals(writers[0], writers[1], StringComparison.Ordinal)) return false;
                    foreach (var contribution in adopted)
                    {
                        var manifest = contribution.Manifest;
                        int index = String.Equals(manifest.Writer, writers[0], StringComparison.Ordinal) ? 0 :
                            String.Equals(manifest.Writer, writers[1], StringComparison.Ordinal) ? 1 : -1;
                        if (index < 0 || lastSequences[index] <= 0 || lastSequences[index] != manifest.Drain.Last) return false;
                    }
                    success = IsSourceVerified; return success;
                }
                finally { if (!success) Dispose(); }
            }
        }

        // Original output I/O shares adoption/source work accounting. Preserve
        // an already running processor interval; never stop/reset its clock.
        internal bool TryRunRecoveryRead(Func<bool> read)
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this)) return false;
                bool success = false, measuring = work.IsRunning;
                work.Start();
                try { success = IsSourceVerified && read() && IsSourceVerified; return success; }
                catch { return false; }
                finally { if (!measuring) work.Stop(); if (!success) Dispose(); }
            }
        }

        // Internal transport operation, not a caller-provided authority gate.
        // The original GUI wrapper supplies its own lock-free closure checks.
        internal bool TrySealSource(Func<bool> closed, SafeFileHandle ledgerOrigin = null)
        {
            lock (sync)
            {
                if (!Object.ReferenceEquals(original, this) || disposed) return false;
                bool success = false, measuring = work.IsRunning;
                SafeFileHandle handle = null;
                var hashes = new List<SHA256>();
                work.Start();
                try
                {
                    if (sealAttempted || adopted.Count != 2 || !IsRetained || closed == null) return false;
                    if (ledgerOrigin != null && (!Matches(ledgerOrigin, adopted[0].Manifest.Source, true) ||
                        !Matches(ledgerOrigin, adopted[1].Manifest.Source, true))) return false;
                    sealAttempted = true; sealScope = closed;
                    if (!Current()) return false;
                    // One additional GUI-owned handle. No path lookup, and no
                    // extra reader per range. Existing writers/delete handles
                    // prevent this restrictive reopen and fail the whole seal.
                    handle = ReOpenFile(adopted[0].Files[0].SafeFileHandle, 0x80000000, 1, 0x08000000);
                    if (!Matches(handle, adopted[0].Manifest.Source, true) || !Matches(handle, adopted[1].Manifest.Source, true)) return false;
                    sealedSource = new FileStream(handle, FileAccess.Read); handle = null;
                    sealedLength = sealedSource.Length;
                    var ranges = new List<LocalFrontierFileManifest.Range>();
                    foreach (var item in adopted) ranges.AddRange(Ranges(item.Manifest));
                    if (ranges.Count < 2 || ranges.Count > 66) return false;
                    ranges.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                    for (int i = 0; i < ranges.Count; i++)
                    {
                        var range = ranges[i];
                        if (range.Offset < 0 || range.Length <= 0 || range.Offset > sealedLength - range.Length ||
                            (i != 0 && ranges[i - 1].Offset + ranges[i - 1].Length > range.Offset)) return false;
                        hashes.Add(SHA256.Create());
                    }
                    using (var full = SHA256.Create())
                    {
                        var buffer = new byte[32768]; long offset = 0;
                        while (offset < sealedLength)
                        {
                            if (!Current()) return false;
                            int read = sealedSource.Read(buffer, 0, (int)Math.Min(buffer.Length, sealedLength - offset));
                            if (read == 0 || !Current()) return false;
                            full.TransformBlock(buffer, 0, read, buffer, 0);
                            for (int i = 0; i < ranges.Count; i++)
                            {
                                long start = Math.Max(offset, ranges[i].Offset);
                                long end = Math.Min(offset + read, ranges[i].Offset + ranges[i].Length);
                                if (end > start) hashes[i].TransformBlock(buffer, (int)(start - offset), (int)(end - start), buffer, (int)(start - offset));
                            }
                            offset += read;
                        }
                        if (sealedSource.ReadByte() != -1 || !SealedSourceMatches() || !Current()) return false;
                        for (int i = 0; i < ranges.Count; i++)
                        {
                            hashes[i].TransformFinalBlock(new byte[0], 0, 0);
                            if (!SameHash(hashes[i].Hash, ranges[i].RawHash)) return false;
                        }
                        full.TransformFinalBlock(new byte[0], 0, 0);
                        sealedHash = BitConverter.ToString(full.Hash).Replace("-", "").ToLowerInvariant();
                    }
                    sealedRangeCount = ranges.Count; sourceVerified = true;
                    success = IsRetained; return success;
                }
                catch { return false; }
                finally
                {
                    if (handle != null) handle.Dispose();
                    foreach (var hash in hashes) hash.Dispose();
                    if (!measuring) work.Stop();
                    if (!success) Dispose();
                }
            }
        }

        private bool SealedSourceMatches()
        {
#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
            long sample = NLink.Tests.NativeContentParserProfileForTest.ReaderStart();
            bool result = sealedSource != null && sealedLength > 0 && sealedLength <= LocalFrontierFileManifest.MaximumSourceBytes &&
                sealedSource.Length == sealedLength && Matches(sealedSource.SafeFileHandle, adopted[0].Manifest.Source, true) &&
                Matches(sealedSource.SafeFileHandle, adopted[1].Manifest.Source, true);
            NLink.Tests.NativeContentParserProfileForTest.ReaderStage(18, sample); return result;
#else
            return sealedSource != null && sealedLength > 0 && sealedLength <= LocalFrontierFileManifest.MaximumSourceBytes &&
                sealedSource.Length == sealedLength && Matches(sealedSource.SafeFileHandle, adopted[0].Manifest.Source, true) &&
                Matches(sealedSource.SafeFileHandle, adopted[1].Manifest.Source, true);
#endif
        }
        private static bool SameHash(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != 32 || b.Length != 32) return false;
            for (int i = 0; i < 32; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
