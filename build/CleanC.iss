#define AppVersion "1.7.1"
[Setup]
AppId={{728E2BF8-5E71-4C4B-B5E4-B9C12F7CAE22}
AppName=CleanC
AppVersion={#AppVersion}
AppPublisher=CleanC
DefaultDirName={autopf}\CleanC
DefaultGroupName=CleanC
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=CleanC-Setup
SetupIconFile=..\src\CleanC.App\Assets\CleanC-shell-1.6.8.ico
UninstallDisplayIcon={app}\Assets\CleanC-shell-1.6.8.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\CleanC-User-Guide.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\CleanC"; Filename: "{app}\CleanC.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\CleanC-shell-1.6.8.ico"; IconIndex: 0; AppUserModelID: "CleanC.Desktop"
Name: "{autodesktop}\CleanC"; Filename: "{app}\CleanC.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\CleanC-shell-1.6.8.ico"; IconIndex: 0; AppUserModelID: "CleanC.Desktop"

; No automatic scan, repair, activation or background service is started by installation.
; User license, logs and recovery files are intentionally retained on uninstall.

[Run]
Filename: "{app}\CleanC.exe"; Description: "Launch CleanC"; Flags: nowait; Check: IsUpdate

[Code]
function IsUpdate: Boolean;
begin
  Result := Pos('/UPDATE', UpperCase(GetCmdTail)) > 0;
end;
