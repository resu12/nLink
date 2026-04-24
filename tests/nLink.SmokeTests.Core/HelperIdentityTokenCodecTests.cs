using System.Text.RegularExpressions;
using NLink.Core.SessionConnect;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class HelperIdentityTokenCodecTests
{
    private static readonly Regex TokenPattern = new("^nhid1-(?:[0-9A-Z]{4}-)*[0-9A-Z]{2,4}$", RegexOptions.Compiled);

    [Fact]
    public void EncodeDecode_RoundTripsPeerAddress()
    {
        var address = new PeerAddress("nlink-4534ca7b.4702209ef2649a7b222a6ceba05bbb6b293f7ae4a713b49ac96798296c513873");

        var token = HelperIdentityTokenCodec.Encode(address);
        var decoded = HelperIdentityTokenCodec.Decode(token);

        Assert.True(decoded.IsSuccess, decoded.Message);
        Assert.Equal(address, decoded.Address);
    }

    [Fact]
    public void Encode_UsesExpectedPrefixAndGrouping()
    {
        var token = HelperIdentityTokenCodec.Encode(new PeerAddress("nlink-helper.bootstrap.actual.1234567890"));

        Assert.Matches(TokenPattern, token);
    }

    [Fact]
    public void Decode_IsCaseInsensitive_AndIgnoresSeparators()
    {
        var address = new PeerAddress("nlink-helper.bootstrap.actual.1234567890");
        var token = HelperIdentityTokenCodec.Encode(address);
        var normalized = token.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var pasted = "  nhid1-" + normalized.Substring(5) + "  ";

        var decoded = HelperIdentityTokenCodec.Decode(pasted);

        Assert.True(decoded.IsSuccess, decoded.Message);
        Assert.Equal(address, decoded.Address);
    }

    [Fact]
    public void Decode_ModifiedToken_FailsChecksum()
    {
        var address = new PeerAddress("nlink-helper.bootstrap.actual.1234567890");
        var token = HelperIdentityTokenCodec.Encode(address);
        var chars = token.ToCharArray();

        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (chars[i] == '-')
            {
                continue;
            }

            chars[i] = chars[i] == 'Z' ? '2' : 'Z';
            break;
        }

        var tampered = new string(chars);

        var decoded = HelperIdentityTokenCodec.Decode(tampered);

        Assert.False(decoded.IsSuccess);
        Assert.Equal(HelperIdentityTokenDecodeError.InvalidChecksum, decoded.Error);
    }

    [Fact]
    public void Decode_InvalidPrefix_IsRejected()
    {
        var decoded = HelperIdentityTokenCodec.Decode("helper-1234-5678");

        Assert.False(decoded.IsSuccess);
        Assert.Equal(HelperIdentityTokenDecodeError.InvalidPrefix, decoded.Error);
    }
}
