param([string]$RepoRoot,[string]$OutputPath)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$parameters=[CodeDom.Compiler.CompilerParameters]::new()
$parameters.CompilerOptions='/define:NLINK_TEST_NATIVE_CONTENT_ACQUISITION'
[void]$parameters.ReferencedAssemblies.Add('System.dll')
$arguments=@{Path=(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeZeroWaitObservation.cs');ErrorAction='Stop'}
if($PSVersionTable.PSVersion.Major -lt 7) {$arguments.CompilerParameters=$parameters}
else {$arguments.CompilerOptions=$parameters.CompilerOptions}
Add-Type @arguments
$rows=[NLink.Tests.NativeZeroWaitObservation]::Run()
foreach($row in $rows) {
    if($row.ManagedMatches -ne 20000 -or $row.NativeMatches -ne 20000) {throw 'Zero-time observation result differs'}
}
$result=[ordered]@{DiagnosticOnly=$true;OwnedAuthorityInputs=$false;IterationsPerMode=20000;
    Timing=$rows;ManagedPendingInterrupt=[NLink.Tests.NativeZeroWaitObservation]::PendingInterrupt($false);
    NativePendingInterrupt=[NLink.Tests.NativeZeroWaitObservation]::PendingInterrupt($true);
    ProductionApiChanged=$false;StopwatchFrequency=[Diagnostics.Stopwatch]::Frequency}
[IO.File]::WriteAllText($OutputPath,($result | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
'native-zero-wait-observation=diagnostic-only'
