using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using System.Security.Cryptography;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferPullWindowingTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public async Task PullSession_V3Streaming_CompletesWithoutV2RequestLoop()
    {
        const string transferId = "transfer_service_pull_v3_streaming";
        var payload = Enumerable.Range(0, 256_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_streaming")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_streaming")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-streaming.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 15000);
        Assert.Equal(payload, destination.ToArray());
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV3);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3);
        Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferGrantWindowFrameV3);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferRequestChunksFrameV2);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferAckProgressFrameV3 or FileTransferGrantWindowFrameV3 or FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3);
    }

    [Fact]
    public async Task PullSession_V3Streaming_UsesLargerChunks_AndHealthyGrantWindow_WithMinimalLegacyControlNoise()
    {
        const string transferId = "transfer_service_pull_v3_tuned_healthy";
        var payload = Enumerable.Range(0, 3_500_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_healthy")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_healthy")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-tuned-healthy.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        Assert.Equal(40 * 1024, manifest.ChunkSizeBytes);
        var maxGrantWindowBytes = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes).DefaultIfEmpty(0).Max();
        Assert.True(maxGrantWindowBytes >= (2 * 1024 * 1024) - manifest.ChunkSizeBytes, $"Expected a healthy V3 grant window near 2 MiB, but saw {maxGrantWindowBytes} bytes.");
        Assert.Empty(receiverTransport.SentWindowUpdates);
        Assert.True(receiverTransport.SentPressureStates.Count <= 3, $"Expected V3 healthy flow to keep pressure chatter low, but saw {receiverTransport.SentPressureStates.Count} pressure-state messages.");
    }

    [Fact]
    public async Task PullSession_V3Streaming_OnConservativeNknStartup_UsesReducedStartupChunkAndGrantWindow()
    {
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousPipelineDepth = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_DEPTH");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Current");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_DEPTH", "1");
        try
        {
            const string transferId = "transfer_service_pull_v3_nkn_conservative_startup";
            var payload = Enumerable.Range(0, 5_500_000).Select(static i => (byte)(i % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_nkn_conservative_startup")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_nkn_conservative_startup")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);
            senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
            {
                if (frame is FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3)
                {
                    await Task.Delay(20, ct).ConfigureAwait(false);
                }

                return false;
            };
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            var logStart = GetOperationalLogLength();
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-nkn-conservative-startup.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 30000);
            Assert.Equal(payload, destination.ToArray());
            var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
            Assert.Equal(24 * 1024, manifest.ChunkSizeBytes);
            var firstGrantWindow = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().FirstOrDefault();
            Assert.NotNull(firstGrantWindow);
            var firstGrantWindowBytes = (firstGrantWindow!.GrantedUntilChunkIndexExclusive - firstGrantWindow.NextExpectedChunkIndex) * manifest.ChunkSizeBytes;
            Assert.True(firstGrantWindowBytes <= (512 * 1024) + (manifest.ChunkSizeBytes * 2), $"Expected conservative NKN startup to keep the initial grant window near 512 KiB, but saw {firstGrantWindowBytes} bytes.");
            var grantWindowBytes = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes).ToList();
            Assert.Contains(grantWindowBytes, windowBytes => windowBytes >= (1024 * 1024) - manifest.ChunkSizeBytes && windowBytes < (2 * 1024 * 1024));
            Assert.Contains(grantWindowBytes, windowBytes => windowBytes >= (2 * 1024 * 1024) - manifest.ChunkSizeBytes);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.True(
                logTail.Contains("reason=startup_fast_clean", StringComparison.Ordinal) ||
                logTail.Contains("reason=file_only_reorder_tolerated", StringComparison.Ordinal),
                "Expected conservative startup to exit after clean progress or benign file-only sparse reorder.");
            if (logTail.Contains("reason=startup_probe", StringComparison.Ordinal))
            {
                Assert.Contains("startup_probe_window_bytes=1048576", logTail, StringComparison.Ordinal);
            }

            Assert.Contains("first_repair_or_timeout_before_startup_exit=0", logTail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_DEPTH", previousPipelineDepth);
        }
    }

    [Theory]
    [InlineData("Packed3x20KiB", 20 * 1024, 3)]
    [InlineData("Packed3x21KiB", 21 * 1024, 3)]
    [InlineData("LargeSingle48KiB", 48 * 1024, 1)]
    public async Task PullSession_V3Streaming_PayloadEfficiencyProfile_SelectsChunkAndBatchShape(string profile, int expectedChunkSizeBytes, int expectedMaxBatchCount)
    {
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousAllowScreenshare = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.AllowScreenShareEnvironmentVariableName);
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, profile);
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.AllowScreenShareEnvironmentVariableName, null);
        try
        {
            var transferId = $"transfer_service_pull_v3_payload_profile_{profile}";
            var sessionId = $"session_service_pull_v3_payload_profile_{profile}";
            var payload = Enumerable.Range(0, 512 * 1024).Select(static i => (byte)(i % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId)
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            var logStart = GetOperationalLogLength();
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-payload-profile.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);

            Assert.Equal(payload, destination.ToArray());
            var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
            Assert.Equal(expectedChunkSizeBytes, manifest.ChunkSizeBytes);
            var maxBatchCount = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV3>().Select(static batch => batch.DataSegments.Count).DefaultIfEmpty(1).Max();
            Assert.True(maxBatchCount <= expectedMaxBatchCount, $"Expected max batch count <= {expectedMaxBatchCount}, saw {maxBatchCount}.");
            if (expectedMaxBatchCount > 1)
            {
                Assert.Contains(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV3>(), batch => batch.DataSegments.Count == expectedMaxBatchCount);
            }
            else
            {
                Assert.Empty(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV3>());
            }

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_payload_efficiency_profile_selected", logTail, StringComparison.Ordinal);
            Assert.Contains($"profile={profile}", logTail, StringComparison.Ordinal);
            Assert.Contains($"chunk_size_bytes={expectedChunkSizeBytes}", logTail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.AllowScreenShareEnvironmentVariableName, previousAllowScreenshare);
        }
    }

    [Fact]
    public async Task PullSession_V3Streaming_PayloadEfficiencyProfile_ForcesCurrentWhenScreenshareActive()
    {
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousAllowScreenshare = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.AllowScreenShareEnvironmentVariableName);
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "LargeSingle48KiB");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.AllowScreenShareEnvironmentVariableName, null);
        try
        {
            const string transferId = "transfer_service_pull_v3_payload_profile_screenshare_forced_current";
            var payload = Enumerable.Range(0, 256 * 1024).Select(static i => (byte)(i % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_payload_profile_screenshare_forced_current")
            {
                SupportsFileTransferV3Streaming = true,
            };
            using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_payload_profile_screenshare_forced_current")
            {
                SupportsFileTransferV3Streaming = true,
            };
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            sender.SetSessionScreenShareActive(true);
            receiver.SetSessionScreenShareActive(true);
            var logStart = GetOperationalLogLength();
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-payload-profile-screenshare.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 15000);

            Assert.Equal(payload, destination.ToArray());
            var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
            Assert.Equal(24 * 1024, manifest.ChunkSizeBytes);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("profile=Current", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=screen_share_active_forced_current", logTail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.AllowScreenShareEnvironmentVariableName, previousAllowScreenshare);
        }
    }

    [Fact]
    public async Task PullSession_V3Streaming_PayloadEfficiencyProfile_DefaultsPackedForNknFileOnly()
    {
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousAllowScreenshare = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.AllowScreenShareEnvironmentVariableName);
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, null);
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.AllowScreenShareEnvironmentVariableName, null);
        try
        {
            const string transferId = "transfer_service_pull_v3_payload_profile_nkn_default";
            var payload = Enumerable.Range(0, 512 * 1024).Select(static i => (byte)(i % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_payload_profile_nkn_default")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_payload_profile_nkn_default")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            var logStart = GetOperationalLogLength();
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-payload-profile-nkn-default.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);

            Assert.Equal(payload, destination.ToArray());
            var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
            Assert.Equal(21 * 1024, manifest.ChunkSizeBytes);
            Assert.Contains(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV3>(), batch => batch.DataSegments.Count == 3);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("profile=Packed3x21KiB", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=nkn_file_only_default", logTail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.AllowScreenShareEnvironmentVariableName, previousAllowScreenshare);
        }
    }

    [Fact]
    public async Task PullSession_V3Streaming_NknFileOnly_DefaultSenderTransportPipelineDepthIsEight()
    {
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousPipelineDepth = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_DEPTH");
        var previousPendingBytes = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_PENDING_BYTES");
        var previousAsyncPump = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_ASYNC_SENDER_PUMP");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Packed3x21KiB");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_DEPTH", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_PENDING_BYTES", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_ASYNC_SENDER_PUMP", null);
        try
        {
            const string transferId = "transfer_service_pull_v3_sender_transport_pipeline_default";
            var payload = Enumerable.Range(0, 2 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_sender_transport_pipeline_default")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
                DataSessionSendDelayMs = 75,
            };
            using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_sender_transport_pipeline_default")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-sender-transport-pipeline-default.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 30000);

            Assert.Equal(payload, destination.ToArray());
            var retainedLogs = ReadRetainedOperationalLogs();
            Assert.Contains("event=filetransfer_v3_sender_pipeline_summary", retainedLogs, StringComparison.Ordinal);
            Assert.Contains("effective_depth=8", retainedLogs, StringComparison.Ordinal);
            Assert.Contains("pending_bytes_limit=2097152", retainedLogs, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v3_sender_feed_summary", retainedLogs, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v3_sender_grant_apply_summary", retainedLogs, StringComparison.Ordinal);
            Assert.Contains("async_sender_pump=1", retainedLogs, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v3_receiver_grant_decision_summary", retainedLogs, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_DEPTH", previousPipelineDepth);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_PENDING_BYTES", previousPendingBytes);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_ASYNC_SENDER_PUMP", previousAsyncPump);
        }
    }

    [Fact]
    public async Task PullSession_V3Streaming_SenderTransportPipeline_AllowsMultipleInFlightSends()
    {
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousPipelineDepth = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_DEPTH");
        var previousPendingBytes = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_PENDING_BYTES");
        var previousAsyncPump = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_ASYNC_SENDER_PUMP");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Packed3x21KiB");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_DEPTH", "4");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_PENDING_BYTES", "262144");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_ASYNC_SENDER_PUMP", null);
        try
        {
            const string transferId = "transfer_service_pull_v3_sender_transport_pipeline";
            var payload = Enumerable.Range(0, 2 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_sender_transport_pipeline")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
                DataSessionSendDelayMs = 75,
            };
            using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_sender_transport_pipeline")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            var logStart = GetOperationalLogLength();
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-sender-transport-pipeline.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 30000);

            Assert.Equal(payload, destination.ToArray());
            Assert.True(senderTransport.MaxConcurrentDataSessionSends >= 2, $"Expected pipelined transport sends, saw max concurrency {senderTransport.MaxConcurrentDataSessionSends}.");
            var retainedLogs = ReadRetainedOperationalLogs();
            Assert.Contains("event=filetransfer_v3_sender_pipeline_summary", retainedLogs, StringComparison.Ordinal);
            Assert.Contains("effective_depth=4", retainedLogs, StringComparison.Ordinal);
            Assert.Contains("pending_bytes_limit=262144", retainedLogs, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v3_sender_feed_summary", retainedLogs, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v3_sender_grant_apply_summary", retainedLogs, StringComparison.Ordinal);
            Assert.Contains("async_sender_pump=1", retainedLogs, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_DEPTH", previousPipelineDepth);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_PENDING_BYTES", previousPendingBytes);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_ASYNC_SENDER_PUMP", previousAsyncPump);
        }
    }

    [Fact]
    public async Task PullSession_V3Streaming_SenderTransportPipeline_ForcesSerialWhenScreenshareActive()
    {
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousPipelineDepth = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_DEPTH");
        var previousAsyncPump = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_ASYNC_SENDER_PUMP");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Packed3x21KiB");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_DEPTH", "4");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_ASYNC_SENDER_PUMP", null);
        try
        {
            const string transferId = "transfer_service_pull_v3_sender_transport_pipeline_screenshare";
            var payload = Enumerable.Range(0, 512 * 1024).Select(static i => (byte)(i % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_sender_transport_pipeline_screenshare")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
                DataSessionSendDelayMs = 50,
            };
            using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_sender_transport_pipeline_screenshare")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            sender.SetSessionScreenShareActive(true);
            receiver.SetSessionScreenShareActive(true);
            var logStart = GetOperationalLogLength();
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-sender-transport-pipeline-screenshare.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);

            Assert.Equal(payload, destination.ToArray());
            Assert.True(senderTransport.MaxConcurrentDataSessionSends <= 1, $"Expected screen-share active sends to stay serial, saw max concurrency {senderTransport.MaxConcurrentDataSessionSends}.");
            var retainedLogs = ReadRetainedOperationalLogs();
            Assert.Contains("event=filetransfer_v3_sender_pipeline_summary", retainedLogs, StringComparison.Ordinal);
            Assert.Contains("effective_depth=1", retainedLogs, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v3_sender_grant_apply_summary", retainedLogs, StringComparison.Ordinal);
            Assert.Contains("async_sender_pump=0", retainedLogs, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SEND_PIPELINE_DEPTH", previousPipelineDepth);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_ASYNC_SENDER_PUMP", previousAsyncPump);
        }
    }

    [Fact]
    public async Task PullSession_V3Streaming_OnConservativeNknStartup_DegradesEarlierUnderRepairChurn()
    {
        const string transferId = "transfer_service_pull_v3_nkn_conservative_repair";
        var payload = Enumerable.Range(0, 2_500_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_nkn_conservative_repair")
        {
            SupportsFileTransferV3Streaming = true,
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_nkn_conservative_repair")
        {
            SupportsFileTransferV3Streaming = true,
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);
        var delayedChunkGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedFrames = new ConcurrentQueue<(LoopbackFileTransferTransport Target, FileTransferDataFrameV2 Frame)>();
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, ct) =>
        {
            if (!delayedChunkGate.Task.IsCompleted && frame is FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3)
            {
                delayedFrames.Enqueue((target, frame));
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var logStart = GetOperationalLogLength();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-nkn-conservative-repair.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferRepairRequestFrameV3>().Any(), timeoutMs: 15000);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferRequestChunksFrameV2);
        await WaitUntilAsync(() =>
        {
            var tail = ReadOperationalLogTail(logStart);
            return tail.Contains("updated_profile=healthy_limited", StringComparison.Ordinal) &&
                   tail.Contains("reason=repair", StringComparison.Ordinal);
        }, timeoutMs: 8000);
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        var maxGrantWindowBytesBeforeRelease = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes).DefaultIfEmpty(0).Max();
        Assert.True(maxGrantWindowBytesBeforeRelease < 2 * 1024 * 1024, $"Expected conservative NKN startup under repair churn to stay below the old 2 MiB healthy window, but saw {maxGrantWindowBytesBeforeRelease} bytes.");
        delayedChunkGate.TrySetResult(true);
        while (delayedFrames.TryDequeue(out var delayed))
        {
            delayed.Target.ReceiveDeliveredDataFrame(delayed.Frame);
        }

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 25000);
        Assert.Equal(payload, destination.ToArray());
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferRequestChunksFrameV2);
    }

    [Fact]
    public async Task PullSession_V3Streaming_ExpandsHealthyGrantWindow_AfterSustainedProgress()
    {
        const string transferId = "transfer_service_pull_v3_tuned_expand";
        var payload = Enumerable.Range(0, 8 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_expand")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_expand")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3)
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
            }

            return false;
        };
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-tuned-expand.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 30000);
        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        Assert.Equal(40 * 1024, manifest.ChunkSizeBytes);
        var maxGrantWindowBytes = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes).DefaultIfEmpty(0).Max();
        Assert.True(maxGrantWindowBytes >= (4 * 1024 * 1024) - manifest.ChunkSizeBytes, $"Expected V3 healthy flow to step up near 4 MiB, but saw {maxGrantWindowBytes} bytes.");
    }

    [Fact]
    public async Task PullSession_V3Streaming_WithScreenshare_UsesBalancedChunkAndGrantTargets()
    {
        const string transferId = "transfer_service_pull_v3_tuned_screenshare";
        var payload = Enumerable.Range(0, 1_500_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_screenshare")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_screenshare")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        sender.SetSessionScreenShareActive(true);
        receiver.SetSessionScreenShareActive(true);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-tuned-screenshare.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        Assert.Equal(24 * 1024, manifest.ChunkSizeBytes);
        var maxGrantWindowBytes = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes).DefaultIfEmpty(0).Max();
        Assert.True(maxGrantWindowBytes >= (256 * 1024) - manifest.ChunkSizeBytes, $"Expected balanced screenshare V3 grant window near 256 KiB, but saw {maxGrantWindowBytes} bytes.");
        Assert.True(maxGrantWindowBytes < 2 * 1024 * 1024, "Expected screenshare-balanced V3 window to stay below the healthy 2 MiB target.");
    }

    [Fact]
    public async Task PullSession_V3Streaming_WhenScreenshareActivatesMidTransfer_ForcesReducedGrantWindow()
    {
        const string transferId = "transfer_service_pull_v3_midstream_screenshare_clamp";
        var payload = Enumerable.Range(0, 5 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_midstream_screenshare_clamp")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_midstream_screenshare_clamp")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3)
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
            }

            return false;
        };
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-midstream-screenshare-clamp.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Any(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * 40 * 1024 >= (2 * 1024 * 1024) - (40 * 1024)), timeoutMs: 10000);
        var grantCountBeforeScreenshare = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Count();
        sender.SetSessionScreenShareActive(true);
        receiver.SetSessionScreenShareActive(true);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Skip(grantCountBeforeScreenshare).Any(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * 40 * 1024 <= 256 * 1024), timeoutMs: 10000);
        var reducedGrant = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Skip(grantCountBeforeScreenshare).First(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * 40 * 1024 <= 256 * 1024);
        var reducedGrantBytes = (reducedGrant.GrantedUntilChunkIndexExclusive - reducedGrant.NextExpectedChunkIndex) * 40 * 1024;
        Assert.True(reducedGrantBytes <= 256 * 1024, $"Expected a forced reduced grant at or below 256 KiB, but saw {reducedGrantBytes} bytes.");
    }

    [Fact]
    public async Task PullSession_V3Streaming_WhenDegraded_UsesReducedChunkTarget()
    {
        const string transferId = "transfer_service_pull_v3_tuned_degraded";
        var payload = Enumerable.Range(0, 900_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_degraded")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_degraded")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        sender.SetSessionScreenShareDegraded(true);
        receiver.SetSessionScreenShareDegraded(true);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-tuned-degraded.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        Assert.Equal(20 * 1024, manifest.ChunkSizeBytes);
    }

    [Fact]
    public async Task PullSession_V3Streaming_BatchedChunks_AreNotResentIndividually()
    {
        const string transferId = "transfer_service_pull_v3_batched_no_duplicates";
        const int chunkSizeBytes = 4096;
        var payload = Enumerable.Range(0, chunkSizeBytes * 12).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_batched_no_duplicates")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_batched_no_duplicates")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-batch-no-duplicates.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 15000);
        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        var batches = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV3>().ToList();
        Assert.Contains(batches, static batch => batch.DataSegments.Count > 2);
        var sentChunkIndices = senderTransport.SentDataFrames.Where(static frame => frame is FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3).SelectMany(frame => frame switch
        {
            FileTransferChunkDataFrameV3 chunk => [chunk.ChunkIndex],
            FileTransferChunkBatchFrameV3 batch => Enumerable.Range(batch.StartChunkIndex, batch.DataSegments.Count),
            _ => Enumerable.Empty<int>(),
        }).OrderBy(static chunkIndex => chunkIndex).ToList();
        Assert.Equal(manifest.ChunkCount, sentChunkIndices.Count);
        Assert.Equal(sentChunkIndices.Distinct().Count(), sentChunkIndices.Count);
    }

    [Fact]
    public async Task PullSession_V3Streaming_ReorderBurst_ClampsExpandedWindowToHealthyLimitedProfile()
    {
        const string transferId = "transfer_service_pull_v3_reorder_limited";
        var sessionId = "session_service_pull_v3_reorder_limited_" + Guid.NewGuid().ToString("N");
        var payload = Enumerable.Range(0, 9 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);
        bool OverlapsDelayedReorderRange(FileTransferDataFrameV2 frame)
            => frame switch
            {
                FileTransferChunkDataFrameV3 chunk when chunk.TransferId == transferId => chunk.ChunkIndex is >= 90 and < 190,
                FileTransferChunkBatchFrameV3 batch when batch.TransferId == transferId => batch.StartChunkIndex < 190 && batch.StartChunkIndex + batch.DataSegments.Count > 90,
                _ => false,
            };

        var delayedFrames = new ConcurrentQueue<(LoopbackFileTransferTransport Target, FileTransferDataFrameV2 Frame)>();
        var releaseStarted = 0;
        var released = 0;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, ct) =>
        {
            if (Volatile.Read(ref released) == 0 && OverlapsDelayedReorderRange(frame))
            {
                delayedFrames.Enqueue((target, frame));
                if (Interlocked.Exchange(ref releaseStarted, 1) == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(1800, ct).ConfigureAwait(false);
                            while (delayedFrames.TryDequeue(out var delayed))
                            {
                                delayed.Target.ReceiveDeliveredDataFrame(delayed.Frame);
                            }

                            Volatile.Write(ref released, 1);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }, CancellationToken.None);
                }

                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-reorder-limited.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 35000);
        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        var grantWindows = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes).ToList();
        var expandedIndex = grantWindows.FindIndex(windowBytes => windowBytes >= (4 * 1024 * 1024) - manifest.ChunkSizeBytes);
        Assert.True(expandedIndex >= 0, "Expected the transfer to reach the healthy expanded 4 MiB window before the reorder clamp.");
        var grantFrames = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().ToList();
        Assert.Contains(
            grantFrames.Skip(expandedIndex + 1),
            frame => frame.NextExpectedChunkIndex >= 190 &&
                     (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes <= (512 * 1024) + manifest.ChunkSizeBytes);
        var logText = ReadRetainedOperationalLogs();
        Assert.Contains("event=filetransfer_v3_profile_changed", logText, StringComparison.Ordinal);
        Assert.Contains("updated_profile=healthy_limited", logText, StringComparison.Ordinal);
        Assert.True(
            logText.Contains("reason=high_reorder", StringComparison.Ordinal) ||
            logText.Contains("reason=severe_reorder", StringComparison.Ordinal),
            "Expected the V3 profile change log to explain the reorder-limited clamp.");
    }

    [Fact]
    public async Task PullSession_V3Streaming_NknFileOnlySparseReorder_DoesNotClampToHealthyLimitedProfile()
    {
        const string transferId = "transfer_service_pull_v3_file_only_sparse_reorder_tolerated";
        var previousPolicy = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY");
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousSoftLimitBytes = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_LIMIT_BYTES");
        var previousSoftLimitReorderThreshold = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_LIMIT_REORDER_THRESHOLD");
        var previousSoftGapStallMs = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_GAP_STALL_MS");
        var previousSoftRecoveryMs = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_RECOVERY_MS");
        var previousSparseAheadGapStallLimitMs = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_AHEAD_GAP_STALL_LIMIT_MS");
        var previousSparseCreditMode = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_MODE");
        var previousSparseCreditAccounting = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_ACCOUNTING");
        var previousSparseCreditTopupBytes = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_TOPUP_BYTES");
        var previousSparseCreditHoldMs = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_HOLD_MS");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", "SparseTolerant");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Current");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_LIMIT_BYTES", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_LIMIT_REORDER_THRESHOLD", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_GAP_STALL_MS", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_RECOVERY_MS", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_AHEAD_GAP_STALL_LIMIT_MS", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_MODE", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_ACCOUNTING", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_TOPUP_BYTES", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_HOLD_MS", null);

        try
        {
            var logStart = GetOperationalLogLength();
            var sessionId = "session_service_pull_v3_file_only_sparse_reorder_tolerated_" + Guid.NewGuid().ToString("N");
            var payload = Enumerable.Range(0, 32 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId)
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);

            bool OverlapsDelayedReorderRange(FileTransferDataFrameV2 frame)
                => frame switch
                {
                    FileTransferChunkDataFrameV3 chunk when chunk.TransferId == transferId => chunk.ChunkIndex is >= 1000 and < 1008,
                    FileTransferChunkBatchFrameV3 batch when batch.TransferId == transferId => batch.StartChunkIndex < 1008 && batch.StartChunkIndex + batch.DataSegments.Count > 1000,
                    _ => false,
                };

            var delayedFrames = new ConcurrentQueue<(LoopbackFileTransferTransport Target, FileTransferDataFrameV2 Frame)>();
            var releaseStarted = 0;
            var released = 0;
            senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, ct) =>
            {
                if (Volatile.Read(ref released) == 0 && OverlapsDelayedReorderRange(frame))
                {
                    delayedFrames.Enqueue((target, frame));
                    if (Interlocked.Exchange(ref releaseStarted, 1) == 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(600, ct).ConfigureAwait(false);
                                while (delayedFrames.TryDequeue(out var delayed))
                                {
                                    delayed.Target.ReceiveDeliveredDataFrame(delayed.Frame);
                                }

                                Volatile.Write(ref released, 1);
                            }
                            catch (OperationCanceledException)
                            {
                            }
                        }, CancellationToken.None);
                    }

                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            };

            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-file-only-sparse-reorder.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 35000);

            Assert.Equal(payload, destination.ToArray());
            var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
            var grantFrames = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().ToList();
            Assert.DoesNotContain(
                grantFrames,
                frame => frame.NextExpectedChunkIndex >= 990 &&
                         frame.NextExpectedChunkIndex <= 1100 &&
                         (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes <= (512 * 1024) + manifest.ChunkSizeBytes);
            Assert.Contains(
                grantFrames,
                frame => frame.NextExpectedChunkIndex >= 990 &&
                         frame.NextExpectedChunkIndex <= 1100 &&
                         (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes >= (2 * 1024 * 1024) - manifest.ChunkSizeBytes);

            var logText = ReadOperationalLogTail(logStart);
            Assert.Contains("grant_base_reason=sparse_ahead", logText, StringComparison.Ordinal);
            Assert.Contains("credit_base_reason=sparse_base", logText, StringComparison.Ordinal);
            Assert.Contains("target_base_reason=sparse_ahead", logText, StringComparison.Ordinal);
            Assert.Contains("sparse_credit_mode=Dominant", logText, StringComparison.Ordinal);
            Assert.Contains("sparse_credit_eligible=1", logText, StringComparison.Ordinal);
            Assert.Contains("sparse_credit_topup_bytes=131072", logText, StringComparison.Ordinal);
            Assert.Contains("soft_reorder_threshold=512", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("decision=soft_limited", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("decision=limited", logText, StringComparison.Ordinal);
            Assert.DoesNotContain($"transfer_id={transferId}; session_id={sessionId}; previous_profile=healthy_expanded; updated_profile=healthy_limited", logText, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", previousPolicy);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_LIMIT_BYTES", previousSoftLimitBytes);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_LIMIT_REORDER_THRESHOLD", previousSoftLimitReorderThreshold);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_GAP_STALL_MS", previousSoftGapStallMs);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_SOFT_RECOVERY_MS", previousSoftRecoveryMs);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_AHEAD_GAP_STALL_LIMIT_MS", previousSparseAheadGapStallLimitMs);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_MODE", previousSparseCreditMode);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_ACCOUNTING", previousSparseCreditAccounting);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_TOPUP_BYTES", previousSparseCreditTopupBytes);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_HOLD_MS", previousSparseCreditHoldMs);
        }
    }

    [Fact]
    public void PullSession_V3Streaming_NknFileOnlySparseLimitedProfile_RecoversAfterCleanWindow()
    {
        const string transferId = "transfer_service_pull_v3_file_only_sparse_limited_recovery";
        var previousPolicy = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY");
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousFixedWindow = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES");
        var previousProactiveRepair = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR");
        var previousLimitedRecovery = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_LIMITED_RECOVERY_MS");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", "SparseTolerant");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Current");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR", "0");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_LIMITED_RECOVERY_MS", null);

        try
        {
            using var receiver = new SessionFileTransferService();
            var contextType = typeof(SessionFileTransferService).GetNestedType("InboundTransferContext", BindingFlags.NonPublic);
            Assert.NotNull(contextType);
            var context = Activator.CreateInstance(
                contextType!,
                new FileTransferOfferV2
                {
                    SessionId = "sess_reflection_limited_recovery",
                    TransferId = transferId,
                    FileName = "limited-recovery.bin",
                    FileSizeBytes = 16 * 1024 * 1024,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV3,
                });
            Assert.NotNull(context);

            static void SetProperty(object target, string name, object? value)
            {
                var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                Assert.NotNull(property);
                property!.SetValue(target, value);
            }

            static T GetProperty<T>(object target, string name)
            {
                var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                Assert.NotNull(property);
                return (T)(property!.GetValue(target) ?? throw new InvalidOperationException($"Property {name} was null."));
            }

            SetProperty(context!, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV3);
            SetProperty(context!, "ReceiverSparseWriteActive", true);
            SetProperty(context!, "TransportProfileKind", FileTransferTransportProfileKind.ConservativeNknStartup);
            SetProperty(context!, "PullV3ConservativeStartupActive", false);
            SetProperty(context!, "PullV3ExpandedWindowActive", false);
            SetProperty(context!, "PullV3LimitedWindowActive", true);
            SetProperty(context!, "ChunkSizeBytes", 21 * 1024);
            SetProperty(context!, "ChunkCount", 1024);
            SetProperty(context!, "NextChunkIndex", 512);
            SetProperty(context!, "PullHighestReceivedChunkIndex", 700);
            SetProperty(context!, "PullLateArrivalDistance", 188);
            SetProperty(context!, "PullV3GrantedUntilExclusive", 536);

            var method = typeof(SessionFileTransferService).GetMethod("UpdateInboundV3WindowProfileLocked", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var start = DateTimeOffset.UtcNow;
            Assert.Equal("healthy_limited", method!.Invoke(receiver, new[] { context, start }));
            Assert.True(GetProperty<DateTimeOffset?>(context!, "PullV3CleanSinceUtc") is not null);

            Assert.Equal("healthy_limited", method.Invoke(receiver, new[] { context, start.AddMilliseconds(600) }));

            var updatedProfile = (string?)method.Invoke(receiver, new[] { context, start.AddMilliseconds(800) });
            Assert.Equal("healthy_expanded", updatedProfile);
            Assert.False(GetProperty<bool>(context!, "PullV3LimitedWindowActive"));
            Assert.True(GetProperty<bool>(context!, "PullV3ExpandedWindowActive"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", previousPolicy);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES", previousFixedWindow);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR", previousProactiveRepair);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_LIMITED_RECOVERY_MS", previousLimitedRecovery);
        }
    }

    [Fact]
    public void PullSession_V3Streaming_NknFileOnlySparseLimitedProfile_OldGapStaysLimited()
    {
        const string transferId = "transfer_service_pull_v3_file_only_sparse_limited_old_gap";
        var previousPolicy = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY");
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousFixedWindow = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES");
        var previousProactiveRepair = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", "SparseTolerant");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Current");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR", "0");

        try
        {
            using var receiver = new SessionFileTransferService();
            var contextType = typeof(SessionFileTransferService).GetNestedType("InboundTransferContext", BindingFlags.NonPublic);
            Assert.NotNull(contextType);
            var context = Activator.CreateInstance(
                contextType!,
                new FileTransferOfferV2
                {
                    SessionId = "sess_reflection_limited_old_gap",
                    TransferId = transferId,
                    FileName = "limited-old-gap.bin",
                    FileSizeBytes = 16 * 1024 * 1024,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV3,
                });
            Assert.NotNull(context);

            static void SetProperty(object target, string name, object? value)
            {
                var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                Assert.NotNull(property);
                property!.SetValue(target, value);
            }

            static T GetProperty<T>(object target, string name)
            {
                var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                Assert.NotNull(property);
                return (T)(property!.GetValue(target) ?? throw new InvalidOperationException($"Property {name} was null."));
            }

            var start = DateTimeOffset.UtcNow;
            SetProperty(context!, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV3);
            SetProperty(context!, "ReceiverSparseWriteActive", true);
            SetProperty(context!, "TransportProfileKind", FileTransferTransportProfileKind.ConservativeNknStartup);
            SetProperty(context!, "PullV3ConservativeStartupActive", false);
            SetProperty(context!, "PullV3ExpandedWindowActive", true);
            SetProperty(context!, "PullV3LimitedWindowActive", true);
            SetProperty(context!, "PullV3CleanSinceUtc", start.AddSeconds(-5));
            SetProperty(context!, "ChunkSizeBytes", 21 * 1024);
            SetProperty(context!, "ChunkCount", 1024);
            SetProperty(context!, "NextChunkIndex", 512);
            SetProperty(context!, "PullHighestReceivedChunkIndex", 700);
            SetProperty(context!, "PullLateArrivalDistance", 188);
            SetProperty(context!, "PullV3GrantedUntilExclusive", 536);
            SetProperty(context!, "PullV3GapStallStartChunkIndex", 512);
            SetProperty(context!, "PullV3GapStallSinceUtc", start.AddMilliseconds(-3000));

            var method = typeof(SessionFileTransferService).GetMethod("UpdateInboundV3WindowProfileLocked", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            Assert.Equal("healthy_limited", method!.Invoke(receiver, new[] { context, start }));
            Assert.True(GetProperty<bool>(context!, "PullV3LimitedWindowActive"));
            var cleanSinceProperty = context!.GetType().GetProperty("PullV3CleanSinceUtc", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(cleanSinceProperty);
            Assert.Null(cleanSinceProperty!.GetValue(context));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", previousPolicy);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES", previousFixedWindow);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR", previousProactiveRepair);
        }
    }

    [Fact]
    public void PullSession_V3Streaming_NknFileOnlySparse_ResetsStaleProactiveRepairState()
    {
        const string transferId = "transfer_service_pull_v3_file_only_sparse_stale_proactive_reset";
        var previousPolicy = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY");
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", "SparseTolerant");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Current");
        var logStart = ReadOperationalLogText().Length;

        try
        {
            using var receiver = new SessionFileTransferService();
            var contextType = typeof(SessionFileTransferService).GetNestedType("InboundTransferContext", BindingFlags.NonPublic);
            Assert.NotNull(contextType);
            var context = Activator.CreateInstance(
                contextType!,
                new FileTransferOfferV2
                {
                    SessionId = "sess_reflection_stale_proactive_reset",
                    TransferId = transferId,
                    FileName = "stale-proactive-reset.bin",
                    FileSizeBytes = 16 * 1024 * 1024,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV3,
                });
            Assert.NotNull(context);

            static void SetProperty(object target, string name, object? value)
            {
                var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                Assert.NotNull(property);
                property!.SetValue(target, value);
            }

            static object? GetPropertyValue(object target, string name)
            {
                var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                Assert.NotNull(property);
                return property!.GetValue(target);
            }

            var repairSentUtc = DateTimeOffset.UtcNow.AddMilliseconds(-900);
            SetProperty(context!, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV3);
            SetProperty(context!, "ReceiverSparseWriteActive", true);
            SetProperty(context!, "ReceiverSparseChunksWritten", new System.Collections.BitArray(1024));
            SetProperty(context!, "TransportProfileKind", FileTransferTransportProfileKind.ConservativeNknStartup);
            SetProperty(context!, "PullV3ConservativeStartupActive", false);
            SetProperty(context!, "PullV3ExpandedWindowActive", true);
            SetProperty(context!, "ChunkSizeBytes", 21 * 1024);
            SetProperty(context!, "ChunkCount", 1024);
            SetProperty(context!, "NextChunkIndex", 21);
            SetProperty(context!, "PullHighestReceivedChunkIndex", 60);
            SetProperty(context!, "PullV3LastRepairRequestSentUtc", repairSentUtc);
            SetProperty(context!, "PullV3LastProactiveFrontierRepairSentUtc", repairSentUtc);
            SetProperty(context!, "PullV3LastProactiveFrontierRepairStartChunkIndex", 20);
            SetProperty(context!, "PullV3LastProactiveFrontierRepairRequestedChunkCount", 1);
            SetProperty(context!, "PullV3LastProactiveFrontierRepairHighestReceivedChunkIndex", 60);
            SetProperty(context!, "PullV3ConsecutiveProactiveFrontierRepairCount", 2);

            var method = typeof(SessionFileTransferService).GetMethod("UpdateInboundV3WindowProfileLocked", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            _ = method!.Invoke(receiver, new object[] { context!, DateTimeOffset.UtcNow });

            Assert.Null(GetPropertyValue(context!, "PullV3LastProactiveFrontierRepairSentUtc"));
            Assert.Null(GetPropertyValue(context!, "PullV3LastRepairRequestSentUtc"));
            Assert.Equal(-1, Assert.IsType<int>(GetPropertyValue(context!, "PullV3LastProactiveFrontierRepairStartChunkIndex")));
            Assert.Equal(0, Assert.IsType<int>(GetPropertyValue(context!, "PullV3ConsecutiveProactiveFrontierRepairCount")));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_proactive_frontier_repair_state_reset", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=frontier_advanced", logTail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", previousPolicy);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
        }
    }

    [Fact]
    public void PullSession_V3Streaming_NknFileOnlySparseDefaultWindow_Uses16MiBTarget()
    {
        const string transferId = "transfer_service_pull_v3_file_only_sparse_default_window";
        var previousPolicy = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY");
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousFixedWindow = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES");
        var previousTargetWindow = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_TARGET_WINDOW_BYTES");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", "SparseTolerant");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Current");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_TARGET_WINDOW_BYTES", null);

        try
        {
            using var receiver = new SessionFileTransferService();
            var context = CreateExpandedFileOnlySparseContextForTargetWindow(transferId);
            var method = typeof(SessionFileTransferService).GetMethod("ResolveInboundV3TargetWindowChunksLocked", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var targetWindowChunks = (int)(method!.Invoke(receiver, new[] { context }) ?? 0);
            var chunkSizeBytes = GetContextProperty<int>(context, "ChunkSizeBytes");
            var targetWindowBytes = targetWindowChunks * chunkSizeBytes;
            Assert.InRange(targetWindowBytes, 16 * 1024 * 1024, (16 * 1024 * 1024) + chunkSizeBytes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", previousPolicy);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES", previousFixedWindow);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_TARGET_WINDOW_BYTES", previousTargetWindow);
        }
    }

    [Fact]
    public void PullSession_V3Streaming_NknFileOnlySparseTargetWindowEnv_Restores8MiBTarget()
    {
        const string transferId = "transfer_service_pull_v3_file_only_sparse_target_env";
        var previousPolicy = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY");
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousFixedWindow = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES");
        var previousTargetWindow = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_TARGET_WINDOW_BYTES");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", "SparseTolerant");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Current");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_TARGET_WINDOW_BYTES", (8 * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            using var receiver = new SessionFileTransferService();
            var context = CreateExpandedFileOnlySparseContextForTargetWindow(transferId);
            var method = typeof(SessionFileTransferService).GetMethod("ResolveInboundV3TargetWindowChunksLocked", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var targetWindowChunks = (int)(method!.Invoke(receiver, new[] { context }) ?? 0);
            var chunkSizeBytes = GetContextProperty<int>(context, "ChunkSizeBytes");
            var targetWindowBytes = targetWindowChunks * chunkSizeBytes;
            Assert.InRange(targetWindowBytes, 8 * 1024 * 1024, (8 * 1024 * 1024) + chunkSizeBytes);
            Assert.True(targetWindowBytes < 12 * 1024 * 1024, $"Expected configured target to remain below 16 MiB default, but saw {targetWindowBytes} bytes.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", previousPolicy);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES", previousFixedWindow);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_TARGET_WINDOW_BYTES", previousTargetWindow);
        }
    }

    private static object CreateExpandedFileOnlySparseContextForTargetWindow(string transferId)
    {
        var contextType = typeof(SessionFileTransferService).GetNestedType("InboundTransferContext", BindingFlags.NonPublic);
        Assert.NotNull(contextType);
        var context = Activator.CreateInstance(
            contextType!,
            new FileTransferOfferV2
            {
                SessionId = "sess_reflection_target_window",
                TransferId = transferId,
                FileName = "target-window.bin",
                FileSizeBytes = 64 * 1024 * 1024,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV3,
            });
        Assert.NotNull(context);

        SetContextProperty(context!, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV3);
        SetContextProperty(context!, "ReceiverSparseWriteActive", true);
        SetContextProperty(context!, "TransportProfileKind", FileTransferTransportProfileKind.ConservativeNknStartup);
        SetContextProperty(context!, "PullV3ConservativeStartupActive", false);
        SetContextProperty(context!, "PullV3ExpandedWindowActive", true);
        SetContextProperty(context!, "PullV3LimitedWindowActive", false);
        SetContextProperty(context!, "PullV3FileOnlySoftLimitedWindowActive", false);
        SetContextProperty(context!, "ChunkSizeBytes", 21 * 1024);
        SetContextProperty(context!, "ChunkCount", 4096);
        SetContextProperty(context!, "NextChunkIndex", 1024);
        SetContextProperty(context!, "PullHighestReceivedChunkIndex", 1024);
        SetContextProperty(context!, "PullV3GrantedUntilExclusive", 1200);
        return context!;
    }

    private static void SetContextProperty(object target, string name, object? value)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        property!.SetValue(target, value);
    }

    private static T GetContextProperty<T>(object target, string name)
    {
        var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return (T)(property!.GetValue(target) ?? throw new InvalidOperationException($"Property {name} was null."));
    }

    [Fact]
    public async Task PullSession_V3Streaming_FixedFileOnlyWindowDiagnostic_UsesConfiguredWindow()
    {
        const string transferId = "transfer_service_pull_v3_fixed_file_only_window";
        var previousPolicy = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY");
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousFixedWindow = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", "SparseTolerant");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Current");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES", (8 * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            var logStart = GetOperationalLogLength();
            var sessionId = "session_service_pull_v3_fixed_file_only_window_" + Guid.NewGuid().ToString("N");
            var payload = Enumerable.Range(0, 16 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId)
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);

            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-fixed-file-only-window.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 30000);

            Assert.Equal(payload, destination.ToArray());
            var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
            var maxGrantWindowBytes = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>()
                .Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes)
                .DefaultIfEmpty(0)
                .Max();
            Assert.True(maxGrantWindowBytes >= (8 * 1024 * 1024) - manifest.ChunkSizeBytes, $"Expected fixed diagnostic grant window near 8 MiB, but saw {maxGrantWindowBytes} bytes.");
            var logText = ReadOperationalLogTail(logStart);
            Assert.Contains("fixed_file_only_window_active=1", logText, StringComparison.Ordinal);
            Assert.Contains("fixed_file_only_window_bytes=8388608", logText, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", previousPolicy);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES", previousFixedWindow);
        }
    }

    [Fact]
    public async Task PullSession_V3Streaming_NknFileOnlySparseCreditRollback_UsesContiguousCreditBase()
    {
        const string transferId = "transfer_service_pull_v3_sparse_credit_rollback";
        var previousPolicy = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY");
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        var previousSparseCreditMode = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_MODE");
        var previousSparseCreditAccounting = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_ACCOUNTING");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", "SparseTolerant");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Current");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_MODE", "Current");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_ACCOUNTING", "ContiguousFrontier");

        try
        {
            var logStart = GetOperationalLogLength();
            var sessionId = "session_service_pull_v3_sparse_credit_rollback_" + Guid.NewGuid().ToString("N");
            var payload = Enumerable.Range(0, 12 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId)
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);

            bool ShouldDelay(FileTransferDataFrameV2 frame)
                => frame switch
                {
                    FileTransferChunkDataFrameV3 chunk when chunk.TransferId == transferId => chunk.ChunkIndex is >= 220 and < 228,
                    FileTransferChunkBatchFrameV3 batch when batch.TransferId == transferId => batch.StartChunkIndex < 228 && batch.StartChunkIndex + batch.DataSegments.Count > 220,
                    _ => false,
                };

            var delayedFrames = new ConcurrentQueue<(LoopbackFileTransferTransport Target, FileTransferDataFrameV2 Frame)>();
            var releaseStarted = 0;
            var released = 0;
            senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, ct) =>
            {
                if (Volatile.Read(ref released) == 0 && ShouldDelay(frame))
                {
                    delayedFrames.Enqueue((target, frame));
                    if (Interlocked.Exchange(ref releaseStarted, 1) == 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(500, ct).ConfigureAwait(false);
                                while (delayedFrames.TryDequeue(out var delayed))
                                {
                                    delayed.Target.ReceiveDeliveredDataFrame(delayed.Frame);
                                }

                                Volatile.Write(ref released, 1);
                            }
                            catch (OperationCanceledException)
                            {
                            }
                        }, CancellationToken.None);
                    }

                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            };

            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-sparse-credit-rollback.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 25000);

            Assert.Equal(payload, destination.ToArray());
            var logText = ReadOperationalLogTail(logStart);
            Assert.Contains("grant_base_reason=sparse_ahead", logText, StringComparison.Ordinal);
            Assert.Contains("credit_base_reason=contiguous_frontier", logText, StringComparison.Ordinal);
            Assert.Contains("sparse_credit_mode=Current", logText, StringComparison.Ordinal);
            Assert.Contains("sparse_credit_block_reason=accounting_disabled", logText, StringComparison.Ordinal);
            Assert.DoesNotContain("credit_base_reason=sparse_base", logText, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", previousPolicy);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_MODE", previousSparseCreditMode);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_SPARSE_CREDIT_ACCOUNTING", previousSparseCreditAccounting);
        }
    }

    [Fact(Skip = "Obsolete internal send-ahead clamp coverage after file-transfer pipeline refactors.")]
    public async Task SenderSendAheadClamp_LimitsSequentialBurst_BeforeRemoteAckAdvances()
    {
        const string transferId = "transfer_service_send_ahead_clamp";
        const int chunkSizeBytes = 1024;
        var payload = Enumerable.Range(0, chunkSizeBytes * 300).Select(static i => (byte)(i % 251)).ToArray();
        var sentChunkCount = 0;
        var highestSentChunkIndex = -1;
        var logStartIndex = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_send_ahead_clamp")
        {
            OutboundChunkDeliveryOverrideAsync = (_, message, _) =>
            {
                Interlocked.Increment(ref sentChunkCount);
                var currentHighest = Volatile.Read(ref highestSentChunkIndex);
                while (message.ChunkIndex > currentHighest)
                {
                    var observed = Interlocked.CompareExchange(ref highestSentChunkIndex, message.ChunkIndex, currentHighest);
                    if (observed == currentHighest)
                    {
                        break;
                    }

                    currentHighest = observed;
                }

                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_send_ahead_clamp");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("send-ahead-clamp.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()), CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref sentChunkCount) >= 32, timeoutMs: 5000);
        await Task.Delay(400);
        Assert.Equal(32, Volatile.Read(ref sentChunkCount));
        Assert.Equal(31, Volatile.Read(ref highestSentChunkIndex));
        var logTail = ReadOperationalLogTail(logStartIndex);
        Assert.Contains("event=window_send_ahead_clamped", logTail, StringComparison.Ordinal);
        Assert.Contains("effective_send_limit=32", logTail, StringComparison.Ordinal);
        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Canceled && receiver.Snapshot.InboundState == FileTransferTransferState.Canceled);
    }

    [Fact]
    public async Task ScreenshareActiveStartupGrant_IsCappedToScreenshareTarget()
    {
        const string transferId = "transfer_service_screenshare_startup_cap";
        const int chunkSizeBytes = 4096;
        var payload = Enumerable.Range(0, chunkSizeBytes * 200).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_screenshare_startup_cap");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_screenshare_startup_cap");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        sender.SetSessionScreenShareActive(true);
        receiver.SetSessionScreenShareActive(true);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("screenshare-startup-cap.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Any(static frame => frame.NextExpectedChunkIndex == 0), timeoutMs: 5000);
        var firstGrant = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().First(static frame => frame.NextExpectedChunkIndex == 0);
        var grantedChunks = firstGrant.GrantedUntilChunkIndexExclusive - firstGrant.NextExpectedChunkIndex;
        Assert.InRange(grantedChunks * chunkSizeBytes, 1, 256 * 1024);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferRequestChunksFrameV2);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Completed && receiver.Snapshot.InboundState == FileTransferTransferState.Completed, timeoutMs: 12000);
    }

    [Fact]
    public async Task PullSession_ActiveScreenshare_UsesTwentyFourKilobyteChunks_AndThreeChunkPipeline()
    {
        const string transferId = "transfer_service_screenshare_active_profile";
        var payload = Enumerable.Range(0, 24576 * 12).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_screenshare_active_profile");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_screenshare_active_profile");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        sender.SetSessionScreenShareActive(true);
        receiver.SetSessionScreenShareActive(true);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("screenshare-active-profile.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>().Any() &&
                  receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Any(),
            timeoutMs: 5000);
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        var sessionOpen = Assert.Single(senderTransport.SentSessionOpens);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV3, sessionOpen.ProtocolVersion);
        Assert.Equal(3, sessionOpen.InitialPipelineDepth);
        Assert.Equal(24576, manifest.ChunkSizeBytes);
        var firstGrant = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().First();
        var grantWindowBytes = (firstGrant.GrantedUntilChunkIndexExclusive - firstGrant.NextExpectedChunkIndex) * manifest.ChunkSizeBytes;
        Assert.InRange(grantWindowBytes, 1, 256 * 1024 + manifest.ChunkSizeBytes);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferRequestChunksFrameV2);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Completed && receiver.Snapshot.InboundState == FileTransferTransferState.Completed, timeoutMs: 12000);
    }

    [Fact(Skip = "Legacy chunk-read implementation detail coverage no longer matches the current sender pipeline.")]
    public async Task RoundTrip_DoesNotReadPastAdvertisedChunkSize_WhenArrayPoolReturnsLargerBuffer()
    {
        const string transferId = "transfer_service_chunk_size_cap";
        var payload = Enumerable.Range(0, 49_153).Select(static i => (byte)(i % 251)).ToArray();
        var observedChunkSizes = new List<int>();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_chunk_size_cap")
        {
            OutboundChunkTransform = message =>
            {
                observedChunkSizes.Add(Convert.FromBase64String(message.DataBase64).Length);
                return message;
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_chunk_size_cap");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();
        var started = await sender.TryStartSendAsync(new FileTransferSendDescriptor("pooled.bin", payload.Length, transferId, ChunkSizeBytes: 24_576), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed);
        Assert.Equal([24_576, 24_576, 1], observedChunkSizes);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact(Skip = "Obsolete gap-frontier grant-growth coverage after file-transfer pipeline refactors.")]
    public async Task OpenGap_DoesNotExtendGrantFromBufferedFrontierUntilContiguousProgressAdvances()
    {
        const string transferId = "transfer_service_gap_grant_suppression";
        const int chunkSizeBytes = 1024;
        const int droppedChunkIndex = 129;
        const int initialSteadyStateGrant = 321;
        var payload = Enumerable.Range(0, chunkSizeBytes * 700).Select(static i => (byte)(i % 251)).ToArray();
        var initialDrop = 0;
        var allowRepairDelivery = 0;
        var repairChunkHeld = 0;
        var logStartIndex = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_gap_grant_suppression")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.ChunkIndex == droppedChunkIndex && Interlocked.CompareExchange(ref initialDrop, 1, 0) == 0)
                {
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex == droppedChunkIndex && Volatile.Read(ref allowRepairDelivery) == 0)
                {
                    Interlocked.Exchange(ref repairChunkHeld, 1);
                    return Task.FromResult(true);
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_gap_grant_suppression");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("gap-grant-suppression.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentMissingRanges.Any(static range => range.StartChunkIndex == droppedChunkIndex), timeoutMs: 5000);
        await WaitUntilAsync(() => Volatile.Read(ref repairChunkHeld) != 0, timeoutMs: 5000);
        Assert.All(receiverTransport.SentWindowUpdates.Where(update => update.NextExpectedChunkIndex == droppedChunkIndex), update => Assert.True(update.GrantedUntilChunkIndexExclusive <= initialSteadyStateGrant));
        var deferredLogTail = ReadOperationalLogTail(logStartIndex);
        Assert.Contains("event=window_extension_deferred_due_to_gap", deferredLogTail, StringComparison.Ordinal);
        Interlocked.Exchange(ref allowRepairDelivery, 1);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact(Skip = "Obsolete gap-progress ack coverage after file-transfer pipeline refactors.")]
    public async Task OpenGap_AdvancingContiguousProgress_EmitsGapProgressAckWithoutGrantGrowth()
    {
        const string transferId = "transfer_service_gap_progress_ack";
        const int chunkSizeBytes = 1024;
        var droppedChunkIndices = new HashSet<int>
        {
            129,
            130,
            131
        };
        var droppedSequentialChunks = new HashSet<int>();
        var allowFinalRepairChunk = 0;
        var logStartIndex = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_gap_progress_ack")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (droppedChunkIndices.Contains(message.ChunkIndex) && droppedSequentialChunks.Add(message.ChunkIndex))
                {
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex == 131 && Volatile.Read(ref allowFinalRepairChunk) == 0)
                {
                    return Task.FromResult(true);
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_gap_progress_ack");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var payload = Enumerable.Range(0, chunkSizeBytes * 700).Select(static i => (byte)(i % 251)).ToArray();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("gap-progress-ack.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentWindowUpdates.Any(update => update.NextExpectedChunkIndex == 129), timeoutMs: 5000);
        var deferredGrant = receiverTransport.SentWindowUpdates.Where(update => update.NextExpectedChunkIndex == 129).Select(update => update.GrantedUntilChunkIndexExclusive).Last();
        await WaitUntilAsync(() => receiverTransport.SentWindowUpdates.Any(update => update.NextExpectedChunkIndex >= 131 && update.GrantedUntilChunkIndexExclusive == deferredGrant), timeoutMs: 5000);
        var logTail = ReadOperationalLogTail(logStartIndex);
        Assert.Contains("reason=gap_progress_ack", logTail, StringComparison.Ordinal);
        Interlocked.Exchange(ref allowFinalRepairChunk, 1);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact(Skip = "Obsolete repair batching coverage after file-transfer pipeline refactors.")]
    public async Task MissingRange_RepairMode_SendsTwoChunkBatchBeforeWaitingForAck()
    {
        const string transferId = "transfer_service_repair_batch";
        const int chunkSizeBytes = 1024;
        var droppedChunkIndices = new HashSet<int>
        {
            129,
            130,
            131,
            132
        };
        var initiallyDropped = new HashSet<int>();
        var missingRangeObserved = 0;
        var repairChunkIndices = new List<int>();
        var repairGate = new object ();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_repair_batch")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (droppedChunkIndices.Contains(message.ChunkIndex) && initiallyDropped.Add(message.ChunkIndex))
                {
                    return Task.FromResult(true);
                }

                if (Volatile.Read(ref missingRangeObserved) != 0 && message.ChunkIndex >= 129 && message.ChunkIndex <= 132)
                {
                    lock (repairGate)
                    {
                        repairChunkIndices.Add(message.ChunkIndex);
                    }

                    if (message.ChunkIndex <= 130)
                    {
                        return Task.FromResult(true);
                    }
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_repair_batch")
        {
            OutboundMissingRangeDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.StartChunkIndex == 129)
                {
                    Interlocked.Exchange(ref missingRangeObserved, 1);
                }

                target.ReceiveDeliveredMissingRange(message);
                return Task.FromResult(true);
            },
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var payload = Enumerable.Range(0, chunkSizeBytes * 700).Select(static i => (byte)(i % 251)).ToArray();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("repair-batch.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref missingRangeObserved) != 0, timeoutMs: 5000);
        await Task.Delay(200);
        int[] repairSnapshot;
        lock (repairGate)
        {
            repairSnapshot = repairChunkIndices.ToArray();
        }

        Assert.Equal(1, repairSnapshot.Count(index => index == 129));
        Assert.Equal(1, repairSnapshot.Count(index => index == 130));
        Assert.DoesNotContain(131, repairSnapshot);
        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
    }

    [Fact(Skip = "Obsolete degraded-repair growth-cap coverage after file-transfer pipeline refactors.")]
    public async Task PersistentGap_EntersDegradedRepairMode_AndCapsFutureGrantGrowth()
    {
        const string transferId = "transfer_service_degraded_repair_cap";
        const int chunkSizeBytes = 1024;
        const int droppedChunkIndex = 129;
        var initialDrop = 0;
        var allowRepairDelivery = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_service_degraded_repair_cap")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.ChunkIndex == droppedChunkIndex && Interlocked.CompareExchange(ref initialDrop, 1, 0) == 0)
                {
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex == droppedChunkIndex && Volatile.Read(ref allowRepairDelivery) == 0)
                {
                    return Task.FromResult(true);
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_degraded_repair_cap");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var payload = Enumerable.Range(0, chunkSizeBytes * 700).Select(static i => (byte)(i % 251)).ToArray();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("degraded-repair-cap.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentMissingRanges.Count(static range => range.StartChunkIndex == droppedChunkIndex) >= 3, timeoutMs: 5000);
        Interlocked.Exchange(ref allowRepairDelivery, 1);
        await WaitUntilAsync(() => receiverTransport.SentWindowUpdates.Any(update => update.NextExpectedChunkIndex >= droppedChunkIndex && update.GrantedUntilChunkIndexExclusive - update.NextExpectedChunkIndex <= 32), timeoutMs: 10000);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
        Assert.Equal(payload, destination.ToArray());
    }

}
