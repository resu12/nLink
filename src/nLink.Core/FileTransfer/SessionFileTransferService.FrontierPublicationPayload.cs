using System.Security.Cryptography;
using System.Text;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed partial class FrontierBoundaryObservation
    {
        private const int MaximumBoundaryPayloadBytes = 262144;
        private const string BoundaryPayloadFormat = "nlink-frontier-boundary-producer-v1";
        private const string BoundaryPayloadHashDomain = "sha256-utf8-frontier-boundary-producer-v1";
        private static readonly UTF8Encoding BoundaryPayloadUtf8 = new(false, true);
        private readonly object payloadGate = new();
        private readonly FrontierPayloadCatalog? payloadCatalog;
        private PreparedBoundaryPayload? preparedPayload;

        // Private preparation only. Possessing these bytes establishes neither
        // native writer custody nor a primary/parent publication capability.
        public sealed class PreparedBoundaryPayload(byte[] bytes)
        {
            private readonly byte[] payload = (byte[])bytes.Clone();
            public int ByteCount => payload.Length;
            public string Sha256 { get; } = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            public byte[] CopyBytes() => (byte[])payload.Clone();
            public bool Matches(byte[]? candidate) => candidate is not null && payload.AsSpan().SequenceEqual(candidate);
            public bool LiveProofEligible => false;
            public bool NativeWriterCustodyEstablished => false;
            public bool PublicationCompletionIssued => false;
        }

        public bool OwnsPreparedPayload(PreparedBoundaryPayload? candidate)
        {
            lock (payloadGate) { return candidate is not null && ReferenceEquals(preparedPayload, candidate); }
        }

        // Call after closure, outside the service gate. No input-log argument:
        // the writer marker and native input binding must be attached later.
        public PreparedBoundaryPayload? TryPreparePayload(out string reason)
            => PreparePayload(null, out reason);

        public PreparedBoundaryPayload? TryPrepareCatalogPayload(FrontierPayloadCatalog catalog, out string reason)
            => PreparePayload(catalog, out reason);

        private PreparedBoundaryPayload? PreparePayload(FrontierPayloadCatalog? catalog, out string reason)
        {
            lock (payloadGate)
            {
                reason = "";
                if (!ReferenceEquals(payloadCatalog, catalog))
                { reason = "catalog_preparation_owner"; return null; }
                if (preparedPayload is not null) { return preparedPayload; }
                reason = "producer_not_closed_positive";
                try
                {
                    if (!closed || Timing?.Outcome != Status || Status is not
                        ("PublishedBeforeOriginalDeadline" or "PublishedWithinObservationBound")) { return null; }
                    reason = "producer_lifecycle";
                    if (!ValidateClosedBindings(out var repair, out var completion)) { return null; }
                    reason = "payload_capacity";
                    if (missingRanges.Length > FileTransferProtocol.MaxStateMissingRangesV6) { return null; }
                    var evidence = CreateCoordinatorSnapshotEvidence(completion!);
                    var authority = repair!.Authority;
                    var continuity = capturedRepair!;
                    var wake = root!;
                    var frame = frozenFrame!;
                    var delivery = token!.Value;
                    var projection = new
                    {
                        Format = BoundaryPayloadFormat, HashDomain = BoundaryPayloadHashDomain,
                        ClockDomain = FileTransferCoordinator.FrontierPublicationBoundaryTiming.Profile,
                        ObservationId = observationId, ClockInstanceId = clockId,
                        ProcessId = processId, ProcessCreationFileTime = processCreation, Frequency = frequency,
                        Outcome = Status, LiveProofEligible = false, NativeWriterCustodyEstablished = false, PublicationCompletionIssued = false,
                        Source = new
                        {
                            SessionId = Evidence(wake.SessionId), TransferId = Evidence(wake.TransferId),
                            CoordinatorInstanceId = evidence.CoordinatorInstanceId,
                            SourceSha256 = BoundaryHash(CanonicalBoundary(source!)),
                            RuntimeOwnerSha256 = BoundaryHash(CanonicalBoundary(wake.RuntimeOwner)),
                            RoutePublicationSha256 = BoundaryHash(CanonicalBoundary(wake.RoutePublication)),
                            Route = "post_tuna_fallback_v6", wake.ProtocolVersion, wake.LiveRouteEpoch,
                            wake.RuntimeGeneration, wake.TerminalGeneration, wake.RoutePublicationGeneration, wake.RouteCommitGeneration,
                            wake.LegGeneration, LegId = Evidence(wake.LegId), LegState = (int)wake.LegState, wake.LegCanSendData,
                            wake.LegProtocolVersion, wake.LegLiveRouteEpoch, wake.TransportEpoch,
                            wake.BridgeRecoveryGeneration, wake.BridgeDecisionGeneration,
                            CheckpointRequestId = Evidence(wake.CheckpointRequestId), wake.CheckpointGeneration,
                            DataSessionIdentity = Evidence(wake.DataSessionIdentity), wake.DataSessionGeneration,
                        },
                        Repair = new
                        {
                            AuthorityId = Evidence(authority.AuthorityId.ToString()), authority.DecisionGeneration,
                            AuthoritySha256 = BoundaryHash(CanonicalBoundary(authority)),
                            CanonicalRepairKey = Evidence(authority.CanonicalRepairKey), RangeFingerprint = Evidence(authority.RangeFingerprint),
                            authority.FrontierChunkIndex, authority.DurableReceivedHighestChunkIndex,
                            authority.RangeCount, authority.RequestedChunkCount,
                            ProtocolRepairRequestId = Evidence(continuity.ProtocolRepairRequestId), RequestCorrelationId = Evidence(continuity.RequestCorrelationId),
                            SourceScopeFingerprint = Evidence(continuity.SourceScopeFingerprint), CurrentScopeFingerprint = Evidence(continuity.CurrentScopeFingerprint),
                            AcceptanceIdentity = Evidence(continuity.AcceptanceIdentity), continuity.AcceptedStateGeneration,
                            OriginReceiptIdentity = Evidence(continuity.OriginReceiptIdentity), OriginReceiptFingerprint = Evidence(continuity.OriginReceiptFingerprint),
                            ContinuityReceiptIdentity = Evidence(continuity.ContinuityReceiptIdentity), ContinuityReceiptFingerprint = Evidence(continuity.ContinuityReceiptFingerprint),
                        },
                        RootGeneration = wake.WakeGeneration, RecheckedGeneration = recheckedTip!.WakeGeneration,
                        FirstWriteGeneration = writeTip!.WakeGeneration,
                        OriginalDeadlineUtcTicks = wake.DueUtc.UtcTicks,
                        PublicationDeadlineUtcTicks = checked(firstWrite!.Clock.UtcTicks + TimeSpan.TicksPerSecond),
                        Lifecycle = entries.Select((entry, index) => new
                        {
                            Sequence = index + 1, entry.Kind, entry.Clock, Binding = PayloadBinding(entry),
                        }).ToArray(),
                        Frame = new
                        {
                            frame.Epoch, frame.TransportEpoch, frame.ContiguousCommittedChunkIndex, frame.DurableReceivedHighestChunkIndex,
                            frame.CreditUntilChunkIndexExclusive, frame.BytesCommitted, frame.TransferPaused, frame.TerminalReady,
                            frame.ReceiverMemoryPressure, frame.ReceiverDiskPressure,
                            MissingRanges = missingRanges.Select(r => new { r.StartChunkIndex, r.ChunkCount }).ToArray(),
                            BatchId = Evidence(frame.BatchId), RepairRequestId = Evidence(frame.RepairRequestId),
                            Priority = Evidence(frame.Priority), RecoveryMode = Evidence(frame.RecoveryMode), TransferPauseReason = Evidence(frame.TransferPauseReason),
                        },
                        Delivery = new
                        {
                            AuthorityId = Evidence(delivery.AuthorityId.ToString()), delivery.TokenGeneration,
                            delivery.TransportEpoch, ControlPlaneKind = "ReceiverState",
                            TokenSha256 = BoundaryHash(CanonicalBoundary(delivery)),
                        },
                        Completion = new { evidence.CoordinatorInstanceId, evidence.TransitionId,
                            evidence.DecisionKind, evidence.DecisionStatus, SnapshotSha256 = evidence.CalculatePayloadSha256() },
                    };
                    var bytes = EncodeBoundaryPayload(CanonicalBoundary(projection));
                    if (bytes is null) { return null; }
                    preparedPayload = new(bytes);
                    reason = "";
                    return preparedPayload;
                }
                catch (Exception) { return null; }
            }
        }

        private object? PayloadBinding(Entry entry) => entry.Binding switch
        {
            InboundFallbackFrontierProgressReceipt => new
            {
                progress!.Before, progress.After, progress.Count, progress.Highest, progress.Bytes, progress.Path,
            },
            InboundV6FrontierRecentProgressGraceWake w => new
            {
                w.WakeGeneration, w.FrontierChunkIndex, w.HighestReceivedChunkIndex,
                ProgressUtcTicks = w.CommittedProgressUtc.UtcTicks, DeadlineUtcTicks = w.DueUtc.UtcTicks,
            },
            InboundV6FrontierStallGraceWake w => new
            {
                w.WakeGeneration, w.FrontierChunkIndex, w.HighestReceivedChunkIndex,
                StartedUtcTicks = w.StallStartedUtc.UtcTicks, DeadlineUtcTicks = w.DueUtc.UtcTicks,
            },
            ValueTuple<InboundV6FrontierStallGraceWake, InboundV6FrontierStallGraceWake> p => new
            {
                PredecessorGeneration = p.Item1.WakeGeneration, SuccessorGeneration = p.Item2.WakeGeneration,
                p.Item2.HighestReceivedChunkIndex,
            },
            ValueTuple<InboundV6FrontierRecentProgressGraceWake, InboundV6FrontierStallGraceWake> p => new
            {
                RecentGeneration = p.Item1.WakeGeneration, StallGeneration = p.Item2.WakeGeneration,
            },
            WriteBoundary w => new { w.Before, w.After, w.Count, w.Highest, w.Bytes, w.Path },
            FileTransferReceiverStateFrameV6 f => new { f.Epoch, f.TransportEpoch },
            FallbackControlPlaneToken t => new { AuthorityId = Evidence(t.AuthorityId.ToString()), t.TokenGeneration },
            FileTransferCoordinatorTransitionEnvelope e => new { TransitionId = Evidence(e.TransitionId) },
            null => null,
            _ => throw new InvalidDataException("unsupported_payload_binding"),
        };

        private static string Evidence(string? value) => CheckpointContinuityEvidenceToken(value ?? "(none)");

        private static byte[]? EncodeBoundaryPayload(string text)
        {
            try
            {
                if (text.Length is < 1 or > MaximumBoundaryPayloadBytes || BoundaryPayloadUtf8.GetByteCount(text) > MaximumBoundaryPayloadBytes)
                { return null; }
                return BoundaryPayloadUtf8.GetBytes(text);
            }
            catch (EncoderFallbackException) { return null; }
        }
    }
}
