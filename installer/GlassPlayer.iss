; Community fork installer. Copyright (c) 2026 N3moStudio. GPL-3.0-only.
#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef Arch
  #define Arch "arm64"
#endif
#ifndef PublishDir
  #error PublishDir must identify a verified clean publish directory
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installers"
#endif

[Setup]
AppId={{DA70F1F5-3F5C-472D-A9B4-74516C945B32}
AppName=GlassPlayer
AppVersion={#AppVersion}
AppVerName=GlassPlayer {#AppVersion} ({#Arch})
AppPublisher=N3moStudio · original application by Jonzu00
AppPublisherURL=https://github.com/N3moStudio/GlassPlayer
AppSupportURL=https://github.com/N3moStudio/GlassPlayer/issues
AppUpdatesURL=https://github.com/N3moStudio/GlassPlayer/releases
DefaultDirName={localappdata}\Programs\GlassPlayer
DefaultGroupName=GlassPlayer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.22000
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputDir={#OutputDir}
OutputBaseFilename=GlassPlayer-{#AppVersion}-windows-{#Arch}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
InfoBeforeFile=install-notes.txt
UninstallDisplayIcon={app}\GlassPlayer.exe
UninstallDisplayName=GlassPlayer ({#Arch})
AppMutex=Local\N3moStudio.GlassPlayer
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
VersionInfoVersion=0.2.0.0
VersionInfoProductName=GlassPlayer
VersionInfoDescription=GlassPlayer Setup ({#Arch})

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "config.json,*.pdb"
Source: "{#PublishDir}\config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{autoprograms}\GlassPlayer"; Filename: "{app}\GlassPlayer.exe"; WorkingDir: "{app}"; AppUserModelID: "N3moStudio.GlassPlayer"
Name: "{autodesktop}\GlassPlayer"; Filename: "{app}\GlassPlayer.exe"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "N3moStudio.GlassPlayer"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\GlassPlayer.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\GlassPlayer.exe"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\GlassPlayer.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"

[Run]
Filename: "{app}\GlassPlayer.exe"; Description: "Open GlassPlayer"; Flags: nowait postinstall skipifsilent
