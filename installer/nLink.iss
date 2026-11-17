#ifndef SourceDir
  #error SourceDir is required. Pass /DSourceDir=... to ISCC.
#endif

#ifndef OutDir
  #define OutDir "artifacts\installer"
#endif

#ifndef AppVersion
  #define AppVersion "0.7.0"
#endif

#define MyAppName "nLink"
#define MyAppExeName "nLink.exe"

[Setup]
; Public release policy:
; - installer artifacts must be Authenticode-signed before publication
; - local/manual packaging may remain unsigned until the release signing step runs
; - if SignTool is configured in the release environment, keep installer and generated uninstaller signing enabled
; Keep AppId stable across releases so silent upgrade/rollback tests exercise true in-place upgrades.
AppId={{9D5C9C2D-7D66-4E6E-8A5A-20F64C2F31A7}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher=nLink
DefaultDirName={localappdata}\Programs\nLink
DefaultGroupName=nLink
DisableProgramGroupPage=yes
OutputDir={#OutDir}
OutputBaseFilename=nLink-Setup-win-x64-{#AppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=no
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[InstallDelete]
Type: filesandordirs; Name: "{app}\bridge"
Type: filesandordirs; Name: "{app}\tuna"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "bridge\*"
Source: "{#SourceDir}\bridge\*"; DestDir: "{app}\bridge"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\nLink"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\nLink"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch nLink"; Flags: nowait postinstall skipifsilent

[Code]
function EscapePowerShellLiteral(const Value: string): string;
begin
  Result := Value;
  StringChangeEx(Result, '''', '''''', True);
end;

procedure StopProcessesUnderInstallDir(const TargetDir: string; const ExcludeUninstallers: Boolean);
var
  PowerShellExe: string;
  Script: string;
  ResultCode: Integer;
begin
  if TargetDir = '' then
  begin
    Exit;
  end;

  Exec(
    ExpandConstant('{cmd}'),
    '/C taskkill /F /T /IM {#MyAppExeName} >NUL 2>&1',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);

  PowerShellExe := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(PowerShellExe) then
  begin
    PowerShellExe := 'powershell.exe';
  end;

  Script :=
    '$ErrorActionPreference=''SilentlyContinue'';' +
    '$dir=[System.IO.Path]::GetFullPath(''' + EscapePowerShellLiteral(TargetDir) + ''').TrimEnd(''\'')+''\'';' +
    '$procs=@(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and ([System.IO.Path]::GetFullPath($_.ExecutablePath)).StartsWith($dir,[System.StringComparison]::OrdinalIgnoreCase)';

  if ExcludeUninstallers then
  begin
    Script :=
      Script +
      ' -and -not (([System.IO.Path]::GetFileName($_.ExecutablePath)) -like ''unins*.exe'')';
  end;

  Script :=
    Script +
    ' } | Sort-Object ProcessId -Descending);' +
    'foreach($p in $procs){ try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop } catch {} };' +
    'Start-Sleep -Milliseconds 500;';

  Exec(
    PowerShellExe,
    '-NoProfile -ExecutionPolicy Bypass -Command "' + Script + '"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    StopProcessesUnderInstallDir(ExpandConstant('{app}'), False);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopProcessesUnderInstallDir(ExpandConstant('{app}'), True);
  end;
end;
