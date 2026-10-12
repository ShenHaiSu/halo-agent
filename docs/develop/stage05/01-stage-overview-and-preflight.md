# Stage 05 - 01：Win7 兼容工具执行引擎（CMD/Python 优先）与标准基础 Tools（前置检查与当期目标）

> **所属阶段**：Stage 05（标准基础工具箱与跨代命令执行引擎）
> **依赖前置**：Stage 01 ~ Stage 04 已完成并通过验收
> **直接覆盖核心需求**：需求 #3（默认命令操作推荐使用 `cmd`，其次是 `py`，规避不同系统 PowerShell 版本差异）、需求 #6（提供文本 `read`/`write`/`edit`、命令 `exec` 等标准基础 Tool 供 LLM 调用）

---

## 一、 前置检查（Preflight Checklist）

1. **为什么在面向 Win7 的 Agent 中必须“首选 `cmd`，次选 `py`，降级 PowerShell”？**
   * **Windows 7 SP1 原生环境现实**：Win7 默认仅预装 **PowerShell 2.0**（基于 .NET 2.0 CLR），不仅没有 `Invoke-RestMethod`、`ConvertFrom-Json`、`Get-FileHash` 等常用 Cmdlet，且其标准输出管道在处理 UTF-8 与高并发重定向时极易死锁或产生不可逆乱码；而 Win10/11 则是 PowerShell 5.1 或 pwsh 7。
   * **`cmd.exe` 的跨代一致性**：从 Windows XP/7 到 Windows 10/11，`cmd.exe` 的内置命令（`dir`、`type`、`findstr`、`copy`、`move`、`del`、`mkdir`、`set`、`where`、`tasklist`）语法与行为接近 **100% 跨代一致**，且启动耗时仅 `< 10ms`（PowerShell 2.0 冷启动往往需要 `800ms ~ 2500ms`）。
   * **`python` / `py` 的脚本能力互补**：当遇到需要复杂文本替换、正则计算、数据处理或 JSON/XML 解析的场景，使用 `python -c "..."` 或执行临时 `.py` 脚本远比写复杂的批处理或老版本 PowerShell 更可靠、更跨平台。

2. **前置基础设施确认**：
   * [x] `WorkspaceInfo.RootPath` 已提供当前会话绑定的项目工作目录作为所有工具的基准路径（Base Working Directory）。
   * [x] `HaloLogger.Tool` 日志通道已就绪，用于记录所有工具调用的参数、耗时与退出码。

---

## 二、 当期阶段目标（Stage 05 Objectives）

1. **建立统一的 `IToolHandler` 与 `ToolRegistry` 注册调度中心**：
   * 自动生成同时兼容 OpenAI `Chat Completions` (`chat`) 与 `Responses API` (`resp`) 的 JSON Schema。
   * 支持并发安全调用、超时强制中止、异常捕获转自然语言诊断（让 LLM 可根据报错信息自我修正重试，而不是让宿主崩溃）。
2. **打造工业级 `exec_command` 执行引擎（CMD 第一优先、Python 第二优先）**：
   * 支持 `runner` 参数：默认 `"cmd"`（通过 `cmd.exe /d /s /c` 执行），次选 `"python"`（自动探测本机 `python`/`py` 执行内联脚本或脚本文件）。
   * 引入 **Win32 Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`)** 进程树管控，确保超时或用户点击“停止”时，连同 `cmd.exe` 启动的所有子孙进程一并干净杀灭。
   * 实现 **GBK (CP936) 与 UTF-8 智能双编码探测解码器**，彻底根治 Win7 中文环境下命令行输出乱码。
3. **实现标准文件与检索基础工具集（Standard Foundation Tools）**：
   * `read_file`：支持带行号分页读取（`offset`、`limit`），自动识别 UTF-8 / UTF-8 BOM / GB18030(GBK) / UTF-16 编码。
   * `write_file`：支持新建或全量覆写文件，自动创建父目录、保持原文件编码与换行符风格，并返回变更统计。
   * `edit_file`：支持精确文本块替换（`old_string` -> `new_string`，含 CRLF/LF 自动容错匹配与 `replace_all`），自动生成标准 Unified Diff 供前端渲染对比。
   * `list_dir`：快速列出目录结构（支持递归深度控制与忽略 `.git`/`node_modules`/`bin`/`obj` 噪音目录）。
   * `grep_search`：纯 C# 实现的高速正则文件内容检索工具（无需外部依赖 64 位 `rg.exe`，在 32/64 位 Win7 上 100% 原生可用）。

---

## 三、 标准基础工具集一览表

| 工具名称 (`name`) | 分类 | 核心用途说明 | 默认安全策略 |
| :--- | :--- | :--- | :--- |
| **`read_file`** | 文件读取 | 按行号范围读取文本文件内容（支持大文件分页与 GBK/UTF-8 自动解码） | 工作区内免确认读取 |
| **`write_file`** | 文件写入 | 创建新文件或完整替换文件内容（原子写入，防半截损坏） | 校验工作区路径边界，返回新建/覆盖行数统计 |
| **`edit_file`** | 文件修改 | 用 `new_string` 精确替换文件中的 `old_string`（自动兼容 CRLF/LF 差异） | 唯一性校验，生成 Unified Diff 供 UI 展示 |
| **`exec_command`** | 命令执行 | **默认使用 `cmd`**，次选 **`python`** 执行命令或脚本，实时回传 stdout/stderr | 绑定 Win32 Job Object，超时自动强杀进程树 |
| **`list_dir`** | 目录探查 | 查看工作区或指定子目录的文件与文件夹树（含文件大小与修改时间） | 自动过滤构建产物与巨型依赖目录 |
| **`grep_search`** | 内容检索 | 在工作区文件中按正则表达式搜索代码或文本，返回文件名、行号与匹配行 | 纯 C# 异步并行扫描，限制最大返回条数防爆上下文 |

---

## 四、 本阶段文档导航

* 下一篇：[`02-cmd-and-python-execution-engine.md`](./02-cmd-and-python-execution-engine.md) —— 详细设计 `exec_command`（CMD/Python 优先策略、Win32 Job Object 进程树回收、GBK/UTF-8 编码自适应）。
* 第三篇：[`03-file-and-search-standard-tools.md`](./03-file-and-search-standard-tools.md) —— 详细设计 `read_file`、`write_file`、`edit_file`、`list_dir`、`grep_search` 的 Schema 与实现细节。
* 第四篇：[`04-tool-safety-and-acceptance.md`](./04-tool-safety-and-acceptance.md) —— 详细设计路径沙箱、大输出转存保护与 Stage 05 验收标准。
