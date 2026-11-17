using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferServiceRoundTripTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public void Snapshot_DefaultsToIdleStates_WhenNoTransferIsActive()
    {
        using var service = new SessionFileTransferService();

        var snapshot = service.Snapshot;

        Assert.Null(snapshot.Outbound);
        Assert.Null(snapshot.Inbound);
        Assert.Equal(FileTransferTransferState.Idle, snapshot.OutboundState);
        Assert.Equal(FileTransferTransferState.Idle, snapshot.InboundState);
    }

    [Fact]
    public async Task ResetSessionState_ClearsCompletedTransferSnapshots()
    {
        const string transferId = "transfer_service_reset_state";
        var payload = Enumerable.Range(0, 10_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_reset_state");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_reset_state");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("reset.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        using var destination = new NonDisposingMemoryStream();
        var accepted = await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        Assert.NotNull(accepted);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed);

        sender.ResetSessionState();
        receiver.ResetSessionState();

        Assert.Null(sender.Snapshot.Outbound);
        Assert.Null(receiver.Snapshot.Inbound);
        Assert.Equal(FileTransferTransferState.Idle, sender.Snapshot.OutboundState);
        Assert.Equal(FileTransferTransferState.Idle, receiver.Snapshot.InboundState);
    }

    [Fact]
    public async Task RoundTrip_Completes_AndWritesExpectedBytes()
    {
        const string transferId = "transfer_service_roundtrip";
        var payload = Enumerable.Range(0, 40_000).Select(static i => (byte)(i % 251)).ToArray();
        var openReadCount = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_service_roundtrip");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_roundtrip");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("sample.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ =>
            {
                Interlocked.Increment(ref openReadCount);
                return Task.FromResult<Stream>(new MemoryStream(payload, writable: false));
            },
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        using var destination = new NonDisposingMemoryStream();
        var accepted = await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        Assert.NotNull(accepted);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed);

        Assert.Equal(2, Volatile.Read(ref openReadCount));
        Assert.Equal(payload, destination.ToArray());
        Assert.Equal(payload.Length, sender.Snapshot.Outbound!.BytesTransferred);
        Assert.Equal(payload.Length, receiver.Snapshot.Inbound!.BytesTransferred);
    }

    [Fact]
    public async Task RoundTrip_Completes_WhenInboundChunkHandlersOverlap()
    {
        const string transferId = "transfer_service_chunk_overlap";
        var payload = Enumerable.Range(0, 345_143).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_chunk_overlap");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_chunk_overlap");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new DelayedWriteMemoryStream(delayMilliseconds: 15);

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("overlap.bin", payload.Length, transferId, ChunkSizeBytes: 16_384),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        var accepted = await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        Assert.NotNull(accepted);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 5000);

        Assert.Equal(payload, destination.ToArray());
        Assert.Null(sender.Snapshot.Outbound!.ErrorCode);
        Assert.Null(receiver.Snapshot.Inbound!.ErrorCode);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    public async Task SnapshotEvidence_OutboundStreamStartsOnceAndRemainsContiguousAfterAccept()
    {
        const string transferId = "transfer_snapshot_stream_continuity";
        var payload = Enumerable.Range(0, 16_384)
            .Select(static i => (byte)(i % 251))
            .ToArray();
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport(
            "session_snapshot_stream_continuity");
        using var receiverTransport = new LoopbackFileTransferTransport(
            "session_snapshot_stream_continuity");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor(
                "snapshot.bin",
                payload.Length,
                transferId,
                ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(
                new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        Assert.NotNull(started);
        await WaitUntilAsync(() =>
            receiver.Snapshot.Inbound?.State ==
            FileTransferTransferState.PendingDecision);

        using var destination = new NonDisposingMemoryStream();
        var accepted = await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);
        Assert.NotNull(accepted);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed);
        await WaitUntilAsync(() =>
            ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_coordinator_snapshot_stream_completed",
                StringComparison.Ordinal));

        var lines = ReadOperationalLogTail(logStart)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var projectedTransferId = "fte1-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(transferId)))
            .ToLowerInvariant()[..24];
        var outboundStart = Assert.Single(lines.Where(line =>
            line.Contains(
                "event=filetransfer_coordinator_snapshot_stream_started",
                StringComparison.Ordinal) &&
            line.Contains("direction=outbound", StringComparison.Ordinal) &&
            GetSnapshotField(line, "transfer_id") == projectedTransferId));
        var coordinatorId = GetSnapshotField(
            outboundStart,
            "coordinator_instance_id");
        var transitionLines = lines.Where(line =>
                line.Contains(
                    "event=filetransfer_coordinator_transition_snapshot",
                    StringComparison.Ordinal) &&
                GetSnapshotField(line, "coordinator_instance_id") == coordinatorId)
            .ToArray();
        Assert.NotEmpty(transitionLines);

        var currentGeneration = long.Parse(
            GetSnapshotField(outboundStart, "initial_state_generation"),
            System.Globalization.CultureInfo.InvariantCulture);
        var currentHash = GetSnapshotField(
            outboundStart,
            "initial_state_hash");
        var operationKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in transitionLines)
        {
            using var document = DecodeSnapshotPayload(
                GetSnapshotField(line, "snapshot_payload"));
            var root = document.RootElement;
            var aggregate = root.GetProperty("aggregate");
            Assert.Equal(
                currentGeneration,
                aggregate.GetProperty("beforeStateGeneration").GetInt64());
            Assert.Equal(
                currentHash,
                aggregate.GetProperty("beforeStateFingerprint").GetString());

            var transitionId = root.GetProperty("transitionId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(transitionId));
            foreach (var operation in aggregate.GetProperty("operations")
                         .EnumerateArray())
            {
                var family = operation.GetProperty("family").GetString();
                var role = operation.GetProperty("operationRole").GetString();
                var authority = operation.GetProperty("authorityId").GetString();
                var generation = operation.GetProperty("operationGeneration")
                    .GetInt64();
                var kind = operation.GetProperty("operationKind").GetString();
                var operationKey = operation.GetProperty("operationKey")
                    .GetString();
                Assert.Equal(
                    $"{family}|{role}|{authority}|{generation}|{kind}",
                    operationKey);
                operationKeys.Add($"{transitionId}|{operationKey}");
            }

            currentGeneration = aggregate
                .GetProperty("afterStateGeneration")
                .GetInt64();
            currentHash = aggregate.GetProperty("afterStateFingerprint")
                .GetString()!;
        }

        var outboundCompletion = Assert.Single(lines.Where(line =>
            line.Contains(
                "event=filetransfer_coordinator_snapshot_stream_completed",
                StringComparison.Ordinal) &&
            GetSnapshotField(line, "coordinator_instance_id") == coordinatorId));
        Assert.Equal(
            currentGeneration.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            GetSnapshotField(outboundCompletion, "final_state_generation"));
        Assert.Equal(
            currentHash,
            GetSnapshotField(outboundCompletion, "final_state_hash"));

        var boundEffects = lines.Where(line =>
                line.Contains(
                    "event=filetransfer_coordinator_authority_effect",
                    StringComparison.Ordinal) &&
                GetSnapshotField(line, "coordinator_instance_id") == coordinatorId &&
                GetSnapshotField(line, "operation_key") != "(none)")
            .ToArray();
        foreach (var effect in boundEffects)
        {
            Assert.Contains(
                $"{GetSnapshotField(effect, "transition_id")}|" +
                GetSnapshotField(effect, "operation_key"),
                operationKeys);
        }
    }

    private static string GetSnapshotField(string line, string name)
    {
        var match = Regex.Match(
            line,
            $@"(?:^|[; ]){Regex.Escape(name)}=([^;\r\n]+)",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Missing snapshot field '{name}' in: {line}");
        return match.Groups[1].Value.Trim();
    }

    private static JsonDocument DecodeSnapshotPayload(string payload)
    {
        var normalized = payload.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(
            normalized.Length + ((4 - normalized.Length % 4) % 4),
            '=');
        return JsonDocument.Parse(
            Encoding.UTF8.GetString(Convert.FromBase64String(normalized)));
    }
}

