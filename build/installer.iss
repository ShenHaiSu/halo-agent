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
 ; 由 build-dist.ps1 按 build/vcredist 下 MSU 是否齐套传入（/DKB_BUNDLED=1/0）。
 ; 为 0 时 Win7 缺 KB 照旧硬拦；为 1 且有管理员时放行，由 ssPostInstall 代装。
 #ifndef KB_BUNDLED
   #define KB_BUNDLED "0"
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

 ; ---------- VC++ 运行库（方案 C：随包 + 无条件静默装，见 docs/P22-bundle/11 §2/§6）----------
 ; 二进制需自备（许可原因不入库）：build/vcredist/vcredist_x86.exe / vcredist_x64.exe
 ; （微软官方“Visual C++ 2015-2022 Redistributable”合一包）。
 ; skipifsourcedoesntexist：没放二进制也能编译安装器；放了则自动随包。
 #if Platform == "x86"
 Source: "vcredist\vcredist_x86.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist
 #else
 Source: "vcredist\vcredist_x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist
 #endif
 ; ---------- KB2533623（Win7 内核补丁，体积小；x86 包需同时带双架构，安装时按内核选择下发）----------
 ; MSU 放 build/vcredist/（用户已备好）；skipifsourcedoesntexist：没放也能编译。
 #if Platform == "x86"
 Source: "vcredist\Windows6.1-KB2533623-x86.msu"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist
 Source: "vcredist\Windows6.1-KB2533623-x64.msu"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist
 #else
 Source: "vcredist\Windows6.1-KB2533623-x64.msu"; DestDir: "{tmp}"; Flags: deleteafterinstall skipifsourcedoesntexist
 #endif


[Icons]
Name: "{group}\HaloAgent"; Filename: "{app}\HaloAgent.exe"; \
    IconFilename: "{app}\HaloAgent.exe"; WorkingDir: "{app}"
Name: "{group}\卸载 HaloAgent"; Filename: "{uninstallexe}"
 Name: "{autodesktop}\HaloAgent"; Filename: "{app}\HaloAgent.exe"; \
     IconFilename: "{app}\HaloAgent.exe"; Tasks: desktopicon
 
 
 [Run]
 ; ---------- VC++ 静默安装（方案 C：无条件执行，vcredist 自己判断已装则秒退）----------
 ; skipifdoesntexist：没随包（没放二进制）则跳过，不报错。
 #if Platform == "x86"
 Filename: "{tmp}\vcredist_x86.exe"; Parameters: "/install /quiet /norestart"; \
     StatusMsg: "{cm:InstallingVCRedist}"; Flags: waituntilterminated skipifdoesntexist
 #else
 Filename: "{tmp}\vcredist_x64.exe"; Parameters: "/install /quiet /norestart"; \
     StatusMsg: "{cm:InstallingVCRedist}"; Flags: waituntilterminated skipifdoesntexist
 #endif
 
 ; ---------- 启动应用（KB 要求重启时跳过，用户重启重跑后再启动）----------
 Filename: "{app}\HaloAgent.exe"; Description: "{cm:LaunchProgram,HaloAgent}"; \
     Flags: nowait postinstall skipifsilent; Check: LaunchAllowed


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
 
 [Code]
 // KB 代装状态：ssPostInstall 里 wusa 返回 3010（要求重启）时置 True，
 // 此时跳过“立即启动”，并在完成页提示重启后重跑。
 var
   RebootNeeded: Boolean;
 // ---------- 先决条件检测（docs/P22-bundle/11 §2：Pascal 实现，零额外二进制）----------
 // 顺序：InitializeSetup 返回 False 则 Inno 不写任何文件（红线 R-6）。
 // 注意：Inno Pascal Script 不是完整 Delphi ——
 //   1. 只用 String（不用 AnsiString/PAnsiChar 强转）；
 //   2. 无 IsAdmin()，用 IsAdminLoggedOn()；
 //   3. GetSpaceOnDisk 后两个是 var 输出参数，不能传 nil。
 
 function GetModuleHandle(lpModuleName: String): THandle;
 external 'GetModuleHandleW@kernel32.dll stdcall';
 function LoadLibraryW(lpFileName: String): THandle;
 external 'LoadLibraryW@kernel32.dll stdcall';
 function FreeLibrary(hLibModule: THandle): Boolean;
 external 'FreeLibrary@kernel32.dll stdcall';
 function GetProcAddress(hModule: THandle; lpProcName: String): LongWord;
 external 'GetProcAddress@kernel32.dll stdcall';
 function DllExports(const DllName, ProcName: String): Boolean;
 var
   h: THandle;
 begin
   Result := False;
   h := GetModuleHandle(DllName);
   if h = 0 then
     h := LoadLibraryW(DllName);
   if h = 0 then Exit;
   Result := GetProcAddress(h, ProcName) <> 0;
 end;
 
 function NeedsKB2533623(): Boolean;
 var
   Ver: TWindowsVersion;
 begin
   Result := False;
   GetWindowsVersionEx(Ver);
   if (Ver.Major = 6) and (Ver.Minor = 1) then
     Result := not DllExports('kernel32.dll', 'SetDefaultDllDirectories');
 end;
 
 function NeedsVCRedist(): Boolean;
 var
   h: THandle;
 begin
   Result := False;
   h := LoadLibraryW('vcruntime140.dll');
   if h <> 0 then FreeLibrary(h) else begin Result := True; Exit; end;
   h := LoadLibraryW('msvcp140.dll');
   if h <> 0 then FreeLibrary(h) else begin Result := True; Exit; end;
 #if Platform == "x64"
   { 32 位安装器下 {sys} 指向 System32，正好用来探 64 位 vcruntime140_1.dll（见 11 §2.2 修正 5） }
   if not FileExists(ExpandConstant('{sys}\vcruntime140_1.dll')) then
     Result := True;
 #endif
 end;
 
 // KBBundled 由编译期 /DKB_BUNDLED 传入（build-dist.ps1 按 MSU 是否齐套决定）。
 function KBMSUBundled(): Boolean;
 begin
 #if KB_BUNDLED == "1"
   Result := True;
 #else
   Result := False;
 #endif
 end;
 
 // 补丁必须跟内核位数走（不是跟我们程序包走）：x86 包落在 64 位 Win7 上也要装 x64 MSU。
 function NeedsKBx86(): Boolean;
 begin
   Result := NeedsKB2533623() and (not IsWin64());
 end;
 
 function NeedsKBx64(): Boolean;
 begin
   Result := NeedsKB2533623() and IsWin64();
 end;
 
 function InitializeSetup(): Boolean;
 var
   MissingList: TStringList;
   Msg: String;
   NL: String;
   FreeMB, TotalMB: Cardinal;
 begin
   Result := False;
   NL := #13#10;
   MissingList := TStringList.Create;
   try
     // KB2533623：随了 MSU 且有管理员 → 放行，ssPostInstall 会静默代装；
     // 无管理员（装不了补丁）或没随包 → 照旧硬拦。
     if NeedsKB2533623() and (not KBMSUBundled() or not IsAdminLoggedOn()) then
     begin
       if KBMSUBundled() then
         MissingList.Add('KB2533623（Windows 7 安全更新，安装它需要管理员权限，请右键“以管理员身份运行”本安装程序）')
       else
         MissingList.Add('KB2533623（Windows 7 安全更新，Chromium 内核必需。请先安装该补丁再重跑安装程序）');
     end;
     if NeedsVCRedist() and not IsAdminLoggedOn() then
       MissingList.Add('Microsoft Visual C++ 2015-2022 运行库（安装它需要管理员权限，请右键“以管理员身份运行”本安装程序）');
     if GetSpaceOnDisk(ExpandConstant('{autopf}'), True, FreeMB, TotalMB) then
     begin
       if FreeMB < 500 then
         MissingList.Add('磁盘可用空间不足 500 MB（当前 ' + IntToStr(FreeMB) + ' MB）');
     end;
     if MissingList.Count > 0 then
     begin
       Msg := '缺少运行 HaloAgent 必需的系统组件：' + NL + NL + MissingList.Text +
              NL + '请先安装以上组件，然后重新运行本安装程序。';
       MsgBox(Msg, mbCriticalError, MB_OK);
       Exit;
     end;
     Result := True;
   finally
     MissingList.Free;
   end;
 end;
 
 // KB 代装：文件复制完（ssPostInstall）后用 wusa 静默装随包 MSU。
 // 退出码 0=成功无需重启；3010=要求重启（置旗标，跳过启动并提示重跑）；
 // 2359302=已安装/不适用；其他→继续走，运行时探针兜底。
 procedure CurStepChanged(CurStep: TSetupStep);
 var
   KBPath: String;
   ResultCode: Integer;
 begin
   if CurStep = ssPostInstall then
   begin
     KBPath := '';
     if NeedsKBx86() then
       KBPath := ExpandConstant('{tmp}\Windows6.1-KB2533623-x86.msu')
     else if NeedsKBx64() then
       KBPath := ExpandConstant('{tmp}\Windows6.1-KB2533623-x64.msu');
     if (KBPath <> '') and FileExists(KBPath) then
     begin
       if Exec(ExpandConstant('{sys}\wusa.exe'), '"' + KBPath + '" /quiet /norestart',
               '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
       begin
         if ResultCode = 3010 then
           RebootNeeded := True;
       end;
     end;
   end
   else if (CurStep = ssDone) and RebootNeeded then
   begin
     MsgBox('已安装 Windows 7 必要更新 KB2533623，需要重启电脑。' + #13#10 + #13#10 +
            '请重启后重新运行安装程序完成安装。',
            mbInformation, MB_OK);
   end;
 end;
 
 function LaunchAllowed(): Boolean;
 begin
   Result := not RebootNeeded;
 end;
