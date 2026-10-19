# Stage 01 - 03：C盘固定默认存储与动态迁移引擎 + 分级日志系统

> **所属阶段**：Stage 01
> **核心对应需求**：需求 #5 —— “将历史以及日志写入到 C 盘的固定位置，不过允许用户手动修改路径”

---

## 一、 两级引导存储架构设计（Two-Tier Storage Bootstrap）

### 1. 为什么需要两级引导？
如果用户将存储目录从默认的 `C:\HaloAgentData` 修改到了 `D:\MyAgentData`，程序下次冷启动时必须知道去哪里加载配置文件与会话历史。因此系统必须有一个**永远固定在 C 盘用户目录下的极小引导指针文件（Bootstrap File）**，由它指向真正的**数据根目录（Data Root Directory）**。

### 2. 路径规划与权限降级兜底
1. **固定引导指针文件（不可变位置）**：
   * 路径：`C:\Users\<Username>\AppData\Local\HaloAgent\bootstrap.json`（通过 `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)` 获取，在 Win7/Win10/Win11 下 100% 位于系统盘且当前用户拥有完全读写权限）。
2. **默认数据根目录（C盘固定位置）**：
   * 首选固定位置：`C:\HaloAgentData`
   * **Win7 / 企业域控受限账户降级策略**：启动初始化时，若尝试创建或写入 `C:\HaloAgentData\.write_test` 抛出 `UnauthorizedAccessException`（例如非管理员账户禁止在 C 盘根目录建文件夹），自动平滑回退至 `C:\Users\<Username>\AppData\Local\HaloAgent\Data`，并在日志与设置面板中提示当前生效路径。

### 3. `bootstrap.json` 结构定义
```json
{
  "schemaVersion": 1,
  "dataRootPath": "C:\\HaloAgentData",
  "previousDataRootPath": null,
  "lastModifiedUtc": "2026-03-30T08:00:00Z"
}
```

### 4. 数据根目录 (`DataRootPath`) 内部标准子目录树
无论 `DataRootPath` 位于默认 `C:\HaloAgentData` 还是用户手动修改后的任意磁盘目录，其内部结构严格保持一致：

```text
<DataRootPath>/                           # 默认 C:\HaloAgentData (支持用户手动修改)
├── config/
│   ├── app-settings.json                 # 全局设置（主题、默认Shell偏好、代理配置、日志保留天数）
│   └── providers.json                    # 多 Provider 与 Model 配置（API Key 经 DPAPI 加密）
├── workspaces/
│   └── workspaces-index.json             # 多工作路径（项目文件夹）注册表
├── sessions/
│   └── <workspace-id>/                   # 按工作区隔离会话存储
│       ├── sessions-index.json           # 当前工作区下的会话元数据列表（快速渲染侧边栏）
│       └── <session-id>/
│           ├── meta.json                 # 会话详情（含不可变 sessionHash、绑定模型、Token 累计）
│           ├── messages.jsonl            # Append-Only 消息历史记录（每行一条 JSON，防断电损坏）
│           └── tool-outputs/             # 单次超长命令输出或大文件读取快照
├── logs/
│   ├── app-20260330.log                  # 宿主生命周期、异常、配置变更日志
│   ├── llm-trace-20260330.log            # LLM 请求/响应摘要、耗时、首字延迟(TTFT)、Token 审计
│   ├── tool-exec-20260330.log            # CMD/Python 命令执行、文件读写审计日志
│   └── cef-native.log                    # Chromium 底层引擎日志
└── cef-cache/                            # CefSharp LocalStorage / IndexedDB 缓存目录
```

---

## 二、 用户手动修改路径与安全迁移方案（`StoragePathManager`）

当用户在设置中心点击“更改存储路径”并选择新文件夹（例如 `E:\HaloAgentStore`）时，后端提供以下三步安全事务流程：

### 1. 目标路径合法性与可写性预检（`ValidateTargetRootPathAsync`）
* 校验路径格式合法，非系统受保护目录（如禁止直指 `C:\Windows` 或 `C:\Program Files`）。
* 检查目标磁盘剩余可用空间（`DriveInfo.AvailableFreeSpace >= 200MB`）。
* 写入临时探测文件 `.halo_write_probe` 并删除，确认当前进程拥有完整读写与删除权限。

### 2. 数据迁移模式选择（支持两种模式）
* **模式 A：复制并切换（推荐默认，`migrateExistingData = true`）**
  1. 将 `config/`、`workspaces/`、`sessions/`、`logs/` 下的所有文件（排除正在被 CEF 进程独占锁定的 `cef-cache/`）异步拷贝至新目录，并向前端推送拷贝进度。
  2. 原子更新 `%LocalAppData%\HaloAgent\bootstrap.json` 中的 `dataRootPath`。
  3. 切换内存中的 `StoragePathManager.CurrentDataRoot` 与 `HaloLogger` 输出文件流（无需重启即可立即在新路径生效；`cef-cache` 在下次启动时自动挂载到新路径）。
* **模式 B：仅切换路径（全新工作空间，`migrateExistingData = false`）**
  1. 在新目录初始化空白的 `config/`、`workspaces/`、`sessions/`、`logs/` 目录结构。
  2. 更新 `bootstrap.json` 并热重载内存配置。

---

## 三、 原子文件写入与分级滚动日志实现

### 1. 配置文件防损坏原子写入（`AtomicFileWriter`）
Win7 老旧机器偶发断电或强制杀进程，若直接 `File.WriteAllText("providers.json")` 易导致配置文件变为 0 字节损坏文件。所有 JSON 配置文件写入必须遵循**“写临时文件 -> Flush 磁盘 -> 原子替换”**：

```csharp
public static async Task WriteJsonAtomicAsync<T>(string filePath, T data, CancellationToken ct = default)
{
    var dir = Path.GetDirectoryName(filePath)!;
    Directory.CreateDirectory(dir);
    var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";

    var json = JsonSerializer.Serialize(data, HaloJsonOptions.IndentedCamelCase);
    await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
    await using (var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
    {
        await sw.WriteAsync(json.AsMemory(), ct);
        await sw.FlushAsync();
        fs.Flush(flushToDisk: true);
    }

    File.Move(tempPath, filePath, overwrite: true);
}
```

### 2. 高吞吐无锁分级日志引擎（`HaloLogger`）
* **三通道分离**：
  * `HaloLogger.App` -> `logs/app-YYYYMMDD.log`
  * `HaloLogger.Llm` -> `logs/llm-trace-YYYYMMDD.log`
  * `HaloLogger.Tool` -> `logs/tool-exec-YYYYMMDD.log`
* **异步非阻塞写入**：
  * 采用 `System.Threading.Channels.Channel<LogEntry>` 无锁单消费者队列，调用方（无论是 UI 线程、CEF 桥接线程还是 Agent 后台线程）写日志耗时 `< 1μs`，由后台专属线程批量 `Flush` 到磁盘。
* **敏感信息脱敏**：
  * 写入 `llm-trace` 日志前，自动对 `Authorization: Bearer sk-...` 或自定义 Secret Header 执行掩码处理（如保留前 4 位与后 4 位 `sk-8f***9a2b`）。
* **自动过期清理**：
  * 启动时扫描 `logs/` 目录，自动删除超过 `LogRetentionDays`（默认 14 天，用户可配）的历史日志文件。
