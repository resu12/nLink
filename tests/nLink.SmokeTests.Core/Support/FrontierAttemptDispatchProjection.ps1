function Read-AttemptAst([string]$Path) {
    $tokens=$null; $errors=$null
    $result=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$tokens,[ref]$errors)
    if($errors.Count) {throw ('Attempt source parse failed: '+$Path)}
    return $result
}
function Get-AttemptFunction($Ast,[string]$Name) {
    $definitions=@($Ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $Name},$true))
    if($definitions.Count -ne 1) {throw ('Attempt function ambiguous: '+$Name)}
    return $definitions[0].Extent.Text
}
function Replace-AttemptSeam([string]$Text,[string]$Needle,[string]$Replacement) {
    if(($Text.Split(@($Needle),[StringSplitOptions]::None)).Count -ne 2) {throw ('Attempt seam changed: '+$Needle)}
    return $Text.Replace($Needle,$Replacement)
}
function Get-AttemptPhase([string]$RouteMode) {
    $gui=Read-AttemptAst (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1')
    $phase=@($gui.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$measuredPhaseName'},$true))
    if($phase.Count -ne 1) {throw 'Original measured phase assignment ambiguous'}
    Invoke-Expression $phase[0].Extent.Text
    return $measuredPhaseName
}
function Get-AttemptLaunchProjection([string[]]$Arguments,[string]$Proof) {
    $values=@{}
    if($Arguments.Count%2) {throw 'Odd launch argument projection'}
    for($i=0;$i -lt $Arguments.Count;$i+=2) {
        if($values.ContainsKey($Arguments[$i])) {throw 'Duplicate launch argument'}
        $values[$Arguments[$i]]=$Arguments[$i+1]
    }
    # Never retain or serialize wallet, sidecar or password arguments.
    return [string[]]@($values['-ExePath'],$values['-Direction'],$values['-PayerMode'],$values['-Fault'],
        $values['-RouteMode'],$values['-PayloadSize'],$values['-LiveToggleSequence'],$values['-TimeoutSeconds'],
        $values['-ProgressTimeoutSeconds'],$Proof,(Get-AttemptPhase $values['-RouteMode']),(Get-Process -Id $PID).Path)
}
