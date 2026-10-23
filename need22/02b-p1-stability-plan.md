# need22-02b P1 稳定性施工方案

> 对应诊断：[02a](./02a-p1-stability-diagnosis.md)。

## 1. 前置检查

- [ ] P0 已合入且 `dotnet test` 绿（共用回归基线）。
- [ ] 记录三基线：连点丢轮复现、Barrier hung 复现、大输出内存快照。

## 2. T1 重入不再丢轮

* **改**：`AgentOrchestrator.cs:709,863` + `HostBridgeFacade.cs:842-884`：同 session 重入返回 `TURN_IN_PROGRESS`（附 `activeRequestId`），或队列 depth=1；旧轮走正常 `TurnCancelled` 落盘，前端 toast“上一轮进行中”。
* **改**：`sendPrompt` 携带 `requestId`（P2-F2 联动，后端回显去重）。

## 3. T2 取消与资源释放

* **改**：`_sessionAppendLocks` + `_activeSessionTokens` + 子 `Cts`：`finally { TryRemove + Dispose }`。
* **改**：`LlmGatewayClient.cs:84`：`Task.Run(..., ct)` + 取消后 `ChannelWriter.TryComplete()`，不再写旧事件。
* **改**：`SseStreamReader.cs:45`：`ReadLineAsync(ct)` 全链透传。

## 4. T3 超时与重试

* **改**：`HaloHttpClientFactory`：`HttpClient.Timeout=120s`；每轮总体超时 10min（`AgentConfigService` 可配），超时终轮 + `TIMEOUT` 码。
* **改**：`LlmGatewayClient.cs`：429/5xx/超时指数退避 3 次（1s/2s/4s + 抖动），错误码透传前端。
* **改**：`AgentOrchestrator.cs:303`：`effectiveMax==0` 视为配置非法直接拒绝，不再无限迭代。
* **改**：`:634` 空 `FunctionName` 返回 `EMPTY_TOOL_NAME` 并计连续失败；`:790` 连续 3 次同错直接终轮。

## 5. T4 Barrier 超时

* **改**：`SubAgentCoordinator.cs:250-270`：Barrier 总体超时（子数×单子超时+buffer）；超时子按 `summary` 截断回填 + 标记 `timedOut:true`，主轮继续。

## 6. T5 压缩不阻塞

* **改**：`CompactionService.cs:104`：快照拷贝（messages 复制）后释锁，再调 LLM；压缩 `CTS` 接入取消（`HostBridgeFacade:1272` 新增 `cancelCompaction`）。
* **改**：压缩中查询可用、新轮返回 `COMPACTING` 明确码。

## 7. T6 内存与事件

* **改**：大 `toolOutput`（>64KB）先落盘再引用 `fileRef:{path,size}`，`content` 只留前 4K 预览。
* **改**：图片缩略图 + 原图懒取（P3-G3 联动，后端提供 `imageRef` 而非 `dataURL` 全量）。
* **改**：`JsEventDispatcher.EventQueue` 有界 1000 + 背压策略（保 `TurnCompleted/TurnError`，丢中间 `TextDelta` 可重放）；`Shutdown` 先排空再关。

## 8. T7 用量精准

* **改**：`Barrier:415` 累加全部子 `usage`；`TurnCompleted:696` 回填真实子耗。
* **改**：`UsageMetricsService.cs:281`：`INSERT OR IGNORE` 改 `ON CONFLICT(request_id) DO UPDATE` 累加。

## 9. 提交切分

```text
t1-reentry → t2-cancel → t3-timeout-retry → t4-barrier → t5-compact
  → t6-memory-events → t7-usage
```
