param([string]$RepoRoot,[string]$Directory,[string]$Role,[string]$Signal,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$configuration=$env:NLINK_TEST_FRONTIER_WORKER_CONFIGURATION
if($configuration -cnotin @('Debug','Release')) {throw 'Unsupported frontier worker configuration'}
$corePath=Join-Path $RepoRoot "src/nLink.Core/bin/$configuration/net8.0/nLink.Core.dll"
$testsPath=Join-Path $RepoRoot "tests/nLink.SmokeTests.Core/bin/$configuration/net8.0/NLink.SmokeTests.Core.dll"
$core=[Reflection.Assembly]::LoadFrom($corePath)
$tests=[Reflection.Assembly]::LoadFrom($testsPath)
foreach($assembly in @($core,$tests)) {
    $attribute=@($assembly.GetCustomAttributes([Reflection.AssemblyConfigurationAttribute],$false))
    if($attribute.Count -ne 1 -or $attribute[0].Configuration -cne $configuration) {throw 'Frontier worker assembly configuration mismatch'}
}
"frontier-worker-configuration=$configuration; core_sha256=$((Get-FileHash -LiteralPath $corePath -Algorithm SHA256).Hash); tests_sha256=$((Get-FileHash -LiteralPath $testsPath -Algorithm SHA256).Hash)"
[void][NLink.SmokeTests.FrontierAttemptWritersTests]::RunAppAsync($Directory,$Role,$Signal,$Case).GetAwaiter().GetResult()
