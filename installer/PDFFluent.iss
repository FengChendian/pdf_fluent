#define AppName "Fluent PDF"
#define AppExeName "pdf_fluent.exe"

#ifndef AppVersion
#define AppVersion "0.0.0"
#endif

#ifndef SourceDir
#define SourceDir "..\artifacts\publish\win-x64"
#endif

#ifndef OutputDir
#define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{C5925B07-56D3-41C5-B506-DE3F00F7CC02}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Fluent PDF
DefaultDirName={autopf}\Fluent PDF
DefaultGroupName=Fluent PDF
UsePreviousGroup=no
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=Fluent PDF Setup {#AppVersion} win-x64
SetupIconFile=..\src\Assets\logo.ico
UninstallDisplayIcon={app}\{#AppExeName}
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\src\Assets\pdf.ico"; DestDir: "{app}\Assets"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional tasks:"; Flags: unchecked
Name: "startmenuicon"; Description: "Create Start Menu shortcuts"; GroupDescription: "Additional tasks:"

[Icons]
Name: "{autodesktop}\Fluent PDF"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{group}\Fluent PDF"; Filename: "{app}\{#AppExeName}"; Tasks: startmenuicon
Name: "{group}\Uninstall Fluent PDF"; Filename: "{uninstallexe}"; Tasks: startmenuicon

[InstallDelete]
Type: files; Name: "{autodesktop}\PDF Fluent.lnk"
Type: filesandordirs; Name: "{commonstartmenu}\Programs\PDF Fluent"

[Registry]
Root: HKLM; Subkey: "Software\PDF Fluent"; Flags: deletekey
Root: HKLM; Subkey: "Software\RegisteredApplications"; ValueName: "PDF Fluent"; Flags: deletevalue
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"",0"
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""
Root: HKLM; Subkey: "Software\Classes\PDFFluent.Document"; ValueType: string; ValueName: ""; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\PDFFluent.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Assets\pdf.ico"",0"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\PDFFluent.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""
Root: HKLM; Subkey: "Software\Classes\.pdf"; ValueType: string; ValueName: ""; ValueData: "PDFFluent.Document"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "PDFFluent.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Fluent PDF\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Fluent PDF"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Fluent PDF\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "A PDF reader"
Root: HKLM; Subkey: "Software\Fluent PDF\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "PDFFluent.Document"
Root: HKLM; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Fluent PDF"; ValueData: "Software\Fluent PDF\Capabilities"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch Fluent PDF"; Flags: nowait postinstall skipifsilent unchecked
