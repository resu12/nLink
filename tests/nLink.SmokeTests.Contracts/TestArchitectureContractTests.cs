using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NLink.SmokeTests;

[Trait("Area", "Contracts")]
[Trait("Category", "ContractFreeze")]
public sealed class TestArchitectureContractTests
{
    private static readonly (string Area, string ProjectName)[] DomainProjects =
    [
        ("Contracts", "nLink.SmokeTests.Contracts"),
        ("Core", "nLink.SmokeTests.Core"),
        ("Gui", "nLink.SmokeTests.Gui"),
        ("RemoteControl", "nLink.SmokeTests.RemoteControl"),
        ("ScreenShare", "nLink.SmokeTests.ScreenShare")
    ];

    private static readonly string[] AllowedAreas = DomainProjects
        .Select(project => project.Area)
        .ToArray();

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
    public void DomainTestProjects_Exist_AndRetiredMonolithProjectDoesNot()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();

        foreach (var (_, projectName) in DomainProjects)
        {
            var projectPath = Path.Combine(repoRoot, "tests", projectName, $"{projectName}.csproj");
            Assert.True(File.Exists(projectPath), $"Expected domain test project: {projectPath}");
        }

        var retiredProjectPath = Path.Combine(repoRoot, "tests", "nLink.SmokeTests", "nLink.SmokeTests.csproj");
        Assert.False(File.Exists(retiredProjectPath), $"Retired monolith project still exists: {retiredProjectPath}");
    }

    [Fact]
    public void TestFiles_WithFactsOrTheories_HaveExactlyOneAllowedAreaTraitMatchingProject()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();

        foreach (var (area, projectName) in DomainProjects)
        {
            var projectRoot = Path.Combine(repoRoot, "tests", projectName);
            var files = Directory.GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                if (!ContainsTestMethod(text))
                {
                    continue;
                }

                var matches = Regex.Matches(text, "\\[Trait\\(\"Area\",\\s*\"(?<area>[^\"]+)\"\\)\\]");
                Assert.True(matches.Count == 1, $"Expected exactly one Area trait in {file}, found {matches.Count}.");

                var actualArea = matches[0].Groups["area"].Value;
                Assert.Contains(actualArea, AllowedAreas);
                Assert.Equal(area, actualArea);
            }
        }
    }

    [Fact]
    public void PartialSmokeTests_DoesNotExist()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var files = DomainProjects
            .SelectMany(project => Directory.GetFiles(
                Path.Combine(repoRoot, "tests", project.ProjectName),
                "*.cs",
                SearchOption.AllDirectories))
            .ToArray();

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
        Assert.Equal("false", isTestProjectValue);

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
    public void CollectionDefinitions_RemainProjectLocal_AndOutOfSharedHarness()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var harnessRoot = Path.Combine(repoRoot, "tests", "nLink.TestCommon");
        var expectedCollectionFiles = new[]
            {
                Path.Combine(repoRoot, "tests", "nLink.SmokeTests.Core", "Support", "Infrastructure", "TestCollections.cs"),
                Path.Combine(repoRoot, "tests", "nLink.SmokeTests.Gui", "Support", "Infrastructure", "TestCollections.cs"),
                Path.Combine(repoRoot, "tests", "nLink.SmokeTests.RemoteControl", "Support", "Infrastructure", "TestCollections.cs"),
                Path.Combine(repoRoot, "tests", "nLink.SmokeTests.ScreenShare", "Support", "Infrastructure", "TestCollections.cs")
            }
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var domainCollectionFiles = DomainProjects
            .SelectMany(project => Directory.GetFiles(
                Path.Combine(repoRoot, "tests", project.ProjectName),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(file => Regex.IsMatch(
                File.ReadAllText(file),
                @"^\s*\[CollectionDefinition\(",
                RegexOptions.Multiline))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var harnessCollectionFiles = Directory.GetFiles(harnessRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => Regex.IsMatch(
                File.ReadAllText(file),
                @"^\s*\[CollectionDefinition\(",
                RegexOptions.Multiline))
            .ToArray();

        Assert.Empty(harnessCollectionFiles);
        Assert.Equal(expectedCollectionFiles, domainCollectionFiles);
    }

    [Fact]
    public void EveryTestFile_LivesUnderExactlyOneDomainProject()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var testsRoot = Path.Combine(repoRoot, "tests");
        var domainRoots = DomainProjects
            .Select(project => Path.Combine(testsRoot, project.ProjectName))
            .ToArray();

        foreach (var file in Directory.GetFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            var text = File.ReadAllText(file);
            if (!ContainsTestMethod(text))
            {
                continue;
            }

            var owningProjectCount = domainRoots.Count(root => IsPathUnder(file, root));
            Assert.Equal(1, owningProjectCount);
            Assert.DoesNotContain("/nLink.SmokeTests/", relativePath);
            Assert.DoesNotContain("/nLink.TestCommon/", relativePath);
        }
    }

    private static bool ContainsTestMethod(string text)
    {
        return text.Contains("[Fact]", StringComparison.Ordinal)
            || text.Contains("[Theory]", StringComparison.Ordinal)
            || text.Contains("[ManualBridgeFact]", StringComparison.Ordinal)
            || text.Contains("[GuiSmokeFact]", StringComparison.Ordinal)
            || text.Contains("[MfDiagnosticFact]", StringComparison.Ordinal);
    }

    private static bool IsPathUnder(string file, string directory)
    {
        var relative = Path.GetRelativePath(directory, file);
        return relative != "."
            && !relative.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}
