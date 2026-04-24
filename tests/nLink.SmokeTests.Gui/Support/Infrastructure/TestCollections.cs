namespace NLink.SmokeTests;

[CollectionDefinition(AvaloniaHeadlessUiCollection.Name, DisableParallelization = true)]
public sealed class AvaloniaHeadlessUiCollection
{
    public const string Name = "Avalonia Headless UI";
}

[CollectionDefinition(FakeNknNetworkCollection.Name, DisableParallelization = true)]
public sealed class FakeNknNetworkCollection
{
    public const string Name = "FakeNknNetwork";
}

[CollectionDefinition(GuiSmokeCollection.Name, DisableParallelization = true)]
public sealed class GuiSmokeCollection
{
    public const string Name = "GuiSmoke";
}
