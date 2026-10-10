; InferHub Node — Windows setup (phase 101).
;
; Installs InferHub.Node.Service.exe as the "InferHubNode" Windows service and asks how to run it. Its
; answers go to %ProgramData%\InferHub\Node\node.settings.json through the exe's own `configure` verb (D1),
; never into Program Files, so the next setup — run by hand or by the node's own updater — replaces every
; program file and loses no setting.
;
; Build: deploy/windows/installer/build-installer.ps1 (publishes, then runs ISCC with the defines below).
;   ISCC /DAppVersion=3.66.0 /DSourceDir=<publish dir> /DOutputDir=<dir> InferHubNode.iss
;
; Silent use (every parameter optional; a silent run with none of them keeps the current settings, which is
; exactly what an update does):
;   InferHub-Node-Setup-X.Y.Z-win-x64.exe /VERYSILENT /SUPPRESSMSGBOXES
;     /Mode=mesh|solo /CoordinatorUrl=https://hub:5080/ /EnrollmentSecret=...
;     /LocalApiUrls=http://localhost:5081 /LocalApiKey=...
;     /NodeName=gpu-01 /Labels=gpu=3090,room=lab /MaxConcurrency=4 /VramBudgetMiB=24000
;     /OllamaEndpoint=http://localhost:11434/ /Account=system|virtual /Updates=auto|request|report|off
;     /UpdateSource=<a mirror of GitHub's release list>   (not in the wizard; written only when given)

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<the dotnet publish output of src/InferHub.Node.WindowsService>
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#define RepoRoot AddBackslash(SourcePath) + "..\..\.."
#define ServiceName "InferHubNode"
#define ServiceExe "InferHub.Node.Service.exe"

[Setup]
AppId={{6F4B2E3A-9C1D-4E57-8A2B-1D0C7E5F9A31}
AppName=InferHub Node
AppVersion={#AppVersion}
AppVerName=InferHub Node {#AppVersion}
AppPublisher=Dev Art Solutions
AppPublisherURL=https://inferhub.devart.solutions
AppSupportURL=https://github.com/Dev-Art-Solutions/InferHub/issues
AppUpdatesURL=https://github.com/Dev-Art-Solutions/InferHub/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\InferHub\Node
DefaultGroupName=InferHub Node
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile={#RepoRoot}\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=InferHub-Node-Setup-{#AppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The service is stopped by PrepareToInstall below, and only that: Restart Manager would try to close a
; service it does not know how to restart.
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\{#ServiceExe}
UninstallDisplayName=InferHub Node
SetupLogging=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,appsettings.Development.json,\data\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{commonappdata}\InferHub\Node"

[Icons]
Name: "{group}\Check for InferHub Node updates"; Filename: "{app}\{#ServiceExe}"; Parameters: "update --pause"; Comment: "Look for a newer InferHub release and install it"
Name: "{group}\Uninstall InferHub Node"; Filename: "{uninstallexe}"

[Code]
const
  ServiceName = '{#ServiceName}';
  VirtualAccount = 'NT SERVICE\{#ServiceName}';

var
  ModePage: TInputOptionWizardPage;
  CoordPage: TInputQueryWizardPage;
  SoloPage: TInputQueryWizardPage;
  NodePage: TInputQueryWizardPage;
  BackendPage: TInputQueryWizardPage;
  AccountPage: TInputOptionWizardPage;
  UpdatePage: TInputOptionWizardPage;
  ServiceWasStopped: Boolean;
  InstallCompleted: Boolean;

{ ---------------------------------------------------------------- helpers }

function SettingsDir: String;
begin
  Result := ExpandConstant('{commonappdata}\InferHub\Node');
end;

function SettingsFile: String;
begin
  Result := SettingsDir + '\node.settings.json';
end;

{ A command-line parameter (/Name=value), else the answer this setup was given last time, else Default. }
function Answer(Name, Default: String): String;
begin
  Result := ExpandConstant('{param:' + Name + '|' + GetPreviousData(Name, Default) + '}');
end;

function ParamGiven(Name: String): Boolean;
begin
  Result := ExpandConstant('{param:' + Name + '|*none*}') <> '*none*';
end;

function AnyParamGiven: Boolean;
begin
  Result := ParamGiven('Mode') or ParamGiven('CoordinatorUrl') or ParamGiven('EnrollmentSecret')
    or ParamGiven('LocalApiUrls') or ParamGiven('LocalApiKey') or ParamGiven('NodeName')
    or ParamGiven('Labels') or ParamGiven('MaxConcurrency') or ParamGiven('VramBudgetMiB')
    or ParamGiven('OllamaEndpoint') or ParamGiven('Account') or ParamGiven('Updates')
    or ParamGiven('UpdateSource');
end;

{ Write the answers on an interactive run, on a silent run that was given any, and whenever there are no
  settings at all yet. A silent run with none — the node updating itself — keeps every setting. }
function NeedConfigure: Boolean;
begin
  Result := (not WizardSilent) or AnyParamGiven or (not FileExists(SettingsFile));
end;

function IsSolo: Boolean;
begin
  Result := ModePage.SelectedValueIndex = 1;
end;

function IsVirtualAccount: Boolean;
begin
  Result := AccountPage.SelectedValueIndex = 1;
end;

function UpdatesValue: String;
begin
  case UpdatePage.SelectedValueIndex of
    0: Result := 'auto';
    1: Result := 'request';
    2: Result := 'report';
  else
    Result := 'off';
  end;
end;

function IsDigits(S: String): Boolean;
var
  I: Integer;
begin
  Result := Length(S) > 0;
  for I := 1 to Length(S) do
    if (S[I] < '0') or (S[I] > '9') then
      Result := False;
end;

function IsLoopbackUrls(Urls: String): Boolean;
var
  L: String;
begin
  L := Lowercase(Urls);
  Result := ((Pos('localhost', L) > 0) or (Pos('127.0.0.1', L) > 0) or (Pos('[::1]', L) > 0))
    and (Pos('0.0.0.0', L) = 0) and (Pos('*', L) = 0) and (Pos('+', L) = 0);
end;

function LabelsValid(Labels: String): Boolean;
var
  S, Item: String;
  P: Integer;
begin
  Result := True;
  S := Labels;
  while Length(S) > 0 do
  begin
    P := Pos(',', S);
    if P = 0 then begin Item := S; S := ''; end
    else begin Item := Copy(S, 1, P - 1); Delete(S, 1, P); end;
    Item := Trim(Item);
    if (Item <> '') and (Pos('=', Item) < 2) then
      Result := False;
  end;
end;

procedure AddLine(var Lines: TArrayOfString; Line: String);
begin
  SetArrayLength(Lines, GetArrayLength(Lines) + 1);
  Lines[GetArrayLength(Lines) - 1] := Line;
end;

function Run(FileName, Params: String): Integer;
begin
  Log('Running: ' + FileName + ' ' + Params);
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
  Log('  exit code ' + IntToStr(Result));
end;

function Sc(Params: String): Integer;
begin
  Result := Run(ExpandConstant('{sys}\sc.exe'), Params);
end;

function PowerShell(Command: String): Integer;
begin
  Result := Run(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Command + '"');
end;

function ServiceExists: Boolean;
begin
  Result := Sc('query ' + ServiceName) = 0;
end;

{ Stops the service and waits for its process to be gone: a file the old exe still holds cannot be replaced.
  The service's own shutdown grace is 30 s (in-flight jobs drain); the leftover kill catches an `update`
  console that started this setup and has not exited yet. }
procedure StopService;
begin
  PowerShell('$s = Get-Service ' + ServiceName + ' -ErrorAction SilentlyContinue; ' +
    'if ($s -and $s.Status -ne ''Stopped'') { Stop-Service ' + ServiceName + ' -Force -ErrorAction SilentlyContinue; ' +
    'try { $s.WaitForStatus(''Stopped'', [TimeSpan]::FromSeconds(90)) } catch { } }; ' +
    'Wait-Process -Name InferHub.Node.Service -Timeout 30 -ErrorAction SilentlyContinue; ' +
    'Stop-Process -Name InferHub.Node.Service -Force -ErrorAction SilentlyContinue');
end;

{ ---------------------------------------------------------------- wizard }

procedure InitializeWizard;
var
  Mode, Account, Updates: String;
begin
  ModePage := CreateInputOptionPage(wpSelectDir,
    'Node mode', 'How will this node serve requests?',
    'Choose one, then click Next.', True, False);
  ModePage.Add('Join a coordinator — take jobs from an InferHub hub (the usual mesh node)');
  ModePage.Add('Solo — answer clients on a local API, with no coordinator');
  Mode := Lowercase(Answer('Mode', 'mesh'));
  if Mode = 'solo' then ModePage.SelectedValueIndex := 1 else ModePage.SelectedValueIndex := 0;

  CoordPage := CreateInputQueryPage(ModePage.ID,
    'Coordinator', 'Where is the hub, and what is its enrollment secret?',
    'The secret must match the coordinator''s Auth:NodeEnrollmentSecret. When updating an existing node, leave it empty to keep the current one.');
  CoordPage.Add('Coordinator URL:', False);
  CoordPage.Add('Enrollment secret:', True);
  CoordPage.Values[0] := Answer('CoordinatorUrl', 'http://localhost:5080/');
  CoordPage.Values[1] := ExpandConstant('{param:EnrollmentSecret|}');

  SoloPage := CreateInputQueryPage(CoordPage.ID,
    'Local API', 'Where do clients reach this node?',
    'Clients call its Ollama- and OpenAI-shaped API here. http://localhost:5081 serves this computer only; ' +
    'http://0.0.0.0:5081 serves the network and needs an API key (and the port allowed in Windows Firewall). ' +
    'When updating, leave the key empty to keep the current one.');
  SoloPage.Add('Listen on:', False);
  SoloPage.Add('API key (clients send it as a Bearer token):', True);
  SoloPage.Values[0] := Answer('LocalApiUrls', 'http://localhost:5081');
  SoloPage.Values[1] := ExpandConstant('{param:LocalApiKey|}');

  NodePage := CreateInputQueryPage(SoloPage.ID,
    'This node', 'How the hub and its console know this computer',
    'Labels are key=value pairs separated by commas (gpu=3090, room=lab). Leave max concurrency empty for no cap; ' +
    'a VRAM budget of 0 means none.');
  NodePage.Add('Node name:', False);
  NodePage.Add('Labels:', False);
  NodePage.Add('Max concurrent jobs:', False);
  NodePage.Add('VRAM budget (MiB):', False);
  NodePage.Values[0] := Answer('NodeName', GetComputerNameString);
  NodePage.Values[1] := Answer('Labels', '');
  NodePage.Values[2] := Answer('MaxConcurrency', '');
  NodePage.Values[3] := Answer('VramBudgetMiB', '0');

  BackendPage := CreateInputQueryPage(NodePage.ID,
    'Ollama', 'Where does this node''s Ollama answer?',
    'Install Ollama (https://ollama.com) first. Other engines — llama.cpp, colibri, Strata — are configured ' +
    'afterwards in ' + SettingsFile + '; see deploy/windows/README.md.');
  BackendPage.Add('Ollama endpoint:', False);
  BackendPage.Values[0] := Answer('OllamaEndpoint', 'http://localhost:11434/');

  AccountPage := CreateInputOptionPage(BackendPage.ID,
    'Service account', 'Which account runs the InferHub Node service?',
    'The service starts with Windows (delayed, so the network and Ollama come up first) and restarts if it fails.',
    True, False);
  AccountPage.Add('LocalSystem — can apply updates and restart a local Ollama service (recommended)');
  AccountPage.Add('Virtual account ' + VirtualAccount + ' — least privilege; cannot apply updates');
  Account := Lowercase(Answer('Account', 'system'));
  if Account = 'virtual' then AccountPage.SelectedValueIndex := 1 else AccountPage.SelectedValueIndex := 0;

  UpdatePage := CreateInputOptionPage(AccountPage.ID,
    'Updates', 'How should this node stay current?',
    'Releases come from github.com/Dev-Art-Solutions/InferHub, checked every 6 hours. A release is code this ' +
    'node will run. Whatever you choose, "Check for InferHub Node updates" in the Start menu updates it by hand.',
    True, False);
  UpdatePage.Add('Automatically — install a new release by itself once it is idle');
  UpdatePage.Add('When an admin says — report it to the coordinator, update when Update is pressed in its console');
  UpdatePage.Add('Report only — show new releases; I update this node by hand');
  UpdatePage.Add('Never — do not look for releases');
  Updates := Lowercase(Answer('Updates', 'auto'));
  if Updates = 'request' then UpdatePage.SelectedValueIndex := 1
  else if Updates = 'report' then UpdatePage.SelectedValueIndex := 2
  else if Updates = 'off' then UpdatePage.SelectedValueIndex := 3
  else UpdatePage.SelectedValueIndex := 0;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = CoordPage.ID then Result := IsSolo;
  if PageID = SoloPage.ID then Result := not IsSolo;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Url: String;
begin
  Result := True;

  if CurPageID = CoordPage.ID then
  begin
    Url := Lowercase(Trim(CoordPage.Values[0]));
    if (Pos('http://', Url) <> 1) and (Pos('https://', Url) <> 1) then
    begin
      SuppressibleMsgBox('The coordinator URL must start with http:// or https://.', mbError, MB_OK, IDOK);
      Result := False;
    end
    else if (Trim(CoordPage.Values[1]) = '') and (not FileExists(SettingsFile))
      and (SuppressibleMsgBox('No enrollment secret: the coordinator will refuse this node until one is set in ' +
        SettingsFile + '. Continue anyway?', mbConfirmation, MB_YESNO, IDYES) = IDNO) then
      Result := False;
  end;

  if CurPageID = SoloPage.ID then
  begin
    if (not IsLoopbackUrls(SoloPage.Values[0])) and (Trim(SoloPage.Values[1]) = '')
      and (GetPreviousData('HasLocalApiKey', 'no') <> 'yes') then
    begin
      SuppressibleMsgBox('A node listening beyond this computer needs an API key: without one it refuses to start ' +
        'rather than serve anyone who can reach it.', mbError, MB_OK, IDOK);
      Result := False;
    end;
  end;

  if CurPageID = NodePage.ID then
  begin
    if Trim(NodePage.Values[0]) = '' then
    begin
      SuppressibleMsgBox('The node needs a name.', mbError, MB_OK, IDOK);
      Result := False;
    end
    else if not LabelsValid(NodePage.Values[1]) then
    begin
      SuppressibleMsgBox('Labels are key=value pairs separated by commas, e.g. gpu=3090, room=lab.', mbError, MB_OK, IDOK);
      Result := False;
    end
    else if (Trim(NodePage.Values[2]) <> '') and not IsDigits(Trim(NodePage.Values[2])) then
    begin
      SuppressibleMsgBox('Max concurrent jobs is a whole number, or empty for no cap.', mbError, MB_OK, IDOK);
      Result := False;
    end
    else if not IsDigits(Trim(NodePage.Values[3])) then
    begin
      SuppressibleMsgBox('The VRAM budget is a whole number of MiB (0 for none).', mbError, MB_OK, IDOK);
      Result := False;
    end;
  end;

  if CurPageID = UpdatePage.ID then
  begin
    if IsVirtualAccount and (UpdatePage.SelectedValueIndex <= 1) then
    begin
      SuppressibleMsgBox('A node running as ' + VirtualAccount + ' cannot run a setup, so it cannot apply updates. ' +
        'Go back and choose LocalSystem, or choose "Report only".', mbError, MB_OK, IDOK);
      Result := False;
    end
    else if IsSolo and (UpdatePage.SelectedValueIndex = 1) then
    begin
      SuppressibleMsgBox('A solo node has no coordinator to press Update. Choose automatic or report only.', mbError, MB_OK, IDOK);
      Result := False;
    end;
  end;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
var
  Mode, Account, HasKey: String;
begin
  if IsSolo then Mode := 'solo' else Mode := 'mesh';
  if IsVirtualAccount then Account := 'virtual' else Account := 'system';
  HasKey := GetPreviousData('HasLocalApiKey', 'no');
  if IsSolo and (Trim(SoloPage.Values[1]) <> '') then HasKey := 'yes';

  SetPreviousData(PreviousDataKey, 'Mode', Mode);
  SetPreviousData(PreviousDataKey, 'CoordinatorUrl', Trim(CoordPage.Values[0]));
  SetPreviousData(PreviousDataKey, 'LocalApiUrls', Trim(SoloPage.Values[0]));
  SetPreviousData(PreviousDataKey, 'HasLocalApiKey', HasKey);
  SetPreviousData(PreviousDataKey, 'NodeName', Trim(NodePage.Values[0]));
  SetPreviousData(PreviousDataKey, 'Labels', Trim(NodePage.Values[1]));
  SetPreviousData(PreviousDataKey, 'MaxConcurrency', Trim(NodePage.Values[2]));
  SetPreviousData(PreviousDataKey, 'VramBudgetMiB', Trim(NodePage.Values[3]));
  SetPreviousData(PreviousDataKey, 'OllamaEndpoint', Trim(BackendPage.Values[0]));
  SetPreviousData(PreviousDataKey, 'Account', Account);
  SetPreviousData(PreviousDataKey, 'Updates', UpdatesValue);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  S: String;
begin
  S := MemoDirInfo + NewLine + NewLine;
  if IsSolo then
    S := S + 'Mode:' + NewLine + Space + 'solo, listening on ' + Trim(SoloPage.Values[0]) + NewLine
  else
    S := S + 'Mode:' + NewLine + Space + 'mesh node of ' + Trim(CoordPage.Values[0]) + NewLine;
  S := S + 'Node:' + NewLine + Space + Trim(NodePage.Values[0]);
  if Trim(NodePage.Values[1]) <> '' then S := S + ' (' + Trim(NodePage.Values[1]) + ')';
  S := S + NewLine + 'Ollama:' + NewLine + Space + Trim(BackendPage.Values[0]) + NewLine;
  if IsVirtualAccount then
    S := S + 'Service account:' + NewLine + Space + VirtualAccount + NewLine
  else
    S := S + 'Service account:' + NewLine + Space + 'LocalSystem' + NewLine;
  S := S + 'Updates:' + NewLine + Space + UpdatesValue + NewLine;
  if not NeedConfigure then
    S := S + NewLine + 'The current settings in ' + SettingsFile + ' are kept.';
  Result := S;
end;

{ ---------------------------------------------------------------- install }

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if ServiceExists then
  begin
    StopService;
    ServiceWasStopped := True;
  end;
end;

{ The answers go through the exe's `configure` verb in a temp file the setup deletes, so the secret is never
  on a command line and the JSON merge is the tested C# one (phase 101, D1). }
procedure WriteSettings;
var
  Lines: TArrayOfString;
  S, Item, Input: String;
  P, E: Integer;
begin
  if IsSolo then
  begin
    AddLine(Lines, 'Coordinator:Enabled=false');
    AddLine(Lines, 'LocalApi:Enabled=true');
    AddLine(Lines, 'LocalApi:Urls=' + Trim(SoloPage.Values[0]));
    if Trim(SoloPage.Values[1]) <> '' then
    begin
      AddLine(Lines, 'LocalApi:ApiKeys=');
      AddLine(Lines, 'LocalApi:ApiKeys:0=' + Trim(SoloPage.Values[1]));
    end;
  end
  else
  begin
    AddLine(Lines, 'Coordinator:Enabled=true');
    AddLine(Lines, 'Coordinator:Url=' + Trim(CoordPage.Values[0]));
    if Trim(CoordPage.Values[1]) <> '' then
      AddLine(Lines, 'Coordinator:EnrollmentSecret=' + Trim(CoordPage.Values[1]));
    AddLine(Lines, 'LocalApi:Enabled=false');
  end;

  AddLine(Lines, 'Node:Name=' + Trim(NodePage.Values[0]));
  AddLine(Lines, 'Node:DataDirectory=' + SettingsDir);
  AddLine(Lines, 'Node:MaxConcurrency=' + Trim(NodePage.Values[2]));
  AddLine(Lines, 'Node:Vram:BudgetMiB=' + Trim(NodePage.Values[3]));

  { Replace the labels rather than add to them: an empty key removes the section first. }
  AddLine(Lines, 'Node:Labels=');
  S := NodePage.Values[1];
  while Length(S) > 0 do
  begin
    P := Pos(',', S);
    if P = 0 then begin Item := S; S := ''; end
    else begin Item := Copy(S, 1, P - 1); Delete(S, 1, P); end;
    Item := Trim(Item);
    E := Pos('=', Item);
    if E > 1 then
      AddLine(Lines, 'Node:Labels:' + Trim(Copy(Item, 1, E - 1)) + '=' + Trim(Copy(Item, E + 1, Length(Item))));
  end;

  AddLine(Lines, 'Ollama:Endpoint=' + Trim(BackendPage.Values[0]));

  case UpdatePage.SelectedValueIndex of
    0: begin AddLine(Lines, 'Update:Check=true'); AddLine(Lines, 'Update:Auto=true'); AddLine(Lines, 'Update:AllowFromHub=true'); end;
    1: begin AddLine(Lines, 'Update:Check=true'); AddLine(Lines, 'Update:Auto=false'); AddLine(Lines, 'Update:AllowFromHub=true'); end;
    2: begin AddLine(Lines, 'Update:Check=true'); AddLine(Lines, 'Update:Auto=false'); AddLine(Lines, 'Update:AllowFromHub=false'); end;
  else
    begin AddLine(Lines, 'Update:Check=false'); AddLine(Lines, 'Update:Auto=false'); AddLine(Lines, 'Update:AllowFromHub=false'); end;
  end;
  if ParamGiven('UpdateSource') then
    AddLine(Lines, 'Update:Source=' + ExpandConstant('{param:UpdateSource|}'));

  Input := ExpandConstant('{tmp}\node-answers.txt');
  SaveStringsToUTF8File(Input, Lines, False);
  try
    if Run(ExpandConstant('{app}\{#ServiceExe}'), 'configure --file "' + SettingsFile + '" --input "' + Input + '"') <> 0 then
      RaiseException('Could not write ' + SettingsFile + '; see the setup log.');
  finally
    DeleteFile(Input);
  end;
end;

{ The settings file carries the enrollment secret: SYSTEM and Administrators only, plus read for a virtual
  account. Everything else in the data directory is the node's own state, which the account may modify. }
procedure SecureSettings;
var
  Grant: String;
begin
  Grant := '';
  if IsVirtualAccount then
  begin
    Grant := ' "' + VirtualAccount + ':R"';
    Run(ExpandConstant('{sys}\icacls.exe'), '"' + SettingsDir + '" /grant "' + VirtualAccount + ':(OI)(CI)M"');
  end;
  if FileExists(SettingsFile) then
    Run(ExpandConstant('{sys}\icacls.exe'), '"' + SettingsFile + '" /inheritance:r /grant:r *S-1-5-18:F *S-1-5-32-544:F' + Grant);
end;

procedure InstallService;
var
  BinPath, Account: String;
begin
  BinPath := '"\"' + ExpandConstant('{app}\{#ServiceExe}') + '\""';
  if IsVirtualAccount then Account := VirtualAccount else Account := 'LocalSystem';

  if not ServiceExists then
  begin
    if Sc('create ' + ServiceName + ' binPath= ' + BinPath + ' DisplayName= "InferHub Node" start= delayed-auto obj= "' + Account + '"') <> 0 then
      RaiseException('Could not create the ' + ServiceName + ' service; see the setup log.');
    Sc('description ' + ServiceName + ' "InferHub inference node. Settings: ' + SettingsFile + '"');
  end
  else if NeedConfigure then
    Sc('config ' + ServiceName + ' binPath= ' + BinPath + ' start= delayed-auto obj= "' + Account + '"')
  else
    Sc('config ' + ServiceName + ' binPath= ' + BinPath);

  Sc('failure ' + ServiceName + ' reset= 86400 actions= restart/5000/restart/5000/restart/30000');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    InstallService;
    if NeedConfigure then
      WriteSettings;
    SecureSettings;
    Sc('start ' + ServiceName);
    InstallCompleted := True;
  end;
end;

{ Whatever happened, a node that was running before the setup is running after it: an update that fails must
  not leave the node down (phase 101, D4). }
procedure DeinitializeSetup;
begin
  if ServiceWasStopped and not InstallCompleted then
  begin
    Log('The setup did not complete; starting the service it stopped.');
    Sc('start ' + ServiceName);
  end;
end;

{ ---------------------------------------------------------------- uninstall }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    if ServiceExists then
    begin
      StopService;
      Sc('delete ' + ServiceName);
    end;
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    if DirExists(SettingsDir) and not UninstallSilent and
      (MsgBox('Also delete this node''s settings and identity in ' + SettingsDir + '?' + #13#10 +
        'Keep them to reinstall later as the same node.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(SettingsDir, True, True, True);
  end;
end;
