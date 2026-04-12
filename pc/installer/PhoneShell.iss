; PhoneShell installer (Inno Setup)

#if !defined(AppName)
  #define AppName "PhoneShell"
#endif
#if !defined(AppVersion)
  #define AppVersion "1.0.0"
#endif
#if !defined(Publisher)
  #define Publisher "PhoneShell"
#endif
#if !defined(AppExeName)
  #define AppExeName "PhoneShell.App.exe"
#endif
#if !defined(PublishDir)
  #define PublishDir "..\\installer\\publish"
#endif

[Setup]
AppId={{B8B7B08E-2A2B-4D93-BF0E-ACFC8B6F7C0A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={%USERPROFILE}\\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
OutputDir=.\\out
SetupIconFile=..\\src\\PhoneShell.App\\Assets\\phoneshell.ico
UninstallDisplayIcon={app}\\{#AppExeName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; GroupDescription: "Additional icons"; Flags: unchecked

[Files]
Source: "{#PublishDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb, data\\*, *.log"

[Icons]
Name: "{group}\\{#AppName}"; Filename: "{app}\\{#AppExeName}"
Name: "{userdesktop}\\{#AppName}"; Filename: "{app}\\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
