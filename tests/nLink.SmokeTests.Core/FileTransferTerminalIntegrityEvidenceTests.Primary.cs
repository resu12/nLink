using System.Diagnostics;
using System.Text.Json;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Fact]
    public Task TerminalPrimary_WindowsPowerShellRetainsSourceWithoutInferringIntegrity()
        => RunSourceBindingCaseAsync("primary-powershell", nameof(TerminalPrimary_WindowsPowerShellRetainsSourceWithoutInferringIntegrity));

    [Fact]
    public Task TerminalPrimary_PowerShell7RetainsSourceWithoutInferringIntegrity()
        => RunSourceBindingCaseAsync("primary-pwsh", nameof(TerminalPrimary_PowerShell7RetainsSourceWithoutInferringIntegrity));

    private static async Task CheckOriginalPrimaryAsync(BoundOwner owner, BoundCapture capture, string directory, string host,
        bool nativeHandoff = false, bool nativeRequestBinding = false, bool nativePrimaryFiles = false, bool nativeCollector = false,
        bool collectorFinalizer = false, bool collectorOutputs = false, bool collectorPrimary = false,
        bool collectorMembership = false, string membershipMutation = "", bool terminalPayloadJoin = false, string terminalMutation = "",
        string preVerdictCase = "", bool nativeWitnessWriter = false)
    {
        // Keep all genuine producer files fixed throughout collection/analysis.
        // The diagnostic returned below is a test observation, not a capability.
        using var held = AcquireHeldFiles(owner, capture);
        AssertHeldFilesProtected(owner);
        var root = FindRepoRoot();
        var nativeRequest = Guid.NewGuid();
        using var process = new Process { StartInfo = new ProcessStartInfo(host)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = nativeHandoff, CreateNoWindow = true, WorkingDirectory = root,
        } };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support",
                nativeHandoff ? "PublicationTerminalNativeParent.ps1" : "PublicationTerminalSourceGenerated.ps1"), root, directory })
        { process.StartInfo.ArgumentList.Add(argument); }
        if (nativeHandoff) { process.StartInfo.Environment["NLINK_TEST_NATIVE_FILE_REQUEST"] = nativeRequest.ToString("D"); }
        process.StartInfo.Environment["NLINK_TEST_NATIVE_REQUEST_BINDING"] = nativeRequestBinding ? "1" : "0";
        process.StartInfo.Environment["NLINK_TEST_NATIVE_PRIMARY_FILES"] = nativePrimaryFiles ? "1" : "0";
        process.StartInfo.Environment["NLINK_TEST_NATIVE_PRIMARY_MUTATION"] = "";
        process.StartInfo.Environment["NLINK_TEST_NATIVE_COLLECTOR"] = nativeCollector ? "1" : "0";
        process.StartInfo.Environment["NLINK_TEST_NATIVE_COLLECTOR_MUTATION"] = "";
        process.StartInfo.Environment["NLINK_TEST_COLLECTOR_FINALIZER"] = collectorFinalizer ? "1" : "0";
        process.StartInfo.Environment["NLINK_TEST_COLLECTOR_OUTPUTS"] = collectorOutputs ? "1" : "0";
        process.StartInfo.Environment["NLINK_TEST_COLLECTOR_OUTPUT_FAILURE"] = "";
        process.StartInfo.Environment["NLINK_TEST_COLLECTOR_PRIMARY"] = collectorPrimary ? "1" : "0";
        process.StartInfo.Environment["NLINK_TEST_COLLECTOR_MEMBERSHIP"] = collectorMembership ? "1" : "0";
        process.StartInfo.Environment["NLINK_TEST_COLLECTOR_MEMBERSHIP_MUTATION"] = membershipMutation;
        process.StartInfo.Environment["NLINK_TEST_TERMINAL_PAYLOAD_JOIN"] = terminalPayloadJoin ? "1" : "0";
        process.StartInfo.Environment["NLINK_TEST_TERMINAL_PAYLOAD_MUTATION"] = terminalMutation;
        process.StartInfo.Environment["NLINK_TEST_PRE_VERDICT_INTEGRITY"] = preVerdictCase.Length > 0 ? "1" : "0";
        process.StartInfo.Environment["NLINK_TEST_PRE_VERDICT_CASE"] = preVerdictCase;
        process.StartInfo.Environment["NLINK_TEST_NATIVE_WITNESS_ADAPTER"] = nativeWitnessWriter ? "1" : "0";
        process.Start();
        Task<string> stdout;
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        try
        {
            if (nativeHandoff) { await AdoptIntoNativeParentAsync(owner, held, process, nativeRequest, deadline.Token); }
            stdout = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(deadline.Token);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                Assert.True(process.WaitForExit(5000), "Owned primary parent did not exit after cancellation.");
            }
            throw;
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "primary.stdout.txt"), await stdout);
        await File.WriteAllTextAsync(Path.Combine(directory, "primary.stderr.txt"), await stderr);
        Assert.True(process.ExitCode == 0, await stdout + Environment.NewLine + await stderr);

        var output = Path.Combine(directory, "primary");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "terminal-source-primary-observation.json")));
        var observation = document.RootElement;
        Assert.Equal("verified_primary_terminal_source_only", observation.GetProperty("Status").GetString());
        Assert.Equal(terminalPayloadJoin && terminalMutation.Length == 0 ? "primary_integrity_gate_not_integrated" :
            terminalPayloadJoin && terminalMutation != "origin" ? "terminal_payload_binding_unproven" :
            nativePrimaryFiles ? "file_writer_provenance_unproven" : "file_owner_handoff_unavailable",
            observation.GetProperty("FirstMissingEdge").GetString());
        Assert.Equal("unproven", observation.GetProperty("TerminalIntegrityGate").GetString());
        Assert.False(observation.GetProperty("ProbeChangesAcceptance").GetBoolean());
        Assert.False(observation.GetProperty("AlternativeAcceptanceAdopted").GetBoolean());
        Assert.False(observation.GetProperty("CanonicalScenarioProven").GetBoolean());
        Assert.True(observation.GetProperty("InputsUnchanged").GetBoolean());
        Assert.Equal(Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID"), observation.GetProperty("RunId").GetString());
        Assert.Equal(Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SCENARIO_RUN_ID"), observation.GetProperty("ScenarioId").GetString());
        Assert.Equal(terminalMutation == "foreign" ? 4 : 2, observation.GetProperty("TerminalRecordCount").GetInt32());
        Assert.True(observation.GetProperty("GuiCompleted").GetBoolean());
        Assert.Equal(preVerdictCase == "true", observation.GetProperty("GuiIntegrityOk").GetBoolean());
        Assert.Equal("fail", observation.GetProperty("PrimaryGateStatus").GetString());
        var retained = await File.ReadAllLinesAsync(observation.GetProperty("SourcePath").GetString()!);
        var writer = await File.ReadAllLinesAsync(Path.Combine(directory, "gui", "valid", "filetransfer-gui-log-writer-evidence-slice.log"));
        var semantic = await File.ReadAllLinesAsync(Path.Combine(directory, "gui", "valid", "filetransfer-gui-semantic-evidence-slice.log"));
        var observedRows = observation.GetProperty("Terminals").EnumerateArray().ToArray();
        Assert.Equal(terminalMutation == "foreign" ? 4 : 2, observedRows.Length);
        foreach (var terminal in capture.Terminals)
        {
            Assert.Single(writer, raw => raw == terminal.RawLine);
            Assert.Single(semantic, raw => raw == terminal.RawLine);
            Assert.Single(retained, raw => raw == terminal.RawLine);
            var observed = Assert.Single(observedRows, row => row.GetProperty("RawLine").GetString() == terminal.RawLine);
            Assert.Equal(terminal.RawLine, retained[observed.GetProperty("SourceLine").GetInt32() - 1]);
        }
        var retainedTerminals = retained.Where(raw => raw.Contains("event=transfer_terminal;", StringComparison.Ordinal));
        Assert.Equal(capture.Terminals.Select(row => row.RawLine),
            terminalMutation == "foreign" ? retainedTerminals.Take(2) : retainedTerminals);
        Assert.Equal(6, observation.GetProperty("RejectedCustodyControls").GetInt32());
        if (nativeRequestBinding) { CheckNativeRequestBindingResult(directory, nativeRequest, observation, nativePrimaryFiles); }
        else { Assert.False(File.Exists(Path.Combine(directory, "native-request-binding-result.json"))); }
        if (nativePrimaryFiles) { CheckNativePrimaryResult(directory, observation, terminalPayloadJoin && terminalMutation.Length == 0); }
        if (nativeCollector) { CheckNativeCollectorResult(directory, completionRequest: observation.GetProperty("WriterRequestId").GetString()!); }
        if (collectorFinalizer) { CheckCollectorFinalizerResult(directory); }
        if (collectorOutputs && !collectorPrimary) { CheckCollectorOutputResult(directory); }
        if (collectorPrimary) { CheckCollectorPrimaryResult(directory, observation); }
        if (collectorMembership) { CheckCollectorMembershipResult(directory, observation, membershipMutation); }
        if (nativeWitnessWriter) { CheckNativeWitnessWriterResult(directory, observation); }
        if (terminalPayloadJoin) { CheckTerminalPayloadJoinResult(directory, observation, terminalMutation); }
        if (preVerdictCase.Length > 0) { CheckTerminalPreVerdictResult(directory, observation, preVerdictCase); }
        else { Assert.False(observation.TryGetProperty("PreVerdictIntegrity", out _)); }
        if (nativeHandoff) { CheckNativeParentResult(owner, capture, process, nativeRequest, directory, nativePrimaryFiles); }
        else
        {
            Assert.Equal(Held, held.CheckCurrent(owner));
            AssertHeldFilesProtected(owner);
            WriteHeldDiagnostic(directory, "primary-held-files-result.json", owner, held, new(), host);
            held.Dispose();
        }
        Assert.Equal("held_owner_closed", held.CheckCurrent(owner));
        AssertHeldFilesReleased(owner);
    }
}
