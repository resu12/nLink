using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        private const int FailedRelayBodyMagic = 0x4e474631;

        // Failure diagnostics have a distinct type. Never issue a successful GUI
        // completion or native publication custody from a nonzero process exit.
        public FailedCapture TakeFailedAfterExit()
        {
            try
            {
                var body = TakeBodyAfterExit(false, true);
                int code;
                if (body == null || !GetExitCodeProcess(workerExit.SafeWaitHandle, out code) || code == 0) return null;
                var writer = Decode(body, RequestId, run, scenario, root, workerId, workerCreation);
                return FailedCapture.Create(writer, code, true, null);
            }
            catch { return null; }
        }

        public FailedCapture TakeFailedRelayAfterExit()
        {
            try
            {
                var body = TakeBodyAfterExit(true, true);
                if (body == null) return null;
                using (var bytes = new MemoryStream(body, false))
                using (var reader = new BinaryReader(bytes, new UTF8Encoding(false, true)))
                {
                    if (reader.ReadInt32() != FailedRelayBodyMagic) return null;
                    int guiExit = reader.ReadInt32();
                    string identity = reader.ReadString();
                    var writer = Completion.ReadFrom(reader, run, scenario);
                    if (guiExit == 0 || writer == null || writer.ArtifactRoot != root || bytes.Position != bytes.Length) return null;
                    return FailedCapture.Create(writer, guiExit, false, identity);
                }
            }
            catch { return null; }
        }

        public sealed partial class Relay
        {
            public bool ForwardFailed(FailedCapture capture)
            {
                if (!Object.ReferenceEquals(original, this) || Interlocked.Exchange(ref attempted, 1) != 0) return false;
                bool held = false;
                try
                {
                    if (!owner.IsCurrent || capture == null || !capture.CanForward(owner.run, owner.scenario, owner.root)) return false;
                    byte[] body;
                    using (var bytes = new MemoryStream())
                    using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true)))
                    {
                        writer.Write(FailedRelayBodyMagic); writer.Write(capture.GuiExitCode);
                        writer.Write(capture.SourceIdentity); capture.WriteWriter(writer);
                        writer.Flush(); body = bytes.ToArray();
                    }
                    if (body.Length > MaximumCompletionBytes) return false;
                    held = owner.gate.WaitOne(0);
                    if (!held || !owner.IsCurrent || owner.view.ReadInt32(0) != RelayMagic ||
                        owner.view.ReadInt32(48) != 1 || ReadId(owner.view) != owner.id) return false;
                    owner.view.Write(52, body.Length);
                    owner.view.WriteArray(HeaderBytes + RequestBytes, body, 0, body.Length);
                    if (!owner.IsCurrent || !capture.IsCurrent) return false;
                    owner.view.Write(48, 2);
                    return true;
                }
                catch (AbandonedMutexException) { held = true; return false; }
                catch { return false; }
                finally { Release(owner.gate, held); }
            }
        }

        // No public constructor, writer export, success-completion conversion or
        // publication API. The original parent may consume this capture once.
        public sealed class FailedCapture : IDisposable
        {
            private readonly FailedCapture original;
            private readonly Completion writer;
            private readonly LocalInputReadLease source;
            private readonly bool forwardOnly;
            private readonly int processId;
            private readonly long processCreation;
            private int claimed, disposed;
            private FailedCapture(Completion writer, LocalInputReadLease source, int exit, bool forwardOnly)
            {
                original = this; this.writer = writer; this.source = source; this.forwardOnly = forwardOnly; GuiExitCode = exit;
                using (var process = Process.GetCurrentProcess())
                { processId = process.Id; processCreation = process.StartTime.ToUniversalTime().ToFileTimeUtc(); }
            }
            public int GuiExitCode { get; private set; }
            public string AnalysisAttemptId { get; private set; }
            public int GuiProcessId { get { return writer.GuiProcessId; } }
            public string SourcePath { get { return source.Path; } }
            public string SourceSha256 { get { return source.ExpectedSha256; } }
            public long SourceLines { get { return source.VerifiedLineCount; } }
            internal string SourceIdentity { get { return source.FileIdentity; } }
            public bool IsCurrent
            {
                get
                {
                    if (!Object.ReferenceEquals(original, this) || Volatile.Read(ref disposed) != 0 || !source.IsVerified) return false;
                    using (var process = Process.GetCurrentProcess())
                        return process.Id == processId && process.StartTime.ToUniversalTime().ToFileTimeUtc() == processCreation;
                }
            }
            internal static FailedCapture Create(Completion writer, int exit, bool forwardOnly, string identity)
            {
                LocalInputReadLease source = null;
                try
                {
                    if (writer == null || exit == 0 || writer.Outputs == null || writer.Outputs.Count != 4) return null;
                    var output = writer.Outputs[3];
                    source = LocalInputReadLease.OpenExpected(output.Path, output.Sha256, output.LineCount);
                    if (source == null || source.ByteCount != output.ByteCount || (identity != null && identity != source.FileIdentity)) return null;
                    foreach (var line in source.VerifyAndReadLines()) { }
                    if (!source.IsVerified || !writer.MatchesSource(writer.AcceptanceRunId, writer.ScenarioRunId,
                        source.Path, source.ExpectedSha256, source.ByteCount, source.VerifiedLineCount)) return null;
                    var result = new FailedCapture(writer, source, exit, forwardOnly); source = null; return result;
                }
                catch { return null; }
                finally { if (source != null) source.Dispose(); }
            }
            public bool Matches(string run, string scenario, string root)
            { return IsCurrent && writer.AcceptanceRunId == run && writer.ScenarioRunId == scenario && writer.ArtifactRoot == root; }
            internal bool CanForward(string run, string scenario, string root)
            { return forwardOnly && Matches(run, scenario, root) && Interlocked.CompareExchange(ref claimed, 1, 0) == 0; }
            internal void WriteWriter(BinaryWriter target) { writer.WriteTo(target); }
            public LocalRetainedSourceRequest TryPreparePrimary(Guid attempt, string run, string scenario, string root)
            {
                if (forwardOnly || !Matches(run, scenario, root) || Interlocked.CompareExchange(ref claimed, 1, 0) != 0) return null;
                AnalysisAttemptId = attempt.ToString("N");
                // Source/writer evidence only. This does not request publication,
                // native frontier custody, or any GUI-success authority.
                return LocalRetainedSourceRequest.TryCreateWithWriter(attempt, "RouteAcceptance", run, scenario, source, SourceLines, writer);
            }
            public void Dispose()
            {
                if (!Object.ReferenceEquals(original, this) || Interlocked.Exchange(ref disposed, 1) != 0) return;
                source.Dispose();
            }
        }
    }
}
