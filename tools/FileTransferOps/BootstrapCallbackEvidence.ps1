Set-StrictMode -Version Latest

$script:FileTransferCoordinatorBootstrapCallbackCatalogVersion =
    'snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1-source-callback-v1'
$script:FileTransferCoordinatorBootstrapCallbackCatalogHash = '0ad879a93ec4d4639ecffd0fecf1c64d2aa32dc594bd39c680b8ca30c3c492dd'
$script:FileTransferCoordinatorBootstrapCallbackCatalog = [ordered]@{
    version=1; family='route_transition'; operationKind='FallbackBootstrapSourceCallback'
    operationRole='fallback_bootstrap_source_callback'; decisionKind='ObserveFallbackBootstrapSourceCallback'
    payloadName='bootstrapCallbacks'; stages=@('Capture','Admitted','Consumed')
    dispositions=@('Current','Held','Stale'); allowedEffects=@('none','progress','terminal')
    payloadFields=[ordered]@{
        stage='string'; callbackFingerprint='string'; leaseGeneration='int64'; sourceFingerprint='string'
        agreementFingerprint='string'; providerGeneration='int64'; sourcePauseInstance='int64'
        holdCommandFingerprint='string'; captureFingerprint='string'; capturedCallbacks='string[]'
        disposition='string'; allowedEffect='string'
    }
    history='admitted-exact-source-capture-consumed-once-v1'; physicalAuthority=$false
}

function Get-FileTransferCoordinatorBootstrapCallbackCatalog {
    $parent=Get-FileTransferCoordinatorBootstrapInitiationCatalog
    $json=ConvertTo-Json -InputObject $script:FileTransferCoordinatorBootstrapCallbackCatalog -Depth 16 -Compress
    $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.UTF8Encoding]::new($false).GetBytes(
        "parentHash=$($parent.Hash)`nBootstrapCallbackCatalog=$json"))
    if ($hash -cne $script:FileTransferCoordinatorBootstrapCallbackCatalogHash) { throw "callback_catalog_hash_invalid:$hash" }
    $result=[ordered]@{}
    foreach ($property in $parent.PSObject.Properties) { $result[$property.Name]=$property.Value }
    $result.Version=$script:FileTransferCoordinatorBootstrapCallbackCatalogVersion
    $result.Hash=$hash; $result.ParentCatalogHash=$parent.Hash
    $result.BootstrapCallbackCatalog=$script:FileTransferCoordinatorBootstrapCallbackCatalog
    [pscustomobject]$result
}

function Test-FileTransferCoordinatorBootstrapCallbackMarker {
    param($Operation)
    $null -ne $Operation -and (($Operation.PSObject.Properties.Name -contains 'operationRole' -and
        $Operation.operationRole -ceq 'fallback_bootstrap_source_callback') -or
        ($Operation.PSObject.Properties.Name -contains 'routeTransition' -and $null -ne $Operation.routeTransition -and
        $Operation.routeTransition.PSObject.Properties.Name -contains 'bootstrapCallbacks'))
}

function Test-FileTransferCoordinatorBootstrapCallbackOperation {
    param($Envelope,$Operation,$Catalog)
    try {
        if ($Catalog.Version -cnotin @($script:FileTransferCoordinatorBootstrapCallbackCatalogVersion, $script:FileTransferCoordinatorRepairOwnerCatalogVersion)) { return $false }
        $p=$Operation.routeTransition.bootstrapCallbacks
        $fields=$script:FileTransferCoordinatorBootstrapCallbackCatalog.payloadFields
        if (@($p.PSObject.Properties).Count -ne $fields.Count -or $Operation.family -cne 'route_transition') { return $false }
        foreach ($name in $fields.Keys) {
            if ($p.PSObject.Properties.Name -cnotcontains $name) { return $false }
            switch ($fields[$name]) {
                'string' { if ($p.$name -isnot [string]) { return $false } }
                'int64' { if ($p.$name -isnot [long] -and $p.$name -isnot [int]) { return $false } }
                'string[]' { if ($p.$name -isnot [array] -or @($p.$name | Where-Object { $_ -isnot [string] }).Count) { return $false } }
            }
        }
        foreach ($name in @('callbackFingerprint','sourceFingerprint','agreementFingerprint','holdCommandFingerprint','captureFingerprint')) {
            if ($p.$name -cnotmatch '^(?:[a-f0-9]{64}|fte1-[a-f0-9]{24}|\(none\))$') { return $false }
        }
        if (@($p.capturedCallbacks | Sort-Object -Unique).Count -ne $p.capturedCallbacks.Count -or
            @($p.capturedCallbacks | Where-Object { $_ -cnotmatch '^(?:[a-f0-9]{64}|fte1-[a-f0-9]{24})$' }).Count) { return $false }
        if ($Operation.operationRole -ceq 'fallback_bootstrap_source_hold') {
            $origin=$Operation.routeTransition.bootstrapInitiation
            return $p.stage -ceq 'Capture' -and $p.callbackFingerprint -ceq '(none)' -and $p.leaseGeneration -eq 0 -and
                $p.providerGeneration -eq 0 -and $p.sourcePauseInstance -eq 0 -and $p.disposition -ceq 'None' -and $p.allowedEffect -ceq 'none' -and
                $p.sourceFingerprint -ceq $origin.sourceFingerprint -and $p.agreementFingerprint -ceq $origin.agreementFingerprint -and
                $p.holdCommandFingerprint -ceq $origin.holdCommandFingerprint -and $p.captureFingerprint -ceq $origin.captureFingerprint -and
                ($Operation.afterState -cne 'Prepared' -or $p.capturedCallbacks.Count -eq 0) -and
                ($Envelope.direction -ceq 'outbound' -or $p.capturedCallbacks.Count -eq 0)
        }
        if ($Operation.operationRole -cne 'fallback_bootstrap_source_callback' -or $Operation.operationKind -cne 'FallbackBootstrapSourceCallback' -or
            $Envelope.decisionKind -cne 'ObserveFallbackBootstrapSourceCallback' -or $Operation.stateApplied -or $Operation.receiptValidated -or
            $Operation.routeTransition.role -cne $Operation.operationRole -or $p.stage -cnotin @('Admitted','Consumed') -or
            $p.callbackFingerprint -ceq '(none)' -or $p.sourceFingerprint -ceq '(none)' -or $p.agreementFingerprint -ceq '(none)' -or
            $p.leaseGeneration -le 0 -or $p.providerGeneration -le 0 -or $p.sourcePauseInstance -lt 0 -or $p.capturedCallbacks.Count -ne 0 -or
            $Operation.commandFingerprint -cne $p.callbackFingerprint -or $Operation.commandGeneration -ne $p.leaseGeneration -or
            $Operation.operationGeneration -ne $p.leaseGeneration -or $Operation.afterState -cne $p.stage -or
            $p.disposition -cnotin @('Current','Held','Stale') -or $p.allowedEffect -cnotin @('none','progress','terminal') -or
            ($p.disposition -cne 'Current' -and $p.allowedEffect -cne 'none') -or
            ($p.stage -ceq 'Admitted' -and ($p.disposition -cne 'Current' -or $p.allowedEffect -cne 'none' -or $Operation.beforeState -cne 'None')) -or
            ($p.stage -ceq 'Consumed' -and $Operation.beforeState -cne 'Admitted')) { return $false }
        $source=$Operation.routeTransition.sourceBoundary
        if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $source) -or $source.route -cne 'file_tuna_v4' -or $source.protocolVersion -ne 4) { return $false }
        if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $source $Operation.routeTransition.targetBoundary) -or
            $Operation.commandIdentity -cne $Operation.authorityId -or
            $Operation.operationKey -cne "route_transition|fallback_bootstrap_source_callback|$($Operation.authorityId)|$($p.leaseGeneration)|FallbackBootstrapSourceCallback" -or
            @($Operation.routeTransition.receipts).Count -ne 0 -or $Operation.receiptGeneration -ne 0 -or
            $Operation.receiptIdentity -cne '(none)' -or $Operation.receiptStatus -cne '(none)' -or $Operation.receiptFingerprint -cne '(none)' -or
            $Envelope.decisionStatus -cne 'Idempotent' -or
            $Envelope.aggregate.beforeStateFingerprint -cne $Envelope.aggregate.afterStateFingerprint) { return $false }
        if ($p.disposition -ceq 'Current' -and (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $source $Envelope.aggregate.beforeBoundary) -or
            $p.holdCommandFingerprint -cne '(none)' -or $p.captureFingerprint -cne '(none)')) { return $false }
        return $p.disposition -cne 'Held' -or ($p.holdCommandFingerprint -cne '(none)' -and $p.captureFingerprint -cne '(none)')
    } catch { return $false }
}

function Get-FileTransferBootstrapCallbackHistoryErrors {
    param([object[]]$Boundaries)
    $admitted=@{}; $consumed=@{}; $captures=@{}; $holdSets=@{}; $sourceBoundaries=@{}
    foreach ($boundary in $Boundaries) {
        foreach ($op in @($boundary.operations)) {
            if (-not (Test-FileTransferCoordinatorBootstrapCallbackMarker $op)) { continue }
            $p=$op.routeTransition.bootstrapCallbacks
            $prefix="$($boundary.coordinatorInstanceId)|"
            if ($p.stage -ceq 'Capture') {
                if ($op.afterState -cin @('Prepared','None')) { continue }
                $holdKey=$prefix+$p.holdCommandFingerprint
                $set=(@($p.capturedCallbacks | Sort-Object) -join ',')
                if ($holdSets.ContainsKey($holdKey)) {
                    if ($holdSets[$holdKey] -cne $set) { 'snapshot_bootstrap_callback_capture_changed'; return }
                } else {
                    $expected=@($admitted.Keys | Where-Object { $_.StartsWith($prefix,[StringComparison]::Ordinal) -and
                        -not $consumed.ContainsKey($_) -and $admitted[$_].sourceFingerprint -ceq $p.sourceFingerprint } |
                        ForEach-Object { $admitted[$_].callbackFingerprint } | Sort-Object) -join ','
                    if ($expected -cne $set) { 'snapshot_bootstrap_callback_capture_missing'; return }
                    $holdSets[$holdKey]=$set
                }
                foreach ($id in $p.capturedCallbacks) {
                    $key=$prefix+$id
                    if (-not $admitted.ContainsKey($key) -or ($consumed.ContainsKey($key) -and -not $captures.ContainsKey($key)) -or
                        $admitted[$key].sourceFingerprint -cne $p.sourceFingerprint -or $admitted[$key].agreementFingerprint -cne $p.agreementFingerprint) {
                        'snapshot_bootstrap_callback_capture_unowned'; return
                    }
                    $binding="$($p.holdCommandFingerprint)|$($p.captureFingerprint)"
                    if ($captures.ContainsKey($key) -and $captures[$key] -cne $binding) { 'snapshot_bootstrap_callback_capture_changed'; return }
                    $captures[$key]=$binding
                }
                continue
            }
            $key=$prefix+$p.callbackFingerprint
            if ($p.stage -ceq 'Admitted') {
                if ($admitted.ContainsKey($key)) { 'snapshot_bootstrap_callback_duplicate_admission'; return }
                $admitted[$key]=$p; $sourceBoundaries[$key]=$op.routeTransition.sourceBoundary; continue
            }
            if (-not $admitted.ContainsKey($key) -or $consumed.ContainsKey($key)) { 'snapshot_bootstrap_callback_consumption_unowned'; return }
            foreach ($field in @('leaseGeneration','sourceFingerprint','agreementFingerprint','providerGeneration','sourcePauseInstance')) {
                if ($p.$field -cne $admitted[$key].$field) { 'snapshot_bootstrap_callback_source_changed'; return }
            }
            if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $sourceBoundaries[$key] $op.routeTransition.sourceBoundary)) {
                'snapshot_bootstrap_callback_source_changed'; return
            }
            if ($captures.ContainsKey($key)) {
                if ($p.disposition -ceq 'Current' -or $p.allowedEffect -cne 'none' -or
                    $captures[$key] -cne "$($p.holdCommandFingerprint)|$($p.captureFingerprint)") { 'snapshot_bootstrap_callback_captured_effect_invalid'; return }
            } elseif ($p.disposition -ceq 'Held' -or $p.holdCommandFingerprint -cne '(none)' -or $p.captureFingerprint -cne '(none)') {
                'snapshot_bootstrap_callback_hold_missing'; return
            }
            $consumed[$key]=$true
        }
    }
    foreach ($key in $admitted.Keys) { if (-not $consumed.ContainsKey($key)) { 'snapshot_bootstrap_callback_consumption_missing'; return } }
}
