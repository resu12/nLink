namespace NLink.Infra.Nkn;

internal sealed class NknEnvelopeRouter
{
    private readonly NknLifecycleChannel lifecycleChannel;
    private readonly NknSecureControlChannel controlChannel;
    private readonly NknScreenShareChannel screenShareChannel;
    private readonly NknFileTransferChannel fileTransferChannel;

    public NknEnvelopeRouter(
        NknLifecycleChannel lifecycleChannel,
        NknSecureControlChannel controlChannel,
        NknScreenShareChannel screenShareChannel,
        NknFileTransferChannel fileTransferChannel)
    {
        this.lifecycleChannel = lifecycleChannel;
        this.controlChannel = controlChannel;
        this.screenShareChannel = screenShareChannel;
        this.fileTransferChannel = fileTransferChannel;
    }

    public void RouteInboundMessage(string source, NknBridgeChannel channel, Envelope env)
    {
        switch (env.Type)
        {
            case MsgType.JoinRequest:
            case MsgType.Approve:
            case MsgType.Reject:
            case MsgType.Chat:
            case MsgType.Ack:
            case MsgType.SessionEnd:
            case MsgType.SessionHandshakeStart:
            case MsgType.SessionHandshakeChallenge:
            case MsgType.SessionHandshakeResponse:
            case MsgType.SessionHandshakeResult:
            case MsgType.HelpRequest:
            case MsgType.HelpRequestDecision:
                lifecycleChannel.Handle(source, env);
                break;
            case MsgType.ControlRequest:
            case MsgType.ControlResponse:
            case MsgType.ControlStart:
            case MsgType.ControlStop:
            case MsgType.ControlInput:
            case MsgType.ControlAck:
            case MsgType.ControlStateSnapshot:
            case MsgType.ControlDisplayInfo:
                controlChannel.Handle(source, env);
                break;
            case MsgType.ScreenShareFrame:
            case MsgType.ScreenShareStop:
                screenShareChannel.Handle(source, env);
                break;
            case MsgType.FileTransferOffer:
            case MsgType.FileTransferAccept:
            case MsgType.FileTransferDecline:
            case MsgType.FileTransferStart:
            case MsgType.FileTransferChunk:
            case MsgType.FileTransferWindowUpdate:
            case MsgType.FileTransferMissingRange:
            case MsgType.FileTransferPressureState:
            case MsgType.FileTransferCancel:
            case MsgType.FileTransferError:
            case MsgType.FileTransferComplete:
            case MsgType.FileTransferSessionOpen:
            case MsgType.FileTransferDataFrame:
                fileTransferChannel.Handle(source, channel, env);
                break;
            default:
                lifecycleChannel.HandleUnexpected(env);
                break;
        }
    }
}

internal sealed class NknLifecycleChannel
{
    private readonly NknSignalingTransport owner;

    public NknLifecycleChannel(NknSignalingTransport owner) => this.owner = owner;

    public void Handle(string source, Envelope env) => owner.RouteLifecycleEnvelope(source, env);

    public void HandleUnexpected(Envelope env) => owner.HandleUnexpectedEnvelopeType(env);
}

internal sealed class NknSecureControlChannel
{
    private readonly NknSignalingTransport owner;

    public NknSecureControlChannel(NknSignalingTransport owner) => this.owner = owner;

    public void Handle(string source, Envelope env) => owner.RouteControlEnvelope(source, env);
}

internal sealed class NknScreenShareChannel
{
    private readonly NknSignalingTransport owner;

    public NknScreenShareChannel(NknSignalingTransport owner) => this.owner = owner;

    public void Handle(string source, Envelope env) => owner.RouteScreenShareEnvelope(source, env);
}

internal sealed class NknFileTransferChannel
{
    private readonly NknSignalingTransport owner;

    public NknFileTransferChannel(NknSignalingTransport owner) => this.owner = owner;

    public void Handle(string source, NknBridgeChannel channel, Envelope env) => owner.RouteFileTransferEnvelope(source, channel, env);
}
