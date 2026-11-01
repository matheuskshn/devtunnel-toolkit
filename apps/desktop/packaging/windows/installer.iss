; Per-user installer for the same files distributed in the portable ZIP.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef StageDir
  #error StageDir is required
#endif
#ifndef TargetArchitecture
  #error TargetArchitecture is required
#endif
#ifndef RuntimeIdentifier
  #error RuntimeIdentifier is required
#endif

[Setup]
AppId={{F5DB0F65-513E-4B13-990E-5069ADFA75DA}
AppName=DevTunnel Toolkit Desktop
AppVersion={#AppVersion}
AppPublisher=DevTunnel Toolkit contributors
AppPublisherURL=https://github.com/matheuskshn/devtunnel-toolkit
DefaultDirName={localappdata}\Programs\DevTunnel Toolkit Desktop
DefaultGroupName=DevTunnel Toolkit Desktop
PrivilegesRequired=lowest
ArchitecturesAllowed={#TargetArchitecture}
OutputDir=.
OutputBaseFilename=devtunnel-toolkit-desktop-{#AppVersion}-{#RuntimeIdentifier}-setup
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\DevTunnelToolkit.App.exe
WizardStyle=modern
SetupIconFile=..\branding\app.ico
WizardImageFile=..\branding\installer-panel.bmp
WizardSmallImageFile=..\branding\installer-small.bmp
WizardImageStretch=yes
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\DevTunnel Toolkit Desktop"; Filename: "{app}\DevTunnelToolkit.App.exe"
Name: "{autodesktop}\DevTunnel Toolkit Desktop"; Filename: "{app}\DevTunnelToolkit.App.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos adicionais:"; Flags: unchecked

[Code]
// Remove only startup values owned by this app and pointing to this installation.
// User profiles and credential vault entries are intentionally preserved.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Names: TArrayOfString;
  I: Integer;
  RunValue, OwnerValue, Prefix: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  if not RegGetValueNames(HKCU, 'Software\DevTunnelToolkit\Startup', Names) then Exit;
  Prefix := '"' + ExpandConstant('{app}\agent\DevTunnelToolkit.Agent.exe') + '" --data-dir ';
  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    if Pos('devtunnel-toolkit-agent-', Names[I]) <> 1 then Continue;
    if RegQueryStringValue(HKCU, 'Software\DevTunnelToolkit\Startup', Names[I], OwnerValue) and
       RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', Names[I], RunValue) and
       (CompareText(OwnerValue, RunValue) = 0) and
       (CompareText(Copy(RunValue, 1, Length(Prefix)), Prefix) = 0) then
    begin
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', Names[I]);
      RegDeleteValue(HKCU, 'Software\DevTunnelToolkit\Startup', Names[I]);
    end;
  end;
end;
