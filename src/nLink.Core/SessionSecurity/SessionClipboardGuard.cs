using NLink.Core.SessionConnect;

namespace NLink.Core.SessionSecurity;

public enum ClipboardValidationFailure
{
    None = 0,
    AuthorizationDenied = 1,
    SessionIdMissing = 2,
    SessionIdMismatch = 3,
    HelperIdentityMissing = 4,
    HelperIdentityMismatch = 5,
    InvalidTextLength = 6,
    TextTooLarge = 7,
}

public readonly record struct ClipboardAccessResult(
    bool IsAllowed,
    ClipboardValidationFailure Failure,
    SessionAuthorizationFailure AuthorizationFailure,
    string Message)
{
    public static ClipboardAccessResult Allowed()
        => new(true, ClipboardValidationFailure.None, SessionAuthorizationFailure.None, string.Empty);

    public static ClipboardAccessResult Denied(
        ClipboardValidationFailure failure,
        string message,
        SessionAuthorizationFailure authorizationFailure = SessionAuthorizationFailure.None)
        => new(false, failure, authorizationFailure, message);
}

public sealed record ClipboardTransferDescriptor(
    SessionId SessionId,
    PeerAddress HelperIdentity,
    int TextLength);

public static class ClipboardTransferDefaults
{
    public const int DefaultMaxTextLength = 64 * 1024;
}

public sealed class SessionClipboardGuard
{
    private readonly SessionAuthorizationGuard authorizationGuard;

    public SessionClipboardGuard(Func<DateTimeOffset>? nowProvider = null)
    {
        authorizationGuard = new SessionAuthorizationGuard(nowProvider);
    }

    public ClipboardAccessResult AuthorizeSync(
        bool hasSecurityTransport,
        SessionSecurityState securityState,
        SessionGrant? grant)
    {
        ArgumentNullException.ThrowIfNull(securityState);

        var authorization = authorizationGuard.Evaluate(
            hasSecurityTransport,
            securityState,
            grant,
            SessionCapability.Clipboard);
        if (!authorization.IsAuthorized)
        {
            return ClipboardAccessResult.Denied(
                ClipboardValidationFailure.AuthorizationDenied,
                $"Clipboard authorization failed: {authorization.Failure}.",
                authorization.Failure);
        }

        return ClipboardAccessResult.Allowed();
    }

    public ClipboardAccessResult ValidateTransfer(
        bool hasSecurityTransport,
        SessionSecurityState securityState,
        SessionGrant? grant,
        ClipboardTransferDescriptor descriptor,
        int maxTextLength = ClipboardTransferDefaults.DefaultMaxTextLength)
    {
        ArgumentNullException.ThrowIfNull(securityState);
        ArgumentNullException.ThrowIfNull(descriptor);

        var authorization = AuthorizeSync(hasSecurityTransport, securityState, grant);
        if (!authorization.IsAllowed)
        {
            return authorization;
        }

        var binding = ValidateBinding(securityState, descriptor.SessionId, descriptor.HelperIdentity);
        if (!binding.IsAllowed)
        {
            return binding;
        }

        return ValidateTextLength(descriptor.TextLength, maxTextLength);
    }

    private static ClipboardAccessResult ValidateBinding(
        SessionSecurityState securityState,
        SessionId descriptorSessionId,
        PeerAddress descriptorHelperIdentity)
    {
        if (securityState.SessionId is not SessionId activeSessionId)
        {
            return ClipboardAccessResult.Denied(
                ClipboardValidationFailure.SessionIdMissing,
                "Clipboard session binding is unavailable.");
        }

        if (descriptorSessionId != activeSessionId)
        {
            return ClipboardAccessResult.Denied(
                ClipboardValidationFailure.SessionIdMismatch,
                "Clipboard session id does not match the active session.");
        }

        if (securityState.HelperAddress is not PeerAddress activeHelperIdentity)
        {
            return ClipboardAccessResult.Denied(
                ClipboardValidationFailure.HelperIdentityMissing,
                "Clipboard helper identity is unavailable.");
        }

        if (descriptorHelperIdentity != activeHelperIdentity)
        {
            return ClipboardAccessResult.Denied(
                ClipboardValidationFailure.HelperIdentityMismatch,
                "Clipboard helper identity does not match the approved helper.");
        }

        return ClipboardAccessResult.Allowed();
    }

    private static ClipboardAccessResult ValidateTextLength(int textLength, int maxTextLength)
    {
        if (textLength < 0)
        {
            return ClipboardAccessResult.Denied(
                ClipboardValidationFailure.InvalidTextLength,
                "Clipboard text length cannot be negative.");
        }

        if (maxTextLength <= 0)
        {
            return ClipboardAccessResult.Denied(
                ClipboardValidationFailure.TextTooLarge,
                "Clipboard text limit must be positive.");
        }

        if (textLength > maxTextLength)
        {
            return ClipboardAccessResult.Denied(
                ClipboardValidationFailure.TextTooLarge,
                $"Clipboard text exceeds the {maxTextLength}-character limit.");
        }

        return ClipboardAccessResult.Allowed();
    }
}
