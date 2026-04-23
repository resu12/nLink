using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Core;
using NLink.Core.Diagnostics;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class DiagnosticsRedactorTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void DiagnosticsRedactor_Redacts_Seeds_PrivateKeys_AndWalletSecrets()
    {
        var sample = string.Join(Environment.NewLine, new[]
        {
            "seedBase64=QkFTRTY0U0VFRA==",
            "seedHex=0123456789abcdef0123456789abcdef",
            "walletSeed: horse battery staple",
            "privateKey=-----BEGIN PRIVATE KEY-----abc-----END PRIVATE KEY-----",
            "walletMnemonic=\"alpha beta gamma delta\"",
            "normal=value"
        });

        var redacted = DiagnosticsRedactor.Redact(sample);

        Assert.DoesNotContain("QkFTRTY0U0VFRA==", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("horse battery staple", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alpha beta gamma delta", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("normal=value", redacted, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void DiagnosticsCopy_UsesDiagnosticsRedactor_ForSensitiveDiagnosticsContent()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        try
        {
            PersistenceDiagnostics.ClearForTests();
            SessionTimeline.Clear();
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "DEVLOCAL");
            NknRuntimeDiagnostics.SetLastError("walletSeed: top secret wallet seed");
            NknRuntimeDiagnostics.SetLastDisconnectReason("seedHex=deadbeefdeadbeefdeadbeefdeadbeef");

            var config = TransportRuntimeConfig.Select();
            var vm = new DiagnosticsPageViewModel(static () => { }, config);

            string? copied = null;
            vm.CopyReliabilityLogRequested += (_, text) => copied = text;
            vm.CopyReliabilityLogCommand.Execute(null);

            Assert.NotNull(copied);
            Assert.DoesNotContain("top secret wallet seed", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("deadbeefdeadbeefdeadbeefdeadbeef", copied!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[REDACTED]", copied!, StringComparison.Ordinal);
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            SessionTimeline.Clear();
            NknRuntimeDiagnostics.SetLastError("(none)");
            NknRuntimeDiagnostics.SetLastDisconnectReason("(none)");
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void DiagnosticsRedactor_Redacts_Session_And_Peer_Metadata()
    {
        var sample = string.Join(Environment.NewLine, new[]
        {
            "session_id=abc123-session",
            "expected_session_id=expected-session",
            "helper_identity=nlink-helper-123",
            "target=nlink-target-456",
            "source=nlink-source-789",
            "expected_source=nlink-expected-000",
            "peer_id=peer-123",
            "reply_to=req-555",
            "msg_id=msg-999",
            "run_id=run-444",
            "normal=value"
        });

        var redacted = DiagnosticsRedactor.Redact(sample);

        Assert.DoesNotContain("abc123-session", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("expected-session", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("nlink-helper-123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("nlink-target-456", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("nlink-source-789", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("nlink-expected-000", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("peer-123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("req-555", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("msg-999", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("run-444", redacted, StringComparison.Ordinal);
        Assert.Contains("normal=value", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void DiagnosticsCopy_IncludesBestEffortNotice_AndPersistenceSummary()
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        try
        {
            PersistenceDiagnostics.ClearForTests();
            PersistenceDiagnostics.Record(
                domain: "recent_connect_targets",
                operation: "save",
                severity: PersistenceDiagnosticSeverity.Warning,
                outcome: PersistenceDiagnosticOutcome.Fallback,
                reason: "UnauthorizedAccessException",
                userWarning: "Recent targets could not be saved.");

            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "DEVLOCAL");
            var config = TransportRuntimeConfig.Select();
            var vm = new DiagnosticsPageViewModel(static () => { }, config);

            var copied = vm.BuildDiagnosticsCopyTextForTests();

            Assert.Contains("Redaction is best-effort only", copied, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("persistence_summary:", copied, StringComparison.Ordinal);
            Assert.Contains("persistence_warning:", copied, StringComparison.Ordinal);
            Assert.Contains("Recent targets could not be saved.", copied, StringComparison.Ordinal);
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }
}
