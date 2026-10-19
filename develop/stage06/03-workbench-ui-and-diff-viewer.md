# Stage 06 - 03：前端主工作台、ReAct 消息流、Diff 对比卡片与终端面板详细设计（Naive UI + Tailwind）

> **所属阶段**：Stage 06
> **核心目标**：在 Chromium 109 下结合 **Naive UI 全量组件** 与 **Tailwind CSS v3.4** 打造媲美 Cursor / Windsurf 的桌面级 AI IDE 工作台，兼具高信息密度、丝滑流式渲染与极致跨代兼容性。

---

## 一、 主工作台全局视口布局与响应式层次（`WorkbenchView.vue`）

主工作台严格采用 **全屏桌面应用无外框滚动布局（`h-screen w-screen overflow-hidden flex flex-col`）**，像素级尺寸规划如下：

```text
+------------------------------------------------------------------------------------------------------+
| TopBar (h-10 / 40px):                                                                                |
| [折叠侧栏] [Workspace: halo-agent v] / [Session: 重构代理模块] | [Model: DeepSeek-V3 [CHAT] v]       |
|                                         [水位仪: 18.4K/128K] [Hash: a94f8c] [代理: SYSTEM] [设置]    |
+-------------------------+----------------------------------------------------------------------------+
| Left Sidebar            | Main Chat & ReAct Stream Area (Flex-1, flex-col, bg-[var(--bg-app)])       |
| (NSplit Pane 1: 260px)  | +------------------------------------------------------------------------+ |
|                         | | MessageTimeline (Flex-1, overflow-y-auto, max-w-4xl mx-auto w-full)    | |
| [+ 新建会话 (Ctrl+N)]   | |                                                                        | |
| [搜索会话...]           | |  [User Message Card - 右对齐或通栏紧凑卡片]                            | |
| ----------------------- | |  帮我检查当前目录下的文件，修改 config.py 并用 python 运行测试。       | |
| PINNED                  | |                                                                        | |
| * 重构代理模块          | |  [Assistant ReAct Turn Container]                                      | |
| TODAY                   | |  +-- (1) 思考过程折叠舱 (ReasoningCollapseBox) ---------------------+  | |
|   测试 Responses 协议   | |  | [SVG: BrainCircuit] 深度思考过程 (耗时 1.8s)           [点击折叠] |  | |
|   清理临时日志文件      | |  | > 用户需要检查目录并修改 config.py，我应先调用 list_dir 和        |  | |
|                         | |  |   read_file 查看现有内容，再用 edit_file 修改，最后用 python 验证 |  | |
|                         | |  +------------------------------------------------------------------+  | |
|                         | |  +-- (2) 工具调用时间轴卡片组 (ToolCallCard Group) -----------------+  | |
|                         | |  | [SVG: FolderSearch] list_dir (path: ".")        [已完成 · 8ms] [v]|  | |
|                         | |  +------------------------------------------------------------------+  | |
|                         | |  | [SVG: FileDiff] edit_file ("config.py")  [+3 -1 · 已完成 14ms] [^]|  | |
|                         | |  |   10 | TIMEOUT = 30                                              |  | |
|                         | |  | - 11 | RETRY_COUNT = 1                                           |  | |
|                         | |  | + 11 | RETRY_COUNT = 3                                           |  | |
|                         | |  | + 12 | ENABLE_PROXY = True                                       |  | |
|                         | |  +------------------------------------------------------------------+  | |
|                         | |  | [SVG: Terminal] exec_command [PYTHON]    [ExitCode: 0 · 112ms] [^]|  | |
|                         | |  | $ python test_runner.py                                          |  | |
|                         | |  | [PASS] All 4 configuration tests succeeded!                      |  | |
|                         | |  +------------------------------------------------------------------+  | |
|                         | |  +-- (3) 最终 Markdown 正文回复 (MarkdownRenderer) -----------------+  | |
|                         | |  | 已完成 `config.py` 的重试参数更新并通过 Python 测试脚本验证。     |  | |
|                         | |  +------------------------------------------------------------------+  | |
|                         | |  [Footer: 协议 CHAT | 首字 320ms | 总耗时 4.1s | In: 2.1K (Cache 85%)] | |
|                         | +------------------------------------------------------------------------+ |
|                         | +------------------------------------------------------------------------+ |
|                         | | Bottom Prompt Composer (底部悬浮输入舱, px-6 pb-4 pt-2)                | |
|                         | | +--------------------------------------------------------------------+ | |
|                         | | | [多行自适应输入框 NInput autosize: Enter 发送, Shift+Enter 换行]   | | |
|                         | | | ------------------------------------------------------------------ | | |
|                         | | | [工作目录: F:\...\halo-agent] [默认Runner: CMD优先/PY次之]  [发送] | | |
|                         | | +--------------------------------------------------------------------+ | |
|                         | +------------------------------------------------------------------------+ |
+-------------------------+----------------------------------------------------------------------------+
| Bottom StatusBar (h-6 / 26px, bg-[var(--bg-sidebar)] border-t border-[var(--border-subtle)]):        |
| [● Ready] [OS: Windows 7 SP1 / Windows 11] [存储根目录: C:\HaloAgentData] [Python: 3.8.10 已就绪]    |
+------------------------------------------------------------------------------------------------------+
```

---

## 二、 核心可视化组件与交互细节设计

### 1. 深度思考流折叠舱（`ReasoningCollapseBox.vue`）
针对支持推理过程输出的模型（如 OpenAI `o1`/`o3`/`o4-mini` 的 `response.reasoning_summary_text.delta` 或 DeepSeek-R1 的 `reasoning_content`）：
* **视觉样式**：采用 Tailwind `bg-slate-900/60 border-l-2 border-indigo-500/70 rounded-r-md my-2` 区分于普通正文。
* **自动展开/折叠状态机**：
  * 当模型**正在输出思考流**时，自动保持展开状态，标题右侧显示 Lucide `Loader2` 旋转图标与实时计时器（`思考中... 1.4s`）。
  * 当模型**思考结束、开始输出正式正文或发起 ToolCall** 时，可自动平滑收起（或根据用户偏好保持展开），收起态仅占单行 `28px` 高度，随时可点击查看完整思维链。

---

### 2. 标准基础工具可视化卡片（`ToolCallCard.vue`）
拒绝将工具调用渲染为枯燥的 JSON 原始字符串，基于工具类型打造 **4 类专属高颜值交互卡片**（全部采用 Lucide SVG 图标 + Naive UI `NTag` / `NCollapseTransition`）：

#### (1) 文件修改与写入卡片（`edit_file` & `write_file` -> 内置 `UnifiedDiffViewer.vue`）
* **卡片头部（Header Bar）**：
  * 左侧：`FileCode` / `FileEdit` SVG 图标 + 等宽字体文件路径（如 `src/config.py`，点击可调用 `hostBridge.openPathInExplorer` 在资源管理器中定位该文件）。
  * 右侧：变更统计徽标 `<span class="text-emerald-400 font-mono text-xs">+12</span> <span class="text-rose-400 font-mono text-xs">-4</span>` + 耗时 `<n-tag size="tiny">15ms</n-tag>` + 展开/折叠箭头。
* **卡片主体（双列行号 Unified Diff 视图）**：
  * 默认自动展开前 15 行核心变更（超出部分提供 `[展开全部 48 行变更]` 按钮）：
  * 删除行：`bg-rose-500/15 text-rose-200 border-l-2 border-rose-500`（前缀 `-`）
  * 新增行：`bg-emerald-500/15 text-emerald-200 border-l-2 border-emerald-500`（前缀 `+`）
  * 上下文行：`text-slate-400`

#### (2) 跨代命令行执行卡片（`exec_command` -> 内置 `TerminalOutputBox.vue`）
* **卡片头部（Terminal Title Bar）**：
  * 左侧：**执行器语义徽标（Runner Badge）**：
    * 若 `runner === 'cmd'`：显示深蓝工业风徽标 `<n-tag type="info" size="tiny">CMD.EXE</n-tag>`
    * 若 `runner === 'python'`：显示青绿徽标 `<n-tag type="success" size="tiny">PYTHON</n-tag>`
  * 中部：单行高亮显示执行的命令字符串（如 `dir /b /s *.cs` 或 `python -c "..."`）。
  * 右侧：
    * 运行中：`<n-button size="tiny" type="error" ghost @click="killProcess">终止进程</n-button>` + 实时秒表。
    * 已完成：退出码徽标（`ExitCode === 0` 显示绿色 `Exit: 0`，非 0 显示红色 `Exit: 1`）+ 耗时（如 `142ms`）+ `[复制输出]` 按钮。
* **卡片主体（仿真黑底终端视窗）**：
  * 采用固定最大高度 `max-h-72 overflow-y-auto bg-[#090d12] p-3 font-mono text-xs leading-relaxed text-slate-200 select-text`。
  * 实时流式追加 `stdout`（亮白字）与 `stderr`（琥珀/浅红字），在流式输出期间自动平滑锚定到底部（Auto-scroll to bottom）。

#### (3) 文件读取与检索卡片（`read_file` / `list_dir` / `grep_search`）
* 默认采用 **单行紧凑折叠条（高度 `32px`）**，避免大段文件读取内容刷屏淹没对话主线：
  * 例如：`[SVG: FileText] 已读取文件 src/Program.cs (第 1 - 120 行 · UTF-8) [点击展开源码]`
  * 例如：`[SVG: Search] grep_search 检索 "UseProxy" (在 4 个文件中匹配到 9 处结果) [点击查看匹配行]`
* 点击后通过 `<n-collapse-transition>` 展开带行号与语法高亮的详情面板。

---

### 3. 底部多功能输入舱（`PromptComposer.vue`）
* **焦点光晕反馈**：外框采用 Tailwind `rounded-xl border border-[var(--border-subtle)] bg-[var(--bg-surface)] focus-within:border-blue-500/80 focus-within:ring-2 focus-within:ring-blue-500/15 transition-all shadow-lg`。
* **自适应多行输入**：使用 `<n-input type="textarea" :autosize="{ minRows: 2, maxRows: 8 }" :bordered="false" />`，支持 `Enter` 直接发送、`Shift + Enter` 换行。
* **底部操控工具条**：
  * 左侧展示当前工作路径安全沙箱徽标（`Workspace: halo-agent`）与工具权限模式（`自动执行标准工具` / `写入与命令需确认`）。
  * 右侧：
    * 空闲态：`<n-button type="primary" size="small" @click="sendPrompt">`（图标：`SendHorizontal`）`发送 (Enter)`。
    * 生成/执行态：平滑切换为红色脉冲按钮 `<n-button type="error" size="small" @click="abortAgentLoop">`（图标：`Square`）`停止生成 (Esc)`，点击即触发后端 `CancellationTokenSource.Cancel()` 并通过 Job Object 立即中止正在运行的子进程。

---

### 4. 消息底部遥测与缓存命中审计栏（Message Telemetry Footer）
在每轮 Assistant 回复卡片底部，以不干扰阅读的 `11px` 次要文字展示底层网关指标，并在悬停时显示完整报文统计：
* `协议: CHAT (/v1/chat/completions)` 或 `协议: RESP (/v1/responses)`
* `首字延迟 (TTFT): 340ms | 总耗时: 3.2s`
* `Tokens: 输入 4,120 (Prompt Cache 命中 3,584 · 87.0%) / 输出 318`
* 右侧提供快捷操作图标按钮：`[复制 Markdown 全文]`、`[以此处为起点新建分支会话]`、`[重新生成]`。
