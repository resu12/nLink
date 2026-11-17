using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    [Trait("FileTransferRuntimeSuite", "PublicationEvidence")]
    public void RoutePublicationEvidence_ExactReceiptLookupIsReadOnly(FileTransferDirection direction)
    {
        var transferId = $"transfer_publication_evidence_{direction}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var outbound = direction == FileTransferDirection.Outbound;
        var context = outbound
            ? CreateRegularNknV4OutboundContext(transferId, 0, 0, 0, DateTimeOffset.UtcNow, seedRuntimeOwner: false)
            : CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService).GetField(outbound ? "outboundTransfer" : "inboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        var emitter = GetPrivatePropertyValue(context, "CoordinatorSnapshotEmitter")!;
        var lookup = emitter.GetType().GetMethod("GetRoutePublicationEvidence", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(lookup);
        StartCoordinatorSnapshotStreamForTest(context);
        Assert.Null(lookup.Invoke(emitter, [null]));
        Assert.NotNull(typeof(SessionFileTransferService).GetMethod(outbound ? "EnsureOutboundTransferLegLocked" : "EnsureInboundTransferLegLocked",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, outbound
                ? [context, "unit_publication_evidence", null, null] : [context, "unit_publication_evidence", null]));
        var publish = typeof(SessionFileTransferService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "TryPublishRouteSelectedIfAuthorizedLocked" &&
                method.GetParameters()[0].ParameterType == context.GetType() && method.GetParameters()[4].ParameterType.IsByRef);
        object?[] args = [context, FileTransferRouteResolverInput.FromTransport(transport),
            "unit_publication_evidence", "unit_publication_evidence", null, null];
        Assert.True(Assert.IsType<bool>(publish.Invoke(service, args)));
        var publication = Assert.IsType<RoutePublicationToken>(args[4]);
        var before = FileTransferCoordinator.CalculateSnapshotStateFingerprint(CreateCoordinatorStateForTest(context));
        var evidence = lookup.Invoke(emitter, [publication]);
        Assert.NotNull(evidence);
        var project = emitter.GetType().GetMethod("EvidenceToken", BindingFlags.Static | BindingFlags.NonPublic)!;
        string Project(string value) => Assert.IsType<string>(project.Invoke(null, [value]));
        Assert.Equal(Project(GetPrivateProperty<string>(context, "CoordinatorInstanceId")), GetPrivateProperty<string>(evidence, "CoordinatorId"));
        Assert.Equal(Project(publication.SessionId), GetPrivateProperty<string>(evidence, "SessionId"));
        Assert.Equal(Project(publication.TransferId), GetPrivateProperty<string>(evidence, "TransferId"));
        Assert.Equal(Project(FileTransferCoordinator.CalculateSnapshotFingerprint(publication)), GetPrivateProperty<string>(evidence, "TokenFingerprint"));
        Assert.StartsWith("fte1-", GetPrivateProperty<string>(evidence, "BoundaryId"), StringComparison.Ordinal);
        Assert.StartsWith("route_runtime|route_publication|", GetPrivateProperty<string>(evidence, "OperationKey"), StringComparison.Ordinal);
        Assert.Equal(evidence, lookup.Invoke(emitter, [publication]));
        foreach (var foreign in new[] { publication with { PublicationGeneration = publication.PublicationGeneration + 1 },
            publication with { SessionId = "foreign" }, publication with { TransferId = "foreign" },
            publication with { Direction = outbound ? FileTransferDirection.Inbound : FileTransferDirection.Outbound },
            publication with { Reason = "unissued" } })
        {
            Assert.Null(lookup.Invoke(emitter, [foreign]));
        }
        Assert.Equal(before, FileTransferCoordinator.CalculateSnapshotStateFingerprint(CreateCoordinatorStateForTest(context)));
        emitter.GetType().GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(emitter,
            [GetPrivateProperty<string>(context, "CoordinatorInstanceId"), $"session_{transferId}", transferId, direction]);
        Assert.Null(lookup.Invoke(emitter, [publication]));
    }
}
