using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData("current", true)]
    [InlineData("missing_binding", false)]
    [InlineData("provider_replaced", false)]
    [InlineData("provider_aliases_local", false)]
    [InlineData("local_replaced", false)]
    [InlineData("authority_replaced", false)]
    [InlineData("hold_request_replaced", false)]
    [Trait("FileTransferInfraSuite", "InitialActivationHoldBinding")]
    public void RuntimeUnlockInitialActivationHoldBinding_RequiresExactCoordinatorBinding(
        string change, bool expected)
    {
        var issued = FileTransferCoordinatorTests.CreateProviderLocalInitialActivationHoldForTests();
        Assert.Equal(RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Held, issued.Decision.Status);
        var decision = issued.Decision;
        var operation = decision.ReceiveRecoveryOperation!.Value;
        Assert.Equal(2, decision.Request.ExpectedScope.DataSessionGeneration);
        Assert.Equal(1, operation.Authority.Observation.DataSessionGeneration);
        if (change == "missing_binding") decision = decision with { DataSessionBinding = null };
        if (change is "provider_replaced" or "provider_aliases_local")
        {
            var generation = change == "provider_replaced" ? 3 : 1;
            decision = decision with { Request = decision.Request with
            { ExpectedScope = decision.Request.ExpectedScope with
            {
                DataSessionIdentity = $"{decision.Request.TransferId}:{generation}",
                DataSessionGeneration = generation,
            } } };
        }
        if (change == "local_replaced") decision = decision with { ReceiveRecoveryOperation = operation with
        { Authority = operation.Authority with { Observation = operation.Authority.Observation with
        { DataSessionIdentity = "replacement", DataSessionGeneration = 2 } } } };
        if (change == "authority_replaced") decision = decision with { ReceiveRecoveryOperation = operation with
        { Authority = operation.Authority with { OperationGeneration = operation.Authority.OperationGeneration + 1 } } };
        if (change == "hold_request_replaced") decision = decision with
        { Request = decision.Request with { OfferGeneration = decision.Request.OfferGeneration + 1 } };

        using var transport = new NknSignalingTransport(
            new FakeNknClient("hold-binding.aaaaaaaaaaaaaaaa"), NknTransportOptions.Load(),
            new NknIdentity("hold-binding-id", "hold-binding.aaaaaaaaaaaaaaaa"),
            NknTunaAccelerationOptions.Disabled,
            new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0));
        var method = typeof(NknSignalingTransport).GetMethod(
            "RuntimeUnlockInitialActivationReceiveRecoveryHoldDecisionMatchesRequest",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(expected, Assert.IsType<bool>(method.Invoke(transport, [decision, null])));
        Assert.Null(FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(issued.State).InstalledAttempt);
        Assert.Null(issued.State.CurrentRuntimeUnlockPendingPreCommitProbe);
    }
}
