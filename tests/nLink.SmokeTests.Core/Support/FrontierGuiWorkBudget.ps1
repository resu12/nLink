# Intentional fixture-only resource policy, not a passive diagnostic or authority.
function Get-AttemptGuiWorkHash([string]$Path) {
    $file=[IO.File]::OpenRead($Path);$hash=[Security.Cryptography.SHA256]::Create()
    try {return [BitConverter]::ToString($hash.ComputeHash($file)).Replace('-','')} finally {$hash.Dispose();$file.Dispose()}
}
function Test-AttemptGuiWorkBudgetScope {
    return $env:NLINK_TEST_ATTEMPT_DISPATCH -ceq '1' -and
        $env:NLINK_TEST_FRONTIER_WORKER_CONFIGURATION -ceq 'Debug' -and
        $env:NLINK_TEST_FRONTIER_ROUTE_CYCLE -ceq 'canonical-cycle-v1' -and
        $env:NLINK_TEST_ATTEMPT_CASE -ceq 'writer-source-read-cross-process-carrier-full-canonical-tail-late-publication' -and
        $env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -ceq 'writer-valid' -and
        $env:NLINK_TEST_PAYLOAD_PARENT_CASE -ceq 'valid' -and
        $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -ceq 'canonical-tail-late-publication' -and
        [string]::IsNullOrEmpty($env:NLINK_TEST_DIAGNOSTIC_CUSTODY)
}
function New-AttemptGuiWorkBudgetSources([string[]]$Paths,[string]$Directory) {
    if(!(Test-AttemptGuiWorkBudgetScope)) {throw 'Canonical GUI work budget scope missing'}
    if(Test-Path -LiteralPath $Directory) {throw 'GUI work budget projection already exists'}
    $matches=@($Paths|Where-Object {[IO.Path]::GetFileName($_) -ceq 'LocalFrontierFileAdoption.cs'})
    if($matches.Count -ne 1) {throw 'GUI work budget source ambiguous'}
    $source=$matches[0];$text=[IO.File]::ReadAllText($source)
    $before='internal const int MaximumActiveMilliseconds = 15000;'
    $after='internal const int MaximumActiveMilliseconds = 30000;'
    if(($text.Split(@($before),[StringSplitOptions]::None)).Count -ne 2) {throw 'Default GUI work budget changed'}
    $projected=$text.Replace($before,$after)
    if($projected.Replace($after,$before) -cne $text) {throw 'GUI work projection changed another byte'}
    [void][IO.Directory]::CreateDirectory($Directory)
    $target=Join-Path $Directory 'LocalFrontierFileAdoption.cs'
    $file=[IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    $writer=[IO.StreamWriter]::new($file,[Text.UTF8Encoding]::new($false))
    try {$writer.Write($projected)} finally {$writer.Dispose()}
    (Get-Item -LiteralPath $target).IsReadOnly=$true
    $proof=[ordered]@{schemaVersion=1;policy='debug-canonical-cycle-gui-work-v1';defaultMilliseconds=15000;fixtureMilliseconds=30000;
        original=$source;projected=$target;originalSha256=(Get-AttemptGuiWorkHash $source);
        projectedSha256=(Get-AttemptGuiWorkHash $target);constantEdits=1;allOtherTextUnchanged=$true;
        clock='original cumulative Stopwatch; no reset,refill or excluded work';authorityGranted=$false}
    $file=[IO.File]::Open((Join-Path $Directory 'projection.json'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    $writer=[IO.StreamWriter]::new($file,[Text.UTF8Encoding]::new($false))
    try {$writer.Write(($proof|ConvertTo-Json -Depth 3))} finally {$writer.Dispose()}
    (Get-Item -LiteralPath (Join-Path $Directory 'projection.json')).IsReadOnly=$true
    return @($Paths|ForEach-Object {if($_ -ceq $source){$target}else{$_}})
}
