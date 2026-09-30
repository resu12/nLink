using System.Collections;
using System.IO.Pipes;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Tests;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    public void CanonicalPrefixSenderBootstrapPauseDoesNotRetireSetupScope()
    {
        using var service = new SessionFileTransferService();
        var context = CreateCanonicalPrefixSenderContext(service);
        SetPrivateProperty(context, "PeerPaused", true);
        var before = CreateCoordinatorStateForTest(context).PersistentState;
        var scope = new AttemptCanonicalPrefixScope(service, context, true, () => { });
        scope.CheckSetup();
        Assert.True(GetPrivateProperty<bool>(context, "PeerPaused"));
        Assert.Equal("canonical_prefix_scope_paused", Assert.Throws<IOException>(scope.Check).Message);
        Assert.Equal(before, CreateCoordinatorStateForTest(context).PersistentState);
        SetPrivateProperty(context, "PeerPaused", false); // Simulated ordinary receiver feedback, not a fixture write.
        scope.Check();
        TryCancelContextLifetime(context);
    }

    [Theory]
    [InlineData("context")] [InlineData("data_session")] [InlineData("generation")]
    [InlineData("owner")] [InlineData("publication")] [InlineData("cancel")]
    [InlineData("terminal")] [InlineData("user_pause")] [InlineData("peer_retired")]
    [InlineData("session")] [InlineData("geometry")]
    public void CanonicalPrefixPausedSenderSetupStillRejectsInvalidScope(string mutation)
    {
        using var service = new SessionFileTransferService();
        var context = CreateCanonicalPrefixSenderContext(service);
        SetPrivateProperty(context, "PeerPaused", true);
        var current = true;
        var scope = new AttemptCanonicalPrefixScope(service, context, true, () =>
        { if (!current) throw new IOException("canonical_prefix_peer_retired"); });
        switch (mutation)
        {
            case "context": typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, null); break;
            case "data_session": SetPrivateProperty(context, "DataSession", new CompletingDataSession("prefix-session", "prefix-transfer")); break;
            case "generation": SetPrivateProperty(context, "DataSessionGeneration", 2L); break;
            case "owner": SetPrivateProperty(context, "CurrentRuntimeOwner", null); break;
            case "publication": SetPrivateProperty(context, "CurrentRoutePublicationToken", null); break;
            case "cancel": TryCancelContextLifetime(context); break;
            case "terminal": SetPrivateProperty(context, "State", FileTransferTransferState.Canceled); break;
            case "user_pause": SetPrivateProperty(context, "UserPaused", true); break;
            case "peer_retired": current = false; break;
            case "session": SetPrivateProperty(context, "SessionId", "foreign"); break;
            case "geometry": SetPrivateProperty(context, "ChunkCount", 12483); break;
        }
        Assert.Throws<IOException>(scope.CheckSetup);
        Assert.Throws<IOException>(scope.Check);
        TryCancelContextLifetime(context);
    }

    [Theory]
    [InlineData("unpause")] [InlineData("still_paused")] [InlineData("user_pause")]
    [InlineData("repause_after_eof")] [InlineData("retire")] [InlineData("context_cancel")]
    [InlineData("dispose")] [InlineData("peer_retired")]
    public async Task CanonicalPrefixPausedSenderPipeCannotReleaseTailUntilStrictConsumption(string fault)
    {
        if (!OperatingSystem.IsWindows()) return;
        var previous = Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable);
        Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, "3");
        try
        {
            using var service = new SessionFileTransferService();
            var context = CreateCanonicalPrefixSenderContext(service);
            SetPrivateProperty(context, "PeerPaused", true);
            var current = true;
            var scope = new AttemptCanonicalPrefixScope(service, context, true, () =>
            { if (!current) throw new IOException("canonical_prefix_peer_retired"); });
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(scope.ContextToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5)); // Unit-test bound only; native lifetime is unchanged.
            var name = "nlink-prefix-setup-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await Task.WhenAll(server.WaitForConnectionAsync(deadline.Token), client.ConnectAsync(deadline.Token));
            var witness = new AttemptCanonicalPrefixWitness(Guid.NewGuid(), Guid.NewGuid(), scope.Session, scope.Transfer);
            await using var consumer = new AttemptCanonicalPrefixHandoff(server, witness, true, scope.Check, deadline.Token, scope.CheckSetup);
            await using var producer = new AttemptCanonicalPrefixHandoff(client, witness, false, () => { }, deadline.Token);
            Assert.False(consumer.Received.IsCompleted);
            using var profile = new AttemptCanonicalTailProfile(268435456, 21504, 12484);
            profile.BeforeRead(AttemptCanonicalTailProfile.PrearmOffset, deadline.Token);
            profile.BindPrefix(consumer.Received, consumer.Check);
            var sourceChecks = 0;
            Task Tail() => profile.BeforeTailReadAsync(AttemptCanonicalTailProfile.PrefixBytes, deadline.Token,
                () => sourceChecks++).AsTask();
            var tail = fault == "repause_after_eof" ? null : Tail();
            Assert.False(tail?.IsCompleted ?? false);
            Assert.Throws<IOException>(profile.RequirePrefixConsumed);
            await Task.Run(() =>
            {
                // Independent ordinary feedback can take the real service gate
                // while the dedicated record read and tail gate remain pending.
                lock (GetServiceGate(service))
                {
                    Assert.False(consumer.Received.IsCompleted);
                    if (fault != "still_paused") SetPrivateProperty(context, "PeerPaused", false);
                    if (fault == "user_pause") SetPrivateProperty(context, "UserPaused", true);
                    if (fault == "retire") SetPrivateProperty(context, "CurrentRuntimeOwner", null);
                    if (fault == "peer_retired") current = false;
                }
            }, deadline.Token);
            if (fault == "context_cancel") TryCancelContextLifetime(context);
            else if (fault == "dispose") await consumer.DisposeAsync();
            else await producer.PublishAsync();
            if (fault == "repause_after_eof")
            {
                await consumer.Received;
                SetPrivateProperty(context, "PeerPaused", true);
                tail = Tail();
            }
            if (fault == "unpause")
            {
                await tail!; Assert.Equal(1, sourceChecks); profile.RequirePrefixConsumed();
            }
            else
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() => tail!);
                if (fault == "context_cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
                else
                {
                    Assert.False(deadline.IsCancellationRequested);
                    if (fault != "dispose") Assert.Equal(fault switch
                    {
                        "retire" => "canonical_prefix_scope_retired",
                        "peer_retired" => "canonical_prefix_peer_retired",
                        _ => "canonical_prefix_scope_paused"
                    }, Assert.IsType<IOException>(error).Message);
                }
                Assert.Equal(0, sourceChecks);
                Assert.ThrowsAny<Exception>(profile.RequirePrefixConsumed);
                if (fault != "repause_after_eof") Assert.False(consumer.Received.IsCompletedSuccessfully);
            }
            TryCancelContextLifetime(context);
        }
        finally { Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, previous); }
    }

    private static object CreateCanonicalPrefixSenderContext(SessionFileTransferService service)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FileTransferReadStreamFactory read = _ => Task.FromResult<Stream>(new MemoryStream());
        var context = Activator.CreateInstance(typeof(SessionFileTransferService).GetNestedType("OutboundTransferContext", flags)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [new FileTransferSendDescriptor("prefix.bin", 268435456, "prefix-transfer"), read], null)!;
        var route = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        SetPrivateProperty(context, "SessionId", "prefix-session");
        SetPrivateProperty(context, "RouteSelection", route);
        SetPrivateProperty(context, "RouteRuntime", route.RuntimeDescriptor);
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", 6);
        SetPrivateProperty(context, "State", FileTransferTransferState.Sending);
        SetPrivateProperty(context, "ChunkSizeBytes", 21504);
        SetPrivateProperty(context, "ChunkCount", 12484);
        SetPrivateProperty(context, "DataSession", new CompletingDataSession("prefix-session", "prefix-transfer"));
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Outbound, "canonical_prefix_unit");
        typeof(SessionFileTransferService).GetField("outboundTransfer", flags)!.SetValue(service, context);
        return context;
    }

    [Theory]
    [InlineData("current")] [InlineData("context")]
    [InlineData("data_session")] [InlineData("generation")]
    [InlineData("owner")] [InlineData("publication")]
    [InlineData("cancel")] [InlineData("terminal")]
    [InlineData("pause")] [InlineData("peer_pause")]
    [InlineData("peer_retired")]
    public void CanonicalPrefixScopeRetainsOriginalContextAndPublishedFallbackIdentity(string mutation)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var service = new SessionFileTransferService();
        var route = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var context = Activator.CreateInstance(typeof(SessionFileTransferService).GetNestedType("InboundTransferContext", flags)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [new FileTransferOfferV2 { SessionId = "prefix-session", TransferId = "prefix-transfer", FileName = "prefix.bin",
                FileSizeBytes = 268435456, PreferredDataProtocolVersion = 6, FileTransferRoute = route.TelemetryToken }, route], null)!;
        SetPrivateProperty(context, "State", FileTransferTransferState.Receiving);
        SetPrivateProperty(context, "ChunkSizeBytes", 21504);
        SetPrivateProperty(context, "ChunkCount", 12484);
        SetPrivateProperty(context, "PullManifestReceived", true);
        SetPrivateProperty(context, "NextChunkIndex", 12476);
        SetPrivateProperty(context, "ChunksTransferred", 12476);
        SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 12475);
        SetPrivateProperty(context, "BytesTransferred", 268283904L);
        SetPrivateProperty(context, "ReceiverSparseBytesWritten", 268283904L);
        SetPrivateProperty(context, "ReceiverSparseChunksWritten", new BitArray(12484));
        SetPrivateProperty(context, "DataSession", new CompletingDataSession("prefix-session", "prefix-transfer"));
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "canonical_prefix_unit");
        var inbound = typeof(SessionFileTransferService).GetField("inboundTransfer", flags)!;
        inbound.SetValue(service, context);
        var peerCurrent = true;
        var scope = new AttemptCanonicalPrefixScope(service, context, false, () =>
        {
            Assert.True(Monitor.IsEntered(GetServiceGate(service)));
            if (!peerCurrent) throw new IOException("canonical_prefix_peer_retired");
        });
        var before = CreateCoordinatorStateForTest(context).PersistentState;
        Assert.True(scope.Ready()); scope.Check();
        Assert.Equal(before, CreateCoordinatorStateForTest(context).PersistentState);
        switch (mutation)
        {
            case "context": inbound.SetValue(service, null); break;
            case "data_session": SetPrivateProperty(context, "DataSession", new CompletingDataSession("prefix-session", "prefix-transfer")); break;
            case "generation": SetPrivateProperty(context, "DataSessionGeneration", 2L); break;
            case "owner": SetPrivateProperty(context, "CurrentRuntimeOwner", null); break;
            case "publication": SetPrivateProperty(context, "CurrentRoutePublicationToken", null); break;
            case "cancel": TryCancelContextLifetime(context); break;
            case "terminal": SetPrivateProperty(context, "State", FileTransferTransferState.Canceled); break;
            case "pause": SetPrivateProperty(context, "UserPaused", true); break;
            case "peer_pause": SetPrivateProperty(context, "PeerPaused", true); break;
            case "peer_retired": peerCurrent = false; break;
        }
        if (mutation == "current") Assert.True(scope.Ready());
        else
        {
            Assert.Throws<IOException>(scope.Check);
            Assert.Throws<IOException>(scope.CheckSetup);
            Assert.Throws<IOException>(() => scope.Ready());
        }
        TryCancelContextLifetime(context);
    }
}
