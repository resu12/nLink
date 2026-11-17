using System.Diagnostics;
using System.Text.Json;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Fact]
    public Task TerminalNativeCollector_WindowsPowerShellReadsProtectedLog()
        => RunSourceBindingCaseAsync("native-collector-powershell", nameof(TerminalNativeCollector_WindowsPowerShellReadsProtectedLog));

    [Fact]
    public Task TerminalNativeCollector_PowerShell7ReadsProtectedLog()
        => RunSourceBindingCaseAsync("native-collector-pwsh", nameof(TerminalNativeCollector_PowerShell7ReadsProtectedLog));

    [Fact]
    public Task TerminalNativeCollector_RejectsForeignRequest()
        => RunSourceBindingCaseAsync("native-collector-reject-request", nameof(TerminalNativeCollector_RejectsForeignRequest));

    [Fact]
    public Task TerminalNativeCollector_RejectsWrongFileIdentity()
        => RunSourceBindingCaseAsync("native-collector-reject-identity", nameof(TerminalNativeCollector_RejectsWrongFileIdentity));

    [Fact]
    public Task TerminalNativeCollector_RejectsPartialPacket()
        => RunSourceBindingCaseAsync("native-collector-reject-truncated", nameof(TerminalNativeCollector_RejectsPartialPacket));

    private static void CheckNativeCollectorResult(string directory, string completionRequest)
    {
        using var worker = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "gui", "valid", "worker-result.json")));
        var result = worker.RootElement.GetProperty("NativeCollectorRead");
        Assert.True(worker.RootElement.GetProperty("NativeCapture").GetBoolean());
        Assert.True(worker.RootElement.GetProperty("PrivateIssuerNotExposed").GetBoolean());
        Assert.Equal("verified_offline_native_collector_read_only", result.GetProperty("Status").GetString());
        Assert.Equal(completionRequest, result.GetProperty("RequestId").GetString());
        Assert.True(result.GetProperty("RepeatedReadRejected").GetBoolean());
        Assert.False(result.GetProperty("WriterCopyProvenanceProven").GetBoolean());
        Assert.False(result.GetProperty("WriterMembershipVerified").GetBoolean());
        Assert.False(result.GetProperty("AcceptanceChanged").GetBoolean());
        using var parent = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "native-parent-result.json")));
        var original = parent.RootElement.GetProperty("Files")[0];
        Assert.Equal(original.GetProperty("Identity").GetString(), result.GetProperty("SourceIdentity").GetString());
        Assert.Equal(original.GetProperty("Bytes").GetInt64(), result.GetProperty("CopiedBytes").GetInt64());
        // Independent post-run corroboration, never imported as a capability.
        Assert.Equal(File.ReadAllBytes(Path.Combine(directory, "nlink.log")),
            File.ReadAllBytes(Path.Combine(directory, "gui", "valid", "append.log")));
    }

    private static async Task CheckNativeCollectorRejectionAsync(BoundOwner owner, BoundCapture capture, string directory, string mutation,
        bool collectorOutputs = false, string host = "pwsh")
    {
        using var held = AcquireHeldFiles(owner, capture);
        var root = FindRepoRoot();
        var request = Guid.NewGuid();
        using var process = new Process { StartInfo = new ProcessStartInfo(host)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root,
        } };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "PublicationTerminalNativeParent.ps1"), root, directory })
        { process.StartInfo.ArgumentList.Add(argument); }
        process.StartInfo.Environment["NLINK_TEST_NATIVE_FILE_REQUEST"] = request.ToString("D");
        process.StartInfo.Environment["NLINK_TEST_NATIVE_REQUEST_BINDING"] = "1";
        process.StartInfo.Environment["NLINK_TEST_NATIVE_PRIMARY_FILES"] = "0";
        process.StartInfo.Environment["NLINK_TEST_NATIVE_PRIMARY_MUTATION"] = "";
        process.StartInfo.Environment["NLINK_TEST_NATIVE_COLLECTOR"] = "1";
        process.StartInfo.Environment["NLINK_TEST_NATIVE_COLLECTOR_MUTATION"] = collectorOutputs ? "" : mutation;
        process.StartInfo.Environment["NLINK_TEST_COLLECTOR_FINALIZER"] = collectorOutputs ? "1" : "0";
        process.StartInfo.Environment["NLINK_TEST_COLLECTOR_OUTPUTS"] = collectorOutputs ? "1" : "0";
        process.StartInfo.Environment["NLINK_TEST_COLLECTOR_OUTPUT_FAILURE"] = collectorOutputs ? mutation : "";
        process.StartInfo.Environment["NLINK_TEST_COLLECTOR_PRIMARY"] = "0";
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string output;
        try
        {
            await AdoptIntoNativeParentAsync(owner, held, process, request, deadline.Token);
            var stdout = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(deadline.Token);
            output = await stdout;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); Assert.True(process.WaitForExit(5000)); }
        }
        File.WriteAllText(Path.Combine(directory, "native-collector-parent.stdout.txt"), output);
        File.WriteAllText(Path.Combine(directory, "native-collector-parent.stderr.txt"), await stderr);
        Assert.True(process.ExitCode == 0, output + Environment.NewLine + await stderr);
        if (collectorOutputs)
        {
            CheckCollectorOutputRejection(directory, mutation);
            CheckNativeParentResult(owner, capture, process, request, directory);
            AssertHeldFilesReleased(owner);
            return;
        }
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "gui", "native-collector-rejection-result.json")));
        Assert.Equal(mutation, result.RootElement.GetProperty("Mutation").GetString());
        Assert.True(result.RootElement.GetProperty("ChildExited").GetBoolean());
        Assert.Equal(1, result.RootElement.GetProperty("ChildExitCode").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("AuthorityEvaluations").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("TargetBytes").GetInt32());
        Assert.False(result.RootElement.GetProperty("WriterCompletionAccepted").GetBoolean());
        Assert.False(result.RootElement.GetProperty("CollectorReadCompleted").GetBoolean());
        Assert.False(result.RootElement.GetProperty("AcceptanceChanged").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(directory, "primary")));
        CheckNativeParentResult(owner, capture, process, request, directory);
        AssertHeldFilesReleased(owner);
    }
}
