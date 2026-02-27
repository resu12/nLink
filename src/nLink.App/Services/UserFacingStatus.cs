namespace NLink.App.Services;

public enum UserStatusKind
{
    Idle,
    Connecting,
    Handshake,
    Connected,
    Reconnecting,
    Failed,
    Degraded
}

public enum FailureSeverity
{
    None,
    Info,
    Warning,
    Error
}

public sealed record UserFacingStatus(
    UserStatusKind Kind,
    string Title,
    string Message,
    FailureSeverity Severity,
    int? Attempt = null,
    int? NextRetryInSeconds = null,
    bool CanCancel = false,
    bool CanCopyDiagnostics = false,
    string? CorrelationId = null)
{
    // Override synthesized properties to enforce null-safe/normalized values for banner binding.
    public UserStatusKind Kind { get; init; } = Kind;
    public string Title { get; init; } = Title ?? string.Empty;
    public string Message { get; init; } = Message ?? string.Empty;
    public FailureSeverity Severity { get; init; } = Severity;
    public int? Attempt { get; init; } = Attempt is > 0 ? Attempt : null;
    public int? NextRetryInSeconds { get; init; } = NextRetryInSeconds is >= 0 ? NextRetryInSeconds : null;
    public bool CanCancel { get; init; } = CanCancel;
    public bool CanCopyDiagnostics { get; init; } = CanCopyDiagnostics;
    public string? CorrelationId { get; init; } = string.IsNullOrWhiteSpace(CorrelationId) ? null : CorrelationId;

    public static UserFacingStatus IdleStatus { get; } = new(
        UserStatusKind.Idle,
        Title: string.Empty,
        Message: string.Empty,
        Severity: FailureSeverity.None);

    public static UserFacingStatus ConnectedStatus(string title = "Connected", string message = "")
        => new(
            UserStatusKind.Connected,
            title,
            message,
            FailureSeverity.Info);

    public static UserFacingStatus FailedStatus(
        string title,
        string message,
        string? correlationId = null,
        bool canCopyDiagnostics = true)
        => new(
            UserStatusKind.Failed,
            title,
            message,
            FailureSeverity.Error,
            CanCopyDiagnostics: canCopyDiagnostics,
            CorrelationId: correlationId);
}
