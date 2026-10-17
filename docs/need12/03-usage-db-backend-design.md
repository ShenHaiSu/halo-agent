# 03 用量库后端设计（SQLite + 轮末批量写）

## 3.1 设计一句话

新增 `UsageMetricsService`（单例、单写者队列）管理 `{DataRoot}/metrics/usage.db`；`AgentOrchestrator` 在轮内收集 `List<PendingUsageRow>`（每次 HTTP 请求的终值 usage + 快照的 provider/model），**轮次结束（Completed/Truncated/Error/有 usage 的 Cancelled）一次 `EnqueueFlush(rows)`**，后台线程单事务 `INSERT`；查询侧全部走参数化 SQL 聚合，前端拿不到裸全表。

## 3.2 依赖与版本（红线）

- 新增包：`Microsoft.Data.Sqlite` **`6.0.28`（锁 6.0.x，随 net6；禁止 8/9）**。理由：纯托管 + `SQLitePCLRaw.bundle_e_sqlite3` 自带 native，Win7 x64 免部署、与 `ProtectedData 6.0.0` 同代；`System.Data.SQLite` 需额外 interop DLL 且与 CefSharp 混合装载冲突，否决；不引 Dapper/EF（单表 + 手写 SQL 更快、更少依赖）。
- `Host.Desktop.csproj` 仅加一行 `PackageReference`；构建后 `e_sqlite3` 由 NuGet 自动平铺，无需 `Content` 手配。
- PRAGMA：`journal_mode=WAL; synchronous=NORMAL; busy_timeout=5000; foreign_keys=OFF`（单表无需外键）。WAL 在 Win7 NTFS 可用；`-wal/-shm` 随 DB 同目录（`metrics/`），迁移/备份时三文件一起拷（见 3.6）。

## 3.3 表结构（v1，只一表，不超前设计）

```sql
CREATE TABLE IF NOT EXISTS llm_usage_records (
  id                INTEGER PRIMARY KEY AUTOINCREMENT,
  provider_id       TEXT NOT NULL,            -- 联合筛选键（稳定 ID）
  provider_name     TEXT NOT NULL,            -- 需求：提供商名称（当时快照）
  model_id          TEXT NOT NULL,            -- 需求：模型名称（实际请求 ID，如 gpt-4o）
  model_display     TEXT NOT NULL DEFAULT '', -- 展示名（若配置有别名）
  protocol          TEXT NOT NULL DEFAULT '', -- chat | resp
  input_tokens      INTEGER NOT NULL DEFAULT 0,
  output_tokens     INTEGER NOT NULL DEFAULT 0,
  cached_tokens     INTEGER NOT NULL DEFAULT 0,
  hit_rate          REAL NOT NULL DEFAULT 0,  -- cached/input，input=0 则 0，写库时算好
  status            TEXT NOT NULL DEFAULT 'ok', -- ok | error | cancelled | degraded(no-usage)
  request_id        TEXT NOT NULL DEFAULT '', -- 去重/追溯键（UNIQUE）
  session_id        TEXT NOT NULL DEFAULT '',
  workspace_id      TEXT NOT NULL DEFAULT '',
  request_date      TEXT NOT NULL,            -- 本地 yyyy-MM-dd（需求列）
  request_time      TEXT NOT NULL,            -- 本地 HH:mm:ss（需求列）
  request_datetime  TEXT NOT NULL,            -- 本地 yyyy-MM-dd HH:mm:ss（需求列）
  created_utc       TEXT NOT NULL             -- ISO8601 UTC 审计列
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_llm_usage_request ON llm_usage_records(request_id);
CREATE INDEX IF NOT EXISTS ix_llm_usage_dt ON llm_usage_records(request_datetime);
CREATE INDEX IF NOT EXISTS ix_llm_usage_date ON llm_usage_records(request_date);
CREATE INDEX IF NOT EXISTS ix_llm_usage_pm ON llm_usage_records(provider_id, model_id);
```

- 为什么三列日期冗余：需求明示“日期、时间、日期+时间”三列都要，且筛选预设（今日/本周/本月）直接对 `request_date/request_datetime` 做字符串区间比较（`yyyy-MM-dd` 字典序 = 时间序），无需 SQLite 日期函数，Win7 旧版 SQLite 全兼容。
- `hit_rate` 写库时算好（`input>0 ? (double)cached/input : 0`，钳制 0–1），查询综合命中率用 `SUM(cached)/SUM(input)` 重算，不依赖行均值。
- 同名模型隔离：筛选键是 `(provider_id, model_id)` 二元组，`model_id` 相同但 `provider_id` 不同 → 不同系列（堆叠图 `series key = provider_name + "/" + model_id`）。

## 3.4 写入路径（低频核心）

```
LLM HTTP 请求完成（UsageUpdated 终值）
  → Orchestrator 轮内 List<PendingUsageRow>.Add(...)   // 纯内存，零 IO
  → 轮次结束 Completed/Truncated/Error/Cancelled(有usage才入队)
  → UsageMetricsService.EnqueueFlush(rows)              // 入 ConcurrentQueue，立即返回
  → 后台单写者线程：取一批 → BEGIN → N 条 INSERT OR IGNORE → COMMIT
```

**`AgentOrchestrator` 改动（约 200-260 行轮循环 + 540-570 TurnCompleted）：**

1. 轮开始 `var usageRows = new List<PendingUsageRow>();` + 轮内快照 `var (provider, model) = ResolveProviderModel(...)`（约 716 行已有方法复用）。
2. 每次 `StreamCompleted` 后（`usage != null` 处，约 460-530 行）：
   ```csharp
   usageRows.Add(new PendingUsageRow {
     ProviderId = provider.Id, ProviderName = provider.Name,
     ModelId = model.Id, ModelDisplay = model.DisplayName ?? model.Id,
     Protocol = model.Protocol.ToString().ToLowerInvariant(),
     Input = usage.InputTokens, Output = usage.OutputTokens, Cached = usage.CachedInputTokens,
     Status = "ok", RequestId = requestId, SessionId = sessionId, WorkspaceId = workspaceId,
   });
   // 失败/取消分支：若 usage 全 0 仍记一行 status=error/cancelled（保留“我调过”证据），token 记 0
   ```
   时间三列**不在此处生成**，在 `EnqueueFlush` 时由 service 按 `DateTime.Now` 统一生成（保证同轮多行时间一致、测试可注入时钟）。
3. 轮结束统一（`TurnCompleted/TurnTruncated/TurnError` + 有行才 `Cancelled`）：
   ```csharp
   try { _usageMetrics?.EnqueueFlush(usageRows, sessionId, workspaceId); } catch { /* 永不抛 */ }
   ```
   `usageRows.Count == 0`（如鉴权失败前就崩）则不入队，零空事务。

**`UsageMetricsService`（新文件 `Services/UsageMetricsService.cs`）要点：**

- 构造注入 `StoragePathManager`，`MetricsDirectory = {DataRoot}/metrics`，`DbPath = metrics/usage.db`；`Initialize()` 建目录 + `CREATE TABLE/INDEX` + PRAGMA（幂等）。
- 写队列：`ConcurrentQueue<FlushBatch>` + `AutoResetEvent` + 常驻后台 `Task`（单写者，SQLite 写串行化天然适配）；`EnqueueFlush` 只入队不等待；后台 `INSERT OR IGNORE`（`request_id` 唯一防重放/分支重试双记）单事务提交；失败记 `HaloLogger.App.Warn` + 内存保留最近 1 批（防无限堆积），**绝不回抛给 Orchestrator**。
- 数据根切换：监听 `StoragePathManager` 变更（或 Bridge 迁移成功后调用 `Reinitialize()`），旧 DB 随迁移工具一起拷（`metrics/` 整体复制），新路径延迟初始化。
- 并发：CefSharp 单进程单实例写为主；多开实例极端情况靠 `busy_timeout` + `OR IGNORE` 兜底，不做跨进程锁。

## 3.5 查询路径（全部服务端聚合，前端不算数）

| Bridge 方法（详见 05） | SQL 本质 | 返回 |
|---|---|---|
| `UsageGetFilterOptions` | `SELECT DISTINCT provider_id,provider_name / model_id,model_display,provider_id` | 下拉选项（含 `provider/name → models` 联动） |
| `UsageGetOverview` | `COUNT, SUM(in/out/cached), SUM(cached)/SUM(in)` + `COUNT(DISTINCT model)` | 6 瓦片 |
| `UsageGetTrend` | `GROUP BY request_date`（日桶；跨月自动补零） | 命中率折线 + 用量堆叠柱数据源 |
| `UsageGetStackedByModel` | `GROUP BY request_date, provider_id, model_id` | ECharts `dataset` 直供 |
| `UsageGetProviderShare` | `GROUP BY provider_id` | 环形图 |
| `UsageQueryPage` | `WHERE dt BETWEEN + provider IN + model IN ORDER BY dt DESC LIMIT/OFFSET` | 明细表分页（≤200/页） |

- 时间预设换算（后端换算，前端只传 `preset`，防时区/周起算分歧）：`today/yesterday/last3/last7/last30/thisWeek(Mon起)/thisMonth/all + custom(start,end)`，全部换算为 `[startDate,endDate]` 闭区间后进 SQL（字符串比较）。
- 性能：万行内全索引命中 <50ms；十万行 `GROUP BY date,pm` 仍 <300ms（见 06 基线）；前端图表点数上限按天桶（30 天 × 模型数 ≤ 500 系列点，ECharts 无压力）。

## 3.6 迁移、备份与隐私

- 数据根迁移（`StorageSettingsPanel` “迁移数据”）：`metrics/` 随 `workspaces/sessions/logs` 同规则整体复制（含 `-wal/-shm`），复制前 `Checkpoint(TRUNCATE)` 合并 WAL，保证单文件可用。
- 只迁不删旧库 + 200MB 预检复用既有逻辑；`usage.db` 纳入 `logRetentionDays` 之外（用量是资产不是日志，**不自动清**，只提供 Tab 内“清空筛选范围”二期入口，本期不做删除 API）。
- 隐私：库内仅 token 计数 + 名称 + 时间，无 prompt 正文、无 API Key；文件权限沿用数据根默认 ACL；DPAPI 不涉及。
