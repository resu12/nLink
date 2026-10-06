using System.Reflection;
using System.Text.Json;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferPublicationUseCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "PublicationUseEvidence")]
    public void PublicationUse_ReferenceIdentityDoesNotChangeAuthorityValues(bool prePump)
    {
        var kind = prePump ? FileTransferPublicationUseKind.V4PrePumpRequest : FileTransferPublicationUseKind.V4PhysicalSubmission;
        var first = CreateCycle("publication-use-session", "publication-use-transfer", FileTransferDirection.Outbound)[0];
        var other = CreateCycle("publication-use-session", "publication-use-transfer", FileTransferDirection.Outbound)[0];
        var authority = CreateAuthority(first.Token);
        var clone = authority with { };
        var json = JsonSerializer.Serialize(authority);
        var hash = authority.GetHashCode();
        Assert.Equal(authority, CreateAuthority(other.Token));
        Assert.NotEqual(first.Evidence.CoordinatorId, other.Evidence.CoordinatorId);
        Assert.Null(FileTransferPublicationUseEvidence.GetSend(authority));
        FileTransferPublicationUseEvidence.CaptureSend(authority, first.Evidence, kind);
        var captured = FileTransferPublicationUseEvidence.GetSend(authority);
        Assert.Equal(new FileTransferPublicationUseEvidence(first.Evidence, kind), captured);
        Assert.Null(FileTransferPublicationUseEvidence.GetSend(clone));
        Assert.Null(FileTransferPublicationUseEvidence.GetSend(CreateAuthority(other.Token)));
        Assert.Equal(authority, clone);
        Assert.Equal(hash, authority.GetHashCode());
        Assert.Equal(json, JsonSerializer.Serialize(authority));
        Assert.Equal(json, JsonSerializer.Serialize(clone));
        FileTransferPublicationUseEvidence.CaptureSend(authority, other.Evidence, kind);
        Assert.Same(captured, FileTransferPublicationUseEvidence.GetSend(authority));
        FileTransferPublicationUseEvidence.CaptureSend(clone, null, kind);
        Assert.Null(FileTransferPublicationUseEvidence.GetSend(clone));
    }

    internal static FileTransferDataPathSendAuthority CreateAuthority(RoutePublicationToken token)
        => Assert.IsType<FileTransferDataPathSendAuthority>(typeof(SessionFileTransferService)
            .GetMethod("CreateDataPathSendAuthority", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [token, token.Reason]));

    internal static (RoutePublicationToken Token, FileTransferRoutePublicationEvidence Evidence)[] CreateCycle(
        string sessionId, string transferId, FileTransferDirection direction)
    {
        var history = new List<FileTransferCoordinatorTransitionEnvelope>();
        var tokens = FileTransferCoordinatorTests.CreateDataPathIngressRouteCycle(sessionId, transferId, direction,
            envelope => { if (envelope is not null) { history.Add(envelope); } });
        return tokens.Select(token =>
        {
            var fingerprint = FileTransferCoordinator.CalculateSnapshotFingerprint(token);
            var raw = history.FirstOrDefault(envelope => envelope.Aggregate.Operations.Any(operation =>
                operation.OperationRole == "route_publication" && operation.ReceiptFingerprint == fingerprint));
            Assert.True(raw is not null, $"Missing authored publication for epoch {token.LiveRouteEpoch}; captured: {string.Join(',', history.Select(e => e.DecisionKind))}");
            var projected = SessionFileTransferService.CreateCoordinatorSnapshotEvidenceForTesting(raw);
            var operation = Assert.Single(projected.Aggregate.Operations, operation =>
                operation.OperationRole == "route_publication" && operation.RouteRuntime!.Boundary.LiveRouteEpoch == token.LiveRouteEpoch);
            Assert.True(operation.ReceiptValidated);
            Assert.Equal("Accepted", operation.ReceiptStatus);
            return (token, new FileTransferRoutePublicationEvidence(projected.CoordinatorInstanceId,
                projected.SessionId, projected.TransferId, projected.TransitionId, projected.Aggregate.AfterStateGeneration,
                projected.Aggregate.AfterStateGeneration, operation.OperationKey, operation.ReceiptFingerprint));
        }).ToArray();
    }
}
