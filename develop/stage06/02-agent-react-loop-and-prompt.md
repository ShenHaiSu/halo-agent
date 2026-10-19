# Stage 06 - 02：Agent ReAct 核心循环与 CMD/Python 优先系统提示词工程

> **所属阶段**：Stage 06
> **核心对应需求**：需求 #2（双协议支持）、需求 #3（默认推荐 `cmd`，其次 `py`）、需求 #6（标准基础 Tools 闭环调用）

---

## 一、 动态系统提示词工程（`SystemPromptBuilder.cs`）

为了让不同厂商的模型（OpenAI GPT-4o/4.1/o3、DeepSeek V3/R1、Claude 等）在执行任务时严格遵守跨代 Windows 规范并高效使用基础工具，`SystemPromptBuilder` 在每次会话推理时动态生成如下结构化系统指令：

```markdown
You are Halo-Agent, an expert software engineering and system automation AI agent running natively on Windows.

## 1. Runtime & Environment Facts
- Host OS: {{OsVersionString}} (Architecture: {{OsArch}})
- Active Workspace Name: {{WorkspaceName}}
- Active Workspace Root Path: {{WorkspaceRootPath}}
- Default Command Shell: `cmd.exe` (Windows Command Prompt)
- Detected Python Runtime: {{PythonStatusAndVersion}}
- Current Date: {{CurrentLocalDate}}

## 2. Command Execution Strategy (CRITICAL RULE)
Because PowerShell versions vary drastically between Windows 7 (PowerShell 2.0) and Windows 10/11 (PowerShell 5.1+), you MUST follow this strict execution priority when using `exec_command`:
1. **FIRST CHOICE (`runner: "cmd"`)**: Always default to standard Windows `cmd.exe` commands (`dir`, `type`, `findstr`, `copy`, `move`, `del`, `mkdir`, `where`, `tasklist`, `dotnet`, `node`, `git`, etc.) for shell operations and project builds.
2. **SECOND CHOICE (`runner: "python"`)**: When you need complex text processing, regex transformations, JSON/XML manipulation, or multi-step data calculation that is awkward in `cmd.exe`, use `runner: "python"` to run inline Python code or `.py` scripts.
3. **AVOID PowerShell**: Do NOT invoke `powershell` or `pwsh` unless the user explicitly asks you to debug a `.ps1` script.

## 3. Standard Tool Usage Guidelines
- Before modifying an existing file, ALWAYS read the relevant section first using `read_file`.
- For targeted modifications to existing files, prefer `edit_file` (providing enough surrounding lines in `old_string` for a unique match) instead of overwriting the entire file with `write_file`.
- Use `list_dir` and `grep_search` to explore the workspace efficiently rather than spawning unnecessary shell processes.
- All relative paths passed to tools resolve against the Active Workspace Root Path (`{{WorkspaceRootPath}}`).
```

---

## 二、 Agent ReAct 核心循环状态机（`AgentOrchestrator.cs`）

### 1. ReAct 状态流转图
```text
[User 发送消息]
       │
       ▼
(1) 追加 UserMessage 至 messages.jsonl & 更新会话标题
       │
       ▼
+---> (2) ReAct Turn Start (Iteration = 1..MaxIterations)
|      │
|      ▼
|   (3) 调用 TokenBudgetService 结合 Model.MaxContextTokens & MaxOutputTokens 裁剪历史窗口
|      │
|      ▼
|   (4) 构造 HeaderResolutionContext (注入会话固定 ${session-hash} 与新 ${request-id})
|      │
|      ▼
|   (5) 调用 LlmGatewayClient.StreamAsync (根据 Model.Protocol 自动分发至 Chat 或 Resp 适配器)
|      │   ├── 实时推送 ReasoningDelta / TextDelta / ToolCallStarted 至前端 UI
|      │   └── 汇总完整 AssistantMessage 并追加写入 messages.jsonl
|      │
|      ▼
|   (6) 检查本轮 AssistantMessage 是否包含 ToolCalls？
|      ├── [否：无 ToolCalls] ---> 任务完成，推送 TurnCompleted 事件，结束循环！
|      │
|      └── [是：含 1..N 个 ToolCalls]
|             │
|             ▼
|          (7) 依次调度 ToolRegistry 执行每个 ToolCall (read / write / edit / exec / list_dir / grep)
|             │   ├── 实时向前端推送 ToolExecutionStarted / StdoutDelta / ToolExecutionCompleted
|             │   └── 将每个 ToolResult 封装为 UnifiedChatMessage(Role=Tool) 追加写入 messages.jsonl
|             │
+-------------+ (自动进入下一轮 Iteration，将工具执行结果回传给 LLM)
```

### 2. 工具调用容错与自愈设计（Tool Self-Healing）
在步骤 `(7)` 中，如果 LLM 生成的工具参数存在瑕疵（例如：JSON 格式轻微截断、`edit_file` 的 `old_string` 未命中、或访问了不存在的文件路径）：
* **绝不抛出未捕获异常中断整个 ReAct 循环**！
* `ToolRegistry` 捕获该异常并将其格式化为带指导建议的工具输出消息：
  `"[Tool Error - edit_file]: old_string was not found in 'src/main.py'. Hint: Call read_file('src/main.py') first to inspect the exact lines before retrying edit_file."`
* 将该错误作为 `ToolResult` 正常回传给 LLM，模型在下一轮 Iteration 中会自动先调用 `read_file` 再重新 `edit_file`，实现全自动错误自愈。
