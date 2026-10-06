using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NLink.Core.Logging;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class TunaDeliveryTraceRetainedCaptureTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task TunaDeliveryTrace_BothStdoutAdaptersSurviveFullCaptureWhenCompactOmitsThem()
    {
        var capture = Guid.NewGuid().ToString("D");
        var logStart = CoreSmokeTestsBase.GetOperationalLogLength();
        using var traceOverride = NknTunaDeliveryTrace.OverrideForTests(new NknTunaDeliveryTrace.Producer(capture,
            line => LocalOperationalLog.Info("NKN.Tuna", line)));
        var wire = NknTunaDeliveryTrace.WireHash([1, 2, 3]);
        var line = JsonSerializer.Serialize(new
        {
            @event = "tuna_delivery_trace", trace_capture = capture, trace_producer = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            trace_direction = "app_to_tuna", trace_stage = "write_completed", trace_reason = "none", trace_order = 1,
            trace_connection = 1, trace_seq = 1, trace_occurrence = 1, trace_wire = wire, trace_bytes = 3,
        });
        foreach (var role in new[] { "listener", "dialer" })
        {
            using var process = new Process(); // Unstarted, no job assignment or network.
            var owner = (NknTunaSidecarProcessOwner)Activator.CreateInstance(typeof(NknTunaSidecarProcessOwner),
                BindingFlags.NonPublic | BindingFlags.Instance, null, [role, process, null], null)!;
            if (role == "listener")
            {
                using var supervisor = new NknTunaListenerSidecarSupervisor(new NknTunaListenerSidecarOptions
                {
                    SidecarExePath = "unused", WalletPath = "unused", TakeWalletPassword = () => null,
                    MaxPriceNknPerMb = "0", MaxTotalMiB = 0, MaxDurationSec = 0,
                });
                typeof(NknTunaListenerSidecarSupervisor).GetMethod("HandleOwnedStdoutLine", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(supervisor, [owner, process, line, new TaskCompletionSource<NknTunaListenerSidecarEndpoint>(), Stopwatch.StartNew()]);
            }
            else
            {
                Assert.Equal(true, typeof(NknTunaAccelerationLane).GetMethod("TryPreserveOwnedDialerTrace", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [owner, line]));
            }
        }
        var fullLog = CoreSmokeTestsBase.ReadOperationalLogTail(logStart);
        var traceLines = fullLog.Split('\n').Where(value => value.Contains("trace_capture=" + capture, StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, traceLines.Length);
        Assert.All(traceLines, value => Assert.Contains(wire, value, StringComparison.Ordinal));
        // The existing streaming ledger writes separators between lines, with no trailing newline.
        var source = string.Join(Environment.NewLine, traceLines.Select(value => value.TrimEnd('\r')));
        var sourceBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(source));
        var processorPath = Path.Combine(FindRepoRoot(), "tools", "GuiSmokeLogLedgerStreamingProcessor.cs").Replace("'", "''", StringComparison.Ordinal);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Add-Type -Path '{{processorPath}}'
            $root = Join-Path ([IO.Path]::GetTempPath()) ('nlink-trace-capture-' + [guid]::NewGuid().ToString('N'))
            [void][IO.Directory]::CreateDirectory($root)
            try {
                $source = Join-Path $root 'source.log'
                $retained = Join-Path $root 'retained.log'
                $writer = Join-Path $root 'writer.log'
                $compact = Join-Path $root 'compact.log'
                [IO.File]::WriteAllBytes($source, [Convert]::FromBase64String('{{sourceBase64}}'))
                $result = [NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::Process(
                    $source, 2L, 0L, $retained, $writer, '', '', [string[]]@(),
                    $compact, [string[]]@('filetransfer_data_path_send_result'))
                if ($result.RetainedLineCount -ne 2 -or $result.SemanticLineCount -ne 0) { throw 'trace capture counts changed' }
                $sha = [Security.Cryptography.SHA256]::Create()
                try {
                    $sourceHash = [Convert]::ToBase64String($sha.ComputeHash([IO.File]::ReadAllBytes($source)))
                    $retainedHash = [Convert]::ToBase64String($sha.ComputeHash([IO.File]::ReadAllBytes($retained)))
                    if ($sourceHash -ne $retainedHash) { throw 'full trace bytes changed' }
                } finally { $sha.Dispose() }
                if (([IO.File]::ReadAllText($compact)).Length -ne 0) { throw 'trace became primary compact evidence' }
                Write-Output 'trace-full-retention-ok'
            } finally {
                if (-not ([IO.Path]::GetFullPath($root)).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'unsafe cleanup path' }
                [IO.Directory]::Delete($root, $true)
            }
            """;
        // Calls only the streaming copy/filter, never an acceptance report or evaluator.
        var result = await RunPowerShellScriptTextAsync(script, Array.Empty<string>());
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("trace-full-retention-ok", result.Stdout, StringComparison.Ordinal);
    }
}
