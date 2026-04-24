using NLink.App.Services;
using NLink.Core.SessionConnect;

namespace NLink.SmokeTests;

[Trait("Area", "Gui")]
public sealed class ConnectUiSupportServicesTests
{
    [Trait("Category", "Smoke")]
    [Fact]
    public void QrCodeService_GenerateAndDecode_RoundTripsInviteToken()
    {
        var service = new QrCodeService();
        var source = "nlinki1.testpayload.testsignature";

        var generated = service.TryCreatePng(source, out var pngBytes, out var generateError);
        Assert.True(generated, generateError);
        Assert.NotEmpty(pngBytes);

        using var stream = new MemoryStream(pngBytes);
        var decoded = service.TryDecode(stream, out var decodedText, out var decodeError);
        Assert.True(decoded, decodeError);
        Assert.Equal(source, decodedText);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void QrCodeService_GenerateAndDecode_RoundTripsWrappedInvitePayload()
    {
        var service = new QrCodeService();
        var source = InviteQrPayload.Format("nlinki1.testpayload.testsignature");

        var generated = service.TryCreatePng(source, out var pngBytes, out var generateError);
        Assert.True(generated, generateError);
        Assert.NotEmpty(pngBytes);

        using var stream = new MemoryStream(pngBytes);
        var decoded = service.TryDecode(stream, out var decodedText, out var decodeError);
        Assert.True(decoded, decodeError);
        Assert.Equal(source, decodedText);
    }

}
