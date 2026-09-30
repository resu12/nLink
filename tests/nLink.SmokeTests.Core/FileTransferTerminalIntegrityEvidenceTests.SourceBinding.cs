using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

public sealed partial class FileTransferTerminalIntegrityEvidenceTests
{
    private const string Bound = "verified_offline_source_terminal_binding";

    [Fact]
    public Task TerminalSource_RealTransferAndDetachedControls()
        => RunSourceBindingCaseAsync("positive", nameof(TerminalSource_RealTransferAndDetachedControls));

    [Fact]
    public Task TerminalSource_AnotherRealTransferCannotSupplyEvidence()
        => RunSourceBindingCaseAsync("replay", nameof(TerminalSource_AnotherRealTransferCannotSupplyEvidence));

    [Fact]
    public Task TerminalSource_ChangedLogCannotReuseCapture()
        => RunSourceBindingCaseAsync("log_changed", nameof(TerminalSource_ChangedLogCannotReuseCapture));

    [Fact]
    public Task TerminalSource_ByteIdenticalReplacementCannotReuseCapture()
        => RunSourceBindingCaseAsync("log_replaced", nameof(TerminalSource_ByteIdenticalReplacementCannotReuseCapture));

    [Fact]
    public Task TerminalSource_MissingLogCannotReuseCapture()
        => RunSourceBindingCaseAsync("log_missing", nameof(TerminalSource_MissingLogCannotReuseCapture));

    [Fact]
    public Task TerminalSource_ChangedDestinationCannotReuseCapture()
        => RunSourceBindingCaseAsync("destination_changed", nameof(TerminalSource_ChangedDestinationCannotReuseCapture));

    [Fact]
    public Task TerminalSource_CanceledTransferCannotProveCompletion()
        => RunSourceBindingCaseAsync("canceled", nameof(TerminalSource_CanceledTransferCannotProveCompletion));

    [Fact]
    public void TerminalSource_TokensSurviveWithoutUnredactingRawIdsOrSecrets()
    {
        var raw = "terminal_session_0123456789abcdef0123456789abcdef";
        var token = TerminalToken(raw);
        var text = $"event=transfer_terminal; terminal_session_id={token}; terminal_transfer_id={token}; " +
            $"session_id={raw}; transfer_id={raw}; saved_path=C:/files/{new string('a', 32)}/received.bin; " +
            $"sharedKey={raw}; private_key={raw}; payloadBase64={raw}; terminal_other={new string('b', 64)}";
        var output = SensitiveDataRedactor.Redact(text);
        Assert.Contains($"terminal_session_id={token}; terminal_transfer_id={token};", output);
        Assert.Contains("session_id=[redacted]; transfer_id=[redacted];", output);
        Assert.Contains("saved_path=", output);
        Assert.DoesNotContain(new string('a', 32), output);
        Assert.DoesNotContain(raw, output);
        Assert.DoesNotContain(new string('b', 64), output);
        Assert.Equal(output, SensitiveDataRedactor.Redact(output));
    }

    private static async Task RunSourceBindingCaseAsync(string scenario, string testName)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        if (Environment.GetEnvironmentVariable("NLINK_TERMINAL_SOURCE_FIXTURE") == "1")
        {
            await CaptureTerminalSourceAsync(scenario);
            return;
        }

        var root = FindRepoRoot();
        var runId = Guid.NewGuid().ToString("D");
        var directory = Path.Combine(root, "artifacts", scenario.StartsWith("native-authenticated-terminal-", StringComparison.Ordinal)
            ? "selected-terminal-join-20260926" : scenario.StartsWith("native-authenticated-", StringComparison.Ordinal)
            ? "authenticated-payload-handoff-20260926" : "terminal-source-tokens-20260925", runId);
        Directory.CreateDirectory(directory);
        // The isolated child alone owns/drains this static logger. Its scope
        // is fixed before initialization, with no global reset in the runner.
        using var child = new Process { StartInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = root,
        } };
        child.StartInfo.ArgumentList.Add("vstest");
        child.StartInfo.ArgumentList.Add(typeof(FileTransferTerminalIntegrityEvidenceTests).Assembly.Location);
        child.StartInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=NLink.SmokeTests.FileTransferTerminalIntegrityEvidenceTests." + testName);
        child.StartInfo.Environment["NLINK_TERMINAL_SOURCE_FIXTURE"] = "1";
        child.StartInfo.Environment["NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR"] = directory;
        child.StartInfo.Environment["NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH"] = Path.Combine(directory, "nlink.log");
        child.StartInfo.Environment["NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID"] = runId;
        child.StartInfo.Environment["NLINK_FILETRANSFER_SCENARIO_RUN_ID"] = runId + ":terminal-source";
        child.Start();
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(
            scenario.StartsWith("primary-", StringComparison.Ordinal) || scenario.StartsWith("native-", StringComparison.Ordinal) ? 120 : 60));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch { if (!child.HasExited) { child.Kill(entireProcessTree: true); } throw; }
        await File.WriteAllTextAsync(Path.Combine(directory, "child.stdout.txt"), await stdout);
        await File.WriteAllTextAsync(Path.Combine(directory, "child.stderr.txt"), await stderr);
        Assert.True(child.ExitCode == 0, await stdout + Environment.NewLine + await stderr);
        // The parent checks actual child execution, never imports JSON as a capability.
        Assert.True(File.Exists(Path.Combine(directory, "source-terminal-result.json")));
    }

    private static async Task CaptureTerminalSourceAsync(string scenario)
    {
        if (scenario.StartsWith("native-authenticated-", StringComparison.Ordinal))
        {
            await CheckAuthenticatedPayloadHandoffAsync(scenario);
            return;
        }
        var directory = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR")!;
        var path = Path.Combine(directory, "nlink.log");
        Assert.Equal(path, LocalOperationalLog.LogFilePath);
        var run = await TransferAsync(interrupt: scenario == "canceled");
        // Two genuine transfers in one isolated writer scope test transfer-owner
        // replay. This is not a claim about independent peers or primary owners.
        var second = scenario is "replay" or "held-replay" or "payload-foreign" ? await TransferAsync() : null;
        await WaitUntilAsync(() => ReadTerminalRows(path).Length == (second is null ? 2 : 4), 5000);
        Assert.Equal("writer_not_completed", CheckTerminalRows(run, ReadTerminalRows(path), false));
        Assert.True(LocalOperationalLog.CompleteAndFlush(TimeSpan.FromSeconds(5)));

        var owner = CaptureBoundOwner(run, path);
        var capture = CaptureBoundEvidence(owner);
        var expected = scenario == "canceled" ? "terminal_not_completed" : Bound;
        Assert.Equal(expected, CheckBinding(owner, capture));
        WriteDiagnostic(run, capture.Terminal, "terminal-evidence.json");
        var controls = new Dictionary<string, string>();
        if (scenario == "positive") { CheckDetachedTerminalControls(owner, capture, controls); }
        if (second is not null)
        {
            var other = CaptureBoundOwner(second, path);
            var otherCapture = CaptureBoundEvidence(other);
            Assert.Equal(Bound, CheckBinding(other, otherCapture));
            Assert.Equal(capture.Terminal.Destination!.Sha256Base64, otherCapture.Terminal.Destination!.Sha256Base64);
            Reject("other_owner", CheckBinding(other, capture), "source_owner");
            Reject("rebound_source", CheckBinding(other, capture with { Owner = other }), "capture_owner");
            Reject("relabeled_attempt", CheckBinding(other, capture with
            {
                Owner = other, Terminal = capture.Terminal with { Attempt = second.Attempt },
            }), "capture_owner");
            Reject("foreign_rows", CheckBinding(other, otherCapture with { Terminals = capture.Terminals }), "terminal_scope_mismatch");
            WriteBoundDiagnostic(directory, "second-transfer.json", other, otherCapture, Bound, new());
            WriteDiagnostic(second, otherCapture.Terminal, "terminal-evidence.json");
        }
        WriteBoundDiagnostic(directory, "source-terminal-observation.json", owner, capture, expected, controls);

        if (scenario is "log_changed" or "log_replaced" or "log_missing")
        {
            if (scenario == "log_changed")
            {
                File.Copy(path, path + ".before-change");
                CorruptOneByte(path);
                expected = "log_changed";
            }
            else
            {
                File.Move(path, path + ".original");
                if (scenario == "log_replaced") { File.Copy(path + ".original", path); }
                expected = scenario == "log_replaced" ? "log_identity" : "log_missing";
            }
        }
        else if (scenario == "destination_changed")
        {
            File.Copy(run.DestinationPath, run.DestinationPath + ".before-change");
            CorruptOneByte(run.DestinationPath);
            expected = "destination_hash";
        }
        Assert.Equal(expected, CheckBinding(owner, capture));
        WriteBoundDiagnostic(directory, "source-terminal-result.json", owner, capture, expected, controls);
        if (scenario.StartsWith("primary-", StringComparison.Ordinal))
        {
            await CheckOriginalPrimaryAsync(owner, capture, directory, scenario["primary-".Length..]);
        }
        if (scenario.StartsWith("held-", StringComparison.Ordinal))
        {
            CheckHeldFileControls(owner, capture, second, directory, scenario);
        }
        if (scenario.StartsWith("native-writer-", StringComparison.Ordinal))
        {
            await CheckOriginalPrimaryAsync(owner, capture, directory, scenario["native-writer-".Length..],
                nativeHandoff: true, nativeRequestBinding: true, nativePrimaryFiles: true, nativeCollector: true,
                collectorFinalizer: true, collectorOutputs: true, collectorPrimary: true, nativeWitnessWriter: true);
        }
        else if (scenario.StartsWith("preverdict-", StringComparison.Ordinal))
        {
            var mode = scenario["preverdict-".Length..];
            await CheckOriginalPrimaryAsync(owner, capture, directory, mode == "powershell" ? "powershell" : "pwsh",
                nativeHandoff: true, nativeRequestBinding: true, nativePrimaryFiles: true, nativeCollector: true,
                collectorFinalizer: true, collectorOutputs: true, collectorPrimary: true, collectorMembership: true,
                terminalPayloadJoin: true, terminalMutation: mode == "unjoined" ? "token" : "", preVerdictCase: mode);
        }
        else if (scenario.StartsWith("payload-", StringComparison.Ordinal))
        {
            var mode = scenario["payload-".Length..];
            var mutation = mode is "pwsh" or "powershell" ? "" : mode;
            await CheckOriginalPrimaryAsync(owner, capture, directory, mutation.Length == 0 ? mode : "pwsh",
                nativeHandoff: true, nativeRequestBinding: true, nativePrimaryFiles: true, nativeCollector: true,
                collectorFinalizer: true, collectorOutputs: true, collectorPrimary: true, collectorMembership: true,
                membershipMutation: mutation == "origin" ? "origin-coordinate" : "", terminalPayloadJoin: true, terminalMutation: mutation);
        }
        else if (scenario == "native-controls") { await CheckNativeParentControlsAsync(owner, capture, directory); }
        else if (scenario.StartsWith("collector-origin-join-reject-", StringComparison.Ordinal))
        {
            await CheckOriginalPrimaryAsync(owner, capture, directory, scenario["collector-origin-join-reject-".Length..],
                nativeHandoff: true, nativeRequestBinding: true, nativePrimaryFiles: true, nativeCollector: true,
                collectorFinalizer: true, collectorOutputs: true, collectorPrimary: true,
                collectorMembership: true, membershipMutation: "origin-coordinate");
        }
        else if (scenario.StartsWith("collector-membership-", StringComparison.Ordinal))
        {
            var mode = scenario["collector-membership-".Length..];
            var mutation = mode is "foreign-member" or "partial-read" ? mode : "";
            await CheckOriginalPrimaryAsync(owner, capture, directory, mutation.Length == 0 ? mode : "pwsh",
                nativeHandoff: true, nativeRequestBinding: true, nativePrimaryFiles: true, nativeCollector: true,
                collectorFinalizer: true, collectorOutputs: true, collectorPrimary: true,
                collectorMembership: true, membershipMutation: mutation);
        }
        else if (scenario.StartsWith("collector-primary-reject-", StringComparison.Ordinal))
        {
            await CheckNativePrimaryRejectionAsync(owner, capture, directory, scenario["collector-primary-reject-".Length..], collectorPrimary: true);
        }
        else if (scenario.StartsWith("collector-primary-", StringComparison.Ordinal))
        {
            await CheckOriginalPrimaryAsync(owner, capture, directory, scenario["collector-primary-".Length..],
                nativeHandoff: true, nativeRequestBinding: true, nativePrimaryFiles: true, nativeCollector: true,
                collectorFinalizer: true, collectorOutputs: true, collectorPrimary: true);
        }
        else if (scenario.StartsWith("native-output-expiry-", StringComparison.Ordinal))
        {
            await CheckNativeCollectorRejectionAsync(owner, capture, directory, "capture-expired",
                collectorOutputs: true, host: scenario["native-output-expiry-".Length..]);
        }
        else if (scenario.StartsWith("native-output-reject-", StringComparison.Ordinal))
        {
            await CheckNativeCollectorRejectionAsync(owner, capture, directory, scenario["native-output-reject-".Length..], collectorOutputs: true);
        }
        else if (scenario.StartsWith("native-output-", StringComparison.Ordinal))
        {
            await CheckOriginalPrimaryAsync(owner, capture, directory, scenario["native-output-".Length..],
                nativeHandoff: true, nativeRequestBinding: true, nativePrimaryFiles: true, nativeCollector: true,
                collectorFinalizer: true, collectorOutputs: true);
        }
        else if (scenario.StartsWith("native-collector-finalizer-", StringComparison.Ordinal))
        {
            await CheckOriginalPrimaryAsync(owner, capture, directory, scenario["native-collector-finalizer-".Length..],
                nativeHandoff: true, nativeRequestBinding: true, nativePrimaryFiles: true, nativeCollector: true, collectorFinalizer: true);
        }
        else if (scenario.StartsWith("native-collector-reject-", StringComparison.Ordinal))
        {
            await CheckNativeCollectorRejectionAsync(owner, capture, directory, scenario["native-collector-reject-".Length..]);
        }
        else if (scenario.StartsWith("native-collector-", StringComparison.Ordinal))
        {
            await CheckOriginalPrimaryAsync(owner, capture, directory, scenario["native-collector-".Length..],
                nativeHandoff: true, nativeRequestBinding: true, nativePrimaryFiles: true, nativeCollector: true);
        }
        else if (scenario.StartsWith("native-primary-reject-", StringComparison.Ordinal))
        {
            await CheckNativePrimaryRejectionAsync(owner, capture, directory, scenario["native-primary-reject-".Length..]);
        }
        else if (scenario.StartsWith("native-primary-", StringComparison.Ordinal))
        {
            await CheckOriginalPrimaryAsync(owner, capture, directory, scenario["native-primary-".Length..],
                nativeHandoff: true, nativeRequestBinding: true, nativePrimaryFiles: true);
        }
        else if (scenario.StartsWith("native-request-", StringComparison.Ordinal))
        {
            await CheckOriginalPrimaryAsync(owner, capture, directory, scenario["native-request-".Length..],
                nativeHandoff: true, nativeRequestBinding: true);
        }
        else if (scenario.StartsWith("native-", StringComparison.Ordinal))
        {
            await CheckOriginalPrimaryAsync(owner, capture, directory, scenario["native-".Length..], nativeHandoff: true);
        }

        void Reject(string name, string actual, string reason)
        {
            Assert.Equal(reason, actual);
            controls.Add(name, actual);
        }
    }

    private sealed record TerminalLogRow(int LineNumber, string RawLine)
    {
        // Required fields must appear exactly once; repeated unrelated route
        // descriptions neither matter nor get promoted into evidence.
        public string? Field(string name)
        {
            var matches = Regex.Matches(RawLine, @"(?:^|;|\] )\s*" + Regex.Escape(name) + @"=([^;]*)",
                RegexOptions.CultureInvariant);
            return matches.Count == 1 ? matches[0].Groups[1].Value.Trim() : null;
        }
    }

    private sealed record BoundOwner(TransferRun Run, string Path, string FileIdentity,
        FileDigest Digest, TerminalLogRow[] Rows, string WriterId);
    private sealed record BoundCapture(BoundOwner Owner, Capture Terminal, TerminalLogRow[] Terminals);

    private static BoundOwner CaptureBoundOwner(TransferRun run, string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.True(OperationalLogNativeSource.TryDescribe(source.SafeFileHandle, out var identity, out var bytes));
        Assert.InRange(bytes, 1, 4 * 1024 * 1024);
        var digest = Digest(source, path);
        var rows = ReadLogRows(source);
        Assert.InRange(rows.Length, 3, 4096);
        var writer = LocalOperationalLog.WriterIdForTests;
        Assert.All(rows, row => Assert.Equal(writer, row.Field("log_writer_id")));
        Assert.Equal(Enumerable.Range(1, rows.Length).Select(i => (long)i),
            rows.Select(row => long.Parse(row.Field("log_writer_sequence")!)));
        Assert.Equal("operational_log_writer_started", rows[0].Field("event"));
        Assert.Equal("operational_log_writer_drained", rows[^1].Field("event"));
        Assert.Equal((rows.Length - 1).ToString(), rows[^1].Field("last_payload_sequence"));
        foreach (var row in new[] { rows[0], rows[^1] })
        {
            Assert.Equal(Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID"), row.Field("acceptance_run_id"));
            Assert.Equal(Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SCENARIO_RUN_ID"), row.Field("scenario_run_id"));
        }
        Assert.All(rows.Where(row => row.Field("event") != "transfer_terminal"), row =>
        {
            Assert.Null(row.Field("terminal_session_id"));
            Assert.Null(row.Field("terminal_transfer_id"));
        });
        Assert.Throws<IOException>(() => { using var write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        Assert.Throws<IOException>(() => File.Move(path, path + ".unexpected-move"));
        Assert.Equal(digest, Digest(source, path));
        Assert.Equal(bytes, digest.Bytes);
        Assert.Equal((long)rows.Length, LocalOperationalLog.WriterSequenceForTests);
        return new BoundOwner(run, path, identity!, digest, rows, writer);
    }

    private static BoundCapture CaptureBoundEvidence(BoundOwner owner)
    {
        var terminal = Observe(owner.Run);
        var rows = owner.Rows.Where(row => row.Field("event") == "transfer_terminal" &&
            row.Field("terminal_transfer_id") == TerminalToken(owner.Run.TransferId)).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.Equal("[redacted]", row.Field("session_id"));
            Assert.Equal("[redacted]", row.Field("transfer_id"));
            Assert.Equal(TerminalToken(owner.Run.SessionId), row.Field("terminal_session_id"));
        });
        return new(owner, terminal, rows);
    }

    private static string CheckBinding(BoundOwner expected, BoundCapture capture)
    {
        if (!ReferenceEquals(expected, capture.Owner)) { return "source_owner"; }
        var terminalStatus = Check(expected.Run, capture.Terminal);
        if (terminalStatus != Verified) { return terminalStatus; }
        if (!File.Exists(expected.Path)) { return "log_missing"; }
        using var source = new FileStream(expected.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var binding = CheckBoundLog(expected, capture, source);
        if (binding != Bound) { return binding; }
        // An earlier successful capture is not a fresh file-integrity result.
        terminalStatus = Check(expected.Run, Observe(expected.Run));
        return terminalStatus == Verified ? Bound : terminalStatus;
    }

    private static string CheckBoundLog(BoundOwner expected, BoundCapture capture, FileStream source)
    {
        if (!ReferenceEquals(expected, capture.Owner)) { return "source_owner"; }
        var terminalStatus = Check(expected.Run, capture.Terminal);
        if (terminalStatus != Verified) { return terminalStatus; }
        if (!OperationalLogNativeSource.TryDescribe(source.SafeFileHandle, out var identity, out var bytes) ||
            identity != expected.FileIdentity) { return "log_identity"; }
        if (bytes != expected.Digest.Bytes) { return "log_changed"; }
        if (Digest(source, expected.Path) != expected.Digest) { return "log_changed"; }
        foreach (var row in capture.Terminals)
        {
            if (row.LineNumber < 1 || row.LineNumber > expected.Rows.Length ||
                row.RawLine != expected.Rows[row.LineNumber - 1].RawLine) { return "source_origin"; }
        }
        var identityStatus = CheckTerminalRows(expected.Run, capture.Terminals, true);
        if (identityStatus != "terminal_pair_matches") { return identityStatus; }
        if (LocalOperationalLog.WriterSequenceForTests != expected.Rows.Length) { return "writer_changed"; }
        return Bound;
    }

    private static string CheckTerminalRows(TransferRun run, TerminalLogRow[] rows, bool writerCompleted)
    {
        if (!writerCompleted) { return "writer_not_completed"; }
        if (rows.Length != 2 || rows.Select(row => row.LineNumber).Distinct().Count() != 2) { return "terminal_pair_missing_or_duplicate"; }
        if (rows.Any(row => row.Field("event") != "transfer_terminal")) { return "terminal_event"; }
        if (!rows.Select(row => row.Field("direction")).OrderBy(value => value).SequenceEqual(new[] { "inbound", "outbound" }))
        { return "terminal_directions"; }
        foreach (var row in rows)
        {
            foreach (var key in new[] { "terminal_session_id", "terminal_transfer_id" })
            {
                if (!Regex.IsMatch(row.Field(key) ?? "", @"\Afte1-[a-f0-9]{24}\z", RegexOptions.CultureInvariant))
                { return "terminal_token_missing_or_malformed"; }
            }
            if (row.Field("terminal_session_id") != TerminalToken(run.SessionId) ||
                row.Field("terminal_transfer_id") != TerminalToken(run.TransferId)) { return "terminal_scope_mismatch"; }
            if (row.Field("error_code") != "(none)") { return "terminal_error"; }
            if (row.Field("reason") != "Transfer complete.") { return "terminal_not_completed"; }
            if (row.Field("file_size_bytes") != run.ExpectedSource.Bytes.ToString() ||
                row.Field("bytes_transferred") != run.ExpectedSource.Bytes.ToString()) { return "terminal_size"; }
        }
        return "terminal_pair_matches";
    }

    private static void CheckDetachedTerminalControls(BoundOwner owner, BoundCapture capture, Dictionary<string, string> controls)
    {
        var rows = capture.Terminals;
        Test("missing_row", rows[..1], "terminal_pair_missing_or_duplicate");
        Test("duplicate_row", [rows[0], rows[0]], "terminal_pair_missing_or_duplicate");
        foreach (var key in new[] { "terminal_session_id", "terminal_transfer_id" })
        {
            foreach (var value in new[] { "", "(none)", "[redacted]", "fte2-" + new string('a', 24),
                "fte1-" + new string('A', 24), "fte1-" + new string('a', 23), "fte1-" + new string('a', 25) })
            { Test(key + ":" + value, Change(key, value), "terminal_token_missing_or_malformed"); }
            Test(key + ":foreign", Change(key, TerminalToken("foreign")), "terminal_scope_mismatch");
            Test(key + ":absent", [rows[0] with { RawLine = rows[0].RawLine.Replace(key + "=", "omitted_" + key + "=") }, rows[1]],
                "terminal_token_missing_or_malformed");
            Test(key + ":duplicate", [rows[0] with { RawLine = rows[0].RawLine + "; " + key + "=" + rows[0].Field(key) }, rows[1]],
                "terminal_token_missing_or_malformed");
        }
        var swapped = Change("terminal_session_id", rows[0].Field("terminal_transfer_id")!);
        swapped[0] = swapped[0] with { RawLine = swapped[0].RawLine.Replace(
            "terminal_transfer_id=" + rows[0].Field("terminal_transfer_id"), "terminal_transfer_id=" + rows[0].Field("terminal_session_id")) };
        Test("swapped_tokens", swapped, "terminal_scope_mismatch");
        Test("wrong_direction", Change("direction", rows[1].Field("direction")!), "terminal_directions");
        Test("wrong_event", Change("event", "unrelated"), "terminal_event");
        Test("terminal_error", Change("error_code", "failed"), "terminal_error");
        Test("terminal_incomplete", Change("reason", "Canceled"), "terminal_not_completed");
        Test("terminal_size", Change("file_size_bytes", "1"), "terminal_size");
        Test("terminal_progress", Change("bytes_transferred", "1"), "terminal_size");
        Assert.Equal("source_owner", CheckBinding(owner, capture with { Owner = owner with { } }));
        controls.Add("copied_owner", "source_owner");
        Assert.Equal("source_origin", CheckBinding(owner, capture with { Terminals = Change("terminal_session_id", TerminalToken("foreign")) }));
        controls.Add("changed_origin_text", "source_origin");
        Assert.Equal("source_origin", CheckBinding(owner, capture with
        { Terminals = [rows[0] with { LineNumber = rows[0].LineNumber + 1 }, rows[1]] }));
        controls.Add("changed_origin_line", "source_origin");
        Assert.Equal(Bound, CheckBinding(owner, capture));

        TerminalLogRow[] Change(string key, string value)
            => [rows[0] with { RawLine = rows[0].RawLine.Replace(key + "=" + rows[0].Field(key), key + "=" + value) }, rows[1]];
        void Test(string name, TerminalLogRow[] copy, string reason)
        {
            var result = CheckTerminalRows(owner.Run, copy, true);
            Assert.Equal(reason, result);
            controls.Add(name, result);
        }
    }

    private static string TerminalToken(string value)
        => "fte1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    private static TerminalLogRow[] ReadLogRows(FileStream source)
    {
        source.Position = 0;
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var rows = new List<TerminalLogRow>();
        while (reader.ReadLine() is { } line) { rows.Add(new(rows.Count + 1, line)); }
        return rows.ToArray();
    }

    private static TerminalLogRow[] ReadTerminalRows(string path)
    {
        if (!File.Exists(path)) { return []; }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ReadLogRows(stream).Where(row => row.Field("event") == "transfer_terminal" &&
            row.Field("log_writer_sequence") is not null).ToArray();
    }

    private static void CorruptOneByte(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        file.Position = file.Length / 2;
        var value = file.ReadByte();
        file.Position--;
        file.WriteByte((byte)(value ^ 0xff));
        file.Flush(flushToDisk: true);
    }

    private static void WriteBoundDiagnostic(string directory, string name, BoundOwner owner, BoundCapture capture,
        string status, Dictionary<string, string> controls)
        => File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(new
        {
            Schema = "nlink.test-terminal-source-binding/v1", Scope = "offline_service_source_terminal_binding",
            Status = status, SameTransferBindingProven = status == Bound,
            owner.Run.Attempt, owner.Run.SessionId, owner.Run.TransferId, TransferDirectory = owner.Run.Directory,
            LogSource = new { owner.Path, owner.FileIdentity, owner.Digest, LineCount = owner.Rows.Length, owner.WriterId },
            capture.Terminal.Outbound, capture.Terminal.Inbound, capture.Terminal.Source, capture.Terminal.Destination,
            TerminalRows = capture.Terminals, Controls = controls, ControlsAreAcceptanceEvidence = false,
            AuthorityEvaluations = 0, CanonicalScenarioProven = false, WholeScenarioHandoffProven = false,
            AcceptanceChanged = false, Live = false,
        }, JsonOptions));
}
