namespace NLink.SmokeTests;

public sealed partial class FileTransferPublicationUseConsumerTests
{
    [Theory]
    [InlineData("authenticated-positive")]
    [InlineData("authenticated-foreign")]
    [Trait("FileTransferAnalyzerSuite", "AuthenticatedTransferProducer")]
    public Task AuthenticatedTransfer_PreservesOriginalWitnessAndOwnerBoundaries(string scenario)
        => RunGeneratedConsumerAsync("AuthenticatedTransferGenerated", "authenticated-transfer=",
            "authenticated-transfer-20260926", membershipBorrowerCase: "active", sameTransferFixture: scenario);
}
