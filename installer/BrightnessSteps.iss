; Per-user install: no UAC prompt, since the app itself never needs elevation.
#define AppName        "BrightnessSteps"
#define AppVersion     "1.0.0"
#define AppPublisher   "Patrick Reinbold"
#define AppURL         "https://projects.patrickreinbold.com/brightness-steps/"
#define AppExe         "BrightnessSteps.exe"

[Setup]
AppId={{7D2C4E19-5B3A-4F6C-9E88-2A1F0C7B4D53}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=BrightnessSteps-{#AppVersion}-setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in"; GroupDescription: "Startup"

[Files]
Source: "..\BrightnessSteps.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app.ico";            DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md";          DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE";            DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";                 Filename: "{app}\{#AppExe}"; IconFilename: "{app}\app.ico"
Name: "{group}\Uninstall {#AppName}";       Filename: "{uninstallexe}"

[Registry]
; Same value the app's own tray toggle writes, so the two agree.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "BrightnessSteps"; ValueData: """{app}\{#AppExe}"""; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The tray app holds the backlight device open; stop it before removing files.
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "StopApp"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  // The app targets .NET Framework 4.x, which ships with Windows 8 and later.
  if not RegKeyExists(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full') then
  begin
    if MsgBox('BrightnessSteps needs the .NET Framework 4.x, which was not found.' + #13#10 +
              'It is included with Windows 8 and later.' + #13#10#13#10 +
              'Install anyway?', mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;
