namespace NLink.Core.FileTransfer;

public interface IFileTransferRouteStatus
{
    bool IsFileTunaActiveForRouteSelection { get; }

    bool IsPostTunaFileFallbackActiveForRouteSelection { get; }

    bool IsDiagnosticRegularNknV6RouteEnabled { get; }
}

internal enum FileTransferRoute
{
    RegularNknV4Fast,
    FileTunaV4,
    PostTunaFallbackV6,
    DiagnosticRegularNknV6,
}

internal enum FileTransferFrameFamily
{
    V4,
    V6,
}

internal enum FileTransferRouteRuntimeProfile
{
    RegularNknV4Fast,
    FileTunaV4Fast,
    DefaultV6,
    PrimaryRegularNknBulkV6,
}

internal enum FileTransferRouteSenderPumpFamily
{
    V4SparseCredit,
    V6RequestDriven,
    V6SparseCredit,
}

internal enum FileTransferRouteReceiverPumpFamily
{
    V4SparseCredit,
    V6RequestDriven,
    V6SparseCredit,
}

internal enum FileTransferRouteFeedbackEnvelopeFamily
{
    V4,
    V6,
}

internal enum FileTransferRouteBridgeRecoveryPolicy
{
    RegularNknV4Fast,
    TunaStrictRecovery,
    PostTunaFallbackStrictRecovery,
    PrimaryRegularNknQuietRecovery,
}

internal enum FileTransferRouteLivenessTerminalPolicy
{
    RegularNknV4Fast,
    FileTunaV4Fast,
    PostTunaFallbackV6Repair,
    DiagnosticRegularNknV6,
}

internal readonly record struct FileTransferRouteResolverInput(
    bool IsFileTunaActive,
    bool IsPostTunaFileFallbackActive,
    bool IsDiagnosticRegularNknV6RouteEnabled,
    FileTransferTransportHandoffKind HandoffKind,
    FileTransferTransportProfileKind TransportProfileKind)
{
    public static FileTransferRouteResolverInput RegularNkn { get; } = new(
        IsFileTunaActive: false,
        IsPostTunaFileFallbackActive: false,
        IsDiagnosticRegularNknV6RouteEnabled: false,
        HandoffKind: FileTransferTransportHandoffKind.None,
        TransportProfileKind: FileTransferTransportProfileKind.Default);

    public static FileTransferRouteResolverInput FromTransport(IFileTransferSignalingTransport? transport)
    {
        var transportProfileKind = transport is IFileTransferTransportProfileProvider profileProvider
            ? profileProvider.FileTransferTransportProfileKind
            : FileTransferTransportProfileKind.Default;

        if (transport is IFileTransferRouteStatus routeStatus)
        {
            return new FileTransferRouteResolverInput(
                routeStatus.IsFileTunaActiveForRouteSelection,
                routeStatus.IsPostTunaFileFallbackActiveForRouteSelection,
                routeStatus.IsDiagnosticRegularNknV6RouteEnabled,
                FileTransferTransportHandoffKind.None,
                transportProfileKind);
        }

        return new FileTransferRouteResolverInput(
            IsFileTunaActive: false,
            IsPostTunaFileFallbackActive: false,
            IsDiagnosticRegularNknV6RouteEnabled: false,
            HandoffKind: FileTransferTransportHandoffKind.None,
            TransportProfileKind: transportProfileKind);
    }
}

internal readonly record struct FileTransferRouteSelection(
    FileTransferRoute Route,
    string TelemetryToken,
    int ProtocolVersion,
    FileTransferRouteRuntimeProfile RuntimeProfile,
    FileTransferFrameFamily FrameFamily,
    FileTransferTransportHandoffKind HandoffKind,
    FileTransferRouteBridgeRecoveryPolicy BridgeRecoveryPolicy,
    FileTransferRouteLivenessTerminalPolicy LivenessTerminalPolicy,
    string SelectionReason)
{
    public FileTransferRouteRuntimeDescriptor RuntimeDescriptor =>
        FileTransferRouteRuntimeDescriptor.FromSelection(this);
}

internal readonly record struct FileTransferRouteRuntimeDescriptor(
    FileTransferRoute Route,
    string TelemetryToken,
    int ProtocolVersion,
    FileTransferRouteRuntimeProfile RuntimeProfile,
    FileTransferFrameFamily FrameFamily,
    FileTransferRouteSenderPumpFamily SenderPumpFamily,
    FileTransferRouteReceiverPumpFamily ReceiverPumpFamily,
    FileTransferRouteFeedbackEnvelopeFamily FeedbackEnvelopeFamily,
    FileTransferRouteBridgeRecoveryPolicy BridgeRecoveryPolicy,
    FileTransferRouteLivenessTerminalPolicy LivenessTerminalPolicy,
    bool IsDiagnosticOnly,
    bool AllowsPostTunaFallbackRecovery)
{
    public static FileTransferRouteRuntimeDescriptor FromSelection(FileTransferRouteSelection selection)
        => selection.Route switch
        {
            FileTransferRoute.FileTunaV4 => new FileTransferRouteRuntimeDescriptor(
                selection.Route,
                selection.TelemetryToken,
                selection.ProtocolVersion,
                selection.RuntimeProfile,
                selection.FrameFamily,
                FileTransferRouteSenderPumpFamily.V4SparseCredit,
                FileTransferRouteReceiverPumpFamily.V4SparseCredit,
                FileTransferRouteFeedbackEnvelopeFamily.V4,
                selection.BridgeRecoveryPolicy,
                selection.LivenessTerminalPolicy,
                IsDiagnosticOnly: false,
                AllowsPostTunaFallbackRecovery: false),
            FileTransferRoute.PostTunaFallbackV6 => new FileTransferRouteRuntimeDescriptor(
                selection.Route,
                selection.TelemetryToken,
                selection.ProtocolVersion,
                selection.RuntimeProfile,
                selection.FrameFamily,
                FileTransferRouteSenderPumpFamily.V6RequestDriven,
                FileTransferRouteReceiverPumpFamily.V6RequestDriven,
                FileTransferRouteFeedbackEnvelopeFamily.V6,
                selection.BridgeRecoveryPolicy,
                selection.LivenessTerminalPolicy,
                IsDiagnosticOnly: false,
                AllowsPostTunaFallbackRecovery: true),
            FileTransferRoute.DiagnosticRegularNknV6 => new FileTransferRouteRuntimeDescriptor(
                selection.Route,
                selection.TelemetryToken,
                selection.ProtocolVersion,
                selection.RuntimeProfile,
                selection.FrameFamily,
                FileTransferRouteSenderPumpFamily.V6SparseCredit,
                FileTransferRouteReceiverPumpFamily.V6SparseCredit,
                FileTransferRouteFeedbackEnvelopeFamily.V6,
                selection.BridgeRecoveryPolicy,
                selection.LivenessTerminalPolicy,
                IsDiagnosticOnly: true,
                AllowsPostTunaFallbackRecovery: false),
            _ => new FileTransferRouteRuntimeDescriptor(
                FileTransferRoute.RegularNknV4Fast,
                selection.TelemetryToken,
                selection.ProtocolVersion,
                selection.RuntimeProfile,
                selection.FrameFamily,
                FileTransferRouteSenderPumpFamily.V4SparseCredit,
                FileTransferRouteReceiverPumpFamily.V4SparseCredit,
                FileTransferRouteFeedbackEnvelopeFamily.V4,
                selection.BridgeRecoveryPolicy,
                selection.LivenessTerminalPolicy,
                IsDiagnosticOnly: false,
                AllowsPostTunaFallbackRecovery: false),
        };

    public bool UsesRegularNknV4FastRuntime => Route == FileTransferRoute.RegularNknV4Fast;

    public bool UsesFileTunaV4Runtime => Route == FileTransferRoute.FileTunaV4;

    public bool UsesPostTunaFallbackV6Runtime => Route == FileTransferRoute.PostTunaFallbackV6;

    public bool UsesDiagnosticRegularNknV6Runtime => Route == FileTransferRoute.DiagnosticRegularNknV6;

    public bool UsesV4SparsePump => SenderPumpFamily == FileTransferRouteSenderPumpFamily.V4SparseCredit;

    public bool UsesV6RequestPump => SenderPumpFamily == FileTransferRouteSenderPumpFamily.V6RequestDriven;

    public bool UsesV6SparsePump => SenderPumpFamily == FileTransferRouteSenderPumpFamily.V6SparseCredit;

    public bool UsesV6FeedbackEnvelope => FeedbackEnvelopeFamily == FileTransferRouteFeedbackEnvelopeFamily.V6;
}

internal static class FileTransferRouteResolver
{
    public const string RegularNknV4FastToken = "regular_nkn_v4_fast";
    public const string FileTunaV4Token = "file_tuna_v4";
    public const string PostTunaFallbackV6Token = "post_tuna_fallback_v6";
    public const string DiagnosticRegularNknV6Token = "diagnostic_regular_nkn_v6";

    public static FileTransferRouteSelection Resolve(FileTransferRouteResolverInput input)
    {
        if (input.IsPostTunaFileFallbackActive)
        {
            return new FileTransferRouteSelection(
                FileTransferRoute.PostTunaFallbackV6,
                PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                FileTransferRouteRuntimeProfile.DefaultV6,
                FileTransferFrameFamily.V6,
                NormalizeHandoffKind(input.HandoffKind, FileTransferTransportHandoffKind.TunaToNormalFallback),
                FileTransferRouteBridgeRecoveryPolicy.PostTunaFallbackStrictRecovery,
                FileTransferRouteLivenessTerminalPolicy.PostTunaFallbackV6Repair,
                "post_tuna_file_fallback_active");
        }

        if (input.IsFileTunaActive)
        {
            return new FileTransferRouteSelection(
                FileTransferRoute.FileTunaV4,
                FileTunaV4Token,
                FileTransferProtocol.ProtocolVersionV4,
                FileTransferRouteRuntimeProfile.FileTunaV4Fast,
                FileTransferFrameFamily.V4,
                input.HandoffKind,
                FileTransferRouteBridgeRecoveryPolicy.TunaStrictRecovery,
                FileTransferRouteLivenessTerminalPolicy.FileTunaV4Fast,
                "file_tuna_active");
        }

        if (input.IsDiagnosticRegularNknV6RouteEnabled)
        {
            return new FileTransferRouteSelection(
                FileTransferRoute.DiagnosticRegularNknV6,
                DiagnosticRegularNknV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                FileTransferRouteRuntimeProfile.PrimaryRegularNknBulkV6,
                FileTransferFrameFamily.V6,
                input.HandoffKind,
                FileTransferRouteBridgeRecoveryPolicy.PrimaryRegularNknQuietRecovery,
                FileTransferRouteLivenessTerminalPolicy.DiagnosticRegularNknV6,
                "diagnostic_regular_nkn_v6_opt_in");
        }

        return new FileTransferRouteSelection(
            FileTransferRoute.RegularNknV4Fast,
            RegularNknV4FastToken,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferRouteRuntimeProfile.RegularNknV4Fast,
            FileTransferFrameFamily.V4,
            input.HandoffKind,
            FileTransferRouteBridgeRecoveryPolicy.RegularNknV4Fast,
            FileTransferRouteLivenessTerminalPolicy.RegularNknV4Fast,
            "regular_nkn_default_v4");
    }

    public static FileTransferRouteSelection Resolve(FileTransferRoute route)
        => route switch
        {
            FileTransferRoute.FileTunaV4 => Resolve(new FileTransferRouteResolverInput(
                IsFileTunaActive: true,
                IsPostTunaFileFallbackActive: false,
                IsDiagnosticRegularNknV6RouteEnabled: false,
                HandoffKind: FileTransferTransportHandoffKind.None,
                TransportProfileKind: FileTransferTransportProfileKind.Default)),
            FileTransferRoute.PostTunaFallbackV6 => Resolve(new FileTransferRouteResolverInput(
                IsFileTunaActive: false,
                IsPostTunaFileFallbackActive: true,
                IsDiagnosticRegularNknV6RouteEnabled: false,
                HandoffKind: FileTransferTransportHandoffKind.None,
                TransportProfileKind: FileTransferTransportProfileKind.Default)),
            FileTransferRoute.DiagnosticRegularNknV6 => Resolve(new FileTransferRouteResolverInput(
                IsFileTunaActive: false,
                IsPostTunaFileFallbackActive: false,
                IsDiagnosticRegularNknV6RouteEnabled: true,
                HandoffKind: FileTransferTransportHandoffKind.None,
                TransportProfileKind: FileTransferTransportProfileKind.ConservativeNknStartup)),
            _ => Resolve(FileTransferRouteResolverInput.RegularNkn),
        };

    public static bool TryNormalizeTelemetryToken(string? value, int protocolVersion, out string? normalizedToken)
    {
        normalizedToken = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var candidate = value.Trim();
        if (!TryParseTelemetryToken(candidate, out var route))
        {
            return false;
        }

        var selection = Resolve(route);
        if (selection.ProtocolVersion != protocolVersion)
        {
            return false;
        }

        normalizedToken = selection.TelemetryToken;
        return true;
    }

    public static bool TryParseTelemetryToken(string? value, out FileTransferRoute route)
    {
        route = FileTransferRoute.RegularNknV4Fast;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case RegularNknV4FastToken:
                route = FileTransferRoute.RegularNknV4Fast;
                return true;
            case FileTunaV4Token:
                route = FileTransferRoute.FileTunaV4;
                return true;
            case PostTunaFallbackV6Token:
                route = FileTransferRoute.PostTunaFallbackV6;
                return true;
            case DiagnosticRegularNknV6Token:
                route = FileTransferRoute.DiagnosticRegularNknV6;
                return true;
            default:
                return false;
        }
    }

    private static FileTransferTransportHandoffKind NormalizeHandoffKind(
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportHandoffKind fallback)
        => handoffKind == FileTransferTransportHandoffKind.None
            ? fallback
            : handoffKind;
}
