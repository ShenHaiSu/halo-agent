# need22-05 P4 后端工程化

> 等级：P2。前置：P0+P1（CI 门禁依赖测试绿）。只改构建与规范，不改运行时行为。

## 1. 前置检查

- [ ] `git status` 干净；确认 `artifacts/publish`、`bin/obj`、`dist`、`wwwroot` 均被 `.gitignore` 忽略（当前已是，未提交）。
- [ ] 记录版本三处：`global.json:6.0.428`、`Host.Desktop.csproj(TFM+包版本)`、`build/installer.iss:MyAppVersion=0.6.0`、Assembly `1.0.0.0`。
- [ ] 确认 `net6.0-windows` EOL 备案缺失（README 无 Win7 约束说明）。

## 2. 既定目标

1. 版本与包集中管控，一处改全局生效。
2. 格式化 + CI 门禁：禁单文件、测试必绿。
3. 发布可签名、可清理、可追溯。

## 3. 改动清单

| # | 动作 | 说明 |
|---|---|---|
| H1 | 新增 `Directory.Build.props` | 统管 `TFM/Nullable/LangVersion/WarnAsError/VersionPrefix`；`installer.iss` 版本改 `/DMyAppVersion` 传入，与 Assembly 对齐 |
| H2 | 新增 `Directory.Packages.props` + `NuGet.config` | 中央包管理（`CefSharp/Sqlite/ProtectedData/xunit`）；锁源 + `TreatWarningsAsErrors`（包降级） |
| H3 | 新增 `.editorconfig` + `.config/dotnet-tools.json(csharpier)` | `dotnet format`/`csharpier` 本地一键；与 CI 同规则 |
| H4 | 新增 `.github/workflows/backend.yml` | `dotnet build --no-restore → dotnet test → dotnet publish -r win-x64 --self-contained true` + 断言 `PublishSingleFile!=true`（CefSharp 原生件要求） |
| H5 | `build/` 加 `clean.cmd` | 清 `bin/obj/artifacts/dist`；CI 先 clean 再编 |
| H6 | 发布加 `signtool` 时间戳 + `sbom`（如 `Microsoft.Sbom.Targets`） | 无证书时先自签占位，CI 输出签名状态；`installer.iss` 含 `AppVersion` 与 sbom 链接 |
| H7 | `csproj SelfContained=false` 对齐 | 改为与发布一致（`true`）或拆 `Debug=false/Release=true`，消除歧义 |
| H8 | README 备案 | net6-EOL 风险 + Win7 保留原因 + 升级触发条件（CefSharp 109 停止支持/安全补丁缺失） |
| H9 | `MainForm.cs` 启动孤儿扫描 | `Task.Run` 加取消 + 异常进日志（现仅 Warn 吞掉） |

## 4. 验收标准与验收方法

| 验收项 | 标准 | 方法 |
|---|---|---|
| E1 集中版本 | 改一处全局生效 | 改 `Directory.Packages.props` 版本后全解构建绿 |
| E2 格式化 | `dotnet format --verify-no-changes` 绿 | 本地 + CI 双跑 |
| E3 CI | PR 必跑 build/test/publish 门禁 | 推空 PR 看 Actions |
| E4 清理 | `clean.cmd` 后 `bin/obj/artifacts` 为空且可重编 | 运行 + 重编 |
| E5 签名 | 发布物有签名状态可查 | `signtool verify` |

## 5. 回滚

* 纯新增文件，回滚即删文件；H1/H2 回滚后恢复各 `csproj` 原版本号。
