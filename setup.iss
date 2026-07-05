; Claude Widget - Inno Setup Script
; 빌드: "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" setup.iss

#define MyAppName "Claude Widget"
; MyAppVersion can be overridden on the ISCC command line: ISCC /DMyAppVersion=1.2.0 setup.iss
#ifndef MyAppVersion
  #define MyAppVersion "1.3.0"
#endif
#define MyAppPublisher "BaeTab"
#define MyAppExeName "ClaudeWidget.exe"
#define MyAppURL "https://github.com/BaeTab/claude_widget_pet"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=installer_output
OutputBaseFilename=ClaudeWidget_Setup_{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
SetupIconFile=Claude_Widget\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
ArchitecturesInstallIn64BitMode=x64compatible
; Let the Restart Manager close a running widget during an (auto-)update so files unlock.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 바로가기 만들기"; GroupDescription: "추가 아이콘:"
Name: "startup"; Description: "Windows 시작 시 자동 실행"; GroupDescription: "시작 옵션:"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} 제거"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startup

[Run]
; No 'skipifsilent': after a silent auto-update the widget relaunches itself.
Filename: "{app}\{#MyAppExeName}"; Description: "Claude Widget 실행"; Flags: nowait postinstall

[UninstallDelete]
Type: files; Name: "{userstartup}\{#MyAppName}.lnk"
