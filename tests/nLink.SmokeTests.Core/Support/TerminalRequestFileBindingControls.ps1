param($Request,$Files,$Writer,$Lease,[switch]$NativeCollectorLease)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
function Require($Condition,[string]$Reason) {if(-not $Condition) {throw $Reason}}
function Copy-Object($Value) {
    return [object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($Value,@())
}
function New-Request($SourceLease) {
    return [NLink.Tools.LocalRetainedSourceRequest]::TryCreateWithWriter([guid]::NewGuid(),'Wrapper',
        $Writer.AcceptanceRunId,$Writer.ScenarioRunId,$SourceLease,$Writer.Outputs[3].LineCount,$Writer)
}
$controls=[Collections.Generic.List[string]]::new()
$otherLease=$null; $closedLease=$null; $other=$null
try {
    $otherLease=if($NativeCollectorLease) {$Lease.DuplicateCollectorControlLeaseForTest($true)} else {([NLink.Tools.LocalInputReadLease]::HashAndHold($Lease.Path)).Lease}
    $closedLease=if($NativeCollectorLease) {$Lease.DuplicateCollectorControlLeaseForTest($true)} else {([NLink.Tools.LocalInputReadLease]::HashAndHold($Lease.Path)).Lease}
    Require ($null -ne $otherLease -and $null -ne $closedLease) 'Control leases unavailable'
    $closedLease.Dispose()
    $copiedRequest=Copy-Object $Request; $copiedFiles=Copy-Object $Files; $copiedWriter=Copy-Object $Writer
    Require (-not $copiedFiles.IsCurrent) 'Copied native owner admitted'
    $copiedFiles.Dispose()
    Require $Files.IsCurrent 'Disposing copied native owner affected original'
    Require ($null -eq $copiedRequest.TryBindTerminalFilesForTest($Files,$Writer,$Lease)) 'Copied request admitted'
    $controls.Add('copied_request'); $controls.Add('copied_file_owner')
    foreach($case in @('null_files','copied_files','foreign_writer','missing_writer','unverified_lease','closed_lease','closed_request')) {
        $candidate=New-Request $Lease; $pending=$null
        Require ($null -ne $candidate) 'Control request unavailable'
        try {
            $f=$Files; $w=$Writer; $l=$Lease
            switch($case) {
                'null_files' {$f=$null}
                'copied_files' {$f=$copiedFiles}
                'foreign_writer' {$w=$copiedWriter}
                'missing_writer' {$w=$null}
                'unverified_lease' {
                    $pending=if($NativeCollectorLease) {$Lease.DuplicateCollectorControlLeaseForTest($false)} else {[NLink.Tools.LocalInputReadLease]::OpenExpected($Lease.Path,$Lease.ExpectedSha256,$Writer.Outputs[3].LineCount)}
                    Require ($null -ne $pending) 'Unverified lease control unavailable'; $l=$pending
                }
                'closed_lease' {$l=$closedLease}
                'closed_request' {$candidate.Dispose()}
            }
            Require ($null -eq $candidate.TryBindTerminalFilesForTest($f,$w,$l)) ('Binding control admitted: '+$case)
            Require ($null -eq $candidate.TryBindTerminalFilesForTest($Files,$Writer,$Lease)) ('Failed request binding revived: '+$case)
            $controls.Add($case)
        } finally {if($null -ne $pending) {$pending.Dispose()}; $candidate.Dispose()}
    }
    $binding=$Request.TryBindTerminalFilesForTest($Files,$Writer,$Lease)
    Require ($null -ne $binding -and $binding.Owns($Request,$Files,$Writer,$Lease)) 'Original request binding unavailable'
    $other=New-Request $Lease
    Require ($null -ne $other) 'Foreign genuine request unavailable'
    foreach($case in @('request_copy','other_genuine_request','files_copy','writer_copy','same_file_other_lease','binding_copy')) {
        $r=$Request; $f=$Files; $w=$Writer; $l=$Lease; $b=$binding
        switch($case) {
            'request_copy' {$r=$copiedRequest}
            'other_genuine_request' {$r=$other}
            'files_copy' {$f=$copiedFiles}
            'writer_copy' {$w=$copiedWriter}
            'same_file_other_lease' {$l=$otherLease}
            'binding_copy' {$b=Copy-Object $binding; $b.Dispose()}
        }
        Require (-not $b.Owns($r,$f,$w,$l)) ('Ownership control admitted: '+$case)
        Require ($binding.Owns($Request,$Files,$Writer,$Lease)) ('Original binding lost: '+$case)
        $controls.Add($case)
    }
    Require ($null -eq $Request.TryBindTerminalFilesForTest($Files,$Writer,$Lease)) 'Repeated binding admitted'
    Require ($null -eq $other.TryBindTerminalFilesForTest($Files,$Writer,$Lease)) 'Native owner reused by another request'
    $controls.Add('repeated_binding'); $controls.Add('native_owner_reuse')
    [pscustomobject]@{Binding=$binding;Controls=$controls.ToArray()}
} finally {
    if($null -ne $other) {$other.Dispose()}
    if($null -ne $otherLease) {$otherLease.Dispose()}
    if($null -ne $closedLease) {$closedLease.Dispose()}
}
