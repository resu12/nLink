using NLink.App.Configuration;

namespace NLink.SmokeTests;

public sealed class ScreenShareQualitySettingsTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void ApplyStartupMigrationIfNeeded_ExactLegacyHigherClarityTuple_MigratesToTextFirstPreset()
    {
        using var captureFpsOverride = new EnvironmentOverride(ScreenShareQualitySettings.ScreenShareMaxFpsVariable, "20");
        using var transportFpsOverride = new EnvironmentOverride(ScreenShareQualitySettings.ScreenShareTransportMaxFpsVariable, "8");
        using var scaleOverride = new EnvironmentOverride(ScreenShareQualitySettings.ScreenShareScaleVariable, "0.85");
        ScreenShareQualitySettings.ResetMigrationStateForTests();

        try
        {
            var migrated = ScreenShareQualitySettings.ApplyStartupMigrationIfNeeded(persistUserEnvironment: false);

            Assert.Equal(15, migrated.CaptureFramesPerSecond);
            Assert.Equal(8, migrated.TransportFramesPerSecond);
            Assert.Equal(1d, migrated.CaptureScale);
            Assert.Equal(ScreenShareQualitySettings.TextFirstEffectivePresetKey, migrated.EffectivePresetKey);
            Assert.True(migrated.LegacyHigherClarityPresetMigrated);
            Assert.True(ScreenShareQualitySettings.HasPendingUserEnvironmentPersistence);
            Assert.Equal("15", Environment.GetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareMaxFpsVariable));
            Assert.Equal("8", Environment.GetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareTransportMaxFpsVariable));
            Assert.Equal("1", Environment.GetEnvironmentVariable(ScreenShareQualitySettings.ScreenShareScaleVariable));
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
        ScreenShareQualitySettings.ResetMigrationStateForTests();

        try
        {
            var current = ScreenShareQualitySettings.ApplyStartupMigrationIfNeeded(persistUserEnvironment: false);

            Assert.Equal(12, current.CaptureFramesPerSecond);
            Assert.Equal(7, current.TransportFramesPerSecond);
            Assert.Equal(0.9d, current.CaptureScale);
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
