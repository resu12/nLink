using System;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.FileTransfer;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
using NLink.Core.SessionSecurity;

namespace NLink.App.Services;

internal enum SessionPrivilegedActionKind
{
    ChatSend,
    FileTransferStartSend,
    FileTransferAcceptIncoming,
    FileTransferDeclineIncoming,
    FileTransferCancel,
    RemoteControlRequest,
    RemoteControlRespond,
    RemoteControlStop,
    RemoteControlInputSend,
    RemoteControlSnapshotSend,
    ScreenShareDispatch,
    ClipboardSync,
    ClipboardApply,
    ApprovalGrant,
    ApprovalDeny,
}

internal readonly record struct SessionPrivilegedAction(SessionPrivilegedActionKind Kind, string Operation);

internal sealed class SessionAuthorizationCommandExecutor
{
    private readonly SessionRuntime owner;

    public SessionAuthorizationCommandExecutor(SessionRuntime owner)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public Task<T> ExecuteAsync<T>(
        SessionPrivilegedAction action,
        Func<CancellationToken, Task<T>> handler,
        T deniedValue,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return owner.TryAuthorizePrivilegedAction(action) ? handler(ct) : Task.FromResult(deniedValue);
    }

    public Task ExecuteAsync(
        SessionPrivilegedAction action,
        Func<CancellationToken, Task> handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return owner.TryAuthorizePrivilegedAction(action) ? handler(ct) : Task.CompletedTask;
    }

    public Task ExecuteRequiredAsync(
        SessionPrivilegedAction action,
        string deniedMessage,
        Func<CancellationToken, Task> handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!owner.TryAuthorizePrivilegedAction(action))
        {
            throw new InvalidOperationException(deniedMessage);
        }

        return handler(ct);
    }
}

internal sealed class SessionRuntimeApprovalActions
{
    private readonly SessionRuntime owner;

    public SessionRuntimeApprovalActions(SessionRuntime owner) => this.owner = owner;

    public Task ApproveAsync(CapabilityGrant approvedCapabilities, CancellationToken ct)
        => owner.ApproveCoreAsync(approvedCapabilities, ct);

    public Task RejectAsync(string? reason, CancellationToken ct)
        => owner.RejectCoreAsync(reason, ct);
}

internal sealed class SessionRuntimeFileTransferHost
{
    private readonly SessionRuntime owner;

    public SessionRuntimeFileTransferHost(SessionRuntime owner) => this.owner = owner;

    public Task<FileTransferTransferSnapshot?> StartSendAsync(
        FileTransferSendDescriptor descriptor,
        FileTransferReadStreamFactory openReadStreamAsync,
        CancellationToken ct)
        => owner.StartSendCoreAsync(descriptor, openReadStreamAsync, ct);

    public Task<FileTransferTransferSnapshot?> AcceptIncomingAsync(string transferId, CancellationToken ct)
        => owner.AcceptIncomingCoreAsync(transferId, ct);

    public Task<FileTransferTransferSnapshot?> DeclineIncomingAsync(string transferId, string? reason, CancellationToken ct)
        => owner.DeclineIncomingCoreAsync(transferId, reason, ct);

    public Task<FileTransferTransferSnapshot?> CancelTransferAsync(string transferId, string? reason, CancellationToken ct)
        => owner.CancelTransferCoreAsync(transferId, reason, ct);

    public void AttachTransport(ISignalingTransport nextTransport)
    {
        ArgumentNullException.ThrowIfNull(nextTransport);

        if (nextTransport is IFileTransferSignalingTransport fileTransferTransport)
        {
            owner.AttachFileTransferRuntimeTransport(fileTransferTransport);
            return;
        }

        owner.DetachFileTransferRuntimeTransport();
    }

    public void QueueDetachTransport()
    {
        if (owner.IsDisposedForFileTransferHost)
        {
            return;
        }

        owner.RunFileTransferBackgroundTask(DetachTransportAsync);
    }

    public Task DetachTransportAsync()
    {
        try
        {
            owner.DetachFileTransferRuntimeTransport();
        }
        catch (ObjectDisposedException)
        {
        }

        return Task.CompletedTask;
    }

    public Task<FileTransferReceiveDestination> OpenInboundWriteStreamAsync(FileTransferIncomingOffer offer, CancellationToken ct)
    {
        return owner.OpenInboundFileTransferDestinationAsync(offer, ct);
    }

    public void LogSnapshot(SessionFileTransferSnapshot snapshot)
    {
        owner.LogRuntimeFileTransferSnapshotCore(snapshot);
    }
}

internal sealed class SessionRuntimeRemoteControlActions
{
    private readonly SessionRuntime owner;

    public SessionRuntimeRemoteControlActions(SessionRuntime owner) => this.owner = owner;

    public Task<bool> RequestAsync(CancellationToken ct) => owner.RequestRemoteControlCoreAsync(ct);

    public Task<bool> RespondAsync(bool allow, CancellationToken ct) => owner.RespondToRemoteControlRequestCoreAsync(allow, ct);

    public Task<bool> StopAsync(string reason, CancellationToken ct) => owner.StopRemoteControlCoreAsync(reason, ct);

    public Task<bool> SendInputAsync(ControlInputMessageV1 message, CancellationToken ct) => owner.SendRemoteControlInputCoreAsync(message, ct);

    public Task<bool> SendStateSnapshotAsync(ControlStateSnapshotV1 snapshot, CancellationToken ct) => owner.SendRemoteControlStateSnapshotCoreAsync(snapshot, ct);
}

internal sealed class SessionRuntimeScreenShareActions
{
    private readonly SessionRuntime owner;

    public SessionRuntimeScreenShareActions(SessionRuntime owner) => this.owner = owner;

    public Task SendPayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => owner.SendScreenSharePayloadCoreAsync(payload, ct);

    public Task SendPayloadWithRecoveryMetadataAsync(
        ReadOnlyMemory<byte> payload,
        string? recoverySendRole,
        long recoveryBurstToken,
        CancellationToken ct)
        => owner.SendScreenSharePayloadCoreAsync(payload, recoverySendRole, recoveryBurstToken, ct);

    public Task SendVideoStreamConfigAsync(ScreenShareVideoStreamConfigV1 message, CancellationToken ct)
        => owner.SendScreenShareVideoStreamConfigCoreAsync(message, ct);

    public Task SendCursorStateAsync(string sessionId, ScreenShareCursorStateV1 message, CancellationToken ct)
        => owner.SendScreenShareCursorStateCoreAsync(sessionId, message, ct);
}
