using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NLink.App.ViewModels;

namespace NLink.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? subscribedViewModel;
    private bool narwhalImageLoaded;
    private bool closePrepared;
    private bool closePreparationInProgress;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnMainWindowClosing;
        KeyDown += OnMainWindowKeyDown;
        TryLoadNarwhalPeekImage();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
            subscribedViewModel = null;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            subscribedViewModel = vm;
            subscribedViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        }

        UpdateNarwhalVisibility();
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName) ||
            string.Equals(e.PropertyName, nameof(MainWindowViewModel.CurrentPage), StringComparison.Ordinal))
        {
            UpdateNarwhalVisibility();
        }
    }

    private void TryLoadNarwhalPeekImage()
    {
        try
        {
            var uri = new Uri("avares://nLink/Assets/narwhal.png");
            using var stream = AssetLoader.Open(uri);
            NarwhalPeekImage.Source = new Bitmap(stream);
            narwhalImageLoaded = true;
        }
        catch
        {
            narwhalImageLoaded = false;
        }

        UpdateNarwhalVisibility();
    }

    private void UpdateNarwhalVisibility()
    {
        var showOnHomePage = DataContext is MainWindowViewModel vm && vm.CurrentPage is HomePageViewModel;
        NarwhalPeekImage.IsVisible = narwhalImageLoaded && showOnHomePage;
    }

    private void OnMainWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.D)
        {
            return;
        }

        var modifiers = e.KeyModifiers;
        var hasCtrl = modifiers.HasFlag(KeyModifiers.Control);
        var hasShift = modifiers.HasFlag(KeyModifiers.Shift);
        if (!hasCtrl || !hasShift)
        {
            return;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            vm.ToggleDebugPanel();
            e.Handled = true;
        }
    }

    private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closePrepared || closePreparationInProgress)
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        closePreparationInProgress = true;
        e.Cancel = true;

        try
        {
            await vm.PrepareForWindowCloseAsync();
        }
        finally
        {
            closePreparationInProgress = false;
            closePrepared = true;
            Close();
        }
    }
}
