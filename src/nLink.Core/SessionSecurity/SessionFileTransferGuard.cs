using System.Globalization;
using System.IO;
using NLink.Core.Logging;
using NLink.Core.SessionConnect;

namespace NLink.Core.SessionSecurity;

public enum FileTransferValidationFailure
{
    None = 0,
    AuthorizationDenied = 1,
    SessionIdMissing = 2,
    SessionIdMismatch = 3,
    HelperIdentityMissing = 4,
    HelperIdentityMismatch = 5,
    InvalidFileSize = 6,
    FileTooLarge = 7,
    InvalidChunkLength = 8,
    ChunkTooLarge = 9,
    InvalidFileName = 10,
    PathTraversalDetected = 11,
    InvalidRootDirectory = 12,
    WriteOutsideAllowedDirectory = 13,
    OverwriteBlocked = 14,
    DirectoryTargetBlocked = 15,
    OpenFailed = 16,
}

public readonly record struct FileTransferAccessResult(
    bool IsAllowed,
    FileTransferValidationFailure Failure,
    SessionAuthorizationFailure AuthorizationFailure,
    string Message)
{
    public static FileTransferAccessResult Allowed()
        => new(true, FileTransferValidationFailure.None, SessionAuthorizationFailure.None, string.Empty);

    public static FileTransferAccessResult Denied(
        FileTransferValidationFailure failure,
        string message,
        SessionAuthorizationFailure authorizationFailure = SessionAuthorizationFailure.None)
        => new(false, failure, authorizationFailure, message);
}

public sealed record FileTransferDescriptor(
    SessionId SessionId,
    PeerAddress HelperIdentity,
    string FileName,
    long FileSizeBytes);

public sealed record FileTransferChunkDescriptor(
    SessionId SessionId,
    PeerAddress HelperIdentity,
    string FileName,
    long FileSizeBytes,
    int ChunkLength);

public sealed record FileTransferStoragePolicy(
    string RootDirectoryPath,
    long MaxFileSizeBytes = 25L * 1024 * 1024 * 1024,
    int MaxChunkSizeBytes = 256 * 1024,
    bool AllowOverwrite = false)
{
    public const long DefaultMaxFileSizeBytes = 25L * 1024 * 1024 * 1024;
    public const int DefaultMaxChunkSizeBytes = 256 * 1024;
}

public sealed record FileTransferWritePlan(
    string RootDirectoryPath,
    string SafeFileName,
    string TempFileName,
    string TempPath,
    string FinalPath,
    bool AllowOverwrite,
    long FileSizeBytes,
    int MaxChunkSizeBytes);

public sealed class FileTransferWriteHandle : IDisposable, IAsyncDisposable
{
    private bool finalized;
    private bool preserveTempArtifact;

    public FileTransferWriteHandle(FileTransferWritePlan plan, FileStream stream)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public FileTransferWritePlan Plan { get; }

    public FileStream Stream { get; }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (finalized)
        {
            return;
        }

        await Stream.FlushAsync(ct).ConfigureAwait(false);
        await Stream.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (!Plan.AllowOverwrite && (File.Exists(Plan.FinalPath) || Directory.Exists(Plan.FinalPath)))
            {
                preserveTempArtifact = true;
                throw new IOException("File-transfer target already exists and overwrite is disabled.");
            }

            File.Move(Plan.TempPath, Plan.FinalPath, overwrite: Plan.AllowOverwrite);
            finalized = true;
        }
        catch
        {
            preserveTempArtifact = true;
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            Stream.Dispose();
        }
        finally
        {
            CleanupTempFile();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            CleanupTempFile();
        }
    }

    private void CleanupTempFile()
    {
        if (finalized || preserveTempArtifact || string.IsNullOrWhiteSpace(Plan.TempPath))
        {
            return;
        }

        try
        {
            if (File.Exists(Plan.TempPath))
            {
                File.Delete(Plan.TempPath);
            }
        }
        catch (Exception ex)
        {
            LocalOperationalLog.Warn(
                "FileTransferGuard",
                $"event=temp_cleanup_failed; path={Plan.TempPath}; ex={ex.GetType().Name}");
        }
    }
}

public sealed record FileTransferWriteOpenResult(
    FileTransferAccessResult Access,
    FileTransferWritePlan? Plan = null,
    FileTransferWriteHandle? Handle = null)
{
    public bool IsAllowed => Access.IsAllowed;
}

public sealed class SessionFileTransferGuard
{
    private const int MaxSafeFileNameLength = 120;
    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
    private static readonly HashSet<char> ConservativeInvalidFileNameChars =
    [
        '<', '>', ':', '"', '/', '\\', '|', '?', '*'
    ];

    private readonly SessionAuthorizationGuard authorizationGuard;

    public SessionFileTransferGuard(Func<DateTimeOffset>? nowProvider = null)
    {
        authorizationGuard = new SessionAuthorizationGuard(nowProvider);
    }

    public FileTransferAccessResult AuthorizeSend(
        bool hasSecurityTransport,
        SessionSecurityState securityState,
        SessionGrant? grant)
    {
        var authorization = authorizationGuard.Evaluate(
            hasSecurityTransport,
            securityState,
            grant,
            SessionCapability.FileTransfer);
        if (!authorization.IsAuthorized)
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.AuthorizationDenied,
                $"File transfer authorization failed: {authorization.Failure}.",
                authorization.Failure);
        }

        return FileTransferAccessResult.Allowed();
    }

    public FileTransferAccessResult ValidateReceiveMetadata(
        bool hasSecurityTransport,
        SessionSecurityState securityState,
        SessionGrant? grant,
        FileTransferDescriptor descriptor,
        FileTransferStoragePolicy storagePolicy)
    {
        ArgumentNullException.ThrowIfNull(securityState);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(storagePolicy);

        var authorization = AuthorizeSend(hasSecurityTransport, securityState, grant);
        if (!authorization.IsAllowed)
        {
            return authorization;
        }

        var binding = ValidateBinding(securityState, descriptor.SessionId, descriptor.HelperIdentity);
        if (!binding.IsAllowed)
        {
            return binding;
        }

        var sizeValidation = ValidateFileSize(descriptor.FileSizeBytes, storagePolicy.MaxFileSizeBytes);
        if (!sizeValidation.IsAllowed)
        {
            return sizeValidation;
        }

        return TryCreateWritePlan(descriptor, storagePolicy, out _, out var planValidation)
            ? FileTransferAccessResult.Allowed()
            : planValidation;
    }

    public FileTransferAccessResult ValidateChunk(
        bool hasSecurityTransport,
        SessionSecurityState securityState,
        SessionGrant? grant,
        FileTransferChunkDescriptor descriptor,
        FileTransferStoragePolicy storagePolicy)
    {
        ArgumentNullException.ThrowIfNull(securityState);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(storagePolicy);

        var metadataValidation = ValidateReceiveMetadata(
            hasSecurityTransport,
            securityState,
            grant,
            new FileTransferDescriptor(
                descriptor.SessionId,
                descriptor.HelperIdentity,
                descriptor.FileName,
                descriptor.FileSizeBytes),
            storagePolicy);
        if (!metadataValidation.IsAllowed)
        {
            return metadataValidation;
        }

        if (descriptor.ChunkLength <= 0)
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.InvalidChunkLength,
                "File-transfer chunk length must be positive.");
        }

        if (descriptor.ChunkLength > storagePolicy.MaxChunkSizeBytes)
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.ChunkTooLarge,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"File-transfer chunk length exceeds the {storagePolicy.MaxChunkSizeBytes}-byte limit."));
        }

        if (descriptor.ChunkLength > descriptor.FileSizeBytes)
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.InvalidChunkLength,
                "File-transfer chunk length cannot exceed the declared file size.");
        }

        return FileTransferAccessResult.Allowed();
    }

    public FileTransferWriteOpenResult OpenReceiveWriteStream(
        bool hasSecurityTransport,
        SessionSecurityState securityState,
        SessionGrant? grant,
        FileTransferDescriptor descriptor,
        FileTransferStoragePolicy storagePolicy)
    {
        ArgumentNullException.ThrowIfNull(securityState);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(storagePolicy);

        var validation = ValidateReceiveMetadata(
            hasSecurityTransport,
            securityState,
            grant,
            descriptor,
            storagePolicy);
        if (!validation.IsAllowed)
        {
            return new FileTransferWriteOpenResult(validation);
        }

        if (!TryCreateWritePlan(descriptor, storagePolicy, out var plan, out var planValidation))
        {
            return new FileTransferWriteOpenResult(planValidation);
        }

        try
        {
            Directory.CreateDirectory(plan.RootDirectoryPath);
            if (Directory.Exists(plan.FinalPath))
            {
                return new FileTransferWriteOpenResult(
                    FileTransferAccessResult.Denied(
                        FileTransferValidationFailure.DirectoryTargetBlocked,
                        "File-transfer target path resolves to a directory."),
                    plan);
            }

            if (Directory.Exists(plan.TempPath))
            {
                return new FileTransferWriteOpenResult(
                    FileTransferAccessResult.Denied(
                        FileTransferValidationFailure.DirectoryTargetBlocked,
                        "File-transfer temporary path resolves to a directory."),
                    plan);
            }

            var options = new FileStreamOptions
            {
                Access = FileAccess.ReadWrite,
                Mode = FileMode.CreateNew,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.RandomAccess,
                BufferSize = Math.Clamp(storagePolicy.MaxChunkSizeBytes, 4096, 64 * 1024),
            };
            var stream = new FileStream(plan.TempPath, options);
            return new FileTransferWriteOpenResult(
                FileTransferAccessResult.Allowed(),
                plan,
                new FileTransferWriteHandle(plan, stream));
        }
        catch (IOException) when (!storagePolicy.AllowOverwrite && (File.Exists(plan.FinalPath) || Directory.Exists(plan.FinalPath)))
        {
            return new FileTransferWriteOpenResult(
                FileTransferAccessResult.Denied(
                    FileTransferValidationFailure.OverwriteBlocked,
                    "File-transfer target already exists and overwrite is disabled."),
                plan);
        }
        catch (Exception ex)
        {
            return new FileTransferWriteOpenResult(
                FileTransferAccessResult.Denied(
                    FileTransferValidationFailure.OpenFailed,
                    $"Could not open the file-transfer target for writing: {ex.Message}"),
                plan);
        }
    }

    private static FileTransferAccessResult ValidateBinding(
        SessionSecurityState securityState,
        SessionId descriptorSessionId,
        PeerAddress descriptorHelperIdentity)
    {
        if (securityState.SessionId is not SessionId activeSessionId)
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.SessionIdMissing,
                "File-transfer session binding is unavailable.");
        }

        if (descriptorSessionId != activeSessionId)
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.SessionIdMismatch,
                "File-transfer session id does not match the active session.");
        }

        if (securityState.HelperAddress is not PeerAddress activeHelperIdentity)
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.HelperIdentityMissing,
                "File-transfer helper identity is unavailable.");
        }

        if (descriptorHelperIdentity != activeHelperIdentity)
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.HelperIdentityMismatch,
                "File-transfer helper identity does not match the approved helper.");
        }

        return FileTransferAccessResult.Allowed();
    }

    private static FileTransferAccessResult ValidateFileSize(long fileSizeBytes, long maxFileSizeBytes)
    {
        if (fileSizeBytes <= 0)
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.InvalidFileSize,
                "File-transfer size must be positive.");
        }

        if (maxFileSizeBytes <= 0)
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.FileTooLarge,
                "File-transfer size limit is invalid.");
        }

        if (fileSizeBytes > maxFileSizeBytes)
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.FileTooLarge,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"File-transfer size exceeds the {maxFileSizeBytes}-byte limit."));
        }

        return FileTransferAccessResult.Allowed();
    }

    private static bool TryCreateWritePlan(
        FileTransferDescriptor descriptor,
        FileTransferStoragePolicy storagePolicy,
        out FileTransferWritePlan plan,
        out FileTransferAccessResult validation)
    {
        plan = default!;
        validation = ValidateStoragePolicy(storagePolicy);
        if (!validation.IsAllowed)
        {
            return false;
        }

        validation = ValidateSafeFileName(descriptor.FileName, out var safeFileName);
        if (!validation.IsAllowed)
        {
            return false;
        }

        var rootPath = Path.GetFullPath(storagePolicy.RootDirectoryPath.Trim());
        if (!TryResolveAvailableSafeFileName(rootPath, safeFileName, storagePolicy.AllowOverwrite, out var resolvedSafeFileName, out validation))
        {
            return false;
        }

        var candidatePath = Path.GetFullPath(Path.Combine(rootPath, resolvedSafeFileName));
        if (!IsPathWithinRoot(rootPath, candidatePath))
        {
            validation = FileTransferAccessResult.Denied(
                FileTransferValidationFailure.WriteOutsideAllowedDirectory,
                "File-transfer target path escapes the allowed directory.");
            return false;
        }

        var tempFileName = CreateTempFileName(resolvedSafeFileName);
        plan = new FileTransferWritePlan(
            rootPath,
            resolvedSafeFileName,
            tempFileName,
            Path.Combine(rootPath, tempFileName),
            candidatePath,
            storagePolicy.AllowOverwrite,
            descriptor.FileSizeBytes,
            storagePolicy.MaxChunkSizeBytes);
        validation = FileTransferAccessResult.Allowed();
        return true;
    }

    private static FileTransferAccessResult ValidateStoragePolicy(FileTransferStoragePolicy storagePolicy)
    {
        if (string.IsNullOrWhiteSpace(storagePolicy.RootDirectoryPath))
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.InvalidRootDirectory,
                "File-transfer root directory is required.");
        }

        try
        {
            var rootPath = Path.GetFullPath(storagePolicy.RootDirectoryPath.Trim());
            if (File.Exists(rootPath))
            {
                return FileTransferAccessResult.Denied(
                    FileTransferValidationFailure.InvalidRootDirectory,
                    "File-transfer root directory cannot point to a file.");
            }

            if (storagePolicy.MaxFileSizeBytes <= 0)
            {
                return FileTransferAccessResult.Denied(
                    FileTransferValidationFailure.FileTooLarge,
                    "File-transfer size limit must be positive.");
            }

            if (storagePolicy.MaxChunkSizeBytes <= 0)
            {
                return FileTransferAccessResult.Denied(
                    FileTransferValidationFailure.ChunkTooLarge,
                    "File-transfer chunk limit must be positive.");
            }

            _ = rootPath;
            return FileTransferAccessResult.Allowed();
        }
        catch (Exception ex)
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.InvalidRootDirectory,
                $"File-transfer root directory is invalid: {ex.Message}");
        }
    }

    private static FileTransferAccessResult ValidateSafeFileName(string fileName, out string safeFileName)
    {
        safeFileName = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.InvalidFileName,
                "File-transfer file name is required.");
        }

        var normalized = fileName.Trim();
        if (normalized.Length > MaxSafeFileNameLength)
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.InvalidFileName,
                $"File-transfer file name exceeds the {MaxSafeFileNameLength}-character limit.");
        }

        if (normalized is "." or ".." ||
            normalized.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
            normalized.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
            !string.Equals(Path.GetFileName(normalized), normalized, StringComparison.Ordinal))
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.PathTraversalDetected,
                "File-transfer file name cannot contain path traversal or directory separators.");
        }

        if (normalized.EndsWith(' ') || normalized.EndsWith('.'))
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.InvalidFileName,
                "File-transfer file name cannot end with a space or period.");
        }

        foreach (var ch in normalized)
        {
            if (ch < ' ' ||
                ConservativeInvalidFileNameChars.Contains(ch) ||
                Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0)
            {
                return FileTransferAccessResult.Denied(
                    FileTransferValidationFailure.InvalidFileName,
                    $"File-transfer file name contains unsupported character '{ch}'.");
            }
        }

        var stem = Path.GetFileNameWithoutExtension(normalized);
        if (ReservedWindowsDeviceNames.Contains(stem))
        {
            return FileTransferAccessResult.Denied(
                FileTransferValidationFailure.InvalidFileName,
                "File-transfer file name targets a reserved device path.");
        }

        safeFileName = normalized;
        return FileTransferAccessResult.Allowed();
    }

    private static bool TryResolveAvailableSafeFileName(
        string rootPath,
        string safeFileName,
        bool allowOverwrite,
        out string resolvedSafeFileName,
        out FileTransferAccessResult validation)
    {
        resolvedSafeFileName = safeFileName;
        validation = FileTransferAccessResult.Allowed();
        if (allowOverwrite)
        {
            return true;
        }

        for (var attempt = 0; attempt <= 999; attempt++)
        {
            var candidateSafeFileName = attempt == 0
                ? safeFileName
                : CreateNumberedSafeFileName(safeFileName, attempt);
            var candidatePath = Path.GetFullPath(Path.Combine(rootPath, candidateSafeFileName));
            if (!IsPathWithinRoot(rootPath, candidatePath))
            {
                validation = FileTransferAccessResult.Denied(
                    FileTransferValidationFailure.WriteOutsideAllowedDirectory,
                    "File-transfer target path escapes the allowed directory.");
                return false;
            }

            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
            {
                resolvedSafeFileName = candidateSafeFileName;
                return true;
            }
        }

        validation = FileTransferAccessResult.Denied(
            FileTransferValidationFailure.OverwriteBlocked,
            "File-transfer target already exists and no available numbered file name could be found.");
        return false;
    }

    private static string CreateNumberedSafeFileName(string safeFileName, int number)
    {
        var extension = Path.GetExtension(safeFileName);
        var stem = Path.GetFileNameWithoutExtension(safeFileName);
        if (string.IsNullOrEmpty(stem))
        {
            stem = safeFileName;
            extension = string.Empty;
        }

        var suffix = string.Create(CultureInfo.InvariantCulture, $" ({number})");
        var maxStemLength = Math.Max(1, MaxSafeFileNameLength - suffix.Length - extension.Length);
        if (stem.Length > maxStemLength)
        {
            stem = stem[..maxStemLength].TrimEnd(' ', '.');
            if (stem.Length == 0)
            {
                stem = "file";
            }
        }

        return string.Concat(stem, suffix, extension);
    }

    private static bool IsPathWithinRoot(string rootPath, string candidatePath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(rootPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var expectedPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(expectedPrefix, comparison);
    }

    private static string CreateTempFileName(string safeFileName)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{safeFileName}.nlink-{Guid.NewGuid():N}.part");
    }
}
