using System.Security.Cryptography;
using System.Text;

namespace NLink.Core.Logging;

// Diagnostic association only. Native writer/receipt custody supplies ownership.
public static class FileTransferDiagnosticScopeEvidence
{
    public static string Format(string? sessionId, string? transferId)
    {
        var usable = Usable(sessionId) && Usable(transferId);
        return $"frontier_scope_session_id={(usable ? Token(sessionId!) : "(none)")}; " +
               $"frontier_scope_transfer_id={(usable ? Token(transferId!) : "(none)")}; ";
    }

    public static string FormatDataSession(string? dataSessionIdentity)
        => $"frontier_scope_data_session_identity={(Usable(dataSessionIdentity) ? Token(dataSessionIdentity!) : "(none)")}; ";

    private static bool Usable(string? value)
        => !string.IsNullOrWhiteSpace(value) && value != "(none)" &&
           !value.Contains("[redacted]", StringComparison.Ordinal);

    private static string Token(string value)
        => "fte1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..24];
}
