using System.Globalization;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferBootstrapScopeTests
{
    private const string Issuer = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    public void BootstrapScope_MatchesIndependentUtf8LengthPrefixedGolden(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            // Independent 185-field, 1,649-byte vector: UTF-8 byte counts, uint32 big-endian.
            const string scope = "2e117ff12bb250c7a159cf321554d8e419c32777b900d109cff0de049f5394df";
            Assert.Equal($"fbc1:{Issuer}:71:{scope}", Id(CreateCommand()));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void BootstrapScope_ChangesWithAuthorityAndCheckpointBoundaries()
    {
        var command = CreateCommand();
        var checkpoint = command.Observation.DurableCheckpoint;
        var original = Id(command);
        foreach (var changed in new[]
        {
            command with { Observation = command.Observation with { SessionId = "another-session" } },
            command with { Observation = command.Observation with { TransferId = "another-transfer" } },
            command with { Observation = command.Observation with { Direction = FileTransferDirection.Inbound } },
            command with { BootstrapGeneration = 72 }, command with { AuthorityId = new("another-authority") },
            command with { SourceRuntimeOwner = command.SourceRuntimeOwner with { RuntimeGeneration = 8 } },
            command with { SourceRoutePublication = command.SourceRoutePublication with { PublicationGeneration = 30 } },
            command with { SourceRoutePublication = command.SourceRoutePublication with { RuntimeOwner = command.SourceRuntimeOwner with { RuntimeGeneration = 8 } } },
            command with { SourceRoutePublication = command.SourceRoutePublication with { RouteCommitGeneration = null } },
            command with { SourceRouteCommit = command.SourceRouteCommit with { CommitGeneration = 24 } },
            command with { SourceRouteCommit = command.SourceRouteCommit with { TunaPathLeaseGeneration = 20 } },
            command with { SourceRouteCommit = command.SourceRouteCommit with { TunaPathLeaseListenerRunId = null } },
            command with { SourceRouteCommit = command.SourceRouteCommit with { SamePathTunaProbeAckId = "another-probe" } },
            command with { SourceRouteCommit = command.SourceRouteCommit with { RuntimeUnlockAdmission = command.SourceRouteCommit.RuntimeUnlockAdmission with { AdmissionGeneration = 12 } } },
            command with { SourceRouteCommit = command.SourceRouteCommit with { RuntimeUnlockAdmission = command.SourceRouteCommit.RuntimeUnlockAdmission with { FallbackSurvivalState = command.SourceRouteCommit.RuntimeUnlockAdmission.FallbackSurvivalState with { PairedFallbackYieldGranted = false } } } },
            command with { SourceTerminalGeneration = 3 }, command with { SourceLiveRouteEpoch = 4 },
            command with { SourceLegGeneration = 5 }, command with { SourceTransportEpoch = 4 },
            command with { TargetTransportEpoch = 6 }, command with { SourceBridgeRecoveryGeneration = 32 },
            command with { SourceDataSessionGeneration = 38 },
        })
        {
            Assert.NotEqual(original, Id(changed));
        }
        foreach (var changed in new[]
        {
            checkpoint with { SourceRuntimeOwner = checkpoint.SourceRuntimeOwner with { RuntimeGeneration = 8 } },
            checkpoint with { SourceRoutePublication = checkpoint.SourceRoutePublication with { PublicationGeneration = 30 } },
            checkpoint with { SourceTerminalGeneration = 3 }, checkpoint with { SourceLegGeneration = 5 },
            checkpoint with { SourceTransportEpoch = 4 }, checkpoint with { SourceBridgeRecoveryGeneration = 32 },
            checkpoint with { DataSessionGeneration = 38 }, checkpoint with { StateEpoch = 42 },
            checkpoint with { ContiguousCommittedChunkIndex = 44 }, checkpoint with { DurableHighestChunkIndex = 48 },
            checkpoint with { ChunkCount = 129 }, checkpoint with { CheckpointGeneration = 54 },
        })
        {
            Assert.NotEqual(original, Id(command with { Observation = command.Observation with { DurableCheckpoint = changed } }));
        }
        Assert.NotEqual(original, Id(command, issuer: new string('b', 64)));
        Assert.NotEqual(original, Id(command, dataSession: "replacement-data"));
        Assert.NotEqual(original, Id(command, checkpointRequest: "replacement-checkpoint"));
        Assert.NotEqual(original, Id(command, legCheckpoint: 80));
        Assert.NotEqual(Id(command, checkpointRequest: null), Id(command, checkpointRequest: ""));
        Assert.NotEqual(Id(command with { SourceRoutePublication = command.SourceRoutePublication with { RouteCommitGeneration = null } }),
            Id(command with { SourceRoutePublication = command.SourceRoutePublication with { RouteCommitGeneration = 0 } }));
    }

    private static string Id(FileTransferFallbackV6ProofBootstrapCommand command, string issuer = Issuer,
        string dataSession = "data|é", string? checkpointRequest = "checkpoint:é", int legCheckpoint = 79)
        => FileTransferCoordinator.CreateFallbackBootstrapCommandId(issuer, command, dataSession, checkpointRequest, legCheckpoint);

    private static FileTransferFallbackV6ProofBootstrapCommand CreateCommand()
    {
        const string session = "session|é", transfer = "transfer:1";
        var owner = new RuntimeOwnerToken(session, transfer, FileTransferDirection.Outbound,
            FileTransferRoute.FileTunaV4, "file_tuna_v4", 4, 3, 7, 2, "runtime|é", "owner-reason");
        var admissionOwner = owner with
        {
            Route = FileTransferRoute.RegularNknV4Fast, RouteToken = "regular_nkn_v4_fast",
            LiveRouteEpoch = 1, RuntimeGeneration = 5, RuntimeFamily = "regular", CreatedReason = "admission-owner",
        };
        var admission = new RuntimeUnlockAdmissionToken(session, transfer, FileTransferDirection.Outbound,
            admissionOwner, FileTransferRoute.RegularNknV4Fast, "regular_nkn_v4_fast", 4, 1, 5, 2, 2, 11, "admit",
            new(true, false, true, false, true, false, true, false, true, false, true));
        var commit = new RouteCommitToken(session, transfer, FileTransferDirection.Outbound, owner, admission,
            FileTransferRoute.FileTunaV4, "file_tuna_v4", 4, 3, 7, 2, 4, 13, 17, 19, "listener|a", "probe:é",
            FileTransferRoute.FileTunaV4, 4, FileTransferTransportKind.Tuna, 23, "commit");
        var publication = new RoutePublicationToken(session, transfer, FileTransferDirection.Outbound, owner,
            FileTransferRoute.FileTunaV4, "file_tuna_v4", 4, 3, 7, 2, 4, 23, FileTransferRoute.FileTunaV4, 4,
            FileTransferTransportKind.Tuna, 29, FileTransferCoordinatorActionKind.RoutePublication, "published");
        var checkpoint = new FileTransferRouteTransitionDurableCheckpoint(owner, publication, 2, 4, 3, 31, 37, 41, 43, 47, 128, 53);
        return new(new(session, transfer, FileTransferDirection.Outbound, 59, 61, "off", "type", 67, checkpoint),
            owner, publication, commit, 2, 3, 4, 3, 31, 37, 5, FileTransferFallbackV6ProofBootstrapFrameKind.FrontierRequest, 71, new("authority|a"));
    }
}
