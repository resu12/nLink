using System;

namespace NLink.App.Services;

internal static class SessionFlowViewProjection
{
    public static string ResolveStatusText(SessionFlowSnapshot flow, string connectedStatusText)
    {
        if (string.Equals(flow.DisplayConnectionState, "Connected", StringComparison.Ordinal))
        {
            return connectedStatusText;
        }

        return flow.DisplayStatusText;
    }

    public static bool IsConnectedShell(SessionFlowSnapshot flow)
    {
        return flow.UiPhase == SessionUiPhase.Connected &&
               string.Equals(flow.DisplayConnectionState, "Connected", StringComparison.Ordinal);
    }
}
