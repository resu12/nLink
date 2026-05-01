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
public sealed class Beta3ScreenShareShellSmokeTests : Beta3DefaultUiSmokeTestBase
{
    public Beta3ScreenShareShellSmokeTests(Beta3DefaultUiFixture fixture) : base(fixture) { }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePage_WhenScreenShareNotApproved_ShowsDisabledShareScreenButton()
    {
        await fixture.Session.Dispatch(async () =>
        {
            EnsureAppServices();
            using var context = await CreateConnectedSessionContextAsync(helpee => helpee.AllowIncomingScreenShareCapability = false);
            var view = new HelpeePageView
            {
                DataContext = context.Helpee
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
                var window = new Window
                {
                    Width = 1080,
                    Height = 760,
                    Content = shell
                };
                window.Show();
                try
                {
                    await FlushUiAsync();
                    var placeholder = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(x => string.Equals(x.Text, "Content", StringComparison.Ordinal));
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
                var window = new Window
                {
                    Width = 1080,
                    Height = 760,
                    Content = shell
                };
                window.Show();
                try
                {
                    await FlushUiAsync();
                    var placeholder = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(x => string.Equals(x.Text, "Content", StringComparison.Ordinal));
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
                var window = new Window
                {
                    Width = 760,
                    Height = 760,
                    Content = shell
                };
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
    public async Task SessionShell_WhenScreenShareVisibleInResponsiveWideLayout_KeepsStableChatPaneWidth()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var context = new MutableConnectedScreenShareShellContext
            {
                CanShowScreenShareAction = true,
                ShowScreenSharePreviewFrame = true,
                ScreenSharePreviewFrame = CreateTinyBitmap(),
            };
            var shell = new SessionShellView
            {
                Width = 1080,
                MainContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                },
                ChatContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Shell.Chat",
                },
                ShowScreenShareAction = true,
                DataContext = context,
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = shell
            };
            window.Show();
            try
            {
                await FlushUiAsync();
                Assert.True(shell.ShowResponsiveWideLayout);
                var chatPresenter = shell.FindControl<Control>("ResponsiveWideChatContentPresenter");
                var chatLayout = chatPresenter?.Parent as Border;
                Assert.NotNull(chatLayout);
                Assert.Equal(320d, chatLayout!.Width);
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
    public async Task SessionShell_ScreenShareRefresh_DoesNotDetachFocusedChatContent()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var context = new MutableConnectedScreenShareShellContext
            {
                HeaderStatusText = "Connected",
                ShowConnectedPanel = true,
                CanShowScreenShareAction = true,
                IsScreenSharingPreviewActive = true,
                ShowScreenSharePreviewFrame = true,
                ScreenSharePreviewFrame = CreateTinyBitmap(),
            };
            var chatInput = new TextBox
            {
                [AutomationProperties.AutomationIdProperty] = "Shell.Chat.Input",
            };
            var shell = new SessionShellView
            {
                Width = 1080,
                MainContent = new Border
                {
                    [AutomationProperties.AutomationIdProperty] = "Shell.Main",
                },
                ChatContent = chatInput,
                ShowScreenShareAction = true,
                DataContext = context,
            };
            var window = new Window
            {
                Width = 1080,
                Height = 760,
                Content = shell
            };
            window.Show();
            try
            {
                await WaitUntilAsync(() => shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));
                chatInput.Focus();
                chatInput.Text = "message while sharing";
                await FlushUiAsync();
                Assert.True(chatInput.IsFocused);

                context.IsScreenSharingPreviewActive = false;
                await FlushUiAsync();
                context.IsScreenSharingPreviewActive = true;
                await FlushUiAsync();

                Assert.Same(chatInput, FindFirstVisibleControlByAutomationId(window, "Shell.Chat.Input"));
                Assert.Equal("message while sharing", chatInput.Text);
                Assert.True(chatInput.IsFocused);
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
    public async Task SessionShell_WhenScreenShareVisibleInResponsiveNarrowLayout_KeepsStableChatPaneWidth()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var context = new MutableConnectedScreenShareShellContext
            {
                CanShowScreenShareAction = true,
                ShowScreenSharePreviewFrame = true,
                ScreenSharePreviewFrame = CreateTinyBitmap(),
            };
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
                ShowScreenShareAction = true,
                DataContext = context,
            };
            var window = new Window
            {
                Width = 760,
                Height = 760,
                Content = shell
            };
            window.Show();
            try
            {
                await FlushUiAsync();
                Assert.True(shell.ShowResponsiveNarrowLayout);
                var chatPresenter = shell.FindControl<Control>("ResponsiveNarrowChatContentPresenter");
                var chatLayout = chatPresenter?.Parent as Border;
                Assert.NotNull(chatLayout);
                Assert.Equal(420d, chatLayout!.Width);
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
                using var helper = new HelperPageViewModel(cancelAction: static () =>
                {
                }, transportConfig, runtime, openDiagnosticsAction: static () =>
                {
                }, clipboardService: new TestClipboardService(), shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));
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
                var window = new Window
                {
                    Width = 1080,
                    Height = 760,
                    Content = shell
                };
                window.Show();
                try
                {
                    await FlushUiAsync();
                    Assert.False(shell.ShowScreenSharePane);
                    InvokePrivate(helper, "OnScreenShareFrameCompleted", null, new NLink.Core.ScreenShare.ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", CreateTinyImageBytes()));
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
                var window = new Window
                {
                    Width = 1080,
                    Height = 760,
                    Content = shell
                };
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
                var window = new Window
                {
                    Width = 1400,
                    Height = 900,
                    Content = shell
                };
                window.Show();
                try
                {
                    await WaitUntilAsync(() => shell.ShowScreenSharePane, TimeSpan.FromSeconds(2));
                    var viewer = await WaitForLayoutConditionAsync(window, () => FindVisibleScreenShareViewer(window), TimeSpan.FromSeconds(2), "screen share viewer layout");
                    Assert.True(viewer.Bounds.Width > 800, $"Expected large-window screenshare viewer width > 800, got {viewer.Bounds.Width:N1}.");
                    Assert.True(viewer.Bounds.Height > 500, $"Expected large-window screenshare viewer height > 500, got {viewer.Bounds.Height:N1}.");
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
                var window = new Window
                {
                    Width = 1600,
                    Height = 900,
                    Content = shell
                };
                window.Show();
                try
                {
                    await WaitUntilAsync(() => shell.MainPaneHorizontalAlignment == HorizontalAlignment.Center, TimeSpan.FromSeconds(2));
                    Assert.Equal(HorizontalAlignment.Center, shell.MainPaneHorizontalAlignment);
                    Assert.Equal(1120d, shell.MainPaneMaxWidth);
                    context.ScreenSharePreviewFrame = previewFrame;
                    context.ShowScreenSharePreviewFrame = true;
                    await WaitUntilAsync(() => shell.MainPaneHorizontalAlignment == HorizontalAlignment.Stretch, TimeSpan.FromSeconds(2));
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
                using var helper = new HelperPageViewModel(cancelAction: static () =>
                {
                }, transportConfig, runtime, openDiagnosticsAction: static () =>
                {
                }, clipboardService: new TestClipboardService(), shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));
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
                var window = new Window
                {
                    Width = 1080,
                    Height = 760,
                    Content = shell
                };
                window.Show();
                try
                {
                    await FlushUiAsync();
                    InvokePrivate(helper, "OnScreenShareFrameCompleted", null, new NLink.Core.ScreenShare.ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", CreateTinyImageBytes()));
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
                var window = new Window
                {
                    Width = 760,
                    Height = 240,
                    Content = shell
                };
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
                var window = new Window
                {
                    Width = 760,
                    Height = 240,
                    Content = shell
                };
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

}
