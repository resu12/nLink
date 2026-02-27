using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using NLink.App.Services;
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
        bool forcedByEnvironment,
        bool autoSelected,
        bool isDevLocal,
        bool bridgeBundled,
        string bridgeBundleProbeReason,
        string? startupWarningText,
        string? configurationErrorText,
        BridgeReusePolicy bridgeReusePolicy,
        Func<ISignalingTransport> createTransport)
    {
        Key = key;
        DisplayName = displayName;
        BuildMode = buildMode;
        EnvironmentVariableValue = envVarValue;
        SelectionReason = selectionReason;
        ForcedByEnvironment = forcedByEnvironment;
        AutoSelected = autoSelected;
        IsDevLocal = isDevLocal;
        BridgeBundled = bridgeBundled;
        BridgeBundleProbeReason = bridgeBundleProbeReason;
        StartupWarningText = startupWarningText ?? string.Empty;
        ConfigurationErrorText = configurationErrorText ?? string.Empty;
        BridgeReusePolicy = bridgeReusePolicy;
        CreateTransport = createTransport;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public string BuildMode { get; }

    public string EnvironmentVariableValue { get; }

    public string SelectionReason { get; }

    public bool ForcedByEnvironment { get; }

    public bool AutoSelected { get; }

    public bool IsDevLocal { get; }

    public bool BridgeBundled { get; }

    public string BridgeBundleProbeReason { get; }

    public string StartupWarningText { get; }

    public string ConfigurationErrorText { get; }

    public bool HasStartupWarning => !string.IsNullOrWhiteSpace(StartupWarningText);

    public bool HasConfigurationError => !string.IsNullOrWhiteSpace(ConfigurationErrorText);

    public BridgeReusePolicy BridgeReusePolicy { get; }

    public Func<ISignalingTransport> CreateTransport { get; }

    public string HelperHintText =>
        IsDevLocal
            ? "Works for testing on this PC."
            : "Works across devices when both apps are open.";

    public string HelpeeDisconnectedText =>
        "Connection lost.";

    public string HelperDisconnectedText =>
        "Connection lost.";

    public string ApprovedStatusText => "Connected";

    public string AllowStatusText => "Connected";

    public static TransportRuntimeConfig Select()
    {
        var (transportSetting, settingSource) = ReadTransportSetting();
        var normalizedSetting = string.IsNullOrWhiteSpace(transportSetting) ? "(not set)" : transportSetting.Trim();
        var bridgeBundled = IsBridgeBundledForCurrentRid(out var bridgeProbeReason);
        var hasExplicitSetting = !string.IsNullOrWhiteSpace(transportSetting);
        var envForcesNkn = string.Equals(transportSetting, "NKN", StringComparison.OrdinalIgnoreCase);
        var envForcesDevLocal = string.Equals(transportSetting, "DEVLOCAL", StringComparison.OrdinalIgnoreCase);
        var envInvalid = hasExplicitSetting && !envForcesNkn && !envForcesDevLocal;
        var useNkn = envForcesNkn;
        var forcedByEnvironment = hasExplicitSetting && !envInvalid;
        var autoSelected = false;
        string? startupWarningText = null;
        string? configurationErrorText = null;

#if DEBUG
        const string buildMode = "Debug";
#else
        const string buildMode = "Release";
#endif

        if (envInvalid)
        {
            configurationErrorText = "Invalid NLINK_TRANSPORT value. Use NKN or DEVLOCAL.";
            NknRuntimeDiagnostics.SetLastError($"TRANSPORT_CONFIG_INVALID: {transportSetting!.Trim()}");
            useNkn = false;
            settingSource = "env-invalid";
        }
        else if (!hasExplicitSetting)
        {
#if DEBUG
            useNkn = false;
            settingSource = "default-debug";
#else
            useNkn = bridgeBundled;
            autoSelected = useNkn;
            settingSource = bridgeBundled ? "default-release-bundled-bridge" : "default-release-no-bridge";
            if (!bridgeBundled)
            {
                startupWarningText = "Cross-PC requires NKN. This install is missing the bridge runtime. Please reinstall.";
                NknRuntimeDiagnostics.SetLastError($"NKN_BRIDGE_MISSING: {bridgeProbeReason}");
            }
#endif
        }
        else if (envForcesNkn && !bridgeBundled)
        {
            startupWarningText = "Couldn't start the connection. Please reinstall.";
            NknRuntimeDiagnostics.SetLastError($"NKN_START_FAILED: bridge_missing ({bridgeProbeReason})");
        }

        var config = useNkn
            ? CreateNknConfig(bridgeBundled, bridgeProbeReason, buildMode, normalizedSetting, hasExplicitSetting, settingSource, forcedByEnvironment, autoSelected, startupWarningText, configurationErrorText)
            : CreateDevLocalConfig(bridgeBundled, bridgeProbeReason, buildMode, normalizedSetting, hasExplicitSetting, settingSource, forcedByEnvironment, autoSelected, startupWarningText, configurationErrorText);

        AppLog.Info(
            $"Active transport selected: {config.Key} | build={config.BuildMode} | NLINK_TRANSPORT={config.EnvironmentVariableValue} | reason={config.SelectionReason} | bridge_reuse_mode={config.BridgeReusePolicy.Mode}");

        return config;
    }

    private static TransportRuntimeConfig CreateNknConfig(
        bool bridgeBundled,
        string bridgeProbeReason,
        string buildMode,
        string normalizedSetting,
        bool hasExplicitSetting,
        string settingSource,
        bool forcedByEnvironment,
        bool autoSelected,
        string? startupWarningText,
        string? configurationErrorText)
    {
        var policy = ReadBridgeReusePolicy();
        return new TransportRuntimeConfig(
            key: "NKN",
            displayName: "Internet connection",
            buildMode: buildMode,
            envVarValue: normalizedSetting,
            selectionReason: hasExplicitSetting
                ? $"{buildMode} build with NLINK_TRANSPORT=NKN ({settingSource})"
                : $"{buildMode} build default to NKN ({settingSource})",
            forcedByEnvironment: forcedByEnvironment,
            autoSelected: autoSelected,
            isDevLocal: false,
            bridgeBundled: bridgeBundled,
            bridgeBundleProbeReason: bridgeProbeReason,
            startupWarningText: startupWarningText,
            configurationErrorText: configurationErrorText,
            bridgeReusePolicy: policy,
            createTransport: static () => new NknSignalingTransport());
    }

    private static TransportRuntimeConfig CreateDevLocalConfig(
        bool bridgeBundled,
        string bridgeProbeReason,
        string buildMode,
        string normalizedSetting,
        bool hasExplicitSetting,
        string settingSource,
        bool forcedByEnvironment,
        bool autoSelected,
        string? startupWarningText,
        string? configurationErrorText)
    {
        return new TransportRuntimeConfig(
            key: "DevLocal",
            displayName: "Same PC test mode",
            buildMode: buildMode,
            envVarValue: normalizedSetting,
            selectionReason: hasExplicitSetting
                ? $"{buildMode} build explicit/fallback ({settingSource})"
                : $"{buildMode} build default/fallback ({settingSource})",
            forcedByEnvironment: forcedByEnvironment,
            autoSelected: autoSelected,
            isDevLocal: true,
            bridgeBundled: bridgeBundled,
            bridgeBundleProbeReason: bridgeProbeReason,
            startupWarningText: startupWarningText,
            configurationErrorText: configurationErrorText,
            bridgeReusePolicy: BridgeReusePolicy.Default,
            createTransport: static () => new DevLocalTransport());
    }

    private static BridgeReusePolicy ReadBridgeReusePolicy()
    {
        var modeValue =
            Environment.GetEnvironmentVariable("NLINK_BRIDGE_REUSE_MODE")
            ?? AppSettingsJson.TryGet("NLINK_BRIDGE_REUSE_MODE")
            ?? AppSettingsJson.TryGet("nLink:bridgeReuseMode");

        var timeoutValue =
            Environment.GetEnvironmentVariable("NLINK_BRIDGE_KEEPALIVE_IDLE_TIMEOUT_SECONDS")
            ?? AppSettingsJson.TryGet("NLINK_BRIDGE_KEEPALIVE_IDLE_TIMEOUT_SECONDS")
            ?? AppSettingsJson.TryGet("nLink:bridgeKeepAliveIdleTimeoutSeconds");

        var mode = string.Equals(modeValue, "KeepAlive", StringComparison.OrdinalIgnoreCase)
            ? BridgeReuseMode.KeepAlive
            : BridgeReuseMode.PerSession;

        var timeoutSeconds = 60;
        if (!string.IsNullOrWhiteSpace(timeoutValue) &&
            int.TryParse(timeoutValue.Trim(), out var parsed) &&
            parsed > 0)
        {
            timeoutSeconds = parsed;
        }

        return new BridgeReusePolicy(mode, TimeSpan.FromSeconds(timeoutSeconds));
    }

    public static bool IsBridgeBundledForCurrentRid(out string reason)
    {
        try
        {
            var rid = GetBridgeRid();
            var baseDir = AppContext.BaseDirectory;
            var bridgeDir = Path.Combine(baseDir, "bridge", rid);
            var indexJs = Path.Combine(bridgeDir, "index.js");
            var nodeExe = Path.Combine(bridgeDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node");
            var nodeModules = Path.Combine(bridgeDir, "node_modules");

            if (File.Exists(indexJs) && File.Exists(nodeExe) && Directory.Exists(nodeModules))
            {
                reason = "ok";
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var resourcesDir = Path.GetFullPath(Path.Combine(baseDir, "..", "Resources", "bridge", rid));
                var macIndex = Path.Combine(resourcesDir, "index.js");
                var macNode = Path.Combine(resourcesDir, "node");
                var macNodeModules = Path.Combine(resourcesDir, "node_modules");
                if (File.Exists(macIndex) && File.Exists(macNode) && Directory.Exists(macNodeModules))
                {
                    reason = "ok";
                    return true;
                }

                reason = MissingBridgeReason(macIndex, macNode, macNodeModules);
                return false;
            }

            reason = MissingBridgeReason(indexJs, nodeExe, nodeModules);
            return false;
        }
        catch (Exception ex)
        {
            reason = $"probe_error:{ex.GetType().Name}";
            return false;
        }
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

    private static string MissingBridgeReason(string indexJsPath, string nodePath, string nodeModulesPath)
    {
        if (!File.Exists(indexJsPath))
        {
            return $"missing {indexJsPath}";
        }

        if (!File.Exists(nodePath))
        {
            return $"missing {nodePath}";
        }

        if (!Directory.Exists(nodeModulesPath))
        {
            return $"missing {nodeModulesPath}";
        }

        return "bridge runtime incomplete";
    }
}
