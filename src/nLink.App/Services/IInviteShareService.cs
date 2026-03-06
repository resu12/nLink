using System.Threading;
using System.Threading.Tasks;

namespace NLink.App.Services;

public readonly record struct InviteShareResult(
    bool IsSuccess,
    string? Message = null);

public interface IInviteShareService
{
    Task<InviteShareResult> ShareInviteAsync(string inviteText, CancellationToken ct);
}
