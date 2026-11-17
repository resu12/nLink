param([string]$RepoRoot,[string]$SettingsPath)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$settings=[IO.File]::ReadAllText($SettingsPath) | ConvertFrom-Json
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
$owner=$null; $source=$null; $custody=$null
try {
    $owner=[NLink.Tools.LocalRetainedSourceRequest]::TryAttach($env:NLINK_RETAINED_SOURCE_REQUEST,
        [guid]$env:NLINK_RETAINED_SOURCE_REQUEST_ID,$settings.path,$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID)
    if($null -eq $owner) { throw 'Actual retained owner missing' }
    $source=$owner.TryOpenSource()
    if($settings.mode -cne 'early-source') { $null=@($source.ReadLines()) }
    # Opaque test objects deliberately make no authority claim. Production
    # records the actual returned objects at the two original evaluator calls.
    $context=[pscustomobject]@{Marker='context'}
    [string[]]$full=@('full'); [string[]]$compact=@('compact')
    $fullView=[pscustomobject]@{Marker='full';Checksum='a'*64}
    $compactView=[pscustomobject]@{Marker='compact';Checksum='b'*64}
    if($settings.mode -ceq 'record-cap') { $full=[string[]]::new([NLink.Tools.LocalPublicationInputLimits]::InputRecords+1) }
    $custody=$owner.TryBeginPrimary($source,$context,$full,$compact)
    $admitted=$settings.mode -cnotin @('early-source','source-only','record-cap')
    if(($null -ne $custody) -ne $admitted) { throw 'Custody admission changed' }
    if($null -ne $owner.TryBeginPrimary($source,$context,$full,$compact)) { throw 'Second primary acquired custody' }
    $pair=$null; $owned=$false
    if($admitted) {
        $fullCandidate=$full; $compactCandidate=$compact
        $fullResult=$fullView; $compactResult=$compactView; $contextCandidate=$context
        switch($settings.mode) {
            'reversed-order' { if($custody.RecordCompactEvaluation($compact,$compactView)) { throw 'Reversed evaluation admitted' } }
            'premature-take' { if($null -ne $custody.Take($context,$full,$compact,$fullView,$compactView)) { throw 'Partial evaluation pair admitted' } }
            'wrong-full-array' { $fullCandidate=[string[]]$full.Clone() }
            'missing-full-view' { $fullResult=$null }
        }
        $null=$custody.RecordFullEvaluation($fullCandidate,$fullResult)
        if($settings.mode -ceq 'duplicate-full' -and $custody.RecordFullEvaluation($full,$fullView)) { throw 'Full completion replayed' }
        if($settings.mode -ceq 'wrong-compact-array') { $compactCandidate=[string[]]$compact.Clone() }
        if($settings.mode -ceq 'missing-compact-view') { $compactResult=$null }
        $null=$custody.RecordCompactEvaluation($compactCandidate,$compactResult)
        if($settings.mode -ceq 'duplicate-compact' -and $custody.RecordCompactEvaluation($compact,$compactView)) { throw 'Compact completion replayed' }
        switch($settings.mode) {
            'copied-context' { $contextCandidate=$context.PSObject.Copy() }
            'swapped-views' { $fullResult=$compactView; $compactResult=$fullView }
            'copied-view' { $fullResult=$fullView.PSObject.Copy() }
            'copied-array' { $fullCandidate=[string[]]$full.Clone() }
            'source-disposed' { $source.Dispose() }
            'worker-disposed' { $owner.Dispose() }
            'custody-disposed' { $custody.Dispose() }
        }
        $pair=$custody.Take($contextCandidate,$fullCandidate,$compactCandidate,$fullResult,$compactResult)
        $owned=$custody.Owns($pair,$context,$full,$compact,$fullView,$compactView)
        $expected=$settings.mode -cin @('valid','duplicate-begin','forged-token')
        if(($null -ne $pair) -ne $expected -or $owned -ne $expected) { throw "Pair custody changed: $($settings.mode)" }
        if($expected) {
            if($pair.RequestId -ne $owner.RequestId -or $pair.FullInvocationId -eq [guid]::Empty -or
                $pair.CompactInvocationId -eq [guid]::Empty -or $pair.FullInvocationId -eq $pair.CompactInvocationId -or
                $pair.GetType().GetConstructors().Count -ne 0 -or $custody.GetType().GetConstructors().Count -ne 0) { throw 'Invocation identities are not privately issued' }
            foreach($property in $pair.GetType().GetProperties()) {
                if($null -ne $property.GetSetMethod()) { throw 'Publicly writable pair metadata' }
            }
            if($null -ne $custody.Take($context,$full,$compact,$fullView,$compactView)) { throw 'Pair was issued twice' }
            if($custody.Owns($pair,$context.PSObject.Copy(),$full,$compact,$fullView,$compactView) -or
                $custody.Owns($pair,$context,$full,$compact,$fullView.PSObject.Copy(),$compactView)) { throw 'Matching metadata substituted for exact custody' }
            if($settings.mode -ceq 'forged-token') {
                $forged=$pair | ConvertTo-Json | ConvertFrom-Json
                $accepted=$false
                try { $accepted=$custody.Owns($forged,$context,$full,$compact,$fullView,$compactView) } catch { }
                if($accepted) { throw 'Serialized self-consistent pair forged custody' }
            }
            if(-not $custody.Owns($pair,$context,$full,$compact,$fullView,$compactView)) { throw 'Read-only rejection invalidated genuine pair' }
        }
        $custody.Dispose()
        if($custody.Owns($pair,$context,$full,$compact,$fullView,$compactView)) { throw 'Pair survived disposal' }
    }
    [IO.File]::WriteAllText($settings.result,(@{Mode=$settings.mode;Admitted=$admitted;Owned=$owned;
        ActualRetainedOwner=$true;AuthorityEvaluations=0;PublicationCompletionIssued=$false} | ConvertTo-Json -Compress))
} finally {
    if($null -ne $custody) { $custody.Dispose() }; if($null -ne $source) { $source.Dispose() }; if($null -ne $owner) { $owner.Dispose() }
}
