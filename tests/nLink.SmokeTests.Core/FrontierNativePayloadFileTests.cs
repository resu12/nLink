using System.Security.Cryptography;
using System.Text;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferEvidenceSuite", "FrontierNativePayloadFile")]
public sealed class FrontierNativePayloadFileTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(262144, true)]
    [InlineData(0, false)]
    [InlineData(262145, false)]
    public void NativePayloadFile_ExactByteLimitsAndOwnedCleanup(int count, bool expected)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var scope = new OutputDirectory();
        var bytes = Enumerable.Range(0, count).Select(i => (byte)(i % 251)).ToArray();
        using var file = FrontierNativePayloadFile.TryCreate(scope.Path, bytes);
        Assert.Equal(expected, file is not null);
        if (file is null) { Assert.Empty(Directory.GetFiles(scope.Path)); return; }
        Assert.True(file.IsCurrent);
        Assert.Equal(count, file.ByteCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), file.Sha256);
        Assert.Equal(bytes, Read(file));
        var path = Assert.Single(Directory.GetFiles(scope.Path));
        AssertDenied(() => File.WriteAllBytes(path, [1]));
        file.Dispose();
        Assert.False(file.IsCurrent);
        Assert.Null(file.TryOpenRead());
        Assert.Empty(Directory.GetFiles(scope.Path));
        file.Dispose();
    }

    [Fact]
    public void NativePayloadFile_PreservesExactUtf8AndRejectsPathReopeningOrReplacement()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var scope = new OutputDirectory();
        var bytes = new UTF8Encoding(false, true).GetBytes("{\"text\":\"λ\"}\n");
        using var file = FrontierNativePayloadFile.TryCreate(scope.Path, bytes);
        Assert.NotNull(file);
        var path = Assert.Single(Directory.GetFiles(scope.Path));
        AssertDenied(() => File.Move(path, path + ".moved"));
        AssertDenied(() => File.ReadAllBytes(path));
        AssertDenied(() => File.Delete(path));
        Assert.True(file.IsCurrent);
        Assert.Equal(bytes, Read(file));
        file.Dispose();
        Assert.Empty(Directory.GetFiles(scope.Path));
        File.WriteAllText(path, "replacement-must-survive");
        file.Dispose();
        Assert.Equal("replacement-must-survive", File.ReadAllText(path));
    }

    [Fact]
    public void NativePayloadFile_FailedCreationDoesNotCreateDirectoriesOrTouchOtherFiles()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var scope = new OutputDirectory();
        var missing = System.IO.Path.Combine(scope.Path, "missing");
        var sentinel = System.IO.Path.Combine(scope.Path, "sentinel.txt");
        File.WriteAllText(sentinel, "unchanged");
        Assert.Null(FrontierNativePayloadFile.TryCreate(missing, [1]));
        Assert.False(Directory.Exists(missing));
        Assert.Null(FrontierNativePayloadFile.TryCreate(sentinel, [1]));
        Assert.Equal("unchanged", File.ReadAllText(sentinel));
        Assert.Single(Directory.GetFiles(scope.Path));
    }

    [Fact]
    public void NativePayloadFile_FullSixteenFileBudgetRetainsIndependentNativeIdentities()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var scope = new OutputDirectory();
        var files = new List<FrontierNativePayloadFile>();
        try
        {
            var bytes = new byte[FrontierNativePayloadFile.MaximumBytes];
            for (var index = 0; index < 16; index++)
            {
                bytes[0] = (byte)index;
                var file = FrontierNativePayloadFile.TryCreate(scope.Path, bytes);
                Assert.NotNull(file);
                files.Add(file);
                Assert.Equal(bytes, Read(file));
            }
            Assert.Equal(4194304, files.Sum(f => f.ByteCount));
            Assert.Equal(16, files.Select(f => f.FileIdentity).Distinct().Count());
        }
        finally { foreach (var file in files) { file.Dispose(); } }
        Assert.Empty(Directory.GetFiles(scope.Path));
    }

    [Fact]
    public async Task NativePayloadFile_AbruptProcessExitReleasesAndDeletesOwnedFile()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var scope = new OutputDirectory();
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$Core,[string]$Directory)
            $ErrorActionPreference='Stop'
            $assembly=[Reflection.Assembly]::LoadFrom($Core)
            $type=$assembly.GetType('NLink.Core.FileTransfer.FrontierNativePayloadFile',$true)
            $method=$type.GetMethod('TryCreate',[Reflection.BindingFlags]'Static,NonPublic')
            $file=$method.Invoke($null,@($Directory,[byte[]](1,2,3)))
            if($null -eq $file -or [IO.Directory]::GetFiles($Directory).Length -ne 1) { throw 'Native file was not held' }
            'native-payload-held'
            [Environment]::Exit(0)
            """, [typeof(FrontierNativePayloadFile).Assembly.Location, scope.Path], "pwsh", TimeSpan.FromSeconds(20));
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("native-payload-held", result.Stdout);
        Assert.Empty(Directory.GetFiles(scope.Path));
    }

    private static byte[] Read(FrontierNativePayloadFile file)
    {
        using var read = file.TryOpenRead();
        Assert.NotNull(read);
        using var result = new MemoryStream();
        read.CopyTo(result);
        return result.ToArray();
    }

    private static void AssertDenied(Action action)
        => Assert.True(Record.Exception(action) is IOException or UnauthorizedAccessException, "Path access was not denied.");

    private sealed class OutputDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nlink-native-payload-" + Guid.NewGuid().ToString("N"));
        internal OutputDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
