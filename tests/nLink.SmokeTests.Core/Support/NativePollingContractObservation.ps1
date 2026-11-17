param([string]$RepoRoot,[string]$OutputPath)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$parameters=[CodeDom.Compiler.CompilerParameters]::new()
$parameters.CompilerOptions='/define:NLINK_TEST_NATIVE_CONTENT_ACQUISITION'
[void]$parameters.ReferencedAssemblies.Add('System.dll')
$arguments=@{Path=(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativePollingContractObservation.cs');ErrorAction='Stop'}
if($PSVersionTable.PSVersion.Major -lt 7) {$arguments.CompilerParameters=$parameters}
else {$arguments.CompilerOptions=$parameters.CompilerOptions}
Add-Type @arguments
$rows=[NLink.Tests.NativePollingContractObservation]::Run()
if($rows.Count -ne 19) {throw 'Polling contract observation count differs'}
$result=[ordered]@{DiagnosticOnly=$true;OwnedAuthorityInputs=$false;
    HostVersion=$PSVersionTable.PSVersion.ToString();RuntimeVersion=[Environment]::Version.ToString();
    Observations=$rows;ProductionApiChanged=$false;BudgetsChanged=$false;AcceptanceChanged=$false}
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath))
[IO.File]::WriteAllText($OutputPath,($result | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
'native-polling-contract=diagnostic-only'
