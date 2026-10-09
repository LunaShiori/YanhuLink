; ============================================================================
; 砚湖连 YanhuLink —— Inno Setup 安装包脚本
; ============================================================================
; 使用方式：
;   1. 先发布「自包含」版本（必须自包含，否则用户需另装 Windows App Runtime）：
;        dotnet publish src\CampusNetLogin\CampusNetLogin.csproj -c Release ^
;          -r win-x64 -p:Platform=x64 -o publish\win-x64
;      说明：csproj 中已设 WindowsAppSDKSelfContained=true，
;            发布产物会自带 Windows App Runtime，用户无需额外安装任何依赖。
;
;   2. 用 Inno Setup 编译本脚本：
;        iscc installer\setup.iss
;
;   产物：installer\Output\YanhuLink-v2.3.2-win-x64-setup.exe
;        （文件名带版本号，方便用户一眼分辨新旧安装包）
; ============================================================================

#define MyAppName "砚湖连"
#define MyAppNameEn "YanhuLink"
#define MyAppVersion "2.3.2"
#define MyAppPublisher "扬州职业技术大学高邮湖校区"
#define MyAppURL "https://github.com/LunaShiori/YanhuLink"
#define MyAppExeName "CampusNetLogin.exe"

[Setup]
AppId={{8E4C2A1B-6D3F-4A92-B7C5-1F2E3D4A5B60}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppNameEn}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
LicenseFile=..\LICENSE
OutputDir=Output
; 文件名包含版本号，便于用户区分不同版本的安装包
OutputBaseFilename=YanhuLink-v{#MyAppVersion}-win-x64-setup
; 安装包自身的图标（资源管理器里看到的那个）
SetupIconFile=..\src\CampusNetLogin\Assets\app.ico
; ★ 安装向导的品牌图。
;   不配置这两项时，向导会显示 Inno Setup 的默认品牌图，
;   看起来就像「安装包没有应用图标」。图片由 tools/gen_wizard_images.py 生成。
WizardImageFile=assets\wizard-large.bmp
WizardSmallImageFile=assets\wizard-small.bmp
; exe 文件属性（右键 → 属性 → 详细信息），让安装包看起来是正式发布的软件
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} {#MyAppVersion} 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCopyright=Copyright (C) 2026 LunaShiori
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 最低 Windows 10 1809
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 默认按用户安装（无需管理员）；用户可在向导中切换为「为所有用户安装」
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
; 允许在安装目录中写入大量文件
DiskSpanning=no

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: checkedonce
Name: "startupicon"; Description: "开机自动启动（后台运行，推荐）"; GroupDescription: "附加任务："; Flags: checkedonce

[Files]
; 自包含发布的全部文件（含 Windows App Runtime，用户无需另装依赖）
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 说明书
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
; 显式指定 IconFilename，避免个别系统上快捷方式退化成白板图标
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    IconFilename: "{app}\Assets\app.ico"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    IconFilename: "{app}\Assets\app.ico"; Tasks: desktopicon
; 开机自启（后台模式）
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; \
    IconFilename: "{app}\Assets\app.ico"; \
    Parameters: "--background"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; \
    Flags: nowait postinstall skipifsilent
; 可选：以管理员身份运行（网络热备的自动切换需要）
Filename: "{app}\{#MyAppExeName}"; Description: "以管理员身份运行（启用网络热备自动切换）"; \
    Verb: runas; Flags: nowait postinstall skipifsilent shellexec unchecked

[UninstallDelete]
; 清理启动文件夹残留
Type: files; Name: "{userstartup}\{#MyAppName}.lnk"
; 清理开机计划任务（若用户曾通过程序创建）
Type: filesandordirs; Name: "{app}\Assets"

[Registry]
; 卸载时移除注册表自启项（程序自愈逻辑也会维护它）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: none; ValueName: "{#MyAppNameEn}"; Flags: dontcreatekey uninsdeletevalue

[Code]
{ ---------------------------------------------------------------------------
  自定义卸载：尝试删除开机计划任务。
  计划任务由程序在提权后创建（schtasks），卸载时应一并清理。
  --------------------------------------------------------------------------- }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Exec('schtasks.exe', '/Delete /TN "CampusNetLogin-Startup" /F',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

{ 安装完成后提示管理员权限的相关说明 }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { 无需额外动作：程序内会在启用网络热备时引导用户提权 }
  end;
end;
