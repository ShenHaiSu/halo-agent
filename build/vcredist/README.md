# build/vcredist — VC++ 2015-2022 Redistributable（自备，不入库）

`build/installer.iss`（方案 C）会在打包时把这里的二进制随包，
并在安装时无条件静默执行（已装则 vcredist 自己秒退）。

## 需要放的两个文件

| 文件 | 对应安装包 | 官方来源（手动下载） |
|---|---|---|
| `vcredist_x86.exe` | `Platform=x86` 包 | https://aka.ms/vs/17/release/vc_redist.x86.exe |
| `vcredist_x64.exe` | `Platform=x64` 包 | https://aka.ms/vs/17/release/vc_redist.x64.exe |

> 许可原因不进 git（本目录仅 README + `.gitkeep` 入库）。
> 没放也能编译安装器（`skipifsourcedoesntexist` / `skipifdoesntexist`），
> 但分发给 Win7 用户前必须放，否则裸机启动即缺 `vcruntime140.dll`。

## KB2533623（Win7 内核补丁，用户已备好）

| 文件 | 用途 |
|---|---|
| `Windows6.1-KB2533623-x86.msu` | 32 位 Win7 用 |
| `Windows6.1-KB2533623-x64.msu` | 64 位 Win7 用（x86 安装包落在 64 位系统上也要装这个） |

安装器在 `ssPostInstall` 用 `wusa /quiet /norestart` 静默代装；
`build-dist.ps1` 按这两个文件是否齐套自动传 `/DKB_BUNDLED=1/0`。
同样不入库（`.gitignore` 已配 `*.msu`）。

## 分发前检查

```powershell
Get-Item build\vcredist\*.exe | Select-Object Name,@{n='MB';e={[math]::Round($_.Length/1MB,1)}}
```
