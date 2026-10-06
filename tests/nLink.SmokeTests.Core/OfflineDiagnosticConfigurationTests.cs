using System.Text.Json;
using NLink.Core.Configuration;
using NLink.Tests;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class OfflineDiagnosticConfigurationTests : FileTransferOpsScriptsTestBase
{
#if !DEBUG
    [Theory]
    [InlineData("single_segment", false)] [InlineData("single_segment", true)]
    [InlineData("late_schedule", false)] [InlineData("late_schedule", true)]
    [InlineData("canonical_profile", false)] [InlineData("canonical_profile", true)]
    [InlineData("canonical_owner", false)] [InlineData("canonical_owner", true)]
    [InlineData("canonical_delay", false)] [InlineData("canonical_delay", true)]
    public void ReleaseRejectsDiagnosticProfilesEvenInDeveloperMode(string profile, bool developerMode)
    {
        var previous = Environment.GetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar);
        var batching = Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable);
        try
        {
            Environment.SetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar, developerMode ? "1" : null);
            Action create = profile switch
            {
                "single_segment" => () => _ = new AttemptSingleSegmentProfile(),
                "late_schedule" => () => _ = new AttemptOfflineFrameSchedule("s", "session", "transfer", CancellationToken.None, latePublication: true),
                "canonical_profile" => () => _ = new AttemptCanonicalTailProfile(268435456, 21504, 12484),
                "canonical_owner" => () => _ = new AttemptLatePublicationOwner(null!, null!, CancellationToken.None, canonicalTail: true),
                "canonical_delay" => () => _ = new AttemptOfflinePublicationDelay(null!, "s", "p", 0, 0, 0, CancellationToken.None, canonicalTail: true),
                _ => throw new ArgumentOutOfRangeException(nameof(profile))
            };
            Assert.Equal(profile.StartsWith("canonical_", StringComparison.Ordinal)
                    ? "canonical_tail_requires_debug" : "offline_schedule_requires_debug",
                Assert.Throws<InvalidOperationException>(create).Message);
            Assert.Equal(batching, Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar, previous);
            Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, batching);
        }
    }
#endif

    [Theory]
    [InlineData("pwsh", "Release", true, 0)] [InlineData("powershell", "Release", true, 0)]
    [InlineData("pwsh", "Release", false, 0)] [InlineData("powershell", "Release", false, 0)]
    [InlineData("pwsh", "Debug", true, 0)] [InlineData("powershell", "Debug", true, 0)]
    [InlineData("pwsh", "Debug", false, 0)] [InlineData("powershell", "Debug", false, 0)]
    [InlineData("pwsh", "Release", true, 1)] [InlineData("powershell", "Release", true, 1)]
    [InlineData("pwsh", "Release", true, 2)] [InlineData("powershell", "Release", true, 2)]
    public async Task CoreLane_SelectsBothConfigurationsAndStopsOnFailure(string host, string configuration, bool noBuild, int failAt)
    {
        const string script = """
            param([string]$Root, [string]$Configuration, [int]$NoBuild, [int]$FailAt)
            $ErrorActionPreference = 'Stop'
            $global:TestCalls = [Collections.Generic.List[object]]::new()
            $global:FailAt = $FailAt
            function global:dotnet {
                $global:TestCalls.Add(@($args))
                $global:LASTEXITCODE = if ($global:TestCalls.Count -eq $global:FailAt) { 1 } else { 0 }
            }
            $failed = $false
            try {
                & (Join-Path $Root 'tools/Test-Lanes.ps1') -Lane Core -Configuration $Configuration -NoBuild:([bool]$NoBuild) -Logger 'trx;LogFilePrefix=selection'
            }
            catch { $failed = $true }
            if ($failed -ne ($FailAt -gt 0)) { throw 'Unexpected lane exit status' }
            'SELECTION=' + (ConvertTo-Json -InputObject @($global:TestCalls.ToArray()) -Depth 4 -Compress)
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), configuration, noBuild ? "1" : "0", failAt.ToString()], host);
        Assert.True(result.ExitCode == 0, result.Stdout + Environment.NewLine + result.Stderr);
        var line = Assert.Single(result.Stdout.Split('\n').Where(value => value.StartsWith("SELECTION=", StringComparison.Ordinal)));
        using var json = JsonDocument.Parse(line["SELECTION=".Length..]);
        var calls = json.RootElement.EnumerateArray().Select(call => call.EnumerateArray().Select(value => value.GetString()!).ToArray()).ToArray();
        Assert.Equal(failAt == 1 || configuration == "Debug" ? 1 : 2, calls.Length);
        for (var i = 0; i < calls.Length; i++)
        {
            var args = calls[i];
            Assert.Equal("test", args[0]);
            Assert.EndsWith("nLink.SmokeTests.Core.csproj", args[1]);
            Assert.Equal(i == 0 ? configuration : "Debug", args[Array.IndexOf(args, "-c") + 1]);
            Assert.Equal(noBuild, args.Contains("--no-build"));
            if (configuration == "Release")
            {
                Assert.Equal(i == 0 ? "RequiredConfiguration!=Debug" : "RequiredConfiguration=Debug", args[Array.IndexOf(args, "--filter") + 1]);
                Assert.Contains(i == 0 ? "trx;LogFilePrefix=selection-core-release" : "trx;LogFilePrefix=selection-core-debug", args);
            }
            else
            {
                Assert.DoesNotContain("--filter", args);
                Assert.Contains("trx;LogFilePrefix=selection", args);
            }
        }
    }
}
