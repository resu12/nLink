using System.Diagnostics;
using System.Text.Json;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Fact]
    public Task AuthenticatedHandoff_WindowsPowerShellKeepsOriginalWitnessAndPayloadOwner()
        => RunSourceBindingCaseAsync("native-authenticated-powershell", nameof(AuthenticatedHandoff_WindowsPowerShellKeepsOriginalWitnessAndPayloadOwner));

    [Fact]
    public Task AuthenticatedHandoff_PowerShell7KeepsOriginalWitnessAndPayloadOwner()
        => RunSourceBindingCaseAsync("native-authenticated-pwsh", nameof(AuthenticatedHandoff_PowerShell7KeepsOriginalWitnessAndPayloadOwner));

    [Fact]
    public Task AuthenticatedHandoff_EqualByteWrongNativeFileStopsBeforeAnalysis()
        => RunSourceBindingCaseAsync("native-authenticated-file", nameof(AuthenticatedHandoff_EqualByteWrongNativeFileStopsBeforeAnalysis));

    [Fact]
    public Task AuthenticatedHandoff_ForeignTransferScopeCannotBindOrRetry()
        => RunSourceBindingCaseAsync("native-authenticated-scope", nameof(AuthenticatedHandoff_ForeignTransferScopeCannotBindOrRetry));

    [Fact]
    public Task AuthenticatedHandoff_ResultDisposalPreservesOriginalOwners()
        => RunSourceBindingCaseAsync("native-authenticated-dispose", nameof(AuthenticatedHandoff_ResultDisposalPreservesOriginalOwners));

    private static async Task CheckAuthenticatedPayloadHandoffAsync(string scenario)
    {
        var directory = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR")!;
        var path = Path.Combine(directory, "nlink.log");
        var producer = await AuthenticatedTransferOwner.CreateAsync(Path.Combine(directory, "transfer"));
        await WaitUntilAsync(() => ReadTerminalRows(path).Length == 2, 5000);
        Assert.True(LocalOperationalLog.CompleteAndFlush(TimeSpan.FromSeconds(5)));
        var capture = producer.Capture(path);
        Assert.Equal(Bound, producer.CheckCapture(capture));
        var owner = capture.Files.Owner;
        using var held = AcquireHeldFiles(owner, capture.Files);
        AssertHeldFilesProtected(owner);
        var root = FindRepoRoot();
        var request = Guid.NewGuid();
        var rejectFile = scenario == "native-authenticated-file";
        var terminalCase = scenario.StartsWith("native-authenticated-terminal-", StringComparison.Ordinal)
            ? scenario["native-authenticated-terminal-".Length..] : "";
        var host = scenario.EndsWith("-powershell", StringComparison.Ordinal) ? "powershell" : "pwsh";
        using var process = new Process { StartInfo = new ProcessStartInfo(host)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root,
        } };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/AuthenticatedPayloadParent.ps1"), root, directory })
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["NLINK_TEST_NATIVE_FILE_REQUEST"] = request.ToString("D");
        process.StartInfo.Environment["NLINK_TEST_PAYLOAD_OWNER_CASE"] = scenario == "native-authenticated-scope" ? "scope" :
            scenario == "native-authenticated-dispose" ? "dispose" : "active";
        process.StartInfo.Environment["NLINK_TEST_SELECTED_TERMINAL_CASE"] = terminalCase is "pwsh" or "powershell" ? "active" : terminalCase;
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        Task<string> stdout;
        try
        {
            if (rejectFile) { held.SendToNativeParent(owner, process, request, "file"); }
            else { await AdoptIntoNativeParentAsync(owner, held, process, request, deadline.Token); }
            stdout = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); Assert.True(process.WaitForExit(5000)); }
        }
        var output = await stdout; var error = await stderr;
        File.WriteAllText(Path.Combine(directory, "parent.stdout.txt"), output);
        File.WriteAllText(Path.Combine(directory, "parent.stderr.txt"), error);
        if (rejectFile)
        {
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("native_parent_file_identity", error, StringComparison.Ordinal);
            Assert.DoesNotContain("nlink-native-parent-adopted:", output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(directory, "selected-native-7")));
            Assert.Equal(Held, held.CheckCurrent(owner));
            held.Dispose();
        }
        else
        {
            Assert.True(process.ExitCode == 0, output + Environment.NewLine + error);
            var analysis = Path.Combine(directory, host == "powershell" ? "selected-native-5" : "selected-native-7");
            using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(analysis, "selected-payload-owner.json")));
            Assert.Equal(scenario == "native-authenticated-scope" ? "foreign_scope_rejected" : "verified_offline_selected_payload_owner_only",
                result.RootElement.GetProperty("Status").GetString());
            var selected = File.ReadAllLines(Path.Combine(analysis, "gui/valid/filetransfer-retained-log-slice.log"));
            Assert.Equal(capture.Seeded.RawLine, selected[result.RootElement.GetProperty("SourceLine").GetInt32() - 1]);
            Assert.Equal(capture.Seeded.LineNumber, result.RootElement.GetProperty("ProducerLine").GetInt32());
            Assert.False(result.RootElement.GetProperty("TerminalIntegrityEvaluated").GetBoolean());
            if (terminalCase.Length != 0) { CheckSelectedTerminalJoin(analysis, capture.Files, terminalCase, selected); }
        }
        AssertHeldFilesReleased(owner);
        Assert.Equal(Bound, producer.CheckCapture(capture));
        WriteBoundDiagnostic(directory, "source-terminal-result.json", owner, capture.Files, Bound,
            new() { ["NativeHandoff"] = rejectFile ? "equal_byte_wrong_file_rejected" : "selected_payload_owner_verified" });
    }
}
