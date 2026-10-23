; Imvello Windows installer (Inno Setup 6)
; Build with installer\build-installer.ps1, which publishes the app first.

#define AppName      "Imvello"
#define AppExe       "Imvello.exe"
#define AppPublisher "Imvello"
#define SrcDir       "..\artifacts\Imvello-win-x64"
#define AppVersion   GetStringFileInfo(SrcDir + "\" + AppExe, "ProductVersion")

[Setup]
AppId={{77B2D8E4-6C41-4BCF-9A2C-5E3F1D0A8B21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright (C) 2026 {#AppPublisher}
VersionInfoVersion={#GetVersionNumbersString(SrcDir + "\" + AppExe)}
VersionInfoTextVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
VersionInfoCompany={#AppPublisher}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
; Let the user choose "all users" (admin) or "just me" (no admin needed)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

OutputDir=..\artifacts
OutputBaseFilename=Imvello-Setup
Compression=lzma2/max
SolidCompression=yes

WizardStyle=modern
WizardSizePercent=110
SetupIconFile=..\src\Imvello\Assets\Imvello.ico
WizardImageFile=build\wizard-large-100.bmp,build\wizard-large-200.bmp
WizardSmallImageFile=build\wizard-small-100.bmp,build\wizard-small-200.bmp
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

; Close a running Imvello before upgrading
CloseApplications=yes
CloseApplicationsFilter={#AppExe}
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
english.WebView2Title=Microsoft Edge WebView2 Runtime
english.WebView2Missing=Imvello needs the Microsoft Edge WebView2 Runtime to display and compose email. Setup will download and install it now (about 2 MB).
english.WebView2Installing=Installing Microsoft Edge WebView2 Runtime...
english.WebView2Failed=The WebView2 Runtime could not be installed. Imvello will be installed, but you need to install WebView2 from https://go.microsoft.com/fwlink/p/?LinkId=2124703 before reading email.
spanish.WebView2Title=Microsoft Edge WebView2 Runtime
spanish.WebView2Missing=Imvello necesita Microsoft Edge WebView2 Runtime para mostrar y redactar correo. El instalador lo descargará e instalará ahora (unos 2 MB).
spanish.WebView2Installing=Instalando Microsoft Edge WebView2 Runtime...
spanish.WebView2Failed=No se pudo instalar WebView2 Runtime. Imvello se instalará, pero debe instalar WebView2 desde https://go.microsoft.com/fwlink/p/?LinkId=2124703 antes de leer el correo.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SrcDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[InstallDelete]
; Remove the debug symbols shipped by older installers (now embedded in the exe)
Type: files; Name: "{app}\Imvello.pdb"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; User data in %LOCALAPPDATA%\LetMail (mail, accounts, settings) is intentionally
; NOT removed on uninstall, so reinstalling or upgrading never loses mail.

[Code]
const
  WebView2Guid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Url  = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

function WebView2Installed: Boolean;
var
  V: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2Guid, 'pv', V) and (V <> '') and (V <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Guid, 'pv', V) and (V <> '') and (V <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2Guid, 'pv', V) and (V <> '') and (V <> '0.0.0.0'));
end;

procedure InstallWebView2;
var
  ResultCode: Integer;
  Ok: Boolean;
begin
  Ok := False;
  try
    DownloadTemporaryFile(WebView2Url, 'MicrosoftEdgeWebview2Setup.exe', '', nil);
    WizardForm.StatusLabel.Caption := CustomMessage('WebView2Installing');
    Ok := Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '',
               SW_HIDE, ewWaitUntilTerminated, ResultCode) and WebView2Installed;
  except
    Log('WebView2 download/install failed: ' + GetExceptionMessage);
  end;
  if not Ok then
    SuppressibleMsgBox(CustomMessage('WebView2Failed'), mbInformation, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WebView2Installed then
  begin
    if WizardSilent or
       (SuppressibleMsgBox(CustomMessage('WebView2Missing'), mbInformation, MB_OKCANCEL, IDOK) = IDOK) then
      InstallWebView2;
  end;
end;
