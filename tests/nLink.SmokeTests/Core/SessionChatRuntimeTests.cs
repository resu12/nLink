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
public sealed class SessionChatRuntimeTests : CoreSmokeTestsBase
{
    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void ChatKeyAgreement_ProducesSameSessionKey_OnBothSides()
    {
        using ChatKeyPair chatKeyPair = ChatKeyAgreement.CreateKeyPair();
        using ChatKeyPair chatKeyPair2 = ChatKeyAgreement.CreateKeyPair();
        byte[] array = chatKeyPair.DeriveSharedKey(chatKeyPair2.PublicKey);
        byte[] actual = chatKeyPair2.DeriveSharedKey(chatKeyPair.PublicKey);
        Assert.Equal(32, array.Length);
        Assert.Equal(array, actual);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void ChatAesGcm_EncryptDecrypt_RoundTrip()
    {
        byte[] key = CoreSmokeTestsBase.SHA256LikeDeterministicBytes("test-key", 32);
        byte[] array = CoreSmokeTestsBase.SHA256LikeDeterministicBytes("test-nonce", 12);
        byte[] bytes = Encoding.UTF8.GetBytes("hello chat");
        ChatEncryptedData chatEncryptedData = ChatAesGcmCrypto.EncryptWithNonce(key, bytes, array);
        byte[] actual = ChatAesGcmCrypto.Decrypt(key, chatEncryptedData.Nonce, chatEncryptedData.Tag, chatEncryptedData.Ciphertext);
        Assert.Equal(bytes, actual);
        Assert.Equal(16, chatEncryptedData.Tag.Length);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void ChatEnvelope_SerializeDeserialize_IsStableAndVersioned()
    {
        ChatEnvelope chatEnvelope = new ChatEnvelope
        {
            Version = 1,
            Type = "chat.message",
            NonceBase64 = "AQIDBAUGBwgJCgsM",
            TagBase64 = "AAAAAAAAAAAAAAAAAAAAAA==",
            CiphertextBase64 = "SGVsbG8="
        };
        byte[] array = ChatEnvelopeCodec.SerializeEnvelope(chatEnvelope);
        string actual = Encoding.UTF8.GetString(array);
        Assert.Equal("{\"v\":1,\"t\":\"chat.message\",\"n\":\"AQIDBAUGBwgJCgsM\",\"g\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"c\":\"SGVsbG8=\"}", actual);
        ChatEnvelope chatEnvelope2 = ChatEnvelopeCodec.DeserializeEnvelope(array);
        Assert.Equal(1, chatEnvelope2.Version);
        Assert.Equal("chat.message", chatEnvelope2.Type);
        Assert.Equal(chatEnvelope.NonceBase64, chatEnvelope2.NonceBase64);
        Assert.Equal(chatEnvelope.TagBase64, chatEnvelope2.TagBase64);
        Assert.Equal(chatEnvelope.CiphertextBase64, chatEnvelope2.CiphertextBase64);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_TrySendChatText_RequiresGrantedCapability()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        string helperAddress = $"devlocal.helper.{Guid.NewGuid():N}";
        SessionRuntime helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        try
        {
            SessionRuntime helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
                ChatMessageRecord? received = null;
                helpeeRuntime.ChatMessageReceived += delegate (object? _, ChatMessageEventArgs e)
                {
                    received = e.Message;
                };
                await helpeeRuntime.StartHelpeeAsync(cts.Token);
                PeerAddress targetAddress = new PeerAddress(hostAddress);
                PeerAddress? boundHelperAddress = new PeerAddress(helperAddress);
                string rawToken;
                ValidatedInviteV1 invite = CoreSmokeTestsBase.CreateValidatedInviteForTarget(targetAddress, out rawToken, InviteCapabilities.ScreenShare, null, boundHelperAddress);
                await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => (object)helpeeRuntime.PendingApprovalRequest != null, TimeSpan.FromSeconds(2.0));
                await helpeeRuntime.ApproveAsync(cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(2.0));
                ChatMessageRecord? sent = await helperRuntime.TrySendChatTextAsync("blocked", cts.Token);
                await Task.Delay(150, cts.Token);
                Assert.False(helperRuntime.CanPerform(SessionCapability.Chat));
                Assert.Null(sent);
                Assert.Null(received);
            }
            finally
            {
                if (helperRuntime != null)
                {
                    ((IDisposable)helperRuntime).Dispose();
                }
            }
        }
        finally
        {
            if (helpeeRuntime != null)
            {
                ((IDisposable)helpeeRuntime).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_InboundChat_IsRejected_WhenChatCapabilityMissing()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        string helperAddress = $"devlocal.helper.{Guid.NewGuid():N}";
        SessionRuntime helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        try
        {
            SessionRuntime helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
                ChatMessageRecord? received = null;
                helpeeRuntime.ChatMessageReceived += delegate (object? _, ChatMessageEventArgs e)
                {
                    received = e.Message;
                };
                await helpeeRuntime.StartHelpeeAsync(cts.Token);
                PeerAddress targetAddress = new PeerAddress(hostAddress);
                PeerAddress? boundHelperAddress = new PeerAddress(helperAddress);
                string rawToken;
                ValidatedInviteV1 invite = CoreSmokeTestsBase.CreateValidatedInviteForTarget(targetAddress, out rawToken, InviteCapabilities.ScreenShare, null, boundHelperAddress);
                await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => (object)helpeeRuntime.PendingApprovalRequest != null, TimeSpan.FromSeconds(2.0));
                await helpeeRuntime.ApproveAsync(cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected && helpeeRuntime.HasSessionKey && helperRuntime.HasSessionKey, TimeSpan.FromSeconds(2.0));
                FieldInfo chatServiceField = typeof(SessionRuntime).GetField("chatService", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(chatServiceField);
                SessionChatService helperChatService = Assert.IsType<SessionChatService>(chatServiceField.GetValue(helperRuntime));
                ChatMessageRecord? bypassSent = await helperChatService.TrySendTextAsync("bypass-chat", cts.Token);
                await Task.Delay(200, cts.Token);
                Assert.NotNull(bypassSent);
                Assert.False(helperRuntime.CanPerform(SessionCapability.Chat));
                Assert.False(helpeeRuntime.CanPerform(SessionCapability.Chat));
                Assert.Null(received);
            }
            finally
            {
                if (helperRuntime != null)
                {
                    ((IDisposable)helperRuntime).Dispose();
                }
            }
        }
        finally
        {
            if (helpeeRuntime != null)
            {
                ((IDisposable)helpeeRuntime).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task DevLocalTransport_Chat_HelperToHelpee_And_HelpeeToHelper_RoundTrip()
    {
        ChatRuntimeCounters.ResetForTests();
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        using DevLocalTransport hostTransport = new DevLocalTransport(hostAddress);
        using DevLocalTransport helperTransport = new DevLocalTransport();
        SessionChatService helpeeChat = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 18, 0, 0, TimeSpan.Zero));
        try
        {
            SessionChatService helperChat = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 18, 0, 5, TimeSpan.Zero));
            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
                helpeeChat.AttachTransport(hostTransport);
                helperChat.AttachTransport(helperTransport);
                IncomingJoinRequestEventArgs pendingJoin = null;
                TaskCompletionSource joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                hostTransport.IncomingJoinRequest += delegate (object? _, IncomingJoinRequestEventArgs e)
                {
                    pendingJoin = e;
                    joinRaised.TrySetResult();
                };
                TaskCompletionSource<string> helpeeMessageTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<string> helperMessageTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource preApprovalNoticeRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                helpeeChat.MessageReceived += delegate (object? _, ChatMessageEventArgs e)
                {
                    helpeeMessageTcs.TrySetResult(e.Message.Text);
                };
                helpeeChat.MessageReceivedBeforeApproved += delegate
                {
                    preApprovalNoticeRaised.TrySetResult();
                };
                helperChat.MessageReceived += delegate (object? _, ChatMessageEventArgs e)
                {
                    helperMessageTcs.TrySetResult(e.Message.Text);
                };
                hostTransport.HostByAddressAsync(cts.Token);
                await Task.Delay(75, cts.Token);
                await helperTransport.JoinByInviteAsync(invite: CoreSmokeTestsBase.CreateValidatedInviteForTarget(new PeerAddress(hostAddress), out string rawToken, InviteCapabilities.Chat), inviteToken: rawToken, ct: cts.Token).WaitAsync(TimeSpan.FromSeconds(3.0));
                Assert.Null(await helperChat.TrySendTextAsync("Hi, it's me", cts.Token));
                Assert.False(preApprovalNoticeRaised.Task.IsCompleted);
                await joinRaised.Task.WaitAsync(cts.Token);
                await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => helperChat.IsApproved && helpeeChat.IsApproved, TimeSpan.FromSeconds(3.0));
                await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeChat.HasSessionKey && helperChat.HasSessionKey, TimeSpan.FromSeconds(3.0));
                Assert.NotNull(await helperChat.TrySendTextAsync("Hi, it's me", cts.Token));
                string helpeeReceived = await helpeeMessageTcs.Task.WaitAsync(cts.Token);
                Assert.NotNull(await helpeeChat.TrySendTextAsync("I can see your message", cts.Token));
                string helperReceived = await helperMessageTcs.Task.WaitAsync(cts.Token);
                Assert.Equal("Hi, it's me", helpeeReceived);
                Assert.Equal("I can see your message", helperReceived);
                ChatRuntimeCountersSnapshot counters = ChatRuntimeCounters.Snapshot();
                Assert.True(counters.ChatSent >= 2);
                Assert.True(counters.ChatReceived >= 2);
                Assert.Equal(0L, counters.ChatDecryptFailed);
                helperTransport.Dispose();
                hostTransport.Dispose();
                cts.Cancel();
                await Task.Delay(50, CancellationToken.None);
            }
            finally
            {
                if (helperChat != null)
                {
                    ((IDisposable)helperChat).Dispose();
                }
            }
        }
        finally
        {
            if (helpeeChat != null)
            {
                ((IDisposable)helpeeChat).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionChatService_ValidReceivedPayload_IncrementsChatReceived()
    {
        ChatRuntimeCounters.ResetForTests();
        using FakeSignalingTransport transport = new FakeSignalingTransport();
        using SessionChatService chat = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 19, 0, 0, TimeSpan.Zero));
        TaskCompletionSource<string> receivedText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.MessageReceived += delegate (object? _, ChatMessageEventArgs e)
        {
            receivedText.TrySetResult(e.Message.Text);
        };
        chat.AttachTransport(transport);
        byte[] key = CoreSmokeTestsBase.SHA256LikeDeterministicBytes("chat-key-valid", 32);
        transport.RaiseSessionKeyReady(key);
        byte[] payloadBytes = CoreSmokeTestsBase.CreateChatPayloadBytes("msg-valid-1", "hello from helper", new DateTimeOffset(2026, 2, 23, 19, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        transport.RaiseChatMessage(payloadBytes);
        Assert.Equal("hello from helper", await receivedText.Task.WaitAsync(TimeSpan.FromSeconds(1.0)));
        ChatRuntimeCountersSnapshot counters = ChatRuntimeCounters.Snapshot();
        Assert.Equal(1L, counters.ChatReceived);
        Assert.Equal(0L, counters.ChatDecryptFailed);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionChatService_DuplicateMessageId_IsRejectedWithoutSecondDelivery()
    {
        ChatRuntimeCounters.ResetForTests();
        using FakeSignalingTransport transport = new FakeSignalingTransport();
        using SessionChatService chat = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 19, 2, 0, TimeSpan.Zero));
        List<string> receivedMessages = new List<string>();
        chat.MessageReceived += delegate (object? _, ChatMessageEventArgs e)
        {
            receivedMessages.Add(e.Message.Text);
        };
        chat.AttachTransport(transport);
        byte[] key = CoreSmokeTestsBase.SHA256LikeDeterministicBytes("chat-key-duplicate", 32);
        transport.RaiseSessionKeyReady(key);
        byte[] payloadBytes = CoreSmokeTestsBase.CreateChatPayloadBytes("msg-duplicate-1", "hello once", new DateTimeOffset(2026, 2, 23, 19, 2, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        transport.RaiseChatMessage(payloadBytes);
        transport.RaiseChatMessage(payloadBytes);
        await CoreSmokeTestsBase.WaitUntilAsync(() => receivedMessages.Count == 1, TimeSpan.FromSeconds(1.0));
        Assert.Single(receivedMessages);
        Assert.Equal("hello once", receivedMessages[0]);
        ChatRuntimeCountersSnapshot counters = ChatRuntimeCounters.Snapshot();
        Assert.Equal(2L, counters.ChatReceived);
        Assert.Equal(0L, counters.ChatDecryptFailed);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionChatService_InvalidPayload_IncrementsDecryptFailed()
    {
        ChatRuntimeCounters.ResetForTests();
        using FakeSignalingTransport fakeSignalingTransport = new FakeSignalingTransport();
        using SessionChatService sessionChatService = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 19, 5, 0, TimeSpan.Zero));
        sessionChatService.AttachTransport(fakeSignalingTransport);
        byte[] sharedKey = CoreSmokeTestsBase.SHA256LikeDeterministicBytes("chat-key-invalid", 32);
        fakeSignalingTransport.RaiseSessionKeyReady(sharedKey);
        fakeSignalingTransport.RaiseChatMessage(Encoding.UTF8.GetBytes("{not-json"));
        ChatRuntimeCountersSnapshot chatRuntimeCountersSnapshot = ChatRuntimeCounters.Snapshot();
        Assert.Equal(1L, chatRuntimeCountersSnapshot.ChatReceived);
        Assert.Equal(1L, chatRuntimeCountersSnapshot.ChatDecryptFailed);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionChatService_SessionKeyRotation_ClearsReplayCache()
    {
        ChatRuntimeCounters.ResetForTests();
        using FakeSignalingTransport transport = new FakeSignalingTransport();
        using SessionChatService chat = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 19, 7, 0, TimeSpan.Zero));
        List<string> receivedTexts = new List<string>();
        TaskCompletionSource receivedBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.MessageReceived += delegate (object? _, ChatMessageEventArgs e)
        {
            receivedTexts.Add(e.Message.Text);
            if (receivedTexts.Count >= 2)
            {
                receivedBoth.TrySetResult();
            }
        };
        chat.AttachTransport(transport);
        byte[] oldKey = CoreSmokeTestsBase.SHA256LikeDeterministicBytes("chat-key-rotation-old", 32);
        byte[] newKey = CoreSmokeTestsBase.SHA256LikeDeterministicBytes("chat-key-rotation-new", 32);
        transport.RaiseSessionKeyReady(oldKey);
        transport.RaiseChatMessage(CoreSmokeTestsBase.CreateChatPayloadBytes("msg-rotated-key", "stale message", new DateTimeOffset(2026, 2, 23, 19, 7, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()));
        transport.RaiseSessionKeyReady(newKey);
        transport.RaiseChatMessage(CoreSmokeTestsBase.CreateChatPayloadBytes("msg-rotated-key", "fresh message", new DateTimeOffset(2026, 2, 23, 19, 7, 1, TimeSpan.Zero).ToUnixTimeMilliseconds()));
        await receivedBoth.Task.WaitAsync(TimeSpan.FromSeconds(1.0));
        Assert.Equal(new string[2] { "stale message", "fresh message" }, receivedTexts);
        ChatRuntimeCountersSnapshot counters = ChatRuntimeCounters.Snapshot();
        Assert.Equal(2L, counters.ChatReceived);
        Assert.Equal(0L, counters.ChatDecryptFailed);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionChatService_SessionSecureEnvelopePayload_IsRejectedAsInvalidPayload()
    {
        ChatRuntimeCounters.ResetForTests();
        using FakeSignalingTransport fakeSignalingTransport = new FakeSignalingTransport();
        using SessionChatService sessionChatService = new SessionChatService(() => new DateTimeOffset(2026, 2, 23, 19, 9, 0, TimeSpan.Zero));
        bool delivered = false;
        sessionChatService.MessageReceived += delegate
        {
            delivered = true;
        };
        sessionChatService.AttachTransport(fakeSignalingTransport);
        byte[] array = CoreSmokeTestsBase.SHA256LikeDeterministicBytes("chat-key-secure-envelope", 32);
        fakeSignalingTransport.RaiseSessionKeyReady(array);
        byte[] payload = SessionSecureEnvelopeCodec.Encrypt(array, new SessionSecureEnvelopeMetadata(SessionSecureMessageFamily.RemoteControl, "control_input", new SessionId("sess_chat_cross_family"), new PeerAddress("helper.chat.secure-envelope"), 0L, "req-chat-cross-family"), Encoding.UTF8.GetBytes("{\"x\":1}"));
        fakeSignalingTransport.RaiseChatMessage(payload);
        Assert.False(delivered);
        ChatRuntimeCountersSnapshot chatRuntimeCountersSnapshot = ChatRuntimeCounters.Snapshot();
        Assert.Equal(1L, chatRuntimeCountersSnapshot.ChatReceived);
        Assert.Equal(1L, chatRuntimeCountersSnapshot.ChatDecryptFailed);
    }

}
