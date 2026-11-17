function Add-AttemptPayloadPrimaryProbeGuards([string]$Definition) {
    $bind=@'
try { . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryProbe.ps1') -Stage Bind }
catch {
    $script:primaryPayloadBindError=$_
    $bindDiagnostic='attempt-payload-primary-first-error; stage=bind; reason='+$_.Exception.Message
    [Console]::Error.WriteLine($bindDiagnostic)
    if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-fallback') {Write-FrontierPrimaryCheckpoint 'bind-error' $bindDiagnostic}
    throw
}
'@
    $anchor='# Only an admitted bounded private context retains these array references.'
    $Definition=Replace-AttemptSeam $Definition $anchor ($bind+"`n"+$anchor)
    $closed=@'
try { . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadPrimaryProbe.ps1') -Stage Closed }
catch {
    $cleanupDiagnostic='attempt-payload-primary-cleanup-error; reason='+$_.Exception.Message
    [Console]::Error.WriteLine($cleanupDiagnostic)
    if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-fallback') {Write-FrontierPrimaryCheckpoint 'cleanup-error' $cleanupDiagnostic}
    # The original Bind throw is already unwinding through this finally path.
    if($null -eq $script:primaryPayloadBindError) {throw}
}
'@
    $anchor='try { $private:nativePrimaryCustody.Dispose() } catch { }'
    return (Replace-AttemptSeam $Definition $anchor ($anchor+"`n"+$closed))
}
