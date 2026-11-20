using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Core;
using NLink.Core.Diagnostics;
using NLink.Core.Logging;
using NLink.Infra.Nkn;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class TunaWalletDiagnosticsTests
{
    private const string VerifiedFixturePayer = "NKN0123456789PUBLICADDRESS";

    [Theory]
    [InlineData(false, "write")]
    [InlineData(true, "write")]
    [InlineData(false, "initial-read")]
    [InlineData(true, "initial-read")]
    [InlineData(false, "conditional-read")]
    [InlineData(true, "conditional-read")]
    public async Task DiagnosticsTunaWallet_OptionsReportsWalletStorageFailure(bool unlock, string failure)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", VerifiedFixturePayer, "7"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var store = new BlockingWalletSave(context.WalletStore);
            var runtime = new TunaRuntimePilotService(context.PreferenceStore, context.UsageStore, store, verifier);
            using var page = CreateViewModel(store, verifier, runtime);
            var wallet = await store.LoadAsync();
            var statePath = Path.Combine(root, "tuna-wallet-link.json");
            if (failure == "initial-read")
            {
                await File.WriteAllTextAsync(statePath, "{");
                store.ReleaseSave.TrySetResult();
            }
            else if (failure == "conditional-read")
            {
                store.DuringSave = () => File.Open(statePath, FileMode.Open, FileAccess.Read, FileShare.None);
                store.ReleaseSave.TrySetResult();
            }
            else store.ReleaseSave.SetException(new IOException("fixture-private-wallet-path"));
            var before = await File.ReadAllBytesAsync(statePath);
            var password = "fixture".ToCharArray();
            if (unlock) await page.UnlockTunaRuntimeAsync(wallet, password);
            else await page.ValidateTunaWalletAsync(wallet, password);

            Assert.Equal(unlock ? "Couldn't confirm the wallet unlock. Check access to nLink's wallet settings."
                : "Couldn't confirm the wallet balance. Check access to nLink's wallet settings.", page.CopyFeedbackText);
            Assert.True(page.ShowCopyFeedback);
            Assert.Equal(VerifiedFixturePayer, page.TunaWalletAddress);
            Assert.False(page.IsTunaWalletValidating);
            Assert.False(runtime.HasSessionUnlock);
            Assert.Equal(failure == "initial-read" ? 0 : 1, verifier.PasswordsSeen.Count);
            Assert.Equal(failure != "initial-read", store.SaveEntered.Task.IsCompleted);
            Assert.Equal(before, await File.ReadAllBytesAsync(statePath));
            Assert.All(password, c => Assert.Equal('\0', c));
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData("unlock_validating")]
    [InlineData("waiting_for_approved_session")]
    public async Task DiagnosticsTunaWallet_UnlockPreferenceWriteFailureLeavesRuntimeLocked(string failStatus)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", VerifiedFixturePayer, "1"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var prefs = new FailingStopPreferenceStore(context.PreferenceStore) { FailStatus = failStatus };
            var runtime = new TunaRuntimePilotService(prefs, context.UsageStore, context.WalletStore, verifier);
            using var page = CreateViewModel(context.WalletStore, verifier, runtime);
            var password = "fixture".ToCharArray();
            await page.UnlockTunaRuntimeAsync(OpenUnlockPrompt(page), password);

            Assert.Equal(1, prefs.FailedWrites);
            Assert.Equal(failStatus == "unlock_validating" ? 0 : 1, verifier.PasswordsSeen.Count);
            Assert.Equal("Couldn't confirm the wallet unlock. Check access to nLink's wallet settings.", page.CopyFeedbackText);
            Assert.False(runtime.HasSessionUnlock);
            Assert.False((await runtime.GetUnlockStateAsync()).IsOn);
            Assert.Equal("locked", runtime.RuntimeStatus);
            Assert.False(RuntimeListenerOptions(runtime).CanTakeWalletPassword!());
            Assert.False(page.IsTunaWalletValidating);
            Assert.All(password, c => Assert.Equal('\0', c));
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiagnosticsTunaWallet_RevokedDuringRefreshDoesNotPublishUnlock(bool disable)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", VerifiedFixturePayer, "1"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var blocked = new BlockingWalletLoad(context.WalletStore);
            using var page = CreateViewModel(blocked, verifier, context.RuntimeService);
            var original = OpenUnlockPrompt(page);
            blocked.BlockNextLoad = 1;
            var pending = page.UnlockTunaRuntimeAsync(original, "fixture".ToCharArray());
            try
            {
                await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(context.RuntimeService.HasSessionUnlock);
                if (disable) page.IsTunaRuntimeEnabled = false;
                else await context.RuntimeService.LockOrStopForSessionAsync("user_locked", TunaRuntimeUnlockSource.Header);
            }
            finally { blocked.Release.TrySetResult(); }
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var latest = await context.WalletStore.LoadAsync();
            Assert.True(original.IsSamePayerLinkAs(latest));
            Assert.Equal(TunaWalletLinkStatus.VerifiedFunded, latest.Status);
            Assert.False(context.RuntimeService.HasSessionUnlock);
            Assert.NotEqual("Tuna unlocked for the next approved session.", page.CopyFeedbackText);
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData("other-funded")]
    [InlineData("same-empty")]
    [InlineData("same-failed")]
    [InlineData("same-funded")]
    public async Task DiagnosticsTunaWallet_UnlockFeedbackRequiresRefreshedFundedPayer(string refreshed)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", VerifiedFixturePayer, "1"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var blocked = new BlockingWalletLoad(context.WalletStore);
            using var page = CreateViewModel(blocked, verifier, context.RuntimeService);
            var original = OpenUnlockPrompt(page);
            blocked.BlockNextLoad = 1;
            var pending = page.UnlockTunaRuntimeAsync(original, "fixture".ToCharArray());
            try
            {
                await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(context.RuntimeService.HasSessionUnlock);
                var peer = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
                var current = await peer.LoadAsync();
                Assert.True(original.IsSamePayerLinkAs(current));
                var updated = current.WithValidationResult(refreshed == "same-failed" ? TunaWalletValidationResult.Fail("lookup_failed")
                    : TunaWalletValidationResult.Ok("wallet", refreshed == "other-funded" ? "payer-b" : VerifiedFixturePayer,
                        refreshed == "same-empty" ? "0" : "5"), DateTimeOffset.UtcNow);
                Assert.True(await peer.SaveAsync(updated, expectedValidation: current));
            }
            finally { blocked.Release.TrySetResult(); }
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(refreshed == "same-funded" ? "Tuna unlocked for the next approved session." : "Wallet changed.", page.CopyFeedbackText);
            Assert.Equal(refreshed switch { "other-funded" => "payer-b", "same-failed" => "(not verified)", _ => VerifiedFixturePayer },
                page.TunaWalletAddress);
            Assert.Equal(refreshed == "same-funded", context.RuntimeService.HasSessionUnlock);
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiagnosticsTunaWallet_DisableCheckboxReportsSessionOnlyWhenSaveFails(bool failSave)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Fail("unused"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var prefs = new FailingStopPreferenceStore(context.PreferenceStore);
            var runtime = new TunaRuntimePilotService(prefs, context.UsageStore, context.WalletStore, verifier);
            var control = new RecordingTransportAccelerationControl();
            SetPrivateField(runtime, "currentTransportControl", control);
            runtime.UnlockForNextSession(await context.WalletStore.LoadAsync(), "fixture".ToCharArray());
            Assert.True(RuntimeListenerOptions(runtime).BeginUsageAttemptIfCurrent!("checkbox-owner", () => true));
            using var page = CreateViewModel(context.WalletStore, verifier, runtime);
            Assert.True(page.IsTunaRuntimeEnabled);
            prefs.FailWrites = failSave;

            page.IsTunaRuntimeEnabled = false;

            Assert.False(page.IsTunaRuntimeEnabled);
            Assert.Equal("off", runtime.RuntimeStatus);
            Assert.False(runtime.HasSessionUnlock);
            Assert.Equal(failSave, context.PreferenceStore.Load().Enabled);
            Assert.Equal(failSave, page.CopyFeedback.IsVisible);
            if (failSave)
                Assert.Equal("Tuna is off for this session, but the setting couldn't be saved.", page.CopyFeedback.Text);
            await WaitUntilAsync(() => control.StopCalls == 1, TimeSpan.FromSeconds(3));
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData("unlink")]
    [InlineData("lock")]
    [InlineData("disable")]
    public async Task TunaRuntimeWallet_PreferenceWriteFailureStillStopsOwner(string operation)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Fail("unused"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var prefs = new FailingStopPreferenceStore(context.PreferenceStore);
            var runtime = new TunaRuntimePilotService(prefs, context.UsageStore, context.WalletStore, verifier);
            var control = new RecordingTransportAccelerationControl();
            SetPrivateField(runtime, "currentTransportControl", control);
            var wallet = await context.WalletStore.LoadAsync();
            runtime.UnlockForNextSession(wallet, "fixture".ToCharArray());
            var options = RuntimeListenerOptions(runtime);
            Assert.True(options.BeginUsageAttemptIfCurrent!("paid-owner", () => true));
            options.UsageSink!.RecordPayment(new NknTunaPaymentTelemetry(0.25m, 0.25m, 100, null));
            var lastPersisted = context.PreferenceStore.Load().LastRuntimeStatus;
            prefs.FailWrites = true;

            if (operation == "unlink") Assert.True(await runtime.TryUnlinkWalletAsync(wallet, CancellationToken.None));
            else if (operation == "lock") Assert.True((await runtime.LockOrStopForSessionAsync("test_lock", TunaRuntimeUnlockSource.Header)).Success);
            else Assert.Throws<IOException>(() => runtime.SavePreferences(new TunaRuntimePreferenceState { Enabled = false }));

            var terminal = operation == "disable" ? "off" : "locked";
            await WaitUntilAsync(() =>
            {
                lock (control.StopReasons) return control.StopReasons.Count != 0 && runtime.RuntimeStatus == terminal;
            }, TimeSpan.FromSeconds(3));
            Assert.Equal(1, control.StopCalls);
            Assert.False(runtime.HasSessionUnlock);
            Assert.Equal(operation != "unlink", (await context.WalletStore.LoadAsync()).IsLinked);
            Assert.Equal(lastPersisted, context.PreferenceStore.Load().LastRuntimeStatus);
            Assert.True(context.PreferenceStore.Load().Enabled);
            Assert.Equal(operation == "disable" ? 1 : 2, prefs.FailedWrites);
            var reason = operation == "disable" ? "runtime_disabled" : operation == "lock" ? "test_lock" : "wallet_unlinked";
            Assert.Equal(reason, Assert.Single(control.StopReasons));
            options.UsageSink.RecordIncomplete("late");
            var closed = Assert.Single(context.UsageStore.Load().SessionRecords);
            Assert.Equal(reason, closed.StopReason);
            Assert.Equal("paid-owner", closed.SessionRunId);
            Assert.Equal(0.25m, closed.PaidNkn);
            var log = LocalOperationalLog.GetRecentLogText();
            Assert.Contains($"event=tuna_stop_preferences_persist_failed; status={terminal}; error=IOException", log);
            Assert.DoesNotContain("fixture-private-preference-path", log);
        }
        finally { DeleteTempRoot(root); }
    }

    private sealed class FailingStopPreferenceStore(ITunaRuntimePreferenceStore inner) : ITunaRuntimePreferenceStore
    {
        public bool FailWrites;
        public string? FailStatus;
        public int FailedWrites;
        public TunaRuntimePreferenceState Load() => inner.Load();
        public void Save(TunaRuntimePreferenceState state)
        {
            if (!FailWrites && (FailStatus is null || state.LastRuntimeStatus != FailStatus)) { inner.Save(state); return; }
            Interlocked.Increment(ref FailedWrites);
            throw new IOException("fixture-private-preference-path");
        }
    }

    [Theory]
    [InlineData(false, "0")]
    [InlineData(true, "5")]
    public async Task TunaRuntimeWallet_StaleUnlockCannotOverwriteNewerFundedBalance(bool blockSave, string oldBalance)
    {
        var root = CreateTempRoot();
        var verifier = new BlockingTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", VerifiedFixturePayer, oldBalance));
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var store = new BlockingWalletSave(context.WalletStore);
            var runtime = new TunaRuntimePilotService(context.PreferenceStore, context.UsageStore, store, verifier);
            if (blockSave) verifier.Release();
            else store.ReleaseSave.TrySetResult();
            var wallet = await store.LoadAsync();
            var password = "fixture".ToCharArray();
            var pending = runtime.UnlockForSessionAsync(password, TunaRuntimeUnlockSource.Options, wallet);
            var newer = wallet.WithValidationResult(TunaWalletValidationResult.Ok("wallet", VerifiedFixturePayer, "7"), DateTimeOffset.UtcNow);
            try
            {
                await (blockSave ? store.SaveEntered.Task : verifier.Entered).WaitAsync(TimeSpan.FromSeconds(5));
                var peer = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
                Assert.True(await peer.SaveAsync(newer, expectedValidation: wallet));
            }
            finally { verifier.Release(); store.ReleaseSave.TrySetResult(); }
            Assert.False((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Success);
            Assert.Equal(JsonSerializer.Serialize(newer), JsonSerializer.Serialize(await store.LoadAsync()));
            Assert.False(runtime.HasSessionUnlock);
            Assert.All(password, c => Assert.Equal('\0', c));
        }
        finally { verifier.Release(); DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TunaWalletStore_IdenticalChecksStillInvalidatePriorSnapshot(bool success)
    {
        var root = CreateTempRoot();
        try
        {
            var path = Path.Combine(root, "state.json");
            var store = new JsonTunaWalletLinkStore(() => path);
            var peer = new JsonTunaWalletLinkStore(() => path);
            var instant = DateTimeOffset.UnixEpoch;
            var result = success ? TunaWalletValidationResult.Ok("wallet", VerifiedFixturePayer, "1") : TunaWalletValidationResult.Fail("lookup_failed");
            var first = TunaWalletLinkState.Linked(Path.Combine(root, "wallet.json"), instant).WithValidationResult(result, instant);
            Assert.True(await store.SaveAsync(first));
            var before = await store.LoadAsync();
            var second = before.WithValidationResult(result, instant);
            Assert.True(await peer.SaveAsync(second, expectedValidation: before));
            var stale = before.WithValidationResult(TunaWalletValidationResult.Ok("wallet", VerifiedFixturePayer, "9"), instant);
            Assert.False(await store.SaveAsync(stale, expectedValidation: before));
            Assert.NotNull(before.ValidationRevision);
            Assert.NotNull(second.ValidationRevision);
            Assert.NotEqual(before.ValidationRevision, second.ValidationRevision);
            Assert.True(second.IsSameValidationAs(await store.LoadAsync()));
            Assert.False(before.IsSameValidationAs(second));
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticsTunaWallet_FailedBalanceUnlinkStopsOnlyLastVerifiedOwner(bool differentPayer)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Fail("lookup_failed", "wallet"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var runtime = context.RuntimeService;
            var control = new RecordingTransportAccelerationControl();
            SetPrivateField(runtime, "currentTransportControl", control);
            var wallet = await context.WalletStore.LoadAsync();
            runtime.UnlockForNextSession(wallet, "test".ToCharArray());
            var listener = RuntimeListenerOptions(runtime);
            Assert.True(listener.BeginUsageAttemptIfCurrent!("owner", () => true));
            listener.StatusChanged!("active");
            listener.UsageSink!.RecordPayment(new NknTunaPaymentTelemetry(0.25m, 0.25m, 100, null));
            if (differentPayer)
                await context.WalletStore.SaveAsync(wallet.WithValidationResult(
                    TunaWalletValidationResult.Ok("wallet", "other-payer", "1"), DateTimeOffset.UtcNow));
            using var page = CreateViewModel(context.WalletStore, verifier, runtime);
            await page.ValidateTunaWalletAsync("test".ToCharArray());
            await page.ValidateTunaWalletAsync("test".ToCharArray()); // A second failure must not lose ownership.
            Assert.Equal(TunaWalletLinkStatus.ValidationFailed, (await context.WalletStore.LoadAsync()).Status);
            Assert.Equal("(not verified)", page.TunaWalletAddress);
            Assert.False(listener.CanTakeWalletPassword!());
            await page.UnlinkTunaWalletAsync();
            Assert.False((await context.WalletStore.LoadAsync()).IsLinked);
            if (differentPayer)
            {
                Assert.Null(Assert.Single(context.UsageStore.Load().SessionRecords).EndedUtc);
                listener.UsageSink.RecordSummary(new NknTunaSessionUsageTelemetry(100, "completed", true, 0.25m, PaymentEventCount: 1));
            }
            else
            {
                Assert.False(runtime.HasSessionUnlock);
                await WaitUntilAsync(() => control.StopCalls == 1 && runtime.RuntimeStatus == "locked", TimeSpan.FromSeconds(2));
                Assert.Equal("wallet_unlinked", Assert.Single(control.StopReasons));
            }
            var record = Assert.Single(context.UsageStore.Load().SessionRecords);
            Assert.Equal("owner", record.SessionRunId);
            Assert.NotNull(record.EndedUtc);
            Assert.Equal(differentPayer, record.CompletedFromSummary);
            Assert.Equal(0.25m, record.PaidNkn);
            Assert.Equal(differentPayer ? 0 : 1, control.StopCalls);
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true, true)]
    public async Task DiagnosticsTunaWallet_DelayedBalanceCannotOverwritePeerPayer(bool useRuntime, bool success, bool samePayer = false)
    {
        var root = CreateTempRoot();
        var blocked = new BlockingTunaWalletVerifier(success ? TunaWalletValidationResult.Ok("wallet", VerifiedFixturePayer, "9")
            : TunaWalletValidationResult.Fail("lookup_failed", "wallet"));
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(root, blocked);
            using var page = CreateViewModel(context.WalletStore, blocked, useRuntime ? context.RuntimeService : null);
            var peerStore = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
            var peerPayer = samePayer ? VerifiedFixturePayer : "peer-payer";
            var peerVerifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", peerPayer, "7"));
            using var peer = CreateViewModel(peerStore, peerVerifier);
            var original = await peerStore.LoadAsync();
            var pending = page.ValidateTunaWalletAsync("first".ToCharArray());
            try
            {
                await blocked.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                await peer.ValidateTunaWalletAsync("second".ToCharArray());
            }
            finally { blocked.Release(); }
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            var current = await peerStore.LoadAsync();
            Assert.True(original.IsSameLinkAs(current));
            Assert.Equal(peerPayer, current.WalletAddress);
            Assert.Equal("7", current.BalanceNkn);
            Assert.Equal(peerPayer, page.TunaWalletAddress);
            Assert.Equal("Wallet changed. Balance check discarded.", page.CopyFeedbackText);

            var password = "stale-prompt".ToCharArray();
            await page.ValidateTunaWalletAsync(original, password);
            Assert.Single(blocked.PasswordsSeen);
            Assert.All(password, c => Assert.Equal('\0', c));
        }
        finally { blocked.Release(); DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TunaRuntimeListenerSupervisor_RefreshesIdleWalletBindingWithoutRetiringOwner(bool newLink)
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateListenerSupervisorRuntimeContextAsync(root);
            var runtime = context.RuntimeService;
            var owner = await context.WalletStore.LoadAsync();
            runtime.UnlockForNextSession(owner, "test".ToCharArray());
            using var outer = runtime.CreateRuntimeListenerSupervisorForTests();
            var get = outer.GetType().GetMethod("GetOrCreateSupervisor", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object? Select(bool idle) => get.Invoke(outer, [idle, CancellationToken.None, 0L]);
            var peer = (newLink ? TunaWalletLinkState.Linked(owner.WalletPath!, owner.LinkedUtc!.Value) : owner)
                .WithValidationResult(TunaWalletValidationResult.Ok("wallet", newLink ? owner.WalletAddress! : "peer-payer", "1"), DateTimeOffset.UtcNow);
            await context.WalletStore.SaveAsync(peer);
            var idlePeer = Assert.IsType<NknTunaListenerSidecarSupervisor>(Select(true));
            Assert.False(idlePeer.CanOfferListener);
            await context.WalletStore.SaveAsync(owner);
            var restored = Assert.IsType<NknTunaListenerSidecarSupervisor>(Select(true));
            Assert.NotSame(idlePeer, restored);
            Assert.True(restored.CanOfferListener);
            var options = (NknTunaListenerSidecarOptions)typeof(NknTunaListenerSidecarSupervisor)
                .GetField("options", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(restored)!;
            Assert.True(options.BeginUsageAttemptIfCurrent!("owner", () => true));
            await context.WalletStore.SaveAsync(peer);
            Assert.Null(Select(false)); // Accounting may already be open before the child publishes readiness.
            var ready = new TaskCompletionSource<NknTunaListenerSidecarEndpoint>();
            SetPrivateField(restored, "pendingReady", ready);
            Assert.Null(Select(true));
            Assert.Null(Select(false));
            Assert.False(ready.Task.IsCompleted);
            options.UsageSink!.RecordPayment(new NknTunaPaymentTelemetry(0.25m, 0.25m, 100, null));
            options.UsageSink.RecordSummary(new NknTunaSessionUsageTelemetry(100, "completed", true, 0.25m, PaymentEventCount: 1));
            var record = Assert.Single(context.UsageStore.Load().SessionRecords);
            Assert.Equal("owner", record.SessionRunId);
            Assert.True(record.CompletedFromSummary);
            Assert.Equal(0.25m, record.PaidNkn);
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaRuntimeWalletRelink_PreservesOpenRunAndAllowsReplacementAfterSummary()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", "address", "1"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var runtime = context.RuntimeService;
            using var vm = CreateViewModel(context.WalletStore, verifier, runtime);
            var original = await context.WalletStore.LoadAsync();
            runtime.UnlockForNextSession(original, "test-only".ToCharArray());
            var source = CreateListenerAdoptionSnapshot();
            var control = new BoundCapControl { CurrentSource = source };
            SetPrivateField(runtime, "currentTransportControl", control);
            var options = RuntimeListenerOptions(runtime);
            Assert.True(options.BeginUsageAttemptIfCurrent!(source.ProcessRunId, () => true));
            options.UsageSink!.RecordPayment(new NknTunaPaymentTelemetry(0.25m, 0.25m, 100, null));

            var replacement = Path.Combine(root, "replacement.json");
            await vm.LinkTunaWalletAsync(replacement);
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await context.WalletStore.LoadAsync()));
            Assert.True(runtime.HasSessionUnlock);
            options.UsageSink.RecordPayment(new NknTunaPaymentTelemetry(0.5m, 0.75m, 200, null));
            options.UsageSink.RecordSummary(new NknTunaSessionUsageTelemetry(256, "completed", true, 0.75m, PaymentEventCount: 2));
            var record = Assert.Single(context.UsageStore.Load().SessionRecords);
            Assert.Equal(source.ProcessRunId, record.SessionRunId);
            Assert.True(record.CompletedFromSummary);
            Assert.Equal(256, record.BytesMoved);
            Assert.Equal(0.75m, record.PaidNkn);
            Assert.Equal(2, record.PaymentEventCount);
            options.ListenerCapHandoffRequested!("byte_cap_reached", source);
            await runtime.ListenerCapStopForTests!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, control.BoundStops);
            Assert.Equal(0, control.UnboundStops);

            await vm.LinkTunaWalletAsync(replacement);
            Assert.Equal(replacement, (await context.WalletStore.LoadAsync()).WalletPath);
            Assert.False(runtime.HasSessionUnlock);
            Assert.Equal(record, Assert.Single(context.UsageStore.Load().SessionRecords));
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaRuntimeWalletRelink_BlockedSaveRejectsPaidStartup()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", "address", "1"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var store = new BlockingWalletSave(context.WalletStore);
            var runtime = new TunaRuntimePilotService(context.PreferenceStore, context.UsageStore, store, verifier);
            runtime.UnlockForNextSession(await store.LoadAsync(), "test-only".ToCharArray());
            using var supervisor = runtime.CreateRuntimeListenerSupervisorForTests();
            using var retiring = runtime.CreateRuntimeListenerSupervisorForTests(
                new RecordingTunaListenerSidecarSupervisor(CreateListenerAdoptionSnapshot()));
            Assert.True(supervisor.CanOfferListener);
            var old = RuntimeListenerOptions(runtime);
            var next = TunaWalletLinkState.Linked(Path.Combine(root, "replacement.json"), DateTimeOffset.UtcNow);
            var saving = runtime.TryReplaceWalletLinkAsync(next, CancellationToken.None);
            try
            {
                await store.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(supervisor.CanOfferListener);
                Assert.False(retiring.CanOfferListener);
                var current = RuntimeListenerOptions(runtime);
                Assert.False(old.BeginUsageAttemptIfCurrent!("stale-run", () => true));
                Assert.False(current.CanTakeWalletPassword!());
                Assert.Null(current.TakeWalletPasswordIfCurrent!(() => true));
                Assert.False(current.BeginUsageAttemptIfCurrent!("new-run", () => true));
            }
            finally { store.ReleaseSave.TrySetResult(); }
            Assert.True(await saving.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Empty(context.UsageStore.Load().SessionRecords);
            Assert.False(runtime.HasSessionUnlock);
            Assert.False(supervisor.CanOfferListener);
            Assert.True(retiring.CanOfferListener); // An existing listener does not require a retained password.
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task DiagnosticsTunaWallet_DelayedBalanceCannotVerifyReplacement(bool useRuntime, bool samePathFromOtherPage)
    {
        var root = CreateTempRoot();
        var verifier = new BlockingTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet-a", "address-a", "3"));
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var runtime = useRuntime ? context.RuntimeService : null;
            using var page = CreateViewModel(context.WalletStore, verifier, runtime);
            // A relink need not have a distinct clock reading, even at the same path.
            var original = await context.WalletStore.LoadAsync();
            using var other = CreateViewModel(context.WalletStore, verifier, runtime, () => original.LinkedUtc!.Value);
            var changingPage = samePathFromOtherPage ? other : page;
            var replacementPath = samePathFromOtherPage ? context.WalletPath : Path.Combine(root, "wallet-b.json");
            var password = "test-only".ToCharArray();
            var pending = page.ValidateTunaWalletAsync(password);
            try
            {
                await verifier.Entered.WaitAsync(TimeSpan.FromSeconds(2));
                await changingPage.LinkTunaWalletAsync(replacementPath);
            }
            finally { verifier.Release(); }
            await pending.WaitAsync(TimeSpan.FromSeconds(2));

            var stored = await context.WalletStore.LoadAsync();
            Assert.Equal(replacementPath, stored.WalletPath);
            if (samePathFromOtherPage) Assert.Equal(original.LinkedUtc, stored.LinkedUtc);
            Assert.Equal(TunaWalletLinkStatus.LinkedUnverified, stored.Status);
            Assert.Null(stored.WalletAddress);
            Assert.Null(stored.BalanceNkn);
            Assert.Null(stored.LastVerifiedUtc);
            Assert.True(changingPage.NeedsTunaWalletBalanceCheck);
            Assert.True(page.NeedsTunaWalletBalanceCheck);
            Assert.Equal(Path.GetFileName(replacementPath), page.TunaWalletFileName);
            Assert.Equal("(not verified)", page.TunaWalletAddress);
            Assert.All(password, c => Assert.Equal('\0', c));
        }
        finally { verifier.Release(); DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaRuntimeWallet_PeerRelinkRequiresItsOwnUnlockEvenAtTheSamePath()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", "address", "1"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var runtime = context.RuntimeService;
            var original = await context.WalletStore.LoadAsync();
            runtime.UnlockForNextSession(original, "first-password".ToCharArray());
            var old = RuntimeListenerOptions(runtime);
            using var supervisor = runtime.CreateRuntimeListenerSupervisorForTests();
            Assert.True(supervisor.CanOfferListener);

            var peer = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
            var replacement = TunaWalletLinkState.Linked(original.WalletPath!, original.LinkedUtc!.Value)
                .WithValidationResult(TunaWalletValidationResult.Ok("wallet", "address", "1"), DateTimeOffset.UtcNow);
            await peer.SaveAsync(replacement);
            var next = RuntimeListenerOptions(runtime);
            Assert.False(supervisor.CanOfferListener);
            foreach (var options in new[] { old, next })
            {
                Assert.False(options.CanTakeWalletPassword!());
                Assert.Null(options.TakeWalletPassword());
                Assert.Null(options.TakeWalletPasswordIfCurrent!(() => true));
                Assert.False(options.BeginUsageAttemptIfCurrent!("wrong-wallet", () => true));
            }
            Assert.Empty(context.UsageStore.Load().SessionRecords);
            runtime.UnlockForNextSession(replacement, "second-password".ToCharArray());
            Assert.Null(old.TakeWalletPasswordIfCurrent!(() => true));
            var current = RuntimeListenerOptions(runtime);
            var password = Assert.IsType<char[]>(current.TakeWalletPasswordIfCurrent!(() => true));
            try { Assert.Equal("second-password".ToCharArray(), password); }
            finally { Array.Clear(password); }
            Assert.True(current.BeginUsageAttemptIfCurrent!("right-wallet", () => true));
        }
        finally { DeleteTempRoot(root); }
    }

    private static TunaWalletLinkState OpenUnlockPrompt(DiagnosticsPageViewModel page)
    {
        TunaWalletLinkState? selection = null;
        void Capture(object? sender, TunaWalletLinkState wallet) => selection = wallet;
        page.UnlockTunaRuntimePasswordRequested += Capture;
        try { page.UnlockTunaRuntimeCommand.Execute(null); }
        finally { page.UnlockTunaRuntimePasswordRequested -= Capture; }
        return Assert.IsType<TunaWalletLinkState>(selection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticsTunaWallet_PromptForOldLinkDoesNotVerifyReplacement(bool localReselection)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", "new-address", "1"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            using var page = CreateViewModel(context.WalletStore, verifier, context.RuntimeService);
            var original = OpenUnlockPrompt(page);
            var peer = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
            if (localReselection) await page.LinkTunaWalletAsync(original.WalletPath);
            var replacement = (localReselection ? await peer.LoadAsync() : TunaWalletLinkState.Linked(original.WalletPath!, original.LinkedUtc!.Value))
                .WithValidationResult(TunaWalletValidationResult.Ok("wallet", "new-address", "1"), DateTimeOffset.UtcNow);
            await peer.SaveAsync(replacement);
            var password = "shared-fixture-password".ToCharArray();
            await page.UnlockTunaRuntimeAsync(original, password);
            Assert.Empty(verifier.PasswordsSeen);
            Assert.All(password, c => Assert.Equal('\0', c));
            Assert.False(context.RuntimeService.HasSessionUnlock);
            Assert.Equal(localReselection ? "(not verified)" : "new-address", page.TunaWalletAddress);
            Assert.Equal(localReselection ? "Wallet linked" : "Wallet changed.", page.CopyFeedbackText);
            Assert.Equal(replacement.LastVerifiedUtc, (await peer.LoadAsync()).LastVerifiedUtc);
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaRuntimeWallet_ListenerStartsDuringUnlockSaveKeepsAccounting()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", VerifiedFixturePayer, "1"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var store = new BlockingWalletSave(context.WalletStore);
            var runtime = new TunaRuntimePilotService(context.PreferenceStore, context.UsageStore, store, verifier);
            var wallet = await store.LoadAsync();
            runtime.UnlockForNextSession(wallet, "owner-password".ToCharArray());
            var listener = RuntimeListenerOptions(runtime);
            var pending = runtime.UnlockForSessionAsync("fixture".ToCharArray(), TunaRuntimeUnlockSource.Header, wallet);
            try
            {
                await store.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(listener.BeginUsageAttemptIfCurrent!("save-owner", () => true));
            }
            finally { store.ReleaseSave.TrySetResult(); }
            Assert.False((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Success);
            Assert.True((await runtime.GetUnlockStateAsync()).IsOn);
            listener.UsageSink!.RecordPayment(new NknTunaPaymentTelemetry(0.25m, 0.25m, 100, null));
            listener.UsageSink.RecordSummary(new NknTunaSessionUsageTelemetry(100, "completed", true, 0.25m, PaymentEventCount: 1));
            var summary = Assert.Single(context.UsageStore.Load().SessionRecords);
            Assert.True(summary.CompletedFromSummary);
            Assert.Equal("save-owner", summary.SessionRunId);
            Assert.Equal(0.25m, summary.PaidNkn);
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaRuntimeWallet_ListenerArgumentsAndUnlinkAreBoundToCurrentPayer()
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(root, new FakeTunaWalletVerifier(TunaWalletValidationResult.Fail("unused", "wallet")));
            var runtime = context.RuntimeService;
            var prior = await context.WalletStore.LoadAsync();
            var current = prior.WithValidationResult(TunaWalletValidationResult.Ok("wallet", "abc", "1"), DateTimeOffset.UtcNow);
            Assert.True(await context.WalletStore.SaveAsync(current, expectedValidation: prior));
            runtime.UnlockForNextSession(current, "fixture".ToCharArray());
            // A peer's balance refresh can change the payer without changing this local listener generation.
            Assert.True(await context.WalletStore.SaveAsync(prior, expectedValidation: current));
            var staleOptions = RuntimeListenerOptions(runtime);
            Assert.True(await context.WalletStore.SaveAsync(current, expectedValidation: prior));
            Assert.False(staleOptions.CanTakeWalletPassword!());
            Assert.Null(staleOptions.TakeWalletPassword());
            var listener = RuntimeListenerOptions(runtime);
            var start = new ProcessStartInfo();
            Assert.True(listener.TryAddWalletArguments(start));
            Assert.Equal(new[] { "--wallet", current.WalletPath, "--expected-payer-sha256",
                "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", "--password-stdin" }, start.ArgumentList.ToArray());
            Assert.True(listener.BeginUsageAttemptIfCurrent!("current-payer", () => true));
            Assert.False(await runtime.TryUnlinkWalletAsync(prior, CancellationToken.None));
            Assert.True(current.IsSamePayerLinkAs(await context.WalletStore.LoadAsync()));
            Assert.True(runtime.HasSessionUnlock);
            listener.UsageSink!.RecordSummary(new NknTunaSessionUsageTelemetry(1, "completed", true, 0m));
            Assert.True(Assert.Single(context.UsageStore.Load().SessionRecords).CompletedFromSummary);
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaRuntimeWallet_VerifiedPayerMismatchDoesNotSaveOrRetainCredential()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", "different-payer", "10"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var runtime = context.RuntimeService;
            var original = (await runtime.GetUnlockStateAsync()).PromptWallet;
            runtime.UnlockForNextSession(original, "previous-password".ToCharArray());
            var password = "shared-password".ToCharArray();
            var result = await runtime.UnlockForSessionAsync(password, TunaRuntimeUnlockSource.Header, original);
            Assert.False(result.Success);
            Assert.Single(verifier.PasswordsSeen);
            Assert.All(password, c => Assert.Equal('\0', c));
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await context.WalletStore.LoadAsync()));
            Assert.False(runtime.HasSessionUnlock);
            Assert.False((await runtime.GetUnlockStateAsync()).IsOn);
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(true, "mismatch")]
    [InlineData(false, "mismatch")]
    [InlineData(true, "valid")]
    [InlineData(true, "empty")]
    [InlineData(true, "failed")]
    public async Task TunaRuntimeWallet_PayerMismatchPreservesAlreadyOwnedRun(bool startsDuringVerification, string outcome)
    {
        var root = CreateTempRoot();
        var verifier = new BlockingTunaWalletVerifier(outcome == "failed" ? TunaWalletValidationResult.Fail("invalid_password", "wallet")
            : TunaWalletValidationResult.Ok("wallet", outcome == "mismatch" ? "different-payer" : VerifiedFixturePayer, outcome == "empty" ? "0" : "1"));
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var runtime = context.RuntimeService;
            var wallet = await context.WalletStore.LoadAsync();
            runtime.UnlockForNextSession(wallet, "owner-password".ToCharArray());
            var listener = RuntimeListenerOptions(runtime);
            if (!startsDuringVerification) Assert.True(listener.BeginUsageAttemptIfCurrent!("owner-run", () => true));
            var attempt = runtime.UnlockForSessionAsync("fixture".ToCharArray(), TunaRuntimeUnlockSource.Header, wallet);
            try
            {
                if (startsDuringVerification)
                {
                    await verifier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(listener.BeginUsageAttemptIfCurrent!("owner-run", () => true));
                }
            }
            finally { verifier.Release(); }
            Assert.False((await attempt.WaitAsync(TimeSpan.FromSeconds(5))).Success);
            Assert.Equal(startsDuringVerification ? 1 : 0, verifier.PasswordsSeen.Count);
            Assert.Equal(!startsDuringVerification || outcome == "valid", runtime.HasSessionUnlock);
            Assert.True((await runtime.GetUnlockStateAsync()).IsOn); // The actual listener still owns its run.
            listener.UsageSink!.RecordPayment(new NknTunaPaymentTelemetry(0.25m, 0.25m, 100, null));
            listener.UsageSink.RecordSummary(new NknTunaSessionUsageTelemetry(100, "completed", true, 0.25m, PaymentEventCount: 1));
            var summary = Assert.Single(context.UsageStore.Load().SessionRecords);
            Assert.True(summary.CompletedFromSummary);
            Assert.Equal("owner-run", summary.SessionRunId);
            Assert.Equal(0.25m, summary.PaidNkn);
        }
        finally { verifier.Release(); DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TunaRuntimeWallet_PayerChangedOnSameLinkCannotBeUnlocked(bool beforeSubmission)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet",
                beforeSubmission ? "changed-payer" : VerifiedFixturePayer, "10"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var store = new BlockingWalletSave(context.WalletStore);
            var runtime = new TunaRuntimePilotService(context.PreferenceStore, context.UsageStore, store, verifier);
            var original = (await runtime.GetUnlockStateAsync()).PromptWallet;
            var changed = original.WithValidationResult(TunaWalletValidationResult.Ok("wallet", "changed-payer", "5"), DateTimeOffset.UtcNow);
            if (beforeSubmission)
            {
                await context.WalletStore.SaveAsync(changed);
                store.ReleaseSave.TrySetResult();
            }
            var password = "shared-password".ToCharArray();
            var unlock = runtime.UnlockForSessionAsync(password, TunaRuntimeUnlockSource.Options, original);
            if (!beforeSubmission)
            {
                try
                {
                    await store.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await context.WalletStore.SaveAsync(changed);
                }
                finally { store.ReleaseSave.TrySetResult(); }
            }
            Assert.False((await unlock.WaitAsync(TimeSpan.FromSeconds(5))).Success);
            Assert.Equal(beforeSubmission ? 0 : 1, verifier.PasswordsSeen.Count);
            Assert.All(password, c => Assert.Equal('\0', c));
            Assert.Equal(JsonSerializer.Serialize(changed), JsonSerializer.Serialize(await context.WalletStore.LoadAsync()));
            Assert.False(runtime.HasSessionUnlock);
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, true, true)]
    public async Task TunaRuntimeWallet_UnlinkCanceledAtWriteGateRevokesOnlyOwner(bool matches, bool active, bool changedPayer = false)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Fail("unused", "wallet"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var store = new BlockingWalletSave(context.WalletStore);
            var runtime = new TunaRuntimePilotService(context.PreferenceStore, context.UsageStore, store, verifier);
            var wallet = await store.LoadAsync();
            var other = changedPayer ? wallet.WithValidationResult(TunaWalletValidationResult.Ok("wallet", "other-payer", "1"), DateTimeOffset.UtcNow)
                : TunaWalletLinkState.Linked(wallet.WalletPath!, wallet.LinkedUtc!.Value);
            runtime.UnlockForNextSession(wallet, "owner-password".ToCharArray());
            if (active) Assert.True(RuntimeListenerOptions(runtime).BeginUsageAttemptIfCurrent!("owner-run", () => true));
            var holdingGate = runtime.TrySaveWalletValidationAsync(wallet, wallet, CancellationToken.None);
            try
            {
                await store.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var cancel = new CancellationTokenSource();
                var unlink = runtime.TryUnlinkWalletAsync(matches ? wallet : other, cancel.Token);
                Assert.False(unlink.IsCompleted);
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unlink.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(!matches, runtime.HasSessionUnlock);
                Assert.Equal(!matches, (await runtime.GetUnlockStateAsync()).IsOn);
                if (active)
                {
                    var session = Assert.Single(context.UsageStore.Load().SessionRecords);
                    Assert.Equal("owner-run", session.SessionRunId);
                    Assert.Equal(matches, session.EndedUtc.HasValue);
                    if (matches) Assert.Equal("wallet_unlink_failed", session.StopReason);
                }
                Assert.True(wallet.IsSameLinkAs(await store.LoadAsync()));
            }
            finally { store.ReleaseSave.TrySetResult(); await holdingGate; }
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TunaRuntimeWallet_PendingUnlinkProjectsOnlyMatchingValidationOff(bool matches)
    {
        var root = CreateTempRoot();
        var verifier = new BlockingTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", VerifiedFixturePayer, "1"));
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var store = new BlockingWalletSave(context.WalletStore) { BlockClear = true };
            var runtime = new TunaRuntimePilotService(context.PreferenceStore, context.UsageStore, store, verifier);
            store.ReleaseSave.TrySetResult();
            var wallet = await store.LoadAsync();
            var other = TunaWalletLinkState.Linked(wallet.WalletPath!, wallet.LinkedUtc!.Value);
            var checking = runtime.UnlockForSessionAsync("fixture".ToCharArray(), TunaRuntimeUnlockSource.Header, wallet);
            await verifier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var transport = new RecordingTransportAccelerationControl { HangStop = true };
            SetPrivateField(runtime, "currentTransportControl", transport);
            Assert.True((await runtime.GetUnlockStateAsync()).IsOn);
            var notifications = 0;
            runtime.StateChanged += (_, _) => Interlocked.Increment(ref notifications);
            var unlink = runtime.TryUnlinkWalletAsync(matches ? wallet : other, CancellationToken.None);
            try
            {
                Assert.False(unlink.IsCompleted);
                var header = await runtime.GetUnlockStateAsync();
                Assert.Equal(!matches, header.IsOn);
                if (matches)
                {
                    Assert.True(Volatile.Read(ref notifications) > 0);
                    Assert.False(header.CanToggle);
                    Assert.Equal("locked", header.RuntimeStatus);
                }
            }
            finally
            {
                store.ReleaseClear.TrySetResult();
                try { await unlink.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { verifier.Release(); await checking.WaitAsync(TimeSpan.FromSeconds(5)); }
            }
            Assert.Equal(matches, await unlink);
            Assert.Equal(!matches, (await checking).Success);
            Assert.Equal(!matches, runtime.HasSessionUnlock);
            if (matches) Assert.Equal("locked", runtime.RuntimeStatus);
            Assert.Equal(0, transport.StopCalls);
        }
        finally { verifier.Release(); DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaRuntimeWallet_HeaderPromptCannotUnlockSamePathReplacement()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", "address", "1"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var runtime = context.RuntimeService;
            var state = await runtime.GetUnlockStateAsync();
            Assert.True(state.CanToggle);
            var original = state.PromptWallet;
            var replacement = TunaWalletLinkState.Linked(original.WalletPath!, original.LinkedUtc!.Value)
                .WithValidationResult(TunaWalletValidationResult.Ok("wallet", "address", "1"), DateTimeOffset.UtcNow);
            await context.WalletStore.SaveAsync(replacement);
            var password = "shared-fixture-password".ToCharArray();
            Assert.False((await runtime.UnlockForSessionAsync(password, TunaRuntimeUnlockSource.Header, expectedWallet: original)).Success);
            Assert.Empty(verifier.PasswordsSeen);
            Assert.All(password, c => Assert.Equal('\0', c));
            Assert.False(runtime.HasSessionUnlock);
            Assert.Equal(replacement.LastVerifiedUtc, (await context.WalletStore.LoadAsync()).LastVerifiedUtc);
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task TunaRuntimeWallet_FailedUnlinkDuringFirstReadTargetsOnlyPromptWallet(bool matching, bool oldUnlock)
    {
        var root = CreateTempRoot();
        var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", "address", "1"));
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var store = new BlockingWalletLoad(context.WalletStore, snapshotBeforeWait: true);
            var runtime = new TunaRuntimePilotService(context.PreferenceStore, context.UsageStore, store, verifier);
            var old = await store.LoadAsync();
            if (oldUnlock) runtime.UnlockForNextSession(old, "old-password".ToCharArray());
            var wallet = TunaWalletLinkState.Linked(old.WalletPath!, old.LinkedUtc!.Value)
                .WithValidationResult(TunaWalletValidationResult.Ok("wallet", "address", "1"), DateTimeOffset.UtcNow);
            await context.WalletStore.SaveAsync(wallet);
            store.BlockNextLoad = 1;
            var password = "new-password".ToCharArray();
            var pending = runtime.UnlockForSessionAsync(password, TunaRuntimeUnlockSource.Header, expectedWallet: wallet);
            try
            {
                await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var peerLock = File.Open(Path.Combine(root, "tuna-wallet-link.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                using var cancel = new CancellationTokenSource();
                var unlink = runtime.TryUnlinkWalletAsync(matching ? wallet : old, cancel.Token);
                Assert.False(unlink.IsCompleted);
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unlink.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally { store.Release.TrySetResult(); }
            Assert.Equal(!matching, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Success);
            Assert.Equal(matching ? 0 : 1, verifier.PasswordsSeen.Count);
            Assert.All(password, c => Assert.Equal('\0', c));
            Assert.Equal(!matching, runtime.HasSessionUnlock);
            Assert.Equal(wallet.LinkId, (await context.WalletStore.LoadAsync()).LinkId);
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticsTunaWallet_BalancePromptCannotVerifySamePathReplacement(bool local)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", "address", "1"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            using var page = CreateViewModel(context.WalletStore, verifier, context.RuntimeService);
            TunaWalletLinkState? prompted = null;
            page.ValidateTunaWalletPasswordRequested += (_, wallet) => prompted = wallet;
            page.ValidateTunaWalletCommand.Execute(null);
            var original = Assert.IsType<TunaWalletLinkState>(prompted);
            if (local) await page.LinkTunaWalletAsync(original.WalletPath);
            else await context.WalletStore.SaveAsync(TunaWalletLinkState.Linked(original.WalletPath!, original.LinkedUtc!.Value));
            var replacement = await context.WalletStore.LoadAsync();
            var password = "shared-fixture-password".ToCharArray();
            await page.ValidateTunaWalletAsync(original, password);
            Assert.Empty(verifier.PasswordsSeen);
            Assert.All(password, c => Assert.Equal('\0', c));
            var stored = await context.WalletStore.LoadAsync();
            Assert.Equal(replacement.LinkId, stored.LinkId);
            Assert.Null(stored.LastVerifiedUtc);
            Assert.True(page.NeedsTunaWalletBalanceCheck);
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task DiagnosticsTunaWallet_BalanceLookupCannotVerifyAfterLocalReselection()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", "address", "1"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var store = new BlockingWalletLoad(context.WalletStore, snapshotBeforeWait: true);
            using var page = CreateViewModel(store, verifier, context.RuntimeService);
            var original = await store.LoadAsync();
            store.BlockNextLoad = 1;
            var password = "shared-fixture-password".ToCharArray();
            var check = page.ValidateTunaWalletAsync(original, password);
            try
            {
                await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await page.LinkTunaWalletAsync(original.WalletPath);
            }
            finally { store.Release.TrySetResult(); }
            await check.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(verifier.PasswordsSeen);
            Assert.All(password, c => Assert.Equal('\0', c));
            Assert.True(page.NeedsTunaWalletBalanceCheck);
            Assert.Equal("Wallet linked", page.CopyFeedbackText);
            Assert.NotEqual(original.LinkId, (await store.LoadAsync()).LinkId);
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaRuntimeWallet_UnlinkCurrentDiskLinkPreservesOtherPaidRun()
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateListenerSupervisorRuntimeContextAsync(root);
            var runtime = context.RuntimeService;
            var wallet = await context.WalletStore.LoadAsync();
            runtime.UnlockForNextSession(wallet, "owner-password".ToCharArray());
            var control = new BoundCapControl();
            SetPrivateField(runtime, "currentTransportControl", control);
            var listener = RuntimeListenerOptions(runtime);
            Assert.True(listener.BeginUsageAttemptIfCurrent!("owner-run", () => true));
            listener.UsageSink!.RecordPayment(new NknTunaPaymentTelemetry(0.25m, 0.25m, 100, null));
            var open = Assert.Single(context.UsageStore.Load().SessionRecords);
            Assert.Null(open.EndedUtc);
            Assert.Equal(0.25m, open.PaidNkn);
            var replacement = TunaWalletLinkState.Linked(wallet.WalletPath!, wallet.LinkedUtc!.Value);
            await context.WalletStore.SaveAsync(replacement); // Written by a peer while the local run is active.

            Assert.True(await runtime.TryUnlinkWalletAsync(replacement, CancellationToken.None));
            Assert.False((await context.WalletStore.LoadAsync()).IsLinked);
            Assert.Equal(open, Assert.Single(context.UsageStore.Load().SessionRecords));
            listener.UsageSink.RecordPayment(new NknTunaPaymentTelemetry(0.5m, 0.75m, 200, null));
            listener.UsageSink.RecordSummary(new NknTunaSessionUsageTelemetry(256, "completed", true, 0.75m, PaymentEventCount: 2));
            var summary = Assert.Single(context.UsageStore.Load().SessionRecords);
            Assert.Equal("owner-run", summary.SessionRunId);
            Assert.True(summary.CompletedFromSummary);
            Assert.Equal(2, summary.PaymentEventCount);
            Assert.Equal(0.75m, summary.PaidNkn);
            Assert.Equal(0, control.UnboundStops);
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TunaRuntimeWallet_FailedUnlinkRevokesOnlyMatchingPendingVerification(bool matching, bool previouslyUnlocked)
    {
        var root = CreateTempRoot();
        var verifier = new BlockingTunaWalletVerifier(TunaWalletValidationResult.Ok("B", "new-address", "1"));
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var runtime = context.RuntimeService;
            var original = await context.WalletStore.LoadAsync();
            if (previouslyUnlocked) runtime.UnlockForNextSession(original, "old-fixture-password".ToCharArray());
            var replacement = TunaWalletLinkState.Linked(original.WalletPath!, original.LinkedUtc!.Value)
                .WithValidationResult(TunaWalletValidationResult.Ok("B", "new-address", "1"), DateTimeOffset.UtcNow);
            await context.WalletStore.SaveAsync(replacement);
            var unlock = runtime.UnlockForSessionAsync("fixture".ToCharArray(), TunaRuntimeUnlockSource.Header, replacement);
            try
            {
                await verifier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                using var peerLock = File.Open(Path.Combine(root, "tuna-wallet-link.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                using var cancel = new CancellationTokenSource();
                var unlink = runtime.TryUnlinkWalletAsync(matching ? replacement : original, cancel.Token);
                Assert.False(unlink.IsCompleted);
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unlink.WaitAsync(TimeSpan.FromSeconds(5)));
                var header = await runtime.GetUnlockStateAsync();
                Assert.Equal(matching ? "locked" : "unlock_validating", header.RuntimeStatus);
                Assert.Equal(!matching, header.IsOn);
                if (matching) Assert.False(header.CanToggle); // The canceled verifier still owns the slot.
            }
            finally { verifier.Release(); }
            Assert.Equal(!matching, (await unlock.WaitAsync(TimeSpan.FromSeconds(5))).Success);
            Assert.Equal(!matching, runtime.HasSessionUnlock);
            Assert.Equal(replacement.LinkId, (await context.WalletStore.LoadAsync()).LinkId);
            Assert.Empty(context.UsageStore.Load().SessionRecords);
        }
        finally { verifier.Release(); DeleteTempRoot(root); }
    }

    [Fact]
    public async Task DiagnosticsTunaWallet_DelayedUnlockRefreshCannotReplaceNewLocalSelection()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("A", VerifiedFixturePayer, "1"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            // Only the Options reload is blocked; the runtime has already finished unlocking A.
            var blocked = new BlockingWalletLoad(context.WalletStore, snapshotBeforeWait: true);
            using var page = CreateViewModel(blocked, verifier, context.RuntimeService);
            blocked.BlockNextLoad = 1;
            var unlock = page.UnlockTunaRuntimeAsync(OpenUnlockPrompt(page), "test-only".ToCharArray());
            try
            {
                await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(context.RuntimeService.HasSessionUnlock);
                await page.LinkTunaWalletAsync(Path.Combine(root, "B.json"));
                Assert.False(context.RuntimeService.HasSessionUnlock);
            }
            finally { blocked.Release.TrySetResult(); }
            await unlock.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("B.json", page.TunaWalletFileName);
            Assert.True(page.NeedsTunaWalletBalanceCheck);
            Assert.Equal("Wallet linked", page.CopyFeedbackText);
            Assert.Equal("B.json", (await context.WalletStore.LoadAsync()).WalletFileName);
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticsTunaWallet_StaleUnlinkLeavesReplacementAndItsRunAlone(bool useRuntime)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", "new-address", "1"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var runtime = context.RuntimeService;
            using var stalePage = CreateViewModel(context.WalletStore, verifier, useRuntime ? runtime : null);
            var peer = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
            var original = await peer.LoadAsync();
            var replacement = TunaWalletLinkState.Linked(original.WalletPath!, original.LinkedUtc!.Value)
                .WithValidationResult(TunaWalletValidationResult.Ok("wallet", "new-address", "1"), DateTimeOffset.UtcNow);
            await peer.SaveAsync(replacement);
            runtime.UnlockForNextSession(replacement, "new-password".ToCharArray());
            var listener = RuntimeListenerOptions(runtime);
            Assert.True(listener.BeginUsageAttemptIfCurrent!("new-run", () => true));

            await stalePage.UnlinkTunaWalletAsync();
            Assert.Equal(replacement.LinkId, (await peer.LoadAsync()).LinkId);
            Assert.Equal("new-address", stalePage.TunaWalletAddress);
            Assert.Contains("not unlinked", stalePage.CopyFeedbackText, StringComparison.OrdinalIgnoreCase);
            Assert.True(runtime.HasSessionUnlock);
            listener.UsageSink!.RecordPayment(new NknTunaPaymentTelemetry(0.25m, 0.25m, 100, null));
            listener.UsageSink.RecordSummary(new NknTunaSessionUsageTelemetry(256, "completed", true, 0.25m, PaymentEventCount: 1));
            var summary = Assert.Single(context.UsageStore.Load().SessionRecords);
            Assert.Equal("new-run", summary.SessionRunId);
            Assert.True(summary.CompletedFromSummary);
            Assert.Equal(0.25m, summary.PaidNkn);
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task DiagnosticsTunaWallet_StaleRefreshCannotReplaceNewLocalSelection()
    {
        var root = CreateTempRoot();
        var verifier = new BlockingTunaWalletVerifier(TunaWalletValidationResult.Ok("A", "old-address", "1"));
        var path = Path.Combine(root, "state.json");
        var store = new JsonTunaWalletLinkStore(() => path);
        var blocked = new BlockingWalletLoad(store, snapshotBeforeWait: true);
        try
        {
            using var page = CreateViewModel(blocked, verifier);
            await page.LinkTunaWalletAsync(Path.Combine(root, "A.json"));
            var pending = page.ValidateTunaWalletAsync("test-only".ToCharArray());
            try
            {
                await verifier.Entered.WaitAsync(TimeSpan.FromSeconds(2));
                var other = new JsonTunaWalletLinkStore(() => path);
                await other.SaveAsync(TunaWalletLinkState.Linked(Path.Combine(root, "B.json"), DateTimeOffset.UtcNow));
                blocked.BlockNextLoad = 1;
                verifier.Release();
                await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await page.LinkTunaWalletAsync(Path.Combine(root, "C.json"));
            }
            finally { verifier.Release(); blocked.Release.TrySetResult(); }
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("C.json", page.TunaWalletFileName);
            Assert.True(page.NeedsTunaWalletBalanceCheck);
            Assert.Equal("C.json", (await store.LoadAsync()).WalletFileName);
        }
        finally { verifier.Release(); blocked.Release.TrySetResult(); DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData("link")]
    [InlineData("unlink")]
    [InlineData("validate")]
    public async Task TunaWalletStore_ContendingWriterCanCancelWithoutChangingState(string operation)
    {
        var root = CreateTempRoot();
        try
        {
            var path = Path.Combine(root, "state.json");
            var first = new JsonTunaWalletLinkStore(() => path);
            var other = new JsonTunaWalletLinkStore(() => path);
            var wallet = TunaWalletLinkState.Linked(Path.Combine(root, "A.json"), DateTimeOffset.UtcNow);
            await first.SaveAsync(wallet);
            var before = await File.ReadAllTextAsync(path);
            using (File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var cancel = new CancellationTokenSource())
            {
                var pending = operation switch
                {
                    "unlink" => other.TryClearAsync(wallet, cancel.Token),
                    "link" => other.SaveAsync(TunaWalletLinkState.Linked(Path.Combine(root, "B.json"), DateTimeOffset.UtcNow), cancel.Token),
                    _ => other.SaveAsync(wallet.WithValidationResult(TunaWalletValidationResult.Ok("A", "addr", "1"), DateTimeOffset.UtcNow),
                        cancel.Token, expectedValidation: wallet),
                };
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            Assert.Equal(before, await File.ReadAllTextAsync(path));
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaWalletStore_UnlinkRechecksIdentityAfterWaitingForPeerWriter()
    {
        var root = CreateTempRoot();
        try
        {
            var path = Path.Combine(root, "state.json");
            var store = new JsonTunaWalletLinkStore(() => path);
            var original = TunaWalletLinkState.Linked(Path.Combine(root, "wallet.json"), DateTimeOffset.UnixEpoch);
            var replacement = TunaWalletLinkState.Linked(original.WalletPath!, original.LinkedUtc!.Value);
            await store.SaveAsync(original);
            Task<bool> unlink;
            using (File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                unlink = store.TryClearAsync(original);
                Assert.False(unlink.IsCompleted);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(replacement, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            Assert.False(await unlink.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(replacement.LinkId, (await store.LoadAsync()).LinkId);
            Assert.True(await store.TryClearAsync(replacement));
            Assert.False(File.Exists(path));
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaWalletStore_CanceledUnlinkRevokesUnlockButDoesNotReportWalletRemoved()
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateVerifiedRuntimeContextAsync(root,
                new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("A", "address", "1")));
            var wallet = await context.WalletStore.LoadAsync();
            var runtime = context.RuntimeService;
            runtime.UnlockForNextSession(wallet, "test-only".ToCharArray());
            Assert.True(RuntimeListenerOptions(runtime).BeginUsageAttemptIfCurrent!("paid-run", () => true));
            using var peerLock = File.Open(Path.Combine(root, "tuna-wallet-link.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var cancel = new CancellationTokenSource();
            var unlink = runtime.TryUnlinkWalletAsync(wallet, cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unlink.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(runtime.HasSessionUnlock);
            Assert.Equal(wallet.LinkId, (await context.WalletStore.LoadAsync()).LinkId);
            Assert.Equal("wallet_unlink_failed", Assert.Single(context.UsageStore.Load().SessionRecords).StopReason);
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaWalletStore_WindowsPeerRelinkWinsAgainstWaitingValidation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = CreateTempRoot();
        try
        {
            var path = Path.Combine(root, "state.json");
            var store = new JsonTunaWalletLinkStore(() => path);
            var original = TunaWalletLinkState.Linked(Path.Combine(root, "wallet.json"), DateTimeOffset.UnixEpoch);
            var replacement = TunaWalletLinkState.Linked(original.WalletPath!, original.LinkedUtc!.Value);
            await store.SaveAsync(original);
            await new JsonTunaWalletLinkStore(() => Path.Combine(root, "next.json")).SaveAsync(replacement);
            // Stand-in for a second instance: only touches synthetic state in this test directory.
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", """
                    $ErrorActionPreference = 'Stop'
                    $h = [IO.File]::Open('state.json.lock','OpenOrCreate','ReadWrite','None')
                    try {
                        [Console]::WriteLine('locked')
                        if ([Console]::ReadLine() -eq 'replace') { [IO.File]::Copy('next.json','state.json',$true) }
                    } finally { $h.Dispose() }
                    """ }
            };
            using var peer = Process.Start(start)!;
            var timeout = TimeSpan.FromSeconds(10);
            try
            {
                Assert.Equal("locked", await peer.StandardOutput.ReadLineAsync().WaitAsync(timeout));
                var pending = store.SaveAsync(original.WithValidationResult(
                    TunaWalletValidationResult.Ok("wallet.json", "old-address", "1"), DateTimeOffset.UtcNow), expectedValidation: original);
                Assert.False(pending.IsCompleted);
                await peer.StandardInput.WriteLineAsync("replace");
                Assert.False(await pending.WaitAsync(timeout));
            }
            finally
            {
                peer.StandardInput.Close();
                await peer.WaitForExitAsync().WaitAsync(timeout);
            }
            Assert.Equal(0, peer.ExitCode);
            var stored = await store.LoadAsync();
            Assert.Equal(replacement.LinkId, stored.LinkId);
            Assert.Equal(TunaWalletLinkStatus.LinkedUnverified, stored.Status);
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaWalletStore_HeaderUnlockCannotRestoreReplacementDuringSave()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("A", VerifiedFixturePayer, "1"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var blocked = new BlockingWalletSave(context.WalletStore);
            var runtime = new TunaRuntimePilotService(context.PreferenceStore, context.UsageStore, blocked, verifier);
            var peer = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
            var replacement = TunaWalletLinkState.Linked(Path.Combine(root, "B.json"), DateTimeOffset.UtcNow);
            var unlock = runtime.UnlockForSessionAsync("test-only".ToCharArray(), TunaRuntimeUnlockSource.Header, await peer.LoadAsync());
            try
            {
                await blocked.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await peer.SaveAsync(replacement);
            }
            finally { blocked.ReleaseSave.TrySetResult(); }
            Assert.False((await unlock.WaitAsync(TimeSpan.FromSeconds(5))).Success);
            Assert.False(runtime.HasSessionUnlock);
            Assert.Equal(replacement.LinkId, (await peer.LoadAsync()).LinkId);
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task DiagnosticsTunaWallet_ExistingLinkWithoutIdCanStillValidate()
    {
        var root = CreateTempRoot();
        try
        {
            var statePath = Path.Combine(root, "state.json");
            var walletPath = Path.Combine(root, "wallet.json");
            await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(new
            {
                walletPath, linkedUtc = DateTimeOffset.UnixEpoch, status = TunaWalletLinkStatus.LinkedUnverified
            }));
            var store = new JsonTunaWalletLinkStore(() => statePath);
            using var vm = CreateViewModel(store, new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet.json", "address", "1")));
            await vm.ValidateTunaWalletAsync("test-only".ToCharArray());
            var stored = await store.LoadAsync();
            Assert.Equal(walletPath, stored.WalletPath);
            Assert.Equal(TunaWalletLinkStatus.VerifiedFunded, stored.Status);
            Assert.NotNull(stored.ValidationRevision);
            Assert.Equal("address", stored.WalletAddress);
        }
        finally { DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData("lock", false)]
    [InlineData("lock", true)]
    [InlineData("disable", false)]
    [InlineData("disable", true)]
    public async Task TunaRuntimeListenerLifetime_AccountingWriteFailureCannotPreventStop(string operation, bool accessDenied)
    {
        var root = CreateTempRoot();
        VerifiedRuntimeContext? context = null;
        FailingUsageSaveStore? store = null;
        try
        {
            context = await CreateVerifiedRuntimeContextAsync(root,
                new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", "public-address", "1")),
                wrapUsageStore: inner => store = new FailingUsageSaveStore(inner));
            var runtime = context.RuntimeService;
            var control = new BoundCapControl();
            SetPrivateField(runtime, "currentTransportControl", control);
            var wallet = await context.WalletStore.LoadAsync();
            runtime.UnlockForNextSession(wallet, "test-only".ToCharArray());
            var options = RuntimeListenerOptions(runtime);
            var runId = Guid.NewGuid().ToString("N");
            Assert.True(options.BeginUsageAttemptIfCurrent!(runId, () => true));
            options.UsageSink!.RecordPayment(new NknTunaPaymentTelemetry(0.25m, 0.25m, 1048576, null));
            options.StatusChanged!("listener_ready");
            var persisted = JsonSerializer.Serialize(context.UsageStore.Load());
            var saves = store!.SaveCalls;
            const string privateDetail = "test-only-private-accounting-path";
            store.Failure = accessDenied ? new UnauthorizedAccessException(privateDetail) : new IOException(privateDetail);

            async Task RevokeAsync()
            {
                if (operation == "lock")
                    await runtime.LockOrStopForSessionAsync("test_lock", TunaRuntimeUnlockSource.Header);
                else
                    runtime.SavePreferences(new TunaRuntimePreferenceState { Enabled = false });
            }

            await RevokeAsync();
            await WaitUntilAsync(() => control.UnboundStops == 1 &&
                runtime.RuntimeStatus == (operation == "lock" ? "locked" : "off"), TimeSpan.FromSeconds(5));
            Assert.False(runtime.HasSessionUnlock);
            var closed = Assert.Single(runtime.Usage.SessionRecords);
            Assert.Equal(runId, closed.SessionRunId);
            Assert.NotNull(closed.EndedUtc);
            Assert.False(closed.CompletedFromSummary);
            Assert.Equal(TunaPaymentTelemetryStatus.AccountingIncomplete, closed.PaymentTelemetryStatus);
            Assert.Equal(0.25m, closed.PaidNkn);
            Assert.Equal(1, closed.PaymentEventCount);
            Assert.Equal(1048576, closed.BytesMoved);
            Assert.Equal(persisted, JsonSerializer.Serialize(context.UsageStore.Load())); // Failed save is not durable evidence.
            Assert.Equal(saves + 1, store.SaveCalls);
            var runHash = Assert.IsType<string>(typeof(TunaRuntimePilotService)
                .GetMethod("HashForLog", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [runId]));
            var log = LocalOperationalLog.GetRecentLogText();
            Assert.Contains($"event=tuna_usage_session_persist_failed; session_run_id_hash={runHash}; error={store.Failure.GetType().Name}", log);
            Assert.DoesNotContain($"event=tuna_usage_session_recorded; session_run_id_hash={runHash}", log);
            Assert.DoesNotContain(privateDetail, log);

            await RevokeAsync();
            options.UsageSink.RecordIncomplete("late_stop");
            Assert.Equal(saves + 1, store.SaveCalls); // No automatic retry or duplicate closure.
            Assert.Equal(closed, Assert.Single(runtime.Usage.SessionRecords));

            store.Failure = null;
            runtime.SavePreferences(new TunaRuntimePreferenceState { Enabled = true, LastRuntimeStatus = "locked" });
            runtime.UnlockForNextSession(wallet, "replacement".ToCharArray());
            var successor = RuntimeListenerOptions(runtime);
            Assert.True(successor.BeginUsageAttemptIfCurrent!("successor-run", () => true));
            successor.UsageSink!.RecordPayment(new NknTunaPaymentTelemetry(0.5m, 0.5m, 2097152, null));
            var beforeLate = JsonSerializer.Serialize(context.UsageStore.Load());
            options.UsageSink.RecordSummary(new NknTunaSessionUsageTelemetry(9999999, "late_summary", true, 9m));
            options.UsageSink.RecordPayment(new NknTunaPaymentTelemetry(9m, 9m, 9999999, null));
            Assert.Equal(beforeLate, JsonSerializer.Serialize(context.UsageStore.Load()));
            Assert.Equal(closed, context.UsageStore.Load().SessionRecords[0]);
            Assert.Equal("successor-run", context.UsageStore.Load().LastSessionRecord!.SessionRunId);
            Assert.Equal(0.75m, context.UsageStore.Load().TotalPaidNkn);
            Assert.Equal(1, control.UnboundStops);
            Assert.Equal(0, control.BoundStops);
        }
        finally { context?.RuntimeService.ClearSessionUnlock(); DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData("lock", "listener_ready")]
    [InlineData("lock", "peer_connected")]
    [InlineData("disable", "listener_ready")]
    [InlineData("disable", "peer_connected")]
    public async Task TunaRuntimeListenerLifetime_RejectedStreamPreservesLifecycleAndUserStop(string operation, string status)
    {
        var root = CreateTempRoot();
        VerifiedRuntimeContext? context = null;
        try
        {
            context = await CreateListenerSupervisorRuntimeContextAsync(root);
            var runtime = context.RuntimeService;
            var control = new BoundCapControl();
            SetPrivateField(runtime, "currentTransportControl", control);
            runtime.UnlockForNextSession(await context.WalletStore.LoadAsync(), "test-only".ToCharArray());
            var options = RuntimeListenerOptions(runtime);
            using var supervisor = new NknTunaListenerSidecarSupervisor(options);
            using var owner = UnstartedListenerOwner(); // No process or network launch.
            var source = CreateListenerAdoptionSnapshot();
            SetPrivateField(supervisor, "processOwner", owner);
            SetPrivateField(supervisor, "currentRequest", source.CurrentRequest);
            SetPrivateField(supervisor, "endpoint", source.Endpoint);
            Assert.True(options.BeginUsageAttemptIfCurrent!(owner.RunId, () => true));
            options.StatusChanged!(status);
            var preferences = JsonSerializer.Serialize(context.PreferenceStore.Load());
            var usage = JsonSerializer.Serialize(context.UsageStore.Load());
            var observations = new List<AccelerationStateChangedEventArgs>();
            supervisor.StateChanged += (_, e) => observations.Add(e);

            ListenerOutput(supervisor, owner, "{\"event\":\"tuna_stream_rejected\",\"reason\":\"remote_address_mismatch\"}");

            Assert.Equal(status, runtime.RuntimeStatus);
            Assert.Equal(preferences, JsonSerializer.Serialize(context.PreferenceStore.Load()));
            Assert.Equal(usage, JsonSerializer.Serialize(context.UsageStore.Load()));
            Assert.True(runtime.HasSessionUnlock);
            Assert.Equal("listener_stream_rejected_remote_address_mismatch", Assert.Single(observations).Reason);
            Assert.False((bool)typeof(NknTunaListenerSidecarSupervisor)
                .GetField("terminalFailureObserved", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(supervisor)!);
            Assert.Equal(0, control.UnboundStops);

            if (operation == "lock")
                await runtime.LockOrStopForSessionAsync("test_lock", TunaRuntimeUnlockSource.Header);
            else
                runtime.SavePreferences(new TunaRuntimePreferenceState { Enabled = false });
            await WaitUntilAsync(() => control.UnboundStops == 1 &&
                runtime.RuntimeStatus == (operation == "lock" ? "locked" : "off"), TimeSpan.FromSeconds(5));
            Assert.False(runtime.HasSessionUnlock);
            var closed = Assert.Single(context.UsageStore.Load().SessionRecords);
            Assert.Equal(owner.RunId, closed.SessionRunId);
            Assert.NotNull(closed.EndedUtc);
            Assert.Equal(TunaPaymentTelemetryStatus.AccountingIncomplete, closed.PaymentTelemetryStatus);
            Assert.Equal(0, control.BoundStops);
        }
        finally { context?.RuntimeService.ClearSessionUnlock(); DeleteTempRoot(root); }
    }

    private sealed class FailingUsageSaveStore(ITunaUsageAccountingStore inner) : ITunaUsageAccountingStore
    {
        public Exception? Failure;
        public int SaveCalls;
        public TunaUsageAccountingState Load() => inner.Load();
        public void Save(TunaUsageAccountingState state)
        {
            SaveCalls++;
            if (Failure is { } failure) throw failure;
            inner.Save(state);
        }
    }

    [Theory]
    [InlineData("lock", false)]
    [InlineData("lock", true)]
    [InlineData("disable", false)]
    [InlineData("disable", true)]
    public async Task TunaRuntimeListenerLifetime_LockDisableClosesExactRunAndRejectsRetiredCallbacks(string operation, bool startSuccessor)
    {
        var root = CreateTempRoot();
        VerifiedRuntimeContext? context = null;
        try
        {
            context = await CreateListenerSupervisorRuntimeContextAsync(root);
            var runtime = context.RuntimeService;
            var control = new BoundCapControl();
            SetPrivateField(runtime, "currentTransportControl", control);
            var wallet = await context.WalletStore.LoadAsync();
            runtime.UnlockForNextSession(wallet, "test-only".ToCharArray());
            var options = RuntimeListenerOptions(runtime);
            // Synthetic usage callbacks only: no listener process or network is started.
            Assert.True(options.BeginUsageAttemptIfCurrent!("retired-run", () => true));
            options.UsageSink!.RecordPayment(new NknTunaPaymentTelemetry(0.25m, 0.25m, 1048576, null));
            options.StatusChanged!("listener_ready");

            async Task RevokeAsync()
            {
                if (operation == "lock")
                    await runtime.LockOrStopForSessionAsync("test_lock", TunaRuntimeUnlockSource.Header);
                else
                    runtime.SavePreferences(new TunaRuntimePreferenceState { Enabled = false });
            }

            await RevokeAsync();
            Assert.False(runtime.HasSessionUnlock);
            Assert.Null(typeof(TunaRuntimePilotService).GetField("currentSessionRunId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime));
            var closed = Assert.Single(context.UsageStore.Load().SessionRecords);
            Assert.Equal("retired-run", closed.SessionRunId);
            Assert.NotNull(closed.EndedUtc);
            Assert.Equal(TunaPaymentTelemetryStatus.AccountingIncomplete, closed.PaymentTelemetryStatus);
            Assert.False(closed.CompletedFromSummary);
            Assert.Equal(0.25m, closed.PaidNkn);
            Assert.Equal(1048576, closed.BytesMoved);
            Assert.Equal(1, closed.PaymentEventCount);
            Assert.Equal(operation == "lock" ? "test_lock" : "runtime_disabled", closed.StopReason);
            Assert.Equal(0.25m, context.UsageStore.Load().TotalPaidNkn);
            await WaitUntilAsync(() => control.UnboundStops == 1 &&
                runtime.RuntimeStatus == (operation == "lock" ? "locked" : "off"), TimeSpan.FromSeconds(5));

            // Repeated revocation must not create or rewrite historical accounting.
            var closedUsage = JsonSerializer.Serialize(context.UsageStore.Load());
            await RevokeAsync();
            Assert.Equal(closedUsage, JsonSerializer.Serialize(context.UsageStore.Load()));

            if (startSuccessor)
            {
                runtime.SavePreferences(new TunaRuntimePreferenceState { Enabled = true, LastRuntimeStatus = "locked" });
                runtime.UnlockForNextSession(wallet, "replacement".ToCharArray());
                var successor = RuntimeListenerOptions(runtime);
                Assert.True(successor.BeginUsageAttemptIfCurrent!("successor-run", () => true));
                successor.UsageSink!.RecordPayment(new NknTunaPaymentTelemetry(0.5m, 0.5m, 2097152, null));
                successor.StatusChanged!("listener_ready");
            }

            var usage = JsonSerializer.Serialize(context.UsageStore.Load());
            var preferences = JsonSerializer.Serialize(context.PreferenceStore.Load());
            var status = runtime.RuntimeStatus;
            options.UsageSink.RecordIncomplete("late_stop");
            options.UsageSink.RecordSummary(new NknTunaSessionUsageTelemetry(9999999, "late_cap", true, 9m,
                PaymentEventCount: 9, CapReached: true));
            options.UsageSink.RecordPayment(new NknTunaPaymentTelemetry(9m, 9m, 9999999, null));
            options.StatusChanged!("active");
            Assert.False(options.BeginUsageAttemptIfCurrent!("late-run", () => true));

            Assert.Equal(usage, JsonSerializer.Serialize(context.UsageStore.Load()));
            Assert.Equal(preferences, JsonSerializer.Serialize(context.PreferenceStore.Load()));
            Assert.Equal(status, runtime.RuntimeStatus);
            Assert.Equal(startSuccessor, runtime.HasSessionUnlock);
            Assert.Equal(closed, context.UsageStore.Load().SessionRecords[0]);
            Assert.Equal(startSuccessor ? "successor-run" : null,
                typeof(TunaRuntimePilotService).GetField("currentSessionRunId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime));
            Assert.Equal(1, control.UnboundStops);
            Assert.Equal(0, control.BoundStops);
        }
        finally { context?.RuntimeService.ClearSessionUnlock(); DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData("lock", false)]
    [InlineData("lock", true)]
    [InlineData("disable", false)]
    [InlineData("disable", true)]
    public async Task TunaRuntimeListenerLifetime_LockDisableWithoutActiveRunPreservesAccounting(string operation, bool completedSummary)
    {
        var root = CreateTempRoot();
        VerifiedRuntimeContext? context = null;
        try
        {
            context = await CreateListenerSupervisorRuntimeContextAsync(root);
            var runtime = context.RuntimeService;
            runtime.UnlockForNextSession(await context.WalletStore.LoadAsync(), "test-only".ToCharArray());
            if (completedSummary)
            {
                var options = RuntimeListenerOptions(runtime);
                Assert.True(options.BeginUsageAttemptIfCurrent!("completed-run", () => true));
                options.UsageSink!.RecordPayment(new NknTunaPaymentTelemetry(0.25m, 0.25m, 1048576, null));
                options.UsageSink.RecordSummary(new NknTunaSessionUsageTelemetry(1048576, "completed", true, 0.25m,
                    PaymentEventCount: 1));
                Assert.True(Assert.Single(context.UsageStore.Load().SessionRecords).CompletedFromSummary);
            }

            var before = JsonSerializer.Serialize(context.UsageStore.Load());
            if (operation == "lock")
                await runtime.LockOrStopForSessionAsync("test_lock", TunaRuntimeUnlockSource.Header);
            else
                runtime.SavePreferences(new TunaRuntimePreferenceState { Enabled = false });

            Assert.False(runtime.HasSessionUnlock);
            Assert.Equal(before, JsonSerializer.Serialize(context.UsageStore.Load()));
            Assert.Equal(operation == "lock" ? "locked" : "off", runtime.RuntimeStatus);
        }
        finally { context?.RuntimeService.ClearSessionUnlock(); DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData("unlock", false)]
    [InlineData("unlock", true)]
    [InlineData("stop", false)]
    [InlineData("stop", true)]
    [InlineData("transport", false)]
    [InlineData("transport", true)]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    public async Task TunaRuntimeListenerLifetime_BlockedOptionsCannotReplaceSuccessor(string retirement, bool requireIdle)
    {
        var root = CreateTempRoot();
        VerifiedRuntimeContext? context = null;
        BlockingWalletLoad? store = null;
        Task<object?>? oldSelection = null;
        using var cancellation = new CancellationTokenSource();
        try
        {
            context = await CreateVerifiedRuntimeContextAsync(root,
                new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", "public-address", "1")),
                wrapWalletStore: inner => store = new BlockingWalletLoad(inner));
            var runtime = context.RuntimeService;
            var wallet = await context.WalletStore.LoadAsync();
            runtime.UnlockForNextSession(wallet, "first".ToCharArray());
            using var wrapper = runtime.CreateRuntimeListenerSupervisorForTests();
            var get = wrapper.GetType().GetMethod("GetOrCreateSupervisor", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.IsType<NknTunaListenerSidecarSupervisor>(get.Invoke(wrapper, [false, CancellationToken.None, 0L]));
            store!.BlockNextLoad = 1;
            oldSelection = Task.Run(() => get.Invoke(wrapper, [requireIdle, cancellation.Token, 0L]));
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (retirement == "unlock") runtime.UnlockForNextSession(wallet, "replacement".ToCharArray());
            if (retirement == "stop") wrapper.Stop("retired");
            if (retirement == "transport") SetPrivateField(runtime, "currentTransportControl", new BoundCapControl());
            if (retirement == "cancel") cancellation.Cancel();
            var successor = Assert.IsType<NknTunaListenerSidecarSupervisor>(get.Invoke(wrapper, [false, CancellationToken.None, 0L]));
            var pending = new TaskCompletionSource<NknTunaListenerSidecarEndpoint>();
            SetPrivateField(successor, "pendingReady", pending);
            store.Release.TrySetResult();
            Assert.Null(await oldSelection.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(successor, wrapper.GetType().GetField("supervisor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(wrapper));
            Assert.False((bool)typeof(NknTunaListenerSidecarSupervisor).GetField("disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(successor)!);
            Assert.False(pending.Task.IsCompleted);
        }
        finally
        {
            store?.Release.TrySetResult();
            if (oldSelection is not null) await oldSelection.WaitAsync(TimeSpan.FromSeconds(5));
            context?.RuntimeService.ClearSessionUnlock();
            DeleteTempRoot(root);
        }
    }

    [Theory]
    [InlineData("retry")]
    [InlineData("replacement")]
    public async Task TunaRuntimeListenerLifetime_EachPhysicalAttemptRetainsItsOwnAccounting(string transition)
    {
        var root = CreateTempRoot();
        VerifiedRuntimeContext? context = null;
        try
        {
            context = await CreateListenerSupervisorRuntimeContextAsync(root);
            var runtime = context.RuntimeService;
            runtime.UnlockForNextSession(await context.WalletStore.LoadAsync(), "test-only".ToCharArray());
            var options = RuntimeListenerOptions(runtime);
            var password = Assert.IsType<char[]>(options.TakeWalletPasswordIfCurrent!(() => true));
            Array.Clear(password);
            using var supervisor = new NknTunaListenerSidecarSupervisor(options);
            // Unstarted Process objects exercise ownership/accounting only; no child is launched.
            using var first = UnstartedListenerOwner();
            using var second = UnstartedListenerOwner();
            var request = CreateListenerAdoptionSnapshot().CurrentRequest;
            SetPrivateField(supervisor, "processOwner", first);
            SetPrivateField(supervisor, "currentRequest", request);
            Assert.True((bool)InvokeListener(supervisor, "BeginUsageAttempt", first, 0L, CancellationToken.None)!);
            if (transition == "retry")
                Assert.True((bool)InvokeListener(supervisor, "TryReportStartFailure", 0L, first, "listener_ready_timeout", "ready_timeout")!);
            Assert.Same(first, InvokeListener(supervisor, "DetachProcessForStop_NoLock", "listener_replaced"));
            var failed = Assert.Single(context.UsageStore.Load().SessionRecords);
            Assert.Equal(first.RunId, failed.SessionRunId);
            Assert.Equal(TunaPaymentTelemetryStatus.AccountingIncomplete, failed.PaymentTelemetryStatus);

            SetPrivateField(supervisor, "processOwner", second);
            SetPrivateField(supervisor, "summaryObserved", false);
            Assert.True((bool)InvokeListener(supervisor, "BeginUsageAttempt", second, 0L, CancellationToken.None)!);
            var before = JsonSerializer.Serialize(context.UsageStore.Load());
            ListenerOutput(supervisor, first, "{\"event\":\"tuna_payment\",\"amountNkn\":9,\"cumulativeSpendNkn\":9}");
            Assert.Equal(before, JsonSerializer.Serialize(context.UsageStore.Load()));
            ListenerOutput(supervisor, second, "{\"event\":\"tuna_payment\",\"amountNkn\":0.25,\"cumulativeSpendNkn\":0.25,\"bytesMoved\":1048576}");
            ListenerOutput(supervisor, second, "{\"event\":\"summary\",\"reason\":\"completed\",\"paymentObserved\":true,\"paymentEventCount\":1,\"cumulativeSpendNkn\":0.25,\"bytesMoved\":1048576}");
            var usage = context.UsageStore.Load();
            Assert.Equal(0.25m, usage.TotalPaidNkn);
            Assert.Equal(2, usage.SessionRecords.Count);
            Assert.Equal(failed, usage.SessionRecords[0]);
            Assert.Equal(second.RunId, usage.LastSessionRecord!.SessionRunId);
            Assert.True(usage.LastSessionRecord.CompletedFromSummary);
        }
        finally { context?.RuntimeService.ClearSessionUnlock(); DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("adoption")]
    public async Task TunaRuntimeListenerLifetime_StaleCapCannotPublishSuccessorStatus(string change)
    {
        var root = CreateTempRoot();
        VerifiedRuntimeContext? context = null;
        try
        {
            context = await CreateListenerSupervisorRuntimeContextAsync(root);
            var runtime = context.RuntimeService;
            var control = new BoundCapControl();
            SetPrivateField(runtime, "currentTransportControl", control);
            runtime.UnlockForNextSession(await context.WalletStore.LoadAsync(), "test-only".ToCharArray());
            var options = RuntimeListenerOptions(runtime);
            var source = CreateListenerAdoptionSnapshot();
            Assert.True(options.BeginUsageAttemptIfCurrent!(source.ProcessRunId, () => true));
            using var supervisor = new NknTunaListenerSidecarSupervisor(options);
            options.StatusChanged!("listener_ready");
            var beforeCap = JsonSerializer.Serialize(context.PreferenceStore.Load());
            Action? deferredCap = null;
            InvokeListener(supervisor, "HandleStdoutLineCore",
                "{\"event\":\"tuna_cap_handoff_requested\",\"capReason\":\"byte_cap_reached\"}",
                new TaskCompletionSource<NknTunaListenerSidecarEndpoint>(), new Stopwatch(), source.CurrentRequest,
                source, (Func<bool>)(() => true), null, (Action<Action>)(action => deferredCap = action));
            Assert.NotNull(deferredCap);
            Assert.Equal(beforeCap, JsonSerializer.Serialize(context.PreferenceStore.Load()));
            var successor = change == "replacement"
                ? source with { ProcessRunId = "successor-run" }
                : source with { OwnershipGeneration = source.OwnershipGeneration + 1 };
            if (change == "replacement")
            {
                options.UsageSink!.RecordIncomplete("replaced");
                Assert.True(options.BeginUsageAttemptIfCurrent!(successor.ProcessRunId, () => true));
            }
            control.CurrentSource = successor;
            options.StatusChanged!("listener_ready");
            var preferences = JsonSerializer.Serialize(context.PreferenceStore.Load());
            var usage = JsonSerializer.Serialize(context.UsageStore.Load());
            var notifications = 0;
            runtime.StateChanged += (_, _) => notifications++;
            deferredCap!();
            if (runtime.ListenerCapStopForTests is { } rejected) await rejected.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, control.BoundStops);
            Assert.Equal(0, control.UnboundStops);
            Assert.Equal(0, notifications);
            Assert.Equal("listener_ready", runtime.RuntimeStatus);
            Assert.Equal(preferences, JsonSerializer.Serialize(context.PreferenceStore.Load()));
            Assert.Equal(usage, JsonSerializer.Serialize(context.UsageStore.Load()));

            options.ListenerCapHandoffRequested!("byte_cap_reached", successor);
            await runtime.ListenerCapStopForTests!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, control.BoundStops);
            Assert.Equal("cap_reached", runtime.RuntimeStatus);
            Assert.Equal(successor.ProcessRunId, context.UsageStore.Load().LastSessionRecord!.SessionRunId);
        }
        finally { context?.RuntimeService.ClearSessionUnlock(); DeleteTempRoot(root); }
    }

    private static NknTunaSidecarProcessOwner UnstartedListenerOwner()
        => (NknTunaSidecarProcessOwner)typeof(NknTunaSidecarProcessOwner)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke(new object?[] { "listener", new Process(), null });

    private static object? InvokeListener(NknTunaListenerSidecarSupervisor supervisor, string method, params object?[] args)
        => typeof(NknTunaListenerSidecarSupervisor).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(supervisor, args);

    private static void ListenerOutput(NknTunaListenerSidecarSupervisor supervisor, NknTunaSidecarProcessOwner owner, string line)
        => InvokeListener(supervisor, "HandleOwnedStdoutLine", owner,
            typeof(NknTunaSidecarProcessOwner).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner),
            line, new TaskCompletionSource<NknTunaListenerSidecarEndpoint>(), new Stopwatch());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TunaRuntimeListenerLifetime_NewUnlockRefreshesOnlyIdleSupervisor(bool pending)
    {
        var root = CreateTempRoot();
        VerifiedRuntimeContext? context = null;
        try
        {
            context = await CreateListenerSupervisorRuntimeContextAsync(root);
            var runtime = context.RuntimeService;
            var wallet = await context.WalletStore.LoadAsync(CancellationToken.None);
            runtime.UnlockForNextSession(wallet, "first".ToCharArray());
            using var wrapper = runtime.CreateRuntimeListenerSupervisorForTests();
            var get = wrapper.GetType().GetMethod("GetOrCreateSupervisor", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var original = Assert.IsType<NknTunaListenerSidecarSupervisor>(get.Invoke(wrapper, [false, CancellationToken.None, 0L]));
            if (pending) SetPrivateField(original, "pendingReady", new TaskCompletionSource<NknTunaListenerSidecarEndpoint>());
            runtime.UnlockForNextSession(wallet, "replacement".ToCharArray());
            var next = get.Invoke(wrapper, [true, CancellationToken.None, 0L]);
            if (pending) Assert.Null(next);
            else Assert.NotSame(original, Assert.IsType<NknTunaListenerSidecarSupervisor>(next));
        }
        finally { context?.RuntimeService.ClearSessionUnlock(); DeleteTempRoot(root); }
    }

    [Theory]
    [InlineData("current")]
    [InlineData("unlock_before_stop")]
    [InlineData("transport_before_stop")]
    [InlineData("run_before_stop")]
    [InlineData("unlock_after_stop")]
    [InlineData("run_after_stop")]
    public async Task TunaRuntimeListenerLifetime_CapCannotStopOrOverwriteSuccessor(string change)
    {
        var root = CreateTempRoot();
        VerifiedRuntimeContext? context = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            context = await CreateListenerSupervisorRuntimeContextAsync(root);
            var runtime = context.RuntimeService;
            var control = new BoundCapControl { Completion = change is "unlock_after_stop" or "run_after_stop" ? completed.Task : Task.CompletedTask };
            SetPrivateField(runtime, "currentTransportControl", control);
            var wallet = await context.WalletStore.LoadAsync(CancellationToken.None);
            runtime.UnlockForNextSession(wallet, "test-only".ToCharArray());
            var options = RuntimeListenerOptions(runtime);
            var password = Assert.IsType<char[]>(options.TakeWalletPasswordIfCurrent!(() => true));
            Array.Clear(password);
            var source = CreateListenerAdoptionSnapshot();
            Assert.True(options.BeginUsageAttemptIfCurrent!(source.ProcessRunId, () => true));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.BeforeListenerCapStopForTests = () => { entered.TrySetResult(); return release.Task; };
            options.ListenerCapHandoffRequested!("byte_cap_reached", source);
            var task = Assert.IsAssignableFrom<Task>(runtime.ListenerCapStopForTests);
            options.ListenerCapHandoffRequested!("byte_cap_reached", source);
            Assert.Same(task, runtime.ListenerCapStopForTests);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (change == "unlock_before_stop") runtime.UnlockForNextSession(wallet, "replacement".ToCharArray());
            if (change == "transport_before_stop") SetPrivateField(runtime, "currentTransportControl", new BoundCapControl());
            if (change == "run_before_stop")
            {
                var replacementPassword = Assert.IsType<char[]>(options.TakeWalletPasswordIfCurrent!(() => true));
                Array.Clear(replacementPassword);
                Assert.True(options.BeginUsageAttemptIfCurrent!("replacement-run", () => true));
            }
            var successorStatus = runtime.RuntimeStatus;
            release.TrySetResult();
            if (change is "unlock_after_stop" or "run_after_stop")
            {
                await control.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (change == "unlock_after_stop") runtime.UnlockForNextSession(wallet, "replacement".ToCharArray());
                else
                {
                    var replacementPassword = Assert.IsType<char[]>(options.TakeWalletPasswordIfCurrent!(() => true));
                    Array.Clear(replacementPassword);
                    Assert.True(options.BeginUsageAttemptIfCurrent!("replacement-run", () => true));
                }
                successorStatus = runtime.RuntimeStatus;
                completed.TrySetResult();
            }
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(change is "current" or "unlock_after_stop" or "run_after_stop" ? 1 : 0, control.BoundStops);
            Assert.Equal(0, control.UnboundStops);
            Assert.Equal(change == "current" ? "cap_reached" : successorStatus, runtime.RuntimeStatus);
            if (control.BoundStops != 0) Assert.Equal(source, control.Source);
            if (change is "run_before_stop" or "run_after_stop")
                Assert.NotNull(typeof(TunaRuntimePilotService).GetField("currentSessionRunId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime));
        }
        finally
        {
            release.TrySetResult();
            completed.TrySetResult();
            if (context?.RuntimeService.ListenerCapStopForTests is { } task) await task.WaitAsync(TimeSpan.FromSeconds(5));
            context?.RuntimeService.ClearSessionUnlock();
            DeleteTempRoot(root);
        }
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("cancel")]
    [InlineData("unlock")]
    [InlineData("transport")]
    public async Task TunaRuntimeListenerLifetime_RetiredPreflightCannotMutateWalletOrUsage(string retirement)
    {
        var root = CreateTempRoot();
        VerifiedRuntimeContext? context = null;
        using var release = new ManualResetEventSlim();
        try
        {
            var sidecar = Path.Combine(root, "not-an-executable.fixture");
            await File.WriteAllTextAsync(sidecar, "fixture only");
            context = await CreateListenerSupervisorRuntimeContextAsync(root, sidecar);
            var runtime = context.RuntimeService;
            var wallet = await context.WalletStore.LoadAsync(CancellationToken.None);
            runtime.UnlockForNextSession(wallet, "test-only".ToCharArray());
            var options = RuntimeListenerOptions(runtime);
            using var supervisor = new NknTunaListenerSidecarSupervisor(options);
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            supervisor.BeforeWalletPasswordForTests = () =>
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            };
            var start = Task.Run(() => supervisor.EnsureStartedAsync(
                new("fixture-peer", NknAccelerationLaneKind.File), cancellation.Token));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (retirement == "stop") supervisor.Stop("test_retired");
                if (retirement == "cancel") cancellation.Cancel();
                if (retirement == "transport") SetPrivateField(runtime, "currentTransportControl", new BoundCapControl());
                if (retirement == "unlock") runtime.UnlockForNextSession(wallet, "replacement".ToCharArray());
                var status = runtime.RuntimeStatus;
                var usage = context.UsageStore.Load();
                release.Set();
                Assert.Null(await start.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(status, runtime.RuntimeStatus);
                Assert.Equal(usage, context.UsageStore.Load());
                Assert.Null(typeof(TunaRuntimePilotService).GetField("listenerRestartUnlockRetentionCts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime));
                Assert.True(runtime.HasSessionUnlock);
                // Stale configuration callbacks cannot overwrite the successor either.
                if (retirement is "unlock" or "transport")
                {
                    options.StatusChanged!("sidecar_missing");
                    Assert.Equal(status, runtime.RuntimeStatus);
                    Assert.Null(options.TakeWalletPasswordIfCurrent!(() => true));
                }
            }
            finally { release.Set(); await start.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        finally { release.Set(); context?.RuntimeService.ClearSessionUnlock(); DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaRuntimeListenerLifetime_CurrentPreflightRetainsPasswordButOnlyAttemptStartsAccounting()
    {
        var root = CreateTempRoot();
        VerifiedRuntimeContext? context = null;
        try
        {
            context = await CreateListenerSupervisorRuntimeContextAsync(root);
            var runtime = context.RuntimeService;
            runtime.UnlockForNextSession(await context.WalletStore.LoadAsync(CancellationToken.None), "fixture-password".ToCharArray());
            var options = RuntimeListenerOptions(runtime);
            var before = context.UsageStore.Load();
            Assert.Null(options.TakeWalletPasswordIfCurrent!(() => false));
            Assert.Equal(before, context.UsageStore.Load());
            var password = Assert.IsType<char[]>(options.TakeWalletPasswordIfCurrent!(() => true));
            Assert.Equal("fixture-password".ToCharArray(), password);
            Array.Clear(password);
            Assert.Equal(before, context.UsageStore.Load());
            Assert.False(options.BeginUsageAttemptIfCurrent!("retired-attempt", () => false));
            Assert.Equal(before, context.UsageStore.Load());
            Assert.True(options.BeginUsageAttemptIfCurrent!("current-attempt", () => true));
            Assert.Equal("listener_starting", runtime.RuntimeStatus);
            Assert.True(runtime.HasSessionUnlock);
            Assert.NotNull(typeof(TunaRuntimePilotService).GetField("currentSessionRunId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime));
        }
        finally { context?.RuntimeService.ClearSessionUnlock(); DeleteTempRoot(root); }
    }

    private static NknTunaListenerSidecarOptions RuntimeListenerOptions(TunaRuntimePilotService runtime)
    {
        var sinkType = typeof(TunaRuntimePilotService).GetNestedType("RuntimeUsageSink", BindingFlags.NonPublic)!;
        var sink = Activator.CreateInstance(sinkType, runtime);
        return Assert.IsType<NknTunaListenerSidecarOptions>(typeof(TunaRuntimePilotService)
            .GetMethod("CreateListenerSidecarOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, [sink, null]));
    }

    private sealed class BoundCapControl : ITransportAccelerationControl, INknListenerCapHandoffControl
    {
        public int BoundStops, UnboundStops;
        public NknTunaListenerAdoptionSnapshot? Source;
        public NknTunaListenerAdoptionSnapshot? CurrentSource;
        public Task Completion = Task.CompletedTask;
        public TaskCompletionSource Committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RequestAccelerationNegotiationAsync(string reason, CancellationToken ct) => Task.CompletedTask;
        public Task StopAccelerationAsync(string reason, CancellationToken ct) { UnboundStops++; return Task.CompletedTask; }
        public async Task<bool> StopListenerForCapAsync(NknTunaListenerAdoptionSnapshot source, string reason,
            Func<bool> isRuntimeCurrent, CancellationToken ct)
        {
            if (!isRuntimeCurrent() || (CurrentSource is not null && CurrentSource != source)) return false;
            Source = source;
            BoundStops++;
            Committed.TrySetResult();
            await Completion;
            return true;
        }
    }

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

    [Theory]
    [InlineData("funded")]
    [InlineData("empty")]
    [InlineData("failed")]
    public async Task DiagnosticsTunaWallet_BalanceCheckHighlight_IsOneTimePerLink(string outcome)
    {
        var root = CreateTempRoot();
        var result = outcome == "failed"
            ? TunaWalletValidationResult.Fail("wrong_password")
            : TunaWalletValidationResult.Ok("wallet.json", "NKN-public-address", outcome == "funded" ? "1" : "0");
        var verifier = new BlockingTunaWalletVerifier(result);
        try
        {
            var walletPath = Path.Combine(root, "wallet.json");
            var store = new JsonTunaWalletLinkStore(() => Path.Combine(root, "state.json"));
            using var vm = CreateViewModel(store, verifier);
            Assert.False(vm.NeedsTunaWalletBalanceCheck);

            await vm.LinkTunaWalletAsync(walletPath);
            Assert.True(vm.NeedsTunaWalletBalanceCheck);
            using (var reopened = CreateViewModel(store, verifier))
            {
                Assert.True(reopened.NeedsTunaWalletBalanceCheck);
            }

            // Opening or canceling the password prompt is not a balance check.
            vm.ValidateTunaWalletCommand.Execute(null);
            await vm.ValidateTunaWalletAsync(null);
            Assert.True(vm.NeedsTunaWalletBalanceCheck);

            var highlightChanges = new List<bool>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(vm.NeedsTunaWalletBalanceCheck))
                {
                    highlightChanges.Add(vm.NeedsTunaWalletBalanceCheck);
                }
            };
            var validation = vm.ValidateTunaWalletAsync("test-password".ToCharArray());
            Assert.True(vm.IsTunaWalletValidating);
            Assert.False(vm.NeedsTunaWalletBalanceCheck);
            Assert.Contains(false, highlightChanges);
            verifier.Release();
            await validation;
            Assert.False(vm.NeedsTunaWalletBalanceCheck);
            using (var reopened = CreateViewModel(store, verifier))
            {
                Assert.False(reopened.NeedsTunaWalletBalanceCheck);
            }

            await vm.ValidateTunaWalletAsync("test-password".ToCharArray());
            Assert.DoesNotContain(true, highlightChanges);

            await vm.LinkTunaWalletAsync(walletPath);
            Assert.True(vm.NeedsTunaWalletBalanceCheck);
            await vm.UnlinkTunaWalletAsync();
            Assert.False(vm.NeedsTunaWalletBalanceCheck);
        }
        finally
        {
            verifier.Release();
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
    public void TunaUsageAccountingState_IncompletePaidSessionPreservesStatusAndObservedPayment()
    {
        var now = DateTimeOffset.Parse("2026-05-05T12:00:00Z");
        var usage = TunaUsageAccountingState.Empty
            .StartNewSession("run-incomplete-paid", "listener", now)
            .AddPayment("run-incomplete-paid", 0.25m, 1_000_000, now)
            .CompleteSession(
                "run-incomplete-paid",
                1_000_000,
                paymentTelemetryObserved: true,
                cumulativeSpendNkn: null,
                paymentTelemetryStatus: TunaPaymentTelemetryStatus.AccountingIncomplete,
                paymentEventCount: 1,
                stopReason: "user_locked",
                capReason: string.Empty,
                fallbackReason: "user_locked",
                completedFromSummary: false,
                now.AddSeconds(1));

        var session = Assert.Single(usage.SessionRecords);
        Assert.Equal(TunaPaymentTelemetryStatus.AccountingIncomplete, session.PaymentTelemetryStatus);
        Assert.False(session.CompletedFromSummary);
        Assert.Equal(now.AddSeconds(1), session.EndedUtc);
        Assert.Equal(0.25m, session.PaidNkn);
        Assert.Equal(0.25m, usage.TotalPaidNkn);
        Assert.Equal(1, session.PaymentEventCount);
        Assert.Equal(1_000_000, session.BytesMoved);
        Assert.True(usage.LastSessionCostUnknown);
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
            await vm.UnlockTunaRuntimeAsync(OpenUnlockPrompt(vm), runtimePassword);

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
            await vm.UnlockTunaRuntimeAsync(OpenUnlockPrompt(vm), wrongPassword);

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
            await vm.UnlockTunaRuntimeAsync(OpenUnlockPrompt(vm), secondWrongPassword);

            Assert.All(secondWrongPassword, c => Assert.Equal('\0', c));
            Assert.False(runtimeService.HasSessionUnlock);
            Assert.Equal("unlock_failed_wrong_password", runtimeService.RuntimeStatus);
            Assert.Equal("Verified, funded", vm.TunaWalletStatus);
            Assert.False(vm.UnlockTunaRuntimeCommand.CanExecute(null));
            Assert.Contains("Try again in", vm.TunaRuntimeUnlockStatus, StringComparison.Ordinal);
            Assert.Equal(3, verifier.PasswordsSeen.Count);

            var blockedPassword = "blocked-pass".ToCharArray();
            await vm.UnlockTunaRuntimeAsync(OpenUnlockPrompt(vm), blockedPassword);

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
                TunaRuntimeUnlockSource.Header, await walletStore.LoadAsync());
            var second = await runtimeService.UnlockForSessionAsync(
                "wrong-pass-2".ToCharArray(),
                TunaRuntimeUnlockSource.Options, expectedWallet: await walletStore.LoadAsync());
            var blocked = await runtimeService.UnlockForSessionAsync(
                "correct-but-blocked".ToCharArray(),
                TunaRuntimeUnlockSource.Header, await walletStore.LoadAsync());

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TunaRuntimeWallet_StartupDoesNotRestoreUnlockValidating(bool enabled)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Fail("unused"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier, enabled);
            context.PreferenceStore.Save(context.PreferenceStore.Load().WithStatus("unlock_validating"));
            var runtime = new TunaRuntimePilotService(context.PreferenceStore, context.UsageStore, context.WalletStore, verifier);
            var state = await runtime.GetUnlockStateAsync();
            Assert.Equal(enabled ? "locked" : "off", state.RuntimeStatus);
            Assert.Equal(state.RuntimeStatus, runtime.Preferences.LastRuntimeStatus);
            Assert.False(state.IsOn);
            Assert.False(runtime.HasSessionUnlock);
            Assert.Empty(verifier.PasswordsSeen);
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaRuntimeWallet_RevokedWrongPasswordDoesNotCountAgainstNextUnlock()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new BlockingTunaWalletVerifier(TunaWalletValidationResult.Fail("wrong_password"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var runtime = context.RuntimeService;
            var wallet = await context.WalletStore.LoadAsync();
            var pending = runtime.UnlockForSessionAsync("stale".ToCharArray(), TunaRuntimeUnlockSource.Options, wallet);
            try
            {
                await verifier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                await runtime.LockOrStopForSessionAsync("user_locked", TunaRuntimeUnlockSource.Header);
            }
            finally { verifier.Release(); }
            Assert.False((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Success);
            var current = await runtime.UnlockForSessionAsync("wrong".ToCharArray(), TunaRuntimeUnlockSource.Header, wallet);
            Assert.False(current.Success);
            Assert.False(current.IsCooldownActive);
            Assert.Equal(2, verifier.PasswordsSeen.Count);
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    public async Task TunaRuntimeWallet_WrongPasswordCountsWhenFailureStatusCannotBeSaved()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Fail("wrong_password"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var prefs = new FailingStopPreferenceStore(context.PreferenceStore) { FailStatus = "unlock_failed_wrong_password" };
            var runtime = new TunaRuntimePilotService(prefs, context.UsageStore, context.WalletStore, verifier);
            var wallet = await context.WalletStore.LoadAsync();
            using var page = CreateViewModel(context.WalletStore, verifier, runtime);
            await page.UnlockTunaRuntimeAsync(wallet, "wrong-1".ToCharArray());
            Assert.Equal(1, prefs.FailedWrites);
            Assert.Equal("Couldn't confirm the wallet unlock. Check access to nLink's wallet settings.", page.CopyFeedbackText);
            Assert.False((await runtime.GetUnlockStateAsync()).IsCooldownActive);

            prefs.FailStatus = null;
            var second = await runtime.UnlockForSessionAsync("wrong-2".ToCharArray(), TunaRuntimeUnlockSource.Header, wallet);
            Assert.False(second.Success);
            Assert.True(second.IsCooldownActive);
            var blocked = await runtime.UnlockForSessionAsync("blocked".ToCharArray(), TunaRuntimeUnlockSource.Options, wallet);
            Assert.False(blocked.Success);
            Assert.True(blocked.IsCooldownActive);
            Assert.Equal(2, verifier.PasswordsSeen.Count);
            Assert.False(runtime.HasSessionUnlock);
        }
        finally { DeleteTempRoot(root); }
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
                await context.WalletStore.LoadAsync());
            await WaitUntilAsync(() => verifier.PasswordsSeen.Count == 1, TimeSpan.FromSeconds(2));

            var secondPassword = "second-pass".ToCharArray();
            var secondResult = await context.RuntimeService.UnlockForSessionAsync(
                secondPassword,
                TunaRuntimeUnlockSource.Options,
                await context.WalletStore.LoadAsync());

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
    public async Task TunaWalletSidecarVerifier_CancellationReapsOwnedChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempRoot();
        var readyPath = Path.Combine(root, "verifier-ready");
        using var cts = new CancellationTokenSource();
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                    "$null = [Console]::ReadLine(); [IO.File]::WriteAllText($env:NLINK_TEST_VERIFIER_READY, 'ready'); Start-Sleep -Seconds 30" },
                Environment = { ["NLINK_TEST_VERIFIER_READY"] = readyPath },
            },
        };

        try
        {
            Assert.True(child.Start());
            try
            {
                var reading = TunaWalletSidecarVerifier.ReadWalletStatusAsync(child, "stub-password".ToCharArray(), cts.Token);
                await WaitUntilAsync(() => File.Exists(readyPath), TimeSpan.FromSeconds(5));
                Assert.False(child.HasExited);

                cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => reading.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(child.HasExited);
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("unlink")]
    [InlineData("relink")]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeUnlockCoordinator_DiscardsPendingValidationAfterWalletChange(string change)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new BlockingTunaWalletVerifier(
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "2.5000"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            using var vm = CreateViewModel(context.WalletStore, verifier, context.RuntimeService);
            var password = "runtime-pass".ToCharArray();
            var pending = context.RuntimeService.UnlockForSessionAsync(password, TunaRuntimeUnlockSource.Header, await context.WalletStore.LoadAsync());

            try
            {
                await verifier.Entered.WaitAsync(TimeSpan.FromSeconds(2));
                switch (change)
                {
                    case "lock":
                        await context.RuntimeService.LockOrStopForSessionAsync("test_lock", TunaRuntimeUnlockSource.Header);
                        break;
                    case "unlink":
                        await vm.UnlinkTunaWalletAsync();
                        break;
                    case "relink":
                        // Even the same file is a different link when the user links it again.
                        await vm.LinkTunaWalletAsync(context.WalletPath);
                        break;
                }
            }
            finally
            {
                verifier.Release();
            }

            var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(result.Success);
            Assert.False(context.RuntimeService.HasSessionUnlock);
            Assert.Equal("locked", context.RuntimeService.RuntimeStatus);
            Assert.All(password, c => Assert.Equal('\0', c));

            var wallet = await context.WalletStore.LoadAsync();
            switch (change)
            {
                case "lock": Assert.Equal("1.2500", wallet.BalanceNkn); break;
                case "unlink": Assert.Equal(TunaWalletLinkStatus.Unlinked, wallet.Status); break;
                case "relink": Assert.Equal(TunaWalletLinkStatus.LinkedUnverified, wallet.Status); break;
            }
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeUnlockCoordinator_LinkChangeDuringValidationSaveDoesNotUnlock(bool relink)
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "2.5000"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            var store = new BlockingWalletSave(context.WalletStore) { BlockClear = !relink };
            var runtime = new TunaRuntimePilotService(context.PreferenceStore, context.UsageStore, store, verifier);
            using var vm = CreateViewModel(store, verifier, runtime);
            var replacementPath = Path.Combine(root, "replacement-wallet.json");
            var password = "runtime-pass".ToCharArray();
            var pending = runtime.UnlockForSessionAsync(password, TunaRuntimeUnlockSource.Header, await store.LoadAsync());
            Task change;
            try
            {
                await store.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                change = relink ? vm.LinkTunaWalletAsync(replacementPath) : vm.UnlinkTunaWalletAsync();
                Assert.False(change.IsCompleted);
            }
            finally
            {
                store.ReleaseSave.TrySetResult();
            }

            // Ensure deletion cannot finish until after the rejected attempt releases its identity.
            try { Assert.False((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Success); }
            finally { store.ReleaseClear.TrySetResult(); }
            await change.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(runtime.HasSessionUnlock);
            Assert.Equal("locked", runtime.RuntimeStatus);
            Assert.False((await runtime.GetUnlockStateAsync()).IsOn);
            Assert.All(password, c => Assert.Equal('\0', c));
            var wallet = await store.LoadAsync();
            Assert.Equal(relink ? TunaWalletLinkStatus.LinkedUnverified : TunaWalletLinkStatus.Unlinked, wallet.Status);
            Assert.Equal(relink ? replacementPath : null, wallet.WalletPath);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeUnlockCoordinator_RelinkClearsPreviouslyRetainedUnlock()
    {
        var root = CreateTempRoot();
        try
        {
            var verifier = new FakeTunaWalletVerifier(
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", "NKN0123456789PUBLICADDRESS", "1.2500"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier);
            using var vm = CreateViewModel(context.WalletStore, verifier, context.RuntimeService);
            var result = await context.RuntimeService.UnlockForSessionAsync(
                "runtime-pass".ToCharArray(), TunaRuntimeUnlockSource.Header, await context.WalletStore.LoadAsync());
            Assert.True(result.Success);
            Assert.True(context.RuntimeService.HasSessionUnlock);

            var replacementPath = Path.Combine(root, "replacement-wallet.json");
            await vm.LinkTunaWalletAsync(replacementPath);

            Assert.False(context.RuntimeService.HasSessionUnlock);
            Assert.Equal("locked", context.RuntimeService.RuntimeStatus);
            var wallet = await context.WalletStore.LoadAsync();
            Assert.Equal(TunaWalletLinkStatus.LinkedUnverified, wallet.Status);
            Assert.Equal(replacementPath, wallet.WalletPath);
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
                TunaRuntimeUnlockSource.Options, expectedWallet: await walletStore.LoadAsync());
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
                TunaRuntimeUnlockSource.Header, await context.WalletStore.LoadAsync());

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
    public async Task TunaRuntimeListenerSupervisor_ForwardsCurrentListenerAdoptionWithoutReplacingPhysicalListener()
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateListenerSupervisorRuntimeContextAsync(root);
            var snapshot = CreateListenerAdoptionSnapshot(
                NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen);
            var currentRequest = snapshot.CurrentRequest;
            var child = new RecordingTunaListenerSidecarSupervisor(snapshot);
            using var supervisor = context.RuntimeService.CreateRuntimeListenerSupervisorForTests(child);
            var successorRequest = currentRequest with
            {
                ExecutionIdentity = "fallback-paired-installed-retry-1",
            };

            var captured = supervisor.CaptureCurrentListenerAdoptionSnapshot();
            var adopted = supervisor.TryAdoptCurrentListener(snapshot, successorRequest);
            var current = supervisor.CaptureCurrentListenerAdoptionSnapshot();

            Assert.Same(snapshot, captured);
            Assert.True(adopted);
            Assert.NotNull(current);
            Assert.Equal(successorRequest, current.CurrentRequest);
            Assert.Equal(snapshot.Endpoint, current.Endpoint);
            Assert.Equal(snapshot.ProcessId, current.ProcessId);
            Assert.Equal(snapshot.ProcessRunId, current.ProcessRunId);
            Assert.Equal(snapshot.ProcessStartedUtc, current.ProcessStartedUtc);
            Assert.Equal(1, child.SuccessfulAdoptionCount);
            Assert.Equal(0, child.EnsureStartedCount);
            Assert.Equal(0, child.StopCount);

            Assert.False(
                supervisor.TryAdoptCurrentListener(
                    snapshot,
                    successorRequest with { ExecutionIdentity = "stale-successor" }));
            Assert.Equal(1, child.SuccessfulAdoptionCount);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationLane")]
    public async Task TunaRuntimeListenerSupervisor_ExactCleanupAndIdleStartNeverReplaceChild(bool retireChild)
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateListenerSupervisorRuntimeContextAsync(root);
            var snapshot = CreateListenerAdoptionSnapshot();
            var child = new RecordingTunaListenerSidecarSupervisor(snapshot);
            using var supervisor = context.RuntimeService.CreateRuntimeListenerSupervisorForTests(child);
            // The injected child's option identity is unknown. Strict preparation
            // must not replace it just to obtain a matching runtime configuration.
            Assert.Null(await supervisor.EnsureStartedIfIdleAsync(snapshot.CurrentRequest, CancellationToken.None));
            Assert.Equal(0, child.DisposeCount);
            Assert.Equal(0, child.EnsureStartedCount);
            if (retireChild) supervisor.Dispose();
            Assert.Equal(!retireChild, supervisor.TryDetachCurrentListener(snapshot, out var cleanup));
            if (!retireChild)
            {
                Assert.NotNull(cleanup);
                Assert.Equal(0, child.StopCount);
                Assert.False(supervisor.TryDetachCurrentListener(snapshot, out _));
                cleanup!("exact_cleanup");
                Assert.Equal(1, child.StopCount);
            }
            else Assert.Null(cleanup);
        }
        finally { DeleteTempRoot(root); }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeListenerSupervisor_NoChildOrDisposedReturnsNoAdoptionAuthority()
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateListenerSupervisorRuntimeContextAsync(root);
            var snapshot = CreateListenerAdoptionSnapshot();
            var request = snapshot.CurrentRequest;
            using var noChild = context.RuntimeService.CreateRuntimeListenerSupervisorForTests();

            Assert.Null(noChild.CaptureCurrentListenerAdoptionSnapshot());
            Assert.False(noChild.TryAdoptCurrentListener(snapshot, request));

            var child = new RecordingTunaListenerSidecarSupervisor(snapshot);
            var disposed = context.RuntimeService.CreateRuntimeListenerSupervisorForTests(child);
            disposed.Dispose();

            Assert.Null(disposed.CaptureCurrentListenerAdoptionSnapshot());
            Assert.False(disposed.TryAdoptCurrentListener(snapshot, request));
            Assert.Equal(1, child.DisposeCount);
            Assert.Equal(0, child.AdoptionAttemptCount);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeListenerSupervisor_ChildReplacementRejectsCapturedSnapshot()
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateListenerSupervisorRuntimeContextAsync(
                root,
                Path.Combine(root, "missing-sidecar.exe"));
            var snapshot = CreateListenerAdoptionSnapshot();
            var request = snapshot.CurrentRequest;
            var child = new RecordingTunaListenerSidecarSupervisor(snapshot);
            using var supervisor = context.RuntimeService.CreateRuntimeListenerSupervisorForTests(child);

            Assert.Same(snapshot, supervisor.CaptureCurrentListenerAdoptionSnapshot());
            Assert.Null(await supervisor.EnsureStartedAsync(request, CancellationToken.None));
            Assert.Equal(1, child.DisposeCount);

            Assert.False(
                supervisor.TryAdoptCurrentListener(
                    snapshot,
                    request with { ExecutionIdentity = "fallback-paired-installed-retry-1" }));
            Assert.Null(supervisor.CaptureCurrentListenerAdoptionSnapshot());
            Assert.Equal(0, child.AdoptionAttemptCount);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimeListenerSupervisor_StopCannotInterleaveWithAdoption()
    {
        var root = CreateTempRoot();
        try
        {
            var context = await CreateListenerSupervisorRuntimeContextAsync(root);
            var snapshot = CreateListenerAdoptionSnapshot();
            var request = snapshot.CurrentRequest;
            var child = new RecordingTunaListenerSidecarSupervisor(snapshot)
            {
                BlockAdoption = true,
            };
            using var supervisor = context.RuntimeService.CreateRuntimeListenerSupervisorForTests(child);
            var successor = request with
            {
                ExecutionIdentity = "fallback-paired-installed-retry-1",
            };

            var adoption = Task.Run(
                () => supervisor.TryAdoptCurrentListener(snapshot, successor));
            Task? stop = null;
            try
            {
                await child.AdoptionEntered.WaitAsync(TimeSpan.FromSeconds(2));
                stop = Task.Run(() => supervisor.Stop("stop_race"));
                await Task.Delay(100);

                Assert.False(stop.IsCompleted);
                Assert.Equal(0, child.StopCount);
            }
            finally
            {
                child.ReleaseAdoption();
            }

            Assert.True(await adoption);
            Assert.NotNull(stop);
            await stop;
            Assert.Equal(1, child.StopCount);
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
                TunaRuntimeUnlockSource.Header, await context.WalletStore.LoadAsync());
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
            var wallet = await context.WalletStore.LoadAsync();
            context.RuntimeService.UnlockForNextSession(wallet, "test-only".ToCharArray());
            var listener = RuntimeListenerOptions(context.RuntimeService);
            Assert.True(listener.BeginUsageAttemptIfCurrent!("unlink-owner", () => true));
            listener.StatusChanged!("active");
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
            var closed = Assert.Single(context.UsageStore.Load().SessionRecords);
            Assert.Equal("unlink-owner", closed.SessionRunId);
            Assert.Equal("wallet_unlinked", closed.StopReason);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Smoke")]
    public async Task TunaRuntimePilot_EnableAndUnlockAfterTransportCreation_OffersListenerWithoutReconnect(bool fileOnly)
    {
        var root = CreateTempRoot();
        var previousTunaEnabled = Environment.GetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_TUNA_ENABLED", null);
            using var localAppData = NknTransportOptions.OverrideLocalAppDataPathForTests(root);
            using var perProcessIdentity = NknTransportOptions.OverrideShouldUsePerProcessLocalIdentityForTests(false);
            var verifier = new FakeTunaWalletVerifier(TunaWalletValidationResult.Ok("wallet", VerifiedFixturePayer, "1"),
                sidecarPath: Path.Combine(root, "nlink-tuna-sidecar.exe"));
            var context = await CreateVerifiedRuntimeContextAsync(root, verifier, enabled: false);
            var runtime = context.RuntimeService;
            using var transport = Assert.IsType<NknSignalingTransport>(runtime.CreateNknTransport());
            var lane = Assert.IsAssignableFrom<INknTunaAccelerationSession>(
                CoreSmokeTestsBase.GetPrivateField(transport, "accelerationLane"));
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, lane.ConfiguredLanes);
            Assert.False(transport.AccelerationCanOfferListenerForTests);

            runtime.SavePreferences(new TunaRuntimePreferenceState
                { Enabled = true, FileLaneEnabled = fileOnly, ScreenLaneEnabled = !fileOnly });
            Assert.False(transport.AccelerationCanOfferListenerForTests);
            var unlock = await runtime.UnlockForSessionAsync("fixture".ToCharArray(), TunaRuntimeUnlockSource.Header,
                await context.WalletStore.LoadAsync());
            Assert.True(unlock.Success);
            Assert.True(transport.AccelerationCanOfferListenerForTests);
            var selectedLane = fileOnly ? NknAccelerationLaneKind.File : NknAccelerationLaneKind.Screen;
            Assert.Equal(selectedLane, lane.ConfiguredLanes);
            Assert.Equal(selectedLane, lane.SupportedLanes);
            Assert.False(transport.IsAccelerationAvailableForTests);

            runtime.SavePreferences(new TunaRuntimePreferenceState
                { Enabled = false, FileLaneEnabled = fileOnly, ScreenLaneEnabled = !fileOnly });
            Assert.False(transport.AccelerationCanOfferListenerForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, lane.ConfiguredLanes);
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
        ITunaRuntimePilotService? runtimePilotService = null,
        Func<DateTimeOffset>? nowProvider = null)
    {
        var previousTransport = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");
        Environment.SetEnvironmentVariable("NLINK_TRANSPORT", null);
        try
        {
            return new DiagnosticsPageViewModel(
                ScreenShareEvidenceLocator.CreateDefault(),
                static () => { },
                TransportRuntimeConfig.Select(),
                nowProvider: nowProvider,
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
        TimeSpan? stopCompletionTimeout = null,
        Func<ITunaWalletLinkStore, ITunaWalletLinkStore>? wrapWalletStore = null,
        Func<ITunaUsageAccountingStore, ITunaUsageAccountingStore>? wrapUsageStore = null)
    {
        var walletPath = Path.Combine(root, "wallet-test-nkn.json");
        await File.WriteAllTextAsync(walletPath, "{}");
        var walletStore = new JsonTunaWalletLinkStore(() => Path.Combine(root, "tuna-wallet-link.json"));
        await walletStore.SaveAsync(TunaWalletLinkState.Linked(walletPath, DateTimeOffset.Parse("2026-05-05T10:00:00Z"))
            .WithValidationResult(
                TunaWalletValidationResult.Ok("wallet-test-nkn.json", VerifiedFixturePayer, "1.2500"),
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
            wrapUsageStore?.Invoke(usageStore) ?? usageStore,
            wrapWalletStore?.Invoke(walletStore) ?? walletStore,
            verifier,
            stopCompletionTimeout: stopCompletionTimeout);
        return new VerifiedRuntimeContext(walletStore, preferenceStore, usageStore, runtimeService, walletPath);
    }

    private static Task<VerifiedRuntimeContext> CreateListenerSupervisorRuntimeContextAsync(
        string root,
        string sidecarPath = "nlink-tuna-sidecar.exe")
        => CreateVerifiedRuntimeContextAsync(
            root,
            new FakeTunaWalletVerifier(
                TunaWalletValidationResult.Ok(
                    "wallet-test-nkn.json",
                    "NKN0123456789PUBLICADDRESS",
                    "1.2500"),
                sidecarPath));

    internal static async Task<(TunaRuntimePilotService Runtime, TunaWalletLinkState Wallet)> CreateFundedFakePeerRuntimeAsync(string root)
    {
        var context = await CreateListenerSupervisorRuntimeContextAsync(root, Path.Combine(root, "fake-sidecar.exe"));
        return (context.RuntimeService, await context.WalletStore.LoadAsync());
    }

    private static NknTunaListenerAdoptionSnapshot CreateListenerAdoptionSnapshot(
        NknAccelerationLaneKind lanes = NknAccelerationLaneKind.File)
    {
        var request = new NknTunaListenerStartRequest(
            "remote-peer",
            lanes,
            NknTunaListenerLifetimePolicy.CoordinatorScopedInitialActivation,
            "listener-start-2");
        return new NknTunaListenerAdoptionSnapshot(
            request,
            new NknTunaListenerSidecarEndpoint(
                "127.0.0.1:30303",
                "nlink-tuna-sidecar.listener-address"),
            ProcessId: 21084,
            ProcessRunId: "listener-process-run-2",
            ProcessStartedUtc: DateTimeOffset.Parse("2026-08-08T12:51:00Z"));
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

    private sealed class RecordingTunaListenerSidecarSupervisor : INknTunaListenerSidecarSupervisor
    {
        private readonly object gate = new();
        private NknTunaListenerAdoptionSnapshot? current;
        private bool disposed;
        private int ensureStartedCount;
        private int adoptionAttemptCount;
        private int successfulAdoptionCount;
        private int stopCount;
        private int disposeCount;
        private readonly TaskCompletionSource adoptionEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource adoptionRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingTunaListenerSidecarSupervisor(
            NknTunaListenerAdoptionSnapshot current)
        {
            this.current = current;
        }

        public bool CanOfferListener => !disposed && current is not null;

        public int EnsureStartedCount => Volatile.Read(ref ensureStartedCount);

        public int AdoptionAttemptCount => Volatile.Read(ref adoptionAttemptCount);

        public int SuccessfulAdoptionCount => Volatile.Read(ref successfulAdoptionCount);

        public int StopCount => Volatile.Read(ref stopCount);

        public int DisposeCount => Volatile.Read(ref disposeCount);

        public bool BlockAdoption { get; init; }

        public Task AdoptionEntered => adoptionEntered.Task;

        public event EventHandler<AccelerationStateChangedEventArgs>? StateChanged;

        public Task<NknTunaListenerSidecarEndpoint?> EnsureStartedAsync(
            NknTunaListenerStartRequest request,
            CancellationToken ct)
        {
            Interlocked.Increment(ref ensureStartedCount);
            lock (gate)
            {
                return Task.FromResult(disposed ? null : current?.Endpoint);
            }
        }

        public NknTunaListenerAdoptionSnapshot? CaptureCurrentListenerAdoptionSnapshot()
        {
            lock (gate)
            {
                return disposed ? null : current;
            }
        }

        public bool TryAdoptCurrentListener(
            NknTunaListenerAdoptionSnapshot snapshot,
            NknTunaListenerStartRequest successorRequest)
        {
            Interlocked.Increment(ref adoptionAttemptCount);
            if (BlockAdoption)
            {
                adoptionEntered.TrySetResult();
                adoptionRelease.Task.GetAwaiter().GetResult();
            }

            lock (gate)
            {
                if (disposed ||
                    current != snapshot ||
                    snapshot.CurrentRequest.LifetimePolicy !=
                        NknTunaListenerLifetimePolicy.CoordinatorScopedInitialActivation ||
                    successorRequest.LifetimePolicy !=
                        NknTunaListenerLifetimePolicy.CoordinatorScopedInitialActivation ||
                    !string.Equals(
                        snapshot.CurrentRequest.ExpectedRemotePeer,
                        successorRequest.ExpectedRemotePeer,
                        StringComparison.Ordinal) ||
                    snapshot.CurrentRequest.Lanes != successorRequest.Lanes)
                {
                    return false;
                }

                current = snapshot with { CurrentRequest = successorRequest };
                Interlocked.Increment(ref successfulAdoptionCount);
                return true;
            }
        }

        public void Stop(string reason)
            => Interlocked.Increment(ref stopCount);

        public bool TryDetachCurrentListener(NknTunaListenerAdoptionSnapshot snapshot, out Action<string>? stop)
        {
            lock (gate)
            {
                stop = null;
                if (disposed || current != snapshot) return false;
                current = null;
                stop = Stop;
                return true;
            }
        }

        public void ReleaseAdoption()
            => adoptionRelease.TrySetResult();

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                current = null;
                Interlocked.Increment(ref disposeCount);
            }
        }
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

    private sealed class BlockingWalletLoad(ITunaWalletLinkStore inner, bool snapshotBeforeWait = false) : ITunaWalletLinkStore
    {
        public int BlockNextLoad;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TunaWalletLinkState> LoadAsync(CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref BlockNextLoad, 0) != 0)
            {
                var snapshot = snapshotBeforeWait ? await inner.LoadAsync(ct) : null;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
                if (snapshot is not null) return snapshot;
            }
            return await inner.LoadAsync(ct);
        }
        public Task<bool> SaveAsync(TunaWalletLinkState state, CancellationToken ct = default, TunaWalletLinkState? expectedValidation = null,
            string? expectedFundedPayer = null) => inner.SaveAsync(state, ct, expectedValidation, expectedFundedPayer);
        public Task<bool> TryClearAsync(TunaWalletLinkState expected, CancellationToken ct = default) => inner.TryClearAsync(expected, ct);
    }

    private sealed class BlockingWalletSave(ITunaWalletLinkStore inner) : ITunaWalletLinkStore
    {
        public bool BlockClear;
        public Func<IDisposable>? DuringSave;
        public TaskCompletionSource ReleaseClear { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TunaWalletLinkState> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);

        public async Task<bool> TryClearAsync(TunaWalletLinkState expected, CancellationToken ct = default)
        {
            if (BlockClear) await ReleaseClear.Task.WaitAsync(ct);
            return await inner.TryClearAsync(expected, ct);
        }

        public async Task<bool> SaveAsync(TunaWalletLinkState state, CancellationToken ct = default, TunaWalletLinkState? expectedValidation = null,
            string? expectedFundedPayer = null)
        {
            SaveEntered.TrySetResult();
            await ReleaseSave.Task.WaitAsync(ct);
            using var scope = DuringSave?.Invoke();
            return await inner.SaveAsync(state, ct, expectedValidation, expectedFundedPayer);
        }
    }

    private sealed class BlockingTunaWalletVerifier : ITunaWalletVerifier
    {
        private readonly TunaWalletValidationResult result;
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingTunaWalletVerifier(TunaWalletValidationResult result)
        {
            this.result = result;
        }

        public List<char[]> PasswordsSeen { get; } = new();

        public Task Entered => entered.Task;

        public TunaWalletVerifierAvailability GetAvailability()
            => new(true, "available", "nlink-tuna-sidecar.exe");

        public async Task<TunaWalletValidationResult> ValidateAsync(string walletPath, char[] password, CancellationToken ct)
        {
            PasswordsSeen.Add(password.ToArray());
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return result;
        }

        public void Release()
            => release.TrySetResult();
    }
}
