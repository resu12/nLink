namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    // Diagnostic lifecycle only. Holds original completion tasks, never transfer
    // contexts or replacement authority. Capacity is lifetime, not concurrent.
    internal sealed class FrontierProducerRegistry
    {
        private readonly object gate = new();
        private readonly FrontierProducerRegistry original;
        private readonly HashSet<ServiceScope> services = new(ReferenceEqualityComparer.Instance);
        private readonly List<Task> completions = [];
        private bool sealedForDrain, failed;

        internal FrontierProducerRegistry() { original = this; }
        internal bool IsCurrent { get { lock (gate) { return ReferenceEquals(original, this) && !failed; } } }

        internal ServiceScope? RegisterService()
        {
            lock (gate)
            {
                if (!ReferenceEquals(original, this)) { return null; }
                if (failed || sealedForDrain || services.Count == 16) { failed = true; return null; }
                var scope = new ServiceScope(this);
                services.Add(scope);
                return scope;
            }
        }

        private object CreateEmitter(ServiceScope scope)
        {
            var emitter = new CoordinatorSnapshotStreamEmitter();
            lock (gate)
            {
                if (!ReferenceEquals(original, this) || !services.Contains(scope)) { return emitter; }
                if (failed || sealedForDrain || scope.Closed || completions.Count == 64) { failed = true; return emitter; }
                completions.Add(emitter.FinalSubmission);
            }
            return emitter;
        }

        private void Close(ServiceScope scope)
        {
            lock (gate)
            {
                if (ReferenceEquals(original, this) && services.Contains(scope)) { scope.Closed = true; }
            }
        }

        internal bool SealAndWait(TimeSpan timeout)
        {
            Task[] pending;
            lock (gate)
            {
                if (!ReferenceEquals(original, this) || sealedForDrain) { return false; }
                sealedForDrain = true;
                if (failed || services.Any(s => !s.Closed)) { failed = true; return false; }
                pending = completions.ToArray();
            }
            // No registry/service/catalog/native gate is held while waiting.
            var ready = Task.WhenAll(pending).Wait(timeout);
            lock (gate) { failed |= !ready; return !failed; }
        }

        internal sealed class ServiceScope(FrontierProducerRegistry owner)
        {
            internal bool Closed;
            internal object CreateEmitter() => owner.CreateEmitter(this);
            internal void Close() => owner.Close(this);
        }
    }
}
