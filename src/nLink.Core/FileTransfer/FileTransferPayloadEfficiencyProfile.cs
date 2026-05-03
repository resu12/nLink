using NLink.Core.Configuration;

namespace NLink.Core.FileTransfer;

public enum FileTransferPayloadEfficiencyProfileKind
{
    Current,
    Packed3x20KiB,
    Packed3x21KiB,
    LargeSingle48KiB,
}

public readonly record struct FileTransferPayloadEfficiencyProfile(
    FileTransferPayloadEfficiencyProfileKind Kind,
    string Name,
    int? PreferredChunkSizeBytes,
    int MaxBatchChunkCount,
    int TargetBatchRawBytes)
{
    public const string EnvironmentVariableName = "NLINK_FILETRANSFER_PAYLOAD_EFFICIENCY_PROFILE";
    public const string AllowScreenShareEnvironmentVariableName = "NLINK_FILETRANSFER_PAYLOAD_EFFICIENCY_ALLOW_SCREENSHARE";

    public static FileTransferPayloadEfficiencyProfile Current { get; } = new(
        FileTransferPayloadEfficiencyProfileKind.Current,
        nameof(FileTransferPayloadEfficiencyProfileKind.Current),
        PreferredChunkSizeBytes: null,
        MaxBatchChunkCount: 4,
        TargetBatchRawBytes: FileTransferChunkBudget.MaxRawChunkBytes);

    public static FileTransferPayloadEfficiencyProfile Packed3x20KiB { get; } = new(
        FileTransferPayloadEfficiencyProfileKind.Packed3x20KiB,
        nameof(FileTransferPayloadEfficiencyProfileKind.Packed3x20KiB),
        PreferredChunkSizeBytes: 20 * 1024,
        MaxBatchChunkCount: 3,
        TargetBatchRawBytes: 3 * 20 * 1024);

    public static FileTransferPayloadEfficiencyProfile Packed3x21KiB { get; } = new(
        FileTransferPayloadEfficiencyProfileKind.Packed3x21KiB,
        nameof(FileTransferPayloadEfficiencyProfileKind.Packed3x21KiB),
        PreferredChunkSizeBytes: 21 * 1024,
        MaxBatchChunkCount: 3,
        TargetBatchRawBytes: 3 * 21 * 1024);

    public static FileTransferPayloadEfficiencyProfile LargeSingle48KiB { get; } = new(
        FileTransferPayloadEfficiencyProfileKind.LargeSingle48KiB,
        nameof(FileTransferPayloadEfficiencyProfileKind.LargeSingle48KiB),
        PreferredChunkSizeBytes: 48 * 1024,
        MaxBatchChunkCount: 1,
        TargetBatchRawBytes: FileTransferChunkBudget.MaxRawChunkBytes);

    public static FileTransferPayloadEfficiencyProfile ForKind(FileTransferPayloadEfficiencyProfileKind kind)
        => kind switch
        {
            FileTransferPayloadEfficiencyProfileKind.Packed3x20KiB => Packed3x20KiB,
            FileTransferPayloadEfficiencyProfileKind.Packed3x21KiB => Packed3x21KiB,
            FileTransferPayloadEfficiencyProfileKind.LargeSingle48KiB => LargeSingle48KiB,
            _ => Current,
        };

    public static bool TryParse(string? value, out FileTransferPayloadEfficiencyProfile profile)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            profile = Current;
            return true;
        }

        foreach (var candidate in new[] { Current, Packed3x20KiB, Packed3x21KiB, LargeSingle48KiB })
        {
            if (string.Equals(value.Trim(), candidate.Name, StringComparison.OrdinalIgnoreCase))
            {
                profile = candidate;
                return true;
            }
        }

        profile = Current;
        return false;
    }

    public static FileTransferPayloadEfficiencyProfile ResolveRequestedFromEnvironment(out string reason)
    {
        var value = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable(EnvironmentVariableName, category: "filetransfer_tuning");
        if (string.IsNullOrWhiteSpace(value))
        {
            reason = "current_default";
            return Current;
        }

        if (TryParse(value, out var profile))
        {
            reason = profile.Kind == FileTransferPayloadEfficiencyProfileKind.Current
                ? "current_explicit"
                : "env_profile";
            return profile;
        }

        reason = "invalid_env_forced_current";
        return Current;
    }

    public static bool AllowExperimentalProfileDuringScreenShare()
    {
        var value = ReleaseOverridePolicy.ReadUnsafeEnvironmentVariable(AllowScreenShareEnvironmentVariableName, category: "filetransfer_tuning");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
