using NLink.Core.SessionConnect;

namespace NLink.SmokeTests;

public sealed class HelperBootstrapPayloadTests
{
    [Fact]
    public void FormatAndParse_RoundTripsCompactHelperBootstrapPayload()
    {
        var address = new PeerAddress("nlink-helper.bootstrap.actual.1234567890");
        var payload = HelperBootstrapPayload.Create(
            address,
            helperId: HelperIdentityTokenCodec.Encode(address),
            fingerprintHint: "1234");

        var formatted = HelperBootstrapQrPayload.Format(payload);

        var parsed = HelperBootstrapQrPayload.TryParse(formatted, out var decoded);

        Assert.True(parsed);
        Assert.NotNull(decoded);
        Assert.Equal(address, decoded!.HelperAddress);
        Assert.Equal(payload.HelperId, decoded.HelperId);
        Assert.Null(decoded.FingerprintHint);
        Assert.StartsWith(HelperBootstrapQrPayload.TokenPrefix + ".", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_AcceptsLegacyJsonBootstrapPayload()
    {
        var address = new PeerAddress("nlink-helper.bootstrap.actual.1234567890");
        var helperId = HelperIdentityTokenCodec.Encode(address);
        var legacyJson =
            $"{{\"type\":\"{HelperBootstrapPayload.PayloadType}\",\"version\":1,\"helperAddress\":\"{address.Value}\",\"helperId\":\"{helperId}\",\"fingerprintHint\":\"1234\"}}";

        var parsed = HelperBootstrapQrPayload.TryParse(legacyJson, out var decoded);

        Assert.True(parsed);
        Assert.NotNull(decoded);
        Assert.Equal(address, decoded!.HelperAddress);
        Assert.Equal(helperId, decoded.HelperId);
        Assert.Equal("1234", decoded.FingerprintHint);
    }

    [Fact]
    public void TryParse_RejectsMalformedCompactPayload()
    {
        var parsed = HelperBootstrapQrPayload.TryParse("nlinkh1.invalid!payload", out var decoded);

        Assert.False(parsed);
        Assert.Null(decoded);
    }

    [Fact]
    public void TryParse_RejectsUnsupportedCompactVersion()
    {
        var helperAddress = "nlink-helper.bootstrap.actual.1234567890";
        var helperId = HelperIdentityTokenCodec.Encode(new PeerAddress(helperAddress));
        var helperAddressBytes = System.Text.Encoding.UTF8.GetBytes(helperAddress);
        var helperIdBytes = System.Text.Encoding.UTF8.GetBytes(helperId);
        var payload = new byte[6 + helperAddressBytes.Length + 2 + helperIdBytes.Length];
        payload[0] = (byte)'N';
        payload[1] = (byte)'H';
        payload[2] = 2;
        payload[3] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4, 2), (ushort)helperAddressBytes.Length);
        helperAddressBytes.CopyTo(payload.AsSpan(6));
        var helperIdLengthOffset = 6 + helperAddressBytes.Length;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(helperIdLengthOffset, 2), (ushort)helperIdBytes.Length);
        helperIdBytes.CopyTo(payload.AsSpan(helperIdLengthOffset + 2));
        var encoded = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var parsed = HelperBootstrapQrPayload.TryParse(HelperBootstrapQrPayload.TokenPrefix + "." + encoded, out var decoded);

        Assert.False(parsed);
        Assert.Null(decoded);
    }

    [Fact]
    public void ConnectResolver_ResolvesCompactHelperBootstrapPayloadToPeerAddress()
    {
        var codec = InviteTokenServiceFactory.CreateInviteTokenCodec();
        var resolver = new ConnectInputResolver(codec, new InviteExpiryValidator());
        var address = new PeerAddress("nlink-helper.bootstrap.actual.1234567890");
        var formatted = HelperBootstrapQrPayload.Format(HelperBootstrapPayload.Create(address, helperId: HelperIdentityTokenCodec.Encode(address)));

        var result = resolver.Resolve(formatted, DateTimeOffset.UtcNow);

        Assert.True(result.IsValid);
        Assert.Equal(ConnectInputKind.PeerAddress, result.Kind);
        Assert.Equal(address, result.TargetAddress);
    }

    [Fact]
    public void ConnectResolver_ResolvesLegacyJsonHelperBootstrapPayloadToPeerAddress()
    {
        var codec = InviteTokenServiceFactory.CreateInviteTokenCodec();
        var resolver = new ConnectInputResolver(codec, new InviteExpiryValidator());
        var address = new PeerAddress("nlink-helper.bootstrap.actual.legacy.1234567890");
        var helperId = HelperIdentityTokenCodec.Encode(address);
        var formatted =
            $"{{\"type\":\"{HelperBootstrapPayload.PayloadType}\",\"version\":1,\"helperAddress\":\"{address.Value}\",\"helperId\":\"{helperId}\"}}";

        var result = resolver.Resolve(formatted, DateTimeOffset.UtcNow);

        Assert.True(result.IsValid);
        Assert.Equal(ConnectInputKind.PeerAddress, result.Kind);
        Assert.Equal(address, result.TargetAddress);
    }
}
