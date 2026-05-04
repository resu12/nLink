using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using NLink.App.ViewModels;
using NLink.App.Views;

namespace NLink.SmokeTests;

[Trait("Area", "Gui")]
public sealed class UiCommandBindingContractTests
{
    [Theory]
    [InlineData("HelpeePageView.axaml", typeof(HelpeePageViewModel))]
    [InlineData("HelperPageView.axaml", typeof(HelperPageViewModel))]
    public void PageCommandBindings_ResolveToViewModelCommands(string viewFileName, Type viewModelType)
    {
        var xaml = ReadViewXaml(viewFileName);
        var commandNames = ExtractSimpleBindingPaths(xaml, "Command")
            .Where(path => path.EndsWith("Command", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(commandNames);
        foreach (var commandName in commandNames)
        {
            var property = viewModelType.GetProperty(commandName, BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(property);
            Assert.True(
                typeof(ICommand).IsAssignableFrom(property!.PropertyType),
                $"{viewModelType.Name}.{commandName} must implement ICommand.");
        }
    }

    [Theory]
    [InlineData("HelpeePageView.axaml", typeof(HelpeePageViewModel), "IsEnabled")]
    [InlineData("HelpeePageView.axaml", typeof(HelpeePageViewModel), "IsVisible")]
    [InlineData("HelperPageView.axaml", typeof(HelperPageViewModel), "IsEnabled")]
    [InlineData("HelperPageView.axaml", typeof(HelperPageViewModel), "IsVisible")]
    public void PageBooleanBindings_ResolveToViewModelProperties(
        string viewFileName,
        Type viewModelType,
        string attributeName)
    {
        var xaml = ReadViewXaml(viewFileName);
        var propertyNames = ExtractSimpleBindingPaths(xaml, attributeName)
            .Where(IsPlainPropertyPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(propertyNames);
        foreach (var propertyName in propertyNames)
        {
            var property = viewModelType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(property);
            Assert.True(
                property!.PropertyType == typeof(bool),
                $"{viewModelType.Name}.{propertyName} must be a bool binding target.");
        }
    }

    [Fact]
    public void ChatView_RootCommandSurface_MatchesChatPanelBindings()
    {
        var xaml = ReadViewXaml("ChatView.axaml");

        Assert.Contains("Command=\"{Binding EndSessionCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SendFileButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SendChatButton_Click\"", xaml, StringComparison.Ordinal);

        AssertCommandProperty<IChatPanelBindings>(nameof(IChatPanelBindings.EndSessionCommand));
        AssertCommandProperty<IChatPanelBindings>(nameof(IChatPanelBindings.SendFileCommand));
        AssertCommandProperty<IChatPanelBindings>(nameof(IChatPanelBindings.SendChatCommand));
        AssertCommandProperty<IChatPanelBindings>(nameof(IChatPanelBindings.AcceptIncomingFileCommand));
        AssertCommandProperty<IChatPanelBindings>(nameof(IChatPanelBindings.DeclineIncomingFileCommand));
        AssertCommandProperty<IChatPanelBindings>(nameof(IChatPanelBindings.CancelFileTransferCommand));
        AssertCommandProperty<IChatPanelBindings>(nameof(IChatPanelBindings.PauseFileTransferCommand));
        AssertCommandProperty<IChatPanelBindings>(nameof(IChatPanelBindings.ResumeFileTransferCommand));
    }

    [Fact]
    public void ChatView_FileTransferCardBindings_ResolveToItemViewModelProperties()
    {
        var xaml = ReadViewXaml("ChatView.axaml");
        var expectedItemBindings = new[]
        {
            nameof(FileTransferPanelItemViewModel.ShowProgress),
            nameof(FileTransferPanelItemViewModel.ShowSavedLocation),
            nameof(FileTransferPanelItemViewModel.ShowRiskWarning),
            nameof(FileTransferPanelItemViewModel.RiskWarningText),
            nameof(FileTransferPanelItemViewModel.ShowActions),
            nameof(FileTransferPanelItemViewModel.ShowAccept),
            nameof(FileTransferPanelItemViewModel.ShowDecline),
            nameof(FileTransferPanelItemViewModel.ShowPause),
            nameof(FileTransferPanelItemViewModel.ShowResume),
            nameof(FileTransferPanelItemViewModel.ShowCancel),
            nameof(FileTransferPanelItemViewModel.TransferId),
        };

        foreach (var propertyName in expectedItemBindings)
        {
            Assert.Contains($"{{Binding {propertyName}}}", xaml, StringComparison.Ordinal);
            Assert.NotNull(typeof(FileTransferPanelItemViewModel).GetProperty(propertyName));
        }
    }

    [Fact]
    public void SessionHeader_CommandBindings_ResolveToCommandProperties()
    {
        var xaml = ReadViewXaml("SessionHeaderView.axaml");
        var commandNames = new[]
        {
            nameof(SessionHeaderView.EndSessionCommand),
            nameof(SessionHeaderView.ScreenShareCommand),
            nameof(SessionHeaderView.RequestControlCommand),
            nameof(SessionHeaderView.StopControlCommand),
            nameof(SessionHeaderView.ControlModeToggleCommand),
        };

        foreach (var commandName in commandNames)
        {
            Assert.Contains($"#Root.{commandName}", xaml, StringComparison.Ordinal);
            AssertCommandProperty<SessionHeaderView>(commandName);
        }
    }

    private static void AssertCommandProperty<T>(string propertyName)
    {
        var property = typeof(T).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        Assert.True(
            typeof(ICommand).IsAssignableFrom(property!.PropertyType),
            $"{typeof(T).Name}.{propertyName} must implement ICommand.");
    }

    private static IReadOnlyList<string> ExtractSimpleBindingPaths(string xaml, string attributeName)
    {
        var pattern = $@"\b{Regex.Escape(attributeName)}\s*=\s*""\{{Binding\s+([^}},\s]+)";
        return Regex.Matches(xaml, pattern)
            .Select(match => match.Groups[1].Value)
            .Where(path => !path.StartsWith("#Root.", StringComparison.Ordinal))
            .ToArray();
    }

    private static bool IsPlainPropertyPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               path.IndexOf('.') < 0 &&
               path.All(static c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static string ReadViewXaml(string viewFileName)
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "src", "nLink.App", "Views", viewFileName);
        Assert.True(File.Exists(path), $"View file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "nLink.App", "Views")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
