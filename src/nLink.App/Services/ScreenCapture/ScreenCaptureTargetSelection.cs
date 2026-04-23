using System;
using System.Globalization;
using System.Text.Json;

namespace NLink.App.Services.ScreenCapture;

public enum ScreenCaptureTargetMode
{
    PrimaryDisplay = 0,
    Display = 1,
    Window = 2,
    Region = 3,
}

internal readonly record struct ScreenCaptureTargetSelection(
    ScreenCaptureTargetMode Mode,
    string? DisplayId,
    string? WindowId,
    ScreenCapturePixelRect RegionPx)
{
    public static ScreenCaptureTargetSelection PrimaryDisplay =>
        new(ScreenCaptureTargetMode.PrimaryDisplay, null, null, default);

    public bool HasDisplayId => !string.IsNullOrWhiteSpace(DisplayId);

    public bool HasWindowId => !string.IsNullOrWhiteSpace(WindowId);

    public bool HasRegion => RegionPx.IsValid;

    public string Describe()
    {
        return Mode switch
        {
            ScreenCaptureTargetMode.Display => $"display:{DisplayId ?? "(none)"}",
            ScreenCaptureTargetMode.Window => $"window:{WindowId ?? "(none)"}",
            ScreenCaptureTargetMode.Region => $"region:{DisplayId ?? "(none)"}:{RegionPx.X},{RegionPx.Y},{RegionPx.Width}x{RegionPx.Height}",
            _ => "primary",
        };
    }
}

internal static class ScreenCaptureTargetStore
{
    private const string CaptureTargetEnvVar = "NLINK_FEATURE_SCREENCAP_TARGET";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ScreenCaptureTargetSelection Load()
    {
        var raw = Environment.GetEnvironmentVariable(CaptureTargetEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ScreenCaptureTargetSelection.PrimaryDisplay;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<PersistedCaptureTarget>(raw, JsonOptions);
            if (payload is null)
            {
                return ScreenCaptureTargetSelection.PrimaryDisplay;
            }

            if (!Enum.TryParse<ScreenCaptureTargetMode>(payload.Mode, ignoreCase: true, out var mode))
            {
                mode = ScreenCaptureTargetMode.PrimaryDisplay;
            }

            return new ScreenCaptureTargetSelection(
                mode,
                Normalize(payload.DisplayId),
                Normalize(payload.WindowId),
                new ScreenCapturePixelRect(payload.RegionX, payload.RegionY, payload.RegionWidth, payload.RegionHeight));
        }
        catch
        {
            return ScreenCaptureTargetSelection.PrimaryDisplay;
        }
    }

    public static void Save(ScreenCaptureTargetSelection selection)
    {
        var payload = new PersistedCaptureTarget
        {
            Mode = selection.Mode.ToString(),
            DisplayId = Normalize(selection.DisplayId),
            WindowId = Normalize(selection.WindowId),
            RegionX = selection.RegionPx.X,
            RegionY = selection.RegionPx.Y,
            RegionWidth = selection.RegionPx.Width,
            RegionHeight = selection.RegionPx.Height,
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        Environment.SetEnvironmentVariable(CaptureTargetEnvVar, json);
        _ = PersistToUserEnvironmentAsync(json, selection.Describe());
    }

    private static async System.Threading.Tasks.Task PersistToUserEnvironmentAsync(string json, string selectionDescription)
    {
        await System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Environment.SetEnvironmentVariable(CaptureTargetEnvVar, json, EnvironmentVariableTarget.User);
            }
            catch (Exception ex)
            {
                NLink.Core.Logging.LocalOperationalLog.Warn(
                    "ScreenShareTarget",
                    $"Persisting capture target '{selectionDescription}' failed: {ex.GetType().Name}");
            }
        }).ConfigureAwait(false);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class PersistedCaptureTarget
    {
        public string? Mode { get; set; }

        public string? DisplayId { get; set; }

        public string? WindowId { get; set; }

        public int RegionX { get; set; }

        public int RegionY { get; set; }

        public int RegionWidth { get; set; }

        public int RegionHeight { get; set; }
    }
}

public sealed record ScreenCaptureDisplayOption(
    string Id,
    string Label,
    ScreenCapturePixelRect BoundsPx,
    bool IsPrimary,
    double? DpiScale)
{
    public override string ToString() => Label;
}

public sealed record ScreenCaptureDisplayPickerOption(
    string? DisplayId,
    string Label)
{
    public bool IsPrimaryDisplay => string.IsNullOrWhiteSpace(DisplayId);

    public override string ToString() => Label;
}

public sealed record ScreenCaptureWindowOption(
    string Id,
    string Label,
    ScreenCapturePixelRect BoundsPx)
{
    public override string ToString() => Label;
}
