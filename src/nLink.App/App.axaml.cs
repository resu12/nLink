using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core.Logging;
using NLink.Core.Metrics;

namespace NLink.App;

public partial class App : Application
{
    public AppServiceRegistry Services { get; } = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            ConfigureAppServices();
            LocalOperationalLog.LogAppStart();
            var mainWindowViewModel = new MainWindowViewModel(Services);
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow
            {
                DataContext = mainWindowViewModel,
            };
            desktop.Exit += (_, _) => mainWindowViewModel.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureAppServices()
    {
        if (!Services.TryGet<IClipboardService>(out _))
        {
            var clipboard = new AvaloniaClipboardService();
            Services.AddSingleton<IClipboardService>(clipboard);
            Services.AddSingleton(clipboard);
        }

        if (!Services.TryGet<IQrCodeService>(out _))
        {
            Services.AddSingleton<IQrCodeService>(new QrCodeService());
        }

        if (!Services.TryGet<ICameraQrCaptureService>(out _))
        {
            Services.AddSingleton<ICameraQrCaptureService>(CameraQrCaptureServiceFactory.CreateDefault());
        }

        if (!Services.TryGet<IInviteShareService>(out _))
        {
            Services.AddSingleton<IInviteShareService>(new DefaultInviteShareService());
        }

        if (!Services.TryGet<IRecentConnectTargetsStore>(out _))
        {
            Services.AddSingleton<IRecentConnectTargetsStore>(new LocalRecentConnectTargetsStore());
        }

        if (!Services.TryGet<ShareMessageConfig>(out _))
        {
            Services.AddSingleton(ShareMessageConfig.Load());
        }

        if (!Services.TryGet<MetricsRegistry>(out _))
        {
            Services.AddSingleton(new MetricsRegistry());
        }

        if (!Services.TryGet<ResourceRuntimeTracker>(out _))
        {
            var tracker = new ResourceRuntimeTracker();
            tracker.Start();
            Services.AddSingleton(tracker);
        }
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
