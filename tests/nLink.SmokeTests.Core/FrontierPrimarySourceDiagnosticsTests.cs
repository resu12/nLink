using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FrontierPrimarySourceDiagnosticsTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("LocalFrontierPrimarySource.cs", "3eaabbd0cf73a81ce618e1bb332de90658f8dcc1e0670168a705575c43f6d20d")]
    [InlineData("LocalFrontierPrimaryWorker.cs", "1b6cb2b3ad8599d87c2be10b2392e5bf13d6386640a7fc38c6cfa5d3904326ce")]
    public async Task DefaultProjection_MatchesReviewedContract(string file, string expected)
    {
        var source = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tools/FileTransferOps", file));
        var normal = Project(source, false);
        if (file == "LocalFrontierPrimaryWorker.cs")
        {
            foreach (var hook in new[] { "                        AttachTransferPair(name);\n", "                    CloseTransferPair();\n" })
            {
                Assert.Single(Regex.Matches(normal, Regex.Escape(hook)));
                normal = normal.Replace(hook, string.Empty, StringComparison.Ordinal);
            }
        }
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normal))).ToLowerInvariant());
        Assert.DoesNotContain("PrimarySourceCheckForTest", normal);
        Assert.DoesNotContain("PrimaryBaseCheckForTest", normal);
    }

    [Fact]
    public async Task DiagnosticProfile_PreservesOriginalShortCircuitChecksAndFirstClose()
    {
        var root = FindRepoRoot();
        var worker = Regex.Replace(Project(await File.ReadAllTextAsync(Path.Combine(root, "tools/FileTransferOps/LocalFrontierPrimaryWorker.cs")), true), @"\s+", " ");
        var source = Regex.Replace(Project(await File.ReadAllTextAsync(Path.Combine(root, "tools/FileTransferOps/LocalFrontierPrimarySource.cs")), true), @"\s+", " ");
        Assert.Contains("private bool ResourceCurrent { get { return ResourceCurrentWithin(LocalPublicationInputLimits.ProducerMilliseconds); } }", worker);
        Assert.Contains("return PrimarySourceCheckForTest(\"resource:closed\", !closed) && PrimarySourceBudgetForTest(\"resource:budget\", work.ElapsedMilliseconds, maximumMilliseconds) && PrimarySourceCheckForTest(\"resource:base\", PrimaryMeasuredCurrentForTest(0)) && PrimarySourceCheckForTest(\"resource:scope\", PrimaryMeasuredCurrentForTest(1)) && PrimarySourceCheckForTest(\"resource:files\", PrimaryMeasuredCurrentForTest(2)) && PrimarySourceCheckForTest(\"resource:pins\", PrimaryMeasuredCurrentForTest(3));", worker);
        Assert.Contains("return PrimaryBaseCheckForTest(\"identity\", Object.ReferenceEquals(frontierOriginal, this)) && PrimaryBaseCheckForTest(\"disposed\", Volatile.Read(ref disposed) == 0) && PrimaryBaseCheckForTest(\"parent-missing\", parentExit != null) && PrimaryBaseCheckForTest(\"parent-exit\", !parentExit.WaitOne(0)) && PrimaryBaseCheckForTest(\"request-closed\", !closed.WaitOne(0));", worker);
        Assert.Contains("if (!PrimarySourceCheckForTest(\"read:closed\", !closed) || !PrimarySourceBudgetForTest(\"read:budget\", work.ElapsedMilliseconds) || !PrimarySourceCheckForTest(\"read:base\", PrimaryMeasuredCurrentForTest(0))) throw new IOException(\"frontier_source_not_current\");", source);
        Assert.Contains("private void CheckSource() { if (!ResourceCurrent) throw new IOException(\"frontier_source_not_current\"); }", source);
        Assert.Contains("if (closed) return; closed = true; PrimarySourceCloseForTest = caller;", worker);
        Assert.Contains("catch (Exception error) { PrimarySourceExceptionForTest = error.GetType().FullName + \":\" + error.Message; Close(); throw; }", source);
        Assert.DoesNotContain("Console.", worker + source);
        var support = Regex.Replace(await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimary.cs")), @"\s+", " ");
        Assert.Contains("if (!current && PrimarySourceRejectionForTest == \"none\")", support);
        Assert.Contains("private bool PrimarySourceBudgetForTest(string reason, long elapsed, int maximumMilliseconds = LocalPublicationInputLimits.ProducerMilliseconds)", support);
        Assert.Contains("return PrimarySourceCheckForTest(reason, elapsed <= maximumMilliseconds, elapsed);", support);
        Assert.Contains("if (!current) Interlocked.CompareExchange(ref PrimaryBaseRejectionForTest, reason, \"none\"); return current;", support);
        var helper = support[support.IndexOf("private bool PrimarySourceCheckForTest", StringComparison.Ordinal)..];
        Assert.DoesNotContain("IsCurrent", helper);
        Assert.DoesNotContain("Start(", helper);
        Assert.DoesNotContain("Stop(", helper);
        Assert.DoesNotContain("Reset(", helper);
        Assert.Contains("return current;", helper);
    }

    [Fact]
    public async Task WorkProfile_MeasuresEachOriginalOperationOnceWithoutChangingClocks()
    {
        var root = FindRepoRoot();
        var support = Regex.Replace(await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimary.cs")), @"\s+", " ");
        var measure = support[support.IndexOf("private bool PrimaryMeasuredCurrentForTest", StringComparison.Ordinal)..support.IndexOf("internal string GetPrimaryWorkProfileForTest", StringComparison.Ordinal)];
        Assert.Contains("(work.IsRunning ? (primaryReadingForTest ? 4 : 0) : 8) + predicate", measure);
        foreach (var item in new[] { "case 0: return owner.FrontierBaseCurrent;", "case 1: return scope.IsCurrent;", "case 2: return files == null || files.IsCurrent;", "case 3: return pins == null || pins.IsCurrent;" })
            Assert.Equal(1, Regex.Matches(measure, Regex.Escape(item)).Count);
        Assert.Contains("finally { long elapsed = Stopwatch.GetTimestamp() - started; PrimaryCheckTicksForTest[index] += elapsed; PrimaryCheckCallsForTest[index]++; if (primaryOriginForTest) { PrimaryOriginCheckTicksForTest[predicate] += elapsed; PrimaryOriginCheckCallsForTest[predicate]++; } }", measure);
        foreach (var mutation in new[] { "Start(", "Stop(", "Reset(", "Restart(", "catch", "Console." }) Assert.DoesNotContain(mutation, measure);
        var output = support[support.IndexOf("internal string GetPrimaryWorkProfileForTest", StringComparison.Ordinal)..support.IndexOf("internal string PrimarySourceRejectionForTest", StringComparison.Ordinal)];
        Assert.DoesNotContain("IsCurrent", output);
        Assert.DoesNotContain("PrimaryMeasuredCurrentForTest", output);
        var source = Regex.Replace(Project(await File.ReadAllTextAsync(Path.Combine(root, "tools/FileTransferOps/LocalFrontierPrimarySource.cs")), true), @"\s+", " ");
        Assert.Contains("long readWorkStartForTest = work.ElapsedTicks; primaryReadingForTest = true; PrimaryReadCallsForTest++; work.Start();", source);
        Assert.Contains("finally { work.Stop(); PrimaryReadWorkTicksForTest += work.ElapsedTicks - readWorkStartForTest; primaryReadingForTest = false; }", source);
        Assert.Contains("position += take; return take; } finally { ReadTicksForTest += System.Diagnostics.Stopwatch.GetTimestamp() - readStartForTest; ReadCallsForTest++; }", source);
        Assert.Contains("if (!OriginalSource(candidate)) return 0; long originWorkStartForTest = work.ElapsedTicks;", source);
        Assert.Contains("primaryOriginForTest = true; PrimaryOriginCallsForTest++; work.Start();", source);
        Assert.Contains("finally { work.Stop(); PrimaryOriginWorkTicksForTest += work.ElapsedTicks - originWorkStartForTest; primaryOriginForTest = false; }", source);
        Assert.Contains("int originForTest = sourceEnded && ordinal > 0 && ordinal <= sourceLines.Count && Object.ReferenceEquals(sourceLines[ordinal - 1], line) ? facts.SemanticOrigin(ordinal - 1) : 0; if (originForTest > 0) PrimaryOriginAcceptedForTest++; return originForTest;", source);
    }

    [Theory]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "valid")]
    [InlineData("pwsh", "read-budget")]
    [InlineData("pwsh", "post-scope")]
    [InlineData("pwsh", "post-facts")]
    [InlineData("pwsh", "pin-close")]
    [InlineData("pwsh", "parent-cancel")]
    [InlineData("pwsh", "origin-budget")]
    [InlineData("pwsh", "origin-substitution")]
    [InlineData("pwsh", "origin-index")]
    [InlineData("pwsh", "copied-reader")]
    public async Task NativeControls_RecordFirstFailureWithoutChangingOutcome(string host, string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", "primary-work-profile-20260927", host + "-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierGuiLaunchRequest.ps1"));
        var result = await RunPowerShellScriptTextAsync(script,
            [root, directory, "cursor-parent-files-facts-ack-commit-primary-worker-source-diagnostic-" + scenario, "seal",
                typeof(NLink.Core.FileTransfer.SessionFileTransferService).Assembly.Location, typeof(FrontierGenuineSubmissionFixture).Assembly.Location], host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        Assert.Contains("primary-source-diagnostic; case=" + scenario, result.Stdout);
        Assert.Contains("primary-work-profile; frequency=", result.Stdout);
        Assert.Contains("frontier-primary-source=" + scenario, result.Stdout);
    }

    private static string Project(string source, bool profile)
    {
        var output = new List<string>();
        bool? branch = null;
        foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            switch (line.Trim())
            {
                case "#if NLINK_TEST_ATTEMPT_DISPATCH": Assert.Null(branch); branch = profile; break;
                case "#elif NLINK_TEST_NATIVE_SELECTION_COMPONENT_PROFILE": Assert.NotNull(branch); branch = false; break;
                case "#else": Assert.NotNull(branch); branch = !profile; break;
                case "#endif": Assert.NotNull(branch); branch = null; break;
                default: Assert.False(line.TrimStart().StartsWith('#')); if (branch != false) output.Add(line); break;
            }
        }
        Assert.Null(branch);
        return string.Join("\n", output);
    }
}
