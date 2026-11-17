param([string]$RepoRoot, [string]$FixturePath, [switch]$CharacterizationOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
if (-not $CharacterizationOnly) {
    . (Join-Path $RepoRoot 'tools/FileTransferOps/GuiRejectedIngressShadow.ps1')
}
$tokens = $null; $errors = $null
$guiAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$errors)
if (@($errors).Count -ne 0) { throw 'GUI definitions failed to parse.' }
foreach ($name in @('Get-TunaGuiLogLineFieldValue', 'Get-TunaGuiFileTunaV4DataPathMismatchFailure')) {
    $definition = @($guiAst.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true))
    if ($definition.Count -eq 0) { throw "Missing GUI definition: $name" }
    Invoke-Expression $definition[0].Extent.Text
}

function Set-ShadowFixtureField {
    param([string]$Line, [string]$Name, [string]$Value)
    $pattern = '(?<=; )' + [regex]::Escape($Name) + '=[^;]*'
    if ([regex]::IsMatch($Line, $pattern)) { return [regex]::Replace($Line, $pattern, ($Name + '=' + $Value)) }
    return "$Line; $Name=$Value"
}
function Convert-ShadowFixtureEvents {
    param([string[]]$Lines)
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        ConvertFrom-FileTransferLogLine -Line $Lines[$index] -FilePath 'fixture.log' -LineNumber ($index + 1) -Sequence ($index + 1)
    }
}
function Get-ShadowFixtureDiagnostics {
    param([object[]]$Events, $Index)
    $seen = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($event in $Events) {
        if ($event.EventName -cne 'filetransfer_data_path_receive_result' -or
            -not (Test-FileTransferFileTunaV4DataPathActualEvent -Event $event) -or
            ((Get-FileTransferEventField $event 'actual_transport') -cne 'regular_nkn' -and
             (Get-FileTransferEventField $event 'delivery_transport') -cne 'regular_nkn')) { continue }
        if (-not $seen.Add(($event.RawLine -replace '; gui_evidence_sequence=\d+$', ''))) { continue }
        $diagnostic = Get-FileTransferFileTunaV4DataPathMismatchRejectedFailClosedResult -Mismatch $event -Events $Events -DataPathIndex $Index
        $record = [pscustomobject][ordered]@{
            LineNumber=[int]$event.LineNumber
            WriterId=Get-FileTransferEventField $event 'log_writer_id'
            WriterSequence=Get-FileTransferEventInt64Field $event 'log_writer_sequence'
            Recovered=[bool]$diagnostic.Recovered
            RejectionChainMatched=[bool]$diagnostic.RejectionChainMatched
            ReceiveDisposition=[string]$diagnostic.ReceiveDisposition
            Reason=[string]$diagnostic.Reason
            Variant=[string]$diagnostic.Variant
        }
        if ($null -ne $diagnostic.PSObject.Properties['Explanation']) {
            $record | Add-Member -NotePropertyName Explanation -NotePropertyValue $diagnostic.Explanation
        }
        $record
    }
}
function Convert-ShadowDiagnosticJson {
    param([object[]]$Records)
    return (ConvertTo-Json -InputObject @($Records | Select-Object LineNumber, WriterId, WriterSequence,
        Recovered, RejectionChainMatched, ReceiveDisposition, Reason, Variant, Explanation) -Depth 5 -Compress)
}

# Reuse the existing retained initial-activation fixture, not a historical live log.
$base = [IO.File]::ReadAllLines($FixturePath)
$source = @($base | Where-Object { $_ -match 'event=filetransfer_data_path_send_result;' })[0]
$transition = @($base | Where-Object { $_ -match 'event=filetransfer_route_transitioned; direction=inbound;' })[0]
$receive = @($base | Where-Object { $_ -match 'event=filetransfer_data_path_receive_result;' })[0]
$mismatch = @($base | Where-Object { $_ -match 'event=filetransfer_tuna_data_path_mismatch;' })[0]
$ignore = @($base | Where-Object { $_ -match 'event=filetransfer_data_frame_ignored;' })[0]
$terminal = $ignore -replace 'event=filetransfer_data_frame_ignored;', 'event=filetransfer_runtime_terminalized;'
$terminal = Set-ShadowFixtureField $terminal 'log_writer_sequence' '40'
$replacement = Set-ShadowFixtureField $transition 'log_writer_sequence' '30'
$replacement = Set-ShadowFixtureField $replacement 'route' 'post_tuna_fallback_v6'
$replacement = Set-ShadowFixtureField $replacement 'new_route' 'post_tuna_fallback_v6'
$replacement = Set-ShadowFixtureField $replacement 'protocol_version' '6'
$tunaSend = $source.Replace('route=regular_nkn_v4_fast', 'route=file_tuna_v4').Replace('transport=regular_nkn', 'transport=tuna')
$tunaSend = Set-ShadowFixtureField $tunaSend 'log_writer_sequence' '3'
foreach ($field in @('live_route_epoch', 'runtime_generation', 'terminal_generation', 'leg_generation', 'route_publication_generation', 'route_commit_generation')) {
    $tunaSend = Set-ShadowFixtureField $tunaSend $field (Get-TunaGuiLogLineFieldValue $receive $field)
}
$tunaReceive = $receive.Replace('transport=regular_nkn', 'transport=tuna')
$tunaReceive = Set-ShadowFixtureField $tunaReceive 'log_writer_sequence' '8'
$caseNames = @('initial', 'multiple_sources', 'source_after_transition', 'route_replaced', 'route_aba',
    'terminal_after_receive', 'terminal_after_mismatch', 'second_complete', 'second_missing_receive',
    'tuna_before_transition', 'tuna_other_publication', 'missing_source', 'missing_transition',
    'missing_mismatch', 'missing_ignore', 'conflicting_receive', 'exact_duplicate_receive',
    'unrelated_telemetry', 'empty', 'singleton')
$observations = [Collections.Generic.List[object]]::new()
if (-not $CharacterizationOnly) { $caseNames += 'second_missing_send' }
foreach ($case in $caseNames) {
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($original in $base) {
        if (($case -in @('missing_source', 'source_after_transition') -and $original -ceq $source) -or
            ($case -ceq 'missing_transition' -and $original -ceq $transition) -or
            ($case -ceq 'missing_mismatch' -and $original -ceq $mismatch) -or
            ($case -ceq 'missing_ignore' -and $original -ceq $ignore)) { continue }
        $line = $original
        if ($original -ceq $transition) {
            if ($case -ceq 'tuna_before_transition') { $lines.Add($tunaSend); $lines.Add($tunaReceive) }
            if ($case -in @('second_complete', 'second_missing_receive', 'second_missing_send', 'tuna_before_transition', 'tuna_other_publication')) {
                $line = $line.Replace('previous_route=regular_nkn_v4_fast', 'previous_route=post_tuna_fallback_v6')
                # A genuine positive receipt at sequence 8 follows publication.
                # The before-transition case still violates retained input order.
                $line = Set-ShadowFixtureField $line 'log_writer_sequence' '7'
            }
        }
        if ($original -ceq $receive) {
            if ($case -in @('route_replaced', 'route_aba')) { $lines.Add($replacement) }
            if ($case -ceq 'route_aba') { $lines.Add((Set-ShadowFixtureField $transition 'log_writer_sequence' '31')) }
            if ($case -eq 'source_after_transition') { $lines.Add($source) }
            if ($case -in @('second_complete', 'second_missing_receive', 'tuna_other_publication')) {
                $lines.Add($tunaSend)
                if ($case -ne 'second_missing_receive') {
                    $receipt = if ($case -eq 'tuna_other_publication') { Set-ShadowFixtureField $tunaReceive 'route_publication_generation' '88' } else { $tunaReceive }
                    $lines.Add($receipt)
                }
            }
        }
        $lines.Add($line)
        if ($original -ceq $source -and $case -ceq 'multiple_sources') { $lines.Add((Set-ShadowFixtureField $source 'log_writer_sequence' '4')) }
        if ($original -ceq $receive) {
            if ($case -ceq 'terminal_after_receive') { $lines.Add($terminal) }
            if ($case -ceq 'conflicting_receive') { $lines.Add((Set-ShadowFixtureField $receive 'raw_payload_bytes' '65535')) }
            if ($case -ceq 'exact_duplicate_receive') { $lines.Add($receive) }
        }
        if ($original -ceq $mismatch) {
            if ($case -ceq 'terminal_after_mismatch') { $lines.Add($terminal) }
            if ($case -ceq 'unrelated_telemetry') {
                for ($extra = 0; $extra -lt 12; $extra++) { $lines.Add("event=tuna_dialer_sidecar_stderr; marker=$extra") }
            }
        }
    }
    if ($case -ceq 'empty') { $lines.Clear() }
    if ($case -ceq 'singleton') { $lines.Clear(); $lines.Add($receive) }
    Reset-FileTransferRetainedEventIndex
    $events = @(Convert-ShadowFixtureEvents -Lines $lines.ToArray())
    $index = New-FileTransferFileTunaV4DataPathCorrelationIndex -Events $events
    $fullDiagnostics = @(Get-ShadowFixtureDiagnostics -Events $events -Index $index)
    $fullProof = Get-FileTransferFileTunaV4DataPathMismatchProof -Events $events -DataPathIndex $index
    $guiLines = @(for ($n = 0; $n -lt $lines.Count; $n++) { "$($lines[$n]); gui_evidence_sequence=$($n + 1)" })
    $primary = Get-TunaGuiFileTunaV4DataPathMismatchFailure -Lines $guiLines
    $observation = [pscustomobject][ordered]@{
        Case=$case; PrimaryQuartet=@($primary.RawCount, $primary.DistinctCount, $primary.RecoveredCount, $primary.UnresolvedCount)
        RetainedQuartet=@($fullProof.RawCandidateCount, $fullProof.DistinctCount, $fullProof.RecoveredCount, $fullProof.UnresolvedCount)
        Scalar=$fullProof.EffectiveCount; Diagnostics=@($fullDiagnostics | Select-Object * -ExcludeProperty Explanation)
    }
    # The added reporting case is not part of the immutable pre-extraction baseline.
    if ($case -cne 'second_missing_send') { $observations.Add($observation) }
    if ($CharacterizationOnly) { continue }

    # Regenerate compact input from its production catalog, retaining original locations.
    $retainedIndex = New-FileTransferRetainedEventIndex -Events $events -IncludeFieldStatistics $false
    $selected = @(foreach ($name in @(Get-FileTransferFileTunaV4DataPathEventNames)) {
        if ($name -notin @('filetransfer_route_transition_physical_send_lease_registered',
            'filetransfer_route_transition_physical_send_lease_retired', 'filetransfer_route_transition_physical_send_lease_rejected')) {
            Get-FileTransferCompactIndexEventsByName -Index $retainedIndex -Name $name
        }
    })
    Reset-FileTransferRetainedEventIndex
    $compactEvents = @(foreach ($event in @($selected | Sort-Object Sequence, LineNumber)) {
        ConvertFrom-FileTransferLogLine -Line $event.RawLine -FilePath $event.FilePath -LineNumber $event.LineNumber -Sequence $event.Sequence
    })
    $compactIndex = New-FileTransferFileTunaV4DataPathCorrelationIndex -Events $compactEvents
    $compactDiagnostics = @(Get-ShadowFixtureDiagnostics -Events $compactEvents -Index $compactIndex)
    $compactProof = Get-FileTransferFileTunaV4DataPathMismatchProof -Events $compactEvents -DataPathIndex $compactIndex
    $primaryBefore = $primary | ConvertTo-Json -Depth 6 -Compress
    $shadow = Get-TunaGuiRejectedIngressShadowDiagnostics -Lines $guiLines -PrimaryGuiDecision $primary
    if ($case -in @('second_missing_receive','second_missing_send')) {
        $kind = if ($case -ceq 'second_missing_send') { 'send' } else { 'receive' }
        if ($fullDiagnostics.Count -ne 1 -or
            $fullDiagnostics[0].Explanation -cnotlike "The rejection chain matched, but the required pre-rejection exact Tuna $kind was not found.*") {
            throw "${case}: missing reporting explanation"
        }
    }
    if (-not [object]::ReferenceEquals($primary, $shadow.PrimaryGuiDecision) -or
        ($primary | ConvertTo-Json -Depth 6 -Compress) -cne $primaryBefore) { throw "${case}: primary GUI decision changed" }
    $fullJson = Convert-ShadowDiagnosticJson $fullDiagnostics
    if ($fullJson -cne (Convert-ShadowDiagnosticJson $compactDiagnostics) -or
        $fullJson -cne (Convert-ShadowDiagnosticJson @($shadow.SharedDiagnostics))) {
        throw "${case}: full/compact/GUI shared per-receive diagnostics differ"
    }
    foreach ($field in @('RawCandidateCount', 'DistinctCount', 'RecoveredCount', 'UnresolvedCount', 'EffectiveCount')) {
        if ($fullProof.$field -ne $compactProof.$field) { throw "${case}: retained full/compact $field changed" }
    }
    if ($shadow.IndexBuildVisitCount -ne $shadow.RelevantEventCount -or $shadow.BoundedLookupVisitCount -lt 0) {
        throw "${case}: shadow index visit accounting changed"
    }
    $guiSelected = @($guiLines | Where-Object {
        (Get-TunaGuiLogLineFieldValue $_ 'event') -in @('filetransfer_data_path_send_result',
            'filetransfer_data_path_receive_result', 'filetransfer_tuna_data_path_mismatch',
            'filetransfer_data_frame_ignored', 'filetransfer_route_transitioned',
            'file_transfer_inbound_terminal', 'file_transfer_outbound_terminal', 'transfer_terminal',
            'filetransfer_runtime_terminalized')
    })
    $selectedShadow = Get-TunaGuiRejectedIngressShadowDiagnostics -Lines $guiSelected -PrimaryGuiDecision $primary
    if ($shadow.RelevantEventCount -ne $guiSelected.Count -or
        $fullJson -cne (Convert-ShadowDiagnosticJson @($selectedShadow.SharedDiagnostics)) -or
        -not [object]::ReferenceEquals($primary, $selectedShadow.PrimaryGuiDecision)) {
        throw "${case}: selected/unfiltered shadow changed original locations or membership"
    }
    if ($case -ceq 'initial') {
        foreach ($parserCase in @('uppercase_event_key', 'last_repeated_event_field')) {
            $parserLines = @($lines.ToArray() | ForEach-Object {
                if ($_ -ceq $receive) {
                    if ($parserCase -ceq 'uppercase_event_key') { $_.Replace('event=', 'EVENT=') }
                    else { $_.Replace('event=filetransfer_data_path_receive_result;', 'event=unrelated; event=filetransfer_data_path_receive_result;') }
                } else { $_ }
            })
            $parserEvents = @(Convert-ShadowFixtureEvents -Lines $parserLines)
            $parserIndex = New-FileTransferFileTunaV4DataPathCorrelationIndex -Events $parserEvents
            $parserExpected = @(Get-ShadowFixtureDiagnostics -Events $parserEvents -Index $parserIndex)
            $parserShadow = Get-TunaGuiRejectedIngressShadowDiagnostics -Lines $parserLines -PrimaryGuiDecision $primary
            if ($fullJson -cne (Convert-ShadowDiagnosticJson $parserExpected) -or
                $fullJson -cne (Convert-ShadowDiagnosticJson @($parserShadow.SharedDiagnostics)) -or
                -not [object]::ReferenceEquals($primary, $parserShadow.PrimaryGuiDecision)) {
                throw "${parserCase}: shadow selection differs from the existing parser"
            }
        }
    }
}
$normalized = ConvertTo-Json -InputObject $observations.ToArray() -Depth 8 -Compress
# Captured on PS5.1 and PS7 before the matcher extraction. Intentional primary/shared
# policy and count differences are pinned here; this is not a zero-difference gate.
$baselinePath = Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/GuiRejectedIngressShadowBaseline.json'
$baseline = [IO.File]::ReadAllText($baselinePath) | ConvertFrom-Json
# Preserve the historical characterization file. Only these two previously
# borrowed receipts change recovery under the reviewed binding correction;
# the independent GUI primary and every other field remain pinned.
foreach ($case in @('tuna_before_transition', 'tuna_other_publication')) {
    $entry = @($baseline | Where-Object Case -CEQ $case)
    if ($entry.Count -ne 1 -or $entry[0].Diagnostics.Count -ne 1 -or
        -not $entry[0].Diagnostics[0].Recovered -or $entry[0].Scalar -ne 0 -or
        ($entry[0].RetainedQuartet -join ',') -cne '1,1,1,0') { throw 'Historical receipt baseline changed.' }
    $entry[0].RetainedQuartet = @(1,1,0,1)
    $entry[0].Scalar = 2
    $entry[0].Diagnostics[0].Recovered = $false
    $entry[0].Diagnostics[0].Reason = 'missing_exact_tuna_receive'
    $entry[0].Diagnostics[0].Variant = 'second_tuna_rejected_regular_ingress_exact_tuna_receive_missing'
}
$expected = ConvertTo-Json -InputObject @($baseline) -Depth 8 -Compress
if ($normalized -cne $expected) { throw "Characterization changed beyond the reviewed receipt bindings: $normalized" }
Write-Output ('gui-rejected-ingress-shadow=' + $normalized)
