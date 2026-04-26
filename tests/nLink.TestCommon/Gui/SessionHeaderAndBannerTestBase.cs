using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using NLink.App;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Metrics;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.Retry;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Core.Logging;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

public abstract class SessionHeaderAndBannerTestBase : CoreSmokeTestsBase
{
    protected static SessionFlowSnapshot BuildHelperConnectedFlow(SessionRuntime runtime)
    {
        return runtime.FlowSnapshot with
        {
            Phase = SessionFlowPhase.ActiveSession,
            UiPhase = SessionUiPhase.Connected,
            Role = SessionRuntimeRole.Helper,
            RuntimeState = SessionRuntimeState.Connected,
            TransportState = TransportState.Connected,
            ApprovalActive = true,
            DisplayStatusText = "Connected",
            DisplayConnectionState = "Connected",
        };
    }

    protected static SessionFlowSnapshot BuildHelperWaitingFlow(SessionRuntime runtime)
    {
        return runtime.FlowSnapshot with
        {
            Phase = SessionFlowPhase.ListenerWaiting,
            UiPhase = SessionUiPhase.Waiting,
            Role = SessionRuntimeRole.Helper,
            RuntimeState = SessionRuntimeState.Waiting,
            DisplayStatusText = "Waiting for help requests…",
            DisplayConnectionState = "Waiting",
            StatusText = "Waiting for help requests…",
        };
    }

    protected static SessionFlowSnapshot BuildHelperPeerEndedFlow(SessionRuntime runtime, string statusText)
    {
        return runtime.FlowSnapshot with
        {
            Phase = SessionFlowPhase.Ended,
            UiPhase = SessionUiPhase.Waiting,
            Role = SessionRuntimeRole.Helper,
            RuntimeState = SessionRuntimeState.Disconnected,
            LastEndOrigin = SessionFlowEndOrigin.Remote,
            TerminalKind = SessionTerminalKind.PeerEnded,
            TerminalStatusText = statusText,
            ShouldShowPeerEndedNotice = true,
            ShouldClearConversationUi = true,
            ShouldSuppressConnectedControls = true,
            DisplayStatusText = statusText,
            DisplayConnectionState = "Waiting",
            StatusText = statusText,
        };
    }

    protected static SessionFlowSnapshot BuildHelpeeConnectedFlow(SessionRuntime runtime)
    {
        return runtime.FlowSnapshot with
        {
            Phase = SessionFlowPhase.ActiveSession,
            UiPhase = SessionUiPhase.Connected,
            Role = SessionRuntimeRole.Helpee,
            RuntimeState = SessionRuntimeState.Connected,
            TransportState = TransportState.Connected,
            ApprovalActive = true,
            DisplayStatusText = "Connected",
            DisplayConnectionState = "Connected",
        };
    }

    protected static SessionFlowSnapshot BuildHelpeeWaitingFlow(SessionRuntime runtime)
    {
        return runtime.FlowSnapshot with
        {
            Phase = SessionFlowPhase.HelpeeWaiting,
            UiPhase = SessionUiPhase.Waiting,
            Role = SessionRuntimeRole.Helpee,
            RuntimeState = SessionRuntimeState.Waiting,
            DisplayStatusText = "Waiting for helper…",
            DisplayConnectionState = "Waiting",
            StatusText = "Waiting for helper…",
        };
    }

}

