using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace NLink.Core.FileTransfer;

internal sealed record SessionPreparationDiagnosticScope(string SessionId, Guid Incarnation,
    string HelpeeIdentity, string HelperIdentity);

internal sealed record SessionPreparationDiagnostic(SessionPreparationDiagnosticScope? Scope,
    bool Ready, string Reason, string Phase, string Direction, string EpisodeFingerprint,
    long PreparationGeneration, long SnapshotGeneration, long LeaseGeneration,
    DateTimeOffset SourceCapturedUtc, DateTimeOffset ExpiresUtc, bool Consumed, bool Revoked)
{
    public string AuthorityEffect => "none";
}

public sealed partial class SessionFileTransferService
{
    // Discovery only: never register a scope or inspect/consume an admission provider.
    internal SessionPreparationDiagnosticScope? GetSessionPreparationDiagnosticScope()
    {
        lock (gate) return DiagnosticScope(sessionPreparation.Scope);
    }

    internal SessionPreparationDiagnostic ObserveSessionPreparationForDiagnostics(string expectedSessionId,
        FileTransferDirection direction, DateTimeOffset requestedUtc)
    {
        FileTransferSessionPreparationState captured;
        FileTransferSessionPreparationAttachment? attachment;
        FileTransferSessionPreparationSecurityEpoch? epoch;
        lock (gate)
        {
            captured = sessionPreparation;
            attachment = preparationAttachment;
            epoch = preparationEpoch;
        }

        // Observe recaptures the lease outside the service gate and epoch fence.
        // It is deliberately NOT the consuming initial-readiness provider.
        var observation = captured.Scope?.SessionId == expectedSessionId && attachment is not null && epoch is not null
            ? ((IFileTransferSessionPreparationAuthority)this).Observe(attachment, epoch, direction, requestedUtc)
            : null;
        var current = false;
        lock (gate)
        {
            epoch?.TryUse(() => current = ReferenceEquals(captured, sessionPreparation) && attachment is not null &&
                IsPreparationAttachmentCurrentLocked(attachment, epoch) && ReferenceEquals(preparationEpoch, epoch) &&
                IsPreparationResourceCurrentLocked() && captured.Scope is { } scope &&
                scope.ApprovalExpiresUtc > DateTimeOffset.UtcNow);
        }

        var readiness = captured.Readiness ?? captured.Admission?.Readiness;
        var ready = current && observation?.Ready == true;
        var reason = !current ? "preparation_scope_unavailable"
            : captured.Scope?.SessionId != expectedSessionId ? "expected_session_mismatch"
            : observation?.Reason ?? "preparation_lease_unavailable";
        // Fingerprint shared authenticated fields, not independently allocated local generations.
        var fingerprint = readiness is null || captured.Scope is null ? string.Empty : Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new[] { captured.Scope.SessionId, captured.Scope.HelpeeIdentity,
                captured.Scope.HelperIdentity, readiness.Proof.Nonce,
                readiness.Proof.PayerDecisionId.ToString(CultureInfo.InvariantCulture) })));
        return new(DiagnosticScope(captured.Scope), ready, reason, captured.Phase.ToString(), direction.ToString(),
            fingerprint, captured.LastPreparationGeneration, readiness?.Proof.SnapshotGeneration ?? 0,
            readiness?.Proof.LeaseGeneration ?? 0, readiness?.Proof.ObservedUtc ?? default,
            readiness?.ExpiresUtc ?? default, captured.ClaimedEvidence is not null,
            !current || captured.Phase == FileTransferSessionPreparationPhase.Revoked);
    }

    private static SessionPreparationDiagnosticScope? DiagnosticScope(FileTransferSessionPreparationScope? scope)
        => scope is null ? null : new(scope.SessionId, scope.SecurityIncarnation, scope.HelpeeIdentity, scope.HelperIdentity);
}
