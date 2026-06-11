using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Core;
using NLink.Core.Diagnostics;
using NLink.Infra.Nkn;
using System.Diagnostics;
using System.Reflection;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class TunaWalletDiagnosticsTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaWalletLinkStore_PersistsLinkedPathOnlyWithoutSecrets()
    {
        var root = CreateTempRoot();
        try
        {
            var walletPath = Path.Combine(root, "wallet-test-nkn.json");
            var storePath = Path.Combine(root, "state", "tuna-wallet-link.json");
            var store = new JsonTunaWalletLinkStore(() => storePath);

            await store.SaveAsync(TunaWalletLinkState.Linked(walletPath, DateTimeOffset.Parse("2026-05-05T10:00:00Z")));

            var content = await File.ReadAllTextAsync(storePath);
            var loaded = await store.LoadAsync();
            Assert.Equal(Path.GetFullPath(walletPath), loaded.WalletPath);
            Assert.Contains("wallet-test-nkn.json", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seed", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task DiagnosticsTunaWallet_LinkValidateAndUnlink_RedactsShareableCopy()
    {
        var root = CreateTempRoot();
        var previousTunaEnabled = Environment.GetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED");
        try
        {
            PersistenceDiagnostics.ClearForTests();
            SessionTimeline.Clear();
            Environment.SetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED", null);
            var walletPath = Path.Combine(root, "wallet-test-nkn.json");
            await File.WriteAllTextAsync(walletPath, "{}");
            var store = new JsonTunaWalletLinkStore(() => Path.Combine(root, "state.json"));
            var verifier = new FakeTunaWalletVerifier(
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"));
            var vm = CreateViewModel(store, verifier);

            await vm.LinkTunaWalletAsync(walletPath);

            Assert.True(vm.IsTunaWalletLinked);
            Assert.Equal("wallet-test-nkn.json", vm.TunaWalletFileName);
            Assert.Equal("Linked, not verified", vm.TunaWalletStatus);
            Assert.True(vm.ValidateTunaWalletCommand.CanExecute(null));
            Assert.False(vm.CopyTunaWalletAddressCommand.CanExecute(null));

            var password = "session-only".ToCharArray();
            await vm.ValidateTunaWalletAsync(password);

            Assert.All(password, c => Assert.Equal('\0', c));
            Assert.Equal("Verified, funded", vm.TunaWalletStatus);
            Assert.Equal("funded", vm.TunaWalletBalanceCategory);
            Assert.Equal("NKN0123456789PUBLICADDRESS", vm.TunaWalletAddress);
            Assert.True(vm.CopyTunaWalletAddressCommand.CanExecute(null));
            Assert.Single(verifier.PasswordsSeen);
            Assert.Equal("session-only", new string(verifier.PasswordsSeen[0]));

            var copied = vm.BuildDiagnosticsCopyTextForTests();
            Assert.Contains("tuna_runtime_flag: Off", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_fallback_state: Current NKN will be used.", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_wallet_file: wallet-test-nkn.json", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_wallet_balance_category: funded", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_wallet_path: [REDACTED]", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_wallet_address: [REDACTED]", copied, StringComparison.Ordinal);
            Assert.DoesNotContain(root, copied, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("NKN0123456789PUBLICADDRESS", copied, StringComparison.Ordinal);
            Assert.DoesNotContain("session-only", copied, StringComparison.Ordinal);

            await vm.UnlinkTunaWalletAsync();
            Assert.False(vm.IsTunaWalletLinked);
            Assert.Equal("Not linked", vm.TunaWalletStatus);
            Assert.False(File.Exists(Path.Combine(root, "state.json")));
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
            SessionTimeline.Clear();
            Environment.SetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED", previousTunaEnabled);
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task DiagnosticsTunaWallet_ValidationFailureIsNonFatalAndDoesNotPersistPassword()
    {
        var root = CreateTempRoot();
        try
        {
            var walletPath = Path.Combine(root, "wallet.json");
            await File.WriteAllTextAsync(walletPath, "{}");
            var store = new JsonTunaWalletLinkStore(() => Path.Combine(root, "state.json"));
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Fail("wrong_password"));
            var vm = CreateViewModel(store, verifier);

            await vm.LinkTunaWalletAsync(walletPath);
            var password = "bad-password".ToCharArray();
            await vm.ValidateTunaWalletAsync(password);

            Assert.All(password, c => Assert.Equal('\0', c));
            Assert.Equal("Validation failed", vm.TunaWalletStatus);
            Assert.True(vm.ShowTunaWalletFailure);
            Assert.Equal("wrong_password", vm.TunaWalletLastFailure);

            var stateText = await File.ReadAllTextAsync(Path.Combine(root, "state.json"));
            Assert.DoesNotContain("bad-password", stateText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void DiagnosticsTunaWallet_SidecarCompatibilityStatusUsesFriendlyLabelsAndRedactedExport()
    {
        var root = CreateTempRoot();
        try
        {
            var cases = new[]
            {
                (
                    Availability: new TunaWalletVerifierAvailability(false, "sidecar_missing", Path.Combine(root, "tuna", "win-x64", "nlink-tuna-sidecar.exe"))
                    {
                        ManifestStatus = "sidecar_missing",
                        Detail = "Missing: expected nlink-tuna-sidecar.exe.",
                    },
                    ExpectedStatus: "Missing"
                ),
                (
                    Availability: new TunaWalletVerifierAvailability(false, "sidecar_version_mismatch", Path.Combine(root, "tuna", "win-x64", "nlink-tuna-sidecar.exe"))
                    {
                        ManifestPath = Path.Combine(root, "tuna", "win-x64", "tuna-sidecar-manifest.json"),
                        ActualSidecarVersion = "0.6.9",
                        ActualRuntime = "win-x64",
                        ActualAppProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                        ActualFrameProtocolVersion = NknTunaSidecarFrameProtocol.ProtocolVersion,
                        ManifestStatus = "sidecar_version_mismatch",
                        Detail = $"Wrong version: expected {NknTunaSidecarCompatibility.ExpectedSidecarVersion}, found 0.6.9.",
                    },
                    ExpectedStatus: "Wrong version"
                ),
                (
                    Availability: new TunaWalletVerifierAvailability(false, "sidecar_protocol_mismatch", Path.Combine(root, "tuna", "win-x64", "nlink-tuna-sidecar.exe"))
                    {
                        ManifestPath = Path.Combine(root, "tuna", "win-x64", "tuna-sidecar-manifest.json"),
                        ActualSidecarVersion = NknTunaSidecarCompatibility.ExpectedSidecarVersion,
                        ActualRuntime = "win-x64",
                        ActualAppProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion + 1,
                        ActualFrameProtocolVersion = NknTunaSidecarFrameProtocol.ProtocolVersion,
                        ManifestStatus = "sidecar_protocol_mismatch",
                        Detail = "Protocol mismatch: packaged Tuna sidecar is not compatible with this app.",
                    },
                    ExpectedStatus: "Protocol mismatch"
                ),
                (
                    Availability: new TunaWalletVerifierAvailability(false, "sidecar_manifest_hash_mismatch", Path.Combine(root, "tuna", "win-x64", "nlink-tuna-sidecar.exe"))
                    {
                        ManifestPath = Path.Combine(root, "tuna", "win-x64", "tuna-sidecar-manifest.json"),
                        ActualSidecarVersion = NknTunaSidecarCompatibility.ExpectedSidecarVersion,
                        ActualRuntime = "win-x64",
                        ActualAppProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                        ActualFrameProtocolVersion = NknTunaSidecarFrameProtocol.ProtocolVersion,
                        ManifestStatus = "sidecar_manifest_hash_mismatch",
                        Detail = "Manifest invalid: sidecar hash does not match the packaged manifest.",
                    },
                    ExpectedStatus: "Manifest invalid"
                ),
            };

            foreach (var testCase in cases)
            {
                var store = new JsonTunaWalletLinkStore(() => Path.Combine(root, Guid.NewGuid().ToString("N") + ".json"));
                var verifier = new FakeTunaWalletVerifier(
                    TunaWalletValidationResult.Fail("not_used"),
                    availability: testCase.Availability);
                var vm = CreateViewModel(store, verifier);

                Assert.Equal(testCase.ExpectedStatus, vm.TunaSidecarVerifierStatus);
                Assert.Equal(testCase.Availability.Detail, vm.TunaSidecarVerifierDetail);
            }

            var exportAvailability = new TunaWalletVerifierAvailability(false, "sidecar_version_mismatch", Path.Combine(root, "tuna", "win-x64", "nlink-tuna-sidecar.exe"))
            {
                ManifestPath = Path.Combine(root, "tuna", "win-x64", "tuna-sidecar-manifest.json"),
                ActualSidecarVersion = "0.6.9",
                ActualRuntime = "win-x64",
                ActualAppProtocolVersion = 77,
                ActualFrameProtocolVersion = 88,
                ManifestStatus = "sidecar_version_mismatch",
                Detail = "Wrong version: expected current, found 0.6.9.",
            };
            var exportVerifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Fail("not_used"), availability: exportAvailability);
            var exportVm = CreateViewModel(new JsonTunaWalletLinkStore(() => Path.Combine(root, "export.json")), exportVerifier);
            var copied = exportVm.BuildDiagnosticsCopyTextForTests();

            Assert.Contains("tuna_sidecar_actual_app_protocol: 77", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_sidecar_actual_frame_protocol: 88", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_sidecar_actual_version: 0.6.9", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_sidecar_actual_runtime: win-x64", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_sidecar_manifest_status: sidecar_version_mismatch", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_sidecar_path: [REDACTED]", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_sidecar_manifest_path: [REDACTED]", copied, StringComparison.Ordinal);
            Assert.DoesNotContain(root, copied, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaRuntimePreferenceStore_PersistsOptInCapsWithoutSecrets()
    {
        var root = CreateTempRoot();
        try
        {
            var storePath = Path.Combine(root, "runtime", "tuna-runtime-preferences.json");
            var store = new JsonTunaRuntimePreferenceStore(() => storePath);

            store.Save(new TunaRuntimePreferenceState
            {
                Enabled = true,
                FileLaneEnabled = true,
                ScreenLaneEnabled = false,
                MaxPriceNknPerMb = "0.0002000",
                MaxTotalMiB = 2048,
                MaxDurationSec = 1800,
                AllowDegradedProviderReady = true,
                LastRuntimeStatus = "locked",
            });

            var loaded = store.Load();
            var content = File.ReadAllText(storePath);

            Assert.True(loaded.Enabled);
            Assert.True(loaded.FileLaneEnabled);
            Assert.False(loaded.ScreenLaneEnabled);
            Assert.Equal("0.0002", loaded.MaxPriceNknPerMb);
            Assert.Equal(2048, loaded.MaxTotalMiB);
            Assert.Equal(1800, loaded.MaxDurationSec);
            Assert.True(loaded.AllowDegradedProviderReady);
            Assert.DoesNotContain("password", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seed", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaUsageAccountingStore_PersistsLocalSpendAndAverageCost()
    {
        var root = CreateTempRoot();
        try
        {
            var storePath = Path.Combine(root, "usage", "tuna-usage-accounting.json");
            var store = new JsonTunaUsageAccountingStore(() => storePath);
            var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
            var usage = TunaUsageAccountingState.Empty
                .StartNewSession()
                .AddPayment(0.006m, now)
                .AddPayment(0.004m, now)
                .CompleteSession(100_000_000, paymentTelemetryObserved: true, now);

            store.Save(usage);
            var loaded = store.Load();
            var content = File.ReadAllText(storePath);

            Assert.Equal(0.010m, loaded.TotalPaidNkn);
            Assert.Equal(100m, loaded.TotalAppPayloadMb);
            Assert.Equal(0.0001m, loaded.AverageNknPerMb);
            Assert.Equal(0.010m, loaded.LastSessionPaidNkn);
            Assert.Equal(100m, loaded.LastSessionAppPayloadMb);
            Assert.Equal(0.0001m, loaded.LastSessionAverageNknPerMb);
            Assert.False(loaded.HasUnknownCost);
            Assert.False(loaded.LastSessionCostUnknown);
            var session = Assert.Single(loaded.SessionRecords);
            Assert.Equal(TunaPaymentTelemetryStatus.Reported, session.PaymentTelemetryStatus);
            Assert.Equal(2, session.PaymentEventCount);
            Assert.Equal(100_000_000, session.BytesMoved);
            Assert.Equal(100m, session.AppPayloadMb);
            Assert.Equal(0.010m, session.PaidNkn);
            Assert.True(session.CompletedFromSummary);
            Assert.DoesNotContain("wallet", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("seed", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaUsageAccountingStore_MarksMovedBytesWithoutPaymentTelemetryAsUnknownCost()
    {
        var root = CreateTempRoot();
        try
        {
            var storePath = Path.Combine(root, "usage", "tuna-usage-accounting.json");
            var store = new JsonTunaUsageAccountingStore(() => storePath);
            var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
            var usage = TunaUsageAccountingState.Empty
                .StartNewSession()
                .CompleteSession(61_112_208, paymentTelemetryObserved: false, now);

            store.Save(usage);
            var loaded = store.Load();

            Assert.Equal(0m, loaded.TotalPaidNkn);
            Assert.Equal(61.112208m, loaded.TotalAppPayloadMb);
            Assert.True(loaded.HasUnknownCost);
            Assert.True(loaded.LastSessionCostUnknown);
            var session = Assert.Single(loaded.SessionRecords);
            Assert.Equal(TunaPaymentTelemetryStatus.NoPaymentTelemetryReported, session.PaymentTelemetryStatus);
            Assert.Equal(61.112208m, session.AppPayloadMb);
            Assert.Equal(0, session.PaymentEventCount);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaUsageAccountingStore_ZeroByteSummaryWithoutPaymentDoesNotWarnAboutUnknownCost()
    {
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");

        var usage = TunaUsageAccountingState.Empty
            .StartNewSession("run-zero", "listener", now)
            .CompleteSession("run-zero", 0, paymentTelemetryObserved: false, null, "none", 0, "normal_close", string.Empty, string.Empty, completedFromSummary: true, now);

        Assert.Equal(0m, usage.TotalPaidNkn);
        Assert.Equal(0m, usage.TotalAppPayloadMb);
        Assert.False(usage.HasUnknownCost);
        Assert.False(usage.LastSessionCostUnknown);
        Assert.Equal(TunaPaymentTelemetryStatus.None, Assert.Single(usage.SessionRecords).PaymentTelemetryStatus);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaUsageAccountingState_CumulativeSummaryAddsOnlyUnseenPaymentDelta()
    {
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");

        var usage = TunaUsageAccountingState.Empty
            .StartNewSession("run-cumulative", "listener", now)
            .AddPayment("run-cumulative", 0.006m, 10_000_000, now)
            .CompleteSession(
                "run-cumulative",
                20_000_000,
                paymentTelemetryObserved: true,
                cumulativeSpendNkn: 0.010m,
                paymentTelemetryStatus: TunaPaymentTelemetryStatus.Reported,
                paymentEventCount: 1,
                stopReason: "normal_close",
                capReason: string.Empty,
                fallbackReason: "connection_closed",
                completedFromSummary: true,
                now);

        Assert.Equal(0.010m, usage.TotalPaidNkn);
        Assert.Equal(20m, usage.TotalAppPayloadMb);
        Assert.Equal(20m, usage.TotalKnownAppPayloadMb);
        Assert.Equal(0.0005m, usage.AverageNknPerMb);
        var session = Assert.Single(usage.SessionRecords);
        Assert.Equal(0.010m, session.PaidNkn);
        Assert.Equal(20m, session.AppPayloadMb);
        Assert.Equal(TunaPaymentTelemetryStatus.Reported, session.PaymentTelemetryStatus);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaUsageAccountingState_IncompleteSessionRecordsAccountingIncomplete()
    {
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");

        var usage = TunaUsageAccountingState.Empty
            .StartNewSession("run-incomplete", "listener", now)
            .CompleteSession(
                "run-incomplete",
                12_500_000,
                paymentTelemetryObserved: false,
                cumulativeSpendNkn: null,
                paymentTelemetryStatus: TunaPaymentTelemetryStatus.AccountingIncomplete,
                paymentEventCount: 0,
                stopReason: "sidecar_exited_before_summary",
                capReason: string.Empty,
                fallbackReason: "sidecar_exited_before_summary",
                completedFromSummary: false,
                now);

        var session = Assert.Single(usage.SessionRecords);
        Assert.Equal(TunaPaymentTelemetryStatus.AccountingIncomplete, session.PaymentTelemetryStatus);
        Assert.False(session.CompletedFromSummary);
        Assert.Equal("sidecar_exited_before_summary", session.StopReason);
        Assert.True(usage.HasUnknownCost);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaUsageAccountingState_RetainsNewestOneHundredSessionRecords()
    {
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
        var usage = TunaUsageAccountingState.Empty;

        for (var i = 0; i < 105; i++)
        {
            var runId = $"run-{i:000}";
            usage = usage
                .StartNewSession(runId, "listener", now.AddSeconds(i))
                .CompleteSession(runId, 0, paymentTelemetryObserved: false, null, "none", 0, "normal_close", string.Empty, string.Empty, completedFromSummary: true, now.AddSeconds(i));
        }

        Assert.Equal(100, usage.SessionRecords.Count);
        Assert.Equal("run-005", usage.SessionRecords[0].SessionRunId);
        Assert.Equal("run-104", usage.SessionRecords[^1].SessionRunId);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaUsageAccountingState_UsesPaymentBytesWhenFinalSummaryHasNoBytes()
    {
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");

        var usage = TunaUsageAccountingState.Empty
            .StartNewSession()
            .AddPayment(0.006m, now)
            .CompleteSession(10_000_000, paymentTelemetryObserved: true, now)
            .AddPayment(0.004m, now)
            .CompleteSession(61_112_208, paymentTelemetryObserved: true, now)
            .CompleteSession(0, paymentTelemetryObserved: true, now);

        Assert.Equal(0.010m, usage.TotalPaidNkn);
        Assert.Equal(61.112208m, usage.TotalAppPayloadMb);
        Assert.Equal(61.112208m, usage.LastSessionAppPayloadMb);
        Assert.Equal(0.010m, usage.LastSessionPaidNkn);
        Assert.False(usage.HasUnknownCost);
        Assert.False(usage.LastSessionCostUnknown);
        Assert.Equal(TunaPaymentTelemetryStatus.Reported, Assert.Single(usage.SessionRecords).PaymentTelemetryStatus);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task DiagnosticsTunaRuntimeOptIn_RequiresToggleAndSessionUnlockDoesNotPersistPassword()
    {
        var root = CreateTempRoot();
        var previousTunaEnabled = Environment.GetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED", null);
            var walletPath = Path.Combine(root, "wallet-test-nkn.json");
            await File.WriteAllTextAsync(walletPath, "{}");
            var walletStorePath = Path.Combine(root, "tuna-wallet-link.json");
            var preferenceStorePath = Path.Combine(root, "tuna-runtime-preferences.json");
            var usageStorePath = Path.Combine(root, "tuna-usage-accounting.json");
            var walletStore = new JsonTunaWalletLinkStore(() => walletStorePath);
            var preferenceStore = new JsonTunaRuntimePreferenceStore(() => preferenceStorePath);
            var usageStore = new JsonTunaUsageAccountingStore(() => usageStorePath);
            var verifier = new FakeTunaWalletVerifier(
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"));
            var runtimeService = new TunaRuntimePilotService(preferenceStore, usageStore, walletStore, verifier);
            var vm = CreateViewModel(walletStore, verifier, runtimeService);

            await vm.LinkTunaWalletAsync(walletPath);
            var validationPassword = "validation-pass".ToCharArray();
            await vm.ValidateTunaWalletAsync(validationPassword);

            Assert.All(validationPassword, c => Assert.Equal('\0', c));
            Assert.Equal("Off", vm.TunaRuntimeFlagStatus);
            Assert.False(vm.IsTunaRuntimeEnabled);
            Assert.False(runtimeService.Preferences.Enabled);
            Assert.False(runtimeService.HasSessionUnlock);
            Assert.False(vm.UnlockTunaRuntimeCommand.CanExecute(null));

            vm.IsTunaRuntimeEnabled = true;

            Assert.Equal("Advanced opt-in", vm.TunaRuntimeFlagStatus);
            Assert.True(vm.UnlockTunaRuntimeCommand.CanExecute(null));
            Assert.Equal("0.0002", vm.TunaMaxPriceNknPerMb);
            Assert.Equal("2048", vm.TunaMaxTotalMiB);
            Assert.Equal("30", vm.TunaMaxDurationMinutes);

            var runtimePassword = "runtime-pass".ToCharArray();
            await vm.UnlockTunaRuntimeAsync(runtimePassword);

            Assert.All(runtimePassword, c => Assert.Equal('\0', c));
            Assert.True(runtimeService.HasSessionUnlock);
            Assert.Equal("Unlocked for next session", vm.TunaRuntimeUnlockStatus);
            Assert.Equal("waiting_for_approved_session", runtimeService.RuntimeStatus);
            Assert.Equal("Tuna is unlocked and waiting for an approved session.", vm.TunaCurrentState);
            Assert.Contains("waiting for approved session", vm.TunaStartupTiming, StringComparison.Ordinal);
            Assert.Equal(2, verifier.PasswordsSeen.Count);
            Assert.Equal("runtime-pass", new string(verifier.PasswordsSeen[1]));

            var walletState = await File.ReadAllTextAsync(walletStorePath);
            var runtimeState = await File.ReadAllTextAsync(preferenceStorePath);
            Assert.DoesNotContain("validation-pass", walletState, StringComparison.Ordinal);
            Assert.DoesNotContain("runtime-pass", walletState, StringComparison.Ordinal);
            Assert.DoesNotContain("runtime-pass", runtimeState, StringComparison.Ordinal);
            Assert.DoesNotContain("seed", runtimeState, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private", runtimeState, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED", previousTunaEnabled);
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task DiagnosticsTunaRuntimeUnlock_WrongPasswordDoesNotInvalidateVerifiedWallet()
    {
        var root = CreateTempRoot();
        var previousTunaEnabled = Environment.GetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED", null);
            var walletPath = Path.Combine(root, "wallet-test-nkn.json");
            await File.WriteAllTextAsync(walletPath, "{}");
            var walletStore = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
            var preferenceStore = new JsonTunaRuntimePreferenceStore(() => Path.Combine(root, "tuna-runtime-preferences.json"));
            var usageStore = new JsonTunaUsageAccountingStore(() => Path.Combine(root, "tuna-usage-accounting.json"));
            var verifier = new SequenceTunaWalletVerifier(
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"),
                TunaWalletValidationResult.Fail("wallet password verification failed", "wallet-test-nkn.json"),
                TunaWalletValidationResult.Fail("wallet password verification failed", "wallet-test-nkn.json"));
            var runtimeService = new TunaRuntimePilotService(preferenceStore, usageStore, walletStore, verifier);
            var vm = CreateViewModel(walletStore, verifier, runtimeService);

            await vm.LinkTunaWalletAsync(walletPath);
            var validationPassword = "correct-pass".ToCharArray();
            await vm.ValidateTunaWalletAsync(validationPassword);
            vm.IsTunaRuntimeEnabled = true;

            var wrongPassword = "wrong-pass".ToCharArray();
            await vm.UnlockTunaRuntimeAsync(wrongPassword);

            Assert.All(validationPassword, c => Assert.Equal('\0', c));
            Assert.All(wrongPassword, c => Assert.Equal('\0', c));
            Assert.False(runtimeService.HasSessionUnlock);
            Assert.Equal("unlock_failed_wrong_password", runtimeService.RuntimeStatus);
            Assert.Equal("Verified, funded", vm.TunaWalletStatus);
            Assert.True(vm.UnlockTunaRuntimeCommand.CanExecute(null));

            var persisted = await walletStore.LoadAsync();
            Assert.Equal(TunaWalletLinkStatus.VerifiedFunded, persisted.Status);
            Assert.Equal("NKN0123456789PUBLICADDRESS", persisted.WalletAddress);
            Assert.Equal("1.2500", persisted.BalanceNkn);
            Assert.Equal(2, verifier.PasswordsSeen.Count);
            Assert.Equal("wrong-pass", new string(verifier.PasswordsSeen[1]));

            var secondWrongPassword = "wrong-pass-2".ToCharArray();
            await vm.UnlockTunaRuntimeAsync(secondWrongPassword);

            Assert.All(secondWrongPassword, c => Assert.Equal('\0', c));
            Assert.False(runtimeService.HasSessionUnlock);
            Assert.Equal("unlock_failed_wrong_password", runtimeService.RuntimeStatus);
            Assert.Equal("Verified, funded", vm.TunaWalletStatus);
            Assert.False(vm.UnlockTunaRuntimeCommand.CanExecute(null));
            Assert.Contains("Try again in", vm.TunaRuntimeUnlockStatus, StringComparison.Ordinal);
            Assert.Equal(3, verifier.PasswordsSeen.Count);

            var blockedPassword = "blocked-pass".ToCharArray();
            await vm.UnlockTunaRuntimeAsync(blockedPassword);

            Assert.All(blockedPassword, c => Assert.Equal('\0', c));
            Assert.Equal(3, verifier.PasswordsSeen.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED", previousTunaEnabled);
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeUnlockCoordinator_CooldownIsSharedAcrossOptionsAndHeader()
    {
        var root = CreateTempRoot();
        try
        {
            var walletPath = Path.Combine(root, "wallet-test-nkn.json");
            await File.WriteAllTextAsync(walletPath, "{}");
            var walletStore = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
            await walletStore.SaveAsync(TunaWalletLinkState.Linked(walletPath, DateTimeOffset.Parse("2026-05-05T10:00:00Z"))
                .WithValidationResult(
                    TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"),
                    DateTimeOffset.Parse("2026-05-05T10:01:00Z")));
            var preferenceStore = new JsonTunaRuntimePreferenceStore(() => Path.Combine(root, "tuna-runtime-preferences.json"));
            preferenceStore.Save(new TunaRuntimePreferenceState
            {
                Enabled = true,
                FileLaneEnabled = true,
                ScreenLaneEnabled = true,
                MaxPriceNknPerMb = TunaRuntimePreferenceState.DefaultMaxPriceNknPerMb,
                MaxTotalMiB = TunaRuntimePreferenceState.DefaultMaxTotalMiB,
                MaxDurationSec = TunaRuntimePreferenceState.DefaultMaxDurationSec,
                LastRuntimeStatus = "locked",
            });
            var usageStore = new JsonTunaUsageAccountingStore(() => Path.Combine(root, "tuna-usage-accounting.json"));
            var verifier = new SequenceTunaWalletVerifier(
                TunaWalletValidationResult.Fail("wallet password verification failed", "wallet-test-nkn.json"),
                TunaWalletValidationResult.Fail("wallet password verification failed", "wallet-test-nkn.json"),
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"));
            var runtimeService = new TunaRuntimePilotService(preferenceStore, usageStore, walletStore, verifier);

            var first = await runtimeService.UnlockForSessionAsync(
                "wrong-pass-1".ToCharArray(),
                TunaRuntimeUnlockSource.Header);
            var second = await runtimeService.UnlockForSessionAsync(
                "wrong-pass-2".ToCharArray(),
                TunaRuntimeUnlockSource.Options);
            var blocked = await runtimeService.UnlockForSessionAsync(
                "correct-but-blocked".ToCharArray(),
                TunaRuntimeUnlockSource.Header);

            Assert.False(first.Success);
            Assert.False(first.IsCooldownActive);
            Assert.False(second.Success);
            Assert.True(second.IsCooldownActive);
            Assert.False(blocked.Success);
            Assert.True(blocked.IsCooldownActive);
            Assert.Equal(2, verifier.PasswordsSeen.Count);
            Assert.False(runtimeService.HasSessionUnlock);

            var persisted = await walletStore.LoadAsync();
            Assert.Equal(TunaWalletLinkStatus.VerifiedFunded, persisted.Status);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeUnlockCoordinator_DebouncesConcurrentUnlockAttempts()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new BlockingTunaWalletVerifier(
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);

            var firstPassword = "first-pass".ToCharArray();
            var firstUnlock = context.RuntimeService.UnlockForSessionAsync(
                firstPassword,
                TunaRuntimeUnlockSource.Header,
                CancellationToken.None);
            await WaitUntilAsync(() => verifier.PasswordsSeen.Count == 1, TimeSpan.FromSeconds(2));

            var secondPassword = "second-pass".ToCharArray();
            var secondResult = await context.RuntimeService.UnlockForSessionAsync(
                secondPassword,
                TunaRuntimeUnlockSource.Options,
                CancellationToken.None);

            Assert.False(secondResult.Success);
            Assert.Contains("already in progress", secondResult.Message, StringComparison.OrdinalIgnoreCase);
            Assert.All(secondPassword, c => Assert.Equal('\0', c));
            Assert.Single(verifier.PasswordsSeen);

            verifier.Release();
            var firstResult = await firstUnlock.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(firstResult.Success);
            Assert.True(context.RuntimeService.HasSessionUnlock);
            Assert.All(firstPassword, c => Assert.Equal('\0', c));
            Assert.Single(verifier.PasswordsSeen);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeUnlockCoordinator_LockWhileWaitingClearsSessionUnlock()
    {
        var root = CreateTempRoot();
        try
        {
            var walletPath = Path.Combine(root, "wallet-test-nkn.json");
            await File.WriteAllTextAsync(walletPath, "{}");
            var walletStore = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
            await walletStore.SaveAsync(TunaWalletLinkState.Linked(walletPath, DateTimeOffset.Parse("2026-05-05T10:00:00Z"))
                .WithValidationResult(
                    TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"),
                    DateTimeOffset.Parse("2026-05-05T10:01:00Z")));
            var preferenceStore = new JsonTunaRuntimePreferenceStore(() => Path.Combine(root, "tuna-runtime-preferences.json"));
            preferenceStore.Save(new TunaRuntimePreferenceState
            {
                Enabled = true,
                FileLaneEnabled = true,
                ScreenLaneEnabled = true,
                MaxPriceNknPerMb = TunaRuntimePreferenceState.DefaultMaxPriceNknPerMb,
                MaxTotalMiB = TunaRuntimePreferenceState.DefaultMaxTotalMiB,
                MaxDurationSec = TunaRuntimePreferenceState.DefaultMaxDurationSec,
                LastRuntimeStatus = "locked",
            });
            var usageStore = new JsonTunaUsageAccountingStore(() => Path.Combine(root, "tuna-usage-accounting.json"));
            var verifier = new FakeTunaWalletVerifier(
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"));
            var runtimeService = new TunaRuntimePilotService(preferenceStore, usageStore, walletStore, verifier);

            var unlock = await runtimeService.UnlockForSessionAsync(
                "runtime-pass".ToCharArray(),
                TunaRuntimeUnlockSource.Options);
            var locked = await runtimeService.LockOrStopForSessionAsync(
                "test_lock",
                TunaRuntimeUnlockSource.Header);

            Assert.True(unlock.Success);
            Assert.True(locked.Success);
            Assert.False(runtimeService.HasSessionUnlock);
            Assert.Equal("locked", runtimeService.RuntimeStatus);
            Assert.Equal("Locked", (await runtimeService.GetUnlockStateAsync()).StatusText);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeListenerSupervisor_CanOfferListenerOnlyWhileUnlockedOrRunning()
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(
                root,
                new FakeTunaWalletVerifier(
                    TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500")));
            using var supervisor = context.RuntimeService.CreateRuntimeListenerSupervisorForTests();

            Assert.False(supervisor.CanOfferListener);

            var unlock = await context.RuntimeService.UnlockForSessionAsync(
                "runtime-pass".ToCharArray(),
                TunaRuntimeUnlockSource.Header);

            Assert.True(unlock.Success);
            Assert.True(supervisor.CanOfferListener);

            await context.RuntimeService.LockOrStopForSessionAsync(
                "header_switch_off",
                TunaRuntimeUnlockSource.Header);

            Assert.False(supervisor.CanOfferListener);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaRuntimeListenerSupervisor_RetainsUnlockAcrossListenerAcceptWindow()
    {
        Assert.True(
            TunaRuntimePilotService.ListenerRestartUnlockRetentionForTests >= TimeSpan.FromSeconds(150),
            "Listener restart retention must cover the sidecar accept timeout plus startup/recovery margin.");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeUnlockCoordinator_EmptyPasswordDoesNotValidateOrStartCooldown()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new SequenceTunaWalletVerifier(
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);

            var result = await context.RuntimeService.UnlockForSessionAsync(
                [],
                TunaRuntimeUnlockSource.Header);
            var state = await context.RuntimeService.GetUnlockStateAsync();

            Assert.False(result.Success);
            Assert.Equal("Password required.", result.Message);
            Assert.False(result.IsCooldownActive);
            Assert.False(state.IsCooldownActive);
            Assert.True(state.CanToggle);
            Assert.False(context.RuntimeService.HasSessionUnlock);
            Assert.Empty(verifier.PasswordsSeen);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeUnlockCoordinator_LockWhileActiveStopsAccelerationOnce()
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(
                root,
                new FakeTunaWalletVerifier(
                    TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500")));
            var control = new RecordingTransportAccelerationControl();
            SetPrivateField(context.RuntimeService, "currentTransportControl", control);
            SetPrivateField(context.RuntimeService, "runtimeStatus", "active");

            var first = await context.RuntimeService.LockOrStopForSessionAsync(
                "header_switch_off",
                TunaRuntimeUnlockSource.Header);
            var second = await context.RuntimeService.LockOrStopForSessionAsync(
                "header_switch_off",
                TunaRuntimeUnlockSource.Header);

            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.False(context.RuntimeService.HasSessionUnlock);
            await WaitUntilAsync(
                () => context.RuntimeService.RuntimeStatus == "locked" && control.StopCalls == 1,
                TimeSpan.FromSeconds(2));
            Assert.Equal("locked", context.RuntimeService.RuntimeStatus);
            Assert.Equal(1, control.StopCalls);
            Assert.Equal("header_switch_off", control.StopReasons.Single());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeUnlockCoordinator_ActiveRuntimeCanToggleOffWhenUnlockAttemptFlagIsStale()
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(
                root,
                new FakeTunaWalletVerifier(
                    TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500")));
            var control = new RecordingTransportAccelerationControl();
            SetPrivateField(context.RuntimeService, "currentTransportControl", control);
            SetPrivateField(context.RuntimeService, "runtimeStatus", "active");
            SetPrivateField(context.RuntimeService, "unlockAttemptInProgress", 1);

            var state = await context.RuntimeService.GetUnlockStateAsync();

            Assert.True(state.IsVisible);
            Assert.True(state.IsOn);
            Assert.True(state.CanToggle);

            var result = await context.RuntimeService.LockOrStopForSessionAsync(
                "header_switch_off",
                TunaRuntimeUnlockSource.Header);

            Assert.True(result.Success);
            await WaitUntilAsync(
                () => context.RuntimeService.RuntimeStatus == "locked" && control.StopCalls == 1,
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeUnlockCoordinator_HangingStopDoesNotPinHeaderToggle()
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(
                root,
                new FakeTunaWalletVerifier(
                    TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500")),
                stopCompletionTimeout: TimeSpan.FromMilliseconds(50));
            var control = new RecordingTransportAccelerationControl { HangStop = true };
            SetPrivateField(context.RuntimeService, "currentTransportControl", control);
            SetPrivateField(context.RuntimeService, "runtimeStatus", "active");

            var result = await context.RuntimeService.LockOrStopForSessionAsync(
                "header_switch_off",
                TunaRuntimeUnlockSource.Header);

            Assert.True(result.Success);
            Assert.False(context.RuntimeService.HasSessionUnlock);
            Assert.Equal(1, control.StopCalls);
            await WaitUntilAsync(
                () => context.RuntimeService.RuntimeStatus == "locked",
                TimeSpan.FromSeconds(2));
            var state = await context.RuntimeService.GetUnlockStateAsync();
            Assert.True(state.IsVisible);
            Assert.True(state.CanToggle);
            Assert.False(state.IsOn);
            Assert.Equal("Locked", state.StatusText);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeUnlockCoordinator_BlockingStopCallDoesNotPinHeaderToggle()
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(
                root,
                new FakeTunaWalletVerifier(
                    TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500")),
                stopCompletionTimeout: TimeSpan.FromMilliseconds(50));
            var control = new RecordingTransportAccelerationControl { BlockBeforeReturningStop = true };
            SetPrivateField(context.RuntimeService, "currentTransportControl", control);
            SetPrivateField(context.RuntimeService, "runtimeStatus", "active");

            var result = await context.RuntimeService.LockOrStopForSessionAsync(
                "header_switch_off",
                TunaRuntimeUnlockSource.Header);

            Assert.True(result.Success);
            Assert.False(context.RuntimeService.HasSessionUnlock);
            await WaitUntilAsync(
                () => control.StopCalls == 1 && context.RuntimeService.RuntimeStatus == "locked",
                TimeSpan.FromSeconds(2));
            var state = await context.RuntimeService.GetUnlockStateAsync();
            Assert.True(state.IsVisible);
            Assert.True(state.CanToggle);
            Assert.False(state.IsOn);
            Assert.Equal("Locked", state.StatusText);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeUnlockCoordinator_RuntimeDisableWhileActiveStopsAccelerationAndPersistsOff()
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(
                root,
                new FakeTunaWalletVerifier(
                    TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500")));
            var control = new RecordingTransportAccelerationControl();
            SetPrivateField(context.RuntimeService, "currentTransportControl", control);
            SetPrivateField(context.RuntimeService, "runtimeStatus", "active");

            context.RuntimeService.SavePreferences(new TunaRuntimePreferenceState
            {
                Enabled = false,
                FileLaneEnabled = true,
                ScreenLaneEnabled = true,
                MaxPriceNknPerMb = TunaRuntimePreferenceState.DefaultMaxPriceNknPerMb,
                MaxTotalMiB = TunaRuntimePreferenceState.DefaultMaxTotalMiB,
                MaxDurationSec = TunaRuntimePreferenceState.DefaultMaxDurationSec,
                LastRuntimeStatus = "off",
            });

            await WaitUntilAsync(() => control.StopCalls == 1, TimeSpan.FromSeconds(2));
            Assert.False(context.RuntimeService.Preferences.Enabled);
            Assert.Equal("off", context.RuntimeService.RuntimeStatus);
            Assert.False(context.RuntimeService.HasSessionUnlock);
            Assert.Equal("runtime_disabled", control.StopReasons.Single());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task DiagnosticsTunaWallet_UnlinkWhileActiveStopsAccelerationAndClearsWallet()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var control = new RecordingTransportAccelerationControl();
            SetPrivateField(context.RuntimeService, "currentTransportControl", control);
            SetPrivateField(context.RuntimeService, "runtimeStatus", "active");
            var vm = CreateViewModel(context.WalletStore, verifier, context.RuntimeService);

            await vm.UnlinkTunaWalletAsync();

            Assert.False(vm.IsTunaWalletLinked);
            Assert.Equal("Not linked", vm.TunaWalletStatus);
            Assert.False(context.RuntimeService.HasSessionUnlock);
            Assert.True(
                context.RuntimeService.RuntimeStatus is "switching_to_regular_nkn" or "locked",
                $"Unexpected runtime status: {context.RuntimeService.RuntimeStatus}");
            await WaitUntilAsync(
                () => context.RuntimeService.RuntimeStatus == "locked" && control.StopCalls == 1,
                TimeSpan.FromSeconds(2));
            Assert.Equal("locked", context.RuntimeService.RuntimeStatus);
            Assert.Equal(1, control.StopCalls);
            Assert.Equal("wallet_unlinked", control.StopReasons.Single());
            Assert.False(File.Exists(Path.Combine(root, "tuna-wallet-link.json")));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeListenerSupervisor_CanUseUnlockAddedAfterTransportCreation()
    {
        var root = CreateTempRoot();
        try
        {
            var walletPath = Path.Combine(root, "wallet-test-nkn.json");
            var fakeSidecarPath = Path.Combine(root, "nlink-tuna-sidecar.exe");
            await File.WriteAllTextAsync(walletPath, "{}");
            await File.WriteAllTextAsync(fakeSidecarPath, "not an executable");
            var walletStore = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
            var verifiedWallet = TunaWalletLinkState.Linked(walletPath, DateTimeOffset.Parse("2026-05-05T10:00:00Z"))
                .WithValidationResult(
                    TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"),
                    DateTimeOffset.Parse("2026-05-05T10:01:00Z"));
            await walletStore.SaveAsync(verifiedWallet);
            var preferenceStore = new JsonTunaRuntimePreferenceStore(() => Path.Combine(root, "tuna-runtime-preferences.json"));
            preferenceStore.Save(new TunaRuntimePreferenceState
            {
                Enabled = true,
                FileLaneEnabled = true,
                ScreenLaneEnabled = true,
                MaxPriceNknPerMb = TunaRuntimePreferenceState.DefaultMaxPriceNknPerMb,
                MaxTotalMiB = TunaRuntimePreferenceState.DefaultMaxTotalMiB,
                MaxDurationSec = TunaRuntimePreferenceState.DefaultMaxDurationSec,
                LastRuntimeStatus = "locked",
            });
            var usageStore = new JsonTunaUsageAccountingStore(() => Path.Combine(root, "tuna-usage-accounting.json"));
            var verifier = new FakeTunaWalletVerifier(
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"),
                sidecarPath: fakeSidecarPath);
            var runtimeService = new TunaRuntimePilotService(preferenceStore, usageStore, walletStore, verifier);
            using var supervisor = runtimeService.CreateRuntimeListenerSupervisorForTests();

            var lockedResult = await supervisor.EnsureStartedAsync(
                new NknTunaListenerStartRequest("expected-peer", NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen),
                CancellationToken.None);

            Assert.Null(lockedResult);
            Assert.Equal("wallet_not_unlocked", runtimeService.RuntimeStatus);

            var password = "runtime-pass".ToCharArray();
            runtimeService.UnlockForNextSession(verifiedWallet, password);
            Array.Clear(password);

            var unlockedResult = await supervisor.EnsureStartedAsync(
                new NknTunaListenerStartRequest("expected-peer", NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen),
                CancellationToken.None);

            Assert.Null(unlockedResult);
            Assert.True(runtimeService.HasSessionUnlock);
            Assert.Equal("listener_failed", runtimeService.RuntimeStatus);
            var persisted = await File.ReadAllTextAsync(Path.Combine(root, "tuna-runtime-preferences.json"));
            Assert.DoesNotContain("runtime-pass", persisted, StringComparison.Ordinal);

            await runtimeService.LockOrStopForSessionAsync(
                "test_lock",
                TunaRuntimeUnlockSource.Header);

            Assert.False(runtimeService.HasSessionUnlock);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void NknTunaAccelerationOptions_BindsDialerSeedAndIdentifierForExactAllowList()
    {
        var sidecarPath = Path.Combine(Path.GetTempPath(), "nlink-tuna-sidecar.exe");
        var seedBase64 = Convert.ToBase64String(new byte[32]);

        var options = NknTunaAccelerationOptions
            .CreateRuntimePilot(sidecarPath, NknAccelerationLaneKind.File)
            .WithDialerIdentity("nlink-test-identity", seedBase64);

        Assert.True(options.Enabled);
        Assert.Equal("nlink-test-identity", options.DialerIdentifier);
        Assert.Equal(seedBase64, options.DialerSeedBase64);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void NknTunaAccelerationOptions_PassiveDialerDoesNotOfferPaidListener()
    {
        var sidecarPath = Path.Combine(Path.GetTempPath(), "nlink-tuna-sidecar.exe");

        var options = NknTunaAccelerationOptions.CreatePassiveDialer(sidecarPath, NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen);

        Assert.True(options.Enabled);
        Assert.False(options.CanOfferListener);
        Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, options.Lanes);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaRuntimePilot_CreateTransportWithToggleOff_StillAllowsPassiveFreeDialer()
    {
        var root = CreateTempRoot();
        var previousTunaEnabled = Environment.GetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED", null);
            using var localAppData = NknTransportOptions.OverrideLocalAppDataPathForTests(root);
            using var perProcessIdentity = NknTransportOptions.OverrideShouldUsePerProcessLocalIdentityForTests(false);
            var walletStore = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
            var preferenceStore = new JsonTunaRuntimePreferenceStore(() => Path.Combine(root, "tuna-runtime-preferences.json"));
            var usageStore = new JsonTunaUsageAccountingStore(() => Path.Combine(root, "tuna-usage-accounting.json"));
            var verifier = new FakeTunaWalletVerifier(
                TunaWalletValidationResult.Fail("not_used"),
                sidecarPath: Path.Combine(root, "nlink-tuna-sidecar.exe"));
            var runtimeService = new TunaRuntimePilotService(preferenceStore, usageStore, walletStore, verifier);

            using var transport = Assert.IsType<NknSignalingTransport>(runtimeService.CreateNknTransport());

            Assert.False(runtimeService.Preferences.Enabled);
            Assert.Equal("off", runtimeService.RuntimeStatus);
            Assert.True(transport.HasAccelerationLaneForTests);
            Assert.False(transport.AccelerationCanOfferListenerForTests);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED", previousTunaEnabled);
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaListenerSidecarSupervisor_ParsesSummaryPaymentAndCapConfidence()
    {
        var sink = new RecordingTunaUsageSink();
        using var supervisor = CreateSupervisorWithUsageSink(sink);
        var json = """
            {"event":"summary","bytesMoved":20971520,"reason":"context deadline exceeded","paymentObserved":true,"paymentStatus":"reported","paymentEventCount":2,"cumulativeSpendNkn":"0.0042","nknPerMb":"0.0002","capReached":true,"capReason":"duration_cap_reached","fallbackReason":""}
            """;

        InvokeSupervisorStdout(supervisor, json);

        var summary = Assert.Single(sink.Summaries);
        Assert.Equal(20_971_520, summary.BytesMoved);
        Assert.Equal("context deadline exceeded", summary.Reason);
        Assert.True(summary.PaymentTelemetryObserved);
        Assert.Equal("reported", summary.PaymentStatus);
        Assert.Equal(2, summary.PaymentEventCount);
        Assert.Equal(0.0042m, summary.CumulativeSpendNkn);
        Assert.Equal(0.0002m, summary.NknPerMb);
        Assert.True(summary.CapReached);
        Assert.Equal("duration_cap_reached", summary.CapReason);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaListenerSidecarSupervisor_ParsesMissingPaymentSummaryAsNoTelemetry()
    {
        var sink = new RecordingTunaUsageSink();
        using var supervisor = CreateSupervisorWithUsageSink(sink);
        var json = """
            {"event":"summary","bytesMoved":61112208,"reason":"EOF","paymentObserved":false,"paymentStatus":"no_payment_telemetry_reported","paymentEventCount":0,"cumulativeSpendNkn":"not-a-number","capReached":false,"capReason":"","fallbackReason":"EOF"}
            """;

        InvokeSupervisorStdout(supervisor, json);

        var summary = Assert.Single(sink.Summaries);
        Assert.Equal(61_112_208, summary.BytesMoved);
        Assert.False(summary.PaymentTelemetryObserved);
        Assert.Equal("no_payment_telemetry_reported", summary.PaymentStatus);
        Assert.Equal(0, summary.PaymentEventCount);
        Assert.Null(summary.CumulativeSpendNkn);
        Assert.False(summary.CapReached);
        Assert.Equal("EOF", summary.FallbackReason);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaListenerSidecarSupervisor_ProviderReadyRetryKeepsConnectingStatus()
    {
        var statuses = new List<string>();
        using var supervisor = new NknTunaListenerSidecarSupervisor(new NknTunaListenerSidecarOptions
        {
            SidecarExePath = Path.Combine(Path.GetTempPath(), "nlink-tuna-sidecar.exe"),
            WalletPath = Path.Combine(Path.GetTempPath(), "wallet-test-nkn.json"),
            TakeWalletPassword = static () => "unused".ToCharArray(),
            MaxPriceNknPerMb = TunaRuntimePreferenceState.DefaultMaxPriceNknPerMb,
            MaxTotalMiB = TunaRuntimePreferenceState.DefaultMaxTotalMiB,
            MaxDurationSec = TunaRuntimePreferenceState.DefaultMaxDurationSec,
            StatusChanged = statuses.Add,
        });

        InvokeSupervisorStdout(
            supervisor,
            "{\"event\":\"tuna_provider_paths_ready_timeout\",\"usableCount\":2,\"minProviderCnt\":4,\"attempt\":1,\"maxAttempts\":2,\"willRetry\":true}");
        InvokeSupervisorStdout(
            supervisor,
            "{\"event\":\"tuna_provider_paths_ready_timeout\",\"usableCount\":2,\"minProviderCnt\":4,\"attempt\":2,\"maxAttempts\":2,\"willRetry\":false}");

        Assert.Contains("provider_paths_retrying", statuses);
        Assert.Contains("provider_paths_wait_timeout", statuses);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaListenerSidecarSupervisor_TracksProviderPathDiagnosticsCounters()
    {
        var statuses = new List<string>();
        using var supervisor = new NknTunaListenerSidecarSupervisor(new NknTunaListenerSidecarOptions
        {
            SidecarExePath = Path.Combine(Path.GetTempPath(), "nlink-tuna-sidecar.exe"),
            WalletPath = Path.Combine(Path.GetTempPath(), "wallet-test-nkn.json"),
            TakeWalletPassword = static () => "unused".ToCharArray(),
            MaxPriceNknPerMb = TunaRuntimePreferenceState.DefaultMaxPriceNknPerMb,
            MaxTotalMiB = TunaRuntimePreferenceState.DefaultMaxTotalMiB,
            MaxDurationSec = TunaRuntimePreferenceState.DefaultMaxDurationSec,
            StatusChanged = statuses.Add,
        });

        InvokeSupervisorStdout(
            supervisor,
            "{\"event\":\"provider_paths_degraded_accepted\",\"usableCount\":3,\"minProviderCnt\":4,\"degradedProviderCnt\":3}");
        InvokeSupervisorStdout(
            supervisor,
            "{\"event\":\"provider_paths_recovered\",\"usableCount\":4,\"minProviderCnt\":4,\"degradedProviderCnt\":3}");
        InvokeSupervisorStdout(
            supervisor,
            "{\"event\":\"provider_paths_still_degraded\",\"usableCount\":3,\"minProviderCnt\":4,\"degradedProviderCnt\":3}");
        InvokeSupervisorStdout(
            supervisor,
            "{\"event\":\"provider_path_quality_summary\",\"qualityClass\":\"persistent_missing_path\",\"usableCount\":3,\"missingIndices\":[0],\"recoveryLatencyMs\":-1,\"stable3OnlyMs\":12000,\"finalPathReasons\":[{\"index\":0,\"stateReason\":\"empty_endpoint\"},{\"index\":1,\"stateReason\":\"usable\"}]}");

        Assert.Contains("provider_paths_degraded", statuses);
        Assert.Contains("provider_paths_ready", statuses);
        var diagnostics = supervisor.ProviderPathDiagnostics;
        Assert.Equal(1, diagnostics.DegradedAcceptedCount);
        Assert.Equal(1, diagnostics.RecoveredCount);
        Assert.Equal(1, diagnostics.StillDegradedCount);
        Assert.NotNull(diagnostics.LatestQualitySummary);
        Assert.Equal("persistent_missing_path", diagnostics.LatestQualitySummary.QualityClass);
        Assert.Equal(3, diagnostics.LatestQualitySummary.UsableCount);
        Assert.Equal([0], diagnostics.LatestQualitySummary.MissingIndices);
        Assert.Equal(12000, diagnostics.LatestQualitySummary.Stable3OnlyMs);
        Assert.Contains("0:empty_endpoint", diagnostics.LatestQualitySummary.FinalPathReasons);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaListenerSidecarSupervisor_CapHandoffEventRequestsRuntimeStop()
    {
        var statuses = new List<string>();
        var reasons = new List<string>();
        using var supervisor = new NknTunaListenerSidecarSupervisor(new NknTunaListenerSidecarOptions
        {
            SidecarExePath = Path.Combine(Path.GetTempPath(), "nlink-tuna-sidecar.exe"),
            WalletPath = Path.Combine(Path.GetTempPath(), "wallet-test-nkn.json"),
            TakeWalletPassword = static () => "unused".ToCharArray(),
            MaxPriceNknPerMb = TunaRuntimePreferenceState.DefaultMaxPriceNknPerMb,
            MaxTotalMiB = TunaRuntimePreferenceState.DefaultMaxTotalMiB,
            MaxDurationSec = TunaRuntimePreferenceState.DefaultMaxDurationSec,
            StatusChanged = statuses.Add,
            CapHandoffRequested = reasons.Add,
        });

        InvokeSupervisorStdout(
            supervisor,
            "{\"event\":\"tuna_cap_handoff_requested\",\"capReason\":\"byte_cap_reached\",\"bytesMoved\":10485760,\"projectedBytes\":11534336,\"limitBytes\":12582912,\"remainingBytes\":2097152}");

        Assert.Contains("cap_handoff_pending", statuses);
        Assert.Equal("byte_cap_reached", Assert.Single(reasons));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void DiagnosticsTunaRuntimeCopy_IncludesSpendCostAndBenchmarkEstimate()
    {
        var root = CreateTempRoot();
        try
        {
            var walletStore = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
            var preferenceStore = new JsonTunaRuntimePreferenceStore(() => Path.Combine(root, "tuna-runtime-preferences.json"));
            var usageStore = new JsonTunaUsageAccountingStore(() => Path.Combine(root, "tuna-usage-accounting.json"));
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Fail("not_used"));
            preferenceStore.Save(new TunaRuntimePreferenceState
            {
                Enabled = true,
                FileLaneEnabled = true,
                ScreenLaneEnabled = true,
                MaxPriceNknPerMb = TunaRuntimePreferenceState.DefaultMaxPriceNknPerMb,
                MaxTotalMiB = TunaRuntimePreferenceState.DefaultMaxTotalMiB,
                MaxDurationSec = TunaRuntimePreferenceState.DefaultMaxDurationSec,
                LastRuntimeStatus = "locked",
            });
            usageStore.Save(new TunaUsageAccountingState
            {
                TotalPaidNkn = 0.010m,
                TotalAppPayloadMb = 100m,
                LastSessionPaidNkn = 0.004m,
                LastSessionAppPayloadMb = 40m,
                LastUpdatedUtc = DateTimeOffset.Parse("2026-05-05T12:00:00Z"),
            });
            var runtimeService = new TunaRuntimePilotService(preferenceStore, usageStore, walletStore, verifier);
            var vm = CreateViewModel(walletStore, verifier, runtimeService);

            var copied = vm.BuildDiagnosticsCopyTextForTests();

            Assert.Contains("tuna_runtime_flag: Advanced opt-in", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_runtime_enabled: yes", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_runtime_caps: max_price_nkn_per_mb=0.0002; max_total_mib=2048; max_duration_minutes=30", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_provider_readiness: strict_4_paths", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_startup_timing:", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_spend_by_nlink: 0.01 NKN", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_average_cost: 0.0001 NKN/MB", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_last_session_cost: 0.004 NKN over 40 MB", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_last_session_reason: (none)", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_last_session_payment_status: (none)", copied, StringComparison.Ordinal);
            Assert.DoesNotContain("tuna_expected_improvement:", copied, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void DiagnosticsTunaRuntimeCopy_ShowsUnknownCostWhenPaymentTelemetryIsMissing()
    {
        var root = CreateTempRoot();
        try
        {
            var walletStore = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
            var preferenceStore = new JsonTunaRuntimePreferenceStore(() => Path.Combine(root, "tuna-runtime-preferences.json"));
            var usageStore = new JsonTunaUsageAccountingStore(() => Path.Combine(root, "tuna-usage-accounting.json"));
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Fail("not_used"));
            var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
            usageStore.Save(TunaUsageAccountingState.Empty
                .StartNewSession("run-missing-payment", "listener", now)
                .CompleteSession(
                    "run-missing-payment",
                    61_112_208,
                    paymentTelemetryObserved: false,
                    cumulativeSpendNkn: null,
                    paymentTelemetryStatus: TunaPaymentTelemetryStatus.NoPaymentTelemetryReported,
                    paymentEventCount: 0,
                    stopReason: "EOF",
                    capReason: string.Empty,
                    fallbackReason: "EOF",
                    completedFromSummary: true,
                    now));
            var runtimeService = new TunaRuntimePilotService(preferenceStore, usageStore, walletStore, verifier);
            var vm = CreateViewModel(walletStore, verifier, runtimeService);

            var copied = vm.BuildDiagnosticsCopyTextForTests();

            Assert.Contains("tuna_spend_by_nlink: no payment telemetry reported", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_average_cost: no payment telemetry reported", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_last_session_cost: no payment telemetry reported over 61.11 MB", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_last_session_reason: fallback to NKN", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_last_session_payment_status: no_payment_telemetry_reported", copied, StringComparison.Ordinal);
            Assert.Contains("tuna_last_session_completed_from_summary: yes", copied, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void DiagnosticsTunaRuntimePreferences_RejectsTurningOffBothLanes()
    {
        var root = CreateTempRoot();
        try
        {
            var walletStore = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
            var preferenceStore = new JsonTunaRuntimePreferenceStore(() => Path.Combine(root, "tuna-runtime-preferences.json"));
            preferenceStore.Save(new TunaRuntimePreferenceState
            {
                Enabled = true,
                FileLaneEnabled = true,
                ScreenLaneEnabled = false,
                MaxPriceNknPerMb = TunaRuntimePreferenceState.DefaultMaxPriceNknPerMb,
                MaxTotalMiB = TunaRuntimePreferenceState.DefaultMaxTotalMiB,
                MaxDurationSec = TunaRuntimePreferenceState.DefaultMaxDurationSec,
                LastRuntimeStatus = "locked",
            });
            var usageStore = new JsonTunaUsageAccountingStore(() => Path.Combine(root, "tuna-usage-accounting.json"));
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Fail("not_used"));
            var runtimeService = new TunaRuntimePilotService(preferenceStore, usageStore, walletStore, verifier);
            var vm = CreateViewModel(walletStore, verifier, runtimeService);

            vm.IsTunaFileLaneEnabled = false;

            Assert.True(vm.IsTunaFileLaneEnabled);
            Assert.False(vm.IsTunaScreenLaneEnabled);
            Assert.True(vm.ShowCopyFeedback);
            Assert.Contains("Choose at least one Tuna lane", vm.CopyFeedbackText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void DiagnosticsRedactor_Redacts_TunaWalletPathAndAddressMetadata()
    {
        var sample = string.Join(Environment.NewLine, new[]
        {
            "wallet_path=C:\\Users\\Juraj\\Desktop\\Remote help\\artifacts\\tuna-poc\\wallet-test-nkn.json",
            "tuna_wallet_path=C:\\Users\\Juraj\\Desktop\\Remote help\\artifacts\\tuna-poc\\wallet-test-nkn.json",
            "wallet_address=NKNabcdef123456789",
            "tuna_wallet_address=NKNabcdef987654321",
            "\"walletAddress\":\"NKNjson123456789\"",
            "normal=value",
        });

        var redacted = DiagnosticsRedactor.Redact(sample);

        Assert.DoesNotContain("Remote help", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wallet-test-nkn.json", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NKNabcdef", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("NKNjson", redacted, StringComparison.Ordinal);
        Assert.Contains("normal=value", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaRuntimeFlag_DefaultsOffEvenWhenWalletStateExists()
    {
        using var enabled = new EnvironmentOverride("NLINK_NKN_TUNA_ENABLED", null);
        var options = NknTunaAccelerationOptions.Load();

        Assert.False(options.Enabled);
    }

    private static NknTunaListenerSidecarSupervisor CreateSupervisorWithUsageSink(INknTunaUsageTelemetrySink sink)
        => new(new NknTunaListenerSidecarOptions
        {
            SidecarExePath = Path.Combine(Path.GetTempPath(), "nlink-tuna-sidecar.exe"),
            WalletPath = Path.Combine(Path.GetTempPath(), "wallet-test-nkn.json"),
            TakeWalletPassword = static () => "unused".ToCharArray(),
            MaxPriceNknPerMb = TunaRuntimePreferenceState.DefaultMaxPriceNknPerMb,
            MaxTotalMiB = TunaRuntimePreferenceState.DefaultMaxTotalMiB,
            MaxDurationSec = TunaRuntimePreferenceState.DefaultMaxDurationSec,
            UsageSink = sink,
        });

    private static void InvokeSupervisorStdout(NknTunaListenerSidecarSupervisor supervisor, string line)
    {
        var method = typeof(NknTunaListenerSidecarSupervisor).GetMethod(
            "HandleStdoutLine",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(
            supervisor,
            [
                line,
                new TaskCompletionSource<NknTunaListenerSidecarEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously),
                Stopwatch.StartNew(),
            ]);
    }

    private static DiagnosticsPageViewModel CreateViewModel(
        ITunaWalletLinkStore store,
        ITunaWalletVerifier verifier,
        ITunaRuntimePilotService? runtimePilotService = null)
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        Environment.SetEnvironmentVariable("NLINK_TRANSPORT", null);
        try
        {
            return new DiagnosticsPageViewModel(
                ScreenShareEvidenceLocator.CreateDefault(),
                static () => { },
                TransportRuntimeConfig.Select(),
                tunaWalletLinkStore: store,
                tunaWalletVerifier: verifier,
                tunaRuntimePilotService: runtimePilotService);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previousTransport);
        }
    }

    private static async Task<VerifiedRuntimeContext> CreateVerifiedRuntimeContextAsync(
        string root,
        ITunaWalletVerifier verifier,
        bool enabled = true,
        TimeSpan? stopCompletionTimeout = null)
    {
        var walletPath = Path.Combine(root, "wallet-test-nkn.json");
        await File.WriteAllTextAsync(walletPath, "{}");
        var walletStore = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
        await walletStore.SaveAsync(TunaWalletLinkState.Linked(walletPath, DateTimeOffset.Parse("2026-05-05T10:00:00Z"))
            .WithValidationResult(
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"),
                DateTimeOffset.Parse("2026-05-05T10:01:00Z")));
        var preferenceStore = new JsonTunaRuntimePreferenceStore(() => Path.Combine(root, "tuna-runtime-preferences.json"));
        preferenceStore.Save(new TunaRuntimePreferenceState
        {
            Enabled = enabled,
            FileLaneEnabled = true,
            ScreenLaneEnabled = true,
            MaxPriceNknPerMb = TunaRuntimePreferenceState.DefaultMaxPriceNknPerMb,
            MaxTotalMiB = TunaRuntimePreferenceState.DefaultMaxTotalMiB,
            MaxDurationSec = TunaRuntimePreferenceState.DefaultMaxDurationSec,
            LastRuntimeStatus = enabled ? "locked" : "off",
        });
        var usageStore = new JsonTunaUsageAccountingStore(() => Path.Combine(root, "tuna-usage-accounting.json"));
        var runtimeService = new TunaRuntimePilotService(
            preferenceStore,
            usageStore,
            walletStore,
            verifier,
            stopCompletionTimeout: stopCompletionTimeout);
        return new VerifiedRuntimeContext(walletStore, preferenceStore, usageStore, runtimeService, walletPath);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(predicate(), "Condition was not met before timeout.");
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nlink-tuna-wallet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root) &&
                root.Contains("nlink-tuna-wallet-tests", StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // Test cleanup only.
        }
    }

    private sealed class RecordingTunaUsageSink : INknTunaUsageTelemetrySink
    {
        public List<NknTunaPaymentTelemetry> Payments { get; } = new();

        public List<NknTunaSessionUsageTelemetry> Summaries { get; } = new();

        public List<string> IncompleteReasons { get; } = new();

        public void RecordPayment(NknTunaPaymentTelemetry payment) => Payments.Add(payment);

        public void RecordSummary(NknTunaSessionUsageTelemetry summary) => Summaries.Add(summary);

        public void RecordIncomplete(string reason) => IncompleteReasons.Add(reason);
    }

    private sealed record VerifiedRuntimeContext(
        JsonTunaWalletLinkStore WalletStore,
        JsonTunaRuntimePreferenceStore PreferenceStore,
        JsonTunaUsageAccountingStore UsageStore,
        TunaRuntimePilotService RuntimeService,
        string WalletPath);

    private sealed class RecordingTransportAccelerationControl : ITransportAccelerationControl
    {
        private int requestCalls;
        private int stopCalls;
        private readonly TaskCompletionSource<object?> stopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCalls => Volatile.Read(ref requestCalls);

        public int StopCalls => Volatile.Read(ref stopCalls);

        public List<string> RequestReasons { get; } = new();

        public List<string> StopReasons { get; } = new();

        public bool HangStop { get; init; }

        public bool BlockBeforeReturningStop { get; init; }

        public Task RequestAccelerationNegotiationAsync(string reason, CancellationToken ct)
        {
            Interlocked.Increment(ref requestCalls);
            lock (RequestReasons)
            {
                RequestReasons.Add(reason);
            }

            return Task.CompletedTask;
        }

        public Task StopAccelerationAsync(string reason, CancellationToken ct)
        {
            Interlocked.Increment(ref stopCalls);
            lock (StopReasons)
            {
                StopReasons.Add(reason);
            }

            if (BlockBeforeReturningStop)
            {
                Thread.Sleep(TimeSpan.FromSeconds(5));
            }

            if (HangStop)
            {
                return stopCompletion.Task;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeTunaWalletVerifier : ITunaWalletVerifier
    {
        private readonly TunaWalletValidationResult result;
        private readonly string sidecarPath;
        private readonly TunaWalletVerifierAvailability? availability;

        public FakeTunaWalletVerifier(
            TunaWalletValidationResult result,
            string sidecarPath = "nlink-tuna-sidecar.exe",
            TunaWalletVerifierAvailability? availability = null)
        {
            this.result = result;
            this.sidecarPath = sidecarPath;
            this.availability = availability;
        }

        public List<char[]> PasswordsSeen { get; } = new();

        public TunaWalletVerifierAvailability GetAvailability()
            => availability ?? new(true, "available", sidecarPath);

        public Task<TunaWalletValidationResult> ValidateAsync(string walletPath, char[] password, CancellationToken ct)
        {
            PasswordsSeen.Add(password.ToArray());
            return Task.FromResult(result);
        }
    }

    private sealed class SequenceTunaWalletVerifier : ITunaWalletVerifier
    {
        private readonly Queue<TunaWalletValidationResult> results;
        private readonly string sidecarPath;
        private TunaWalletValidationResult? lastResult;

        public SequenceTunaWalletVerifier(params TunaWalletValidationResult[] results)
            : this("nlink-tuna-sidecar.exe", results)
        {
        }

        public SequenceTunaWalletVerifier(string sidecarPath, params TunaWalletValidationResult[] results)
        {
            this.sidecarPath = sidecarPath;
            this.results = new Queue<TunaWalletValidationResult>(results);
        }

        public List<char[]> PasswordsSeen { get; } = new();

        public TunaWalletVerifierAvailability GetAvailability()
            => new(true, "available", sidecarPath);

        public Task<TunaWalletValidationResult> ValidateAsync(string walletPath, char[] password, CancellationToken ct)
        {
            PasswordsSeen.Add(password.ToArray());
            if (results.Count > 0)
            {
                lastResult = results.Dequeue();
                return Task.FromResult(lastResult);
            }

            return Task.FromResult(lastResult ?? TunaWalletValidationResult.Fail("no_result"));
        }
    }

    private sealed class BlockingTunaWalletVerifier : ITunaWalletVerifier
    {
        private readonly TunaWalletValidationResult result;
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingTunaWalletVerifier(TunaWalletValidationResult result)
        {
            this.result = result;
        }

        public List<char[]> PasswordsSeen { get; } = new();

        public TunaWalletVerifierAvailability GetAvailability()
            => new(true, "available", "nlink-tuna-sidecar.exe");

        public async Task<TunaWalletValidationResult> ValidateAsync(string walletPath, char[] password, CancellationToken ct)
        {
            PasswordsSeen.Add(password.ToArray());
            await release.Task.WaitAsync(ct);
            return result;
        }

        public void Release()
            => release.TrySetResult();
    }
}
