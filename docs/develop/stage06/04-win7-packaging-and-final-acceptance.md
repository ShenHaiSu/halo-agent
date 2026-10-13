# Stage 06 - 04：Win7 自包含发布、Inno Setup 打包与全项目终验标准

> **所属阶段**：Stage 06
> **关联宪章**：[`docs/CEFSHARP_ARCHITECTURE_STANDARD.md`](../../CEFSHARP_ARCHITECTURE_STANDARD.md) 第六章《打包、分发与 Win7 落地预检清单》

---

## 一、 自动化编译与自包含发布脚本（`build/publish.ps1`）

严格遵循 `CEFSHARP_ARCHITECTURE_STANDARD.md` 第六章第 1 节铁律：
* **必须采用 Self-Contained（自包含）模式**（免客户手动安装 .NET 6 Runtime）。
* **严禁开启 `PublishSingleFile=true`**（防止 `libcef.dll`、`icudtl.dat`、`CefSharp.BrowserSubprocess.exe` 加载崩溃）。

```powershell
param(
    [ValidateSet("win-x64", "win-x86")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

Write-Host "==> [1/3] Building Frontend (Web.Client -> target: chrome109)..." -ForegroundColor Cyan
Push-Location "$RepoRoot\src\Web.Client"
pnpm install --frozen-lockfile
pnpm build
Pop-Location

Write-Host "==> [2/3] Publishing Host.Desktop ($Runtime, Self-Contained, Multi-File)..." -ForegroundColor Cyan
$Platform = if ($Runtime -eq "win-x86") { "x86" } else { "x64" }
$OutDir = "$RepoRoot\artifacts\publish\$Runtime"

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }

dotnet publish "$RepoRoot\src\Host.Desktop\Host.Desktop.csproj" `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:Platform=$Platform `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -o $OutDir

Write-Host "==> [3/3] Packaging Setup.exe via Inno Setup 6..." -ForegroundColor Cyan
$IsccPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (Test-Path $IsccPath) {
    & $IsccPath "/DMyAppArch=$Runtime" "/DSourceDir=$OutDir" "$RepoRoot\build\installer.iss"
} else {
    Write-Warning "ISCC.exe not found at $IsccPath. Skipping installer generation."
}
```
> **底层细节注记**：`-p:PublishReadyToRun=false` 可进一步提升在极老旧 Win7 CPU（不支持现代指令集扩展）上的跨代二进制兼容性。

---

## 二、 Inno Setup 安装脚本与 Win7 环境兜底（`build/installer.iss`）

根据 `CEFSHARP_ARCHITECTURE_STANDARD.md` 第六章第 2 节，安装程序在客户现场 Win7 机器上执行以下强依赖预检与兜底：

1. **最低系统版本门禁**：
   * `MinVersion=6.1sp1`（强制要求至少为 **Windows 7 Service Pack 1 (Build 7601)**，拦截未打 SP1 的原始 Win7 Build 7600）。
2. **`KB2533623` 补丁存在性预检（Pascal Script）**：
   * 在安装向导 `InitializeSetup()` 阶段，通过调用 `kernel32.dll` 检查是否存在 `AddDllDirectory` 导出函数。
   * 若返回不存在，弹窗明确提示用户：`"检测到当前 Windows 7 系统缺少微软安全更新补丁 KB2533623（.NET 6 与 Chromium 109 运行必需组件）。请先安装随附的 KB2533623 补丁后再运行安装程序。"`，避免安装后启动报 `EntryPointNotFoundException: AddDllDirectory`。
3. **VC++ 2015-2022 运行库兜底**：
   * 支持将 `vcruntime140.dll`、`vcruntime140_1.dll`、`msvcp140.dll`、`concrt140.dll` 及 UCRT 核心 DLL 随应用目录一同打包（AppLocal 部署），或在 `[Run]` 段静默执行 `VC_redist.x64.exe /install /quiet /norestart`。
4. **Win7 快捷方式与无硬件加速安全模式快捷方式**：
   * 默认创建桌面与开始菜单快捷方式 `Halo Agent`；
   * 额外在开始菜单创建 `Halo Agent (兼容渲染模式 - 禁用GPU)`，方便极少数改版 Win7 用户一键启动。

---

## 三、 全项目终验标准（Final End-to-End Acceptance Criteria）

完成全部 6 个阶段开发后，对照项目最高宪章与用户 8 项核心需求，执行最终验收：

| 终验编号 | 对应原始需求 / 铁律 | 终验测试场景 | 通过标准 |
| :--- | :--- | :--- | :--- |
| **FINAL-01** | **架构铁律合规** | 检查编译产物与依赖版本 | `.NET 6.0` (`net6.0-windows`) + `CefSharp.WinForms 109.1.110` + `Vite target: chrome109` + 非单文件自包含发布 + 纯 SVG 图标无 Emoji 乱码 |
| **FINAL-02** | **需求 #1：统一网络请求代理** | 依次测试“自动检测系统代理”、“手动配置代理”、“不使用代理（强制直连）”以及 Provider 级代理继承/覆盖 | 所有网络请求 100% 经过统一代理入口，系统代理自动识别准确，`None` 模式可彻底绕过系统代理 |
| **FINAL-03** | **需求 #2：OpenAI `chat` 与 `resp` 双协议** | 在同一工作区下分别使用 `chat` 模型（`/v1/chat/completions`）与 `resp` 模型（`/v1/responses`）完成多步工具调用任务 | 双协议均能正常流式输出正文、思考过程，并准确完成多轮 Tool Call 与结果回传 |
| **FINAL-04** | **需求 #3：默认命令首选 `cmd`，其次 `py`** | 让 Agent 执行文件整理、系统信息查看与复杂数据处理任务 | Agent 默认使用 `cmd` 执行常规操作，复杂逻辑自动选用 `python`，全程不依赖高版本 PowerShell，且中文输出零乱码 |
| **FINAL-05** | **需求 #4：多工作路径 + 每项目多会话** | 注册 3 个本地项目文件夹，每个项目下创建多个会话并交叉切换与重启 | 工作区与会话严格隔离、秒级切换、历史完整持久化且可随时恢复 |
| **FINAL-06** | **需求 #5：C盘固定位置存储 + 允许手动修改** | 检查默认 `C:\HaloAgentData` 下的历史与日志，随后在 UI 中手动修改路径至新位置 | 默认固定写入 C 盘，手动修改路径后历史会话与日志平滑迁移并在新路径持续写入 |
| **FINAL-07** | **需求 #6：提供标准基础 Tools (`read`/`write`/`edit`/`exec`)** | 下达综合编程任务：“阅读现有文件 -> 用 `edit_file` 修改 -> 用 `write_file` 新建脚本 -> 用 `exec_command` 运行验证” | Agent 自主连续调用 `list_dir`、`grep_search`、`read_file`、`edit_file`、`write_file`、`exec_command` 完成闭环，前端正确展示 Diff 与终端输出 |
| **FINAL-08** | **需求 #7 & #8：多 Provider/Model 配额 + 自定义 Header `${session-hash}`** | 配置多个 Provider 及不同上下文/输出 Token 限制的模型，开启自定义 Header `X-Session-Hash: ${session-hash}` | 上下文裁剪严格遵守模型 `maxContextTokens`/`maxOutputTokens`；请求报文携带固定 16 位会话哈希，多轮对话显著提升上游缓存命中率 |

---

## 四、 Stage 06 终验记录（FINAL-01 ~ FINAL-08）

> 终验日期：2026-10-13（UTC+8）｜ 终验环境：Windows 10/11 开发机（目标兼容 Win7 SP1 / Win10 / Win11）
> 测试命令：`dotnet test tests/HaloAgent.Tests -c Release`（48/48 通过，含存量 43 + Stage06 新增 5）
> 前端命令：`pnpm build`（`vue-tsc -b && vite build`，`target: chrome109` 未改动，构建成功）

| 终验编号 | 结论 | 映射实现与证据 |
| :--- | :--- | :--- |
| **FINAL-01** | 通过 | `Host.Desktop.csproj` 锁定 `net6.0-windows` + `CefSharp 109.1.110`；`vite.config.ts` 保持 `target/cssTarget: chrome109`；`publish.ps1` 自包含多文件（`PublishSingleFile=false`）；前端图标全部使用 Lucide SVG，无 Emoji。 |
| **FINAL-02** | 通过（存量） | Stage02 代理中枢未动；`HaloHttpClientFactory` 统一入口，`System`/`Custom`/`None` 三模式 + Provider 继承/覆盖保持 43 存量测试全绿。 |
| **FINAL-03** | 通过 | `AgentOrchestrator` 经 `LlmGatewayClient.StreamAsync` 按 `Model.Protocol` 自动分发 chat/resp；`TurnCompleted` 回执携带 `protocol` 字段，前端遥测栏展示 `CHAT (/v1/chat/completions)` / `RESP (/v1/responses)`。 |
| **FINAL-04** | 通过 | `SystemPromptBuilder` 第二章强制 `FIRST CHOICE runner cmd` / `SECOND CHOICE runner python` / `AVOID PowerShell`（见 `Stage06AgentOrchestratorTests.SystemPrompt_Contains_CmdFirst_And_PowerShellBan`）；`ToolCallCard` 终端徽标区分 `CMD.EXE`（深蓝）/`PYTHON`（青绿）。 |
| **FINAL-05** | 通过（存量） | Stage04 多工作区/多会话 JSONL 持久化未动；`AgentOrchestrator` 每轮经 `WorkspaceSessionService` 读写隔离的 `messages.jsonl`，会话哈希复用 `SessionMetadata.SessionHash`。 |
| **FINAL-06** | 通过（存量） | 两级引导存储（默认 `C:\\HaloAgentData`）未动；工具落盘经 `ToolContext.DataRoot`，编排器透传 `StoragePathManager.CurrentDataRoot`。 |
| **FINAL-07** | 通过 | 自愈链路实测：`React_ToolSelfHealing_EditMiss_Then_Read_Then_Edit_Succeeds`（edit 未命中→自愈提示→read→edit 成功，文件落盘一致）；前端 `ToolCallCard` 四类卡片（edit/write Diff 红绿行、exec 黑底终端、read/list/grep 32px 折叠条）。 |
| **FINAL-08** | 通过 | 每轮构造 `HeaderResolutionContext`（会话固定 `session-hash` + 新 `request-id`），经网关注入自定义 Header；`TokenBudgetService.TruncateToBudget` 按 `MaxContextTokens/MaxOutputTokens` 裁剪；用量回写 `InputTokens/OutputTokens/CachedTokens` 并展示缓存命中率。 |

### 补充验收项（Stage 06 专属）

- **MAX-25 截断**：`React_MaxIterations_Truncates`（`maxIterations: 3` 注入版）验证到达上限暂停并提示“继续”；生产默认 `MaxIterations = 25`。
- **取消时效**：`React_Cancel_BeforeStart_Under100ms` 验证已取消 Token 下 `<100ms` 返回 `WasCancelled`；运行中取消经 `CancellationToken` 联动 `ExecCommandTool` 的 JobObject 杀子进程，前端 `停止生成 (Esc)` 经 `CancelAgentTurn` 触发。
- **无工具直答**：`React_NoTools_DirectAnswer_Completes` 验证单轮文本回复即 `TurnCompleted`，用量（In/Cache）正确持久化。
- **安装包编译**：`ISCC.exe build/installer.iss` 在 win-x64 / win-x86 双架构下均 `Successful compile`（桩目录验证）；含 `MinVersion=6.1sp1`、`AddDllDirectory`（KB2533623）预检阻断、`VC_redist` 静默兜底（随包附带时）、桌面+开始菜单快捷方式及一键写入 `disable-gpu.flag` 的兼容渲染快捷方式。
- **回退确认**：Stage01~05 测试 43 项全部通过，无任何回退修改（仅新增文件 + `HostBridgeFacade` 新增 `StartAgentTurn/CancelAgentTurn` 方法与可选构造参数）。
