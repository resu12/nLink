using System.Text.Json;
using System.Text.Json.Serialization;
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
        "docs/release/upgrade-0.1.0-beta.5-to-0.2.0-rc.1.md",
        "docs/address-native-connect.md",
        "docs/remote-control-p6-manual-qa.md",
        "docs/remote-control-quick-sanity-checklist.md",
        "docs/remote-control-sanity-checklist.md",
        "docs/soak/0.3.3-screenshare-soak.md"
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

    private static readonly string[] RequiredScreenShareOperatorFlows =
    [
        "Code-change validation",
        "Local stability soak",
        "Live NKN evidence",
        "Support/debug capture"
    ];

    private static readonly string[] RequiredScreenShareOpsModes =
    [
        "Test",
        "LocalSoak",
        "NknSoak",
        "AnalyzeRetained",
        "TrackBRetained",
        "SupportCapture"
    ];

    private static readonly string[] RetiredScreenShareUnimplementedPhrases =
    [
        "Screenshare UI is scaffolded only",
        "actual screenshare streaming is not implemented",
        "No WebRTC or screenshare streaming implementation",
        "No WebRTC or screenshare implementation"
    ];

    private static readonly string[] RequiredTrackDCloseoutPhrases =
    [
        "Track D Closeout State",
        @"tools\ScreenShare-Ops.ps1` is the only screenshare operator entry point",
        "`screenshare-operator-verdict.txt` is the first-read live evidence artifact",
        "App Diagnostics and Save Hang Report are the first support capture surfaces",
        "Retained Track B analyzers are preserved closeout evidence"
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
            .Where(directory =>
            {
                var name = Path.GetFileName(directory);
                return !string.IsNullOrWhiteSpace(name) &&
                    File.Exists(Path.Combine(directory, $"{name}.csproj"));
            })
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
    public void ScreenShareOperatorGuide_Exists_AndDefinesRequiredFlows()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var guidePath = Path.Combine(repoRoot, "docs", "screenshare-operability.md");

        Assert.True(File.Exists(guidePath), $"Expected screenshare operator guide: {guidePath}");

        var guideText = File.ReadAllText(guidePath);
        foreach (var flow in RequiredScreenShareOperatorFlows)
        {
            Assert.Contains(flow, guideText, StringComparison.Ordinal);
        }

        Assert.Contains(@"tools\ScreenShare-Ops.ps1", guideText, StringComparison.Ordinal);
        Assert.Contains("-Mode Test", guideText, StringComparison.Ordinal);
        Assert.Contains("-Mode LocalSoak", guideText, StringComparison.Ordinal);
        Assert.Contains("-Mode NknSoak", guideText, StringComparison.Ordinal);
        Assert.Contains("-Mode AnalyzeRetained", guideText, StringComparison.Ordinal);
        Assert.Contains("-Mode SupportCapture", guideText, StringComparison.Ordinal);
        Assert.Contains("screenshare-operator-verdict.txt", guideText, StringComparison.Ordinal);
        Assert.Contains(@"tools\Test-Lanes.ps1 -Lane ScreenShare", guideText, StringComparison.Ordinal);
        Assert.Contains("--screenshare-soak", guideText, StringComparison.Ordinal);
        Assert.Contains(@"tools\Run-ScreenShareNknSoak.ps1", guideText, StringComparison.Ordinal);
        Assert.Contains("Diagnostics -> Copy diagnostics", guideText, StringComparison.Ordinal);
        Assert.Contains("Diagnostics -> Save Hang Report", guideText, StringComparison.Ordinal);
        Assert.Contains("screenshare evidence", guideText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("screenshare-evidence.txt", guideText, StringComparison.Ordinal);
        Assert.Contains("steady_external_delivery_latency", guideText, StringComparison.Ordinal);
        foreach (var phrase in RequiredTrackDCloseoutPhrases)
        {
            Assert.Contains(phrase, guideText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ScreenShareOpsScript_Exists_AndContainsRequiredModes()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "ScreenShare-Ops.ps1");
        var orchestrationPath = Path.Combine(repoRoot, "tools", "ScreenShareOps", "AnalyzerOrchestration.ps1");
        var manifestPath = Path.Combine(repoRoot, "tools", "ScreenShareOps", "retained-analyzer-chain.json");

        Assert.True(File.Exists(scriptPath), $"Expected screenshare ops script: {scriptPath}");
        Assert.True(File.Exists(orchestrationPath), $"Expected screenshare analyzer orchestration module: {orchestrationPath}");
        Assert.True(File.Exists(manifestPath), $"Expected screenshare analyzer manifest: {manifestPath}");

        var scriptText = File.ReadAllText(scriptPath);
        var orchestrationText = File.ReadAllText(orchestrationPath);
        var expected = RequiredScreenShareOpsModes.OrderBy(mode => mode, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, ExtractPowerShellValidateSetValues(scriptText, "Mode"));
        Assert.Contains("Test-Lanes.ps1", scriptText, StringComparison.Ordinal);
        Assert.Contains("Run-ScreenShareNknSoak.ps1", scriptText, StringComparison.Ordinal);
        Assert.Contains("AnalyzerOrchestration.ps1", scriptText, StringComparison.Ordinal);
        Assert.Contains("Invoke-ScreenShareRetainedAnalyzerChain", scriptText, StringComparison.Ordinal);
        Assert.Contains("Write-ScreenShareOperatorVerdictReport", scriptText, StringComparison.Ordinal);
        Assert.Contains("screenshare-operator-verdict.txt", scriptText, StringComparison.Ordinal);
        Assert.Contains("retained-analyzer-chain.json", orchestrationText, StringComparison.Ordinal);
        Assert.Contains("Analyze-ScreenShareLatencyRegression.ps1", File.ReadAllText(manifestPath), StringComparison.Ordinal);
        Assert.Contains("Diagnostics -> Copy diagnostics", scriptText, StringComparison.Ordinal);
        Assert.Contains("Diagnostics -> Save Hang Report", scriptText, StringComparison.Ordinal);
        Assert.Contains("screenshare evidence summary", scriptText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("screenshare-evidence.txt", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenShareAnalyzerOrchestrationManifest_MatchesDocumentedRetainedChain()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var manifest = LoadScreenShareAnalyzerManifest(repoRoot);
        var docsPath = Path.Combine(repoRoot, "docs", "screenshare-soak.md");
        var docsText = File.ReadAllText(docsPath);
        var scripts = manifest.RetainedAnalyzers.Select(analyzer => analyzer.Script).ToArray();

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(10, scripts.Length);
        Assert.Equal(scripts.Length, scripts.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("steady_external_delivery_latency", manifest.ExternalTransportClassifications);
        foreach (var script in scripts)
        {
            Assert.True(File.Exists(Path.Combine(repoRoot, "tools", script)), $"Expected retained analyzer script: {script}");
            Assert.Contains(script, docsText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NknSoakRefactor_KeepsSingleOperatorFacingSoakCommand()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var opsScriptPath = Path.Combine(repoRoot, "tools", "ScreenShare-Ops.ps1");
        var soakFacadePath = Path.Combine(repoRoot, "tools", "Run-ScreenShareNknSoak.ps1");
        var soakImplementationRoot = Path.Combine(repoRoot, "tools", "ScreenShareSoak");

        var opsScriptText = File.ReadAllText(opsScriptPath);
        Assert.Contains("Run-ScreenShareNknSoak.ps1", opsScriptText, StringComparison.Ordinal);
        Assert.True(File.Exists(soakFacadePath), $"Expected NKN soak facade: {soakFacadePath}");
        Assert.True(Directory.Exists(soakImplementationRoot), $"Expected internal NKN soak implementation root: {soakImplementationRoot}");

        var docsAdvertisingImplementation = GetActiveDocsFiles(repoRoot)
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains(@"tools\ScreenShareSoak", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("tools/ScreenShareSoak", StringComparison.OrdinalIgnoreCase);
            })
            .Select(file => Path.GetRelativePath(repoRoot, file).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(docsAdvertisingImplementation);
    }

    [Fact]
    public void ScreenShareOps_IsTheOnlyRootScreenshareOperatorScript()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var toolsRoot = Path.Combine(repoRoot, "tools");
        var rootScreenshareScripts = Directory.GetFiles(toolsRoot, "ScreenShare-*.ps1", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ScreenShare-Ops.ps1"], rootScreenshareScripts);
    }

    [Fact]
    public void ScreenShareOperatorVerdict_IsDocumentedAsFirstReadArtifact()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var operatorGuidePath = Path.Combine(repoRoot, "docs", "screenshare-operability.md");
        var soakDocsPath = Path.Combine(repoRoot, "docs", "screenshare-soak.md");

        var operatorGuideText = File.ReadAllText(operatorGuidePath);
        var soakDocsText = File.ReadAllText(soakDocsPath);

        Assert.Contains("screenshare-operator-verdict.txt", operatorGuideText, StringComparison.Ordinal);
        Assert.Contains("screenshare-operator-verdict.txt", soakDocsText, StringComparison.Ordinal);
        Assert.Contains("first operator-facing artifact to read", soakDocsText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-Mode AnalyzeRetained", operatorGuideText, StringComparison.Ordinal);
        Assert.Contains("-Mode AnalyzeRetained", soakDocsText, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveDocs_DescribeDiagnosticsScreenshareEvidence()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var docsToCheck = new[]
        {
            Path.Combine(repoRoot, "README.md"),
            Path.Combine(repoRoot, "docs", "screenshare-operability.md"),
            Path.Combine(repoRoot, "docs", "screenshare-soak.md")
        };

        foreach (var path in docsToCheck)
        {
            var text = File.ReadAllText(path);
            Assert.Contains("screenshare evidence", text, StringComparison.OrdinalIgnoreCase);
        }

        var operatorGuideText = File.ReadAllText(docsToCheck[1]);
        var soakDocsText = File.ReadAllText(docsToCheck[2]);
        Assert.Contains("Diagnostics -> Copy diagnostics", operatorGuideText, StringComparison.Ordinal);
        Assert.Contains("Diagnostics -> Save Hang Report", operatorGuideText, StringComparison.Ordinal);
        Assert.Contains("screenshare-evidence.txt", operatorGuideText, StringComparison.Ordinal);
        Assert.Contains("screenshare-evidence.txt", soakDocsText, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveDocs_CrossReferenceTrackDCloseoutState()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var operatorGuideText = File.ReadAllText(Path.Combine(repoRoot, "docs", "screenshare-operability.md"));
        var soakDocsText = File.ReadAllText(Path.Combine(repoRoot, "docs", "screenshare-soak.md"));
        var protocolText = File.ReadAllText(Path.Combine(repoRoot, "docs", "screenshare-stabilization-protocol.md"));

        foreach (var phrase in RequiredTrackDCloseoutPhrases)
        {
            Assert.Contains(phrase, operatorGuideText, StringComparison.Ordinal);
        }

        Assert.Contains("Track D closeout state", soakDocsText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Track D is closed", protocolText, StringComparison.Ordinal);
        Assert.Contains(@"tools\ScreenShare-Ops.ps1", protocolText, StringComparison.Ordinal);
        Assert.Contains("screenshare-operator-verdict.txt", protocolText, StringComparison.Ordinal);
        Assert.Contains("app Diagnostics / Save Hang Report", protocolText, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_AdvertisesTestLanes_AndCurrentRcChecklist()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var readmePath = Path.Combine(repoRoot, "README.md");
        var readmeText = File.ReadAllText(readmePath);

        Assert.Contains("docs/README.md", readmeText, StringComparison.Ordinal);
        Assert.Contains("docs/supportability.md", readmeText, StringComparison.Ordinal);
        Assert.Contains(@"tools\Test-Lanes.ps1", readmeText, StringComparison.Ordinal);
        Assert.Contains(@"tools\ScreenShare-Ops.ps1", readmeText, StringComparison.Ordinal);
        Assert.Contains("docs/test-lanes.md", readmeText, StringComparison.Ordinal);
        Assert.Contains("docs/screenshare-operability.md", readmeText, StringComparison.Ordinal);
        Assert.Contains("docs/release/rc-validation-checklist.md", readmeText, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/release/0.3.0-rc-validation-checklist.md", readmeText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet test -c Release --filter Category=Smoke", readmeText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"tests\nLink.SmokeTests\nLink.SmokeTests.csproj", readmeText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tests/nLink.SmokeTests/nLink.SmokeTests.csproj", readmeText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocsIndexAndSupportabilityGuide_Exist_AndDescribeCurrentSupportPath()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var docsIndexPath = Path.Combine(repoRoot, "docs", "README.md");
        var supportabilityPath = Path.Combine(repoRoot, "docs", "supportability.md");

        Assert.True(File.Exists(docsIndexPath), $"Expected docs index: {docsIndexPath}");
        Assert.True(File.Exists(supportabilityPath), $"Expected supportability guide: {supportabilityPath}");

        var docsIndex = File.ReadAllText(docsIndexPath);
        Assert.Contains("docs/supportability.md", docsIndex, StringComparison.Ordinal);
        Assert.Contains("docs/screenshare-operability.md", docsIndex, StringComparison.Ordinal);
        Assert.Contains("docs/test-lanes.md", docsIndex, StringComparison.Ordinal);
        Assert.Contains("docs/ReleaseRunbook.md", docsIndex, StringComparison.Ordinal);
        Assert.Contains("docs/releases/**", docsIndex, StringComparison.Ordinal);

        var supportability = File.ReadAllText(supportabilityPath);
        Assert.Contains("Diagnostics", supportability, StringComparison.Ordinal);
        Assert.Contains("Copy diagnostics", supportability, StringComparison.Ordinal);
        Assert.Contains("Save Hang Report", supportability, StringComparison.Ordinal);
        Assert.Contains("screenshare-operator-verdict.txt", supportability, StringComparison.Ordinal);
        Assert.Contains(@"tools\ScreenShare-Ops.ps1", supportability, StringComparison.Ordinal);
        Assert.Contains("%LOCALAPPDATA%\\nLink\\logs", supportability, StringComparison.Ordinal);
        Assert.Contains("%LOCALAPPDATA%\\nLink\\artifacts\\hang", supportability, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveDocsAndTemplates_HaveResolvableLocalMarkdownLinks()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var missingLinks = GetActiveDocsAndTemplateFiles(repoRoot)
            .SelectMany(file => FindMissingLocalMarkdownLinks(repoRoot, file))
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missingLinks);
    }

    [Fact]
    public void DeletedStaleProcessDocs_DoNotExist()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var existingDeletedDocs = DeletedProcessDocReferences
            .Select(path => Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar)))
            .Where(File.Exists)
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(existingDeletedDocs);
    }

    [Fact]
    public void ActiveDocsAndTemplates_DoNotHardCodeStaleReleaseVersions()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var currentVersion = File.ReadAllText(Path.Combine(repoRoot, "VERSION")).Trim();
        var offenders = new List<string>();
        var versionPattern = new Regex(@"\b0\.\d+\.\d+(?:\.\d+)?(?:-[A-Za-z0-9.]+)?\b");

        foreach (var file in GetActiveDocsAndTemplateFiles(repoRoot))
        {
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            var lines = File.ReadLines(file).Select((line, index) => (Line: line, Number: index + 1));
            foreach (var (line, lineNumber) in lines)
            {
                foreach (Match match in versionPattern.Matches(line))
                {
                    var value = match.Value;
                    if (string.Equals(value, currentVersion, StringComparison.Ordinal) ||
                        value.StartsWith(currentVersion + "-", StringComparison.Ordinal) ||
                        IsAllowedImageVersionReference(line))
                    {
                        continue;
                    }

                    offenders.Add($"{relativePath}:{lineNumber}: {value}");
                }
            }
        }

        Assert.Empty(offenders.OrderBy(item => item, StringComparer.Ordinal));
    }

    [Fact]
    public void BugReportTemplates_RequestCurrentSupportEvidence()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var templatePaths = new[]
        {
            Path.Combine(repoRoot, ".github", "ISSUE_TEMPLATE", "bug_report.md"),
            Path.Combine(repoRoot, ".github", "ISSUE_TEMPLATE", "bug_report.yml")
        };
        var currentVersion = File.ReadAllText(Path.Combine(repoRoot, "VERSION")).Trim();

        foreach (var path in templatePaths)
        {
            var text = File.ReadAllText(path);
            Assert.Contains($"v{currentVersion}", text, StringComparison.Ordinal);
            Assert.Contains("Diagnostics", text, StringComparison.Ordinal);
            Assert.Contains("Copy diagnostics", text, StringComparison.Ordinal);
            Assert.Contains("Save Hang Report", text, StringComparison.Ordinal);
            Assert.Contains("screenshare evidence", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("screenshare-operator-verdict.txt", text, StringComparison.Ordinal);
            Assert.Contains("docs/supportability.md", text, StringComparison.Ordinal);
        }
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
    public void ActiveDocs_DoNotClaimScreenShareStreamingIsUnimplemented()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var offenders = GetActiveDocsFiles(repoRoot)
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return RetiredScreenShareUnimplementedPhrases.Any(phrase =>
                    text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
            })
            .Select(file => Path.GetRelativePath(repoRoot, file).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ActiveDocs_PresentRetainedTrackBAnalyzersOnlyAsCloseoutEvidence()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var offenders = GetActiveDocsFiles(repoRoot)
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                if (!Regex.IsMatch(text, @"Analyze-ScreenShare[A-Za-z0-9-]*\.ps1", RegexOptions.IgnoreCase))
                {
                    return false;
                }

                return !text.Contains("Retained Track B Closeout Evidence", StringComparison.Ordinal) ||
                    !text.Contains("closeout evidence", StringComparison.OrdinalIgnoreCase) ||
                    !text.Contains("not as the default invitation", StringComparison.OrdinalIgnoreCase);
            })
            .Select(file => Path.GetRelativePath(repoRoot, file).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void AppCode_DoesNotInvokeScreenShareAnalyzersOrLiveSoakTooling()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var srcRoot = Path.Combine(repoRoot, "src");
        var forbiddenReference = new Regex(
            @"Analyze-ScreenShare[A-Za-z0-9-]*\.ps1|Run-ScreenShareNknSoak\.ps1",
            RegexOptions.IgnoreCase);
        var sourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".axaml",
            ".csproj",
            ".json",
            ".ps1"
        };

        var offenders = Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories)
            .Where(file => sourceExtensions.Contains(Path.GetExtension(file)))
            .Where(file =>
            {
                var relativePath = Path.GetRelativePath(srcRoot, file).Replace('\\', '/');
                return !relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
                    !relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
            })
            .Where(file => forbiddenReference.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(repoRoot, file).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void AppDiagnostics_DoNotAttachFullScreenShareSoakArtifacts()
    {
        var repoRoot = CoreSmokeTestsBase.FindRepoRoot();
        var diagnosticsPaths = new[]
        {
            Path.Combine(repoRoot, "src", "nLink.App", "Services", "DiagnosticsPackBuilder.cs"),
            Path.Combine(repoRoot, "src", "nLink.App", "Services", "HangReportService.cs"),
            Path.Combine(repoRoot, "src", "nLink.App", "ViewModels", "DiagnosticsPageViewModel.cs")
        };
        var combinedText = string.Join(Environment.NewLine, diagnosticsPaths.Select(File.ReadAllText));

        Assert.Contains("screenshare-evidence.txt", combinedText, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Copy", combinedText, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateEntryFromFile", combinedText, StringComparison.Ordinal);
        Assert.DoesNotContain("helper-socket-receive-summary.txt", combinedText, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge-media-send-summary.txt", combinedText, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge-transport-health-summary.txt", combinedText, StringComparison.Ordinal);
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

    private static ScreenShareAnalyzerManifest LoadScreenShareAnalyzerManifest(string repoRoot)
    {
        var manifestPath = Path.Combine(repoRoot, "tools", "ScreenShareOps", "retained-analyzer-chain.json");
        var manifest = JsonSerializer.Deserialize<ScreenShareAnalyzerManifest>(File.ReadAllText(manifestPath));
        Assert.NotNull(manifest);
        Assert.NotNull(manifest.RetainedAnalyzers);
        Assert.NotNull(manifest.ExternalTransportClassifications);
        return manifest;
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

    private static string[] ExtractPowerShellValidateSetValues(string scriptText, string parameterName)
    {
        var pattern = @"(?s)\[ValidateSet\((?<body>.*?)\)\]\s*\[string\]\$" + Regex.Escape(parameterName) + @"\b";
        var match = Regex.Match(scriptText, pattern);
        Assert.True(match.Success, $"Could not find ValidateSet for ${parameterName}.");

        return Regex.Matches(match.Groups["body"].Value, @"[""'](?<value>[^""']+)[""']")
            .Select(match => match.Groups["value"].Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] FindMissingLocalMarkdownLinks(string repoRoot, string file)
    {
        var text = File.ReadAllText(file);
        var baseDirectory = Path.GetDirectoryName(file) ?? repoRoot;
        var missing = new List<string>();
        foreach (Match match in Regex.Matches(text, @"!?\[[^\]]+\]\((?<target>[^)]+)\)"))
        {
            var target = match.Groups["target"].Value.Trim();
            if (string.IsNullOrWhiteSpace(target) ||
                target.StartsWith("#", StringComparison.Ordinal) ||
                target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("app://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetWithoutAnchor = target.Split('#', 2)[0].Trim();
            if (string.IsNullOrWhiteSpace(targetWithoutAnchor))
            {
                continue;
            }

            var candidate = Path.IsPathRooted(targetWithoutAnchor)
                ? Path.Combine(repoRoot, targetWithoutAnchor.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : Path.Combine(baseDirectory, targetWithoutAnchor.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                missing.Add($"{relativePath} -> {target}");
            }
        }

        return missing.ToArray();
    }

    private static bool IsAllowedImageVersionReference(string line)
        => line.Contains("docs/images/", StringComparison.OrdinalIgnoreCase) &&
            line.Contains(".png", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> GetActiveDocsAndTemplateFiles(string repoRoot)
    {
        var readmePath = Path.Combine(repoRoot, "README.md");
        if (File.Exists(readmePath))
        {
            yield return readmePath;
        }

        var docsRoot = Path.Combine(repoRoot, "docs");
        if (Directory.Exists(docsRoot))
        {
            foreach (var file in Directory.GetFiles(docsRoot, "*.md", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                if (relativePath.StartsWith("docs/releases/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return file;
            }
        }

        var pullRequestTemplatePath = Path.Combine(repoRoot, ".github", "pull_request_template.md");
        if (File.Exists(pullRequestTemplatePath))
        {
            yield return pullRequestTemplatePath;
        }

        var issueTemplateRoot = Path.Combine(repoRoot, ".github", "ISSUE_TEMPLATE");
        if (Directory.Exists(issueTemplateRoot))
        {
            foreach (var file in Directory.GetFiles(issueTemplateRoot, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (extension is ".md" or ".yml" or ".yaml")
                {
                    yield return file;
                }
            }
        }
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

    private static IEnumerable<string> GetActiveDocsFiles(string repoRoot)
    {
        var readmePath = Path.Combine(repoRoot, "README.md");
        if (File.Exists(readmePath))
        {
            yield return readmePath;
        }

        var docsRoot = Path.Combine(repoRoot, "docs");
        if (!Directory.Exists(docsRoot))
        {
            yield break;
        }

        foreach (var file in Directory.GetFiles(docsRoot, "*.md", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (relativePath.StartsWith("docs/releases/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    private sealed class ScreenShareAnalyzerManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("retained_analyzers")]
        public ScreenShareAnalyzerEntry[] RetainedAnalyzers { get; init; } = [];

        [JsonPropertyName("external_transport_classifications")]
        public string[] ExternalTransportClassifications { get; init; } = [];
    }

    private sealed class ScreenShareAnalyzerEntry
    {
        [JsonPropertyName("script")]
        public string Script { get; init; } = "";
    }
}
