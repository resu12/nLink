using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Core;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

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
            SessionTimeline.Clear();
            NknRuntimeDiagnostics.SetLastError("(none)");
            NknRuntimeDiagnostics.SetLastDisconnectReason("(none)");
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }
}
