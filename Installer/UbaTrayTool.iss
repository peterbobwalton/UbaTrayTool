; UbaTrayTool installer (Inno Setup 6).
; Build with build.ps1 (or build the Installer project in Release in Visual Studio), which publishes
; the app self-contained into the staging folder first and then compiles this script.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "publish"
#endif
#define AppExe "UbaTrayTool.exe"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
AppId={{8E3F2B71-6C4D-4A9B-9E15-0B7D52C4A1F3}
AppName=UBA Tray Tool
AppVersion={#AppVersion}
AppVerName=UBA Tray Tool {#AppVersion}
AppPublisher=Pete Walton
AppPublisherURL=https://github.com/peterbobwalton/UbaTrayTool
AppSupportURL=https://github.com/peterbobwalton/UbaTrayTool/issues
DefaultDirName={autopf}\UbaTrayTool
DefaultGroupName=UBA Tray Tool
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; "Start with Windows" lives under HKCU, matching the app's own menu option.
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=output
OutputBaseFilename=UbaTrayToolSetup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\UbaTrayTool\app.ico
UninstallDisplayName=UBA Tray Tool
UninstallDisplayIcon={app}\{#AppExe}
; The app is stopped explicitly (see [Code]); a tray app has no window for Restart Manager to close.
CloseApplications=no

[Tasks]
Name: "startup"; Description: "Start UBA Tray Tool with Windows"; GroupDescription: "Options:"
Name: "suppressvisualizer"; Description: "Stop UBA from opening UbaVisualizer (the tray tool replaces it)"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\UBA Tray Tool"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall UBA Tray Tool"; Filename: "{uninstallexe}"

[Registry]
; Same value name and quoting as StartupRegistration.cs, so the menu item shows as ticked.
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "UbaTrayTool"; ValueData: """{app}\{#AppExe}"""; Tasks: startup
; Same setting as the tray menu's "Stop UBA launching UbaVisualizer".
Root: HKCU; Subkey: "Software\UbaTrayTool"; ValueType: dword; ValueName: "SuppressVisualizer"; ValueData: 1; Tasks: suppressvisualizer

[Run]
; runasoriginaluser: not elevated like the installer, so it sees the same session objects as UBT.
Filename: "{app}\{#AppExe}"; Description: "Launch UBA Tray Tool"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
{ A tray app has no main window, so close it the hard way before files are replaced or removed. }
procedure StopApp();
var
  ResultCode: Integer;
begin
  { Non-zero exit just means it wasn't running. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopApp();
  Result := True;
end;

{ Remove the startup entry however it was made: by this installer or from the app's own menu. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU, '{#RunKey}', 'UbaTrayTool');
end;
