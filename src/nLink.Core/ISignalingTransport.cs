using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.Core;

public interface ISignalingTransport : IDisposable
{
    event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;

    event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;

    event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;

    event EventHandler? Approved;

    event EventHandler? Rejected;

    event EventHandler? Disconnected;

    Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
}

public interface IAddressTargetSignalingTransport
{
    Task JoinByAddressAsync(string peerAddress, CancellationToken ct);
}

public interface IInviteTargetSignalingTransport
{
    Task JoinByInviteAsync(string inviteToken, ValidatedInviteV1 invite, CancellationToken ct);
}

public interface IAddressHostSignalingTransport
{
    Task HostByAddressAsync(CancellationToken ct);
}

public interface IHelpRequestSignalingTransport
{
    event EventHandler<IncomingHelpRequestEventArgs>? IncomingHelpRequest;

    event EventHandler<HelpRequestDecisionEventArgs>? HelpRequestDecisionReceived;

    Task SendHelpRequestAsync(HelpRequestMessage request, CancellationToken ct);

    Task SendHelpRequestDecisionAsync(HelpRequestDecisionMessage decision, CancellationToken ct);
}

public interface IHostReadySignalingTransport
{
    Task WaitUntilHostReadyAsync(CancellationToken ct);
}

public interface ILocalPeerAddressSignalingTransport
{
    string LocalPeerAddress { get; }
}

public interface ISessionSecuritySignalingTransport
{
    event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;

    SessionSecurityState CurrentSessionSecurityState { get; }
}

public sealed class IncomingJoinRequestEventArgs : EventArgs
{
    private readonly Func<ApprovalDecision?, CancellationToken, Task> approveAsync;
    private readonly Func<string?, CancellationToken, Task> rejectAsync;
    private int handled;

    public IncomingJoinRequestEventArgs(
        Func<CancellationToken, Task> approveAsync,
        Func<CancellationToken, Task> rejectAsync,
        ApprovalRequest? approvalRequest = null)
        : this((_, ct) => approveAsync(ct), (_, ct) => rejectAsync(ct), approvalRequest)
    {
    }

    public IncomingJoinRequestEventArgs(
        Func<ApprovalDecision?, CancellationToken, Task> approveAsync,
        Func<CancellationToken, Task> rejectAsync,
        ApprovalRequest? approvalRequest = null)
        : this(approveAsync, (_, ct) => rejectAsync(ct), approvalRequest)
    {
    }

    public IncomingJoinRequestEventArgs(
        Func<ApprovalDecision?, CancellationToken, Task> approveAsync,
        Func<string?, CancellationToken, Task> rejectAsync,
        ApprovalRequest? approvalRequest = null)
    {
        this.approveAsync = approveAsync;
        this.rejectAsync = rejectAsync;
        ApprovalRequest = approvalRequest;
    }

    public bool IsHandled => handled != 0;
    public ApprovalRequest? ApprovalRequest { get; }
    public bool RequiresExplicitApprovalDecision => ApprovalRequest is not null;

    public Task ApproveAsync(CancellationToken ct = default)
    {
        if (RequiresExplicitApprovalDecision)
        {
            throw new InvalidOperationException("Explicit approval decision is required for security-scoped join approval.");
        }

        return ApproveAsync(decision: null, ct);
    }

    public Task ApproveAsync(ApprovalDecision? decision, CancellationToken ct = default)
    {
        if (RequiresExplicitApprovalDecision && decision is null)
        {
            throw new InvalidOperationException("Explicit approval decision is required for security-scoped join approval.");
        }

        if (Interlocked.Exchange(ref handled, 1) != 0)
        {
            return Task.CompletedTask;
        }

        return approveAsync(decision, ct);
    }

    public Task RejectAsync(CancellationToken ct = default)
    {
        return RejectWithReasonAsync(reason: null, ct);
    }

    public Task RejectWithReasonAsync(string? reason, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref handled, 1) != 0)
        {
            return Task.CompletedTask;
        }

        return rejectAsync(reason, ct);
    }
}

public sealed record HelpRequestMessage(
    string RequestId,
    PeerAddress HelpeeAddress,
    PeerAddress HelperAddress,
    string InviteToken);

public sealed record HelpRequestDecisionMessage(
    string RequestId,
    PeerAddress HelpeeAddress,
    PeerAddress HelperAddress,
    bool Accepted,
    string? Reason = null);

public sealed class IncomingHelpRequestEventArgs : EventArgs
{
    public IncomingHelpRequestEventArgs(HelpRequestMessage request)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
    }

    public HelpRequestMessage Request { get; }
}

public sealed class HelpRequestDecisionEventArgs : EventArgs
{
    public HelpRequestDecisionEventArgs(HelpRequestDecisionMessage decision)
    {
        Decision = decision ?? throw new ArgumentNullException(nameof(decision));
    }

    public HelpRequestDecisionMessage Decision { get; }
}

public sealed class TransportSessionKeyReadyEventArgs : EventArgs
{
    public TransportSessionKeyReadyEventArgs(byte[] sharedKey)
    {
        SharedKey = sharedKey ?? throw new ArgumentNullException(nameof(sharedKey));
    }

    public byte[] SharedKey { get; }
}

public sealed class TransportChatMessageEventArgs : EventArgs
{
    public TransportChatMessageEventArgs(byte[] payload)
    {
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    public byte[] Payload { get; }
}

public sealed class TransportSessionSecurityStateChangedEventArgs : EventArgs
{
    public TransportSessionSecurityStateChangedEventArgs(SessionSecurityState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public SessionSecurityState State { get; }
}
