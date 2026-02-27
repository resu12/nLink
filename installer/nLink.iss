#ifndef SourceDir
  #error SourceDir is required. Pass /DSourceDir=... to ISCC.
#endif

#ifndef OutDir
  #define OutDir "artifacts\installer"
#endif

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define MyAppName "nLink"
#define MyAppExeName "nLink.exe"

[Setup]
; Keep AppId stable across beta builds so silent upgrade/rollback tests exercise true in-place upgrades.
AppId={{9D5C9C2D-7D66-4E6E-8A5A-20F64C2F31A7}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher=nLink
DefaultDirName={localappdata}\Programs\nLink Helper
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

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "bridge\*"
Source: "{#SourceDir}\bridge\*"; DestDir: "{app}\bridge"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\nLink"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\nLink"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch nLink"; Flags: nowait postinstall skipifsilent
