; PixelPipe Windows installer. Build it with scripts\build-windows.ps1.

#define MyAppName "PixelPipe"
#define MyAppVersion "0.2.3"
#define MyAppPublisher "PixelPipe"

[Setup]
AppId={{B7E1C4A2-9D55-4F0E-8A61-3C2D9E7F4A18}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion=0.2.3.0
VersionInfoProductVersion=0.2.3
DefaultDirName={autopf}\PixelPipe
DefaultGroupName=PixelPipe
DisableProgramGroupPage=yes
UsePreviousTasks=yes
CloseApplications=force
RestartApplications=no
OutputDir=..\dist
OutputBaseFilename=PixelPipe-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern

[Files]
Source: "..\dist\windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: startup; Description: "Start PixelPipe when you sign in (recommended; this account can see \\server\share)"; GroupDescription: "How PixelPipe runs:"; Flags: exclusive
Name: service; Description: "Run as a Windows service (LocalSystem). A password-protected network folder is usually invisible until you set the service account."; GroupDescription: "How PixelPipe runs:"; Flags: exclusive unchecked
Name: firewall; Description: "Open the firewall for the web UI (TCP 8080), the engine (TCP 9090), and WebRTC (UDP 50000-50100)"; Flags: checkedonce

[Icons]
Name: "{group}\PixelPipe"; Filename: "{app}\PixelPipe.url"
Name: "{userstartup}\PixelPipe"; Filename: "{app}\PixelPipe.Tray.exe"; Tasks: startup

[Run]
Filename: "{app}\PixelPipe.Tray.exe"; Description: "Start PixelPipe"; Flags: nowait postinstall skipifsilent; Tasks: startup
Filename: "http://127.0.0.1:8080"; Description: "Open PixelPipe in the browser"; Flags: shellexec postinstall skipifsilent unchecked nowait

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM PixelPipe.Tray.exe /T"; Flags: runhidden; RunOnceId: "StopPixelPipeTray"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM PixelPipe.Web.exe /T"; Flags: runhidden; RunOnceId: "StopPixelPipeWeb"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM pixelpipe-engine.exe /T"; Flags: runhidden; RunOnceId: "StopPixelPipeEngine"
Filename: "{app}\pixelpipe-engine.exe"; Parameters: "--uninstall-service"; Flags: runhidden; RunOnceId: "RemovePixelPipeService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PixelPipe UI"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PixelPipe Engine"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PixelPipe WebRTC"""; Flags: runhidden

[Code]
var
  RomsPage: TWizardPage;
  RomsEdit: TNewEdit;

{ CreateInputDirPage refuses a missing folder and offers to create it.
  A blank path and a UNC share such as \\unas-pro\roms must both be allowed,
  and the installer must not create the folder. }
procedure BrowseRomsClick(Sender: TObject);
var
  Dir: String;
begin
  Dir := Trim(RomsEdit.Text);
  if BrowseForFolder('Mapped drives such as M: may not be listed because Setup is running as administrator. Cancel and type the path, or choose the folder later in Settings.', Dir, False) then
    RomsEdit.Text := Dir;
end;

procedure InitializeWizard;
var
  Prompt: TNewStaticText;
  Browse: TNewButton;
begin
  RomsPage := CreateCustomPage(wpSelectTasks,
    'ROM folder',
    'Where should PixelPipe look for ROMs?');

  Prompt := TNewStaticText.Create(RomsPage);
  Prompt.Parent := RomsPage.Surface;
  Prompt.Caption :=
    'Leave this blank to play the bundled demo ROMs, or type a path such as M:\Games\ROMs or \\unas-pro\roms. ' +
    'The path is saved as you type it. Setup runs as administrator, so Browse may not list mapped drives; type the drive letter or use the folder browser in Settings after install. PixelPipe does not create the folder.';
  Prompt.AutoSize := False;
  Prompt.WordWrap := True;
  Prompt.Left := 0;
  Prompt.Top := 0;
  Prompt.Width := RomsPage.SurfaceWidth;
  Prompt.Height := ScaleY(96);

  RomsEdit := TNewEdit.Create(RomsPage);
  RomsEdit.Parent := RomsPage.Surface;
  RomsEdit.Left := 0;
  RomsEdit.Top := Prompt.Top + Prompt.Height + ScaleY(8);
  RomsEdit.Width := RomsPage.SurfaceWidth - ScaleX(96);
  RomsEdit.Text := '';

  Browse := TNewButton.Create(RomsPage);
  Browse.Parent := RomsPage.Surface;
  Browse.Caption := 'Browse...';
  Browse.Left := RomsEdit.Left + RomsEdit.Width + ScaleX(8);
  Browse.Top := RomsEdit.Top - ScaleY(1);
  Browse.Width := ScaleX(88);
  Browse.Height := ScaleY(25);
  Browse.OnClick := @BrowseRomsClick;
end;

function JsonQuote(const S: String): String;
var
  I: Integer;
  C: String;
begin
  Result := '"';
  for I := 1 to Length(S) do
  begin
    { Copy returns a string. S[I] is a Char, and Pascal Script rejects
      Char-to-String comparisons and concatenation at runtime. }
    C := Copy(S, I, 1);
    if C = '\' then
      Result := Result + '\\'
    else if C = '"' then
      Result := Result + '\"'
    else
      Result := Result + C;
  end;
  Result := Result + '"';
end;

function ShouldWriteConfig: Boolean;
var
  Param, Typed: String;
begin
  Param := Trim(ExpandConstant('{param:ROMDIR|}'));
  if Param <> '' then
  begin
    Result := True;
    Exit;
  end;
  Typed := '';
  if RomsEdit <> nil then
    Typed := Trim(RomsEdit.Text);
  if Typed <> '' then
  begin
    Result := True;
    Exit;
  end;
  Result := not FileExists(ExpandConstant('{commonappdata}\PixelPipe\config.json'));
end;

function ChosenRoms: String;
var
  FromParam, FromPage: String;
begin
  FromParam := Trim(ExpandConstant('{param:ROMDIR|}'));
  if FromParam <> '' then
  begin
    Result := FromParam;
    Exit;
  end;
  FromPage := '';
  if RomsEdit <> nil then
    FromPage := Trim(RomsEdit.Text);
  if FromPage <> '' then
    Result := FromPage
  else
    Result := ExpandConstant('{app}\demo-roms');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigDir, Json, App: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  App := ExpandConstant('{app}');
  ConfigDir := ExpandConstant('{commonappdata}\PixelPipe');
  ForceDirectories(ConfigDir);
  { An upgrade with a blank ROM page keeps the existing library path and saves. }
  if ShouldWriteConfig then
  begin
    Json :=
      '{' + #13#10 +
      '  "roms": ' + JsonQuote(ChosenRoms) + ',' + #13#10 +
      '  "cores": ' + JsonQuote(App + '\cores') + ',' + #13#10 +
      '  "data": ' + JsonQuote(ConfigDir) + ',' + #13#10 +
      '  "bind": "0.0.0.0:9090",' + #13#10 +
      '  "webrtc_port_min": 50000,' + #13#10 +
      '  "webrtc_port_max": 50100' + #13#10 +
      '}' + #13#10;
    SaveStringToFile(ConfigDir + '\config.json', Json, False);
  end;
  if WizardIsTaskSelected('startup') then
    SaveStringToFile(ConfigDir + '\launch-mode.txt', 'tray', False)
  else
    SaveStringToFile(ConfigDir + '\launch-mode.txt', 'service', False);

  if WizardIsTaskSelected('firewall') then
  begin
    Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="PixelPipe UI"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall add rule name="PixelPipe UI" dir=in action=allow protocol=TCP localport=8080', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="PixelPipe Engine"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall add rule name="PixelPipe Engine" dir=in action=allow protocol=TCP localport=9090', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="PixelPipe WebRTC"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall add rule name="PixelPipe WebRTC" dir=in action=allow protocol=UDP localport=50000-50100', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  if WizardIsTaskSelected('service') then
  begin
    Exec(App + '\pixelpipe-engine.exe', '--install-service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\sc.exe'), 'start PixelPipe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end
  else if WizardIsTaskSelected('startup') and WizardSilent then
  begin
    { Interactive installs use the finish-page checkbox, which starts the tray. }
    Exec(App + '\PixelPipe.Tray.exe', '', '', SW_HIDE, ewNoWait, ResultCode);
  end;
end;
