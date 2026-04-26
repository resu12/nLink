using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core;
using NLink.Core.Logging;
using NLink.Core.Resources;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.App.Services;

public sealed partial class SessionRuntime
{
    private PendingIncomingHelpRequest? pendingIncomingHelpRequest;
    private PendingOutboundHelpRequest? pendingOutboundHelpRequest;

    public event EventHandler? IncomingHelpRequestAvailable;
    public event EventHandler? HelpRequestDecisionAvailable;

    public bool HasPendingHelpRequest => pendingIncomingHelpRequest is not null;
    public bool HasPendingOutboundHelpRequest => pendingOutboundHelpRequest is not null;
    public HelpRequestMessage? PendingHelpRequest => pendingIncomingHelpRequest?.Request;
    public HelpRequestDecisionMessage? PendingOutboundHelpRequestDecision { get; private set; }

    public async Task StartHelperListeningAsync(CancellationToken uiCt)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            ThrowIfStartInProgress();
            startInProgress = true;

            ClearGuiSmokeHelperAddressArtifact();
            await ResetCoreAsync(notifyRemoteSessionEnd: false).ConfigureAwait(false);
            var listenerGeneration = Interlocked.Increment(ref helperListenerGeneration);
            BeginConnectAttempt(SessionRuntimeRole.Helper, "helper_listener");
            TransitionTo(TransportState.TransportInitializing, "start_helper_listener");

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(uiCt);
            var nextTransport = AcquireTransportForNewSession(out var reusedCachedBridge);
            EnsureSessionSecurityTransport(nextTransport);
            sessionCts = linkedCts;
            transport = nextTransport;
            role = SessionRuntimeRole.Helper;
            helperConnectOrigin = HelperConnectOrigin.Listener;
            helperShouldReturnToListenerWaiting = true;
            hostReady = false;
            currentHelperTargetAddress = null;
            pendingJoinRequest = null;
            RefreshRemoteControlCapabilitiesFromTransport();
            PublishSessionFlowEvent(new SessionFlowEvent(
                SessionFlowEventKind.StartHelperListener,
                role,
                state,
                transportState,
                "start_helper_listener"));

            WireTransport(nextTransport);
            chatService.AttachTransport(nextTransport);
            AttachFileTransferTransport(nextTransport);
            if (nextTransport is NknSignalingTransport)
            {
                TransitionTo(TransportState.BridgeStarting, "nkn_bridge_starting");
                if (reusedCachedBridge)
                {
                    EmitSyntheticWarmBridgeLifecycle();
                }
            }
            else
            {
                TransitionTo(TransportState.Connecting, "host_start");
            }

            SetState(SessionRuntimeState.Waiting, "Waiting for help requests…");
            _ = RunHostAsync(nextTransport, linkedCts.Token);
            if (nextTransport is IHostReadySignalingTransport hostReadyTransport)
            {
                await hostReadyTransport.WaitUntilHostReadyAsync(linkedCts.Token).ConfigureAwait(false);
            }

            hostReady = true;
            SetState(SessionRuntimeState.Waiting, "Waiting for help requests…");
            PublishHelperListenerBootstrapSnapshotIfCurrent(nextTransport, linkedCts, listenerGeneration);
        }
        finally
        {
            startInProgress = false;
            lifecycleGate.Release();
        }
    }

    public async Task RequestHelpAsync(PeerAddress helperAddress, string inviteToken, CancellationToken uiCt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteToken);
        ObjectDisposedException.ThrowIf(disposed, this);

        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            if (transport is not IHelpRequestSignalingTransport helpRequestTransport)
            {
                throw new NotSupportedException("This transport does not support direct help requests.");
            }

            if (CurrentLocalPeerAddress is not PeerAddress helpeeAddress)
            {
                throw new InvalidOperationException("Helpee address is not ready yet.");
            }

            var request = new HelpRequestMessage(
                $"hr_{Guid.NewGuid():N}",
                helpeeAddress,
                helperAddress,
                inviteToken.Trim());
            pendingOutboundHelpRequest = new PendingOutboundHelpRequest(request);
            PendingOutboundHelpRequestDecision = null;
            HelpRequestDecisionAvailable?.Invoke(this, EventArgs.Empty);
            SetState(SessionRuntimeState.Waiting, "Waiting for helper approval…");
            PublishSessionFlowEvent(new SessionFlowEvent(
                SessionFlowEventKind.OutboundHelpRequestSent,
                role,
                state,
                transportState,
                "request_help"));
            try
            {
                await helpRequestTransport.SendHelpRequestAsync(request, uiCt).ConfigureAwait(false);
            }
            catch
            {
                pendingOutboundHelpRequest = null;
                PendingOutboundHelpRequestDecision = null;
                HelpRequestDecisionAvailable?.Invoke(this, EventArgs.Empty);
                SetState(SessionRuntimeState.Waiting, "Waiting for helper…");
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task AcceptIncomingHelpRequestAsync(CancellationToken uiCt)
    {
        PendingIncomingHelpRequest? pending;
        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            pending = pendingIncomingHelpRequest;
            pendingIncomingHelpRequest = null;
            IncomingHelpRequestAvailable?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (pending is null)
        {
            return;
        }

        if (transport is IHelpRequestSignalingTransport helpRequestTransport)
        {
            await helpRequestTransport.SendHelpRequestDecisionAsync(
                new HelpRequestDecisionMessage(
                    pending.Request.RequestId,
                    pending.Request.HelpeeAddress,
                    pending.Request.HelperAddress,
                    Accepted: true),
                uiCt).ConfigureAwait(false);
        }

        var validator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        var validation = validator.Validate(pending.Request.InviteToken, DateTimeOffset.UtcNow, InviteValidationMode.InspectOnly);
        if (!validation.IsSuccess || validation.Invite is null)
        {
            throw new InvalidOperationException(validation.Message ?? "Help request invite is invalid.");
        }

        await StartHelperCoreAsync(
                validation.Invite.TargetAddress,
                validation.Invite,
                pending.Request.InviteToken,
                HelperConnectOrigin.IncomingHelpRequest,
                uiCt)
            .ConfigureAwait(false);
    }

    private void PublishHelperListenerBootstrapSnapshotIfCurrent(
        ISignalingTransport nextTransport,
        CancellationTokenSource? linkedCts,
        long listenerGeneration)
    {
        if (disposed ||
            linkedCts is null ||
            linkedCts.IsCancellationRequested ||
            !hostReady ||
            !ReferenceEquals(transport, nextTransport) ||
            !ReferenceEquals(sessionCts, linkedCts) ||
            role != SessionRuntimeRole.Helper ||
            helperConnectOrigin != HelperConnectOrigin.Listener ||
            state != SessionRuntimeState.Waiting ||
            helperListenerGeneration != listenerGeneration ||
            CurrentLocalPeerAddress is not PeerAddress helperAddress)
        {
            return;
        }

        var snapshot = new HelperListenerBootstrapSnapshot(
            helperAddress,
            GetRunIdForLog(),
            listenerGeneration,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            HostReady: true);
        helperListenerBootstrapSnapshot = snapshot;
        WriteGuiSmokeHelperAddressArtifact(snapshot);
        LocalOperationalLog.Info(
            "Session",
            $"event=helper_local_peer_address_ready; address={snapshot.Address.Value}; transport={GetCurrentTransportKind()}; state={state}; run_id={snapshot.RunId}; listener_generation={snapshot.ListenerGeneration}; published_utc_ms={snapshot.PublishedUtcMs}; host_ready={(snapshot.HostReady ? 1 : 0)}");
        HelperListenerBootstrapSnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearHelperListenerBootstrapSnapshot(string reason)
    {
        var snapshot = helperListenerBootstrapSnapshot;
        helperListenerBootstrapSnapshot = null;
        if (snapshot is null)
        {
            return;
        }

        ClearGuiSmokeHelperAddressArtifact();
        LocalOperationalLog.Info(
            "Session",
            $"event=helper_local_peer_address_cleared; reason={reason}; address={snapshot.Address.Value}; run_id={snapshot.RunId}; listener_generation={snapshot.ListenerGeneration}; published_utc_ms={snapshot.PublishedUtcMs}; host_ready={(snapshot.HostReady ? 1 : 0)}");
        HelperListenerBootstrapSnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void WriteGuiSmokeHelperAddressArtifact(HelperListenerBootstrapSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_GUI_SMOKE_SCENARIOS")))
        {
            return;
        }

        try
        {
            var directory = GetGuiSmokeHelperAddressArtifactDirectory();
            Directory.CreateDirectory(directory);
            var payload = string.Create(
                CultureInfo.InvariantCulture,
                $"run_id={snapshot.RunId.Trim()};listener_generation={snapshot.ListenerGeneration};address={snapshot.Address.Value.Trim()};published_utc_ms={snapshot.PublishedUtcMs};host_ready={(snapshot.HostReady ? 1 : 0)}");
            File.WriteAllText(GetGuiSmokeHelperAddressArtifactPath(snapshot), payload);
        }
        catch
        {
            // GUI smoke artifacts must never disrupt the runtime path.
        }
    }

    private static void ClearGuiSmokeHelperAddressArtifact()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NLINK_GUI_SMOKE_SCENARIOS")))
        {
            return;
        }

        try
        {
            foreach (var artifactPath in Directory.EnumerateFiles(
                         GetGuiSmokeHelperAddressArtifactDirectory(),
                         "helper-address*.txt",
                         SearchOption.TopDirectoryOnly))
            {
                if (File.Exists(artifactPath))
                {
                    File.Delete(artifactPath);
                }
            }
        }
        catch
        {
            // GUI smoke artifacts must never disrupt the runtime path.
        }
    }

    private static string GetGuiSmokeHelperAddressArtifactDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nLink",
            "gui-smoke");
    }

    private static string GetGuiSmokeHelperAddressArtifactPath(HelperListenerBootstrapSnapshot snapshot)
    {
        var sanitizedRunId = SanitizeHelperArtifactSegment(snapshot.RunId);
        var generation = snapshot.ListenerGeneration.ToString(CultureInfo.InvariantCulture);
        return Path.Combine(
            GetGuiSmokeHelperAddressArtifactDirectory(),
            $"helper-address.{sanitizedRunId}.{generation}.txt");
    }

    private static string SanitizeHelperArtifactSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var trimmed = value.Trim();
        return string.Concat(trimmed.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
    }

    public async Task RejectIncomingHelpRequestAsync(string? reason, CancellationToken uiCt)
    {
        PendingIncomingHelpRequest? pending;
        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            pending = pendingIncomingHelpRequest;
            pendingIncomingHelpRequest = null;
            IncomingHelpRequestAvailable?.Invoke(this, EventArgs.Empty);
            SetState(SessionRuntimeState.Waiting, "Waiting for help requests…");
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (pending is null || transport is not IHelpRequestSignalingTransport helpRequestTransport)
        {
            return;
        }

        await helpRequestTransport.SendHelpRequestDecisionAsync(
            new HelpRequestDecisionMessage(
                pending.Request.RequestId,
                pending.Request.HelpeeAddress,
                pending.Request.HelperAddress,
                Accepted: false,
                Reason: reason ?? "request_rejected"),
            uiCt).ConfigureAwait(false);
    }

    private void OnIncomingHelpRequest(object? sender, IncomingHelpRequestEventArgs e)
    {
        if (!IsFromCurrentTransport(sender) || disposed || resetInProgress)
        {
            return;
        }

        pendingIncomingHelpRequest = new PendingIncomingHelpRequest(e.Request);
        SetState(SessionRuntimeState.Waiting, "Incoming help request.");
        PublishSessionFlowEvent(new SessionFlowEvent(
            SessionFlowEventKind.InboundHelpRequestReceived,
            role,
            state,
            transportState,
            "incoming_help_request"));
        IncomingHelpRequestAvailable?.Invoke(this, EventArgs.Empty);
    }

    private void OnHelpRequestDecisionReceived(object? sender, HelpRequestDecisionEventArgs e)
    {
        if (!IsFromCurrentTransport(sender) || disposed || resetInProgress)
        {
            return;
        }

        PendingOutboundHelpRequestDecision = e.Decision;
        if (pendingOutboundHelpRequest is not null &&
            string.Equals(pendingOutboundHelpRequest.Request.RequestId, e.Decision.RequestId, StringComparison.Ordinal))
        {
            if (e.Decision.Accepted)
            {
                pendingOutboundHelpRequest = null;
                SetState(SessionRuntimeState.Waiting, "Helper accepted. Finalizing secure connection…");
                PublishSessionFlowEvent(new SessionFlowEvent(
                    SessionFlowEventKind.LocalApprovalStarted,
                    role,
                    state,
                    transportState,
                    "help_request_accepted"));
            }
            else
            {
                SetState(SessionRuntimeState.Rejected, "The helper declined the request.");
                PublishSessionFlowEvent(new SessionFlowEvent(
                    SessionFlowEventKind.TransportRejected,
                    role,
                    state,
                    transportState,
                    "help_request_rejected"));
            }
        }

        HelpRequestDecisionAvailable?.Invoke(this, EventArgs.Empty);
    }

    private void ClearHelpRequestState()
    {
        pendingIncomingHelpRequest = null;
        pendingOutboundHelpRequest = null;
        PendingOutboundHelpRequestDecision = null;
    }

    private bool TryScheduleQuietHelperListenerRestart(string reason)
    {
        if (Interlocked.Exchange(ref quietHelperListenerRestartInProgress, 1) != 0)
        {
            return false;
        }

        LocalOperationalLog.Info(
            "Session",
            $"event={reason}; role=Helper; host_mode=listener; attempt={connectAttempt}; transport={GetCurrentTransportKind()}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");

        ActiveRuntimeCounters.IncWatchdogs();
        RunCountedBackgroundTask(async () =>
        {
            try
            {
                await ResetAsync(notifyRemoteSessionEnd: false).ConfigureAwait(false);
                await StartHelperListeningAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // If quiet restart fails, the next explicit failure/start path can surface UI state.
            }
            finally
            {
                Interlocked.Exchange(ref quietHelperListenerRestartInProgress, 0);
            }
        });

        return true;
    }

    internal bool TryReturnHelperListenerToWaiting(string reason)
    {
        if (disposed)
        {
            return false;
        }

        return TryScheduleQuietHelperListenerRestart(reason);
    }

    private void BeginHelperListenerWaitingRecovery(
        string reason,
        string transientText,
        TransportFailure? failure = null)
    {
        if (failure is not null)
        {
            LogTransportFailure(failure, reason);
        }

        SetTransientStatus(isVisible: true, text: transientText, canCancel: false);
        SetState(SessionRuntimeState.Waiting, "Waiting for help requests…");
        PublishSessionFlowEvent(new SessionFlowEvent(
            SessionFlowEventKind.None,
            role,
            state,
            transportState,
            reason));
        QueueDetachFileTransferTransport();
    }

    internal async Task HandleHelperApprovalTimeoutAsync()
    {
        TransportFailure failure;
        var shouldReturnToListenerWaiting = false;

        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            const string approvalTimeoutReason = "approval_timeout";
            failure = TransportFailureMapper.CreateTimeout(approvalTimeoutReason);
            shouldReturnToListenerWaiting = ShouldReturnHelperListenerToWaitingForCurrentAttempt();
            SessionTimeline.Record("Rejected", approvalTimeoutReason);
            if (shouldReturnToListenerWaiting)
            {
                BeginHelperListenerWaitingRecovery(
                    "helper_approval_timeout",
                    UserErrorMapper.HelperApprovalTimeout(),
                    failure);
            }
            else
            {
                TransitionTo(TransportState.Failed, approvalTimeoutReason);
                SetState(SessionRuntimeState.Failed, UserErrorMapper.HelperApprovalTimeout());
                LogTransportFailure(failure, "helper_approval_timeout");
                PublishSessionFlowEvent(new SessionFlowEvent(
                    SessionFlowEventKind.FailureObserved,
                    role,
                    state,
                    transportState,
                    approvalTimeoutReason));
                QueueDetachFileTransferTransport();
            }
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (shouldReturnToListenerWaiting)
        {
            TryScheduleQuietHelperListenerRestart("helper_approval_timeout_return_to_listener_waiting");
        }
    }

    internal bool IsHelperListenerRestartInProgress =>
        Interlocked.CompareExchange(ref quietHelperListenerRestartInProgress, 0, 0) != 0;

    private sealed record PendingIncomingHelpRequest(HelpRequestMessage Request);
    private sealed record PendingOutboundHelpRequest(HelpRequestMessage Request);

    private bool ShouldReturnHelperListenerToWaitingForCurrentAttempt()
    {
        return role == SessionRuntimeRole.Helper &&
               (helperShouldReturnToListenerWaiting ||
                helperConnectOrigin is HelperConnectOrigin.Listener or HelperConnectOrigin.IncomingHelpRequest);
    }
}
