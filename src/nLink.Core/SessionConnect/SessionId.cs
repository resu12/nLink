namespace NLink.Core.SessionConnect;

public enum SessionIdValidationError
{
    None = 0,
    Empty = 1,
    TooLong = 2,
    InvalidCharacter = 3,
}

public readonly record struct SessionIdValidationResult(
    bool IsValid,
    SessionIdValidationError Error,
    string? Message = null)
{
    public static SessionIdValidationResult Valid()
        => new(true, SessionIdValidationError.None, null);

    public static SessionIdValidationResult Invalid(SessionIdValidationError error, string message)
        => new(false, error, message);
}

public readonly record struct SessionId
{
    private const int MaxLength = 96;

    public SessionId(string value)
    {
        var validation = Validate(value);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.Message ?? "Invalid session id.", nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public static bool TryParse(string? value, out SessionId sessionId)
    {
        var validation = Validate(value);
        if (validation.IsValid)
        {
            sessionId = new SessionId(value!.Trim());
            return true;
        }

        sessionId = default;
        return false;
    }

    public static SessionIdValidationResult Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SessionIdValidationResult.Invalid(SessionIdValidationError.Empty, "Session id is required.");
        }

        var normalized = value.Trim();
        if (normalized.Length > MaxLength)
        {
            return SessionIdValidationResult.Invalid(SessionIdValidationError.TooLong, "Session id is too long.");
        }

        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                continue;
            }

            return SessionIdValidationResult.Invalid(
                SessionIdValidationError.InvalidCharacter,
                $"Session id contains unsupported character '{ch}'.");
        }

        return SessionIdValidationResult.Valid();
    }

    public override string ToString()
    {
        return Value;
    }
}
