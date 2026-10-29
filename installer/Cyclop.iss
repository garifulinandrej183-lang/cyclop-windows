#ifndef SourceDir
  #error SourceDir must be passed by publish.ps1
#endif
#ifndef ArtifactDir
  #error ArtifactDir must be passed by publish.ps1
#endif
[Setup]
AppId={{2710B48C-7978-47A5-BF83-98A428F4E07C}
AppName=Cyclop Windows
AppVersion=0.1.0
AppPublisher=Cyclop Windows contributors
AppPublisherURL=https://github.com/akalikbergenov/cyclop
SetupIconFile={#SourceDir}\Assets\Cyclop.ico
DefaultDirName={localappdata}\Programs\Cyclop Windows
DefaultGroupName=Cyclop Windows
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
UninstallDisplayIcon={app}\Cyclop.exe
LicenseFile=..\LICENSE
OutputDir={#ArtifactDir}
OutputBaseFilename=Cyclop-Windows-0.1.0-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{autoprograms}\Cyclop Windows"; Filename: "{app}\Cyclop.exe"; Parameters: "--open"; IconFilename: "{app}\Cyclop.exe"
Name: "{autodesktop}\Cyclop Windows"; Filename: "{app}\Cyclop.exe"; Parameters: "--open"; IconFilename: "{app}\Cyclop.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\Cyclop.exe"; Parameters: "--open"; Description: "Launch Cyclop Windows"; Flags: nowait postinstall skipifsilent
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "CyclopWindows"; Flags: uninsdeletevalue
