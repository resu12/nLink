using System;
using NLink.Core.FileTransfer;

namespace NLink.App.ViewModels;

// Presentation only: no estimated bytes and no feedback into transfer state.
internal sealed class FileTransferProgressPresentation
{
    private const double DurationMs = 200d;
    private (string Session, string Transfer, FileTransferDirection Direction, long Size)? scope;
    private long fileSizeBytes;
    private long targetBytes;
    private double displayedBytes;
    private double animationFrom;
    private long animationStartedMs;

    public long DisplayedBytes => (long)Math.Clamp(displayedBytes, 0d, targetBytes);
    public double Fraction => fileSizeBytes <= 0 ? 0d : (double)DisplayedBytes / fileSizeBytes;
    public string Text => FileTransferPanelItemViewModel.FormatProgressText(DisplayedBytes, fileSizeBytes);
    public bool IsAnimating => displayedBytes < targetBytes;

    public void SetTarget(FileTransferPanelItemViewModel? item, long nowMs, bool animate)
    {
        if (item is null)
        {
            scope = null;
            fileSizeBytes = targetBytes = 0;
            displayedBytes = animationFrom = 0d;
            return;
        }

        var nextScope = (item.SessionId, item.TransferId, item.Direction, item.FileSizeBytes);
        var nextBytes = Math.Clamp(item.ProgressBytes, 0L, Math.Max(0L, item.FileSizeBytes));
        if (scope != nextScope)
        {
            scope = nextScope;
            fileSizeBytes = Math.Max(0L, item.FileSizeBytes);
            displayedBytes = targetBytes = nextBytes;
            return;
        }

        Advance(nowMs);
        // Route handoffs can temporarily report a smaller frontier. Preserve only
        // bytes already confirmed for this exact transfer, never a send estimate.
        nextBytes = Math.Max(targetBytes, nextBytes);
        if (!animate || !item.AnimateProgress)
        {
            displayedBytes = targetBytes = nextBytes;
        }
        else if (nextBytes != targetBytes)
        {
            animationFrom = displayedBytes;
            targetBytes = nextBytes;
            animationStartedMs = nowMs;
        }
    }

    public void Advance(long nowMs)
    {
        if (!IsAnimating)
        {
            return;
        }

        var elapsed = Math.Clamp((nowMs - animationStartedMs) / DurationMs, 0d, 1d);
        displayedBytes = animationFrom + (targetBytes - animationFrom) * elapsed;
    }
}
