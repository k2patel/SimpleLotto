; SimpleLotto Windows installer.
; CI publishes the self-contained WinUI app, then runs this script with:
;   iscc /DPublishDir=<publish-dir> /DMyAppVersion=0.0.1-<sha>
;
; The installer also opens the local Rdisplay/API port on private/domain
; networks. SimpleLotto reuses the WindowsPOS display communication model.

#define MyAppName        "SimpleLotto"
#ifndef MyAppVersion
  #define MyAppVersion   "0.0.1-dev"
#endif
#ifndef PublishDir
  #define PublishDir     "..\SimpleLotto.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish"
#endif
#define MyAppPublisher   "AMS LottoMachine"
#define MyAppExeName     "SimpleLotto.App.exe"
#define MyFirewallRuleName "SimpleLotto.App - Local API"
#define MyApiPort        "5000"

[Setup]
AppId={{0757C4E2-4281-4DCD-8E5D-F4B1DC863A1B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
OutputDir=..\dist
OutputBaseFilename=SimpleLotto-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
SetupIconFile=..\SimpleLotto.App\Assets\SimpleLotto.ico
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "redist\VC_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "signing\SimpleLotto-CodeSigning.cer"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\VC_redist.x64.exe"; \
    Parameters: "/install /quiet /norestart"; \
    StatusMsg: "Installing Visual C++ runtime..."; \
    Flags: waituntilterminated; \
    Check: VCRedistNeeded

Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$cert = '{tmp}\SimpleLotto-CodeSigning.cer'; if (Test-Path $cert) {{ Import-Certificate -FilePath $cert -CertStoreLocation Cert:\LocalMachine\Root | Out-Null; Import-Certificate -FilePath $cert -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null }}"""; \
    Flags: runhidden waituntilterminated; \
    StatusMsg: "Trusting SimpleLotto installer certificate..."; \
    Check: SigningCertificateAvailable

Filename: "netsh.exe"; \
    Parameters: "advfirewall firewall delete rule name=""{#MyFirewallRuleName}"""; \
    Flags: runhidden runascurrentuser; \
    StatusMsg: "Refreshing Windows Firewall rule for port {#MyApiPort}..."

Filename: "netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""{#MyFirewallRuleName}"" dir=in action=allow protocol=TCP localport={#MyApiPort} profile=private,domain scope=Subnet description=""Allow SimpleLotto Rdisplay/local API on the local network"""; \
    Flags: runhidden runascurrentuser; \
    StatusMsg: "Adding Windows Firewall rule for port {#MyApiPort}..."

Filename: "{app}\{#MyAppExeName}"; \
    Description: "Launch {#MyAppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "netsh.exe"; \
    Parameters: "advfirewall firewall delete rule name=""{#MyFirewallRuleName}"""; \
    Flags: runhidden runascurrentuser; \
    RunOnceId: "DelSimpleLottoFirewall"

[Code]
procedure KillSimpleLotto();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'),
       '/F /IM {#MyAppExeName}',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  KillSimpleLotto();
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  KillSimpleLotto();
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    SaveStringToFile(ExpandConstant('{app}\version.txt'),
                     '{#MyAppVersion}' + #13#10, False);
  end;
end;

function VCRedistNeeded(): Boolean;
var
  Installed, Major, Minor: Cardinal;
begin
  if not RegQueryDWordValue(HKLM64,
       'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
       'Installed', Installed) then
  begin
    Result := True;
    exit;
  end;
  if Installed <> 1 then
  begin
    Result := True;
    exit;
  end;
  if not RegQueryDWordValue(HKLM64,
       'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
       'Major', Major) then
  begin
    Result := True;
    exit;
  end;
  if not RegQueryDWordValue(HKLM64,
       'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
       'Minor', Minor) then
  begin
    Result := True;
    exit;
  end;
  Result := (Major < 14)
        or ((Major = 14) and (Minor < 40));
end;

function SigningCertificateAvailable(): Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\SimpleLotto-CodeSigning.cer'));
end;
