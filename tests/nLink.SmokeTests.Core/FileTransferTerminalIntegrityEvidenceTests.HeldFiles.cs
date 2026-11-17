using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    private const string Held = "verified_offline_held_terminal_files";

    [Fact]
    public Task TerminalHeldFiles_ProtectsFilesAndInvalidatesLostOrDisposedHandles()
        => RunSourceBindingCaseAsync("held-lifetime", nameof(TerminalHeldFiles_ProtectsFilesAndInvalidatesLostOrDisposedHandles));

    [Fact]
    public Task TerminalHeldFiles_RejectsAlteredFilesAndReleasesPartialAcquisitions()
        => RunSourceBindingCaseAsync("held-altered", nameof(TerminalHeldFiles_RejectsAlteredFilesAndReleasesPartialAcquisitions));

    [Fact]
    public Task TerminalHeldFiles_RejectsAnotherRealOwnerWithIdenticalBytes()
        => RunSourceBindingCaseAsync("held-replay", nameof(TerminalHeldFiles_RejectsAnotherRealOwnerWithIdenticalBytes));

    // Private test lifetime only. No serializer/constructor can supply this owner
    // to the primary, and its measurements say nothing about the pre-acquisition interval.
    private sealed partial class HeldTerminalEvidence : IDisposable
    {
        private readonly BoundOwner owner;
        private readonly BoundCapture capture;
        private readonly FileStream?[] files = new FileStream?[3];
        private readonly string?[] identities = new string?[3];
        private bool disposed;
        private HeldTerminalEvidence(BoundOwner owner, BoundCapture capture)
        { this.owner = owner; this.capture = capture; }

        internal static HeldTerminalEvidence? TryAcquire(BoundOwner owner, BoundCapture capture, out string reason)
        {
            reason = "source_owner";
            if (!ReferenceEquals(owner, capture.Owner)) { return null; }
            reason = Check(owner.Run, capture.Terminal);
            if (reason != Verified) { return null; }
            var held = new HeldTerminalEvidence(owner, capture);
            var accepted = false;
            var role = "log";
            try
            {
                var paths = HeldPaths(owner);
                for (var i = 0; i < paths.Length; i++)
                {
                    role = HeldRoles[i];
                    var file = new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.Read);
                    held.files[i] = file;
                    if (!OperationalLogNativeSource.TryDescribe(file.SafeFileHandle, out held.identities[i], out _))
                    { reason = role + "_identity"; return null; }
                }
                reason = held.CheckCurrent(owner);
                accepted = reason == Held;
                return accepted ? held : null;
            }
            catch (FileNotFoundException) { reason = role + "_missing"; return null; }
            catch (IOException) { reason = role + "_unavailable"; return null; }
            catch (UnauthorizedAccessException) { reason = role + "_unavailable"; return null; }
            finally { if (!accepted) { held.Dispose(); } }
        }

        internal string CheckCurrent(BoundOwner expected)
        {
            if (!ReferenceEquals(owner, expected)) { return "held_owner"; }
            if (disposed) { return "held_owner_closed"; }
            var result = "held_file_unavailable";
            try { result = Validate(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { if (result != Held) { Dispose(); } }
            return result;
        }

        private string Validate()
        {
            for (var i = 0; i < files.Length; i++)
            {
                var file = files[i];
                if (file is null || !file.CanRead) { return "held_file_unavailable"; }
                if (!OperationalLogNativeSource.TryDescribe(file.SafeFileHandle, out var identity, out var bytes) || identity != identities[i])
                { return HeldRoles[i] + "_identity"; }
                var expectedBytes = i == 0 ? owner.Digest.Bytes : owner.Run.ExpectedSource.Bytes;
                if (bytes != expectedBytes) { return i == 0 ? "log_changed" : i == 1 ? "source_changed" : "destination_size"; }
            }
            var binding = CheckBoundLog(owner, capture, files[0]!);
            if (binding != Bound) { return binding; }
            var measured = capture.Terminal with
            {
                Source = Digest(files[1]!, owner.Run.SourcePath),
                Destination = Digest(files[2]!, owner.Run.DestinationPath),
            };
            var status = Check(owner.Run, measured);
            return status == Verified ? Held : status;
        }

        internal object[] DiagnosticFiles(BoundOwner expected)
        {
            Assert.Equal(Held, CheckCurrent(expected));
            var paths = HeldPaths(owner);
            return files.Select((file, i) => (object)new
            { Role = HeldRoles[i], Identity = identities[i], Measurement = Digest(file!, paths[i]) }).ToArray();
        }

        // Deliberate local fault injection; the helper exposes no transfer/primary authority.
        internal void CloseHandleForTest(int index) => files[index]!.Dispose();

        public void Dispose()
        {
            if (disposed) { return; }
            disposed = true;
            foreach (var file in files) { file?.Dispose(); }
        }
    }

    private static readonly string[] HeldRoles = ["log", "source", "destination"];
    private static string[] HeldPaths(BoundOwner owner) => [owner.Path, owner.Run.SourcePath, owner.Run.DestinationPath];

    private static HeldTerminalEvidence AcquireHeldFiles(BoundOwner owner, BoundCapture capture)
    {
        var held = HeldTerminalEvidence.TryAcquire(owner, capture, out var reason);
        Assert.Equal(Held, reason);
        return Assert.IsType<HeldTerminalEvidence>(held);
    }

    private static void AssertHeldFilesProtected(BoundOwner owner)
    {
        foreach (var path in HeldPaths(owner))
        {
            Assert.Throws<IOException>(() => { using var write = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); });
            Assert.Throws<IOException>(() => File.Move(path, path + ".denied-move"));
            var replacement = path + ".denied-replacement";
            File.Copy(path, replacement, overwrite: true);
            Assert.Throws<IOException>(() => File.Replace(replacement, path, null));
            Assert.True(File.Exists(replacement));
        }
    }

    private static void AssertHeldFilesReleased(BoundOwner owner)
    {
        foreach (var path in HeldPaths(owner).Where(File.Exists))
        {
            using (var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Move(path, path + ".released");
            File.Move(path + ".released", path);
        }
    }

    private static void CheckHeldFileControls(BoundOwner owner, BoundCapture capture, TransferRun? second, string directory, string scenario)
    {
        var controls = new Dictionary<string, string>();
        if (scenario == "held-lifetime")
        {
            using var held = AcquireHeldFiles(owner, capture);
            AssertHeldFilesProtected(owner);
            Reject("copied_owner", held.CheckCurrent(owner with { }), "held_owner");
            Assert.Equal(Held, held.CheckCurrent(owner));
            WriteHeldDiagnostic(directory, "held-before-disposal.json", owner, held, controls);
            held.Dispose();
            Reject("disposed", held.CheckCurrent(owner), "held_owner_closed");
            AssertHeldFilesReleased(owner);
            foreach (var index in Enumerable.Range(0, 3))
            {
                using var lost = AcquireHeldFiles(owner, capture);
                lost.CloseHandleForTest(index);
                Reject("lost_" + HeldRoles[index], lost.CheckCurrent(owner), "held_file_unavailable");
                Reject("retired_" + HeldRoles[index], lost.CheckCurrent(owner), "held_owner_closed");
                AssertHeldFilesReleased(owner);
            }
        }
        else if (scenario == "held-replay")
        {
            Assert.NotNull(second);
            var other = CaptureBoundOwner(second, owner.Path);
            var otherCapture = CaptureBoundEvidence(other);
            using var held = AcquireHeldFiles(owner, capture);
            using var foreign = AcquireHeldFiles(other, otherCapture);
            Assert.Equal(capture.Terminal.Source.Sha256Base64, otherCapture.Terminal.Source.Sha256Base64);
            Reject("foreign_owner", held.CheckCurrent(other), "held_owner");
            RejectAcquire("foreign_capture", other, capture, "source_owner");
            RejectAcquire("relabeled_capture", other, capture with
            { Owner = other, Terminal = capture.Terminal with { Attempt = second.Attempt } }, "capture_owner");
            Assert.Equal(Held, held.CheckCurrent(owner));
            Assert.Equal(Held, foreign.CheckCurrent(other));
        }
        else
        {
            Assert.Equal("held-altered", scenario);
            RejectAcquire("invalid_terminal", owner, capture with
            { Terminal = capture.Terminal with { Inbound = capture.Terminal.Inbound! with { State = FileTransferTransferState.Failed } } }, "terminal_not_completed");
            RejectAcquire("invalid_origin", owner, capture with
            { Terminals = [capture.Terminals[0] with { LineNumber = 1 }, capture.Terminals[1]] }, "source_origin");
            foreach (var change in new[] { "missing", "truncated", "extra", "corrupt", "source_changed", "log_changed", "log_replaced", "log_missing" })
            {
                var path = change.StartsWith("log_", StringComparison.Ordinal) ? owner.Path : change == "source_changed" ? owner.Run.SourcePath : owner.Run.DestinationPath;
                var original = path + ".held-original";
                var bytes = File.ReadAllBytes(path);
                if (change is "missing" or "log_missing" or "log_replaced")
                {
                    File.Move(path, original);
                    if (change == "log_replaced") { File.Copy(original, path); }
                }
                else if (change is "truncated" or "extra")
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                    file.SetLength(bytes.Length + (change == "extra" ? 1 : -1));
                }
                else { CorruptOneByte(path); }
                var reason = change switch
                {
                    "missing" => "destination_missing", "truncated" or "extra" => "destination_size", "corrupt" => "destination_hash",
                    "log_replaced" => "log_identity", _ => change,
                };
                RejectAcquire(change, owner, capture, reason);
                AssertHeldFilesReleased(owner);
                if (File.Exists(original))
                {
                    if (File.Exists(path)) { File.Delete(path); }
                    File.Move(original, path);
                }
                else { File.WriteAllBytes(path, bytes); }
            }
            // Block the second/third acquisition after earlier files were opened.
            foreach (var index in new[] { 1, 2 })
            {
                using (var blocker = new FileStream(HeldPaths(owner)[index], FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    RejectAcquire("partial_" + HeldRoles[index], owner, capture, HeldRoles[index] + "_unavailable");
                    foreach (var path in HeldPaths(owner).Take(index))
                    { using var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                }
                AssertHeldFilesReleased(owner);
            }
        }
        using var final = AcquireHeldFiles(owner, capture);
        WriteHeldDiagnostic(directory, "held-files-result.json", owner, final, controls);

        void Reject(string name, string actual, string expected)
        { Assert.Equal(expected, actual); controls.Add(name, actual); }
        void RejectAcquire(string name, BoundOwner expected, BoundCapture supplied, string reason)
        {
            using var rejected = HeldTerminalEvidence.TryAcquire(expected, supplied, out var actual);
            Assert.Null(rejected);
            Reject(name, actual, reason);
        }
    }

    private static void WriteHeldDiagnostic(string directory, string name, BoundOwner owner, HeldTerminalEvidence held,
        Dictionary<string, string> controls, string? primaryHost = null)
        => File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(new
        {
            Schema = "nlink.test-held-terminal-files/v1", Status = held.CheckCurrent(owner),
            Scope = "offline_post_finalization_held_file_interval", owner.Run.Attempt,
            Files = held.DiagnosticFiles(owner), Controls = controls, PrimaryHost = primaryHost,
            ControlsAreAcceptanceEvidence = false, FileOwnerHandoffProven = false,
            CanonicalScenarioProven = false, AcceptanceChanged = false, Live = false,
        }, JsonOptions));
}
