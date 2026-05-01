using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using NLink.App;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Metrics;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.Retry;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Core.Logging;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Gui")]
public sealed class SessionIdentityAndVerificationTests : SessionHeaderAndBannerTestBase
{
    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_RecoveredLocalIdentity_HidesFallbackAddress_ButKeepsRecoveryNoticeVisible()
    {
        PersistenceDiagnostics.ClearForTests();
        try
        {
            PersistenceDiagnostics.Record(domain: "nkn_identity_store", operation: "automatic_identity_recovery", severity: PersistenceDiagnosticSeverity.Warning, outcome: PersistenceDiagnosticOutcome.Partial, reason: "default_identity_recreated", userWarning: "Local protected identity storage was unreadable. nLink created a new local identity. Previous helper address and invites are no longer valid.");
            var transportConfig = CreateNknTestConfig();
            using var helperRuntime = new SessionRuntime(() => new ScriptedSignalingTransport());
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, transportConfig, helperRuntime, bootstrapHelperIdentityResolver: _ => Task.FromResult<PeerAddress?>(new PeerAddress("nlink-helper.bootstrap.recovered")), qrCodeService: new NoOpQrCodeService());
            var pending = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helper, "ResolveBootstrapHelperIdentityAsync", CancellationToken.None));
            await pending;
            Assert.Contains("created a new local identity", helper.HelperIdentityBootstrapHintText, StringComparison.Ordinal);
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_RecoveredLocalIdentity_KeepsRecoveryNoticeVisible_AfterLaterPersistenceWarning()
    {
        PersistenceDiagnostics.ClearForTests();
        try
        {
            PersistenceDiagnostics.Record(domain: "nkn_identity_store", operation: "automatic_identity_recovery", severity: PersistenceDiagnosticSeverity.Warning, outcome: PersistenceDiagnosticOutcome.Partial, reason: "default_identity_recreated", userWarning: "Local protected identity storage was unreadable. nLink created a new local identity. Previous helper address and invites are no longer valid.");
            var transportConfig = CreateNknTestConfig();
            using var helperRuntime = new SessionRuntime(() => new ScriptedSignalingTransport());
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, transportConfig, helperRuntime, bootstrapHelperIdentityResolver: _ => Task.FromResult<PeerAddress?>(new PeerAddress("nlink-helper.bootstrap.recovered")), qrCodeService: new NoOpQrCodeService());
            var pending = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helper, "ResolveBootstrapHelperIdentityAsync", CancellationToken.None));
            await pending;
            PersistenceDiagnostics.Record(domain: "nkn_secret_store", operation: "read_seed", severity: PersistenceDiagnosticSeverity.Warning, outcome: PersistenceDiagnosticOutcome.Fallback, reason: "CryptographicException", userWarning: "Protected seed storage could not be read.");
            Assert.Contains("created a new local identity", helper.HelperIdentityBootstrapHintText, StringComparison.Ordinal);
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_RuntimeHelperAddress_OverridesSeparatelyResolvedBootstrapAddress()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var transportConfig = CreateNknTestConfig();
            var fallbackAddress = "nlink-helper.bootstrap.fallback.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var authoritativeAddress = "helper.listener.authoritative.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var separatelyResolvedAddress = new PeerAddress("helper.bootstrap.separate.cccccccccccccccccccccccccccccccc");
            var options = NknTransportOptions.Load();
            var fakeClient = new FakeNknClient(fallbackAddress, authoritativeAddress);
            var identity = new NknIdentity("helper-id", fallbackAddress);
            using var transport = new NknSignalingTransport(fakeClient, options, identity);
            using var helperRuntime = new SessionRuntime(() => new ScriptedSignalingTransport());
            SetPrivateField(helperRuntime, "role", SessionRuntimeRole.Helper);
            SetPrivateField(helperRuntime, "transport", transport);
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, transportConfig, helperRuntime, bootstrapHelperIdentityResolver: _ => Task.FromResult<PeerAddress?>(separatelyResolvedAddress), qrCodeService: new NoOpQrCodeService());
            var pending = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helper, "ResolveBootstrapHelperIdentityAsync", CancellationToken.None));
            await pending;
            Assert.Equal(string.Empty, helper.HelperIdentityBootstrapText);
            Assert.False(helper.HasHelperIdentityBootstrapVerificationCode);
            await transport.HostByAddressAsync(CancellationToken.None);
            InvokePrivateMethod(helper, "CacheBootstrapHelperIdentityFromRuntimeIfAvailable");
            Assert.StartsWith("nlinkh1.", helper.HelperIdentityBootstrapText, StringComparison.Ordinal);
            Assert.NotEqual(separatelyResolvedAddress.Value, helper.HelperIdentityBootstrapText);
            Assert.True(HelperBootstrapQrPayload.TryParse(helper.HelperIdentityBootstrapText, out var parsedBootstrap));
            Assert.NotNull(parsedBootstrap);
            Assert.Equal(authoritativeAddress, parsedBootstrap!.HelperAddress.Value);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeInvite_WithVerifiedHelperIdentity_GeneratesHelperBoundInvite()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new DevLocalTransport());
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, openDiagnosticsAction: static () =>
        {
        }, clipboardService: new FakeClipboardService(), shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));
    #if DEBUG
            await Task.CompletedTask;
    #else
        Assert.True(string.IsNullOrWhiteSpace(helpee.ShareInvite));
        Assert.Equal("Invite setup requires a verified helper address.", helpee.ShareInviteStatusText);
        var helperIdentity = new PeerAddress("nlink-helper.boundpublic.1234");
        helpee.SetVerifiedInviteHelperIdentity(helperIdentity);
        await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(3));
        var validator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        Assert.Equal(helpee.ShareInvite, helpee.ShareInviteRawToken);
        var validation = validator.Validate(helpee.ShareInvite, DateTimeOffset.UtcNow);
        Assert.True(validation.IsSuccess, validation.Message);
        Assert.NotNull(validation.Invite);
        Assert.Equal(helperIdentity, validation.Invite!.BoundHelperAddress);
        Assert.True((validation.Invite.Payload.Capabilities & InviteCapabilities.FileTransfer) == InviteCapabilities.FileTransfer, "Generated helpee invite should request file transfer.");
        Assert.Equal("Invite ready", helpee.ShareInviteStatusText);
        Assert.True(helpee.ShowWaitingInviteActions);
    #endif
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_InviteShareCodeInput_AllowsIntendedBoundHelperJoin()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        var transportConfig = CreateNknTestConfig();
        using var helpeeTransport = new ScriptedSignalingTransport(onHostByAddressAsync: _ => Task.CompletedTask, localPeerAddress: "helpee.bound.host");
        string? observedInviteToken = null;
        ValidatedInviteV1? observedInvite = null;
        using var helperTransport = new ScriptedSignalingTransport(onJoinByInviteAsync: (inviteToken, invite, _) =>
        {
            observedInviteToken = inviteToken;
            observedInvite = invite;
            return Task.CompletedTask;
        }, localPeerAddress: "helper.bound.host");
        using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
        using var helperRuntime = new SessionRuntime(() => helperTransport);
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime, connectFailureCooldown: TimeSpan.Zero);
        await WaitUntilAsync(() => helperRuntime.State == SessionRuntimeState.Waiting && string.Equals(helper.ConnectionState, "Waiting", StringComparison.Ordinal), TimeSpan.FromSeconds(3));
        var helperHostedAddress = GetHostedAddressOrThrow(helperRuntime);
        helpee.SetVerifiedInviteHelperIdentity(helperHostedAddress);
        await WaitUntilAsync(() =>
        {
            var validator = InviteTokenServiceFactory.CreateInviteTokenValidator();
            var validation = validator.Validate(helpee.ShareInviteRawToken, DateTimeOffset.UtcNow);
            return validation.IsSuccess && validation.Invite?.BoundHelperAddress?.Value == helperHostedAddress.Value;
        }, TimeSpan.FromSeconds(3));
        var inviteCode = await WaitForShareInviteAsync(helpee);
        Assert.Equal(helpee.ShareInviteRawToken, inviteCode);
        helper.CodeInput = inviteCode;
        await WaitUntilAsync(() => helper.ConnectCommand.CanExecute(null), TimeSpan.FromSeconds(2));
        var connectTask = helper.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => observedInvite is not null && string.Equals(observedInviteToken, inviteCode, StringComparison.Ordinal), TimeSpan.FromSeconds(2));
        await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Equal(inviteCode, observedInviteToken);
        Assert.NotNull(observedInvite);
        Assert.Equal(helperHostedAddress, observedInvite!.BoundHelperAddress);
        Assert.Equal(GetHostedAddressOrThrow(helpeeRuntime), observedInvite.TargetAddress);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task ApprovalVerificationCode_IsExposedOnHelpeeAndHelper_FromSharedHelperIdentity()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-verify-code-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-verify-code-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        _ = await WaitForShareInviteAsync(helpee);
        var connectTask = helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpee.IsIncomingRequestView && helpee.ShowIncomingRequestPanel && helpee.HasIncomingHelperVerificationCode && helper.HasHelperVerificationCode && helpee.ShowSessionVerificationCode && helper.ShowSessionVerificationCode, TimeSpan.FromSeconds(3));
        Assert.Equal(helpee.IncomingHelperVerificationCode, helper.HelperVerificationCode);
        Assert.Equal(helpee.SessionVerificationEmojiSequence, helper.SessionVerificationEmojiSequence);
        Assert.Equal(helpee.SessionVerificationFallbackCode, helper.SessionVerificationFallbackCode);
        Assert.True(helpee.HasIncomingTechnicalDetails);
        Assert.False(string.IsNullOrWhiteSpace(helpee.IncomingTechnicalHelperIdentityText));
        Assert.False(string.IsNullOrWhiteSpace(helpee.IncomingTechnicalSessionIdText));
        Assert.True(helper.HasHelperTechnicalDetails);
        Assert.False(string.IsNullOrWhiteSpace(helper.HelperTechnicalIdentityText));
        Assert.False(string.IsNullOrWhiteSpace(helper.HelperTechnicalSessionIdText));
        Assert.True(helper.ShowHelperVerificationCode);
        await helpee.DeclineCommand.ExecuteAsync(null);
        await connectTask;
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task ApprovalVerificationCode_HidesAfterApprovalCompletes()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-verify-hide-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-verify-hide-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        _ = await WaitForShareInviteAsync(helpee);
        var connectTask = helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);

        await WaitUntilAsync(
            () => helpee.ShowSessionVerificationCode && helper.ShowSessionVerificationCode,
            TimeSpan.FromSeconds(3));

        helpee.AllowCommand.Execute(null);
        await connectTask;

        await WaitUntilAsync(
            () => string.Equals(helpee.ConnectionState, "Connected", StringComparison.Ordinal) &&
                  string.Equals(helper.ConnectionState, "Connected", StringComparison.Ordinal),
            TimeSpan.FromSeconds(3));

        Assert.False(helpee.ShowSessionVerificationCode);
        Assert.False(helper.ShowSessionVerificationCode);
        Assert.False(helpee.IsIncomingRequestView);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperFirstPillVerificationCode_IsHidden_WhileConnecting_WhenBootstrapIdentityIsNotAuthoritative()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        var transportConfig = CreateNknTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        var bootstrapHelperIdentity = new PeerAddress("nlink-bootstrap.shared.identity");
        SetPrivateField(helperRuntime, "transport", new ScriptedSignalingTransport(localPeerAddress: "nlink-runtime.local.identity"));
        SetPrivateField(helper, "bootstrapHelperIdentity", bootstrapHelperIdentity);
        SetPrivateField(helper, "bootstrapHelperIdentityIsAuthoritative", false);
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connecting);
        Assert.Equal(string.Empty, helper.FirstPillVerificationCodeText);
        Assert.False(helper.ShowFirstPillVerificationCode);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperFirstPillVerificationCode_Shows_WhenActiveListenerAddressIsAuthoritative()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        var transportConfig = CreateNknTestConfig();
        var listenerAddress = new PeerAddress("nlink-runtime.authoritative.listener");
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime, qrCodeService: new NoOpQrCodeService());

        SetPrivateField(helperRuntime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Waiting);
        SetPrivateField(helperRuntime, "transport", new ScriptedSignalingTransport(localPeerAddress: listenerAddress.Value));
        SetPrivateField(helper, "bootstrapHelperIdentity", new PeerAddress("nlink-bootstrap.persisted.identity"));
        SetPrivateField(helper, "bootstrapHelperIdentityIsAuthoritative", false);
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Waiting);
        InvokePrivateMethod(helper, "NotifyHelperIdentityBootstrapChanged");

        var expected = HelperVerificationCodeFormatter.Format(listenerAddress);
        Assert.Equal(expected, helper.FirstPillVerificationCodeText);
        Assert.True(helper.HasHelperIdentityBootstrapVerificationCode);
        Assert.True(HelperBootstrapQrPayload.TryParse(helper.HelperIdentityBootstrapText, out var parsedBootstrap));
        Assert.NotNull(parsedBootstrap);
        Assert.Equal(listenerAddress, parsedBootstrap!.HelperAddress);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperRegenerateIdentityCommand_RegeneratesImmediately_AndUsesInjectedRegenerator()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        var transportConfig = CreateNknTestConfig();
        var regeneratedAddress = new PeerAddress("nlink-runtime.regenerated.identity");
        var regenerateCalls = 0;
        using var helperRuntime = new SessionRuntime(() => new ScriptedSignalingTransport(localPeerAddress: regeneratedAddress.Value));
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime, bootstrapHelperIdentityResolver: null, regenerateHelperIdentityAsync: _ =>
        {
            regenerateCalls++;
            return Task.FromResult<PeerAddress?>(regeneratedAddress);
        }, qrCodeService: new NoOpQrCodeService());

        SetPrivateField(helperRuntime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(helperRuntime, "state", SessionRuntimeState.Waiting);
        SetPrivateField(helperRuntime, "transport", new ScriptedSignalingTransport(localPeerAddress: "nlink-runtime.old.identity"));
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Waiting);
        InvokePrivateMethod(helper, "NotifyHelperIdentityBootstrapChanged");

        Assert.True(helper.CanRegenerateHelperIdentity);
        await helper.RegenerateHelperIdentityCommand.ExecuteAsync(null);
        Assert.Equal(1, regenerateCalls);
        Assert.Equal("Regenerate helper address", helper.RegenerateHelperIdentityButtonText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void HelperRegenerateIdentity_PrivacyExplanation_IsNotRenderedPersistently()
    {
        var viewPath = FindFileUpwards(Path.Combine("src", "nLink.App", "Views", "HelperPageView.axaml"));
        Assert.False(string.IsNullOrWhiteSpace(viewPath), "Expected HelperPageView.axaml to exist.");
        var xaml = File.ReadAllText(viewPath!);

        Assert.Contains("Helper.RegenerateHelperIdentity", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"↻\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Helper.HelperIdentityBootstrapHint", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Helper.HelperIdentityBootstrapPrivacyHint", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("This helper address stays saved on this PC", xaml, StringComparison.Ordinal);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperFirstPillVerificationCode_RemainsHidden_WhenLateBootstrapResolutionReturnsNonAuthoritativeIdentity()
    {
        using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
        var transportConfig = CreateNknTestConfig();
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport());
        var bootstrapResolution = new TaskCompletionSource<PeerAddress?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime, bootstrapHelperIdentityResolver: _ => bootstrapResolution.Task);
        var bootstrapHelperIdentity = new PeerAddress("nlink-bootstrap.shared.identity");
        var lateResolvedIdentity = new PeerAddress("nlink-late.resolved.identity");
        SetPrivateField(helper, "bootstrapHelperIdentity", bootstrapHelperIdentity);
        SetPrivateField(helper, "bootstrapHelperIdentityIsAuthoritative", false);
        SetPrivateField(helper, "effectivePhase", SessionUiPhase.Connecting);
        var pending = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(helper, "ResolveBootstrapHelperIdentityAsync", CancellationToken.None));
        SetPrivateField(helper, "bootstrapHelperIdentityResolutionTask", pending);
        bootstrapResolution.SetResult(lateResolvedIdentity);
        await pending;
        Assert.Equal(string.Empty, helper.FirstPillVerificationCodeText);
        Assert.False(helper.ShowFirstPillVerificationCode);
    }

}
