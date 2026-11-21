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
Compression=lzma2/fast
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#Payload}"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
Type: files; Name: "{autoprograms}\SshEnv (WinUI).lnk"
Type: files; Name: "{app}\SshEnv-WinUI-{#RuntimeIdentifier}.exe"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
