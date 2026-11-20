; PixelPipe Windows installer. Build it with scripts\build-windows.ps1.

#define MyAppName "PixelPipe"
#define MyAppVersion "0.2.1"
#define MyAppPublisher "PixelPipe"

[Setup]
AppId={{B7E1C4A2-9D55-4F0E-8A61-3C2D9E7F4A18}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\PixelPipe
DefaultGroupName=PixelPipe
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=PixelPipe-Setup
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
Name: "{userstartup}\PixelPipe"; Filename: "{app}\pixelpipe-engine.exe"; Tasks: startup

[UninstallRun]
Filename: "{app}\pixelpipe-engine.exe"; Parameters: "--uninstall-service"; Flags: runhidden; RunOnceId: "RemovePixelPipeService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PixelPipe UI"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PixelPipe Engine"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PixelPipe WebRTC"""; Flags: runhidden

[Code]
var
  RomsPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  RomsPage := CreateInputQueryPage(wpSelectTasks,
    'ROM folder',
    'Where should PixelPipe look for ROMs?',
    'Leave this blank to play the bundled demo ROMs. A UNC path such as \\unas-pro\roms is accepted. Your ROMs are not copied. A silent install can pass /ROMDIR=C:\roms instead.');
  { The second argument is "password edit", not a default value. }
  RomsPage.Add('ROM folder:', False);
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
  if RomsPage <> nil then
    FromPage := Trim(RomsPage.Values[0]);
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
  else if WizardIsTaskSelected('startup') then
  begin
    Exec(App + '\pixelpipe-engine.exe', '', '', SW_HIDE, ewNoWait, ResultCode);
  end;
end;
