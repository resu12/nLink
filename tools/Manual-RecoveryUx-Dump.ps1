param([string]$ExePath = '.\\src\\nLink.App\\bin\\Release\\net8.0\\nLink.exe')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
function Wait-Until{param([int]$TimeoutMs,[int]$PollMs,[scriptblock]$Condition,[string]$OnTimeoutMessage) $sw=[Diagnostics.Stopwatch]::StartNew(); while($sw.ElapsedMilliseconds -lt $TimeoutMs){ try{$r=& $Condition; if($r){return $r}}catch{}; Start-Sleep -Milliseconds $PollMs}; throw $OnTimeoutMessage }
function GetWin([int]$pid){$root=[System.Windows.Automation.AutomationElement]::RootElement;$proc=[System.Windows.Automation.AutomationElement]::ProcessIdProperty;$name=[System.Windows.Automation.AutomationElement]::NameProperty;$cond=New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($proc,$pid)),(New-Object System.Windows.Automation.PropertyCondition($name,'nLink')));$root.FindFirst([System.Windows.Automation.TreeScope]::Children,$cond)}
function FindNameType($r,[string]$n,$t){$np=[System.Windows.Automation.AutomationElement]::NameProperty;$tp=[System.Windows.Automation.AutomationElement]::ControlTypeProperty;$c=New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($np,$n)),(New-Object System.Windows.Automation.PropertyCondition($tp,$t)));$r.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)}
function FindAllType($r,$t){$tp=[System.Windows.Automation.AutomationElement]::ControlTypeProperty;$c=New-Object System.Windows.Automation.PropertyCondition($tp,$t);$r.FindAll([System.Windows.Automation.TreeScope]::Descendants,$c)}
function FindById($r,[string]$id){$ip=[System.Windows.Automation.AutomationElement]::AutomationIdProperty;$c=New-Object System.Windows.Automation.PropertyCondition($ip,$id);$all=$r.FindAll([System.Windows.Automation.TreeScope]::Descendants,$c);foreach($e in @($all)){if($e -and -not $e.Current.IsOffscreen){return $e}};$null}
function Click($e){try{$p=$e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern);([System.Windows.Automation.InvokePattern]$p).Invoke(); return}catch{}; throw 'click fail'}
function SetTxt($e,[string]$t){$p=$e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern);([System.Windows.Automation.ValuePattern]$p).SetValue($t)}
function WaitWin($p){Wait-Until -TimeoutMs 30000 -PollMs 200 -OnTimeoutMessage 'win timeout' -Condition {GetWin $p.Id}}
function Home($w,[string]$txt){$b=Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage "home timeout $txt" -Condition { $x=FindNameType $w $txt ([System.Windows.Automation.ControlType]::Button); if($x -and -not $x.Current.IsOffscreen){$x}}; Click $b}
function RoleIf($w,[string]$txt){$title=FindNameType $w 'Choose your role' ([System.Windows.Automation.ControlType]::Text); if($title -and -not $title.Current.IsOffscreen){$b=FindNameType $w $txt ([System.Windows.Automation.ControlType]::Button); if($b -and -not $b.Current.IsOffscreen){Click $b}}}
$procs=@(); try{
 if([string]::IsNullOrWhiteSpace($env:NLINK_TRANSPORT)){$env:NLINK_TRANSPORT='DEVLOCAL'}
 $hlee=Start-Process -FilePath (Resolve-Path $ExePath) -PassThru; $procs+=$hlee; $w1=WaitWin $hlee; Home $w1 'I need help'; RoleIf $w1 'I need help'
 $hlpr=Start-Process -FilePath (Resolve-Path $ExePath) -PassThru; $procs+=$hlpr; $w2=WaitWin $hlpr; Home $w2 'I want to help someone'; RoleIf $w2 'I want to help someone'
 $copy=Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage 'copy timeout' -Condition {FindById $w1 'Helpee.CopyCode'}; Click $copy
 $code=Wait-Until -TimeoutMs 7000 -PollMs 150 -OnTimeoutMessage 'clip timeout' -Condition {$t=[string](Get-Clipboard);$m=[regex]::Match($t,'\d{3}\s?\d{3}'); if($m.Success){$m.Value}}; $code=($code -replace '\D','')
 $inp=Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage 'input timeout' -Condition {FindById $w2 'Helper.CodeInput'}; SetTxt $inp $code
 $conn=Wait-Until -TimeoutMs 15000 -PollMs 200 -OnTimeoutMessage 'connect timeout' -Condition { $b=FindById $w2 'Helper.Connect'; if($b -and $b.Current.IsEnabled){$b}}; Click $conn
 [void](Wait-Until -TimeoutMs 30000 -PollMs 200 -OnTimeoutMessage 'allow timeout' -Condition { $b=FindById $w1 'Helpee.Allow'; if($b -and $b.Current.IsEnabled){$b}})
 Start-Sleep -Seconds 35
 $texts=FindAllType $w2 ([System.Windows.Automation.ControlType]::Text)
 "--- Visible texts helper after 35s ---"
 foreach($t in @($texts)){ if($t.Current.IsOffscreen){continue}; $n=[string]$t.Current.Name; if([string]::IsNullOrWhiteSpace($n)){continue}; $n }
} finally { foreach($p in $procs){ try{ if($p -and -not $p.HasExited){ Stop-Process -Id $p.Id -Force } }catch{} } }
