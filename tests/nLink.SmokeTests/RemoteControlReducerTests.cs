using NLink.App.Services.RemoteControl;
using NLink.Core.RemoteControl;

namespace NLink.SmokeTests;

public sealed class RemoteControlReducerTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void Reducer_HelperRequest_TransitionsToRequesting_AndSchedulesRequestTimeout()
    {
        var current = SupportedState(
            ControlState.Off,
            requestId: null,
            controllerPeerId: null,
            consentToken: null);

        var transition = RemoteControlReducer.Apply(
            current,
            new RemoteControlReducerEvent(
                RemoteControlReducerEventKind.HelperRequestClicked,
                "helper_request",
                RequestId: "req-1",
                OccurredAtUnixMs: 10_000));

        Assert.Equal(ControlState.Requesting, transition.NextState.ControlState);
        Assert.Equal("req-1", transition.NextState.CurrentControlRequestId);
        Assert.Null(transition.NextState.ControllerPeerId);
        Assert.Null(transition.NextState.ConsentToken);
        Assert.Contains(
            transition.SideEffects,
            e => e.Kind == RemoteControlSideEffectKind.SendControlRequest && e.RequestId == "req-1");
        Assert.Contains(
            transition.SideEffects,
            e => e.Kind == RemoteControlSideEffectKind.ScheduleTimeout &&
                 e.TimeoutKind == RemoteControlReducerTimeoutKind.Request &&
                 e.RequestId == "req-1");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Reducer_DenyThenDeniedCooldownTimeout_TransitionsDeniedThenOff()
    {
        var requesting = SupportedState(
            ControlState.Requesting,
            requestId: "req-2",
            controllerPeerId: "peer-2",
            consentToken: null);

        var denied = RemoteControlReducer.Apply(
            requesting,
            new RemoteControlReducerEvent(
                RemoteControlReducerEventKind.TransportControlResponseReceived,
                "deny",
                RequestId: "req-2",
                Decision: RemoteControlReducerResponseDecision.Deny,
                OccurredAtUnixMs: 20_000));

        Assert.Equal(ControlState.Denied, denied.NextState.ControlState);
        Assert.Contains(
            denied.SideEffects,
            e => e.Kind == RemoteControlSideEffectKind.ScheduleTimeout &&
                 e.TimeoutKind == RemoteControlReducerTimeoutKind.DeniedCooldown);

        var cleared = RemoteControlReducer.Apply(
            denied.NextState,
            new RemoteControlReducerEvent(
                RemoteControlReducerEventKind.SystemTimeout,
                "denied_cooldown_elapsed",
                RequestId: "req-2",
                TimeoutKind: RemoteControlReducerTimeoutKind.DeniedCooldown,
                OccurredAtUnixMs: 25_000));

        Assert.Equal(ControlState.Off, cleared.NextState.ControlState);
        Assert.Null(cleared.NextState.CurrentControlRequestId);
        Assert.Null(cleared.NextState.ControllerPeerId);
        Assert.Null(cleared.NextState.ConsentToken);
        Assert.Contains(cleared.SideEffects, e => e.Kind == RemoteControlSideEffectKind.CancelTimeouts);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Reducer_AllowThenStart_TransitionsToActive()
    {
        var requesting = SupportedState(
            ControlState.Requesting,
            requestId: "req-3",
            controllerPeerId: "peer-3",
            consentToken: null);

        var allowed = RemoteControlReducer.Apply(
            requesting,
            new RemoteControlReducerEvent(
                RemoteControlReducerEventKind.TransportControlResponseReceived,
                "allow",
                RequestId: "req-3",
                ConsentToken: "token-3",
                Decision: RemoteControlReducerResponseDecision.Allow,
                OccurredAtUnixMs: 30_000));

        Assert.Equal(ControlState.Requesting, allowed.NextState.ControlState);
        Assert.Equal("token-3", allowed.NextState.ConsentToken);
        Assert.Contains(
            allowed.SideEffects,
            e => e.Kind == RemoteControlSideEffectKind.SendControlStart &&
                 e.RequestId == "req-3" &&
                 e.ConsentToken == "token-3");

        var started = RemoteControlReducer.Apply(
            allowed.NextState,
            new RemoteControlReducerEvent(
                RemoteControlReducerEventKind.TransportControlStartReceived,
                "start_received",
                RequestId: "req-3",
                PeerId: "peer-3",
                OccurredAtUnixMs: 31_000));

        Assert.Equal(ControlState.Active, started.NextState.ControlState);
        Assert.Equal("req-3", started.NextState.CurrentControlRequestId);
        Assert.Equal("peer-3", started.NextState.ControllerPeerId);
        Assert.Null(started.NextState.ConsentToken);
        Assert.Contains(started.SideEffects, e => e.Kind == RemoteControlSideEffectKind.CancelTimeouts);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Reducer_StopAlways_TransitionsToOff_AndEmitsFlushCancel()
    {
        var active = SupportedState(
            ControlState.Active,
            requestId: "req-stop",
            controllerPeerId: "peer-stop",
            consentToken: null);

        var stopped = RemoteControlReducer.Apply(
            active,
            new RemoteControlReducerEvent(
                RemoteControlReducerEventKind.HelperStopClicked,
                "helper_stop",
                RequestId: "req-stop",
                PeerId: "peer-stop",
                OccurredAtUnixMs: 40_000));

        Assert.Equal(ControlState.Off, stopped.NextState.ControlState);
        Assert.Null(stopped.NextState.CurrentControlRequestId);
        Assert.Null(stopped.NextState.ControllerPeerId);
        Assert.Contains(stopped.SideEffects, e => e.Kind == RemoteControlSideEffectKind.SendControlStop);
        Assert.Contains(stopped.SideEffects, e => e.Kind == RemoteControlSideEffectKind.FlushOutgoingMouseMoves);
        Assert.Contains(stopped.SideEffects, e => e.Kind == RemoteControlSideEffectKind.FlushInjectionQueue);
        Assert.Contains(stopped.SideEffects, e => e.Kind == RemoteControlSideEffectKind.CancelTimeouts);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Reducer_DisconnectInAnyState_TransitionsToOff_AndEmitsFlushActions()
    {
        var cases = new[]
        {
            SupportedState(ControlState.Off, requestId: null, controllerPeerId: null, consentToken: null),
            SupportedState(ControlState.Requesting, requestId: "req-a", controllerPeerId: "peer-a", consentToken: null),
            SupportedState(ControlState.Denied, requestId: "req-b", controllerPeerId: null, consentToken: null),
            SupportedState(ControlState.Active, requestId: "req-c", controllerPeerId: "peer-c", consentToken: null),
        };

        foreach (var current in cases)
        {
            var transition = RemoteControlReducer.Apply(
                current,
                new RemoteControlReducerEvent(
                    RemoteControlReducerEventKind.SystemDisconnect,
                    "disconnect",
                    RequestId: current.CurrentControlRequestId,
                    PeerId: current.ControllerPeerId,
                    OccurredAtUnixMs: 50_000));

            Assert.Equal(ControlState.Off, transition.NextState.ControlState);
            Assert.Contains(transition.SideEffects, e => e.Kind == RemoteControlSideEffectKind.FlushOutgoingMouseMoves);
            Assert.Contains(transition.SideEffects, e => e.Kind == RemoteControlSideEffectKind.FlushInjectionQueue);
            Assert.Contains(transition.SideEffects, e => e.Kind == RemoteControlSideEffectKind.CancelTimeouts);

            if (string.IsNullOrWhiteSpace(current.CurrentControlRequestId))
            {
                Assert.DoesNotContain(transition.SideEffects, e => e.Kind == RemoteControlSideEffectKind.SendControlStop);
            }
            else
            {
                Assert.Contains(
                    transition.SideEffects,
                    e => e.Kind == RemoteControlSideEffectKind.SendControlStop &&
                         e.RequestId == current.CurrentControlRequestId);
            }
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void Reducer_LateResponseOrStart_ForOldRequest_IsIgnoredWithoutStateRegression()
    {
        var current = SupportedState(
            ControlState.Requesting,
            requestId: "req-current",
            controllerPeerId: "peer-current",
            consentToken: null);

        var lateResponse = RemoteControlReducer.Apply(
            current,
            new RemoteControlReducerEvent(
                RemoteControlReducerEventKind.TransportControlResponseReceived,
                "late_response",
                RequestId: "req-old",
                Decision: RemoteControlReducerResponseDecision.Allow,
                ConsentToken: "token-old",
                OccurredAtUnixMs: 60_000));

        Assert.Equal(current, lateResponse.NextState);
        Assert.DoesNotContain(lateResponse.SideEffects, e => e.Kind == RemoteControlSideEffectKind.SendControlStart);

        var lateStart = RemoteControlReducer.Apply(
            current,
            new RemoteControlReducerEvent(
                RemoteControlReducerEventKind.TransportControlStartReceived,
                "late_start",
                RequestId: "req-old",
                PeerId: "peer-current",
                OccurredAtUnixMs: 60_500));

        Assert.Equal(current, lateStart.NextState);
        Assert.Contains(lateStart.SideEffects, e => e.Kind == RemoteControlSideEffectKind.SendControlStop);
    }

    private static RemoteControlSessionState SupportedState(
        ControlState controlState,
        string? requestId,
        string? controllerPeerId,
        string? consentToken)
    {
        return new RemoteControlSessionState(
            ControlState: controlState,
            ControllerPeerId: controllerPeerId,
            CurrentControlRequestId: requestId,
            ConsentToken: consentToken,
            SupportsRemoteControl: true,
            PeerSupportsRemoteControl: true);
    }
}
