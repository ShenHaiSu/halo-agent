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
