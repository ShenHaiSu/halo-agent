; Halo-Agent Inno Setup 6 安装脚本（Win7 SP1 / Win10 / Win11）
; 发布目录由 build/publish.ps1 经 /DSourceDir 传入；架构经 /DMyAppArch 传入（win-x64 | win-x86）。

#ifndef MyAppArch
  #define MyAppArch "win-x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif

#define MyAppName "Halo Agent"
#define MyAppVersion "0.6.0"
#define MyAppPublisher "Halo-Agent"
#define MyAppExeName "Host.Desktop.exe"

[Setup]
AppId={{8F3A2B1C-6D4E-4A5F-9C0B-1E2D3A4B5C6D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\HaloAgent
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 铁律：最低要求 Windows 7 SP1 (Build 7601)，拦截未打 SP1 的原始 Win7
MinVersion=6.1sp1
#if MyAppArch == "win-x86"
ArchitecturesAllowed=x86
#else
ArchitecturesAllowed=x64compatible
#endif
OutputDir=..\artifacts\setup
OutputBaseFilename=HaloAgent-Setup-{#MyAppArch}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
UninstallDisplayName={#MyAppName}

; 注：本机 Inno 未附带 ChineseSimplified.isl，故使用内置默认英文向导语言，保证全环境可编译。

[Files]
; 自包含多文件发布目录整体打包（严禁单文件模式，保留 libcef.dll / *.pak / BrowserSubprocess）
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
; 兼容渲染模式（禁用 GPU）快捷方式：一键写入 disable-gpu.flag 后启动（Program.cs 检测到该文件即禁用 GPU 加速）
Name: "{group}\{#MyAppName} (兼容渲染模式 - 禁用GPU)"; Filename: "{cmd}"; Parameters: "/c type nul > ""{app}\disable-gpu.flag"" & start """" ""{app}\{#MyAppExeName}"""; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
; VC++ 2015-2022 运行库兜底：若随包附带则静默安装（否则跳过，需手动安装 VC_redist）
; Filename: "{app}\\vc_redist\\VC_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "正在安装 VC++ 运行库..."; Flags: skipifdoesntexist; Check: NeedsVCRedist

[Code]
{ KB2533623 预检：.NET 6 与 Chromium 109 强依赖 AddDllDirectory (kernel32.dll)。
  缺失即弹窗阻断安装，提示先安装 KB2533623 补丁。 }
function LoadLibraryW(lpFileName: String): THandle;
external 'LoadLibraryW@kernel32.dll stdcall';
function GetProcAddress(hModule: THandle; lpProcName: AnsiString): LongWord;
external 'GetProcAddress@kernel32.dll stdcall';
function FreeLibrary(hLibModule: THandle): Boolean;
external 'FreeLibrary@kernel32.dll stdcall';

function CheckAddDllDirectoryApi(): Boolean;
var
  Lib: THandle;
begin
  Result := False;
  Lib := LoadLibraryW('kernel32.dll');
  if Lib <> 0 then
  begin
    Result := GetProcAddress(Lib, 'AddDllDirectory') <> 0;
    FreeLibrary(Lib);
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not CheckAddDllDirectoryApi() then
  begin
    MsgBox(
      '检测到当前 Windows 7 系统缺少微软安全更新补丁 KB2533623（.NET 6 与 Chromium 109 运行必需组件）。' + #13#10 +
      '请先安装随附的 KB2533623 补丁后再运行安装程序，否则程序启动将报 EntryPointNotFoundException: AddDllDirectory。',
      mbError, MB_OK);
    Result := False;
  end;
end;
