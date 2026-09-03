; ============================================================================
;  WiseAcademy — Posto (Desktop)  ·  instalador Inno Setup
;  Compilar:  "C:\Program Files\Inno Setup 7\ISCC.exe" installer\WiseAcademyPosto.iss
;  (a partir da pasta  desktop\ ,  depois de  dotnet publish ... -o publish )
; ============================================================================

#define AppName    "WiseAcademy Posto"
#define AppVer     "1.0.0"
#define AppPub     "WiseAcademy"
#define AppExe     "WiseAcademy.Desktop.exe"
#define WebView2Id "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

[Setup]
AppId={{7B2C4A10-9D3E-4F51-8C2A-3F0A9B1C2D3E}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} {#AppVer}
AppPublisher={#AppPub}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=Output
OutputBaseFilename=WiseAcademyPosto-Setup-{#AppVer}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart";   Description: "Iniciar o WiseAcademy Posto no arranque do Windows"; GroupDescription: "Opções do posto:"; Flags: unchecked

[Files]
; app publicada (self-contained) — tudo menos o appsettings.json
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "appsettings.json"; Flags: recursesubdirs ignoreversion
; config: só na primeira instalação — as atualizações preservam a config do posto
Source: "..\publish\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Icons]
Name: "{group}\{#AppName}";        Filename: "{app}\{#AppExe}"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{commonstartup}\{#AppName}";  Filename: "{app}\{#AppExe}"; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir o {#AppName} agora"; Flags: nowait postinstall skipifsilent

[Code]
function WebView2Installed(): Boolean;
var
  V: string;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + '{#WebView2Id}', 'pv', V) or
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + '{#WebView2Id}', 'pv', V) or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + '{#WebView2Id}', 'pv', V);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WebView2Installed()) then
    MsgBox(
      'O "WebView2 Runtime" da Microsoft n' + #227 + 'o foi detetado.' + #13#10#13#10 +
      'Sem ele a aplica' + #231 + #227 + 'o abre apenas no modo offline (registo de ponto).' + #13#10 +
      'Instale-o (gr' + #225 + 'tis) a partir de:' + #13#10 +
      'https://developer.microsoft.com/microsoft-edge/webview2/',
      mbInformation, MB_OK);
end;
