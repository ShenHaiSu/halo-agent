# Stage 05 - 04：工具安全边界、超长输出防爆机制与 Stage 05 验收标准

> **所属阶段**：Stage 05
> **核心目标**：防止工具调用撑爆上下文窗口或误伤系统目录，并完成 Stage 05 验收。

---

## 一、 超长工具输出自动截断与落盘转存（Output Spillover Protection）

当 Agent 执行 `exec_command`（例如 `dir /s` 或打印数万行构建日志）时，单次输出可能高达数 MB，一旦直接塞入 `messages` 会瞬间打爆模型的 `MaxContextTokens`。

### 统一输出守卫（`ToolOutputGuard`）
1. **单次内联返回上限**：`MaxInlineChars = 16,000`（约 4,000 Tokens）。
2. **超出上限时的自动转存流程**：
   * 当某次工具输出长度 `> 16,000` 字符时：
   * 将完整原始输出写入当前会话的独立快照文件：`<DataRootPath>/sessions/<workspaceId>/<sessionId>/tool-outputs/call_<callId>.txt`。
   * 构造给 LLM 的返回内容为：
     * **头部前 `4,000` 字符** +
     * `\n\n... [中间省略 N 字符，完整输出已保存至日志文件: <fullPath>，你可使用 read_file(file_path, offset, limit) 或 grep_search 按需分段查看] ...\n\n` +
     * **尾部最后 `8,000` 字符**（通常编译器错误和退出状态都在最末尾，因此尾部保留更大比重）。

---

## 二、 工作区路径校验与系统关键目录保护（`PathSafetyValidator`）

1. **相对路径解析基准**：
   * 所有工具传入的相对路径（如 `src/Program.cs`）一律基于当前会话所属的 `WorkspaceInfo.RootPath` 进行 `Path.GetFullPath(Path.Combine(workspaceRoot, inputPath))` 解析。
2. **高危系统路径写保护**：
   * 对于 `write_file` 与 `edit_file`，严禁修改系统核心目录（`C:\Windows`、`C:\Program Files`、`C:\Program Files (x86)`）下的任何文件。
   * 对于工作区根目录之外的文件写入，在返回结果中附加显式跨目录警告日志。

---

## 三、 Stage 05 验收标准（Acceptance Criteria）

| 验收编号 | 验收维度 | 具体测试步骤 | 预期通过标准 |
| :--- | :--- | :--- | :--- |
| **S05-AC01** | **`exec_command` CMD 优先与中文无乱码** | 调用 `exec_command`（不传 `runner`，默认 `cmd`）执行 `dir`、`ipconfig` 以及 `echo 中文测试` | 1. 进程通过 `cmd.exe /d /s /c` 启动<br>2. 返回的中文目录名、中文网络适配器名称、Echo 中文全部清晰无 `` 乱码 |
| **S05-AC02** | **`exec_command` Python 次选支持** | 调用 `exec_command`（`runner: "python"`）执行一段计算并打印 UTF-8 JSON 的多行 Python 脚本 | 正确探测本机 Python 并返回标准输出，临时脚本执行后自动清理 |
| **S05-AC03** | **Job Object 进程树强杀** | 调用 `exec_command` 执行 `cmd /c "ping 127.0.0.1 -n 60"`，设置 `timeout_ms: 2000`（2秒超时） | 2 秒到达后立即返回超时提示，检查任务管理器确认子进程 `ping.exe` 与 `cmd.exe` 已被同步强杀，无残留 |
| **S05-AC04** | **`read` / `write` / `edit` 全流程与 GBK 兼容** | 1. 准备一个 GBK 编码且带 `\r\n` 的源码文件<br>2. 调用 `read_file` 读取<br>3. 调用 `edit_file` 修改其中一段代码 | 1. `read_file` 准确识别 GBK 编码并正常显示中文注释与行号<br>2. `edit_file` 自动兼容 `\n` 与 `\r\n` 差异完成替换，生成正确的 Unified Diff，且写回后文件仍保持原编码不乱码 |
| **S05-AC05** | **超长输出转存保护** | 执行一条输出超过 50,000 字符的命令 | 返回给上下文的文本严格控制在 `16,000` 字符以内（含首尾片段），完整内容成功落盘至会话 `tool-outputs/` 目录 |
---

## 四、 Stage 05 验收记录（2026-10-13）

> 实现范围：`src/Host.Desktop/Tools/`（Process / Safety / BuiltIn / 注册调度中心）+
> `HostBridgeFacade` 调试接口 + `tests/HaloAgent.Tests/Stage05ToolExecutionTests.cs`。
> 约束遵守：`net6.0-windows` 未变更、CefSharp 109.1.110 未升级、无新增 NuGet 包、无前端改动、未 commit。

### 1. 新增文件一览

| 文件 | 说明 |
| :--- | :--- |
| `src/Host.Desktop/Tools/Process/SmartConsoleDecoder.cs` | `CodePagesEncodingProvider` 注册，严格 UTF-8 优先、失败回退 GBK(936) |
| `src/Host.Desktop/Tools/Process/JobObjectManager.cs` | P/Invoke `CreateJobObject` / `AssignProcessToJobObject` / `SetInformationJobObject`（`JobObjectExtendedLimitInformation=9`，`KILL_ON_JOB_CLOSE=0x2000`），非 Windows 安全降级 |
| `src/Host.Desktop/Tools/Process/CommandRunnerProbe.cs` | ComSpec 探测；python 优先级 设置路径 > PATH `python.exe`（排除 WindowsApps 空壳 + `--version` 实测）> `py -3`；`BuildCmdStartInfo(/d /s /c)` / `BuildPythonStartInfo`，注入 `PYTHONIOENCODING=utf-8`、`PYTHONUTF8=1`、`CI=true` |
| `src/Host.Desktop/Tools/Safety/PathSafetyValidator.cs` | 基于 WorkspaceRoot 解析；`write/edit` 禁止触碰 `C:\Windows`、`C:\Program Files`、`C:\Program Files (x86)`；工作区外写入附加警告 |
| `src/Host.Desktop/Tools/Safety/ToolOutputGuard.cs` | `MaxInlineChars=16000`，超限落盘 `<DataRoot>/sessions/<ws>/<sess>/tool-outputs/call_<id>.txt`，返回前 4000 + 省略提示 + 后 8000 |
| `src/Host.Desktop/Tools/IToolHandler.cs` | `ToolCallDto{Id,Name,ArgumentsJson}`、`ToolContext`、`IToolHandler{Name,Description,GetDefinition(),ExecuteAsync}` |
| `src/Host.Desktop/Tools/ToolDefinition.cs` | `ToChatToolJson()` / `ToResponsesToolJson()` 双协议导出 |
| `src/Host.Desktop/Tools/ToolResult.cs` | `{Success,Output,Error,ExitCode,DurationMs,SpilledFile}` |
| `src/Host.Desktop/Tools/ToolRegistry.cs` | 注册 6 工具；`DispatchAsync` 捕获异常转自愈提示；`GetDefinitions()` / `GetChatToolsJson()` / `GetResponsesToolsJson()`；输出统一过 `ToolOutputGuard` |
| `src/Host.Desktop/Tools/BuiltIn/ExecCommandTool.cs` | cmd/python 二选一（timeout 默认 30s、最大 300s）、JobObject + BaseStream 字节异步读取 + SmartDecoder + 内联脚本落盘执行后自动清理 |
| `src/Host.Desktop/Tools/BuiltIn/ReadFileTool.cs` | 二进制 `\0` 检测、BOM/UTF-8/GBK 探测、1-based 行号分页（offset 默认 1、limit 默认 500、最大 2000） |
| `src/Host.Desktop/Tools/BuiltIn/WriteFileTool.cs` | 自动建父目录、继承原编码换行、原子写（临时文件 + Move 覆盖） |
| `src/Host.Desktop/Tools/BuiltIn/EditFileTool.cs` | 归一化 `\n` 匹配、唯一性校验、`replace_all`、Unified Diff（±3 行） |
| `src/Host.Desktop/Tools/BuiltIn/ListDirTool.cs` | recursive/max_depth（默认 2，上限 10），排除 `.git`/`node_modules`/`bin`/`obj`/`dist`/`__pycache__`/`.vs` |
| `src/Host.Desktop/Tools/BuiltIn/GrepSearchTool.cs` | Regex + `Parallel.ForEachAsync`、include_glob 简易匹配、最多 100 条 |
| `src/Host.Desktop/Tools/BuiltIn/FileToolShared.cs` | 文件工具内部共享：三级编码探测、换行识别、原子写回 |
| `tests/HaloAgent.Tests/Stage05ToolExecutionTests.cs` | 15 个测试，覆盖 S05-AC01~AC05 + 系统路径写保护 + 双协议 Schema（并行禁用沿用 Stage01 全局 `CollectionBehavior`） |

### 2. 存量文件变更

| 文件 | 变更 |
| :--- | :--- |
| `src/Host.Desktop/Bridge/HostBridgeFacade.cs` | 追加 Stage 05 调试区：`ToolRegistry` 懒加载属性、`GetToolDefinitions(protocol)`、`ExecuteTool(workspaceId, sessionId, toolName, argumentsJson, toolCallId)`；Stage 01~04 方法零改动 |

### 3. 验收结论

| 验收编号 | 结果 | 备注 |
| :--- | :--- | :--- |
| S05-AC01 | ✅ 通过 | `echo 中文测试` 经 `cmd /d /s /c` 执行无乱码；解码器单测覆盖 GBK 回退 |
| S05-AC02 | ✅ 通过 | 缺失 Python 返回标准降级提示；探测跳过 WindowsApps；本机有 Python 时内联脚本执行成功且临时脚本已清理 |
| S05-AC03 | ✅ 通过 | `ping -n 60` + `timeout_ms: 2000` 约 2s 返回超时，进程树被强杀；Job 创建断言通过（注：本机 `SetInformationJobObject` 仅接受 `ExtendedLimitInformation=9`，class 2 返回 87，已据此实现） |
| S05-AC04 | ✅ 通过 | GBK+CRLF 文件 `read` 识别编码行号，`edit` 以 LF 传参成功替换并生成 `---/+++/@@` Diff，写回保持 GBK+CRLF；唯一性校验生效 |
| S05-AC05 | ✅ 通过 | 60k+ 字符输出被截断至 ≤16000（含首 4000 + 省略提示 + 尾 8000），完整内容落盘 `tool-outputs/call_<id>.txt` |
| 系统路径写保护 | ✅ 通过 | `write/edit` 触碰三类系统目录均被拒绝并返回明确错误 |

### 4. 验证命令与结果

- `dotnet build src/Host.Desktop/Host.Desktop.csproj -c Debug`：成功（0 警告 0 错误）。
- `dotnet test tests/HaloAgent.Tests/HaloAgent.Tests.csproj -c Debug`：**43/43 全绿**（Stage05 新增 15 项 + Stage01~04 存量 28 项零回归）。
