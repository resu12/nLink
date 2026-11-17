using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NLink.SmokeTests;

[Trait("FileTransferInfraSuite", "BridgeStdinFraming")]
[Trait("Area", "Core")]
public sealed class BridgeStdinFramingTests : CoreSmokeTestsBase
{
    [Theory]
    [InlineData("json", 1)]
    [InlineData("json", -1)]
    [InlineData("binary", 1)]
    [InlineData("binary", 7)]
    [InlineData("binary", 15)]
    [InlineData("binary", 16)]
    [InlineData("binary", 20)]
    [InlineData("binary", -1)]
    public async Task PartialFrame_WaitsForInputWithoutStarvingTimers(string kind, int split)
    {
        await using var child = await FramingChild.StartAsync();
        var frame = kind == "json" ? Ping("fragmented") : BinarySend();
        if (split < 0) split = frame.Length + split;

        // Include complete frames and whitespace ahead of the incomplete tail.
        await child.WriteAsync(Ping("first").Concat(Ping("second"))
            .Concat(Encoding.UTF8.GetBytes(" \r\n\t"))
            .Concat(frame.Take(split)).ToArray());
        await child.WaitAsync(() => child.HasPong("second"));
        var timers = child.CountEvent("bridge_event_loop_summary");
        await child.WaitAsync(() => child.CountEvent("bridge_event_loop_summary") > timers);
        Assert.False(child.HasPong("fragmented"));

        await child.WriteAsync(frame.AsMemory(split));
        await child.WriteAsync(Ping("after"));
        await child.WaitAsync(() => child.HasPong("after"));
        Assert.Equal(kind == "json"
                ? new[] { "first", "second", "fragmented", "after" }
                : new[] { "first", "second", "after" },
            child.PongIds().Where(id => id != "baseline"));
        if (kind == "binary")
        {
            await child.WaitAsync(() => child.BulkEnqueuedCount() == 1);
            Assert.Equal(1, child.BulkEnqueuedCount());
        }
        Assert.Empty(child.Errors);
        await child.ShutdownAsync();
    }

    [Fact]
    public async Task InputDuringAwaitedCommand_IsDrainedOnceInOrder()
    {
        await using var child = await FramingChild.StartAsync(controlDelayMs: 300);
        await child.WriteAsync(Encoding.UTF8.GetBytes(
            "{\"cmd\":\"send\",\"id\":\"delayed\",\"destination\":\"peer.fake\",\"payloadBase64\":\"AQ==\"}\n"));
        await Task.Delay(50);
        await child.WriteAsync(Ping("during-await").Concat(Ping("partial").Take(1)).ToArray());
        await child.WaitAsync(() => child.HasPong("during-await"));
        await child.WriteAsync(Ping("partial").AsMemory(1));
        await child.WriteAsync(Ping("last"));
        await child.WaitAsync(() => child.HasPong("last"));
        Assert.Equal(new[] { "baseline", "during-await", "partial", "last" }, child.PongIds());
        Assert.Single(child.Output.Where(line => line.Contains("\"id\":\"delayed\"", StringComparison.Ordinal)));
        Assert.Empty(child.Errors);
        await child.ShutdownAsync();
    }

    [Theory]
    [InlineData("header")]
    [InlineData("json")]
    [InlineData("oversized")]
    public async Task InvalidInput_PreservesErrorAndNextInputHandling(string kind)
    {
        await using var child = await FramingChild.StartAsync();
        byte[] invalid;
        if (kind == "header")
        {
            invalid = BinarySend();
            invalid[1] = 99;
        }
        else invalid = Encoding.UTF8.GetBytes(kind == "json" ? "{invalid}\n" : new string('x', 256 * 1024 + 1));
        await child.WriteAsync(invalid);
        await child.WaitAsync(() => child.Errors.Count > 0 || child.CountEvent("error") > 0);
        await child.WriteAsync(Ping("after-error"));
        await child.WaitAsync(() => child.HasPong("after-error"));
        await child.ShutdownAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EofWithPartialInput_ExitsWithoutBusyLoop(bool binary)
    {
        await using var child = await FramingChild.StartAsync();
        await child.WriteAsync((binary ? BinarySend() : Ping("partial")).AsMemory(0, 1));
        var timers = child.CountEvent("bridge_event_loop_summary");
        await child.WaitAsync(() => child.CountEvent("bridge_event_loop_summary") > timers);
        child.CloseInput();
        await child.WaitForExitAsync();
    }

    private static byte[] Ping(string id) => Encoding.UTF8.GetBytes($"{{\"cmd\":\"ping\",\"id\":\"{id}\"}}\n");

    private static byte[] BinarySend()
    {
        var destination = Encoding.UTF8.GetBytes("peer.fake");
        var frame = new byte[16 + destination.Length + 32];
        frame[1] = 2;
        frame[2] = 1;
        frame[3] = 2; // bulk
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)destination.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(10), 32);
        destination.CopyTo(frame, 16);
        return frame;
    }

    private sealed class FramingChild : IAsyncDisposable
    {
        private readonly Process process;
        public ConcurrentQueue<string> Output { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();

        private FramingChild(Process process)
        {
            this.process = process;
            process.OutputDataReceived += (_, e) => { if (e.Data is { } line) Output.Enqueue(line); };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { } line && (line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Invalid", StringComparison.OrdinalIgnoreCase))) Errors.Enqueue(line);
            };
        }

        public static async Task<FramingChild> StartAsync(int controlDelayMs = 0)
        {
            var bundle = TryFindBridgeBundleDirectory();
            Assert.NotNull(bundle);
            var source = FindFileUpwards(Path.Combine("tools", "nkn-bridge", "index.js"));
            Assert.NotNull(source);
            var process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(bundle!, "node.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            }};
            process.StartInfo.ArgumentList.Add(source!);
            process.StartInfo.Environment["NLINK_BRIDGE_FAKE_NKN_RUNTIME"] = "1";
            process.StartInfo.Environment["NLINK_BRIDGE_FAKE_CONTROL_SEND_DELAY_MS"] = controlDelayMs.ToString();
            var child = new FramingChild(process);
            try
            {
                Assert.True(process.Start());
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await child.WriteAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"hello\",\"id\":\"hello\",\"protocol\":2}\n"));
                await child.WaitAsync(() => child.CountEvent("hello_ok") == 1);
                await child.WriteAsync(Ping("baseline"));
                await child.WaitAsync(() => child.HasPong("baseline"));
                await child.WriteAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"connect\",\"id\":\"connect\",\"identifier\":\"framing-regression\"}\n"));
                await child.WaitAsync(() => child.CountEvent("ready") > 0);
                return child;
            }
            catch { await child.DisposeAsync(); throw; }
        }

        public async Task WriteAsync(ReadOnlyMemory<byte> bytes)
        {
            await process.StandardInput.BaseStream.WriteAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await process.StandardInput.BaseStream.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }

        public int CountEvent(string name) => Output.Count(line => line.Contains($"\"event\":\"{name}\"", StringComparison.Ordinal));
        public bool HasPong(string id) => PongIds().Contains(id);
        public string[] PongIds() => Output.Where(line => line.Contains("\"type\":\"pong\"", StringComparison.Ordinal))
            .Select(line => { using var json = JsonDocument.Parse(line); return json.RootElement.GetProperty("id").GetString()!; }).ToArray();
        public long BulkEnqueuedCount() => Output.Where(line => line.Contains("\"event\":\"bridge_bulk_send_summary\"", StringComparison.Ordinal))
            .Sum(line => { using var json = JsonDocument.Parse(line); return json.RootElement.GetProperty("frames_enqueued").GetInt64(); });

        public async Task WaitAsync(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), string.Join(Environment.NewLine, Output.Concat(Errors)));
                await Task.Delay(20);
            }
        }

        public void CloseInput() => process.StandardInput.Close();
        public async Task ShutdownAsync()
        {
            await WriteAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"shutdown\",\"id\":\"shutdown\"}\n"));
            await WaitForExitAsync();
        }
        public async Task WaitForExitAsync()
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, process.ExitCode);
        }
        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            process.Dispose();
        }
    }
}
