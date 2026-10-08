using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("FileTransferInfraSuite", "SessionPreparationLane")]
[Trait("Area", "Core")]
public sealed class NknTunaSetupDiagnosticsTests
{
    private const string ProviderLine = """
        {"event":"provider_path_state_changed","role":"listener","stage":"startup","index":2,"usable":true,"stateReason":"usable","endpointHash":"03a7284c89aae09c","previousEndpointHash":"","observedUnixMs":1790176100000,"ip":"private-diagnostic-value","localIpc":"must-not-complete-ready","address":"must-not-install"}
        """;
    private const string DialLine = "2026/09/23 15:08:25 Tuna session dial to ip 192.0.2.10 port 43210 err: dial tcp 192.0.2.10:43210: connect: connection refused";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListenerCallback_RetainsCapturedOwnerWithoutGrantingReadiness(bool stale)
    {
        using var oldProcess = new Process();
        using var nextProcess = new Process();
        using var oldOwner = Owner("listener", oldProcess);
        using var nextOwner = Owner("listener", nextProcess);
        using var supervisor = new NknTunaListenerSidecarSupervisor(new()
        {
            SidecarExePath = "unused", WalletPath = "unused", TakeWalletPassword = () => null,
            MaxPriceNknPerMb = "0", MaxTotalMiB = 0, MaxDurationSec = 0,
        });
        var request = new NknTunaListenerStartRequest("test-peer", NknAccelerationLaneKind.File,
            NknTunaListenerLifetimePolicy.SessionPreparation, "test-preparation");
        Set(supervisor, "processOwner", stale ? nextOwner : oldOwner);
        Set(supervisor, "currentRequest", request);
        var stateChanges = 0;
        supervisor.StateChanged += (_, _) => stateChanges++;
        var ready = new TaskCompletionSource<NknTunaListenerSidecarEndpoint>();
        var logStart = CoreSmokeTestsBase.GetOperationalLogLength();
        typeof(NknTunaListenerSidecarSupervisor).GetMethod("HandleOwnedStdoutLine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(supervisor, [oldOwner, oldProcess, ProviderLine, ready, Stopwatch.StartNew()]);
        var lines = CoreSmokeTestsBase.ReadOperationalLogTail(logStart).Split('\n')
            .Where(line => line.Contains("event=tuna_setup_diagnostic;", StringComparison.Ordinal)).ToArray();
        var diagnostic = Assert.Single(lines);
        Assert.Contains("run_id=" + Guid.Parse(oldOwner.RunId).ToString("D"), diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(Guid.Parse(nextOwner.RunId).ToString("D"), diagnostic, StringComparison.Ordinal);
        Assert.Contains("endpoint_hash=03a7284c89aae09c", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-diagnostic-value", diagnostic, StringComparison.Ordinal);
        Assert.False(ready.Task.IsCompleted);
        Assert.Equal(0, stateChanges);
        Assert.Same(request, Field(supervisor, "currentRequest"));
        Assert.Same(stale ? nextOwner : oldOwner, Field(supervisor, "processOwner"));
        Assert.Null(Field(supervisor, "endpoint"));
    }

    [Fact]
    public void DialerCallback_RetainsCapturedOwnerAndOriginalGenericDiagnostic()
    {
        using var process = new Process();
        using var owner = Owner("dialer", process);
        owner.Stop("test_retired_owner");
        var logStart = CoreSmokeTestsBase.GetOperationalLogLength();
        typeof(NknTunaAccelerationLane).GetMethod("HandleOwnedDialerStderr", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [owner, DialLine]);
        var log = CoreSmokeTestsBase.ReadOperationalLogTail(logStart);
        Assert.Contains("event=tuna_dialer_sidecar_stderr; line_len=" + DialLine.Length, log, StringComparison.Ordinal);
        var line = Assert.Single(log.Split('\n').Where(value => value.Contains("event=tuna_setup_diagnostic;", StringComparison.Ordinal)));
        Assert.Contains("run_id=" + Guid.Parse(owner.RunId).ToString("D"), line, StringComparison.Ordinal);
        Assert.Contains("after_stop=1", line, StringComparison.Ordinal);
        Assert.Contains("endpoint_hash=c4a5a39e9b6b40cf; error_category=tcp_connection_refused", line, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.10", line, StringComparison.Ordinal);
        Assert.DoesNotContain("43210", line, StringComparison.Ordinal);
        Assert.False(owner.IsRunning);
    }

    [Theory]
    [InlineData("dial tcp 192.0.2.10:43210: connect: connection refused", "tcp_connection_refused")]
    [InlineData("dial tcp 192.0.2.10:43210: connectex: No connection could be made because the target machine actively refused it.", "tcp_connection_refused")]
    [InlineData("dial tcp 192.0.2.10:43210: i/o timeout", "timeout")]
    [InlineData("context deadline exceeded", "timeout")]
    [InlineData("seed=do-not-retain; arbitrary-error", "other")]
    public void DialParser_RetainsOnlyFingerprintAndBoundedCategory(string error, string category)
    {
        var lines = new List<string>();
        var diagnostics = new NknTunaSetupDiagnostics("dialer", 42, Guid.NewGuid(), lines.Add);
        diagnostics.DialErrorLine("Tuna session dial to ip 192.0.2.10 port 43210 err: " + error);
        var line = Assert.Single(lines);
        Assert.Contains("endpoint_hash=c4a5a39e9b6b40cf; error_category=" + category, line, StringComparison.Ordinal);
        Assert.DoesNotContain(error, line, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.10", line, StringComparison.Ordinal);
        Assert.DoesNotContain("43210", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DialParser_HashesLiteralIpv6LikeGoWithoutAddingBrackets()
    {
        var lines = new List<string>();
        new NknTunaSetupDiagnostics("dialer", 42, Guid.NewGuid(), lines.Add)
            .DialErrorLine("Tuna session dial to ip 2001:db8::1 port 43210 err: context deadline exceeded");
        Assert.Contains("endpoint_hash=e61bf65e6abcc23e", Assert.Single(lines), StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderProjection_RetainsPreviousEndpointButDropsAllUnlistedFields()
    {
        var lines = new List<string>();
        new NknTunaSetupDiagnostics("listener", 42, Guid.NewGuid(), lines.Add).ProviderLine(
            ProviderLine.Replace("\"previousEndpointHash\":\"\"", "\"previousEndpointHash\":\"aaaaaaaaaaaaaaaa\"", StringComparison.Ordinal));
        var line = Assert.Single(lines);
        Assert.Contains("path_index=2; endpoint_hash=03a7284c89aae09c; previous_endpoint_hash=aaaaaaaaaaaaaaaa; usable=1; state_reason=usable; observed_unix_ms=1790176100000", line, StringComparison.Ordinal);
        Assert.DoesNotContain("private-diagnostic-value", line, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("bad_hash")]
    [InlineData("bad_index")]
    [InlineData("bad_type")]
    [InlineData("bad_timestamp")]
    [InlineData("bad_reason")]
    [InlineData("bad_role")]
    [InlineData("incomplete")]
    [InlineData("oversized")]
    public void ProviderParser_RejectsMalformedDetailsWithoutThrowing(string change)
    {
        var input = change switch
        {
            "duplicate" => ProviderLine.Replace("\"index\":2", "\"index\":2,\"index\":3", StringComparison.Ordinal),
            "bad_hash" => ProviderLine.Replace("03a7284c89aae09c", "secret;injected=true", StringComparison.Ordinal),
            "bad_index" => ProviderLine.Replace("\"index\":2", "\"index\":-1", StringComparison.Ordinal),
            "bad_type" => ProviderLine.Replace("\"usable\":true", "\"usable\":\"true\"", StringComparison.Ordinal),
            "bad_timestamp" => ProviderLine.Replace("1790176100000", "9223372036854775808", StringComparison.Ordinal),
            "bad_reason" => ProviderLine.Replace("\"stateReason\":\"usable\"", "\"stateReason\":\"secret\"", StringComparison.Ordinal),
            "bad_role" => ProviderLine.Replace("\"role\":\"listener\"", "\"role\":\"dialer\"", StringComparison.Ordinal),
            "incomplete" => "{\"event\":\"provider_path_state_changed\"}",
            _ => ProviderLine + new string(' ', NknTunaSetupDiagnostics.LineLimit),
        };
        var lines = new List<string>();
        new NknTunaSetupDiagnostics("listener", 42, Guid.NewGuid(), lines.Add).ProviderLine(input);
        var line = Assert.Single(lines);
        Assert.Contains("kind=invalid", line, StringComparison.Ordinal);
        Assert.DoesNotContain("endpoint_hash=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("prefix")]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("newline")]
    [InlineData("oversized")]
    public void DialParser_RejectsMalformedDetailsWithoutFabricatingCategory(string change)
    {
        var input = change switch
        {
            "prefix" => "unrecognized " + DialLine,
            "host" => DialLine.Replace("192.0.2.10 port", "secret port", StringComparison.Ordinal),
            "port" => DialLine.Replace("port 43210", "port 65536", StringComparison.Ordinal),
            "newline" => DialLine + "\nsecret",
            _ => DialLine + new string('x', NknTunaSetupDiagnostics.LineLimit),
        };
        var lines = new List<string>();
        new NknTunaSetupDiagnostics("dialer", 42, Guid.NewGuid(), lines.Add).DialErrorLine(input);
        var line = Assert.Single(lines);
        Assert.Contains("kind=invalid", line, StringComparison.Ordinal);
        Assert.DoesNotContain("error_category=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentCapAndLateCallbacks_AreIsolatedFromReplacementOwner()
    {
        var lines = new List<string>();
        var old = new NknTunaSetupDiagnostics("listener", 1, Guid.NewGuid(), lines.Add);
        Parallel.For(0, 1000, _ => old.ProviderLine(ProviderLine));
        Assert.Equal(64, lines.Count(line => line.Contains("kind=provider_path;", StringComparison.Ordinal)));
        Assert.Single(lines.Where(line => line.Contains("kind=truncated;", StringComparison.Ordinal)));
        for (var i = 0; i < 64; i++) Assert.Contains($"record={i + 1};", lines[i], StringComparison.Ordinal);
        old.ObserveStop();
        old.ObserveStop();
        old.ProviderLine(ProviderLine);
        Assert.Equal(66, lines.Count); // 64 details, one cap marker, one at-stop snapshot.
        var nextId = Guid.NewGuid();
        var next = new NknTunaSetupDiagnostics("listener", 2, nextId, lines.Add);
        next.ProviderLine(ProviderLine);
        Assert.Contains($"run_id={nextId:D}; record=1; after_stop=0;", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownOutputAndMissingDetails_AreNotReportedAsDialFailure()
    {
        var lines = new List<string>();
        var diagnostics = new NknTunaSetupDiagnostics("dialer", 42, Guid.NewGuid(), lines.Add);
        diagnostics.DialErrorLine("unrelated secret stderr");
        diagnostics.ProviderLine(ProviderLine);
        Assert.Empty(lines);
        diagnostics.ObserveStop();
        Assert.Contains("diagnostic_status=no_details_at_stop", Assert.Single(lines), StringComparison.Ordinal);
        diagnostics.DialErrorLine(DialLine);
        Assert.Contains("after_stop=1", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void FailedDiagnosticSink_CannotThrowIntoIoOrCleanup()
    {
        var diagnostics = new NknTunaSetupDiagnostics("dialer", 42, Guid.NewGuid(), _ => throw new IOException("sink failure"));
        for (var i = 0; i < 100; i++) diagnostics.DialErrorLine(DialLine);
        diagnostics.StreamErrorLine("handle msg error: decrypt message failed");
        diagnostics.ObserveStop();
    }

    [Theory]
    [InlineData(null, "unavailable")]
    [InlineData("", "none")]
    [InlineData("session closed", "session_closed")]
    [InlineData("EOF", "eof")]
    [InlineData("unexpected EOF", "unexpected_eof")]
    [InlineData("context canceled", "context_canceled")]
    [InlineData("context deadline exceeded", "timeout")]
    [InlineData("read deadline exceeded", "timeout")]
    [InlineData("write deadline exceeded", "timeout")]
    [InlineData("decrypt message failed", "decrypt_failed")]
    [InlineData("io: read/write on closed pipe", "closed_pipe")]
    [InlineData("write tcp secret:123->private:456: i/o timeout", "timeout")]
    [InlineData("write tcp secret:123->private:456: use of closed network connection", "connection_closed")]
    [InlineData("write tcp secret:123->private:456: connection reset by peer", "connection_reset")]
    [InlineData("read tcp secret:123->private:456: wsarecv: An existing connection was forcibly closed by the remote host.", "connection_reset")]
    [InlineData("write tcp secret:123->private:456: broken pipe", "broken_pipe")]
    [InlineData("seed=secret; event=authoritative", "other")]
    [InlineData("EOF\nsecret", "other")]
    public void StreamCategory_EmitsOnlyAllowlistedToken(string? error, string category)
        => Assert.Equal(category, NknTunaSetupDiagnostics.StreamErrorCategory(error));

    [Theory]
    [InlineData("listener", false)]
    [InlineData("listener", true)]
    [InlineData("dialer", false)]
    [InlineData("dialer", true)]
    public void StreamCallback_RetainsOnlyFirstCategoryOnCapturedOwner(string role, bool stopped)
    {
        using var process = new Process();
        using var owner = Owner(role, process);
        if (stopped) owner.Stop("retired_before_output");
        var type = role == "listener" ? typeof(NknTunaListenerSidecarSupervisor) : typeof(NknTunaAccelerationLane);
        var callback = type.GetMethod(role == "listener" ? "HandleOwnedListenerStderr" : "HandleOwnedDialerStderr",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        const string input = "2026/10/08 12:32:34 handle msg error: decrypt message failed";
        var logStart = CoreSmokeTestsBase.GetOperationalLogLength();
        callback.Invoke(null, [owner, input]);
        callback.Invoke(null, [owner, "Write message error: write tcp secret:123: i/o timeout"]);
        var log = CoreSmokeTestsBase.ReadOperationalLogTail(logStart);
        var line = Assert.Single(log.Split('\n').Where(value => value.Contains("kind=stream_error;", StringComparison.Ordinal)));
        Assert.Contains("run_id=" + Guid.Parse(owner.RunId).ToString("D"), line, StringComparison.Ordinal);
        Assert.Contains("after_stop=" + (stopped ? "1" : "0"), line, StringComparison.Ordinal);
        Assert.Contains("operation=read; error_category=decrypt_failed", line, StringComparison.Ordinal);
        Assert.Contains($"event=tuna_{role}_sidecar_stderr; line_len={input.Length}", log, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain(input, log, StringComparison.Ordinal);
        Assert.False(owner.IsRunning);
    }

    [Theory]
    [InlineData("listener", "session closed", "session_closed")]
    [InlineData("dialer", "session closed", "session_closed")]
    [InlineData("listener", "secret; event=authoritative", "other")]
    [InlineData("dialer", "secret; event=authoritative", "other")]
    public void TerminalProjection_AddsCategoryWithoutChangingTerminalReason(string role, string error, string category)
    {
        var input = JsonSerializer.Serialize(new { terminalReason = "tuna_write_failed", rawReason = error, role });
        var logStart = CoreSmokeTestsBase.GetOperationalLogLength();
        if (role == "listener")
        {
            using var document = JsonDocument.Parse(input);
            typeof(NknTunaListenerSidecarSupervisor).GetMethod("LogBridgeTerminal", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [document.RootElement]);
        }
        else
            typeof(NknTunaAccelerationLane).GetMethod("LogBridgeTerminal", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [input]);
        var log = CoreSmokeTestsBase.ReadOperationalLogTail(logStart);
        Assert.Contains("terminal_reason=tuna_write_failed; stream_error_category=" + category, log, StringComparison.Ordinal);
        Assert.DoesNotContain(error, log, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamParser_IsBoundedAndDoesNotCaptureUnrecognizedOutput()
    {
        var lines = new List<string>();
        var diagnostics = new NknTunaSetupDiagnostics("listener", 42, Guid.NewGuid(), lines.Add);
        diagnostics.StreamErrorLine("unrecognized handle msg error: secret");
        diagnostics.StreamErrorLine("handle msg error: EOF\nsecret");
        diagnostics.StreamErrorLine("handle msg error: " + new string('x', NknTunaSetupDiagnostics.LineLimit));
        Assert.Empty(lines);
        Parallel.For(0, 100, _ => diagnostics.StreamErrorLine("Write message error: session closed"));
        Assert.Contains("operation=write; error_category=session_closed", Assert.Single(lines), StringComparison.Ordinal);
        Assert.Equal("other", NknTunaSetupDiagnostics.StreamErrorCategory(new string('x', NknTunaSetupDiagnostics.LineLimit + 1)));

        lines.Clear();
        diagnostics = new("listener", 42, Guid.NewGuid(), lines.Add);
        for (var i = 0; i < NknTunaSetupDiagnostics.RecordLimit; i++) diagnostics.ProviderLine(ProviderLine);
        diagnostics.StreamErrorLine("Read message error: EOF");
        Assert.Equal(NknTunaSetupDiagnostics.RecordLimit + 1, lines.Count);
        Assert.Contains("kind=truncated; reason=record_limit; limit=64", lines[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, line => line.Contains("kind=stream_error;", StringComparison.Ordinal));
    }

    private static NknTunaSidecarProcessOwner Owner(string role, Process process)
        => (NknTunaSidecarProcessOwner)Activator.CreateInstance(typeof(NknTunaSidecarProcessOwner),
            BindingFlags.NonPublic | BindingFlags.Instance, null, [role, process, null], null)!;

    private static object? Field(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void Set(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
