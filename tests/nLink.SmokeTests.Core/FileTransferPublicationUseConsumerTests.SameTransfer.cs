namespace NLink.SmokeTests;

public sealed partial class FileTransferPublicationUseConsumerTests
{
    [Theory]
    [InlineData("positive")]
    [InlineData("foreign")]
    [Trait("FileTransferAnalyzerSuite", "SameTransferPublicationProducer")]
    public Task SameTransferPublication_SelectedFailureRemainsUnboundWithoutSecurityCapture(string scenario)
        => RunGeneratedConsumerAsync("SameTransferPublicationGenerated", "same-transfer-publication=",
            "same-transfer-publication-20260926", membershipBorrowerCase: "active", sameTransferFixture: scenario);
}
