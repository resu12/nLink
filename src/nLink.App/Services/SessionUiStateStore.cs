using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NLink.App.Services;

public sealed class SessionUiStateStore : ObservableObject
{
    private SessionUiPhase phase = SessionUiPhase.Idle;
    private string lastChangeReason = "initial";
    private DateTimeOffset lastChangeAt = DateTimeOffset.UtcNow;
    private SessionUxContext? context;

    public SessionUiPhase Phase
    {
        get => phase;
        private set => SetProperty(ref phase, value);
    }

    public string LastChangeReason
    {
        get => lastChangeReason;
        private set => SetProperty(ref lastChangeReason, value);
    }

    public DateTimeOffset LastChangeAt
    {
        get => lastChangeAt;
        private set => SetProperty(ref lastChangeAt, value);
    }

    public SessionUxContext? Context
    {
        get => context;
        private set => SetProperty(ref context, value);
    }

    public void SetPhase(SessionUiPhase next, string reason, SessionUxContext? context = null)
    {
        Phase = next;
        LastChangeReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
        LastChangeAt = DateTimeOffset.UtcNow;
        Context = context;
    }
}
