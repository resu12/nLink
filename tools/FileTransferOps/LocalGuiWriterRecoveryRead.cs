using System;
using System.Collections.ObjectModel;
using System.IO;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierLaunchOwner
        {
            internal FrontierAppLaunch[] GetRecoveryLaunches(FrontierSealedFiles value)
            { lock (sync) { return OwnsSealedFiles(value) ? apps.ToArray() : null; } }
        }
        public sealed partial class FrontierFinalizerBinding
        {
            private bool recoveryReadAttempted;
            private ReadOnlyCollection<FrontierRecoveryRead> recoveryReaders;
            public ReadOnlyCollection<FrontierRecoveryRead> TryOpenRecoveryOutputs()
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return null;
                    if (recoveryReadAttempted) { Dispose(); return null; }
                    recoveryReadAttempted = true;
                    bool success = false;
                    try
                    {
                        var adoption = files.GetOriginalAdoption();
                        var launches = files.GetOriginalRecoveryLaunches();
                        if (adoption == null || launches == null || launches.Length != 2) return null;
                        var readers = new FrontierRecoveryRead[4];
                        for (int i = 0; i < 4; i++)
                        {
                            var launch = launches[i / 2];
                            var output = launch.Output.RecoveryOutputs[i % 2];
                            var reader = new FrontierRecoveryRead(this, adoption, output, launch, i % 2 == 1);
                            if (!output.TryOpen(reader)) return null;
                            readers[i] = reader;
                        }
                        recoveryReaders = Array.AsReadOnly(readers);
                        success = IsCurrent; return success ? recoveryReaders : null;
                    }
                    finally { if (!success) Dispose(); }
                }
            }
        }

        // Read-only original bytes, not parsed markers or recovery approval.
        // No handle, path, replacement stream or caller verdict is accepted.
        public sealed class FrontierRecoveryRead : Stream
        {
            private readonly FrontierRecoveryRead original;
            private readonly FrontierFinalizerBinding binding;
            private readonly LocalFrontierFileAdoption adoption;
            private readonly FrontierRecoveryOutput output;
            public int AppProcessId { get; private set; }
            public long AppProcessCreationFileTime { get; private set; }
            public bool IsStandardError { get; private set; }
            internal FrontierRecoveryRead(FrontierFinalizerBinding binding, LocalFrontierFileAdoption adoption,
                FrontierRecoveryOutput output, FrontierAppLaunch launch, bool error)
            {
                original = this; this.binding = binding; this.adoption = adoption; this.output = output;
                AppProcessId = launch.AppProcessId; AppProcessCreationFileTime = launch.AppProcessCreationFileTime;
                IsStandardError = error;
            }
            public bool IsCurrent
            {
                get
                {
                    if (!Object.ReferenceEquals(original, this) || !binding.IsCurrent) return false;
                    if (output.IsCurrent) return true;
                    binding.Dispose(); return false;
                }
            }
            public bool ReachedEnd { get { return IsCurrent && output.ReachedEnd(this) && IsCurrent; } }
            public bool LiveProofEligible { get { return false; } }
            public bool NativeWriterCustodyEstablished { get { return false; } }
            public bool PublicationCompletionIssued { get { return false; } }
            public override bool CanRead { get { return IsCurrent; } }
            public override bool CanWrite { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override long Length { get { if (!IsCurrent) throw new IOException("frontier_recovery_not_current"); return output.Length; } }
            public override long Position
            {
                get { if (!IsCurrent) throw new IOException("frontier_recovery_not_current"); return output.Position(this); }
                set { throw new NotSupportedException(); }
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException("buffer");
                if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
                if (!IsCurrent) throw new IOException("frontier_recovery_not_current");
                int read = 0;
                // Under the adopter lock this callback only takes the output
                // resource lock. It never enters finalizer/GUI ownership locks.
                bool success = adoption.TryRunRecoveryRead(() => output.TryRead(this, buffer, offset, count, out read));
                if (!success || !IsCurrent) { binding.Dispose(); throw new IOException("frontier_recovery_read_failed"); }
                return read;
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing)
            {
                if (disposing && Object.ReferenceEquals(original, this)) binding.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
