namespace NLink.Core.ScreenShare;

public sealed record ScreenShareRecoveryReceiptV1
{
    public string Kind { get; init; } = "screenshare";

    public string Type { get; init; } = ScreenShareRecoveryReceiptCodec.ScreenShareRecoveryReceiptTypeV1;

    public string SessionId { get; init; } = string.Empty;

    public long StreamEpoch { get; init; }

    public long OwnerFrameId { get; init; }

    public long VisibleRecoveryFrameId { get; init; }

    public long VisibleHeadFrameId { get; init; }

    public string ReceiptKind { get; init; } = ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind;
}
