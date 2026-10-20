#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
[Setup]
AppId={{30BA33EA-41ED-4681-B989-42BDBBD504BE}
AppName=ZPL Print Manager
AppVersion={#AppVersion}
DefaultDirName={autopf}\ZPL Print Manager
DefaultGroupName=ZPL Print Manager
OutputDir=..\artifacts\installer
OutputBaseFilename=ZplPrintManager-{#AppVersion}-win-x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\manager\ZplPrintManager.Manager.exe
CloseApplications=yes
DiskSpanning=no
ExtraDiskSpaceRequired=1073741824

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
Source: "..\artifacts\publish\service\*"; DestDir: "{app}\service"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\manager\*"; DestDir: "{app}\manager"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "ServiceLifecycle.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "ServiceLifecycle.ps1"; Flags: dontcopy
Source: "..\docs\OPERACAO.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\HOMOLOGACAO.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{group}\ZPL Print Manager"; Filename: "{app}\manager\ZplPrintManager.Manager.exe"
Name: "{group}\Desinstalar"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\manager\ZplPrintManager.Manager.exe"; Description: "Abrir ZPL Print Manager"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
function Lifecycle(ScriptPath: String; Phase: String): Boolean;
var
  ExitCode: Integer;
  Arguments: String;
begin
  Arguments := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '" -Phase ' + Phase + ' -InstallDirectory "' + ExpandConstant('{app}') + '"';
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Arguments, '', SW_SHOW, ewWaitUntilTerminated, ExitCode);
  if Result then Result := ExitCode = 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  ExtractTemporaryFile('ServiceLifecycle.ps1');
  if not Lifecycle(ExpandConstant('{tmp}\ServiceLifecycle.ps1'), 'Prepare') then
    Result := 'Não foi possível pausar, criar backup e parar o serviço. Instalação interrompida; consulte o diagnóstico do PowerShell.'
  else Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if not Lifecycle(ExpandConstant('{app}\installer\ServiceLifecycle.ps1'), 'Configure') then
      RaiseException('Configuração ou verificação de saúde do serviço falhou. A instalação não está operacional. Consulte o Event Log e preserve ProgramData para recuperação.');
end;

function InitializeUninstall(): Boolean;
begin
  Result := Lifecycle(ExpandConstant('{app}\installer\ServiceLifecycle.ps1'), 'Remove');
  if not Result then MsgBox('Não foi possível parar/remover o serviço. Desinstalação interrompida.', mbError, MB_OK);
end;
