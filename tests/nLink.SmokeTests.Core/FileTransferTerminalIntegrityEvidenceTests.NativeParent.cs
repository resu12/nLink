using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NLink.Tests;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Fact]
    public Task TerminalNativeFiles_WindowsPowerShellParentAdoptsBeforePrimary()
        => RunSourceBindingCaseAsync("native-powershell", nameof(TerminalNativeFiles_WindowsPowerShellParentAdoptsBeforePrimary));

    [Fact]
    public Task TerminalNativeFiles_PowerShell7ParentAdoptsBeforePrimary()
        => RunSourceBindingCaseAsync("native-pwsh", nameof(TerminalNativeFiles_PowerShell7ParentAdoptsBeforePrimary));

    [Fact]
    public Task TerminalNativeFiles_RejectsWrongScopeProcessAndPartialPackets()
        => RunSourceBindingCaseAsync("native-controls", nameof(TerminalNativeFiles_RejectsWrongScopeProcessAndPartialPackets));

    [Fact]
    public Task TerminalNativeRequest_WindowsPowerShellBindsOriginalRequest()
        => RunSourceBindingCaseAsync("native-request-powershell", nameof(TerminalNativeRequest_WindowsPowerShellBindsOriginalRequest));

    [Fact]
    public Task TerminalNativeRequest_PowerShell7BindsOriginalRequest()
        => RunSourceBindingCaseAsync("native-request-pwsh", nameof(TerminalNativeRequest_PowerShell7BindsOriginalRequest));

    private sealed partial class HeldTerminalEvidence
    {
        private bool nativeSendAttempted;

        // The only issuer is the genuine held owner, to the Process just launched
        // by its fixture. Metadata travels over that Process's owned stdin pipe,
        // not a path-nominated/JSON receipt or a production capability endpoint.
        internal void SendToNativeParent(BoundOwner expected, Process process, Guid request, string mutation = "")
        {
            if (nativeSendAttempted) { throw new InvalidOperationException("native_parent_send_repeated"); }
            nativeSendAttempted = true;
            Assert.Equal(Held, CheckCurrent(expected));
            using var packet = new MemoryStream();
            using (var writer = new BinaryWriter(packet, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(HeldFileNativeParent.Magic); writer.Write(mutation == "binding-version" ? 1 : 2);
                writer.Write((mutation == "request" ? Guid.NewGuid() : request).ToByteArray());
                writer.Write(process.Id);
                writer.Write(process.StartTime.ToUniversalTime().ToFileTimeUtc() + (mutation == "process" ? 1 : 0));
                HeldFileNativeParent.WriteText(writer, Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID")!);
                HeldFileNativeParent.WriteText(writer, mutation == "scope" ? "foreign" : Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SCENARIO_RUN_ID")!);
                if (mutation != "binding-missing")
                {
                    HeldFileNativeParent.WriteText(writer, mutation == "binding-token" ? "malformed" : TerminalToken(owner.Run.SessionId));
                    HeldFileNativeParent.WriteText(writer, TerminalToken(owner.Run.TransferId));
                    int inbound = capture.Terminals.Single(row => row.Field("direction") == "inbound").LineNumber;
                    int outbound = capture.Terminals.Single(row => row.Field("direction") == "outbound").LineNumber;
                    writer.Write(mutation == "binding-range" ? 4097 : inbound);
                    if (mutation != "binding-truncated") writer.Write(mutation == "binding-duplicate" ? inbound : outbound);
                }
                var paths = HeldPaths(owner);
                for (var i = 0; i < (mutation is "binding-missing" or "binding-truncated" ? 0 : files.Length); i++)
                {
                    var source = mutation == "file" && i == 2 ? files[1]! : files[i]!;
                    writer.Write(HeldFileNativeParent.DuplicateForChild(process, source.SafeFileHandle));
                    HeldFileNativeParent.WriteText(writer, paths[i]);
                    HeldFileNativeParent.WriteText(writer, identities[i]!);
                    var measured = Digest(files[i]!, paths[i]);
                    writer.Write(measured.Bytes);
                    writer.Write(Convert.FromBase64String(measured.Sha256Base64));
                }
            }
            var bytes = packet.ToArray();
            Assert.InRange(bytes.Length, 1, HeldFileNativeParent.MaximumPacketBytes);
            using (var output = new BinaryWriter(process.StandardInput.BaseStream, Encoding.UTF8, leaveOpen: true))
            {
                output.Write(bytes.Length);
                output.Write(bytes, 0, mutation == "truncated" ? bytes.Length / 2 : bytes.Length);
                output.Flush();
            }
            if (mutation == "truncated") { process.StandardInput.Close(); }
        }
    }

    private static async Task AdoptIntoNativeParentAsync(BoundOwner owner, HeldTerminalEvidence held, Process process, Guid request,
        CancellationToken cancellationToken)
    {
        held.SendToNativeParent(owner, process, request);
        using var acknowledgement = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        acknowledgement.CancelAfter(TimeSpan.FromSeconds(10));
        var line = await process.StandardOutput.ReadLineAsync(acknowledgement.Token);
        Assert.Equal("nlink-native-parent-adopted:" + request.ToString("D"), line);
        Assert.Equal("native_parent_send_repeated", Assert.Throws<InvalidOperationException>(
            () => held.SendToNativeParent(owner, process, request)).Message);
        held.Dispose();
        Assert.Equal("held_owner_closed", held.CheckCurrent(owner));
        // Only the actual parent's duplicate handles now exclude mutation.
        AssertHeldFilesProtected(owner);
        await process.StandardInput.BaseStream.WriteAsync(request.ToByteArray(), cancellationToken);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        process.StandardInput.Close();
    }

    private static void CheckNativeParentResult(BoundOwner owner, BoundCapture capture, Process process, Guid request, string directory,
        bool forwardedToPrimary = false)
    {
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "native-parent-result.json")));
        var root = result.RootElement;
        Assert.Equal("verified_offline_native_parent_adoption_only", root.GetProperty("Status").GetString());
        Assert.Equal(request.ToString("D"), root.GetProperty("RequestId").GetString());
        Assert.Equal(process.Id, root.GetProperty("ProcessId").GetInt32());
        Assert.True(root.GetProperty("RepeatedAdoptionRejected").GetBoolean());
        Assert.True(root.GetProperty("Disposed").GetBoolean());
        Assert.Equal(forwardedToPrimary, root.GetProperty("ForwardedToPrimary").GetBoolean());
        Assert.True(root.GetProperty("HashesAreProducerMeasurements").GetBoolean());
        Assert.False(root.GetProperty("PrimaryFileOwnerHandoffProven").GetBoolean());
        Assert.False(root.GetProperty("AcceptanceChanged").GetBoolean());
        var files = root.GetProperty("Files").EnumerateArray().ToArray();
        Assert.Equal(3, files.Length);
        var paths = HeldPaths(owner);
        for (var i = 0; i < paths.Length; i++)
        {
            Assert.Equal(HeldRoles[i], files[i].GetProperty("Role").GetString());
            Assert.Equal(paths[i], files[i].GetProperty("Path").GetString());
            using var file = new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.True(NLink.Core.Logging.OperationalLogNativeSource.TryDescribe(file.SafeFileHandle, out var identity, out var length));
            Assert.Equal(identity, files[i].GetProperty("Identity").GetString());
            Assert.Equal(length, files[i].GetProperty("Bytes").GetInt64());
            Assert.Equal(Digest(file, paths[i]).Sha256Base64, files[i].GetProperty("Sha256Base64").GetString());
        }
        // Post-run corroboration, not a new primary input or authority evaluation.
        Assert.Equal(Bound, CheckBinding(owner, capture));
    }

    private static void CheckNativeRequestBindingResult(string directory, Guid nativeRequest, JsonElement primary, bool primaryFiles = false)
    {
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "native-request-binding-result.json")));
        var root = result.RootElement;
        Assert.Equal("verified_offline_parent_request_association_only", root.GetProperty("Status").GetString());
        Assert.Equal(nativeRequest.ToString("D"), root.GetProperty("NativeRequestId").GetString());
        Assert.Equal(primary.GetProperty("RequestId").GetString(), root.GetProperty("SourceRequestId").GetString());
        Assert.Equal(primary.GetProperty("WriterRequestId").GetString(), root.GetProperty("WriterRequestId").GetString());
        foreach (var property in new[] { "CurrentAfterOriginalWorkerBinding", "InvalidAfterOriginalWorkerExit",
            "InvalidAfterDisposal" })
        { Assert.True(root.GetProperty(property).GetBoolean(), property); }
        Assert.Equal(!primaryFiles, root.GetProperty("FileOwnerStillHeldAfterRequestDisposal").GetBoolean());
        Assert.Equal(primaryFiles, root.GetProperty("WorkerAdoptionAcknowledged").GetBoolean());
        foreach (var property in new[] { "PrimaryFileOwnerHandoffProven", "WriterCopyProvenanceProven",
            "WriterMembershipVerified", "AcceptanceChanged", "Live" })
        { Assert.False(root.GetProperty(property).GetBoolean(), property); }
        var expected = new[] { "copied_request", "copied_file_owner", "null_files", "copied_files", "foreign_writer",
            "missing_writer", "unverified_lease", "closed_lease", "closed_request", "request_copy", "other_genuine_request",
            "files_copy", "writer_copy", "same_file_other_lease", "binding_copy", "repeated_binding", "native_owner_reuse" };
        Assert.Equal(expected, root.GetProperty("Controls").EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    private static async Task CheckNativeParentControlsAsync(BoundOwner owner, BoundCapture capture, string directory)
    {
        var root = FindRepoRoot();
        var controls = new Dictionary<string, string>();
        foreach (var mutation in new[] { "request", "scope", "process", "file", "truncated",
            "binding-version", "binding-missing", "binding-token", "binding-range", "binding-duplicate", "binding-truncated" })
        {
            using var held = AcquireHeldFiles(owner, capture);
            var request = Guid.NewGuid();
            using var process = new Process { StartInfo = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root,
            } };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "PublicationTerminalNativeParent.ps1"), root, directory })
            { process.StartInfo.ArgumentList.Add(argument); }
            process.StartInfo.Environment["NLINK_TEST_NATIVE_FILE_REQUEST"] = request.ToString("D");
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                held.SendToNativeParent(owner, process, request, mutation);
                await process.WaitForExitAsync(deadline.Token);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); Assert.True(process.WaitForExit(5000)); }
            }
            var output = await stdout;
            var error = await stderr;
            File.WriteAllText(Path.Combine(directory, "native-" + mutation + ".stdout.txt"), output);
            File.WriteAllText(Path.Combine(directory, "native-" + mutation + ".stderr.txt"), error);
            var reason = mutation == "file" ? "native_parent_file_identity" :
                mutation == "truncated" ? "native_parent_packet_truncated" :
                mutation == "binding-version" ? "native_parent_packet_version" :
                mutation is "binding-missing" or "binding-truncated" ? "native_parent_transfer_binding_truncated" :
                mutation.StartsWith("binding-", StringComparison.Ordinal) ? "native_parent_transfer_binding" : "native_parent_" + mutation;
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(reason, error);
            Assert.DoesNotContain("nlink-native-parent-adopted:", output);
            Assert.False(File.Exists(Path.Combine(directory, "native-parent-result.json")));
            Assert.Equal(Held, held.CheckCurrent(owner));
            held.Dispose();
            AssertHeldFilesReleased(owner);
            controls.Add(mutation, reason);
        }
        File.WriteAllText(Path.Combine(directory, "native-parent-controls.json"), JsonSerializer.Serialize(new
        {
            Schema = "nlink.test-native-parent-controls/v1", Controls = controls,
            AuthorityEvaluations = 0, AcceptanceChanged = false, Live = false,
        }, JsonOptions));
    }
}
