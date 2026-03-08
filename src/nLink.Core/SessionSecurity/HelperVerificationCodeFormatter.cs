using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NLink.Core.SessionConnect;

namespace NLink.Core.SessionSecurity;

public static class HelperVerificationCodeFormatter
{
    private const string DomainSeparator = "nlink/helper-verification/v1|";

    private static readonly string[] Adjectives =
    {
        "amber", "arctic", "aspen", "atlas", "autumn", "azure", "bright", "bronze",
        "cedar", "clear", "coral", "cosmic", "crimson", "crystal", "dawn", "ember",
        "fern", "flint", "forest", "frost", "golden", "granite", "harbor", "hazel",
        "hidden", "ivory", "jade", "juniper", "lagoon", "lunar", "maple", "meadow",
        "misty", "moss", "night", "north", "ocean", "olive", "orbit", "pebble",
        "pine", "prairie", "quartz", "quiet", "raven", "river", "rose", "ruby",
        "sable", "sage", "silver", "solar", "spruce", "star", "stone", "storm",
        "summer", "swift", "timber", "valley", "velvet", "wild", "willow", "winter"
    };

    private static readonly string[] Nouns =
    {
        "anchor", "bay", "bloom", "brook", "canyon", "cloud", "coast", "comet",
        "creek", "crown", "delta", "dune", "echo", "falcon", "field", "fire",
        "fjord", "garden", "glade", "grove", "harbor", "horizon", "island", "keystone",
        "lake", "lantern", "meadow", "mesa", "moon", "mountain", "oasis", "path",
        "peak", "planet", "prairie", "rain", "reef", "ridge", "river", "shore",
        "sky", "spring", "star", "stone", "summit", "sun", "thicket", "trail",
        "valley", "vista", "water", "wave", "willow", "wind", "wood", "brookside",
        "cascade", "harvest", "marsh", "meadowlark", "pinecone", "redwood", "wildflower", "woodland"
    };

    public static string? FormatOrNull(PeerAddress? helperIdentity)
        => helperIdentity is null ? null : FormatOrNull(helperIdentity.Value.Value);

    public static string? FormatOrNull(string? helperIdentity)
        => string.IsNullOrWhiteSpace(helperIdentity) ? null : Format(helperIdentity);

    public static string Format(PeerAddress helperIdentity)
        => Format(helperIdentity.Value);

    public static string Format(string helperIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperIdentity);

        var normalizedIdentity = Normalize(helperIdentity);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(DomainSeparator + normalizedIdentity));

        var adjectiveIndex = (int)(BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(0, 4)) % (uint)Adjectives.Length);
        var nounIndex = (int)(BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(4, 4)) % (uint)Nouns.Length);
        var suffix = (int)(BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(8, 4)) % 10000u);

        return $"{Adjectives[adjectiveIndex]}-{Nouns[nounIndex]}-{suffix:0000}";
    }

    private static string Normalize(string helperIdentity)
        => helperIdentity.Trim().ToLowerInvariant();
}
