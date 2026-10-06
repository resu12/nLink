using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FrontierNativeAcceptanceDiagnosticsTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task DefaultProjection_ContainsOnlyReviewedPullOrderAndPayloadLifetime()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tools/FileTransferOps/LocalGuiWriterRelayTransfer.cs"));
        var normal = Project(source, false);
        const string fixedGuard = "                        // Validate registration before releasing the wrapper to forward or exit.\n" +
            "                        // After publication, keep the work limit without reusing pre-forward scope.\n" +
            "                        if (!current() || !transfer.MarkPulled() || transferWork.ElapsedMilliseconds > 5000) return null;";
        const string oldGuard = "                        if (!transfer.MarkPulled() || !current()) return null;";
        Assert.Contains(fixedGuard, normal);
        Assert.DoesNotContain(oldGuard, normal);
        const string payloadLifetime = "                    payloadLifetime.Cancel();\n";
        Assert.Single(Regex.Matches(normal, Regex.Escape(payloadLifetime)));
        Assert.Contains("if (closed) return; closed = true; payloadLifetime.Cancel(); foreach (var watch in watches) watch.Unregister(null); pins.Dispose(); transfer.Dispose();",
            Regex.Replace(normal, @"\s+", " "));
        // Reversing only these two reviewed changes reproduces the pre-diagnostics source.
        var beforeFix = normal.Replace(fixedGuard, oldGuard, StringComparison.Ordinal)
            .Replace(payloadLifetime, string.Empty, StringComparison.Ordinal);
        Assert.Equal("983bf3a6138949c6da89e0c459d17d10cee2bd095ea612092c171937e12bdd78",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(beforeFix))).ToLowerInvariant());
        Assert.DoesNotContain("DiagnosticForTest", normal);
        Assert.DoesNotContain("NativeRejectForTest", normal);
        Assert.DoesNotContain("ObserveNativePullForTest", normal);
    }

    [Fact]
    public async Task NativeAcceptanceTrace_PreservesPredicateOrderAndOriginalResults()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tools/FileTransferOps/LocalGuiWriterRelayTransfer.cs"));
        var profile = Regex.Replace(Project(source, true), @"\s+", " ");
        var begin = profile[profile.IndexOf("internal void BeginTake()", StringComparison.Ordinal)..profile.IndexOf("internal bool Accept(", StringComparison.Ordinal)];
        var accept = profile[profile.IndexOf("internal bool Accept(", StringComparison.Ordinal)..profile.IndexOf("internal FrontierRelayCustody Detach(", StringComparison.Ordinal)];
        Assert.Contains("if (" + string.Join(" || ", new[]
        {
            "NativeRejectForTest(\"begin\", \"closed\", closed)",
            "NativeRejectForTest(\"begin\", \"taking\", taking)",
            "NativeRejectForTest(\"begin\", \"owner-closed\", owner.closed.WaitOne(0))",
            "NativeRejectForTest(\"begin\", \"wrapper-exit\", !WrapperSucceeded())",
            "NativeRejectForTest(\"begin\", \"transfer-state\", transfer.State != 3)",
            "NativeRejectForTest(\"begin\", \"transfer-current\", !transfer.IsCurrent)",
            "NativeRejectForTest(\"begin\", \"pins-current\", !pins.IsCurrent)"
        }) + ") { Close(); return; } taking = true;", begin);
        Assert.Contains("if (" + string.Join(" || ", new[]
        {
            "NativeRejectForTest(\"accept\", \"closed\", closed)",
            "NativeRejectForTest(\"accept\", \"not-taking\", !taking)",
            "NativeRejectForTest(\"accept\", \"already-accepted\", accepted != null)",
            "NativeRejectForTest(\"accept\", \"wrapper-exit\", !WrapperSucceeded())",
            "NativeRejectForTest(\"accept\", \"transfer-current\", !transfer.IsCurrent)",
            "NativeRejectForTest(\"accept\", \"transfer-state\", transfer.State != 3)",
            "NativeRejectForTest(\"accept\", \"transfer-body\", !transfer.Matches(body))",
            "NativeRejectForTest(\"accept\", \"pins-current\", !pins.IsCurrent)",
            "NativeRejectForTest(\"accept\", \"completion-facts\", !facts.MatchesCompletion(result.WriterContext))"
        }) + ") return false;", accept);
        foreach (var body in new[] { begin, accept })
        {
            foreach (var call in new[] { "WrapperSucceeded()", "transfer.IsCurrent", "pins.IsCurrent", "transfer.State" })
                Assert.Equal(1, Regex.Matches(body, Regex.Escape(call)).Count);
            Assert.DoesNotContain("Console.", body);
        }
        Assert.Equal(1, Regex.Matches(accept, Regex.Escape("owner.view.ReadByte(i)")).Count);
        Assert.Equal(1, Regex.Matches(accept, Regex.Escape("transfer.Matches(body)")).Count);
        Assert.Equal(1, Regex.Matches(accept, Regex.Escape("facts.MatchesCompletion(result.WriterContext)")).Count);
        Assert.Contains("if ((i < 48 || i >= 56) && owner.view.ReadByte(i) != scope[i]) { NativeAcceptDiagnosticForTest = \"scope-byte:\" + i; return false; }", accept);
        Assert.Contains("accepted = result; return true;", accept);
        Assert.Contains("error.Message; throw;", begin);
        Assert.Contains("error.Message; return false;", accept);
        Assert.Contains("if (closed) return; closed = true; NativeCloseDiagnosticForTest =", profile);
        var support = Regex.Replace(await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadParent.cs")), @"\s+", " ");
        Assert.Contains("private bool NativeRejectForTest(string stage, string reason, bool rejected) { if (rejected) { if (stage == \"begin\") NativeBeginDiagnosticForTest = reason; else NativeAcceptDiagnosticForTest = reason; } return rejected; }", support);
    }

    [Fact]
    public async Task RetirementTrace_PreservesOriginalCallbacksAndFirstClose()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tools/FileTransferOps/LocalGuiWriterRelayTransfer.cs"));
        var profile = Regex.Replace(Project(source, true), @"\s+", " ");
        Assert.Contains("if (!closed && !detached && (NativeExitRejectForTest(\"wrapper-exit\", !WrapperSucceeded()) || NativeExitRejectForTest(\"transfer-state\", transfer.State != 3) || NativeExitRejectForTest(\"transfer-current\", !transfer.IsCurrent))) Close();", profile);
        Assert.Contains("if (owner.NativePullRejectForTest(\"pre-pull-current\", !current()) || owner.NativePullRejectForTest(\"mark-pulled\", !ObserveNativePullForTest(transfer.MarkPulled())) || owner.NativePullRejectForTest(\"post-pull-budget\", transferWork.ElapsedMilliseconds > 5000)) return null; owner.NativePullDiagnosticForTest = \"pulled\"; success = true;", profile);
        Assert.Contains("if (frontierRelayCustody != null && !frontierRelayCustody.Detached) frontierRelayCustody.Close(\"retire:\" + Path.GetFileName(file) + \":\" + caller);", profile);
        Assert.Contains("internal void Close([System.Runtime.CompilerServices.CallerMemberName] string caller = \"\")", profile);
        var close = profile[profile.IndexOf("internal void Close([", StringComparison.Ordinal)..];
        Assert.True(close.IndexOf("if (closed) return; closed = true;", StringComparison.Ordinal) < close.IndexOf("NativeRetirementDiagnosticForTest =", StringComparison.Ordinal));
        Assert.Contains("NativeRetirementDiagnosticForTest = caller + \",wrapper-exit:\" + NativeExitDiagnosticForTest + \",pull:\" + owner.NativePullDiagnosticForTest; payloadLifetime.Cancel();", close);
        var forward = profile[profile.IndexOf("internal void Forwarded(", StringComparison.Ordinal)..profile.IndexOf("public sealed partial class FrontierRelayRegistration", StringComparison.Ordinal)];
        Assert.Contains("if (" + string.Join(" && ", new[]
        {
            "NativeForwardCheckForTest(\"closed\", !closed)",
            "NativeForwardCheckForTest(\"transfer-missing\", transfer != null)",
            "NativeForwardCheckForTest(\"pins-missing\", offerPins != null)",
            "NativeForwardCheckForTest(\"pins-current\", offerPins.IsCurrent)",
            "NativeForwardCheckForTest(\"channel-current\", channel.IsCurrent)",
            "NativeForwardCheckForTest(\"channel-attached\", channel.Attached)",
            "NativeForwardCheckForTest(\"owner-current\", owner.owner.IsCurrent)",
            "NativeForwardCheckForTest(\"mark-forwarded\", transfer.MarkForwarded(body, check))"
        }) + ") { NativeForwardDiagnosticForTest = \"forwarded\"; return; }", forward);
        Assert.Contains("if (transfer.State == 3 && transfer.IsCurrent) { NativeTransferCloseDiagnosticForTest = caller + \":release\"; transfer.Release(); } else { NativeTransferCloseDiagnosticForTest = caller + \":cancel\"; transfer.Dispose(); } transfer = null;", forward);
        foreach (var call in new[] { "transfer.MarkForwarded(", "offerPins.IsCurrent", "channel.IsCurrent", "channel.Attached", "owner.owner.IsCurrent", "transfer.State", "transfer.IsCurrent" })
            Assert.Equal(1, Regex.Matches(forward, Regex.Escape(call)).Count);
        Assert.DoesNotContain("Console.", profile);
        var support = Regex.Replace(await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadParent.cs")), @"\s+", " ");
        Assert.Contains("{ if (rejected) NativeExitDiagnosticForTest = reason; return rejected; }", support);
        Assert.Contains("{ if (rejected) NativePullDiagnosticForTest = reason; return rejected; }", support);
        Assert.Contains("{ if (!accepted) NativeForwardDiagnosticForTest = reason; return accepted; }", support);
        Assert.Contains("private bool ObserveNativePullForTest(bool pulled) { if (pulled && NativeAfterPullForTest != null && !NativeAfterPullForTest.WaitOne(2000)) throw new TimeoutException(\"native_pull_order_fixture_forward_timeout\"); return pulled; }", support);
    }

    private static string Project(string source, bool profile)
    {
        var output = new List<string>();
        bool? branch = null;
        foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            switch (line.Trim())
            {
                case "#if NLINK_TEST_ATTEMPT_DISPATCH":
                    Assert.Null(branch);
                    branch = profile;
                    break;
                case "#else":
                    Assert.NotNull(branch);
                    branch = !branch.Value;
                    break;
                case "#endif":
                    Assert.NotNull(branch);
                    branch = null;
                    break;
                default:
                    Assert.False(line.TrimStart().StartsWith('#'));
                    if (branch != false) output.Add(line);
                    break;
            }
        }
        Assert.Null(branch);
        return string.Join("\n", output);
    }
}
