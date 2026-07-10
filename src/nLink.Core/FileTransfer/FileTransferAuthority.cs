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

internal sealed class FileTransferAuthorityLifecycle<TOutcome>
    where TOutcome : struct, Enum
{
    public FileTransferAuthorityLifecycle(FileTransferAuthorityId authorityId)
    {
        if (authorityId.IsDefault)
        {
            throw new ArgumentException("Authority id must be non-default.", nameof(authorityId));
        }

        AuthorityId = authorityId;
    }

    public FileTransferAuthorityId AuthorityId { get; }

    public bool DispatchObserved { get; private set; }

    public TOutcome? SettlementOutcome { get; private set; }

    public string? SettlementReason { get; private set; }

    public FileTransferAuthorityId? SuccessorAuthorityId { get; private set; }

    public bool IsSettled => SettlementOutcome.HasValue;

    public bool TryMarkDispatched()
    {
        if (DispatchObserved || IsSettled)
        {
            return false;
        }

        DispatchObserved = true;
        return true;
    }

    public bool TrySettle(
        TOutcome outcome,
        string reason,
        FileTransferAuthorityId? successorAuthorityId = null)
    {
        if (IsSettled)
        {
            return false;
        }

        SettlementOutcome = outcome;
        SettlementReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
        SuccessorAuthorityId = successorAuthorityId is { IsDefault: false }
            ? successorAuthorityId
            : null;
        return true;
    }
}
