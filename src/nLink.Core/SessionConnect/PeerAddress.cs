namespace NLink.Core.SessionConnect;

public enum PeerAddressValidationError
{
    None = 0,
    Empty = 1,
    TooShort = 2,
    TooLong = 3,
    InvalidCharacter = 4,
}

public readonly record struct PeerAddressValidationResult(
    bool IsValid,
    PeerAddressValidationError Error,
    string? Message = null)
{
    public static PeerAddressValidationResult Valid()
        => new(true, PeerAddressValidationError.None, null);

    public static PeerAddressValidationResult Invalid(PeerAddressValidationError error, string message)
        => new(false, error, message);
}

public readonly record struct PeerAddress
{
    private const int MinLength = 3;
    private const int MaxLength = 255;

    public PeerAddress(string value)
    {
        var validation = Validate(value);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.Message ?? "Invalid peer address.", nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public static bool TryParse(string? value, out PeerAddress address)
    {
        var validation = Validate(value);
        if (validation.IsValid)
        {
            address = new PeerAddress(value!.Trim());
            return true;
        }

        address = default;
        return false;
    }

    public static PeerAddressValidationResult Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PeerAddressValidationResult.Invalid(PeerAddressValidationError.Empty, "Peer address is required.");
        }

        var normalized = value.Trim();
        if (normalized.Length < MinLength)
        {
            return PeerAddressValidationResult.Invalid(PeerAddressValidationError.TooShort, "Peer address is too short.");
        }

        if (normalized.Length > MaxLength)
        {
            return PeerAddressValidationResult.Invalid(PeerAddressValidationError.TooLong, "Peer address is too long.");
        }

        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                continue;
            }

            return PeerAddressValidationResult.Invalid(
                PeerAddressValidationError.InvalidCharacter,
                $"Peer address contains unsupported character '{ch}'.");
        }

        return PeerAddressValidationResult.Valid();
    }

    public override string ToString()
    {
        return Value;
    }
}
