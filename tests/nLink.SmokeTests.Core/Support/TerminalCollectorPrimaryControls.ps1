param($Request,$Binding,$Outputs,$Completion,$Lease)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$clone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
$copiedOutputs=$clone.Invoke($Outputs,@()); $copiedBinding=$clone.Invoke($Binding,@())
if($copiedBinding.BindCollectorOutputsForTest($Outputs) -or $Binding.BindCollectorOutputsForTest($copiedOutputs)) {throw 'Copied collector binding admitted'}
if(-not $Binding.BindCollectorOutputsForTest($Outputs)) {throw 'Original collector binding unavailable'}
$controls=[Collections.Generic.List[string]]::new()
$controls.Add('copied_binding'); $controls.Add('copied_outputs')
$otherLease=$Lease.DuplicateCollectorControlLeaseForTest($true)
try {
    foreach($case in @('request','writer','lease','outputs','binding','same_file_other_lease')) {
        $r=$Request; $w=$Completion; $l=$Lease; $o=$Outputs; $b=$Binding
        switch($case) {
            'request' {$r=$clone.Invoke($r,@())}
            'writer' {$w=$clone.Invoke($w,@())}
            'lease' {$l=$clone.Invoke($l,@())}
            'outputs' {$o=$copiedOutputs; $o.Dispose()}
            'binding' {$b=$copiedBinding}
            'same_file_other_lease' {$l=$otherLease}
        }
        if($b.OwnsCollectorOutputsForTest($o,$r,$w,$l)) {throw "Collector association substitution admitted: $case"}
        if(-not $Binding.OwnsCollectorOutputsForTest($Outputs,$Request,$Completion,$Lease)) {throw 'Original collector association lost'}
        $controls.Add($case)
    }
    if($Binding.BindCollectorOutputsForTest($Outputs)) {throw 'Repeated collector binding admitted'}
    $controls.Add('repeated_binding')
    [pscustomobject]@{Controls=$controls.ToArray()}
} finally {if($null -ne $otherLease) {$otherLease.Dispose()}}
