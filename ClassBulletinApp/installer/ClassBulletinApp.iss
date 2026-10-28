; ============================================================================
; 班级公布栏 (ClassBulletinApp) 安装脚本 —— Inno Setup 6
; 用法：安装 Inno Setup 6 后，右键本文件「用 Inno Setup 编译」，或命令行：
;       "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" ClassBulletinApp.iss
; 脚本会把 ../publish 下的自包含程序打包成 setup.exe。
; ============================================================================

#define MyAppName "班级公布栏"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "ClassBulletinApp"
#define MyAppExeName "ClassBulletinApp.exe"
#define MyAppId "{8F3C9A1E-2B7D-4E55-9C1A-1B6E2F3D4C5A}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=Output
OutputBaseFilename=ClassBulletinApp_Setup_{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
UsePreviousAppDir=yes
SetupIconFile=..\publish\{#MyAppExeName}

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项:"; Flags: unchecked
Name: "autostart"; Description: "开机自动启动（当前用户）"; GroupDescription: "附加选项:"; Flags: unchecked

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ClassBulletinApp"; ValueData: "{app}\{#MyAppExeName}"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
