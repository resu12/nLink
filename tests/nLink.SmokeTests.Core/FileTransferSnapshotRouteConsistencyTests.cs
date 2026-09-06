namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Trait("OpsSuite", "RetainedRouteLaw")]
[Trait("RetainedAuthorityProofSuite", "FileTransferRouteTransition")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferSnapshotRouteConsistencyTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task SnapshotRouteConsistency_TwoDirectionFiveRouteCycleUsesExactPublications()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string script = """
            param([string]$RepoRoot)
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')

            function New-Scope(
                [string]$route,
                [long]$publication,
                [long]$commit,
                [long]$runtime,
                [long]$leg,
                [long]$epoch,
                [string]$dataSession) {
                $protocol = if ($route -ceq 'post_tuna_fallback_v6') { 6 } else { 4 }
                [pscustomobject][ordered]@{
                    route=$route; protocolVersion=$protocol; liveRouteEpoch=$epoch
                    routePublicationGeneration=$publication; routeCommitGeneration=$commit
                    runtimeGeneration=$runtime; terminalGeneration=0L; legGeneration=$leg
                    transportEpoch=0L; bridgeRecoveryGeneration=0L
                    checkpointGeneration=0L; checkpointRequestId='(none)'
                    dataSessionIdentity=$dataSession; dataSessionGeneration=1L
                }
            }
            function New-Operation(
                [string]$family,
                [string]$role,
                [string]$kind,
                [string]$authority,
                [long]$generation,
                [string]$before,
                [string]$after,
                $scope) {
                [pscustomobject][ordered]@{
                    family=$family; operationRole=$role
                    operationKey="$family|$role|$authority|$generation|$kind"
                    operationKind=$kind; authorityId=$authority
                    predecessorAuthorityId='(none)'; operationGeneration=$generation
                    beforeState=$before; afterState=$after
                    commandIdentity=$authority; commandGeneration=$generation
                    commandFingerprint="$authority-fingerprint"
                    receiptIdentity=if($role -ceq 'runtime_owner'){'(none)'}else{$authority}
                    receiptGeneration=if($role -ceq 'runtime_owner'){0L}else{$generation}
                    receiptStatus=if($role -ceq 'runtime_owner'){'(none)'}else{'Accepted'}
                    receiptFingerprint=if($role -ceq 'runtime_owner'){'(none)'}else{"$authority-fingerprint"}
                    receiptValidated=($role -cne 'runtime_owner'); stateApplied=$true
                    routeRuntime=if ($family -ceq 'route_runtime') {
                        [pscustomobject]@{ role=$role; boundary=$scope; tokenFingerprint="$authority-fingerprint" }
                    } else { $null }
                    routeTransition=$null
                }
            }
            function Get-TransitionKind([string]$source,[string]$target) {
                if ($source -ceq 'regular_nkn_v4_fast' -and $target -ceq 'file_tuna_v4') { return 'RegularV4ToTunaV4' }
                if ($source -ceq 'file_tuna_v4' -and $target -ceq 'post_tuna_fallback_v6') { return 'TunaV4ToPostTunaFallbackV6' }
                if ($source -ceq 'post_tuna_fallback_v6' -and $target -ceq 'file_tuna_v4') { return 'PostTunaFallbackV6ToTunaV4' }
                throw "unsupported fixture transition $source -> $target"
            }
            function Get-Transport([string]$route) {
                if ($route -ceq 'file_tuna_v4') { return 'Tuna' }
                return 'RegularNkn'
            }
            function Get-PublicationBoundary($evaluation,[string]$direction,[long]$generation) {
                $matches=@($evaluation.Boundaries | Where-Object {
                        $_.direction -ceq $direction -and
                        @($_.operations | Where-Object {
                                $_.family -ceq 'route_runtime' -and
                                $_.operationRole -ceq 'route_publication' -and
                                $_.beforeState -ceq 'None' -and
                                [long]$_.operationGeneration -eq $generation
                            }).Count -eq 1
                    })
                if ($matches.Count -ne 1) {
                    throw "fixture publication boundary is not unique: direction=$direction; generation=$generation; count=$($matches.Count)"
                }
                return $matches[0]
            }
            function Assert-StrictRouteFailure($evaluation,[string]$name,[string]$expectedBoundaryId) {
                $failure=Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $evaluation
                if ($null -eq $failure -or $failure.Verdict -cne 'fail' -or
                    @($failure.Findings).Count -eq 0) {
                    throw "$name passed: $($failure | ConvertTo-Json -Depth 16 -Compress)"
                }
                $first=$failure.FirstUnresolvedBoundary
                if ($null -eq $first -or [string]$first.boundaryId -cne $expectedBoundaryId -or
                    [string]$first.taxonomyClass -cne 'filetransfer_route_consistency_analysis_parity_invalid' -or
                    [string]$first.decisionReason -cne 'snapshot_authoritative_route_consistency_invalid' -or
                    [string]$first.variant -cne 'snapshot_authoritative_route_boundary_invalid' -or
                    [int]$first.rawCount -ne 1 -or [int]$first.distinctCount -ne 1 -or
                    [int]$first.recoveredCount -ne 0 -or [int]$first.unresolvedCount -ne 1) {
                    throw "$name did not expose the normalized unresolved boundary/quartet: $($failure | ConvertTo-Json -Depth 16 -Compress)"
                }
                $expectedBoundary=@($evaluation.Boundaries | Where-Object {
                        [string]$_.boundaryId -ceq $expectedBoundaryId
                    })[0]
                if ([string]$first.boundaryHash -cne [string]$expectedBoundary.boundaryHash -or
                    [string]$first.writer -cne [string]$expectedBoundary.writer -or
                    [long]$first.writerSequence -ne [long]$expectedBoundary.writerSequence -or
                    [long]$first.evidenceLineIndex -ne [long]$expectedBoundary.evidenceLineIndex -or
                    [string]$first.sessionId -cne [string]$expectedBoundary.sessionId -or
                    [string]$first.transferId -cne [string]$expectedBoundary.transferId -or
                    [string]$first.direction -cne [string]$expectedBoundary.direction) {
                    throw "$name unresolved-boundary location differs from its exact snapshot boundary"
                }
                $normalized=$failure.NormalizedJson | ConvertFrom-Json
                $normalizedFirst=$normalized.firstUnresolvedBoundary
                if ($null -eq $normalizedFirst -or
                    [string]$normalizedFirst.boundaryId -cne [string]$first.boundaryId -or
                    [string]$normalizedFirst.boundaryHash -cne [string]$first.boundaryHash -or
                    [int]$normalizedFirst.rawCount -ne 1 -or
                    [int]$normalizedFirst.distinctCount -ne 1 -or
                    [int]$normalizedFirst.recoveredCount -ne 0 -or
                    [int]$normalizedFirst.unresolvedCount -ne 1) {
                    throw "$name normalized JSON omitted the exact unresolved boundary/quartet"
                }
                $compact=$evaluation | ConvertTo-Json -Depth 32 | ConvertFrom-Json
                foreach ($boundary in $compact.Boundaries) {
                    $boundary.evidenceLineIndex += 1000L
                }
                $compactFailure=Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
                if ($compactFailure.Verdict -cne 'fail' -or
                    $compactFailure.NormalizedJson -cne $failure.NormalizedJson -or
                    $compactFailure.NormalizedHash -cne $failure.NormalizedHash -or
                    $compactFailure.FirstUnresolvedBoundary.evidenceLineIndex -ne
                        ($first.evidenceLineIndex + 1000L) -or
                    $null -ne $normalizedFirst.PSObject.Properties['evidenceLineIndex'] -or
                    (Compare-FileTransferCoordinatorSnapshotRouteConsistency -Full $failure -Compact $compactFailure).Verdict -cne 'pass') {
                    throw "$name input-local offsets changed semantic failure parity or lost diagnostic location"
                }
            }
            function New-Boundary(
                [string]$direction,
                [string]$writer,
                [long]$sequence,
                [long]$stateGeneration,
                $source,
                $target) {
                $instance="coordinator-$direction"
                $publicationAuthority=('fte1-{0:x24}' -f (1000L + $sequence))
                $operations=New-Object System.Collections.Generic.List[object]
                if ($null -eq $source) {
                    $operations.Add((New-Operation 'route_runtime' 'route_publication' 'RoutePublication' $publicationAuthority $target.routePublicationGeneration 'None' 'Published' $target))
                    $runtime=New-Operation 'route_runtime' 'runtime_owner' 'RuntimeOwner' ('fte1-{0:x24}' -f (5000L + $sequence)) $target.runtimeGeneration 'None' 'Current' $target
                    $runtime.receiptValidated=$false
                    $operations.Add($runtime)
                    $kind='RoutePublication'; $status='Accepted'; $reason='initial_route_publication'
                    $before=$target
                } else {
                    $transitionAuthority=('fte1-{0:x24}' -f (2000L + $sequence))
                    $commitAuthority=('fte1-{0:x24}' -f (3000L + $sequence))
                    $priorPublicationAuthority=('fte1-{0:x24}' -f (999L + $sequence))
                    $priorPublicationKind=if ([long]$source.routeCommitGeneration -eq 0L) { 'RoutePublication' } else { 'RouteCommitPublication' }
                    $operations.Add((New-Operation 'route_runtime' 'route_publication' $priorPublicationKind $priorPublicationAuthority $source.routePublicationGeneration 'Published' 'None' $source))
                    $retiredRuntime=New-Operation 'route_runtime' 'runtime_owner' 'RuntimeOwner' ('fte1-{0:x24}' -f (4999L + $sequence)) $source.runtimeGeneration 'Current' 'None' $source
                    $retiredRuntime.receiptValidated=$false
                    $operations.Add($retiredRuntime)
                    $currentRuntime=New-Operation 'route_runtime' 'runtime_owner' 'RuntimeOwner' ('fte1-{0:x24}' -f (5000L + $sequence)) $target.runtimeGeneration 'None' 'Current' $target
                    $currentRuntime.receiptValidated=$false
                    $operations.Add($currentRuntime)
                    if ([long]$source.routeCommitGeneration -gt 0L) {
                        $priorCommitAuthority=('fte1-{0:x24}' -f (2999L + $sequence))
                        $operations.Add((New-Operation 'route_runtime' 'route_commit' 'RouteCommit' $priorCommitAuthority $source.routeCommitGeneration 'Committed' 'None' $source))
                    }
                    if ($target.route -ceq 'post_tuna_fallback_v6') {
                        $publication=New-Operation 'route_runtime' 'route_publication' 'RoutePublication' $publicationAuthority $target.routePublicationGeneration 'None' 'Published' $target
                    } else {
                        $operations.Add((New-Operation 'route_runtime' 'route_commit' 'RouteCommit' $commitAuthority $target.routeCommitGeneration 'None' 'Committed' $target))
                        $publication=New-Operation 'route_runtime' 'route_publication' 'RouteCommitPublication' $publicationAuthority $target.routePublicationGeneration 'None' 'Published' $target
                        $publication.predecessorAuthorityId=$commitAuthority
                    }
                    $operations.Add($publication)
                    $transitionKind=Get-TransitionKind $source.route $target.route
                    $transitionGeneration=[long]$target.routePublicationGeneration - 1L
                    $transitionPayload=[pscustomobject][ordered]@{
                        role='terminal'; transitionKind=$transitionKind
                        sourceBoundary=$source; targetBoundary=$target
                        sourceTransport=(Get-Transport $source.route)
                        targetRoute=$target.route; targetProtocolVersion=$target.protocolVersion
                        targetTransport=(Get-Transport $target.route)
                        dispositionKind='Committed'
                    }
                    $currentTransition=New-Operation 'route_transition' 'current' $transitionKind $transitionAuthority $transitionGeneration 'Draining' 'None' $target
                    $currentTransition.receiptStatus='Committed'
                    $currentTransition.routeTransition=$transitionPayload | Select-Object *
                    $currentTransition.routeTransition.role='current'
                    $operations.Add($currentTransition)
                    $terminalTransition=New-Operation 'route_transition' 'terminal' $transitionKind $transitionAuthority $transitionGeneration 'None' 'Committed' $target
                    $terminalTransition.receiptStatus='Committed'
                    $terminalTransition.routeTransition=$transitionPayload
                    $operations.Add($terminalTransition)
                    $attempt=New-Operation 'route_transition' $direction 'CommitFileTransferRouteTransition' $transitionAuthority $transitionGeneration 'Current' 'Committed' $target
                    $attempt.receiptStatus='Committed'
                    $attempt.stateApplied=$false
                    $attempt.routeTransition=$transitionPayload | Select-Object *
                    $attempt.routeTransition.role=$direction
                    $operations.Add($attempt)
                    $kind='CommitFileTransferRouteTransition'; $status='Committed'; $reason='filetransfer_route_transition_committed'
                    $before=$source
                }
                [pscustomobject][ordered]@{
                    boundaryId="boundary-$direction-$sequence"; boundaryHash="hash-$direction-$sequence"
                    coordinatorInstanceId=$instance; writer=$writer; writerSequence=$sequence
                    evidenceLineIndex=($sequence - 1L)
                    decisionUtcMilliseconds=(100000L + $sequence)
                    beforeStateGeneration=($stateGeneration - 1L); afterStateGeneration=$stateGeneration
                    beforeStateHash="hash-$direction-$($stateGeneration - 1L)"; afterStateHash="hash-$direction-$stateGeneration"
                    decisionKind=$kind; decisionStatus=$status; decisionReason=$reason
                    verdict='pass'; taxonomyClass='(none)'; domain='route_runtime'; phase='route_transition'
                    sessionId='session'; transferId='transfer'; direction=$direction
                    beforeBoundary=$before; afterBoundary=$target; families=@('route_runtime','route_transition')
                    operations=$operations.ToArray()
                }
            }
            function New-FiveRouteCycle([string]$direction,[string]$writer,[long]$offset) {
                $routes=@('regular_nkn_v4_fast','file_tuna_v4','post_tuna_fallback_v6','file_tuna_v4','post_tuna_fallback_v6')
                $boundaries=New-Object System.Collections.Generic.List[object]
                $prior=$null
                for ($i=0; $i -lt $routes.Count; $i++) {
                    $commitGeneration=if ($routes[$i] -ceq 'post_tuna_fallback_v6') { 0L } else { [long]$i }
                    $scope=New-Scope $routes[$i] ($i+1L) $commitGeneration ($i+1L) ($i+1L) ($i+1L) "$direction-data-session"
                    $boundaries.Add((New-Boundary $direction $writer ($offset + $i + 1L) ($i+1L) $prior $scope))
                    $prior=$scope
                }
                return $boundaries.ToArray()
            }
            function Add-OwnerRefresh($evaluation,[string]$direction) {
                $previous=@($evaluation.Boundaries | Where-Object {$_.direction -ceq $direction -and $_.operations.Count -gt 0} |
                    Sort-Object afterStateGeneration)[-1]
                $aggregatePrevious=@($evaluation.Boundaries | Where-Object {$_.direction -ceq $direction -and
                    $null -ne $_.PSObject.Properties['afterBoundary']} | Sort-Object afterStateGeneration)[-1]
                $before=$aggregatePrevious.afterBoundary
                $after=$before | ConvertTo-Json | ConvertFrom-Json
                $after.routePublicationGeneration++;$after.runtimeGeneration++;$after.legGeneration++
                $sequence=[long]$aggregatePrevious.writerSequence+1L
                $pub=New-Operation 'route_runtime' 'route_publication' 'RoutePublication' ('fte1-{0:x24}' -f (1000L+$sequence)) $after.routePublicationGeneration 'None' 'Published' $after
                $owner=New-Operation 'route_runtime' 'runtime_owner' 'RuntimeOwner' ('fte1-{0:x24}' -f (5000L+$sequence)) $after.runtimeGeneration 'None' 'Current' $after
                $retired=@($previous.operations | Where-Object {$_.family -ceq 'route_runtime' -and $_.beforeState -ceq 'None'} |
                    ForEach-Object { $_ | ConvertTo-Json -Depth 16 | ConvertFrom-Json })
                foreach($op in $retired){$op.beforeState=$op.afterState;$op.afterState='None';$op.routeRuntime.boundary=$before}
                $attempt=New-Operation 'route_runtime' 'runtime_owner' 'StartTransfer' $owner.authorityId $owner.operationGeneration 'Attempted' 'Attempted' $before
                $attempt.stateApplied=$false
                $terminal=@($previous.operations | Where-Object {$_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal'})[0] |
                    ConvertTo-Json -Depth 16 | ConvertFrom-Json
                $terminal.beforeState='Committed';$terminal.afterState='Committed'
                $bootstrap=New-Operation 'route_transition' 'fallback_v6_bootstrap' 'FallbackV6ProofBootstrap' 'fte1-000000000000000000009000' 2L 'Delivered' 'Delivered' $after
                $boundary=[pscustomobject]@{
                    boundaryId="refresh-$direction-$sequence";boundaryHash="refresh-hash-$direction-$sequence"
                    coordinatorInstanceId=$previous.coordinatorInstanceId;writer=$previous.writer;writerSequence=$sequence
                    evidenceLineIndex=$sequence;decisionUtcMilliseconds=(100000L+$sequence)
                    beforeStateGeneration=$aggregatePrevious.afterStateGeneration;afterStateGeneration=($aggregatePrevious.afterStateGeneration+1L)
                    beforeStateHash=$aggregatePrevious.afterStateHash;afterStateHash="refresh-state-$sequence"
                    decisionKind='StartTransfer';decisionStatus='Accepted';decisionReason='state_refresh_received'
                    verdict='pass';taxonomyClass='(none)';domain='route_runtime';phase='route_transition'
                    sessionId=$previous.sessionId;transferId=$previous.transferId;direction=$direction
                    beforeBoundary=$before;afterBoundary=$after;families=@('route_runtime','route_transition')
                    operations=@($retired)+@($pub,$owner,$attempt,$terminal,$bootstrap)
                }
                $effect=[ordered]@{
                    CoordinatorInstanceId=$boundary.coordinatorInstanceId;Writer=$boundary.writer;TransitionId=$boundary.boundaryId
                    StateGeneration=$boundary.afterStateGeneration;Family='route_runtime';OperationKey=$pub.operationKey
                    AuthorityId=$pub.authorityId;OperationGeneration=$pub.operationGeneration;EffectKind='route_publication_forwarded'
                    EffectIdentity="effect-$sequence";ReceiptIdentity=$pub.receiptIdentity;ReceiptGeneration=$pub.receiptGeneration
                    ReceiptStatus=$pub.receiptStatus;ReceiptFingerprint=$pub.receiptFingerprint
                }
                foreach($field in $after.PSObject.Properties){$effect[$field.Name]=$field.Value}
                $evaluation.Boundaries+=@($boundary)
                $evaluation.__RoutePublicationEffects+=@([pscustomobject]$effect)
                return $boundary
            }

            $boundaries=@(New-FiveRouteCycle 'outbound' 'writer-sender' 0L) + @(New-FiveRouteCycle 'inbound' 'writer-helper' 100L)
            # A non-authority diagnostic with route=(none) must not become route evidence.
            $boundaries += [pscustomobject]@{
                boundaryId='diagnostic'; boundaryHash='diagnostic-hash'; coordinatorInstanceId='coordinator-outbound'
                writer='writer-sender'; writerSequence=200L; decisionUtcMilliseconds=200000L
                evidenceLineIndex=199L
                beforeStateGeneration=5L; afterStateGeneration=5L; decisionKind='InitialActivationHold'
                decisionStatus='Released'; decisionReason='not_blocked'; verdict='pass'
                sessionId='session'; transferId='transfer'; direction='outbound'; families=@('receive_recovery')
                operations=@()
            }
            $evaluation=[pscustomobject]@{
                Mode='SnapshotV1Authoritative'; ArtifactEvidenceVerdict='pass'; StreamComplete=$true
                AuthoritativeFamilies=@('route_runtime','route_transition'); Boundaries=$boundaries
            }
            $result=Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $evaluation
            if ($null -eq $result -or $result.Source -cne 'SnapshotAuthorityV1' -or
                $result.Verdict -cne 'pass' -or @($result.Findings).Count -ne 0 -or
                @($result.RouteSelectedEvents).Count -ne 10 -or
                @($result.SelectedRouteChanges).Count -ne 5 -or
                @($result.RouteAwareEvents).Count -ne 18 -or
                [string]::IsNullOrWhiteSpace([string]$result.NormalizedJson) -or
                [string]::IsNullOrWhiteSpace([string]$result.NormalizedHash)) {
                throw "exact five-route Snapshot V1 cycle failed: $($result | ConvertTo-Json -Depth 16 -Compress)"
            }
            $expected=@('regular_nkn_v4_fast','file_tuna_v4','post_tuna_fallback_v6','file_tuna_v4','post_tuna_fallback_v6')
            for ($i=0; $i -lt $expected.Count; $i++) {
                if ([string]$result.SelectedRouteChanges[$i] -cne $expected[$i]) {
                    throw "route cycle differs at $i"
                }
            }
            $clone=$evaluation | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $again=Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $clone
            if ($again.NormalizedHash -cne $result.NormalizedHash -or $again.NormalizedJson -cne $result.NormalizedJson) {
                throw 'independent full/compact route reduction was not byte-identical'
            }
            $parity=Compare-FileTransferCoordinatorSnapshotRouteConsistency -Full $result -Compact $again
            if ($parity.Verdict -cne 'pass' -or
                $parity.FullBoundaryHash -cne $result.BoundaryHash -or
                $parity.CompactBoundaryHash -cne $again.BoundaryHash) {
                throw "identical full/compact route results failed parity: $($parity | ConvertTo-Json -Compress)"
            }
            $refresh=$evaluation | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $refresh | Add-Member -NotePropertyName __RoutePublicationEffects -NotePropertyValue @()
            $firstRefresh=Add-OwnerRefresh $refresh 'inbound'
            $secondRefresh=Add-OwnerRefresh $refresh 'inbound'
            $refreshed=Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $refresh
            if($refreshed.Verdict -cne 'pass' -or $refreshed.RouteSelectedEvents.Count -ne 12 -or
                $refreshed.RouteAwareEvents.Count -ne 20 -or $refreshed.SelectedRouteChanges.Count -ne 5){
                throw "owner refresh rejected or publications hidden: $($refreshed.Findings -join ';')"
            }
            $bilateral=$refresh | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            [void](Add-OwnerRefresh $bilateral 'outbound')
            if((Get-FileTransferCoordinatorSnapshotRouteConsistency $bilateral).Verdict -cne 'pass'){
                throw 'outbound exact fallback owner successor rejected'
            }
            foreach($badKind in @('missing_effect','wrong_effect','duplicate_effect','missing_owner','wrong_receipt',
                    'wrong_attempt','wrong_scope','wrong_generation','missing_carry','wrong_prior_token','terminal_poison','authority_aba')) {
                $bad=$refresh | ConvertTo-Json -Depth 32 | ConvertFrom-Json
                $subject=@($bad.Boundaries | Where-Object {$_.boundaryId -ceq $firstRefresh.boundaryId})[0]
                switch($badKind){
                    'missing_effect' {$bad.__RoutePublicationEffects=@($bad.__RoutePublicationEffects | Where-Object {$_.TransitionId -cne $subject.boundaryId})}
                    'wrong_effect' {$bad.__RoutePublicationEffects[0].ReceiptFingerprint='wrong'}
                    'duplicate_effect' {$bad.__RoutePublicationEffects+=@($bad.__RoutePublicationEffects[0])}
                    'missing_owner' {$subject.operations=@($subject.operations | Where-Object {$_.operationKind -cne 'RuntimeOwner' -or $_.beforeState -cne 'Current'})}
                    'wrong_receipt' {@($subject.operations | Where-Object {$_.operationRole -ceq 'route_publication' -and $_.beforeState -ceq 'None'})[0].receiptFingerprint='wrong'}
                    'wrong_attempt' {@($subject.operations | Where-Object {$_.operationKind -ceq 'StartTransfer'})[0].stateApplied=$true}
                    'wrong_scope' {$subject.afterBoundary.dataSessionGeneration=2L}
                    'wrong_generation' {$subject.afterStateGeneration++}
                    'missing_carry' {$subject.operations=@($subject.operations | Where-Object {$_.operationRole -cne 'fallback_v6_bootstrap'})}
                    'wrong_prior_token' {@($subject.operations | Where-Object {$_.operationKind -ceq 'RuntimeOwner' -and $_.beforeState -ceq 'Current'})[0].commandFingerprint='mutated'}
                    'terminal_poison' {$subject.afterBoundary.terminalGeneration=1L}
                    'authority_aba' {
                        $old='fte1-0000000000000000000013ef'
                        foreach($op in @($subject.operations | Where-Object {$_.operationRole -ceq 'runtime_owner' -and $_.afterState -ne 'None'})){
                            $op.authorityId=$old;$op.commandIdentity=$old
                            $op.operationKey="route_runtime|runtime_owner|$old|$($op.operationGeneration)|$($op.operationKind)"
                        }
                    }
                }
                Assert-StrictRouteFailure $bad $badKind $subject.boundaryId
            }
            $later=$refresh | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            foreach($direction in @('outbound','inbound')) {
                $prior=if($direction -ceq 'outbound'){Get-PublicationBoundary $later $direction 5L}else{Get-PublicationBoundary $later $direction 7L}
                $s=$prior.afterBoundary
                $target=New-Scope 'file_tuna_v4' ($s.routePublicationGeneration+1L) 5L ($s.runtimeGeneration+1L) ($s.legGeneration+1L) 6L $s.dataSessionIdentity
                $next=New-Boundary $direction $prior.writer ($prior.writerSequence+1L) ($prior.afterStateGeneration+1L) $s $target
                foreach($op in @($next.operations | Where-Object {$_.family -ceq 'route_transition'})) {
                    $op.operationGeneration=5L
                    $op.operationKey="$($op.family)|$($op.operationRole)|$($op.authorityId)|5|$($op.operationKind)"
                    $op.commandGeneration=5L;$op.receiptGeneration=5L
                }
                $later.Boundaries+=@($next)
            }
            $afterRefresh=Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $later
            if($afterRefresh.Verdict -cne 'pass' -or $afterRefresh.SelectedRouteChanges.Count -ne 6){
                throw "independent publication/transition generations rejected: $($afterRefresh.Findings -join ';')"
            }
            $checkpointed=[pscustomobject]@{
                Mode='SnapshotV1Authoritative';ArtifactEvidenceVerdict='pass';StreamComplete=$true
                AuthoritativeFamilies=@('route_runtime','route_transition')
                Boundaries=@(New-FiveRouteCycle 'outbound' 'writer-sender' 0L)+@(New-FiveRouteCycle 'inbound' 'writer-helper' 100L)
                __RoutePublicationEffects=@()
            }
            $birth=Get-PublicationBoundary $checkpointed 'inbound' 5L
            $birth.afterBoundary.checkpointGeneration=1L;$birth.afterBoundary.checkpointRequestId='fte1-00000000000000000000c001'
            $acceptedScope=$birth.afterBoundary | ConvertTo-Json | ConvertFrom-Json
            $acceptedScope.checkpointGeneration=0L;$acceptedScope.checkpointRequestId='(none)'
            $accept=[pscustomobject]@{
                boundaryId='checkpoint-accepted';boundaryHash='checkpoint-hash';coordinatorInstanceId=$birth.coordinatorInstanceId
                writer=$birth.writer;writerSequence=106L;evidenceLineIndex=106L;decisionUtcMilliseconds=100106L
                beforeStateGeneration=5L;afterStateGeneration=6L;beforeStateHash=$birth.afterStateHash;afterStateHash='checkpoint-state-6'
                decisionKind='FallbackCheckpointAccepted';decisionStatus='Accepted';decisionReason='peer_current_state_superseded_checkpoint'
                sessionId=$birth.sessionId;transferId=$birth.transferId;direction=$birth.direction
                beforeBoundary=$birth.afterBoundary;afterBoundary=$acceptedScope;operations=@();families=@('route_runtime');verdict='pass'
            }
            $checkpointed.Boundaries+=@($accept)
            $checkpointRefresh=Add-OwnerRefresh $checkpointed 'inbound'
            $checkpointResult=Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $checkpointed
            if($checkpointResult.Verdict -cne 'pass'){throw "exact checkpoint interval rejected: $($checkpointResult.Findings -join ';')"}
            foreach($mutation in @('missing','hash_gap','source_mutation','untyped_scope_change','terminal_poison','token_retirement')){
                $bad=$checkpointed | ConvertTo-Json -Depth 32 | ConvertFrom-Json
                $step=@($bad.Boundaries|Where-Object {$_.boundaryId -ceq 'checkpoint-accepted'})[0]
                switch($mutation){
                    'missing' {$bad.Boundaries=@($bad.Boundaries|Where-Object {$_.boundaryId -cne 'checkpoint-accepted'})}
                    'hash_gap' {$step.beforeStateHash='wrong'}
                    'source_mutation' {$step.afterBoundary.dataSessionIdentity='other'}
                    'untyped_scope_change' {$step.decisionKind='RepairReplay'}
                    'terminal_poison' {$step.afterBoundary.terminalGeneration=1L}
                    'token_retirement' {$op=New-Operation 'route_runtime' 'route_publication' 'RoutePublication' 'fte1-000000000000000000000451' 5L 'Published' 'None' $step.beforeBoundary;$step.operations=@($op)}
                }
                Assert-StrictRouteFailure $bad "checkpoint interval $mutation" $checkpointRefresh.boundaryId
            }
            $different=$again | Select-Object *
            $different.NormalizedJson += ' '
            if ((Compare-FileTransferCoordinatorSnapshotRouteConsistency -Full $result -Compact $different).Verdict -cne 'fail') {
                throw 'different full/compact normalized route results passed parity'
            }
            $unbacked=$evaluation | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $unbackedBoundary=@($unbacked.Boundaries | Where-Object {
                    $_.direction -ceq 'outbound' -and
                    @($_.operations | Where-Object {
                            $_.family -ceq 'route_runtime' -and
                            $_.operationRole -ceq 'route_publication' -and
                            $_.operationGeneration -eq 2L -and
                            $_.beforeState -ceq 'None'
                        }).Count -eq 1
                })[0]
            $unbackedBoundary.operations=@($unbackedBoundary.operations | Where-Object {
                    $_.family -cne 'route_transition'
                })
            if ((Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $unbacked).Verdict -cne 'fail') {
                throw 'an unbacked mid-transfer route publication passed'
            }
            $wrongTransport=$evaluation | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $wrongTransition=@($wrongTransport.Boundaries | ForEach-Object { $_.operations } | Where-Object {
                    $_.family -ceq 'route_transition' -and
                    $_.routeTransition.targetRoute -ceq 'file_tuna_v4'
                })[0]
            $wrongTransition.routeTransition.targetTransport='RegularNkn'
            if ((Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $wrongTransport).Verdict -cne 'fail') {
                throw 'a route-transition target-transport mismatch passed'
            }
            $wrongScope=$evaluation | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $wrongPublication=@($wrongScope.Boundaries | ForEach-Object { $_.operations } | Where-Object {
                    $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_publication' -and
                    $_.beforeState -ceq 'None' -and $_.routeRuntime.boundary.route -ceq 'file_tuna_v4'
                })[0]
            $wrongPublication.routeRuntime.boundary.dataSessionGeneration=2L
            if ((Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $wrongScope).Verdict -cne 'fail') {
                throw 'a data-session-mutated publication passed'
            }

            $wrongLineage=$evaluation | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $wrongLineageBoundary=Get-PublicationBoundary $wrongLineage 'outbound' 3L
            $wrongLineageBoundary.writer='writer-injected'
            $wrongLineageBoundary.coordinatorInstanceId='coordinator-injected'
            Assert-StrictRouteFailure $wrongLineage 'a cross-writer/coordinator route lineage' $wrongLineageBoundary.boundaryId

            foreach ($mutation in @(
                    [pscustomobject]@{Name='retired publication transport';Selector='publication';Field='transportEpoch';Value=7L},
                    [pscustomobject]@{Name='retired runtime bridge';Selector='runtime_retirement';Field='bridgeRecoveryGeneration';Value=7L},
                    [pscustomobject]@{Name='retired runtime checkpoint';Selector='runtime_retirement';Field='checkpointGeneration';Value=7L},
                    [pscustomobject]@{Name='runtime entrant data session';Selector='runtime_entrant';Field='dataSessionGeneration';Value=7L}
                )) {
                $mutated=$evaluation | ConvertTo-Json -Depth 32 | ConvertFrom-Json
                $candidate=Get-PublicationBoundary $mutated 'outbound' 2L
                $subject=@($candidate.operations | Where-Object {
                        if ($mutation.Selector -ceq 'publication') {
                            return $_.family -ceq 'route_runtime' -and
                                $_.operationRole -ceq 'route_publication' -and
                                $_.beforeState -ceq 'Published' -and $_.afterState -ceq 'None'
                        }
                        if ($mutation.Selector -ceq 'runtime_retirement') {
                            return $_.family -ceq 'route_runtime' -and
                                $_.operationRole -ceq 'runtime_owner' -and
                                $_.beforeState -ceq 'Current' -and $_.afterState -ceq 'None'
                        }
                        return $_.family -ceq 'route_runtime' -and
                            $_.operationRole -ceq 'runtime_owner' -and
                            $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Current'
                    })
                if ($subject.Count -ne 1) {
                    throw "$($mutation.Name) fixture subject is not unique"
                }
                $subject[0].routeRuntime.boundary.($mutation.Field)=$mutation.Value
                if ($mutation.Field -ceq 'checkpointGeneration') {
                    $subject[0].routeRuntime.boundary.checkpointRequestId='fte1-000000000000000000000777'
                }
                Assert-StrictRouteFailure $mutated $mutation.Name $candidate.boundaryId
            }

            $missingCommit=$evaluation | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $missingCommitBoundary=Get-PublicationBoundary $missingCommit 'outbound' 2L
            $missingCommitBoundary.operations=@($missingCommitBoundary.operations | Where-Object {
                    $_.family -cne 'route_runtime' -or $_.operationRole -cne 'route_commit'
                })
            Assert-StrictRouteFailure $missingCommit 'a publication without its route commit' $missingCommitBoundary.boundaryId

            $missingRuntimeRetirement=$evaluation | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $missingRuntimeRetirementBoundary=Get-PublicationBoundary $missingRuntimeRetirement 'outbound' 2L
            $missingRuntimeRetirementBoundary.operations=@($missingRuntimeRetirementBoundary.operations | Where-Object {
                    $_.family -cne 'route_runtime' -or $_.operationRole -cne 'runtime_owner' -or
                    $_.beforeState -cne 'Current' -or $_.afterState -cne 'None'
                })
            Assert-StrictRouteFailure $missingRuntimeRetirement 'a publication without prior runtime retirement' $missingRuntimeRetirementBoundary.boundaryId

            $brokenPredecessor=$evaluation | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $brokenPredecessorBoundary=Get-PublicationBoundary $brokenPredecessor 'outbound' 2L
            $brokenPublication=@($brokenPredecessorBoundary.operations | Where-Object {
                    $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_publication' -and
                    $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Published'
                })[0]
            $brokenPublication.predecessorAuthorityId='fte1-00000000000000000000dead'
            Assert-StrictRouteFailure $brokenPredecessor 'a publication with the wrong route-commit predecessor' $brokenPredecessorBoundary.boundaryId

            $wrongTransitionKind=$evaluation | ConvertTo-Json -Depth 32 | ConvertFrom-Json
            $wrongTransitionBoundary=Get-PublicationBoundary $wrongTransitionKind 'outbound' 3L
            $wrongTransitionOperation=@($wrongTransitionBoundary.operations | Where-Object {
                    $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal'
                })[0]
            $wrongTransitionOperation.operationKind='RegularV4ToTunaV4'
            $wrongTransitionOperation.routeTransition.transitionKind='RegularV4ToTunaV4'
            $wrongTransitionOperation.operationKey="route_transition|terminal|$($wrongTransitionOperation.authorityId)|$($wrongTransitionOperation.operationGeneration)|RegularV4ToTunaV4"
            Assert-StrictRouteFailure $wrongTransitionKind 'a route transition with the wrong typed kind' $wrongTransitionBoundary.boundaryId
            'snapshot-route-cycle-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()]);
        Assert.True(
            result.ExitCode == 0,
            $"Snapshot route-cycle reducer failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
        Assert.Contains("snapshot-route-cycle-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotRouteConsistency_InvalidRouteAndMalformedV1FailClosedWithoutLegacyFallback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string script = """
            param([string]$RepoRoot)
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')

            function New-Evaluation([string]$route,[int]$protocol,[string]$verdict='pass') {
                $scope=[pscustomobject]@{
                    route=$route; protocolVersion=$protocol; liveRouteEpoch=1L
                    routePublicationGeneration=1L; routeCommitGeneration=0L
                    runtimeGeneration=1L; terminalGeneration=0L; legGeneration=1L
                    transportEpoch=0L; bridgeRecoveryGeneration=0L; checkpointGeneration=0L
                    checkpointRequestId='(none)'; dataSessionIdentity='data-session'; dataSessionGeneration=1L
                }
                $operation=[pscustomobject]@{
                    family='route_runtime'; operationRole='route_publication'; operationKind='RoutePublication'
                    operationKey='route_runtime|route_publication|fte1-000000000000000000000001|1|RoutePublication'
                    authorityId='fte1-000000000000000000000001'; predecessorAuthorityId='(none)'; operationGeneration=1L
                    beforeState='None'; afterState='Published'; receiptValidated=$true; stateApplied=$true
                    routeRuntime=[pscustomobject]@{role='route_publication';boundary=$scope}
                }
                $boundary=[pscustomobject]@{
                    boundaryId='boundary'; boundaryHash='boundary-hash'; coordinatorInstanceId='coordinator'
                    writer='writer'; writerSequence=1L; decisionUtcMilliseconds=1L
                    evidenceLineIndex=0L
                    beforeStateGeneration=0L; afterStateGeneration=1L; decisionKind='RoutePublication'
                    decisionStatus='Accepted'; decisionReason='initial_route_publication'; verdict='pass'
                    sessionId='session'; transferId='transfer'; direction='outbound'
                    beforeBoundary=$scope; afterBoundary=$scope; families=@('route_runtime'); operations=@($operation)
                }
                [pscustomobject]@{
                    Mode='SnapshotV1Authoritative'; ArtifactEvidenceVerdict=$verdict; StreamComplete=$true
                    AuthoritativeFamilies=@('route_runtime','route_transition'); Boundaries=@($boundary)
                    FirstUnresolvedBoundary=if($verdict -ceq 'fail') {
                        [pscustomobject]@{
                            boundaryId='invalid'; boundaryHash='invalid-hash'; writer='writer'; writerSequence=1L
                            evidenceLineIndex=0L; sessionId='session'; transferId='transfer'; direction='outbound'
                            taxonomyClass='filetransfer_coordinator_snapshot_evidence_invalid'
                            decisionReason='snapshot_operation_catalog_mismatch'; variant='malformed_v1'
                            rawCount=7; distinctCount=3; recoveredCount=2; unresolvedCount=1
                        }
                    } else { $null }
                }
            }

            $invalidLaw=Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation (New-Evaluation 'file_tuna_v6' 6)
            if ($null -eq $invalidLaw -or $invalidLaw.Verdict -cne 'fail' -or @($invalidLaw.Findings).Count -eq 0) {
                throw 'invalid file_tuna_v6 route law did not fail closed'
            }
            if ($null -eq $invalidLaw.FirstUnresolvedBoundary -or
                [string]$invalidLaw.FirstUnresolvedBoundary.taxonomyClass -cne 'filetransfer_route_consistency_analysis_parity_invalid' -or
                [string]$invalidLaw.FirstUnresolvedBoundary.decisionReason -cne 'snapshot_authoritative_route_consistency_invalid' -or
                [string]$invalidLaw.FirstUnresolvedBoundary.variant -cne 'snapshot_authoritative_route_boundary_invalid' -or
                [int]$invalidLaw.FirstUnresolvedBoundary.rawCount -ne 1 -or
                [int]$invalidLaw.FirstUnresolvedBoundary.distinctCount -ne 1 -or
                [int]$invalidLaw.FirstUnresolvedBoundary.recoveredCount -ne 0 -or
                [int]$invalidLaw.FirstUnresolvedBoundary.unresolvedCount -ne 1) {
                throw 'invalid route law omitted its normalized unresolved boundary/quartet'
            }
            $malformed=Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation (New-Evaluation 'regular_nkn_v4_fast' 4 'fail')
            if ($null -eq $malformed -or $malformed.Verdict -cne 'fail' -or @($malformed.Findings).Count -eq 0) {
                throw 'declared malformed V1 evidence fell back or passed'
            }
            $malformedFirst=$malformed.FirstUnresolvedBoundary
            if ($null -eq $malformedFirst -or [string]$malformedFirst.boundaryId -cne 'invalid' -or
                [string]$malformedFirst.boundaryHash -cne 'invalid-hash' -or
                [string]$malformedFirst.taxonomyClass -cne 'filetransfer_coordinator_snapshot_evidence_invalid' -or
                [string]$malformedFirst.decisionReason -cne 'snapshot_operation_catalog_mismatch' -or
                [string]$malformedFirst.variant -cne 'malformed_v1' -or
                [int]$malformedFirst.rawCount -ne 7 -or [int]$malformedFirst.distinctCount -ne 3 -or
                [int]$malformedFirst.recoveredCount -ne 2 -or [int]$malformedFirst.unresolvedCount -ne 1 -or
                [string]$malformedFirst.writer -cne 'writer' -or [long]$malformedFirst.writerSequence -ne 1L -or
                [long]$malformedFirst.evidenceLineIndex -ne 0L -or [string]$malformedFirst.sessionId -cne 'session' -or
                [string]$malformedFirst.transferId -cne 'transfer' -or [string]$malformedFirst.direction -cne 'outbound') {
                throw "declared malformed V1 boundary metadata was not preserved: $($malformed | ConvertTo-Json -Depth 16 -Compress)"
            }
            $malformedNormalized=$malformed.NormalizedJson | ConvertFrom-Json
            if ($null -eq $malformedNormalized.firstUnresolvedBoundary -or
                [string]$malformedNormalized.firstUnresolvedBoundary.boundaryId -cne 'invalid' -or
                [int]$malformedNormalized.firstUnresolvedBoundary.rawCount -ne 7 -or
                [int]$malformedNormalized.firstUnresolvedBoundary.distinctCount -ne 3 -or
                [int]$malformedNormalized.firstUnresolvedBoundary.recoveredCount -ne 2 -or
                [int]$malformedNormalized.firstUnresolvedBoundary.unresolvedCount -ne 1) {
                throw 'declared malformed V1 boundary/quartet was omitted from normalized JSON'
            }
            $shifted=New-Evaluation 'regular_nkn_v4_fast' 4 'fail'
            $shifted.FirstUnresolvedBoundary.evidenceLineIndex=1000L
            $shiftedResult=Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $shifted
            if ($shiftedResult.NormalizedJson -cne $malformed.NormalizedJson -or
                $shiftedResult.NormalizedHash -cne $malformed.NormalizedHash -or
                $shiftedResult.FirstUnresolvedBoundary.evidenceLineIndex -ne 1000L -or
                $null -ne $malformedNormalized.firstUnresolvedBoundary.PSObject.Properties['evidenceLineIndex'] -or
                (Compare-FileTransferCoordinatorSnapshotRouteConsistency -Full $malformed -Compact $shiftedResult).Verdict -cne 'pass') {
                throw 'malformed V1 location-only differences changed canonical parity'
            }
            foreach ($field in @('boundaryId','boundaryHash','writer','writerSequence',
                    'sessionId','transferId','direction','taxonomyClass','decisionReason','variant',
                    'rawCount','distinctCount','recoveredCount','unresolvedCount')) {
                $different=New-Evaluation 'regular_nkn_v4_fast' 4 'fail'
                if ($different.FirstUnresolvedBoundary.$field -is [string]) {
                    $different.FirstUnresolvedBoundary.$field += '-mutated'
                } else { $different.FirstUnresolvedBoundary.$field += 1 }
                $differentResult=Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $different
                if ((Compare-FileTransferCoordinatorSnapshotRouteConsistency -Full $malformed -Compact $differentResult).Verdict -cne 'fail') {
                    throw "semantic failure difference was lost: $field"
                }
            }
            $legacy=[pscustomobject]@{
                Mode='LegacyV0'; ArtifactEvidenceVerdict='pass'; StreamComplete=$true
                AuthoritativeFamilies=@(); Boundaries=@()
            }
            if ($null -ne (Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $legacy)) {
                throw 'LegacyV0 route consistency did not remain on its frozen evaluator'
            }
            $partial=[pscustomobject]@{
                Mode='SnapshotV1Shadow'; ArtifactEvidenceVerdict='pass'; StreamComplete=$true
                AuthoritativeFamilies=@('route_runtime'); Boundaries=@()
            }
            if ($null -ne (Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $partial)) {
                throw 'partial family authority incorrectly selected the V1 route reducer'
            }
            'snapshot-route-negative-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()]);
        Assert.True(
            result.ExitCode == 0,
            $"Snapshot route negative controls failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
        Assert.Contains("snapshot-route-negative-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotRouteConsistency_FullAndCompactUseSharedReducerWithoutHardcodedPass()
    {
        var repoRoot = FindRepoRoot();
        var evaluator = File.ReadAllText(Path.Combine(repoRoot, "tools", "FileTransferOps", "CoordinatorSnapshotEvidence.ps1"));
        var orchestration = File.ReadAllText(Path.Combine(repoRoot, "tools", "FileTransferOps", "AnalyzerOrchestration.ps1"));
        var writers = File.ReadAllText(Path.Combine(repoRoot, "tools", "FileTransferSoak", "ArtifactWriters.ps1"));
        var gui = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var legacy = File.ReadAllText(Path.Combine(repoRoot, "tools", "FileTransferSoak", "SoakSummaryExtraction.ps1"));

        Assert.Contains("function Get-FileTransferCoordinatorSnapshotRouteConsistency", evaluator, StringComparison.Ordinal);
        Assert.Contains("Get-FileTransferCoordinatorSnapshotRouteConsistency", orchestration, StringComparison.Ordinal);
        Assert.Contains("Get-FileTransferCoordinatorSnapshotRouteConsistency", writers, StringComparison.Ordinal);
        Assert.Contains("Get-FileTransferCoordinatorSnapshotRouteConsistency", gui, StringComparison.Ordinal);
        Assert.Contains("coordinator_snapshot_authority_v1_route_consistency", gui, StringComparison.Ordinal);
        Assert.DoesNotContain("'route_consistency_verdict=pass'", orchestration, StringComparison.Ordinal);
        Assert.DoesNotContain("'release_route_law_verdict=pass'", orchestration, StringComparison.Ordinal);
        Assert.Contains("function Get-FileTransferRouteConsistency", legacy, StringComparison.Ordinal);
        const string privateSnapshotExports = "Select-Object * -ExcludeProperty __HeldPublicationProofClosures, __FallbackTailPeerProofBindings, __RoutePublicationEffects";
        Assert.Equal(4, orchestration.Split(privateSnapshotExports, StringSplitOptions.None).Length - 1);
        Assert.Contains(privateSnapshotExports, writers, StringComparison.Ordinal);
    }
}
