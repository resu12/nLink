namespace NLink.Core.FileTransfer;

internal sealed record RuntimeUnlockPostFallbackDialFailureObservation(
    RuntimeUnlockPostFallbackPreCommitProbeFailureSourceScope SourceScope,
    long OfferGeneration,
    string OfferNonce,
    long PayerDecisionId,
    RuntimeUnlockRetryDispatchInstallReceipt? InstallReceipt = null,
    RuntimeUnlockPostFallbackPairedRetryOfferBindingReceipt? BindingReceipt = null,
    RuntimeUnlockTransactionSnapshot? FailedTransaction = null)
{
    public bool IsDefault => SourceScope.IsDefault || OfferGeneration <= 0 ||
        string.IsNullOrWhiteSpace(OfferNonce) || PayerDecisionId <= 0 ||
        (SourceScope.PublicationAuthority.Direction == FileTransferDirection.Outbound
            ? InstallReceipt is not { IsDefault: false } || BindingReceipt is not null || FailedTransaction is null
            : BindingReceipt is not { IsDefault: false } || InstallReceipt is not null || FailedTransaction is not null);
}

internal sealed class RuntimeUnlockPostFallbackDialFailureObservedEventArgs(
    RuntimeUnlockPostFallbackDialFailureObservation observation) : EventArgs
{
    public RuntimeUnlockPostFallbackDialFailureObservation Observation { get; } = observation;
    public bool Accepted { get; set; }
}
