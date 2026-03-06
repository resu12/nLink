using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.App.Services;

public sealed class DefaultInviteShareService : IInviteShareService
{
    public Task<InviteShareResult> ShareInviteAsync(string inviteText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(inviteText))
        {
            return Task.FromResult(new InviteShareResult(false, "Invite is empty."));
        }

        try
        {
            var subject = Uri.EscapeDataString("nLink invite");
            var body = Uri.EscapeDataString(inviteText.Trim());
            var uri = $"mailto:?subject={subject}&body={body}";
            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            });

            return Task.FromResult(new InviteShareResult(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new InviteShareResult(false, $"Share action failed: {ex.GetType().Name}."));
        }
    }
}
