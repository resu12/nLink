using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.App.Views;

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
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(Services),
            };
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

        if (!Services.TryGet<ShareMessageConfig>(out _))
        {
            Services.AddSingleton(ShareMessageConfig.Load());
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
