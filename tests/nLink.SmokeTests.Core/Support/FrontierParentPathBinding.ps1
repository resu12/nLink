# Exercise the pure join on copied scalar snapshots, never import them as custody.
$originalFacts=$custody.GetType().GetField('facts',$parentFlags).GetValue($custody)
$factsType=$originalFacts.GetType(); $matches=$factsType.GetMethod('MatchesCompletion',$parentFlags)
$outputsField=$factsType.GetField('outputs',$parentFlags)
$originalOutputs=$outputsField.GetValue($originalFacts)
$nativePath=Join-Path $Directory 'filetransfer-frontier-retained-log-slice.log'
$legacyPath=Join-Path $Directory 'filetransfer-retained-log-slice.log'
if($originalOutputs[3].Path -cne $nativePath -or $completion.Outputs[3].Path -cne $legacyPath -or
    -not $matches.Invoke($originalFacts,@($completion))) {throw 'Original exact native/legacy mapping missing'}
$faults=@('native-label','native-other-root','native-case','native-traversal','legacy-label','legacy-other-root',
    'legacy-case','legacy-traversal','completion-root','hash','bytes','lines','identity')
foreach($fault in $faults) {
    $factCopy=$parentClone.Invoke($originalFacts,$null)
    $nativeOutputs=$originalOutputs.Clone()
    $nativeOutputs[3]=$parentClone.Invoke($nativeOutputs[3],$null)
    $outputsField.SetValue($factCopy,$nativeOutputs)
    $completionCopy=$parentClone.Invoke($completion,$null)
    $legacyOutputs=[NLink.Tools.LocalGuiWriterRequest+Output[]]@($completion.Outputs)
    $legacyOutputs[3]=$parentClone.Invoke($legacyOutputs[3],$null)
    $legacyOutputs[0]=$parentClone.Invoke($legacyOutputs[0],$null)
    $completionCopy.GetType().GetProperty('Outputs').SetValue($completionCopy,
        [Collections.ObjectModel.ReadOnlyCollection[NLink.Tools.LocalGuiWriterRequest+Output]]::new($legacyOutputs),$null)
    $target=$null; $property='Path'; $replacement=$null
    switch($fault) {
        'native-label' {$target=$nativeOutputs[3]; $replacement=$legacyPath}
        'native-other-root' {$target=$nativeOutputs[3]; $replacement=Join-Path (Join-Path $Directory 'foreign') 'filetransfer-frontier-retained-log-slice.log'}
        'native-case' {$target=$nativeOutputs[3]; $replacement=$nativePath.ToUpperInvariant()}
        'native-traversal' {$target=$nativeOutputs[3]; $replacement=Join-Path $Directory '.\filetransfer-frontier-retained-log-slice.log'}
        'legacy-label' {$target=$legacyOutputs[3]; $replacement=$nativePath}
        'legacy-other-root' {$target=$legacyOutputs[3]; $replacement=Join-Path (Join-Path $Directory 'foreign') 'filetransfer-retained-log-slice.log'}
        'legacy-case' {$target=$legacyOutputs[3]; $replacement=$legacyPath.ToUpperInvariant()}
        'legacy-traversal' {$target=$legacyOutputs[3]; $replacement=Join-Path $Directory '.\filetransfer-retained-log-slice.log'}
        'completion-root' {$target=$completionCopy; $property='ArtifactRoot'; $replacement=Join-Path $Directory 'foreign'}
        'hash' {$target=$legacyOutputs[3]; $property='Sha256'; $replacement=('0'*64)}
        'bytes' {$target=$legacyOutputs[3]; $property='ByteCount'; $replacement=[long]($target.ByteCount+1)}
        'lines' {$target=$legacyOutputs[3]; $property='LineCount'; $replacement=[long]($target.LineCount+1)}
        'identity' {$target=$legacyOutputs[0]; $property='OriginalFileIdentity'; $replacement='00000000:0000000000000000'}
    }
    $target.GetType().GetProperty($property).SetValue($target,$replacement,$null)
    if($matches.Invoke($factCopy,@($completionCopy))) {throw "Foreign native/legacy mapping accepted: $fault"}
    if(-not $custody.IsCurrent -or -not $matches.Invoke($originalFacts,@($completion))) {throw 'Mapping controls changed original custody'}
}
'frontier-parent-path-binding='+$faults.Count
