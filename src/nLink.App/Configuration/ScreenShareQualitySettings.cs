using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core.Logging;

namespace NLink.App.Configuration;

internal readonly record struct ScreenSharePresetDefinition(
    string Key,
    string DisplayName,
    int CaptureFramesPerSecond,
    int TransportFramesPerSecond,
    double CaptureScale)
{
    public string Describe()
        => $"capture_fps={CaptureFramesPerSecond}, transport_fps={TransportFramesPerSecond}, scale={CaptureScale:0.00}";
}

internal readonly record struct ScreenShareQualityEnvironmentState(
    int CaptureFramesPerSecond,
    int TransportFramesPerSecond,
    double CaptureScale,
    string EffectivePresetKey,
    string EffectivePresetName,
    bool LegacyHigherClarityPresetMigrated);

internal static class ScreenShareQualitySettings
{
    internal const string ScreenShareMaxFpsVariable = "NLINK_FEATURE_SCREENCAP_MAX_FPS";
    internal const string ScreenShareTransportMaxFpsVariable = "NLINK_FEATURE_SCREENCAP_TRANSPORT_MAX_FPS";
    internal const string ScreenShareScaleVariable = "NLINK_FEATURE_SCREENCAP_SCALE";
    internal static readonly ScreenSharePresetDefinition BalancedPreset =
        new("balanced", "Balanced", 15, 8, 1d);

    internal static readonly ScreenSharePresetDefinition HighQualityPreset =
        new("high_quality", "High quality", 20, 12, 1d);

    internal static readonly ScreenSharePresetDefinition HighPerformancePreset =
        new("high_performance", "High performance", 10, 6, 0.60d);

    private static readonly ScreenSharePresetDefinition LegacyHigherClarityPreset =
        new("legacy_higher_clarity", "Higher clarity", 20, 8, 0.85d);

    private static int legacyHigherClarityPresetMigrated;
    private static int pendingUserEnvironmentPersistence;
    private static int backgroundUserEnvironmentPersistenceStarted;

    internal static bool WasLegacyHigherClarityPresetMigrated
        => Volatile.Read(ref legacyHigherClarityPresetMigrated) == 1;

    internal static bool HasPendingUserEnvironmentPersistence
        => Volatile.Read(ref pendingUserEnvironmentPersistence) == 1;

    internal static ScreenShareQualityEnvironmentState ApplyStartupMigrationIfNeeded(bool persistUserEnvironment = true)
    {
        if (MatchesPreset(LegacyHigherClarityPreset))
        {
            ApplyPresetToProcessEnvironment(BalancedPreset);
            Interlocked.Exchange(ref legacyHigherClarityPresetMigrated, 1);

            if (persistUserEnvironment)
            {
                if (PersistPresetToUserEnvironment(BalancedPreset))
                {
                    Interlocked.Exchange(ref pendingUserEnvironmentPersistence, 0);
                }
                else
                {
                    Interlocked.Exchange(ref pendingUserEnvironmentPersistence, 1);
                }
            }
            else
            {
                Interlocked.Exchange(ref pendingUserEnvironmentPersistence, 1);
            }

            LocalOperationalLog.Warn(
                "ScreenShareQuality",
                $"event=screenshare_quality_preset_migrated; from={LegacyHigherClarityPreset.Key}; to={BalancedPreset.Key}; {BalancedPreset.Describe()}");
        }

        return GetCurrentEnvironmentState();
    }

    internal static void PersistPendingUserEnvironmentMigrationInBackground()
    {
        if (!HasPendingUserEnvironmentPersistence)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref backgroundUserEnvironmentPersistenceStarted, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                LocalOperationalLog.Info(
                    "ScreenShareQuality",
                    $"event=screenshare_quality_preset_persist_async_started; to={BalancedPreset.Key}");

                if (PersistPresetToUserEnvironment(BalancedPreset))
                {
                    Interlocked.Exchange(ref pendingUserEnvironmentPersistence, 0);
                    LocalOperationalLog.Info(
                        "ScreenShareQuality",
                        $"event=screenshare_quality_preset_persist_async_completed; to={BalancedPreset.Key}; duration_ms={stopwatch.ElapsedMilliseconds}");
                }
                else
                {
                    LocalOperationalLog.Warn(
                        "ScreenShareQuality",
                        $"event=screenshare_quality_preset_persist_async_failed; to={BalancedPreset.Key}; duration_ms={stopwatch.ElapsedMilliseconds}; reason=user_environment_write_failed");
                }
            }
            catch (Exception ex)
            {
                LocalOperationalLog.Warn(
                    "ScreenShareQuality",
                    $"event=screenshare_quality_preset_persist_async_failed; to={BalancedPreset.Key}; duration_ms={stopwatch.ElapsedMilliseconds}; reason={ex.GetType().Name}");
            }
            finally
            {
                Interlocked.Exchange(ref backgroundUserEnvironmentPersistenceStarted, 0);
            }
        });
    }

    internal static ScreenShareQualityEnvironmentState GetCurrentEnvironmentState()
    {
        var effectivePresetKey = ResolveEffectivePresetKey(
            FeatureFlags.ScreenShareMaxFps,
            FeatureFlags.ScreenShareTransportMaxFps,
            FeatureFlags.ScreenShareScale);

        return new ScreenShareQualityEnvironmentState(
            CaptureFramesPerSecond: FeatureFlags.ScreenShareMaxFps,
            TransportFramesPerSecond: FeatureFlags.ScreenShareTransportMaxFps,
            CaptureScale: FeatureFlags.ScreenShareScale,
            EffectivePresetKey: effectivePresetKey,
            EffectivePresetName: ResolveEffectivePresetName(effectivePresetKey),
            LegacyHigherClarityPresetMigrated: WasLegacyHigherClarityPresetMigrated);
    }

    internal static string ResolveEffectivePresetKey(int captureFramesPerSecond, int transportFramesPerSecond, double captureScale)
    {
        if (MatchesPreset(BalancedPreset, captureFramesPerSecond, transportFramesPerSecond, captureScale))
        {
            return BalancedPreset.Key;
        }

        if (MatchesPreset(HighQualityPreset, captureFramesPerSecond, transportFramesPerSecond, captureScale))
        {
            return HighQualityPreset.Key;
        }

        if (MatchesPreset(HighPerformancePreset, captureFramesPerSecond, transportFramesPerSecond, captureScale))
        {
            return HighPerformancePreset.Key;
        }

        return "custom";
    }

    internal static string ResolveEffectivePresetName(string effectivePresetKey)
    {
        return effectivePresetKey switch
        {
            "balanced" => BalancedPreset.DisplayName,
            "high_quality" => HighQualityPreset.DisplayName,
            "high_performance" => HighPerformancePreset.DisplayName,
            _ => "Custom",
        };
    }

    internal static string FormatScale(double scale)
        => scale.ToString("0.###", CultureInfo.InvariantCulture);

    internal static void ResetMigrationStateForTests()
    {
        Interlocked.Exchange(ref legacyHigherClarityPresetMigrated, 0);
        Interlocked.Exchange(ref pendingUserEnvironmentPersistence, 0);
        Interlocked.Exchange(ref backgroundUserEnvironmentPersistenceStarted, 0);
    }

    private static bool MatchesPreset(ScreenSharePresetDefinition preset)
    {
        return MatchesPreset(
            preset,
            FeatureFlags.ScreenShareMaxFps,
            FeatureFlags.ScreenShareTransportMaxFps,
            FeatureFlags.ScreenShareScale);
    }

    private static bool MatchesPreset(
        ScreenSharePresetDefinition preset,
        int captureFramesPerSecond,
        int transportFramesPerSecond,
        double captureScale)
    {
        return captureFramesPerSecond == preset.CaptureFramesPerSecond &&
               transportFramesPerSecond == preset.TransportFramesPerSecond &&
               Math.Abs(captureScale - preset.CaptureScale) < 0.0001d;
    }

    private static void ApplyPresetToProcessEnvironment(ScreenSharePresetDefinition preset)
    {
        var captureFramesPerSecond = preset.CaptureFramesPerSecond.ToString(CultureInfo.InvariantCulture);
        var transportFramesPerSecond = preset.TransportFramesPerSecond.ToString(CultureInfo.InvariantCulture);
        var captureScale = preset.CaptureScale.ToString("0.##", CultureInfo.InvariantCulture);

        Environment.SetEnvironmentVariable(ScreenShareMaxFpsVariable, captureFramesPerSecond);
        Environment.SetEnvironmentVariable(ScreenShareTransportMaxFpsVariable, transportFramesPerSecond);
        Environment.SetEnvironmentVariable(ScreenShareScaleVariable, captureScale);
    }

    private static bool PersistPresetToUserEnvironment(ScreenSharePresetDefinition preset)
    {
        var captureFramesPerSecond = preset.CaptureFramesPerSecond.ToString(CultureInfo.InvariantCulture);
        var transportFramesPerSecond = preset.TransportFramesPerSecond.ToString(CultureInfo.InvariantCulture);
        var captureScale = preset.CaptureScale.ToString("0.##", CultureInfo.InvariantCulture);

        var capturePersisted = TrySetUserEnvironmentVariable(ScreenShareMaxFpsVariable, captureFramesPerSecond);
        var transportPersisted = TrySetUserEnvironmentVariable(ScreenShareTransportMaxFpsVariable, transportFramesPerSecond);
        var scalePersisted = TrySetUserEnvironmentVariable(ScreenShareScaleVariable, captureScale);
        return capturePersisted && transportPersisted && scalePersisted;
    }

    private static bool TrySetUserEnvironmentVariable(string name, string value)
    {
        try
        {
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
            return true;
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "ScreenShareQuality",
                $"event=screenshare_quality_preset_persist_failed; variable={name}; reason={ex.GetType().Name}");
            return false;
        }
    }
}
