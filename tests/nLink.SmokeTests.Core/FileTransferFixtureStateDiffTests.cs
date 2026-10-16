using System.Text.Json;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferFixtureStateDiffTests
{
    [Fact]
    public void StateDiff_ReportsNestedChangesAddedRemovedAndArrayValues()
    {
        using var before = JsonDocument.Parse("""{"Owner":{"Generation":1},"Items":[1,2],"Removed":true,"Unchanged":4}""");
        using var after = JsonDocument.Parse("""{"Owner":{"Generation":2},"Items":[1,3,4],"Added":null,"Unchanged":4}""");
        var diff = FileTransferFixtureStateDiff.Describe(before.RootElement, after.RootElement);
        Assert.Contains("$.Owner.Generation: 1 -> 2", diff);
        Assert.Contains("$.Items[1]: 2 -> 3", diff);
        Assert.Contains("$.Items[2]: <missing> -> 4", diff);
        Assert.Contains("$.Removed: true -> <missing>", diff);
        Assert.Contains("$.Added: <missing> -> null", diff);
        Assert.DoesNotContain("Unchanged", diff);
    }

    [Fact]
    public void StateDiff_IgnoresObjectPropertyOrder()
    {
        using var before = JsonDocument.Parse("""{"a":1,"b":null}""");
        using var after = JsonDocument.Parse("""{"b":null,"a":1}""");
        Assert.Equal(string.Empty, FileTransferFixtureStateDiff.Describe(before.RootElement, after.RootElement));
    }
}
