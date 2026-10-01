using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "InboundPublicationTransition")]
    public async Task InboundSparseRuntimeStart_ActiveSessionAdoptsCurrentPublicationsOnce()
    {
        using var fixture = new SparseReceiverRuntimeStartFixture("receiver_runtime_publications");
        var logStart = GetOperationalLogLength();
        fixture.Start();
        Assert.Single(fixture.RuntimeLines(logStart));
        var count = 1;
        foreach (var route in new[] { FileTransferRoute.FileTunaV4, FileTransferRoute.PostTunaFallbackV6, FileTransferRoute.FileTunaV4 })
        {
            lock (GetServiceGate(fixture.Service))
            {
                var publication = ReplaceSparseRouteAuthority(fixture.Context, route);
                var before = CreateCoordinatorStateForTest(fixture.Context);
                Assert.True(fixture.Publish(publication));
                Assert.True(fixture.Publish(publication));
                Assert.Equal(before, CreateCoordinatorStateForTest(fixture.Context));
                var lines = fixture.RuntimeLines(logStart);
                Assert.Equal(++count, lines.Length);
                Assert.Contains($"route_publication_generation={publication.PublicationGeneration};", lines[^1], StringComparison.Ordinal);
                Assert.Contains($"route={publication.RouteToken};", lines[^1], StringComparison.Ordinal);
            }
        }
        // The same session reader is still pending: route adoption must not need
        // a received packet or create a second reader to report the live runtime.
        Assert.Equal(1, fixture.ReceiveCalls);
        Assert.Empty(fixture.Session.SentFrames);
        await fixture.StopAsync();
        Assert.Null(GetPrivatePropertyValue(fixture.Context, "SparseReceiverRuntimeEvidence"));
        var finalPublication = GetPrivateProperty<RoutePublicationToken>(fixture.Context, "CurrentRoutePublicationToken");
        Assert.False(fixture.Publish(finalPublication));
        Assert.Equal(count, fixture.RuntimeLines(logStart).Length);
    }

    [Theory]
    [InlineData("not_started")]
    [InlineData("session")]
    [InlineData("service_generation")]
    [InlineData("provider_generation")]
    [InlineData("context")]
    [InlineData("terminal")]
    [InlineData("canceled")]
    [InlineData("stale_publication")]
    [InlineData("missing_publication")]
    [InlineData("runtime_owner")]
    [InlineData("leg")]
    [Trait("FileTransferRuntimeSuite", "InboundPublicationTransition")]
    public async Task InboundSparseRuntimeStart_StaleExecutionOrPublicationCannotEmit(string invalidation)
    {
        using var fixture = new SparseReceiverRuntimeStartFixture($"receiver_runtime_rejected_{invalidation}");
        if (invalidation != "not_started") fixture.Start();
        var previous = GetPrivateProperty<RoutePublicationToken>(fixture.Context, "CurrentRoutePublicationToken");
        var logStart = GetOperationalLogLength();
        using var replacement = new InitialBootstrapRecordingDataSession(fixture.Session.SessionId, fixture.Session.TransferId);
        lock (GetServiceGate(fixture.Service))
        {
            RoutePublicationToken? publication = ReplaceSparseRouteAuthority(fixture.Context, FileTransferRoute.FileTunaV4);
            switch (invalidation)
            {
                case "not_started": break;
                case "session": SetPrivateProperty(fixture.Context, "DataSession", replacement); break;
                case "service_generation": SetPrivateProperty(fixture.Context, "DataSessionGeneration", 2L); break;
                case "provider_generation": fixture.Session.Generation++; break;
                case "context": fixture.SetServiceContext(null); break;
                case "terminal": SetPrivateProperty(fixture.Context, "State", FileTransferTransferState.Failed); break;
                case "canceled": TryCancelContextLifetime(fixture.Context); break;
                case "stale_publication": publication = previous; break;
                case "missing_publication": publication = null; break;
                case "runtime_owner":
                    var owner = GetPrivateProperty<RuntimeOwnerToken>(fixture.Context, "CurrentRuntimeOwner");
                    SetPrivateProperty(fixture.Context, "CurrentRuntimeOwner", owner with { RuntimeGeneration = owner.RuntimeGeneration + 1 });
                    break;
                case "leg":
                    var leg = GetPrivateProperty<FileTransferLeg>(fixture.Context, "CurrentTransferLeg");
                    SetPrivateProperty(leg, "Generation", leg.Generation + 1);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(invalidation));
            }
            var before = CreateCoordinatorStateForTest(fixture.Context);
            Assert.False(fixture.Publish(publication));
            Assert.Equal(before, CreateCoordinatorStateForTest(fixture.Context));
            Assert.Empty(fixture.RuntimeLines(logStart));
            Assert.Empty(fixture.Session.SentFrames);
            Assert.Empty(replacement.SentFrames);
        }
        await fixture.StopAsync();
    }

    private sealed class SparseReceiverRuntimeStartFixture : IDisposable
    {
        public SessionFileTransferService Service { get; } = new();
        public InitialBootstrapRecordingDataSession Session { get; }
        public object Context { get; }
        public int ReceiveCalls { get; private set; }
        private Task? loop;

        public SparseReceiverRuntimeStartFixture(string transferId)
        {
            var received = new TaskCompletionSource<FileTransferReceivedDataFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            Session = new($"session_{transferId}", transferId)
            {
                ReceiveWithMetadataForTest = ct =>
                {
                    ReceiveCalls++;
                    return new(received.Task.WaitAsync(ct));
                },
            };
            Context = CreatePostTunaFallbackV6InboundContext(transferId, dataSession: Session);
            SetPrivateProperty(Context, "UserPaused", true);
            SetServiceContext(Context);
        }

        public void SetServiceContext(object? context)
            => typeof(SessionFileTransferService).GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Service, context);

        public void Start()
        {
            loop = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
                .GetMethod("RunInboundRegularNknV4Async", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Service, [Context, new FileTransferSessionOpenV2
                {
                    SessionId = Session.SessionId, TransferId = Session.TransferId,
                    ProtocolVersion = 6, ChunkSizeBytes = 21 * 1024, InitialPipelineDepth = 1,
                }]));
            Assert.Equal(1, ReceiveCalls);
            Assert.False(loop.IsCompleted);
        }

        public bool Publish(RoutePublicationToken? publication)
            => (bool)typeof(SessionFileTransferService)
                .GetMethod("TryPublishInboundSparseReceiverRuntimeStartedLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Service, [Context, publication])!;

        public string[] RuntimeLines(int logStart)
            => ReadOperationalLogTail(logStart).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("event=filetransfer_runtime_started; direction=inbound;", StringComparison.Ordinal) &&
                    line.Contains($"transfer_id={Session.TransferId};", StringComparison.Ordinal)).ToArray();

        public async Task StopAsync()
        {
            TryCancelContextLifetime(Context);
            if (loop is not null) await loop.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            TryCancelContextLifetime(Context);
            Service.Dispose();
            Session.Dispose();
        }
    }
}
