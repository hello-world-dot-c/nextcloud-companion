; Inno Setup script for Nextcloud Companion.
; Per-user installation: no administrator rights required.
; Built by build-installer.ps1, which passes /DAppVersion and /DSourceDir.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif

#define AppName "Nextcloud Companion"
#define AppExe  "NextcloudCompanion.exe"
#define LegacyAppName "Nextcloud Status"

[Setup]
; Keep this GUID stable: it identifies the app for upgrades and uninstall.
AppId={{6C1B7E54-3F0A-4D52-9B8E-2A7D5C41E0B9}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=hello-world-dot-c
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\NextcloudCompanion\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\artifacts\installer
OutputBaseFilename=NextcloudCompanion-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; The tray app has no main window, so let the Restart Manager close it when upgrading.
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
; Same name/location as StartupShortcut.cs, so the app's own "Start with Windows" toggle stays in sync.
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{#AppName} in the system tray"; Tasks: startup

[InstallDelete]
Type: files; Name: "{userstartup}\{#LegacyAppName}.lnk"
Type: files; Name: "{autoprograms}\{#LegacyAppName}.lnk"
Type: files; Name: "{autodesktop}\{#LegacyAppName}.lnk"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The app may re-create the startup shortcut itself via its tray menu.
Type: files; Name: "{userstartup}\{#AppName}.lnk"
Type: files; Name: "{userstartup}\{#LegacyAppName}.lnk"
