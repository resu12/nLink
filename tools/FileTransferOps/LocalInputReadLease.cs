using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    // Read-only file identity. Not a parent/worker, evaluator or completion token.
    // One reader owns verification; Dispose may invalidate the lease concurrently.
    public sealed partial class LocalInputReadLease : IDisposable
    {
        public const int MaximumVerificationRecords = LocalPublicationInputLimits.InputRecords;
        public const long MaximumVerificationCodeUnits = LocalPublicationInputLimits.InputCodeUnits;
        public const int MaximumLineCodeUnits = LocalPublicationInputLimits.LineCodeUnits;
        public const long MaximumWriterBytes = LocalPublicationInputLimits.FileBytes;
        public const int MaximumVerificationMilliseconds = LocalPublicationInputLimits.WriterMilliseconds;
        private FileStream anchor;
        private readonly long expectedLines;
        private int disposed;
        private int verification; // 0 pending, 1 reading, 2 verified, 3 omitted
        private Stopwatch verificationClock;
        public string Path { get; private set; }
        public string ExpectedSha256 { get; private set; }
        public string FileIdentity { get; private set; }
        public long ByteCount { get; private set; }
        public long VerifiedLineCount { get; private set; }
        public long VerificationRecordVisits { get; private set; }
        public long VerificationCodeUnitVisits { get; private set; }
        public string OmissionReason { get; private set; }
        public bool IsActive { get { return Volatile.Read(ref disposed) == 0; } }
        public bool IsVerified { get { return IsActive && Volatile.Read(ref verification) == 2; } }

        private LocalInputReadLease(FileStream stream, string path, string sha256, long lines, bool verified)
        {
            FileInformation info;
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out info)) throw new IOException("input_file_identity_unavailable");
            anchor = stream; Path = System.IO.Path.GetFullPath(path); ExpectedSha256 = sha256;
            ByteCount = ((long)info.SizeHigh << 32) | info.SizeLow;
            FileIdentity = info.Volume.ToString("x8") + ":" + info.IndexHigh.ToString("x8") + info.IndexLow.ToString("x8");
            expectedLines = lines; VerifiedLineCount = -1; verification = verified ? 2 : 0;
        }

        // Used instead of, not after, the parent's existing source-hash pass.
        // Optional identity acquisition failure still returns the original hash.
        public static HashReadResult HashAndHold(string path)
        {
            FileStream stream = File.OpenRead(path);
            try
            {
                string digest;
                using (var sha = SHA256.Create()) digest = Hex(sha.ComputeHash(stream));
                LocalInputReadLease lease = null;
                try { lease = new LocalInputReadLease(stream, path, digest, -1, true); }
                catch { }
                if (lease != null) stream = null;
                return new HashReadResult(digest, lease);
            }
            finally { if (stream != null) stream.Dispose(); }
        }

        // Pin a writer candidate without a pre-launch scan. Its declared hash
        // is NOT verified until the bounded evidence traversal reaches EOF.
        public static LocalInputReadLease OpenExpected(string path, string expectedSha256, long expectedLineCount)
        {
            if (expectedSha256 == null || !Regex.IsMatch(expectedSha256, @"\A[0-9a-f]{64}\z") ||
                expectedLineCount < 1 || expectedLineCount > MaximumVerificationRecords) return null;
            FileStream stream = null;
            try
            {
                stream = File.OpenRead(path);
                if (stream.Length > MaximumWriterBytes) return null;
                var lease = new LocalInputReadLease(stream, path, expectedSha256, expectedLineCount, false);
                stream = null; return lease;
            }
            catch { return null; }
            finally { if (stream != null) stream.Dispose(); }
        }

        public FileStream TryOpenVerifiedRead()
        {
            return IsVerified ? OpenHeldRead() : null;
        }

        public IEnumerable<string> VerifyAndReadLines()
        {
            if (!IsActive || Interlocked.CompareExchange(ref verification, 1, 0) != 0)
                throw new InvalidOperationException("input_verification_not_available");
            bool complete = false;
            verificationClock = Stopwatch.StartNew();
            try
            {
                using (var stream = OpenHeldRead())
                using (var sha = SHA256.Create())
                {
                    if (stream == null) throw new IOException("input_owned_read_unavailable");
                    using (var hashed = new CryptoStream(stream, sha, CryptoStreamMode.Read))
                    using (var reader = new StreamReader(hashed, new UTF8Encoding(false, true), true, 4096))
                    {
                        string line;
                        while ((line = ReadBoundedLine(reader)) != null)
                        {
                            if (!IsActive) { OmissionReason = "input_owner_inactive"; yield break; }
                            yield return line;
                        }
                        if (!IsActive || VerificationRecordVisits != expectedLines || Hex(sha.Hash) != ExpectedSha256)
                        { OmissionReason = "input_verification_mismatch_or_inactive"; yield break; }
                        VerifiedLineCount = VerificationRecordVisits;
                        complete = Interlocked.CompareExchange(ref verification, 2, 1) == 1 && IsActive;
                    }
                }
            }
            finally
            {
                if (!complete)
                {
                    Interlocked.CompareExchange(ref verification, 3, 1);
                    if (OmissionReason == null) OmissionReason = "input_verification_incomplete";
                }
            }
        }

        private string ReadBoundedLine(StreamReader reader)
        {
            CheckVerification();
            if (reader.Peek() < 0) return null;
            if (VerificationRecordVisits >= expectedLines)
            { OmissionReason = "input_verification_record_count"; throw new InvalidDataException(OmissionReason); }
            VerificationRecordVisits++;
            var line = new StringBuilder(256);
            int value;
            while ((value = reader.Read()) >= 0)
            {
                if (value == '\n') break;
                if (value == '\r') { if (reader.Peek() == '\n') reader.Read(); break; }
                if (line.Length >= MaximumLineCodeUnits || VerificationCodeUnitVisits >= MaximumVerificationCodeUnits)
                { OmissionReason = "input_verification_code_unit_limit"; throw new InvalidDataException(OmissionReason); }
                line.Append((char)value); VerificationCodeUnitVisits++;
                if ((line.Length & 2047) == 0) CheckVerification();
            }
            CheckVerification();
            return line.ToString();
        }

        private void CheckVerification()
        {
            if (!IsActive) OmissionReason = "input_owner_inactive";
            else if (verificationClock.ElapsedMilliseconds > MaximumVerificationMilliseconds) OmissionReason = "input_verification_time_limit";
            else return;
            throw new InvalidDataException(OmissionReason);
        }

        private FileStream OpenHeldRead()
        {
            SafeFileHandle handle = null;
            try
            {
                if (!IsActive) return null;
                handle = frontierNative ? DuplicateFrontierRead(anchor.SafeFileHandle) : ReOpenFile(anchor.SafeFileHandle, 0x80000000, 1, 0x08000000);
                if (handle == null) return null;
                if (handle.IsInvalid || !IsActive) return null;
                var stream = new FileStream(handle, FileAccess.Read);
                handle = null; return stream;
            }
            catch { return null; }
            finally { if (handle != null) handle.Dispose(); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            anchor.Dispose();
        }

        private static string Hex(byte[] bytes)
        { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }

        public sealed class HashReadResult
        {
            public string Sha256 { get; private set; }
            public LocalInputReadLease Lease { get; private set; }
            internal HashReadResult(string digest, LocalInputReadLease lease) { Sha256 = digest; Lease = lease; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle ReOpenFile(SafeFileHandle handle, uint access, uint share, uint flags);
    }
}
