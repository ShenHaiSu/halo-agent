# need22-05a P4 后端工程化诊断

> 只摆证据。根 `develop/`。

## 1. 构建：闭环有， hygiene 差

| 位置 | 现状 |
|---|---|
| `HaloAgent.sln` | 仅 `Host.Desktop + HaloAgent.Tests`，结构简单健康 |
| `build-dist.cmd` / `build/publish.ps1` | “前端 build → `dotnet publish -r win-x64 --self-contained true` → `artifacts/publish` → Inno Setup”三段式可用 |
| `artifacts/publish/win-x64`（200+ DLL）、`src/*/bin|obj`、`tests/*/bin|obj` | 本地堆积，无 `clean` 目标；虽被 `.gitignore` 忽略未提交，但新人 `git clean` 风险 + 磁盘浪费 |
| `src/Host.Desktop/Host.Desktop.csproj` | `SelfContained=false`，与发布 `true` 不一致 |
| `build/installer.iss:MyAppVersion=0.6.0` vs Assembly `1.0.0.0` | 版本三处脱节（iss/csproj/global.json 各说各话） |

## 2. 依赖：能跑，无管控

| 位置 | 现状 |
|---|---|
| `Host.Desktop.csproj` | `net6.0-windows`（2024-11 EOL，为 Win7 保留，可接受但 README 无备案） |
| 全解 | 无 `Directory.Packages.props`/`NuGet.config`，包版本散落各 csproj |
| CefSharp 109 | 靠 NuGet 自动平铺原生件正确，但“禁单文件”只靠注释+参数，无 CI 门禁，一误开即炸 |

## 3. 质量：零门禁

| 缺失 | 后果 |
|---|---|
| 无签名（无 pfx/signtool） | 发布物无身份，用户 SmartScreen 告警 |
| 无 CI（无 `.github/workflows`） | P0/P1 全靠人肉跑 `dotnet test` |
| 无 `.editorconfig`、无 `.config/dotnet-tools` | 风格靠自觉，`StoragePathManager.cs` 741 行大类 + `needXX` 注释残留无人管 |
| `MainForm.cs` 启动孤儿扫描 `Task.Run` 无取消，异常仅 Warn | 启动期异常被吞 |

## 4. 定级

| 等级 | 项 |
|---|---|
| P2 阻塞 | 无 CI、无集中版本、无 clean |
| P3 一般 | 无签名、无 editorconfig、EOL 未备案 |
