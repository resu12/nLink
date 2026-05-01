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
    private CancellationTokenSource? pendingOutboundHelpRequestTimeoutCts;

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

        IHelpRequestSignalingTransport helpRequestTransport;
        HelpRequestMessage request;
        await lifecycleGate.WaitAsync(uiCt).ConfigureAwait(false);
        try
        {
            if (transport is not IHelpRequestSignalingTransport currentHelpRequestTransport)
            {
                throw new NotSupportedException("This transport does not support direct help requests.");
            }

            helpRequestTransport = currentHelpRequestTransport;

            if (CurrentLocalPeerAddress is not PeerAddress helpeeAddress)
            {
                throw new InvalidOperationException("Helpee address is not ready yet.");
            }

            request = new HelpRequestMessage(
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
            StartPendingOutboundHelpRequestTimeoutLocked(request);
        }
        finally
        {
            lifecycleGate.Release();
        }

        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(uiCt);
        if (outboundHelpRequestDecisionTimeout > TimeSpan.Zero)
        {
            sendCts.CancelAfter(outboundHelpRequestDecisionTimeout + TimeSpan.FromMilliseconds(250));
        }

        try
        {
            await helpRequestTransport.SendHelpRequestAsync(request, sendCts.Token).ConfigureAwait(false);
            if (IsPendingOutboundHelpRequestTimedOut(request.RequestId))
            {
                throw new OperationCanceledException("The help request expired before transport acknowledgement.");
            }
        }
        catch
        {
            if (IsPendingOutboundHelpRequestTimedOut(request.RequestId))
            {
                throw new OperationCanceledException("The help request expired before transport acknowledgement.");
            }

            await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (pendingOutboundHelpRequest is { } pending &&
                    string.Equals(pending.Request.RequestId, request.RequestId, StringComparison.Ordinal))
                {
                    CancelPendingOutboundHelpRequestTimeoutLocked();
                    pendingOutboundHelpRequest = null;
                    PendingOutboundHelpRequestDecision = null;
                    HelpRequestDecisionAvailable?.Invoke(this, EventArgs.Empty);
                    SetState(SessionRuntimeState.Waiting, "Waiting for helper…");
                }
            }
            finally
            {
                lifecycleGate.Release();
            }

            throw;
        }
    }

    private bool IsPendingOutboundHelpRequestTimedOut(string requestId)
        => PendingOutboundHelpRequestDecision is { Accepted: false, Reason: "request_timeout" } decision &&
           string.Equals(decision.RequestId, requestId, StringComparison.Ordinal);

    private bool TryCompletePendingOutboundHelpRequestAsUnavailable(string reason, string trigger)
    {
        if (pendingOutboundHelpRequest is not { } pending)
        {
            return false;
        }

        CancelPendingOutboundHelpRequestTimeoutLocked();
        pendingOutboundHelpRequest = null;
        PendingOutboundHelpRequestDecision = new HelpRequestDecisionMessage(
            pending.Request.RequestId,
            pending.Request.HelpeeAddress,
            pending.Request.HelperAddress,
            Accepted: false,
            Reason: reason);
        SetState(SessionRuntimeState.Waiting, "Waiting for helper…");
        PublishSessionFlowEvent(new SessionFlowEvent(
            SessionFlowEventKind.None,
            role,
            state,
            transportState,
            reason));
        LocalOperationalLog.Info(
            "DirectHelpRequest",
            $"event=help_request_outbound_unavailable; request_id={pending.Request.RequestId}; reason={reason}; trigger={trigger}; helper_address={pending.Request.HelperAddress.Value}; helpee_address={pending.Request.HelpeeAddress.Value}; role={role}; state={state}; transport_state={transportState}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
        HelpRequestDecisionAvailable?.Invoke(this, EventArgs.Empty);
        return true;
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
            try
            {
                await helpRequestTransport.SendHelpRequestDecisionAsync(
                    new HelpRequestDecisionMessage(
                        pending.Request.RequestId,
                        pending.Request.HelpeeAddress,
                        pending.Request.HelperAddress,
                        Accepted: true),
                    uiCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (uiCt.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn(
                    "DirectHelpRequest",
                    $"event=help_request_accept_failed; reason=decision_send_failed; request_id={pending.Request.RequestId}; helpee={pending.Request.HelpeeAddress.Value}; helper={pending.Request.HelperAddress.Value}; exception_type={ex.GetType().Name}; role={role}; state={state}; transport_state={transportState}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");

                await MarkIncomingHelpRequestAcceptFailedAsync(
                        pending.Request.RequestId,
                        "The help request is no longer available.")
                    .ConfigureAwait(false);
                return;
            }
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

    private async Task MarkIncomingHelpRequestAcceptFailedAsync(string requestId, string statusText)
    {
        await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            if (pendingIncomingHelpRequest is not null)
            {
                return;
            }

            SetTransientStatus(isVisible: true, text: statusText, canCancel: false);
            SetState(SessionRuntimeState.Waiting, "Waiting for help requests…");
            PublishSessionFlowEvent(new SessionFlowEvent(
                SessionFlowEventKind.None,
                role,
                state,
                transportState,
                $"help_request_accept_failed:{requestId}"));
        }
        finally
        {
            lifecycleGate.Release();
        }
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

        if (pendingIncomingHelpRequest is { } pending)
        {
            var reason = string.Equals(pending.Request.RequestId, e.Request.RequestId, StringComparison.Ordinal)
                ? "duplicate_pending"
                : "already_pending";
            LocalOperationalLog.Info(
                "DirectHelpRequest",
                $"event=help_request_ignored; reason={reason}; request_id={e.Request.RequestId}; pending_request_id={pending.Request.RequestId}; helper_address={e.Request.HelperAddress.Value}; helpee_address={e.Request.HelpeeAddress.Value}; role={role}; state={state}; transport_state={transportState}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
            return;
        }

        if (role != SessionRuntimeRole.Helper ||
            helperConnectOrigin != HelperConnectOrigin.Listener ||
            state != SessionRuntimeState.Waiting)
        {
            LocalOperationalLog.Info(
                "DirectHelpRequest",
                $"event=help_request_ignored; reason=helper_not_waiting; request_id={e.Request.RequestId}; helper_address={e.Request.HelperAddress.Value}; helpee_address={e.Request.HelpeeAddress.Value}; role={role}; state={state}; transport_state={transportState}; helper_origin={helperConnectOrigin}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
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

        if (TryHandleIncomingHelpRequestCancellation(e.Decision))
        {
            return;
        }

        if (pendingOutboundHelpRequest is not { } pendingOutbound ||
            !string.Equals(pendingOutbound.Request.RequestId, e.Decision.RequestId, StringComparison.Ordinal))
        {
            LocalOperationalLog.Info(
                "DirectHelpRequest",
                $"event=help_request_decision_ignored; reason=no_matching_pending_request; request_id={e.Decision.RequestId}; accepted={e.Decision.Accepted}; helper_address={e.Decision.HelperAddress.Value}; helpee_address={e.Decision.HelpeeAddress.Value}; role={role}; state={state}; transport_state={transportState}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
            return;
        }

        CancelPendingOutboundHelpRequestTimeoutLocked();
        PendingOutboundHelpRequestDecision = e.Decision;
        pendingOutboundHelpRequest = null;
        if (e.Decision.Accepted)
        {
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
            var rejectedStatus = GetHelpRequestRejectedStatus(e.Decision.Reason);
            SetState(SessionRuntimeState.Rejected, rejectedStatus);
            PublishSessionFlowEvent(new SessionFlowEvent(
                SessionFlowEventKind.TransportRejected,
                role,
                state,
                transportState,
                NormalizeHelpRequestRejectedReason(e.Decision.Reason)));
        }

        HelpRequestDecisionAvailable?.Invoke(this, EventArgs.Empty);
    }

    private bool TryHandleIncomingHelpRequestCancellation(HelpRequestDecisionMessage decision)
    {
        if (decision.Accepted ||
            pendingIncomingHelpRequest is not { } pending ||
            !string.Equals(pending.Request.RequestId, decision.RequestId, StringComparison.Ordinal))
        {
            return false;
        }

        var reason = string.IsNullOrWhiteSpace(decision.Reason) ? "request_canceled" : decision.Reason.Trim();
        if (!IsHelpRequestCancellationReason(reason))
        {
            return false;
        }

        pendingIncomingHelpRequest = null;
        IncomingHelpRequestAvailable?.Invoke(this, EventArgs.Empty);
        SetTransientStatus(isVisible: true, text: "The help request is no longer available.", canCancel: false);
        SetState(SessionRuntimeState.Waiting, "Waiting for help requests…");
        PublishSessionFlowEvent(new SessionFlowEvent(
            SessionFlowEventKind.None,
            role,
            state,
            transportState,
            $"help_request_canceled:{reason}"));
        LocalOperationalLog.Info(
            "DirectHelpRequest",
            $"event=help_request_cancellation_received; request_id={decision.RequestId}; reason={reason}; helper_address={decision.HelperAddress.Value}; helpee_address={decision.HelpeeAddress.Value}; role={role}; state={state}; transport_state={transportState}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
        return true;
    }

    private static bool IsHelpRequestCancellationReason(string reason) =>
        string.Equals(reason, "request_canceled", StringComparison.Ordinal) ||
        string.Equals(reason, "helpee_closed", StringComparison.Ordinal) ||
        string.Equals(reason, "helper_closed", StringComparison.Ordinal) ||
        string.Equals(reason, "request_timeout", StringComparison.Ordinal);

    private static string GetHelpRequestRejectedStatus(string? reason) =>
        NormalizeHelpRequestRejectedReason(reason) switch
        {
            "helper_closed" => "The helper is no longer available.",
            "request_timeout" => "The help request expired.",
            _ => "The helper declined the request.",
        };

    private static string NormalizeHelpRequestRejectedReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? "help_request_rejected" : reason.Trim();

    private void StartPendingOutboundHelpRequestTimeoutLocked(HelpRequestMessage request)
    {
        CancelPendingOutboundHelpRequestTimeoutLocked();
        if (outboundHelpRequestDecisionTimeout <= TimeSpan.Zero)
        {
            return;
        }

        var timeoutCts = new CancellationTokenSource();
        pendingOutboundHelpRequestTimeoutCts = timeoutCts;
        LocalOperationalLog.Info(
            "DirectHelpRequest",
            $"event=help_request_outbound_timeout_scheduled; request_id={request.RequestId}; timeout_ms={outboundHelpRequestDecisionTimeout.TotalMilliseconds:0}; helper_address={request.HelperAddress.Value}; helpee_address={request.HelpeeAddress.Value}; role={role}; state={state}; transport_state={transportState}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
        RunCountedBackgroundTask(
            () => RunPendingOutboundHelpRequestTimeoutAsync(request, timeoutCts),
            countAsTransportTask: false);
    }

    private async Task RunPendingOutboundHelpRequestTimeoutAsync(HelpRequestMessage request, CancellationTokenSource timeoutCts)
    {
        var ct = timeoutCts.Token;
        try
        {
            await Task.Delay(outboundHelpRequestDecisionTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        await lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (disposed ||
                ct.IsCancellationRequested ||
                pendingOutboundHelpRequest is not { } pending ||
                !string.Equals(pending.Request.RequestId, request.RequestId, StringComparison.Ordinal))
            {
                return;
            }

            pendingOutboundHelpRequest = null;
            PendingOutboundHelpRequestDecision = new HelpRequestDecisionMessage(
                request.RequestId,
                request.HelpeeAddress,
                request.HelperAddress,
                Accepted: false,
                Reason: "request_timeout");
            SetState(SessionRuntimeState.Waiting, "Waiting for helper…");
            PublishSessionFlowEvent(new SessionFlowEvent(
                SessionFlowEventKind.None,
                role,
                state,
                transportState,
                "request_timeout"));
            LocalOperationalLog.Info(
                "DirectHelpRequest",
                $"event=help_request_outbound_timeout; request_id={request.RequestId}; timeout_ms={outboundHelpRequestDecisionTimeout.TotalMilliseconds:0}; helper_address={request.HelperAddress.Value}; helpee_address={request.HelpeeAddress.Value}; role={role}; state={state}; transport_state={transportState}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
            HelpRequestDecisionAvailable?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            if (ReferenceEquals(pendingOutboundHelpRequestTimeoutCts, timeoutCts))
            {
                pendingOutboundHelpRequestTimeoutCts = null;
            }

            timeoutCts.Dispose();
            lifecycleGate.Release();
        }
    }

    private void CancelPendingOutboundHelpRequestTimeoutLocked()
    {
        var timeoutCts = pendingOutboundHelpRequestTimeoutCts;
        pendingOutboundHelpRequestTimeoutCts = null;
        if (timeoutCts is null)
        {
            return;
        }

        try
        {
            timeoutCts.Cancel();
        }
        catch
        {
            // Best-effort timer cancellation.
        }
        finally
        {
            timeoutCts.Dispose();
        }
    }

    private void ClearHelpRequestState()
    {
        CancelPendingOutboundHelpRequestTimeoutLocked();
        pendingIncomingHelpRequest = null;
        pendingOutboundHelpRequest = null;
        PendingOutboundHelpRequestDecision = null;
    }

    private async Task TrySendPendingOutboundHelpRequestCancellationAsync(
        ISignalingTransport? oldTransport,
        string reason)
    {
        if (pendingOutboundHelpRequest is not { } pending ||
            oldTransport is not IHelpRequestSignalingTransport helpRequestTransport)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await helpRequestTransport.SendHelpRequestCancellationAsync(pending.Request, reason, cts.Token).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "DirectHelpRequest",
                $"event=help_request_cancellation_requested; request_id={pending.Request.RequestId}; reason={reason}; helper_address={pending.Request.HelperAddress.Value}; helpee_address={pending.Request.HelpeeAddress.Value}; role={role}; state={state}; transport_state={transportState}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "DirectHelpRequest",
                $"event=help_request_cancellation_failed; request_id={pending.Request.RequestId}; reason={reason}; helper_address={pending.Request.HelperAddress.Value}; helpee_address={pending.Request.HelpeeAddress.Value}; exception_type={ex.GetType().Name}; role={role}; state={state}; transport_state={transportState}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
        }
    }

    private async Task TrySendPendingIncomingHelpRequestCancellationAsync(
        ISignalingTransport? oldTransport,
        string reason)
    {
        if (pendingIncomingHelpRequest is not { } pending ||
            oldTransport is not IHelpRequestSignalingTransport helpRequestTransport)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await helpRequestTransport.SendHelpRequestDecisionAsync(
                    new HelpRequestDecisionMessage(
                        pending.Request.RequestId,
                        pending.Request.HelpeeAddress,
                        pending.Request.HelperAddress,
                        Accepted: false,
                        Reason: reason),
                    cts.Token)
                .ConfigureAwait(false);
            LocalOperationalLog.Info(
                "DirectHelpRequest",
                $"event=help_request_incoming_cancellation_requested; request_id={pending.Request.RequestId}; reason={reason}; helper_address={pending.Request.HelperAddress.Value}; helpee_address={pending.Request.HelpeeAddress.Value}; role={role}; state={state}; transport_state={transportState}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "DirectHelpRequest",
                $"event=help_request_incoming_cancellation_failed; request_id={pending.Request.RequestId}; reason={reason}; helper_address={pending.Request.HelperAddress.Value}; helpee_address={pending.Request.HelpeeAddress.Value}; exception_type={ex.GetType().Name}; role={role}; state={state}; transport_state={transportState}; run_id={GetRunIdForLog()}; session_id={GetSessionIdForLog()}; scenario={GetScenarioForLog()}");
        }
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
