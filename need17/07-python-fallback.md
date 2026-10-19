# need17-07 · Python 缺失兜底专项：启动快照 + 后端秒拒 + 指定文案

> 前置：无（可与 01/02 并行；Schema 注记消费 01 的动态描述点，文案码复用 02 码表）。
> 对应 pi `toolDocs/05 §5.2`（外壳差异/环境感知）+ 本地需求：无 Python 的电脑上，LLM 调 `runner:"python"` 必须在后端直接拒绝，并给出中文安装引导。
> 冻结原则：探测逻辑（`CommandRunnerProbe.ProbePython` 优先级：设置路径 > PATH > `py -3`）不动；本专项只加**快照 + 秒拒 + 文案 + 教学**。

---

## 7.1 指定文案（冻结，逐字，含标点）

后端拒绝 `Error` 全文模板（`FailCode("PYTHON_NOT_AVAILABLE", ...)`，首 token 即码）：

```text
PYTHON_NOT_AVAILABLE：当前电脑环境中没有检测到或者没有安装python，如果必须py，请引导用户前往官网进行下载和安装配置后重启HaloAgent。
```

* 后续拼接（同一 `Error` 内换行追加，不另起码）：`已收到 runner:'python'，本次未执行。请改用 runner:'cmd' 重试（如 {"command":"dir /b","runner":"cmd"}）；或请用户安装 Python 后重试。`
* `HintForTool("exec_command", "PYTHON_NOT_AVAILABLE")` 返回中文：` Hint: 本机无 Python，请用 runner:'cmd' 继续；需要 Python 请按上方指引安装后重启 HaloAgent。`（全仓库唯一中文 Hint，单测逐字断言，见 7.5。）
* 英文存量 `PythonMissingMessage`（`ExecCommandTool.cs:17-19`）保留为 fallback 常量，不再作为主文案（主文案走上方冻结中文）。

## 7.2 启动探测快照（不阻塞启动）

新增 `Tools/Process/PythonAvailabilitySnapshot.cs`（进程级单例，线程安全）：

```csharp
public sealed class PythonAvailabilitySnapshot
{
    public bool Found { get; init; }
    public string? ExePath { get; init; }
    public string? DisplayName { get; init; }
    public DateTimeOffset ProbedAtUtc { get; init; }
    public static PythonAvailabilitySnapshot Current { get; private set; } = Unknown;
    public static void Refresh();                       // 同步探测一次并替换 Current
    public static void OverrideForTests(PythonAvailabilitySnapshot s); // 单测注入
    public static void ClearOverrideForTests();
}
```

* 调用点：`Program.Main`（`src/Host.Desktop/Program.cs:30` 附近）启动链最早处 `Task.Run(() => PythonAvailabilitySnapshot.Refresh())`（后台探测，不阻塞 UI/网关启动；失败吞异常记日志，`Current` 保持 `Found:false`）。
* `Refresh()` 内部复用 `CommandRunnerProbe.ProbePython()`（进程缓存自动复用，无双重开销）+ `SystemPromptBuilder.GetPythonStatus()` 的展示逻辑收敛到快照（`GetPythonStatus` 改为读快照，60s 缓存逻辑删除，行为对外不变）。
* 设置中心改了 Python 路径后：保存处调 `CommandRunnerProbe.InvalidateCache()` + `PythonAvailabilitySnapshot.Refresh()`（同步，前端按钮转一圈即可）。

### 验收

* `Snapshot_Refresh_DoesNotThrow_WithoutPython`：清空 PATH 模拟下 `Refresh()` 不抛，`Current.Found==false`。
* `OverrideForTests` 注入后 `GetPythonStatus()` 读到注入值（旧缓存单测同步更新）。

## 7.3 执行秒拒（`ExecCommandTool` 开头，双保险）

改动 `ExecCommandTool.ExecuteAsync`（`ExecCommandTool.cs:38-96` 参数解析之后、进程启动之前，现 `probe` 处 `128-135` 之前插入）：

```
解析 runner（02 Validator 已保证 enum 合法）
→ 若 runner == "python"：
    ① 查 PythonAvailabilitySnapshot.Current.Found
       → false：直接 return FailCode("PYTHON_NOT_AVAILABLE", §7.1文案)，不 Probe、不建进程、不落临时脚本
    ② 快照 true 但本次 ProbePython 仍 Missing（用户装后又卸载）：
       → 同样 FailCode(§7.1文案)（双保险，文案一致）
→ runner == "cmd"：零变化
```

* 秒拒不计时、不进 `JobObject`、不写 `SessionTempDir`（单测断言无子进程/无临时文件）。
* `ToolContext` 新增可选 `PythonAvailabilityOverride`（单测/子代理注入；生产为空→读静态快照）。

### 验收

* `PythonMissing_FastReject_NoProcess`：快照 false + `runner:python` → `ErrorCode==PYTHON_NOT_AVAILABLE`，`Error` 含 §7.1 整句，`ExitCode==-1`，spy 进程工厂零调用。
* `PythonMissing_ProbeRace_StillRejects`：快照 true + `PythonProbeOverride=Missing` → 同样文案（双保险）。
* `PythonPresent_BehaviorUnchanged`：快照 true + 真实/假 probe Found → 走老内联脚本路径（老单测全绿）。

## 7.4 Schema + 提示词动态注记（事前教学，让模型少调）

1. **Schema 注记**（`ExecCommandTool.GetDefinition`）：当快照 `Found==false` 时，`runner` 描述追加 ` (注意：当前环境未检测到 Python，runner:'python' 不可用，请使用 'cmd')`；`Description` 尾追加同义一句。实现：静态读快照（无 DI），有单测覆盖两种快照。有 Python 时文案与 04 §4.1 逐字一致（无注记）。
2. **提示词注记**（`PromptModels.CmdPythonText` + `SystemPromptBuilder.BuildCore`）：`cmdPython` 块渲染后，若快照 false 则追加一行（冻结英文，保持 prefix-cache 稳定）：`NOTE: Python was NOT detected on this machine. Do NOT use runner:'python'; use 'cmd'. If the user needs Python, guide them to install it from python.org and restart HaloAgent.` —— 中文引导由工具错误承担，提示词保持英文纪律。
3. **设置引导**：设置中心 Python 路径配置项旁：快照 false 时显示“未检测到 Python”红点 + 文案 `未检测到 Python（python runner 不可用）。请前往 python.org 下载安装，配置路径后重启 HaloAgent 生效。` + “重新检测”按钮（调 `Refresh`）。前端实现细节由前端文档跟进，后端提供 `PythonAvailabilitySnapshot.Current` 查询接口（Bridge 加只读方法，不在本专项强制）。

### 验收

* `Schema_Warns_When_NoPython`：快照 false 时 Schema 含 `runner:'python' 不可用`；快照 true 时无该串。
* `Prompt_Warns_When_NoPython`：`BuildPreview` 产物在 false 时含 `Python was NOT detected`。

## 7.5 测试清单（`Stage17PythonFallbackTests.cs`，≥8 用例）

* 文案逐字：`Error.Contains("当前电脑环境中没有检测到或者没有安装python，如果必须py，请引导用户前往官网进行下载和安装配置后重启HaloAgent")`（复制粘贴断言，含 `py` 小写与 `HaloAgent` 大小写）。
* `ErrorCode==PYTHON_NOT_AVAILABLE` 且 `Error.StartsWith("PYTHON_NOT_AVAILABLE")`（need15 码约定）。
* 秒拒无副作用：无进程、无临时 `.py` 文件、无 `SessionTempDir` 创建（mock 文件系统或断言目录无新增）。
* 快照注入隔离：每个用例 `OverrideForTests` + `try/finally ClearOverrideForTests`，CI 有/无 Python 均绿。
* 与 02 联动：`runner:"powershell"` 仍走 `ENUM_VIOLATION`（不是 PYTHON 码）；`runner` 缺失默认 `cmd` 不受快照影响。
