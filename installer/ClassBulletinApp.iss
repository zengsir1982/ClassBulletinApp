; 班级公布栏 安装脚本（Inno Setup 7）
; 构建：ISCC.exe ClassBulletinApp.iss
; 前置：先执行 publish，生成 ..\dist\portable 目录

#define MyAppName      "班级公布栏"
#define MyAppVersion   "1.0.1"
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
OutputBaseFilename=ClassBulletinApp-{#MyAppVersion}-setup-win-x64
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

; ----------------------------------------------------------------------------
; 卸载增强：
;  1) 卸载前强制结束仍在运行的程序（程序常驻托盘，点关闭只隐藏不退出，会占用文件）
;  2) 卸载时弹窗让用户自行选择「彻底无残留 / 保留用户数据」
;  3) 始终恢复桌面壁纸（若曾应用「桌面背景」）并清理临时调试日志
; ----------------------------------------------------------------------------
[Code]
var
  DeleteUserData: Boolean;

function InitializeUninstall(): Boolean;
var
  RC: Integer;
  Res: Integer;
begin
  // 程序常驻托盘（关闭只隐藏不退出），卸载前先强制结束，解除文件占用
  Exec('taskkill.exe', '/f /im ClassBulletinApp.exe', '', SW_HIDE, ewWaitUntilTerminated, RC);

  // 卸载窗体(UninstallProgressForm)不支持向导页，改用系统消息框询问。
  // 用 MB_YESNO + MB_DEFBUTTON2 让「否」(保留用户数据) 预聚焦，最安全，避免误删。
  Res := MsgBox('是否一并删除用户数据？' + #13#10#13#10 +
    '用户数据位于 AppData\Roaming\ClassBulletinApp，包含：课表、设置、作业公示、缺交名单、桌面背景图。' + #13#10#13#10 +
    '• 选「是」：彻底无残留（删除程序及全部用户数据，重装需重新导入课表）' + #13#10 +
    '• 选「否」：仅删除程序，保留用户数据（重装免重新导入，推荐）',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2);

  // IDYES=彻底无残留；其余情况（含关闭对话框/默认回车）一律按「保留」处理
  DeleteUserData := (Res = IDYES);
  Result := True;
end;

procedure RestoreWallpaperIfApplied();
var
  PsPath, Ps: string;
  RC: Integer;
begin
  // 仅当「桌面背景」当前正生效时才恢复原始壁纸，避免误改用户原有壁纸
  Ps :=
    '$d = Join-Path $env:APPDATA "ClassBulletinApp"' + #13#10 +
    '$set = Join-Path $d "settings.json"' + #13#10 +
    '$bp = Join-Path $d "desktop_backdrop.png"' + #13#10 +
    'if (-not (Test-Path $set)) { exit }' + #13#10 +
    '$s = Get-Content $set -Raw -Encoding UTF8 | ConvertFrom-Json' + #13#10 +
    '$saved = $s.SavedWallpaper' + #13#10 +
    '$cur = (Get-ItemProperty "HKCU:\Control Panel\Desktop").Wallpaper' + #13#10 +
    'if ($cur -and ($cur -replace [char]34, "") -eq ($bp -replace [char]34, "")) {' + #13#10 +
    '  Add-Type -TypeDefinition "using System;using System.Runtime.InteropServices;public class WB{[DllImport(\"user32.dll\")]public static extern int SystemParametersInfo(int a,int b,string c,int d);}"' + #13#10 +
    '  [WB]::SystemParametersInfo(20, 0, $(if ($saved) {$saved} else {""}), 3)' + #13#10 +
    '}';
  PsPath := ExpandConstant('{tmp}\restore_wp.ps1');
  if SaveStringToFile(PsPath, Ps, False) then
    Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -File "' + PsPath + '"', '', SW_HIDE, ewWaitUntilTerminated, RC);
end;

procedure DeleteWildcard(const Pattern: string);
var
  FindRec: TFindRec;
  Path: string;
begin
  if FindFirst(Pattern, FindRec) then begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
          Path := AddBackslash(ExtractFilePath(Pattern)) + FindRec.Name;
          DeleteFile(Path);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurStep: TUninstallStep);
begin
  if CurStep = usUninstall then begin
    // 1) 仅在曾应用「桌面背景」时恢复原始壁纸
    RestoreWallpaperIfApplied();
    // 2) 始终清理临时调试日志
    DeleteWildcard(ExpandConstant('{localappdata}\Temp\ClassBulletinApp*.log'));
    // 3) 按用户在弹窗中的选择决定是否删除用户数据
    if UninstallSilent then
      DeleteUserData := False;   // 静默卸载默认保留用户数据
    if DeleteUserData then
      DelTree(ExpandConstant('{userappdata}\ClassBulletinApp'), True, True, True);
  end;
end;
