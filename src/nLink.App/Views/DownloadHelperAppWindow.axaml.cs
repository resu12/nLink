using System;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NLink.App.Configuration;

namespace NLink.App.Views;

public partial class DownloadHelperAppWindow : Window
{
    private static readonly Uri NftpUri = new("https://nftp.nkn.org");
    private bool browserOpened;
    private bool shouldAutoOpenBrowser;

    public DownloadHelperAppWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        ConfigureContent();
    }

    private void ConfigureContent()
    {
        if (AppFeatureFlags.UseEmbeddedWebView && TryCreateEmbeddedWebView(out var webView))
        {
            WebContentHost.Content = webView;
            SurfaceBorder.IsVisible = true;
            FallbackOverlay.IsVisible = false;
            return;
        }

        WebContentHost.Content = null;
        SurfaceBorder.IsVisible = false;
        FallbackOverlay.IsVisible = true;

        if (!AppFeatureFlags.UseEmbeddedWebView)
        {
            FallbackMessageText.Text = "Built-in page view is off on this device. We will open your browser.";
        }
        else
        {
            FallbackMessageText.Text = "Built-in page view is not available in this build. We will open your browser.";
        }

        shouldAutoOpenBrowser = true;
    }

    private bool TryCreateEmbeddedWebView(out Control? webViewControl)
    {
        webViewControl = null;

        var candidateTypes = new[]
        {
            "Avalonia.Controls.NativeWebView, Avalonia.Controls.WebView",
            "Avalonia.Controls.WebView, Avalonia.Controls.WebView",
        };

        foreach (var typeName in candidateTypes)
        {
            var type = Type.GetType(typeName, throwOnError: false);
            if (type is null || !typeof(Control).IsAssignableFrom(type))
            {
                continue;
            }

            if (Activator.CreateInstance(type) is not Control control)
            {
                continue;
            }

            if (!TrySetSource(control, type))
            {
                continue;
            }

            webViewControl = control;
            return true;
        }

        return false;
    }

    private bool TrySetSource(Control control, Type type)
    {
        var sourceProperty = type.GetProperty("Source", BindingFlags.Public | BindingFlags.Instance);
        if (sourceProperty is null || !sourceProperty.CanWrite)
        {
            return false;
        }

        var propertyType = sourceProperty.PropertyType;

        if (propertyType == typeof(Uri) || propertyType.IsAssignableFrom(typeof(Uri)))
        {
            sourceProperty.SetValue(control, NftpUri);
            return true;
        }

        if (propertyType == typeof(string))
        {
            sourceProperty.SetValue(control, NftpUri.ToString());
            return true;
        }

        return false;
    }

    private void OpenInBrowser_Click(object? sender, RoutedEventArgs e)
    {
        TryOpenBrowser(force: true);
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (!shouldAutoOpenBrowser)
        {
            return;
        }

        shouldAutoOpenBrowser = false;
        Dispatcher.UIThread.Post(() => TryOpenBrowser());
    }

    private void TryOpenBrowser(bool force = false)
    {
        if (browserOpened && !force)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = NftpUri.ToString(),
                UseShellExecute = true,
            });

            browserOpened = true;
        }
        catch (Exception ex)
        {
            FallbackTitleText.Text = "Could not open your browser";
            FallbackMessageText.Text = "Please open https://nftp.nkn.org manually. " + ex.Message;
        }
    }
}

