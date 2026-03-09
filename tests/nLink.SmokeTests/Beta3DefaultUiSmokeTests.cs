using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core.Metrics;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class Beta3DefaultUiSmokeTests : IClassFixture<Beta3DefaultUiFixture>
{
    private readonly Beta3DefaultUiFixture fixture;

    public Beta3DefaultUiSmokeTests(Beta3DefaultUiFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task MainWindow_HelperDiagnosticsBack_ReturnsToHelper()
    {
        var services = CreateServicesForMainWindow();
        using var vm = new MainWindowViewModel(services);

        Assert.IsType<HomePageViewModel>(vm.CurrentPage);
        InvokePrivate(vm, "ShowHelperPage");
        Assert.IsType<HelperPageViewModel>(vm.CurrentPage);

        InvokePrivate(vm, "ShowDiagnosticsPage");
        var diagnostics = Assert.IsType<DiagnosticsPageViewModel>(vm.CurrentPage);

        diagnostics.BackCommand.Execute(null);
        Assert.IsType<HelperPageViewModel>(vm.CurrentPage);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task MainWindow_HelpeeDiagnosticsBack_ReturnsToHelpee()
    {
        var services = CreateServicesForMainWindow();
        using var vm = new MainWindowViewModel(services);

        Assert.IsType<HomePageViewModel>(vm.CurrentPage);
        InvokePrivate(vm, "ShowHelpeePage");
        Assert.IsType<HelpeePageViewModel>(vm.CurrentPage);

        InvokePrivate(vm, "ShowDiagnosticsPage");
        var diagnostics = Assert.IsType<DiagnosticsPageViewModel>(vm.CurrentPage);

        diagnostics.BackCommand.Execute(null);
        Assert.IsType<HelpeePageViewModel>(vm.CurrentPage);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelperPage_DefaultShell_ContainsChatControls_AndHidesInlineDisconnect()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var context = await CreateConnectedSessionContextAsync();
            var view = new HelperPageView { DataContext = context.Helper };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            window.Show();

            try
            {
                await FlushUiAsync();

                Assert.True(NLink.App.Configuration.FeatureFlags.EnableSessionHeader);

                Assert.NotNull(FindFirstControlByAutomationId(window, "Chat.Messages"));
                Assert.NotNull(FindFirstControlByAutomationId(window, "Chat.Input"));
                Assert.NotNull(FindFirstControlByAutomationId(window, "Chat.Send"));

                var disconnect = FindFirstControlByAutomationId(window, "Chat.Disconnect");
                Assert.NotNull(disconnect);
                Assert.False(disconnect!.IsVisible);
                Assert.NotNull(FindFirstDescendant<SessionHeaderView>(window));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_DefaultShell_ContainsChatControls_AndHidesInlineDisconnect()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var context = await CreateConnectedSessionContextAsync();
            var view = new HelpeePageView { DataContext = context.Helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            window.Show();

            try
            {
                await FlushUiAsync();

                Assert.True(NLink.App.Configuration.FeatureFlags.EnableSessionHeader);

                Assert.NotNull(FindFirstControlByAutomationId(window, "Chat.Messages"));
                Assert.NotNull(FindFirstControlByAutomationId(window, "Chat.Input"));
                Assert.NotNull(FindFirstControlByAutomationId(window, "Chat.Send"));

                var disconnect = FindFirstControlByAutomationId(window, "Chat.Disconnect");
                Assert.NotNull(disconnect);
                Assert.False(disconnect!.IsVisible);
                Assert.NotNull(FindFirstDescendant<SessionHeaderView>(window));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_DefaultShell_PrioritizesQr_AndOmitsRawInviteFallback()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

            var view = new HelpeePageView { DataContext = helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

                var inviteQr = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helpee.InviteQr") as Image is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helpee invite qr");

                Assert.NotNull(inviteQr);
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helpee.ShareInvite"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helpee.CopyInvite"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helpee.RefreshInvite"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helpee.InviteDetails"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helpee.InviteText"));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_PublicInviteFlowBlockedWithoutVerifiedHelperIdentity_HidesInviteActions()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

            var view = new HelpeePageView { DataContext = helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

#if DEBUG
                Assert.False(string.IsNullOrWhiteSpace(helpee.ShareInvite));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helpee.ShareInvite"));
#else
                Assert.True(string.IsNullOrWhiteSpace(helpee.ShareInvite));
                Assert.False(helpee.ShowShareInviteQr);
                Assert.True(helpee.ShowShareInviteQrPlaceholder);
                Assert.False(helpee.ShowWaitingInviteActions);
                Assert.Equal("Invite setup requires a verified helper address.", helpee.ShareInviteStatusText);

                var inviteStatus = Assert.IsType<TextBlock>(FindFirstVisibleControlByAutomationId(window, "Helpee.InviteStatus"));
                Assert.Equal("Invite setup requires a verified helper address.", inviteStatus.Text);
#endif
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_PublicInviteFlow_UnlocksInviteActionsAfterEnteringHelperIdentity()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

            var view = new HelpeePageView { DataContext = helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

#if DEBUG
                Assert.False(string.IsNullOrWhiteSpace(helpee.ShareInvite));
#else
                var helperIdentity = new PeerAddress("nlink-helper.boundpublic.ui.1234");
                var helperInput = Assert.IsType<TextBox>(FindFirstVisibleControlByAutomationId(window, "Helpee.HelperIdentityInput"));
                Assert.Null(FindFirstVisibleControlByAutomationId(window, "Helpee.UseHelperIdentity"));

                helperInput.Text = helperIdentity.Value;
                await FlushUiAsync();

                await WaitUntilAsync(
                    () => !string.IsNullOrWhiteSpace(helpee.ShareInvite) && helpee.ShowWaitingInviteActions,
                    TimeSpan.FromSeconds(3));
                await FlushUiAsync();

                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helpee.ShareInvite"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helpee.CopyInvite"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helpee.RefreshInvite"));

                Assert.Equal(helperIdentity.Value, helperInput.Text);
                Assert.Null(FindFirstVisibleControlByAutomationId(window, "Helpee.HelperIdentityHint"));
                Assert.Null(FindFirstVisibleControlByAutomationId(window, "Helpee.BoundHelperIdentity"));
                var boundVerificationCode = Assert.IsType<TextBlock>(FindFirstVisibleControlByAutomationId(window, "Helpee.FirstPillVerificationCode"));
                Assert.Equal(helpee.VerifiedInviteHelperVerificationCode, boundVerificationCode.Text);
                Assert.Null(FindFirstVisibleControlByAutomationId(window, "Helpee.BoundHelperTechnicalIdentity"));
                Assert.Null(FindFirstVisibleControlByAutomationId(window, "Helpee.InviteCodeText"));
                Assert.Null(FindFirstVisibleControlByAutomationId(window, "Helpee.InviteTechnicalToken"));
                Assert.Equal(helpee.ShareInvite, helpee.ShareInviteRawToken);

                var validator = InviteTokenServiceFactory.CreateInviteTokenValidator();
                var validation = validator.Validate(helpee.ShareInvite, DateTimeOffset.UtcNow);
                Assert.True(validation.IsSuccess, validation.Message);
                Assert.Equal(helperIdentity, validation.Invite!.BoundHelperAddress);
#endif
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_PublicInviteFlow_CopyInvite_CopiesInviteShareCode()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            var clipboard = new TestClipboardService();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: clipboard,
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

            var helperIdentity = new PeerAddress("nlink-helper.boundpublic.copyinvite.1234");
            var helperToken = HelperIdentityTokenCodec.Encode(helperIdentity);
            helpee.SetVerifiedInviteHelperIdentity(helperIdentity, normalizedInputOverride: helperToken);

            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(3));
            await helpee.CopyInviteCommand.ExecuteAsync(null);

            Assert.Equal(helpee.ShareInvite, clipboard.LastText);
            Assert.Equal(helpee.ShareInviteRawToken, clipboard.LastText);

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_PublicInviteFlow_ShareInvite_UsesInviteShareCode()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            var shareService = new TestInviteShareService();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null),
                inviteShareService: shareService);

            var helperIdentity = new PeerAddress("nlink-helper.boundpublic.shareinvite.1234");
            var helperToken = HelperIdentityTokenCodec.Encode(helperIdentity);
            helpee.SetVerifiedInviteHelperIdentity(helperIdentity, normalizedInputOverride: helperToken);

            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(3));
            await helpee.ShareInviteCommand.ExecuteAsync(null);

            Assert.Equal(helpee.ShareInvite, shareService.LastInviteText);
            Assert.Equal(helpee.ShareInviteRawToken, shareService.LastInviteText);

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelperPage_PublicInviteFlow_ShowsHelperIdentityBootstrapPanel()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
            var transportConfig = CreateNknUiTestConfig();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helper = new HelperPageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null),
                bootstrapHelperIdentityResolver: _ => Task.FromResult<PeerAddress?>(new PeerAddress("nlink-helper.bootstrap.actual.1234567890")));

            var view = new HelperPageView { DataContext = helper };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

#if DEBUG
                Assert.Null(FindFirstControlByAutomationId(window, "Helper.CopyHelperIdentity"));
#else
                Assert.True(helper.ShowHelperIdentityBootstrapPanel);
                Assert.False(string.IsNullOrWhiteSpace(helper.HelperIdentityBootstrapText));
                var shareButton = Assert.IsType<Button>(FindFirstVisibleControlByAutomationId(window, "Helper.ShareHelperIdentity"));
                var copyButton = Assert.IsType<Button>(FindFirstVisibleControlByAutomationId(window, "Helper.CopyHelperIdentity"));
                var verificationCode = Assert.IsType<TextBlock>(FindFirstVisibleControlByAutomationId(window, "Helper.FirstPillVerificationCode"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helper.IdentityText"));
                Assert.Equal(helper.HelperIdentityBootstrapVerificationCode, verificationCode.Text);
                Assert.Null(FindFirstVisibleControlByAutomationId(window, "SessionHeader.VerificationCode"));
                Assert.Equal("Share helper address", shareButton.Content?.ToString());
                Assert.Equal("Copy helper address", copyButton.Content?.ToString());
#endif
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelperPage_PublicInviteFlow_CopyHelperCode_CopiesTokenToClipboard()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
            var transportConfig = CreateNknUiTestConfig();
            var clipboard = new TestClipboardService();
            var expectedHelperIdentity = new PeerAddress("nlink-helper.bootstrap.actual.1234567890");
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helper = new HelperPageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: clipboard,
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null),
                bootstrapHelperIdentityResolver: _ => Task.FromResult<PeerAddress?>(expectedHelperIdentity));

            var view = new HelperPageView { DataContext = helper };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

#if DEBUG
                Assert.Null(FindFirstControlByAutomationId(window, "Helper.CopyHelperIdentity"));
#else
                Assert.True(helper.ShowHelperIdentityBootstrapPanel);
                await helper.CopyHelperIdentityCommand.ExecuteAsync(null);
                Assert.Equal(expectedHelperIdentity.Value, clipboard.LastText);
#endif
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelperPage_PublicInviteFlow_ShareHelperAddress_UsesShareService()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
            var transportConfig = CreateNknUiTestConfig();
            var shareService = new TestInviteShareService();
            var expectedHelperIdentity = new PeerAddress("nlink-helper.bootstrap.actual.1234567890");
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helper = new HelperPageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null),
                bootstrapHelperIdentityResolver: _ => Task.FromResult<PeerAddress?>(expectedHelperIdentity),
                inviteShareService: shareService);

            var view = new HelperPageView { DataContext = helper };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

#if DEBUG
                Assert.Null(FindFirstControlByAutomationId(window, "Helper.ShareHelperIdentity"));
#else
                Assert.True(helper.ShowHelperIdentityBootstrapPanel);
                var shareButton = Assert.IsType<Button>(FindFirstVisibleControlByAutomationId(window, "Helper.ShareHelperIdentity"));
                Assert.Equal("Share helper address", shareButton.Content?.ToString());
                await helper.ShareHelperIdentityCommand.ExecuteAsync(null);
                Assert.Equal(expectedHelperIdentity.Value, shareService.LastInviteText);
#endif
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_WhenScreenShareNotApproved_ShowsDisabledShareScreenButton()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var context = await CreateConnectedSessionContextAsync(
                helpee => helpee.AllowIncomingScreenShareCapability = false);
            var view = new HelpeePageView { DataContext = context.Helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            window.Show();

            try
            {
                await FlushUiAsync();

                var shareButton = Assert.IsType<Button>(FindFirstControlByAutomationId(window, "SessionHeader.ShareScreen"));
                Assert.True(shareButton.IsVisible);
                Assert.False(shareButton.IsEnabled);
                Assert.Equal("Share screen", Assert.IsType<string>(shareButton.Content));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeApprovalPanel_ShowsVerificationCode_AndHidesTechnicalIdentityBehindExpander()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            var network = new FakeSessionTransportNetwork();
            using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-approval-ui-" + Guid.NewGuid().ToString("N")));
            using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-approval-ui-" + Guid.NewGuid().ToString("N")));
            using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);

            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(3));
            await helperRuntime.StartHelperAsync(
                new NLink.Core.SessionConnect.PeerAddress(helpeeRuntime.CurrentLocalPeerAddress!.Value.Value),
                CancellationToken.None);
            await WaitUntilAsync(
                () => helpee.IsIncomingRequestView &&
                      helpee.ShowIncomingRequestPanel &&
                      helpee.HasIncomingHelperVerificationCode,
                TimeSpan.FromSeconds(3));

            var view = new HelpeePageView { DataContext = helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            window.Show();

            try
            {
                await FlushUiAsync();

                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helpee.IncomingApprovalTitle"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helpee.IncomingApprovalExplanation"));
                var verificationCode = Assert.IsType<TextBlock>(FindFirstVisibleControlByAutomationId(window, "SessionHeader.VerificationCode"));
                Assert.Equal(helpee.IncomingHelperVerificationCode, verificationCode.Text);
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helpee.RequestedAccessTitle"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helpee.Status"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helpee.RequestedCapabilities"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helpee.ApprovedCapabilities"));
                Assert.Null(FindFirstVisibleControlByAutomationId(window, "Helpee.IncomingVerificationCode"));
                Assert.Null(FindFirstVisibleControlByAutomationId(window, "Helpee.IncomingVerificationHint"));

                var chatCapability = FindFirstControlByAutomationId(window, "Helpee.AllowCapability.Chat") as CheckBox;
                Assert.NotNull(chatCapability);
                Assert.Equal("Send messages", chatCapability!.Content?.ToString());

                var screenShareCapability = FindFirstControlByAutomationId(window, "Helpee.AllowCapability.ScreenShare") as CheckBox;
                Assert.NotNull(screenShareCapability);
                Assert.Equal("View your screen", screenShareCapability!.Content?.ToString());

                var allowButton = FindFirstVisibleControlByAutomationId(window, "Helpee.Allow") as Button;
                var declineButton = FindFirstVisibleControlByAutomationId(window, "Helpee.Decline") as Button;
                Assert.NotNull(allowButton);
                Assert.NotNull(declineButton);
                Assert.True(
                    Math.Abs(allowButton!.Bounds.Y - declineButton!.Bounds.Y) < 2,
                    $"Expected approval actions on the same row, got Y={allowButton.Bounds.Y:N1} and {declineButton.Bounds.Y:N1}.");

                Assert.Null(FindFirstControlByAutomationId(window, "Helpee.IncomingHelperIdentity"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helpee.IncomingSessionId"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helpee.IncomingTechnicalDetails"));
                Assert.DoesNotContain(
                    window.GetVisualDescendants().OfType<TextBlock>(),
                    textBlock => textBlock.IsVisible &&
                                 string.Equals(textBlock.Text, "Helper verification code", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeApprovalPanel_NoCapabilitiesSelected_DisablesAllowButton()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            var network = new FakeSessionTransportNetwork();
            using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-approval-disable-" + Guid.NewGuid().ToString("N")));
            using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-approval-disable-" + Guid.NewGuid().ToString("N")));
            using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);

            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(3));
            await helperRuntime.StartHelperAsync(
                new NLink.Core.SessionConnect.PeerAddress(helpeeRuntime.CurrentLocalPeerAddress!.Value.Value),
                CancellationToken.None);
            await WaitUntilAsync(
                () => helpee.IsIncomingRequestView && helpee.ShowIncomingRequestPanel,
                TimeSpan.FromSeconds(3));

            var view = new HelpeePageView { DataContext = helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            window.Show();

            try
            {
                await FlushUiAsync();

                var allowButton = Assert.IsType<Button>(FindFirstVisibleControlByAutomationId(window, "Helpee.Allow"));
                Assert.True(allowButton.IsEnabled);

                helpee.AllowIncomingChatCapability = false;
                helpee.AllowIncomingScreenShareCapability = false;
                helpee.AllowIncomingRemoteControlCapability = false;
                helpee.AllowIncomingFileTransferCapability = false;
                helpee.AllowIncomingClipboardCapability = false;

                await FlushUiAsync();

                Assert.False(helpee.CanAllowIncomingRequestAction);
                Assert.False(allowButton.IsEnabled);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_WaitingView_MakesQrProminent_AndKeepsRefreshSecondary()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

            var view = new HelpeePageView { DataContext = helpee };
            var window = new Window { Width = 860, Height = 980, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

                var inviteQr = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helpee.InviteQr") as Image is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helpee invite qr");

                var copyInvite = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helpee.CopyInvite") as Button is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helpee copy invite button");

                var shareInvite = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helpee.ShareInvite") as Button is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helpee share invite button");

                var refreshInvite = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helpee.RefreshInvite") as Button is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helpee refresh invite button");

                Assert.True(inviteQr.Bounds.Width >= 200, $"Expected invite QR width >= 200, got {inviteQr.Bounds.Width:N1}.");
                Assert.True(inviteQr.Bounds.Height >= 200, $"Expected invite QR height >= 200, got {inviteQr.Bounds.Height:N1}.");
                Assert.True(copyInvite.Bounds.Width >= 160, $"Expected copy invite button width >= 160, got {copyInvite.Bounds.Width:N1}.");
                Assert.True(shareInvite.Bounds.Width >= 160, $"Expected share invite button width >= 160, got {shareInvite.Bounds.Width:N1}.");
                Assert.True(refreshInvite.Bounds.Height < shareInvite.Bounds.Height, $"Expected refresh invite button height < share invite height, got {refreshInvite.Bounds.Height:N1} vs {shareInvite.Bounds.Height:N1}.");

                Assert.True(
                    Math.Abs(copyInvite.Bounds.Y - shareInvite.Bounds.Y) < 2,
                    $"Expected copy/share invite buttons on the same row, got Y={copyInvite.Bounds.Y:N1} and {shareInvite.Bounds.Y:N1}.");

                var inviteReadyText = window.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .FirstOrDefault(textBlock =>
                        textBlock.IsVisible &&
                        string.Equals(textBlock.Text, "Invite ready", StringComparison.Ordinal));
                Assert.Null(inviteReadyText);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_RefreshInvite_KeepsExistingQrVisible_WhileReplacementRenders()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            var blockingQrService = new BlockingQrCodeService();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null),
                qrCodeService: blockingQrService);

            var view = new HelpeePageView { DataContext = helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

                _ = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helpee.InviteQr") as Image is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "initial helpee invite qr");

                await WaitUntilAsync(
                    () => helpee.ShowShareInviteQr && !helpee.ShowShareInviteQrPlaceholder,
                    TimeSpan.FromSeconds(2));

                var initialInvite = helpee.ShareInvite;
                Assert.False(string.IsNullOrWhiteSpace(initialInvite));

                blockingQrService.BlockNextCreate();
                helpee.RefreshInviteCommand.Execute(null);

                await WaitUntilAsync(
                    () => !string.Equals(initialInvite, helpee.ShareInvite, StringComparison.Ordinal),
                    TimeSpan.FromSeconds(2));
                await FlushUiAsync();

                Assert.True(helpee.ShowShareInviteQr);
                Assert.False(helpee.ShowShareInviteQrPlaceholder);

                blockingQrService.ReleaseBlockedCreate();

                await WaitUntilAsync(() => blockingQrService.CompletedCreateCount >= 2, TimeSpan.FromSeconds(2));
                await WaitUntilAsync(
                    () => helpee.ShowShareInviteQr && !helpee.ShowShareInviteQrPlaceholder,
                    TimeSpan.FromSeconds(2));
                await FlushUiAsync();
            }
            finally
            {
                blockingQrService.ReleaseBlockedCreate();
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_DoesNotPrepareInviteUntilHostReady()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var delayedTransport = new DelayedHostReadyTransport("beta3-delayed-host-ready");
            using var runtime = new SessionRuntime(() => delayedTransport);
            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                NLink.App.Configuration.TransportRuntimeConfig.Select(),
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

            var view = new HelpeePageView { DataContext = helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

                Assert.Null(runtime.CurrentLocalPeerAddress);
                Assert.Null(runtime.CurrentLocalPeerAddress);
                Assert.Null(runtime.CurrentInvitePeerAddress);
                Assert.False(helpee.ShowWaitingInviteActions);
                Assert.True(string.IsNullOrWhiteSpace(helpee.ShareInvite));
                Assert.Equal("Preparing invite…", helpee.ShareInviteStatusText);

                delayedTransport.ReleaseHostReady();

                await WaitUntilAsync(
                    () => string.Equals(runtime.CurrentInvitePeerAddress?.Value, delayedTransport.LocalPeerAddress, StringComparison.Ordinal),
                    TimeSpan.FromSeconds(2));
                await WaitUntilAsync(
                    () => !string.IsNullOrWhiteSpace(helpee.ShareInvite),
                    TimeSpan.FromSeconds(2));
                await WaitUntilAsync(
                    () => helpee.ShowWaitingInviteActions &&
                          helpee.ShowShareInviteQr &&
                          !helpee.ShowShareInviteQrPlaceholder,
                    TimeSpan.FromSeconds(2));

                Assert.Equal("Invite ready", helpee.ShareInviteStatusText);
                Assert.Equal(delayedTransport.LocalPeerAddress, runtime.CurrentLocalPeerAddress?.Value);
                Assert.Equal(delayedTransport.LocalPeerAddress, runtime.CurrentInvitePeerAddress?.Value);
            }
            finally
            {
                delayedTransport.ReleaseHostReady();
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_ResponsiveResize_WideNarrowWide_KeepsControlsAvailable()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var shell = new SessionShellView
            {
                MainContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Responsive.Main",
                    MinWidth = 620,
                },
                ChatContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Responsive.Chat",
                },
                DataContext = new ConnectedChatShellContext(),
            };
            var window = new Window { Width = 1400, Height = 760, Content = shell };
            window.Show();

            try
            {
                await FlushUiAsync();
                shell.ShowScreenShareAction = true;
                shell.ScreenShareCommand?.Execute(null);
                await FlushUiAsync();

                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Responsive.Main"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Responsive.Chat"));

                window.Width = 760;
                await FlushUiAsync();
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Responsive.Main"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Responsive.Chat"));

                window.Width = 1400;
                await FlushUiAsync();
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Responsive.Main"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Responsive.Chat"));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelperPage_DefaultShell_EndSessionButton_StartsDisabled()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helper = new HelperPageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

            var view = new HelperPageView { DataContext = helper };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

                var header = FindFirstDescendant<SessionHeaderView>(window);
                Assert.NotNull(header);
                var endButton = header.GetVisualDescendants().OfType<Button>().FirstOrDefault(b =>
                    string.Equals(b.Content?.ToString(), "End session", StringComparison.Ordinal));

                Assert.NotNull(endButton);
                Assert.False(helper.CanEndSession);
                Assert.False(endButton!.IsEnabled);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelperWaitingForApproval_ShowsVerificationCodePanel()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            var network = new FakeSessionTransportNetwork();
            using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-helper-verify-ui-" + Guid.NewGuid().ToString("N")));
            using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-verify-ui-" + Guid.NewGuid().ToString("N")));
            using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
            using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(3));
            _ = helperRuntime.StartHelperAsync(
                new NLink.Core.SessionConnect.PeerAddress(helpeeRuntime.CurrentLocalPeerAddress!.Value.Value),
                CancellationToken.None);

            await WaitUntilAsync(
                () => helpee.IsIncomingRequestView &&
                      helper.ShowHelperVerificationCode &&
                      !string.IsNullOrWhiteSpace(helper.HelperVerificationCode),
                TimeSpan.FromSeconds(3));

            var view = new HelperPageView { DataContext = helper };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            window.Show();

            try
            {
                await FlushUiAsync();

                var verificationCode = FindFirstVisibleControlByAutomationId(window, "SessionHeader.VerificationCode") as TextBlock;
                Assert.NotNull(verificationCode);
                Assert.Equal(helper.HelperVerificationCode, verificationCode!.Text);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelperConnectedSession_HidesVerificationCode()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            var network = new FakeSessionTransportNetwork();
            using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-header-verify-ui-" + Guid.NewGuid().ToString("N")));
            using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-header-verify-ui-" + Guid.NewGuid().ToString("N")));
            using var helpee = new HelpeePageViewModel(cancelAction: static () => { }, transportConfig, helpeeRuntime);
            using var helper = new HelperPageViewModel(cancelAction: static () => { }, transportConfig, helperRuntime);

            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(3));
            var connectTask = helperRuntime.StartHelperAsync(
                new NLink.Core.SessionConnect.PeerAddress(helpeeRuntime.CurrentLocalPeerAddress!.Value.Value),
                CancellationToken.None);

            await WaitUntilAsync(() => helpee.IsIncomingRequestView, TimeSpan.FromSeconds(3));
            helpee.AllowCommand.Execute(null);
            await connectTask;

            await WaitUntilAsync(
                () => helper.ConnectionState == "Connected",
                TimeSpan.FromSeconds(3));

            var view = new HelperPageView { DataContext = helper };
            var window = new Window { Width = 1280, Height = 860, Content = view };
            window.Show();

            try
            {
                await FlushUiAsync();

                Assert.Null(FindFirstVisibleControlByAutomationId(window, "SessionHeader.VerificationCode"));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelperPage_WaitingView_ShowsCenteredInstallLink_WithoutRecentTargets()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helper = new HelperPageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

            var view = new HelperPageView { DataContext = helper };
            var window = new Window { Width = 820, Height = 900, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

                var scanQrButton = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helper.ScanQr") as Button is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helper scan qr action");

                var pasteButton = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helper.PasteFromClipboard") as Button is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helper paste action");

                var installLink = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helper.CopyInstallLink") as Button is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helper install link");

                var codeInput = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helper.CodeInput") as TextBox is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helper invite input");

                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helper.Connect"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helper.ConnectHint"));
                Assert.Equal("Paste invite", pasteButton.Content?.ToString());
                Assert.Equal("Scan QR", scanQrButton.Content?.ToString());
                Assert.Null(FindFirstControlByAutomationId(window, "Helper.SecondaryOptions"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helper.UseNftp"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helper.RecentTarget"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helper.ClearRecentTargets"));

                var initialInputWidth = codeInput.Bounds.Width;
                var initialInputCenter = codeInput.TranslatePoint(
                    new Point(codeInput.Bounds.Width / 2, codeInput.Bounds.Height / 2),
                    window);
                helper.CodeInput = new string('x', 240);
                await FlushUiAsync();

                Assert.True(
                    Math.Abs(codeInput.Bounds.Width - initialInputWidth) < 1,
                    $"Expected helper invite input width to remain stable after pasting a long invite, got {initialInputWidth:N1} then {codeInput.Bounds.Width:N1}.");

                var inputCenter = codeInput.TranslatePoint(
                    new Point(codeInput.Bounds.Width / 2, codeInput.Bounds.Height / 2),
                    window);
                var pasteCenter = pasteButton.TranslatePoint(
                    new Point(pasteButton.Bounds.Width / 2, pasteButton.Bounds.Height / 2),
                    window);
                var scanCenter = scanQrButton.TranslatePoint(
                    new Point(scanQrButton.Bounds.Width / 2, scanQrButton.Bounds.Height / 2),
                    window);
                var installCenter = installLink.TranslatePoint(
                    new Point(installLink.Bounds.Width / 2, installLink.Bounds.Height / 2),
                    window);

                Assert.Equal("Copy install link", installLink.Content?.ToString());
                Assert.True(
                    Math.Abs(pasteButton.Bounds.Width - scanQrButton.Bounds.Width) < 1,
                    $"Expected equal helper secondary action widths, got {pasteButton.Bounds.Width:N1} and {scanQrButton.Bounds.Width:N1}.");
                Assert.True(
                    Math.Abs(pasteButton.Bounds.Y - scanQrButton.Bounds.Y) < 2,
                    $"Expected helper secondary actions on the same row, got Y={pasteButton.Bounds.Y:N1} and {scanQrButton.Bounds.Y:N1}.");
                Assert.True(inputCenter.HasValue, "Expected helper invite input to translate into window coordinates.");
                Assert.True(pasteCenter.HasValue, "Expected helper paste action to translate into window coordinates.");
                Assert.True(scanCenter.HasValue, "Expected helper scan action to translate into window coordinates.");
                Assert.True(installCenter.HasValue, "Expected install link to translate into window coordinates.");
                Assert.True(
                    Math.Abs(inputCenter!.Value.X - initialInputCenter!.Value.X) < 1,
                    $"Expected helper invite input center to remain stable after paste, got {initialInputCenter.Value.X:N1} then {inputCenter.Value.X:N1}.");
                Assert.True(
                    Math.Abs(inputCenter!.Value.X - ((pasteCenter!.Value.X + scanCenter!.Value.X) / 2)) < 12,
                    $"Expected centered helper action row, got input center X={inputCenter.Value.X:N1}, paste X={pasteCenter.Value.X:N1}, scan X={scanCenter.Value.X:N1}.");
                Assert.True(
                    Math.Abs(inputCenter!.Value.X - installCenter!.Value.X) < 12,
                    $"Expected centered install link, got input center X={inputCenter.Value.X:N1} and install link center X={installCenter.Value.X:N1}.");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelperPage_CopyInstallFeedback_ShowsBelowLink_WithoutMovingLayout()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helper = new HelperPageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

            var view = new HelperPageView { DataContext = helper };
            var window = new Window { Width = 820, Height = 900, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

                var installLink = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helper.CopyInstallLink") as Button is { IsVisible: true } control &&
                          control.Bounds.Width > 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helper install link");

                var feedback = await WaitForLayoutConditionAsync(
                    window,
                    () => FindFirstControlByAutomationId(window, "Helper.CopyInstallFeedback") as TextBlock is { IsVisible: true } control &&
                          control.Bounds.Width >= 0 &&
                          control.Bounds.Height > 0
                        ? control
                        : null,
                    TimeSpan.FromSeconds(2),
                    "helper copy install feedback");

                var installLinkY = installLink.Bounds.Y;
                await helper.CopyInstallMessageCommand.ExecuteAsync(null);
                await FlushUiAsync();

                Assert.Equal(installLinkY, installLink.Bounds.Y);
                Assert.Equal("Copied. Paste it in your chat.", feedback.Text);
                Assert.True(
                    feedback.Bounds.Y > installLink.Bounds.Y + installLink.Bounds.Height,
                    $"Expected helper copy feedback below install link, got button bottom={installLink.Bounds.Y + installLink.Bounds.Height:N1} and feedback Y={feedback.Bounds.Y:N1}.");
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_WhenConnected_ShowsVisibleChatImmediately()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var context = await CreateConnectedSessionContextAsync();
            var view = new HelpeePageView { DataContext = context.Helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            window.Show();

            try
            {
                await FlushUiAsync();

                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Chat.Messages"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Chat.Input"));
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Chat.Send"));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenConnectedArrivesBeforeConnectedPanel_ShowsChatImmediately()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var context = new MutableConnectedChatShellContext();
            var shell = new SessionShellView
            {
                ChatContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                },
                DataContext = context,
            };

            var window = new Window { Width = 1080, Height = 760, Content = shell };
            window.Show();

            try
            {
                await FlushUiAsync();

                context.HeaderStatusText = "Connected";
                await FlushUiAsync();
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Shell.Chat"));

                context.ShowConnectedPanel = true;
                await FlushUiAsync();
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Shell.Chat"));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_WaitingView_DoesNotRenderEmptyChatPane()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helpee = new HelpeePageViewModel(
                cancelAction: static () => { },
                transportConfig,
                runtime,
                openDiagnosticsAction: static () => { },
                clipboardService: new TestClipboardService(),
                shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

            var view = new HelpeePageView { DataContext = helpee };
            var window = new Window { Width = 1080, Height = 760, Content = view };
            try
            {
                window.Show();
                await FlushUiAsync();

                Assert.NotNull(FindFirstControlByAutomationId(window, "Helpee.CopyInvite"));
                Assert.Null(FindFirstVisibleControlByAutomationId(window, "Chat.Messages"));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_ScreenShareScaffold_ToggleControlsPlaceholderVisibility()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var shell = new SessionShellView
                {
                    ShowScreenShareAction = true,
                    DataContext = new ConnectedShellContext(),
                };

                Assert.True(shell.EffectiveShowScreenShareAction);
                Assert.False(shell.ShowScreenSharePane);

                shell.ScreenShareCommand?.Execute(null);
                await FlushUiAsync();
                Assert.True(shell.ShowScreenSharePane);

                shell.ScreenShareCommand?.Execute(null);
                await FlushUiAsync();
                Assert.False(shell.ShowScreenSharePane);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenScreenSharePaneVisible_HidesContentPlaceholder()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var shell = new SessionShellView
                {
                    ShowScreenShareAction = true,
                    DataContext = new ConnectedShellContext(),
                };

                var window = new Window { Width = 1080, Height = 760, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();

                    var placeholder = window.GetVisualDescendants()
                        .OfType<TextBlock>()
                        .FirstOrDefault(x => string.Equals(x.Text, "Content", StringComparison.Ordinal));
                    Assert.NotNull(placeholder);

                    shell.ScreenShareCommand?.Execute(null);
                    await FlushUiAsync();

                    Assert.True(shell.ShowScreenSharePane);
                    Assert.False(placeholder!.IsVisible);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenMainContentExists_PlaceholderStaysHidden_BeforeAndAfterScreenShareStops()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var context = new MutableConnectedScreenShareShellContext
                {
                    HeaderStatusText = "Connected",
                    ShowConnectedPanel = true,
                    CanShowScreenShareAction = true,
                };

                var shell = new SessionShellView
                {
                    Width = 1080,
                    ShowScreenShareAction = true,
                    MainContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                    },
                    ChatContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                    },
                    DataContext = context,
                };

                var window = new Window { Width = 1080, Height = 760, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();

                    var placeholder = window.GetVisualDescendants()
                        .OfType<TextBlock>()
                        .FirstOrDefault(x => string.Equals(x.Text, "Content", StringComparison.Ordinal));
                    Assert.NotNull(placeholder);
                    Assert.False(placeholder!.IsVisible);

                    shell.ScreenShareCommand?.Execute(null);
                    await WaitUntilAsync(() => shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));
                    await FlushUiAsync();
                    Assert.False(placeholder.IsVisible);

                    shell.ScreenShareCommand?.Execute(null);
                    await WaitUntilAsync(() => !shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));
                    await FlushUiAsync();
                    Assert.False(placeholder.IsVisible);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenConnected_HidesMainPaneUntilScreenShareIsToggled()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var shell = new SessionShellView
                {
                    Width = 760,
                    ShowScreenShareAction = true,
                    MainContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                    },
                    ChatContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                    },
                    DataContext = new ConnectedChatShellContext(),
                };

                var window = new Window { Width = 760, Height = 760, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();

                    Assert.True(shell.EffectiveShowScreenShareAction);
                    Assert.False(shell.ShowScreenSharePane);
                    Assert.False(shell.ShowResponsiveNarrowLayout);
                    Assert.True(shell.ShowResponsiveNarrowChatOnlyLayout);
                    Assert.Null(FindFirstControlByAutomationId(window, "Shell.Main"));
                    Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Shell.Chat"));

                    shell.ScreenShareCommand?.Execute(null);
                    await FlushUiAsync();

                    Assert.True(shell.ShowScreenSharePane);
                    Assert.True(shell.ShowResponsiveNarrowLayout);
                    Assert.False(shell.ShowResponsiveNarrowChatOnlyLayout);
                    Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Shell.Main"));
                    Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Shell.Chat"));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenHelperReceivesFrame_AutoShowsViewer_AndClearsOnStop()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                EnsureAppServices();
                var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
                using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
                using var helper = new HelperPageViewModel(
                    cancelAction: static () => { },
                    transportConfig,
                    runtime,
                    openDiagnosticsAction: static () => { },
                    clipboardService: new TestClipboardService(),
                    shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

                var shell = new SessionShellView
                {
                    Width = 1080,
                    ShowScreenShareAction = true,
                    MainContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                    },
                    ChatContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                    },
                    DataContext = helper,
                };

                var window = new Window { Width = 1080, Height = 760, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();
                    Assert.False(shell.ShowScreenSharePane);

                    InvokePrivate(
                        helper,
                        "OnScreenShareFrameCompleted",
                        null,
                        new NLink.Core.ScreenShare.ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", CreateTinyImageBytes()));

                    await WaitUntilAsync(() => helper.ScreenShareViewer.CurrentFrame is not null, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => helper.ShowRemoteScreenShareFrame, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));

                    InvokePrivate(helper, "ClearRemoteScreenShareFrame");

                    await WaitUntilAsync(() => helper.ScreenShareViewer.CurrentFrame is null, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => !helper.ShowRemoteScreenShareFrame, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => !shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenHelpeePreviewStarts_AutoShowsViewer_AndLeavesChatOnlyLayout()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previousScaffold = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var context = new MutableConnectedScreenShareShellContext
                {
                    HeaderStatusText = "Connected",
                    ShowConnectedPanel = true,
                };

                var shell = new SessionShellView
                {
                    Width = 1080,
                    ShowScreenShareAction = true,
                    MainContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                    },
                    ChatContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                    },
                    DataContext = context,
                };

                var window = new Window { Width = 1080, Height = 760, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();
                    Assert.False(shell.ShowScreenSharePane);

                    context.ShowScreenSharePreviewFrame = true;

                    await WaitUntilAsync(() => context.ShowScreenSharePreviewFrame, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));

                    Assert.True(shell.ShowFixedShellLayout || shell.ShowResponsiveWideLayout || shell.ShowResponsiveNarrowLayout);
                    Assert.False(shell.ShowFixedChatOnlyLayout || shell.ShowResponsiveWideChatOnlyLayout || shell.ShowResponsiveNarrowChatOnlyLayout);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previousScaffold);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenWindowIsLarge_ScreenShareViewerExceedsExpectedBounds()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previousScaffold = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                using var previewFrame = CreateBitmap(1280, 720);
                var context = new MutableConnectedScreenShareShellContext
                {
                    HeaderStatusText = "Connected",
                    ShowConnectedPanel = true,
                    ShowScreenSharePreviewFrame = true,
                    ScreenSharePreviewFrame = previewFrame,
                };

                var shell = new SessionShellView
                {
                    Width = 1400,
                    ShowScreenShareAction = true,
                    MainContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                    },
                    ChatContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                    },
                    DataContext = context,
                };

                var window = new Window { Width = 1400, Height = 900, Content = shell };
                window.Show();

                try
                {
                    await WaitUntilAsync(() => shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));

                    var viewer = await WaitForLayoutConditionAsync(
                        window,
                        () => FindVisibleScreenShareViewer(window),
                        TimeSpan.FromSeconds(2),
                        "screen share viewer layout");

                    Assert.True(
                        viewer.Bounds.Width > 800,
                        $"Expected large-window screenshare viewer width > 800, got {viewer.Bounds.Width:N1}.");
                    Assert.True(
                        viewer.Bounds.Height > 500,
                        $"Expected large-window screenshare viewer height > 500, got {viewer.Bounds.Height:N1}.");
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previousScaffold);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_MainPaneWidthPolicy_OnlyStretchesWhenScreenShareIsVisible()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previousScaffold = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                using var previewFrame = CreateBitmap(1280, 720);
                var context = new MutableConnectedScreenShareShellContext
                {
                    HeaderStatusText = "Connected",
                    ShowConnectedPanel = true,
                };

                var shell = new SessionShellView
                {
                    Width = 1600,
                    Height = 900,
                    ShowScreenShareAction = true,
                    MainContent = new Border
                    {
                        Width = 900,
                        Height = 400,
                    },
                    DataContext = context,
                };

                var window = new Window { Width = 1600, Height = 900, Content = shell };
                window.Show();

                try
                {
                    await WaitUntilAsync(
                        () => shell.MainPaneHorizontalAlignment == HorizontalAlignment.Center,
                        TimeSpan.FromSeconds(2));

                    Assert.Equal(HorizontalAlignment.Center, shell.MainPaneHorizontalAlignment);
                    Assert.Equal(1120d, shell.MainPaneMaxWidth);

                    context.ScreenSharePreviewFrame = previewFrame;
                    context.ShowScreenSharePreviewFrame = true;

                    await WaitUntilAsync(
                        () => shell.MainPaneHorizontalAlignment == HorizontalAlignment.Stretch,
                        TimeSpan.FromSeconds(2));

                    Assert.Equal(HorizontalAlignment.Stretch, shell.MainPaneHorizontalAlignment);
                    Assert.True(double.IsPositiveInfinity(shell.MainPaneMaxWidth));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previousScaffold);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenDisconnected_AndLaterApproved_KeepsScreenShareInactive_UntilNewFrameArrives()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                EnsureAppServices();
                var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
                using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
                using var helper = new HelperPageViewModel(
                    cancelAction: static () => { },
                    transportConfig,
                    runtime,
                    openDiagnosticsAction: static () => { },
                    clipboardService: new TestClipboardService(),
                    shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

                var shell = new SessionShellView
                {
                    Width = 1080,
                    ShowScreenShareAction = true,
                    MainContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                    },
                    ChatContent = new Border
                    {
                        [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                    },
                    DataContext = helper,
                };

                var window = new Window { Width = 1080, Height = 760, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();

                    InvokePrivate(
                        helper,
                        "OnScreenShareFrameCompleted",
                        null,
                        new NLink.Core.ScreenShare.ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", CreateTinyImageBytes()));

                    await WaitUntilAsync(() => helper.ScreenShareViewer.CurrentFrame is not null, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => helper.ShowRemoteScreenShareFrame, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));

                    InvokePrivate(helper, "OnDisconnected", null, EventArgs.Empty);

                    await WaitUntilAsync(() => helper.ScreenShareViewer.CurrentFrame is null, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => !helper.ShowRemoteScreenShareFrame, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => !shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));

                    InvokePrivate(helper, "OnApproved", null, EventArgs.Empty);
                    await FlushUiAsync();

                    Assert.Null(helper.ScreenShareViewer.CurrentFrame);
                    Assert.False(helper.ShowRemoteScreenShareFrame);
                    Assert.False(shell.ShowScreenSharePane);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenNotConnected_ScreenShareCommand_NoOps()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var shell = new SessionShellView
                {
                    ShowScreenShareAction = true,
                    DataContext = new StaleDisconnectedShellContext(),
                };

                await FlushUiAsync();

                shell.ScreenShareCommand?.Execute(null);
                await FlushUiAsync();

                Assert.False(shell.EffectiveShowScreenShareAction);
                Assert.False(shell.ShowScreenSharePane);
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_ScreenShareHeaderButton_TogglesBetweenShareAndStop()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var context = new MutableConnectedScreenShareShellContext
                {
                    HeaderStatusText = "Connected",
                    ShowConnectedPanel = true,
                    CanShowScreenShareAction = true,
                };
                var shell = new SessionShellView
                {
                    Width = 760,
                    ShowScreenShareAction = true,
                    DataContext = context,
                };

                var window = new Window { Width = 760, Height = 240, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();

                    var shareButton = Assert.IsType<Button>(FindFirstControlByAutomationId(window, "SessionHeader.ShareScreen"));
                    Assert.Equal("Share screen", Assert.IsType<string>(shareButton.Content));
                    Assert.False(shell.ShowScreenSharePane);

                    shell.ScreenShareCommand?.Execute(null);
                    await WaitUntilAsync(() => context.IsScreenSharingPreviewActive, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));
                    await FlushUiAsync();
                    Assert.Equal("Stop sharing", Assert.IsType<string>(shareButton.Content));

                    shell.ScreenShareCommand?.Execute(null);
                    await WaitUntilAsync(() => !context.IsScreenSharingPreviewActive, TimeSpan.FromSeconds(2));
                    await WaitUntilAsync(() => !shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));
                    await FlushUiAsync();
                    Assert.Equal("Share screen", Assert.IsType<string>(shareButton.Content));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_ScreenShareHeaderButton_DisablesWhenToggleCommandCannotExecute()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var previous = Environment.GetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD");
            try
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", "1");
                var context = new MutableConnectedScreenShareShellContext
                {
                    HeaderStatusText = "Connected",
                    ShowConnectedPanel = true,
                    CanShowScreenShareAction = true,
                    CanToggleScreenSharePreview = true,
                };
                var shell = new SessionShellView
                {
                    Width = 760,
                    ShowScreenShareAction = true,
                    DataContext = context,
                };

                var window = new Window { Width = 760, Height = 240, Content = shell };
                window.Show();

                try
                {
                    await FlushUiAsync();

                    var shareButton = Assert.IsType<Button>(FindFirstControlByAutomationId(window, "SessionHeader.ShareScreen"));
                    Assert.True(shareButton.IsEnabled);

                    context.CanToggleScreenSharePreview = false;
                    await WaitUntilAsync(() => !shareButton.IsEnabled, TimeSpan.FromSeconds(2));

                    context.CanToggleScreenSharePreview = true;
                    await WaitUntilAsync(() => shareButton.IsEnabled, TimeSpan.FromSeconds(2));
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("NLINK_FEATURE_SCREENCAP_SCAFFOLD", previous);
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SessionShell_WhenNotConnected_HidesChatPane_EvenIfDataContextPanelFlagIsStale()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var shell = new SessionShellView
            {
                Width = 760,
                MainContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                },
                ChatContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                },
                DataContext = new StaleDisconnectedShellContext(),
            };

            var window = new Window { Width = 760, Height = 760, Content = shell };
            window.Show();

            try
            {
                await FlushUiAsync();

                Assert.False(shell.ShowChatPane);
                Assert.NotNull(FindFirstControlByAutomationId(window, "Shell.Main"));
                Assert.Null(FindFirstVisibleControlByAutomationId(window, "Shell.Chat"));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    private static AppServiceRegistry EnsureAppServices()
    {
        var app = Assert.IsType<NLink.App.App>(Application.Current);
        var services = app.Services;

        if (!services.TryGet<IClipboardService>(out _))
        {
            var clipboard = new TestClipboardService();
            services.AddSingleton<IClipboardService>(clipboard);
        }

        if (!services.TryGet<IInviteShareService>(out _))
        {
            services.AddSingleton<IInviteShareService>(new DefaultInviteShareService());
        }

        if (!services.TryGet<IQrCodeService>(out _))
        {
            services.AddSingleton<IQrCodeService>(new QrCodeService());
        }

        if (!services.TryGet<IRecentConnectTargetsStore>(out _))
        {
            services.AddSingleton<IRecentConnectTargetsStore>(new LocalRecentConnectTargetsStore());
        }

        if (!services.TryGet<NLink.App.Configuration.ShareMessageConfig>(out _))
        {
            services.AddSingleton(new NLink.App.Configuration.ShareMessageConfig(null));
        }

        if (!services.TryGet<MetricsRegistry>(out _))
        {
            services.AddSingleton(new MetricsRegistry());
        }

        if (!services.TryGet<ResourceRuntimeTracker>(out _))
        {
            services.AddSingleton(new ResourceRuntimeTracker());
        }

        return services;
    }

    private static AppServiceRegistry CreateServicesForMainWindow()
    {
        var services = new AppServiceRegistry();
        services.AddSingleton<IClipboardService>(new TestClipboardService());
        services.AddSingleton<IInviteShareService>(new DefaultInviteShareService());
        services.AddSingleton<IQrCodeService>(new QrCodeService());
        services.AddSingleton<IRecentConnectTargetsStore>(new LocalRecentConnectTargetsStore());
        services.AddSingleton(new NLink.App.Configuration.ShareMessageConfig(null));
        services.AddSingleton(new MetricsRegistry());
        services.AddSingleton(new ResourceRuntimeTracker());
        return services;
    }

    private static T? FindFirstDescendant<T>(Control root)
        where T : class
        => root.GetVisualDescendants().OfType<T>().FirstOrDefault();

    private static Control? FindFirstControlByAutomationId(Control root, string automationId)
        => root.GetVisualDescendants()
            .OfType<Control>()
            .Concat(root.GetLogicalDescendants().OfType<Control>())
            .FirstOrDefault(control =>
                string.Equals(
                    AutomationProperties.GetAutomationId(control),
                    automationId,
                    StringComparison.Ordinal));

    private static Control? FindFirstVisibleControlByAutomationId(Control root, string automationId)
        => root.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control =>
                control.IsVisible &&
                string.Equals(
                    AutomationProperties.GetAutomationId(control),
                    automationId,
                    StringComparison.Ordinal));

    private static async Task FlushUiAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (predicate())
            {
                return;
            }

            await FlushUiAsync();
        }

        throw new TimeoutException($"Condition not met within {timeout.TotalSeconds:N1}s.");
    }

    private static async Task<T> WaitForLayoutConditionAsync<T>(
        Control root,
        Func<T?> probe,
        TimeSpan timeout,
        string phase)
        where T : class
    {
        var current = probe();
        if (current is not null)
        {
            return current;
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        void TryComplete()
        {
            var value = probe();
            if (value is not null)
            {
                tcs.TrySetResult(value);
            }
        }

        EventHandler? handler = null;
        handler = (_, _) => TryComplete();
        root.LayoutUpdated += handler;

        try
        {
            await FlushUiAsync();
            TryComplete();

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var registration = timeoutCts.Token.Register(() =>
                tcs.TrySetException(new TimeoutException($"Timed out waiting for {phase} after {timeout.TotalSeconds:N1}s.")));
            return await tcs.Task;
        }
        finally
        {
            root.LayoutUpdated -= handler;
        }
    }

    private sealed class BlockingQrCodeService : IQrCodeService
    {
        private readonly QrCodeService inner = new();
        private readonly object sync = new();
        private TaskCompletionSource<bool>? nextCreateGate;
        private int completedCreateCount;

        public int CompletedCreateCount => Volatile.Read(ref completedCreateCount);

        public void BlockNextCreate()
        {
            lock (sync)
            {
                nextCreateGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void ReleaseBlockedCreate()
        {
            TaskCompletionSource<bool>? gate;
            lock (sync)
            {
                gate = nextCreateGate;
                nextCreateGate = null;
            }

            gate?.TrySetResult(true);
        }

        public bool TryCreatePng(string text, out byte[] pngBytes, out string? errorMessage)
        {
            TaskCompletionSource<bool>? gate;
            lock (sync)
            {
                gate = nextCreateGate;
            }

            gate?.Task.GetAwaiter().GetResult();
            var created = inner.TryCreatePng(text, out pngBytes, out errorMessage);
            Interlocked.Increment(ref completedCreateCount);
            return created;
        }

        public bool TryDecode(Stream imageStream, out string? decodedText, out string? errorMessage)
            => inner.TryDecode(imageStream, out decodedText, out errorMessage);
    }

    private static Image? FindVisibleScreenShareViewer(Control root)
        => root.GetVisualDescendants()
            .OfType<Image>()
            .FirstOrDefault(control =>
                control.IsVisible &&
                string.Equals(
                    AutomationProperties.GetAutomationId(control),
                    "ScreenShare.Viewer",
                    StringComparison.Ordinal) &&
                control.Bounds.Width > 0 &&
                control.Bounds.Height > 0);

    private static void InvokePrivate(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, null);
    }

    private static void InvokePrivate(object target, string methodName, params object?[]? args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, args);
    }

    private static byte[] CreateTinyImageBytes()
    {
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a5kAAAAASUVORK5CYII=");
    }

    private static Bitmap CreateBitmap(int width, int height)
    {
        var writeable = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var locked = writeable.Lock())
        {
            var totalBytes = width * height * 4;
            var pixels = new byte[totalBytes];
            Marshal.Copy(pixels, 0, locked.Address, totalBytes);
        }

        return writeable;
    }

    private static Bitmap CreateTinyBitmap()
    {
        using var stream = new MemoryStream(CreateTinyImageBytes(), writable: false);
        return new Bitmap(stream);
    }

    private static void SetPrivateProperty(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(property);
        property!.SetValue(target, value);
    }

    private static async Task<ConnectedSessionContext> CreateConnectedSessionContextAsync(Action<HelpeePageViewModel>? configureIncomingApproval = null)
    {
        var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
        var network = new FakeSessionTransportNetwork();
        var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-ui-smoke-" + Guid.NewGuid().ToString("N")));
        var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-ui-smoke-" + Guid.NewGuid().ToString("N")));
        var helper = new HelperPageViewModel(
            cancelAction: static () => { },
            transportConfig,
            helperRuntime,
            openDiagnosticsAction: static () => { },
            clipboardService: new TestClipboardService(),
            shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));
        var helpee = new HelpeePageViewModel(
            cancelAction: static () => { },
            transportConfig,
            helpeeRuntime,
            openDiagnosticsAction: static () => { },
            clipboardService: new TestClipboardService(),
            shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));

        await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(3));
        var connectTask = helperRuntime.StartHelperAsync(new NLink.Core.SessionConnect.PeerAddress(helpeeRuntime.CurrentLocalPeerAddress!.Value.Value), CancellationToken.None);

        await WaitUntilAsync(
            () => helpee.HasIncomingRequest && helpee.ConnectionState == "IncomingRequest",
            TimeSpan.FromSeconds(5));

        configureIncomingApproval?.Invoke(helpee);
        helpee.AllowCommand.Execute(null);
        await connectTask;

        await WaitUntilAsync(
            () => helpee.ConnectionState == "Connected" && helper.ConnectionState == "Connected",
            TimeSpan.FromSeconds(5));

        return new ConnectedSessionContext(helper, helpee, helperRuntime, helpeeRuntime);
    }

    private static NLink.App.Configuration.TransportRuntimeConfig CreateNknUiTestConfig()
    {
        var constructor = typeof(NLink.App.Configuration.TransportRuntimeConfig).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[]
            {
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(BridgeReusePolicy),
                typeof(Func<NLink.Core.ISignalingTransport>),
            },
            modifiers: null);

        Assert.NotNull(constructor);

        return (NLink.App.Configuration.TransportRuntimeConfig)constructor!.Invoke(
            new object?[]
            {
                "NKN",
                "Internet connection",
                "Release",
                "NKN",
                "ui-test",
                true,
                false,
                false,
                true,
                "ui-test",
                string.Empty,
                string.Empty,
                BridgeReusePolicy.Default,
                (Func<NLink.Core.ISignalingTransport>)(() => new NLink.Infra.DevLocal.DevLocalTransport()),
            });
    }

    private sealed class TestClipboardService : IClipboardService
    {
        public string? LastText { get; private set; }

        public Task SetTextAsync(string text)
        {
            LastText = text;
            return Task.CompletedTask;
        }
    }

    private sealed class TestInviteShareService : IInviteShareService
    {
        public string? LastInviteText { get; private set; }

        public Task<InviteShareResult> ShareInviteAsync(string inviteText, CancellationToken ct)
        {
            LastInviteText = inviteText;
            return Task.FromResult(new InviteShareResult(true));
        }
    }

    private sealed class FixedRecentConnectTargetsStore : IRecentConnectTargetsStore
    {
        private readonly IReadOnlyList<string> targets;

        public FixedRecentConnectTargetsStore(params string[] targets)
        {
            this.targets = targets;
        }

        public IReadOnlyList<string> LoadTargets() => targets;

        public void SaveTargets(IReadOnlyList<string> targets)
        {
        }
    }

    private sealed class ConnectedShellContext
    {
        public string HeaderStatusText => "Connected";
    }

    private sealed class ConnectedChatShellContext
    {
        public string HeaderStatusText => "Connected";

        public bool ShowConnectedPanel => true;
    }

    private sealed class MutableConnectedChatShellContext : INotifyPropertyChanged
    {
        private string headerStatusText = "Ready";
        private bool showConnectedPanel;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string HeaderStatusText
        {
            get => headerStatusText;
            set
            {
                if (string.Equals(headerStatusText, value, StringComparison.Ordinal))
                {
                    return;
                }

                headerStatusText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderStatusText)));
            }
        }

        public bool ShowConnectedPanel
        {
            get => showConnectedPanel;
            set
            {
                if (showConnectedPanel == value)
                {
                    return;
                }

                showConnectedPanel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowConnectedPanel)));
            }
        }
    }

    private sealed class MutableConnectedScreenShareShellContext : INotifyPropertyChanged
    {
        private string headerStatusText = "Ready";
        private bool showConnectedPanel;
        private bool showScreenSharePreviewFrame;
        private Bitmap? screenSharePreviewFrame;
        private bool isScreenSharingPreviewActive;
        private bool canShowScreenShareAction;
        private bool canToggleScreenSharePreview = true;
        private readonly RelayCommand toggleScreenSharePreviewCommand;

        public MutableConnectedScreenShareShellContext()
        {
            toggleScreenSharePreviewCommand = new RelayCommand(() =>
            {
                IsScreenSharingPreviewActive = !IsScreenSharingPreviewActive;
                ShowScreenSharePreviewFrame = IsScreenSharingPreviewActive;
            }, () => CanToggleScreenSharePreview);
            ToggleScreenSharePreviewCommand = toggleScreenSharePreviewCommand;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string HeaderStatusText
        {
            get => headerStatusText;
            set
            {
                if (string.Equals(headerStatusText, value, StringComparison.Ordinal))
                {
                    return;
                }

                headerStatusText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderStatusText)));
            }
        }

        public bool ShowConnectedPanel
        {
            get => showConnectedPanel;
            set
            {
                if (showConnectedPanel == value)
                {
                    return;
                }

                showConnectedPanel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowConnectedPanel)));
            }
        }

        public bool CanShowScreenShareAction
        {
            get => canShowScreenShareAction;
            set
            {
                if (canShowScreenShareAction == value)
                {
                    return;
                }

                canShowScreenShareAction = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanShowScreenShareAction)));
            }
        }

        public bool IsScreenSharingPreviewActive
        {
            get => isScreenSharingPreviewActive;
            set
            {
                if (isScreenSharingPreviewActive == value)
                {
                    return;
                }

                isScreenSharingPreviewActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsScreenSharingPreviewActive)));
            }
        }

        public bool ShowScreenSharePreviewFrame
        {
            get => showScreenSharePreviewFrame;
            set
            {
                if (showScreenSharePreviewFrame == value)
                {
                    return;
                }

                showScreenSharePreviewFrame = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowScreenSharePreviewFrame)));
            }
        }

        public Bitmap? ScreenSharePreviewFrame
        {
            get => screenSharePreviewFrame;
            set
            {
                if (ReferenceEquals(screenSharePreviewFrame, value))
                {
                    return;
                }

                screenSharePreviewFrame = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScreenSharePreviewFrame)));
            }
        }

        public bool CanToggleScreenSharePreview
        {
            get => canToggleScreenSharePreview;
            set
            {
                if (canToggleScreenSharePreview == value)
                {
                    return;
                }

                canToggleScreenSharePreview = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanToggleScreenSharePreview)));
                toggleScreenSharePreviewCommand.NotifyCanExecuteChanged();
            }
        }

        public ICommand ToggleScreenSharePreviewCommand { get; }
    }

    private sealed class StaleDisconnectedShellContext
    {
        public string HeaderStatusText => "Request rejected";

        public bool ShowConnectedPanel => true;
    }

    private sealed class ConnectedSessionContext : IDisposable
    {
        public ConnectedSessionContext(
            HelperPageViewModel helper,
            HelpeePageViewModel helpee,
            SessionRuntime helperRuntime,
            SessionRuntime helpeeRuntime)
        {
            Helper = helper;
            Helpee = helpee;
            HelperRuntime = helperRuntime;
            HelpeeRuntime = helpeeRuntime;
        }

        public HelperPageViewModel Helper { get; }

        public HelpeePageViewModel Helpee { get; }

        public SessionRuntime HelperRuntime { get; }

        public SessionRuntime HelpeeRuntime { get; }

        public void Dispose()
        {
            Helper.Dispose();
            Helpee.Dispose();
            HelperRuntime.Dispose();
            HelpeeRuntime.Dispose();
        }
    }

    private sealed class FakeSessionTransportNetwork
    {
        private readonly object gate = new();
        private readonly Dictionary<string, FakeSessionTransport> hostsByAddress = new(StringComparer.Ordinal);

        public FakeSessionTransport CreateTransport(string address)
        {
            return new FakeSessionTransport(this, address);
        }

        public void RegisterHost(string address, FakeSessionTransport host)
        {
            lock (gate)
            {
                hostsByAddress[address] = host;
            }
        }

        public void UnregisterHost(FakeSessionTransport transport)
        {
            lock (gate)
            {
                foreach (var pair in hostsByAddress.ToArray())
                {
                    if (ReferenceEquals(pair.Value, transport))
                    {
                        hostsByAddress.Remove(pair.Key);
                    }
                }
            }
        }

        public FakeSessionTransport? TryFindHost(string address)
        {
            lock (gate)
            {
                return hostsByAddress.TryGetValue(address, out var host) ? host : null;
            }
        }
    }

    private sealed class FakeSessionTransport : NLink.Core.ISignalingTransport, NLink.Core.IAddressTargetSignalingTransport, NLink.Core.IInviteTargetSignalingTransport, NLink.Core.IAddressHostSignalingTransport, NLink.Core.IHostReadySignalingTransport, NLink.Core.ILocalPeerAddressSignalingTransport, NLink.Core.ISessionSecuritySignalingTransport
    {
        private readonly FakeSessionTransportNetwork network;
        private readonly byte[] sharedKey = SHA256LikeDeterministicBytes("beta3-ui-smoke-key", 32);
        private readonly TaskCompletionSource<bool> hostReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;
        private FakeSessionTransport? peer;
        private bool disposed;

        public FakeSessionTransport(FakeSessionTransportNetwork network, string address)
        {
            this.network = network;
            Address = address;
        }

        public string Address { get; }
        public string LocalPeerAddress => Address;

        public event EventHandler<NLink.Core.IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<NLink.Core.TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<NLink.Core.TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<NLink.Core.TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public Task WaitUntilHostReadyAsync(CancellationToken ct) => hostReadyTcs.Task.WaitAsync(ct);

        public Task HostByAddressAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            network.RegisterHost(Address, this);
            UpdateSessionSecurityState(SessionSecurityState.CreateHelpeeWaiting(new PeerAddress(Address)));
            hostReadyTcs.TrySetResult(true);
            return Task.Delay(Timeout.Infinite, ct);
        }

        public Task JoinByAddressAsync(string peerAddress, CancellationToken ct)
        {
            ThrowIfDisposed();
            var host = network.TryFindHost(peerAddress) ?? throw new TimeoutException("Host not found.");
            return JoinCoreAsync(
                host,
                new SessionId($"fake_session_{Guid.NewGuid():N}"),
                SessionSecurityDefaults.AllCapabilityGrants,
                inviteValidated: true);
        }

        public Task JoinByInviteAsync(string inviteToken, ValidatedInviteV1 invite, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(inviteToken))
            {
                throw new ArgumentException("Invite token is required.", nameof(inviteToken));
            }

            ArgumentNullException.ThrowIfNull(invite);
            var host = network.TryFindHost(invite.TargetAddress.Value) ?? throw new TimeoutException("Host not found.");
            var helperAddress = new PeerAddress(Address);
            if (invite.BoundHelperAddress is not null && invite.BoundHelperAddress != helperAddress)
            {
                throw new InvalidOperationException("Invite token is bound to a different helper identity.");
            }

            return JoinCoreAsync(
                host,
                invite.SessionId,
                invite.Payload.Capabilities.ToCapabilityGrant(),
                inviteValidated: true);
        }

        private Task JoinCoreAsync(
            FakeSessionTransport host,
            SessionId sessionId,
            CapabilityGrant requestedCapabilities,
            bool inviteValidated)
        {
            peer = host;
            host.peer = this;
            var helpeeAddress = new PeerAddress(host.Address);
            var helperAddress = new PeerAddress(Address);
            var approvalRequest = new ApprovalRequest(
                helperAddress,
                requestedCapabilities,
                sessionId);

            var verifiedState = CreateVerifiedSecurityState(sessionId, helpeeAddress, helperAddress, inviteValidated);
            UpdateSessionSecurityState(verifiedState);
            host.UpdateSessionSecurityState(verifiedState);

            var joinRequest = new NLink.Core.IncomingJoinRequestEventArgs(
                approveAsync: (decision, _) =>
                {
                    if (decision is null)
                    {
                        throw new InvalidOperationException("Explicit approval decision is required.");
                    }

                    ValidateApprovalDecision(approvalRequest, decision);
                    var grant = decision.ToGrant();
                    host.UpdateSessionSecurityState(host.CurrentSessionSecurityState.WithApproval(grant));
                    UpdateSessionSecurityState(CurrentSessionSecurityState.WithApproval(grant));
                    host.SessionKeyReady?.Invoke(host, new NLink.Core.TransportSessionKeyReadyEventArgs(host.sharedKey));
                    SessionKeyReady?.Invoke(this, new NLink.Core.TransportSessionKeyReadyEventArgs(sharedKey));
                    host.Approved?.Invoke(host, EventArgs.Empty);
                    Approved?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                },
                rejectAsync: _ =>
                {
                    host.UpdateSessionSecurityState(host.CurrentSessionSecurityState.Invalidate("local_reject"));
                    UpdateSessionSecurityState(CurrentSessionSecurityState.Invalidate("local_reject"));
                    Rejected?.Invoke(this, EventArgs.Empty);
                    return Task.CompletedTask;
                },
                approvalRequest: approvalRequest);

            host.IncomingJoinRequest?.Invoke(host, joinRequest);
            return Task.CompletedTask;
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ThrowIfDisposed();
            var target = peer ?? throw new InvalidOperationException("No peer connected.");
            target.ChatMessageReceived?.Invoke(target, new NLink.Core.TransportChatMessageEventArgs(payload.ToArray()));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            network.UnregisterHost(this);

            if (peer is { } target)
            {
                peer = null;
                target.peer = null;
                UpdateSessionSecurityState(CurrentSessionSecurityState.Invalidate("transport_disposed"));
                target.UpdateSessionSecurityState(target.CurrentSessionSecurityState.Invalidate("transport_disposed"));
                target.Disconnected?.Invoke(target, EventArgs.Empty);
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(FakeSessionTransport));
            }
        }

        private void UpdateSessionSecurityState(SessionSecurityState nextState)
        {
            if (Equals(currentSessionSecurityState, nextState))
            {
                return;
            }

            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new NLink.Core.TransportSessionSecurityStateChangedEventArgs(nextState));
        }

        private static SessionSecurityState CreateVerifiedSecurityState(
            SessionId sessionId,
            PeerAddress helpeeAddress,
            PeerAddress helperAddress,
            bool inviteValidated)
        {
            return (SessionSecurityState.Empty with
            {
                SessionId = sessionId,
                HelpeeAddress = helpeeAddress,
                HelperAddress = helperAddress,
                InviteValidated = inviteValidated,
            }).WithHandshakeVerified(helperAddress);
        }

        private static void ValidateApprovalDecision(ApprovalRequest approvalRequest, ApprovalDecision decision)
        {
            if (decision.SessionId != approvalRequest.SessionId ||
                decision.HelperIdentity != approvalRequest.HelperIdentity ||
                decision.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
                (decision.ApprovedCapabilities & ~approvalRequest.RequestedCapabilities) != 0)
            {
                throw new InvalidOperationException("Approval decision does not match the pending approval request.");
            }
        }
    }

    private sealed class DelayedHostReadyTransport : NLink.Core.ISignalingTransport, NLink.Core.IAddressHostSignalingTransport, NLink.Core.IHostReadySignalingTransport, NLink.Core.ILocalPeerAddressSignalingTransport, NLink.Core.ISessionSecuritySignalingTransport
    {
        private readonly TaskCompletionSource<bool> hostReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;
        private bool disposed;

        public DelayedHostReadyTransport(string address)
        {
            LocalPeerAddress = address;
        }

        public string LocalPeerAddress { get; }
        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public event EventHandler<NLink.Core.IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<NLink.Core.TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<NLink.Core.TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<NLink.Core.TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;

        public Task HostByAddressAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            UpdateSessionSecurityState(SessionSecurityState.CreateHelpeeWaiting(new PeerAddress(LocalPeerAddress)));
            return Task.Delay(Timeout.Infinite, ct);
        }

        public Task WaitUntilHostReadyAsync(CancellationToken ct) => hostReadyTcs.Task.WaitAsync(ct);

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
            => Task.CompletedTask;

        public void ReleaseHostReady()
        {
            hostReadyTcs.TrySetResult(true);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            hostReadyTcs.TrySetCanceled();
        }

        private void UpdateSessionSecurityState(SessionSecurityState nextState)
        {
            if (Equals(currentSessionSecurityState, nextState))
            {
                return;
            }

            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new NLink.Core.TransportSessionSecurityStateChangedEventArgs(nextState));
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(DelayedHostReadyTransport));
            }
        }
    }

    private static byte[] SHA256LikeDeterministicBytes(string text, int length)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        if (hash.Length == length)
        {
            return hash;
        }

        return hash[..length];
    }
}

public sealed class Beta3DefaultUiFixture : IDisposable
{
    public Beta3DefaultUiFixture()
    {
        Session = HeadlessUnitTestSession.StartNew(typeof(AvaloniaHeadlessUiAppBootstrap));
    }

    public HeadlessUnitTestSession Session { get; }

    public void Dispose()
    {
        Session.Dispose();
    }
}
