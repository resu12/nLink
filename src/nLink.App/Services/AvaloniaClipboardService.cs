using System;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace NLink.App.Services;

public sealed class AvaloniaClipboardService : IClipboardService
{
    private readonly object gate = new();
    private WeakReference<TopLevel>? topLevelRef;

    public void SetTopLevel(TopLevel topLevel)
    {
        ArgumentNullException.ThrowIfNull(topLevel);

        lock (gate)
        {
            topLevelRef = new WeakReference<TopLevel>(topLevel);
        }
    }

    public async Task SetTextAsync(string text)
    {
        TopLevel? topLevel = null;
        lock (gate)
        {
            topLevelRef?.TryGetTarget(out topLevel);
        }

        if (topLevel?.Clipboard is null)
        {
            throw new InvalidOperationException("Clipboard is not available.");
        }

        await topLevel.Clipboard.SetTextAsync(text ?? string.Empty);
    }
}
