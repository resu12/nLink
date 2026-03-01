param([string]$ExePath = 'src/nLink.App/bin/Release/net8.0/nLink.exe')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

function Wait-Until { param([int]$TimeoutMs,[int]$PollMs,[scriptblock]$Condition,[string]$OnTimeoutMessage) $sw=[Diagnostics.Stopwatch]::StartNew(); while($sw.ElapsedMilliseconds -lt $TimeoutMs){ try{$r=& $Condition; if($r){return $r}}catch{}; Start-Sleep -Milliseconds $PollMs }; throw $OnTimeoutMessage }
function Get-Window([int]$ProcessId){$root=[System.Windows.Automation.AutomationElement]::RootElement;$proc=[System.Windows.Automation.AutomationElement]::ProcessIdProperty;$name=[System.Windows.Automation.AutomationElement]::NameProperty;$cond=New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($proc,$ProcessId)),(New-Object System.Windows.Automation.PropertyCondition($name,'nLink')));$root.FindFirst([System.Windows.Automation.TreeScope]::Children,$cond)}
function Find-ByNameAndType($Root,[string]$Name,$Type){$np=[System.Windows.Automation.AutomationElement]::NameProperty;$tp=[System.Windows.Automation.AutomationElement]::ControlTypeProperty;$cond=New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($np,$Name)),(New-Object System.Windows.Automation.PropertyCondition($tp,$Type)));$Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$cond)}
function Find-AllByType($Root,$Type){$tp=[System.Windows.Automation.AutomationElement]::ControlTypeProperty;$cond=New-Object System.Windows.Automation.PropertyCondition($tp,$Type);$Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$cond)}
function Find-VisibleByAutomationId($Root,[string]$AutomationId){$id=[System.Windows.Automation.AutomationElement]::AutomationIdProperty;$cond=New-Object System.Windows.Automation.PropertyCondition($id,$AutomationId);$all=$Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$cond); foreach($el in @($all)){ if($el -and -not $el.Current.IsOffscreen){ return $el } }; $null}
function Find-VisibleByAutomationIdOrName($Root,[string]$AutomationId,[string]$Name,[System.Windows.Automation.ControlType]$Type){$x=Find-VisibleByAutomationId $Root $AutomationId; if($x){return $x}; $x=Find-ByNameAndType $Root $Name $Type; if($x -and -not $x.Current.IsOffscreen){return $x}; $null}
function Click($e){ $p=$e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); ([System.Windows.Automation.InvokePattern]$p).Invoke() }
function SetText($e,[string]$t){ try { $vp=$e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); ([System.Windows.Automation.ValuePattern]$vp).SetValue($t); return } catch {}; [System.Windows.Forms.SendKeys]::SendWait('^a'); Start-Sleep -Milliseconds 60; [System.Windows.Forms.SendKeys]::SendWait($t) }
function WaitWindow($p){ Wait-Until -TimeoutMs 20000 -PollMs 200 -OnTimeoutMessage "Window timeout pid=$($p.Id)" -Condition { Get-Window $p.Id } }
function ClickHome($w,[string]$txt){ $b=Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage "Home button timeout $txt" -Condition { $x=Find-ByNameAndType $w $txt ([System.Windows.Automation.ControlType]::Button); if($x -and -not $x.Current.IsOffscreen){$x}}; Click $b }
function RoleIf($w,[string]$txt){ $t=Find-ByNameAndType $w 'Choose your role' ([System.Windows.Automation.ControlType]::Text); if($t -and -not $t.Current.IsOffscreen){ $b=Find-ByNameAndType $w $txt ([System.Windows.Automation.ControlType]::Button); if($b -and -not $b.Current.IsOffscreen -and $b.Current.IsEnabled){ Click $b } } }
function CopyCode($w){ $b=Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage 'Copy code timeout' -Condition { Find-VisibleByAutomationIdOrName $w 'Helpee.CopyCode' 'Copy code' ([System.Windows.Automation.ControlType]::Button) }; Click $b; $raw=Wait-Until -TimeoutMs 7000 -PollMs 150 -OnTimeoutMessage 'Clipboard code timeout' -Condition { $txt=[string](Get-Clipboard); $m=[regex]::Match($txt,'\d{3}\s?\d{3}'); if($m.Success){$m.Value}}; ([string]$raw -replace '\D','') }
function ConnectPair($helpeeW,$helperW){ $code=CopyCode $helpeeW; $input=Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage 'Helper input timeout' -Condition { Find-VisibleByAutomationId $helperW 'Helper.CodeInput' }; SetText $input $code; $connect=Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage 'Connect button timeout' -Condition { $x=Find-VisibleByAutomationIdOrName $helperW 'Helper.Connect' 'Connect' ([System.Windows.Automation.ControlType]::Button); if($x -and $x.Current.IsEnabled){$x}}; Click $connect; $allow=Wait-Until -TimeoutMs 25000 -PollMs 200 -OnTimeoutMessage 'Allow timeout' -Condition { $x=Find-VisibleByAutomationIdOrName $helpeeW 'Helpee.Allow' 'Allow' ([System.Windows.Automation.ControlType]::Button); if($x -and $x.Current.IsEnabled){$x}}; Click $allow; [void](Wait-Until -TimeoutMs 12000 -PollMs 200 -OnTimeoutMessage 'Connected chat timeout' -Condition { Find-VisibleByAutomationIdOrName $helperW 'Chat.Disconnect' 'Disconnect' ([System.Windows.Automation.ControlType]::Button) }) }
function HasTextLike($w,[string[]]$tokens){ $texts=Find-AllByType $w ([System.Windows.Automation.ControlType]::Text); foreach($t in @($texts)){ if($t.Current.IsOffscreen){continue}; $n=[string]$t.Current.Name; foreach($tok in $tokens){ if($n.IndexOf($tok,[System.StringComparison]::OrdinalIgnoreCase) -ge 0){ return $n } } }; return $null }
function OnHome($w){ $a=Find-ByNameAndType $w 'I need help' ([System.Windows.Automation.ControlType]::Button); $b=Find-ByNameAndType $w 'I want to help someone' ([System.Windows.Automation.ControlType]::Button); return ($a -and -not $a.Current.IsOffscreen -and $b -and -not $b.Current.IsOffscreen) }

$exe=(Resolve-Path $ExePath).Path
$oldTransport=$env:NLINK_TRANSPORT
$procs=@(); $results=New-Object System.Collections.Generic.List[string]; $ok=$true
try {
 if([string]::IsNullOrWhiteSpace($env:NLINK_TRANSPORT)){ $env:NLINK_TRANSPORT='DEVLOCAL' }

 # Helper end session
 $helpee=Start-Process -FilePath $exe -PassThru; $procs += $helpee
 $helpeeW=WaitWindow $helpee; ClickHome $helpeeW 'I need help'; RoleIf $helpeeW 'I need help'
 $helper=Start-Process -FilePath $exe -PassThru; $procs += $helper
 $helperW=WaitWindow $helper; ClickHome $helperW 'I want to help someone'; RoleIf $helperW 'I want to help someone'
 ConnectPair $helpeeW $helperW
 $disc=Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Disconnect button timeout (helper)' -Condition { $x=Find-VisibleByAutomationIdOrName $helperW 'Chat.Disconnect' 'Disconnect' ([System.Windows.Automation.ControlType]::Button); if($x -and $x.Current.IsEnabled){$x}}
 Click $disc
 try { Click $disc } catch {}
 [void](Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Helper did not show ended status' -Condition { HasTextLike $helperW @('Session ended','You ended the session') })
 if(OnHome $helperW){ throw 'Helper auto-navigated to home after End session.' }
 [void]$results.Add('Helper end-session: stayed on page, ended status visible, repeat click safe')
 foreach($p in @($procs)){ try{ if($p -and -not $p.HasExited){ Stop-Process -Id $p.Id -Force } }catch{} }
 $procs=@()

 # Helpee end session
 $helpee2=Start-Process -FilePath $exe -PassThru; $procs += $helpee2
 $helpeeW2=WaitWindow $helpee2; ClickHome $helpeeW2 'I need help'; RoleIf $helpeeW2 'I need help'
 $helper2=Start-Process -FilePath $exe -PassThru; $procs += $helper2
 $helperW2=WaitWindow $helper2; ClickHome $helperW2 'I want to help someone'; RoleIf $helperW2 'I want to help someone'
 ConnectPair $helpeeW2 $helperW2
 $disc2=Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Disconnect button timeout (helpee)' -Condition { $x=Find-VisibleByAutomationIdOrName $helpeeW2 'Chat.Disconnect' 'Disconnect' ([System.Windows.Automation.ControlType]::Button); if($x -and $x.Current.IsEnabled){$x}}
 Click $disc2
 try { Click $disc2 } catch {}
 [void](Wait-Until -TimeoutMs 10000 -PollMs 200 -OnTimeoutMessage 'Helpee did not show ended status' -Condition { HasTextLike $helpeeW2 @('Session ended','You ended the session') })
 if(OnHome $helpeeW2){ throw 'Helpee auto-navigated to home after End session.' }
 [void]$results.Add('Helpee end-session: stayed on page, ended status visible, repeat click safe')

 foreach($line in $results){ Write-Host "[EndSessionCheck] $line" }
 Write-Host '[EndSessionCheck] PASS' -ForegroundColor Green
}
catch {
 $ok=$false
 Write-Host "[EndSessionCheck] FAIL: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
 foreach($p in @($procs)){ try{ if($p -and -not $p.HasExited){ Stop-Process -Id $p.Id -Force } }catch{} }
 if($null -eq $oldTransport){ Remove-Item Env:NLINK_TRANSPORT -ErrorAction SilentlyContinue } else { $env:NLINK_TRANSPORT=$oldTransport }
 if(-not $ok){ exit 1 }
}
