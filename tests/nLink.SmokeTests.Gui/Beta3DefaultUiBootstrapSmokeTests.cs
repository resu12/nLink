using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
using NLink.Core;
using NLink.Core.Metrics;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
[Trait("Area", "Gui")]
public sealed class Beta3DefaultUiBootstrapSmokeTests : Beta3DefaultUiSmokeTestBase
{
    public Beta3DefaultUiBootstrapSmokeTests(Beta3DefaultUiFixture fixture) : base(fixture) { }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelperPage_PublicInviteFlow_ShowsHelperIdentityBootstrapPanel()
    {
    #if DEBUG
            await Task.CompletedTask;
            return;
    #endif
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
            var transportConfig = CreateNknUiTestConfig();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, transportConfig, runtime, openDiagnosticsAction: static () =>
            {
            }, clipboardService: new TestClipboardService(), shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null), bootstrapHelperIdentityResolver: _ => Task.FromResult<PeerAddress?>(new PeerAddress("nlink-helper.bootstrap.actual.1234567890")));
            var view = new HelperPageView
            {
                DataContext = helper
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = view
            };
            try
            {
                window.Show();
                await FlushUiAsync();
                Assert.False(string.IsNullOrWhiteSpace(helper.HelperIdentityBootstrapText));
                var shareButton = Assert.IsType<Button>(FindFirstVisibleControlByAutomationId(window, "Helper.ShareHelperIdentity"));
                var copyButton = Assert.IsType<Button>(FindFirstVisibleControlByAutomationId(window, "Helper.CopyHelperIdentity"));
                var helperTextBox = Assert.IsType<TextBox>(FindFirstVisibleControlByAutomationId(window, "Helper.HelperIdentityBootstrapTextBox"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helper.IdentityText"));
                Assert.Equal("Share helper address", shareButton.Content?.ToString());
                Assert.Equal("Copy helper address", copyButton.Content?.ToString());
                Assert.Equal(helper.HelperIdentityBootstrapText, helperTextBox.Text);
                Assert.True(helperTextBox.IsEnabled);
                Assert.True(shareButton.IsEnabled);
                Assert.True(copyButton.IsEnabled);
                Assert.True(helper.ShowHelperBootstrapQr);
                Assert.False(helper.ShowHelperBootstrapQrPlaceholder);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "LegacySmoke")]
    public async Task HelperPage_PublicInviteFlow_BootstrapFailure_ShowsExplicitHelperAddressError()
    {
    #if DEBUG
            await Task.CompletedTask;
            return;
    #endif
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
            var transportConfig = CreateNknUiTestConfig();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, transportConfig, runtime, openDiagnosticsAction: static () =>
            {
            }, clipboardService: new TestClipboardService(), shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null), bootstrapHelperIdentityResolver: _ => Task.FromException<PeerAddress?>(new CryptographicException("The data is invalid.")));
            var view = new HelperPageView
            {
                DataContext = helper
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = view
            };
            try
            {
                window.Show();
                await FlushUiAsync();
                var resolveMethod = typeof(HelperPageViewModel).GetMethod("ResolveBootstrapHelperIdentityAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(resolveMethod);
                var resolveTask = Assert.IsAssignableFrom<Task>(resolveMethod.Invoke(helper, new object[] { CancellationToken.None }));
                await resolveTask;
                await FlushUiAsync();
                await WaitUntilAsync(() => string.Equals(helper.HelperIdentityBootstrapHintText, "Protected seed storage could not be read.", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
                var hint = Assert.IsType<TextBlock>(FindFirstVisibleControlByAutomationId(window, "Helper.HelperIdentityBootstrapHint"));
                var shareButton = Assert.IsType<Button>(FindFirstVisibleControlByAutomationId(window, "Helper.ShareHelperIdentity"));
                var copyButton = Assert.IsType<Button>(FindFirstVisibleControlByAutomationId(window, "Helper.CopyHelperIdentity"));
                Assert.Equal("Protected seed storage could not be read.", hint.Text);
                Assert.True(shareButton.IsVisible);
                Assert.True(copyButton.IsVisible);
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
    #if DEBUG
            await Task.CompletedTask;
            return;
    #endif
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
            var transportConfig = CreateNknUiTestConfig();
            var clipboard = new TestClipboardService();
            var expectedHelperIdentity = new PeerAddress("nlink-helper.bootstrap.actual.1234567890");
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, transportConfig, runtime, openDiagnosticsAction: static () =>
            {
            }, clipboardService: clipboard, shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null), bootstrapHelperIdentityResolver: _ => Task.FromResult<PeerAddress?>(expectedHelperIdentity));
            SetPrivateField(runtime, "transport", new FixedLocalPeerAddressTransport("nlink-runtime.local.identity"));
            var view = new HelperPageView
            {
                DataContext = helper
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = view
            };
            try
            {
                window.Show();
                await FlushUiAsync();
                await helper.CopyHelperIdentityCommand.ExecuteAsync(null);
                Assert.Equal(helper.HelperIdentityBootstrapText, clipboard.LastText);
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
    #if DEBUG
            await Task.CompletedTask;
            return;
    #endif
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var unboundInviteOptIn = new EnvironmentOverride(NLink.App.Configuration.AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, null);
            var transportConfig = CreateNknUiTestConfig();
            var shareService = new TestInviteShareService();
            var expectedHelperIdentity = new PeerAddress("nlink-helper.bootstrap.actual.1234567890");
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, transportConfig, runtime, openDiagnosticsAction: static () =>
            {
            }, clipboardService: new TestClipboardService(), shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null), bootstrapHelperIdentityResolver: _ => Task.FromResult<PeerAddress?>(expectedHelperIdentity), inviteShareService: shareService);
            SetPrivateField(runtime, "transport", new FixedLocalPeerAddressTransport("nlink-runtime.local.identity"));
            var view = new HelperPageView
            {
                DataContext = helper
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = view
            };
            try
            {
                window.Show();
                await FlushUiAsync();
                var shareButton = Assert.IsType<Button>(FindFirstVisibleControlByAutomationId(window, "Helper.ShareHelperIdentity"));
                Assert.Equal("Share helper address", shareButton.Content?.ToString());
                await helper.ShareHelperIdentityCommand.ExecuteAsync(null);
                Assert.Equal(helper.HelperIdentityBootstrapText, shareService.LastInviteText);
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
            using var helpee = new HelpeePageViewModel(cancelAction: static () =>
            {
            }, transportConfig, helpeeRuntime);
            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(3));
            await helperRuntime.StartHelperAsync(new NLink.Core.SessionConnect.PeerAddress(helpeeRuntime.CurrentLocalPeerAddress!.Value.Value), CancellationToken.None);
            await WaitUntilAsync(() => helpee.IsIncomingRequestView && helpee.ShowIncomingRequestPanel && helpee.HasIncomingHelperVerificationCode && helpee.ShowIncomingRequestTimeout, TimeSpan.FromSeconds(3));
            var view = new HelpeePageView
            {
                DataContext = helpee
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = view
            };
            window.Show();
            try
            {
                await FlushUiAsync();
                Assert.NotNull(FindFirstVisibleControlByAutomationId(window, "Helpee.IncomingApprovalTitle"));
                var approvalTimer = Assert.IsType<TextBlock>(FindFirstVisibleControlByAutomationId(window, "Helpee.IncomingApprovalTimer"));
                Assert.Equal(helpee.IncomingRequestTimeoutText, approvalTimer.Text);
                Assert.False(string.IsNullOrWhiteSpace(approvalTimer.Text));
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
                Assert.True(Math.Abs(allowButton!.Bounds.Y - declineButton!.Bounds.Y) < 2, $"Expected approval actions on the same row, got Y={allowButton.Bounds.Y:N1} and {declineButton.Bounds.Y:N1}.");
                Assert.Null(FindFirstControlByAutomationId(window, "Helpee.IncomingHelperIdentity"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helpee.IncomingSessionId"));
                Assert.Null(FindFirstControlByAutomationId(window, "Helpee.IncomingTechnicalDetails"));
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), textBlock => textBlock.IsVisible && string.Equals(textBlock.Text, "Helper verification code", StringComparison.Ordinal));
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
            using var helpee = new HelpeePageViewModel(cancelAction: static () =>
            {
            }, transportConfig, helpeeRuntime);
            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(3));
            await helperRuntime.StartHelperAsync(new NLink.Core.SessionConnect.PeerAddress(helpeeRuntime.CurrentLocalPeerAddress!.Value.Value), CancellationToken.None);
            await WaitUntilAsync(() => helpee.IsIncomingRequestView && helpee.ShowIncomingRequestPanel, TimeSpan.FromSeconds(3));
            var view = new HelpeePageView
            {
                DataContext = helpee
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = view
            };
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
    public async Task HelperWaitingForApproval_ShowsVerificationCodePanel()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            var network = new FakeSessionTransportNetwork();
            using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-helper-verify-ui-" + Guid.NewGuid().ToString("N")));
            using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-verify-ui-" + Guid.NewGuid().ToString("N")));
            using var helpee = new HelpeePageViewModel(cancelAction: static () =>
            {
            }, transportConfig, helpeeRuntime);
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, transportConfig, helperRuntime);
            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(3));
            _ = helperRuntime.StartHelperAsync(new NLink.Core.SessionConnect.PeerAddress(helpeeRuntime.CurrentLocalPeerAddress!.Value.Value), CancellationToken.None);
            await WaitUntilAsync(() => helpee.IsIncomingRequestView && helper.ShowHelperVerificationCode && !string.IsNullOrWhiteSpace(helper.HelperVerificationCode), TimeSpan.FromSeconds(3));
            var view = new HelperPageView
            {
                DataContext = helper
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = view
            };
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
            using var helpee = new HelpeePageViewModel(cancelAction: static () =>
            {
            }, transportConfig, helpeeRuntime);
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, transportConfig, helperRuntime);
            await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(3));
            var connectTask = helperRuntime.StartHelperAsync(new NLink.Core.SessionConnect.PeerAddress(helpeeRuntime.CurrentLocalPeerAddress!.Value.Value), CancellationToken.None);
            await WaitUntilAsync(() => helpee.IsIncomingRequestView, TimeSpan.FromSeconds(3));
            helpee.AllowCommand.Execute(null);
            await connectTask;
            await WaitUntilAsync(() => helper.ConnectionState == "Connected", TimeSpan.FromSeconds(3));
            var view = new HelperPageView
            {
                DataContext = helper
            };
            var window = new Window
            {
                Width = 1280,
                Height = 860,
                Content = view
            };
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
    public async Task HelperPage_CopyInstallFeedback_ShowsBelowLink_WithoutMovingLayout()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
            using var runtime = new SessionRuntime(() => new NLink.Infra.DevLocal.DevLocalTransport());
            using var helper = new HelperPageViewModel(cancelAction: static () =>
            {
            }, transportConfig, runtime, openDiagnosticsAction: static () =>
            {
            }, clipboardService: new TestClipboardService(), shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));
            var view = new HelperPageView
            {
                DataContext = helper
            };
            var window = new Window
            {
                Width = 820,
                Height = 900,
                Content = view
            };
            try
            {
                window.Show();
                await FlushUiAsync();
                var installLink = await WaitForLayoutConditionAsync(window, () => FindFirstControlByAutomationId(window, "Helper.CopyInstallLink") as Button is { IsVisible: true } control ? control : null, TimeSpan.FromSeconds(2), "helper install link");
                var feedback = Assert.IsType<TextBlock>(FindFirstControlByAutomationId(window, "Helper.CopyInstallFeedback"));
                var installLinkY = installLink.Bounds.Y;
                await helper.CopyInstallMessageCommand.ExecuteAsync(null);
                await WaitUntilAsync(() => string.Equals(feedback.Text, "Copied. Paste it in your chat.", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
                await FlushUiAsync();
                Assert.Equal(installLinkY, installLink.Bounds.Y);
                Assert.Equal("Copied. Paste it in your chat.", feedback.Text);
            }
            finally
            {
                window.Close();
            }

            return true;
        }, CancellationToken.None);
    }

}
