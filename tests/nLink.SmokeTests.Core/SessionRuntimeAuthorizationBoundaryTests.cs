using NLink.App.Services;
using NLink.Core;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class SessionRuntimeAuthorizationBoundaryTests
{
    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionAuthorizationCommandExecutor_DeniedChatSend_DoesNotRunHandler()
    {
        using var runtime = new SessionRuntime(() => new NoopSignalingTransport());
        var executor = new SessionAuthorizationCommandExecutor(runtime);
        var invoked = false;

        var result = await executor.ExecuteAsync(
            new SessionPrivilegedAction(SessionPrivilegedActionKind.ChatSend, "chat_send"),
            _ =>
            {
                invoked = true;
                return Task.FromResult(true);
            },
            deniedValue: false,
            CancellationToken.None);

        Assert.False(result);
        Assert.False(invoked);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionAuthorizationCommandExecutor_DeniedRequiredChatSend_ThrowsBeforeHandlerRuns()
    {
        using var runtime = new SessionRuntime(() => new NoopSignalingTransport());
        var executor = new SessionAuthorizationCommandExecutor(runtime);
        var invoked = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteRequiredAsync(
                new SessionPrivilegedAction(SessionPrivilegedActionKind.ChatSend, "chat_send"),
                "Chat capability is not authorized for the current session.",
                _ =>
                {
                    invoked = true;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.Equal("Chat capability is not authorized for the current session.", ex.Message);
        Assert.False(invoked);
    }

    [Trait("Category", "Smoke")]
    [Theory]
    [InlineData(nameof(SessionPrivilegedActionKind.ChatSend), "chat_send")]
    [InlineData(nameof(SessionPrivilegedActionKind.FileTransferStartSend), "file_transfer_send")]
    [InlineData(nameof(SessionPrivilegedActionKind.FileTransferAcceptIncoming), "file_transfer_accept")]
    [InlineData(nameof(SessionPrivilegedActionKind.FileTransferDeclineIncoming), "file_transfer_decline")]
    [InlineData(nameof(SessionPrivilegedActionKind.FileTransferCancel), "file_transfer_cancel")]
    [InlineData(nameof(SessionPrivilegedActionKind.FileTransferPause), "file_transfer_pause")]
    [InlineData(nameof(SessionPrivilegedActionKind.FileTransferResume), "file_transfer_resume")]
    [InlineData(nameof(SessionPrivilegedActionKind.RemoteControlRequest), "remote_control_request")]
    [InlineData(nameof(SessionPrivilegedActionKind.RemoteControlRespond), "remote_control_respond")]
    [InlineData(nameof(SessionPrivilegedActionKind.RemoteControlStop), "remote_control_stop")]
    [InlineData(nameof(SessionPrivilegedActionKind.RemoteControlInputSend), "remote_control_input_send")]
    [InlineData(nameof(SessionPrivilegedActionKind.RemoteControlSnapshotSend), "remote_control_snapshot_send")]
    [InlineData(nameof(SessionPrivilegedActionKind.ScreenShareDispatch), "screen_share_stream")]
    [InlineData(nameof(SessionPrivilegedActionKind.ClipboardSync), "clipboard_sync")]
    [InlineData(nameof(SessionPrivilegedActionKind.ClipboardApply), "clipboard_apply")]
    public void TryAuthorizePrivilegedAction_DeniesProtectedActions_BeforeApproval(
        string kindName,
        string operation)
    {
        using var runtime = new SessionRuntime(() => new NoopSignalingTransport());
        var kind = Enum.Parse<SessionPrivilegedActionKind>(kindName);

        var authorized = runtime.TryAuthorizePrivilegedAction(new SessionPrivilegedAction(kind, operation));

        Assert.False(authorized);
        Assert.Contains("authorization_", runtime.GetDiagnosticsSnapshot().LastAuthorizationDenialReason, StringComparison.Ordinal);
    }

    [Trait("Category", "Smoke")]
    [Theory]
    [InlineData(nameof(SessionPrivilegedActionKind.ApprovalGrant))]
    [InlineData(nameof(SessionPrivilegedActionKind.ApprovalDeny))]
    public void TryAuthorizePrivilegedAction_AllowsApprovalDecisionActions(
        string kindName)
    {
        using var runtime = new SessionRuntime(() => new NoopSignalingTransport());
        var kind = Enum.Parse<SessionPrivilegedActionKind>(kindName);

        var authorized = runtime.TryAuthorizePrivilegedAction(new SessionPrivilegedAction(kind, "approval"));

        Assert.True(authorized);
    }

#pragma warning disable CS0067
    private sealed class NoopSignalingTransport : ISignalingTransport
    {
        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;

        public void Dispose()
        {
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;
    }
#pragma warning restore CS0067
}
