; ============================================================
;  HaloAgent 安装器 —— 最小可用版
;
;  编译：
;    ISCC.exe /DAppVersion=0.0.1 /DPlatform=x86 ^
;             /DSourceDir=<dist\win-x86 绝对路径> ^
;             /DOutDir=<输出目录绝对路径> ^
;             build\installer.iss
;
;  说明：本文件是"能装能跑能卸"的最小集。
;        先决条件拦截、静默安装、更新模式见 docs/P22-bundle/11。
; ============================================================

#ifndef AppVersion
  #define AppVersion "0.0.1"
#endif
#ifndef Platform
  #define Platform "x86"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\win-x86"
#endif
#ifndef OutDir
  #define OutDir "..\artifacts\" + AppVersion + "\win-" + Platform
#endif

[Setup]
; ---------- 身份 ----------
; AppId 一旦发布就永远不能改：改了等于让已装用户变成"两个程序并存"，
; 且旧版本无法被新版覆盖安装。
AppId={{6E2C1A54-5D0B-4F71-9A2E-3F8C7B1D4A09}
AppName=HaloAgent
AppVersion={#AppVersion}
AppVerName=HaloAgent {#AppVersion}
AppPublisher=HaloAgent

; ---------- 安装位置 ----------
; {autopf} 在 PrivilegesRequired=lowest 下解析为 {localappdata}\Programs
; 在 admin 下解析为 {pf}。避免写死 "Program Files"，也避开 WOW64 重定向。
DefaultDirName={autopf}\HaloAgent
DisableDirPage=no
DefaultGroupName=HaloAgent
DisableProgramGroupPage=yes
AllowNoIcons=yes

; 默认免提权；用户可在向导里选"为所有用户安装"
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 最低 Win7 SP1
; 写成 6.1sp1 而不是 6.1.7601：后者会让 Inno 告警
; "Minimum version is set to 6.1 but using 6.1sp1 is recommended"
MinVersion=6.1sp1

; ---------- 架构 ----------
; x86 包：允许装在 32/64 位系统上
; x64 包：声明 64 位安装模式，注册表走 64 位视图
#if Platform == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=x86compatible
#endif

; ---------- 压缩 ----------
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=2

; ---------- 界面 ----------
WizardStyle=modern
WizardSizePercent=110

; ---------- 输出 ----------
OutputDir={#OutDir}
OutputBaseFilename=HaloAgentSetup-{#Platform}-{#AppVersion}

; ---------- 图标（可选：文件不存在时注释掉这两行）----------
; SetupIconFile=logos\app.ico
UninstallDisplayIcon={app}\HaloAgent.exe
UninstallDisplayName=HaloAgent {#AppVersion}

; ---------- 许可页（可选：文件不存在时注释掉）----------
; LicenseFile=license.zh-CN.txt

; ---------- 卸载 ----------
CreateUninstallRegKey=yes
Uninstallable=yes

; ---------- 绝不自动重启 ----------
RestartIfNeededByRun=no
RestartApplications=no

; ---------- 覆盖安装行为 ----------
UsePreviousAppDir=yes
UsePreviousTasks=yes
UsePreviousSetupType=no
ShowLanguageDialog=no


[Languages]
; 中文语言包（Inno 官方 issrc/Files/Languages/ChineseSimplified.isl，6.5.0+ 版）
; 若该文件缺失，改为下面那行英文即可编译
Name: "zhcn"; MessagesFile: "msgs\ChineseSimplified.isl"
; Name: "en"; MessagesFile: "compiler:Default.isl"


[CustomMessages]
; zhcn.* 是安装器自有的附加文案（不覆盖语言包里的标准消息）
zhcn.InstallingVCRedist=正在安装 VC++ 运行库（可能需要 1~2 分钟）…


[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; \
    GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce


[Files]
; ---------- 主产物 ----------
Source: "{#SourceDir}\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; \
    Excludes: "*.pdb,*.log,dev.flag,disable-gpu.flag,force-software.flag"

; ---------- 第三方声明（可选）----------
; Source: "THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

; ---------- VC++ 运行库（方案 C 时取消注释；需要先补 build\vcredist\）----------
; #if Platform == "x86"
; Source: "vcredist\vcredist_x86.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
; #else
; Source: "vcredist\vcredist_x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
; #endif


[Icons]
Name: "{group}\HaloAgent"; Filename: "{app}\HaloAgent.exe"; \
    IconFilename: "{app}\HaloAgent.exe"; WorkingDir: "{app}"
Name: "{group}\卸载 HaloAgent"; Filename: "{uninstallexe}"
Name: "{autodesktop}\HaloAgent"; Filename: "{app}\HaloAgent.exe"; \
    IconFilename: "{app}\HaloAgent.exe"; Tasks: desktopicon


[Run]
; ---------- VC++ 静默安装（方案 C 时取消注释）----------
; 注意：不加 Check 条件，无条件执行是安全的
;       —— vcredist 安装器自己会判断"已安装且版本够"并秒退。
; #if Platform == "x86"
; Filename: "{tmp}\vcredist_x86.exe"; Parameters: "/install /quiet /norestart"; \
;     StatusMsg: "{cm:InstallingVCRedist}"; Flags: waituntilterminated
; #else
; Filename: "{tmp}\vcredist_x64.exe"; Parameters: "/install /quiet /norestart"; \
;     StatusMsg: "{cm:InstallingVCRedist}"; Flags: waituntilterminated
; #endif

; ---------- 启动应用 ----------
Filename: "{app}\HaloAgent.exe"; Description: "{cm:LaunchProgram,HaloAgent}"; \
    Flags: nowait postinstall skipifsilent


[UninstallDelete]
; Inno 只删自己装的文件与空目录，以下需显式声明
Type: filesandordirs; Name: "{app}\wwwroot"
Type: filesandordirs; Name: "{app}\logs"
Type: dirifempty;     Name: "{app}"

; 运行态缓存
Type: filesandordirs; Name: "{localappdata}\HaloAgent\CefCache"
Type: files;          Name: "{localappdata}\HaloAgent\window.json"
Type: files;          Name: "{localappdata}\HaloAgent\running.marker"
Type: dirifempty;     Name: "{localappdata}\HaloAgent"
