using System.Diagnostics;
using NLink.App.Configuration;

namespace NLink.App.Services;

internal static class SessionUiDebug
{
    [Conditional("DEBUG")]
    public static void LogPhaseChange(
        string source,
        SessionUiPhase previous,
        SessionUiPhase next,
        SessionRuntimeState runtimeState)
    {
        AppLog.Info(
            $"event=session_ui_phase_change; source={source}; previous={previous}; next={next}; runtime_state={runtimeState}");
    }
}
