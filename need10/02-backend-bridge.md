# need10-02 后端与 Bridge（5 方法只读/受限写，复用 Service）

> 前置：`01` 冻结。本篇按文件顺序施工，不动存储格式。

## 1. 总原则（冻结）

- 唯一出入口仍是 `Services/SkillMemoryService.cs`（`List/Get/SetEnabled/Delete`）；Bridge 层只做**参数翻译+工作区解析+事件透传**，不写新 IO。
- **不新增** `Create/Update/WriteAsset/RebuildIndex` 的 Bridge 暴露；前端搜不到对应方法即合规。
- `SkillMemoryEntry.Kind` 已存在（`Services` 版 record 带 `Kind`），Bridge DTO 复用字段子集，前端不拼路径。

## 2. 新增文件 `Bridge/HostBridgeFacade.SkillMemory.cs`（partial，200 行内）

5 方法（CefSharp 异步铁律：全返回 `Task<BridgeResponse<T>>`，小驼峰经 `HaloJsonOptions.IndentedCamelCase`）：

```csharp
Task<BridgeResponse<List<SkillMemoryListItemDto>>> ListSkillMemory(string kind, string scope, string? workspaceId);
  // kind: skill|memory；scope: global|project|all（默认 all，前端传）
  // workspaceId 为空且 scope 含 project → Project 视为空（沿用 need09-07 E5），不报错
Task<BridgeResponse<SkillMemoryDetailDto?>> GetSkillMemoryDetail(string kind, string scope, string name, string? workspaceId);
  // name 非法 → SKILL_VALIDATION；不存在 → SKILL_NOT_FOUND；正文截断 8000→返回前 800 字 + truncated:true
Task<BridgeResponse<SkillMemoryOpResult>> SetSkillMemoryEnabled(string kind, string scope, string name, bool enabled, string? workspaceId);
Task<BridgeResponse<SkillMemoryOpResult>> DeleteSkillMemory(string kind, string scope, string name, string? workspaceId);
  // scope 必须 global|project（all 拒绝，SKILL_VALIDATION）；成功 Publish "config:skill-memory-changed"
Task<BridgeResponse<bool>> OpenSkillMemoryFolder(string kind, string scope, string name, string? workspaceId);
  // 复用既有 OpenPathInExplorer(dirPath)：查到 DirPath 后直接打开目录；查不到 → SKILL_NOT_FOUND
```

DTO（与 `SkillMemoryService.SkillMemoryEntry` 对齐的子集）：

```csharp
sealed class SkillMemoryListItemDto {
  string Name, Scope, Title, Summary, Pin; bool Enabled;
  int Priority, Version, EstTokens, Chars; string Hash; bool HasAssets;
  string UpdatedAtUtc; string DirPath;  // 前端仅用于展示/打开，不做写盘
}
sealed class SkillMemoryDetailDto : SkillMemoryListItemDto {
  string BodyPreview; bool Truncated; int BodyTotalChars;
}
sealed class SkillMemoryOpResult { bool Ok; string BakFileName?; string Notice; }
```

## 3. 实现要点（精确到调用）

1. `workspaceId → workspaceRoot`：经 `_workspaceSessionService.GetWorkspaceAsync(workspaceId)` 取 `RootPath`；`workspaceId` 空且 `scope=project` → Project 空列表/或 `SKILL_VALIDATION`（List 宽容空、写操作严格错，见 §4）。
2. `scope=all`：后端拼 `List(Global)+List(Project)`，Project 同名覆盖 Global（复用 `GetMergedForPrompt` 语义，global 侧剔除被覆盖项），排序 `name` 升序。
3. 关键字搜索**后端不做**（列表量 ≤400，前端内存过滤标题/名称/摘要；避免后端加查询参数破坏稳定签名）。
4. `SetEnabled/Delete` 成功后：`JsEventDispatcher.Publish("config:skill-memory-changed", new { kind, scope, name })`（模型侧已有订阅位，无面板则忽略，沿用 need09-07 §5）。
5. `SkillMemoryService` 构造注入：`HostBridgeFacade` 构造新增可选 `SkillMemoryService?` 参数（为空则 `new SkillMemoryService(_storageManager)`，保证老单测 `new ToolRegistry()` 式构造不崩）。

## 4. 边角表（02/04 必测）

| # | 边角 | 预期 |
|---|---|---|
| E1 | 非法名/保留名/`../` | `SKILL_VALIDATION` / `PATH_TRAVERSAL`，不写盘 |
| E2 | `scope=all` 写操作 | 拒绝（写必须明确 `global|project`） |
| E3 | 无工作区 + `scope=project` 写 | `SKILL_VALIDATION`（提示先选工作区或用 global） |
| E4 | 无工作区 + `scope=all` 读 | 只返 Global，`note=(no workspace)` |
| E5 | `_index.json` 损坏/缺 `readme.md` | Service 层跳过+Warn，Bridge 返回健康子集 |
| E6 | 删除不存在条目 | `SKILL_NOT_FOUND`，前端提示先刷新 |
| E7 | 打开文件夹时目录刚被删 | `SKILL_NOT_FOUND`，不抛未处理异常 |
