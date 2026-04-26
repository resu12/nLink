using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using NLink.App;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Metrics;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.Retry;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Core.Logging;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class NknIdentityStoreAndStartupCleanupTests : SessionRuntimeConnectionTestBase
{
    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknIdentityStore_OnWindows_PersistsSeedOnlyInProtectedSidecar()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-protected-seed-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keyPath = Path.Combine(tempDir, "identity.json");
            var options = LoadNknOptionsWithOverrides(keyPath, "protected-seed-test");
            var identity = NknIdentityStore.LoadOrCreate(options);
            Assert.False(string.IsNullOrWhiteSpace(identity.Address));
            Assert.True(File.Exists(keyPath));
            var secretPath = NknSecretStore.GetSecretPath(keyPath);
            Assert.True(File.Exists(secretPath));
            using var identityDoc = JsonDocument.Parse(File.ReadAllText(keyPath));
            var root = identityDoc.RootElement;
            Assert.Equal(3, root.GetProperty("Version").GetInt32());
            Assert.Equal("protected-seed-test", root.GetProperty("Identifier").GetString());
            Assert.Equal(identity.Address, root.GetProperty("Address").GetString());
            Assert.True(root.TryGetProperty("SeedBase64", out var seedProp));
            Assert.Equal(JsonValueKind.Null, seedProp.ValueKind);
            var protectedSecretText = File.ReadAllText(secretPath).Trim();
            Assert.False(string.IsNullOrWhiteSpace(protectedSecretText));
            Assert.DoesNotContain("\"SeedBase64\": \"", File.ReadAllText(keyPath), StringComparison.Ordinal);
            var connectSeedBase64 = NknIdentityStore.ReadSeedBase64ForConnect(keyPath);
            Assert.False(string.IsNullOrWhiteSpace(connectSeedBase64));
        }
        finally
        {
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknIdentityStore_OnWindows_ManualRegenerate_ChangesAddressAndKeepsProtectedSeedSidecar()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        PersistenceDiagnostics.ClearForTests();
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-protected-seed-regenerate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keyPath = Path.Combine(tempDir, "identity.json");
            var options = LoadNknOptionsWithOverrides(keyPath, "protected-seed-regenerate-test");
            var originalIdentity = NknIdentityStore.LoadOrCreate(options);
            var regeneratedIdentity = NknIdentityStore.Regenerate(options);

            Assert.False(string.IsNullOrWhiteSpace(regeneratedIdentity.Address));
            Assert.NotEqual(originalIdentity.Address, regeneratedIdentity.Address);
            Assert.True(File.Exists(keyPath));
            Assert.True(File.Exists(NknSecretStore.GetSecretPath(keyPath)));

            using var identityDoc = JsonDocument.Parse(File.ReadAllText(keyPath));
            var root = identityDoc.RootElement;
            Assert.Equal(3, root.GetProperty("Version").GetInt32());
            Assert.Equal("protected-seed-regenerate-test", root.GetProperty("Identifier").GetString());
            Assert.Equal(regeneratedIdentity.Address, root.GetProperty("Address").GetString());
            Assert.True(root.TryGetProperty("SeedBase64", out var seedProp));
            Assert.Equal(JsonValueKind.Null, seedProp.ValueKind);

            var snapshot = PersistenceDiagnostics.Snapshot();
            Assert.Contains(snapshot.RecentEvents, e => string.Equals(e.Operation, "manual_identity_regeneration", StringComparison.Ordinal));
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknIdentityStore_OnWindows_MigratesLegacySeedBase64_ToProtectedStore()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-protected-seed-migrate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keyPath = Path.Combine(tempDir, "identity.json");
            var legacySeedBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("legacy-seed-material-for-migration"));
            var createdUtc = DateTimeOffset.UtcNow.AddDays(-1);
            File.WriteAllText(keyPath, JsonSerializer.Serialize(new { Version = 1, CreatedUtc = createdUtc, Identifier = "legacy-protected-seed-test", SeedBase64 = legacySeedBase64, Address = (string? )null, }, new JsonSerializerOptions { WriteIndented = true }));
            var options = LoadNknOptionsWithOverrides(keyPath, "legacy-protected-seed-test");
            var identity = NknIdentityStore.LoadOrCreate(options);
            using var identityDoc = JsonDocument.Parse(File.ReadAllText(keyPath));
            var root = identityDoc.RootElement;
            Assert.Equal(3, root.GetProperty("Version").GetInt32());
            Assert.Equal("legacy-protected-seed-test", root.GetProperty("Identifier").GetString());
            Assert.Equal(identity.Address, root.GetProperty("Address").GetString());
            Assert.True(root.TryGetProperty("SeedBase64", out var seedProp));
            Assert.Equal(JsonValueKind.Null, seedProp.ValueKind);
            var secretPath = NknSecretStore.GetSecretPath(keyPath);
            Assert.True(File.Exists(secretPath));
            var migratedSeedBase64 = NknIdentityStore.ReadSeedBase64ForConnect(keyPath);
            Assert.Equal(legacySeedBase64, migratedSeedBase64);
        }
        finally
        {
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknIdentityStore_OnWindows_MissingProtectedSeed_DoesNotSilentlyRotateIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-protected-seed-missing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keyPath = Path.Combine(tempDir, "identity.json");
            var options = LoadNknOptionsWithOverrides(keyPath, "missing-protected-seed-test");
            var identity = NknIdentityStore.LoadOrCreate(options);
            var originalJson = File.ReadAllText(keyPath);
            NknSecretStore.DeleteSeed(keyPath);
            var ex = Assert.Throws<InvalidOperationException>(() => NknIdentityStore.LoadOrCreate(options));
            Assert.Contains("missing recoverable seed material", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(originalJson, File.ReadAllText(keyPath));
            Assert.Equal(identity.Address, JsonDocument.Parse(originalJson).RootElement.GetProperty("Address").GetString());
        }
        finally
        {
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknIdentityStore_DefaultSharedIdentity_WithCorruptedProtectedSeed_QuarantinesAndRecreatesIdentity()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-protected-seed-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        PersistenceDiagnostics.ClearForTests();
        try
        {
            var keyPath = Path.Combine(tempDir, "identity.json");
            var options = LoadNknOptionsWithOverrides(keyPath, "default-shared-recovery-test");
            var backend = new CorruptedProtectedSeedBackend();
            using var backendOverride = NknSecretStore.OverrideBackendForTests(backend);
            using var sharedPathOverride = NknIdentityStore.OverrideDefaultSharedKeyPathForTests(keyPath);
            var recoveryDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nLink", "security", "identity-recovery");
            var beforeRecoveryFiles = Directory.Exists(recoveryDir) ? Directory.GetFiles(recoveryDir).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var originalIdentity = NknIdentityStore.LoadOrCreate(options);
            var originalJson = File.ReadAllText(keyPath);
            backend.ThrowOnLoad = true;
            var recoveredIdentity = NknIdentityStore.LoadOrCreate(options);
            Assert.NotEqual(originalIdentity.Address, recoveredIdentity.Address);
            Assert.NotEqual(originalJson, File.ReadAllText(keyPath));
            Assert.True(File.Exists(keyPath));
            Assert.True(File.Exists(NknSecretStore.GetSecretPath(keyPath)));
            Assert.True(Directory.Exists(recoveryDir));
            var newRecoveryFiles = Directory.GetFiles(recoveryDir).Where(path => !beforeRecoveryFiles.Contains(path)).ToArray();
            Assert.Contains(newRecoveryFiles, path => Path.GetFileName(path).StartsWith("identity.", StringComparison.Ordinal) && Path.GetFileName(path).Contains(".corrupt.json", StringComparison.Ordinal));
            Assert.Contains(newRecoveryFiles, path => Path.GetFileName(path).StartsWith("identity.json.", StringComparison.Ordinal) && Path.GetFileName(path).Contains(".corrupt.seed", StringComparison.Ordinal));
            var snapshot = PersistenceDiagnostics.Snapshot();
            Assert.Equal("Local protected identity storage was unreadable. nLink created a new local identity. Previous helper address and invites are no longer valid.", snapshot.LastWarning);
            Assert.Contains(snapshot.RecentEvents, e => string.Equals(e.Operation, "detect_corrupted_protected_seed", StringComparison.Ordinal));
            Assert.Contains(snapshot.RecentEvents, e => string.Equals(e.Operation, "quarantine_corrupted_identity", StringComparison.Ordinal));
            Assert.Contains(snapshot.RecentEvents, e => string.Equals(e.Operation, "automatic_identity_recovery", StringComparison.Ordinal));
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknIdentityStore_CustomKeyPath_WithCorruptedProtectedSeed_DoesNotAutoRotate()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-protected-seed-no-auto-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        PersistenceDiagnostics.ClearForTests();
        try
        {
            var keyPath = Path.Combine(tempDir, "custom-identity.json");
            var options = LoadNknOptionsWithOverrides(keyPath, "custom-seed-corruption-test");
            var backend = new CorruptedProtectedSeedBackend();
            using var backendOverride = NknSecretStore.OverrideBackendForTests(backend);
            var originalIdentity = NknIdentityStore.LoadOrCreate(options);
            backend.ThrowOnLoad = true;
            var ex = Assert.Throws<CryptographicException>(() => NknIdentityStore.LoadOrCreate(options));
            Assert.Contains("data", ex.Message, StringComparison.OrdinalIgnoreCase);
            using var identityDoc = JsonDocument.Parse(File.ReadAllText(keyPath));
            Assert.Equal(originalIdentity.Address, identityDoc.RootElement.GetProperty("Address").GetString());
            var snapshot = PersistenceDiagnostics.Snapshot();
            Assert.DoesNotContain(snapshot.RecentEvents, e => string.Equals(e.Operation, "automatic_identity_recovery", StringComparison.Ordinal));
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknIdentityStore_CustomInstanceLikeKeyPath_WithCorruptedProtectedSeed_DoesNotAutoRotate()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-protected-seed-custom-instance-like", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        PersistenceDiagnostics.ClearForTests();
        try
        {
            var keyPath = Path.Combine(tempDir, "identity.instance-4242.json");
            var options = LoadNknOptionsWithOverrides(keyPath, "custom-instance-like-corruption-test");
            var backend = new CorruptedProtectedSeedBackend();
            using var backendOverride = NknSecretStore.OverrideBackendForTests(backend);
            var originalIdentity = NknIdentityStore.LoadOrCreate(options);
            backend.ThrowOnLoad = true;
            var ex = Assert.Throws<CryptographicException>(() => NknIdentityStore.LoadOrCreate(options));
            Assert.Contains("data", ex.Message, StringComparison.OrdinalIgnoreCase);
            using var identityDoc = JsonDocument.Parse(File.ReadAllText(keyPath));
            Assert.Equal(originalIdentity.Address, identityDoc.RootElement.GetProperty("Address").GetString());
            var snapshot = PersistenceDiagnostics.Snapshot();
            Assert.DoesNotContain(snapshot.RecentEvents, e => string.Equals(e.Operation, "automatic_identity_recovery", StringComparison.Ordinal));
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknIdentityStore_PerProcessIdentity_WithCorruptedProtectedSeed_QuarantinesAndRecreatesIdentity()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-protected-seed-instance-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        PersistenceDiagnostics.ClearForTests();
        var previousIdentifier = Environment.GetEnvironmentVariable("NLINK_NKN_IDENTIFIER");
        try
        {
            using var localAppDataOverride = NknTransportOptions.OverrideLocalAppDataPathForTests(tempDir);
            using var perProcessOverride = NknTransportOptions.OverrideShouldUsePerProcessLocalIdentityForTests(true);
            Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", "per-process-recovery-test");
            var options = NknTransportOptions.Load();
            var keyPath = options.KeyPath;
            var backend = new CorruptedProtectedSeedBackend();
            using var backendOverride = NknSecretStore.OverrideBackendForTests(backend);
            var recoveryDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nLink", "security", "identity-recovery");
            var beforeRecoveryFiles = Directory.Exists(recoveryDir) ? Directory.GetFiles(recoveryDir).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var originalIdentity = NknIdentityStore.LoadOrCreate(options);
            var originalJson = File.ReadAllText(keyPath);
            backend.ThrowOnLoad = true;
            var recoveredIdentity = NknIdentityStore.LoadOrCreate(options);
            Assert.NotEqual(originalIdentity.Address, recoveredIdentity.Address);
            Assert.NotEqual(originalJson, File.ReadAllText(keyPath));
            Assert.True(File.Exists(keyPath));
            Assert.True(File.Exists(NknSecretStore.GetSecretPath(keyPath)));
            Assert.True(Directory.Exists(recoveryDir));
            var newRecoveryFiles = Directory.GetFiles(recoveryDir).Where(path => !beforeRecoveryFiles.Contains(path)).ToArray();
            var expectedPrefix = Path.GetFileNameWithoutExtension(keyPath);
            Assert.Contains(newRecoveryFiles, path => Path.GetFileName(path).StartsWith(expectedPrefix + ".", StringComparison.Ordinal) && Path.GetFileName(path).Contains(".corrupt.json", StringComparison.Ordinal));
            Assert.Contains(newRecoveryFiles, path => Path.GetFileName(path).StartsWith(Path.GetFileName(keyPath) + ".", StringComparison.Ordinal) && Path.GetFileName(path).Contains(".corrupt.seed", StringComparison.Ordinal));
            var snapshot = PersistenceDiagnostics.Snapshot();
            Assert.Contains(snapshot.RecentEvents, e => string.Equals(e.Operation, "automatic_identity_recovery", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", previousIdentifier);
            PersistenceDiagnostics.ClearForTests();
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknIdentityStore_DefaultSharedIdentity_WithBlankProtectedSeedFile_QuarantinesAndRecreatesIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-protected-seed-blank-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        PersistenceDiagnostics.ClearForTests();
        try
        {
            var keyPath = Path.Combine(tempDir, "identity.json");
            var options = LoadNknOptionsWithOverrides(keyPath, "blank-protected-seed-test");
            using var sharedPathOverride = NknIdentityStore.OverrideDefaultSharedKeyPathForTests(keyPath);
            var originalIdentity = NknIdentityStore.LoadOrCreate(options);
            File.WriteAllText(NknSecretStore.GetSecretPath(keyPath), string.Empty);
            var recoveredIdentity = NknIdentityStore.LoadOrCreate(options);
            Assert.NotEqual(originalIdentity.Address, recoveredIdentity.Address);
            Assert.True(File.Exists(keyPath));
            Assert.True(File.Exists(NknSecretStore.GetSecretPath(keyPath)));
            var snapshot = PersistenceDiagnostics.Snapshot();
            Assert.Contains(snapshot.RecentEvents, e => string.Equals(e.Operation, "automatic_identity_recovery", StringComparison.Ordinal));
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknTransportOptions_StartupCleanup_DeletesOnlyStalePerProcessIdentityFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-instance-cleanup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        PersistenceDiagnostics.ClearForTests();
        try
        {
            var localAppData = Path.Combine(tempDir, "appdata");
            var nlinkDir = Path.Combine(localAppData, "nLink");
            Directory.CreateDirectory(nlinkDir);
            var sharedIdentityPath = Path.Combine(nlinkDir, "identity.json");
            var staleIdentityPath = Path.Combine(nlinkDir, "identity.instance-4242.json");
            var runningIdentityPath = Path.Combine(nlinkDir, $"identity.instance-{Environment.ProcessId}.json");
            var malformedIdentityPath = Path.Combine(nlinkDir, "identity.instance-bad.json");
            var orphanSeedPath = Path.Combine(nlinkDir, "identity.instance-9999.json.seed");
            WriteIdentityFile(sharedIdentityPath, "shared-test");
            WriteIdentityFile(staleIdentityPath, "stale-test");
            WriteIdentityFile(runningIdentityPath, "running-test");
            WriteIdentityFile(malformedIdentityPath, "malformed-test");
            File.WriteAllText(orphanSeedPath, "orphan-seed");
            using var localAppDataOverride = NknTransportOptions.OverrideLocalAppDataPathForTests(localAppData);
            using var perProcessOverride = NknTransportOptions.OverrideShouldUsePerProcessLocalIdentityForTests(true);
            using var runningOverride = NknTransportOptions.OverrideIsProcessRunningForTests(pid => pid == Environment.ProcessId);
            var options = NknTransportOptions.Load();
            Assert.Equal(runningIdentityPath, options.KeyPath);
            Assert.True(File.Exists(sharedIdentityPath));
            Assert.True(File.Exists(NknSecretStore.GetSecretPath(sharedIdentityPath)));
            Assert.False(File.Exists(staleIdentityPath));
            Assert.False(File.Exists(NknSecretStore.GetSecretPath(staleIdentityPath)));
            Assert.True(File.Exists(runningIdentityPath));
            Assert.True(File.Exists(NknSecretStore.GetSecretPath(runningIdentityPath)));
            Assert.True(File.Exists(malformedIdentityPath));
            Assert.True(File.Exists(NknSecretStore.GetSecretPath(malformedIdentityPath)));
            Assert.False(File.Exists(orphanSeedPath));
            var snapshot = PersistenceDiagnostics.Snapshot();
            Assert.Contains(snapshot.RecentEvents, e => string.Equals(e.Operation, "cleanup_stale_instance_identities", StringComparison.Ordinal));
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknTransportOptions_StartupCleanup_CustomKeyPath_SkipsCleanup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-instance-cleanup-custom", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        PersistenceDiagnostics.ClearForTests();
        try
        {
            var localAppData = Path.Combine(tempDir, "appdata");
            var nlinkDir = Path.Combine(localAppData, "nLink");
            Directory.CreateDirectory(nlinkDir);
            var staleIdentityPath = Path.Combine(nlinkDir, "identity.instance-4242.json");
            WriteIdentityFile(staleIdentityPath, "stale-custom-test");
            var customKeyPath = Path.Combine(tempDir, "custom", "identity.json");
            using var localAppDataOverride = NknTransportOptions.OverrideLocalAppDataPathForTests(localAppData);
            using var perProcessOverride = NknTransportOptions.OverrideShouldUsePerProcessLocalIdentityForTests(true);
            var options = LoadNknOptionsWithOverrides(customKeyPath, "custom-key-path-test");
            Assert.Equal(Path.GetFullPath(customKeyPath), options.KeyPath);
            Assert.True(File.Exists(staleIdentityPath));
            Assert.True(File.Exists(NknSecretStore.GetSecretPath(staleIdentityPath)));
            var snapshot = PersistenceDiagnostics.Snapshot();
            Assert.DoesNotContain(snapshot.RecentEvents, e => string.Equals(e.Operation, "cleanup_stale_instance_identities", StringComparison.Ordinal));
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknTransportOptions_StartupCleanup_RunningPerProcessIdentity_IsPreserved()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-instance-cleanup-running", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        PersistenceDiagnostics.ClearForTests();
        try
        {
            var localAppData = Path.Combine(tempDir, "appdata");
            var nlinkDir = Path.Combine(localAppData, "nLink");
            Directory.CreateDirectory(nlinkDir);
            var runningIdentityPath = Path.Combine(nlinkDir, "identity.instance-4242.json");
            WriteIdentityFile(runningIdentityPath, "running-preserve-test");
            using var localAppDataOverride = NknTransportOptions.OverrideLocalAppDataPathForTests(localAppData);
            using var perProcessOverride = NknTransportOptions.OverrideShouldUsePerProcessLocalIdentityForTests(true);
            using var runningOverride = NknTransportOptions.OverrideIsProcessRunningForTests(pid => pid == 4242);
            var options = NknTransportOptions.Load();
            Assert.Equal(Path.Combine(nlinkDir, $"identity.instance-{Environment.ProcessId}.json"), options.KeyPath);
            Assert.True(File.Exists(runningIdentityPath));
            Assert.True(File.Exists(NknSecretStore.GetSecretPath(runningIdentityPath)));
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknTransportOptions_StartupCleanup_DeleteFailure_LeavesFilesAndRecordsPartialResult()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-instance-cleanup-delete-failure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        PersistenceDiagnostics.ClearForTests();
        try
        {
            var localAppData = Path.Combine(tempDir, "appdata");
            var nlinkDir = Path.Combine(localAppData, "nLink");
            Directory.CreateDirectory(nlinkDir);
            var staleIdentityPath = Path.Combine(nlinkDir, "identity.instance-4242.json");
            WriteIdentityFile(staleIdentityPath, "stale-delete-failure-test");
            using var localAppDataOverride = NknTransportOptions.OverrideLocalAppDataPathForTests(localAppData);
            using var perProcessOverride = NknTransportOptions.OverrideShouldUsePerProcessLocalIdentityForTests(true);
            using var runningOverride = NknTransportOptions.OverrideIsProcessRunningForTests(_ => false);
            using var deleteOverride = NknTransportOptions.OverrideDeleteFileForTests(path =>
            {
                throw new UnauthorizedAccessException(path);
            });
            _ = NknTransportOptions.Load();
            Assert.True(File.Exists(staleIdentityPath));
            Assert.True(File.Exists(NknSecretStore.GetSecretPath(staleIdentityPath)));
            var snapshot = PersistenceDiagnostics.Snapshot();
            Assert.Contains(snapshot.RecentEvents, e => string.Equals(e.Operation, "cleanup_stale_instance_identities", StringComparison.Ordinal) && e.Severity == PersistenceDiagnosticSeverity.Warning && e.Outcome == PersistenceDiagnosticOutcome.Partial);
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknTransportOptions_StartupCleanup_EnumerationFailure_IsBestEffort()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-instance-cleanup-enumerate-failure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        PersistenceDiagnostics.ClearForTests();
        try
        {
            var localAppData = Path.Combine(tempDir, "appdata");
            var nlinkDir = Path.Combine(localAppData, "nLink");
            Directory.CreateDirectory(nlinkDir);
            using var localAppDataOverride = NknTransportOptions.OverrideLocalAppDataPathForTests(localAppData);
            using var enumerateOverride = NknTransportOptions.OverrideEnumerateInstanceIdentityFilesForTests(_ => throw new UnauthorizedAccessException("enumeration failed"));
            var options = NknTransportOptions.Load();
            Assert.StartsWith(nlinkDir + Path.DirectorySeparatorChar, options.KeyPath, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("identity", Path.GetFileName(options.KeyPath), StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".json", options.KeyPath, StringComparison.OrdinalIgnoreCase);
            var snapshot = PersistenceDiagnostics.Snapshot();
            Assert.Contains(snapshot.RecentEvents, e => string.Equals(e.Operation, "cleanup_stale_instance_identities", StringComparison.Ordinal) && e.Severity == PersistenceDiagnosticSeverity.Warning && e.Outcome == PersistenceDiagnosticOutcome.Partial);
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknTransportOptions_StartupCleanup_DeletesStaleSeed_ThroughSecretStoreBackend()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-instance-cleanup-backend-seed", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        PersistenceDiagnostics.ClearForTests();
        try
        {
            var localAppData = Path.Combine(tempDir, "appdata");
            var nlinkDir = Path.Combine(localAppData, "nLink");
            Directory.CreateDirectory(nlinkDir);
            var staleIdentityPath = Path.Combine(nlinkDir, "identity.instance-4242.json");
            WriteIdentityFile(staleIdentityPath, "stale-backend-delete-test");
            File.Delete(NknSecretStore.GetSecretPath(staleIdentityPath));
            var backend = new FakeProtectedSeedBackend();
            backend.SaveSeed(staleIdentityPath, new byte[] { 1, 2, 3, 4 });
            using var backendOverride = NknSecretStore.OverrideBackendForTests(backend);
            using var localAppDataOverride = NknTransportOptions.OverrideLocalAppDataPathForTests(localAppData);
            using var perProcessOverride = NknTransportOptions.OverrideShouldUsePerProcessLocalIdentityForTests(true);
            using var runningOverride = NknTransportOptions.OverrideIsProcessRunningForTests(_ => false);
            _ = NknTransportOptions.Load();
            Assert.False(File.Exists(staleIdentityPath));
            Assert.False(backend.StoredSeeds.ContainsKey(Path.GetFullPath(staleIdentityPath)));
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknIdentityStore_OnWindows_InvalidLegacySeed_DoesNotSilentlyRotateIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-invalid-legacy-seed", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keyPath = Path.Combine(tempDir, "identity.json");
            File.WriteAllText(keyPath, JsonSerializer.Serialize(new { Version = 1, CreatedUtc = DateTimeOffset.UtcNow, Identifier = "invalid-legacy-seed-test", SeedBase64 = "not-valid-base64", Address = "invalid-legacy-seed-test.staleaddr", }, new JsonSerializerOptions { WriteIndented = true }));
            var options = LoadNknOptionsWithOverrides(keyPath, "invalid-legacy-seed-test");
            var ex = Assert.Throws<InvalidOperationException>(() => NknIdentityStore.LoadOrCreate(options));
            Assert.Contains("missing recoverable seed material", ex.Message, StringComparison.OrdinalIgnoreCase);
            using var identityDoc = JsonDocument.Parse(File.ReadAllText(keyPath));
            var root = identityDoc.RootElement;
            Assert.Equal("invalid-legacy-seed-test", root.GetProperty("Identifier").GetString());
            Assert.Equal("invalid-legacy-seed-test.staleaddr", root.GetProperty("Address").GetString());
        }
        finally
        {
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknIdentityStore_OnWindows_ReadSeedBase64ForConnect_MigratesLegacyJsonSeed_ToProtectedStore()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-connect-seed-no-legacy-fallback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keyPath = Path.Combine(tempDir, "identity.json");
            File.WriteAllText(keyPath, JsonSerializer.Serialize(new { Version = 1, CreatedUtc = DateTimeOffset.UtcNow, Identifier = "connect-seed-no-legacy-fallback", SeedBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("legacy-only-seed")), Address = "connect-seed-no-legacy-fallback.staleaddr", }, new JsonSerializerOptions { WriteIndented = true }));
            var seedBase64 = NknIdentityStore.ReadSeedBase64ForConnect(keyPath);
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("legacy-only-seed")), seedBase64);
            using var identityDoc = JsonDocument.Parse(File.ReadAllText(keyPath));
            Assert.Equal(JsonValueKind.Null, identityDoc.RootElement.GetProperty("SeedBase64").ValueKind);
        }
        finally
        {
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknIdentityStore_WithInjectedProtectedBackend_MigratesLegacySeedBase64_AndClearsJsonSeed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-protected-seed-migrate-injected", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keyPath = Path.Combine(tempDir, "identity.json");
            var legacySeedBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("legacy-seed-material-cross-platform"));
            File.WriteAllText(keyPath, JsonSerializer.Serialize(new { Version = 1, CreatedUtc = DateTimeOffset.UtcNow.AddDays(-1), Identifier = "cross-platform-protected-seed-test", SeedBase64 = legacySeedBase64, Address = (string? )null, }, new JsonSerializerOptions { WriteIndented = true }));
            var backend = new FakeProtectedSeedBackend();
            using var backendOverride = NknSecretStore.OverrideBackendForTests(backend);
            var options = LoadNknOptionsWithOverrides(keyPath, "cross-platform-protected-seed-test");
            var identity = NknIdentityStore.LoadOrCreate(options);
            Assert.True(backend.StoredSeeds.TryGetValue(Path.GetFullPath(keyPath), out var migratedSeed));
            Assert.Equal(legacySeedBase64, Convert.ToBase64String(migratedSeed!));
            using var identityDoc = JsonDocument.Parse(File.ReadAllText(keyPath));
            var root = identityDoc.RootElement;
            Assert.Equal(3, root.GetProperty("Version").GetInt32());
            Assert.Equal("cross-platform-protected-seed-test", root.GetProperty("Identifier").GetString());
            Assert.Equal(identity.Address, root.GetProperty("Address").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("SeedBase64").ValueKind);
            var connectSeedBase64 = NknIdentityStore.ReadSeedBase64ForConnect(keyPath);
            Assert.Equal(legacySeedBase64, connectSeedBase64);
        }
        finally
        {
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void NknIdentityStore_WithUnavailableProtectedBackend_FailsClosed_WithoutWritingIdentity()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nlink-protected-seed-unavailable", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keyPath = Path.Combine(tempDir, "identity.json");
            using var backendOverride = NknSecretStore.OverrideBackendForTests(new UnavailableProtectedSeedBackend());
            var options = LoadNknOptionsWithOverrides(keyPath, "protected-seed-unavailable-test");
            var ex = Assert.Throws<InvalidOperationException>(() => NknIdentityStore.LoadOrCreate(options));
            Assert.Contains("Protected NKN seed storage is unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(keyPath));
        }
        finally
        {
            try
            {
                CleanupDirectoryIfExists(tempDir);
            }
            catch
            {
            }
        }
    }

}
