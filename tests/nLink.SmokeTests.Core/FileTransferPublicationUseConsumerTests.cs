using System.Diagnostics;
using System.Security.Cryptography;
using NLink.App;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPublicationUseConsumerTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PublicationUseConsumer")]
    public async Task PublicationUseConsumer_GeneratedRecordsAreBoundAndFailClosed()
        => await RunGeneratedConsumerAsync("PublicationUseConsumer", "publication-use-consumer=", "publication-consumer-implementation-20260913");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PublicationForwarding")]
    public async Task PublicationForwarding_GeneratedEffectsBindOnlyTheirOriginalLocalUse()
        => await RunGeneratedConsumerAsync("PublicationForwarding", "publication-forwarding=", "publication-forwarding-implementation-20260913");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "LocalPublicationCarrier")]
    public async Task LocalPublicationCarrier_GeneratedRowsAndCanonicalBytesFailClosed()
        => await RunGeneratedConsumerAsync("LocalPublicationEvidenceCarrier", "local-publication-carrier=", "publication-carrier-implementation-20260913");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "LocalNativeInputCapture")]
    public async Task LocalNativeInputCapture_GeneratedViewsPreserveExactInputsAndForwardingProofs()
        => await RunGeneratedConsumerAsync("NativeInputCaptureGenerated", "native-input-generated=", "publication-native-preparation-20260914");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "LocalNativeInputBinding")]
    public async Task LocalNativeInputBinding_GeneratedSourceWriterAndSelectedRowsFailClosed()
        => await RunGeneratedConsumerAsync("NativeInputBindingGenerated", "native-input-binding=", "publication-native-binding-20260914");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "LocalInputReadLease")]
    public async Task LocalInputReadLease_GeneratedPrimaryViewsKeepOwnedSourceAndWriterInputs()
        => await RunGeneratedConsumerAsync("ReadLeaseGenerated", "read-lease-generated=", "publication-read-inputs-20260914");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PrimaryFailureWitness")]
    public async Task PrimaryFailureWitness_GeneratedCapturesPreserveExactSelectionAndForwarding()
        => await RunGeneratedConsumerAsync("FailureWitnessGenerated", "failure-witness-generated=", "publication-failure-witness-20260914");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "NativeWriterCapture")]
    public async Task NativeWriterCapture_GeneratedOriginalWritesPreserveForwardingProofs()
        => await RunGeneratedConsumerAsync("NativeWriterCaptureGenerated", "native-writer-generated=", "publication-native-writer-20260914");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PrimaryPublicationWitness")]
    public async Task PrimaryPublicationWitness_GeneratedOwnedInputsBindSelectedLocalProofs()
        => await RunGeneratedConsumerAsync("PrimaryPublicationWitnessGenerated", "primary-publication-witness=", "publication-primary-witness-20260914");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PrimaryPublicationCompletion")]
    public async Task PrimaryPublicationCompletion_GeneratedOwnedRowsIssueBoundedPrivateCompletion()
        => await RunGeneratedConsumerAsync("PrimaryPublicationCompletionGenerated", "primary-publication-completion=", "publication-primary-completion-20260914");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PrimaryPublicationChannel")]
    public async Task PrimaryPublicationChannel_GeneratedIssuerBindsParentRequestAndExactWorker()
        => await RunGeneratedConsumerAsync("PrimaryPublicationChannelGenerated", "primary-publication-channel=", "publication-primary-channel-20260914");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PublicationAdoption")]
    public async Task PublicationAdoption_WrapperRequiresOriginalWrittenReceiptAndPreservesOutcomes()
        => await RunGeneratedConsumerAsync("PrimaryPublicationAdoptionWrapper", "publication-adoption=", "publication-adoption-20260914");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PublicationAdoption")]
    public async Task PublicationAdoption_RouteAcceptanceRequiresOriginalWrittenReceiptAndPreservesOutcomes()
        => await RunGeneratedConsumerAsync("PrimaryPublicationAdoptionRouteAcceptance", "publication-adoption=", "publication-adoption-20260914");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "RetainedAnalysisIntegration")]
    public async Task RetainedAnalysisIntegration_RealReportsOmitPublicationWithoutASelectedFailure()
        => await RunGeneratedConsumerAsync("RetainedAnalysisIntegrationGenerated", "retained-analysis-integration=", "publication-offline-integration-20260914");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PublicationPositiveIntegration")]
    public async Task PublicationPositiveIntegration_PrimarySelectsSeededOfflineFailure()
        => await RunGeneratedConsumerAsync("PublicationPositiveIntegrationGenerated", "positive-integration=", "publication-positive-integration-20260914", failureFixture: true);

    [Theory]
    [InlineData("prior")]
    [InlineData("selected")]
    [Trait("FileTransferAnalyzerSuite", "PublicationObservationBudget")]
    public async Task PublicationObservationBudget_OnlySelectedStateAlternativesConsumeLocalBucket(string observationScope)
        => await RunGeneratedConsumerAsync("PublicationObservationBudgetGenerated", "observation-budget=", "publication-observation-budget-20260915",
            failureFixture: true, observationBudgetFixture: observationScope);

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PublicationReceiveIntegration")]
    public async Task PublicationReceiveIntegration_PrimarySelectsGenuineSecondTunaGuardRejection()
        => await RunGeneratedConsumerAsync("PublicationReceiveIntegrationGenerated", "receive-integration=", "publication-receive-integration-20260915",
            receiveFixture: true);

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PublicationTwoWriterIntegration")]
    public async Task PublicationTwoWriterIntegration_PrimaryPreservesGenuineDelayedEnvelopeProvenance()
        => await RunGeneratedConsumerAsync("PublicationTwoWriterIntegrationGenerated", "receive-integration=", "publication-two-writer-integration-20260915",
            twoWriterFixture: true);

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PublicationLaterFallbackIntegration")]
    public async Task PublicationLaterFallbackIntegration_DeliveryCannotRecoverEarlierTunaRejection()
        => await RunGeneratedConsumerAsync("PublicationLaterFallbackIntegrationGenerated", "later-fallback-integration=",
            "publication-later-fallback-integration-20260921", twoWriterFixture: true, finalFallbackFixture: true);

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PublicationServiceDurableFallback")]
    public async Task PublicationServiceDurableFallback_CommittedBytesDoNotReplaceExactTunaProof()
        => await RunGeneratedConsumerAsync("PublicationServiceDurableFallbackGenerated", "later-fallback-integration=",
            "publication-service-durable-fallback-20260921", twoWriterFixture: true, serviceIngressFixture: true, finalFallbackFixture: true);

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PublicationServiceIngressIntegration")]
    public async Task PublicationServiceIngressIntegration_PrimaryRequiresExactTunaPayloadAfterServiceCommit()
        => await RunGeneratedConsumerAsync("PublicationServiceIngressIntegrationGenerated", "receive-integration=", "publication-service-ingress-integration-20260915",
            twoWriterFixture: true, serviceIngressFixture: true);

    [Theory]
    [InlineData("missing_receive")]
    [InlineData("wrong_range")]
    [InlineData("exact")]
    [Trait("FileTransferAnalyzerSuite", "PublicationTunaPayloadIntegration")]
    public async Task PublicationTunaPayloadIntegration_PrimaryRequiresExactSendAndReceive(string mode)
        => await RunGeneratedConsumerAsync("PublicationTunaPayloadIntegrationGenerated", "tuna-payload-integration=", "publication-tuna-payload-integration-20260915",
            twoWriterFixture: true, serviceIngressFixture: true, tunaPayloadMode: mode);

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PublicationPersistence")]
    public async Task PublicationPersistence_RealAdoptedFailurePersistsWithoutChangingPrimaryOutcome()
        => await RunGeneratedConsumerAsync("PublicationPersistenceGenerated", "persistence-integration=", "publication-persistence-20260914", failureFixture: true);

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PublicationLiveInputs")]
    public async Task PublicationLiveInputs_GeneratedOfflineTransferPreservesPrimaryAndPersistence()
        => await RunGeneratedConsumerAsync("PublicationLiveInputsGenerated", "live-input-integration=", "publication-live-inputs-20260914",
            failureFixture: true, liveScaleFixture: true);

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PublicationLiveInputs")]
    public async Task PublicationLiveInputs_CompletionPreservesIssuerAndPayloadLimits()
        => await RunGeneratedConsumerAsync("PublicationLiveCompletionGenerated", "primary-publication-completion=", "publication-live-inputs-20260914");

    [Fact]
    [Trait("FileTransferAnalyzerSuite", "PublicationLiveInputs")]
    public async Task PublicationLiveInputs_ObserverPreservesOriginalPrimaryOnSmallInput()
        => await RunGeneratedConsumerAsync("PublicationLiveObserverGenerated", "positive-integration=", "publication-live-inputs-20260914", failureFixture: true);

    private static async Task RunGeneratedConsumerAsync(string supportScript, string resultPrefix, string artifactName,
        bool failureFixture = false, bool liveScaleFixture = false, string observationBudgetFixture = "none", bool receiveFixture = false,
        bool twoWriterFixture = false, bool serviceIngressFixture = false, string tunaPayloadMode = "none",
        bool finalFallbackFixture = false)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        if (Environment.GetEnvironmentVariable("NLINK_PUBLICATION_CONSUMER_FIXTURE") == "1")
        {
            if (Environment.GetEnvironmentVariable("NLINK_PUBLICATION_LIVE_SCALE_FIXTURE") == "1")
            {
                await NknFileTransferTransportTests.ProducePublicationBulkConsumerFixture();
            }
            if (Environment.GetEnvironmentVariable("NLINK_PUBLICATION_TWO_WRITER_FIXTURE") == "1")
            {
                await NknFileTransferTransportTests.ProduceTwoWriterIngressFixture();
            }
            else if (Environment.GetEnvironmentVariable("NLINK_PUBLICATION_RECEIVE_FIXTURE") == "1")
            {
                await NknFileTransferTransportTests.CreateRejectedDataPathEnvelopeProvenanceFixture(true,
                    capturePublicationUse: true, captureSnapshots: true);
            }
            else if (Environment.GetEnvironmentVariable("NLINK_PUBLICATION_FAILURE_FIXTURE") == "1")
            {
                await NknFileTransferTransportTests.ProducePublicationFailureFixture(
                    Environment.GetEnvironmentVariable("NLINK_PUBLICATION_OBSERVATION_BUDGET_FIXTURE") ?? "none");
            }
            else { await NknFileTransferTransportTests.ProducePublicationConsumerFixture(); }
            Assert.True(LocalOperationalLog.CompleteAndFlush(TimeSpan.FromSeconds(5)));
            return;
        }

        var root = FindRepoRoot();
        var directory = Path.Combine(root, "artifacts", artifactName, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var runId = Guid.NewGuid().ToString();
        var scenarioId = runId + ":offline:1";
        // An isolated test host owns the real logger from startup through drain;
        // no static logger reset, synthetic writer labels, or live NKN transport.
        using var child = new Process { StartInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = root,
        } };
        child.StartInfo.ArgumentList.Add("vstest");
        child.StartInfo.ArgumentList.Add(typeof(FileTransferPublicationUseConsumerTests).Assembly.Location);
        child.StartInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~PublicationUseConsumer_GeneratedRecordsAreBoundAndFailClosed");
        child.StartInfo.Environment["NLINK_PUBLICATION_CONSUMER_FIXTURE"] = "1";
        child.StartInfo.Environment["NLINK_PUBLICATION_FAILURE_FIXTURE"] = failureFixture ? "1" : "0";
        child.StartInfo.Environment["NLINK_PUBLICATION_LIVE_SCALE_FIXTURE"] = liveScaleFixture ? "1" : "0";
        child.StartInfo.Environment["NLINK_PUBLICATION_OBSERVATION_BUDGET_FIXTURE"] = observationBudgetFixture;
        child.StartInfo.Environment["NLINK_PUBLICATION_RECEIVE_FIXTURE"] = receiveFixture ? "1" : "0";
        child.StartInfo.Environment["NLINK_PUBLICATION_TWO_WRITER_FIXTURE"] = twoWriterFixture ? "1" : "0";
        child.StartInfo.Environment["NLINK_PUBLICATION_SERVICE_INGRESS_FIXTURE"] = serviceIngressFixture ? "1" : "0";
        child.StartInfo.Environment["NLINK_PUBLICATION_TUNA_PAYLOAD_MODE"] = tunaPayloadMode;
        child.StartInfo.Environment["NLINK_PUBLICATION_FINAL_FALLBACK_FIXTURE"] = finalFallbackFixture ? "1" : "0";
        child.StartInfo.Environment["NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR"] = directory;
        child.StartInfo.Environment["NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH"] = Path.Combine(directory, "nlink.log");
        child.StartInfo.Environment["NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID"] = runId;
        child.StartInfo.Environment["NLINK_FILETRANSFER_SCENARIO_RUN_ID"] = scenarioId;
        child.Start();
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(liveScaleFixture ? 180 : 60));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch { if (!child.HasExited) { child.Kill(entireProcessTree: true); } throw; }
        Assert.True(child.ExitCode == 0, await stdout + Environment.NewLine + await stderr);
        if (twoWriterFixture)
        {
            // Fresh test collection only: preserve every original record and
            // merge by its actual writer attempt time, never rewrite identities.
            var records = new[] { "nlink.log", "sender.log" }.SelectMany(name => File.ReadAllLines(Path.Combine(directory, name))).ToArray();
            var ordered = records.OrderBy(line => long.Parse(System.Text.RegularExpressions.Regex.Match(line,
                @"(?:^|;\s*)log_writer_attempt_utc_ticks=([0-9]+)").Groups[1].Value));
            await File.WriteAllLinesAsync(Path.Combine(directory, "two-writer-source.log"), ordered);
        }

        const string script = """
            param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[string]$SupportScript,[string]$ObservationBudgetFixture,[string]$TunaPayloadMode)
            $extra=@{}
            if($ObservationBudgetFixture -cne 'none') { $extra.ObservationScope=$ObservationBudgetFixture }
            if($TunaPayloadMode -cne 'none') { $extra.TunaPayloadMode=$TunaPayloadMode }
            & (Join-Path $RepoRoot ('tests/nLink.SmokeTests.Core/Support/'+$SupportScript+'.ps1')) `
                -RepoRoot $RepoRoot -Directory $Directory -RunId $RunId -ScenarioId $ScenarioId @extra
            """;
        string? normalized = null;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            // Large input keeps the original 1,800-second analyzer watchdog plus
            // the 150-second GUI hard deadline and 30 seconds for parent cleanup.
            var result = await RunPowerShellScriptTextAsync(script, [root, directory, runId, scenarioId, supportScript, observationBudgetFixture, tunaPayloadMode], host,
                liveScaleFixture ? TimeSpan.FromSeconds(1980) : null);
            Assert.True(result.ExitCode == 0 && result.Stdout.Contains(resultPrefix, StringComparison.Ordinal),
                $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            var line = Assert.Single(result.Stdout.Split('\n').Select(line => line.Trim()),
                line => line.StartsWith(resultPrefix, StringComparison.Ordinal));
            if (normalized is not null) { Assert.Equal(normalized, line); }
            normalized = line;
            await File.WriteAllTextAsync(Path.Combine(directory, "consumer-" + host + ".json"), line[resultPrefix.Length..]);
        }
    }
}

public sealed partial class NknFileTransferTransportTests
{
    internal static async Task ProducePublicationConsumerFixture()
        => await WithSecurityCaptureSession(async (transport, peerTransport, _, _, ct) =>
        {
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(transport);
            receiver.AttachTransport(peerTransport);
            var payload = Enumerable.Range(0, 128 * 1024).Select(i => (byte)(i % 251)).ToArray();
            var transfer = new string('b', 64);
            var destination = new MemoryStream();
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("consumer.bin", payload.Length, transfer),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), ct);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(5));
            await receiver.AcceptIncomingTransferAsync(transfer, (_, _) => Task.FromResult<Stream>(destination), ct);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, TimeSpan.FromSeconds(8));
            Assert.Equal(payload, destination.ToArray());
        });

    internal static async Task ProducePublicationBulkConsumerFixture()
        => await WithSecurityCaptureSession(async (transport, peerTransport, _, _, ct) =>
        {
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(transport);
            receiver.AttachTransport(peerTransport);
            const int payloadBytes = 512 * 1024 * 1024;
            const string transfer = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var directory = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR")!;
            var path = Path.Combine(directory, "offline-bulk-payload.bin");
            using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("offline-bulk.bin", payloadBytes, transfer),
                _ => Task.FromResult(FileTransferSoakRunner.CreatePayloadStreamForTests(payloadBytes, 42, 0)), ct);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(5));
            await receiver.AcceptIncomingTransferAsync(transfer, (_, _) => Task.FromResult<Stream>(destination), ct);
            await WaitUntilAsync(() =>
            {
                Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
                Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
                return sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                    receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed;
            }, TimeSpan.FromSeconds(140));
            // Completed is published before terminal cleanup closes the sink.
            // Verify only after the service has released its write handle.
            await WaitUntilAsync(() => !destination.CanRead, TimeSpan.FromSeconds(5));
            using var actual = File.OpenRead(path);
            using var expected = FileTransferSoakRunner.CreatePayloadStreamForTests(payloadBytes, 42, 0);
            Assert.Equal(payloadBytes, actual.Length);
            Assert.Equal(SHA256.HashData(expected), SHA256.HashData(actual));
        }, TimeSpan.FromSeconds(160));
}
