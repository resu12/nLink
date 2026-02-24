using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using NLink.Core;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;

namespace NLink.App.Configuration;

public sealed class TransportRuntimeConfig
{
    private TransportRuntimeConfig(
        string key,
        string displayName,
        string buildMode,
        string envVarValue,
        string selectionReason,
        bool isDevLocal,
        Func<ISignalingTransport> createTransport)
    {
        Key = key;
        DisplayName = displayName;
        BuildMode = buildMode;
        EnvironmentVariableValue = envVarValue;
        SelectionReason = selectionReason;
        IsDevLocal = isDevLocal;
        CreateTransport = createTransport;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public string BuildMode { get; }

    public string EnvironmentVariableValue { get; }

    public string SelectionReason { get; }

    public bool IsDevLocal { get; }

    public Func<ISignalingTransport> CreateTransport { get; }

    public string HelperHintText =>
        IsDevLocal
            ? "Works for testing on this PC."
            : "Works across devices when both apps are open.";

    public string HelpeeDisconnectedText =>
        "Connection was lost. Waiting for another try.";

    public string HelperDisconnectedText =>
        IsDevLocal
            ? "Could not connect yet. Please try again."
            : "Could not connect yet. Please try again.";

    public string ApprovedStatusText => "Connected.";

    public string AllowStatusText => "Connected.";

    public static TransportRuntimeConfig Select()
    {
        var (transportSetting, settingSource) = ReadTransportSetting();
        var normalizedSetting = string.IsNullOrWhiteSpace(transportSetting) ? "(not set)" : transportSetting.Trim();
        var bridgeBundled = IsBundledNknBridgeAvailable();
        var hasExplicitSetting = !string.IsNullOrWhiteSpace(transportSetting);
        var useNkn = string.Equals(transportSetting, "NKN", StringComparison.OrdinalIgnoreCase);

#if DEBUG
        const string buildMode = "Debug";
#else
        const string buildMode = "Release";
#endif

        if (!hasExplicitSetting)
        {
#if DEBUG
            useNkn = false;
            settingSource = "default-debug";
#else
            useNkn = bridgeBundled;
            settingSource = bridgeBundled ? "default-release-bundled-bridge" : "default-release-no-bridge";
#endif
        }

        var config = useNkn
            ? new TransportRuntimeConfig(
                key: "NKN",
                displayName: "Internet connection",
                buildMode: buildMode,
                envVarValue: normalizedSetting,
                selectionReason: hasExplicitSetting
                    ? $"{buildMode} build with NLINK_TRANSPORT=NKN ({settingSource})"
                    : $"{buildMode} build default to NKN ({settingSource})",
                isDevLocal: false,
                createTransport: static () => new NknSignalingTransport())
            : new TransportRuntimeConfig(
                key: "DevLocal",
                displayName: "Same PC test mode",
                buildMode: buildMode,
                envVarValue: normalizedSetting,
                selectionReason: hasExplicitSetting
                    ? $"{buildMode} build explicit/fallback ({settingSource})"
                    : $"{buildMode} build default/fallback ({settingSource})",
                isDevLocal: true,
                createTransport: static () => new DevLocalTransport());

        AppLog.Info(
            $"Active transport selected: {config.Key} | build={config.BuildMode} | NLINK_TRANSPORT={config.EnvironmentVariableValue} | reason={config.SelectionReason}");

        return config;
    }

    private static (string? Value, string Source) ReadTransportSetting()
    {
        var envValue = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return (envValue.Trim(), "env");
        }

        var appSettingsValue = AppSettingsJson.TryGet("NLINK_TRANSPORT") ?? AppSettingsJson.TryGet("nLink:transport");
        if (!string.IsNullOrWhiteSpace(appSettingsValue))
        {
            return (appSettingsValue.Trim(), "appsettings");
        }

        return (null, "default");
    }

    private static class AppSettingsJson
    {
        public static string? TryGet(string path)
        {
            foreach (var filePath in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                         Path.Combine(Environment.CurrentDirectory, "appsettings.json"),
                     })
            {
                try
                {
                    if (!File.Exists(filePath))
                    {
                        continue;
                    }

                    using var stream = File.OpenRead(filePath);
                    using var doc = JsonDocument.Parse(stream);
                    return ResolvePath(doc.RootElement, path);
                }
                catch
                {
                    // Ignore invalid config files and continue with defaults.
                }
            }

            return null;
        }

        private static string? ResolvePath(JsonElement root, string path)
        {
            if (!path.Contains(':', StringComparison.Ordinal))
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (string.Equals(property.Name, path, StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }
                }

                return null;
            }

            var current = root;
            foreach (var part in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var found = false;
                foreach (var property in current.EnumerateObject())
                {
                    if (!string.Equals(property.Name, part, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    current = property.Value;
                    found = true;
                    break;
                }

                if (!found)
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }
    }

    private static bool IsBundledNknBridgeAvailable()
    {
        try
        {
            var rid = GetBridgeRid();
            var baseDir = AppContext.BaseDirectory;
            var bridgeDir = Path.Combine(baseDir, "bridge", rid);
            var indexJs = Path.Combine(bridgeDir, "index.js");
            var nodeExe = Path.Combine(bridgeDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node");

            if (File.Exists(indexJs) && File.Exists(nodeExe))
            {
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var resourcesDir = Path.GetFullPath(Path.Combine(baseDir, "..", "Resources", "bridge", rid));
                var macIndex = Path.Combine(resourcesDir, "index.js");
                var macNode = Path.Combine(resourcesDir, "node");
                return File.Exists(macIndex) && File.Exists(macNode);
            }
        }
        catch
        {
            // Ignore bridge detection errors and continue with default fallback.
        }

        return false;
    }

    private static string GetBridgeRid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "win-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "linux-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => throw new NotSupportedException()
            };
        }

        throw new NotSupportedException();
    }
}
