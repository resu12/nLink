using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using Xunit;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class DevLocalFileTransferSecurityBoundaryTests : CoreSmokeTestsBase
{
    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task DevLocalTransport_FileTransfer_RoundTrip_UsesTypedEventsAndStateTransitions()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        using DevLocalTransport host = new DevLocalTransport(hostAddress);
        using DevLocalTransport helper = new DevLocalTransport();
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        IncomingJoinRequestEventArgs pendingJoin = null;
        TaskCompletionSource joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<FileTransferOfferV1> offerReceived = new TaskCompletionSource<FileTransferOfferV1>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<FileTransferStartV1> startReceived = new TaskCompletionSource<FileTransferStartV1>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<FileTransferChunkV1> chunkReceived = new TaskCompletionSource<FileTransferChunkV1>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<FileTransferCompleteV1> completeReceived = new TaskCompletionSource<FileTransferCompleteV1>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.IncomingJoinRequest += delegate (object? _, IncomingJoinRequestEventArgs e)
        {
            pendingJoin = e;
            joinRaised.TrySetResult();
        };
        host.Approved += delegate
        {
            hostApproved.TrySetResult();
        };
        helper.Approved += delegate
        {
            helperApproved.TrySetResult();
        };
        host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
        {
            offerReceived.TrySetResult(e.Message);
        };
        helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
        {
            acceptReceived.TrySetResult(e.Message);
        };
        host.FileTransferStartReceived += delegate (object? _, FileTransferStartReceivedEventArgs e)
        {
            startReceived.TrySetResult(e.Message);
        };
        host.FileTransferChunkReceived += delegate (object? _, FileTransferChunkReceivedEventArgs e)
        {
            chunkReceived.TrySetResult(e.Message);
        };
        helper.FileTransferCompleteReceived += delegate (object? _, FileTransferCompleteReceivedEventArgs e)
        {
            completeReceived.TrySetResult(e.Message);
        };
        host.HostByAddressAsync(cts.Token);
        await host.WaitUntilHostReadyAsync(cts.Token);
        await helper.JoinByInviteAsync(invite: CoreSmokeTestsBase.CreateValidatedInviteForTarget(new PeerAddress(hostAddress), out string rawToken, InviteCapabilities.Chat | InviteCapabilities.FileTransfer), inviteToken: rawToken, ct: cts.Token);
        await joinRaised.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
        await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        string sessionId = host.CurrentSessionSecurityState.SessionId.Value.Value;
        string expectedHash = Convert.ToBase64String(SHA256.HashData(new byte[3] { 1, 2, 3 }));
        await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_devlocal_roundtrip", FileName = "hello.bin", FileSizeBytes = 3L, Sha256Base64 = expectedHash }, cts.Token);
        FileTransferOfferV1 offer = await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        Assert.Equal("transfer_devlocal_roundtrip", offer.TransferId);
        Assert.Equal("hello.bin", offer.FileName);
        await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = "transfer_devlocal_roundtrip" }, cts.Token);
        Assert.Equal("transfer_devlocal_roundtrip", (await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token)).TransferId);
        await helper.SendFileTransferStartAsync(new FileTransferStartV1 { SessionId = sessionId, TransferId = "transfer_devlocal_roundtrip", FileName = "hello.bin", FileSizeBytes = 3L, Sha256Base64 = expectedHash, ChunkCount = 1, ChunkSizeBytes = 3 }, cts.Token);
        Assert.Equal(1, (await startReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token)).ChunkCount);
        await helper.SendFileTransferChunkAsync(new FileTransferChunkV1 { SessionId = sessionId, TransferId = "transfer_devlocal_roundtrip", ChunkIndex = 0, ChunkCount = 1, DataBase64 = Convert.ToBase64String(new byte[3] { 1, 2, 3 }) }, cts.Token);
        FileTransferChunkV1 chunk = await chunkReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        Assert.Equal("transfer_devlocal_roundtrip", chunk.TransferId);
        Assert.Equal(0, chunk.ChunkIndex);
        await host.SendFileTransferCompleteAsync(new FileTransferCompleteV1 { SessionId = sessionId, TransferId = "transfer_devlocal_roundtrip", FileSizeBytes = 3L, Sha256Base64 = expectedHash }, cts.Token);
        Assert.Equal(expectedHash, (await completeReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token)).Sha256Base64);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task DevLocalTransport_FileTransferOffer_WhileSameDirectionTransferIsActive_IsRejectedAsBusy()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        using DevLocalTransport host = new DevLocalTransport(hostAddress);
        using DevLocalTransport helper = new DevLocalTransport();
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        IncomingJoinRequestEventArgs pendingJoin = null;
        TaskCompletionSource joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<FileTransferOfferV1> firstOfferReceived = new TaskCompletionSource<FileTransferOfferV1>(TaskCreationOptions.RunContinuationsAsynchronously);
        int offerCount = 0;
        host.IncomingJoinRequest += delegate (object? _, IncomingJoinRequestEventArgs e)
        {
            pendingJoin = e;
            joinRaised.TrySetResult();
        };
        host.Approved += delegate
        {
            hostApproved.TrySetResult();
        };
        helper.Approved += delegate
        {
            helperApproved.TrySetResult();
        };
        host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
        {
            Interlocked.Increment(ref offerCount);
            firstOfferReceived.TrySetResult(e.Message);
        };
        host.HostByAddressAsync(cts.Token);
        await host.WaitUntilHostReadyAsync(cts.Token);
        await helper.JoinByInviteAsync(invite: CoreSmokeTestsBase.CreateValidatedInviteForTarget(new PeerAddress(hostAddress), out string rawToken, InviteCapabilities.Chat | InviteCapabilities.FileTransfer), inviteToken: rawToken, ct: cts.Token);
        await joinRaised.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
        await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        string sessionId = host.CurrentSessionSecurityState.SessionId.Value.Value;
        string expectedHash = Convert.ToBase64String(new byte[32]);
        await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_devlocal_busy_first", FileName = "first.bin", FileSizeBytes = 1L, Sha256Base64 = expectedHash }, cts.Token);
        Assert.Equal("transfer_devlocal_busy_first", (await firstOfferReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token)).TransferId);
        byte[] securePayload = CoreSmokeTestsBase.BuildSecureDevLocalPayload(helper, SessionSecureMessageFamily.FileTransfer, "file_transfer_offer", FileTransferPayloadCodec.Serialize(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_devlocal_busy_second", FileName = "second.bin", FileSizeBytes = 1L, Sha256Base64 = expectedHash }), "transfer_devlocal_busy_second", 2L);
        await CoreSmokeTestsBase.SendRawDevLocalFrameAsync(helper, "file_transfer_offer", securePayload, cts.Token);
        await Task.Delay(200, cts.Token);
        Assert.Equal(1, Volatile.Read(in offerCount));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task DevLocalTransport_FileTransferChunk_BeforeStart_IsRejected()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        using DevLocalTransport host = new DevLocalTransport(hostAddress);
        using DevLocalTransport helper = new DevLocalTransport();
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        IncomingJoinRequestEventArgs pendingJoin = null;
        TaskCompletionSource joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource offerReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int chunkCount = 0;
        host.IncomingJoinRequest += delegate (object? _, IncomingJoinRequestEventArgs e)
        {
            pendingJoin = e;
            joinRaised.TrySetResult();
        };
        host.Approved += delegate
        {
            hostApproved.TrySetResult();
        };
        helper.Approved += delegate
        {
            helperApproved.TrySetResult();
        };
        host.FileTransferOfferReceived += delegate
        {
            offerReceived.TrySetResult();
        };
        host.FileTransferChunkReceived += delegate
        {
            Interlocked.Increment(ref chunkCount);
        };
        host.HostByAddressAsync(cts.Token);
        await host.WaitUntilHostReadyAsync(cts.Token);
        await helper.JoinByInviteAsync(invite: CoreSmokeTestsBase.CreateValidatedInviteForTarget(new PeerAddress(hostAddress), out string rawToken, InviteCapabilities.Chat | InviteCapabilities.FileTransfer), inviteToken: rawToken, ct: cts.Token);
        await joinRaised.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
        await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        string sessionId = host.CurrentSessionSecurityState.SessionId.Value.Value;
        string expectedHash = Convert.ToBase64String(new byte[32]);
        await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_devlocal_out_of_state", FileName = "state.bin", FileSizeBytes = 1L, Sha256Base64 = expectedHash }, cts.Token);
        await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        byte[] securePayload = CoreSmokeTestsBase.BuildSecureDevLocalPayload(helper, SessionSecureMessageFamily.FileTransfer, "file_transfer_chunk", FileTransferPayloadCodec.Serialize(new FileTransferChunkV1 { SessionId = sessionId, TransferId = "transfer_devlocal_out_of_state", ChunkIndex = 0, ChunkCount = 1, DataBase64 = Convert.ToBase64String(new byte[1] { 122 }) }), "transfer_devlocal_out_of_state", 2L);
        await CoreSmokeTestsBase.SendRawDevLocalFrameAsync(helper, "file_transfer_chunk", securePayload, cts.Token);
        await Task.Delay(200, cts.Token);
        Assert.Equal(0, Volatile.Read(in chunkCount));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task DevLocalTransport_FileTransferDataFrame_UnknownTransferId_IsRejectedWithoutAllocatingSession()
    {
        int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        using DevLocalTransport host = new DevLocalTransport(hostAddress);
        using DevLocalTransport helper = new DevLocalTransport();
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        IncomingJoinRequestEventArgs pendingJoin = null;
        TaskCompletionSource joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.IncomingJoinRequest += delegate (object? _, IncomingJoinRequestEventArgs e)
        {
            pendingJoin = e;
            joinRaised.TrySetResult();
        };
        host.Approved += delegate
        {
            hostApproved.TrySetResult();
        };
        helper.Approved += delegate
        {
            helperApproved.TrySetResult();
        };
        host.HostByAddressAsync(cts.Token);
        await host.WaitUntilHostReadyAsync(cts.Token);
        await helper.JoinByInviteAsync(invite: CoreSmokeTestsBase.CreateValidatedInviteForTarget(new PeerAddress(hostAddress), out string rawToken, InviteCapabilities.Chat | InviteCapabilities.FileTransfer), inviteToken: rawToken, ct: cts.Token);
        await joinRaised.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
        await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        string sessionId = host.CurrentSessionSecurityState.SessionId.Value.Value;
        byte[] securePayload = CoreSmokeTestsBase.BuildSecureDevLocalPayload(helper, SessionSecureMessageFamily.FileTransfer, "file_transfer_data_frame", FileTransferDataFrameCodec.Serialize(new FileTransferManifestFrameV2 { SessionId = sessionId, TransferId = "transfer_devlocal_unknown_data_frame", FileName = "unknown.bin", FileSizeBytes = 16L, ChunkSizeBytes = 16, ChunkCount = 1, Sha256Base64 = Convert.ToBase64String(new byte[32]) }), "transfer_devlocal_unknown_data_frame", 1L);
        await CoreSmokeTestsBase.SendRawDevLocalFrameAsync(helper, "file_transfer_data_frame", securePayload, cts.Token);
        await Task.Delay(200, cts.Token);
        IDictionary dataSessions = Assert.IsAssignableFrom<IDictionary>(CoreSmokeTestsBase.GetPrivateField(host, "fileTransferDataSessions"));
        string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
        Assert.Equal(0, dataSessions.Count);
        Assert.Contains("event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=unknown_transfer_id;", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_data_frame_decode_failed", logTail, StringComparison.Ordinal);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task DevLocalTransport_FileTransferOffer_WithWrongSenderIdentity_IsRejected()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        using DevLocalTransport host = new DevLocalTransport(hostAddress);
        using DevLocalTransport helper = new DevLocalTransport();
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        IncomingJoinRequestEventArgs pendingJoin = null;
        TaskCompletionSource joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int offerCount = 0;
        host.IncomingJoinRequest += delegate (object? _, IncomingJoinRequestEventArgs e)
        {
            pendingJoin = e;
            joinRaised.TrySetResult();
        };
        host.Approved += delegate
        {
            hostApproved.TrySetResult();
        };
        helper.Approved += delegate
        {
            helperApproved.TrySetResult();
        };
        host.FileTransferOfferReceived += delegate
        {
            Interlocked.Increment(ref offerCount);
        };
        host.HostByAddressAsync(cts.Token);
        await host.WaitUntilHostReadyAsync(cts.Token);
        await helper.JoinByInviteAsync(invite: CoreSmokeTestsBase.CreateValidatedInviteForTarget(new PeerAddress(hostAddress), out string rawToken, InviteCapabilities.Chat | InviteCapabilities.FileTransfer), inviteToken: rawToken, ct: cts.Token);
        await joinRaised.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
        await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        string sessionId = host.CurrentSessionSecurityState.SessionId.Value.Value;
        byte[] securePayload = CoreSmokeTestsBase.BuildSecureDevLocalPayload(helper, SessionSecureMessageFamily.FileTransfer, "file_transfer_offer", FileTransferPayloadCodec.Serialize(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_devlocal_wrong_sender", FileName = "wrong.bin", FileSizeBytes = 1L, Sha256Base64 = Convert.ToBase64String(new byte[32]) }), "transfer_devlocal_wrong_sender", 1L, new PeerAddress(CoreSmokeTestsBase.CreateTestPeerAddress()));
        await CoreSmokeTestsBase.SendRawDevLocalFrameAsync(helper, "file_transfer_offer", securePayload, cts.Token);
        await Task.Delay(200, cts.Token);
        Assert.Equal(0, Volatile.Read(in offerCount));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task DevLocalTransport_FileTransferOffer_WithPlaintextSessionMismatch_IsRejected()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        using DevLocalTransport host = new DevLocalTransport(hostAddress);
        using DevLocalTransport helper = new DevLocalTransport();
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        IncomingJoinRequestEventArgs pendingJoin = null;
        TaskCompletionSource joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int offerCount = 0;
        host.IncomingJoinRequest += delegate (object? _, IncomingJoinRequestEventArgs e)
        {
            pendingJoin = e;
            joinRaised.TrySetResult();
        };
        host.Approved += delegate
        {
            hostApproved.TrySetResult();
        };
        helper.Approved += delegate
        {
            helperApproved.TrySetResult();
        };
        host.FileTransferOfferReceived += delegate
        {
            Interlocked.Increment(ref offerCount);
        };
        host.HostByAddressAsync(cts.Token);
        await host.WaitUntilHostReadyAsync(cts.Token);
        await helper.JoinByInviteAsync(invite: CoreSmokeTestsBase.CreateValidatedInviteForTarget(new PeerAddress(hostAddress), out string rawToken, InviteCapabilities.Chat | InviteCapabilities.FileTransfer), inviteToken: rawToken, ct: cts.Token);
        await joinRaised.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
        await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        byte[] securePayload = CoreSmokeTestsBase.BuildSecureDevLocalPayload(helper, SessionSecureMessageFamily.FileTransfer, "file_transfer_offer", FileTransferPayloadCodec.Serialize(new FileTransferOfferV1 { SessionId = "wrong_session", TransferId = "transfer_devlocal_wrong_session", FileName = "wrong-session.bin", FileSizeBytes = 1L, Sha256Base64 = Convert.ToBase64String(new byte[32]) }), "transfer_devlocal_wrong_session", 1L);
        await CoreSmokeTestsBase.SendRawDevLocalFrameAsync(helper, "file_transfer_offer", securePayload, cts.Token);
        await Task.Delay(200, cts.Token);
        Assert.Equal(0, Volatile.Read(in offerCount));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task DevLocalTransport_FileTransferChunk_ReplayedSecureEnvelope_IsRejected()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        using DevLocalTransport host = new DevLocalTransport(hostAddress);
        using DevLocalTransport helper = new DevLocalTransport();
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        IncomingJoinRequestEventArgs pendingJoin = null;
        TaskCompletionSource joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource offerReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource acceptReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource startReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int chunkCount = 0;
        host.IncomingJoinRequest += delegate (object? _, IncomingJoinRequestEventArgs e)
        {
            pendingJoin = e;
            joinRaised.TrySetResult();
        };
        host.Approved += delegate
        {
            hostApproved.TrySetResult();
        };
        helper.Approved += delegate
        {
            helperApproved.TrySetResult();
        };
        host.FileTransferOfferReceived += delegate
        {
            offerReceived.TrySetResult();
        };
        helper.FileTransferAcceptReceived += delegate
        {
            acceptReceived.TrySetResult();
        };
        host.FileTransferStartReceived += delegate
        {
            startReceived.TrySetResult();
        };
        host.FileTransferChunkReceived += delegate
        {
            Interlocked.Increment(ref chunkCount);
        };
        host.HostByAddressAsync(cts.Token);
        await host.WaitUntilHostReadyAsync(cts.Token);
        await helper.JoinByInviteAsync(invite: CoreSmokeTestsBase.CreateValidatedInviteForTarget(new PeerAddress(hostAddress), out string rawToken, InviteCapabilities.Chat | InviteCapabilities.FileTransfer), inviteToken: rawToken, ct: cts.Token);
        await joinRaised.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
        await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        string sessionId = host.CurrentSessionSecurityState.SessionId.Value.Value;
        string expectedHash = Convert.ToBase64String(new byte[32]);
        await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_devlocal_replay", FileName = "replay.bin", FileSizeBytes = 1L, Sha256Base64 = expectedHash }, cts.Token);
        await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = "transfer_devlocal_replay" }, cts.Token);
        await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        await helper.SendFileTransferStartAsync(new FileTransferStartV1 { SessionId = sessionId, TransferId = "transfer_devlocal_replay", FileName = "replay.bin", FileSizeBytes = 1L, Sha256Base64 = expectedHash, ChunkCount = 1, ChunkSizeBytes = 1 }, cts.Token);
        await startReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
        byte[] securePayload = CoreSmokeTestsBase.BuildSecureDevLocalPayload(helper, SessionSecureMessageFamily.FileTransfer, "file_transfer_chunk", FileTransferPayloadCodec.Serialize(new FileTransferChunkV1 { SessionId = sessionId, TransferId = "transfer_devlocal_replay", ChunkIndex = 0, ChunkCount = 1, DataBase64 = Convert.ToBase64String(new byte[1] { 9 }) }), "transfer_devlocal_replay", 3L);
        await CoreSmokeTestsBase.SendRawDevLocalFrameAsync(helper, "file_transfer_chunk", securePayload, cts.Token);
        await Task.Delay(100, cts.Token);
        await CoreSmokeTestsBase.SendRawDevLocalFrameAsync(helper, "file_transfer_chunk", securePayload, cts.Token);
        await Task.Delay(200, cts.Token);
        Assert.Equal(1, Volatile.Read(in chunkCount));
    }

}
