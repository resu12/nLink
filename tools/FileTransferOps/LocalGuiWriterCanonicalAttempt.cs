using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        // The caller supplies the exact argv about to launch, not a separately
        // reconstructed scenario description. Only non-secret fields are retained.
        public bool TryBindCanonicalAttempt(int attempt, string[] arguments, string proofMode, string hostPath)
        {
            try
            {
                if (!Object.ReferenceEquals(original, this) || attempt != 1 ||
                    scenario != run + ":regular-v4-live-activation-off-on-off-256mb:1" ||
                    proofMode != "RegularActivationCycle" || arguments == null || arguments.Length != 28) return false;
                var projection = new string[12];
                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < arguments.Length; i += 2)
                {
                    if (arguments[i] == null || !seen.Add(arguments[i])) return false;
                    var value = arguments[i + 1];
                    switch (arguments[i].ToLowerInvariant())
                    {
                        case "-exepath": projection[0] = value; break;
                        case "-direction": projection[1] = value; break;
                        case "-payermode": projection[2] = value; break;
                        case "-fault": projection[3] = value; break;
                        case "-routemode": projection[4] = value; break;
                        case "-payloadsize": projection[5] = value; break;
                        case "-livetogglesequence": projection[6] = value; break;
                        case "-timeoutseconds": projection[7] = value; break;
                        case "-progresstimeoutseconds": projection[8] = value; break;
                        case "-artifactdir":
                            if (!String.Equals(Path.GetFullPath(value), root, StringComparison.OrdinalIgnoreCase)) return false;
                            break;
                        case "-retainedanalysisowner": if (value != "RouteAcceptance") return false; break;
                        case "-walletpath": case "-walletpassword": case "-sidecarpath": break;
                        default: return false;
                    }
                }
                projection[9] = proofMode;
                projection[10] = "measured_live_regular_activation_cycle";
                projection[11] = hostPath;
                return TryBindAttemptProjection(attempt, projection);
            }
            catch { return false; }
        }

        // Reserve the tested bounded request region; never overwrite original
        // registration or completion bytes. This is diagnostic scope only.
        private const int AttemptOffset = HeaderBytes + 8192, AttemptCapacity = 4096;
        private const int AttemptMagic = 0x4e415431;
        private bool attemptBound;

        private bool TryBindAttemptProjection(int attempt, string[] projection)
        {
            if (!Object.ReferenceEquals(original, this) || !relayMode || attempt != 1 ||
                !ValidAttemptProjection(projection)) return false;
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true)))
            using (var current = Process.GetCurrentProcess())
            {
                writer.Write(RequestId.ToByteArray()); writer.Write(Name); writer.Write(current.Id);
                writer.Write(current.StartTime.ToUniversalTime().ToFileTimeUtc()); writer.Write(attempt);
                foreach (var value in projection) writer.Write(value);
                writer.Flush(); return WriteAttempt(bytes.ToArray());
            }
        }

        private bool WriteAttempt(byte[] bytes)
        {
            bool held = false;
            try
            {
                if (!Object.ReferenceEquals(original, this) || attemptBound || workerExit != null ||
                    Volatile.Read(ref sealedState) != 0 || bytes.Length > AttemptCapacity - 8) return false;
                held = gate.WaitOne(0);
                if (!held || closed.WaitOne(0) || view.ReadInt32(20) > 8192 || view.ReadInt32(AttemptOffset) != 0) return false;
                attemptBound = true;
                view.WriteArray(AttemptOffset + 8, bytes, 0, bytes.Length);
                view.Write(AttemptOffset + 4, bytes.Length); view.Write(AttemptOffset, AttemptMagic);
                return true;
            }
            catch (AbandonedMutexException) { held = true; return false; }
            catch { return false; }
            finally { Release(gate, held); }
        }

        private static byte[] ReadAttempt(MemoryMappedViewAccessor view)
        {
            if (view.ReadInt32(20) > 8192 || view.ReadInt32(AttemptOffset) != AttemptMagic) return null;
            int length = view.ReadInt32(AttemptOffset + 4);
            if (length < 32 || length > AttemptCapacity - 8) return null;
            var bytes = new byte[length];
            return view.ReadArray(AttemptOffset + 8, bytes, 0, length) == length ? bytes : null;
        }

        private static bool ValidAttemptProjection(string[] p)
        {
            int timeout, progress;
            if (p == null || p.Length != 12) return false;
            foreach (var value in p) if (!TextValid(value, 1024)) return false;
            return CanonicalPath(p[0]) && p[1] == "helpee-to-helper" && p[2] == "helpee" &&
                p[3] == "switch-off" && p[4] == "live-regular-activation-cycle" && p[5] == "256MiB" &&
                p[6] == "on,off,on,off" && Int32.TryParse(p[7], out timeout) && timeout > 0 &&
                Int32.TryParse(p[8], out progress) && progress > 0 && progress <= timeout &&
                p[9] == "RegularActivationCycle" && p[10] == "measured_live_regular_activation_cycle" && CanonicalPath(p[11]);
        }

        private static bool SameProjection(byte[] bytes, string[] projection, int attempt,
            out Guid origin, out string name, out int parentId, out long parentCreation)
        {
            origin = Guid.Empty; name = null; parentId = 0; parentCreation = 0;
            if (bytes == null || !ValidAttemptProjection(projection)) return false;
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                origin = new Guid(reader.ReadBytes(16)); name = reader.ReadString(); parentId = reader.ReadInt32(); parentCreation = reader.ReadInt64();
                if (origin == Guid.Empty || !System.Text.RegularExpressions.Regex.IsMatch(name, @"\ALocal\\nlink-gui-relay-[0-9a-f]{32}\z") ||
                    parentId <= 0 || parentCreation <= 0 || reader.ReadInt32() != attempt) return false;
                foreach (var value in projection) if (reader.ReadString() != value) return false;
                return stream.Position == stream.Length;
            }
        }

    }
}

