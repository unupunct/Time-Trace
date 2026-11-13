; Inno Setup script for TimeTrace. Build with:  ISCC.exe installer\TimeTrace.iss  (after publishing to artifacts\publish)
; The version is passed in by tools\build-release.ps1 (/DAppVersion=1.0.0); default below.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "TimeTrace"
#define AppExe "TimeTrace.exe"
#define AppPublisher "TimeTrace contributors"

[Setup]
; Fixed AppId = upgrades install over the previous version instead of side by side.
AppId={{B7C2D5A1-6E3F-4C9B-9A57-3D8F0E1A2B64}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright (c) 2026 TimeTrace contributors
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=TimeTrace Setup - Understand where your time goes.
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
; Per-user install by default: no administrator rights needed. The dialog lets an admin choose "all users" (Program Files).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
UsePreviousAppDir=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupIconFile=..\src\TimeTrace.App\Assets\TimeTrace.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE.txt
InfoBeforeFile=privacy.txt
OutputDir=..\release
OutputBaseFilename=TimeTrace_Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; If TimeTrace is running (it lives in the tray), Setup asks it to exit first (see [Code]).
AppMutex=TimeTrace.SingleInstance
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\release\README.txt"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\release\CHANGELOG.txt"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Launching is optional and never starts tracking by itself: TimeTrace asks for consent on first run.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only program files are removed automatically. Activity data lives in %LOCALAPPDATA%\TimeTrace and is handled in [Code].
Type: filesandordirs; Name: "{app}"

[Code]
procedure AskRunningInstanceToExit(const Dir: String);
var
  ResultCode: Integer;
begin
  if FileExists(Dir + '\{#AppExe}') then
  begin
    Exec(Dir + '\{#AppExe}', '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  AskRunningInstanceToExit(ExpandConstant('{app}'));
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  AskRunningInstanceToExit(ExpandConstant('{app}'));
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Remove the optional "start with Windows" entry that the app itself may have created.
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'TimeTrace');
    DataDir := ExpandConstant('{localappdata}\TimeTrace');
    if DirExists(DataDir) and (not UninstallSilent) then
    begin
      if MsgBox('Do you also want to delete your TimeTrace activity data (database, settings and logs)?' + #13#10 + #13#10 +
                DataDir + #13#10 + #13#10 +
                'Choose No to keep your data, for example if you plan to reinstall TimeTrace.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
