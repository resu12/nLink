using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NLink.Tests;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
[Trait("RequiredConfiguration", "Debug")]
public sealed class FrontierInitialFallbackHandoffTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialFallback_SecondTunaRequestMustNotOutrunUnresolvedStall(bool preparedAfterDeadline)
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "tests/nLink.SmokeTests.Core/FrontierAttemptNknCarrierTests.cs");
        var original = await File.ReadAllTextAsync(path);
        var branch = Assert.Single(CSharpSyntaxTree.ParseText(original).GetRoot().DescendantNodes()
            .OfType<IfStatementSyntax>().Where(node => node.Condition.ToString() == "host" &&
                node.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Any(call => call.Expression.ToString() == "PreparedRetryCurrent")));
        var block = Assert.IsType<BlockSyntax>(branch.Statement);
        Assert.Equal(3, block.Statements.Count);
        Assert.Equal(["PeerReady", "WaitFor", "PreparedRetryCurrent", "RequestAccelerationNegotiationAsync"],
            block.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(call =>
                call.Expression is MemberAccessExpressionSyntax member ? member.Name.ToString() : call.Expression.ToString()));

        var receiver = Assert.IsType<BlockSyntax>(branch.Else!.Statement);
        Assert.Equal(2, receiver.Statements.Count);
        Assert.Equal(["WaitFor", "InitialFallbackRechecked", "Set"],
            receiver.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(call =>
                call.Expression is MemberAccessExpressionSyntax member ? member.Name.ToString() : call.Expression.ToString()));

        var receiverCheck = Assert.Single(CSharpSyntaxTree.ParseText(original).GetRoot().DescendantNodes()
            .OfType<LocalFunctionStatementSyntax>().Where(node => node.Identifier.Text == "InitialFallbackRechecked"));
        Assert.Single(receiverCheck.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression.ToString() == "AttemptInitialFallbackHandoff.Rechecked"));

        // Execute the unchanged scheduling branch, not its production authority
        // predicates. The probe assumes their observations are already current.
        // Framework-only stand-ins cannot issue route or runtime authority.
        var source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            public static class OriginalHostSchedule
            {
                public static async Task Run(Task peer, Task prepared, Action waiting, Action requested)
                {
                    object firstFallback = new object(), cycleFirstFallback = new object();
                    var ct = CancellationToken.None;
                    var transport = new SchedulingProbe(requested);
                    Task PeerReady(object signal)
                    {
                        if (!Object.ReferenceEquals(signal, cycleFirstFallback)) throw new Exception("foreign_peer_signal");
                        return peer;
                    }
                    async Task WaitFor(Func<bool> predicate)
                    {
                        waiting();
                        await prepared;
                        if (!predicate()) throw new Exception("foreign_prepared_scope");
                    }
                    bool PreparedRetryCurrent(object scope) => Object.ReferenceEquals(scope, firstFallback);
                    {{block.ToFullString()}}
                }
                private sealed class SchedulingProbe
                {
                    private readonly Action requested;
                    public SchedulingProbe(Action requested) { this.requested = requested; }
                    public Task RequestAccelerationNegotiationAsync(string reason, CancellationToken ct)
                    {
                        if (reason != "runtime_unlock" || ct.IsCancellationRequested) throw new Exception("changed_request");
                        requested();
                        return Task.CompletedTask;
                    }
                }
            }
            """;
        var framework = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetDirectoryName(p) == framework).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("InitialFallbackProbe" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var assembly = Assembly.Load(image.ToArray());
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name => name.Name!.StartsWith("NLink", StringComparison.OrdinalIgnoreCase));
        var run = assembly.GetType("OriginalHostSchedule")!.GetMethod("Run")!
            .CreateDelegate<Func<Task, Task, Action, Action, Task>>();

        var peer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const long deadline = 750 * TimeSpan.TicksPerMillisecond;
        long logicalTicks = 0, requestedAt = -1;
        var requests = 0;
        var pending = run(peer.Task, prepared.Task, () => waiting.TrySetResult(),
            () => { requestedAt = logicalTicks; Interlocked.Increment(ref requests); });
        Assert.Equal(0, requests);
        // Prepared retry readiness alone cannot release the receiver's signal,
        // whether it arrives just before or after the unchanged stall deadline.
        var preparedAt = preparedAfterDeadline ? deadline + 269499 : deadline - 1;
        logicalTicks = preparedAt;
        prepared.SetResult();
        Assert.False(waiting.Task.IsCompleted);
        Assert.Equal(0, requests);
        const string scope = "frontier_scope_session_id=fte1-111111111111111111111111; frontier_scope_transfer_id=fte1-222222222222222222222222; " +
            "frontier_scope_data_session_identity=fte1-333333333333333333333333; route=post_tuna_fallback_v6; protocol_version=6; " +
            "live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=0; " +
            "bridge_recovery_generation=0; bridge_decision_generation=0; checkpoint_request_id=(none); checkpoint_generation=0; " +
            "route_publication_generation=3; route_commit_generation=0; data_session_generation=1; frontier_chunk_index=3; " +
            "highest_received_chunk_index=2; wake_generation=1; stall_started_utc=2026-10-27T00:00:00.0000000+00:00; " +
            "wake_deadline_utc=2026-10-27T00:00:00.7500000+00:00; ";
        const string header = "event=filetransfer_v6_frontier_stall_grace_recheck_decided; authority_effect=none; ";
        const string stale = "decision_status=stale; decision_reason=committed_write_source_changed; ";
        const string progress = "write_progress_binding_version=1; write_progress_binding=exact_current_fallback_source; " +
            "write_progress_frontier=4; write_progress_highest=3; write_progress_utc=2026-10-27T00:00:00.5000000+00:00; " +
            "write_progress_recheck_utc=2026-10-27T00:00:00.8000000+00:00; ";
        var decision = header + scope + (preparedAfterDeadline ? stale + progress :
            "decision_status=dispatched; decision_reason=exact_receiver_request_dispatched; ");
        var rejected = 0;
        void Reject(string text, string captured = scope)
        {
            Assert.False(AttemptInitialFallbackHandoff.Rechecked(text, captured));
            Assert.False(peer.Task.IsCompleted);
            Assert.Equal(0, requests);
            rejected++;
        }
        Reject(decision.Replace("event=filetransfer_v6_frontier_stall_grace_recheck_decided", "event=unrelated", StringComparison.Ordinal));
        Reject(header + scope + stale); // A stale callback alone is never closure.
        Reject(header + scope + "decision_status=coalesced; decision_reason=exact_receiver_request_not_dispatched; ");
        Reject(decision.Replace("authority_effect=none", "authority_effect=route_commit", StringComparison.Ordinal));
        Reject(decision + "wake_generation=1; "); // Ambiguous diagnostic fields fail closed.
        foreach (var field in scope.Split("; ", StringSplitOptions.RemoveEmptyEntries))
        {
            Reject(decision.Replace(field + ";", field.Split('=')[0] + "=foreign;", StringComparison.Ordinal));
            Reject(decision, scope.Replace(field + "; ", "", StringComparison.Ordinal));
        }
        Reject(header + scope + stale + progress.Replace("write_progress_frontier=4", "write_progress_frontier=3", StringComparison.Ordinal));
        Reject(header + scope + stale + progress.Replace("write_progress_highest=3", "write_progress_highest=4", StringComparison.Ordinal));
        Reject(header + scope + stale + progress.Replace("write_progress_binding_version=1", "write_progress_binding_version=0", StringComparison.Ordinal));
        Reject(header + scope + stale + progress.Replace("exact_current_fallback_source", "unbound", StringComparison.Ordinal));
        Reject(header + scope + stale + progress.Replace("00.5000000", "00.0000000", StringComparison.Ordinal));
        Reject(header + scope + stale + progress.Replace("00.8000000", "00.7499999", StringComparison.Ordinal));
        const long closedAt = deadline + 500000;
        logicalTicks = closedAt;
        var receiverStallClosed = AttemptInitialFallbackHandoff.Rechecked(decision, scope);
        Assert.True(receiverStallClosed);
        peer.SetResult(); // Corresponds only to the guarded receiver fixture signal.
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, requests);
        Assert.Equal(closedAt, requestedAt);
        var directory = Path.Combine(root, "artifacts", "initial-handoff", preparedAfterDeadline ? "late" : "before");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "probe.cs"), source);
        await File.WriteAllTextAsync(Path.Combine(directory, "decision.txt"), decision);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            preparedAfterDeadline, deadlineTicks = deadline, preparedAtTicks = preparedAt, closedAtTicks = closedAt,
            requestedAtTicks = requestedAt, requests, receiverStallClosed, rejectedEvidenceControls = rejected,
            logicalClockOnly = true, authorityObjectsConstructed = 0,
            sourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))),
            branchSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(block.ToFullString()))),
            receiverBranchSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(receiver.ToFullString()))),
            predicateSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/AttemptInitialFallbackHandoff.cs")))),
            probeSha256 = Convert.ToHexString(SHA256.HashData(image.ToArray())),
            compilerVersion = typeof(CSharpCompilation).Assembly.GetName().Version!.ToString(),
            note = "Original sender branch and actual fixture evidence predicate with controlled task ordering; no real coordinator or authority is created."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(requestedAt < deadline || receiverStallClosed,
            "Initial fallback fixture requested second Tuna after the unchanged 750ms deadline with an unresolved receiver stall.");
    }
}
