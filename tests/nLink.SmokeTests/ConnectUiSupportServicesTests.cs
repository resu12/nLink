using NLink.App.Services;
using NLink.Core.Diagnostics;
using NLink.Core.SessionConnect;

namespace NLink.SmokeTests;

public sealed class ConnectUiSupportServicesTests
{
    [Trait("Category", "Smoke")]
    [Fact]
    public void QrCodeService_GenerateAndDecode_RoundTripsInviteToken()
    {
        var service = new QrCodeService();
        var source = "nlinki1.testpayload.testsignature";

        var generated = service.TryCreatePng(source, out var pngBytes, out var generateError);
        Assert.True(generated, generateError);
        Assert.NotEmpty(pngBytes);

        using var stream = new MemoryStream(pngBytes);
        var decoded = service.TryDecode(stream, out var decodedText, out var decodeError);
        Assert.True(decoded, decodeError);
        Assert.Equal(source, decodedText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void QrCodeService_GenerateAndDecode_RoundTripsWrappedInvitePayload()
    {
        var service = new QrCodeService();
        var source = InviteQrPayload.Format("nlinki1.testpayload.testsignature");

        var generated = service.TryCreatePng(source, out var pngBytes, out var generateError);
        Assert.True(generated, generateError);
        Assert.NotEmpty(pngBytes);

        using var stream = new MemoryStream(pngBytes);
        var decoded = service.TryDecode(stream, out var decodedText, out var decodeError);
        Assert.True(decoded, decodeError);
        Assert.Equal(source, decodedText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void RecentTargetsStore_PersistsSanitizedDistinctAddresses()
    {
        var tempFile = Path.Combine(
            Path.GetTempPath(),
            "nlink-tests",
            "recent-targets",
            $"{Guid.NewGuid():N}.json");

        try
        {
            var store = new LocalRecentConnectTargetsStore(tempFile);
            store.SaveTargets(
            [
                "nlink-peer.a1",
                "nlink-peer.a1",
                "invalid target",
                "nlink-peer.b2",
            ]);

            var loaded = store.LoadTargets();
            Assert.Equal(2, loaded.Count);
            Assert.Equal("nlink-peer.a1", loaded[0]);
            Assert.Equal("nlink-peer.b2", loaded[1]);
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }

                var parent = Path.GetDirectoryName(tempFile);
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                {
                    Directory.Delete(parent, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void RecentTargetsStore_LoadFailure_RecordsPersistenceDiagnostic_AndReturnsEmpty()
    {
        var tempFile = Path.Combine(
            Path.GetTempPath(),
            "nlink-tests",
            "recent-targets-invalid",
            $"{Guid.NewGuid():N}.json");

        try
        {
            PersistenceDiagnostics.ClearForTests();
            Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
            File.WriteAllText(tempFile, "{ invalid json");

            var store = new LocalRecentConnectTargetsStore(tempFile);
            var loaded = store.LoadTargets();
            var snapshot = PersistenceDiagnostics.Snapshot();

            Assert.Empty(loaded);
            Assert.Contains("recent_connect_targets", snapshot.Summary, StringComparison.Ordinal);
            Assert.Equal("Recent targets could not be loaded.", snapshot.LastWarning);
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }

                var parent = Path.GetDirectoryName(tempFile);
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                {
                    Directory.Delete(parent, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void RecentTargetsStore_SaveFailure_RecordsPersistenceDiagnostic()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "nlink-tests",
            "recent-targets-save-fail",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(tempDir);

        try
        {
            PersistenceDiagnostics.ClearForTests();
            var store = new LocalRecentConnectTargetsStore(tempDir);
            store.SaveTargets(["nlink-peer.a1"]);
            var snapshot = PersistenceDiagnostics.Snapshot();

            Assert.Contains("recent_connect_targets", snapshot.Summary, StringComparison.Ordinal);
            Assert.Equal("Recent targets could not be saved.", snapshot.LastWarning);
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
