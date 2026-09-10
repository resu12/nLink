using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NLink.Core.Logging;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData("", "f828ecd36fc7cae8effda283dae5531e671624f1df4d156ac30834e9cd3c56cc")]
    [InlineData("{\"messageId\":\"same\",\"payload\":\"a\"}", "a54b75e512ad68c229a6c504c685abf07d88875e9a26dd7d6ab3182aa8321919")]
    [InlineData("{\"messageId\":\"same\",\"payload\":\"b\"}", "b8ba54dbe5567eae4a6df25c1f9e794e1e986eeb7329a0b9a97f9298f67caa9e")]
    public void TunaDeliveryTrace_ExactWireVectors(string input, string expected)
        => Assert.Equal("ftwire1-" + expected, NknTunaDeliveryTrace.WireHash(Encoding.UTF8.GetBytes(input)));

    [Fact]
    public void TunaDeliveryTrace_DefaultOffEvenWithBroaderTracing()
    {
        Assert.Null(NknTunaDeliveryTrace.FromEnvironment(key => key == "NLINK_FILETRANSFER_TRACE" ? "1" : null));
        Assert.Null(NknTunaDeliveryTrace.FromEnvironment(_ => "1")); // Invalid capture ID.
        Assert.NotNull(NknTunaDeliveryTrace.FromEnvironment(key => key == "NLINK_TUNA_DELIVERY_TRACE"
            ? "1" : "11111111-2222-3333-4444-555555555555"));
    }

    [Fact]
    public async Task TunaDeliveryTrace_ExactCallBindingAndRestartAreNotSequenceAuthority()
    {
        var lines = new ConcurrentQueue<string>();
        var producer = new NknTunaDeliveryTrace.Producer(Guid.NewGuid().ToString("D"), lines.Enqueue);
        using var traceOverride = NknTunaDeliveryTrace.OverrideForTests(producer);
        var bytes = "exact-wire"u8.ToArray();
        var first = producer.Connect()!.Select("app_to_tuna", 1, bytes)!;
        var second = producer.Connect()!.Select("app_to_tuna", 1, bytes)!;
        Assert.NotEqual(first.Connection.Number, second.Connection.Number);
        Assert.Equal(first.Wire, second.Wire);
        using (var scope = NknTunaDeliveryTrace.BeginSend(bytes))
        {
            await Task.Run(() => NknTunaDeliveryTrace.BindSend(bytes, first));
            scope!.Result("; start_chunk_index=4602");
        }
        Assert.Single(lines.Where(line => line.Contains("trace_stage=app_send_result;", StringComparison.Ordinal)));
        using (var scope = NknTunaDeliveryTrace.BeginSend(bytes))
        {
            NknTunaDeliveryTrace.BindSend(bytes, first);
            NknTunaDeliveryTrace.BindSend(bytes, second);
            scope!.Result("; start_chunk_index=9999");
        }
        Assert.DoesNotContain(lines, line => line.Contains("start_chunk_index=9999", StringComparison.Ordinal));
        using var receive = NknTunaDeliveryTrace.EnterReceive(first, bytes);
        Assert.Same(first, NknTunaDeliveryTrace.ReceiveFrameFor(bytes));
        Assert.Null(NknTunaDeliveryTrace.ReceiveFrameFor(bytes.ToArray()));
    }

    [Fact]
    public void TunaDeliveryTrace_CapsAreExplicitAndDoNotInventCompletion()
    {
        var lines = new List<string>();
        var producer = new NknTunaDeliveryTrace.Producer(Guid.NewGuid().ToString("D"), lines.Add);
        var connection = producer.Connect()!;
        for (var index = 1; index <= 65; index++)
        {
            Assert.Equal(index <= 64, connection.Select("app_to_tuna", (ulong)index, [1]) is not null);
            Assert.Equal(index <= 64, connection.Select("tuna_to_app", (ulong)index, [1]) is not null);
        }
        Assert.NotNull(producer.Connect());
        Assert.NotNull(producer.Connect());
        Assert.NotNull(producer.Connect());
        Assert.Null(producer.Connect());
        for (var index = 0; index < 5000; index++) producer.Record("trace_stage=test_observation");
        connection.End("app_to_tuna", "test_stop");
        Assert.True(lines.Count <= NknTunaDeliveryTrace.Producer.RecordLimit);
        Assert.Contains(lines, line => line.Contains("trace_stage=incomplete", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("trace_stage=direction_end", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("trace_tail=complete", StringComparison.Ordinal));
        var byteLines = new List<string>();
        var byteProducer = new NknTunaDeliveryTrace.Producer(Guid.NewGuid().ToString("D"), byteLines.Add);
        for (var index = 0; index < 2000; index++) byteProducer.Record("trace_stage=test; padding=" + new string('x', 4096));
        Assert.True(byteLines.Sum(line => Encoding.UTF8.GetByteCount(line) + 1) <= NknTunaDeliveryTrace.Producer.ByteLimit);
        Assert.Contains(byteLines, line => line.Contains("record_or_byte_cap", StringComparison.Ordinal));
    }

    [Fact]
    public void TunaDeliveryTrace_UnavailableCallbackRecordsReadButNeverCallbackEntry()
    {
        var lines = new List<string>();
        var producer = new NknTunaDeliveryTrace.Producer(Guid.NewGuid().ToString("D"), lines.Add);
        using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File, 16);
        var method = typeof(NknTunaSidecarClient).GetMethod("HandleDataFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(client, [new NknTunaSidecarFrame(NknTunaSidecarFrameType.Data, NknTunaSidecarLane.Bulk, 1, 0, [1]), producer.Connect()]);
        Assert.Contains(lines, line => line.Contains("trace_stage=ipc_read_completed;", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("trace_stage=callback_rejected_unavailable;", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("trace_stage=callback_entered;", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("listener")]
    [InlineData("dialer")]
    public void TunaDeliveryTrace_RealStdoutAdaptersPreserveOnlyWhitelistedFields(string role)
    {
        var lines = new List<string>();
        var capture = Guid.NewGuid().ToString("D");
        using var traceOverride = NknTunaDeliveryTrace.OverrideForTests(new NknTunaDeliveryTrace.Producer(capture,
            line => lines.Add(SensitiveDataRedactor.Redact(line))));
        using var process = new Process(); // Never started; no job assignment, bridge, or network.
        var owner = (NknTunaSidecarProcessOwner)Activator.CreateInstance(typeof(NknTunaSidecarProcessOwner),
            BindingFlags.NonPublic | BindingFlags.Instance, null, [role, process, null], null)!;
        var wire = NknTunaDeliveryTrace.WireHash([1, 2, 3]);
        var line = ChildTrace(capture, wire);
        if (role == "listener")
        {
            using var supervisor = new NknTunaListenerSidecarSupervisor(new NknTunaListenerSidecarOptions
            {
                SidecarExePath = "unused", WalletPath = "unused", TakeWalletPassword = () => null,
                MaxPriceNknPerMb = "0", MaxTotalMiB = 0, MaxDurationSec = 0,
            });
            typeof(NknTunaListenerSidecarSupervisor).GetMethod("HandleOwnedStdoutLine", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(supervisor, [owner, process, line, new TaskCompletionSource<NknTunaListenerSidecarEndpoint>(), Stopwatch.StartNew()]);
        }
        else
        {
            using var lane = new NknTunaAccelerationLane(NknTunaAccelerationOptions.Disabled);
            Assert.Equal(true, typeof(NknTunaAccelerationLane).GetMethod("TryPreserveOwnedDialerTrace", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [owner, line]));
            Assert.False(lane.IsAvailable);
        }
        var retained = Assert.Single(lines);
        Assert.Contains("trace_wire=" + wire, retained, StringComparison.Ordinal);
        Assert.Contains("trace_owner=" + Guid.Parse(owner.RunId).ToString("D"), retained, StringComparison.Ordinal);
        Assert.Contains("trace_role=" + role, retained, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive_payload", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("raw_peer", retained, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("wire")]
    [InlineData("duplicate")]
    [InlineData("capture")]
    public void TunaDeliveryTrace_MalformedChildCannotInventAStage(string corruption)
    {
        var lines = new List<string>();
        var capture = Guid.NewGuid().ToString("D");
        using var traceOverride = NknTunaDeliveryTrace.OverrideForTests(new NknTunaDeliveryTrace.Producer(capture, lines.Add));
        var wire = NknTunaDeliveryTrace.WireHash([1]);
        var line = ChildTrace(capture, wire);
        line = corruption switch
        {
            "malformed" => "{tuna_delivery_trace",
            "wire" => line.Replace(wire, "payload_not_a_digest", StringComparison.Ordinal),
            "duplicate" => line.Replace("\"trace_order\":1", "\"trace_order\":1,\"trace_order\":2", StringComparison.Ordinal),
            _ => line.Replace(capture, Guid.NewGuid().ToString("D"), StringComparison.Ordinal),
        };
        Assert.True(NknTunaDeliveryTrace.PreserveChildLine(line, Guid.NewGuid().ToString("N"), "listener"));
        Assert.Contains("malformed_child_trace", Assert.Single(lines), StringComparison.Ordinal);
    }

    [Fact]
    public void TunaDeliveryTrace_RedactorDoesNotAllowUnrelatedSecrets()
    {
        var wire = NknTunaDeliveryTrace.WireHash([1]);
        Assert.Contains(wire, SensitiveDataRedactor.Redact("event=tuna_delivery_trace; trace_wire=" + wire), StringComparison.Ordinal);
        Assert.DoesNotContain(wire, SensitiveDataRedactor.Redact("event=unrelated; trace_wire=" + wire), StringComparison.Ordinal);
        Assert.DoesNotContain(wire, SensitiveDataRedactor.Redact("event=tuna_delivery_trace; secret=" + wire), StringComparison.Ordinal);
    }

    [Fact]
    public void TunaDeliveryTrace_OutputFailureIsVisibleInNextRecord()
    {
        var lines = new List<string>();
        var attempts = 0;
        var producer = new NknTunaDeliveryTrace.Producer(Guid.NewGuid().ToString("D"), line =>
        {
            if (++attempts == 1) throw new IOException("fixture output failure");
            lines.Add(line);
        });
        producer.Connect();
        producer.Record("trace_stage=test_observation");
        Assert.Contains("trace_incomplete=1", Assert.Single(lines), StringComparison.Ordinal);
        Assert.Contains("trace_order=2", lines[0], StringComparison.Ordinal);
    }

    private static string ChildTrace(string capture, string wire)
        => JsonSerializer.Serialize(new
        {
            @event = "tuna_delivery_trace", trace_capture = capture, trace_producer = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            trace_direction = "app_to_tuna", trace_stage = "write_completed", trace_reason = "none", trace_order = 1,
            trace_connection = 1, trace_seq = 1, trace_occurrence = 1, trace_wire = wire, trace_bytes = 3,
            payload = "sensitive_payload", peer = "raw_peer",
        });
}
