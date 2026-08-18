; Inno Setup script for MouseUtil.
; Packages the self-contained Release build output into a per-user installer
; that requires no admin rights and adds a Start Menu shortcut + uninstaller.
;
; Build the Release output first, then compile this script:
;   ..\BuildAndRun.ps1 MouseUtil.csproj -SkipRun /p:Configuration=Release
;   ISCC.exe MouseUtil.iss
;
; Output installer is written to installer\Output\MouseUtilSetup.exe

#define MyAppName "MouseUtil"
#define MyAppVersion "1.3.0"
#define MyAppPublisher "MouseUtil"
#define MyAppExeName "MouseUtil.exe"
#define MyBuildOutputDir "..\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"

[Setup]
; Fixed AppId so future versions upgrade in place instead of installing side-by-side.
AppId={{9C6C7A9E-9C0B-4C0A-9C7B-5B9E6E6B7E10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
; No admin rights required - installs and registers Start Menu entry per-user.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=MouseUtilSetup
SetupIconFile=..\Assets\installer.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic
DisableProgramGroupPage=yes
DisableWelcomePage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=[name] {#MyAppVersion} Setup Wizard
WelcomeLabel2=This will install or update MouseUtil on your computer.

[Files]
Source: "{#MyBuildOutputDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Icons]
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
