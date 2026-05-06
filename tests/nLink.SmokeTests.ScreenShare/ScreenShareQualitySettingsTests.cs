using NLink.App.Configuration;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareQualitySettingsTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void ApplyStartupMigrationIfNeeded_ExactLegacyHigherClarityTuple_MigratesToTextFirstPreset()
    {
        using var captureFpsOverride = new EnvironmentOverride(ScreenShareQualitySettings.ScreenShareMaxFpsVariable, "20");
        using var transportFpsOverride = new EnvironmentOverride(ScreenShareQualitySettings.ScreenShareTransportMaxFpsVariable, "8");
        using var scaleOverride = new EnvironmentOverride(ScreenShareQualitySettings.ScreenShareScaleVariable, "0.85");
        using var profileOverride = new EnvironmentOverride(
            ScreenShareQualitySettings.ScreenShareQualityProfileVariable,
            FeatureFlags.ScreenShareQualityProfileNormal);
        ScreenShareQualitySettings.ResetMigrationStateForTests();

        try
        {
            var migrated = ScreenShareQualitySettings.ApplyStartupMigrationIfNeeded(persistUserEnvironment: false);

            Assert.Equal(15, migrated.CaptureFramesPerSecond);
            Assert.Equal(8, migrated.TransportFramesPerSecond);
            Assert.Equal(1d, migrated.CaptureScale);
            Assert.Equal(FeatureFlags.ScreenShareQualityProfileNormal, migrated.QualityProfile);
            Assert.Equal(ScreenShareQualitySettings.BalancedPreset.Key, migrated.EffectivePresetKey);
            Assert.Equal(ScreenShareQualitySettings.BalancedPreset.DisplayName, migrated.EffectivePresetName);
            Assert.True(migrated.LegacyHigherClarityPresetMigrated);
            Assert.True(ScreenShareQualitySettings.HasPendingUserEnvironmentPersistence);
            Assert.Equal("15", Environment.GetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareMaxFpsVariable));
            Assert.Equal("8", Environment.GetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareTransportMaxFpsVariable));
            Assert.Equal("1", Environment.GetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareScaleVariable));
            Assert.Equal(
                FeatureFlags.ScreenShareQualityProfileNormal,
                Environment.GetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareQualityProfileVariable));
        }
        finally
        {
            ScreenShareQualitySettings.ResetMigrationStateForTests();
        }
    }

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("balanced", "Balanced", 15, 8, 1d, FeatureFlags.ScreenShareQualityProfileNormal)]
    [InlineData("high_quality", "High quality", 24, 15, 1d, FeatureFlags.ScreenShareQualityProfileNormal)]
    [InlineData("tuna_quality", "Tuna quality", 30, 15, 1d, FeatureFlags.ScreenShareQualityProfileTunaQuality)]
    [InlineData("high_performance", "High performance", 10, 6, 0.6d, FeatureFlags.ScreenShareQualityProfileNormal)]
    public void GetCurrentEnvironmentState_KnownPresetTuples_ResolveToNamedProfiles(
        string expectedKey,
        string expectedName,
        int captureFramesPerSecond,
        int transportFramesPerSecond,
        double captureScale,
        string qualityProfile)
    {
        using var captureFpsOverride = new EnvironmentOverride(
            ScreenShareQualitySettings.ScreenShareMaxFpsVariable,
            captureFramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var transportFpsOverride = new EnvironmentOverride(
            ScreenShareQualitySettings.ScreenShareTransportMaxFpsVariable,
            transportFramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var scaleOverride = new EnvironmentOverride(
            ScreenShareQualitySettings.ScreenShareScaleVariable,
            captureScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        using var profileOverride = new EnvironmentOverride(
            ScreenShareQualitySettings.ScreenShareQualityProfileVariable,
            qualityProfile);
        ScreenShareQualitySettings.ResetMigrationStateForTests();

        try
        {
            var current = ScreenShareQualitySettings.GetCurrentEnvironmentState();

            Assert.Equal(captureFramesPerSecond, current.CaptureFramesPerSecond);
            Assert.Equal(transportFramesPerSecond, current.TransportFramesPerSecond);
            Assert.Equal(captureScale, current.CaptureScale);
            Assert.Equal(qualityProfile, current.QualityProfile);
            Assert.Equal(expectedKey, current.EffectivePresetKey);
            Assert.Equal(expectedName, current.EffectivePresetName);
        }
        finally
        {
            ScreenShareQualitySettings.ResetMigrationStateForTests();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void FeatureFlags_ScreenShareTransportMaxFps_AllowsTunaQualityCapAndClampsAboveIt()
    {
        using var tunaQualityOverride = new EnvironmentOverride(
            ScreenShareQualitySettings.ScreenShareTransportMaxFpsVariable,
            "15");

        Assert.Equal(15, FeatureFlags.ScreenShareTransportMaxFps);

        Environment.SetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareTransportMaxFpsVariable, "30");

        Assert.Equal(15, FeatureFlags.ScreenShareTransportMaxFps);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareProfiles_DoNotExceedTransportPipelineCap()
    {
        Assert.True(ScreenShareQualitySettings.BalancedPreset.TransportFramesPerSecond <= ScreenShareFrameSendPipeline.MaxFramesPerSecond);
        Assert.True(ScreenShareQualitySettings.HighQualityPreset.TransportFramesPerSecond <= ScreenShareFrameSendPipeline.MaxFramesPerSecond);
        Assert.True(ScreenShareQualitySettings.TunaQualityPreset.TransportFramesPerSecond <= ScreenShareFrameSendPipeline.MaxFramesPerSecond);
        Assert.True(ScreenShareQualitySettings.HighPerformancePreset.TransportFramesPerSecond <= ScreenShareFrameSendPipeline.MaxFramesPerSecond);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void GetCurrentEnvironmentState_TunaQualityTupleWithNormalProfile_ResolvesToCustom()
    {
        using var captureFpsOverride = new EnvironmentOverride(ScreenShareQualitySettings.ScreenShareMaxFpsVariable, "30");
        using var transportFpsOverride = new EnvironmentOverride(ScreenShareQualitySettings.ScreenShareTransportMaxFpsVariable, "15");
        using var scaleOverride = new EnvironmentOverride(ScreenShareQualitySettings.ScreenShareScaleVariable, "1");
        using var profileOverride = new EnvironmentOverride(
            ScreenShareQualitySettings.ScreenShareQualityProfileVariable,
            FeatureFlags.ScreenShareQualityProfileNormal);
        ScreenShareQualitySettings.ResetMigrationStateForTests();

        try
        {
            var current = ScreenShareQualitySettings.GetCurrentEnvironmentState();

            Assert.Equal("custom", current.EffectivePresetKey);
            Assert.Equal("Custom", current.EffectivePresetName);
        }
        finally
        {
            ScreenShareQualitySettings.ResetMigrationStateForTests();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ApplyStartupMigrationIfNeeded_CustomTuple_PreservesExistingSettings()
    {
        using var captureFpsOverride = new EnvironmentOverride(ScreenShareQualitySettings.ScreenShareMaxFpsVariable, "12");
        using var transportFpsOverride = new EnvironmentOverride(ScreenShareQualitySettings.ScreenShareTransportMaxFpsVariable, "7");
        using var scaleOverride = new EnvironmentOverride(ScreenShareQualitySettings.ScreenShareScaleVariable, "0.9");
        using var profileOverride = new EnvironmentOverride(
            ScreenShareQualitySettings.ScreenShareQualityProfileVariable,
            FeatureFlags.ScreenShareQualityProfileNormal);
        ScreenShareQualitySettings.ResetMigrationStateForTests();

        try
        {
            var current = ScreenShareQualitySettings.ApplyStartupMigrationIfNeeded(persistUserEnvironment: false);

            Assert.Equal(12, current.CaptureFramesPerSecond);
            Assert.Equal(7, current.TransportFramesPerSecond);
            Assert.Equal(0.9d, current.CaptureScale);
            Assert.Equal(FeatureFlags.ScreenShareQualityProfileNormal, current.QualityProfile);
            Assert.Equal("custom", current.EffectivePresetKey);
            Assert.False(current.LegacyHigherClarityPresetMigrated);
            Assert.False(ScreenShareQualitySettings.HasPendingUserEnvironmentPersistence);
        }
        finally
        {
            ScreenShareQualitySettings.ResetMigrationStateForTests();
        }
    }
}
