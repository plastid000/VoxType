#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{8F4B9A2E-7C31-4D8A-9E15-2B6D4F1A8C30}
AppName=VoxType
AppVersion={#AppVersion}
AppPublisher=MR Tech BD
AppPublisherURL=https://mrtechbd.net
DefaultDirName={autopf}\VoxType
DefaultGroupName=VoxType
AllowNoIcons=yes
OutputDir=..\dist
OutputBaseFilename=VoxType-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
CloseApplications=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked
Name: "startup"; Description: "Start VoxType automatically with Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\VoxType"; Filename: "{app}\VoxType.exe"
Name: "{autodesktop}\VoxType"; Filename: "{app}\VoxType.exe"; Tasks: desktopicon
Name: "{userstartup}\VoxType"; Filename: "{app}\VoxType.exe"; Tasks: startup

[Run]
Filename: "{app}\VoxType.exe"; Description: "Launch VoxType"; Flags: nowait postinstall skipifsilent