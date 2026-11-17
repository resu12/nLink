#if NLINK_TEST_TERMINAL_REQUEST
using System;
using System.Threading;
using NLink.Tests;

namespace NLink.Tools
{
    // Compiled only by the opt-in test loader, alongside the unchanged native
    // request implementation. No production loader or receipt includes this.
    public sealed partial class LocalRetainedSourceRequest
    {
        private TerminalFilesForTest terminalFilesForTest;
        private int terminalFileBindingAttempted;

        public TerminalFilesForTest TryBindTerminalFilesForTest(HeldFileNativeParent files,
            LocalGuiWriterRequest.Completion writer, LocalInputReadLease lease)
        {
            if (!Object.ReferenceEquals(frontierOriginal, this) ||
                Interlocked.Exchange(ref terminalFileBindingAttempted, 1) != 0) return null;
            try
            {
                if (files == null || !files.IsCurrent || writer == null || !writer.WasAcceptedHere() ||
                    !FrontierScopeCurrent(writer, true) || lease == null || !lease.IsVerified ||
                    !writer.MatchesSource(files.Run, files.Scenario, lease.Path, lease.ExpectedSha256,
                        lease.ByteCount, writer.Outputs[3].LineCount) ||
                    FileIdentity(frozenSource.SafeFileHandle) != lease.FileIdentity ||
                    frozenSource.Length != lease.ByteCount || !files.TryClaimRequest(this)) return null;
                terminalFilesForTest = new TerminalFilesForTest(this, files, writer, lease);
                return terminalFilesForTest.IsCurrent ? terminalFilesForTest : null;
            }
            catch { return null; }
        }

        // This association records the exact parent objects and their current
        // lifetimes. It does not assert provenance of the collector's log copy,
        // transfer integrity, writer membership, or primary worker adoption.
        public sealed partial class TerminalFilesForTest : IDisposable
        {
            private readonly LocalRetainedSourceRequest request;
            private readonly HeldFileNativeParent files;
            private readonly LocalGuiWriterRequest.Completion writer;
            private readonly LocalInputReadLease lease;
            private int disposed;
            internal TerminalFilesForTest(LocalRetainedSourceRequest request, HeldFileNativeParent files,
                LocalGuiWriterRequest.Completion writer, LocalInputReadLease lease)
            { this.request = request; this.files = files; this.writer = writer; this.lease = lease; }

            public bool IsCurrent
            {
                get
                {
                    try
                    {
                        return Volatile.Read(ref disposed) == 0 &&
                            Object.ReferenceEquals(request.terminalFilesForTest, this) &&
                            request.FrontierScopeCurrent(writer, false) && files.IsClaimedBy(request) &&
                            lease.IsVerified && FileIdentity(request.frozenSource.SafeFileHandle) == lease.FileIdentity &&
                            request.frozenSource.Length == lease.ByteCount;
                    }
                    catch { return false; }
                }
            }

            public bool Owns(LocalRetainedSourceRequest candidateRequest, HeldFileNativeParent candidateFiles,
                LocalGuiWriterRequest.Completion candidateWriter, LocalInputReadLease candidateLease)
            {
                return Object.ReferenceEquals(request, candidateRequest) && Object.ReferenceEquals(files, candidateFiles) &&
                    Object.ReferenceEquals(writer, candidateWriter) && Object.ReferenceEquals(lease, candidateLease) && IsCurrent;
            }

            public void Dispose()
            { if (Object.ReferenceEquals(request.terminalFilesForTest, this)) Interlocked.Exchange(ref disposed, 1); }
        }
    }
}
#endif
