; Halo-Agent Inno Setup 6 安装脚本（Win7 SP1 / Win10 / Win11）
; need22-P4 H7/H6：版本号由 CI 经 /DMyAppVersion 传入（git tag → 与 Assembly VersionPrefix 对齐，E6）；
; 发布目录由 build/publish.ps1 经 /DSourceDir 传入；架构经 /DMyAppArch 传入（win-x64 | win-x86）。
; SBOM（Microsoft.Sbom.Targets 产物 artifacts\publish\<arch>\sbom\spdx.json）如存在则随包（见 [Files]）。

#ifndef MyAppArch
  #define MyAppArch "win-x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#define MyAppName "Halo Agent"
#ifndef MyAppVersion
  #define MyAppVersion "0.6.0"
#endif
#define MyAppPublisher "Halo-Agent"
#define MyAppExeName "Host.Desktop.exe"

#if MyAppArch == "win-x86"
  #define VCRedistExe "VC_redist.x86.exe"
#else
  #define VCRedistExe "VC_redist.x64.exe"
#endif

[Setup]
AppId={{8F3A2B1C-6D4E-4A5F-9C0B-1E2D3A4B5C6D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
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
; H6 SBOM 随包：发布时 -p:GenerateSbom=true 才会产出 sbom\spdx.json；不存在则跳过（安装后位于 {app}\sbom\spdx.json）
; 若 build 目录或 vc_redist 子目录下预置了 VC++ 运行库，自动随包附带打包进安装器（用于离线静默安装）
Source: "vc_redist\{#VCRedistExe}"; DestDir: "{app}\vc_redist"; Flags: skipifsourcedoesntexist ignoreversion
Source: "{#VCRedistExe}"; DestDir: "{app}\vc_redist"; Flags: skipifsourcedoesntexist ignoreversion

[Icons]
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
; 兼容渲染模式（禁用 GPU）快捷方式：一键写入 disable-gpu.flag 后启动（Program.cs 检测到该文件即禁用 GPU 加速）
Name: "{group}\{#MyAppName} (兼容渲染模式 - 禁用GPU)"; Filename: "{cmd}"; Parameters: "/c type nul > ""{app}\disable-gpu.flag"" & start """" ""{app}\{#MyAppExeName}"""; WorkingDir: "{app}"

[Run]
; VC++ 2015-2022 运行库静默检测与后台安装：安装软件时若检测到系统没有，自动静默后台装好
Filename: "{app}\vc_redist\{#VCRedistExe}"; Parameters: "/install /quiet /norestart"; StatusMsg: "正在检测并静默安装 VC++ 运行库..."; Flags: runhidden waituntilterminated skipifdoesntexist; Check: NeedsVCRedist
Filename: "{app}\{#VCRedistExe}"; Parameters: "/install /quiet /norestart"; StatusMsg: "正在检测并静默安装 VC++ 运行库..."; Flags: runhidden waituntilterminated skipifdoesntexist; Check: NeedsVCRedist
; 立即启动应用程序（位于运行库安装之后，确保运行库就绪后再启动）
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

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

{ VC++ 2015-2022 运行时检测：若系统已安装对应架构运行库则跳过，未安装则触发后台静默安装 }
function NeedsVCRedist(): Boolean;
var
  InstalledVal: Cardinal;
begin
  Result := True;
#if MyAppArch == "win-x86"
  // 1. 微软官方标准注册表键 (VS2015-2022 统一在 14.0，Installed = 1)
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86', 'Installed', InstalledVal) and (InstalledVal = 1) then
  begin
    Result := False;
    Exit;
  end;
  // 2. 兼容早期 VC++ 2015 Servicing 键
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\DevDiv\VC\Servicing\14.0\RuntimeMinimum', 'Install', InstalledVal) and (InstalledVal = 1) then
  begin
    Result := False;
    Exit;
  end;
#else
  if IsWin64() then
  begin
    // 1. 微软官方标准 64 位注册表键 (需使用 HKLM64 访问 64 位视图，Installed = 1)
    if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', InstalledVal) and (InstalledVal = 1) then
    begin
      Result := False;
      Exit;
    end;
    // 2. 兼容早期 VC++ 2015 Servicing 64 位键
    if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\DevDiv\VC\Servicing\14.0\RuntimeMinimum', 'Install', InstalledVal) and (InstalledVal = 1) then
    begin
      Result := False;
      Exit;
    end;
  end;
#endif
end;
