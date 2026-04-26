namespace NLink.Core.ScreenShare;

public sealed record ScreenShareCursorStateV1
{
    public string Kind { get; init; } = ScreenShareCursorStateProtocol.Kind;

    public string Type { get; init; } = ScreenShareCursorStateProtocol.CursorStateTypeV1;

    public string SessionId { get; init; } = string.Empty;

    public long Seq { get; init; }

    public long TsUtcMs { get; init; }

    public string DisplayId { get; init; } = string.Empty;

    public long DisplayInfoRevision { get; init; }

    public double Nx { get; init; }

    public double Ny { get; init; }

    public bool Visible { get; init; }

    public string Source { get; init; } = "os_cursor";

    public string Status { get; init; } = "captured_cursor_disabled";

    public bool CapturedCursorEnabled { get; init; }

    public bool CursorCaptureControlSupported { get; init; }
}

public static class ScreenShareCursorStateProtocol
{
    public const string Kind = "screenshare";
    public const string CursorStateTypeV1 = "screenshare.cursor_state.v1";
}

public interface IScreenShareCursorOverlayCapabilityProvider
{
    bool LocalSupportsScreenShareCursorOverlay { get; }

    bool RemoteSupportsScreenShareCursorOverlay { get; }

    bool SessionSupportsScreenShareCursorOverlay { get; }
}

