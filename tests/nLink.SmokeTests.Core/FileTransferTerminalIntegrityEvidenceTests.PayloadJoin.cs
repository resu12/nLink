using System.Text.Json;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Fact]
    public Task TerminalPayloadJoin_PowerShell7ConsumesOwnedBinding()
        => RunSourceBindingCaseAsync("payload-pwsh", nameof(TerminalPayloadJoin_PowerShell7ConsumesOwnedBinding));
    [Fact]
    public Task TerminalPayloadJoin_WindowsPowerShellConsumesOwnedBinding()
        => RunSourceBindingCaseAsync("payload-powershell", nameof(TerminalPayloadJoin_WindowsPowerShellConsumesOwnedBinding));
    [Fact]
    public Task TerminalPayloadJoin_RejectsWrongTransferToken()
        => RunSourceBindingCaseAsync("payload-token", nameof(TerminalPayloadJoin_RejectsWrongTransferToken));
    [Fact]
    public Task TerminalPayloadJoin_RejectsSwappedDirections()
        => RunSourceBindingCaseAsync("payload-swapped", nameof(TerminalPayloadJoin_RejectsSwappedDirections));
    [Fact]
    public Task TerminalPayloadJoin_RejectsAbsentProducerCoordinate()
        => RunSourceBindingCaseAsync("payload-coordinate", nameof(TerminalPayloadJoin_RejectsAbsentProducerCoordinate));
    [Fact]
    public Task TerminalPayloadJoin_RejectsDuplicateTerminal()
        => RunSourceBindingCaseAsync("payload-duplicate", nameof(TerminalPayloadJoin_RejectsDuplicateTerminal));
    [Fact]
    public Task TerminalPayloadJoin_RejectsDetachedChangedDirection()
        => RunSourceBindingCaseAsync("payload-direction", nameof(TerminalPayloadJoin_RejectsDetachedChangedDirection));
    [Fact]
    public Task TerminalPayloadJoin_RejectsAddedVisitBudgetOverflow()
        => RunSourceBindingCaseAsync("payload-budget", nameof(TerminalPayloadJoin_RejectsAddedVisitBudgetOverflow));
    [Fact]
    public Task TerminalPayloadJoin_RejectsExpiredOriginalClock()
        => RunSourceBindingCaseAsync("payload-expired", nameof(TerminalPayloadJoin_RejectsExpiredOriginalClock));
    [Fact]
    public Task TerminalPayloadJoin_RejectsAnotherRealSameByteTransfer()
        => RunSourceBindingCaseAsync("payload-foreign", nameof(TerminalPayloadJoin_RejectsAnotherRealSameByteTransfer));
    [Fact]
    public Task TerminalPayloadJoin_RejectsUnjoinedOrigins()
        => RunSourceBindingCaseAsync("payload-origin", nameof(TerminalPayloadJoin_RejectsUnjoinedOrigins));

    private static void CheckTerminalPayloadJoinResult(string directory, JsonElement primary, string mutation)
    {
        var join = primary.GetProperty("TerminalPayloadJoin");
        bool expected = mutation.Length == 0;
        Assert.Equal(expected, join.GetProperty("Joined").GetBoolean());
        Assert.Equal(mutation, join.GetProperty("Mutation").GetString());
        Assert.Equal(12, join.GetProperty("RejectedOwnerControls").GetInt32());
        Assert.True(join.GetProperty("RepeatedJoinRejected").GetBoolean());
        Assert.Equal(0, join.GetProperty("AdditionalWriterReads").GetInt32());
        Assert.Equal(0, join.GetProperty("PrimaryPayloadHashPasses").GetInt32());
        Assert.False(join.GetProperty("AcceptanceChanged").GetBoolean());
        Assert.InRange(join.GetProperty("CumulativeRecordVisits").GetInt64(), 1, 4096);
        if (expected)
        {
            Assert.Equal(primary.GetProperty("SourceLines").GetInt64() + 2, join.GetProperty("RecordVisits").GetInt64());
            Assert.InRange(join.GetProperty("CodeUnitVisits").GetInt64(), 1, 1_073_741_824);
        }
        if (mutation is "origin" or "expired" or "budget") Assert.Equal(0, join.GetProperty("RecordVisits").GetInt64());
        if (!primary.TryGetProperty("PreVerdictIntegrity", out _)) { Assert.False(primary.GetProperty("GuiIntegrityOk").GetBoolean()); }
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", primary.GetProperty("PrimaryVerdict").GetString());
        var route = File.ReadAllLines(Path.Combine(directory, "primary", "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("live_route_epoch_proof_verdict=fail", route);
        using var cleanup = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "primary", "terminal-payload-cleanup.json")));
        Assert.Equal(expected, cleanup.RootElement.GetProperty("HadJoin").GetBoolean());
        Assert.True(cleanup.RootElement.GetProperty("JoinRetired").GetBoolean());
    }
}
