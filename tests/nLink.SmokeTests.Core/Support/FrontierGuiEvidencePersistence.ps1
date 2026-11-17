$mode=$Case.Substring('cursor-persist-'.Length)
$clone=[object].GetMethod('MemberwiseClone',$flags)
$proof=$owner.TrySealFiles()
if($null -eq $proof) {throw 'Persistence seal missing'}
$finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
$binding=$finalizer.TryTakeFrontierBinding(); $result=$null; $evidence=$null
$destination=[IO.Path]::Combine($Directory,'filetransfer-frontier-finalizer-evidence.json')
try {
    if($null -eq $binding) {throw 'Persistence binding missing'}
    $type=[NLink.Tools.LocalGuiWriterRequest+FrontierPersistedEvidence]
    if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) {throw 'Persistence fabrication exposed'}
    if($mode -ceq 'early') {
        if($null -ne $binding.TryPersistEvidence() -or $binding.IsCurrent) {throw 'Early persistence accepted'}
    } else {
        if($mode -ceq 'copied-binding') {
            $foreign=$clone.Invoke($binding,$null)
            if($null -ne $foreign.TryPersistEvidence()) {throw 'Copied binding persisted'}
            $foreign.Dispose(); if(-not $binding.IsCurrent) {throw 'Copied binding retired original'}
        }
        $events=if($mode -ceq 'empty-semantic') {[string[]]@()} else {[string[]]@('operational_log_writer_started','writer_only_contribution')}
        if($mode -ceq 'catalog-marker') {
            . (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
            $events=[string[]]@(Get-GuiSmokeSemanticEvidenceEventNames)
        }
        $result=$binding.TryProcessObservedLedger($events)
        if($null -eq $result) {throw 'Persistence original processing missing'}
        $copy=$null
        if($mode -cne 'before-copy') {$copy=$binding.TryCopySemanticEvidence(); if($null -eq $copy) {throw 'Persistence original copy missing'}}
        $capture=$result.StreamCapture
        $work=$owner.GetType().GetField('frontierWork',$flags).GetValue($owner); $elapsedBefore=$work.ElapsedTicks
        if($mode -ceq 'missing-copy') {$binding.GetType().GetField('semanticCopy',$flags).SetValue($binding,$null)}
        if($mode -ceq 'active-work') {$adopter.GetType().GetField('processingRunning',$flags).SetValue($adopter,$true)}
        if($mode -ceq 'collision') {[IO.File]::WriteAllText($destination,'sentinel')}
        if($mode -ceq 'directory') {[void][IO.Directory]::CreateDirectory($destination)}
        $lease=$capture.GetType().GetField('outputs',$flags).GetValue($capture)
        if($mode -ceq 'closed-output') {$lease.GetType().GetField('files',$flags).GetValue($lease)[0].Dispose()}
        if($mode -ceq 'closed-copy') {$lease.GetType().GetField('semanticCopy',$flags).GetValue($lease).Dispose()}
        if($mode -ceq 'mutable-result') {$result.SemanticSha256='wrong'; $result.SemanticByteCount=999; $result.Writers=@(); $result.SourceLineVisitCount=999}
        if($mode -ceq 'over-bound') {$capture.Writer.GetType().GetProperty('CreationPath').SetValue($capture.Writer,('x'*4097),$null)}
        if($mode -ceq 'budget') {
            $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*16))
        }
        $evidence=$binding.TryPersistEvidence()
        if($mode -cin @('before-copy','missing-copy','active-work','collision','directory','closed-output','closed-copy','budget','over-bound')) {
            if($null -ne $evidence -or $binding.IsCurrent) {throw ('Invalid persistence accepted: '+$mode)}
            if($mode -ceq 'collision' -and [IO.File]::ReadAllText($destination) -cne 'sentinel') {throw 'Persistence overwrote evidence'}
        } else {
            if($null -eq $evidence -or -not $evidence.IsCurrent -or -not $evidence.EvidencePersisted -or
                -not [object]::ReferenceEquals($evidence,$binding.PersistedEvidence) -or $evidence.LiveProofEligible -or
                $evidence.NativeWriterCustodyEstablished -or $evidence.PublicationCompletionIssued) {throw 'Owned persistence missing/authority'}
            if($work.IsRunning -or $work.ElapsedTicks -le $elapsedBefore) {throw 'Persistence bypassed cumulative clock'}
            $resource=$type.GetField('Resource',$flags).GetValue($evidence); $file=$resource.GetType().GetField('file',$flags).GetValue($resource)
            $read=[IO.File]::Open($destination,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $memory=[IO.MemoryStream]::new()
            try {$read.CopyTo($memory); $bytes=$memory.ToArray()} finally {$memory.Dispose(); $read.Dispose()}
            $hash=[Security.Cryptography.SHA256]::Create()
            try {$digest=[BitConverter]::ToString($hash.ComputeHash($bytes)).Replace('-','').ToLowerInvariant()} finally {$hash.Dispose()}
            $json=[Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
            if($evidence.Output.Sha256 -cne $digest -or $evidence.Output.ByteCount -ne $bytes.Length -or $evidence.Output.LineCount -ne 1 -or
                $evidence.Output.CreationPath -cne $destination -or @([IO.Directory]::GetFiles($Directory,'filetransfer-frontier-finalizer-evidence.json.*.tmp')).Count) {throw 'Persistence native metadata/rename mismatch'}
            if($json.event -cne 'filetransfer_frontier_finalizer_evidence' -or $json.schemaVersion -ne 1 -or -not $json.diagnosticOnly -or
                $json.acceptanceRunId -cne 'frontier-gui-run' -or $json.scenarioRunId -cne 'frontier-gui-scenario' -or
                $json.ledgerId -cne $binding.CursorMatch.Final.LedgerId -or $json.generationId -cne $binding.CursorMatch.Final.GenerationId -or
                $json.source.sha256 -cne $proof.SourceSha256 -or $json.source.byteCount -ne $capture.SourceBytes -or
                $json.source.lineCount -ne $capture.SourceLines -or $json.source.bookmark -ne $capture.Bookmark -or
                $json.initial.lineCount -ne $binding.CursorMatch.Initial.LineCount -or $json.final.lineCount -ne $binding.CursorMatch.Final.LineCount -or
                $json.liveProofEligible -or $json.nativeWriterCustodyEstablished -or $json.publicationCompletionIssued) {throw 'Persistence scope/original facts mismatch'}
            $originals=@($capture.Retained,$capture.Writer,$capture.Semantic,$copy.Output)
            if($json.outputs.Count -ne 4 -or $json.writers.Count -ne 2 -or $json.recovery.batchCount -ne $binding.RecoveryMatch.RecoveredBatchCount) {throw 'Persistence original set mismatch'}
            for($i=0;$i -lt 4;$i++) {
                $actual=$json.outputs[$i]; $original=$originals[$i]
                if($actual.sha256 -cne $original.Sha256 -or $actual.byteCount -ne $original.ByteCount -or $actual.lineCount -ne $original.LineCount -or
                    $actual.fileIdentity -cne $original.FileIdentity -or $actual.path -cne $original.CreationPath) {throw 'Persistence imported mutable output facts'}
            }
            for($i=0;$i -lt 2;$i++) {
                $actual=$json.writers[$i]; $original=$capture.Validation.Writers[$i]
                if($actual.writerId -cne $original.WriterId -or $actual.lastSequence -ne $original.LastSequence -or $actual.drainSequence -ne $original.DrainSequence) {throw 'Persistence imported mutable writer facts'}
            }
            if($mode -ceq 'catalog-marker') {
                $readLines={param($path)
                    $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
                    $reader=[IO.StreamReader]::new($stream)
                    try {while(-not $reader.EndOfStream) {$reader.ReadLine()}} finally {$reader.Dispose()}
                }
                $sourceLines=@(& $readLines $source)
                $semanticLines=@(& $readLines $capture.Semantic.CreationPath)
                $copyLines=@(& $readLines $copy.Output.CreationPath)
                $sourceMarkers=@(for($i=0;$i -lt $sourceLines.Length;$i++) {if($sourceLines[$i].Contains('event=filetransfer_frontier_payload_marker;')) {$i}})
                $semanticMarkers=@(for($i=0;$i -lt $semanticLines.Length;$i++) {if($semanticLines[$i].Contains('event=filetransfer_frontier_payload_marker;')) {$i}})
                if($sourceMarkers.Count -ne 1 -or $semanticMarkers.Count -ne 1) {throw 'Native payload marker missing or duplicated in semantic evidence'}
                $position=$semanticMarkers[0]; $physical=$sourceMarkers[0]+1
                if($semanticLines[$position] -cne $sourceLines[$physical-1] -or $copyLines.Length -ne $semanticLines.Length -or
                    $copyLines[$position] -cne $sourceLines[$physical-1] -or $capture.SemanticOrigins[$position] -ne $physical -or
                    -not [object]::ReferenceEquals($copy.Origins,$capture.SemanticOrigins)) {throw 'Native payload marker text or physical origin changed'}
                foreach($stage in @($capture,$copy,$evidence)) {
                    if($stage.LiveProofEligible -or $stage.NativeWriterCustodyEstablished -or $stage.PublicationCompletionIssued) {throw 'Retained marker granted authority'}
                }
                Write-Output ('frontier-marker-retention=physical:'+ $physical +';semantic:'+($position+1)+';diagnosticOnly:true')
            }
            if($mode -ceq 'copied-evidence') {
                $foreign=$clone.Invoke($evidence,$null)
                if($foreign.IsCurrent -or $foreign.EvidencePersisted) {throw 'Copied evidence current'}
                $foreign.Dispose(); if(-not $evidence.IsCurrent) {throw 'Copied evidence retired original'}
            }
            if($mode -ceq 'repeat') {if($null -ne $binding.TryPersistEvidence()) {throw 'Repeated persistence accepted'}}
            elseif($mode -ceq 'closed-evidence') {$file.Dispose()}
            elseif($mode -ceq 'copy-dispose') {$copy.Dispose()}
            elseif($mode -ceq 'cancel') {
                $cancel=[Threading.EventWaitHandle]::OpenExisting($Name+'.closed')
                try {[void]$cancel.Set(); if($evidence.IsCurrent) {throw 'Cancelled evidence current'}; [void]$cancel.Reset()} finally {$cancel.Dispose()}
            } elseif($mode -ceq 'complete') {if($finalizer.Complete('','',0,0,$null,$null,$null,$null,$null,$null)) {throw 'Invalid completion accepted'}}
            else {$evidence.Dispose()}
            if($evidence.IsCurrent -or $evidence.EvidencePersisted -or $file.CanRead) {throw 'Evidence retirement failed'}
        }
    }
    if($binding.IsCurrent -or $proof.IsCurrent -or $null -ne $binding.PersistedEvidence -or ($null -ne $result -and $result.StreamCapture.IsCurrent)) {throw 'Persistence retained optional input set'}
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) {throw 'Persistence retained payloads'}
    foreach($path in [IO.Directory]::GetFiles($Directory,'filetransfer-frontier-finalizer-evidence.json*')) {[IO.File]::AppendAllText($path,'released')}
    [IO.File]::AppendAllText($source,'persistence-released')
} finally {if($null -ne $result) {$result.StreamCapture.Dispose()}; if($null -ne $binding) {$binding.Dispose()}}
