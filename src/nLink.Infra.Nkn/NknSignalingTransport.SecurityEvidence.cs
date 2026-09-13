using System.Security.Cryptography;
using System.Text;
using NLink.Core.Logging;
using NLink.Core.SessionSecurity;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    // Diagnostic references only. No runtime decision may consume this state.
    private readonly string securityEvidenceTransportId = "ftst1-" + Guid.NewGuid().ToString("N");
    private long securityEvidenceInstallationGeneration;
    private FileTransferSecurityContext? fileTransferSecurityContext;

    private sealed class FileTransferSecurityContext(
        string transportId, long generation, string? sessionId, string establishmentFingerprint)
    {
        public string TransportId { get; } = transportId;
        public string InstallationId { get; } = "ftsi1-" + Guid.NewGuid().ToString("N");
        public long Generation { get; } = generation;
        public string? SessionId { get; } = sessionId;
        public string EstablishmentFingerprint { get; } = establishmentFingerprint;
        public volatile bool Retired;
    }

    private sealed record FileTransferSecurityEvidence(
        FileTransferSecurityContext? Context,
        string PayloadFingerprint,
        long Sequence,
        string SessionId,
        string? TransferId);

    private static string SecurityEvidenceFingerprint(string kind, string prefix, byte[] bytes)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes($"nLink/filetransfer/security-{kind}/v1\0"));
            hash.AppendData(bytes);
            return prefix + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (CryptographicException)
        {
            // Failure to create optional evidence must not change a codec outcome.
            return "(none)";
        }
    }

    private void RetireFileTransferSecurityContextLocked()
    {
        if (fileTransferSecurityContext is not { } context)
        {
            return;
        }

        context.Retired = true;
        fileTransferSecurityContext = null;
        LocalOperationalLog.Info("SessionSecurity",
            "event=filetransfer_security_context_retired" + FormatSecurityContextSuffix(context) +
            $"; security_session_id={FileTransferDataPathScopeEvidenceToken(context.SessionId)}" +
            "; security_context_status=retired; authority_effect=none");
    }

    private void InstallFileTransferSecurityContextLocked(string? sessionId, byte[]? approvalBytes)
    {
        var context = new FileTransferSecurityContext(securityEvidenceTransportId,
            ++securityEvidenceInstallationGeneration, sessionId,
            approvalBytes is null ? "(none)" : SecurityEvidenceFingerprint("establishment", "ftest1-", approvalBytes));
        fileTransferSecurityContext = context;
        LocalOperationalLog.Info("SessionSecurity",
            "event=filetransfer_security_context_installed" + FormatSecurityContextSuffix(context) +
            $"; security_session_id={FileTransferDataPathScopeEvidenceToken(context.SessionId)}" +
            $"; security_context_status={SecurityContextStatus(context, sessionId)}; authority_effect=none");
    }

    private byte[] GetFileTransferKeyWithSecurityEvidence(out FileTransferSecurityContext? context)
    {
        lock (controlSecureStateGate)
        {
            var key = GetFileTransferSessionSharedKeyOrThrow();
            context = fileTransferSecurityContext;
            return key;
        }
    }

    private static FileTransferSecurityEvidence CaptureFileTransferSecurityEvidence(
        FileTransferSecurityContext? context, SessionSecureEnvelopeMetadata metadata, byte[] encryptedPayload)
        => new(context, SecurityEvidenceFingerprint("payload", "ftpay1-", encryptedPayload),
            metadata.Sequence, metadata.SessionId.Value, metadata.RequestId);

    private static string SecurityContextStatus(FileTransferSecurityContext? context, string? sessionId)
        => context?.Retired == true ? "retired"
            : context?.SessionId is not null &&
              string.Equals(context.SessionId, sessionId, StringComparison.Ordinal) &&
              context.EstablishmentFingerprint != "(none)" ? "captured" : "unbound";

    private static string FormatSecurityContextSuffix(FileTransferSecurityContext? context)
        => $"; security_transport_id={context?.TransportId ?? "(none)"}" +
           $"; security_installation_id={context?.InstallationId ?? "(none)"}" +
           $"; security_installation_generation={context?.Generation ?? 0}" +
           $"; security_establishment_fingerprint={context?.EstablishmentFingerprint ?? "(none)"}";

    private static string FormatFileTransferSecurityEvidenceSuffix(FileTransferSecurityEvidence? evidence)
        => evidence is null ? "; security_context_status=unbound"
            : FormatSecurityContextSuffix(evidence.Context) +
              $"; security_context_status={SecurityContextStatus(evidence.Context, evidence.SessionId)}" +
              $"; security_payload_fingerprint={evidence.PayloadFingerprint}; security_sequence={evidence.Sequence}" +
              $"; security_session_id={FileTransferDataPathScopeEvidenceToken(evidence.SessionId)}" +
              $"; security_transfer_id={FileTransferDataPathScopeEvidenceToken(evidence.TransferId)}";
}
