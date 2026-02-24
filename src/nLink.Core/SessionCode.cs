using System;

namespace NLink.Core;

public readonly struct SessionCode : IEquatable<SessionCode>
{
    public SessionCode(string digits)
    {
        if (!IsValidDigits(digits))
        {
            throw new ArgumentException("Session code must be exactly 6 digits.", nameof(digits));
        }

        Digits = digits;
    }

    public string Digits { get; }

    public string DisplayText => Digits[..3] + " " + Digits[3..];

    public static SessionCode CreateRandom()
    {
        return new SessionCode(Random.Shared.Next(0, 1_000_000).ToString("D6"));
    }

    public static bool TryParse(string? value, out SessionCode code)
    {
        var digits = NormalizeDigits(value);
        if (digits.Length == 6)
        {
            code = new SessionCode(digits);
            return true;
        }

        code = default;
        return false;
    }

    public static string NormalizeDigits(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var buffer = new char[6];
        var count = 0;

        foreach (var ch in value)
        {
            if (!char.IsDigit(ch))
            {
                continue;
            }

            buffer[count++] = ch;
            if (count == 6)
            {
                break;
            }
        }

        return new string(buffer, 0, count);
    }

    public static string FormatPartial(string? value)
    {
        var normalized = NormalizeDigits(value);
        if (normalized.Length <= 3)
        {
            return normalized;
        }

        return normalized[..3] + " " + normalized[3..];
    }

    public bool Equals(SessionCode other)
    {
        return string.Equals(Digits, other.Digits, StringComparison.Ordinal);
    }

    public override bool Equals(object? obj)
    {
        return obj is SessionCode other && Equals(other);
    }

    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Digits);
    }

    public override string ToString()
    {
        return Digits;
    }

    public static bool operator ==(SessionCode left, SessionCode right) => left.Equals(right);

    public static bool operator !=(SessionCode left, SessionCode right) => !left.Equals(right);

    private static bool IsValidDigits(string? digits)
    {
        if (digits is null || digits.Length != 6)
        {
            return false;
        }

        foreach (var ch in digits)
        {
            if (!char.IsDigit(ch))
            {
                return false;
            }
        }

        return true;
    }
}

