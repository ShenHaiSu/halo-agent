# 01 · 会话时间“记录链路”修复方案（新建/交互后仍显示时间未知）

## 1. 现状（已读代码，结论：展示层已打补丁，记录链路仍断）

- 卡面模板 `WorkspaceSessionSidebar.vue` L226-229 / L304-307 已有时间行 + `hasNoTime → 时间未知` 兜底 + 60s `nowTick`（need03-01 成果）。
- `timeFormat.ts` 全套就绪：非法返回 `-`，`hasNoTime` 判双 `-`。
- `workspaceStore.loadSessions` L165-170 对空串缺字段做内存回填；`appendMessage` L321-324 发消息后内存态 `cur.updatedAtUtc = now`。
- 后端 `WorkspaceSessionService.CreateSessionAsync` L198-213 / `AppendSessionMessageAsync` L264-268 均写 `CreatedAtUtc/UpdatedAtUtc = DateTime.UtcNow`，`UpdateSessionTitle/SetPinned` 也更新。
- **但用户仍见“时间未知”**，说明问题不在模板，而在**数据从没合法到达模板**，或**到达后又被冲掉**。

## 2. 根因（按概率排序，五个断点）

| # | 断点 | 机理 | 证据/验证 |
|---|---|---|---|
| R1 | Agent 主链路根本不走 `workspaceStore.appendMessage` | `agentStore.sendPrompt` L436-475 直接 `bridge.startAgentTurn`，用户消息由后端 `AgentOrchestrator` L142 `AppendSessionMessageAsync` 落盘；前端 `sessions[].updatedAtUtc` 全程不碰，直到 `refreshAfterTool → loadSessionMessages`（只刷消息不刷会话列表）。所以一轮内“活跃”永不刷新 | 发一条消息后看 `sessions[0].updatedAtUtc` 不变即实锤 |
| R2 | 轮末不刷新会话列表 | `finishTurn/refreshAfterTool` L410-434 只 `loadSessionMessages + refreshBudget`，从不 `getSessions`；后端 index 已更新，前端内存仍是旧时间 | 切卡/刷新后时间突然“恢复”即实锤 |
| R3 | 脏数据 `0001-01-01` 绕过前端回填 | `SessionMetadata.CreatedAtUtc { get; init; }` 老 `meta.json/sessions-index.json` 缺字段反序列化得 `default(DateTime)`；序列化为 `0001-01-01T00:00:00`，前端 `if (!s.createdAtUtc)` 为假（非空字符串），回填不触发；`new Date('0001-01-01T00:00:00')` 合法不返 `-`，应显示 `0001-01-01` 而非“未知”——若用户见“未知”则说明还有空串/`null` 分支（更老的 index 或 bridge 传空） | DevTools 打印原始 `createdAtUtc` 空串 vs `0001` 即可区分 |
| R4 | 新建会话竞态：`createSession` 乐观插入后被 `loadSessions` 覆盖 | `createSession` L217 `sessions=[newSess,...]` 后不排序；若后台同时 `loadSessions` 返回旧 index（未含新会话或含旧时间），新时间被冲掉 | 连续新建两会话复现错乱即实锤 |
| R5 | `normalizeBridgePayload` 大小写/时区误读 | C# `DateTime` 默认序列化带 `T` 无 `Z`（`Unspecified`），`new Date('2026-10-15T14:32:05')` 按本地解析偶发 `Invalid Date`（CefSharp 109 对无时区 ISO 解析保守）；`toCamelKey` 首字母小写对 `CreatedAtUtc → createdAtUtc` 正常，但若后端某分支返回 `created_at` 即丢 | 看 Network/Cef 日志原始 payload |

## 3. 设计目标

1. **记录必达**：任何写消息路径（用户发出 / Agent 追加 / 改名 / 置顶 / 后台轮）结束时，前端内存 `updatedAtUtc` 必达秒级；后端落盘必带合法 UTC。
2. **读取必合法**：`0001-01-01`、空串、`null`、无时区串四种脏输入统一收敛为“可展示、可排序、不崩”。
3. **单向权威**：后端 `sessions-index.json + meta.json` 为权威；前端只做内存态合并与排序，不反写脏时间回磁盘（避免污染排序）。

## 4. 详细设计

### 4.1 后端（C#，小而 closed）

1. **统一 UTC 输出**：`SessionMetadata.CreatedAtUtc/UpdatedAtUtc` 序列化前一律 `DateTime.SpecifyKind(v, Utc).ToString('o')`（带 `Z`）；`HaloJsonOptions` 若未配 `JsonStringEnumConverter` 不动，只加时间处理（建议在 `AtomicFileWriter` 或 DTO 层统一，不逐个调用点改）。
2. **防御性回填（读时修）**：`SessionRepository.LoadSessionsIndexAsync/LoadSessionMetaAsync` 返回前：
   - `if (CreatedAtUtc == default || Year < 2000) CreatedAtUtc = File.GetCreationTimeUtc(meta.json) ?? UpdatedAtUtc ?? Now`；
   - `if (UpdatedAtUtc == default || UpdatedAtUtc < CreatedAtUtc) UpdatedAtUtc = CreatedAtUtc`；
   - 只内存态修正 + 顺手 `SaveSessionMetaAsync` 回写一次（加 `try/catch`，失败不阻断读取）。这是根治老数据 `0001` 的唯一位置，前端再怎么补都只是遮羞。
3. **写时必带**：`AppendSessionMessageAsync` 缺 `meta` 分支（L253-262）当前只填 `CreatedAtUtc`，补 `UpdatedAtUtc = Now` + `MessageCount` 以 jsonl 实际行数校准（防 `MessageCount++` 在并发下漂移）。
4. **新增轻量事件载荷（可选，不加接口）**：`AssistantMessagePersisted/TurnCompleted/ToolExecutionCompleted` 的 `payload` 追加 `updatedAtUtc`（后端 `DateTime.UtcNow'o'`），前端收到即点对点更新对应卡，无需全量 `getSessions`。不改 DTO 字段名，只加事件可选键，兼容老前端。

### 4.2 前端 store（核心修复，R1/R2）

1. **`workspaceStore` 新增 `touchSessionTime(sessionId, iso?)`**：找到 `sessions` 项即 `updatedAtUtc = iso || now`，并按 `isPinned desc, updatedAtUtc desc` 重排（与后端 `GetSessionsByWorkspace` 同序，避免“时间变了位置不动”）。
2. **调用点全覆盖**：
   - `createSession` 成功后：`touchSessionTime(newSess.id, newSess.createdAtUtc)`（用后端返回值，不用本地 now，保证与落盘一致）；
   - `agentStore.sendPrompt` 乐观处：用户点击发送即 `touchSessionTime(activeSessionId)`（先让“活跃”跳变，轮末再以后端为准校准一次）；
   - `agentStore.handleTurnEvent` 的 `AssistantMessagePersisted/TurnCompleted/ToolExecutionCompleted`：若 payload 带 `updatedAtUtc` 用它，否则 `touchSessionTime(sid)`；
   - `renameSession/togglePinSession` 成功后同样 touch（后端已更新，前端同步）；
   - `finishTurn` 末尾：若本轮有写入（`AssistantMessagePersisted` 到过），调用一次 `bridge.getSessions(workspaceId)` 轻量对账（只取时间/条数/标题三字段合并，不替换整个数组引用，避免滚动跳动）。频率上限：每轮 1 次，可接受。
3. **`loadSessions` 回填升级**：现有 `if (!s.createdAtUtc)` 扩展为 `isBadTime()`——空串/`null`/`Invalid Date`/`Year<2000`（含 `0001`）全部视为坏时间，回填 `nowIso`（内存态，不回写磁盘）；同时 `updatedAtUtc < createdAtUtc` 则拉齐。`timeFormat.ts` 新增 `isBadTime(iso)` 纯函数供两处复用。

### 4.3 展示层（不动文案，只加可观测）

- 文案保持 `MM-DD · 活跃 x分钟前` + tooltip 全量（need02 共识不变）。
- `hasNoTime` 改判 `isBadTime(createdAtUtc) && isBadTime(updatedAtUtc)`；理论上修完链路后新会话永不进此分支，该分支仅作老数据迁移期的兜底展示。
- `data-testid="session-meta-line"` 保留；`sessionTimeTitle` 对坏时间显示 `未知（老数据已自动修复）` 而不是 `-`，便于用户区分。

## 5. 施工步骤

1. 后端：`SessionRepository` 加读时回填（2 处：index + meta）→ `WorkspaceSessionService.Append` 缺 meta 分支补 `UpdatedAtUtc` → 事件 payload 追加 `updatedAtUtc`（`AgentOrchestrator` 3 处 `PublishTurnEvent`）→ `dotnet build`。
2. 前端：`timeFormat.ts` 加 `isBadTime` → `workspaceStore` 加 `touchSessionTime` + `loadSessions` 回填升级 + `createSession/rename/togglePin` 调用 → `agentStore` 在 `sendPrompt/handleTurnEvent/finishTurn` 调用 + 轮末一次 `getSessions` 对账 → `vue-tsc -b && vite build`。
3. 数据迁移验证：备份一条老 `sessions-index.json`（缺时间/`0001` 各一），重载后看是否自动修复且排序正确；确认磁盘 `meta.json` 被回写一次后不再反复写（mtime 稳定）。

## 6. 验收清单

- [ ] 新建会话：卡面立即 `MM-DD · 活跃 刚刚`，无“时间未知”；刷新/Cef 重启后仍在。
- [ ] 发一条消息：不切卡情况下 3s 内“活跃”刷新；切到他卡再切回时间一致。
- [ ] 后台轮（A 卡生成中切到 B 卡）：A 卡“活跃”在轮末追上；B 卡不受影响。
- [ ] 脏数据：空串/`null`/`0001-01-01`/无时区串四种输入卡面均非空、排序不崩、tooltip 可读。
- [ ] `document.querySelectorAll('[data-testid=session-meta-line]')` 文本全部非空；暗/亮截图各一。

## 7. 风险与回退

- 后端读时回写加 `try/catch`，失败只打日志不阻断；回退即删 `touchSessionTime` 调用与回填升级，模板兜底保留，前后兼容。
- 轮末 `getSessions` 每轮 1 次，数据量小（index 文件 KB 级），性能可忽略；若极端多会话（>500）可降为只在 `TurnCompleted` 拉一次，`ToolExecutionCompleted` 只内存 touch。
