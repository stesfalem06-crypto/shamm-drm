; Shamm Master (Xama Master) — Inno Setup 6. Same AppId → in-place upgrade; data under Documents\XamaMaster kept.
#define MyAppName "Shamm Master"
#define MyAppVersion "0.5.1"
#define MyAppPublisher "Shamm"
#define MyAppExeName "XamaMaster.exe"
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish-xm"
#endif

[Setup]
AppId={{C2B9F1E4-8A3D-4F7B-9C5E-1D6A0B8F4E72}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\XamaMaster
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=output
#if Arch == "x86"
OutputBaseFilename=ShammMaster-Setup-x86-{#MyAppVersion}
ArchitecturesAllowed=x86compatible
#else
OutputBaseFilename=ShammMaster-Setup-x64-{#MyAppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\XamaMaster\Assets\xamamaster.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
CloseApplicationsFilter=*.exe
UsePreviousAppDir=yes
DisableDirPage=auto
VersionInfoVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "x86\*,*.pdb,*.iss"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM XamaMaster.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;
