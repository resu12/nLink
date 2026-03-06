using System;
using System.Diagnostics;
using NLink.Core.RemoteControl;

namespace NLink.App.Services.RemoteControl;

public enum RemoteControlDiagnosticsRole
{
    Helper,
    Helpee,
}

public readonly record struct RemoteControlRectPx(int X, int Y, int Width, int Height);

public readonly record struct RemoteControlSizePx(int Width, int Height);

public readonly record struct RemoteControlLastMapped(double Nx, double Ny, int Px, int Py);

public readonly record struct RemoteControlDebugSnapshot(
    RemoteControlDiagnosticsRole Role,
    ControlState ControlState,
    string DisplayId,
    long DisplayRevision,
    RemoteControlRectPx? CaptureRegionPx,
    RemoteControlSizePx? FrameSizePx,
    bool? ControlMode,
    int? MouseMoveSentPerSec,
    long MouseMoveDropped,
    int? InjectionQueueSize,
    long SuppressedInjections,
    long QueueFlushes,
    long LastInjectedSeq,
    long LastAckSentSeq,
    long AckSentCount,
    long SnapshotReceivedCount,
    long SnapshotAppliedCount,
    long SnapshotUnstuckButtonsCount,
    long SnapshotUnstuckModifiersCount,
    long HelpeeLastSnapshotReceivedSeq,
    int HelpeeLastSnapshotReceivedModifiersMask,
    int HelpeeLastSnapshotReceivedMouseButtonsMask,
    long HelpeeLastSnapshotAppliedSeq,
    int HelpeeLastSnapshotAppliedModifiersMask,
    int HelpeeLastSnapshotAppliedMouseButtonsMask,
    long HelperLastSnapshotSentSeq,
    int HelperLastSnapshotSentModifiersMask,
    int HelperLastSnapshotSentMouseButtonsMask,
    int? HelperSnapshotSentPerSec,
    long HelperLastAckSeq,
    long? HelperLastAckAgeMs,
    long HelperStallDetectedCount,
    long HelperStallRecoverySentCount,
    RemoteControlLastMapped? LastMapped,
    long OutOfRangeClamps,
    long DroppedMouseMoves,
    long GuardrailSuppressedInjections,
    long GuardrailQueueFlushes)
{
    public static RemoteControlDebugSnapshot Empty(RemoteControlDiagnosticsRole role) =>
        new(
            Role: role,
            ControlState: ControlState.Off,
            DisplayId: "(none)",
            DisplayRevision: 0,
            CaptureRegionPx: null,
            FrameSizePx: null,
            ControlMode: null,
            MouseMoveSentPerSec: null,
            MouseMoveDropped: 0,
            InjectionQueueSize: null,
            SuppressedInjections: 0,
            QueueFlushes: 0,
            LastInjectedSeq: 0,
            LastAckSentSeq: 0,
            AckSentCount: 0,
            SnapshotReceivedCount: 0,
            SnapshotAppliedCount: 0,
            SnapshotUnstuckButtonsCount: 0,
            SnapshotUnstuckModifiersCount: 0,
            HelpeeLastSnapshotReceivedSeq: 0,
            HelpeeLastSnapshotReceivedModifiersMask: 0,
            HelpeeLastSnapshotReceivedMouseButtonsMask: 0,
            HelpeeLastSnapshotAppliedSeq: 0,
            HelpeeLastSnapshotAppliedModifiersMask: 0,
            HelpeeLastSnapshotAppliedMouseButtonsMask: 0,
            HelperLastSnapshotSentSeq: 0,
            HelperLastSnapshotSentModifiersMask: 0,
            HelperLastSnapshotSentMouseButtonsMask: 0,
            HelperSnapshotSentPerSec: null,
            HelperLastAckSeq: 0,
            HelperLastAckAgeMs: null,
            HelperStallDetectedCount: 0,
            HelperStallRecoverySentCount: 0,
            LastMapped: null,
            OutOfRangeClamps: 0,
            DroppedMouseMoves: 0,
            GuardrailSuppressedInjections: 0,
            GuardrailQueueFlushes: 0);
}

public static class RemoteControlDebugDiagnostics
{
#if DEBUG
    private sealed class RoleState
    {
        public readonly object Gate = new();
        public ControlState ControlState = ControlState.Off;
        public string DisplayId = "(none)";
        public long DisplayRevision;
        public RemoteControlRectPx? CaptureRegionPx;
        public RemoteControlSizePx? FrameSizePx;
        public bool? ControlMode;
        public int? MouseMoveSentPerSec;
        public long MouseMoveDropped;
        public int? InjectionQueueSize;
        public long SuppressedInjections;
        public long QueueFlushes;
        public long LastInjectedSeq;
        public long LastAckSentSeq;
        public long AckSentCount;
        public long SnapshotReceivedCount;
        public long SnapshotAppliedCount;
        public long SnapshotUnstuckButtonsCount;
        public long SnapshotUnstuckModifiersCount;
        public long HelpeeLastSnapshotReceivedSeq;
        public int HelpeeLastSnapshotReceivedModifiersMask;
        public int HelpeeLastSnapshotReceivedMouseButtonsMask;
        public long HelpeeLastSnapshotAppliedSeq;
        public int HelpeeLastSnapshotAppliedModifiersMask;
        public int HelpeeLastSnapshotAppliedMouseButtonsMask;
        public long HelperLastSnapshotSentSeq;
        public int HelperLastSnapshotSentModifiersMask;
        public int HelperLastSnapshotSentMouseButtonsMask;
        public int? HelperSnapshotSentPerSec;
        public long HelperLastAckSeq;
        public long? HelperLastAckAgeMs;
        public long HelperStallDetectedCount;
        public long HelperStallRecoverySentCount;
        public RemoteControlLastMapped? LastMapped;
        public long OutOfRangeClamps;
        public long DroppedMouseMoves;
        public long GuardrailSuppressedInjections;
        public long GuardrailQueueFlushes;
    }

    private static readonly RoleState HelperState = new();
    private static readonly RoleState HelpeeState = new();
#endif

    public static RemoteControlDebugSnapshot Snapshot(RemoteControlDiagnosticsRole role)
    {
#if DEBUG
        var state = GetState(role);
        lock (state.Gate)
        {
            return new RemoteControlDebugSnapshot(
                Role: role,
                ControlState: state.ControlState,
                DisplayId: string.IsNullOrWhiteSpace(state.DisplayId) ? "(none)" : state.DisplayId,
                DisplayRevision: state.DisplayRevision,
                CaptureRegionPx: state.CaptureRegionPx,
                FrameSizePx: state.FrameSizePx,
                ControlMode: state.ControlMode,
                MouseMoveSentPerSec: state.MouseMoveSentPerSec,
                MouseMoveDropped: state.MouseMoveDropped,
                InjectionQueueSize: state.InjectionQueueSize,
                SuppressedInjections: state.SuppressedInjections,
                QueueFlushes: state.QueueFlushes,
                LastInjectedSeq: state.LastInjectedSeq,
                LastAckSentSeq: state.LastAckSentSeq,
                AckSentCount: state.AckSentCount,
                SnapshotReceivedCount: state.SnapshotReceivedCount,
                SnapshotAppliedCount: state.SnapshotAppliedCount,
                SnapshotUnstuckButtonsCount: state.SnapshotUnstuckButtonsCount,
                SnapshotUnstuckModifiersCount: state.SnapshotUnstuckModifiersCount,
                HelpeeLastSnapshotReceivedSeq: state.HelpeeLastSnapshotReceivedSeq,
                HelpeeLastSnapshotReceivedModifiersMask: state.HelpeeLastSnapshotReceivedModifiersMask,
                HelpeeLastSnapshotReceivedMouseButtonsMask: state.HelpeeLastSnapshotReceivedMouseButtonsMask,
                HelpeeLastSnapshotAppliedSeq: state.HelpeeLastSnapshotAppliedSeq,
                HelpeeLastSnapshotAppliedModifiersMask: state.HelpeeLastSnapshotAppliedModifiersMask,
                HelpeeLastSnapshotAppliedMouseButtonsMask: state.HelpeeLastSnapshotAppliedMouseButtonsMask,
                HelperLastSnapshotSentSeq: state.HelperLastSnapshotSentSeq,
                HelperLastSnapshotSentModifiersMask: state.HelperLastSnapshotSentModifiersMask,
                HelperLastSnapshotSentMouseButtonsMask: state.HelperLastSnapshotSentMouseButtonsMask,
                HelperSnapshotSentPerSec: state.HelperSnapshotSentPerSec,
                HelperLastAckSeq: state.HelperLastAckSeq,
                HelperLastAckAgeMs: state.HelperLastAckAgeMs,
                HelperStallDetectedCount: state.HelperStallDetectedCount,
                HelperStallRecoverySentCount: state.HelperStallRecoverySentCount,
                LastMapped: state.LastMapped,
                OutOfRangeClamps: state.OutOfRangeClamps,
                DroppedMouseMoves: state.DroppedMouseMoves,
                GuardrailSuppressedInjections: state.GuardrailSuppressedInjections,
                GuardrailQueueFlushes: state.GuardrailQueueFlushes);
        }
#else
        return RemoteControlDebugSnapshot.Empty(role);
#endif
    }

    [Conditional("DEBUG")]
    public static void SetCommon(
        RemoteControlDiagnosticsRole role,
        ControlState controlState,
        string? displayId,
        long? displayRevision,
        RemoteControlRectPx? captureRegionPx,
        RemoteControlSizePx? frameSizePx)
    {
#if DEBUG
        var state = GetState(role);
        lock (state.Gate)
        {
            state.ControlState = controlState;
            state.DisplayId = string.IsNullOrWhiteSpace(displayId) ? "(none)" : displayId.Trim();
            state.DisplayRevision = displayRevision.GetValueOrDefault(0);
            state.CaptureRegionPx = captureRegionPx;
            state.FrameSizePx = frameSizePx;
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void SetHelperControlMode(bool controlMode)
    {
#if DEBUG
        lock (HelperState.Gate)
        {
            HelperState.ControlMode = controlMode;
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void SetHelperFrameSize(RemoteControlSizePx? frameSizePx)
    {
#if DEBUG
        lock (HelperState.Gate)
        {
            HelperState.FrameSizePx = frameSizePx;
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void SetHelperMouseMoveSentPerSec(int mouseMoveSentPerSec)
    {
#if DEBUG
        lock (HelperState.Gate)
        {
            HelperState.MouseMoveSentPerSec = Math.Max(0, mouseMoveSentPerSec);
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void IncrementHelperMouseMoveDropped()
    {
#if DEBUG
        lock (HelperState.Gate)
        {
            HelperState.MouseMoveDropped++;
            HelperState.DroppedMouseMoves++;
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void IncrementHelperOutOfRangeClamp()
    {
#if DEBUG
        lock (HelperState.Gate)
        {
            HelperState.OutOfRangeClamps++;
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void SetHelperGuardrailCounters(
        long outOfRangeClamps,
        long droppedMouseMoves,
        long suppressedInjections,
        long queueFlushes)
    {
#if DEBUG
        lock (HelperState.Gate)
        {
            HelperState.OutOfRangeClamps = Math.Max(0, outOfRangeClamps);
            HelperState.DroppedMouseMoves = Math.Max(0, droppedMouseMoves);
            HelperState.GuardrailSuppressedInjections = Math.Max(0, suppressedInjections);
            HelperState.GuardrailQueueFlushes = Math.Max(0, queueFlushes);
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void SetHelperAckRuntime(
        long lastAckSeq,
        long? lastAckAgeMs,
        long stallDetectedCount,
        long stallRecoverySentCount)
    {
#if DEBUG
        lock (HelperState.Gate)
        {
            HelperState.HelperLastAckSeq = Math.Max(0, lastAckSeq);
            HelperState.HelperLastAckAgeMs = lastAckAgeMs.HasValue ? Math.Max(0, lastAckAgeMs.Value) : null;
            HelperState.HelperStallDetectedCount = Math.Max(0, stallDetectedCount);
            HelperState.HelperStallRecoverySentCount = Math.Max(0, stallRecoverySentCount);
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void SetHelperSnapshotRuntime(
        long lastSentSeq,
        int lastSentModifiersMask,
        int lastSentMouseButtonsMask,
        int? sentPerSec)
    {
#if DEBUG
        lock (HelperState.Gate)
        {
            HelperState.HelperLastSnapshotSentSeq = Math.Max(0, lastSentSeq);
            HelperState.HelperLastSnapshotSentModifiersMask = lastSentModifiersMask;
            HelperState.HelperLastSnapshotSentMouseButtonsMask = lastSentMouseButtonsMask;
            HelperState.HelperSnapshotSentPerSec = sentPerSec.HasValue ? Math.Max(0, sentPerSec.Value) : null;
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void SetHelpeeRuntime(
        int? injectionQueueSize,
        long suppressedInjections,
        long queueFlushes,
        long lastInjectedSeq,
        long lastAckSentSeq,
        long ackSentCount,
        long snapshotReceivedCount = 0,
        long snapshotAppliedCount = 0,
        long snapshotUnstuckButtonsCount = 0,
        long snapshotUnstuckModifiersCount = 0)
    {
#if DEBUG
        lock (HelpeeState.Gate)
        {
            HelpeeState.InjectionQueueSize = injectionQueueSize;
            HelpeeState.SuppressedInjections = Math.Max(0, suppressedInjections);
            HelpeeState.QueueFlushes = Math.Max(0, queueFlushes);
            HelpeeState.LastInjectedSeq = Math.Max(0, lastInjectedSeq);
            HelpeeState.LastAckSentSeq = Math.Max(0, lastAckSentSeq);
            HelpeeState.AckSentCount = Math.Max(0, ackSentCount);
            HelpeeState.SnapshotReceivedCount = Math.Max(0, snapshotReceivedCount);
            HelpeeState.SnapshotAppliedCount = Math.Max(0, snapshotAppliedCount);
            HelpeeState.SnapshotUnstuckButtonsCount = Math.Max(0, snapshotUnstuckButtonsCount);
            HelpeeState.SnapshotUnstuckModifiersCount = Math.Max(0, snapshotUnstuckModifiersCount);
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void SetHelpeeSnapshotRuntime(
        long lastReceivedSeq,
        int lastReceivedModifiersMask,
        int lastReceivedMouseButtonsMask,
        long lastAppliedSeq,
        int lastAppliedModifiersMask,
        int lastAppliedMouseButtonsMask)
    {
#if DEBUG
        lock (HelpeeState.Gate)
        {
            HelpeeState.HelpeeLastSnapshotReceivedSeq = Math.Max(0, lastReceivedSeq);
            HelpeeState.HelpeeLastSnapshotReceivedModifiersMask = lastReceivedModifiersMask;
            HelpeeState.HelpeeLastSnapshotReceivedMouseButtonsMask = lastReceivedMouseButtonsMask;
            HelpeeState.HelpeeLastSnapshotAppliedSeq = Math.Max(0, lastAppliedSeq);
            HelpeeState.HelpeeLastSnapshotAppliedModifiersMask = lastAppliedModifiersMask;
            HelpeeState.HelpeeLastSnapshotAppliedMouseButtonsMask = lastAppliedMouseButtonsMask;
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void SetHelpeeLastMapped(double nx, double ny, int px, int py)
    {
#if DEBUG
        lock (HelpeeState.Gate)
        {
            HelpeeState.LastMapped = new RemoteControlLastMapped(nx, ny, px, py);
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void SetHelpeeGuardrailCounters(
        long outOfRangeClamps,
        long droppedMouseMoves,
        long suppressedInjections,
        long queueFlushes)
    {
#if DEBUG
        lock (HelpeeState.Gate)
        {
            HelpeeState.OutOfRangeClamps = Math.Max(0, outOfRangeClamps);
            HelpeeState.DroppedMouseMoves = Math.Max(0, droppedMouseMoves);
            HelpeeState.GuardrailSuppressedInjections = Math.Max(0, suppressedInjections);
            HelpeeState.GuardrailQueueFlushes = Math.Max(0, queueFlushes);
        }
#endif
    }

#if DEBUG
    private static RoleState GetState(RemoteControlDiagnosticsRole role)
    {
        return role == RemoteControlDiagnosticsRole.Helper ? HelperState : HelpeeState;
    }
#endif
}
