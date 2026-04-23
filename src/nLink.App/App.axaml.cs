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
        AppStartupTelemetry.Mark("app_startup_app_initialize_started");
        AvaloniaXamlLoader.Load(this);
        AppStartupTelemetry.Mark("app_startup_app_initialize_completed");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppStartupTelemetry.Mark("app_startup_framework_initialization_entered");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            AppStartupTelemetry.Mark("app_startup_quality_migration_check_started");
            ScreenShareQualitySettings.ApplyStartupMigrationIfNeeded(persistUserEnvironment: false);
            AppStartupTelemetry.Mark("app_startup_quality_migration_check_completed");
            AppStartupTelemetry.Mark("app_startup_configure_services_started");
            ConfigureAppServices();
            AppStartupTelemetry.Mark("app_startup_configure_services_completed");
            LocalOperationalLog.LogAppStart();
            AppStartupTelemetry.Mark("app_startup_main_window_vm_ctor_started");
            var mainWindowViewModel = new MainWindowViewModel(Services);
            AppStartupTelemetry.Mark("app_startup_main_window_vm_ctor_completed");
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var mainWindow = new MainWindow();
            AppStartupTelemetry.Mark("app_startup_main_window_created");
            mainWindow.DataContext = mainWindowViewModel;
            desktop.MainWindow = mainWindow;
            AppStartupTelemetry.Mark("app_startup_main_window_assigned");
            ScreenShareQualitySettings.PersistPendingUserEnvironmentMigrationInBackground();
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
