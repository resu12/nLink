namespace NLink.SmokeTests;

public sealed partial class FileTransferPublicationUseConsumerTests
{
    [Theory]
    [InlineData("active")]
    [InlineData("result-dispose")]
    [InlineData("borrower-dispose")]
    [InlineData("invalid-map")]
    [InlineData("unavailable")]
    [Trait("FileTransferAnalyzerSuite", "SelectedCollectorOrigin")]
    public async Task SelectedCollectorOrigin_BindsOriginalFailureWithoutAnotherRead(string originCase)
        => await RunGeneratedConsumerAsync("SelectedCollectorOriginGenerated", "selected-collector-origin=",
            "selected-collector-origin-20260926", failureFixture: true, membershipBorrowerCase: originCase);
}
