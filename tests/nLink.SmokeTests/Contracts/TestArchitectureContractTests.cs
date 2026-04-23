using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NLink.SmokeTests;

[Trait("Area", "Contracts")]
[Trait("Category", "ContractFreeze")]
public sealed class TestArchitectureContractTests
{
    private static readonly string[] AllowedAreas =
    [
        "Core",
        "ScreenShare",
        "RemoteControl",
        "Gui",
        "Contracts"
    ];

    private static readonly string[] RetiredMonolithPaths =
    [
        "tests/nLink.SmokeTests/SmokeTests.cs",
        "tests/nLink.SmokeTests/ScreenShareCoordinatorTests.cs",
        "tests/nLink.SmokeTests/ScreenShareTransportBoundaryTests.cs",
        "tests/nLink.SmokeTests/ScreenShareViewerViewModelTests.cs",
        "tests/nLink.SmokeTests/Core/SessionSecurityAndAuthorizationTests.cs",
        "tests/nLink.SmokeTests/Core/ChatAndFileTransferTests.cs",
        "tests/nLink.SmokeTests/Core/SessionRuntimeConnectionTests.cs",
        "tests/nLink.SmokeTests/Core/SessionFileTransferService.PullSessionTests.cs",
        "tests/nLink.SmokeTests/Gui/SessionHeaderAndBannerTests.cs",
        "tests/nLink.SmokeTests/Gui/Beta3DefaultUiSmokeTests.cs",
        "tests/nLink.SmokeTests/ScreenShare/TransportScreenShareCoordinatorRecoveryTests.cs",
        "tests/nLink.SmokeTests/ScreenShare/ScreenCaptureAbstractionTests.cs"
    ];

    [Fact]
    public void TestFiles_WithFactsOrTheories_HaveExactlyOneAllowedAreaTrait()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var testRoot = Path.Combine(repoRoot, "tests", "nLink.SmokeTests");
        var files = Directory.GetFiles(testRoot, "*.cs", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            if (!ContainsTestMethod(text))
            {
                continue;
            }

            var matches = Regex.Matches(text, "\\[Trait\\(\"Area\",\\s*\"(?<area>[^\"]+)\"\\)\\]");
            Assert.True(matches.Count == 1, $"Expected exactly one Area trait in {file}, found {matches.Count}.");

            var area = matches[0].Groups["area"].Value;
            Assert.Contains(area, AllowedAreas);
        }
    }

    [Fact]
    public void TestProjectRoot_DoesNotContainTestMethods()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var testRoot = Path.Combine(repoRoot, "tests", "nLink.SmokeTests");
        var rootFiles = Directory.GetFiles(testRoot, "*.cs", SearchOption.TopDirectoryOnly);

        foreach (var file in rootFiles)
        {
            var text = File.ReadAllText(file);
            Assert.False(
                ContainsTestMethod(text),
                $"Root-level test file detected: {file}");
        }
    }

    [Fact]
    public void PartialSmokeTests_DoesNotExist()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var testRoot = Path.Combine(repoRoot, "tests", "nLink.SmokeTests");
        var files = Directory.GetFiles(testRoot, "*.cs", SearchOption.AllDirectories);

        Assert.DoesNotContain(
            files,
            file => Regex.IsMatch(
                File.ReadAllText(file),
                @"^\s*(?:public|internal)?\s*partial\s+class\s+SmokeTests\b",
                RegexOptions.Multiline));
    }

    [Fact]
    public void RetiredPhaseOneAndPhaseOnePointFiveMonolithPaths_DoNotReappear()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();

        foreach (var relativePath in RetiredMonolithPaths)
        {
            var absolutePath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.False(File.Exists(absolutePath), $"Retired monolith path reappeared: {absolutePath}");
        }
    }

    [Fact]
    public void SharedHarnessProject_IsNotATestProject_AndContainsNoTestMethods()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "tests", "nLink.TestCommon", "nLink.TestCommon.csproj");
        var project = XDocument.Load(projectPath);

        var isTestProjectValue = project.Descendants("IsTestProject")
            .Select(element => element.Value.Trim())
            .FirstOrDefault();
        Assert.NotEqual("true", isTestProjectValue);

        var harnessRoot = Path.Combine(repoRoot, "tests", "nLink.TestCommon");
        var harnessFiles = Directory.GetFiles(harnessRoot, "*.cs", SearchOption.AllDirectories);
        foreach (var file in harnessFiles)
        {
            var text = File.ReadAllText(file);
            Assert.False(
                ContainsTestMethod(text),
                $"Shared harness file contains test methods: {file}");
        }
    }

    [Fact]
    public void CollectionDefinitions_RemainLocalToSmokeTestsAssembly()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var smokeRoot = Path.Combine(repoRoot, "tests", "nLink.SmokeTests");
        var harnessRoot = Path.Combine(repoRoot, "tests", "nLink.TestCommon");
        var expectedLocalCollectionsPath = Path.Combine(smokeRoot, "Support", "Infrastructure", "TestCollections.cs");

        var smokeCollectionFiles = Directory.GetFiles(smokeRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => Regex.IsMatch(
                File.ReadAllText(file),
                @"^\s*\[CollectionDefinition\(",
                RegexOptions.Multiline))
            .ToArray();
        var harnessCollectionFiles = Directory.GetFiles(harnessRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => Regex.IsMatch(
                File.ReadAllText(file),
                @"^\s*\[CollectionDefinition\(",
                RegexOptions.Multiline))
            .ToArray();

        Assert.Empty(harnessCollectionFiles);
        Assert.Equal([expectedLocalCollectionsPath], smokeCollectionFiles);
    }

    [Fact]
    public void SmokeTestsSupportTree_ContainsOnlyCollectionShell()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var supportRoot = Path.Combine(repoRoot, "tests", "nLink.SmokeTests", "Support");
        var supportFiles = Directory.GetFiles(supportRoot, "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["tests/nLink.SmokeTests/Support/Infrastructure/TestCollections.cs"],
            supportFiles);
    }

    private static bool ContainsTestMethod(string text)
    {
        return text.Contains("[Fact]", StringComparison.Ordinal)
            || text.Contains("[Theory]", StringComparison.Ordinal)
            || text.Contains("[ManualBridgeFact]", StringComparison.Ordinal)
            || text.Contains("[GuiSmokeFact]", StringComparison.Ordinal)
            || text.Contains("[MfDiagnosticFact]", StringComparison.Ordinal);
    }
}
