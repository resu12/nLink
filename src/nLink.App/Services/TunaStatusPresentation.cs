using System;

namespace NLink.App.Services;

internal sealed record TunaStatusPresentation(
    string Text,
    bool IsConnecting,
    bool IsLocalPayer = false);

internal static class TunaStatusPresentationMapper
{
    public static TunaStatusPresentation FromState(
        bool transportActive,
        string? transportReason,
        string? runtimeStatus,
        bool sessionUnlockOn)
    {
        if (transportActive)
        {
            var activeToken = Normalize(transportReason);
            if (activeToken == "paid_listener_active")
            {
                return new TunaStatusPresentation(
                    "Tuna is active. This computer is paying as the Tuna listener.",
                    IsConnecting: false,
                    IsLocalPayer: true);
            }

            if (activeToken == "paid_listener_active_file_regular_nkn_fallback")
            {
                return new TunaStatusPresentation(
                    "Tuna is active for the session, but file transfer is using regular NKN. This computer is paying as the Tuna listener.",
                    IsConnecting: false,
                    IsLocalPayer: true);
            }

            if (activeToken == "free_dialer_active")
            {
                return new TunaStatusPresentation(
                    "Tuna is active and the other computer is paying.",
                    IsConnecting: false);
            }

            if (activeToken == "free_dialer_active_file_regular_nkn_fallback")
            {
                return new TunaStatusPresentation(
                    "Tuna is active for the session, but file transfer is using regular NKN.",
                    IsConnecting: false);
            }

            return new TunaStatusPresentation(
                "Tuna acceleration is active.",
                IsConnecting: false,
                IsLocalPayer: IsLocalPayerRuntimeStatus(runtimeStatus));
        }

        var token = ResolveToken(transportReason, runtimeStatus, sessionUnlockOn);
        var localPayer = sessionUnlockOn && IsLocalPayerRuntimeStatus(runtimeStatus);
        return token switch
        {
            "locked" => new TunaStatusPresentation("Tuna wallet is locked. Regular NKN is being used.", false),
            "waiting_for_approved_session" => new TunaStatusPresentation("Tuna is unlocked and waiting for an approved session.", false),
            "checking_payer_priority" => new TunaStatusPresentation("Choosing which side will pay for Tuna.", true),
            "selected_payer_starting_listener" => new TunaStatusPresentation("This computer was selected to pay for Tuna. Starting listener.", true, true),
            "listener_starting" => new TunaStatusPresentation("Starting Tuna listener. Regular NKN stays connected until ready.", true, true),
            "listener_paths_starting" => new TunaStatusPresentation("Starting Tuna relay paths. Regular NKN stays connected until ready.", true, true),
            "listener_retrying" => new TunaStatusPresentation("Retrying Tuna listener startup. Regular NKN stays connected.", true, true),
            "listener_start_timeout" => new TunaStatusPresentation("Tuna listener startup timed out. Retrying if possible; regular NKN stays connected.", true, true),
            "provider_paths_retrying" => new TunaStatusPresentation("Looking for enough Tuna relay paths. Regular NKN stays connected while Tuna retries.", true, true),
            "provider_paths_ready" => new TunaStatusPresentation("Tuna relay paths are ready. Waiting for peer connection.", true, true),
            "provider_paths_degraded" => new TunaStatusPresentation("Tuna relay paths are degraded. Regular NKN is being used while Tuna retries.", true, true),
            "listener_ready" or "waiting_for_peer_dial" or "peer_connected"
                => new TunaStatusPresentation("Waiting for the other side to connect to Tuna.", true, true),
            "waiting_for_answer" or "dialer_starting" or "dialer_ready" or "negotiated"
                => new TunaStatusPresentation("Negotiating Tuna acceleration.", true, localPayer),
            "free_dialer_active"
                => new TunaStatusPresentation("Tuna is active and the other computer is paying.", false),
            "paid_listener_active"
                => new TunaStatusPresentation("Tuna is active. This computer is paying as the Tuna listener.", false, true),
            "free_dialer_active_file_regular_nkn_fallback"
                => new TunaStatusPresentation("Tuna is active for the session, but file transfer is using regular NKN.", false),
            "paid_listener_active_file_regular_nkn_fallback"
                => new TunaStatusPresentation("Tuna is active for the session, but file transfer is using regular NKN. This computer is paying as the Tuna listener.", false, true),
            "suppressed_by_peer_payer" or "payer_yield_to_helpee" or "listener_stopped_payer_switch_to_dialer" or "listener_stopped_payer_yield_to_helpee"
                => new TunaStatusPresentation("The other computer was selected to pay for Tuna. This computer will dial for free.", true),
            "renegotiating_after_user_unlock"
                => new TunaStatusPresentation("Trying Tuna again for this session.", true, localPayer),
            _ when token.StartsWith("negotiation_scheduled_", StringComparison.Ordinal)
                => new TunaStatusPresentation("Negotiating Tuna acceleration.", true, localPayer),
            "off" or "inactive" or "transport_without_acceleration" or "transport_unwired"
                => new TunaStatusPresentation("Tuna acceleration is off. Regular NKN is being used.", false),
            "wallet_empty_dialer_only"
                => new TunaStatusPresentation("Tuna wallet is empty. This computer will not pay; regular NKN is being used unless the peer pays.", false),
            "wallet_unverified_dialer_only"
                => new TunaStatusPresentation("Tuna wallet is not verified. Regular NKN is being used.", false),
            "wallet_validation_failed_dialer_only"
                => new TunaStatusPresentation("Tuna wallet validation failed. Regular NKN is being used.", false),
            "wallet_missing_dialer_only"
                => new TunaStatusPresentation("No Tuna wallet is linked. Regular NKN is being used.", false),
            "sidecar_unavailable" or "listener_sidecar_unavailable"
                => new TunaStatusPresentation("Tuna sidecar is unavailable. Regular NKN is being used.", false),
            "provider_paths_wait_timeout"
                => new TunaStatusPresentation("Tuna relay path discovery timed out. Regular NKN is being used.", false),
            "cap_handoff_pending"
                => new TunaStatusPresentation("Tuna cap reached. Continuing on regular NKN.", false),
            "cap_reached" or "byte_cap_reached" or "duration_cap_reached"
                => new TunaStatusPresentation("Tuna cap reached. Continuing on regular NKN.", false),
            "switching_to_regular_nkn"
                => new TunaStatusPresentation("Switching Tuna off. Regular NKN will continue the session.", false),
            "user_stopped_tuna" or "header_switch_off" or "remote_header_switch_off"
                => new TunaStatusPresentation("Tuna was turned off for this session. Regular NKN is being used.", false),
            _ when IsFallbackOrFailure(token)
                => new TunaStatusPresentation("Tuna is unavailable. Regular NKN is being used.", false),
            _ => new TunaStatusPresentation("Tuna acceleration inactive. Regular NKN is being used.", false),
        };
    }

    public static TunaStatusPresentation FromRuntimeStatus(string? runtimeStatus)
        => FromState(
            transportActive: false,
            transportReason: null,
            runtimeStatus: runtimeStatus,
            sessionUnlockOn: IsRuntimeUnlockOn(runtimeStatus));

    private static string ResolveToken(string? transportReason, string? runtimeStatus, bool sessionUnlockOn)
    {
        var runtime = Normalize(runtimeStatus);
        var reason = Normalize(transportReason);

        if (!sessionUnlockOn && IsLockedLike(runtime))
        {
            return runtime;
        }

        if (IsMeaningfulTransportReason(reason))
        {
            return reason;
        }

        if (IsMeaningfulRuntimeStatus(runtime))
        {
            return runtime;
        }

        return reason;
    }

    private static bool IsRuntimeUnlockOn(string? runtimeStatus)
    {
        var normalized = Normalize(runtimeStatus);
        return normalized is "waiting_for_approved_session" or
            "checking_payer_priority" or
            "listener_starting" or
            "listener_paths_starting" or
            "listener_retrying" or
            "listener_start_timeout" or
            "provider_paths_retrying" or
            "provider_paths_ready" or
            "provider_paths_degraded" or
            "listener_ready" or
            "waiting_for_peer_dial" or
            "peer_connected" or
            "selected_payer_starting_listener" or
            "waiting_for_answer" or
            "renegotiating_after_user_unlock" or
            "dialer_starting" or
            "dialer_ready" or
            "negotiated" or
            "active";
    }

    private static bool IsLocalPayerRuntimeStatus(string? runtimeStatus)
    {
        var normalized = Normalize(runtimeStatus);
        return normalized is "listener_starting" or
            "listener_paths_starting" or
            "listener_retrying" or
            "listener_start_timeout" or
            "selected_payer_starting_listener" or
            "provider_paths_retrying" or
            "provider_paths_ready" or
            "provider_paths_degraded" or
            "listener_ready" or
            "waiting_for_peer_dial" or
            "peer_connected";
    }

    private static bool IsLockedLike(string value)
        => value is "locked" or
            "off" or
            "wallet_missing_dialer_only" or
            "wallet_unverified_dialer_only" or
            "wallet_validation_failed_dialer_only" or
            "wallet_empty_dialer_only" or
            "sidecar_unavailable";

    private static bool IsMeaningfulRuntimeStatus(string value)
        => value is not "unknown" and not "inactive";

    private static bool IsMeaningfulTransportReason(string value)
        => value is not "unknown" and not "inactive" and not "transport_without_acceleration" and not "transport_unwired";

    private static bool IsFallbackOrFailure(string value)
        => value.Contains("fallback", StringComparison.Ordinal) ||
           value.Contains("failed", StringComparison.Ordinal) ||
           value.Contains("unavailable", StringComparison.Ordinal) ||
           value.Contains("timeout", StringComparison.Ordinal) ||
           value.Contains("rejected", StringComparison.Ordinal) ||
           value.Contains("exited", StringComparison.Ordinal) ||
           value.Contains("not_eligible", StringComparison.Ordinal) ||
           value.Contains("no_eligible_lane", StringComparison.Ordinal) ||
           value.Contains("no_supported_lane", StringComparison.Ordinal) ||
           value.Contains("queue_rejected", StringComparison.Ordinal);

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim();
}
