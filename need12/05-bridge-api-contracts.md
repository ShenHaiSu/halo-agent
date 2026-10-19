# 05 Bridge 与 API 契约

## 5.1 命名与文件

- 后端：新建 `src/Host.Desktop/Bridge/HostBridgeFacade.Usage.cs`（`partial HostBridgeFacade`），方法经现有 `BindAsync("usage.*")` 范式注册（照抄 `HostBridgeFacade.SkillMemory.cs` 注册写法）。
- 前端：`src/Web.Client/src/bridge/hostBridge.ts` 追加 `Usage*` 类型 + `usageBridge` 封装（`window.hostBridge.invoke('usage.xxx', args)` 同既有风格）。

## 5.2 方法清单（6 个，只读为主，无删除）

| # | Bridge 名 | 入参 | 出参 `data` | 说明 |
|---|---|---|---|---|
| U1 | `usage.getFilterOptions` | `{}` | `{ providers:[{id,name}], models:[{providerId,providerName,modelId,display,key,label}] }` | 下拉源；空库返回空数组不报错 |
| U2 | `usage.getOverview` | `UsageFilter` | `{ totalRequests, sumInput, sumOutput, sumCached, hitRate, activeModels }` | 瓦片；`hitRate 0-1` |
| U3 | `usage.getTrend` | `UsageFilter + metric: in\|out\|total` | `{ dates:[], perModel:{key,providerName,modelId,values[]}[], hitRates:[], totals:[] }` | 堆叠柱 + 命中率折线同源 |
| U4 | `usage.getProviderShare` | `UsageFilter` | `{ items:[{providerId,name,value,share}] }` | 环形图 |
| U5 | `usage.queryPage` | `UsageFilter + {page,pageSize≤200, sort}` | `{ total, page, pageSize, rows:[UsageRowDto] }` | 明细表 |
| U6 | `usage.getDbStatus` | `{}` | `{ dbPath, recordCount, dbSizeBytes, oldestDate, newestDate }` | 诊断（存设置页“存储与日志”旁注或用量页脚） |

`UsageFilter`（TS/C# 同构）：
```ts
interface UsageFilter {
  preset: 'today'|'yesterday'|'last3'|'last7'|'last30'|'thisWeek'|'thisMonth'|'all'|'custom';
  startDate?: string; endDate?: string;   // preset=custom 时必填 yyyy-MM-dd
  providerIds?: string[];                  // 空 = 全部
  modelKeys?: string[];                    // "provider_id\x1fmodel_id"，空 = 全部
}
```

`UsageRowDto`：`{ id, requestDate, requestTime, requestDatetime, providerName, modelId, modelDisplay, protocol, inputTokens, outputTokens, cachedTokens, hitRate, status }`。

## 5.3 事件载荷变更（需求 1，兼容）

- `ToolCallStarted` 新增 `receivedBytes: 0`；`ToolCallUpdated` 新增 `receivedBytes: number; argsLength: number`（节流后）；`ToolCallCompleted` 新增 `receivedBytes终值`；`ToolExecutionCompleted` 新增可选 `fileBytes`。
- 全部**新增字段**，旧前端忽略即兼容；`JsEventDispatcher` 批量结构不变。

## 5.4 错误码

| code | 何时 | 前端动作 |
|---|---|---|
| `USAGE_DB_NOT_READY` | DB 初始化失败 | 瓦片显示“-”，横幅提示“用量库初始化失败，不影响对话” |
| `USAGE_BAD_FILTER` | 非法日期/preset | 红色表单提示，不发请求 |
| `USAGE_PAGE_OVERFLOW` | 页码超界 | 回到末页 |
| 通用 `BRIDGE_ERROR` | SQL 异常等 | `message.warning` + Warn 日志，面板降级空态 |

所有用量 Bridge 方法内部 `try/catch → BridgeResponse{ok:false}`，**禁止抛到 CefSharp binding 层**（会炸抽屉）。

## 5.5 DTO 施工顺序

1. 先冻结本篇 5.2 的 TS 接口（`hostBridge.ts`）→ 2. 前端用 Mock 数据跑通 04 的全部图表 → 3. 后端 `HostBridgeFacade.Usage.cs` 对 SQL 实现 → 4. 联调只换 `usageBridge` 真实现（见 06 Mock 约定）。
