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

    private static readonly string[] DeletedProcessDocReferences =
    [
        "docs/performance/0.3.4-baseline.md",
        "docs/release/0.2.0-rc-readiness.md",
        "docs/release/0.2.0-rc.1-release-notes.md",
        "docs/release/0.2.0-rc.1.md",
        "docs/release/0.3.0-freeze.md",
        "docs/release/0.3.0-promotion.md",
        "docs/release/0.3.0-rc-validation-checklist.md",
        "docs/release/0.3.3-manual-validation-checklist.md",
        "docs/release/0.3.3-packaging-checklist.md",
        "docs/release/upgrade-0.1.0-beta.5-to-0.2.0-rc.1.md"
    ];

    private static readonly string[] RequiredTestLanes =
    [
        "Core",
        "Gui",
        "ScreenShare",
        "RemoteControl",
        "Contracts",
        "Smoke",
        "NonGui",
        "GuiSmoke",
        "ContractFreeze",
        "BridgeStabilityPromotion",
        "TrackBRetained",
        "All"
    ];

    [Fact]
    public void DomainTestProjects_Exist_AndRetiredMonolithProjectDoesNot()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var expectedProjectNames = DomainProjects
            .Select(project => project.ProjectName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        foreach (var (_, projectName) in DomainProjects)
        {
            var projectPath = Path.Combine(repoRoot, "tests", projectName, $"{projectName}.csproj");
            Assert.True(File.Exists(projectPath), $"Expected domain test project: {projectPath}");
        }

        var actualProjectNames = Directory.GetDirectories(Path.Combine(repoRoot, "tests"), "nLink.SmokeTests.*")
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedProjectNames, actualProjectNames);

        foreach (var (area, _) in DomainProjects)
        {
            Assert.Contains(area, RequiredTestLanes);
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

    [Fact]
    public void TestLaneScript_Exists_AndContainsRequiredLanes()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "Test-Lanes.ps1");
        var docsPath = Path.Combine(repoRoot, "docs", "test-lanes.md");

        Assert.True(File.Exists(scriptPath), $"Expected lane script: {scriptPath}");
        Assert.True(File.Exists(docsPath), $"Expected lane docs: {docsPath}");

        var scriptText = File.ReadAllText(scriptPath);
        var docsText = File.ReadAllText(docsPath);
        var expected = RequiredTestLanes.OrderBy(lane => lane, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, ExtractLaneScriptLanes(scriptText));
        Assert.Equal(expected, ExtractLaneDocLanes(docsText));
    }

    [Fact]
    public void Readme_AdvertisesTestLanes_AndCurrentRcChecklist()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var readmePath = Path.Combine(repoRoot, "README.md");
        var readmeText = File.ReadAllText(readmePath);

        Assert.Contains(@"tools\Test-Lanes.ps1", readmeText, StringComparison.Ordinal);
        Assert.Contains("docs/test-lanes.md", readmeText, StringComparison.Ordinal);
        Assert.Contains("docs/release/rc-validation-checklist.md", readmeText, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/release/0.3.0-rc-validation-checklist.md", readmeText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet test -c Release --filter Category=Smoke", readmeText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"tests\nLink.SmokeTests\nLink.SmokeTests.csproj", readmeText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tests/nLink.SmokeTests/nLink.SmokeTests.csproj", readmeText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActiveDocsScriptsAndCi_DoNotReferenceRetiredMonolithProject()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var retiredReferences = new[]
        {
            @"tests\nLink.SmokeTests\nLink.SmokeTests.csproj",
            "tests/nLink.SmokeTests/nLink.SmokeTests.csproj"
        };

        var offenders = GetActiveDocsScriptsAndCiFiles(repoRoot)
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return retiredReferences.Any(reference =>
                    text.Contains(reference, StringComparison.OrdinalIgnoreCase));
            })
            .Select(file => Path.GetRelativePath(repoRoot, file).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ActiveDocsScriptsCiAndIssueTemplates_DoNotLinkDeletedProcessDocs()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var offenders = GetActiveDocsScriptsAndCiFiles(repoRoot)
            .Where(file =>
            {
                var text = File.ReadAllText(file).Replace('\\', '/');
                return DeletedProcessDocReferences.Any(reference =>
                    text.Contains(reference, StringComparison.OrdinalIgnoreCase));
            })
            .Select(file => Path.GetRelativePath(repoRoot, file).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void CategoryPerformance_IsNotAdvertisedWithoutMatchingTests()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var testFiles = DomainProjects
            .SelectMany(project => Directory.GetFiles(
                Path.Combine(repoRoot, "tests", project.ProjectName),
                "*.cs",
                SearchOption.AllDirectories));
        var hasPerformanceTests = testFiles.Any(file =>
            File.ReadAllText(file).Contains("[Trait(\"Category\", \"Performance\")", StringComparison.Ordinal));

        if (hasPerformanceTests)
        {
            return;
        }

        var offenders = GetActiveDocsScriptsAndCiFiles(repoRoot)
            .Where(file => File.ReadAllText(file).Contains("Category=Performance", StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetRelativePath(repoRoot, file).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
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

    private static string[] ExtractLaneScriptLanes(string scriptText)
    {
        var match = Regex.Match(scriptText, @"(?s)\$validLanes\s*=\s*@\((?<body>.*?)\)");
        Assert.True(match.Success, "Could not find $validLanes in tools/Test-Lanes.ps1.");

        return Regex.Matches(match.Groups["body"].Value, "\"(?<lane>[^\"]+)\"")
            .Select(match => match.Groups["lane"].Value)
            .OrderBy(lane => lane, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] ExtractLaneDocLanes(string docsText)
    {
        return Regex.Matches(docsText, @"^\|\s*`(?<lane>[^`]+)`\s*\|", RegexOptions.Multiline)
            .Select(match => match.Groups["lane"].Value)
            .OrderBy(lane => lane, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> GetActiveDocsScriptsAndCiFiles(string repoRoot)
    {
        var readmePath = Path.Combine(repoRoot, "README.md");
        if (File.Exists(readmePath))
        {
            yield return readmePath;
        }

        var roots = new[]
        {
            Path.Combine(repoRoot, "docs"),
            Path.Combine(repoRoot, "tools"),
            Path.Combine(repoRoot, ".github", "workflows"),
            Path.Combine(repoRoot, ".github", "ISSUE_TEMPLATE")
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                if (relativePath.StartsWith("docs/releases/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (relativePath.StartsWith("tools/node/", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith("tools/nkn-bridge/node_modules/", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith("tools/nkn-bridge/.nlink-bundle/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var extension = Path.GetExtension(file);
                if (extension is ".md" or ".ps1" or ".yml" or ".yaml")
                {
                    yield return file;
                }
            }
        }
    }
}
