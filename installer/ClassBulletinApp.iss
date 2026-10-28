; 班级公布栏 安装脚本（Inno Setup 7）
; 构建：ISCC.exe ClassBulletinApp.iss
; 前置：先执行 publish，生成 ..\dist\portable 目录

#define MyAppName      "班级公布栏"
#define MyAppVersion   "1.0.0"
#define MyAppPublisher "东凤中学"
#define MyAppURL       ""
#define MyAppExeName   "ClassBulletinApp.exe"
#define MySourceDir    "..\dist\portable"

[Setup]
AppId={{A7E3C1D4-5B92-4F86-9C3A-8E1D2B7F4A60}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=教室多媒体屏班级公布栏
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
LicenseFile=
OutputDir=..\dist
OutputBaseFilename=ClassBulletinApp-{#MyAppVersion}-安装版-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 按当前用户安装：不需要管理员权限，双击即可装（教室机更省事）
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
SetupIconFile=
UninstallDisplayIcon={app}\{#MyAppExeName}
; 自包含发布（自带 .NET 8 运行时），无需额外依赖
ChangesEnvironment=no
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked
Name: "startonlogin"; Description: "开机自动启动"; GroupDescription: "启动选项:"; Flags: unchecked

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 开机自启（写入当前用户 Run 项，与程序内「设置」里的开关一致）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "ClassBulletinApp"; ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: startonlogin

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
