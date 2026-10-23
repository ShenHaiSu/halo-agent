# need22-02c P1 稳定性验收与回滚

> 对应方案：[02b](./02b-p1-stability-plan.md)。

## 1. 前置检查

- [ ] P0 验收通过；`dotnet test -c Release` 绿。
- [ ] mock 网关（hung/429 翻转）就绪；内存快照工具就绪。

## 2. 验收矩阵

| # | 验收项 | 标准 | 方法 |
|---|---|---|---|
| B1 重入 | 连点第二发返回 `TURN_IN_PROGRESS`，旧轮正常 `TurnCancelled` 落盘，无静默丢轮 | 双发脚本 + 事件序列断言 | `startAgentTurn×2` 同 session |
| B2 无泄漏 | 100 轮后 `Lock/CTS` 计数不增长，句柄不涨 | 压测前后快照对比 | 100 轮空转脚本 |
| B3 超时 | LLM hung/子卡死在预算内终轮并标记 `TIMEOUT/timedOut` | mock hung 网关 + 超时断言 | hung fixture |
| B4 重试 | 429 两次后成功计 3 次调用；持续 429 三次后明确失败 | mock 429 网关 + 计数断言 | 429 fixture |
| B5 压缩 | 压缩中查询可用、新轮被拒有码、可取消 | 并发压缩+查询脚本 | `cancelCompaction` |
| B6 内存 | 1MB 输出轮内存增量 <50MB；`Shutdown` 不丢 `TurnCompleted` | 大输出压测 + 关闭压测 | 1MB fixture |
| B7 用量 | 3 轮子耗全量累加，非 0；重试用量不丢 | 子 3 轮 fixture + 重试 fixture | `TurnCompleted.usage` 断言 |

## 3. 验证命令

```bash
dotnet test tests/HaloAgent.Tests -c Release --filter "Stage04|Stage05|Stage06|Stage13"
dotnet test tests/HaloAgent.Tests -c Release
```

## 4. 回滚表

| 场景 | 动作 |
|---|---|
| 超时过严误杀长轮 | 只调 `AgentConfigService` 超时值（配置即改），不回滚代码 |
| Barrier 超时截断丢失关键子结果 | 调大 buffer；仍不行回滚 `t4-barrier` |
| 有界队列丢事件被感知 | 先放宽到 5000 并观察；再议重放策略 |
| 用量累加导致账单跳变被质疑 | 保留新口径，前端加“新口径”标注，不回滚 |

## 5. 准出 gate

* B1–B7 全过 + 全量绿，才能进 P2。
