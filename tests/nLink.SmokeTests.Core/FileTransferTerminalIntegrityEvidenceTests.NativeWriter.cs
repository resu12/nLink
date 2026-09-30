using System.Text.Json;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    [Fact]
    public Task NativeWitnessWriter_PowerShell7AcquiresOriginalCollectorHandle()
        => RunSourceBindingCaseAsync("native-writer-pwsh", nameof(NativeWitnessWriter_PowerShell7AcquiresOriginalCollectorHandle));

    [Fact]
    public Task NativeWitnessWriter_WindowsPowerShellAcquiresOriginalCollectorHandle()
        => RunSourceBindingCaseAsync("native-writer-powershell", nameof(NativeWitnessWriter_WindowsPowerShellAcquiresOriginalCollectorHandle));

    private static void CheckNativeWitnessWriterResult(string directory, JsonElement observation)
    {
        var result = observation.GetProperty("CollectorWriterMembership");
        Assert.Equal("verified_native_writer_acquisition_only", result.GetProperty("Status").GetString());
        Assert.True(result.GetProperty("Verified").GetBoolean());
        Assert.Equal(1, result.GetProperty("SelectedIndex").GetInt32());
        Assert.Equal(13, result.GetProperty("PreReadControls").GetInt32());
        Assert.Equal(0, result.GetProperty("PreAcquisitionReads").GetInt32());
        Assert.True(result.GetProperty("RepeatedOpenRejected").GetBoolean());
        Assert.True(result.GetProperty("ReaderRetired").GetBoolean());
        Assert.True(result.GetProperty("PrimaryAndCollectorSurvive").GetBoolean());
        Assert.False(result.GetProperty("PathReopened").GetBoolean());
        Assert.False(result.GetProperty("SelectedFailureProven").GetBoolean());
        Assert.False(result.GetProperty("AcceptanceChanged").GetBoolean());
        Assert.InRange(result.GetProperty("CumulativeRecordVisits").GetInt64(), 1, 4096);
        var writer = File.ReadAllLines(Path.Combine(directory, "gui", "valid", "filetransfer-gui-log-writer-evidence-slice.log"));
        Assert.Equal(writer.Length, result.GetProperty("WriterRecordsRead").GetInt64());
        using var primary = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "primary", "live-primary-observation.json")));
        Assert.Equal(3, primary.RootElement.GetProperty("Evaluations").GetInt32());
        Assert.False(primary.RootElement.GetProperty("Witness").GetBoolean());
        Assert.False(primary.RootElement.GetProperty("Completion").GetBoolean());
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", observation.GetProperty("PrimaryVerdict").GetString());
    }
}
