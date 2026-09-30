using System.Text.Json;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Fact]
    public Task TerminalCollectorPrimary_WindowsPowerShellUsesHeldOutputsInOriginalReader()
        => RunSourceBindingCaseAsync("collector-primary-powershell", nameof(TerminalCollectorPrimary_WindowsPowerShellUsesHeldOutputsInOriginalReader));

    [Fact]
    public Task TerminalCollectorPrimary_PowerShell7UsesHeldOutputsInOriginalReader()
        => RunSourceBindingCaseAsync("collector-primary-pwsh", nameof(TerminalCollectorPrimary_PowerShell7UsesHeldOutputsInOriginalReader));

    [Fact]
    public Task TerminalCollectorPrimary_RejectsWrongOutputIdentityBeforeEvaluation()
        => RunSourceBindingCaseAsync("collector-primary-reject-collector-identity", nameof(TerminalCollectorPrimary_RejectsWrongOutputIdentityBeforeEvaluation));

    [Fact]
    public Task TerminalCollectorPrimary_RejectsTruncatedOutputPacketBeforeEvaluation()
        => RunSourceBindingCaseAsync("collector-primary-reject-collector-truncated", nameof(TerminalCollectorPrimary_RejectsTruncatedOutputPacketBeforeEvaluation));

    [Fact]
    public Task TerminalCollectorPrimary_RejectsForeignOriginProducerBeforeEvaluation()
        => RunSourceBindingCaseAsync("collector-primary-reject-collector-origin-producer", nameof(TerminalCollectorPrimary_RejectsForeignOriginProducerBeforeEvaluation));

    [Fact]
    public Task TerminalCollectorPrimary_RejectsOutOfRangeOriginBeforeEvaluation()
        => RunSourceBindingCaseAsync("collector-primary-reject-collector-origin-range", nameof(TerminalCollectorPrimary_RejectsOutOfRangeOriginBeforeEvaluation));

    [Fact]
    public Task TerminalCollectorPrimary_RejectsOriginCountOverflowBeforeEvaluation()
        => RunSourceBindingCaseAsync("collector-primary-reject-collector-origin-count", nameof(TerminalCollectorPrimary_RejectsOriginCountOverflowBeforeEvaluation));

    private static void CheckCollectorPrimaryResult(string directory, JsonElement primary)
    {
        Assert.True(primary.GetProperty("PrimaryCollectorOutputCustodyProven").GetBoolean());
        Assert.True(primary.GetProperty("OriginalSourceReaderUsed").GetBoolean());
        Assert.True(primary.GetProperty("RawEvidenceUsesOriginalCompleteRead").GetBoolean());
        Assert.True(primary.GetProperty("PrimaryCollectorLineOriginsProven").GetBoolean());
        Assert.Equal(4, primary.GetProperty("CollectorOriginQueryControls").GetInt32());
        Assert.Equal(9, primary.GetProperty("CollectorOutputControls").GetInt32());
        Assert.Equal(8, primary.GetProperty("CollectorOutputProtection").GetInt32());
        Assert.False(primary.GetProperty("PrimaryProvenanceHandoffProven").GetBoolean());
        Assert.False(primary.GetProperty("WriterCopyProvenanceProven").GetBoolean());
        CheckCollectorPrimaryParent(directory, acknowledged: true);
        CheckCollectorOutputChild(directory, "valid");
        using var cleanup = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "primary", "collector-primary-cleanup.json")));
        foreach (var name in new[] { "FourOutputsDisposed", "RepeatedSourceOpenRejected", "CopiedWorkerSourceOpenRejected", "ClosedOriginQueryRejected" })
        { Assert.True(cleanup.RootElement.GetProperty(name).GetBoolean(), name); }
        Assert.False(cleanup.RootElement.GetProperty("AcceptanceChanged").GetBoolean());
        // Independent post-run corroboration only, never primary input or a
        // substitute for the original processor's in-read origin capture.
        var producer = File.ReadAllLines(Path.Combine(directory, "nlink.log"));
        var outputs = new[] { "filetransfer-gui-log-ledger-slice.log", "filetransfer-gui-log-writer-evidence-slice.log",
            "filetransfer-gui-semantic-evidence-slice.log", "filetransfer-retained-log-slice.log" }
            .Select(name => File.ReadAllLines(Path.Combine(directory, "gui", "valid", name))).ToArray();
        var origins = primary.GetProperty("CollectorLineOrigins").EnumerateArray().ToArray();
        Assert.Equal(outputs[3].Length, origins.Length);
        for (var i = 0; i < origins.Length; i++)
        {
            var row = origins[i];
            Assert.Equal(i + 1, row.GetProperty("SemanticLine").GetInt32());
            var line = producer[row.GetProperty("ProducerLine").GetInt32() - 1];
            Assert.Equal(line, outputs[0][row.GetProperty("RetainedLine").GetInt32() - 1]);
            Assert.Equal(line, outputs[1][row.GetProperty("WriterLine").GetInt32() - 1]);
            Assert.Equal(line, outputs[2][i]); Assert.Equal(line, outputs[3][i]);
        }
    }

    private static void CheckCollectorPrimaryParent(string directory, bool acknowledged)
    {
        using var gui = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "gui", "collector-output-parent-result.json")));
        Assert.True(gui.RootElement.GetProperty("OriginalCompletionOwned").GetBoolean());
        Assert.False(gui.RootElement.GetProperty("ReleasedBeforePrimary").GetBoolean());
        Assert.False(gui.RootElement.GetProperty("FourOutputsReleased").GetBoolean());
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "collector-primary-parent-result.json")));
        var result = document.RootElement;
        Assert.Equal(9, result.GetProperty("BindingControls").GetArrayLength());
        Assert.Equal(1, result.GetProperty("HashPasses").GetInt32());
        Assert.True(result.GetProperty("RepeatedHashRejected").GetBoolean());
        Assert.True(result.GetProperty("FourOutputsReleased").GetBoolean());
        Assert.Equal(acknowledged, result.GetProperty("WorkerAcknowledged").GetBoolean());
        Assert.Equal(acknowledged, result.GetProperty("ParentReleasedAfterAcknowledgement").GetBoolean());
        Assert.Equal(!acknowledged, result.GetProperty("RejectedAttemptReleasedInCleanup").GetBoolean());
        Assert.False(result.GetProperty("PathReopenedForPrimary").GetBoolean());
        Assert.False(result.GetProperty("AcceptanceChanged").GetBoolean());
    }
}
