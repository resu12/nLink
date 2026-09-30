using System.Text.Json;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Fact]
    public Task SelectedTerminalJoin_WindowsPowerShellPreservesOriginalFailedVerdict()
        => RunSourceBindingCaseAsync("native-authenticated-terminal-powershell", nameof(SelectedTerminalJoin_WindowsPowerShellPreservesOriginalFailedVerdict));
    [Fact]
    public Task SelectedTerminalJoin_PowerShell7PreservesOriginalFailedVerdict()
        => RunSourceBindingCaseAsync("native-authenticated-terminal-pwsh", nameof(SelectedTerminalJoin_PowerShell7PreservesOriginalFailedVerdict));
    [Fact]
    public Task SelectedTerminalJoin_CopiedTerminalCannotJoinOrRetry()
        => RunSourceBindingCaseAsync("native-authenticated-terminal-copy", nameof(SelectedTerminalJoin_CopiedTerminalCannotJoinOrRetry));
    [Fact]
    public Task SelectedTerminalJoin_ForeignScopeCannotJoinOrRetry()
        => RunSourceBindingCaseAsync("native-authenticated-terminal-scope", nameof(SelectedTerminalJoin_ForeignScopeCannotJoinOrRetry));
    [Fact]
    public Task SelectedTerminalJoin_MissingProducerCoordinateCannotJoinOrRetry()
        => RunSourceBindingCaseAsync("native-authenticated-terminal-coordinate", nameof(SelectedTerminalJoin_MissingProducerCoordinateCannotJoinOrRetry));
    [Fact]
    public Task SelectedTerminalJoin_SwappedDirectionsCannotJoinOrRetry()
        => RunSourceBindingCaseAsync("native-authenticated-terminal-swapped", nameof(SelectedTerminalJoin_SwappedDirectionsCannotJoinOrRetry));
    [Fact]
    public Task SelectedTerminalJoin_ResultDisposalPreservesOriginalOwners()
        => RunSourceBindingCaseAsync("native-authenticated-terminal-dispose", nameof(SelectedTerminalJoin_ResultDisposalPreservesOriginalOwners));

    private static void CheckSelectedTerminalJoin(string analysis, BoundCapture capture, string scenario, string[] selected)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(analysis, "selected-terminal-join.json")));
        var result = document.RootElement;
        var expected = scenario is "pwsh" or "powershell" or "dispose";
        Assert.Equal(expected, result.GetProperty("Joined").GetBoolean());
        Assert.True(result.GetProperty("RepeatedJoinRejected").GetBoolean());
        Assert.Equal(9, result.GetProperty("PreControls").GetInt32());
        Assert.Equal(0, result.GetProperty("AdditionalWriterReads").GetInt32());
        Assert.Equal(0, result.GetProperty("AdditionalPayloadHashes").GetInt32());
        Assert.False(result.GetProperty("AcceptanceChanged").GetBoolean());
        Assert.False(result.GetProperty("Live").GetBoolean());
        Assert.InRange(result.GetProperty("CumulativeRecordVisits").GetInt64(), 1, 4096);
        if (!expected) { return; }
        Assert.Equal(22, result.GetProperty("PostControls").GetInt32());
        Assert.Equal(12, result.GetProperty("ParserControls").GetInt32());
        Assert.Equal(selected.Length + 2, result.GetProperty("RecordVisits").GetInt64());
        long codeUnits = 0;
        foreach (var direction in new[] { "inbound", "outbound" })
        {
            var terminal = Assert.Single(capture.Terminals, row => row.Field("direction") == direction);
            var origin = result.GetProperty(direction == "inbound" ? "InboundOrigin" : "OutboundOrigin");
            Assert.Equal(terminal.LineNumber, origin.GetProperty("ProducerLine").GetInt32());
            Assert.Equal(terminal.LineNumber, origin.GetProperty("RetainedLine").GetInt32());
            Assert.Equal(terminal.RawLine, selected[origin.GetProperty("SemanticLine").GetInt32() - 1]);
            Assert.True(origin.GetProperty("WriterLine").GetInt32() > 0);
            codeUnits += 4L * terminal.RawLine.Length;
        }
        Assert.Equal(codeUnits, result.GetProperty("CodeUnits").GetInt64());
    }
}
