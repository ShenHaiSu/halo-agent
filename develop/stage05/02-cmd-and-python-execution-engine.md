# Stage 05 - 02：`exec_command` 核心实现（CMD/Python 优先、Job Object 进程树与编码自适应）

> **所属阶段**：Stage 05
> **核心对应需求**：需求 #3（agent 的默认命令操作推荐使用 `cmd`，然后再是 `py`，因为不同版本的 PowerShell 差距很大）与需求 #6（提供命令 `exec` 标准基础 Tool）

---

## 一、 `exec_command` 工具 JSON Schema 定义

在向 LLM 暴露 `exec_command` 的 Schema 与 `description` 中，显式锁死优先级规范，引导模型优先生成标准 `cmd` 命令，复杂逻辑使用 `python`：

```json
{
  "name": "exec_command",
  "description": "Execute a command or inline script in the workspace. IMPORTANT FOR WIN7/WIN10/WIN11 COMPATIBILITY: Always prefer 'cmd' (default Windows Command Prompt) for shell operations, file management, and build commands. Secondarily prefer 'python' ('py') for complex text processing, calculation, or scripting. Avoid PowerShell because PowerShell versions differ drastically across Windows 7 (PS 2.0) and Windows 10/11.",
  "parameters": {
    "type": "object",
    "properties": {
      "command": {
        "type": "string",
        "description": "The command line to execute (when runner is 'cmd') or the Python code/script arguments to execute (when runner is 'python')."
      },
      "runner": {
        "type": "string",
        "enum": ["cmd", "python"],
        "description": "Execution runtime. Defaults to 'cmd' (recommended primary choice). Use 'python' as the secondary choice for Python inline scripts (-c) or script execution."
      },
      "workdir": {
        "type": "string",
        "description": "Optional working directory relative to the current workspace root, or an absolute path. Defaults to the current workspace root."
      },
      "timeout_ms": {
        "type": "integer",
        "description": "Optional timeout in milliseconds. Defaults to 30000 (30s), maximum 300000 (5m)."
      }
    },
    "required": ["command"]
  }
}
```

---

## 二、 CMD 与 Python 运行时探测与启动器（`CommandRunnerProbe.cs`）

### 1. 第一优先级：`cmd` 执行模式 (`runner == "cmd"`)
* **可执行文件**：`Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe"`
* **调用参数构造**：
  * 使用 `cmd.exe /d /s /c "<command>"`
  * `/d`：禁止从注册表自动运行 `AutoRun` 命令（防止客户机器被篡改的注册表项干扰 Agent 输出或拖慢启动速度）。
  * `/s /c`：严格保留引号语义执行单条或多条串联命令（如 `dir /b && echo DONE`）。
* **环境变量注入**：
  * `PYTHONIOENCODING=utf-8`
  * `PYTHONUTF8=1`
  * `CI=true`（防止部分命令行工具进入交互式阻塞等待按键状态）

### 2. 第二优先级：`python` (`py`) 执行模式 (`runner == "python"`)
应用启动时，`PythonEnvironmentDetector` 自动按以下优先级探测本机可用的 Python 解释器并缓存结果：
1. 用户在设置中心显式指定的 Python 路径（如自备的 Win7 兼容 Python 3.8 便携版路径）。
2. 环境变量 `PATH` 中的 `python.exe`（执行 `python --version` 验证非 Windows Store 空壳别名）。
3. Windows Python Launcher `py.exe -3` 或 `py.exe`。
* **执行方式**：
  * 若 `command` 为多行代码或不包含 `.py` 文件调用，自动将代码写入会话临时脚本文件 `<SessionTempDir>/exec_<callId>.py`（UTF-8 编码），然后调用 `<pythonExe> -u "<tempScriptPath>"`（`-u` 开启无缓冲实时 stdout 输出），彻底规避命令行引号转义地狱！
  * 若探测到本机未安装 Python，当 LLM 调用 `runner: "python"` 时，立即返回清晰提示：`"Error: Python runtime is not detected on this machine. Please fall back to 'cmd' runner or ask the user to configure Python path in Settings."`

---

## 三、 Win7 中文乱码克星：GBK (CP936) / UTF-8 智能字节流解码器

### 1. 痛点分析
在中文版 Windows 7 / 10 / 11 中：
* 系统原生命令（如 `dir`、`ipconfig`、`ping`、`netstat`、`tasklist`、`findstr`）默认按活动代码页 **CP936 (GBK)** 输出字节流；
* 而现代开发工具（如 `node`、`dotnet`、`git`、设置了 `PYTHONUTF8=1` 的 `python`）则输出 **UTF-8** 字节流；
* 如果简单强制执行 `chcp 65001`，在 Win7 下会导致部分老旧批处理或原生程序直接崩溃或丢字；如果将 `ProcessStartInfo.StandardOutputEncoding` 死锁为 `UTF8`，则 `dir` 和报错信息中的中文路径全部变成 `` 乱码！

### 2. 解决方案：按行原始字节读取 + 严格 UTF-8 校验回退 GBK
在 `.NET 6` 中注册 `CodePagesEncodingProvider.Instance`（支持 `Encoding.GetEncoding(936)`），直接从 `process.StandardOutput.BaseStream` 和 `StandardError.BaseStream` 读取原始 `byte[]` 缓冲：

```csharp
public static class SmartConsoleDecoder
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding GbkEncoding;

    static SmartConsoleDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        GbkEncoding = Encoding.GetEncoding(936); //简体中文 GBK / GB18030 兼容
    }

    public static string DecodeBytes(ReadOnlySpan<byte> rawBytes)
    {
        if (rawBytes.IsEmpty) return string.Empty;
        try
        {
            // 1. 优先使用开启了 throwOnInvalidBytes: true 的严格 UTF-8 校验解码
            return StrictUtf8.GetString(rawBytes);
        }
        catch (DecoderFallbackException)
        {
            // 2. 一旦发现非合法 UTF-8 字节序列（如 Win7 CMD 原生命令输出的 GBK 中文字符），立即无损回退至 CP936 (GBK)
            return GbkEncoding.GetString(rawBytes);
        }
    }
}
```
该方案无需修改系统代码页，即可在同一条命令管道中同时完美兼容 **Win7 原生 GBK 中文输出** 与 **现代工具 UTF-8 输出**！

---

## 四、 Win32 Job Object 进程树安全回收（防僵尸进程）

当 `cmd.exe /c "python long_task.py"` 超时或被用户取消时，如果只调用 `process.Kill()`，在 Win7 上往往只杀死了外层壳 `cmd.exe`，而内部的 `python.exe` 会脱离管控变成后台孤儿进程，持续占用 CPU 和文件锁。

因此通过 P/Invoke 封装 Windows 原生内核对象 **Job Object**（自 Windows XP/7 起原生支持）：
1. 创建内核作业对象：`CreateJobObject(IntPtr.Zero, null)`。
2. 设置扩展限制标志：`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (0x2000)`。
3. 在 `process.Start()` 后立即调用 `AssignProcessToJobObject(jobHandle, process.Handle)`。
4. 当超时、取消或正常结束关闭 `jobHandle` 时，Windows 内核保证**原子级终止该作业下的所有子进程与孙进程**，零残留！
