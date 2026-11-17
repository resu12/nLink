using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("single")]
    [InlineData("coalesced")]
    [InlineData("late")]
    [InlineData("post_deadline_write")]
    [InlineData("commit_at_deadline")]
    [InlineData("capture_at_bound")]
    [InlineData("completion_at_bound")]
    [InlineData("clock_regressed")]
    [InlineData("pause_aba")]
    [InlineData("data_aba")]
    [InlineData("queue_only")]
    [InlineData("absent")]
    [InlineData("newer_token")]
    [InlineData("duplicate")]
    [InlineData("first_rejected_then_success")]
    [InlineData("commit_after_deadline")]
    [InlineData("capture_after_bound")]
    [InlineData("completion_after_bound")]
    [InlineData("peer_pause_aba")]
    [InlineData("transport_pause_aba")]
    [InlineData("data_generation_aba")]
    [InlineData("transport_epoch_aba")]
    [InlineData("terminal")]
    [InlineData("changed_frame")]
    [InlineData("no_result_fallback")]
    [InlineData("capture_source_aba")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationEvidence")]
    public async Task FrontierPublicationEvidence_OrdinaryWriteBindsOnlyExactAcceptedCompletion(string variant)
        => await RunFrontierPublicationFixtureAsync(variant, captureEnabled: true);

    internal static async Task<(IReadOnlyList<string> Lines, string[] Calls, FileTransferCoordinator.FrontierPublicationObservation Producer)> RunFrontierPublicationFixtureAsync(
        string variant, bool captureEnabled, Action<object>? sourceMutation = null)
    {
        string[] calls = [];
        var primaryFailure = variant is "primary_failure" or "primary_overflow" or "primary_required_overflow" or "primary_conflict";
        DateTimeOffset? projected = null;
        Action? captureMutation = null;
        var observation = new FileTransferCoordinator.FrontierPublicationObservation(() =>
        {
            var mutation = captureMutation;
            captureMutation = null;
            mutation?.Invoke();
            return projected ?? DateTimeOffset.UtcNow;
        });
        var expected = variant switch
        {
            "single" or "coalesced" or "newer_token" or "duplicate" or "source_noop" => "PublishedBeforeOriginalDeadline",
            "late" or "post_deadline_write" => "PublishedWithinObservationBound",
            _ when primaryFailure => "PublishedWithinObservationBound",
            "commit_at_deadline" or "commit_after_deadline" => "PreDeadlineCommitUnproven",
            "capture_at_bound" or "completion_at_bound" or "capture_after_bound" or "completion_after_bound"
                => "PublicationOutsideObservationBound",
            "pause_aba" or "data_aba" or "peer_pause_aba" or "transport_pause_aba" or "data_generation_aba" or
                "transport_epoch_aba" or "terminal" or "capture_source_aba" or "source_mutation" => "ObservationInvalidated",
            "queue_only" or "first_rejected_then_success" => "PublicationRejected",
            _ => "EvidenceIncomplete",
        };
        if (!captureEnabled) { expected = "Unobserved"; }
        var lines = await CreateCheckpointRepairContinuityServiceEvidenceFixtureAsync(true,
            captureEnabled ? observation : null, async (service, context, baseTransport) =>
        {
            var transport = Assert.IsType<FrontierPublicationDeliveryTransport>(baseTransport);
            using var destination = new MemoryStream(new byte[256 * 21 * 1024], writable: true);
            if (primaryFailure)
            {
                // Exercise the actual non-contiguous receive path while the
                // settled repair's recent grace is still open. This produces
                // the primary's candidate; never synthesize a failure log.
                await Write(200, 1, advancesFrontier: false);
            }
            object? stall = null;
            await CoreSmokeTestsBase.WaitUntilAsync(() =>
            {
                lock (GetServiceGate(service))
                {
                    stall = GetPrivatePropertyValue(context, "CurrentV6FrontierStallGraceWake");
                    return captureEnabled
                        ? observation.Status is "StallArmed" or "EvidenceIncomplete" or "ObservationInvalidated"
                        : stall is not null && GetPrivateProperty<int>(stall, "FrontierChunkIndex") == 101;
                }
            }, TimeSpan.FromSeconds(5));
            Assert.True(observation.Status == (captureEnabled ? "StallArmed" : "Unobserved"),
                $"{observation.Status}: {System.Text.Json.JsonSerializer.Serialize(observation.Lifecycle)}");
            var deadline = GetPrivateProperty<DateTimeOffset>(stall!, "DueUtc");
            projected = deadline.AddMilliseconds(variant switch
                { "commit_at_deadline" => 0, "commit_after_deadline" => 1, _ => -500 });
            // Control the existing coalescing precondition, never committed progress.
            SetPrivateProperty(context, "PullPostTunaRecoveryActive", false);
            // A future coalescing timestamp keeps first-use JIT cost from opening
            // the runtime interval; the explicit send below restores a past time.
            SetPrivateProperty(context, "V6LastReceiverStateSentUtc", DateTimeOffset.UtcNow.AddMinutes(1));
            await Write(101, 1);
            if (variant == "primary_required_overflow")
            {
                // 56 real contiguous commits fit the receipt lifecycle cap, but
                // their exact write witnesses plus mandatory links exceed 64.
                var lastSentFrontier = GetPrivateProperty<int>(context, "V6LastReceiverStateCommittedChunkIndex");
                try
                {
                    for (var chunk = 102; chunk < 157; chunk++)
                    {
                        // Hold the existing coalescing precondition as well as
                        // its timestamp; never alter actual committed progress.
                        SetPrivateProperty(context, "V6LastReceiverStateCommittedChunkIndex", chunk);
                        await Write(chunk, 1);
                    }
                }
                finally { SetPrivateProperty(context, "V6LastReceiverStateCommittedChunkIndex", lastSentFrontier); }
                Assert.Null(observation.Receipt);
            }
            if (variant is "coalesced" or "post_deadline_write")
            {
                projected = deadline.AddMilliseconds(variant == "post_deadline_write" ? 50 : -250);
                await Write(102, 2);
            }
            if (variant == "absent") { return; }
            projected = variant is "capture_at_bound" or "capture_after_bound"
                ? observation.PublicationDeadline.AddMilliseconds(variant == "capture_after_bound" ? 1 : 0) :
                deadline.AddMilliseconds(variant == "post_deadline_write" ? 75 : -200);
            SetPrivateProperty(context, "V6LastReceiverStateSentUtc", DateTimeOffset.UtcNow.AddSeconds(-2));
            if (variant == "no_result_fallback")
            {
                // Test transport unavailability, retaining the same data session
                // and Coordinator source. This invokes the actual no-result path.
                var field = typeof(SessionFileTransferService).GetField("transport", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var original = field.GetValue(service);
                var data = Assert.IsType<RecordingDataSession>(GetPrivatePropertyValue(context, "DataSession"));
                var before = data.SentFrames.Count;
                try
                {
                    field.SetValue(service, null);
                    Assert.True(await Send());
                }
                finally { field.SetValue(service, original); }
                Assert.Equal(before + 1, data.SentFrames.Count);
                Assert.IsType<FileTransferReceiverStateFrameV6>(data.SentFrames[^1]);
                Assert.Equal("EvidenceIncomplete", observation.Status);
                Assert.Null(observation.Receipt);
                return;
            }
            if (variant == "capture_source_aba")
            {
                // Run at the observer clock read inside the original frame gate,
                // before the separate gate that creates the delivery request.
                captureMutation = () =>
                {
                    var generation = GetPrivateProperty<long>(context, "DataSessionGeneration");
                    SetPrivateProperty(context, "DataSessionGeneration", generation + 1);
                    SetPrivateProperty(context, "DataSessionGeneration", generation);
                };
            }
            transport.Defer = true;
            transport.QueueOnly = variant is "queue_only" or "first_rejected_then_success";
            var send = Send();
            await transport.Captured.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var selectedFrame = Assert.IsType<FileTransferReceiverStateFrameV6>(transport.LastRequest!.Frame);
            Task<bool>? newerSend = null;
            FallbackControlPlaneToken? newerToken = null;
            Assert.Null(observation.Receipt);
            if (sourceMutation is not null)
            {
                lock (GetServiceGate(service)) { sourceMutation(context); }
            }
            if (variant == "pause_aba")
            {
                SetPrivateProperty(context, "UserPaused", true);
                SetPrivateProperty(context, "UserPaused", false);
            }
            if (variant == "data_aba")
            {
                var original = GetPrivatePropertyValue(context, "DataSession");
                SetPrivateProperty(context, "DataSession", new CompletingDataSession(
                    GetPrivateProperty<string>(context, "SessionId"), GetPrivateProperty<string>(context, "TransferId")));
                SetPrivateProperty(context, "DataSession", original);
            }
            if (variant is "peer_pause_aba" or "transport_pause_aba")
            {
                var property = variant == "peer_pause_aba" ? "PeerPaused" : "PullTransportPaused";
                SetPrivateProperty(context, property, true);
                SetPrivateProperty(context, property, false);
            }
            if (variant == "data_generation_aba")
            {
                var original = GetPrivateProperty<long>(context, "DataSessionGeneration");
                SetPrivateProperty(context, "DataSessionGeneration", original + 1);
                SetPrivateProperty(context, "DataSessionGeneration", original);
            }
            if (variant == "transport_epoch_aba")
            {
                var adopt = typeof(SessionFileTransferService).GetMethod("AdoptInboundFallbackLegTransportEpochLocked",
                    BindingFlags.Static | BindingFlags.NonPublic)!;
                lock (GetServiceGate(service))
                {
                    adopt.Invoke(null, [context, 4L, "publication_fixture_source_change"]);
                    adopt.Invoke(null, [context, 3L, "publication_fixture_source_restored"]);
                }
            }
            if (variant == "terminal") { SetPrivateProperty(context, "State", FileTransferTransferState.Completed); }
            if (variant == "changed_frame")
            {
                typeof(FileTransferReceiverStateFrameV6).GetProperty("Epoch")!.SetValue(selectedFrame, selectedFrame.Epoch + 1);
            }
            if (variant == "newer_token")
            {
                SetPrivateProperty(context, "V6LastReceiverStateSentUtc", DateTimeOffset.UtcNow.AddSeconds(-2));
                newerSend = Send();
                await transport.SecondCaptured.Task.WaitAsync(TimeSpan.FromSeconds(3));
                newerToken = GetPrivateProperty<FallbackControlPlaneToken>(context, "CurrentFallbackControlPlaneToken");
                Assert.NotSame(selectedFrame, transport.LastRequest!.Frame);
                Assert.Null(observation.Receipt);
            }
            projected = variant switch
            {
                "completion_at_bound" => observation.PublicationDeadline,
                "completion_after_bound" => observation.PublicationDeadline.AddMilliseconds(1),
                "clock_regressed" => deadline.AddMilliseconds(-600),
                "late" or "post_deadline_write" => deadline.AddMilliseconds(150),
                _ when primaryFailure => deadline.AddMilliseconds(150),
                _ => deadline.AddMilliseconds(-100),
            };
            if (primaryFailure)
            {
                // Real writer timing, not timestamp rewriting: the old primary
                // must still report the missing original-deadline publication.
                var remaining = deadline.AddMilliseconds(100) - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero) { await Task.Delay(remaining); }
            }
            transport.Release.TrySetResult();
            Assert.Equal(variant is not ("queue_only" or "first_rejected_then_success" or "terminal"), await send);
            Assert.Equal(expected, observation.Status);
            if (variant is "first_rejected_then_success" or "duplicate")
            {
                var original = observation.Receipt;
                transport.Defer = false;
                transport.QueueOnly = false;
                SetPrivateProperty(context, "V6LastReceiverStateSentUtc", DateTimeOffset.UtcNow.AddSeconds(-2));
                Assert.True(await Send());
                Assert.Same(original, observation.Receipt);
                Assert.Equal(expected, observation.Status);
            }
            if (variant == "newer_token")
            {
                Assert.Equal(selectedFrame.Epoch, observation.Receipt!.Frame.Epoch);
                Assert.NotEqual(newerToken, observation.Receipt.DeliveryToken);
                Assert.Equal(newerToken, GetPrivateProperty<FallbackControlPlaneToken>(context, "CurrentFallbackControlPlaneToken"));
                transport.ReleaseSecond.TrySetResult();
                Assert.True(await newerSend!);
            }
            if (variant == "primary_overflow")
            {
                var original = observation.Receipt;
                transport.Defer = false;
                for (var i = 0; i < 16; i++)
                {
                    SetPrivateProperty(context, "V6LastReceiverStateSentUtc", DateTimeOffset.UtcNow.AddSeconds(-2));
                    Assert.True(await Send());
                }
                Assert.Same(original, observation.Receipt);
            }
            if (variant == "primary_conflict")
            {
                // Deliberately conflicting diagnostics in the genuine writer,
                // not a fabricated accepted frame or edited retained input.
                LocalOperationalLog.Info("FileTransferService",
                    $"event=filetransfer_v6_receiver_state_sent; transfer_id={selectedFrame.TransferId}; session_id={selectedFrame.SessionId}; reason=conflicting_publication_fixture; epoch={selectedFrame.Epoch}; contiguous_committed_chunk_index={selectedFrame.ContiguousCommittedChunkIndex + 1}; durable_received_highest_chunk_index={selectedFrame.DurableReceivedHighestChunkIndex}; requested_until_chunk_index_exclusive={selectedFrame.CreditUntilChunkIndexExclusive}; missing_range_count={selectedFrame.MissingRanges.Count}; bytes_committed={selectedFrame.BytesCommitted}; destination_mode=sparse_seekable; transfer_paused=0; transport_epoch={selectedFrame.TransportEpoch}; repair_request_id=(none); priority=(none); recovery_mode=(none)");
            }
            calls = transport.Calls.ToArray();

            Task<bool> Send() => Assert.IsAssignableFrom<Task<bool>>(typeof(SessionFileTransferService)
                .GetMethod("SendInboundV6ReceiverStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, "ordinary_publication_fixture", true, false, null]));

            async Task Write(int start, int count, bool advancesFrontier = true)
            {
                const int chunkSize = 21 * 1024;
                var chunk = Enumerable.Repeat((byte)0xb7, chunkSize).ToArray();
                var batch = new FileTransferChunkBatchFrameV6
                {
                    SessionId = GetPrivateProperty<string>(context, "SessionId"),
                    TransferId = GetPrivateProperty<string>(context, "TransferId"),
                    TransportEpoch = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg").TransportEpochId,
                    StartChunkIndex = start, ChunkCount = count, BatchId = "ordinary-publication-fixture",
                    DataSegments = Enumerable.Repeat(chunk, count).ToArray(),
                };
                var chunks = Enumerable.Range(start, count).Select(index => (index, chunk)).ToList();
                await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
                    .GetMethod("HandleInboundV6SparseChunkBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [context, batch, chunks, destination, FileTransferTransportKind.RegularNkn, null]));
                Assert.Equal(advancesFrontier ? start + count : 101, GetPrivateProperty<int>(context, "NextChunkIndex"));
                Assert.Equal(chunk, destination.ToArray().Skip(start * chunkSize).Take(chunkSize));
            }
        }, priorStateSends: variant == "primary_overflow" ? 16 : 0);
        Assert.Equal(expected, observation.Status);
        if (expected.StartsWith("Published", StringComparison.Ordinal))
        {
            var receipt = Assert.IsType<FileTransferCoordinator.FrontierPublicationReceipt>(observation.Receipt);
            Assert.Equal(101, receipt.Commits[0].Before);
            Assert.Equal(receipt.Commits[0].ObservedUtc.AddSeconds(1), receipt.PublicationDeadline);
            Assert.Equal(receipt.Commits[^1].After, receipt.Frame.ContiguousCommittedChunkIndex);
            Assert.Equal(receipt.Repair.CoordinatorInstanceId, receipt.Completion.CoordinatorInstanceId);
            Assert.True(receipt.CompletedUtc < receipt.PublicationDeadline);
            Assert.Equal(receipt.SourceRuntimeOwner, receipt.SourcePublication.RuntimeOwner);
            Assert.Equal(receipt.RecentGeneration, receipt.RecentArms[^1].Generation);
            Assert.Equal(receipt.StallStartedUtc.AddMilliseconds(750), receipt.StallDeadline);
            var persistedCompletion = SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(receipt.Completion);
            var completionLine = Assert.Single(lines, line =>
                line.Contains("event=filetransfer_coordinator_transition_snapshot;", StringComparison.Ordinal) &&
                GetDelimitedLogField(line, "snapshot_sha256") == persistedCompletion.CalculatePayloadSha256());
            Assert.Equal(persistedCompletion.ToBase64UrlPayload(), GetDelimitedLogField(completionLine, "snapshot_payload"));
            var writer = Assert.Single(lines.Select(line => GetDelimitedLogField(line, "log_writer_id")).Distinct());
            var artifactDirectory = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR");
            if (!string.IsNullOrWhiteSpace(artifactDirectory))
            {
                Directory.CreateDirectory(artifactDirectory);
                // Preserve the actual typed receipt and exact frozen writer input;
                // this file is not an authoritative primary-analysis result.
                File.WriteAllLines(Path.Combine(artifactDirectory, $"receipt-{variant}.log"), lines);
                File.WriteAllText(Path.Combine(artifactDirectory, $"receipt-{variant}.json"),
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Kind = "offline_frontier_publication_capture_v2", Receipt = receipt,
                        WriterId = writer,
                        CompletionWriterSequence = GetDelimitedLogField(completionLine, "log_writer_sequence"),
                        CompletionLedgerLine = lines.ToList().IndexOf(completionLine) + 1,
                        PersistedCompletion = persistedCompletion,
                    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
        }
        else
        {
            Assert.Null(observation.Receipt);
            Assert.Null(observation.WriterBinding);
            Assert.Null(observation.TakeWriterBinding());
        }
        Assert.Null(GetPrivateField(observation, "dataSession"));
        Assert.Null(GetPrivateField(observation, "originalFrame"));
        Assert.Null(GetPrivateField(observation, "sourceLeg"));
        Assert.Null(GetPrivateField(observation, "sourceEpoch"));
        return (lines, calls, observation);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationEvidence")]
    public async Task FrontierPublicationEvidence_CaptureDoesNotChangeRuntimeDecisionsOrSends()
    {
        var enabled = await RunFrontierPublicationFixtureAsync("single", captureEnabled: true);
        var disabled = await RunFrontierPublicationFixtureAsync("single", captureEnabled: false);
        Assert.Equal(enabled.Calls, disabled.Calls);
        Assert.Equal(Transitions(enabled.Lines), Transitions(disabled.Lines));

        static string[] Transitions(IReadOnlyList<string> lines) => lines
            .Where(line => line.Contains("event=filetransfer_coordinator_transition_snapshot;", StringComparison.Ordinal))
            .Select(line =>
            {
                var payload = GetDelimitedLogField(line, "snapshot_payload").Replace('-', '+').Replace('_', '/');
                using var json = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
                var root = json.RootElement;
                var aggregate = root.GetProperty("aggregate");
                return string.Join('|', root.GetProperty("decisionKind"), root.GetProperty("decisionStatus"),
                    root.GetProperty("decisionReason"), aggregate.GetProperty("beforeStateGeneration"),
                    aggregate.GetProperty("afterStateGeneration"), aggregate.GetProperty("changedFamilies"),
                    string.Join(',', aggregate.GetProperty("operations").EnumerateArray().Select(operation =>
                        $"{operation.GetProperty("family")}:{operation.GetProperty("operationKind")}:{operation.GetProperty("operationRole")}:{operation.GetProperty("receiptStatus")}:{operation.GetProperty("stateApplied")}")));
            }).ToArray();
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationEvidence")]
    public async Task FrontierPublicationEvidence_MalformedChainsFailClosedWithoutRuntimeMutation()
    {
        var production = new FileTransferCoordinator.FrontierPublicationObservation(() => DateTimeOffset.UtcNow);
        await CreateCheckpointRepairContinuityServiceEvidenceFixtureAsync(true, production, (service, context, _) =>
        {
            lock (GetServiceGate(service))
            {
                var state = CreateCoordinatorStateForTest(context);
                var session = GetPrivatePropertyValue(context, "DataSession")!;
                var repair = Assert.IsType<CheckpointRepairContinuityStamp>(GetPrivateField(production, "repair"));
                var authority = Assert.IsType<FallbackFrontierRepairAuthority>(GetPrivateField(production, "repairAuthority"));
                var bytes = GetPrivateProperty<long>(context, "BytesTransferred");
                var start = DateTimeOffset.UtcNow;
                var now = start;
                FileTransferCoordinator.FrontierPublicationObservation Seed()
                {
                    now = start;
                    var result = new FileTransferCoordinator.FrontierPublicationObservation(() => now);
                    result.RepairProgress(state, repair, authority, session, 101, 255, bytes, false, false, false);
                    Assert.Equal("RepairObserved", result.Status);
                    return result;
                }
                void Arm(FileTransferCoordinator.FrontierPublicationObservation target)
                {
                    target.RecentArm(state, session, 1, 101, 255, start, start.AddSeconds(1));
                    now = start.AddSeconds(1);
                    target.StallArm(state, session, 2, 101, 255, now, now.AddMilliseconds(750));
                    target.RecentRechecked(1, 2);
                    Assert.Equal("StallArmed", target.Status);
                    now = now.AddMilliseconds(100);
                }
                foreach (var changed in new[]
                {
                    state with { SessionId = "wrong_session" }, state with { TransferId = "wrong_transfer" },
                    state with { Direction = FileTransferDirection.Outbound }, state with { IsTerminal = true },
                    state with { PersistentState = state.PersistentState with { CoordinatorInstanceId = "wrong_coordinator" } },
                    state with { CurrentDataSessionGeneration = 2 }, state with { CurrentDataSessionIdentity = "wrong_data" },
                    state with { CurrentRuntimeOwner = null }, state with { CurrentRoutePublicationToken = null },
                })
                {
                    var target = Seed();
                    target.ObserveSource(changed, session);
                    target.ObserveSource(state, session);
                    Assert.Equal("ObservationInvalidated", target.Status);
                    Assert.Null(target.Receipt);
                }
                var wrongRepair = Seed();
                wrongRepair.RepairProgress(state, repair with { ProtocolRepairRequestId = "wrong_repair" }, authority, session, 101, 255, bytes, false, false, false);
                Assert.Equal("EvidenceIncomplete", wrongRepair.Status);
                var duplicate = Seed();
                duplicate.RepairProgress(state, repair, authority, session, 101, 255, bytes, false, false, false);
                duplicate.RecentArm(state, session, 1, 101, 255, start, start.AddSeconds(1));
                duplicate.RecentArm(state, session, 1, 101, 255, start, start.AddSeconds(1));
                Assert.Equal("RecentArmed", duplicate.Status);
                duplicate.EndObservation();
                var exhausted = Seed();
                for (var i = 1; i <= 64; i++)
                { exhausted.RecentArm(state, session, i, 101, 254 + i, start, start.AddSeconds(1)); }
                Assert.Equal("EvidenceIncomplete", exhausted.Status);
                Assert.Equal(64, exhausted.Lifecycle!.LastSequence);
                Assert.Equal("lifecycle_capacity", exhausted.Lifecycle.Entries[^1].Boundary);
                Assert.Equal("RepairObserved", exhausted.Lifecycle.Entries[0].Kind);
                foreach (var pause in new[] { "user", "peer", "transport" })
                {
                    var paused = new FileTransferCoordinator.FrontierPublicationObservation(() => start);
                    paused.RepairProgress(state, repair, authority, session, 101, 255, bytes,
                        pause == "user", pause == "peer", pause == "transport");
                    Assert.Equal("ObservationInvalidated", paused.Status);
                    Assert.Equal("initial_pause", paused.Lifecycle!.Entries[^1].Boundary);
                }
                var missingRoot = Seed();
                missingRoot.StallArm(state, session, 2, 101, 255, now, now.AddMilliseconds(750));
                Assert.Equal("EvidenceIncomplete", missingRoot.Status);
                var missingRearm = Seed();
                missingRearm.RecentArm(state, session, 1, 101, 255, start, start.AddSeconds(1));
                missingRearm.RecentArm(state, session, 3, 101, 256, start, start.AddSeconds(1));
                Assert.Equal("EvidenceIncomplete", missingRearm.Status);
                var wrongWake = Seed();
                Arm(wrongWake);
                wrongWake.RecentRechecked(2, 2);
                Assert.Equal("EvidenceIncomplete", wrongWake.Status);
                foreach (var mutation in new[] { "missing_write", "count", "highest", "bytes", "capacity", "unsupported" })
                {
                    var target = Seed();
                    Arm(target);
                    target.Commit(state, session, mutation == "missing_write" ? 102 : 101,
                        mutation == "count" ? 103 : 102, 1, mutation == "highest" ? 254 : 255,
                        mutation == "bytes" ? bytes - 1 : bytes + 21 * 1024);
                    if (mutation == "capacity")
                    {
                        for (var i = 0; i < 64; i++) { target.Commit(state, session, 102, 102, 0, 255, bytes + 21 * 1024); }
                    }
                    if (mutation == "unsupported")
                    {
                        var frame = new FileTransferReceiverStateFrameV6
                        {
                            SessionId = state.SessionId, TransferId = state.TransferId, Epoch = 1, TransportEpoch = 3,
                            ContiguousCommittedChunkIndex = 102, DurableReceivedHighestChunkIndex = 255,
                            BytesCommitted = bytes + 21 * 1024,
                        };
                        target.Capture(state, session, frame);
                        Assert.Equal("FrameCaptured", target.Status);
                        target.Unsupported(frame);
                    }
                    Assert.Equal("EvidenceIncomplete", target.Status);
                    Assert.Null(target.Receipt);
                    Assert.Null(GetPrivateField(target, "dataSession"));
                }
                Assert.Equal(FileTransferCoordinator.CalculateSnapshotStateFingerprint(state),
                    FileTransferCoordinator.CalculateSnapshotStateFingerprint(CreateCoordinatorStateForTest(context)));
            }
            return Task.CompletedTask;
        });
    }

    private sealed class FrontierPublicationDeliveryTransport(string sessionId)
        : PeerVisibleControlPlaneLoopbackTransport(sessionId, deliverFrames: false)
    {
        internal bool Defer { get; set; }
        internal bool QueueOnly { get; set; }
        internal List<string> Calls { get; } = [];
        internal TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int deferredCount;

        public override async Task<FileTransferControlPlaneDeliveryResult> SendFileTransferControlPlaneFrameAsync(
            FileTransferControlPlaneDeliveryRequest request, CancellationToken ct)
        {
            Calls.Add($"{request.Kind}:{request.Reason}:{request.Frame.Type}");
            var result = await base.SendFileTransferControlPlaneFrameAsync(request, ct);
            if (Defer && request.Frame is FileTransferReceiverStateFrameV6)
            {
                var second = Interlocked.Increment(ref deferredCount) > 1;
                (second ? SecondCaptured : Captured).TrySetResult();
                await (second ? ReleaseSecond : Release).Task.WaitAsync(ct);
                if (QueueOnly) { result = result with { PeerVisibleAny = false, ControlCopy = false }; }
            }
            return result;
        }
    }
}
