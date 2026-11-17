using System.Security.Cryptography;
using System.Text;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class OperationalLogPostDrainDiagnosticTests
{
    private const string Header = "[2026-10-01 09:47:12Z] [WARN] ";

    [Fact]
    public void CompletedWriterMarkerAddsDiagnosticsWithoutChangingExactIdentity()
    {
        var batch = CreateBatch(Header + "[SessionRuntime] event=background_task_failed; secret=do-not-copy; message=private text");
        var marker = LocalOperationalLog.FormatWriteMarker(OperationalLogWriteMarker.Failed(batch, "writer_completed", 0));

        Assert.Equal(
            "event=operational_log_write_failed; acceptance_run_id=run; scenario_run_id=run:scenario:1" +
            "; log_writer_id=writer; batch_generation=42; first_sequence=43; last_sequence=44; line_count=2" +
            $"; payload_sha256={batch.PayloadSha256}; attempt_utc_ticks=639264448326558310; attempt_count=0" +
            "; failure_code=writer_completed; attempted_source=SessionRuntime; attempted_event=background_task_failed",
            marker);
    }

    [Theory]
    [InlineData("[SessionRuntime] free-form message; event=not_the_first_field;", "SessionRuntime", "(none)")]
    [InlineData("[SessionRuntime] event=valid_event; message=private text\nevent=second_line;", "SessionRuntime", "valid_event")]
    [InlineData("[SessionRuntime] message=private text\nevent=second_line;", "SessionRuntime", "(none)")]
    [InlineData("[SessionRuntime] event=missing_delimiter", "SessionRuntime", "(none)")]
    [InlineData("[SessionRuntime] event=C:/private/path;", "SessionRuntime", "(none)")]
    [InlineData("[SessionRuntime] event=private@example.com;", "SessionRuntime", "(none)")]
    [InlineData("[SessionRuntime] event=bad\rname;", "SessionRuntime", "(none)")]
    [InlineData("[SessionRuntime] event=[redacted];", "SessionRuntime", "(none)")]
    [InlineData("[SessionRuntime] event=abcdef0123456789abcdef0123456789;", "SessionRuntime", "(none)")]
    [InlineData("[abcdef0123456789abcdef0123456789] event=valid_event;", "(none)", "valid_event")]
    [InlineData("[C:/private/path] event=valid_event;", "(none)", "(none)")]
    [InlineData("[source; event=injected] event=valid_event;", "(none)", "(none)")]
    [InlineData("[source\nname] event=valid_event;", "(none)", "(none)")]
    public void CompletedWriterDiagnosticsDoNotCopyMessagesOrUnsafeIdentifiers(string body, string source, string eventName)
    {
        var marker = LocalOperationalLog.FormatWriteMarker(
            OperationalLogWriteMarker.Failed(CreateBatch(Header + body), "writer_completed", 0));

        Assert.Equal($"attempted_source={source}; attempted_event={eventName}", marker.Split("; failure_code=writer_completed; ")[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an operational entry")]
    [InlineData("\n[2026-10-01 09:47:12Z] [WARN] [SessionRuntime] event=second_line;")]
    public void MissingOrMalformedEntryStillHasTheFailureMarker(string? text)
    {
        var batch = CreateBatch(string.Empty) with { PersistedText = text! };
        var marker = LocalOperationalLog.FormatWriteMarker(OperationalLogWriteMarker.Failed(batch, "writer_completed", 0));

        Assert.StartsWith("event=operational_log_write_failed;", marker);
        Assert.EndsWith("; failure_code=writer_completed; attempted_source=(none); attempted_event=(none)", marker);
    }

    [Theory]
    [InlineData(64, 128, true, true)]
    [InlineData(65, 128, false, false)]
    [InlineData(64, 129, true, false)]
    [InlineData(64, 100000, true, false)]
    public void DiagnosticsRequireCompleteBoundedIdentifiers(int sourceLength, int eventLength, bool hasSource, bool hasEvent)
    {
        var source = new string('S', sourceLength);
        var eventName = new string('e', eventLength - 1) + 'z';
        var marker = LocalOperationalLog.FormatWriteMarker(
            OperationalLogWriteMarker.Failed(CreateBatch(Header + $"[{source}] event={eventName}; private=value"), "writer_completed", 0));

        Assert.EndsWith($"; attempted_source={(hasSource ? source : "(none)")}; attempted_event={(hasEvent ? eventName : "(none)")}", marker);
        Assert.True(marker.Length < 700);
    }

    [Fact]
    public void OtherMarkerKindsAndFailuresRemainUnchanged()
    {
        var batch = CreateBatch(Header + "[SessionRuntime] event=background_task_failed;");
        var markers = new[]
        {
            OperationalLogWriteMarker.Failed(batch, "io_error", 1),
            OperationalLogWriteMarker.Deferred(batch, "writer_completed"),
            OperationalLogWriteMarker.Recovered(batch, 2),
        };
        foreach (var marker in markers)
        {
            var formatted = LocalOperationalLog.FormatWriteMarker(marker);
            Assert.DoesNotContain("attempted_", formatted);
            Assert.EndsWith($"; attempt_count={marker.AttemptCount}; failure_code={marker.FailureCode}", formatted);
        }
    }

    private static OperationalLogWriteBatch CreateBatch(string text)
        => new("run", "run:scenario:1", "writer", 42, 43, 44, 639264448326558310,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
            text, Encoding.UTF8.GetByteCount(text), false);
}
