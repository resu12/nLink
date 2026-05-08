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
        "inviteToken",
        "invite_token",
        "rootKey",
        "root_key",
        "sessionRootKey",
        "session_root_key",
        "fallbackCode",
        "fallback_code",
        "verificationFallbackCode",
        "verification_fallback_code",
    };

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var protectedEventValues = new List<string>();
        var redacted = ProtectStructuredEventValues(text, protectedEventValues);
        redacted = SecretKeyValueRegex().Replace(redacted, "[redacted]");
        redacted = ChatPlaintextKeyValueRegex().Replace(redacted, "[redacted]");
        var protectedStructuredKeys = new List<string>();
        redacted = ProtectStructuredKeys(redacted, protectedStructuredKeys);
        redacted = LongSecretTokenRegex().Replace(redacted, "[redacted]");
        redacted = RestoreStructuredKeys(redacted, protectedStructuredKeys);
        redacted = RestoreStructuredEventValues(redacted, protectedEventValues);
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

    [GeneratedRegex(@"\b(?:payloadBase64|sharedKey|seedBase64|seed|privateKey|private_key|secret|identifier|key_path|keyPath|inviteToken|invite_token|rootKey|root_key|sessionRootKey|session_root_key|fallbackCode|fallback_code|verificationFallbackCode|verification_fallback_code)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s|,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyValueRegex();

    [GeneratedRegex(@"\b(?:chat|message|text)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\r\n|]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatPlaintextKeyValueRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[A-Fa-f0-9]{32,}|(?=[A-Za-z0-9+/_-]{40,}={0,2}(?![A-Za-z0-9]))(?=[A-Za-z0-9+/_-]*[0-9+/-])[A-Za-z0-9+/_-]{40,}={0,2})(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex LongSecretTokenRegex();

    [GeneratedRegex(@"\bevent=(?<value>[A-Za-z0-9._/-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex EventKeyValueRegex();

    [GeneratedRegex(@"(?<prefix>(?:^|[\s|,;]))(?<key>[A-Za-z_][A-Za-z0-9._/-]{1,127})(?<separator>\s*[:=])", RegexOptions.CultureInvariant)]
    private static partial Regex StructuredKeyRegex();

    private static string ProtectStructuredEventValues(string text, List<string> protectedEventValues)
    {
        return EventKeyValueRegex().Replace(text, match =>
        {
            var placeholder = $"EVP{protectedEventValues.Count}";
            protectedEventValues.Add(match.Groups["value"].Value);
            return $"event={placeholder}";
        });
    }

    private static string RestoreStructuredEventValues(string text, List<string> protectedEventValues)
    {
        if (protectedEventValues.Count == 0)
        {
            return text;
        }

        var restored = text;
        for (var i = 0; i < protectedEventValues.Count; i++)
        {
            restored = restored.Replace($"event=EVP{i}", $"event={protectedEventValues[i]}", StringComparison.Ordinal);
        }

        return restored;
    }

    private static string ProtectStructuredKeys(string text, List<string> protectedStructuredKeys)
    {
        return StructuredKeyRegex().Replace(text, match =>
        {
            var placeholder = $"SKP{protectedStructuredKeys.Count}";
            protectedStructuredKeys.Add(match.Groups["key"].Value);
            return $"{match.Groups["prefix"].Value}{placeholder}{match.Groups["separator"].Value}";
        });
    }

    private static string RestoreStructuredKeys(string text, List<string> protectedStructuredKeys)
    {
        if (protectedStructuredKeys.Count == 0)
        {
            return text;
        }

        var restored = text;
        for (var i = protectedStructuredKeys.Count - 1; i >= 0; i--)
        {
            restored = restored.Replace($"SKP{i}", protectedStructuredKeys[i], StringComparison.Ordinal);
        }

        return restored;
    }
}
