using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace NLink.Tools
{
    public static class LocalPublicationInputLimits
    {
        public const long FileBytes = 268435456;
        public const int InputRecords = 262144;
        public const long InputCodeUnits = 268435456;
        public const int LineCodeUnits = 262144;
        public const long ProducerRecords = 2097152;
        // Cumulative evaluation inspections, separate from producer admission.
        public const long EvaluationVisits = 1073741824;
        public const int EvaluationMilliseconds = 60000;
        public const long ProducerCodeUnits = 1073741824;
        public const int ProducerMilliseconds = 20000;
        public const int NativeMilliseconds = 2000;
        public const int WriterMilliseconds = 5000;
        public const int MembershipMilliseconds = 5000;
        public const int FailureBucketRecords = 16384;
        public const int OperationRows = 8192;
    }

    // Input identity only: never a completed-view, publication, or authority proof.
    // One producer thread owns Begin/Append/End/Take/Dispose; Cancel may be concurrent.
    public sealed class LocalNativeInputCapture : IDisposable
    {
        public const int MaximumRecords = LocalPublicationInputLimits.InputRecords;
        public const long MaximumCodeUnits = LocalPublicationInputLimits.InputCodeUnits;
        public const int MaximumLineCodeUnits = LocalPublicationInputLimits.LineCodeUnits;
        public const int MaximumActiveMilliseconds = LocalPublicationInputLimits.NativeMilliseconds;
        public const int BufferBytes = 4096;
        public const string Domain = "nlink-ordered-utf16le-v1";
        private static readonly byte[] Prefix = Encoding.ASCII.GetBytes(Domain + "\0");
        private static readonly string[] Roles = { "fullCapture", "fullSnapshot", "compactCapture", "compactSnapshot" };
        private readonly Guid requestId;
        private readonly byte[] buffer = new byte[BufferBytes];
        private readonly List<NativeInput> inputs = new List<NativeInput>(4);
        private SHA256 hash;
        private string role;
        private int expectedRecords;
        private int currentRecords;
        private long currentCodeUnits;
        private long activeTicks;
        private long started;
        private int state; // 0 open, 1 taken, 2 omitted/cancelled
        private NativeInputs issued;
        private Func<bool> current;
        private Stopwatch batchClock;
        public long RecordVisits { get; private set; }
        public long CodeUnitVisits { get; private set; }
        public long FramedByteVisits { get; private set; }
        public string OmissionReason { get; private set; }
        public double ActiveMilliseconds { get { return activeTicks * 1000.0 / Stopwatch.Frequency; } }

        public LocalNativeInputCapture(Guid requestId)
        {
            if (requestId == Guid.Empty) throw new ArgumentException("A parent request is required.");
            this.requestId = requestId;
        }

        // Input identity only. The primary's independent custody still binds
        // these inputs to the exact source, original arrays and evaluations.
        public NativeInputs CaptureInputs(string[][] arrays, Func<bool> isCurrent)
        {
            if (Volatile.Read(ref state) != 0) return null;
            try
            {
                if (hash != null || inputs.Count != 0 || RecordVisits != 0 || arrays == null ||
                    arrays.Length != Roles.Length || isCurrent == null)
                { Omit("batch_shape_or_replay"); return null; }
                var originals = (string[][])arrays.Clone();
                long records = 0;
                for (int i = 0; i < originals.Length; i++)
                {
                    if (originals[i] == null) { Omit("batch_shape_or_replay"); return null; }
                    bool shared = false;
                    for (int j = 0; j < i; j++) if (Object.ReferenceEquals(originals[i], originals[j])) { shared = true; break; }
                    if (!shared && (records += originals[i].Length) > MaximumRecords)
                    { Omit("input_shape_or_record_limit"); return null; }
                }
                current = isCurrent;
                // Includes native loop/alias bookkeeping, not only hash API calls.
                batchClock = Stopwatch.StartNew();
                for (int i = 0; i < originals.Length; i++)
                {
                    if (!Start()) return null;
                    int previous = -1;
                    try
                    {
                        for (int j = 0; j < i; j++) if (Object.ReferenceEquals(originals[i], originals[j])) { previous = j; break; }
                        if (previous >= 0)
                        {
                            var input = inputs[previous];
                            inputs.Add(new NativeInput(Roles[i], input.RecordCount, input.CodeUnitCount, input.Sha256));
                        }
                    }
                    finally { Stop(); }
                    if (previous >= 0) continue;
                    if (!BeginInput(Roles[i], originals[i].Length)) return null;
                    for (int j = 0; j < originals[i].Length; j++)
                        if (!AppendLine(j + 1, originals[i][j])) return null;
                    if (!EndInput()) return null;
                }
                return Take();
            }
            catch { Omit("capture_failed"); return null; }
            finally
            {
                if (batchClock != null) { batchClock.Stop(); activeTicks = Math.Max(activeTicks, batchClock.ElapsedTicks); batchClock = null; }
                current = null;
                if (Volatile.Read(ref state) == 2 && hash != null) { hash.Dispose(); hash = null; }
            }
        }

        public bool BeginInput(string inputRole, int recordCount)
        {
            if (!Start()) return false;
            try
            {
                if (hash != null || Array.IndexOf(Roles, inputRole) < 0 || recordCount < 0 ||
                    RecordVisits + recordCount > MaximumRecords || inputs.Exists(input => input.Role == inputRole))
                    return Omit("input_shape_or_record_limit");
                hash = SHA256.Create(); role = inputRole; expectedRecords = recordCount;
                currentRecords = 0; currentCodeUnits = 0;
                Array.Copy(Prefix, buffer, Prefix.Length);
                hash.TransformBlock(buffer, 0, Prefix.Length, buffer, 0);
                FramedByteVisits += Prefix.Length;
                WriteLength((ulong)recordCount);
                return Check();
            }
            catch { return Omit("capture_failed"); }
            finally { Stop(); }
        }

        public bool AppendLine(int oneBasedOrdinal, string line)
        {
            if (!Start()) return false;
            try
            {
                if (hash == null || currentRecords >= expectedRecords || oneBasedOrdinal != currentRecords + 1 ||
                    line == null || line.Length > MaximumLineCodeUnits ||
                    line.Length > MaximumCodeUnits - CodeUnitVisits)
                    return Omit("line_shape_order_or_code_unit_limit");
                RecordVisits++;
                WriteLength((ulong)line.Length);
                for (int offset = 0; offset < line.Length;)
                {
                    // Start checked the owner for this record's first block.
                    if (offset != 0 && !Check()) return false;
                    int count = Math.Min(BufferBytes / 2, line.Length - offset);
                    for (int i = 0; i < count; i++)
                    {
                        char value = line[offset + i];
                        buffer[2 * i] = (byte)value;
                        buffer[2 * i + 1] = (byte)(value >> 8);
                    }
                    hash.TransformBlock(buffer, 0, count * 2, buffer, 0);
                    CodeUnitVisits += count; currentCodeUnits += count;
                    FramedByteVisits += count * 2; offset += count;
                }
                currentRecords++;
                // No new record/block is admitted here. Keep cancellation/time
                // checks; the next record, EndInput and Take recheck the owner.
                return Check(false);
            }
            catch { return Omit("capture_failed"); }
            finally { Stop(); }
        }

        public bool EndInput()
        {
            if (!Start()) return false;
            try
            {
                if (hash == null || currentRecords != expectedRecords) return Omit("input_incomplete");
                hash.TransformFinalBlock(buffer, 0, 0);
                string digest = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                hash.Dispose(); hash = null;
                if (!Check()) return false;
                inputs.Add(new NativeInput(role, currentRecords, currentCodeUnits, digest));
                return true;
            }
            catch { return Omit("capture_failed"); }
            finally { Stop(); }
        }

        public NativeInputs Take()
        {
            if (!Start()) return null;
            try
            {
                if (hash != null || inputs.Count != Roles.Length) { Omit("inputs_incomplete"); return null; }
                var candidate = new NativeInputs(requestId, inputs.ToArray());
                if (!Check() || Interlocked.CompareExchange(ref state, 1, 0) != 0) return null;
                issued = candidate;
                return candidate;
            }
            catch { Omit("capture_failed"); return null; }
            finally { Stop(); }
        }

        public bool Owns(NativeInputs candidate)
        {
            return Volatile.Read(ref state) == 1 && candidate != null && Object.ReferenceEquals(candidate, issued);
        }

        public void Cancel()
        {
            if (Interlocked.Exchange(ref state, 2) != 2) OmissionReason = "cancelled";
        }

        public void Dispose()
        {
            Cancel();
            if (hash != null) { hash.Dispose(); hash = null; }
            inputs.Clear(); issued = null;
        }

        private bool Start()
        {
            if (Volatile.Read(ref state) != 0) return false;
            started = Stopwatch.GetTimestamp();
            if (Check()) return true;
            Stop(); return false;
        }

        private bool Check(bool checkOwner = true)
        {
            if (Volatile.Read(ref state) != 0) return false;
            try { if (checkOwner && current != null && !current()) return Omit("input_owner_inactive"); }
            catch { return Omit("input_owner_inactive"); }
            long ticks = batchClock == null ? activeTicks + Stopwatch.GetTimestamp() - started : Math.Max(activeTicks, batchClock.ElapsedTicks);
            if (ticks * 1000.0 / Stopwatch.Frequency > MaximumActiveMilliseconds)
                return Omit("active_work_budget");
            return true;
        }

        private void Stop()
        {
            activeTicks += Stopwatch.GetTimestamp() - started;
            if (Volatile.Read(ref state) == 2 && hash != null) { hash.Dispose(); hash = null; }
        }

        private bool Omit(string reason)
        {
            OmissionReason = reason; Interlocked.Exchange(ref state, 2); return false;
        }

        private void WriteLength(ulong value)
        {
            for (int i = 0; i < 8; i++) { buffer[i] = (byte)value; value >>= 8; }
            hash.TransformBlock(buffer, 0, 8, buffer, 0); FramedByteVisits += 8;
        }

        public sealed class NativeInput
        {
            public string Role { get; private set; }
            public int RecordCount { get; private set; }
            public long CodeUnitCount { get; private set; }
            public string Sha256 { get; private set; }
            internal NativeInput(string role, int count, long units, string digest)
            { Role = role; RecordCount = count; CodeUnitCount = units; Sha256 = digest; }
        }

        public sealed class NativeInputs
        {
            private readonly NativeInput[] inputs;
            public Guid RequestId { get; private set; }
            internal NativeInputs(Guid request, NativeInput[] values) { RequestId = request; inputs = values; }
            public NativeInput Get(string role) { return Array.Find(inputs, input => input.Role == role); }
        }
    }
}
