param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if(Test-Path -LiteralPath $Directory){throw 'Native pin fixture directory already exists'}
[void][IO.Directory]::CreateDirectory($Directory)
function Write-PinJson([string]$Path,$Value) {
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value|ConvertTo-Json -Depth 12))
    $file=[IO.File]::Open($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try {$file.Write($bytes,0,$bytes.Length)} finally {$file.Dispose()}
}
function Replace-PinSource([string]$Text,[string]$Old,[string]$New) {
    if([regex]::Matches($Text,[regex]::Escape($Old)).Count -ne 1){throw 'Native pin instrumentation anchor changed'}
    return $Text.Replace($Old,$New)
}
$report=[ordered]@{startedUtc=[DateTimeOffset]::UtcNow.ToString('O');endedUtc=$null;passed=$false;error=$null;
    hostVersion=$PSVersionTable.PSVersion.ToString();clrVersion=[Environment]::Version.ToString();framework=$null;
    hostPath=(Get-Process -Id $PID).Path;hostSha256=$null;coreLibPath=[object].Assembly.Location;coreLibSha256=$null;
    frequency=[Diagnostics.Stopwatch]::Frequency;producerMilliseconds=20000;diagnosticOnly=$true;
    liveProofEligible=$false;nativeWriterCustodyEstablished=$false;publicationCompletionIssued=$false;rows=@();compilerInputs=@();transformations=@();compilerOptions=$null}
$report.hostSha256=(Get-FileHash -LiteralPath $report.hostPath).Hash
$report.coreLibSha256=(Get-FileHash -LiteralPath $report.coreLibPath).Hash
try {$report.framework=[Runtime.InteropServices.RuntimeInformation]::FrameworkDescription} catch {$report.framework='CLR '+[Environment]::Version}
$failure=$null
try {
    if($null -ne ('NLink.Tools.LocalGuiWriterRequest' -as [type])){throw 'Native pin fixture requires a fresh host'}
    function Add-Type {
        param([string[]]$Path,$CompilerParameters,[string]$CompilerOptions,[string]$ErrorAction)
        $inputs=@(foreach($source in $Path){
            $name=[IO.Path]::GetFileName($source);$original=[IO.File]::ReadAllText($source);$text=$original
            $oldExpressions=@();$newExpressions=@()
            if($name -ceq 'LocalGuiWriterParentFiles.cs') {
                if((Get-FileHash -LiteralPath $source).Hash -cne '0A7DAC00839190FFCBA1EB73FDA15A31764DE1CF3B4A07E7DE8442BEDFCB3783'){throw 'Original query helper changed'}
                $oldExpressions=@('GetFileType(handle)','GetFileInformationByHandle(handle, out info)')
                $newExpressions=@('NativePinQueryType(handle)','NativePinQueryInformation(handle, out info)')
            } elseif($name -ceq 'LocalFrontierParentCommitPins.cs') {
                if((Get-FileHash -LiteralPath $source).Hash -cne '10C36D5456A818D906C653CA1CD6960D213E4DE7730C1EAB3873662E57B01B1D'){throw 'Original pin ownership helper changed'}
                $nl=if($text.Contains("`r`n")){"`r`n"}else{"`n"}
                $anchor='                        lock (sync)'+$nl+'                        {'+$nl+'                            if (closed || Handles.Count != packet.Entries.Length) return false;'
                $replacement='                        long nativePinWaitStarted = System.Diagnostics.Stopwatch.GetTimestamp();'+$nl+
                    '                        lock (sync)'+$nl+'                        {'+$nl+
                    '                            NativePinRecordLock(System.Diagnostics.Stopwatch.GetTimestamp() - nativePinWaitStarted);'+$nl+
                    '                            if (closed || Handles.Count != packet.Entries.Length) return false;'
                $oldExpressions=@($anchor);$newExpressions=@($replacement)
            }
            if($oldExpressions.Count) {
                for($i=0;$i -lt $oldExpressions.Count;$i++){$text=Replace-PinSource $text $oldExpressions[$i] $newExpressions[$i]}
                $reverse=$text;for($i=$oldExpressions.Count-1;$i -ge 0;$i--){$reverse=Replace-PinSource $reverse $newExpressions[$i] $oldExpressions[$i]}
                if(-not [string]::Equals($reverse,$original,[StringComparison]::Ordinal)){throw 'Instrumentation changed validation semantics'}
                $copy=Join-Path $Directory $name
                $bytes=[Text.UTF8Encoding]::new($false).GetBytes($text);$file=[IO.File]::Open($copy,[IO.FileMode]::CreateNew)
                try {$file.Write($bytes,0,$bytes.Length)} finally {$file.Dispose()}
                $report.transformations+=@([pscustomobject]@{original=$source;originalSha256=(Get-FileHash -LiteralPath $source).Hash;compiled=$copy;compiledSha256=(Get-FileHash -LiteralPath $copy).Hash;reverseTextMatchesOriginal=$true;replacementCount=$oldExpressions.Count})
                $copy
            } else {$source}
        })
        $inputs+=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierNativePinValidation.cs'
        $report.compilerInputs=@(foreach($inputPath in $inputs){[pscustomobject]@{path=$inputPath;sha256=(Get-FileHash -LiteralPath $inputPath).Hash}})
        $arguments=@{Path=$inputs;ErrorAction='Stop'}
        if($null -ne $CompilerParameters){$CompilerParameters.CompilerOptions+=',NLINK_TEST_NATIVE_PIN_BOUNDARY';$report.compilerOptions=$CompilerParameters.CompilerOptions;$arguments.CompilerParameters=$CompilerParameters}
        else {$arguments.CompilerOptions=$CompilerOptions+',NLINK_TEST_NATIVE_PIN_BOUNDARY';$report.compilerOptions=$arguments.CompilerOptions}
        Microsoft.PowerShell.Utility\Add-Type @arguments
    }
    try {. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')}
    finally {Remove-Item Function:Add-Type}
    [NLink.Tools.LocalGuiWriterRequest+FrontierParentFilesPending]::RunPinFixtures($Directory)
    $report.passed=$true
} catch {$failure=$_;$report.error=$_.Exception.ToString()}
finally {
    if($null -ne ('NLink.Tools.LocalGuiWriterRequest+FrontierParentFilesPending' -as [type])){
        $report.rows=@([NLink.Tools.LocalGuiWriterRequest+FrontierParentFilesPending]::GetPinFixtureRows())
    }
    foreach($entry in $report.transformations){if((Get-FileHash -LiteralPath $entry.original).Hash -cne $entry.originalSha256){throw 'Original production helper changed during fixture'}}
    $report.endedUtc=[DateTimeOffset]::UtcNow.ToString('O')
    Write-PinJson (Join-Path $Directory 'pin-results.json') $report
}
if($null -ne $failure){throw $failure}
if($report.rows.Count -ne 10){throw 'Native pin fixture did not execute all ten controls/profiles'}
'native-pin-boundaries-ok; controls=8; profiles=2; host='+$report.hostVersion
