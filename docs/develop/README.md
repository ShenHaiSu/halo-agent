# Halo-Agent 跨代桌面端（Win7 / Win10 / Win11）总体架构设计与开发路线图

> **关联宪章**：本设计严格遵循 [`docs/CEFSHARP_ARCHITECTURE_STANDARD.md`](../CEFSHARP_ARCHITECTURE_STANDARD.md) 中的所有版本红线与防崩溃铁律（`.NET 6.0` + `CefSharp.WinForms 109.1.110` + `Vite build.target = 'chrome109'`）。
> **定位说明**：本文档为 Halo-Agent 项目的顶层技术架构蓝图与分阶段研发总纲，将业务需求细化为可落地、可验证、模块解耦的六大工程阶段（`stage01` ~ `stage06`）。

---

## 一、 核心业务诉求与底层架构深化映射

针对用户提出的 8 项核心要求，结合 Windows 7 SP1 历史系统特性与现代 LLM Agent 工程实践，底层架构进行了如下针对性深化设计：

| 序号 | 需求维度 | 底层架构深化与技术决策 | 对应研发阶段 |
| :--- | :--- | :--- | :--- |
| **1** | **统一网络请求代理配置与入口**<br>（自动系统代理 / 手动配置 / 不使用代理） | 构建 **`ProxyHub`（统一代理中枢）** 与自定义 **`IHttpClientFactory` 池化管理**：<br>1. `System` 模式：通过 WinINet 注册表 (`HKCU\...\Internet Settings`) + `HttpClient.DefaultProxy` 实时感知系统代理与 PAC；<br>2. `Custom` 模式：支持 `http://`、`https://`、`socks5://` 代理地址、认证凭据与 Bypass 列表；<br>3. `None` 模式：显式设置 `SocketsHttpHandler.UseProxy = false`，强制绕过系统全局代理直连；<br>4. **Win7 TLS 安全兼容**：在 `Program.cs` 启动时开启 `Tls12` 并动态探测 `Tls13`（避免 Win7 因不支持 TLS 1.3 枚举直接抛 `NotSupportedException`）。 | `stage02` |
| **2** | **支持 OpenAI `Chat` 与 `Responses` 双协议** | 构建 **`ILlmProtocolAdapter` 双协议归一化网关**：<br>1. `ChatCompletionsAdapter` (`/v1/chat/completions`)：处理经典 `messages` 数组、`tools` 嵌套结构与 `choices[0].delta` SSE 流；<br>2. `ResponsesApiAdapter` (`/v1/responses`)：处理 `instructions`、`input` 多态项（`message`/`function_call`/`function_call_output`）、扁平化 `tools` 以及 `response.output_text.delta` / `response.function_call_arguments.delta` 等语义化 SSE 事件；<br>3. 对上层 `AgentOrchestrator` 暴露完全统一的 `UnifiedLlmStreamEvent` 流，业务层与协议细节零耦合。 | `stage03` |
| **3** | **默认命令操作优先推荐 `cmd`，其次 `py`**<br>（规避不同系统 PowerShell 版本代差） | 构建 **`Win7ProcessExecutor` 跨代命令行执行引擎**：<br>1. Win7 自带 PowerShell 2.0 缺失现代 Cmdlet 且 UTF-8 管道极易损坏，故在 System Prompt 与 `exec_command` 工具 Schema 中将 `shell` 默认值锁死为 `cmd`（`cmd.exe /d /s /c`），次选为 `python`（自动探测环境变量 `python` / `py -3` 或便携版 Python）；<br>2. 引入 **Windows Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`)** 绑定进程树，确保超时或中断时彻底清理 `cmd.exe` 派生的所有孙进程；<br>3. 内置 **CP936 (GBK) / UTF-8 双编码自适应解码器**，彻底解决 Win7 中文系统下 `dir`/`ipconfig`/`ping` 输出乱码问题。 | `stage05` |
| **4** | **多工作路径（项目文件夹）与每项目下多会话** | 构建 **`Workspace -> Session` 二级领域模型与并发安全仓储**：<br>1. 支持绑定任意多个本地项目文件夹作为 `Workspace`，独立维护项目级配置与目录有效性校验；<br>2. 每个 `Workspace` 下支持无限个 `Session`，采用 `meta.json`（轻量元数据索引）+ `messages.jsonl`（Append-Only 消息流）分离存储，秒级渲染会话列表，按需懒加载历史消息。 | `stage04` |
| **5** | **历史及日志固定写入 C 盘，且允许用户手动修改路径** | 构建 **`StorageBootstrap` 两级引导存储架构**：<br>1. 固定引导文件位于 `C:\Users\<User>\AppData\Local\HaloAgent\bootstrap.json`，记录当前生效的 `DataRootPath`；<br>2. 默认数据根目录设为 `C:\HaloAgentData`（若当前受限账户无 C 盘根目录写权限，自动回退至 `C:\Users\<User>\AppData\Local\HaloAgent\Data`）；<br>3. 用户在设置界面可一键修改路径，支持**“仅切换新路径”**或**“安全迁移已有会话/配置数据至新目录”**。 | `stage01` |
| **6** | **提供标准基础 Tool（`read`/`write`/`edit`/`exec` 等）** | 构建 **`IToolHandler` 标准工具箱与安全沙箱**：<br>1. 核心四件套：`read_file`（行号分页 + UTF-8/GBK 自动识别）、`write_file`（临时文件原子替换）、`edit_file`（CRLF/LF 容错精确替换 + Unified Diff 生成）、`exec_command`（CMD/Python 优先 + 超时控制）；<br>2. 增强检索工具：`list_dir`（目录结构探查）与 `grep_search`（纯 C# 正则内容检索，不依赖 Win7 无法运行的外部 64位 `rg.exe`）；<br>3. 超长输出自动截断并落盘至 `tool_outputs/`，避免单次 `exec`/`read` 撑爆模型上下文。 | `stage05` |
| **7** | **多 Provider 与多 Model 精细化配置**<br>（BaseUrl/ApiKey/Proxy继承/协议/最大上下文/最大输出） | 构建 **`ProviderRegistry` 与 `TokenBudgetManager`**：<br>1. `ProviderConfig` 包含 `baseUrl`、`apiKeyEncrypted`（使用 Windows 原生 **DPAPI `ProtectedData`** 加密存储）、`proxyMode`（默认 `Inherit` 继承全局代理，亦可设为 `None` 或 `Custom`）；<br>2. 每个 `ModelConfig` 独立配置 `protocol` (`chat` \| `resp`)、`maxContextTokens`、`maxOutputTokens`，并在发送前由 `TokenBudgetManager` 自动预留 `maxOutputTokens` 并执行滑动窗口裁剪。 | `stage02`<br>`stage04` |
| **8** | **Provider 自定义请求头与 `${session-hash}` 等预设变量** | 构建 **`HeaderVariableResolver` 动态请求头模板引擎**：<br>1. 每个 Provider 可开启/关闭自定义 Header 列表；<br>2. 内置确定性变量替换：`${session-hash}`（基于 `WorkspaceId + SessionId` 的 SHA-256 16位固定短哈希，专为 LLM 厂商 Prompt Cache 路由亲和性优化）、`${session-id}`、`${workspace-hash}`、`${model-id}`、`${request-id}`、`${timestamp}`。 | `stage02` |

---

## 二、 系统分层架构全景图

```text
+---------------------------------------------------------------------------------------------------+
|                      Web.Client (Vue 3 + TS + Naive UI 全量引入 + Tailwind v3.4)                  |
|                                  [构建目标锁定: build.target = 'chrome109']                        |
|  +-----------------------+  +----------------------------+  +----------------------------------+  |
|  |  Workspace & Session  |  |   Agent Chat & Diff View   |  |  Settings Center (NModal/NTabs)  |  |
|  |  Sidebar (NSplit/Tree)|  |   (流式Markdown/Tool卡片)   |  |  (全局代理/多Provider/存储路径)    |  |
|  +-----------+-----------+  +-------------+--------------+  +-----------------+----------------+  |
|              |                            |                                   |                   |
|  +-----------v----------------------------v-----------------------------------v----------------+  |
|  |                     Frontend IPC Client & Event Bus (window.hostBridge / __haloEventBus)    |  |
+--------------+-----------------------------------------------------------------+------------------+
               | CefSharp Async JS Binding (Task<BridgeResponse<T>>)             | 16ms Throttled Push
===============|=================================================================|===================
               v                                                                 |
+--------------+-----------------------------------------------------------------+------------------+
|                                 Host.Desktop (.NET 6.0 WinForms + CefSharp 109.1.110)             |
|  +---------------------------------------------------------------------------------------------+  |
|  | Bridge Layer (SystemBridge / ConfigBridge / WorkspaceBridge / AgentBridge)                  |  |
|  +----------------------------------------------+----------------------------------------------+  |
|                                                 |                                                 |
|  +-----------------------+  +-------------------v-------------------+  +-----------------------+  |
|  | Workspace & Session   |  |         AgentOrchestrator             |  | Tool Execution Engine |  |
|  | Manager               |  |  (ReAct Loop / Context Token Budget   |  | - read / write / edit |  |
|  | - Multi-Workspace     |  |   / Cancellation / Tool Dispatch)     |  | - list_dir / grep     |  |
|  | - SessionHash (SHA256)|  +-------------------+-------------------+  | - exec (CMD -> Python)|  |
|  | - JSONL Persistence   |                      |                      | - Win32 Job Object    |  |
|  +-----------+-----------+                      v                      +-----------+-----------+  |
|              |              +---------------------------------------+              |              |
|              |              |        LLM Dual-Protocol Gateway      |              |              |
|              |              |  - ChatCompletionsAdapter (/v1/chat)  |              |              |
|              |              |  - ResponsesApiAdapter (/v1/responses)|              |              |
|              |              |  - HeaderVariableResolver (${...})    |              |              |
|              |              +-------------------+-------------------+              |              |
|              |                                  |                                  |              |
|  +-----------v----------------------------------v----------------------------------v-----------+  |
|  |                     Infrastructure Foundation (跨代基础设施底座)                              |  |
|  |  +--------------------------+  +---------------------------+  +--------------------------+  |  |
|  |  | Storage & Bootstrap      |  | Unified ProxyHub          |  | Structured Rolling Logger|  |  |
|  |  | (C:\HaloAgentData + 可改) |  | (System / Custom / None)  |  | (App / LLM / Tool Logs)  |  |  |
|  |  +--------------------------+  +---------------------------+  +--------------------------+  |  |
+---------------------------------------------------------------------------------------------------+
```

---

## 三、 仓库标准目录结构设计

严格对齐 `docs/CEFSHARP_ARCHITECTURE_STANDARD.md` 第二章第 3 节规范，细化后的工程完整目录如下：

```text
F:\MyProgram\CSharp\halo-agent\
├── global.json                                 # 锁定 .NET 6.0 SDK 版本 (6.0.428)
├── HaloAgent.sln                               # 解决方案文件
├── .vscode/                                    # 纯 VS Code 构建与前后端联调配置
│   ├── tasks.json
│   └── launch.json
├── src/
│   ├── Host.Desktop/                           # C# WinForms + CefSharp 109 宿主工程
│   │   ├── Host.Desktop.csproj                 # 锁死 net6.0-windows + CefSharp 109.1.110 + x64/x86
│   │   ├── Program.cs                          # TLS1.2 兜底、Win7 GPU 禁用、app://local 注册、CEF 初始化
│   │   ├── MainForm.cs                         # 全屏 ChromiumWebBrowser 宿主窗体与 Bridge 挂载
│   │   ├── Bridge/                             # 暴露给 JS 的异步桥接门面与事件推送器
│   │   │   ├── HostBridgeFacade.cs             # 统一挂载至 window.hostBridge
│   │   │   └── JsEventDispatcher.cs            # C# -> JS 流式事件节流推送器
│   │   ├── Handlers/                           # CEF 生命周期、右键菜单屏蔽、外链拦截处理器
│   │   ├── Core/                               # 核心领域模型与接口定义
│   │   │   ├── Models/                         # Settings, Provider, Model, Workspace, Session, Message
│   │   │   └── Interfaces/                     # 各子系统契约接口
│   │   ├── Infrastructure/                     # 基础设施层
│   │   │   ├── Storage/                        # Bootstrap 路径引导、DPAPI 加密、JSONL 原子读写、目录迁移
│   │   │   ├── Logging/                        # 分级滚动文件日志引擎 (App / LlmTrace / ToolExec)
│   │   │   └── Network/                        # ProxyHub (System/Custom/None)、HttpClient 池、Header 变量解析器
│   │   ├── LlmGateway/                         # OpenAI 双协议网关层
│   │   │   ├── Adapters/                       # ChatCompletionsAdapter & ResponsesApiAdapter
│   │   │   └── Sse/                            # 高性能流式 SSE 解析器
│   │   ├── Tools/                              # Win7 兼容标准工具层
│   │   │   ├── Process/                        # Win32 JobObject 进程树管理、CMD/Python 执行器、编码探测
│   │   │   └── BuiltIn/                        # ReadFile, WriteFile, EditFile, ExecCommand, ListDir, GrepSearch
│   │   └── Services/                           # 业务编排层
│   │       ├── WorkspaceSessionService.cs      # 多工作区与多会话管理
│   │       ├── TokenBudgetService.cs           # 上下文窗口计算与历史裁剪
│   │       └── AgentOrchestrator.cs            # ReAct 循环控制、流式状态机与中断管理
│   └── Web.Client/                             # 前端工程 (Vite + Vue 3 + Pinia + Naive UI 全量 + Tailwind v3.4)
│       ├── package.json
│       ├── vite.config.ts                      # 锁死 target: 'chrome109', outDir: '../Host.Desktop/wwwroot'
│       └── src/
│           ├── theme/                          # Naive UI GlobalThemeOverrides (IDE级暗黑/浅色主题与紧凑字号定制)
│           ├── bridge/                         # hostBridge TS 类型声明与事件总线封装
│           ├── stores/                         # workspaceStore, sessionStore, providerStore, agentStore
│           ├── components/                     # 聊天流、Tool 卡片、Diff 视图、命令行终端、变量胶囊编辑器
│           └── views/                          # 主工作台视图、设置中心模态框
├── build/                                      # 打包发布脚本与 Inno Setup 安装工程
│   ├── publish-win-x64.ps1
│   ├── publish-win-x86.ps1
│   └── installer.iss
└── docs/
    ├── CEFSHARP_ARCHITECTURE_STANDARD.md       # 架构最高铁律宪章
    └── develop/                                # 分阶段详细研发设计文档（本目录）
```

---

## 四、 分阶段开发路线图与文档索引

为保证研发节奏可控、每一阶段均可独立编译、独立运行、独立验收，项目拆分为 **6 个递进式阶段（Stage 01 ~ Stage 06）**：

### [Stage 01：跨代宿主底座、C盘可配置存储日志中枢与前后端脚手架](./stage01/01-stage-overview-and-preflight.md)
* [`01-stage-overview-and-preflight.md`](./stage01/01-stage-overview-and-preflight.md)：前置检查、阶段目标、目录骨架与交付物清单
* [`02-cefsharp-win7-host-and-ipc.md`](./stage01/02-cefsharp-win7-host-and-ipc.md)：WinForms + CefSharp 109 防崩宿主、`app://local` 协议、`hostBridge` 异步通信与事件总线实现方案
* [`03-storage-and-logging-engine.md`](./stage01/03-storage-and-logging-engine.md)：C 盘固定默认位置 (`C:\HaloAgentData`)、用户自定义路径动态修改与迁移、分级滚动日志实现方案
* [`04-frontend-scaffold-and-acceptance.md`](./stage01/04-frontend-scaffold-and-acceptance.md)：Vite (`chrome109`) + Vue 3 前端脚手架、Win7 字体与 SVG 规范、Stage 01 验收标准

### [Stage 02：统一网络代理中枢、多 Provider/Model 体系与动态请求头引擎](./stage02/01-stage-overview-and-preflight.md)
* [`01-stage-overview-and-preflight.md`](./stage02/01-stage-overview-and-preflight.md)：前置检查、阶段目标与配置中心领域模型设计
* [`02-unified-proxy-hub.md`](./stage02/02-unified-proxy-hub.md)：统一网络代理中枢（系统代理自动检测 / 手动配置 / 强制直连 / Provider 级联覆盖）与连通性测试实现方案
* [`03-provider-model-and-header-engine.md`](./stage02/03-provider-model-and-header-engine.md)：多 Provider 与多 Model (`chat`/`resp`、上下文与输出 Token) 管理、DPAPI 加密及 `${session-hash}` 预设变量 Header 引擎
* [`04-settings-ui-and-acceptance.md`](./stage02/04-settings-ui-and-acceptance.md)：前端网络代理、Provider/Model 与自定义 Header 交互界面设计及 Stage 02 验收标准

### [Stage 03：OpenAI 双协议（Chat Completions & Responses）流式网关](./stage03/01-stage-overview-and-preflight.md)
* [`01-stage-overview-and-preflight.md`](./stage03/01-stage-overview-and-preflight.md)：前置检查、阶段目标与双协议统一中间表示（Unified IR）设计
* [`02-chat-completions-adapter.md`](./stage03/02-chat-completions-adapter.md)：OpenAI `Chat Completions` (`/v1/chat/completions`) 协议适配器与流式 ToolCall 聚合实现方案
* [`03-responses-api-adapter.md`](./stage03/03-responses-api-adapter.md)：OpenAI `Responses` (`/v1/responses`) 协议适配器与事件驱动 SSE 状态机实现方案
* [`04-stream-pipeline-and-acceptance.md`](./stage03/04-stream-pipeline-and-acceptance.md)：16ms 节流推送管道、流式中断控制、LLM 报文审计日志与 Stage 03 验收标准

### [Stage 04：多工作路径（Workspace）、多会话（Session）与上下文预算管理](./stage04/01-stage-overview-and-preflight.md)
* [`01-stage-overview-and-preflight.md`](./stage04/01-stage-overview-and-preflight.md)：前置检查、阶段目标与多项目/多会话存储结构设计
* [`02-workspace-and-session-manager.md`](./stage04/02-workspace-and-session-manager.md)：Workspace 文件夹管理、Session 生命周期、确定性 `${session-hash}` 生成与 JSONL 持久化实现方案
* [`03-context-window-and-token-budget.md`](./stage04/03-context-window-and-token-budget.md)：基于 Model `maxContextTokens` 与 `maxOutputTokens` 的 Token 估算、滑动窗口裁剪与工具输出配额控制
* [`04-workspace-ui-and-acceptance.md`](./stage04/04-workspace-ui-and-acceptance.md)：多项目切换器、会话列表管理 UI 与 Stage 04 验收标准

### [Stage 05：Win7 兼容工具执行引擎（CMD/Python 优先）与标准基础 Tools](./stage05/01-stage-overview-and-preflight.md)
* [`01-stage-overview-and-preflight.md`](./stage05/01-stage-overview-and-preflight.md)：前置检查、阶段目标与标准 Tool 契约架构设计
* [`02-cmd-and-python-execution-engine.md`](./stage05/02-cmd-and-python-execution-engine.md)：`exec_command` 核心实现：CMD 优先策略、Python 环境探测、Win32 Job Object 进程树管理与 GBK/UTF-8 自适应解码
* [`03-file-and-search-standard-tools.md`](./stage05/03-file-and-search-standard-tools.md)：`read_file`、`write_file`、`edit_file`、`list_dir`、`grep_search` 五大文件与检索工具实现方案
* [`04-tool-safety-and-acceptance.md`](./stage05/04-tool-safety-and-acceptance.md)：工作区路径沙箱校验、高危命令防护、超长输出转存机制与 Stage 05 验收标准

### [Stage 06：Agent ReAct 核心编排、前端工作台交互闭环与 Win7 打包交付](./stage06/01-stage-overview-and-preflight.md)
* [`01-stage-overview-and-preflight.md`](./stage06/01-stage-overview-and-preflight.md)：前置检查、阶段目标与端到端交付蓝图
* [`02-agent-react-loop-and-prompt.md`](./stage06/02-agent-react-loop-and-prompt.md)：Agent ReAct 多步循环状态机、CMD/Python 优先系统提示词（System Prompt）工程与错误自愈机制
* [`03-workbench-ui-and-diff-viewer.md`](./stage06/03-workbench-ui-and-diff-viewer.md)：Agent 聊天工作台、Tool 执行可视化卡片、文件修改 Diff 对比器与终端输出面板
* [`04-win7-packaging-and-final-acceptance.md`](./stage06/04-win7-packaging-and-final-acceptance.md)：Self-Contained 自动化发布、Inno Setup 安装器（含 VC++ 与 `KB2533623` 预检）与全项目终验标准
