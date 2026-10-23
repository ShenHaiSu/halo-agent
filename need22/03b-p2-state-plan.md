# need22-03b P2 前端状态施工方案

> 对应诊断：[03a](./03a-p2-state-diagnosis.md)。前置：P1 错误码（`TURN_IN_PROGRESS/COMPACTING`）到位。

## 1. 前置检查

- [ ] 三复现录屏：双影、快切覆盖、问询错票（见 03a §1–2）。
- [ ] 确认后端 `requestId` 回显字段名（与 P1-T1 联动）。

## 2. F1 单真相源（最大分支，独立）

* **改**：`TurnStore` 收敛（二选一，推荐 A）：
  * A：`agent.turns` 为唯一真相，`sessionMessages` 降级为持久投影（标题/导出/分支用），渲染只读 `turns`。
  * B：新建 `turnStore(Map<rowId,Row>)`，两者都投影到它。
* **改**：`activeSessionId` 提升到单一 `sessionStore`，`workspace/agent` 只读它；删除 `hiddenToolCallIds` 去重。
* **改**：`buildTurnGroups` 只算一次（放 `TurnStore` getter），`HistoryTocPanel` 与 Timeline 共享。
* **提交**：独立分支 `p2-f1-single-source`，合入前双协议各一轮录屏对比。

## 3. F2 请求幂等

* **改**：`agentStore.sendPrompt` 生成 `requestId=uuid`，`hostBridge.startAgentTurn` 透传，后端回显；前端以 `requestId` 去重事件；连点第二发直接返回既有 `requestId` + toast。
* **单测**：双发脚本只产生一轮。

## 4. F3 切换取消令牌

* **改**：`selectSession` 每次递增 `epoch` + `AbortController`；`select/load/heal/budget` 全接 `signal`；视图只接受当前 `epoch`，旧响应丢弃并 `console.debug` 计数。
* **单测**：快切 5 次，视图与顶栏一致。

## 5. F4 问询归属

* **改**：问询按 `sessionId` 归属渲染，以 `modalSessionId` 为准；非当前会话问询在顶栏 badge（红点+计数），点击跳回。
* **改**：`askUserStore.pendingBySession` 结构不变，`AskUserModal` 只订阅 `modalSessionId` 对应项。

## 6. F5/F6 中断与遮罩

* **改**：`stopGenerating` 改为 await 后端 `TurnCancelled` 事件后再清本地（超时 5s 降级清 + 告警）。
* **改**：`finishTurn` 失败保留流式卡 + 错误 toast + 重试按钮（复用 P3-G6 的 `lastError` 结构）。
* **改**：问询改为非阻塞右侧栏（可继续浏览历史）；取消分两级按钮：“只关问询” vs “取消整轮”，文案明确。

## 7. 提交切分

```text
p2-f1-single-source → p2-f2-idempotency → p2-f3-epoch → p2-f4-askuser → p2-f5-stop
```
