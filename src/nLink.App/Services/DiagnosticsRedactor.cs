using System.Text.RegularExpressions;
using NLink.Core.Logging;

namespace NLink.App.Services;

internal static partial class DiagnosticsRedactor
{
    private const string Redacted = "[REDACTED]";

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = text;
        redacted = PemPrivateKeyBlockRegex().Replace(redacted, Redacted);
        redacted = DiagnosticsSecretKeyValueRegex().Replace(redacted, m => m.Groups["prefix"].Value + Redacted);
        redacted = WalletSeedPhraseRegex().Replace(redacted, m => m.Groups["prefix"].Value + Redacted);
        redacted = DiagnosticsPrivacyMetadataRegex().Replace(redacted, m => m.Groups["prefix"].Value + Redacted);

        // Preserve broad existing protections (chat payloads, generic tokens, etc.).
        redacted = SensitiveDataRedactor.Redact(redacted);

        // Normalize token casing for diagnostics artifacts and tests.
        redacted = GenericRedactedTokenRegex().Replace(redacted, Redacted);
        return redacted;
    }

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PemPrivateKeyBlockRegex();

    [GeneratedRegex(@"(?<prefix>\b(?:seedBase64|seedHex|walletSeed|wallet_seed|seedPhrase|mnemonic|privateKey|private_key|ed25519PrivateKey|walletMnemonic|walletSecret)\b\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\r\n,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticsSecretKeyValueRegex();

    [GeneratedRegex(@"(?<prefix>\bwallet\s+seed\b\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\r\n,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WalletSeedPhraseRegex();

    [GeneratedRegex(@"(?<prefix>\b(?:last_bridge_message_source|session_id|expected_session_id|helper_identity|helper|target|source|expected_source|peer_id|reply_to|expected_reply_to|msg_id|run_id)\b\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\r\n,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticsPrivacyMetadataRegex();

    [GeneratedRegex(@"\[(?:redacted|REDACTED)\]", RegexOptions.CultureInvariant)]
    private static partial Regex GenericRedactedTokenRegex();
}
