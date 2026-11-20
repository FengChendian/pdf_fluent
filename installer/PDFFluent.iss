#define AppName "PDF Fluent"
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
AppPublisher=PDF Fluent
DefaultDirName={autopf}\PDF Fluent
DefaultGroupName=PDF Fluent
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=PDF Fluent Setup {#AppVersion} win-x64
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

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional tasks:"; Flags: unchecked
Name: "startmenuicon"; Description: "Create Start Menu shortcuts"; GroupDescription: "Additional tasks:"
Name: "associatepdf"; Description: "Associate PDF files with PDF Fluent"; GroupDescription: "Additional tasks:"; Flags: unchecked

[Icons]
Name: "{autodesktop}\PDF Fluent"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{group}\PDF Fluent"; Filename: "{app}\{#AppExeName}"; Tasks: startmenuicon
Name: "{group}\Uninstall PDF Fluent"; Filename: "{uninstallexe}"; Tasks: startmenuicon

[Registry]
Root: HKLM; Subkey: "Software\Classes\PDFFluent.Document"; ValueType: string; ValueName: ""; ValueData: "PDF Document"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\PDFFluent.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"",0"
Root: HKLM; Subkey: "Software\Classes\PDFFluent.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""
Root: HKLM; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "PDFFluent.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\PDF Fluent\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "PDF Fluent"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\PDF Fluent\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "A PDF reader"
Root: HKLM; Subkey: "Software\PDF Fluent\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "PDFFluent.Document"
Root: HKLM; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "PDF Fluent"; ValueData: "Software\PDF Fluent\Capabilities"
Root: HKLM; Subkey: "Software\Classes\.pdf"; ValueType: string; ValueName: ""; ValueData: "PDFFluent.Document"; Flags: uninsdeletevalue; Tasks: associatepdf

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch PDF Fluent"; Flags: nowait postinstall skipifsilent unchecked
