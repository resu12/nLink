using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLink.App.Services;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;
using NLink.Tests;
using NLink.Tools;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class FrontierAttemptNknCarrierTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("pwsh", "valid")]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "request")]
    [InlineData("pwsh", "pipe")]
    public async Task OriginalApps_UsePeerBoundOfflineNknCarrier(string host, string fault)
        => await RunCarrierCaseAsync(host, fault, fullSize: false);

    [Theory]
    [InlineData("pwsh", "valid")]
    [InlineData("powershell", "valid")]
    [InlineData("pwsh", "source-retired")]
    [InlineData("pwsh", "gui-valid")]
    [InlineData("powershell", "gui-valid")]
    public async Task OriginalApps_TransferHeld256MiBAfterFallback(string host, string fault)
        => await RunCarrierCaseAsync(host, fault, fullSize: true);

    [Fact]
    public async Task OriginalApps_CharacterizeFrontierHoldOnSmallPair()
        => await RunCarrierCaseAsync("pwsh", "frontier-hold", fullSize: false);

    [Fact]
    [Trait("RequiredConfiguration", "Debug")]
    public async Task OriginalApps_CharacterizeSingleSegmentFrontierSchedule()
        => await RunCarrierCaseAsync("pwsh", "frontier-schedule", fullSize: false);

    [Fact]
    [Trait("RequiredConfiguration", "Debug")]
    public async Task OriginalApps_CarrySmallFrontierReceiptIntoPrimary()
        => await RunCarrierCaseAsync("pwsh", "frontier-schedule", fullSize: false, diagnosticCustody: true);

    [Fact]
    [Trait("RequiredConfiguration", "Debug")]
    public async Task OriginalApps_PreserveIneligibleLateFrontierReceiptInPrimary()
        => await RunCarrierCaseAsync("pwsh", "frontier-late-publication", fullSize: false, diagnosticCustody: true);

    [Fact]
    public async Task OriginalApps_CharacterizeFrontierHoldOnHeld256MiBPair()
        => await RunCarrierCaseAsync("pwsh", "frontier-hold", fullSize: true);

    [Theory]
    [InlineData("release")]
    [InlineData("../Release")]
    public async Task OriginalCarrierWorker_RejectsUnknownConfigurationBeforeLoading(string configuration)
    {
        if (!OperatingSystem.IsWindows()) return;
        var script = """
            param([string]$RepoRoot,[string]$Configuration)
            $ErrorActionPreference='Stop'
            $env:NLINK_TEST_FRONTIER_WORKER_CONFIGURATION=$Configuration
            & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptWritersApp.ps1') -RepoRoot $RepoRoot
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), configuration], "pwsh");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unsupported frontier worker configuration", result.Stderr);
        Assert.DoesNotContain("frontier-worker-configuration=", result.Stdout);
    }

    private async Task RunCarrierCaseAsync(string host, string fault, bool fullSize, bool diagnosticCustody = false)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", fault == "frontier-late-publication" ? "frontier-late" :
            diagnosticCustody ? "frontier-custody" :
            fault == "frontier-schedule" ? "broader-offline-fault-implementation-20260928" :
            fault == "frontier-hold" ? "frame-hold-implementation-20260928" :
            fullSize ? "payload-native-fallback-20260927" : "payload-cross-process-carrier-20260927",
            host + "-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var scenario = "writer-source-read-cross-process-carrier-" + (fullSize ? "full-" : "") + fault;
        var script = await File.ReadAllTextAsync(Path.Combine(root, "tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1"));
        var configuration = typeof(FrontierAttemptNknCarrierTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        Assert.Contains(configuration, new[] { "Debug", "Release" });
        var ineligibleLate = diagnosticCustody && fault == "frontier-late-publication";
        var arguments = new[] { root, directory, scenario, "", "", "", diagnosticCustody ? fault : "", configuration };
        if (ineligibleLate) arguments = [.. arguments, AttemptRouteCycleProfile.Ineligible];
        var result = await RunPowerShellScriptTextAsync(script, arguments, host);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), result.Stdout + Environment.NewLine + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("frontier-attempt-dispatch=" + scenario, result.Stdout);
        var gui = await File.ReadAllTextAsync(Path.Combine(directory, "gui-smoke-stdout.log"));
        // The legacy one-shot hold proves transfer integrity, not positive
        // canonical custody; both sizes must retain their exact rejection.
        var negativeCustodyHold = fault == "frontier-hold";
        if (negativeCustodyHold)
        {
            Assert.Contains("attempt-frontier-custody-unavailable-ok", gui);
            Assert.DoesNotContain("attempt-role-writers-ok", gui);
            Assert.DoesNotContain("attempt-source-custody-ok", gui);
        }
        else
        {
            Assert.Contains("attempt-role-writers-ok", gui); Assert.Contains("attempt-source-custody-ok", gui);
        }
        if (diagnosticCustody)
        {
            Assert.Contains("attempt-diagnostic-native-writer-handoff-ok", gui);
            Assert.Contains("attempt-diagnostic-stopped-before-route-acceptance", result.Stdout);
            var analysis = await File.ReadAllTextAsync(Path.Combine(directory, "filetransfer-retained-analysis.stdout.txt"));
            Assert.Contains(fault == "frontier-late-publication" ? "attempt-late-publication-primary-checked" : "attempt-diagnostic-primary-checked", analysis);
            Assert.Contains("original parent analyzer evaluations=3 nativeReads=1", analysis);
            if (ineligibleLate)
            {
                Assert.Contains("attempt-late-publication-ineligible-checked", analysis);
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "late-publication-primary-observation.json")));
                var observation = json.RootElement;
                Assert.Equal("PublishedWithinObservationBound", observation.GetProperty("Outcome").GetString());
                Assert.Equal("filetransfer_tuna_activation_delivery_unproven", observation.GetProperty("SelectedFailure").GetString());
                Assert.Equal("INCONCLUSIVE", observation.GetProperty("PrimaryVerdict").GetString());
                Assert.Equal("fail", observation.GetProperty("GateStatus").GetString());
                Assert.Equal(0, observation.GetProperty("ResolverCalls").GetInt32());
                Assert.Equal(0, observation.GetProperty("WitnessCount").GetInt32());
                Assert.False(observation.GetProperty("OriginalOutputCommitted").GetBoolean());
                Assert.Equal(0, observation.GetProperty("OpenPinsAfterCleanup").GetInt32());
                Assert.True(observation.GetProperty("TextCleared").GetBoolean());
                foreach (var flag in new[] { "LiveProofEligible", "NativeWriterCustodyEstablished", "PublicationCompletionIssued" })
                    Assert.False(observation.GetProperty(flag).GetBoolean());
                var reportsBefore = observation.GetProperty("ReportsBefore").GetString();
                Assert.False(string.IsNullOrWhiteSpace(reportsBefore));
                Assert.Equal(reportsBefore, observation.GetProperty("ReportsAfter").GetString());
            }
            var received = Assert.Single(Directory.GetFiles(Path.Combine(directory, "native-carrier-received"), "*.bin", SearchOption.AllDirectories));
            using var released = File.Open(received, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(193536, released.Length);
            Assert.Equal("50cb517145d636f8965eadb455970cf61cd6ad2f72881bf132cbaf1e5d6890c8",
                Convert.ToHexString(SHA256.HashData(released)).ToLowerInvariant());
        }
        if (fault == "gui-valid")
        {
            Assert.Contains("attempt-destination-gui-adoption-ok", gui);
            Assert.Contains("attempt-destination-closed-writers-ok", gui);
            foreach (var path in new[] { Path.Combine(directory, "filetransfer-tuna-gui-payload.bin"),
                Assert.Single(Directory.GetFiles(Path.Combine(directory, "native-carrier-received"), "*.bin", SearchOption.AllDirectories)) })
            {
                using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.Equal(268435456, released.Length);
            }
        }
        string? pair = null;
        var coreHash = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(typeof(SessionFileTransferService).Assembly.Location)));
        var testsHash = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(typeof(FrontierAttemptNknCarrierTests).Assembly.Location)));
        foreach (var role in new[] { "helpee", "helper" })
        {
            var path = Assert.Single(Directory.GetFiles(Path.Combine(directory, "process-output"), role + "-*.stdout.log"));
            var text = await File.ReadAllTextAsync(path);
            Assert.Equal($"frontier-worker-configuration={configuration}; core_sha256={coreHash}; tests_sha256={testsHash}",
                Assert.Single(text.Split('\n').Where(line =>
                    line.StartsWith("frontier-worker-configuration=", StringComparison.Ordinal))).Trim());
            if (negativeCustodyHold)
            {
                Assert.Contains("carrier-frontier-custody=unavailable; role=" + role + "; reason=" +
                    (role == "helper" ? "producer_not_closed_positive" : "native_request_revoked"), text);
                if (role == "helper")
                    Assert.Contains("; status=" + (fullSize ? "EvidenceIncomplete" : "ObservationInvalidated") + ";", text);
            }
            if (role == "helper" && fault is "frontier-schedule" or "frontier-late-publication")
            {
                var policy = fault == "frontier-late-publication" ? "late-publication-fixed-4s-v1; max_hold_ms=4000" : "frontier-fixed-3s-v1; max_hold_ms=3000";
                Assert.Equal("carrier-frontier-hold-policy=" + policy,
                    Assert.Single(text.Split('\n').Where(line => line.StartsWith("carrier-frontier-hold-policy=", StringComparison.Ordinal))).Trim());
            }
            Assert.Contains(fault is "valid" or "gui-valid" or "frontier-hold" or "frontier-schedule" or "frontier-late-publication" ? "cross-process-nkn-" + role + "=" + (fullSize ? 268435456 : fault is "frontier-schedule" or "frontier-late-publication" ? 193536 : 65536) + "; route=post_tuna_fallback_v6" :
                fullSize ? "cross-process-nkn-source-rejected=" + role : "cross-process-peer-rejected=" + role, text);
            if (fault is "valid" or "gui-valid" or "frontier-hold" or "frontier-schedule" or "frontier-late-publication")
            {
                var line = Assert.Single(text.Split('\n').Where(line => line.StartsWith("cross-process-nkn-", StringComparison.Ordinal)));
                var identity = line[line.IndexOf("; sha256=", StringComparison.Ordinal)..].Trim();
                if (fullSize)
                {
                    var measurement = await File.ReadAllLinesAsync(Path.Combine(directory, "source-measurement.txt"));
                    Assert.Equal("268435456", measurement[0]);
                    Assert.Contains("; sha256=" + measurement[1] + ";", identity);
                }
                if (pair is not null) Assert.Equal(pair, identity);
                pair = identity;
            }
            if (fault == "frontier-hold")
                Assert.Contains(role == "helpee" ? "carrier-frame-hold=released;" :
                    "carrier-frontier-edge=observed;", text);
            if (fault == "frontier-schedule")
                Assert.Contains(role == "helpee" ? "carrier-frontier-schedule=sent-eight;" :
                    "carrier-frontier-schedule=closed-positive;", text);
            if (fault == "frontier-late-publication")
                Assert.Contains(role == "helpee" ? "carrier-frontier-schedule=sent-eight;" :
                    "carrier-frontier-late-publication=closed-within-bound;", text);
            if (fault is "frontier-schedule" or "frontier-late-publication")
            {
                Assert.Contains(role == "helpee" ? "carrier-frontier-final=dropped-once; chunk=8;" :
                    "carrier-frontier-window=committed-nonterminal; chunks=8; total_chunks=9;", text);
                if (role == "helpee") Assert.Contains("carrier-frontier-final=repair-described; chunk=8;", text);
            }
        }
    }
}

public sealed partial class NknAccelerationTransportTests
{
    internal static TimeSpan GetCarrierBudget(bool fullSize, bool routeCycle) =>
        TimeSpan.FromSeconds(routeCycle ? 240 : fullSize ? 180 : 70);

    internal static async Task RunCrossProcessCarrierAsync(LocalFrontierAppRequest.AppLease lease,
        AttemptPeerProcessChannel peer, string directory, string role, string signal,
        AttemptSenderSourceChannel? source = null, string? nativeCase = null, bool frontierHold = false, bool frontierSchedule = false,
        bool latePublication = false)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var canonicalTail = nativeCase == AttemptCanonicalTailProfile.Name;
        var routeCycle = AttemptRouteCycleProfile.Enabled;
        if (routeCycle && !canonicalTail) throw new InvalidOperationException("route_cycle_requires_canonical_tail");
        if (canonicalTail && (frontierHold || frontierSchedule || latePublication))
            throw new InvalidOperationException("canonical_tail_requires_separate_profile");
        if (frontierSchedule && (nativeCase is not null || frontierHold)) throw new InvalidOperationException("offline_schedule_small_only");
        if (latePublication && !frontierSchedule) throw new InvalidOperationException("late_publication_requires_small_schedule");
        if (canonicalTail) AttemptCanonicalTailProfile.RequireSupported();
        var lifetime = Assert.IsType<SessionFileTransferService.FrontierAppLifetime>(SessionFileTransferService.FrontierAppLifetime.Current);
        var host = role == "helpee";
        using var packetization = frontierSchedule ? new AttemptSingleSegmentProfile() : null;
        // Declared before the runtime/client so the exact prior setting is
        // restored only after their cleanup, including on an unavailable tail.
        using var tailPacketization = canonicalTail && host ? new AttemptCanonicalTailProfile(
            AttemptCanonicalTailProfile.FileBytes, AttemptCanonicalTailProfile.ChunkBytes, AttemptCanonicalTailProfile.ChunkCount) : null;
        // Only the reviewed route-cycle gets tail headroom after its full-size
        // prefix. Other fixtures and local route-edge limits keep their bounds.
        var carrierBudget = GetCarrierBudget(nativeCase is not null, routeCycle);
        var carrierStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(carrierBudget);
        var ct = deadline.Token;
        var fullSize = nativeCase is not null;
        var rejectSource = nativeCase == "source-retired";
        if (fullSize) Assert.Contains(nativeCase, new[] { "valid", "source-retired", "gui-valid", "frontier-hold", AttemptCanonicalTailProfile.Name });
        var nativeReads = fullSize && host ? new RetainedRuntimeReadProbe(Assert.IsType<AttemptSenderSourceChannel>(source), lease,
            rejectSource ? "writer-source-read-runtime-disposed" : "writer-source-read-runtime-valid",
            measureReads: frontierHold || routeCycle, routeCycle: routeCycle) : null;
        await using var client = await AttemptNknPipeClient.OpenAsync(lease, peer, host, ct);
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync("foreign.address", [1], ct));
        await Assert.ThrowsAsync<IOException>(() => client.SendAsync(client.PeerAddress, [1], ct)); // Not connected yet.
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("offline-carrier-" + role, client.Address);
        using var pressure = new RealNknClientAdapter(identity, options); // Never connected.
        var lane = new RetryableTunaAccelerationSession(host, 0, supportedLanes: NknAccelerationLaneKind.File);
        lane.ForwardBytesForTests = client.SendTunaAsync;
        client.TunaReceived = (channel, bytes) =>
        {
            // The physical carrier cannot activate a lane or install a route.
            if (lane.IsAvailable) lane.ReceivePeerBytesForTests(channel, bytes);
        };
        using var transport = new NknSignalingTransport(client, options, identity, NknTunaAccelerationOptions.Disabled, lane, pressure);
        EventHandler<NLink.Core.TransportSessionSecurityStateChangedEventArgs> suppress = (_, _) => SetPrivateField(transport, "accelerationNegotiationScheduled", 1);
        transport.SessionSecurityStateChanged += suppress;
        SetPrivateField(transport, "accelerationNegotiationScheduled", 1);
        using var runtime = new SessionRuntime(() => transport);
        var service = (SessionFileTransferService)typeof(SessionRuntime).GetField("fileTransferService", flags)!.GetValue(runtime)!;
        using var hostReady = EventWaitHandle.OpenExisting(signal + ".cross-host-ready");
        using var verified = EventWaitHandle.OpenExisting(signal + ".destination-verified");
        using var peerRegular = new EventWaitHandle(false, EventResetMode.ManualReset, signal + ".carrier-peer-regular");
        using var peerTuna = new EventWaitHandle(false, EventResetMode.ManualReset, signal + ".carrier-peer-tuna");
        using var peerFallback = new EventWaitHandle(false, EventResetMode.ManualReset, signal + ".carrier-peer-fallback");
        using var cycleFirstDelivered = routeCycle ? new EventWaitHandle(false, EventResetMode.ManualReset, signal + ".cycle-first-delivered") : null;
        using var cycleFirstFallback = routeCycle ? new EventWaitHandle(false, EventResetMode.ManualReset, signal + ".cycle-first-fallback") : null;
        using var cycleSecondDelivered = routeCycle ? new EventWaitHandle(false, EventResetMode.ManualReset, signal + ".cycle-second-delivered") : null;
        using var cycleFirstPublished = routeCycle ? new EventWaitHandle(false, EventResetMode.ManualReset, signal + ".cycle-first-published") : null;
        using var cycleSecondPublished = routeCycle ? new EventWaitHandle(false, EventResetMode.ManualReset, signal + ".cycle-second-published") : null;
        using var sourceBound = new EventWaitHandle(false, EventResetMode.ManualReset, signal + ".carrier-source-bound");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTunaReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTunaReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bytes = fullSize ? null : Enumerable.Range(0, frontierSchedule ? 193536 : 65536).Select(i => (byte)(i % 251)).ToArray();
        var payloadBytes = fullSize ? 268435456 : bytes!.Length;
        byte[]? hash = fullSize ? host ? Convert.FromHexString(source!.Sha256) : null : SHA256.HashData(bytes!);
        var transferId = "native-carrier-" + (host ? lease.RequestId : peer.PeerRequest).ToString("N");
        var priorRoot = Environment.GetEnvironmentVariable("NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT");
        var finalRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionFileTransferService.NativeDestinationEvidence? evidence = null;
        var prefixTiming = routeCycle ? new AttemptPrefixTiming() : null;
        try
        {
            if (host)
            {
                using var invitePipe = new NamedPipeServerStream("nlink-carrier-invite-" + lease.RequestId.ToString("N"),
                    PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _ = CreateValidatedInviteForTarget(new PeerAddress(client.Address), out var token,
                    InviteCapabilities.Chat | InviteCapabilities.FileTransfer, boundHelperAddress: new PeerAddress(client.PeerAddress));
                await runtime.StartHelpeeAsync(ct); hostReady.Set();
                await invitePipe.WaitForConnectionAsync(ct); Assert.True(peer.MatchesPipe(invitePipe.SafePipeHandle, true));
                var encoded = Encoding.UTF8.GetBytes(token);
                await invitePipe.WriteAsync(BitConverter.GetBytes(encoded.Length), ct); await invitePipe.WriteAsync(encoded, ct);
                await invitePipe.FlushAsync(ct);
                await WaitFor(() => runtime.PendingApprovalRequest is not null);
                await runtime.ApproveAsync(CapabilityGrant.Chat | CapabilityGrant.FileTransfer, ct);
            }
            else
            {
                await PeerReady(hostReady);
                using var invitePipe = new NamedPipeClientStream(".", "nlink-carrier-invite-" + peer.PeerRequest.ToString("N"),
                    PipeDirection.In, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await invitePipe.ConnectAsync(ct); Assert.True(peer.MatchesPipe(invitePipe.SafePipeHandle, false));
                var prefix = new byte[4]; await invitePipe.ReadExactlyAsync(prefix, ct);
                var count = BitConverter.ToInt32(prefix); Assert.InRange(count, 1, 16384);
                var encoded = new byte[count]; await invitePipe.ReadExactlyAsync(encoded, ct);
                var invite = InviteTokenServiceFactory.CreateDefaultResolver().Resolve(Encoding.UTF8.GetString(encoded), DateTimeOffset.UtcNow);
                Assert.True(invite.IsValid); Assert.NotNull(invite.Invite);
                Assert.Equal(new PeerAddress(client.PeerAddress), invite.Invite.TargetAddress);
                Assert.Equal(new PeerAddress(client.Address), invite.Invite.BoundHelperAddress);
                await runtime.StartHelperAsync(invite.InviteTokenText!, invite.Invite, ct);
            }
            await WaitFor(() => runtime.State == SessionRuntimeState.Connected);
            transport.SessionSecurityStateChanged -= suppress;
            SetPrivateField(transport, "accelerationNegotiationScheduled", 0);
            Assert.True(((IFileTransferRouteTransitionBridge)transport).SupportsFallbackBootstrapCorrelation);
            object context;
            var opens = 0;
            FileTransferReceiveDestination? destination = null;
            FileTransferWriteHandle? handle = null;
            var renamed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finalizing = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (host)
            {
                await runtime.StartSendAsync(new(fullSize ? "filetransfer-tuna-gui-payload.bin" : "offline-carrier.bin", payloadBytes, transferId), _ =>
                {
                    client.CheckReader();
                    Assert.InRange(++opens, 1, routeCycle ? 6 : 4);
                    var readRelease = routeCycle && opens == 3 ? firstTunaReads.Task :
                        routeCycle && opens == 5 ? secondTunaReads.Task : release.Task;
                    return Task.FromResult<Stream>(nativeReads is not null ? nativeReads.Open(readRelease, tailPacketization) :
                        opens == 1 ? new MemoryStream(bytes!, false) : new JoinedBootstrapReadGate(bytes!, release.Task));
                }, ct);
                context = GetPrivateField(service, "outboundTransfer")!;
                // The peer waits before accepting, so metadata cannot race this
                // binding to the original app-owned outbound context.
                nativeReads?.Bind(service);
                sourceBound.Set();
            }
            else
            {
                await WaitFor(() => service.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
                Assert.Equal(transferId, service.Snapshot.Inbound!.TransferId);
                Assert.Equal(payloadBytes, service.Snapshot.Inbound.FileSizeBytes);
                context = GetPrivateField(service, "inboundTransfer")!;
                Environment.SetEnvironmentVariable("NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT", Path.Combine(directory, "native-carrier-received"));
                service.InboundFinalizationStartedForTests = task => finalizing.TrySetResult(task);
                if (fullSize) await PeerReady(sourceBound);
                await service.AcceptIncomingTransferAsync(transferId, async (offer, token) =>
                {
                    client.CheckReader();
                    destination = await runtime.OpenInboundFileTransferDestinationAsync(offer, token);
                    handle = (FileTransferWriteHandle)destination.GetType().GetField("guardHandle", flags)!.GetValue(destination)!;
                    handle.WindowsAfterRenameForTests = () => { renamed.TrySetResult(); return finalRelease.Task; };
                    return destination;
                }, ct);
            }
            FileTransferCoordinatorState State() => SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
            async Task ReleaseTunaReads(EventWaitHandle peerPublished, TaskCompletionSource readsReady)
            {
                Assert.True(Published(FileTransferRoute.FileTunaV4));
                if (host)
                {
                    await PeerReady(peerPublished);
                    Assert.True(Published(FileTransferRoute.FileTunaV4));
                    readsReady.SetResult(); // Scheduling only; the original runtime read rechecks its retained owner/publication.
                }
                else peerPublished.Set();
            }
            bool PreparedRetryCurrent(RoutePublicationToken fallback)
            {
                var state = State();
                Assert.Equal(fallback, state.CurrentRoutePublicationToken);
                Assert.True(Published(FileTransferRoute.PostTunaFallbackV6));
                var sessionId = service.Snapshot.Outbound!.SessionId;
                var boundary = InvokePrivateMethod(transport, "CaptureRuntimeUnlockPostFallbackSchedulingBoundary",
                    sessionId)!;
                object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
                if (Property(boundary, "Kind").ToString() != "AwaitingRetryAuthority") return false;
                var scope = Property(boundary, "SourceScope");
                lock (GetPrivateField(transport, "accelerationGate")!)
                {
                    var intent = GetPrivateField(transport, "pendingRuntimeUnlockRetryNegotiationIntent");
                    if (intent is null || Property(intent, "Kind").ToString() != "CoordinatorPreparedRequest" ||
                        !(bool)Property(intent, "BackoffSatisfied") ||
                        !(bool)typeof(NknSignalingTransport).GetMethod("RuntimeUnlockRetryNegotiationSourceScopeMatchesBoundary",
                            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Property(intent, "SourceScope"), scope])!) return false;
                    Assert.Same(GetPrivateField(transport, "runtimeUnlockRecoveryRetryState"), Property(intent, "State"));
                    var request = Assert.IsType<RuntimeUnlockRetryDispatchAuthorityRequest>(
                        Property(Property(intent, "QueuedRequest"), "Request"));
                    Assert.Equal(sessionId, request.SessionId);
                    Assert.Equal(transferId, request.TransferId);
                    Assert.Equal(FileTransferDirection.Outbound, request.Direction);
                    Assert.Equal(Property(intent, "SourcePublicationAuthority"), request.SourcePublicationAuthority);
                    return true; // Observation only. The original negotiation path rechecks the exact current intent.
                }
            }
            object? initialFallbackWake = null;
            bool InitialFallbackRechecked(RoutePublicationToken fallback)
            {
                lock (GetPrivateField(service, "gate")!)
                {
                    Assert.Same(context, GetPrivateField(service, "inboundTransfer"));
                    Assert.True(Published(FileTransferRoute.PostTunaFallbackV6));
                    Assert.Equal(fallback, State().CurrentRoutePublicationToken);
                    object Property(string name) => context.GetType().GetProperty(name)!.GetValue(context)!;
                    Assert.False((bool)Property("IsTerminal") || (bool)Property("UserPaused") ||
                        (bool)Property("PeerPaused") || (bool)Property("PullTransportPaused"));
                    initialFallbackWake ??= Property("CurrentV6FrontierStallGraceWake");
                    if (initialFallbackWake is null) return false;
                    object Wake(string name) => initialFallbackWake.GetType().GetProperty(name)!.GetValue(initialFallbackWake)!;
                    Assert.Equal(fallback, Assert.IsType<RoutePublicationToken>(Wake("RoutePublication")));
                    Assert.Equal(Property("CurrentRuntimeOwner"), Wake("RuntimeOwner"));
                    Assert.Same(Property("DataSession"), Wake("DataSession"));
                    Assert.Equal(Property("DataSessionGeneration"), Wake("DataSessionGeneration"));
                    Assert.Equal(Property("SessionId"), Wake("SessionId"));
                    Assert.Equal(transferId, Wake("TransferId"));
                    var scope = FileTransferDiagnosticScopeEvidence.Format((string)Wake("SessionId"), transferId) +
                        FileTransferDiagnosticScopeEvidence.FormatDataSession((string)Wake("DataSessionIdentity")) +
                        "route=post_tuna_fallback_v6; protocol_version=6; ";
                    foreach (var (field, property) in new[] {
                        ("live_route_epoch", "LiveRouteEpoch"), ("runtime_generation", "RuntimeGeneration"),
                        ("terminal_generation", "TerminalGeneration"), ("leg_generation", "LegGeneration"),
                        ("transport_epoch", "TransportEpoch"), ("bridge_recovery_generation", "BridgeRecoveryGeneration"),
                        ("bridge_decision_generation", "BridgeDecisionGeneration"), ("checkpoint_request_id", "CheckpointRequestId"),
                        ("checkpoint_generation", "CheckpointGeneration"), ("route_publication_generation", "RoutePublicationGeneration"),
                        ("route_commit_generation", "RouteCommitGeneration"), ("data_session_generation", "DataSessionGeneration"),
                        ("frontier_chunk_index", "FrontierChunkIndex"), ("highest_received_chunk_index", "HighestReceivedChunkIndex"),
                        ("wake_generation", "WakeGeneration"), ("stall_started_utc", "StallStartedUtc"), ("wake_deadline_utc", "DueUtc") })
                    {
                        var value = Wake(property);
                        scope += field + "=" + (value is DateTimeOffset utc ? utc.ToString("O") :
                            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)) + "; ";
                    }
                    return AttemptInitialFallbackHandoff.Rechecked(LocalOperationalLog.GetRecentLogText(), scope);
                }
            }
            async Task DeliveredPrefix(int chunks, EventWaitHandle delivered)
            {
                if (host) await PeerReady(delivered);
                else
                {
                    await WaitFor(() =>
                    {
                        lock (GetPrivateField(service, "gate")!)
                        {
                            Assert.True(Published(FileTransferRoute.FileTunaV4));
                            var next = (int)context.GetType().GetProperty("NextChunkIndex")!.GetValue(context)!;
                            Assert.InRange(next, 0, chunks);
                            return next == chunks;
                        }
                    });
                    Assert.True(Published(FileTransferRoute.FileTunaV4));
                    delivered.Set(); // Scheduling evidence only; no route or runtime state is written.
                    return;
                }
                Assert.True(Published(FileTransferRoute.FileTunaV4));
            }
            bool Published(FileTransferRoute route) => State() is { CurrentRoutePublicationToken: { } publication } state &&
                publication.Route == route && FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, publication, out _) &&
                (route == FileTransferRoute.RegularNknV4Fast || state.PersistentState.FileTransferRouteTransition.LastTerminalOperation is
                    { DispositionReceipt.Status: FileTransferRouteTransitionDispositionApplyStatus.Applied });
            await WaitFor(() => Published(FileTransferRoute.RegularNknV4Fast) &&
                State().PersistentState.FileTransferRouteTransition.BootstrapAgreement?.IsEnabled == true &&
                (host ? Volatile.Read(ref opens) >= 2 :
                (bool)context.GetType().GetProperty("PullManifestReceived")!.GetValue(context)!));
            Assert.Equal(4, State().CurrentRoutePublicationToken!.Value.ProtocolVersion);
            Assert.True(State().PersistentState.FileTransferRouteTransition.BootstrapAgreement!.IsEnabled);
            if (fullSize && !host) hash = Convert.FromBase64String(service.Snapshot.Inbound!.Sha256Base64!);
            if (canonicalTail)
            {
                RequireCanonicalGeometry();
                Assert.Equal("3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699",
                    Convert.ToHexString(hash!).ToLowerInvariant());
            }
            if (host) { await PeerReady(peerRegular); await transport.RequestAccelerationNegotiationAsync("runtime_unlock", ct); }
            else peerRegular.Set();
            await WaitFor(() => Published(FileTransferRoute.FileTunaV4));
            Assert.Equal(4, State().CurrentRoutePublicationToken!.Value.ProtocolVersion);
            if (routeCycle)
            {
                await ReleaseTunaReads(cycleFirstPublished!, firstTunaReads);
                await DeliveredPrefix(3, cycleFirstDelivered!);
            }
            if (host) { await PeerReady(peerTuna); await transport.StopAccelerationAsync("runtime_disabled", ct); }
            else peerTuna.Set();
            if (rejectSource)
            {
                await WaitFor(() => (host ? service.Snapshot.Outbound : service.Snapshot.Inbound)?.State == FileTransferTransferState.Failed);
                var failed = host ? service.Snapshot.Outbound! : service.Snapshot.Inbound!;
                Assert.Equal("read_failed", failed.ErrorCode);
                Assert.Equal(0, failed.BytesTransferred); Assert.Null(failed.SavedFilePath);
                Assert.False(renamed.Task.IsCompleted);
                if (host) { nativeReads!.VerifyRetired(); await PeerReady(verified); }
                else verified.Set();
                SessionFileTransferV4SenderTests.CancelJoinedNknContextForTest(context);
                Console.WriteLine("cross-process-nkn-source-rejected=" + role);
                return;
            }
            await WaitFor(() => Published(FileTransferRoute.PostTunaFallbackV6));
            if (routeCycle)
            {
                var firstFallback = State().CurrentRoutePublicationToken!.Value;
                if (host)
                {
                    await PeerReady(cycleFirstFallback!);
                    await WaitFor(() => PreparedRetryCurrent(firstFallback));
                    await transport.RequestAccelerationNegotiationAsync("runtime_unlock", ct);
                }
                else
                {
                    await WaitFor(() => InitialFallbackRechecked(firstFallback));
                    cycleFirstFallback!.Set(); // Scheduling only; sender authority is still rechecked by the original path.
                }
                await WaitFor(() => Published(FileTransferRoute.FileTunaV4) &&
                    State().CurrentRoutePublicationToken!.Value.LiveRouteEpoch == firstFallback.LiveRouteEpoch + 1);
                await ReleaseTunaReads(cycleSecondPublished!, secondTunaReads);
                await DeliveredPrefix(6, cycleSecondDelivered!);
                var secondTuna = State().CurrentRoutePublicationToken!.Value;
                if (host) await transport.StopAccelerationAsync("runtime_disabled", ct);
                await WaitFor(() => Published(FileTransferRoute.PostTunaFallbackV6) &&
                    State().CurrentRoutePublicationToken!.Value.LiveRouteEpoch == secondTuna.LiveRouteEpoch + 1);
                Console.WriteLine("carrier-route-cycle=two-tuna-prefixes; role=" + role + "; chunks=3,6; session=" +
                    (host ? service.Snapshot.Outbound : service.Snapshot.Inbound)!.SessionId + "; transfer=" + transferId);
            }
            Assert.Equal(6, State().CurrentRoutePublicationToken!.Value.ProtocolVersion);
            var transition = State().PersistentState.FileTransferRouteTransition;
            Assert.True(transition.BootstrapExchange!.Completed);
            Assert.True(transition.BootstrapExchange.First.IsReady);
            if (transition.BootstrapExchange.Final is { } final) Assert.True(final.IsReady);
            Assert.NotNull(transition.BootstrapSourceHold?.Receipt);
            Assert.Equal(State().CurrentRoutePublicationToken, transition.LastTerminalOperation!.Value.RoutePublicationToken);
            if (canonicalTail) RequireCanonicalGeometry();
            var prefixScope = canonicalTail ? new AttemptCanonicalPrefixScope(service, context, host, () =>
            {
                if (!lease.IsCurrent || !peer.IsCurrent || !client.IsCurrent) throw new IOException("canonical_prefix_peer_retired");
            }, prefixTiming) : null;
            using var prefixLifetime = prefixScope is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, prefixScope.ContextToken);
            await using var prefixHandoff = prefixScope is null ? null : await AttemptCanonicalPrefixHandoff.OpenAsync(
                lease, peer, client, prefixScope, host, prefixLifetime!.Token);
            if (canonicalTail && host) tailPacketization!.BindPrefix(prefixHandoff!.Received, prefixHandoff.Check);
            if (host)
            {
                await PeerReady(peerFallback);
                if (frontierHold)
                {
                    client.OutboundHold = new AttemptNknFrameHold();
                    await Assert.ThrowsAsync<IOException>(() => client.SendBulkAsync("foreign.address", new byte[4096], ct));
                    Assert.False(client.OutboundHold.Selected.IsCompleted);
                }
                if (frontierSchedule || canonicalTail)
                {
                    client.OutboundDescriptors = new(client.Address, service.Snapshot.Outbound!.SessionId, transferId, tailPacketization,
                        nonterminalSmallWindow: frontierSchedule);
                    transport.OfflineFrameObserverForTests = client.OutboundDescriptors.Observe;
                }
                release.TrySetResult();
                if (client.OutboundHold is { } hold)
                {
                    var selected = await hold.Selected.WaitAsync(TimeSpan.FromSeconds(8), ct);
                    await Task.Delay(2000, ct);
                    Assert.True(hold.Release()); Assert.False(hold.Release());
                    Console.WriteLine("carrier-frame-hold=released; bytes=" + selected.ByteCount + "; sha256=" + selected.Sha256);
                }
                if (fullSize || frontierHold)
                {
                    // Held payload recovery can include the existing 15-second
                    // stopped-retry grace. Use the existing overall fixture
                    // deadline, not the eight-second route-edge deadline.
                    while (service.Snapshot.Outbound?.IsTerminal != true) { client.CheckReader(); await Task.Delay(10, ct); }
                }
                else await WaitFor(() => service.Snapshot.Outbound?.IsTerminal == true);
                Assert.Equal(routeCycle ? 6 : 4, opens); await PeerReady(verified);
                if (frontierSchedule || canonicalTail)
                {
                    client.OutboundDescriptors!.VerifyComplete();
                    Console.WriteLine("carrier-frontier-schedule=sent-eight; session=" + service.Snapshot.Outbound!.SessionId + "; transfer=" + transferId);
                }
                nativeReads?.VerifyRetired();
            }
            else
            {
                if (frontierSchedule || canonicalTail)
                    client.InboundSchedule = new(client.PeerAddress, service.Snapshot.Inbound!.SessionId, transferId, ct,
                        latePublication || canonicalTail, canonicalTail);
                peerFallback.Set();
                if (latePublication || canonicalTail) await AttemptOfflineLatePublicationProgress.RunAsync(service, context, client, transport, lease, ct, carrierStarted, carrierBudget,
                    canonicalTail, prefixScope, prefixHandoff, prefixTiming);
                else if (frontierSchedule) await AttemptOfflineFrontierProgress.RunAsync(service, context, client, ct);
                if (frontierSchedule)
                {
                    // The final ordinary frame was dropped once. Observe the closed
                    // nonterminal window without granting or scheduling its tail repair.
                    await WaitFor(() => service.Snapshot.Inbound?.BytesTransferred == 172032);
                    lock (GetPrivateField(service, "gate")!)
                    {
                        Assert.Same(context, GetPrivateField(service, "inboundTransfer"));
                        Assert.Equal(transferId, service.Snapshot.Inbound!.TransferId);
                        Assert.False(service.Snapshot.Inbound.IsTerminal);
                        Assert.Equal(9, (int)context.GetType().GetProperty("ChunkCount")!.GetValue(context)!);
                        Assert.Equal(21504, (int)context.GetType().GetProperty("ChunkSizeBytes")!.GetValue(context)!);
                        Assert.Equal(8, (int)context.GetType().GetProperty("NextChunkIndex")!.GetValue(context)!);
                        Assert.Equal(7, (int)context.GetType().GetProperty("PullHighestReceivedChunkIndex")!.GetValue(context)!);
                        Assert.Empty((System.Collections.IEnumerable)context.GetType().GetProperty("ReceiverSparseChunksPendingWrite")!.GetValue(context)!);
                        Console.WriteLine("carrier-frontier-window=committed-nonterminal; chunks=8; total_chunks=9; session=" +
                            service.Snapshot.Inbound.SessionId + "; transfer=" + transferId);
                    }
                }
                await renamed.Task.WaitAsync(ct);
                Assert.True(OperationalLogNativeSource.TryDescribe(handle!.Stream.SafeFileHandle, out var fileIdentity, out var length));
                Assert.Equal(payloadBytes, length);
                evidence = FrontierNativeReceiverTests.Automatic
                    ? Assert.IsType<SessionFileTransferService.NativeDestinationEvidence>(FrontierNativeReceiverTests.Field(lifetime, "nativeDestinationEvidence"))
                    : service.TryRequestNativeDestinationEvidence(destination!);
                Assert.NotNull(evidence); Assert.Null(evidence.TryOpenRead(service, destination!, lease));
                finalRelease.TrySetResult(); await (await finalizing.Task.WaitAsync(ct)).WaitAsync(ct);
                using (var read = Assert.IsAssignableFrom<Stream>(evidence.TryOpenRead(service, destination!, lease)))
                {
                    Assert.Equal(payloadBytes, evidence.ByteCount);
                    Assert.Equal(transferId, evidence.TransferId);
                    Assert.Equal(service.Snapshot.Inbound!.SessionId, evidence.SessionId);
                    Assert.Equal(Convert.ToBase64String(hash!), evidence.Sha256Base64);
                    Assert.Equal(fileIdentity, evidence.FileIdentity);
                    Assert.Throws<IOException>(() => File.Delete(handle!.Plan.FinalPath));
                    Assert.Equal(hash, await SHA256.HashDataAsync(read, ct));
                    Assert.Equal(-1, read.ReadByte());
                    if (nativeCase == "gui-valid" || canonicalTail)
                    {
                        // Scheduling only: the GUI still validates the original
                        // app request, process, native handle and completion.
                        using var ready = EventWaitHandle.OpenExisting(signal + ".destination-handoff-ready");
                        ready.Set();
                        if (FrontierNativeReceiverTests.Automatic)
                        {
                            Assert.True(await Assert.IsAssignableFrom<Task<bool>>(FrontierNativeReceiverTests.Handoff(lifetime)).WaitAsync(ct));
                            Console.WriteLine("native-receiver-handoff-ok");
                        }
                        else
                        {
                            using var guiPipe = new NamedPipeClientStream(".", "nlink-destination-" + lease.RequestId.ToString("N"),
                                PipeDirection.InOut, PipeOptions.Asynchronous);
                            await guiPipe.ConnectAsync(ct);
                            Assert.True(await evidence.TryHandoffToGuiAsync(service, destination!, lease, guiPipe, ct));
                            Assert.False(await evidence.TryHandoffToGuiAsync(service, destination!, lease, guiPipe, ct));
                        }
                    }
                }
                Assert.Null(evidence.TryOpenRead(service, destination!, lease));
                if (nativeCase == "gui-valid" || canonicalTail) Assert.Throws<IOException>(() => File.Delete(handle!.Plan.FinalPath));
                verified.Set();
            }
            var completed = host ? service.Snapshot.Outbound! : service.Snapshot.Inbound!;
            Assert.Equal(FileTransferTransferState.Completed, completed.State);
            Assert.Equal(payloadBytes, completed.BytesTransferred);
            Assert.Equal("post_tuna_fallback_v6", completed.RouteToken); Assert.Equal(6, completed.ProtocolVersion);
            Assert.Equal(Convert.ToBase64String(hash!), completed.Sha256Base64);
            if (frontierHold && !host)
            {
                // Read-only characterization of the original app-owned context.
                // No receipt/authority is constructed from these diagnostics.
                var observation = context.GetType().GetProperty("FrontierBoundaryObservation")!.GetValue(context);
                var admitted = (bool)context.GetType().GetProperty("FrontierAdmissionAttempted")!.GetValue(context)!;
                var continuity = observation?.GetType().GetField("capturedRepair", flags)?.GetValue(observation);
                var status = observation?.GetType().GetProperty("Status")?.GetValue(observation) ?? "not_admitted";
                Console.WriteLine("carrier-frontier-edge=" + (admitted && continuity is not null ? "observed" : "unavailable") +
                    "; admitted=" + admitted + "; continuity=" + (continuity is not null) + "; status=" + status +
                    "; session=" + completed.SessionId + "; transfer=" + completed.TransferId);
            }
            if (frontierHold && host) Assert.Equal(0, client.OutboundHold!.PendingBytes);
            client.CheckReader();
            Console.WriteLine("cross-process-nkn-" + role + "=" + payloadBytes + "; route=post_tuna_fallback_v6; sha256=" + Convert.ToHexString(hash!).ToLowerInvariant() +
                "; session=" + completed.SessionId + "; transfer=" + completed.TransferId);

            void RequireCanonicalGeometry()
            {
                lock (GetPrivateField(service, "gate")!)
                {
                    Assert.Same(context, GetPrivateField(service, host ? "outboundTransfer" : "inboundTransfer"));
                    AttemptCanonicalTailProfile.RequireGeometry(
                        (long)context.GetType().GetProperty("FileSizeBytes")!.GetValue(context)!,
                        (int)context.GetType().GetProperty("ChunkSizeBytes")!.GetValue(context)!,
                        (int)context.GetType().GetProperty("ChunkCount")!.GetValue(context)!);
                }
            }
        }
        catch (Exception error) { Console.Error.WriteLine(error); throw; }
        finally
        {
            nativeReads?.ReportReadTiming();
            prefixTiming?.Write(role);
            release.TrySetResult(); finalRelease.TrySetResult(); evidence?.Dispose();
            transport.OfflineFrameObserverForTests = null;
            transport.SessionSecurityStateChanged -= suppress;
            Environment.SetEnvironmentVariable("NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT", priorRoot);
        }
        async Task WaitFor(Func<bool> predicate)
        {
            using var edge = CancellationTokenSource.CreateLinkedTokenSource(ct); edge.CancelAfter(TimeSpan.FromSeconds(8));
            while (!predicate()) { client.CheckReader(); await Task.Delay(10, edge.Token); }
            client.CheckReader();
        }
        async Task PeerReady(EventWaitHandle signalEvent)
        { Assert.True(await Task.Run(() => signalEvent.WaitOne(8000), ct)); client.CheckReader(); }
    }

}
