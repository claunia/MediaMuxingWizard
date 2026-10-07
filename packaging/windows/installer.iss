; Inno Setup script. Build with: packaging\windows\build-installer.ps1
#define AppName "Media Muxing Wizard"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
AppId={{6F7C9A0E-7B7E-4D4B-9A8F-4C2E5B1D3A71}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Natalia Portillo
DefaultDirName={autopf}\Media Muxing Wizard
DefaultGroupName={#AppName}
OutputBaseFilename=MediaMuxingWizard-{#AppVersion}-{#Arch}-setup
OutputDir=..\..\artifacts
SetupIconFile=..\icon\icon.ico
UninstallDisplayIcon={app}\MediaMuxingWizard.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed={#Arch}compatible
ArchitecturesInstallIn64BitMode={#Arch}compatible
ChangesAssociations=yes
WizardStyle=modern

[Files]
Source: "..\..\artifacts\publish\win-{#Arch}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\MediaMuxingWizard.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\MediaMuxingWizard.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; Flags: unchecked
Name: "addtopath"; Description: "Add the mmw command line tool to PATH"; Flags: unchecked

[Registry]
; Offer the editor in "Open with" without taking over the default player.
Root: HKA; Subkey: "Software\Classes\MediaMuxingWizard.Media"; ValueType: string; ValueData: "Media file"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\MediaMuxingWizard.Media\DefaultIcon"; ValueType: string; ValueData: "{app}\MediaMuxingWizard.exe,0"
Root: HKA; Subkey: "Software\Classes\MediaMuxingWizard.Media\shell\open\command"; ValueType: string; ValueData: """{app}\MediaMuxingWizard.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "MediaMuxingWizard.Media"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "MediaMuxingWizard.Media"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.m4a\OpenWithProgids"; ValueType: string; ValueName: "MediaMuxingWizard.Media"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.m4b\OpenWithProgids"; ValueType: string; ValueName: "MediaMuxingWizard.Media"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "MediaMuxingWizard.Media"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "MediaMuxingWizard.Media"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.mka\OpenWithProgids"; ValueType: string; ValueName: "MediaMuxingWizard.Media"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "MediaMuxingWizard.Media"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Code]
function NeedsAddPath(Dir: string): Boolean;
var
  Paths: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Paths) then
    Paths := '';
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') = 0;
end;
