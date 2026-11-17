using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;

namespace NLink.App.Services;

internal enum TunaWalletLinkStatus
{
    Unlinked,
    LinkedUnverified,
    VerifiedFunded,
    VerifiedEmpty,
    ValidationFailed,
}

internal sealed class TunaWalletLinkState
{
    public string? WalletPath { get; init; }

    public DateTimeOffset? LinkedUtc { get; init; }

    public DateTimeOffset? LastVerifiedUtc { get; init; }

    public string? WalletAddress { get; init; }

    public string? BalanceNkn { get; init; }

    public TunaWalletLinkStatus Status { get; init; } = TunaWalletLinkStatus.Unlinked;

    public string? LastFailureReason { get; init; }

    [JsonIgnore]
    public bool IsLinked => !string.IsNullOrWhiteSpace(WalletPath);

    [JsonIgnore]
    public string WalletFileName
        => string.IsNullOrWhiteSpace(WalletPath) ? "(none)" : Path.GetFileName(WalletPath);

    [JsonIgnore]
    public string BalanceCategory
    {
        get
        {
            if (!IsLinked || string.IsNullOrWhiteSpace(BalanceNkn))
            {
                return "(unknown)";
            }

            return IsPositiveBalance(BalanceNkn) ? "funded" : "empty";
        }
    }

    public static TunaWalletLinkState Unlinked { get; } = new();

    public static TunaWalletLinkState Linked(string walletPath, DateTimeOffset linkedUtc)
        => new()
        {
            WalletPath = Path.GetFullPath(walletPath),
            LinkedUtc = linkedUtc,
            Status = TunaWalletLinkStatus.LinkedUnverified,
        };

    public TunaWalletLinkState WithValidationResult(TunaWalletValidationResult result, DateTimeOffset verifiedUtc)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Success)
        {
            return new TunaWalletLinkState
            {
                WalletPath = WalletPath,
                LinkedUtc = LinkedUtc,
                Status = TunaWalletLinkStatus.ValidationFailed,
                LastFailureReason = string.IsNullOrWhiteSpace(result.Reason) ? "validation_failed" : result.Reason,
            };
        }

        var balance = string.IsNullOrWhiteSpace(result.BalanceNkn) ? "0" : result.BalanceNkn.Trim();
        return new TunaWalletLinkState
        {
            WalletPath = WalletPath,
            LinkedUtc = LinkedUtc,
            LastVerifiedUtc = verifiedUtc,
            WalletAddress = result.WalletAddress,
            BalanceNkn = balance,
            Status = IsPositiveBalance(balance) ? TunaWalletLinkStatus.VerifiedFunded : TunaWalletLinkStatus.VerifiedEmpty,
        };
    }

    public static bool IsPositiveBalance(string? balanceNkn)
    {
        if (string.IsNullOrWhiteSpace(balanceNkn))
        {
            return false;
        }

        if (decimal.TryParse(
                balanceNkn.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed > 0m;
        }

        foreach (var ch in balanceNkn)
        {
            if (ch is >= '1' and <= '9')
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class TunaWalletValidationResult
{
    private TunaWalletValidationResult(
        bool success,
        string? walletFile,
        string? walletAddress,
        string? balanceNkn,
        string? reason)
    {
        Success = success;
        WalletFile = walletFile;
        WalletAddress = walletAddress;
        BalanceNkn = balanceNkn;
        Reason = reason;
    }

    public bool Success { get; }

    public string? WalletFile { get; }

    public string? WalletAddress { get; }

    public string? BalanceNkn { get; }

    public string? Reason { get; }

    public static TunaWalletValidationResult Ok(string walletFile, string walletAddress, string balanceNkn)
        => new(true, walletFile, walletAddress, balanceNkn, null);

    public static TunaWalletValidationResult Fail(string reason, string? walletFile = null)
        => new(false, walletFile, null, null, string.IsNullOrWhiteSpace(reason) ? "validation_failed" : reason.Trim());
}
