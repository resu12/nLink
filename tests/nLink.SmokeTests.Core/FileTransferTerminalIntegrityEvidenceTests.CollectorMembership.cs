using System.Text.Json;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Fact]
    public Task TerminalCollectorMembership_PowerShell7VerifiesOriginalWriterWithoutFailureWitness()
        => RunSourceBindingCaseAsync("collector-membership-pwsh", nameof(TerminalCollectorMembership_PowerShell7VerifiesOriginalWriterWithoutFailureWitness));

    [Fact]
    public Task TerminalCollectorMembership_WindowsPowerShellVerifiesOriginalWriterWithoutFailureWitness()
        => RunSourceBindingCaseAsync("collector-membership-powershell", nameof(TerminalCollectorMembership_WindowsPowerShellVerifiesOriginalWriterWithoutFailureWitness));

    [Fact]
    public Task TerminalCollectorMembership_ForeignLookupCannotIssueMembership()
        => RunSourceBindingCaseAsync("collector-membership-foreign-member", nameof(TerminalCollectorMembership_ForeignLookupCannotIssueMembership));

    [Fact]
    public Task TerminalCollectorMembership_PartialReadCannotIssueMembership()
        => RunSourceBindingCaseAsync("collector-membership-partial-read", nameof(TerminalCollectorMembership_PartialReadCannotIssueMembership));

    private static void CheckCollectorMembershipResult(string directory, JsonElement primary, string mutation)
    {
        var result = primary.GetProperty("CollectorWriterMembership");
        var expected = mutation.Length == 0 || mutation == "origin-coordinate";
        Assert.Equal(expected, result.GetProperty("Verified").GetBoolean());
        Assert.Equal(mutation, result.GetProperty("Mutation").GetString());
        Assert.Equal(12, result.GetProperty("PreReadControls").GetInt32());
        Assert.Equal(expected ? 9 : 0, result.GetProperty("PostReadControls").GetInt32());
        Assert.Equal(expected ? 0 : -1, result.GetProperty("SelectedIndex").GetInt32());
        foreach (var name in new[] { "LookupIsFailureWitness", "PathReopened", "AcceptanceChanged" })
        { Assert.False(result.GetProperty(name).GetBoolean(), name); }
        foreach (var name in new[] { "RepeatedVerificationRejected", "LegacyReopenRejected" })
        { Assert.True(result.GetProperty(name).GetBoolean(), name); }
        var writerRows = File.ReadAllLines(Path.Combine(directory, "gui", "valid", "filetransfer-gui-log-writer-evidence-slice.log")).Length;
        Assert.Equal(mutation == "partial-read" ? 1 : writerRows, result.GetProperty("WriterRecordsRead").GetInt64());
        if (expected)
        {
            Assert.Equal(2L * writerRows + primary.GetProperty("SourceLines").GetInt64(), result.GetProperty("RecordVisits").GetInt64());
            Assert.InRange(result.GetProperty("CodeUnitVisits").GetInt64(), 1, 1_073_741_824);
        }
        // The legacy publication-witness slot stays empty. Native membership is
        // separately owned and must not masquerade as a selected failure.
        Assert.False(primary.GetProperty("WriterMembershipVerified").GetBoolean());
        using var live = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "primary", "live-primary-observation.json")));
        Assert.Equal(3, live.RootElement.GetProperty("Evaluations").GetInt32());
        Assert.False(live.RootElement.GetProperty("Witness").GetBoolean());
        Assert.False(live.RootElement.GetProperty("Completion").GetBoolean());
        using var cleanup = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "primary", "collector-membership-cleanup.json")));
        Assert.Equal(expected, cleanup.RootElement.GetProperty("HadOwnedMembership").GetBoolean());
        Assert.True(cleanup.RootElement.GetProperty("MembershipRetired").GetBoolean());
        Assert.True(cleanup.RootElement.GetProperty("WriterClosed").GetBoolean());
        Assert.True(cleanup.RootElement.GetProperty("OriginJoinRetired").GetBoolean());
        Assert.Equal(mutation.Length == 0, cleanup.RootElement.GetProperty("HadOriginJoin").GetBoolean());
        Assert.False(cleanup.RootElement.GetProperty("AcceptanceChanged").GetBoolean());
        CheckCollectorMembershipOriginJoin(primary, result, expected, mutation);
    }
}
