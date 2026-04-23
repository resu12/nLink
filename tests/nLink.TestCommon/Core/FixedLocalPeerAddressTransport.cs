using NLink.Core;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

internal sealed class FixedLocalPeerAddressTransport : ISignalingTransport, ILocalPeerAddressSignalingTransport, ISessionSecuritySignalingTransport
{
    public FixedLocalPeerAddressTransport(string localPeerAddress)
    {
        LocalPeerAddress = localPeerAddress;
    }

    public string LocalPeerAddress { get; }

    public SessionSecurityState CurrentSessionSecurityState => SessionSecurityState.Empty;

    public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
    public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
    public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
    public event EventHandler? Approved;
    public event EventHandler? Rejected;
    public event EventHandler? Disconnected;
    public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;

    public void Dispose()
    {
    }

    public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;
}
