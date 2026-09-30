using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

public sealed class FileTransferDiagnosticScopeEvidenceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("data-session:1")]
    [InlineData(" data-session:1 ")]
    [InlineData("žluťoučký-🚀:1")]
    [InlineData("fte1-0123456789abcdef01234567")]
    [InlineData("data-session; injected=1\r\n")]
    public void DataSessionFormatter_UsesOriginalUtf8Identity(string identity)
    {
        var token = "fte1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
        Assert.Equal($"frontier_scope_data_session_identity={token}; ", FileTransferDiagnosticScopeEvidence.FormatDataSession(identity));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("(none)")]
    [InlineData("[redacted]")] [InlineData("[redacted]:1")] [InlineData("prefix[redacted]suffix")]
    public void DataSessionFormatter_UnavailableInputCannotBecomeEvidence(string? identity)
        => Assert.Equal("frontier_scope_data_session_identity=(none); ", FileTransferDiagnosticScopeEvidence.FormatDataSession(identity));

    [Fact]
    public void DataSessionFormatter_PreservesProjectionWithoutExposingOriginalIdentity()
    {
        var transfer = "native-carrier-" + new string('b', 31) + "2";
        var identity = transfer + ":1";
        var projection = FileTransferDiagnosticScopeEvidence.FormatDataSession(identity);
        var raw = $"event=filetransfer_v6_frontier_recent_progress_grace_deferred; {projection}data_session_identity={identity}; data_session_generation=1; secret={new string('c', 64)}";
        var redacted = SensitiveDataRedactor.Redact(raw);
        Assert.Contains(projection, redacted, StringComparison.Ordinal);
        Assert.Contains("data_session_identity=[redacted]:1; data_session_generation=1;", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(transfer, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('c', 64), redacted, StringComparison.Ordinal);
        Assert.Equal(redacted, SensitiveDataRedactor.Redact(redacted));
    }

    [Fact]
    public void DataSessionEmitters_KeepFiveOriginalTypedSourcesAndRawFields()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/nLink.Core/FileTransfer/SessionFileTransferService.PullTransferSessionV6.cs"));
        var sites = new Dictionary<string, string>
        {
            ["filetransfer_v6_fallback_frontier_repair_progress_state_forced"] = "progressReceipt.DataSessionIdentity",
            ["filetransfer_v6_frontier_request_deferred"] = "dataSessionIdentity",
            ["filetransfer_v6_frontier_stall_grace_recheck_decided"] = "wake.DataSessionIdentity",
            ["filetransfer_v6_frontier_recent_progress_grace_deferred"] = "wake.DataSessionIdentity",
            ["filetransfer_v6_frontier_recent_progress_grace_recheck_decided"] = "wake.DataSessionIdentity",
        };
        var lines = source.Split('\n').Where(line => line.Contains("FileTransferDiagnosticScopeEvidence.FormatDataSession(", StringComparison.Ordinal)).ToArray();
        Assert.Equal(sites.Count, lines.Length);
        foreach (var (name, argument) in sites)
        {
            var line = Assert.Single(lines, line => line.Contains("event=" + name + ";", StringComparison.Ordinal));
            Assert.Contains("{FileTransferDiagnosticScopeEvidence.FormatDataSession(" + argument + ")}", line, StringComparison.Ordinal);
            Assert.Contains("data_session_identity={FormatProtocolLogValue(" + argument + ")};", line, StringComparison.Ordinal);
            var generation = name.EndsWith("state_forced", StringComparison.Ordinal) ? "progressReceipt" : argument == "dataSessionIdentity" ? "context" : "wake";
            Assert.Contains("data_session_generation={" + generation + ".DataSessionGeneration};", line, StringComparison.Ordinal);
        }
        Assert.Contains("var dataSessionIdentity = CreateReceiveRecoveryDataSessionIdentity(context.TransferId, context.DataSession, context.DataSessionGeneration);", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("scope-session", "scope-transfer")]
    [InlineData(" Session ", "TRANSFER")]
    [InlineData("s-žluťoučký", "t-🚀")]
    [InlineData("fte1-0123456789abcdef01234567", "transfer")]
    [InlineData("session; injected=1\r\n", "transfer")]
    public void ScopeFormatter_UsesOriginalUtf8Identity(string session, string transfer)
    {
        static string Token(string value) => "fte1-" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
        Assert.Equal($"frontier_scope_session_id={Token(session)}; frontier_scope_transfer_id={Token(transfer)}; ",
            FileTransferDiagnosticScopeEvidence.Format(session, transfer));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("(none)")]
    [InlineData("[redacted]")] [InlineData("sess_[redacted]")] [InlineData("prefix[redacted]suffix")]
    public void ScopeFormatter_UnavailableInputCannotBorrowOtherIdentity(string? missing)
    {
        const string expected = "frontier_scope_session_id=(none); frontier_scope_transfer_id=(none); ";
        Assert.Equal(expected, FileTransferDiagnosticScopeEvidence.Format(missing, "valid"));
        Assert.Equal(expected, FileTransferDiagnosticScopeEvidence.Format("valid", missing));
    }

    [Fact]
    public void ScopeFormatter_PreservesTokensThroughExistingRedaction()
    {
        var session = "sess_smoke_" + new string('a', 31) + "1";
        var transfer = "native-carrier-" + new string('b', 31) + "2";
        var scope = FileTransferDiagnosticScopeEvidence.Format(session, transfer);
        var raw = $"event=filetransfer_v6_receiver_state_deferred; {scope}session_id={session}; transfer_id={transfer}; secret={new string('c', 64)}";
        var redacted = SensitiveDataRedactor.Redact(raw);
        Assert.Contains(scope, redacted, StringComparison.Ordinal);
        Assert.Contains("session_id=[redacted]", redacted, StringComparison.Ordinal);
        Assert.Contains("transfer_id=[redacted]", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(session, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(transfer, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('c', 64), redacted, StringComparison.Ordinal);
        Assert.Equal(redacted, SensitiveDataRedactor.Redact(redacted));
    }

    [Fact]
    public void ScopeEmitters_UseOnlyReviewedTypedIdentities()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "tests", "nLink.SmokeTests.Core", "Support", "FrontierEventScopeEmitterSites.json");
        var sites = JsonSerializer.Deserialize<EmitterSite[]>(File.ReadAllText(path))!;
        Assert.Equal(81, sites.Length);
        var expected = sites.GroupBy(s => $"{s.Path}|{s.Event}|{s.Arguments}")
            .ToDictionary(g => g.Key, g => g.Count());
        var actual = new Dictionary<string, int>();
        foreach (var file in sites.Select(s => s.Path).Distinct())
        {
            foreach (var line in File.ReadLines(Path.Combine(root, file)))
            {
                if (!line.Contains("FileTransferDiagnosticScopeEvidence.Format(", StringComparison.Ordinal)) continue;
                var call = Regex.Match(line, @"\{FileTransferDiagnosticScopeEvidence\.Format\((?<args>.*?)\)\}");
                var eventName = Regex.Match(line, @"event=(?<event>[^;]+);").Groups["event"].Value;
                Assert.True(call.Success && eventName.Length > 0, line);
                var key = $"{file}|{eventName}|{call.Groups["args"].Value}";
                actual[key] = actual.GetValueOrDefault(key) + 1;
            }
        }
        Assert.Equal(expected.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
    }

    private sealed record EmitterSite(string Path, string Event, string Arguments);
}
