# Halo-Agent · 跨代桌面 AI Agent（Win7 / Win10 / Win11）

> 本地优先的桌面 AI Agent：WinForms + CefSharp 宿主 × Vue 3 前端，一次构建，全 Windows 世代可跑。
> 核心能力：多 Provider / 双协议网关（Chat Completions + Responses）→ ReAct 多步循环 → 6 标准本地工具（读 / 写 / 改 / 查 / 列目录 / 执行命令）→ 多工作区多会话持久化。

无 `README.md` 时新补的顶层说明：定位、功能、架构、技术红线、目录、构建运行、测试、配置与排错，全部对齐当前代码与 `docs/` 现状。

---

## 1. 项目定位与亮点

| 维度 | 说明 |
|---|---|
| 产品形态 | 单机桌面应用：`Host.Desktop`（.NET 6 WinForms + CefSharp 内核）内嵌 `Web.Client`（Vue 3 构建产物 `wwwroot`），通过 `app://local` 本地安全协议加载，无外网前端依赖 |
| 最大差异化 | **Win7 SP1 可用**：Chromium 109 最终兼容版 + TLS1.2 兜底 + GPU 禁用 + GBK/UTF-8 自适应 + JobObject 进程树清理 + 不依赖 `rg.exe` 的纯 C# 检索 |
| Agent 能力 | `AgentOrchestrator` ReAct 循环：预算裁剪 → 双协议流式推理 → 工具调度 → 结果回写 JSONL → 下一轮；工具参数瑕疵转自愈提示而非中断；支持取消并杀子进程 |
| 上下文工程 | Token 预算（滑动窗口裁剪 + 输出预留）、Stable Prefix（稳定前缀提缓存命中）、Compaction（超限自动压缩摘要）、PrefixCache 预测、`tool_outputs/` 落盘防撑爆上下文 |
| 交互 | 工作台聊天流（Markdown / 思考折叠 / Tool 卡片 / Diff）、历史右键（复制 / 重放 / 分支）、Prompt 可视化编排、全局设置中心（代理 / Provider / 存储 / 外观 / 运行时） |
| 数据主权 | 历史、配置、日志默认落 C 盘（`C:\HaloAgentData`），可在设置中一键迁移；API Key 经 Windows DPAPI 加密；请求头支持 `${session-hash}` 等变量模板 |

---

## 2. 功能全景（按当前代码）

### 2.1 对话与 Agent

- 流式对话：SSE 归一化为 `UnifiedLlmStreamEvent`，前端 16ms 节流渲染，中途可取消。
- ReAct 工具循环：`exec_command` / `read_file` / `write_file` / `edit_file` / `list_dir` / `grep_search`，最大轮次可配，超限截断明示。
- 历史右键（need07）：用户气泡与 Assistant 轮次均可右键 → **复制原文 / 重放（截断后自动复发）/ 分支（前缀复制建新会话 + 草稿箱预填）**；生成中 destructive 动作禁用，后端忙态以 `SESSION_BUSY` 拒绝；截断前写 `*.replay-bak-*.jsonl` 审计备份。
- 会话标题自动生成、会话时间修正、轮次合并渲染（`turnGroup`）、思考与工具舱折叠、上下文用量表（need01/need04/need05）。
- Prompt 工程（need06）：`PromptConfigService` + `SystemPromptBuilder` + `PromptSettingsView`，支持模板变量、最后发送快照（`PromptLastSentStore`）。

### 2.2 本地工具箱（Win7 兼容）

| 工具名 | 行为 |
|---|---|
| `exec_command` | 默认 `cmd.exe /d /s /c`，次选 `python`（自动探测 `python` / `py -3`）；Win32 JobObject 绑定进程树，超时/取消彻底清理；CP936/UTF-8 自适应解码；超长输出截断并落盘 |
| `read_file` | 行号分页 + 编码自动识别 |
| `write_file` | 临时文件原子替换 |
| `edit_file` | CRLF/LF 容错精确替换 + Unified Diff 生成 |
| `list_dir` | 目录结构探查（沙箱路径校验内） |
| `grep_search` | 纯 C# 正则检索，不依赖外部 `rg.exe` |

安全：工作区路径沙箱校验、高危命令防护（`Tools/Safety`）、超长输出 `ToolOutputGuard` 统一收敛、`DispatchAsync` 异常转自愈提示回传 LLM。

### 2.3 网络与模型接入

- 三档代理：`System`（读 WinINet 注册表 + PAC 感知）/ `Custom`（http/https/socks5 + 认证 + Bypass）/ `None`（强制直连）；Provider 可 `Inherit` 全局或单独覆盖；设置页可一键连通性测试。
- 多 Provider / 多 Model：`baseUrl`、`apiKeyEncrypted`（DPAPI）、`protocol: chat | resp`、`maxContextTokens`、`maxOutputTokens`；发送前 `TokenBudgetService` 预留输出并裁剪。
- 双协议网关：`ChatCompletionsAdapter`（`/v1/chat/completions`）与 `ResponsesApiAdapter`（`/v1/responses`），SSE 状态机聚合 `delta` 与 `function_call`，对上层暴露统一流。
- 自定义请求头：每个 Provider 可开关 Header 列表，支持 `${session-hash}`（WorkspaceId+SessionId 的 SHA-256 短哈希，利于厂商 Prompt Cache 路由亲和）、`${session-id}`、`${workspace-hash}`、`${model-id}`、`${request-id}`、`${timestamp}`。

### 2.4 工作区、会话与存储

- `Workspace → Session` 二级模型：任意本地文件夹绑为 Workspace；每 Workspace 无限 Session；`meta.json`（索引）+ `messages.jsonl`（Append-Only）分离存储，列表秒开、消息懒加载。
- 两级引导存储：固定指针 `%LocalAppData%\HaloAgent\bootstrap.json` → 数据根默认 `C:\HaloAgentData`（无 C 盘根写权限自动降级 `%LocalAppData%\HaloAgent\Data`）；子目录 `config / workspaces / sessions / logs / cef-cache`；支持“仅切换”或“迁移数据”；迁移前后做合法性预检与 200MB 空间检查。
- 三路滚动日志：`App / LlmTrace / ToolExec` + `cef-native.log` + 启动期 `startup-trace.txt / startup-error.txt`（无日志闪退时唯一的排查入口）。

### 2.5 前端工作台

- `WorkbenchView` + 左侧 `WorkspaceSessionSidebar`（多项目切换、会话增删改、内联删除确认）+ 中央 `MessageTimeline`（流式 Markdown、代码高亮、Tool 卡片）+ 底部 `PromptComposer`（配额条、草稿箱）。
- `SettingsCenterDrawer`（NModal/NTabs）：网络代理、Provider/Model（含 Header 变量胶囊编辑器）、存储路径、Prompt 模板、外观（暗黑/浅色 + 字号滑杆）、系统运行时。
- 主题：Naive UI 全量引入 + `GlobalThemeOverrides` + Tailwind v3.4；构建目标锁 `chrome109`，Win7 下字体与 SVG 合规（见 need03 审计）。

---

## 3. 系统架构

```text
┌────────────── Web.Client (Vue 3 + TS + Pinia + Naive UI + Tailwind) ──────────────┐
│ WorkbenchView: Sidebar | MessageTimeline | PromptComposer | SettingsCenterDrawer   │
│ stores: workspace / agent / config / prompt / appearance / system                  │
│ bridge/hostBridge.ts + __haloEventBus  ◀── 16ms 节流推送 ───┐                      │
└───────────────────────────────┬───────────────────────────────────────────────────┘
        CefSharp Async JS Binding (Task<BridgeResponse<T>>) │ window.hostBridge
════════════════════════════════╪═══════════════════════════════════════════════════
┌───────────────────────────────▼───────────────────────────────────────────────────┐
│ Host.Desktop (.NET 6 WinForms + CefSharp 109.1.110)                               │
│ Bridge: HostBridgeFacade (+History/Prompt/SessionTitle) + JsEventDispatcher       │
│ AgentOrchestrator (ReAct) ──▶ ToolRegistry (6 标准工具 + Safety)                  │
│ WorkspaceSessionService ──▶ SessionRepository (meta.json + messages.jsonl 原子写) │
│ TokenBudgetService / CompactionService / PrefixCachePredictor / SystemPromptBuilder│
│ LlmGateway: ChatCompletionsAdapter + ResponsesApiAdapter ─▶ UnifiedLlmStreamEvent  │
│ Infra: StoragePathManager + SecretProtector(DPAPI) + HaloLogger + ProxyHub        │
│        HaloHttpClientFactory + HeaderVariableResolver + JobObject/编码探测         │
└───────────────────────────────────────────────────────────────────────────────────┘
```

关键链路：用户输入 → 预算裁剪 → 双协议流式推理 → 工具调度执行 → 结果回写 JSONL → 下一轮，直至无 ToolCalls 或达最大轮次/被取消。

---

## 4. 技术栈与版本铁律（勿升级）

| 层 | 选型 | 红线 |
|---|---|---|
| 宿主 | .NET 6.0 (`net6.0-windows`)，SDK `6.0.428`（`global.json` 锁定） | 禁止升 net7/8，否则 Win7 直接不可跑 |
| 内核 | `CefSharp.WinForms.NETCore` + `CefSharp.Common.NETCore` **`109.1.110`**（Chromium 109，Win7 最终版） | 禁止升级大版本 |
| 前端构建 | Vite `target/cssTarget = chrome109`，产物直出 `../Host.Desktop/wwwroot`，`app://local` 协议加载 | 禁止用 `file://`；禁止高版本语法不降级 |
| 前端 | Vue 3.5 + TS 5.6 + Pinia + Vue-Router + Naive UI 全量 + Tailwind v3.4 + markdown-it + highlight.js + lucide | Naive UI 全量引入属桌面端正常设计，chunk 告警阈值已放宽至 2500kB |
| 加密 | `System.Security.Cryptography.ProtectedData 6.0.0`（Win7 DPAPI） | — |
| 包管理 | 前端 `pnpm`；后端 `dotnet` | — |

完整宪章见 [`docs/CEFSHARP_ARCHITECTURE_STANDARD.md`](docs/CEFSHARP_ARCHITECTURE_STANDARD.md)，所有构建/选型冲突以它为准。

---

## 5. 仓库结构

```text
HaloAgent.sln                  # 解决方案（Host.Desktop + HaloAgent.Tests）
global.json                    # 锁定 .NET 6.0.428
build-dist.cmd                 # 一键本地构建：前端 → dotnet publish → dist（win-x64 / win-x86）
src/
  Host.Desktop/                # WinForms + CefSharp 宿主
    Program.cs                 # TLS1.2 兜底、GPU 禁用、app://local 注册、CEF 初始化/退出
    MainForm.cs                # ChromiumWebBrowser 宿主窗体与 Bridge 挂载
    Bridge/                    # HostBridgeFacade(+History/Prompt/SessionTitle) + JsEventDispatcher
    Handlers/                  # CEF 生命周期、右键屏蔽、外链拦截
    Core/Models/               # Settings/Provider/Model/Workspace/Session/Message/Proxy/桥接 DTO
    Infrastructure/            # Storage(引导/DPAPI/原子写/迁移) + Logging(滚动日志) + Network(代理/池/Header变量)
    LlmGateway/                # 双协议 Adapter + 统一模型 + SSE 解析器
    Tools/                     # ToolRegistry/契约 + BuiltIn(6 工具) + Process(JobObject/解码) + Safety
    Services/                  # AgentOrchestrator/WorkspaceSession/TokenBudget/Compaction/PrefixCache/Prompt
    Resources/                 # app.ico / app-256.png
    wwwroot/                   # 前端构建产物（由 Vite 生成，不手改）
  Web.Client/                  # Vue 3 前端（Vite + chrome109）
    vite.config.ts / package.json / tailwind.config.js
    src/bridge | stores | components(chat/settings/sidebar/workbench) | views | theme | utils
tests/HaloAgent.Tests/         # xUnit：Stage01～06（Foundation/代理/网关/工作区/工具/Agent/Prompt）
docs/
  CEFSHARP_ARCHITECTURE_STANDARD.md  # 最高铁律宪章
  develop/                     # 顶层蓝图 + stage01～stage06 分阶段设计
  need01～need07/              # 增量需求：配额条/主题/会话标题/Markdown/Token修正/Prompt编排/历史右键
plugins/local.smart-commit/    # 本地 smart-commit 插件
artifacts/ dist/               # publish 中间产物与最终可测二进制（git 忽略，按需生成）
```

---

## 6. 快速开始

### 6.1 前置要求

- Windows 7 SP1 x64（需 `KB2533623` + VC++ 2015-2022）/ Win10 / Win11 x64；备用 `win-x86` 构建供老 32 位机。
- [.NET 6 SDK 6.0.428](https://dotnet.microsoft.com/download)（`dotnet --version` 应为 6.0.4xx）。
- Node.js LTS + `pnpm`（前端构建用）。管理员权限首次运行一次（修复 C 盘数据目录权限）。

### 6.2 一键构建（最常用）

```cmd
:: 默认 win-x64，自包含多文件发布到 .\dist\
build-dist.cmd
:: 老机器 32 位构建
build-dist.cmd win-x86
:: 运行
.\dist\Host.Desktop.exe
```

脚本流程：`pnpm install（有 node_modules 则跳过）→ pnpm build（产物进 wwwroot）→ dotnet publish（Self-Contained，多文件）→ robocopy 到 dist`。若 pnpm 报 `ERR_PNPM_IGNORED_BUILDS`，先在 `src\Web.Client` 执行一次 `pnpm approve-builds` 再重跑。

### 6.3 分步开发联调

```bash
# 前端 dev（浏览器联调，宿主桥走 Mock 通道）
pnpm --dir src/Web.Client install
pnpm --dir src/Web.Client dev      # http://localhost:5173

# 前端类型检查 + 构建（合闸前必跑）
pnpm --dir src/Web.Client exec vue-tsc -b
pnpm --dir src/Web.Client build

# 后端构建 + 测试
dotnet build HaloAgent.sln -c Debug
dotnet test HaloAgent.sln -c Debug
```

VS Code 用 `.vscode/tasks.json + launch.json` 做前后端联调；Win7 真机验证时若花屏/崩溃，在程序目录放一个空文件 `disable-gpu.flag` 强制关闭 GPU 加速后重跑。

---

## 7. 测试

- 后端：`tests/HaloAgent.Tests`（xUnit），覆盖宿主底座、代理/Provider/Header、双协议网关与流聚合、工作区与预算、工具执行与安全、Agent 编排、Prompt 构建、Token/压缩/前缀缓存。合闸命令：`dotnet test` 全绿。
- 前端：`vue-tsc -b && vite build` 即门禁；历史右键等用例另有 `data-testid="message-context-menu / context-menu-copy|replay|branch"` 供手工矩阵（复制×2、重放×2、分支×2、生成态互斥、Mock/真后端一致）验证，详见各 `need0X/04-*test-plan.md`。
- 发布前：`build-dist.cmd` 在 Win7 SP1 与 Win10/11 各跑一次冒烟（启动 → 新建 Workspace/Session → 发一轮带工具调用 → 改存储路径 → 重启数据不丢）。

---

## 8. 配置与数据位置

| 项 | 位置/说明 |
|---|---|
| 引导指针 | `%LocalAppData%\HaloAgent\bootstrap.json`（可用 `HALO_BOOTSTRAP_PATH` 环境变量改指向） |
| 数据根 | 默认 `C:\HaloAgentData`，无权限自动降级 `%LocalAppData%\HaloAgent\Data`；内含 `config / workspaces / sessions / logs / cef-cache` |
| 日志 | `logs/` 下 App/LlmTrace/ToolExec 三路滚动 + `cef-native.log`；启动失败先看程序目录 `startup-trace.txt` 与 `startup-error.txt` |
| 备份 | 重放截断 `*.replay-bak-*.jsonl`、分支源 `*.branch-src*`、压缩归档与 `tool_outputs/` 超长落盘，均在数据根下可审计 |

---

## 9. 文档索引

- 宪章：[`docs/CEFSHARP_ARCHITECTURE_STANDARD.md`](docs/CEFSHARP_ARCHITECTURE_STANDARD.md)
- 总蓝图：[`docs/develop/README.md`](docs/develop/README.md)，分阶段：`develop/stage01`（宿主/存储/脚手架）→ `stage02`（代理/Provider/Header）→ `stage03`（双协议网关）→ `stage04`（工作区/预算）→ `stage05`（工具引擎）→ `stage06`（ReAct/工作台/打包）。
- 增量需求：`need01`（配额条/设置抽屉）· `need02`（主题/代理表/会话时间/Provider 改版）· `need03`（标题生成/字体合规）· `need04`（Markdown/轮次布局/折叠/用量修正）· `need05`（Token/前缀缓存/压缩）· `need06`（Prompt 编排）· `need07`（历史右键复制/重放/分支）——各目录下 `00-README.md` 为总览入口。

---

## 10. 排错速查

| 现象 | 先查 |
|---|---|
| 双击无反应/闪退 | 程序目录 `startup-error.txt` + `startup-trace.txt`；以管理员运行一次；查杀软拦截；补 VC++ 与 `KB2533623` |
| CEF 初始化失败 | 同上；确认 `wwwroot/index.html` 存在（前端是否构建）；`logs/cef-native.log` |
| Win7 花屏/驱动崩溃 | 加 `disable-gpu.flag`；chromium 109 不可升级 |
| 模型 401/超时 | 设置中心做连通性测试；检查 Provider `baseUrl/protocol/chat-resp`、DPAPI 机器变更后需重填 Key、代理 `System/Custom/None` 与 Bypass |
| 中文命令乱码 | 应已由 GBK/UTF-8 自适应处理；若仍乱码，优先用 `cmd` 而非 PowerShell 2.0，并在 issue 附 `ToolExec` 日志 |
| C 盘写不进 | 会自动降级 LocalAppData；或设 `HALO_BOOTSTRAP_PATH` 到可写处；迁移前保证目标盘 ≥200MB |

---

## 11. 许可证与贡献

- 内部项目：许可证未定，默认保留所有权利；需开源请先补 `LICENSE` 再发布。
- 贡献：按 `docs/need0X` 单需求单目录推进；新增 Bridge 方法只增量不改存量 DTO；后端 `dotnet build + test`、前端 `vue-tsc -b && vite build` 全绿方可合闸。
