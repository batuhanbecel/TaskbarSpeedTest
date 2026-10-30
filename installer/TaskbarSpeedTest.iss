; TaskbarSpeedTest installer (Inno Setup 6). Build with build-installer.bat.
; Installs per user (no administrator rights), so background updates never show a UAC prompt.

#define AppExe "..\TaskbarSpeedTest.exe"
#define AppVersion RemoveFileExt(GetVersionNumbersString(AddBackslash(SourcePath) + AppExe))
#define Repo "https://github.com/batuhanbecel/TaskbarSpeedTest"

[Setup]
AppId={{ED13B013-062E-4549-9298-881DD82E2EDD}
AppName=TaskbarSpeedTest
AppVersion={#AppVersion}
AppVerName=TaskbarSpeedTest {#AppVersion}
AppPublisher=Batuhan Becel
AppPublisherURL={#Repo}
AppSupportURL={#Repo}/issues
AppUpdatesURL={#Repo}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\TaskbarSpeedTest
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=TaskbarSpeedTest-Setup-{#AppVersion}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\TaskbarSpeedTest.exe
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[CustomMessages]
en.Autostart=Start TaskbarSpeedTest when I sign in
tr.Autostart=Oturum açınca TaskbarSpeedTest'i başlat
en.Launch=Start TaskbarSpeedTest
tr.Launch=TaskbarSpeedTest'i başlat

[Tasks]
Name: "autostart"; Description: "{cm:Autostart}"

[Files]
Source: "{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"

[Icons]
Name: "{autoprograms}\TaskbarSpeedTest"; Filename: "{app}\TaskbarSpeedTest.exe"

[Registry]
Root: HKCU; Subkey: "Software\TaskbarSpeedTest"; Flags: uninsdeletekey
; The app starts in the language picked for the installer (an existing choice is kept).
Root: HKCU; Subkey: "Software\TaskbarSpeedTest"; ValueType: dword; ValueName: "Language"; ValueData: 1; Languages: tr; Flags: createvalueifdoesntexist
Root: HKCU; Subkey: "Software\TaskbarSpeedTest"; ValueType: dword; ValueName: "Language"; ValueData: 2; Languages: en; Flags: createvalueifdoesntexist
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TaskbarSpeedTest"; ValueData: """{app}\TaskbarSpeedTest.exe"" --autostart"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\TaskbarSpeedTest.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent
; Background updates run the setup with /VERYSILENT /RESTARTAPP=1: start the app again afterwards.
Filename: "{app}\TaskbarSpeedTest.exe"; Flags: nowait; Check: RestartAfterSilentUpdate

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM TaskbarSpeedTest.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopApp"

[Code]
function RestartAfterSilentUpdate: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RESTARTAPP|0}') = '1');
end;

{ "Start with Windows" unticked: remove an entry left by an earlier install or by the app menu. }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('autostart') and not WizardSilent then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'TaskbarSpeedTest');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  { Close a running copy so it can be replaced (updates). }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM TaskbarSpeedTest.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;
