# Stage 04 - 02：多工作路径（Workspace）与多会话（Session）持久化实现方案

> **所属阶段**：Stage 04
> **核心对应需求**：需求 #4（支持多个工作路径，每个项目下多个会话）、需求 #5（写入 C 盘固定/可改位置）、需求 #8（`${session-hash}` 会话哈希值）

---

## 一、 领域模型定义（`WorkspaceModels.cs`）

### 1. 工作区模型（`WorkspaceInfo`）
```csharp
namespace Host.Desktop.Core.Models;

public sealed class WorkspaceInfo
{
    /// <summary>
    /// 工作区唯一 ID (如 "ws_9a8b7c6d")
    /// </summary>
    public string Id { get; init; } = $"ws_{Guid.NewGuid():N}"[..11];

    /// <summary>
    /// 显示名称（默认取文件夹名，允许用户自定义别名）
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 规范化后的本地绝对路径（如 "F:\MyProgram\CSharp\halo-agent"）
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// 路径确定性哈希（用于 ${workspace-hash} 预设变量）
    /// </summary>
    public string WorkspaceHash { get; set; } = string.Empty;

    /// <summary>
    /// 该项目上次选用的 ProviderId 与 ModelId
    /// </summary>
    public string? DefaultProviderId { get; set; }
    public string? DefaultModelId { get; set; }

    public bool IsPinned { get; set; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime LastOpenedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 运行时动态计算属性：当前磁盘上该文件夹是否存在
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DirectoryExists => Directory.Exists(RootPath);
}
```

### 2. 会话元数据模型（`SessionMetadata`）
```csharp
public sealed class SessionMetadata
{
    /// <summary>
    /// 会话唯一 ID (如 "sess_20260330_a1b2c3d4")
    /// </summary>
    public string Id { get; init; } = $"sess_{DateTime.UtcNow:yyyyMMdd}_{Guid.NewGuid():N}"[..22];

    /// <summary>
    /// 所属工作区 ID
    /// </summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>
    /// 会话确定性 16 位哈希值（创建时由 SHA256(WorkspaceId + ":" + Id)[..16] 生成并永久落盘，供 ${session-hash} 使用）
    /// </summary>
    public string SessionHash { get; init; } = string.Empty;

    /// <summary>
    /// 会话标题（首轮用户提问后自动提取前 24 个字符生成，支持手动重命名）
    /// </summary>
    public string Title { get; set; } = "新建会话";

    public string ProviderId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;

    public int MessageCount { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalCachedTokens { get; set; }

    public bool IsPinned { get; set; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
```

---

## 二、 磁盘存储结构与并发安全 JSONL 引擎（`SessionRepository.cs`）

在 `<DataRootPath>`（默认 `C:\HaloAgentData`）下，多工作区与多会话采用如下高性能文件布局：

```text
C:\HaloAgentData\
├── workspaces\
│   └── workspaces-index.json                 # 所有已导入工作路径的数组列表
└── sessions\
    ├── ws_9a8b7c6d\                          # 工作区 A 的专属会话目录
    │   ├── sessions-index.json               # 该工作区下所有会话的 SessionMetadata 摘要列表
    │   ├── sess_20260330_a1b2c3d4\           # 会话 1
    │   │   ├── meta.json                     # 会话完整元数据
    │   │   ├── messages.jsonl                # 每行一条 UnifiedChatMessage JSON
    │   │   └── tool-outputs\                 # 超长命令行输出/文件读取独立快照文件
    │   └── sess_20260330_e5f6g7h8\           # 会话 2
    └── ws_11223344\                          # 工作区 B 的专属会话目录
```

### 1. 为什么消息历史采用 `.jsonl`（JSON Lines）而非单个大 `.json` 数组？
* **抗断电与防崩溃**：Agent 在多步 ReAct 循环中每产生一条 User 消息、Assistant 回复或 Tool 执行结果，只需向 `messages.jsonl` 末尾 **追加一行（Append One Line + Flush）**，无需每次重写整个数 MB 的历史文件。
* **内存友好（尤其针对 32位/4GB 内存的老旧 Win7）**：读取历史或追加消息时，时间复杂度为 $O(1)$，不会产生大对象堆（LOH）内存碎片。

### 2. `AppendMessageAsync` 线程安全实现
每个 `SessionId` 维护一把细粒度 `SemaphoreSlim(1, 1)` 异步锁，确保同一会话的写入严格串行，而不同会话或不同工作区的后台任务可完全并行读写：

```csharp
public async Task AppendMessageAsync(string workspaceId, string sessionId, UnifiedChatMessage message, CancellationToken ct = default)
{
    var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
    await sessionLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        string jsonlPath = GetMessagesJsonlPath(workspaceId, sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(jsonlPath)!);

        // 单行无换行符紧凑序列化
        string line = JsonSerializer.Serialize(message, HaloJsonOptions.CompactCamelCase) + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(line);

        await using var fs = new FileStream(jsonlPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        await fs.WriteAsync(bytes, ct).ConfigureAwait(false);
        await fs.FlushAsync(ct).ConfigureAwait(false);
    }
    finally
    {
        sessionLock.Release();
    }
}
```

---

## 三、 工作路径标准化与重防重机制

当用户通过“添加项目文件夹”选择一个本地路径时：
1. 使用 `Path.GetFullPath(inputPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)` 进行规范化（但保留盘符根目录如 `C:\` 的斜杠）。
2. 在 Windows 下按不区分大小写（`StringComparer.OrdinalIgnoreCase`）检查 `workspaces-index.json` 是否已存在相同路径：
   * 若已存在，直接激活并切换到该工作区，避免重复创建。
   * 若不存在，创建新的 `WorkspaceInfo`，并自动在其下初始化第一个默认会话（生成固定的 `SessionHash`）。
