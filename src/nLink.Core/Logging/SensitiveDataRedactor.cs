using System.Text.RegularExpressions;

namespace NLink.Core.Logging;

public static partial class SensitiveDataRedactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = text;
        redacted = SecretKeyValueRegex().Replace(redacted, "[redacted]");
        redacted = ChatPlaintextKeyValueRegex().Replace(redacted, "[redacted]");
        redacted = LongSecretTokenRegex().Replace(redacted, "[redacted]");
        return redacted;
    }

    [GeneratedRegex(@"\b(?:payloadBase64|sharedKey|seedBase64|seed|privateKey|secret|identifier)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s|,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyValueRegex();

    [GeneratedRegex(@"\b(?:chat|message|text)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\r\n|]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatPlaintextKeyValueRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[A-Fa-f0-9]{32,}|[A-Za-z0-9+/_=-]{40,})(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex LongSecretTokenRegex();
}
