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
