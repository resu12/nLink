using System;
using System.Collections.Generic;
using NLink.Core.RemoteControl;

namespace NLink.App.Services.RemoteControl;

internal enum RemoteControlReducerEventKind
{
    HelperRequestClicked = 0,
    HelperStopClicked = 1,
    HelperControlModeToggled = 2,
    HelpeeConsentAllowed = 3,
    HelpeeConsentDenied = 4,
    HelpeeUserStopClicked = 5,
    TransportControlRequestReceived = 6,
    TransportControlResponseReceived = 7,
    TransportControlStartReceived = 8,
    TransportControlStopReceived = 9,
    TransportDisplayInfoChanged = 10,
    SystemTimeout = 11,
    SystemDisconnect = 12,
}

internal enum RemoteControlReducerTimeoutKind
{
    Request = 0,
    ConsentDecision = 1,
    StartAwait = 2,
    DeniedCooldown = 3,
}

internal enum RemoteControlReducerResponseDecision
{
    Unknown = 0,
    Allow = 1,
    Deny = 2,
}

internal enum RemoteControlSideEffectKind
{
    None = 0,
    SendControlRequest = 1,
    SendControlResponse = 2,
    SendControlStart = 3,
    SendControlStop = 4,
    ScheduleTimeout = 5,
    CancelTimeouts = 6,
    FlushOutgoingMouseMoves = 7,
    FlushInjectionQueue = 8,
    SetConsentPromptVisible = 9,
    SetControlModeEnabled = 10,
    Log = 11,
    // Backward-compat aliases while migrating call sites.
    StartTimer = 12,
    StopTimer = 13,
}

internal readonly record struct RemoteControlReducerEvent(
    RemoteControlReducerEventKind Kind,
    string Reason,
    string? RequestId = null,
    string? PeerId = null,
    string? ConsentToken = null,
    RemoteControlReducerResponseDecision Decision = RemoteControlReducerResponseDecision.Unknown,
    RemoteControlReducerTimeoutKind? TimeoutKind = null,
    long? TimeoutMs = null,
    bool? ControlModeEnabled = null,
    long? OccurredAtUnixMs = null);

internal readonly record struct RemoteControlSideEffect(
    RemoteControlSideEffectKind Kind,
    string? Reason = null,
    string? RequestId = null,
    string? PeerId = null,
    string? ConsentToken = null,
    RemoteControlReducerResponseDecision Decision = RemoteControlReducerResponseDecision.Unknown,
    RemoteControlReducerTimeoutKind? TimeoutKind = null,
    long? TimeoutMs = null,
    bool? BoolValue = null,
    long? DeadlineUnixMs = null);

internal readonly record struct RemoteControlReducerResult(
    RemoteControlSessionState PreviousState,
    RemoteControlSessionState NextState,
    IReadOnlyList<RemoteControlSideEffect> SideEffects);

internal static class RemoteControlReducer
{
    private const long DefaultRequestTimeoutMs = 30_000;
    private const long DefaultConsentDecisionTimeoutMs = 20_000;
    private const long DefaultStartAwaitTimeoutMs = 20_000;
    private const long DefaultDeniedCooldownTimeoutMs = 3_000;

    public static RemoteControlReducerResult Apply(
        RemoteControlSessionState current,
        in RemoteControlReducerEvent evt)
    {
        var next = current;
        var sideEffects = new List<RemoteControlSideEffect>(6);
        var normalizedRequestId = Normalize(evt.RequestId);
        var normalizedPeerId = Normalize(evt.PeerId);
        var normalizedToken = Normalize(evt.ConsentToken);

        switch (evt.Kind)
        {
            case RemoteControlReducerEventKind.HelperRequestClicked:
            {
                if (current.ControlState != ControlState.Off ||
                    !current.SessionSupportsRemoteControl ||
                    string.IsNullOrWhiteSpace(normalizedRequestId))
                {
                    sideEffects.Add(LogEffect(evt.Reason, normalizedRequestId, normalizedPeerId));
                    break;
                }

                next = next with
                {
                    ControlState = ControlState.Requesting,
                    CurrentControlRequestId = normalizedRequestId,
                    ConsentToken = null,
                    ControllerPeerId = null,
                };

                sideEffects.Add(new RemoteControlSideEffect(
                    RemoteControlSideEffectKind.SendControlRequest,
                    evt.Reason,
                    RequestId: normalizedRequestId));
                sideEffects.Add(ScheduleTimeoutEffect(
                    evt,
                    normalizedRequestId,
                    RemoteControlReducerTimeoutKind.Request));
                sideEffects.Add(LogEffect(evt.Reason, normalizedRequestId, normalizedPeerId));
                break;
            }

            case RemoteControlReducerEventKind.HelperStopClicked:
            case RemoteControlReducerEventKind.HelpeeUserStopClicked:
            case RemoteControlReducerEventKind.TransportControlStopReceived:
            case RemoteControlReducerEventKind.SystemDisconnect:
            {
                var shouldSendStop = evt.Kind != RemoteControlReducerEventKind.TransportControlStopReceived &&
                                     !string.IsNullOrWhiteSpace(current.CurrentControlRequestId);
                next = ClearToOff(next);

                if (shouldSendStop)
                {
                    sideEffects.Add(new RemoteControlSideEffect(
                        RemoteControlSideEffectKind.SendControlStop,
                        evt.Reason,
                        RequestId: current.CurrentControlRequestId));
                }

                sideEffects.Add(new RemoteControlSideEffect(
                    RemoteControlSideEffectKind.FlushOutgoingMouseMoves,
                    evt.Reason,
                    RequestId: current.CurrentControlRequestId));
                sideEffects.Add(new RemoteControlSideEffect(
                    RemoteControlSideEffectKind.FlushInjectionQueue,
                    evt.Reason,
                    RequestId: current.CurrentControlRequestId));
                sideEffects.Add(StopAllTimersEffect(evt.Reason));
                sideEffects.Add(new RemoteControlSideEffect(
                    RemoteControlSideEffectKind.SetConsentPromptVisible,
                    evt.Reason,
                    BoolValue: false));
                sideEffects.Add(LogEffect(evt.Reason, current.CurrentControlRequestId, current.ControllerPeerId));
                break;
            }

            case RemoteControlReducerEventKind.HelperControlModeToggled:
            {
                if (evt.ControlModeEnabled.HasValue)
                {
                    sideEffects.Add(new RemoteControlSideEffect(
                        RemoteControlSideEffectKind.SetControlModeEnabled,
                        evt.Reason,
                        BoolValue: evt.ControlModeEnabled.Value));
                }

                sideEffects.Add(LogEffect(evt.Reason, current.CurrentControlRequestId, current.ControllerPeerId));
                break;
            }

            case RemoteControlReducerEventKind.HelpeeConsentAllowed:
            {
                if (current.ControlState != ControlState.Requesting ||
                    string.IsNullOrWhiteSpace(current.CurrentControlRequestId))
                {
                    sideEffects.Add(LogEffect(evt.Reason, normalizedRequestId, normalizedPeerId));
                    break;
                }

                next = next with
                {
                    ControlState = ControlState.Requesting,
                    CurrentControlRequestId = current.CurrentControlRequestId,
                    ConsentToken = null,
                    ControllerPeerId = normalizedPeerId ?? current.ControllerPeerId,
                };

                sideEffects.Add(new RemoteControlSideEffect(
                    RemoteControlSideEffectKind.SendControlResponse,
                    evt.Reason,
                    RequestId: current.CurrentControlRequestId,
                    Decision: RemoteControlReducerResponseDecision.Allow,
                    ConsentToken: normalizedToken));
                sideEffects.Add(ScheduleTimeoutEffect(
                    evt,
                    current.CurrentControlRequestId,
                    RemoteControlReducerTimeoutKind.StartAwait));
                sideEffects.Add(new RemoteControlSideEffect(
                    RemoteControlSideEffectKind.SetConsentPromptVisible,
                    evt.Reason,
                    BoolValue: false));
                sideEffects.Add(LogEffect(evt.Reason, current.CurrentControlRequestId, next.ControllerPeerId));
                break;
            }

            case RemoteControlReducerEventKind.HelpeeConsentDenied:
            {
                if (current.ControlState != ControlState.Requesting ||
                    string.IsNullOrWhiteSpace(current.CurrentControlRequestId))
                {
                    sideEffects.Add(LogEffect(evt.Reason, normalizedRequestId, normalizedPeerId));
                    break;
                }

                next = next with
                {
                    ControlState = ControlState.Denied,
                    ConsentToken = null,
                    ControllerPeerId = null,
                };

                sideEffects.Add(new RemoteControlSideEffect(
                    RemoteControlSideEffectKind.SendControlResponse,
                    evt.Reason,
                    RequestId: current.CurrentControlRequestId,
                    Decision: RemoteControlReducerResponseDecision.Deny));
                sideEffects.Add(ScheduleTimeoutEffect(
                    evt,
                    current.CurrentControlRequestId,
                    RemoteControlReducerTimeoutKind.DeniedCooldown));
                sideEffects.Add(new RemoteControlSideEffect(
                    RemoteControlSideEffectKind.SetConsentPromptVisible,
                    evt.Reason,
                    BoolValue: false));
                sideEffects.Add(LogEffect(evt.Reason, current.CurrentControlRequestId, current.ControllerPeerId));
                break;
            }

            case RemoteControlReducerEventKind.TransportControlRequestReceived:
            {
                if (current.ControlState is ControlState.Active or ControlState.Requesting)
                {
                    sideEffects.Add(new RemoteControlSideEffect(
                        RemoteControlSideEffectKind.SendControlResponse,
                        evt.Reason,
                        RequestId: normalizedRequestId,
                        PeerId: normalizedPeerId,
                        Decision: RemoteControlReducerResponseDecision.Deny));
                    sideEffects.Add(LogEffect(evt.Reason, normalizedRequestId, normalizedPeerId));
                    break;
                }

                if (string.IsNullOrWhiteSpace(normalizedRequestId))
                {
                    sideEffects.Add(LogEffect(evt.Reason, normalizedRequestId, normalizedPeerId));
                    break;
                }

                next = next with
                {
                    ControlState = ControlState.Requesting,
                    CurrentControlRequestId = normalizedRequestId,
                    ControllerPeerId = normalizedPeerId,
                    ConsentToken = null,
                };

                sideEffects.Add(new RemoteControlSideEffect(
                    RemoteControlSideEffectKind.SetConsentPromptVisible,
                    evt.Reason,
                    RequestId: normalizedRequestId,
                    BoolValue: true));
                sideEffects.Add(ScheduleTimeoutEffect(
                    evt,
                    normalizedRequestId,
                    RemoteControlReducerTimeoutKind.ConsentDecision));
                sideEffects.Add(LogEffect(evt.Reason, normalizedRequestId, normalizedPeerId));
                break;
            }

            case RemoteControlReducerEventKind.TransportControlResponseReceived:
            {
                if (current.ControlState != ControlState.Requesting ||
                    !MatchesRequest(current, normalizedRequestId))
                {
                    sideEffects.Add(LogEffect(evt.Reason, normalizedRequestId, normalizedPeerId));
                    break;
                }

                if (evt.Decision == RemoteControlReducerResponseDecision.Allow)
                {
                    next = next with
                    {
                        ControlState = ControlState.Requesting,
                        ConsentToken = normalizedToken,
                        ControllerPeerId = current.ControllerPeerId ?? "local-helper",
                    };

                    sideEffects.Add(new RemoteControlSideEffect(
                        RemoteControlSideEffectKind.SendControlStart,
                        evt.Reason,
                        RequestId: current.CurrentControlRequestId,
                        ConsentToken: normalizedToken));
                    sideEffects.Add(LogEffect(evt.Reason, current.CurrentControlRequestId, current.ControllerPeerId));
                    break;
                }

                next = next with
                {
                    ControlState = ControlState.Denied,
                    ConsentToken = null,
                    ControllerPeerId = null,
                };
                sideEffects.Add(ScheduleTimeoutEffect(
                    evt,
                    current.CurrentControlRequestId,
                    RemoteControlReducerTimeoutKind.DeniedCooldown));
                sideEffects.Add(LogEffect(evt.Reason, current.CurrentControlRequestId, current.ControllerPeerId));
                break;
            }

            case RemoteControlReducerEventKind.TransportControlStartReceived:
            {
                if (current.ControlState != ControlState.Requesting ||
                    !MatchesRequest(current, normalizedRequestId))
                {
                    sideEffects.Add(new RemoteControlSideEffect(
                        RemoteControlSideEffectKind.SendControlStop,
                        evt.Reason,
                        RequestId: normalizedRequestId));
                    sideEffects.Add(LogEffect(evt.Reason, normalizedRequestId, normalizedPeerId));
                    break;
                }

                next = next with
                {
                    ControlState = ControlState.Active,
                    ConsentToken = null,
                    ControllerPeerId = current.ControllerPeerId ?? normalizedPeerId,
                };
                sideEffects.Add(StopAllTimersEffect(evt.Reason));
                sideEffects.Add(new RemoteControlSideEffect(
                    RemoteControlSideEffectKind.SetConsentPromptVisible,
                    evt.Reason,
                    BoolValue: false));
                sideEffects.Add(LogEffect(evt.Reason, current.CurrentControlRequestId, next.ControllerPeerId));
                break;
            }

            case RemoteControlReducerEventKind.TransportDisplayInfoChanged:
            {
                if (current.ControlState == ControlState.Active)
                {
                    next = ClearToOff(next);
                    if (!string.IsNullOrWhiteSpace(current.CurrentControlRequestId))
                    {
                        sideEffects.Add(new RemoteControlSideEffect(
                            RemoteControlSideEffectKind.SendControlStop,
                            evt.Reason,
                            RequestId: current.CurrentControlRequestId));
                    }

                    sideEffects.Add(new RemoteControlSideEffect(
                        RemoteControlSideEffectKind.FlushOutgoingMouseMoves,
                        evt.Reason,
                        RequestId: current.CurrentControlRequestId));
                    sideEffects.Add(new RemoteControlSideEffect(
                        RemoteControlSideEffectKind.FlushInjectionQueue,
                        evt.Reason,
                        RequestId: current.CurrentControlRequestId));
                    sideEffects.Add(StopAllTimersEffect(evt.Reason));
                }

                sideEffects.Add(LogEffect(evt.Reason, current.CurrentControlRequestId, current.ControllerPeerId));
                break;
            }

            case RemoteControlReducerEventKind.SystemTimeout:
            {
                if (!evt.TimeoutKind.HasValue)
                {
                    sideEffects.Add(LogEffect(evt.Reason, current.CurrentControlRequestId, current.ControllerPeerId));
                    break;
                }

                switch (evt.TimeoutKind.Value)
                {
                    case RemoteControlReducerTimeoutKind.Request:
                    case RemoteControlReducerTimeoutKind.ConsentDecision:
                        if (current.ControlState == ControlState.Requesting &&
                            MatchesRequestOrUnspecified(current, normalizedRequestId))
                        {
                            next = next with
                            {
                                ControlState = ControlState.Denied,
                                ConsentToken = null,
                                ControllerPeerId = null,
                            };
                            sideEffects.Add(ScheduleTimeoutEffect(
                                evt,
                                current.CurrentControlRequestId,
                                RemoteControlReducerTimeoutKind.DeniedCooldown));
                            sideEffects.Add(new RemoteControlSideEffect(
                                RemoteControlSideEffectKind.SetConsentPromptVisible,
                                evt.Reason,
                                BoolValue: false));
                        }
                        break;
                    case RemoteControlReducerTimeoutKind.StartAwait:
                    case RemoteControlReducerTimeoutKind.DeniedCooldown:
                        if (current.ControlState is ControlState.Requesting or ControlState.Denied &&
                            MatchesRequestOrUnspecified(current, normalizedRequestId))
                        {
                            next = ClearToOff(next);
                            sideEffects.Add(StopAllTimersEffect(evt.Reason));
                        }
                        break;
                }

                sideEffects.Add(LogEffect(evt.Reason, current.CurrentControlRequestId, current.ControllerPeerId));
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(evt.Kind), evt.Kind, "Unknown remote control reducer event.");
        }

        return new RemoteControlReducerResult(current, next, sideEffects);
    }

    private static bool MatchesRequest(RemoteControlSessionState current, string? requestId)
    {
        return !string.IsNullOrWhiteSpace(requestId) &&
               string.Equals(current.CurrentControlRequestId, requestId, StringComparison.Ordinal);
    }

    private static bool MatchesRequestOrUnspecified(RemoteControlSessionState current, string? requestId)
    {
        return string.IsNullOrWhiteSpace(requestId) ||
               string.Equals(current.CurrentControlRequestId, requestId, StringComparison.Ordinal);
    }

    private static RemoteControlSessionState ClearToOff(RemoteControlSessionState state)
    {
        return state with
        {
            ControlState = ControlState.Off,
            CurrentControlRequestId = null,
            ConsentToken = null,
            ControllerPeerId = null,
        };
    }

    private static RemoteControlSideEffect StopAllTimersEffect(string reason)
    {
        return new RemoteControlSideEffect(
            RemoteControlSideEffectKind.CancelTimeouts,
            reason,
            TimeoutKind: null);
    }

    private static RemoteControlSideEffect ScheduleTimeoutEffect(
        in RemoteControlReducerEvent evt,
        string? requestId,
        RemoteControlReducerTimeoutKind timeoutKind)
    {
        var timeoutMs = ResolveTimeoutMs(evt.TimeoutMs, timeoutKind);
        var occurredAtUnixMs = evt.OccurredAtUnixMs.GetValueOrDefault(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var deadlineUnixMs = occurredAtUnixMs + timeoutMs;
        return new RemoteControlSideEffect(
            RemoteControlSideEffectKind.ScheduleTimeout,
            evt.Reason,
            RequestId: requestId,
            TimeoutKind: timeoutKind,
            TimeoutMs: timeoutMs,
            DeadlineUnixMs: deadlineUnixMs);
    }

    private static long ResolveTimeoutMs(long? timeoutMs, RemoteControlReducerTimeoutKind timeoutKind)
    {
        if (timeoutMs.HasValue && timeoutMs.Value > 0)
        {
            return timeoutMs.Value;
        }

        return timeoutKind switch
        {
            RemoteControlReducerTimeoutKind.Request => DefaultRequestTimeoutMs,
            RemoteControlReducerTimeoutKind.ConsentDecision => DefaultConsentDecisionTimeoutMs,
            RemoteControlReducerTimeoutKind.StartAwait => DefaultStartAwaitTimeoutMs,
            RemoteControlReducerTimeoutKind.DeniedCooldown => DefaultDeniedCooldownTimeoutMs,
            _ => DefaultConsentDecisionTimeoutMs,
        };
    }

    private static RemoteControlSideEffect LogEffect(string reason, string? requestId, string? peerId)
    {
        return new RemoteControlSideEffect(
            RemoteControlSideEffectKind.Log,
            reason,
            RequestId: requestId,
            PeerId: peerId);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
