namespace NLink.Core.FileTransfer;

public static class FileTransferAuthoritySchema
{
    public const int Current = 2;
}

public readonly record struct FileTransferAuthorityId
{
    internal FileTransferAuthorityId(string value)
        => Value = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    public string Value { get; }

    public bool IsDefault => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => IsDefault ? "(none)" : Value;
}

internal static class FileTransferAuthorityIdParser
{
    private static readonly string FallbackTailYieldPrefix =
        $"fta{FileTransferAuthoritySchema.Current}-fallback_tail_yield-";

    public static bool TryNormalizeFallbackTailYield(string? value, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (normalized.Length != FallbackTailYieldPrefix.Length + 24 ||
            !normalized.StartsWith(FallbackTailYieldPrefix, StringComparison.Ordinal))
        {
            normalized = string.Empty;
            return false;
        }

        foreach (var character in normalized.AsSpan(FallbackTailYieldPrefix.Length))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                normalized = string.Empty;
                return false;
            }
        }

        return true;
    }
}

internal sealed class FileTransferAuthorityLifecycle<TOutcome>
    where TOutcome : struct, Enum
{
    private readonly object gate = new();
    private bool dispatchObserved;
    private TOutcome? settlementOutcome;
    private string? settlementReason;
    private FileTransferAuthorityId? successorAuthorityId;

    public FileTransferAuthorityLifecycle(FileTransferAuthorityId authorityId)
    {
        if (authorityId.IsDefault)
        {
            throw new ArgumentException("Authority id must be non-default.", nameof(authorityId));
        }

        AuthorityId = authorityId;
    }

    public FileTransferAuthorityId AuthorityId { get; }

    public bool DispatchObserved
    {
        get
        {
            lock (gate)
            {
                return dispatchObserved;
            }
        }
    }

    public TOutcome? SettlementOutcome
    {
        get
        {
            lock (gate)
            {
                return settlementOutcome;
            }
        }
    }

    public string? SettlementReason
    {
        get
        {
            lock (gate)
            {
                return settlementReason;
            }
        }
    }

    public FileTransferAuthorityId? SuccessorAuthorityId
    {
        get
        {
            lock (gate)
            {
                return successorAuthorityId;
            }
        }
    }

    public bool IsSettled
    {
        get
        {
            lock (gate)
            {
                return settlementOutcome.HasValue;
            }
        }
    }

    public bool TryMarkDispatched()
    {
        lock (gate)
        {
            if (dispatchObserved || settlementOutcome.HasValue)
            {
                return false;
            }

            dispatchObserved = true;
            return true;
        }
    }

    public bool TryMarkDispatchedAndSettle(
        TOutcome outcome,
        string reason,
        FileTransferAuthorityId? successorAuthorityId = null)
    {
        lock (gate)
        {
            if (dispatchObserved || settlementOutcome.HasValue)
            {
                return false;
            }

            dispatchObserved = true;
            settlementOutcome = outcome;
            settlementReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
            this.successorAuthorityId = successorAuthorityId is { IsDefault: false }
                ? successorAuthorityId
                : null;
            return true;
        }
    }

    public bool TrySettle(
        TOutcome outcome,
        string reason,
        FileTransferAuthorityId? successorAuthorityId = null)
    {
        lock (gate)
        {
            if (settlementOutcome.HasValue)
            {
                return false;
            }

            settlementOutcome = outcome;
            settlementReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
            this.successorAuthorityId = successorAuthorityId is { IsDefault: false }
                ? successorAuthorityId
                : null;
            return true;
        }
    }
}
