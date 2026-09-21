using System.Text.Json;
using NLink.App.Services;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferServicePreparationTests
{
    [Fact]
    public void PreparationDiagnostics_DiscoveryDoesNotRegisterAndObservationsDoNotConsumeOrRenew()
    {
        using var transport = new PreparationTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        var before = ServiceField(service, "sessionPreparation");
        Assert.Null(service.GetSessionPreparationDiagnosticScope());
        Assert.False(service.ObserveSessionPreparationForDiagnostics(PreparationTransport.Session,
            FileTransferDirection.Outbound, DateTimeOffset.UtcNow).Ready);
        Assert.Same(before, ServiceField(service, "sessionPreparation"));
        Assert.Equal(0, transport.LeaseReads);
        var binding = Prepare(transport, true);
        before = ServiceField(service, "sessionPreparation");
        var state = (FileTransferSessionPreparationState)before!;
        transport.BeforeLeaseRead = () => Assert.False(Monitor.IsEntered(ServiceField(service, "gate")!));
        foreach (var direction in new[] { FileTransferDirection.Outbound, FileTransferDirection.Inbound, FileTransferDirection.Outbound })
        {
            var observation = service.ObserveSessionPreparationForDiagnostics(PreparationTransport.Session, direction, DateTimeOffset.UtcNow);
            Assert.True(observation.Ready);
            Assert.False(observation.Consumed);
            Assert.False(observation.Revoked);
            Assert.Equal("none", observation.AuthorityEffect);
            Assert.Equal(binding.Epoch.Id, observation.Scope!.Incarnation);
            Assert.Equal(state.Readiness!.Proof.ObservedUtc, observation.SourceCapturedUtc);
            Assert.Equal(state.Readiness.ExpiresUtc, observation.ExpiresUtc);
            Assert.Matches("^[0-9A-F]{64}$", observation.EpisodeFingerprint);
            Assert.DoesNotContain(binding.Permit.Nonce, JsonSerializer.Serialize(observation), StringComparison.Ordinal);
            Assert.Same(before, ServiceField(service, "sessionPreparation"));
        }
        Assert.Null(service.Snapshot.Inbound);
        Assert.Null(service.Snapshot.Outbound);
        Assert.Empty(transport.SentOffers);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("detach")]
    [InlineData("key")]
    [InlineData("approval")]
    [InlineData("peer")]
    [InlineData("dispose")]
    public void PreparationDiagnostics_RecaptureFailsClosedAcrossLeaseRead(string change)
    {
        using var transport = new PreparationTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        var binding = Prepare(transport, true);
        transport.BeforeLeaseRead = () =>
        {
            Assert.False(Monitor.IsEntered(ServiceField(service, "gate")!));
            switch (change)
            {
                case "reset": service.ResetSessionState(); break;
                case "detach": service.DetachTransport(); break;
                case "key": transport.Install(); break;
                case "approval": transport.SessionPreparationLifecycle.ObserveSecurity(transport.Security with { ApprovalGranted = false }); break;
                case "peer": transport.SessionPreparationLifecycle.ObserveSecurity(transport.Security with { HelperAddress = new("other") }); break;
                case "dispose": service.Dispose(); break;
            }
        };
        var view = service.ObserveSessionPreparationForDiagnostics(PreparationTransport.Session, FileTransferDirection.Outbound, DateTimeOffset.UtcNow);
        Assert.False(view.Ready);
        Assert.True(view.Revoked);
        Assert.Equal(binding.Epoch.Id, view.Scope!.Incarnation); // Never blend a successor scope with the old proof.
        Assert.Equal("none", view.AuthorityEffect);
        Assert.Empty(transport.SentOffers);
    }

    [Theory]
    [InlineData("wrong_session")]
    [InlineData("future_request")]
    [InlineData("old_request")]
    [InlineData("lease_missing")]
    [InlineData("invalid_direction")]
    [InlineData("expired")]
    public void PreparationDiagnostics_InvalidEvidenceIsNotReadiness(string failure)
    {
        using var transport = new PreparationTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        Prepare(transport, true);
        var state = (FileTransferSessionPreparationState)ServiceField(service, "sessionPreparation")!;
        if (failure == "lease_missing") transport.LeaseAvailable = false;
        if (failure == "expired")
            typeof(SessionFileTransferService).GetField("sessionPreparation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(service, state with { Readiness = state.Readiness! with { ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1) } });
        var before = ServiceField(service, "sessionPreparation");
        var requested = failure == "future_request" ? DateTimeOffset.UtcNow.AddMinutes(1)
            : failure == "old_request" ? state.Readiness!.Proof.ObservedUtc.AddSeconds(-1) : DateTimeOffset.UtcNow;
        var view = service.ObserveSessionPreparationForDiagnostics(failure == "wrong_session" ? "other" : PreparationTransport.Session,
            failure == "invalid_direction" ? (FileTransferDirection)999 : FileTransferDirection.Outbound, requested);
        Assert.False(view.Ready);
        Assert.Same(before, ServiceField(service, "sessionPreparation"));
    }

    [Fact]
    public async Task PreparationDiagnostics_FirstAdmissionClosesBothDiagnosticDirections()
    {
        using var transport = new PreparationTransport();
        using var peer = new LoopbackFileTransferTransport(PreparationTransport.Session);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        transport.Install();
        Prepare(transport, true);
        Assert.NotNull(await Start(service));
        var before = ServiceField(service, "sessionPreparation");
        foreach (var direction in new[] { FileTransferDirection.Outbound, FileTransferDirection.Inbound })
        {
            var view = service.ObserveSessionPreparationForDiagnostics(PreparationTransport.Session, direction, DateTimeOffset.UtcNow);
            Assert.False(view.Ready);
            Assert.True(view.Consumed);
            Assert.Equal("AdmissionReserved", view.Phase);
            Assert.Same(before, ServiceField(service, "sessionPreparation"));
        }
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("root")]
    [InlineData("boot")]
    [InlineData("relative_root")]
    [InlineData("invalid_boot")]
    public async Task PreparationDiagnosticBridge_RequiresSmokeModeRunRootAndBoot(string missing)
    {
        await WithPreparationBridgeEnvironment((root, boot) =>
        {
            Environment.SetEnvironmentVariable(missing == "mode" ? "NLINK_GUI_SMOKE_SCENARIOS"
                : missing is "root" or "relative_root" ? GuiSmokeSessionPreparationBridge.RootVariable : GuiSmokeSessionPreparationBridge.BootVariable,
                missing == "relative_root" ? "relative" : missing == "invalid_boot" ? "../../outside" : null);
            using var service = new SessionFileTransferService();
            using var bridge = GuiSmokeSessionPreparationBridge.TryStart(service, () => throw new InvalidOperationException());
            Assert.Null(bridge);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task PreparationDiagnosticBridge_AtomicCorrelatedReadOnlyResponsesAndDisposal()
        => WithPreparationBridgeEnvironment(async (root, boot) =>
        {
            using var transport = new PreparationTransport();
            using var service = new SessionFileTransferService();
            service.AttachTransport(transport);
            transport.Install();
            var binding = Prepare(transport, true);
            var before = ServiceField(service, "sessionPreparation");
            using var bridge = GuiSmokeSessionPreparationBridge.TryStart(service, () => "Helpee");
            Assert.NotNull(bridge);
            var directory = Path.Combine(root, $"{Environment.ProcessId}-{boot}");
            var responsePath = Path.Combine(directory, "response.json");
            await WaitUntilAsync(() => File.Exists(Path.Combine(directory, "identity.json")));
            var request = new GuiSmokeSessionPreparationBridge.Request(Guid.NewGuid().ToString("N"), PreparationTransport.Session,
                "Outbound", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            WriteBridgeRequest(directory, request);
            await WaitUntilAsync(() => File.Exists(responsePath));
            var response = JsonSerializer.Deserialize<GuiSmokeSessionPreparationBridge.Response>(File.ReadAllText(responsePath), BridgeJson)!;
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.Equal(boot, response.Identity.BootId);
            Assert.Equal(Environment.ProcessId, response.Identity.ProcessId);
            Assert.Equal(binding.Epoch.Id, response.Identity.Scope!.Incarnation);
            Assert.True(response.CapturedUtcMs >= request.RequestedUtcMs);
            Assert.True(response.Observation.Ready);
            var bytes = File.ReadAllText(responsePath);
            var reads = transport.LeaseReads;
            // Polling the same request must not recapture or extend its answer.
            typeof(GuiSmokeSessionPreparationBridge).GetMethod("Poll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(bridge, null);
            Assert.Equal(bytes, File.ReadAllText(responsePath));
            Assert.Equal(reads, transport.LeaseReads);
            Assert.Same(before, ServiceField(service, "sessionPreparation"));
            bridge!.Dispose();
            WriteBridgeRequest(directory, request with { RequestId = Guid.NewGuid().ToString("N") });
            typeof(GuiSmokeSessionPreparationBridge).GetMethod("Poll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(bridge, null);
            Assert.Equal(bytes, File.ReadAllText(responsePath));
            Assert.Empty(transport.SentOffers);
        });

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("path")]
    [InlineData("direction")]
    [InlineData("future")]
    [InlineData("io")]
    public Task PreparationDiagnosticBridge_InvalidRequestsAndIoFailureDoNotTouchRuntime(string failure)
        => WithPreparationBridgeEnvironment(async (root, boot) =>
        {
            using var transport = new PreparationTransport();
            using var service = new SessionFileTransferService();
            service.AttachTransport(transport);
            transport.Install();
            Prepare(transport, true);
            var before = ServiceField(service, "sessionPreparation");
            using var bridge = GuiSmokeSessionPreparationBridge.TryStart(service, () => "Helpee");
            var directory = Path.Combine(root, $"{Environment.ProcessId}-{boot}");
            await WaitUntilAsync(() => File.Exists(Path.Combine(directory, "identity.json")));
            var request = new GuiSmokeSessionPreparationBridge.Request(Guid.NewGuid().ToString("N"), PreparationTransport.Session,
                failure == "direction" ? "../../secret" : "Outbound", DateTimeOffset.UtcNow.AddMinutes(failure == "future" ? 1 : 0).ToUnixTimeMilliseconds());
            var json = JsonSerializer.Serialize(request, BridgeJson);
            if (failure == "malformed") json = "{";
            if (failure == "oversized") json = new string(' ', 4097);
            if (failure == "path") json = json[..^1] + ",\"path\":\"../../secret\"}";
            if (failure == "io") Directory.CreateDirectory(Path.Combine(directory, "request.json"));
            else File.WriteAllText(Path.Combine(directory, "request.json"), json);
            typeof(GuiSmokeSessionPreparationBridge).GetMethod("Poll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(bridge, null);
            Assert.False(File.Exists(Path.Combine(directory, "response.json")));
            Assert.Equal(0, transport.LeaseReads);
            Assert.Same(before, ServiceField(service, "sessionPreparation"));
        });

    private static readonly JsonSerializerOptions BridgeJson = new(JsonSerializerDefaults.Web);
    private static void WriteBridgeRequest(string directory, GuiSmokeSessionPreparationBridge.Request request)
    {
        var path = Path.Combine(directory, "request.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(request, BridgeJson));
        File.Move(path + ".tmp", path, true);
    }

    private static async Task WithPreparationBridgeEnvironment(Func<string, string, Task> action)
    {
        var names = new[] { "NLINK_GUI_SMOKE_SCENARIOS", GuiSmokeSessionPreparationBridge.RootVariable, GuiSmokeSessionPreparationBridge.BootVariable };
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        var root = Path.Combine(Path.GetTempPath(), "nlink-preparation-observation-" + Guid.NewGuid().ToString("N"));
        var boot = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable(names[0], "preparation-diagnostic-test");
            Environment.SetEnvironmentVariable(names[1], root);
            Environment.SetEnvironmentVariable(names[2], boot);
            await action(root, boot);
        }
        finally
        {
            for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], previous[i]);
            Directory.Delete(root, true);
        }
    }
}
