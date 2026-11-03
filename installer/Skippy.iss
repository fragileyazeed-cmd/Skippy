; Skippy installer (Inno Setup 6). Built by tools\release.ps1:
;   ISCC.exe /DAppVersion=0.2.0 /DSourceDir=<publish folder> installer\Skippy.iss
; Per-user install (no admin / UAC), so the in-app updater can replace files later.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6E7B1C3A-5D2F-4F8E-9A41-3C0B7D2E9F15}
AppName=Skippy
AppVersion={#AppVersion}
AppVerName=Skippy {#AppVersion}
AppPublisher=Skippy
AppPublisherURL=https://github.com/fragileyazeed-cmd/Skippy
AppSupportURL=https://github.com/fragileyazeed-cmd/Skippy/issues
AppUpdatesURL=https://github.com/fragileyazeed-cmd/Skippy/releases
DefaultDirName={localappdata}\Programs\Skippy
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=SkippySetup-{#AppVersion}
SetupIconFile=..\src\Skippy\Skippy.ico
UninstallDisplayIcon={app}\Skippy.exe
UninstallDisplayName=Skippy
WizardStyle=modern
WizardSizePercent=100
Compression=lzma2/max
SolidCompression=yes
CloseApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Skippy
VersionInfoDescription=Skippy installer

[Tasks]
Name: autostart; Description: "Start Skippy when I sign in to Windows (recommended)"
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Skippy"; Filename: "{app}\Skippy.exe"; Comment: "Instant replay"
Name: "{userdesktop}\Skippy"; Filename: "{app}\Skippy.exe"; Tasks: desktopicon

[Registry]
; autostart entry + the StartupApproved flag Task Manager shows (02 = enabled, 03 = disabled)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Skippy"; ValueData: """{app}\Skippy.exe"" --autostart"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: binary; ValueName: "Skippy"; ValueData: "02 00 00 00 00 00 00 00 00 00 00 00"; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: binary; ValueName: "Skippy"; ValueData: "03 00 00 00 00 00 00 00 00 00 00 00"; Tasks: not autostart; Flags: uninsdeletevalue
; toast notification registration written by the app
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\Skippy.InstantReplay"; Flags: uninsdeletekey dontcreatekey

[Run]
Filename: "{app}\Skippy.exe"; Description: "Start Skippy now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; the updater may have added files the install log doesn't know about
Type: filesandordirs; Name: "{app}"
; settings + logs (clips in Videos\Skippy are kept)
Type: filesandordirs; Name: "{userappdata}\Skippy"
Type: filesandordirs; Name: "{localappdata}\Skippy"

[Code]
procedure StopSkippy();
var
  Code, Tries: Integer;
begin
  // taskkill returns 128 once no Skippy.exe is left; keep going until then so no file stays locked
  Tries := 0;
  repeat
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Skippy.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(400);
    Tries := Tries + 1;
  until (Code = 128) or (Tries >= 15);
  Sleep(600);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopSkippy();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopSkippy();
end;
