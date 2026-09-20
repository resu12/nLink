using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NLink.Core.Logging;
using static NLink.Core.Logging.OrderedOperationalLogWritePump;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("FileTransferEvidenceSuite", "ProvisionalSourceAnchor")]
public sealed class OperationalLogNativeAnchorTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(268435456, true)]
    [InlineData(268435457, false)]
    public void NativeAnchor_ExactSourceSizeBounds(long length, bool expected)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new SourceDirectory();
        using var writer = fixture.OpenWriter();
        writer.SetLength(length);
        using var anchor = OperationalLogNativeAnchor.TryRetain(writer.SafeFileHandle);
        Assert.Equal(expected, anchor is not null);
        if (anchor is not null)
        {
            Assert.Equal(length, anchor.CapturedByteCount);
            Assert.True(anchor.IsRetained);
        }
        Assert.False(writer.SafeFileHandle.IsClosed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeAnchor_AllowsOtherWriterMarkersAndDrainEvenWithAnOpenReader(bool openReader)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new SourceDirectory();
        using var writer = fixture.OpenWriter();
        var first = Encoding.UTF8.GetBytes("first λ\r\n");
        writer.Write(first); writer.Flush();
        using var anchor = OperationalLogNativeAnchor.TryRetain(writer.SafeFileHandle);
        Assert.NotNull(anchor);
        writer.Dispose();
        using var early = openReader ? anchor.TryOpenProvisionalRead() : null;
        if (openReader) { Assert.NotNull(early); }
        var pump = new OrderedOperationalLogWritePump(new RollingFileLogger(fixture.LogPath), _ => { });
        try
        {
            Assert.True(pump.SubmitWithReceipt(Batch(1, "second-marker"), out var marker));
            Assert.True(pump.CompleteAndFlushWithReceipt(Batch(2, "second-drain", true), TimeSpan.FromSeconds(5), out var drain));
            Assert.Equal(ExactBatchPersistence.Persisted, pump.GetPersistence(marker));
            Assert.Equal(ExactBatchPersistence.Persisted, pump.GetPersistence(drain));
            Assert.True(anchor.IsRetained);
            Assert.Equal(first.Length, anchor.CapturedByteCount);
            Assert.Equal(File.ReadAllBytes(fixture.LogPath), Read(anchor));
            if (early is not null)
            {
                using var copy = new MemoryStream();
                early.CopyTo(copy);
                Assert.Equal(File.ReadAllBytes(fixture.LogPath), copy.ToArray());
            }
        }
        finally { pump.RevokeExactBatchReceipts(); }
    }

    [Fact]
    public void NativeAnchor_CapturesOriginalHandleNotAReplacementPath()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new SourceDirectory();
        using var writer = new FileStream(fixture.LogPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        writer.Write("original"u8); writer.Flush();
        Assert.True(OperationalLogNativeSource.TryDescribe(writer.SafeFileHandle, out var originalIdentity, out _));
        File.Move(fixture.LogPath, fixture.LogPath + ".moved");
        File.WriteAllText(fixture.LogPath, "replacement");
        using var replacement = File.OpenRead(fixture.LogPath);
        Assert.True(OperationalLogNativeSource.TryDescribe(replacement.SafeFileHandle, out var replacementIdentity, out _));
        using var anchor = OperationalLogNativeAnchor.TryRetain(writer.SafeFileHandle);
        Assert.NotNull(anchor);
        Assert.Equal(originalIdentity, anchor.FileIdentity);
        Assert.NotEqual(replacementIdentity, anchor.FileIdentity);
        writer.Dispose();
        Assert.Equal("original"u8.ToArray(), Read(anchor));
        Assert.Equal("replacement", File.ReadAllText(fixture.LogPath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void NativeAnchor_DoesNotBlockOtherWriterRotation(int retainedFiles)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new SourceDirectory();
        using var writer = fixture.OpenWriter();
        writer.Write("original"u8); writer.Flush();
        using var anchor = OperationalLogNativeAnchor.TryRetain(writer.SafeFileHandle);
        Assert.NotNull(anchor);
        writer.Dispose();
        using var reader = anchor.TryOpenProvisionalRead();
        Assert.NotNull(reader);
        var pump = new OrderedOperationalLogWritePump(new RollingFileLogger(fixture.LogPath, maxFileBytes: 1, retainedFileCount: retainedFiles), _ => { });
        try
        {
            Assert.True(pump.SubmitWithReceipt(Batch(1, "after-rotation"), out var receipt));
            Assert.Equal(ExactBatchPersistence.Persisted, pump.GetPersistence(receipt));
            Assert.Contains("after-rotation", File.ReadAllText(fixture.LogPath));
            using var replacement = File.OpenRead(fixture.LogPath);
            Assert.True(OperationalLogNativeSource.TryDescribe(replacement.SafeFileHandle, out var identity, out _));
            Assert.NotEqual(anchor.FileIdentity, identity);
            using var copied = new MemoryStream();
            reader.CopyTo(copied);
            Assert.Equal("original"u8.ToArray(), copied.ToArray());
        }
        finally { pump.RevokeExactBatchReceipts(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeAnchor_ObservedTruncationOrOverflowPermanentlyRetiresRetention(bool overflow)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new SourceDirectory();
        using var writer = fixture.OpenWriter();
        writer.SetLength(16);
        using var anchor = OperationalLogNativeAnchor.TryRetain(writer.SafeFileHandle);
        Assert.NotNull(anchor);
        writer.SetLength(overflow ? OperationalLogNativeSource.MaximumBytes + 1 : 8);
        Assert.False(anchor.IsRetained);
        writer.SetLength(16);
        Assert.False(anchor.IsRetained);
        Assert.Null(anchor.TryOpenProvisionalRead());
        writer.Dispose();
        File.Move(fixture.LogPath, fixture.LogPath + ".released");
    }

    [Fact]
    public void NativeAnchor_SameLengthMutationRemainsUnverifiedLiveData()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new SourceDirectory();
        using var writer = fixture.OpenWriter();
        writer.Write("before"u8); writer.Flush();
        using var anchor = OperationalLogNativeAnchor.TryRetain(writer.SafeFileHandle);
        Assert.NotNull(anchor);
        writer.Position = 0; writer.Write("after!"u8); writer.Flush();
        Assert.True(anchor.IsRetained);
        Assert.Equal("after!"u8.ToArray(), Read(anchor));
        // Retention is deliberately not immutable source verification. Exact
        // original range/hash checks remain mandatory after the later GUI seal.
        Assert.Null(typeof(OperationalLogNativeAnchor).GetProperty("Sha256", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
    }

    [Fact]
    public void NativeAnchor_RejectsCopiedWrappersWithoutDisposingOriginal()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new SourceDirectory();
        using var writer = fixture.OpenWriter();
        writer.WriteByte(1); writer.Flush();
        using var anchor = OperationalLogNativeAnchor.TryRetain(writer.SafeFileHandle);
        Assert.NotNull(anchor);
        using var copy = (OperationalLogNativeAnchor)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(anchor, null)!;
        Assert.False(copy.IsRetained);
        Assert.Null(copy.TryOpenProvisionalRead());
        copy.Dispose();
        Assert.True(anchor.IsRetained);
        Assert.Equal([1], Read(anchor));
    }

    [Fact]
    public void NativeAnchor_ReadHandleHasNoNativeWriteAccess()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new SourceDirectory();
        using var writer = fixture.OpenWriter();
        writer.WriteByte(1); writer.Flush();
        using var anchor = OperationalLogNativeAnchor.TryRetain(writer.SafeFileHandle);
        Assert.NotNull(anchor);
        using var read = anchor.TryOpenProvisionalRead();
        Assert.NotNull(read);
        Assert.False(read.CanWrite);
        Assert.False(WriteFile(read.SafeFileHandle, [2], 1, out _, IntPtr.Zero));
        Assert.Equal(5, Marshal.GetLastWin32Error());
        Assert.Equal([1], Read(anchor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeAnchor_DisposalReleasesOnlyOwnedHandlesAndPreservesReplacement(bool keepReader)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new SourceDirectory();
        using var writer = fixture.OpenWriter();
        writer.WriteByte(1); writer.Flush();
        using var anchor = OperationalLogNativeAnchor.TryRetain(writer.SafeFileHandle);
        Assert.NotNull(anchor);
        writer.Dispose();
        using var read = keepReader ? anchor.TryOpenProvisionalRead() : null;
        var moved = fixture.LogPath + ".moved";
        File.Move(fixture.LogPath, moved);
        File.WriteAllText(fixture.LogPath, "replacement");
        Assert.Throws<IOException>(() => File.Open(moved, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose());
        anchor.Dispose();
        Assert.False(anchor.IsRetained);
        Assert.Null(anchor.TryOpenProvisionalRead());
        if (keepReader)
        {
            Assert.NotNull(read);
            Assert.Equal(1, read.ReadByte());
            Assert.Throws<IOException>(() => File.Open(moved, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose());
            read.Dispose();
        }
        using (var exclusive = File.Open(moved, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { Assert.Equal(1, exclusive.ReadByte()); }
        anchor.Dispose();
        Assert.Equal("replacement", File.ReadAllText(fixture.LogPath));
        Assert.Equal([1], File.ReadAllBytes(moved));
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("invalid")]
    [InlineData("exclusive")]
    public void NativeAnchor_RejectedInputDoesNotCloseCallerHandle(string variant)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new SourceDirectory();
        using var writer = new FileStream(fixture.LogPath, FileMode.CreateNew, FileAccess.ReadWrite,
            variant == "exclusive" ? FileShare.None : FileShare.Read);
        writer.WriteByte(1); writer.Flush();
        using var invalid = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        var handle = variant == "invalid" ? invalid : writer.SafeFileHandle;
        if (variant == "closed") { writer.Dispose(); }
        Assert.Null(OperationalLogNativeAnchor.TryRetain(handle));
        if (variant != "closed")
        {
            Assert.False(writer.SafeFileHandle.IsClosed);
            writer.WriteByte(2); writer.Flush();
        }
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("device")]
    [InlineData("pipe")]
    public void NativeAnchor_RejectsNonRegularFiles(string variant)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new SourceDirectory();
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var handle = variant == "pipe"
            ? new SafeFileHandle(pipe.SafePipeHandle.DangerousGetHandle(), ownsHandle: false)
            : CreateFile(variant == "directory" ? fixture.DirectoryPath : "NUL", 0x80000000, 7, IntPtr.Zero, 3,
                variant == "directory" ? 0x02000000u : 0, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        Assert.Null(OperationalLogNativeAnchor.TryRetain(handle));
        Assert.False(handle.IsClosed);
    }

    [Fact]
    public async Task NativeAnchor_AnotherProcessCanRetainWhileTheRealWriterCompletes()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new SourceDirectory();
        var name = "Local\\nlink-anchor-test-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".ready");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".release");
        var worker = RunPowerShellScriptTextAsync("""
            param([string]$Core,[string]$Path,[string]$Name)
            $ErrorActionPreference='Stop'
            $assembly=[Reflection.Assembly]::LoadFrom($Core)
            $type=$assembly.GetType('NLink.Core.Logging.OperationalLogNativeAnchor',$true)
            $flags=[Reflection.BindingFlags]'Instance,NonPublic'
            $ready=[Threading.EventWaitHandle]::OpenExisting($Name+'.ready')
            $release=[Threading.EventWaitHandle]::OpenExisting($Name+'.release')
            $writer=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
            $anchor=$null; $read=$null
            try {
                $bytes=[Text.Encoding]::UTF8.GetBytes("first λ`r`n")
                $writer.Write($bytes,0,$bytes.Length); $writer.Flush()
                $anchor=$type.GetMethod('TryRetain',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,@($writer.SafeFileHandle))
                if($null -eq $anchor) { throw 'Native anchor missing' }
                $writer.Dispose()
                [void]$ready.Set()
                if(-not $release.WaitOne(20000)) { throw 'Writer completion deadline' }
                if(-not $type.GetProperty('IsRetained',$flags).GetValue($anchor)) { throw 'Growing source lost' }
                $read=$type.GetMethod('TryOpenProvisionalRead',$flags).Invoke($anchor,$null)
                $reader=[IO.StreamReader]::new($read)
                try { $text=$reader.ReadToEnd(); if($text -notmatch 'second-marker' -or $text -notmatch 'second-drain') { throw 'Final writer bytes missing' } }
                finally { $reader.Dispose() }
                'native-anchor-shared=ok'
            } finally { if($null -ne $read) {$read.Dispose()}; if($null -ne $anchor) {$anchor.Dispose()}; $writer.Dispose(); $ready.Dispose(); $release.Dispose() }
            """, [typeof(OperationalLogNativeAnchor).Assembly.Location, fixture.LogPath, name], "pwsh", TimeSpan.FromSeconds(30));
        var pump = new OrderedOperationalLogWritePump(new RollingFileLogger(fixture.LogPath), _ => { });
        try
        {
            Assert.True(ready.WaitOne(TimeSpan.FromSeconds(20)), "Source-retaining child did not become ready");
            Assert.True(pump.SubmitWithReceipt(Batch(1, "second-marker"), out var marker));
            Assert.True(pump.CompleteAndFlushWithReceipt(Batch(2, "second-drain", true), TimeSpan.FromSeconds(5), out var drain));
            Assert.Equal(ExactBatchPersistence.Persisted, pump.GetPersistence(marker));
            Assert.Equal(ExactBatchPersistence.Persisted, pump.GetPersistence(drain));
        }
        finally { release.Set(); pump.RevokeExactBatchReceipts(); await worker; }
        var result = await worker;
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("native-anchor-shared=ok", result.Stdout);
        File.Move(fixture.LogPath, fixture.LogPath + ".released");
    }

    [Fact]
    public async Task NativeAnchor_AbruptExitReleasesRetentionWithoutDeletingSource()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var fixture = new SourceDirectory();
        var result = await RunPowerShellScriptTextAsync("""
            param([string]$Core,[string]$Path)
            $ErrorActionPreference='Stop'
            $assembly=[Reflection.Assembly]::LoadFrom($Core)
            $type=$assembly.GetType('NLink.Core.Logging.OperationalLogNativeAnchor',$true)
            $writer=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
            $writer.WriteByte(42); $writer.Flush()
            $anchor=$type.GetMethod('TryRetain',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,@($writer.SafeFileHandle))
            if($null -eq $anchor) { throw 'Native anchor missing' }
            $writer.Dispose()
            'native-anchor-held'
            [Environment]::Exit(0)
            """, [typeof(OperationalLogNativeAnchor).Assembly.Location, fixture.LogPath], "pwsh", TimeSpan.FromSeconds(20));
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("native-anchor-held", result.Stdout);
        Assert.Equal([42], File.ReadAllBytes(fixture.LogPath));
        File.Move(fixture.LogPath, fixture.LogPath + ".released");
    }

    private static OperationalLogWriteBatch Batch(long sequence, string text, bool drain = false)
        => new("anchor-run", "anchor-scenario", "anchor-writer", sequence, sequence, sequence, DateTime.UtcNow.Ticks,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), text, Encoding.UTF8.GetByteCount(text), drain);

    private static byte[] Read(OperationalLogNativeAnchor anchor)
    {
        using var stream = anchor.TryOpenProvisionalRead();
        Assert.NotNull(stream);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private sealed class SourceDirectory : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "nlink-source-anchor-" + Guid.NewGuid().ToString("N"));
        internal string LogPath => Path.Combine(DirectoryPath, "writer.log");
        internal SourceDirectory() => Directory.CreateDirectory(DirectoryPath);
        internal FileStream OpenWriter() => new(LogPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle file, byte[] bytes, int count, out int written, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
