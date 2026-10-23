# need22-02 P1 后端稳定性与性能

> 等级：P1。前置：P0 回归基线（共用 `dotnet test` 绿）。

## 1. 前置检查

- [ ] 记录取消/超时现状：连点同一 session 两次，观察旧轮是否被丢；长 `Barrier` 子卡死是否主永等。
- [ ] 记录内存基线：大输出（>1MB `toolOutput`）+ 多图轮的 `textAcc/ArgsBuilder/imgCache` 占用与 `EventQueue` 堆积。
- [ ] 记录用量偏差：多轮子代理的 `TurnCompleted` 子耗是否为 0（`AgentOrchestrator.cs:696`）。

## 2. 既定目标

1. 取消语义端到端透传：连点不再静默丢轮；`CTS`/`Lock` 无泄漏。
2. 超时与重试收敛：LLM/子屏障/压缩均有总体超时；429/5xx 抖动退避。
3. 压缩不阻塞会话读写，可取消。
4. 大输出/图片不驻留内存；事件队列有界背压。
5. 用量精准：子多轮消耗全量累加。

## 3. 改动清单

| # | 文件 | 现状 | 改为 |
|---|---|---|---|
| T1 | `Services/AgentOrchestrator.cs:38,709,863`、`Bridge/HostBridgeFacade.cs:842-884` | 同 session 重入直接 `Cancel` 旧轮，无排队/拒绝反馈 | 重入返回 `TURN_IN_PROGRESS`（或排队 1 位），前端据此 toast；旧轮走正常 `TurnCancelled` 落盘 |
| T2 | `Services/AgentOrchestrator.cs:57`（`_sessionAppendLocks`）、`SubAgentCoordinator.cs:71,209`、`LlmGatewayClient.cs:84` | `Lock` 只增不删；子 `Cts` 未 `Dispose`；`Task.Run(None)` + `writer.Write(None)` 取消后仍写 | `finally { TryRemove + Dispose }`；`Task.Run(..., ct)`；取消后关闭 `ChannelWriter.TryComplete` |
| T3 | `LlmGateway/Sse/SseStreamReader.cs:45`、`LlmGateway/LlmGatewayClient.cs:318,340` | `WaitAsync+ReadLineAsync` 取消语义弱；`ResponseHeadersRead` 但无显式超时 | `ReadLineAsync(ct)` 全链透传；`HttpClient.Timeout=120s` + 每轮总体超时（如 10min） |
| T4 | `SubAgentCoordinator.cs:250-270 Barrier` | `Task.WhenAll` 无超时，子卡死主永等 | Barrier 总体超时（如子数×单子超时+buffer），超时子按 `summary` 截断回填 + 标记 `timedOut` |
| T5 | `Services/AgentOrchestrator.cs:303`、`LlmGatewayClient.cs:165,180` | `effectiveMax==0` 无限迭代；仅重试 `400 stream_options/strict` 各一次 | `effectiveMax==0` 视为配置非法直接拒绝；429/5xx/超时指数退避 3 次（抖动），错误码透传前端 |
| T6 | `Services/AgentOrchestrator.cs:634,790` | 空 `FunctionName` 静默丢弃；工具异常全转自愈文本可空转 25 轮 | 空名返回 `EMPTY_TOOL_NAME` 并计入连续失败计数；连续 3 次同错直接终轮 + 明确错误 |
| T7 | `Services/CompactionService.cs:104,200`、`Bridge/HostBridgeFacade.cs:1272` | 持 `sessionAppendLock` 做全程 LLM 摘要；无取消入口 | 快照拷贝后释锁再调 LLM；压缩 `CTS` 接入取消；压缩中允许查询、禁止新轮（明确错误码） |
| T8 | `Services/AgentOrchestrator.cs:397,420`、`MultimodalPlanner`、`JsEventDispatcher.cs:21,123,171` | `textAcc/ArgsBuilder/imgCache` 全驻留；原图同步读盘转 dataURL；`EventQueue` 无界；`Shutdown` 清空丢事件 | 大 `toolOutput`（>64KB）先落盘再引用 `fileRef`；图片缩略图+原图懒取；`EventQueue` 有界（如 1000）+ 背压丢弃策略（保 `TurnCompleted`）；`Shutdown` 排空后再关 |
| T9 | `Services/UsageMetricsService.cs:104,281`、`AgentOrchestrator.cs:696,415` | 单写线程 + `INSERT OR IGNORE` 以 `request_id` 去重，重试用量丢失；子耗只读末条 | 改 `ON CONFLICT DO UPDATE` 累加；`Barrier` 累加全部子 `usage`；`TurnCompleted` 回填真实子耗 |

## 4. 验收标准与验收方法

| 验收项 | 标准 | 方法 |
|---|---|---|
| B1 重入 | 连点返回明确状态，不静默丢轮 | 双发 `startAgentTurn` 脚本 + 事件序列断言 |
| B2 无泄漏 | 100 轮后 `Lock/CTS` 计数不增长 | 压测前后内存/句柄快照对比 |
| B3 超时 | 子卡死/LLM  hung 在预算内终轮并标记 | mock hung 网关 + 超时断言 |
| B4 重试 | 429/5ms 抖动重试 3 次后明确失败 | mock 429 网关 + 重试计数断言 |
| B5 压缩 | 压缩中查询可用、新轮被拒有码、可取消 | 并发压缩+查询脚本 |
| B6 内存 | 1MB 输出轮内存增量 <50MB，无 OOM | 大输出压测 + 任务管理器/计数器 |
| B7 用量 | 多轮子耗全量累加，非 0 | 子 3 轮 fixture + `TurnCompleted` 断言 |

## 5. 回滚

* T1/T5/T7 独立提交，任一回滚不影响其余；超时值全部收敛到 `AgentConfigService` 可配置，改值无需发版。
