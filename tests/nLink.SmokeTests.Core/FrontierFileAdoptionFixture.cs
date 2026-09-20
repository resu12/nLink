using System.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using Manifest = NLink.Tools.LocalFrontierFileManifest;

namespace NLink.SmokeTests;

// Synthetic transport metadata + actual Core native file primitives. This is
// intentionally not an original producer catalog or exact writer receipt test.
public sealed class FrontierFileAdoptionFixture : IDisposable
{
    private readonly List<IDisposable> owned = [];
    private readonly List<FrontierNativePayloadFile> payloads = [];
    private OperationalLogNativeAnchor? anchor;
    public string Encoded { get; private set; } = "";
    public bool IsCurrent => anchor?.IsRetained == true && payloads.All(f => f.IsCurrent);
    internal Microsoft.Win32.SafeHandles.SafeFileHandle[] NativeHandles => owned.OfType<FileStream>().Select(s => s.SafeFileHandle).ToArray();

    public static FrontierFileAdoptionFixture Create(string directory, Guid gui, Guid app, int count, int ordinal, int payloadBytes = 0)
    {
        var fixture = new FrontierFileAdoptionFixture();
        try
        {
            using var source = new FileStream(Path.Combine(directory, "shared.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fixture.anchor = OperationalLogNativeAnchor.TryRetain(source.SafeFileHandle);
            if (fixture.anchor is null) { throw new InvalidOperationException("Source anchor unavailable"); }
            fixture.owned.Add(fixture.anchor);
            var sourceRead = fixture.anchor.TryOpenProvisionalRead()!; fixture.owned.Add(sourceRead);
            var entries = new List<Manifest.Payload>();
            for (var i = 0; i < count; i++)
            {
                var bytes = payloadBytes == 0 ? System.Text.Encoding.UTF8.GetBytes($"payload-{ordinal}-{i}") : new byte[payloadBytes];
                var file = FrontierNativePayloadFile.TryCreate(directory, bytes)!;
                fixture.owned.Add(file); fixture.payloads.Add(file);
                var read = file.TryOpenRead()!; fixture.owned.Add(read);
                entries.Add(new(Guid.NewGuid(), Describe(read), Convert.FromHexString(file.Sha256),
                    FrontierFileManifestTests.Range(2 * i + 1, ordinal * 100 + 2 * i), FrontierFileManifestTests.Range(2 * i + 2, ordinal * 100 + 2 * i + 1)));
            }
            using var process = Process.GetCurrentProcess();
            var manifest = new Manifest(gui, app, process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(), "writer-" + ordinal,
                Describe(sourceRead), FrontierFileManifestTests.Range(2 * count + 1, ordinal * 100 + 2 * count), entries.ToArray());
            fixture.Encoded = Convert.ToBase64String(manifest.Encode());
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }
    private static Manifest.FileEntry Describe(FileStream stream)
    {
        if (!OperationalLogNativeSource.TryDescribe(stream.SafeFileHandle, out var identity, out var length)) { throw new IOException("Native descriptor unavailable"); }
        return new(stream.SafeFileHandle.DangerousGetHandle().ToInt64(), Convert.ToUInt32(identity![..8], 16),
            Convert.ToUInt32(identity[9..17], 16), Convert.ToUInt32(identity[17..], 16), length);
    }
    public void Dispose() { foreach (var item in owned) { item.Dispose(); } owned.Clear(); }
}
