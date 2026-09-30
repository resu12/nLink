using System;
using System.Diagnostics;
using System.IO;

namespace NLink.Tools
{
    public sealed partial class LocalInputReadLease
    {
        // Compatibility with the original ordinary hash/count, not new native
        // authority. Only the accepted relay custody can supply the native pin.
        internal static LocalInputReadLease HoldMatchingFrontier(Microsoft.Win32.SafeHandles.SafeFileHandle pin,
            LocalGuiWriterRequest.Output expected, LocalInputReadLease ordinary, long lines)
        {
            FileStream stream = null;
            try
            {
                if (ordinary == null || !ordinary.IsVerified || ordinary.Path != expected.Path || ordinary.ExpectedSha256 != expected.Sha256 ||
                    ordinary.ByteCount != expected.ByteCount || lines != expected.LineCount) return null;
                var handle = DuplicateFrontierRead(pin); if (handle == null) return null;
                try { stream = new FileStream(handle, FileAccess.Read); } catch { handle.Dispose(); throw; }
                if (stream.Length != expected.ByteCount) return null;
                var lease = new LocalInputReadLease(stream, expected.Path, expected.Sha256, lines, true);
                lease.frontierNative = true; lease.frontierOriginalLease = lease; lease.frontierHashHandle = stream.SafeFileHandle;
                stream = null; return lease;
            }
            catch { return null; }
            finally { if (stream != null) stream.Dispose(); }
        }
        internal LocalInputReadLease DuplicateVerifiedLease()
        {
            FileStream stream = null;
            try
            {
                if (!IsVerified) return null;
                stream = TryOpenVerifiedRead(); if (stream == null) return null;
                var result = new LocalInputReadLease(stream, Path, ExpectedSha256, expectedLines, true);
                stream = null;
                if (!IsVerified || result.FileIdentity != FileIdentity || result.ByteCount != ByteCount) { result.Dispose(); return null; }
                return result;
            }
            catch { return null; }
            finally { if (stream != null) stream.Dispose(); }
        }
    }
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierRelayCustody
        {
            private bool preparationAttempted;
            public LocalRetainedSourceRequest.FrontierPrimaryPreparation TryPreparePrimarySource(Guid attempt, string run, string scenario,
                string path, Completion completion, LocalInputReadLease ordinary, long lines)
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || preparationAttempted || !detached || !IsCurrent ||
                        attempt == Guid.Empty || !Object.ReferenceEquals(accepted.WriterContext, completion) || ordinary == null || !ordinary.IsVerified ||
                        completion.AcceptanceRunId != run || completion.ScenarioRunId != scenario || path != completion.Outputs[3].Path || ordinary.Path != path) return null;
                    preparationAttempted = true;
                    var work = Stopwatch.StartNew();
                    LocalInputReadLease native = null, compatible = null;
                    LocalRetainedSourceRequest request = null; bool success = false;
                    try
                    {
                        native = LocalInputReadLease.HoldMatchingFrontier(pins.Handles[4], completion.Outputs[3], ordinary, lines);
                        compatible = ordinary.DuplicateVerifiedLease();
                        if (native == null || compatible == null || !facts.MatchesCompletion(completion) || !IsCurrent) return null;
                        request = LocalRetainedSourceRequest.TryCreateWithWriter(attempt, "RouteAcceptance", run, scenario, native, lines, completion);
                        if (request == null || request.AcceptRelayInputs(this) == null || work.ElapsedMilliseconds > 5000) return null;
                        var result = request.PrepareCompatibleRelaySource(native.ExpectedSha256, lines, compatible);
                        if (result == null) return null;
                        compatible = null; success = true; return result;
                    }
                    catch { return null; }
                    finally
                    {
                        Close(); if (native != null) native.Dispose(); if (compatible != null) compatible.Dispose();
                        if (!success && request != null) request.Dispose();
                    }
                }
            }
            internal LocalRetainedSourceRequest.FrontierPrimaryInputs Transfer(LocalRetainedSourceRequest request)
            {
                lock (sync)
                {
                    if (!Object.ReferenceEquals(original, this) || !preparationAttempted || !detached || !IsCurrent ||
                        !request.FrontierScopeCurrent(accepted.WriterContext, true) || !pins.MatchesPrimarySource(request.FrontierSourceHandle)) return null;
                    var duplicates = pins.DuplicateForPrimary(() => IsCurrent && request.FrontierScopeCurrent(accepted.WriterContext, true));
                    if (duplicates == null) return null;
                    var result = new LocalRetainedSourceRequest.FrontierPrimaryInputs(request, facts, duplicates);
                    result.WatchRequest(request.FrontierCancellation); return result;
                }
            }
        }
    }
    public sealed partial class LocalRetainedSourceRequest
    {
        internal FrontierPrimaryInputs AcceptRelayInputs(LocalGuiWriterRequest.FrontierRelayCustody custody)
        {
            if (!Object.ReferenceEquals(frontierOriginal, this)) return null;
            lock (frontierInputSync)
            {
                if (frontierInputAttempted) return null; frontierInputAttempted = true;
                frontierInputs = custody.Transfer(this);
                if (frontierInputs != null && !frontierInputs.IsCurrent) { frontierInputs.Close(); return null; }
                if (frontierInputs != null) custody.CopyTransferPairToPrimary(frontierInputs);
                return frontierInputs;
            }
        }
        internal FrontierPrimaryPreparation PrepareCompatibleRelaySource(string hash, long lines, LocalInputReadLease compatible)
        {
            if (frontierInputs == null || !frontierInputs.IsCurrent || compatible == null || !compatible.IsVerified) return null;
            return new FrontierPrimaryPreparation(this, frontierInputs, hash, lines, "RouteAcceptance", compatible);
        }
        public sealed partial class FrontierPrimaryPreparation
        {
            internal FrontierPrimaryPreparation(LocalRetainedSourceRequest request, FrontierPrimaryInputs inputs, string hash, long lines,
                string owner, LocalInputReadLease compatible) : this(request, inputs, hash, lines, owner)
            { analysisInput = compatible; analysisInputState = 3; }
        }
    }
}
