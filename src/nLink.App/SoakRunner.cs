using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NLink.App;

internal static class SoakRunner
{
    internal sealed record SoakRunnerOptions(
        int? Cycles,
        int? DelayMs,
        string? Transport,
        string? BridgeReuseMode,
        bool FailOnGate);

    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!TryParseOptions(args, out var options, out var parseError))
        {
            error.WriteLine($"FAIL: {parseError}");
            return Task.FromResult(1);
        }

        var benchArgs = BuildBenchmarkArgs(options!);
        output.WriteLine("Soak runner (using benchmark pipeline)");
        output.WriteLine($"  Args: {string.Join(" ", benchArgs)}");
        return BenchmarkRunner.RunAsync(benchArgs, output, error, ct);
    }

    internal static bool TryParseOptionsForTests(string[] args, out SoakRunnerOptions? options, out string error)
        => TryParseOptions(args, out options, out error);

    internal static string[] BuildBenchmarkArgsForTests(SoakRunnerOptions options)
        => BuildBenchmarkArgs(options);

    private static bool TryParseOptions(string[] args, out SoakRunnerOptions? options, out string error)
    {
        options = null;
        error = string.Empty;

        int? cycles = null;
        int? delayMs = null;
        string? transport = null;
        string? bridgeReuseMode = null;
        var failOnGate = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string key;
            string? value = null;
            var eq = arg.IndexOf('=');
            if (eq > 0)
            {
                key = arg[..eq];
                value = arg[(eq + 1)..];
            }
            else
            {
                key = arg;
                if (key is "--soak" or "--fail-on-gate")
                {
                    value = null;
                }
                else if (i + 1 < args.Length)
                {
                    value = args[++i];
                }
            }

            switch (key.ToLowerInvariant())
            {
                case "--soak":
                    break;
                case "--fail-on-gate":
                    failOnGate = string.IsNullOrWhiteSpace(value) || !bool.TryParse(value, out var b) || b;
                    break;
                case "--cycles":
                    if (!int.TryParse(value, out var parsedCycles) || parsedCycles <= 0)
                    {
                        error = "Invalid --cycles value.";
                        return false;
                    }
                    cycles = parsedCycles;
                    break;
                case "--delay-ms":
                    if (!int.TryParse(value, out var parsedDelay) || parsedDelay < 0)
                    {
                        error = "Invalid --delay-ms value.";
                        return false;
                    }
                    delayMs = parsedDelay;
                    break;
                case "--transport":
                    var t = (value ?? string.Empty).Trim().ToLowerInvariant();
                    if (t is not ("devlocal" or "nkn"))
                    {
                        error = "Invalid --transport value. Use devlocal or nkn.";
                        return false;
                    }
                    transport = t;
                    break;
                case "--bridge-reuse-mode":
                    var mode = (value ?? string.Empty).Trim().ToLowerInvariant();
                    if (mode is not ("persession" or "keepalive"))
                    {
                        error = "Invalid --bridge-reuse-mode value. Use persession or keepalive.";
                        return false;
                    }
                    bridgeReuseMode = mode;
                    break;
            }
        }

        options = new SoakRunnerOptions(cycles, delayMs, transport, bridgeReuseMode, failOnGate);
        return true;
    }

    private static string[] BuildBenchmarkArgs(SoakRunnerOptions options)
    {
        var args = new List<string> { "--bench" };

        if (options.Cycles.HasValue)
        {
            args.Add("--cycles");
            args.Add(options.Cycles.Value.ToString());
        }

        if (options.DelayMs.HasValue)
        {
            args.Add("--delay-ms");
            args.Add(options.DelayMs.Value.ToString());
        }

        if (!string.IsNullOrWhiteSpace(options.Transport))
        {
            args.Add("--transport");
            args.Add(options.Transport!);
        }

        if (!string.IsNullOrWhiteSpace(options.BridgeReuseMode))
        {
            args.Add("--bridge-reuse-mode");
            args.Add(options.BridgeReuseMode!);
        }

        if (options.FailOnGate)
        {
            args.Add("--reliability-gate");
        }

        return args.ToArray();
    }
}

