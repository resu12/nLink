param([string]$RepoRoot, [string]$OutputRoot, [ValidateSet('Preparation','Paths','ShortPaths','Entry','Development','Dispatch')][string]$Lane)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Expect-Failure([scriptblock]$Action, [string]$Pattern) {
    $message = ''
    try { & $Action | Out-Null } catch { $message = $_.Exception.Message }
    Check ($message -like $Pattern) "Expected '$Pattern', got '$message'"
}
function Read-Ast([string]$RelativePath) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot $RelativePath), [ref]$tokens, [ref]$errors)
    Check ($errors.Count -eq 0) "Parse errors: $RelativePath"
    return $ast
}
. (Join-Path $RepoRoot 'tools/ScreenShareSoak/ProcessAndBridge.ps1')
$nknAst = Read-Ast 'tools/Run-FileTransferNknSoak.ps1'
$parentAst = Read-Ast 'tools/Run-FileTransferRouteAcceptance.ps1'
foreach ($ast in @($nknAst, $parentAst)) {
    foreach ($node in $ast.EndBlock.Statements) {
        if ($node -is [Management.Automation.Language.FunctionDefinitionAst]) { . ([scriptblock]::Create($node.Extent.Text)) }
    }
}
$fixtureRepo = Join-Path $OutputRoot 'repo'
$script:buildCalls = 0
function powershell {
    $script:buildCalls++
    Write-File (Join-Path $fixtureRepo 'artifacts/bridge/win-x64/index.js') 'cache-index.js'
    $global:LASTEXITCODE = 0
}
function Stop-NLinkProcesses { throw 'Unexpected process cleanup' }
function Invoke-FileTransferGuiSmokeWithTimeout { throw 'Unexpected GUI launch' }
function Write-File([string]$Path, [string]$Text = 'fixture') {
    Check ([IO.Path]::GetFullPath($Path).StartsWith([IO.Path]::GetFullPath($OutputRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'Fixture path escaped output root'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllText($Path, $Text)
}
$required = @('index.js','node.exe','bridge-manifest.json','bridge-dependencies.json','package.json','package-lock.json')
function New-Package([string]$Name, [string]$Missing = '', [switch]$DirectoryInstead, [switch]$External) {
    $base = if ($External) { Join-Path $OutputRoot 'external' } else { Join-Path $fixtureRepo 'packages' }
    $app = Join-Path $base $Name
    Write-File (Join-Path $app 'nLink.exe')
    foreach ($file in $required) {
        $path = Join-Path $app ('bridge/win-x64/' + $file)
        if ($file -eq $Missing) {
            if ($DirectoryInstead) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
        }
        else { Write-File $path ('packaged-' + $file) }
    }
    Write-File (Join-Path $app 'bridge/keep.txt')
    return (Join-Path $app 'nLink.exe')
}
function Inventory([string]$Root) {
    @((Get-ChildItem -LiteralPath $Root -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($Root.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    })) -join "`n"
}
Write-File (Join-Path $fixtureRepo 'VERSION')
Write-File (Join-Path $fixtureRepo 'installer/Build-BridgeBundle.ps1') "throw 'Never execute fixture builder'"
Write-File (Join-Path $fixtureRepo 'tools/nkn-bridge/index.js')
(Get-Item -LiteralPath (Join-Path $fixtureRepo 'tools/nkn-bridge/index.js')).LastWriteTimeUtc = [datetime]'2020-01-01Z'
$cache = Join-Path $fixtureRepo 'artifacts/bridge/win-x64'
foreach ($file in $required) { Write-File (Join-Path $cache $file) ('cache-' + $file) }

if ($Lane -eq 'Preparation') {
    foreach ($external in @($false, $true)) {
        $exe = New-Package "complete-$external" -External:$external
        $app = Split-Path -Parent $exe
        $before = Inventory $app
        Ensure-NknBridgeRuntimeForExe -RepoRoot $fixtureRepo -ResolvedExePath $exe -RuntimeIntent Packaged
        Check ((Inventory $app) -ceq $before) 'Packaged child restaged source'
        $bridge = Ensure-RouteAcceptancePackagedBridgeRuntime -RepoRoot $fixtureRepo -ResolvedExePath $exe -ArtifactDir (Join-Path $OutputRoot "parent-$external")
        Check ($bridge -eq (Join-Path $app 'bridge/win-x64')) 'Wrong packaged bridge'
        Check ((Inventory $app) -ceq $before) 'Packaged parent changed source'
    }
    foreach ($file in $required) {
        foreach ($directoryInstead in @($false, $true)) {
            $exe = New-Package "invalid-$file-$directoryInstead" -Missing $file -DirectoryInstead:$directoryInstead
            $app = Split-Path -Parent $exe
            $before = Inventory $app
            Expect-Failure { Ensure-NknBridgeRuntimeForExe -RepoRoot $fixtureRepo -ResolvedExePath $exe -RuntimeIntent Packaged } '*runtime is incomplete*'
            Expect-Failure { Ensure-RouteAcceptancePackagedBridgeRuntime -RepoRoot $fixtureRepo -ResolvedExePath $exe -ArtifactDir (Join-Path $OutputRoot 'invalid-parent') } '*missing the packaged NKN bridge runtime*'
            Check ((Inventory $app) -ceq $before) "Incomplete package repaired: $file"
        }
    }
    $exe = New-Package 'independent'
    $before = Inventory (Split-Path -Parent $exe)
    # Neither repository discovery nor absent/stale caches/builders may matter.
    function Resolve-NLinkRepoRootForPath { throw 'Unexpected repository discovery' }
    Ensure-NknBridgeRuntimeForExe -RepoRoot (Join-Path $OutputRoot 'absent-repository') -ResolvedExePath $exe -RuntimeIntent Packaged
    $null = Ensure-RouteAcceptancePackagedBridgeRuntime -RepoRoot (Join-Path $OutputRoot 'absent-repository') -ResolvedExePath $exe -ArtifactDir (Join-Path $OutputRoot 'independent-parent')
    Check ((Inventory (Split-Path -Parent $exe)) -ceq $before) 'Absent repository changed package'
    Check ($script:buildCalls -eq 0) 'Packaged preparation attempted build'
}
elseif ($Lane -eq 'Paths') {
    $exe = New-Package 'paths'
    $app = Split-Path -Parent $exe
    Write-File (Join-Path $app 'tuna/win-x64/sidecar.exe')
    Write-File (Join-Path $app 'nlink-tuna-sidecar.exe')
    Write-File (Join-Path $app 'tuna-sidecar-manifest.json')
    $before = Inventory $app
    foreach ($output in @($app, (Join-Path $app 'output'))) {
        Expect-Failure { Copy-FileTransferRegularNknOnlyPortable -ResolvedExePath $exe -ArtifactDir $output -RuntimeIntent Packaged } '*overlaps*'
        Expect-Failure { Ensure-RouteAcceptancePackagedBridgeRuntime -RepoRoot $fixtureRepo -ResolvedExePath $exe -ArtifactDir $output } '*overlaps*'
        Check ((Inventory $app) -ceq $before) 'Rejected overlap changed package'
    }
    $output = $app + '-sibling'
    $staged = Copy-FileTransferRegularNknOnlyPortable -ResolvedExePath $exe -ArtifactDir $output -RuntimeIntent Packaged
    $stage = Split-Path -Parent $staged
    Check ((Inventory (Join-Path $stage 'bridge')) -ceq (Inventory (Join-Path $app 'bridge'))) 'Isolated bridge differs'
    foreach ($excluded in @('tuna','nlink-tuna-sidecar.exe','tuna-sidecar-manifest.json')) { Check (-not (Test-Path -LiteralPath (Join-Path $stage $excluded))) "Copied excluded $excluded" }
    Check ((Inventory $app) -ceq $before) 'Isolated copy changed package'
    $overlapOutput = Join-Path $OutputRoot 'stage-overlap'
    $target = Join-Path $overlapOutput 'regular-nkn-app/sidecar-isolated/a/b/c/d/e/f/app'
    foreach ($suffix in @('', '/child')) {
        $source = Join-Path ($target + $suffix) 'nLink.exe'
        Write-File $source
        $beforeOverlap = Inventory $overlapOutput
        Expect-Failure { Copy-FileTransferRegularNknOnlyPortable -ResolvedExePath $source -ArtifactDir $overlapOutput -RuntimeIntent Packaged } '*overlaps*'
        Check ((Inventory $overlapOutput) -ceq $beforeOverlap) 'Stage deleted source ancestor'
    }
    # Junction creation does not need symlink privilege. Remove the junction only,
    # before the test owner recursively cleans up its verified temporary root.
    $alias = Join-Path $OutputRoot 'package-alias'
    New-Item -ItemType Junction -Path $alias -Target $app | Out-Null
    try {
        Expect-Failure { Assert-PackagedNknOutputPath -ResolvedExePath $exe -OutputPath (Join-Path $alias 'output') } '*reparse*'
        Expect-Failure { Ensure-NknBridgeRuntimeForExe -RepoRoot $fixtureRepo -ResolvedExePath (Join-Path $alias 'nLink.exe') -RuntimeIntent Packaged } '*reparse*'
    }
    finally { [IO.Directory]::Delete($alias) }
    $alias = Join-Path $app 'linked-content'
    New-Item -ItemType Junction -Path $alias -Target $cache | Out-Null
    try { Expect-Failure { Ensure-NknBridgeRuntimeForExe -RepoRoot $fixtureRepo -ResolvedExePath $exe -RuntimeIntent Packaged } '*reparse*' }
    finally { [IO.Directory]::Delete($alias) }
    $diagnostics = Join-Path $OutputRoot 'linked-diagnostics'
    New-Item -ItemType Directory -Path $diagnostics | Out-Null
    $alias = Join-Path $diagnostics 'linked-content'
    New-Item -ItemType Junction -Path $alias -Target $app | Out-Null
    try {
        Expect-Failure { Ensure-RouteAcceptancePackagedBridgeRuntime -RepoRoot $fixtureRepo -ResolvedExePath $exe -ArtifactDir $diagnostics } '*reparse*'
    }
    finally { [IO.Directory]::Delete($alias) }
    Check ((Inventory $app) -ceq $before) 'Alias rejection changed source'
}
elseif ($Lane -eq 'ShortPaths') {
    function Get-ShortPath([string]$Path) {
        $fso = New-Object -ComObject Scripting.FileSystemObject
        $folder = $null
        try {
            $folder = $fso.GetFolder($Path)
            return [string]$folder.ShortPath
        }
        finally {
            if ($null -ne $folder) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($folder) }
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($fso)
        }
    }
    $exe = New-Package 'short-name-package-source'
    $app = Split-Path -Parent $exe
    Write-File (Join-Path $app 'existing-child/keep.txt')
    $short = Get-ShortPath $app
    $alias = Join-Path (Split-Path -Parent $app) (Split-Path -Leaf $short)
    if ([string]::Equals($alias, $app, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Warning 'ShortPaths: fixture volume supplies no distinct 8.3 alias; real alias cases not exercised. Resolver controls still run.'
    }
    else {
        Check (Test-Path -LiteralPath $alias -PathType Container) 'Short alias does not exist'
        $entry = @($nknAst.EndBlock.Statements)
        $start = @($entry | Where-Object { $_.Extent.Text -eq '$repoRoot = Resolve-RepoRoot' })[0].Extent.StartOffset
        $end = @($entry | Where-Object { $_.Extent.Text -eq 'New-Item -ItemType Directory -Force -Path $resolvedArtifactDir | Out-Null' })[0].Extent.StartOffset
        $prefix = [scriptblock]::Create($nknAst.Extent.Text.Substring($start, $end - $start))
        $try = @($parentAst.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] })[-1]
        $rootStatement = @($try.Body.Statements | Where-Object { $_.Extent.Text -like '$runRoot = New-RouteAcceptanceTimestampedRoot*' })[0]
        $parentPrefix = [scriptblock]::Create(($try.Body.Statements | Where-Object { $_.Extent.StartOffset -lt $rootStatement.Extent.StartOffset } | ForEach-Object { $_.Extent.Text }) -join "`n")
        function Resolve-RepoRoot { return $fixtureRepo }
        function Assert-PayloadEfficiencyProfileIsSafeForMode {}
        function Test-FileTransferNknSoakFakeGuiMode { return $false }
        function Test-RouteAcceptanceEnvEnabled { return $false }
        $RuntimeIntent = 'Packaged'; $Build = [Management.Automation.SwitchParameter]$false
        $repoRoot = $fixtureRepo; $MatrixMode = 'phase5-analyzer-gui-acceptance'; $EvidenceDryRun = $false
        $before = Inventory $OutputRoot
        $cases = @(
            @{ Exe = $exe; Output = $alias },
            @{ Exe = (Join-Path $alias 'nLink.exe'); Output = $app },
            @{ Exe = $exe; Output = (Join-Path $alias 'existing-child') },
            @{ Exe = $exe; Output = (Join-Path $alias 'new-output/deeper') },
            @{ Exe = (Join-Path $short 'nLink.exe'); Output = (Join-Path $app 'new-output/deeper') },
            @{ Exe = $exe; Output = (Join-Path $short 'new-output/deeper/') }
        )
        foreach ($case in $cases) {
            $ExePath = $case.Exe; $ArtifactDir = $case.Output; $ArtifactRoot = $case.Output
            # Start with the read-only guard so a regression cannot reach copy/delete.
            Expect-Failure { Assert-PackagedNknOutputPath -ResolvedExePath $ExePath -OutputPath $ArtifactDir } '*overlaps*'
            Expect-Failure { . $prefix } '*overlaps*'
            Expect-Failure { . $parentPrefix } '*overlaps*'
            Expect-Failure { Ensure-RouteAcceptancePackagedBridgeRuntime -RepoRoot $fixtureRepo -ResolvedExePath $ExePath -ArtifactDir $ArtifactDir } '*overlaps*'
            Expect-Failure { Copy-FileTransferRegularNknOnlyPortable -ResolvedExePath $ExePath -ArtifactDir $ArtifactDir -RuntimeIntent Packaged } '*overlaps*'
            Check ((Inventory $OutputRoot) -ceq $before) 'Short-path rejection changed fixture file set or hashes'
            Check (-not (Test-Path -LiteralPath (Join-Path $app 'new-output'))) 'Short-path rejection created output'
        }
        # The isolated-copy destination can also be equal to, or an ancestor of,
        # the source while its artifact root is a permitted source ancestor.
        $overlapOutput = Join-Path $OutputRoot 'short-name-stage-overlap'
        $target = Join-Path $overlapOutput 'regular-nkn-app/sidecar-isolated/a/b/c/d/e/f/app'
        foreach ($suffix in @('', '/child')) {
            $source = Join-Path ($target + $suffix) 'nLink.exe'
            Write-File $source
            $shortOutput = Get-ShortPath $overlapOutput
            $shortSource = Join-Path (Get-ShortPath (Split-Path -Parent $source)) 'nLink.exe'
            $beforeOverlap = Inventory $overlapOutput
            foreach ($pair in @(@{ Exe = $shortSource; Output = $overlapOutput }, @{ Exe = $source; Output = $shortOutput })) {
                $stage = Join-Path $pair.Output 'regular-nkn-app/sidecar-isolated/a/b/c/d/e/f/app'
                Expect-Failure { Assert-PackagedNknOutputPath -ResolvedExePath $pair.Exe -OutputPath $stage -Disjoint } '*overlaps*'
                Expect-Failure { Copy-FileTransferRegularNknOnlyPortable -ResolvedExePath $pair.Exe -ArtifactDir $pair.Output -RuntimeIntent Packaged } '*overlaps*'
                Check ((Inventory $overlapOutput) -ceq $beforeOverlap) 'Aliased stage deleted or changed source'
            }
        }
        # A short-name source with an unrelated short-name output remains valid.
        $disjointOutput = Join-Path $OutputRoot 'short-name-disjoint-output'
        Write-File (Join-Path $disjointOutput 'keep.txt')
        $before = Inventory $app
        $staged = Copy-FileTransferRegularNknOnlyPortable -ResolvedExePath (Join-Path $short 'nLink.exe') -ArtifactDir (Get-ShortPath $disjointOutput) -RuntimeIntent Packaged
        Check ((Inventory (Get-Item -LiteralPath (Split-Path -Parent $staged)).FullName) -ceq $before) 'Disjoint short-name copy differs'
        Check ((Inventory $app) -ceq $before) 'Disjoint short-name copy changed source'
        Write-Output 'short-paths-real-alias-cases-ok'
    }
    # These controls always run, even on volumes with short-name creation disabled.
    Check ((Resolve-PackagedNknFilesystemPath -Path $app) -eq $app) 'Existing path resolution changed'
    $missing = Join-Path $app 'new-output/deeper'
    Check ((Resolve-PackagedNknFilesystemPath -Path ($missing + '/')) -eq $missing) 'Missing suffix was not preserved'
    Check (-not (Test-Path -LiteralPath $missing)) 'Resolver created output'
    $root = [IO.Path]::GetPathRoot($app)
    Check ((Resolve-PackagedNknFilesystemPath -Path $root) -eq $root) 'Filesystem root resolution changed'
    & {
        $syntheticAlias = Join-Path $OutputRoot 'ALIAS~1'
        function Get-Item {
            param($LiteralPath, [switch]$Force, $ErrorAction)
            if ($LiteralPath -eq $syntheticAlias) { return [pscustomobject]@{ FullName = $app } }
            throw [Management.Automation.ItemNotFoundException]::new('Synthetic missing suffix')
        }
        Check ((Resolve-PackagedNknFilesystemPath -Path $syntheticAlias) -eq $app) 'Resolver ignored filesystem identity'
        Check ((Resolve-PackagedNknFilesystemPath -Path (Join-Path $syntheticAlias 'new-output/deeper')) -eq $missing) 'Resolver lost aliased ancestor or suffix order'
    }
    Write-Output 'short-paths-resolver-controls-ok'
}
elseif ($Lane -eq 'Entry') {
    $entry = @($nknAst.EndBlock.Statements)
    $start = @($entry | Where-Object { $_.Extent.Text -eq '$repoRoot = Resolve-RepoRoot' })[0].Extent.StartOffset
    $end = @($entry | Where-Object { $_.Extent.Text -eq 'New-Item -ItemType Directory -Force -Path $resolvedArtifactDir | Out-Null' })[0].Extent.StartOffset
    $prefix = [scriptblock]::Create($nknAst.Extent.Text.Substring($start, $end - $start))
    function Resolve-RepoRoot { return $fixtureRepo }
    function Assert-PayloadEfficiencyProfileIsSafeForMode {}
    function Test-FileTransferNknSoakFakeGuiMode { return $false }
    $RuntimeIntent = 'Packaged'
    $ArtifactDir = Join-Path $OutputRoot 'entry-output'
    $Build = [Management.Automation.SwitchParameter]$false
    $ExePath = ''
    Expect-Failure { . $prefix } '*explicit*ExePath*'
    $ExePath = Join-Path $fixtureRepo 'missing/nLink.exe'
    Expect-Failure { . $prefix } '*executable*'
    $ExePath = New-Package 'entry'
    $validExe = $ExePath
    $ExePath = Join-Path (Split-Path -Parent $validExe) 'Link.exe'
    Expect-Failure { . $prefix } '*executable*'
    $ExePath = $validExe
    $Build = [Management.Automation.SwitchParameter]$true
    Expect-Failure { . $prefix } '*Build*Packaged*'
    $Build = [Management.Automation.SwitchParameter]$false
    $ArtifactDir = Split-Path -Parent $ExePath
    $before = Inventory $ArtifactDir
    Expect-Failure { . $prefix } '*overlaps*'
    Check ((Inventory $ArtifactDir) -ceq $before) 'Entry guard changed source'
    $ArtifactDir = Join-Path $OutputRoot 'entry-output'
    . $prefix
    Check (-not (Test-Path -LiteralPath $ArtifactDir)) 'Validation wrote artifacts'
    $ExePath = New-Package 'entry-incomplete' -Missing 'node.exe'
    Expect-Failure { . $prefix } '*runtime is incomplete*'
    Check ($script:buildCalls -eq 0) 'Entry attempted build'
    function Test-FileTransferNknSoakFakeGuiMode { return $true }
    $ExePath = ''; $Build = [Management.Automation.SwitchParameter]$true
    . $prefix
    $resolveStatement = @($entry | Where-Object { $_.Extent.Text -like '$resolvedExePath = Resolve-FileTransferLiveExePath*' })[0]
    . ([scriptblock]::Create($resolveStatement.Extent.Text))
    Check (-not (Test-Path -LiteralPath $ArtifactDir)) 'Fake input validation wrote artifacts'
    # Execute only the live preparation block with process/build spies. No GUI,
    # environment setup, portable builder or real bridge process can execute.
    $prepare = @($nknAst.FindAll({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
        $n.Clauses[0].Item1.Extent.Text -eq '-not $fakeGuiMode' -and
        $n.Extent.Text -like '*Build-FileTransferPortableIfNeeded*' }, $true))[0]
    $fakeGuiMode = $false; $Mode = 'nkn-fast'; $Build = [Management.Automation.SwitchParameter]$false
    $resolvedExePath = $validExe; $resolvedArtifactDir = Join-Path $OutputRoot 'prepared-output'
    $script:portableBuildCalls = 0
    function Stop-NLinkProcesses {}
    function Build-FileTransferPortableIfNeeded { $script:portableBuildCalls++ }
    . ([scriptblock]::Create($prepare.Extent.Text))
    Check ($script:portableBuildCalls -eq 0) 'Packaged live preparation invoked portable builder'
    $RuntimeIntent = 'Development'
    . ([scriptblock]::Create($prepare.Extent.Text))
    Check ($script:portableBuildCalls -eq 1) 'Development live preparation skipped portable builder'
    # Execute the actual parent's prefix before creating its timestamped root.
    $try = @($parentAst.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] })[-1]
    $rootStatement = @($try.Body.Statements | Where-Object { $_.Extent.Text -like '$runRoot = New-RouteAcceptanceTimestampedRoot*' })[0]
    $parentPrefix = [scriptblock]::Create(($try.Body.Statements | Where-Object { $_.Extent.StartOffset -lt $rootStatement.Extent.StartOffset } | ForEach-Object { $_.Extent.Text }) -join "`n")
    $repoRoot = $fixtureRepo; $MatrixMode = 'phase5-analyzer-gui-acceptance'; $EvidenceDryRun = $false
    function Test-RouteAcceptanceEnvEnabled { return $false }
    $ExePath = New-Package 'parent-entry'; $ArtifactRoot = Split-Path -Parent $ExePath
    Expect-Failure { . $parentPrefix } '*overlaps*'
    $ArtifactRoot = Join-Path $OutputRoot 'parent-entry-output'
    . $parentPrefix
    Check (-not (Test-Path -LiteralPath $ArtifactRoot)) 'Parent guard wrote artifacts'
    $EvidenceDryRun = $true; $ArtifactRoot = Split-Path -Parent $ExePath
    . $parentPrefix
    $EvidenceDryRun = $false; $MatrixMode = 'legacy'
    . $parentPrefix
}
elseif ($Lane -eq 'Development') {
    foreach ($mode in @('fresh','stale','missing')) {
        $exe = New-Package "development-$mode"
        $source = Join-Path $fixtureRepo 'tools/nkn-bridge/index.js'
        $cacheIndex = Join-Path $cache 'index.js'
        if ($mode -eq 'stale') { (Get-Item -LiteralPath $source).LastWriteTimeUtc = [datetime]'2030-01-01Z' }
        if ($mode -eq 'missing') { Remove-Item -LiteralPath $cacheIndex }
        $beforeCalls = $script:buildCalls
        Ensure-NknBridgeRuntimeForExe -RepoRoot $fixtureRepo -ResolvedExePath $exe
        Check ($script:buildCalls -eq ($beforeCalls + $(if ($mode -eq 'fresh') { 0 } else { 1 }))) "Development builder behavior changed: $mode"
        Check ([IO.File]::ReadAllText((Join-Path (Split-Path -Parent $exe) 'bridge/win-x64/bridge-manifest.json')) -ceq 'cache-bridge-manifest.json') 'Development staging changed'
    }
    $exe = New-Package 'external-development' -External -Missing 'bridge-dependencies.json'
    $before = Inventory (Split-Path -Parent $exe)
    Ensure-NknBridgeRuntimeForExe -RepoRoot $fixtureRepo -ResolvedExePath $exe
    Check ((Inventory (Split-Path -Parent $exe)) -ceq $before) 'Legacy external development behavior changed'
}
elseif ($Lane -eq 'Dispatch') {
    $TimeoutSeconds = 900; $ProgressTimeoutSeconds = 180
    function Invoke-RouteAcceptanceChildScript { param($ScriptPath, $Description, $Arguments) $script:childArgs = $Arguments }
    foreach ($intent in @('Packaged','Development')) {
        Invoke-RegularNknRouteAcceptanceRun -RepoRoot $fixtureRepo -ArtifactDir $OutputRoot -PayloadSize 64MiB -ResolvedExePath 'synthetic.exe' -RuntimeIntent $intent
        $index = [array]::IndexOf($script:childArgs, '-RuntimeIntent')
        Check ($index -ge 0 -and $script:childArgs[$index + 1] -eq $intent) 'Intent not forwarded to child'
    }
    Invoke-RegularNknRouteAcceptanceRun -RepoRoot $fixtureRepo -ArtifactDir $OutputRoot -PayloadSize 64MiB -ResolvedExePath 'synthetic.exe'
    Check ($script:childArgs[[array]::IndexOf($script:childArgs, '-RuntimeIntent') + 1] -eq 'Development') 'Legacy default changed'
    $calls = @($parentAst.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-RegularNknRouteAcceptanceRun' }, $true))
    Check ($calls.Count -eq 4) 'Acceptance call inventory changed'
    Check (@($calls | Where-Object { $_.Extent.Text -match '-RuntimeIntent Packaged' }).Count -eq 2) 'Phase 4/5 initial and rerun must be packaged'
    foreach ($call in $calls) {
        if ($call.Extent.Text -match '\$(artifactDir|rerunDir)\b') { Check ($call.Extent.Text -match '-RuntimeIntent Packaged') 'Scenario call omitted packaged intent' }
        else { Check ($call.Extent.Text -notmatch '-RuntimeIntent') 'Legacy call changed intent' }
    }
    $default = @($nknAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'RuntimeIntent' })
    Check ($default.Count -eq 1 -and $default[0].DefaultValue.Value -eq 'Development') 'Standalone default changed'
    $screen = Read-Ast 'tools/Run-ScreenShareNknSoak.ps1'
    $screenCall = @($screen.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Ensure-NknBridgeRuntimeForExe' }, $true))
    Check ($screenCall.Count -eq 1 -and $screenCall[0].Extent.Text -notmatch '-RuntimeIntent') 'Screen-share default changed'
}
Write-Output "packaged-runtime-$Lane-ok"
