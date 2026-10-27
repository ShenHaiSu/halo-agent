; HaloAgent 安装包脚本（P6 阶段实现）
; P0 占位：保证目录结构与 02-阶段既定目标.md §4.1 一致。
; 正式脚本在 P6 编写，需包含：VC++ redist 静默安装、KB2533623 检测、
; 自包含目录打包、桌面快捷方式、THIRD-PARTY-NOTICES。

[Setup]
AppName=HaloAgent
AppVersion=0.0.1
DefaultDirName={localappdata}\Programs\HaloAgent
OutputDir=..\publish
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x86 x64
PrivilegesRequired=lowest
