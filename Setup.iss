#ifndef AppVersion
  #error AppVersion must match the version built into CrossDesk.exe
#endif

[Setup]
AppId=DeskRelay
AppName=CrossDesk
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\CrossDesk
DefaultGroupName=CrossDesk
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
WizardStyle=modern
SetupIconFile=CrossDesk.ico
UninstallDisplayIcon={app}\CrossDesk.exe
CloseApplications=yes
RestartApplications=no
Compression=lzma2/fast
OutputDir=..\..\outputs
OutputBaseFilename=CrossDesk-Setup-{#AppVersion}-x64

[Files]
Source: "..\standalone\CrossDesk.exe"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
Type: files; Name: "{app}\DeskRelay.exe"
Type: files; Name: "{userprograms}\Desk Relay.lnk"

[Icons]
Name: "{userprograms}\CrossDesk"; Filename: "{app}\CrossDesk.exe"

[Run]
Filename: "{app}\CrossDesk.exe"; Description: "Open CrossDesk"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
var
  Installed, MessageText: String;
  InstalledVersion, SetupVersion: Int64;
begin
  Result := True;
  if not RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskRelay_is1',
    'DisplayVersion', Installed) then
    Exit;

  if StrToVersion(Installed, InstalledVersion) and
     StrToVersion('{#AppVersion}', SetupVersion) and
     (InstalledVersion > SetupVersion) then
  begin
    SuppressibleMsgBox('CrossDesk ' + Installed + ' is already installed. Use that version or a newer installer.',
      mbInformation, MB_OK, IDOK);
    Result := False;
    Exit;
  end;

  if Installed = '{#AppVersion}' then
    MessageText := 'CrossDesk ' + Installed + ' is already installed. Repair or reinstall it by replacing the program files?'
  else
    MessageText := 'Update CrossDesk from ' + Installed + ' to {#AppVersion}?';

  Result := SuppressibleMsgBox(MessageText, mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
end;
