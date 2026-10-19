# Stage 06 - 01：Agent ReAct 核心编排、前端工作台闭环与 Win7 安装包交付（前置检查与当期目标）

> **所属阶段**：Stage 06（Agent 业务全闭环与生产级打包交付）
> **依赖前置**：Stage 01 ~ Stage 05 已全部完成并通过验收
> **关联宪章**：[`docs/CEFSHARP_ARCHITECTURE_STANDARD.md`](../../CEFSHARP_ARCHITECTURE_STANDARD.md) 第六章《打包、分发与 Win7 落地预检清单》

---

## 一、 前置检查（Preflight Checklist）

1. **核心子系统联通性确认**：
   * [x] **Stage 01 存储与日志**：`C:\HaloAgentData`（及自定义路径）读写正常，三通道滚动日志正常。
   * [x] **Stage 02 网络与配置**：`HaloHttpClientFactory` 代理中枢、多 Provider/Model、`${session-hash}` 自定义 Header 解析正常。
   * [x] **Stage 03 双协议网关**：`ChatCompletionsAdapter` (`chat`) 与 `ResponsesApiAdapter` (`resp`) 流式输出与 ToolCall 解析正常。
   * [x] **Stage 04 工作区与会话**：多工作路径切换、单项目多会话 JSONL 追加存储、上下文预算裁剪正常。
   * [x] **Stage 05 标准工具箱**：`read_file`、`write_file`、`edit_file`、`exec_command`（CMD 优先、Python 次之）、`list_dir`、`grep_search` 执行正常。
2. **本机打包工具确认**：
   * [x] 本机已安装 Inno Setup 6 (`C:\Program Files (x86)\Inno Setup 6\ISCC.exe`)，可用于自动化生成标准 Windows 安装包 `HaloAgent-Setup.exe`。

---

## 二、 当期阶段目标（Stage 06 Objectives）

1. **实现工业级 `AgentOrchestrator`（多步 ReAct 自动循环状态机）**：
   * 串联“用户输入 -> 上下文预算裁剪 -> 双协议 LLM 流式推理 -> 工具调用自动识别与并发/顺序执行 -> 工具结果回写 JSONL -> 自动触发下一轮推理直至任务完成”的完整闭环。
   * 内置最大单次循环步数保护（`MaxIterations = 25`，到达时暂停询问用户是否继续），防止异常情况下无限死循环消耗 Token。
2. **构建针对 Win7/10/11 优化的动态系统提示词引擎（`SystemPromptBuilder`）**：
   * 将当前操作系统版本、当前激活的工作路径（`Workspace.RootPath`）、本机探测到的 `cmd.exe` 与 `Python` 环境状态、以及 **“命令执行首选 `cmd`，次选 `python`，严禁依赖高版本 PowerShell 专属语法”** 的铁律动态注入上下文。
3. **打造现代、流畅的 Agent 桌面工作台 UI（Workbench UI）**：
   * 支持 Markdown + 代码语法高亮流式渲染、可折叠思考过程（Reasoning Block）、可视化工具调用状态卡片（展示 `read`/`write`/`edit` 的 Unified Diff 对比视图及 `exec_command` 的终端黑底实时输出）。
4. **完成面向 Win7 SP1 / Win10 / Win11 的自包含发布与 Inno Setup 安装工程**：
   * 编写自动化构建脚本（先执行 `pnpm build` 编译 `chrome109` 前端产物，再执行 `dotnet publish -c Release -r win-x64/win-x86 --self-contained true -p:PublishSingleFile=false`）。
   * 编写 `build/installer.iss` 安装脚本，内置 **Windows 7 SP1 (Build 7601) 版本检查、`KB2533623` API (`AddDllDirectory`) 存在性预检、以及 VC++ 运行时兜底**。

---

## 三、 Stage 06 交付产物清单

| 模块分类 | 文件路径 | 核心职责说明 |
| :--- | :--- | :--- |
| **提示词工程** | `src/Host.Desktop/Services/SystemPromptBuilder.cs` | 动态拼装含工作区环境、CMD/Python 优先规范及工具指南的 System Prompt |
| **Agent 编排** | `src/Host.Desktop/Services/AgentOrchestrator.cs` | 多步 ReAct 循环引擎、工具调度、消息持久化与实时事件分发 |
| **前端工作台** | `src/Web.Client/src/views/WorkbenchView.vue` | 三栏/双栏响应式 Agent 主工作台视图 |
| **前端消息流** | `src/Web.Client/src/components/chat/MessageTimeline.vue` | 流式 Markdown、思考链折叠框、Token & Cache 命中徽标 |
| **前端工具卡片** | `src/Web.Client/src/components/chat/ToolCallCard.vue` | 文件读写 Diff 对比视图、CMD/Python 命令行输出面板 |
| **构建脚本** | `build/publish.ps1` | 前后端一体化自包含编译脚本（严禁开启 `PublishSingleFile`） |
| **安装包工程** | `build/installer.iss` | Inno Setup 6 安装包脚本（含 Win7 SP1 补丁与运行库检测） |

---

## 四、 本阶段文档导航

* 下一篇：[`02-agent-react-loop-and-prompt.md`](./02-agent-react-loop-and-prompt.md) —— 详细设计 Agent ReAct 编排状态机与 CMD/Python 优先系统提示词模板。
* 第三篇：[`03-workbench-ui-and-diff-viewer.md`](./03-workbench-ui-and-diff-viewer.md) —— 详细设计前端聊天工作台、Tool 卡片、Diff 视图与终端回显组件。
* 第四篇：[`04-win7-packaging-and-final-acceptance.md`](./04-win7-packaging-and-final-acceptance.md) —— 详细设计自包含发布流水线、Inno Setup Win7 预检脚本与全项目终验标准。
