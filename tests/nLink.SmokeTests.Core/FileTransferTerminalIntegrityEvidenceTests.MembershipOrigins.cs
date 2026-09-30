using System.Diagnostics;
using System.Text.Json;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Fact]
    public Task TerminalCollectorOriginJoin_PowerShell7RejectsShiftedValidCoordinate()
        => RunSourceBindingCaseAsync("collector-origin-join-reject-pwsh", nameof(TerminalCollectorOriginJoin_PowerShell7RejectsShiftedValidCoordinate));

    [Fact]
    public Task TerminalCollectorOriginJoin_WindowsPowerShellRejectsShiftedValidCoordinate()
        => RunSourceBindingCaseAsync("collector-origin-join-reject-powershell", nameof(TerminalCollectorOriginJoin_WindowsPowerShellRejectsShiftedValidCoordinate));

    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    public async Task TerminalCollectorOriginJoin_ProductionLoaderOmitsJoinApi(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "collector-membership-origins-20260925", host + "-loader-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        using var process = new Process { StartInfo = new ProcessStartInfo(host)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = root,
        } };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "TerminalCollectorOriginJoinLoader.ps1"), root })
        { process.StartInfo.ArgumentList.Add(argument); }
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
        await File.WriteAllTextAsync(Path.Combine(directory, "stdout.txt"), await stdout);
        await File.WriteAllTextAsync(Path.Combine(directory, "stderr.txt"), await stderr);
        Assert.True(process.ExitCode == 0, await stdout + Environment.NewLine + await stderr);
        Assert.Contains("collector-origin-join-api-absent", await stdout);
    }

    private static void CheckCollectorMembershipOriginJoin(JsonElement primary, JsonElement membership, bool verified, string mutation)
    {
        var join = membership.GetProperty("OriginJoin");
        if (!verified) { Assert.Equal(JsonValueKind.Null, join.ValueKind); return; }
        var expected = mutation.Length == 0;
        Assert.Equal(expected, join.GetProperty("Joined").GetBoolean());
        Assert.Equal(14, join.GetProperty("RejectedOwnerControls").GetInt32());
        Assert.True(join.GetProperty("RepeatedJoinRejected").GetBoolean());
        Assert.True(join.GetProperty("CopiedJoinRejected").GetBoolean());
        Assert.Equal(0, join.GetProperty("AdditionalWriterReads").GetInt32());
        Assert.False(join.GetProperty("AcceptanceChanged").GetBoolean());
        var rows = join.GetProperty("Rows").EnumerateArray().ToArray();
        var originals = primary.GetProperty("CollectorLineOrigins").EnumerateArray().ToArray();
        Assert.Equal(expected ? originals.Length : 0, rows.Length);
        Assert.Equal(expected ? originals.Length : 1, join.GetProperty("RecordVisits").GetInt32());
        Assert.InRange(join.GetProperty("CumulativeRecordVisits").GetInt64(), 1, 4096);
        for (int i = 0; i < rows.Length; i++)
            foreach (var field in new[] { "SemanticLine", "ProducerLine", "RetainedLine", "WriterLine" })
            { Assert.Equal(originals[i].GetProperty(field).GetInt32(), rows[i].GetProperty(field).GetInt32()); }
        Assert.False(primary.GetProperty("WriterCopyProvenanceProven").GetBoolean());
        Assert.Equal(primary.TryGetProperty("TerminalPayloadJoin", out var payload) && payload.GetProperty("Joined").GetBoolean(),
            primary.GetProperty("PrimaryFileIntegrityJoinProven").GetBoolean());
        if (!primary.TryGetProperty("PreVerdictIntegrity", out _)) { Assert.False(primary.GetProperty("GuiIntegrityOk").GetBoolean()); }
    }
}
