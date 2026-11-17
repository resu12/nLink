using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class SensitiveDataRedactorSecurityEvidenceTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly (string Field, string Value)[] Evidence =
    [
        ("security_transport_id", "ftst1-" + Hex[..32]),
        ("security_installation_id", "ftsi1-" + Hex[..32]),
        ("security_establishment_fingerprint", "ftest1-" + Hex),
        ("security_payload_fingerprint", "ftpay1-" + Hex),
        ("security_session_id", "fte1-" + Hex[..24]),
        ("security_transfer_id", "fte1-" + Hex[..24]),
    ];

    [Theory]
    [InlineData("filetransfer_security_context_installed")]
    [InlineData("filetransfer_security_context_retired")]
    [InlineData("filetransfer_data_path_send_result")]
    [InlineData("filetransfer_data_path_receive_result")]
    [InlineData("filetransfer_tuna_data_path_peer_visible")]
    [InlineData("filetransfer_tuna_data_path_mismatch")]
    [InlineData("filetransfer_data_frame_ignored")]
    [InlineData("filetransfer_fallback_tail_yield_evidence")]
    public void SecurityEvidence_ExactFieldsSurviveWithoutProtectingSecrets(string eventName)
    {
        var input = $"event={eventName}; " + string.Join("; ", Evidence.Select(item => $"{item.Field}={item.Value}")) +
            $"; sharedKey={Hex}; private_key={Hex}; source={Hex}; payloadBase64={Hex}; arbitrary_fingerprint=ftpay1-{Hex}";
        var output = SensitiveDataRedactor.Redact(input);
        Assert.All(Evidence, item => Assert.Contains($"{item.Field}={item.Value};", output, StringComparison.Ordinal));
        Assert.DoesNotContain($"sharedKey={Hex}", output, StringComparison.Ordinal);
        Assert.DoesNotContain($"private_key={Hex}", output, StringComparison.Ordinal);
        Assert.DoesNotContain($"source={Hex}", output, StringComparison.Ordinal);
        Assert.DoesNotContain($"payloadBase64={Hex}", output, StringComparison.Ordinal);
        Assert.DoesNotContain($"arbitrary_fingerprint=ftpay1-{Hex}", output, StringComparison.Ordinal);
        Assert.Equal(output, SensitiveDataRedactor.Redact(output));
    }

    [Theory]
    [InlineData("unrelated")]
    [InlineData("filetransfer_security_context_installed_suffix")]
    [InlineData("filetransfer_data_path_receive_result; event=unrelated")]
    public void SecurityEvidence_UncatalogedAndAmbiguousEventsAreNotProtected(string eventName)
    {
        var input = $"event={eventName}; " + string.Join("; ", Evidence.Select(item => $"{item.Field}={item.Value}"));
        var output = SensitiveDataRedactor.Redact(input);
        // Short scope tokens retain the existing generic redactor behavior;
        // only long sensitive-looking references require explicit protection.
        Assert.All(Evidence.Take(4), item => Assert.DoesNotContain(item.Value, output, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ftpay2-", "")]
    [InlineData("ftpay1-", "0")]
    [InlineData("ftpay1-", "-raw")]
    [InlineData("ftest1-", "")]
    public void SecurityEvidence_WrongShapeAndWrongFieldAreNotProtected(string prefix, string suffix)
    {
        var value = prefix + Hex + suffix;
        Assert.DoesNotContain(value, SensitiveDataRedactor.Redact(
            $"event=filetransfer_data_path_receive_result; security_payload_fingerprint={value}"), StringComparison.Ordinal);
    }

    [Fact]
    public void SecurityEvidence_ProtectionDoesNotCrossLines()
    {
        var field = $"security_payload_fingerprint=ftpay1-{Hex}";
        var lines = SensitiveDataRedactor.Redact($"event=filetransfer_data_path_receive_result; {field}\r\n" +
            $"event=unrelated; {field}").Split("\r\n");
        Assert.Contains(field, lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(field, lines[1], StringComparison.Ordinal);
    }
}
