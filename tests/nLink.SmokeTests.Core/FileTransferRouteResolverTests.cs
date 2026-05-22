using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRouteResolverTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public void Resolve_TunaDisabled_SelectsRegularNknV4Fast()
    {
        var selection = FileTransferRouteResolver.Resolve(FileTransferRouteResolverInput.RegularNkn);

        AssertRegularNknV4Fast(selection, "regular_nkn_default_v4");
    }

    [Fact]
    public void Resolve_TunaEnabledButInactive_SelectsRegularNknV4Fast()
    {
        using var transport = new LoopbackFileTransferTransport("session_route_tuna_inactive")
        {
            TransportAccelerationStatusReason = "test_tuna_configured_inactive",
        };

        var selection = ResolveFromTransport(transport);

        AssertRegularNknV4Fast(selection, "regular_nkn_default_v4");
    }

    [Fact]
    public void Resolve_ScreenShareAccelerationOnly_SelectsRegularNknV4Fast()
    {
        using var transport = new LoopbackFileTransferTransport("session_route_screen_only")
        {
            IsTransportAccelerationActive = true,
            ShouldUseFileTransferV6ForAcceleration = false,
            TransportAccelerationStatusReason = "test_screen_tuna_active_file_regular_nkn",
        };

        var selection = ResolveFromTransport(transport);

        AssertRegularNknV4Fast(selection, "regular_nkn_default_v4");
    }

    [Fact]
    public void Resolve_FailedTunaActivationWithoutFallback_SelectsRegularNknV4Fast()
    {
        using var transport = new LoopbackFileTransferTransport("session_route_tuna_failed")
        {
            IsTransportAccelerationActive = false,
            ShouldUseFileTransferV6ForAcceleration = false,
            TransportAccelerationStatusReason = "test_tuna_activation_failed",
        };

        var selection = ResolveFromTransport(transport);

        AssertRegularNknV4Fast(selection, "regular_nkn_default_v4");
    }

    [Fact]
    public void Resolve_ActiveFileTuna_SelectsFileTunaV4()
    {
        var selection = FileTransferRouteResolver.Resolve(new FileTransferRouteResolverInput(
            IsFileTunaActive: true,
            IsPostTunaFileFallbackActive: false,
            IsDiagnosticRegularNknV6RouteEnabled: false,
            HandoffKind: FileTransferTransportHandoffKind.None,
            TransportProfileKind: FileTransferTransportProfileKind.Default));

        Assert.Equal(FileTransferRoute.FileTunaV4, selection.Route);
        Assert.Equal("file_tuna_v4", selection.TelemetryToken);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, selection.ProtocolVersion);
        Assert.Equal(FileTransferRouteRuntimeProfile.FileTunaV4Fast, selection.RuntimeProfile);
        Assert.Equal(FileTransferFrameFamily.V4, selection.FrameFamily);
        Assert.Equal(FileTransferTransportHandoffKind.None, selection.HandoffKind);
        Assert.Equal(FileTransferRouteBridgeRecoveryPolicy.TunaStrictRecovery, selection.BridgeRecoveryPolicy);
        Assert.Equal(FileTransferRouteLivenessTerminalPolicy.FileTunaV4Fast, selection.LivenessTerminalPolicy);
        Assert.Equal("file_tuna_active", selection.SelectionReason);
    }

    [Fact]
    public void Resolve_PostTunaFallback_SelectsPostTunaFallbackV6()
    {
        var selection = FileTransferRouteResolver.Resolve(new FileTransferRouteResolverInput(
            IsFileTunaActive: false,
            IsPostTunaFileFallbackActive: true,
            IsDiagnosticRegularNknV6RouteEnabled: false,
            HandoffKind: FileTransferTransportHandoffKind.None,
            TransportProfileKind: FileTransferTransportProfileKind.Default));

        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, selection.Route);
        Assert.Equal("post_tuna_fallback_v6", selection.TelemetryToken);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, selection.ProtocolVersion);
        Assert.Equal(FileTransferRouteRuntimeProfile.DefaultV6, selection.RuntimeProfile);
        Assert.Equal(FileTransferFrameFamily.V6, selection.FrameFamily);
        Assert.Equal(FileTransferTransportHandoffKind.TunaToNormalFallback, selection.HandoffKind);
        Assert.Equal(FileTransferRouteBridgeRecoveryPolicy.PostTunaFallbackStrictRecovery, selection.BridgeRecoveryPolicy);
        Assert.Equal(FileTransferRouteLivenessTerminalPolicy.PostTunaFallbackV6Repair, selection.LivenessTerminalPolicy);
        Assert.Equal("post_tuna_file_fallback_active", selection.SelectionReason);
    }

    [Fact]
    public void Resolve_DiagnosticRegularNknV6_RequiresExplicitOptIn()
    {
        var defaultSelection = FileTransferRouteResolver.Resolve(FileTransferRouteResolverInput.RegularNkn);
        var diagnosticSelection = FileTransferRouteResolver.Resolve(new FileTransferRouteResolverInput(
            IsFileTunaActive: false,
            IsPostTunaFileFallbackActive: false,
            IsDiagnosticRegularNknV6RouteEnabled: true,
            HandoffKind: FileTransferTransportHandoffKind.None,
            TransportProfileKind: FileTransferTransportProfileKind.ConservativeNknStartup));

        Assert.Equal(FileTransferRoute.RegularNknV4Fast, defaultSelection.Route);
        Assert.Equal(FileTransferRoute.DiagnosticRegularNknV6, diagnosticSelection.Route);
        Assert.Equal("diagnostic_regular_nkn_v6", diagnosticSelection.TelemetryToken);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, diagnosticSelection.ProtocolVersion);
        Assert.Equal(FileTransferRouteRuntimeProfile.PrimaryRegularNknBulkV6, diagnosticSelection.RuntimeProfile);
        Assert.Equal(FileTransferFrameFamily.V6, diagnosticSelection.FrameFamily);
        Assert.Equal(FileTransferRouteBridgeRecoveryPolicy.PrimaryRegularNknQuietRecovery, diagnosticSelection.BridgeRecoveryPolicy);
        Assert.Equal(FileTransferRouteLivenessTerminalPolicy.DiagnosticRegularNknV6, diagnosticSelection.LivenessTerminalPolicy);
        Assert.Equal("diagnostic_regular_nkn_v6_opt_in", diagnosticSelection.SelectionReason);
    }

    [Fact]
    public void Resolve_PostTunaFallback_TakesPrecedenceOverActiveFileTuna()
    {
        var selection = FileTransferRouteResolver.Resolve(new FileTransferRouteResolverInput(
            IsFileTunaActive: true,
            IsPostTunaFileFallbackActive: true,
            IsDiagnosticRegularNknV6RouteEnabled: true,
            HandoffKind: FileTransferTransportHandoffKind.None,
            TransportProfileKind: FileTransferTransportProfileKind.ConservativeNknStartup));

        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, selection.Route);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, selection.ProtocolVersion);
        Assert.Equal("post_tuna_fallback_v6", selection.TelemetryToken);
        Assert.Equal(FileTransferTransportHandoffKind.TunaToNormalFallback, selection.HandoffKind);
    }

    [Fact]
    public void Resolve_FromTransport_UsesExplicitFileRouteStatus()
    {
        using var activeTunaTransport = new LoopbackFileTransferTransport("session_route_transport_tuna")
        {
            IsFileTunaActiveForRouteSelection = true,
            IsTransportAccelerationActive = true,
            TransportAccelerationStatusReason = "test_file_tuna_active",
        };
        using var fallbackTransport = new LoopbackFileTransferTransport("session_route_transport_fallback")
        {
            IsFileTunaActiveForRouteSelection = true,
            IsPostTunaFileFallbackActiveForRouteSelection = true,
            TransportAccelerationStatusReason = "test_file_regular_nkn_fallback",
        };

        Assert.Equal(FileTransferRoute.FileTunaV4, ResolveFromTransport(activeTunaTransport).Route);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, ResolveFromTransport(fallbackTransport).Route);
    }

    private static FileTransferRouteSelection ResolveFromTransport(IFileTransferSignalingTransport transport)
        => FileTransferRouteResolver.Resolve(FileTransferRouteResolverInput.FromTransport(transport));

    private static void AssertRegularNknV4Fast(FileTransferRouteSelection selection, string reason)
    {
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, selection.Route);
        Assert.Equal("regular_nkn_v4_fast", selection.TelemetryToken);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, selection.ProtocolVersion);
        Assert.Equal(FileTransferRouteRuntimeProfile.RegularNknV4Fast, selection.RuntimeProfile);
        Assert.Equal(FileTransferFrameFamily.V4, selection.FrameFamily);
        Assert.Equal(FileTransferTransportHandoffKind.None, selection.HandoffKind);
        Assert.Equal(FileTransferRouteBridgeRecoveryPolicy.RegularNknV4Fast, selection.BridgeRecoveryPolicy);
        Assert.Equal(FileTransferRouteLivenessTerminalPolicy.RegularNknV4Fast, selection.LivenessTerminalPolicy);
        Assert.Equal(reason, selection.SelectionReason);
    }
}
