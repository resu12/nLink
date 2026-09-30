using System.Text.Json;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Fact]
    public Task TerminalCollectorOutputs_WindowsPowerShellRetainsAcrossSuccessfulExit()
        => RunSourceBindingCaseAsync("native-output-powershell", nameof(TerminalCollectorOutputs_WindowsPowerShellRetainsAcrossSuccessfulExit));

    [Fact]
    public Task TerminalCollectorOutputs_PowerShell7RetainsAcrossSuccessfulExit()
        => RunSourceBindingCaseAsync("native-output-pwsh", nameof(TerminalCollectorOutputs_PowerShell7RetainsAcrossSuccessfulExit));

    [Fact]
    public Task TerminalCollectorOutputs_RejectsNonzeroExitAndReleasesPendingFiles()
        => RunSourceBindingCaseAsync("native-output-reject-nonzero", nameof(TerminalCollectorOutputs_RejectsNonzeroExitAndReleasesPendingFiles));

    [Fact]
    public Task TerminalCollectorOutputs_RejectsFailedOutputCaptureAndReleasesPendingFiles()
        => RunSourceBindingCaseAsync("native-output-reject-output-fails", nameof(TerminalCollectorOutputs_RejectsFailedOutputCaptureAndReleasesPendingFiles));

    [Fact]
    public Task TerminalCollectorExpiry_PowerShell7RejectsBeforePrimaryAndReleasesFiles()
        => RunSourceBindingCaseAsync("native-output-expiry-pwsh", nameof(TerminalCollectorExpiry_PowerShell7RejectsBeforePrimaryAndReleasesFiles));

    [Fact]
    public Task TerminalCollectorExpiry_WindowsPowerShellRejectsBeforePrimaryAndReleasesFiles()
        => RunSourceBindingCaseAsync("native-output-expiry-powershell", nameof(TerminalCollectorExpiry_WindowsPowerShellRejectsBeforePrimaryAndReleasesFiles));

    private static void CheckCollectorOutputResult(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "gui", "collector-output-parent-result.json")));
        var result = document.RootElement;
        foreach (var name in new[] { "OriginalCompletionOwned", "FourOutputsHeldAcrossSuccessfulExit",
            "CopiedRequestCompletionAndOwnerRejected", "RepeatedAdoptionRejected", "FourOutputsReleased", "ChannelsClosed", "ReleasedBeforePrimary" })
        { Assert.True(result.GetProperty(name).GetBoolean(), name); }
        Assert.Equal(8, result.GetProperty("OutputProtectionAfterSuccessfulExit").GetInt32());
        foreach (var name in new[] { "WriterCopyProvenanceProven", "PrimaryProvenanceHandoffProven", "AcceptanceChanged" })
        { Assert.False(result.GetProperty(name).GetBoolean(), name); }
        CheckCollectorOutputChild(directory, "valid");
    }

    private static void CheckCollectorOutputChild(string directory, string mode)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "gui", mode, "worker-result.json")));
        var result = document.RootElement.GetProperty("NativeCollectorFinalizer");
        Assert.True(result.GetProperty("ParentRetainedOutputsBeforeCompletion").GetBoolean());
        Assert.True(result.GetProperty("RepeatedOutputOfferRejected").GetBoolean());
        Assert.Equal(4, result.GetProperty("RejectedOutputOffers").GetInt32());
        Assert.Equal(8, result.GetProperty("OutputProtectionAfterChildCleanup").GetInt32());
    }

    private static void CheckCollectorOutputRejection(string directory, string mode)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "gui", "collector-output-rejection-result.json")));
        var result = document.RootElement;
        var expired = mode == "capture-expired";
        Assert.Equal(mode, result.GetProperty("Mode").GetString());
        Assert.Equal(expired ? 1 : mode == "nonzero" ? 7 : 0, result.GetProperty("ChildExitCode").GetInt32());
        Assert.Equal(0, result.GetProperty("AuthorityEvaluations").GetInt32());
        foreach (var name in new[] { "CompletionAccepted", "OutputOwnerAccepted", "AcceptanceChanged" })
        { Assert.False(result.GetProperty(name).GetBoolean(), name); }
        Assert.Equal(!expired, result.GetProperty("FourOutputsReleased").GetBoolean());
        Assert.Equal(expired ? 3 : 4, result.GetProperty("RetainedOutputCount").GetInt32());
        Assert.True(result.GetProperty("RetainedOutputsReleased").GetBoolean());
        Assert.True(result.GetProperty("ChannelsClosed").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(directory, "primary")));
        if (expired)
        {
            Assert.True(result.GetProperty("ExpiryChannelMapsAndGateClosed").GetBoolean());
            var child = Path.Combine(directory, "gui", mode);
            using var cleanup = JsonDocument.Parse(File.ReadAllText(Path.Combine(child, "collector-expiry-cleanup.json")));
            foreach (var name in new[] { "OriginalFinalizerCleanupObserved", "ExpiredReadRetryRejected", "CollectorReleasedInChild", "ThreeOutputsReleasedInChild" })
            { Assert.True(cleanup.RootElement.GetProperty(name).GetBoolean(), name); }
            foreach (var name in new[] { "NativeCapturePublished", "SemanticCopyCreated", "AcceptanceChanged" })
            { Assert.False(cleanup.RootElement.GetProperty(name).GetBoolean(), name); }
            var error = File.ReadAllText(Path.Combine(child, "collector-capture-failure.txt"));
            Assert.InRange(error.Length, 1, 8192);
            var timing = System.Text.RegularExpressions.Regex.Match(error,
                @"collector_finalizer_capture_missing; before_capture_ms=(\d+); after_capture_ms=(\d+); budget_ms=250; native_capture_present=False; expiry_injected=True");
            Assert.True(timing.Success, error);
            var before = long.Parse(timing.Groups[1].Value);
            Assert.True(before > 250);
            Assert.True(long.Parse(timing.Groups[2].Value) >= before);
            Assert.Contains("filetransfer_gui_evidence_capture_not_persisted", File.ReadAllText(Path.Combine(child, "gui-smoke-stderr.log")));
            foreach (var name in new[] { "worker-result.json", "filetransfer-gui-log-ledger.json", "filetransfer-retained-log-slice.log" })
            { Assert.False(File.Exists(Path.Combine(child, name)), name); }
            return;
        }
        CheckCollectorOutputChild(directory, mode);
    }
}
