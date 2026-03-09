using System.Text.RegularExpressions;
using System.Text;

namespace NLink.Core.Logging;

public static partial class SensitiveDataRedactor
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "payloadBase64",
        "sharedKey",
        "seedBase64",
        "seed",
        "privateKey",
        "private_key",
        "secret",
        "identifier",
        "key_path",
        "keyPath",
    };

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

    public static string RedactValueForKey(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        return SensitiveKeys.Contains(key) ? "[redacted]" : value!;
    }

    public static string FormatStructuredFields(string separator, params (string Key, string? Value)[] fields)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(separator);
            }

            builder.Append(fields[i].Key)
                .Append('=')
                .Append(RedactValueForKey(fields[i].Key, fields[i].Value));
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\b(?:payloadBase64|sharedKey|seedBase64|seed|privateKey|private_key|secret|identifier|key_path|keyPath)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s|,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyValueRegex();

    [GeneratedRegex(@"\b(?:chat|message|text)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\r\n|]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatPlaintextKeyValueRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[A-Fa-f0-9]{32,}|[A-Za-z0-9+/_=-]{40,})(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex LongSecretTokenRegex();
}
