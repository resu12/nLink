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
public sealed class Beta3MainWindowNavigationSmokeTests : Beta3DefaultUiSmokeTestBase
{
    public Beta3MainWindowNavigationSmokeTests(Beta3DefaultUiFixture fixture) : base(fixture) { }

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

}
