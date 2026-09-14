param()
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# Keep standard script-based exports visible across the in-memory script scope.
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility') -Global
$settings=[IO.File]::ReadAllText($env:NLINK_PUBLICATION_INTEGRATION_SETTINGS) | ConvertFrom-Json
$arguments=@{}
if($args.Count % 2) { throw 'Offline Wrapper requires name/value arguments' }
for($i=0;$i -lt $args.Count;$i+=2) {
    $name=([string]$args[$i]).TrimStart('-')
    if($name -cnotin @('ExePath','WalletPath','WalletPassword','SidecarPath','PayerMode','RouteMode','Fault','Direction',
        'PayloadSize','ArtifactDir','TimeoutSeconds','ProgressTimeoutSeconds','RetainedAnalysisOwner','LiveToggleSequence')) { throw 'Unexpected offline Wrapper argument' }
    $arguments[$name]=$args[$i+1]
}
if($arguments.ArtifactDir -cne $settings.Output -or $arguments.RetainedAnalysisOwner -cne 'RouteAcceptance') { throw 'Offline Wrapper scope changed' }
$source=Join-Path $settings.RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1'
$text=[IO.File]::ReadAllText($source)
$tokens=$null; $errors=$null; $ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
if($errors.Count) { throw 'Whole Wrapper parse failed' }
$needle="(Join-Path `$RepoRoot 'tools\GuiSmoke-Windows.ps1')"
$parent=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-TunaGuiSmokeWithHardDeadline'},$true))
if($parent.Count -ne 1 -or $parent[0].Extent.Text.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) { throw 'Exact GUI child launch changed' }
$text=$text.Replace($parent[0].Extent.Text,$parent[0].Extent.Text.Replace($needle,"(Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/PublicationPositiveGuiWorker.ps1')"))
# Execute the entire original script with its original parameter validation,
# setup, relay attach, GUI deadline, summary gates and finally/manifest path.
# Only relocate PSScriptRoot for the in-memory script and replace the GUI child.
$text=$text.Replace('$PSScriptRoot',("'"+(Join-Path $settings.RepoRoot 'tools').Replace("'","''")+"'"))
$env:NLINK_GUI_WRITER_TEST_SETTINGS=$env:NLINK_PUBLICATION_INTEGRATION_SETTINGS
& ([scriptblock]::Create($text)) @arguments
# Make the original manifest reconciliation reject this offline fixture even
# in a clean checkout. Only this test-created inert setup file is changed.
$inertExe=Join-Path $settings.Output 'inert-setup/unused-app.txt'
if($arguments.ExePath -cne $inertExe) { throw 'Inert executable scope changed' }
[IO.File]::WriteAllText($inertExe,'offline-inert-file-changed-after-wrapper-manifest')
[IO.File]::WriteAllText((Join-Path $settings.Output 'whole-wrapper-returned.json'),(@{WholeWrapperMain=$true;WrapperPid=$PID;Live=$false} | ConvertTo-Json))
