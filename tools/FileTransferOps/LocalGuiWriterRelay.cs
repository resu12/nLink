using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        private const int RelayMagic = 0x4e475232;
        private static int relayAttachAttempted;

        // One hop: RouteAcceptance owns this request; its exact Wrapper child
        // may forward only a GUI completion that it actually adopted itself.
        public static LocalGuiWriterRequest TryCreateRelay(Guid id, string run, string scenario, string artifactRoot)
        { return TryCreateCore(id, run, scenario, artifactRoot, true); }

        public static Relay TryAttachRelay(string name, Guid id, string run, string scenario, string artifactRoot)
        {
            var owner = TryAttachCore(name, id, run, scenario, artifactRoot, true);
            return owner == null ? null : new Relay(owner);
        }

        public RelayedCompletion TakeRelayAfterExit()
        {
            bool acceptedNative = false;
            try
            {
                if (Object.ReferenceEquals(original, this) && frontierRelayCustody != null) frontierRelayCustody.BeginTake();
                var clock = Stopwatch.StartNew();
                var body = TakeBodyAfterExit(true);
                if (body == null) return null;
                using (var bytes = new MemoryStream(body, false))
                using (var reader = new BinaryReader(bytes, new UTF8Encoding(false, true)))
                {
                    var writer = Completion.ReadFrom(reader, run, scenario);
                    if (writer == null || writer.ArtifactRoot != root || bytes.Position != bytes.Length ||
                        clock.ElapsedMilliseconds > MaximumCompletionMilliseconds) return null;
                    // Deliberately not MarkAcceptedHere: a relayed/historical
                    // completion is not a fresh GUI adoption by this process.
                    var result = new RelayedCompletion(RequestId, workerId, workerCreation, writer);
                    if (frontierRelayCustody != null && frontierRelayCustody.Accept(body, result))
                    { result.frontierCustody = frontierRelayCustody; acceptedNative = true; }
                    return result;
                }
            }
            catch { return null; }
            finally { if (Object.ReferenceEquals(original, this) && !acceptedNative) RetireFrontierRelayCustody(); }
        }

        public sealed partial class Relay : IDisposable
        {
            internal readonly Finalizer owner;
            private readonly Relay original;
            internal int attempted;
            internal Relay(Finalizer owner) { this.owner = owner; original = this; }

            public bool Forward(Completion completion)
            {
                if (!Object.ReferenceEquals(original, this) || Interlocked.Exchange(ref attempted, 1) != 0) return false;
                bool held = false;
                try
                {
                    var clock = Stopwatch.StartNew();
                    if (!owner.IsCurrent || completion == null || !completion.WasAcceptedHere() ||
                        completion.AcceptanceRunId != owner.run || completion.ScenarioRunId != owner.scenario ||
                        completion.ArtifactRoot != owner.root) return false;
                    byte[] body;
                    using (var bytes = new MemoryStream())
                    using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true)))
                    { completion.WriteTo(writer); writer.Flush(); body = bytes.ToArray(); }
                    if (body.Length < 1 || body.Length > MaximumCompletionBytes || clock.ElapsedMilliseconds > MaximumCompletionMilliseconds) return false;
                    held = owner.gate.WaitOne(0);
                    if (!held || !owner.IsCurrent || owner.view.ReadInt32(0) != RelayMagic ||
                        owner.view.ReadInt32(48) != 1 || ReadId(owner.view) != owner.id) return false;
                    owner.view.Write(52, body.Length);
                    owner.view.WriteArray(HeaderBytes + RequestBytes, body, 0, body.Length);
                    if (!owner.IsCurrent || clock.ElapsedMilliseconds > MaximumCompletionMilliseconds) return false;
                    owner.view.Write(48, 2);
                    if (frontierRelayHandoff != null) frontierRelayHandoff.Forwarded(body);
                    return true;
                }
                catch (AbandonedMutexException) { held = true; return false; }
                catch { return false; }
                finally { Release(owner.gate, held); }
            }

            public void Dispose()
            {
                if (!Object.ReferenceEquals(original, this)) return;
                lock (frontierRelaySync)
                {
                    if (frontierRelayHandoff != null) frontierRelayHandoff.Close();
                    owner.Dispose();
                }
            }
        }

        public sealed class RelayedCompletion
        {
            internal FrontierRelayCustody frontierCustody;
            public FrontierRelayCustody TryTakeFrontierCustody()
            { return frontierCustody == null ? null : frontierCustody.Detach(this); }
            public Guid RelayRequestId { get; private set; }
            public int WrapperProcessId { get; private set; }
            public long WrapperProcessCreationFileTime { get; private set; }
            public Completion WriterContext { get; private set; }
            internal RelayedCompletion(Guid id, int process, long creation, Completion writer)
            { RelayRequestId = id; WrapperProcessId = process; WrapperProcessCreationFileTime = creation; WriterContext = writer; }
        }
    }
}
