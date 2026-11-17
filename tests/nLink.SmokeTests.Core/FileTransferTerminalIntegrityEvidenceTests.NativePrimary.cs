using System.Diagnostics;
using System.Text.Json;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Fact]
    public Task TerminalNativePrimary_WindowsPowerShellAdoptsIntoOriginalCustody()
        => RunSourceBindingCaseAsync("native-primary-powershell", nameof(TerminalNativePrimary_WindowsPowerShellAdoptsIntoOriginalCustody));

    [Fact]
    public Task TerminalNativePrimary_PowerShell7AdoptsIntoOriginalCustody()
        => RunSourceBindingCaseAsync("native-primary-pwsh", nameof(TerminalNativePrimary_PowerShell7AdoptsIntoOriginalCustody));

    [Fact]
    public Task TerminalNativePrimary_RejectsForeignRequest()
        => RunSourceBindingCaseAsync("native-primary-reject-request", nameof(TerminalNativePrimary_RejectsForeignRequest));

    [Fact]
    public Task TerminalNativePrimary_RejectsMismatchedOriginalDescriptor()
        => RunSourceBindingCaseAsync("native-primary-reject-descriptor", nameof(TerminalNativePrimary_RejectsMismatchedOriginalDescriptor));

    [Fact]
    public Task TerminalNativePrimary_RejectsPartialNativePacket()
        => RunSourceBindingCaseAsync("native-primary-reject-truncated", nameof(TerminalNativePrimary_RejectsPartialNativePacket));

    private static void CheckNativePrimaryResult(string directory, JsonElement primary, bool payloadJoined = false)
    {
        Assert.True(primary.GetProperty("PrimaryNativeFileAdoptionProven").GetBoolean());
        Assert.Equal(payloadJoined, primary.GetProperty("PrimaryFileIntegrityJoinProven").GetBoolean());
        Assert.False(primary.GetProperty("WriterCopyProvenanceProven").GetBoolean());
        Assert.True(primary.GetProperty("HashesAreProducerMeasurements").GetBoolean());
        Assert.Equal("verified_offline_original_primary_native_files_only", primary.GetProperty("NativeFileStatus").GetString());
        Assert.Equal(9, primary.GetProperty("RejectedNativeCustodyControls").GetInt32());
        using var parent = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "native-parent-result.json")));
        var expected = parent.RootElement.GetProperty("Files").EnumerateArray().ToArray();
        var actual = primary.GetProperty("NativeFiles").EnumerateArray().ToArray();
        Assert.Equal(3, actual.Length);
        for (var i = 0; i < actual.Length; i++)
        {
            foreach (var name in new[] { "Role", "Path", "Identity", "Sha256Base64" })
            { Assert.Equal(expected[i].GetProperty(name).GetString(), actual[i].GetProperty(name).GetString()); }
            Assert.Equal(expected[i].GetProperty("Bytes").GetInt64(), actual[i].GetProperty("Bytes").GetInt64());
        }
        using var cleanup = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "primary", "native-primary-cleanup.json")));
        Assert.True(cleanup.RootElement.GetProperty("AdoptedFilesDisposed").GetBoolean());
        Assert.True(cleanup.RootElement.GetProperty("RepeatedAdoptionRejected").GetBoolean());
        Assert.False(cleanup.RootElement.GetProperty("AcceptanceChanged").GetBoolean());
    }

    private static async Task CheckNativePrimaryRejectionAsync(BoundOwner owner, BoundCapture capture, string directory, string mutation,
        bool collectorPrimary = false)
    {
        using var held = AcquireHeldFiles(owner, capture);
        var root = FindRepoRoot();
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
        process.StartInfo.Environment["NLINK_TEST_NATIVE_REQUEST_BINDING"] = "1";
        process.StartInfo.Environment["NLINK_TEST_NATIVE_PRIMARY_FILES"] = "1";
        process.StartInfo.Environment["NLINK_TEST_NATIVE_PRIMARY_MUTATION"] = mutation;
        process.StartInfo.Environment["NLINK_TEST_NATIVE_COLLECTOR"] = collectorPrimary ? "1" : "0";
        process.StartInfo.Environment["NLINK_TEST_NATIVE_COLLECTOR_MUTATION"] = "";
        process.StartInfo.Environment["NLINK_TEST_COLLECTOR_FINALIZER"] = collectorPrimary ? "1" : "0";
        process.StartInfo.Environment["NLINK_TEST_COLLECTOR_OUTPUTS"] = collectorPrimary ? "1" : "0";
        process.StartInfo.Environment["NLINK_TEST_COLLECTOR_OUTPUT_FAILURE"] = "";
        process.StartInfo.Environment["NLINK_TEST_COLLECTOR_PRIMARY"] = collectorPrimary ? "1" : "0";
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
        File.WriteAllText(Path.Combine(directory, "native-primary-parent.stdout.txt"), output);
        File.WriteAllText(Path.Combine(directory, "native-primary-parent.stderr.txt"), await stderr);
        Assert.True(process.ExitCode == 0, output + Environment.NewLine + await stderr);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "native-primary-rejection-result.json")));
        Assert.Equal(mutation, result.RootElement.GetProperty("Mutation").GetString());
        Assert.False(result.RootElement.GetProperty("Acknowledged").GetBoolean());
        Assert.Equal(0, result.RootElement.GetProperty("AuthorityEvaluations").GetInt32());
        Assert.True(result.RootElement.GetProperty("ChildExited").GetBoolean());
        Assert.NotEqual(0, result.RootElement.GetProperty("ChildExitCode").GetInt32());
        Assert.False(result.RootElement.GetProperty("AcceptanceChanged").GetBoolean());
        Assert.False(File.Exists(Path.Combine(directory, "primary", "terminal-source-primary-observation.json")));
        Assert.False(File.Exists(Path.Combine(directory, "native-request-binding-result.json")));
        if (collectorPrimary) { CheckCollectorPrimaryParent(directory, acknowledged: false); }
        CheckNativeParentResult(owner, capture, process, request, directory);
        AssertHeldFilesReleased(owner);
    }
}
