using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NLink.Tools;
using Manifest = NLink.Tools.LocalFrontierFileManifest;

namespace NLink.SmokeTests;

public sealed partial class FrontierFileAdoptionTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public void Adoption_ApprovedGuiBudgetKeepsOriginalClockAndAccumulatedWork()
    {
        Assert.Equal(10000, LocalFrontierFileAdoption.MaximumActiveMilliseconds);
        var work = new Stopwatch();
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var elapsed = typeof(Stopwatch).GetField("_elapsed", flags)!;
        elapsed.SetValue(work, Stopwatch.Frequency * 6);
        using var owner = new LocalFrontierFileAdoption(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), () => true, work);
        Assert.Same(work, typeof(LocalFrontierFileAdoption).GetField("work", flags)!.GetValue(owner));
        Assert.True(owner.TryRunControl(() =>
        {
            Assert.True(work.IsRunning);
            Assert.True(owner.IsRetained);
            return work.IsRunning;
        }));
        Assert.False(work.IsRunning);
        Assert.True(work.ElapsedTicks >= Stopwatch.Frequency * 6);
        elapsed.SetValue(work, Stopwatch.Frequency * 9);
        Assert.True(owner.TryRunControl(() => true));
        Assert.True(work.ElapsedTicks >= Stopwatch.Frequency * 9);
        elapsed.SetValue(work, Stopwatch.Frequency * 11);
        Assert.False(owner.TryRunControl(() => throw new InvalidOperationException("Expired work ran")));
        Assert.False(work.IsRunning);
        Assert.False(owner.IsRetained);
    }

    [Fact]
    public void Adoption_ControlBudgetContinuesThroughNestedAdoptionAndPostChecks()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var directory = new TestDirectory();
        var gui = Guid.NewGuid(); var first = Guid.NewGuid();
        using var app = new Child(directory.Path, gui, first, 1, 0);
        using var owner = new LocalFrontierFileAdoption(gui, first, Guid.NewGuid(), () => true);
        var work = (Stopwatch)typeof(LocalFrontierFileAdoption).GetField("work", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
        Assert.False(owner.TryRunControl(() =>
        {
            Assert.True(work.IsRunning);
            Assert.True(owner.TryAdopt(0, app.Handle, app.Bytes));
            Assert.True(work.IsRunning);
            typeof(Stopwatch).GetField("_elapsed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(work, Stopwatch.Frequency * 11);
            return true;
        }));
        Assert.False(work.IsRunning); Assert.False(owner.IsRetained);
        app.Probe(); app.Exit();
        Assert.Empty(Directory.GetFiles(directory.Path, "frontier-payload-*"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(16, false)]
    [InlineData(1, true)]
    public void Adoption_RetainsBothAppFilesAcrossNativeExitWithoutFreezingSource(int count, bool abrupt)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var directory = new TestDirectory();
        var gui = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var payloadBytes = count == 16 ? Manifest.MaximumPayloadBytes : 0;
        using var a = new Child(directory.Path, gui, first, count, 0, payloadBytes);
        using var b = new Child(directory.Path, gui, second, count, 1, payloadBytes);
        using var owner = new LocalFrontierFileAdoption(gui, first, second, () => true);
        Assert.True(owner.TryAdopt(0, a.Handle, a.Bytes));
        using (var writer = new FileStream(directory.Log, FileMode.Append, FileAccess.Write, FileShare.Read)) { writer.WriteByte(42); }
        Assert.True(owner.TryAdopt(1, b.Handle, b.Bytes));
        Assert.Equal(2, owner.ContributionCount);
        var clone = (LocalFrontierFileAdoption)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null)!;
        Assert.False(clone.IsRetained); Assert.False(clone.TryAdopt(0, a.Handle, a.Bytes)); clone.Dispose();
        Assert.True(owner.IsRetained);
        var retained = (System.Collections.IEnumerable)typeof(LocalFrontierFileAdoption).GetField("adopted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        foreach (var item in retained)
        {
            var files = (IEnumerable<FileStream>)item.GetType().GetField("Files", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item)!;
            foreach (var file in files)
            {
                Assert.False(WriteFile(file.SafeFileHandle, [1], 1, out _, IntPtr.Zero));
                Assert.Equal(5, Marshal.GetLastWin32Error());
            }
        }
        // Mutable source content is explicitly not sealed by provisional custody.
        using (var writer = new FileStream(directory.Log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { writer.WriteByte(55); }
        Assert.True(owner.IsRetained);
        if (abrupt) { a.Crash(); b.Crash(); } else { a.Exit(); b.Exit(); }
        Assert.True(owner.IsRetained); // OS-owned files survive both original processes.
        Assert.Equal(count * 2, Directory.GetFiles(directory.Path, "frontier-payload-*").Length);
        foreach (var path in Directory.GetFiles(directory.Path, "frontier-payload-*"))
        {
            var error = Record.Exception(() => { using var write = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); });
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        using (var writer = new FileStream(directory.Log, FileMode.Append, FileAccess.Write, FileShare.Read)) { writer.WriteByte(42); }
        File.Move(directory.Log, directory.Log + ".old"); File.WriteAllText(directory.Log, "replacement");
        Assert.True(owner.IsRetained); // Anchors remain the old native file, never the replacement.
        owner.Dispose();
        Assert.Empty(Directory.GetFiles(directory.Path, "frontier-payload-*"));
        Assert.Equal("replacement", File.ReadAllText(directory.Log));
    }

    [Theory]
    [InlineData("bad-first-hash")]
    [InlineData("bad-second-handle")]
    [InlineData("bad-identity")]
    [InlineData("wrong-gui")]
    [InlineData("wrong-app")]
    [InlineData("wrong-pid")]
    [InlineData("wrong-incarnation")]
    [InlineData("cancel-during")]
    [InlineData("dies-during")]
    [InlineData("throwing-callback")]
    [InlineData("exited")]
    [InlineData("wrong-process")]
    public void Adoption_RejectsWholeContributionAndNeverClosesRemoteHandles(string fault)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var directory = new TestDirectory();
        var gui = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        using var app = new Child(directory.Path, gui, first, 2, 0);
        var m = Manifest.Decode(app.Bytes); var payloads = m.Payloads;
        var pid = m.AppId; var creation = m.AppCreation; var request = m.AppRequest; var guiRequest = m.GuiRequest;
        var p = payloads[1]; var f = p.File;
        switch (fault)
        {
            case "bad-first-hash": payloads[0].Hash[0] ^= 1; break;
            case "bad-second-handle": f = new(long.MaxValue, f.Volume, f.IndexHigh, f.IndexLow, f.Length); break;
            case "bad-identity": f = new(f.Handle, f.Volume, f.IndexHigh, f.IndexLow ^ 1, f.Length); break;
            case "wrong-gui": guiRequest = Guid.NewGuid(); break;
            case "wrong-app": request = Guid.NewGuid(); break;
            case "wrong-pid": pid++; break;
            case "wrong-incarnation": creation++; break;
            case "exited": app.Exit(); break;
        }
        payloads[1] = new(p.Observation, f, p.Hash, p.Completion, p.Marker);
        var encoded = new Manifest(guiRequest, request, pid, creation, m.Writer, m.Source, m.Drain, payloads).Encode();
        var checks = 0;
        using var owner = new LocalFrontierFileAdoption(gui, first, second, () =>
        {
            checks++;
            if (checks == 6 && fault == "dies-during") { app.Exit(); }
            if (checks == 6 && fault == "throwing-callback") { throw new IOException("fixture revocation"); }
            return fault != "cancel-during" || checks < 6;
        });
        using var current = Process.GetCurrentProcess();
        using var wrong = new SafeWaitHandle(current.Handle, false);
        Assert.False(owner.TryAdopt(0, fault == "wrong-process" ? wrong : app.Handle, encoded));
        Assert.False(owner.IsRetained); Assert.Equal(0, owner.ContributionCount);
        Assert.False(owner.TryAdopt(0, app.Handle, app.Bytes));
        if (fault is not ("exited" or "dies-during")) { app.Probe(); app.Exit(); }
        Assert.Empty(Directory.GetFiles(directory.Path, "frontier-payload-*"));
        using var exclusive = new FileStream(directory.Log, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public async Task Adoption_SameWireAndNativePullOnBothPowerShellHosts(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var directory = new TestDirectory();
        const string script = """
            param([string]$RepoRoot,[string]$Core,[string]$Fixture,[string]$Directory)
            $ErrorActionPreference='Stop'
            Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
            $assembly=[NLink.Tools.LocalFrontierAppRequest].Assembly
            $type=$assembly.GetType('NLink.Tools.LocalFrontierFileAdoption',$true)
            $manifestType=$assembly.GetType('NLink.Tools.LocalFrontierFileManifest',$true)
            if($type.IsPublic -or $manifestType.IsPublic) { throw 'Public transport authority exposed' }
            $flags=[Reflection.BindingFlags]'Instance,NonPublic'
            $gui=[guid]::NewGuid(); $first=[guid]::NewGuid(); $second=[guid]::NewGuid()
            $owner=$type.GetConstructors($flags)[0].Invoke([object[]]@($gui,$first,$second,[Func[bool]]{return $true}))
            $children=[Collections.Generic.List[object]]::new()
            function Quote([string]$value) { return "'"+$value.Replace("'","''")+"'" }
            try {
                for($slot=0;$slot -lt 2;$slot++) {
                    $id=if($slot -eq 0) {$first} else {$second}
                    $count=if($slot -eq 0) {1} else {0}
                    $childCode='$ErrorActionPreference=''Stop''; [void][Reflection.Assembly]::LoadFrom('+(Quote $Core)+'); [void][Reflection.Assembly]::LoadFrom('+(Quote $Fixture)+'); '+
                        '$f=[NLink.SmokeTests.FrontierFileAdoptionFixture]::Create('+(Quote $Directory)+',[guid]'''+$gui+''',[guid]'''+$id+''','+$count+','+$slot+'); '+
                        'try {[Console]::WriteLine($f.Encoded); [void][Console]::ReadLine()} finally {$f.Dispose()}'
                    $start=[Diagnostics.ProcessStartInfo]::new()
                    $start.FileName=(Get-Command pwsh).Source; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
                    $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
                    $start.Arguments='-NoProfile -EncodedCommand '+[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCode))
                    $process=[Diagnostics.Process]::Start($start)
                    $entry=@{Process=$process;Errors=$process.StandardError.ReadToEndAsync();Handle=[Microsoft.Win32.SafeHandles.SafeWaitHandle]::new($process.Handle,$false)}
                    $children.Add($entry)
                    $read=$process.StandardOutput.ReadLineAsync()
                    if(-not $read.Wait(20000)) { throw 'App manifest deadline' }
                    $line=$read.GetAwaiter().GetResult()
                    if(-not $line) { throw ('App manifest missing: '+$entry.Errors.GetAwaiter().GetResult()) }
                    $bytes=[Convert]::FromBase64String($line)
                    $decoded=$manifestType.GetMethod('Decode',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,[object[]]@(,$bytes))
                    if($null -eq $decoded) { throw 'Shared wire rejected Core manifest' }
                    $encoded=$manifestType.GetMethod('Encode',$flags).Invoke($decoded,$null)
                    if([Convert]::ToBase64String($encoded) -cne $line) { throw 'Wire round-trip changed' }
                    if(-not $type.GetMethod('TryAdopt',$flags).Invoke($owner,[object[]]@($slot,$entry.Handle,$bytes))) { throw 'Native pull failed' }
                }
                foreach($entry in $children) {
                    $entry.Process.StandardInput.WriteLine('exit'); $entry.Process.StandardInput.Flush()
                    if(-not $entry.Process.WaitForExit(10000) -or $entry.Process.ExitCode -ne 0) { throw ('App exit failed: '+$entry.Errors.GetAwaiter().GetResult()) }
                }
                if(-not $type.GetProperty('IsRetained',$flags).GetValue($owner,$null) -or $type.GetProperty('ContributionCount',$flags).GetValue($owner,$null) -ne 2) { throw 'Custody lost after original native exits' }
                if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Length -ne 1) { throw 'Payload not held by GUI duplicate' }
                $owner.Dispose()
                if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Length -ne 0) { throw 'Payload native cleanup failed' }
                'frontier-file-adoption=ok'
            } finally {
                $owner.Dispose()
                foreach($entry in $children) {
                    if(-not $entry.Process.HasExited) {$entry.Process.Kill(); [void]$entry.Process.WaitForExit(5000)}
                    $entry.Handle.Dispose(); $entry.Process.Dispose()
                }
            }
            """;
        var result = await RunPowerShellScriptTextAsync(script,
            [FindRepoRoot(), typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location,
                typeof(FrontierFileAdoptionFixture).Assembly.Location, directory.Path], host);
        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("frontier-file-adoption=ok", StringComparison.Ordinal),
            result.Stdout + Environment.NewLine + result.Stderr);
    }

    [Theory]
    [InlineData("repeat")]
    [InlineData("bad-second")]
    [InlineData("same-writer")]
    [InlineData("overlapping-ranges")]
    [InlineData("different-source")]
    [InlineData("budget")]
    [InlineData("revoked")]
    [InlineData("truncated-source")]
    [InlineData("oversized-source")]
    public void Adoption_SecondFailureOrRevocationRetiresPreviouslyAdoptedSet(string fault)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var directory = new TestDirectory(); using var other = new TestDirectory();
        var gui = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid(); var current = true;
        using var a = new Child(directory.Path, gui, first, 1, 0);
        using var b = new Child(fault == "different-source" ? other.Path : directory.Path, gui, second, 1, 1);
        using var owner = new LocalFrontierFileAdoption(gui, first, second, () => current);
        Assert.True(owner.TryAdopt(0, a.Handle, a.Bytes));
        if (fault == "revoked") { current = false; Assert.False(owner.IsRetained); }
        else if (fault is "truncated-source" or "oversized-source")
        {
            using (var write = new FileStream(directory.Log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            { write.SetLength(fault == "truncated-source" ? 1 : Manifest.MaximumSourceBytes + 1); }
            Assert.False(owner.IsRetained);
            using (var write = new FileStream(directory.Log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { write.SetLength(1024); }
            Assert.False(owner.IsRetained); // Restoring length cannot revive rejected custody.
        }
        else
        {
            var bytes = b.Bytes;
            if (fault == "bad-second") { bytes = bytes[..^1]; }
            if (fault is "same-writer" or "overlapping-ranges")
            {
                var m = Manifest.Decode(bytes); var old = Manifest.Decode(a.Bytes);
                bytes = new Manifest(m.GuiRequest, m.AppRequest, m.AppId, m.AppCreation,
                    fault == "same-writer" ? old.Writer : m.Writer, m.Source,
                    fault == "overlapping-ranges" ? old.Drain : m.Drain,
                    fault == "overlapping-ranges" ? [] : m.Payloads).Encode();
            }
            if (fault == "budget")
            {
                var work = (Stopwatch)typeof(LocalFrontierFileAdoption).GetField("work", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
                typeof(Stopwatch).GetField("_elapsed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(work, Stopwatch.Frequency * 11);
            }
            Assert.False(fault == "repeat" ? owner.TryAdopt(0, a.Handle, a.Bytes) : owner.TryAdopt(1, b.Handle, bytes));
        }
        Assert.False(owner.IsRetained); Assert.Equal(0, owner.ContributionCount);
        a.Probe(); b.Probe(); a.Exit(); b.Exit();
        Assert.Empty(Directory.GetFiles(directory.Path, "frontier-payload-*"));
        Assert.Empty(Directory.GetFiles(other.Path, "frontier-payload-*"));
    }

    private sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frontier-adoption-" + Guid.NewGuid().ToString("N"));
        internal string Log => System.IO.Path.Combine(Path, "shared.log");
        internal TestDirectory() { Directory.CreateDirectory(Path); File.WriteAllBytes(Log, new byte[1024]); }
        public void Dispose() { Directory.Delete(Path, true); }
    }
    private sealed class Child : IDisposable
    {
        private readonly Process process;
        private readonly Task<string> errors;
        internal SafeWaitHandle Handle { get; }
        internal byte[] Bytes { get; }
        internal Child(string directory, Guid gui, Guid app, int count, int ordinal, int payloadBytes = 0)
        {
            static string Q(string text) => "'" + text.Replace("'", "''") + "'";
            var script = "$ErrorActionPreference='Stop'; " +
                "[void][Reflection.Assembly]::LoadFrom(" + Q(typeof(NLink.Core.Logging.LocalOperationalLog).Assembly.Location) + "); " +
                "[void][Reflection.Assembly]::LoadFrom(" + Q(typeof(FrontierFileAdoptionFixture).Assembly.Location) + "); " +
                "$fixture=[NLink.SmokeTests.FrontierFileAdoptionFixture]::Create(" + Q(directory) + ",[guid]'" + gui + "',[guid]'" + app + "'," + count + "," + ordinal + "," + payloadBytes + "); " +
                "try { [Console]::WriteLine($fixture.Encoded); while(($command=[Console]::ReadLine()) -eq 'probe') { [Console]::WriteLine($fixture.IsCurrent) } } finally { $fixture.Dispose() }";
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            process = Process.Start(start)!; Handle = new(process.Handle, false); errors = process.StandardError.ReadToEndAsync();
            try
            {
                var line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                Assert.True(line is not null, process.HasExited ? errors.GetAwaiter().GetResult() : "Child descriptor unavailable");
                Bytes = Convert.FromBase64String(line);
            }
            catch { Dispose(); throw; }
        }
        internal void Probe(bool expected = true)
        {
            process.StandardInput.WriteLine("probe"); process.StandardInput.Flush();
            Assert.Equal(expected.ToString(), process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        }
        internal void Exit()
        {
            if (!process.HasExited) { process.StandardInput.WriteLine("exit"); process.StandardInput.Flush(); }
            Assert.True(process.WaitForExit(10000));
            Assert.True(process.ExitCode == 0, errors.GetAwaiter().GetResult());
        }
        internal void Crash() { process.Kill(); Assert.True(process.WaitForExit(5000)); }
        public void Dispose()
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
            Handle.Dispose(); process.Dispose();
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle file, byte[] bytes, uint count, out uint written, IntPtr overlapped);
}
