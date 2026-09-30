param([string]$RepoRoot, [ValidateSet('Semantic', 'Scaling')][string]$Mode, [string]$ReferencePath = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
if ($ReferencePath) { . $ReferencePath }
$scope = @{ log_writer_id = 'writer'; session_id = 'session'; transfer_id = 'transfer'; direction = 'inbound';
    route = 'post_tuna_fallback_v6'; protocol_version = '6'; live_route_epoch = '2'; leg_generation = '3';
    transport_epoch = '1'; bridge_recovery_generation = '1'; checkpoint_request_id = '(none)' }
$script:eventNameReads = 0
function Event([string]$Name, [int]$Sequence, [hashtable]$Extra = @{}) {
    $fields = $scope.Clone()
    foreach ($key in $Extra.Keys) { $fields[$key] = $Extra[$key] }
    $event = [pscustomobject]@{ NameForTest = $Name; Sequence = $Sequence; LineNumber = $Sequence; Fields = $fields }
    $event | Add-Member -MemberType ScriptProperty -Name EventName -Value { $script:eventNameReads++; return $this.NameForTest }
    return $event
}
$window = [pscustomobject]@{ PairingEvent = (Event 'pairing' 1); StartSequence = 1; EndSequence = [int]::MaxValue }
function Commit([hashtable]$Extra = @{}, [int]$Sequence = 100) {
    $fields = @{ next_chunk_index = '12'; contiguous_chunks_committed = '2' }
    foreach ($key in $Extra.Keys) { $fields[$key] = $Extra[$key] }
    return Event 'filetransfer_v6_sparse_write_committed' $Sequence $fields
}
function Publication([hashtable]$Extra = @{}, [int]$Sequence = 110) {
    $fields = @{ contiguous_committed_chunk_index = '12' }
    foreach ($key in $Extra.Keys) { $fields[$key] = $Extra[$key] }
    return Event 'filetransfer_v6_receiver_state_sent' $Sequence $fields
}
$natural = [pscustomobject]@{ PreviousFrontier = 10; CurrentFrontier = 12; Sequence = 100; AuthorityLinked = $false }
$linked = [pscustomobject]@{ PreviousFrontier = 10; CurrentFrontier = 12; Sequence = 100; AuthorityLinked = $true }
$script:cases = 0
function Assert-Case([string]$Name, [object[]]$Events, [object[]]$Expected) {
    $actual = @(Get-FileTransferEfficiencyFrontierProgressClosures -Window $window -WindowEvents $Events)
    $json = ConvertTo-Json -InputObject $actual -Depth 5 -Compress
    if ($json -cne (ConvertTo-Json -InputObject $Expected -Depth 5 -Compress)) { throw "closure changed: $Name : $json" }
    if ($ReferencePath) {
        $before = @(Get-FileTransferEfficiencyFrontierProgressClosuresBefore -Window $window -WindowEvents $Events)
        if ($json -cne (ConvertTo-Json -InputObject $before -Depth 5 -Compress)) { throw "reference parity failed: $Name" }
    }
    $script:cases++
}
if ($Mode -eq 'Semantic') {
    Assert-Case 'natural' @((Commit), (Publication)) @($natural)
    Assert-Case 'unordered input' @((Publication), (Commit)) @($natural)
    Assert-Case 'missing publication' @((Commit)) @()
    foreach ($sequence in @(99, 100)) { Assert-Case "not later $sequence" @((Commit), (Publication @{} $sequence)) @() }
    Assert-Case 'later sequence' @((Commit), (Publication @{} 1000)) @($natural)
    foreach ($field in @('log_writer_id', 'session_id', 'transfer_id', 'direction')) {
        foreach ($value in @('foreign', ([string]$scope[$field]).ToUpperInvariant())) {
            Assert-Case "wrong $field/$value" @((Commit), (Publication @{ $field = $value })) @()
        }
    }
    foreach ($direction in @('', '(none)', $null)) {
        Assert-Case 'direction absent allowed' @((Commit), (Publication @{ direction = $direction })) @($natural)
    }
    foreach ($value in @('0012', ' +12 ', '12')) {
        Assert-Case "parsed frontier $value" @((Commit), (Publication @{ contiguous_committed_chunk_index = $value })) @($natural)
    }
    foreach ($value in @('', $null, '-1', '11', '13', '12.0', '2147483648', 'not-a-number')) {
        Assert-Case "invalid frontier $value" @((Commit), (Publication @{ contiguous_committed_chunk_index = $value })) @()
    }
    $missing = Publication; $missing.Fields.Remove('contiguous_committed_chunk_index')
    Assert-Case 'missing numeric field' @((Commit), $missing) @()
    $missingRoot = Publication; $missingRoot.Fields.Remove('log_writer_id')
    Assert-Case 'missing root' @((Commit), $missingRoot) @()
    $nullFields = Publication; $nullFields.Fields = $null
    Assert-Case 'null fields' @((Commit), $nullFields) @()
    $upperName = Publication; $upperName.NameForTest = $upperName.NameForTest.ToUpperInvariant()
    Assert-Case 'event-name case unchanged' @((Commit), $upperName) @($natural)
    $publication = Publication
    Assert-Case 'duplicate publication reference' @((Commit), $publication, $publication) @($natural)
    Assert-Case 'duplicate publication values' @((Commit), (Publication), (Publication)) @($natural)
    Assert-Case 'foreign publication alongside exact' @((Commit), (Publication @{ log_writer_id = 'foreign' }), (Publication)) @($natural)
    Assert-Case 'duplicate sparse record' @((Commit), (Commit), (Publication)) @($natural)
    Assert-Case 'no committed advance' @((Commit @{ contiguous_chunks_committed = '0' }), (Publication)) @()
    Assert-Case 'invalid commit frontier' @((Commit @{ next_chunk_index = '2147483648' }), (Publication)) @()
    $max = [pscustomobject]@{ PreviousFrontier = 2147483646; CurrentFrontier = 2147483647; Sequence = 100; AuthorityLinked = $false }
    Assert-Case 'int maximum' @((Commit @{ next_chunk_index = '2147483647'; contiguous_chunks_committed = '1' }),
        (Publication @{ contiguous_committed_chunk_index = '2147483647' })) @($max)
    $identity = @{ authority_id = 'authority'; repair_generation = '1'; repair_request_key = 'repair'; request_correlation_id = 'correlation';
        settlement_outcome = 'ProgressObserved'; previous_frontier_chunk_index = '10'; current_frontier_chunk_index = '12' }
    $settlement = Event 'filetransfer_fallback_frontier_repair_contract_settled' 80 $identity
    $progress = Event 'filetransfer_fallback_frontier_repair_progress_observed' 90 $identity
    Assert-Case 'authority-linked without publication' @($settlement, $progress, (Commit)) @($linked)
    Assert-Case 'authority-linked takes precedence' @($settlement, $progress, (Commit), (Publication)) @($linked)
    Assert-Case 'ambiguous settlement no authority closure' @($settlement, $settlement, $progress, (Commit)) @()
    Assert-Case 'ambiguous settlement retains natural closure' @($settlement, $settlement, $progress, (Commit), (Publication)) @($natural)
    $second = [pscustomobject]@{ PreviousFrontier = 12; CurrentFrontier = 14; Sequence = 120; AuthorityLinked = $false }
    Assert-Case 'two frontier buckets ordered' @((Publication @{ contiguous_committed_chunk_index = '14' } 130),
        (Commit @{ next_chunk_index = '14' } 120), (Publication), (Commit)) @($natural, $second)
} else {
    foreach ($noiseCount in @(0, 512)) {
        $events = [Collections.Generic.List[object]]::new()
        for ($i = 1; $i -le 64; $i++) {
            $events.Add((Commit @{ next_chunk_index = [string]$i; contiguous_chunks_committed = '1' } ($i * 2)))
            $events.Add((Publication @{ contiguous_committed_chunk_index = [string]$i } ($i * 2 + 1)))
        }
        for ($i = 0; $i -lt $noiseCount; $i++) { $events.Add((Event 'unrelated' (1000 + $i))) }
        $script:eventNameReads = 0
        $actual = @(Get-FileTransferEfficiencyFrontierProgressClosures -Window $window -WindowEvents $events.ToArray())
        $reads = $script:eventNameReads
        if ($actual.Count -ne 64) { throw "scaling fixture lost closures: $($actual.Count)" }
        if ($reads -gt 5 * $events.Count) { throw "publication search rescanned the window: reads=$reads events=$($events.Count)" }
        Write-Host "publication-search-visits=$reads; events=$($events.Count); closures=$($actual.Count)"
        $script:cases++
    }
}
Write-Host "efficiency-publication-lookup-ok: mode=$Mode cases=$script:cases"
