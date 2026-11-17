# Fixture compiler projection only. The original tool files are never edited.
function Get-AttemptPassiveHash([string]$Path) {
    $file=[IO.File]::OpenRead($Path);$hash=[Security.Cryptography.SHA256]::Create()
    try {return [BitConverter]::ToString($hash.ComputeHash($file)).Replace('-','')} finally {$hash.Dispose();$file.Dispose()}
}
function Edit-AttemptPassiveText([ref]$Text,$Edits,[string]$Needle,[string]$Replacement) {
    if(($Text.Value.Split(@("`n"+$Needle),[StringSplitOptions]::None)).Count -ne 2) {throw ('Passive diagnostic seam changed: '+$Needle)}
    [void]$Edits.Add(@($Needle,$Replacement))
    $Text.Value=$Text.Value.Replace("`n"+$Needle,"`n"+$Replacement)
}
function New-AttemptPassiveSources([string[]]$Paths,[string]$Directory) {
    if(Test-Path -LiteralPath $Directory) {throw 'Passive projection directory already exists'}
    [void][IO.Directory]::CreateDirectory($Directory)
    $result=[Collections.Generic.List[string]]::new();$proof=[Collections.Generic.List[object]]::new()
    foreach($path in $Paths) {
        $name=[IO.Path]::GetFileName($path)
        if($name -cnotin @('LocalFrontierFileAdoption.cs','LocalGuiWriterSemanticCopy.cs','GuiSmokeLogLedgerStreamingProcessor.cs',
            'LocalGuiWriterParentFacts.cs','LocalGuiWriterParentAcknowledgment.cs','LocalGuiWriterParentRendezvous.cs')) {
            $result.Add($path);continue
        }
        $original=[IO.File]::ReadAllText($path);$text=$original.Replace("`r`n","`n")
        $edits=[Collections.Generic.List[object]]::new();$guards=[Collections.Generic.List[object]]::new()
        if($name -ceq 'LocalFrontierFileAdoption.cs') {
            Edit-AttemptPassiveText ([ref]$text) $edits @'
            return !disposed && work.ElapsedMilliseconds <= MaximumActiveMilliseconds && current() &&
                (sealScope == null || sealScope()) && !disposed && work.ElapsedMilliseconds <= MaximumActiveMilliseconds;
'@ @'
            return NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("adoption_entry_not_disposed", !disposed) &&
                NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("adoption_entry_budget", work.ElapsedMilliseconds <= MaximumActiveMilliseconds) &&
                NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("adoption_current", current()) &&
                NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("adoption_seal_scope", (sealScope == null || sealScope())) &&
                NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("adoption_exit_not_disposed", !disposed) &&
                NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("adoption_exit_budget", work.ElapsedMilliseconds <= MaximumActiveMilliseconds);
'@
        } elseif($name -ceq 'LocalGuiWriterSemanticCopy.cs') {
            Edit-AttemptPassiveText ([ref]$text) $edits '                    if (!IsCurrent) return null;' @'
                    NLink.Tests.AttemptPassiveDiagnosticsForTest.CopyBegin();
                    if (!NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("binding_current", IsCurrent)) return null;
'@
            Edit-AttemptPassiveText ([ref]$text) $edits '                        if (!success) Dispose();' @'
                        NLink.Tests.AttemptPassiveDiagnosticsForTest.CopyEnd(success);
                        if (!success) Dispose();
'@
        } elseif($name -cin @('LocalGuiWriterParentFacts.cs','LocalGuiWriterParentAcknowledgment.cs','LocalGuiWriterParentRendezvous.cs')) {
            Edit-AttemptPassiveText ([ref]$text) $edits 'namespace NLink.Tools' "using ParentProbe = NLink.Tests.AttemptParentFactsDiagnosticsForTest;`nnamespace NLink.Tools"
            if($name -ceq 'LocalGuiWriterParentFacts.cs') {$method='TryPublishParentFacts';$next='        public sealed partial class FrontierParentFileSubmission'}
            elseif($name -ceq 'LocalGuiWriterParentAcknowledgment.cs') {$method='TryConfirmParentAcknowledgment';$next='        public sealed partial class FrontierParentFileSubmission'}
            else {$method='TryPrepareParentHandoffWhenReady';$next='            private bool WaitForParent('}
            $start=$text.IndexOf('            public bool '+$method+'()',[StringComparison]::Ordinal)
            $end=$text.IndexOf($next,$start,[StringComparison]::Ordinal)
            if($start -lt 0 -or $end -le $start) {throw 'Parent diagnostic section changed'}
            $part=$text.Substring($start,$end-$start);$beforePart=$part;$partEdits=[Collections.Generic.List[object]]::new()
            if($name -ceq 'LocalGuiWriterParentRendezvous.cs') {
                Edit-AttemptPassiveText ([ref]$part) $partEdits '                    if (!IsCurrent) return false;' @'
                    ParentProbe.Begin();
                    if (!ParentProbe.Check("prepare_entry_current", IsCurrent)) return false;
'@
                Edit-AttemptPassiveText ([ref]$part) $partEdits '                    if (parentRendezvousAttempted) { Dispose(); return false; }' '                    if (!ParentProbe.Check("prepare_fresh", !parentRendezvousAttempted)) { Dispose(); return false; }'
                Edit-AttemptPassiveText ([ref]$part) $partEdits @'
                    if (parentRendezvousClock.ElapsedMilliseconds >= LocalPublicationInputLimits.ProducerMilliseconds ||
                        TryAttachParentHandoff() == null || TryPrepareParentFiles() == null || TryPublishParentFiles() == null) return false;
'@ @'
                    if (!ParentProbe.Check("prepare_deadline", parentRendezvousClock.ElapsedMilliseconds < LocalPublicationInputLimits.ProducerMilliseconds) ||
                        !ParentProbe.Step(1, "prepare_attach", TryAttachParentHandoff() != null) ||
                        !ParentProbe.Step(2, "prepare_files", TryPrepareParentFiles() != null) ||
                        !ParentProbe.Step(3, "prepare_publish_files", TryPublishParentFiles() != null)) return false;
'@
                Edit-AttemptPassiveText ([ref]$part) $partEdits @'
                    if (!channel.Signal(ParentRendezvousStage.FilesOffered) ||
                        !WaitForParent(channel, ParentRendezvousStage.FilesPulled) || !TryPublishParentFacts() ||
                        !channel.Signal(ParentRendezvousStage.FactsOffered) ||
                        !WaitForParent(channel, ParentRendezvousStage.AcknowledgmentOffered) || !TryConfirmParentAcknowledgment() ||
                        !channel.Signal(ParentRendezvousStage.AcknowledgmentConfirmed)) return false;
                    success = WaitForParent(channel, ParentRendezvousStage.CommitPrepared);
'@ @'
                    if (!ParentProbe.Step(4, "prepare_offer_files", channel.Signal(ParentRendezvousStage.FilesOffered)) ||
                        !ParentProbe.Step(5, "prepare_wait_files", WaitForParent(channel, ParentRendezvousStage.FilesPulled)) ||
                        !ParentProbe.Step(19, "prepare_publish_facts", TryPublishParentFacts()) ||
                        !ParentProbe.Step(20, "prepare_offer_facts", channel.Signal(ParentRendezvousStage.FactsOffered)) ||
                        !ParentProbe.Step(21, "prepare_wait_ack", WaitForParent(channel, ParentRendezvousStage.AcknowledgmentOffered)) ||
                        !ParentProbe.Check("prepare_confirm_ack", TryConfirmParentAcknowledgment()) ||
                        !ParentProbe.Step(28, "prepare_ack_confirmed", channel.Signal(ParentRendezvousStage.AcknowledgmentConfirmed))) return false;
                    success = ParentProbe.Step(29, "prepare_commit", WaitForParent(channel, ParentRendezvousStage.CommitPrepared));
'@
                Edit-AttemptPassiveText ([ref]$part) $partEdits '                catch { return false; }' '                catch { ParentProbe.Check("prepare_exception", false); return false; }'
                Edit-AttemptPassiveText ([ref]$part) $partEdits '                finally { parentRendezvousClock.Stop(); if (!success) Dispose(); }' '                finally { parentRendezvousClock.Stop(); ParentProbe.End(success); if (!success) Dispose(); }'
            } else {
                $facts=$name -ceq 'LocalGuiWriterParentFacts.cs';$prefix=if($facts){'facts'}else{'ack'}
                $enter=if($facts){6}else{22};$owned=if($facts){7}else{23};$recovery=if($facts){9}else{24};$done=if($facts){16}else{26}
                $flag=if($facts){'parentFactsAttempted'}else{'parentAcknowledgmentAttempted'};$state=if($facts){'FilesPulled'}else{'FactsReceived'}
                Edit-AttemptPassiveText ([ref]$part) $partEdits '                    if (!IsCurrent) return false;' ("                    ParentProbe.Mark($enter);`n                    if (!ParentProbe.Check(`"${prefix}_entry_current`", IsCurrent)) return false;")
                Edit-AttemptPassiveText ([ref]$part) $partEdits "                        if ($flag) return false; $flag = true;" "                        if (!ParentProbe.Check(`"${prefix}_fresh`", !$flag)) return false; $flag = true;"
                Edit-AttemptPassiveText ([ref]$part) $partEdits "                        if (!OwnsParentSubmission(parentSubmission) || !parentSubmission.$state) return false;" @"
                        if (!ParentProbe.Check("${prefix}_owns_submission", OwnsParentSubmission(parentSubmission)) || !ParentProbe.Check("${prefix}_submission_state", parentSubmission.$state)) return false;
                        ParentProbe.Mark($owned);
"@
                $channel=if($facts){'fileChannel.Facts'}else{'fileChannel.Facts.Acknowledgment'}
                Edit-AttemptPassiveText ([ref]$part) $partEdits "                        if (adoption == null || $channel != null) return false;" "                        if (!ParentProbe.Check(`"${prefix}_adoption`", adoption != null) || !ParentProbe.Check(`"${prefix}_channel_absent`", $channel == null)) return false;"
                Edit-AttemptPassiveText ([ref]$part) $partEdits '                        success = adoption.TryRunRecoveryRead(() =>' ("                        ParentProbe.Mark("+$(if($facts){8}else{23})+");`n                        success = adoption.TryRunRecoveryRead(() =>")
                Edit-AttemptPassiveText ([ref]$part) $partEdits '                        {' "                        {`n                            ParentProbe.Mark($recovery);"
                if($facts) {
                    Edit-AttemptPassiveText ([ref]$part) $partEdits '                            Action check = () => { if (!adoption.IsSourceVerified || !parentPreparation.Files.IsCurrent) throw new IOException("parent_facts_revoked"); };' '                            Action check = () => { if (!ParentProbe.Check("facts_callback_source", adoption.IsSourceVerified) || !ParentProbe.Check("facts_callback_pins", parentPreparation.Files.IsCurrent)) throw new IOException("parent_facts_revoked"); };'
                    Edit-AttemptPassiveText ([ref]$part) $partEdits '                            var bytes = LocalFrontierParentFacts.Encode(parentPreparation, files, fileChannel.Body, check);' @'
                            ParentProbe.Mark(10);
                            var bytes = LocalFrontierParentFacts.Encode(parentPreparation, files, fileChannel.Body, check);
                            ParentProbe.EncodedBytes(bytes.Length); ParentProbe.Mark(11);
'@
                    Edit-AttemptPassiveText ([ref]$part) $partEdits '                            if (LocalFrontierParentFacts.Decode(bytes, fileChannel.Body, finalizer.run, finalizer.scenario, check) == null) return false;' @'
                            ParentProbe.Mark(12);
                            if (!ParentProbe.Check("facts_decoded", LocalFrontierParentFacts.Decode(bytes, fileChannel.Body, finalizer.run, finalizer.scenario, check) != null)) return false;
                            ParentProbe.Mark(13);
'@
                    Edit-AttemptPassiveText ([ref]$part) $partEdits '                            channel = LocalFrontierParentFactsChannel.Create(fileChannel.Endpoint, fileChannel.OfferHash, bytes, check);' @'
                            ParentProbe.Mark(14);
                            channel = LocalFrontierParentFactsChannel.Create(fileChannel.Endpoint, fileChannel.OfferHash, bytes, check);
                            ParentProbe.Mark(15);
'@
                    Edit-AttemptPassiveText ([ref]$part) $partEdits '                            return channel != null;' '                            return ParentProbe.Check("facts_channel_created", channel != null);'
                    Edit-AttemptPassiveText ([ref]$part) $partEdits '                        fileChannel.Facts = channel; success = OwnsParentSubmission(parentSubmission); return success;' '                        fileChannel.Facts = channel; ParentProbe.Mark(17); success = ParentProbe.Check("facts_final_owned", OwnsParentSubmission(parentSubmission)); return success;'
                } else {
                    Edit-AttemptPassiveText ([ref]$part) $partEdits '                            if (!adoption.IsSourceVerified || !parentPreparation.Files.IsCurrent) return false;' '                            if (!ParentProbe.Check("ack_source", adoption.IsSourceVerified) || !ParentProbe.Check("ack_pins", parentPreparation.Files.IsCurrent)) return false;'
                    Edit-AttemptPassiveText ([ref]$part) $partEdits '                            return channel != null && adoption.IsSourceVerified && parentPreparation.Files.IsCurrent && channel.MarkConfirmed();' @'
                            ParentProbe.Mark(25);
                            return ParentProbe.Check("ack_channel", channel != null) && ParentProbe.Check("ack_final_source", adoption.IsSourceVerified) && ParentProbe.Check("ack_final_pins", parentPreparation.Files.IsCurrent) && ParentProbe.Check("ack_confirmed", channel.MarkConfirmed());
'@
                    Edit-AttemptPassiveText ([ref]$part) $partEdits '                        success = OwnsParentSubmission(parentSubmission); return success;' '                        success = ParentProbe.Check("ack_final_owned", OwnsParentSubmission(parentSubmission)); return success;'
                }
                Edit-AttemptPassiveText ([ref]$part) $partEdits '                        if (!success) return false;' "                        ParentProbe.Mark($done);`n                        if (!ParentProbe.Check(`"${prefix}_recovery_success`", success)) return false;"
                Edit-AttemptPassiveText ([ref]$part) $partEdits '                    catch { return false; }' "                    catch { ParentProbe.Check(`"${prefix}_exception`", false); return false; }"
                $endMethod=if($facts){'FactsEnd'}else{'AckEnd'}
                Edit-AttemptPassiveText ([ref]$part) $partEdits '                    finally { if (!success) { Dispose(); if (channel != null) channel.Dispose(); } }' "                    finally { ParentProbe.$endMethod(success); if (!success) { Dispose(); if (channel != null) channel.Dispose(); } }"
            }
            foreach($edit in $partEdits) {$guards.Add([pscustomobject]@{original=$edit[0];projected=$edit[1]})}
            Edit-AttemptPassiveText ([ref]$text) $edits $beforePart $part
        } else {
            Edit-AttemptPassiveText ([ref]$text) $edits '                for (long index = 0; index < frozenLineCount; index++)' @'
                NLink.Tests.AttemptPassiveDiagnosticsForTest.Mark(0);
                for (long index = 0; index < frozenLineCount; index++)
'@
            Edit-AttemptPassiveText ([ref]$text) $edits '                    string line = reader.ReadLine();' @'
                    NLink.Tests.AttemptPassiveDiagnosticsForTest.ReadStart(index);
                    string line = reader.ReadLine();
                    NLink.Tests.AttemptPassiveDiagnosticsForTest.ReadEnd();
'@
            Edit-AttemptPassiveText ([ref]$text) $edits '                    ParsedLine parsed = Parse(line);' @'
                    NLink.Tests.AttemptPassiveDiagnosticsForTest.ParseStart();
                    ParsedLine parsed = Parse(line);
                    NLink.Tests.AttemptPassiveDiagnosticsForTest.ParseEnd();
'@
            Edit-AttemptPassiveText ([ref]$text) $edits '                if (streamRead != null && bookmark == frozenLineCount) streamRead.CaptureInitialWriters(writers.Values);' @'
                NLink.Tests.AttemptPassiveDiagnosticsForTest.Mark(1);
                if (streamRead != null && bookmark == frozenLineCount) streamRead.CaptureInitialWriters(writers.Values);
'@
            Edit-AttemptPassiveText ([ref]$text) $edits '                retained.Flush(true);' @'
                NLink.Tests.AttemptPassiveDiagnosticsForTest.Mark(2);
                retained.Flush(true);
'@
            Edit-AttemptPassiveText ([ref]$text) $edits '                result.RetainedSha256 = Hex(retainedHash.Hash);' @'
                NLink.Tests.AttemptPassiveDiagnosticsForTest.Mark(3);
                result.RetainedSha256 = Hex(retainedHash.Hash);
'@
            Edit-AttemptPassiveText ([ref]$text) $edits '                if (outputLease != null)' @'
                NLink.Tests.AttemptPassiveDiagnosticsForTest.Mark(4);
                if (outputLease != null)
'@
            Edit-AttemptPassiveText ([ref]$text) $edits '                    if (streamRead != null) streamRead.CheckOwnedBoundary("after-output-seal");' @'
                    if (streamRead != null) streamRead.CheckOwnedBoundary("after-output-seal");
                    NLink.Tests.AttemptPassiveDiagnosticsForTest.Mark(5);
'@
            $start=$text.IndexOf('        internal GuiSmokeLedgerCapturedOutput CopySemantic(string destination, GuiSmokeLedgerCapturedOutput expected,',[StringComparison]::Ordinal)
            $end=$text.IndexOf('        // The cancellation callback only closes native resources.',$start,[StringComparison]::Ordinal)
            if($start -lt 0 -or $end -le $start) {throw 'Semantic copy section changed'}
            $copy=$text.Substring($start,$end-$start);$beforeCopy=$copy
            # Each operand is evaluated once in its original short-circuit position.
            $copy=$copy.Replace('if (!current() || !IsCurrent) return null;',
                'if (!NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("copy_loop_source_current", current()) || !NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("copy_loop_outputs_current", IsCurrent)) return null;')
            $copy=$copy.Replace('if (read == 0 || !current()) return null;',
                'if (read == 0 || !NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("copy_after_read_current", current())) return null;')
            $copy=$copy.Replace('if (!current() || !IsCurrent || input.ReadByte() != -1 || input.Position != expected.ByteCount) return null;',
                'if (!NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("copy_bytes_source_current", current()) || !NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("copy_bytes_outputs_current", IsCurrent) || input.ReadByte() != -1 || input.Position != expected.ByteCount) return null;')
            $copy=$copy.Replace('if (!current() || !IsCurrent || semanticCopyInfo == null || semanticCopyInfo.FileIdentity == expected.FileIdentity ||',
                'if (!NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("copy_capture_source_current", current()) || !NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("copy_capture_outputs_current", IsCurrent) || semanticCopyInfo == null || semanticCopyInfo.FileIdentity == expected.FileIdentity ||')
            $copy=$copy.Replace('success = current() && OwnsSemanticCopy(semanticCopyInfo);',
                'success = NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("copy_final_source_current", current()) && NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("copy_final_owned", OwnsSemanticCopy(semanticCopyInfo));')
            $copy=$copy.Replace('                        while (bytes < expected.ByteCount)',"                        NLink.Tests.AttemptPassiveDiagnosticsForTest.Mark(7);`n                        while (bytes < expected.ByteCount)")
            $copy=$copy.Replace('                            bytes += read; last = buffer[read - 1];',"                            bytes += read; last = buffer[read - 1];`n                            NLink.Tests.AttemptPassiveDiagnosticsForTest.CopyProgress(bytes, expected.ByteCount);")
            $copy=$copy.Replace('                        if (!NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("copy_bytes_source_current", current())',
                "                        NLink.Tests.AttemptPassiveDiagnosticsForTest.Mark(8);`n                        if (!NLink.Tests.AttemptPassiveDiagnosticsForTest.Check(`"copy_bytes_source_current`", current())")
            $copy=$copy.Replace('                        output.Flush(true);',@'
                        NLink.Tests.AttemptPassiveDiagnosticsForTest.Mark(9);
                        NLink.Tests.AttemptPassiveDiagnosticsForTest.Mark(10);
                        output.Flush(true);
                        NLink.Tests.AttemptPassiveDiagnosticsForTest.Mark(11);
'@)
            $copy=$copy.Replace('                        if (!NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("copy_capture_source_current", current())',
                "                        NLink.Tests.AttemptPassiveDiagnosticsForTest.Mark(12);`n                        if (!NLink.Tests.AttemptPassiveDiagnosticsForTest.Check(`"copy_capture_source_current`", current())")
            $copy=$copy.Replace('                        semanticCopy = new FileStream(duplicate, FileAccess.Read); duplicate = null;',
                "                        semanticCopy = new FileStream(duplicate, FileAccess.Read); duplicate = null;`n                        NLink.Tests.AttemptPassiveDiagnosticsForTest.Mark(13);")
            $copy=$copy.Replace('                    return success ? semanticCopyInfo : null;',
                "                    NLink.Tests.AttemptPassiveDiagnosticsForTest.Mark(14);`n                    return success ? semanticCopyInfo : null;")
            if($copy -ceq $beforeCopy -or ([regex]::Matches($copy,'AttemptPassiveDiagnosticsForTest\.Check\(')).Count -ne 9) {throw 'Semantic copy diagnostic shape changed'}
            $counter=[int[]]@(0)
            $copy=[regex]::Replace($copy,'(?m)^( +)if \((.*?)\) return null;',[Text.RegularExpressions.MatchEvaluator]{param($match)
                $counter[0]++;$label='copy_guard_'+$counter[0]
                $guards.Add([pscustomobject]@{name=$label;rejectedCondition=$match.Groups[2].Value})
                return ($match.Groups[1].Value+'if (!NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("'+$label+'", !('+$match.Groups[2].Value+'))) return null;')
            },[Text.RegularExpressions.RegexOptions]::Singleline)
            if($counter[0] -ne 8) {throw 'Semantic copy return-guard count changed'}
            $copy=$copy.Replace('                catch { return null; }','                catch { NLink.Tests.AttemptPassiveDiagnosticsForTest.Check("copy_exception", false); return null; }')
            Edit-AttemptPassiveText ([ref]$text) $edits $beforeCopy $copy
        }
        $restored=$text
        for($i=$edits.Count-1;$i -ge 0;$i--) {$restored=$restored.Replace($edits[$i][1],$edits[$i][0])}
        if($restored -cne $original.Replace("`r`n","`n")) {throw 'Passive projection cannot restore original text'}
        if($original.Contains("`r`n")) {$text=$text.Replace("`n","`r`n")}
        $target=Join-Path $Directory $name
        $file=[IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        $writer=[IO.StreamWriter]::new($file,[Text.UTF8Encoding]::new($false))
        try {$writer.Write($text)} finally {$writer.Dispose()}
        (Get-Item -LiteralPath $target).IsReadOnly=$true
        $proof.Add([pscustomobject]@{original=$path;projected=$target;originalSha256=(Get-AttemptPassiveHash $path);
            projectedSha256=(Get-AttemptPassiveHash $target);edits=$edits.Count;originalTextRestored=$true;guards=$guards.ToArray()})
        $result.Add($target)
    }
    if($proof.Count -ne 6) {throw 'Expected exactly six passive compiler projections'}
    $proofPath=Join-Path $Directory 'projection.json'
    $file=[IO.File]::Open($proofPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    $writer=[IO.StreamWriter]::new($file,[Text.UTF8Encoding]::new($false))
    try {$writer.Write(($proof|ConvertTo-Json -Depth 3))} finally {$writer.Dispose()}
    (Get-Item -LiteralPath $proofPath).IsReadOnly=$true
    return $result.ToArray()
}

