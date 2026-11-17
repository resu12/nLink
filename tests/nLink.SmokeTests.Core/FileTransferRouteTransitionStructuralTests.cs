namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferRouteTransitionStructuralTests
{
    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public void FileTransferRouteTransition_LegacyFallbackAuthorityLayersAreAbsent()
    {
        var source = ReadProductionFileTransferSource();

        Assert.DoesNotContain(
            "RuntimeUnlockUncommittedTunaFallback",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IRuntimeUnlockUncommittedTunaFallbackAdoptionBridge",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RuntimeUnlockPostTunaFallbackActivationIntent",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IRegularV4ToTunaRouteTransitionBridge",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RegularV4ToTunaRouteTransitionSubstate",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PendingRuntimeUnlockRegularV4RouteCommitDrain",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsPostTunaFallbackRuntimeUnlockRecoveryReason",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AllowsPostTunaFallbackRuntimeUnlockDispatch",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "filetransfer_post_tuna_fallback_v6_route_consumed",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "LastOutboundRegularV4PhysicalSendLeaseGeneration",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ActiveOutboundRegularV4PhysicalSendLeases",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OutboundRegularV4PhysicalSendLease",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RuntimeUnlockUncommittedFallbackFalloutSuppression",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ShouldSuppressUncommittedRuntimeUnlockFileFallback",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "StartTunaFallbackProofAndRebindIfAllowed",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ShouldBypassActiveFileTunaSuppressionForPostTunaFallbackRecovery",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ShouldSuppressUncommittedPostTunaFallbackHandoff",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RebindFileTransferDataSessionsForTunaFallback",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public void FileTransferRouteTransition_DrainingSupersessionIsAcceptedByBothCoreDirections()
    {
        var sourcePath = Path.Combine(
            CoreSmokeTestsBase.FindRepoRoot(),
            "src",
            "nLink.Core",
            "FileTransfer",
            "SessionFileTransferService.RouteTransitionFallback.cs");
        var source = File.ReadAllText(sourcePath);
        const string pattern =
            @"committedSupersession\.Status\s+is\s+not\s+\(\s*" +
            @"FileTransferRouteTransitionDecisionStatus\.Prepared\s+or\s+" +
            @"FileTransferRouteTransitionDecisionStatus\.Draining\s+or\s+" +
            @"FileTransferRouteTransitionDecisionStatus\.Idempotent\s*\)";

        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(source, pattern).Count);
        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(
                source,
                @"State:\s+FileTransferRouteTransitionState\.Prepared,\s+" +
                @"DrainCommand:\s+null,\s*\}\)\s*\{[\s\S]*?" +
                @"BeginFileTransferRouteTransitionPhysicalDrain\(").Count);
    }

    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public void FileTransferRouteTransition_NewerPeerBoundaryInvalidatesBothDirections()
    {
        var sourcePath = Path.Combine(
            CoreSmokeTestsBase.FindRepoRoot(),
            "src",
            "nLink.Core",
            "FileTransfer",
            "SessionFileTransferService.RouteTransitionFallback.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Equal(
            3,
            System.Text.RegularExpressions.Regex.Matches(
                source,
                @"TryInvalidateOutboundFileTransferRouteTransitionForPeerBoundaryReplacementLocked\(")
                .Count);
        Assert.Equal(
            3,
            System.Text.RegularExpressions.Regex.Matches(
                source,
                @"TryInvalidateInboundFileTransferRouteTransitionForPeerBoundaryReplacementLocked\(")
                .Count);
        Assert.Contains(
            "boundary.PeerProofEvidence.CanBeReplacedBy(evidence)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "frameIdentity.TransportEpoch >=",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public void FileTransferRouteTransitionCompatibilityProjection_IsCoordinatorBoundAndEvidenceOnly()
    {
        var sourcePath = Path.Combine(
            CoreSmokeTestsBase.FindRepoRoot(),
            "src",
            "nLink.Core",
            "FileTransfer",
            "SessionFileTransferService.RouteTransitionFallback.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains(
            "StartedLiveRouteEpoch = routeDecision.StartedLiveRouteEpoch",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RecoveredLiveRouteEpoch = routeDecision.RecoveredLiveRouteEpoch",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "projection_source=coordinator_route_transition",
            ReadProductionFileTransferSource(),
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(
                source,
                @"LogLiveRouteEpochRecovered\([\s\S]*?evidenceOnly:\s*true\);")
                .Count);
        var projectionStart = source.IndexOf(
            "EmitOutboundFileTransferRouteTransitionCompatibilityProjectionsLocked",
            StringComparison.Ordinal);
        var projectionEnd = source.IndexOf(
            "TryApplyFileTransferRouteTransitionPressureSettlement",
            projectionStart,
            StringComparison.Ordinal);
        Assert.True(projectionStart >= 0);
        Assert.True(projectionEnd > projectionStart);
        Assert.DoesNotContain(
            "TransitionToRoute(",
            source[projectionStart..projectionEnd],
            StringComparison.Ordinal);
    }

    private static string ReadProductionFileTransferSource()
    {
        var sourceRoot = Path.Combine(CoreSmokeTestsBase.FindRepoRoot(), "src");
        return string.Join(
            "\n",
            Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path =>
                    path.Contains(
                        $"{Path.DirectorySeparatorChar}FileTransfer{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Contains(
                        "NknSignalingTransport",
                        StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Equals(
                        "RealNknClientAdapter.cs",
                        StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(path).Equals(
                        "DevLocalTransport.cs",
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));
    }
}
