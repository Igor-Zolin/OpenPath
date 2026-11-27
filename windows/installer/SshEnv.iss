#define AppName "OpenPath"
#define AppExe ExtractFileName(Payload)

[Setup]
AppId=SshEnv.WinUI.9D8F82EA-2486-45A4-B269-B8FEC695C1F9
AppName={#AppName}
AppVersion={#GetVersionNumbersString(Payload)}
AppPublisher=OpenPath
VersionInfoProductName=OpenPath
VersionInfoVersion={#GetVersionNumbersString(Payload)}
AppPublisherURL=https://github.com/Igor-Zolin/OpenPath
DefaultDirName={localappdata}\Programs\OpenPath
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
PrivilegesRequired=lowest
ArchitecturesAllowed={#Architecture}
ArchitecturesInstallIn64BitMode={#Architecture}
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\src\SshEnv.WinUI\OpenPath.ico
Compression=lzma2/fast
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#Payload}"; DestDir: "{app}"; Flags: ignoreversion; AfterInstall: InstallDependencies

[InstallDelete]
Type: files; Name: "{autoprograms}\SshEnv (WinUI).lnk"
Type: files; Name: "{app}\SshEnv-WinUI-{#RuntimeIdentifier}.exe"

[UninstallDelete]
Type: files; Name: "{localappdata}\SshEnv\tools\tun2socks.exe"
Type: files; Name: "{localappdata}\SshEnv\tools\wintun.dll"
Type: files; Name: "{localappdata}\SshEnv\tools\sing-box.exe"
Type: files; Name: "{localappdata}\SshEnv\tools\licenses\tun2socks-LICENSE.txt"
Type: files; Name: "{localappdata}\SshEnv\tools\licenses\wintun-LICENSE.txt"
Type: files; Name: "{localappdata}\SshEnv\tools\licenses\sing-box-LICENSE.txt"
Type: files; Name: "{localappdata}\SshEnv\tools\licenses\THIRD-PARTY-NOTICES.txt"
Type: files; Name: "{localappdata}\SshEnv\tools\.dependencies.lock"
Type: dirifempty; Name: "{localappdata}\SshEnv\tools\licenses"
Type: dirifempty; Name: "{localappdata}\SshEnv\tools"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: IsAppUpdate

[CustomMessages]
english.DependencyError=OpenPath could not install its bundled dependencies. Close any running OpenPath operations and retry setup.
russian.DependencyError=Не удалось установить компоненты OpenPath. Завершите операции OpenPath и повторите установку.
english.SshError=Windows could not enable OpenSSH Client. For SSH connections, install OpenSSH Client from Windows Optional Features.
russian.SshError=Windows не удалось включить клиент OpenSSH. Для SSH-подключений установите клиент OpenSSH в дополнительных компонентах Windows.
english.RemoveSshError=Windows could not remove OpenSSH Client. You can remove it from Windows Optional Features.
russian.RemoveSshError=Windows не удалось удалить клиент OpenSSH. Его можно удалить в дополнительных компонентах Windows.

[Code]
const
  OwnershipKey = 'Software\OpenPath\Setup';
  SshOwner = 'InstalledOpenSshClient';

function OpenProcess(Access: LongWord; InheritHandle: Boolean; Id: LongWord): THandle;
external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
external 'CloseHandle@kernel32.dll stdcall';

function InitializeSetup: Boolean;
var
  ParentId: Integer;
  Handle: THandle;
begin
  Result := True;
  ParentId := StrToIntDef(ExpandConstant('{param:OPENPATHPID|0}'), 0);
  if ParentId <= 0 then Exit;
  Handle := OpenProcess($100000, False, ParentId); { SYNCHRONIZE }
  if Handle = 0 then Exit;
  Result := WaitForSingleObject(Handle, 120000) = 0;
  CloseHandle(Handle);
  if not Result then MsgBox(CustomMessage('DependencyError'), mbError, MB_OK);
end;

function IsAppUpdate: Boolean;
begin
  Result := ExpandConstant('{param:OPENPATHUPDATE|0}') = '1';
end;

procedure InstallDependencies;
var
  Code: Integer;
begin
  if not Exec(ExpandConstant('{app}\{#AppExe}'), '--setup-dependencies', '',
      SW_HIDE, ewWaitUntilTerminated, Code) then
    RaiseException(CustomMessage('DependencyError'));
  if Code = 2 then begin
    { A missing ssh.exe on PATH does not imply that the Windows capability is absent. }
    if not ShellExec('runas', ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe'),
        '-NoProfile -NonInteractive -Command "try { ' +
        '$name = ''OpenSSH.Client~~~~0.0.1.0''; ' +
        '$state = (Get-WindowsCapability -Online -Name $name -ErrorAction Stop).State; ' +
        'if ($state -eq ''Installed'') { exit 0 }; if ($state -ne ''NotPresent'') { exit 1 }; ' +
        'Add-WindowsCapability -Online -Name $name -ErrorAction Stop; exit 2 } catch { exit 1 }"',
        '', SW_HIDE, ewWaitUntilTerminated, Code) then
      MsgBox(CustomMessage('SshError'), mbInformation, MB_OK)
    else if Code = 2 then
      RegWriteDWordValue(HKCU, OwnershipKey, SshOwner, 1)
    else if Code <> 0 then
      MsgBox(CustomMessage('SshError'), mbInformation, MB_OK);
  end else if Code <> 0 then
    RaiseException(CustomMessage('DependencyError'));
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
var
  Owned: Cardinal;
  Code: Integer;
begin
  if Step <> usPostUninstall then Exit;
  if not RegQueryDWordValue(HKCU, OwnershipKey, SshOwner, Owned) then Exit;
  if Owned <> 1 then Exit;
  if not ShellExec('runas', ExpandConstant('{sysnative}\dism.exe'),
      '/Online /Remove-Capability /CapabilityName:OpenSSH.Client~~~~0.0.1.0 /NoRestart',
      '', SW_HIDE, ewWaitUntilTerminated, Code) or ((Code <> 0) and (Code <> 3010)) then
    SuppressibleMsgBox(CustomMessage('RemoveSshError'), mbInformation, MB_OK, IDOK);
  RegDeleteValue(HKCU, OwnershipKey, SshOwner);
  RegDeleteKeyIfEmpty(HKCU, OwnershipKey);
end;
