using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
public sealed class FileTransferCoordinatorFingerprintTests
{
    [Fact]
    public void StateFingerprint_DefaultAndEmptyRetryHistoryHaveLegacyParity()
    {
        var state = new FileTransferCoordinatorState(
            "fingerprint-session", "fingerprint-transfer", FileTransferDirection.Outbound,
            false, FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast),
            null, null, 0, 0, 0, 0, null, Array.Empty<FileTransferLeg>());
        var emptyHistory = state with
        {
            PersistentState = state.PersistentState with
            {
                RuntimeUnlockRetryLifecycle = state.PersistentState.RuntimeUnlockRetryLifecycle with
                {
                    AttemptHistory = ImmutableArray<RuntimeUnlockRetryLifecycleAttemptSnapshot>.Empty,
                },
            },
        };
        var fingerprint = FileTransferCoordinator.CalculateSnapshotStateFingerprint(state);
        Assert.Equal(fingerprint, FileTransferCoordinator.CalculateSnapshotStateFingerprint(emptyHistory));
        Assert.Equal(LegacyStateFingerprint(state), fingerprint);
        Assert.Equal(LegacyStateFingerprint(emptyHistory), fingerprint);
        Assert.True(state.PersistentState.RuntimeUnlockRetryLifecycle.AttemptHistory.IsDefault);
        Assert.False(emptyHistory.PersistentState.RuntimeUnlockRetryLifecycle.AttemptHistory.IsDefault);
    }

    [Theory]
    [InlineData("history")]
    [InlineData("retry_generation")]
    [InlineData("admission_generation")]
    [InlineData("reservation")]
    [InlineData("receipt")]
    [InlineData("responder_reason")]
    [InlineData("responder_status")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void StateFingerprint_RetainsAuthorityHistoryAndReservationFields(string mutation)
    {
        var state = FileTransferCoordinatorTests.CreateRetryConfirmationFingerprintFixture();
        var before = FileTransferCoordinator.CalculateSnapshotStateFingerprint(state);
        var changed = MutateFingerprintInput(state, mutation);
        var after = FileTransferCoordinator.CalculateSnapshotStateFingerprint(changed);
        Assert.NotEqual(before, after);
        Assert.Equal(before, FileTransferCoordinator.CalculateSnapshotStateFingerprint(state));
        Assert.Equal(after, FileTransferCoordinator.CalculateSnapshotStateFingerprint(changed));
        Assert.Equal(LegacyStateFingerprint(state), before);
        Assert.Equal(LegacyStateFingerprint(changed), after);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState MutateFingerprintInput(FileTransferCoordinatorState state, string mutation)
    {
        var persistent = state.PersistentState;
        var retry = persistent.RuntimeUnlockRetryLifecycle;
        var fallback = persistent.FallbackTailYield;
        var handoff = persistent.RuntimeUnlockResponderPeerProbeRouteTransitionHandoff;
        return mutation switch
        {
            "history" => state with { PersistentState = persistent with
            {
                RuntimeUnlockRetryLifecycle = retry with
                {
                    AttemptHistory = (retry.AttemptHistory.IsDefault
                        ? ImmutableArray<RuntimeUnlockRetryLifecycleAttemptSnapshot>.Empty
                        : retry.AttemptHistory).Add(retry.InstalledAttempt!.Value),
                },
            } },
            "retry_generation" => state with { PersistentState = persistent with
            {
                RuntimeUnlockRetryLifecycle = retry with { LastAuthorityGeneration = retry.LastAuthorityGeneration + 1 },
            } },
            "admission_generation" => state with { LastRuntimeUnlockAdmissionGeneration = state.LastRuntimeUnlockAdmissionGeneration + 1 },
            "reservation" => state with { PersistentState = persistent with
            {
                FallbackTailYield = fallback with
                {
                    LastConsumedReservation = fallback.LastConsumedReservation!.Value with
                    {
                        ReservationGeneration = fallback.LastConsumedReservation.Value.ReservationGeneration + 1,
                    },
                },
            } },
            "receipt" => state with { PersistentState = persistent with
            {
                FallbackTailYield = fallback with
                {
                    LastConsumingInstallReceipt = fallback.LastConsumingInstallReceipt!.Value with
                    {
                        InstallGeneration = fallback.LastConsumingInstallReceipt.Value.InstallGeneration + 1,
                    },
                },
            } },
            "responder_reason" => state with { PersistentState = persistent with
            {
                RuntimeUnlockResponderPeerProbeRouteTransitionHandoff = handoff with { LastReason = "fingerprint-reason" },
            } },
            "responder_status" => state with { PersistentState = persistent with
            {
                RuntimeUnlockResponderPeerProbeRouteTransitionHandoff = handoff with
                {
                    LastStatus = RuntimeUnlockResponderPeerProbeRouteTransitionHandoffStatus.Rejected,
                },
            } },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
    }

    // Frozen pre-phase-split payload shape/options. The unchanged descriptor projection
    // and canonical serializer are shared; the new fingerprint helper is never invoked.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string LegacyStateFingerprint(FileTransferCoordinatorState state)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var operations = typeof(FileTransferCoordinator).GetMethod("CreateOperationDescriptors", flags)!
            .Invoke(null, [state, false]);
        var boundary = typeof(FileTransferCoordinator).GetMethod("CreateStateBoundarySnapshot", flags)!
            .Invoke(null, [state]);
        var responderHandoff = state.PersistentState.RuntimeUnlockResponderPeerProbeRouteTransitionHandoff;
        var retryLifecycle = state.PersistentState.RuntimeUnlockRetryLifecycle;
        var normalizedRetryLifecycle = retryLifecycle with
        {
            AttemptHistory = retryLifecycle.AttemptHistory.IsDefault
                ? ImmutableArray<RuntimeUnlockRetryLifecycleAttemptSnapshot>.Empty : retryLifecycle.AttemptHistory,
        };
        var payload = FileTransferCoordinatorTransitionEnvelope.SerializeCanonical(new
        {
            Boundary = boundary,
            state.IsTerminal,
            state.RouteSelection,
            state.CurrentLiveRouteEpoch,
            state.CurrentLeg,
            state.LegHistory,
            state.LastLiveRouteEpochId,
            state.LastTransferLegGeneration,
            state.LastRuntimeOwnerGeneration,
            state.TerminalGeneration,
            state.CurrentRuntimeOwner,
            state.RuntimeUnlockFallbackSurvivalState,
            state.CurrentRuntimeUnlockAdmission,
            state.LastRuntimeUnlockAdmissionGeneration,
            state.CurrentRouteCommit,
            state.LastRouteCommitGeneration,
            state.CurrentFallbackControlPlaneToken,
            state.LastFallbackControlPlaneTokenGeneration,
            state.CurrentFallbackCheckpointLease,
            state.LastFallbackCheckpointLeaseGeneration,
            state.CurrentFallbackControlPlanePressureToken,
            state.LastFallbackControlPlanePressureGeneration,
            state.CurrentRepairReplayDecision,
            state.LastRepairReplayGeneration,
            state.CurrentTransportEpochDecision,
            state.LastTransportEpochDecisionGeneration,
            state.CurrentRoutePublicationToken,
            state.LastRoutePublicationGeneration,
            state.CurrentRuntimeUnlockPendingPreCommitProbe,
            state.LastRuntimeUnlockPendingPreCommitProbeGeneration,
            state.CurrentDataSessionIdentity,
            state.CurrentDataSessionGeneration,
            RuntimeUnlockRetryLifecycle = normalizedRetryLifecycle,
            ResponderProbeHandoff = new
            {
                CurrentAuthorityId = responderHandoff.CurrentAuthority?.AuthorityId.ToString() ?? "(none)",
                CurrentAuthorityGeneration = responderHandoff.CurrentAuthority?.AuthorityGeneration ?? 0,
                CurrentAckFingerprint = responderHandoff.CurrentAuthority?.Observation.AckSourceFingerprint ?? "(none)",
                CurrentProbeFingerprint = responderHandoff.CurrentAuthority?.Observation.PeerProbeFrameFingerprint ?? "(none)",
                TerminalAuthorityId = responderHandoff.LastTerminalAuthority?.AuthorityId.ToString() ?? "(none)",
                TerminalAuthorityGeneration = responderHandoff.LastTerminalAuthority?.AuthorityGeneration ?? 0,
                Status = responderHandoff.LastStatus?.ToString() ?? "(none)",
                Reason = responderHandoff.LastReason ?? "(none)",
            },
            Operations = operations,
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
