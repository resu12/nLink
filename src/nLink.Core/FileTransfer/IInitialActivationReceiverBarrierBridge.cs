namespace NLink.Core.FileTransfer;

internal interface IInitialActivationReceiverBarrierBridge
{
    event EventHandler<InitialActivationReceiverBarrierBindingEventArgs>? ReceiverBarrierBindingRequested;
    event EventHandler<InitialActivationReceiverBarrierFailureEventArgs>? ReceiverBarrierFailureObserved;
    void ObserveReceiverDataSessionAttached(IFileTransferDataSession dataSession);
    bool IsReceiverBarrierFailureCurrent(InitialActivationReceiverBarrierFailureEventArgs observation);
    InitialActivationReceiverBarrierCloseReceipt ApplyReceiverBarrierClose(InitialActivationReceiverBarrierCloseCommand command);
}

internal sealed class InitialActivationReceiverBarrierBindingEventArgs(
    IFileTransferDataSession dataSession, long pauseGeneration,
    InitialActivationReceiverBarrierNegotiation negotiation,
    InitialActivationReceiverBarrierAuthority? authority) : EventArgs
{
    public IFileTransferDataSession DataSession { get; } = dataSession;
    public long PauseGeneration { get; } = pauseGeneration;
    public InitialActivationReceiverBarrierNegotiation Negotiation { get; } = negotiation;
    public InitialActivationReceiverBarrierAuthority? Authority { get; } = authority;
    public InitialActivationReceiverBarrierDecision? Decision { get; set; }
}

internal sealed class InitialActivationReceiverBarrierFailureEventArgs(
    InitialActivationReceiverBarrierAuthority authority,
    InitialActivationReceiverBarrierNegotiation negotiation,
    InitialActivationReceiverBarrierFailureOrigin origin, string reason,
    InitialActivationReceiverBarrierNegotiation? retainedNegotiation = null) : EventArgs
{
    public InitialActivationReceiverBarrierAuthority Authority { get; } = authority;
    public InitialActivationReceiverBarrierNegotiation Negotiation { get; } = negotiation;
    public InitialActivationReceiverBarrierNegotiation RetainedNegotiation { get; } = retainedNegotiation ?? negotiation;
    public InitialActivationReceiverBarrierFailureOrigin Origin { get; } = origin;
    public string Reason { get; } = reason;
    public InitialActivationReceiverBarrierDecision? Decision { get; set; }
}
