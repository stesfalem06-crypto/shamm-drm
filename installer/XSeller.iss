; X Seller — Inno Setup 6. Same AppId across versions → installing a newer build upgrades in place
; (closes the running app, replaces files, keeps Documents\XSeller data + ProgramData backups).
#define MyAppName "X Seller"
#define MyAppVersion "0.5.0"
#define MyAppPublisher "Shamm"
#define MyAppExeName "XSeller.exe"
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{A7E3C4B1-9F2D-4E8A-B6C1-5D0F8A2E7B93}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\XSeller
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=output
#if Arch == "x86"
OutputBaseFilename=XSeller-Setup-x86-{#MyAppVersion}
ArchitecturesAllowed=x86compatible
#else
OutputBaseFilename=XSeller-Setup-x64-{#MyAppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\XSeller\Assets\xseller.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
; Keep user data: we never touch Documents\XSeller or ProgramData backups.
UsePreviousAppDir=yes
DisableDirPage=auto
InfoBeforeFile=
VersionInfoVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: checkedonce

[Files]
; Everything from the publish folder (exe, shamm_crypto.dll, platform-tools, drivers, …)
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "x86\*,*.pdb,*.iss"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
; Optional: install USB drivers (asks for elevation once)
Filename: "{app}\drivers\install-drivers.cmd"; Parameters: "/quiet"; Flags: runhidden skipifdoesntexist; StatusMsg: "Installing phone USB drivers…"; Check: DriverPrompt

[Code]
function DriverPrompt: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\drivers\install-drivers.cmd'));
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  // Close a running instance so files aren't locked.
  Exec('taskkill.exe', '/F /IM XSeller.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;
