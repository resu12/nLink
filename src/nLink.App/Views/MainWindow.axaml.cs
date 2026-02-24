using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NLink.App.ViewModels;

namespace NLink.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? subscribedViewModel;
    private bool narwhalImageLoaded;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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
}
