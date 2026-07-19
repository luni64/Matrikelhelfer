; Matrikelhelfer Inno Setup Script

; Application metadata
#define MyAppName      "Matrikelhelfer"
#define MyAppPublisher "luni64"
#define MyAppURL       "https://github.com/luni64/Matrikelhelfer"
#define MyAppExeName   "Matrikelhelfer.exe"

; Source paths (relative to this script — one level up from installer folder).
; The win-x64 publish output contains only win-x64 natives (the csproj pins
; RuntimeIdentifier). Build it with:
;   dotnet publish Matrikelhelfer\Matrikelhelfer.csproj -c Release
#define SourceDir  "..\Matrikelhelfer\bin\Release\net8.0-windows\win-x64\publish"
#define InstallerDir "."

; Version — update manually before each release. MyAppVersion is the display
; version (may carry a pre-release suffix); MyAppVersionInfo is the numeric
; file-version resource (VersionInfoVersion requires x.x.x.x).
#define MyAppVersion "1.1.0-beta.1"
#define MyAppVersionInfo "1.1.0.0"

; Generate WHATS_NEW.txt from template with version substituted at compile time
#define WhatsNewTemplate SourcePath + "\WHATS_NEW.template"
#define WhatsNewOutput   SourcePath + "\WHATS_NEW.txt"
#expr Exec("powershell", "-NoProfile -Command ""(Get-Content '" + WhatsNewTemplate + "' -Raw) -replace '\$\{VERSION\}', '" + MyAppVersion + "' | Set-Content -NoNewline '" + WhatsNewOutput + "'""", SourcePath, 1, SW_HIDE)

; CodeDependencies helper (provides Dependency_AddDotNet80Desktop)
; Download from: https://github.com/DomGries/InnoDependencyInstaller
#include "CodeDependencies.iss"

[Setup]
; Installer code signing using an Inno Setup Sign Tool profile named "certum"
; (configured in the Inno Setup IDE: Tools -> Configure Sign Tools; the same
; profile used for AutoNum on this machine — nothing per-project to set up).
; For command-line builds pass the same command via /S, e.g.:
;   ISCC.exe "/Scertum=signtool.exe sign /sha1 <thumbprint> ... $f" setup.iss
SignTool=certum $f
SignedUninstaller=yes

AppId={{3D88B1A1-F017-4A05-907F-5A39956E5376}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersionInfo}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes

; Show "What's New" page after installation completes
InfoAfterFile={#InstallerDir}\WHATS_NEW.txt

OutputDir=bin
OutputBaseFilename=Matrikelhelfer-{#MyAppVersion}-Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
DisableProgramGroupPage=yes
; Wizard / Add-Remove-Programs icon. The exe already carries this same icon
; via <ApplicationIcon> in the csproj, so the installed shortcuts are covered.
SetupIconFile={#SourcePath}\..\Matrikelhelfer\Assets\Matrikelhelfer.ico
; Required so the dependency installer can elevate if needed
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german";  MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Main executable
Source: "{#SourceDir}\{#MyAppExeName}";  DestDir: "{app}"; Flags: ignoreversion

; All DLLs (including subdirectories such as runtimes\)
Source: "{#SourceDir}\*.dll";            DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Runtime configuration files
Source: "{#SourceDir}\*.json";           DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}";                            Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}";      Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";                      Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup: Boolean;
begin
  // Ensure .NET 8 Desktop Runtime (x64) is present; downloads it if missing.
  Dependency_AddDotNet80Desktop;
  Result := True;
end;
