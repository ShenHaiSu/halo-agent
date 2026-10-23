# need22-02a P1 稳定性诊断与代码证据

> 只摆证据。路径前缀 `develop/src/Host.Desktop/`。

## 1. 取消与并发：丢轮 + 泄漏

| 位置 | 现状 | 后果 |
|---|---|---|
| `Services/AgentOrchestrator.cs:38`（`_activeSessionTokens`）+ `:709,863`、`Bridge/HostBridgeFacade.cs:842-884` | 同 session 重入直接 `Cancel` 旧轮，无排队/拒绝反馈 | 连点发送静默丢第一轮，用户以为“没发出去又发一次”实际丢一次 |
| `AgentOrchestrator.cs:57`（`_sessionAppendLocks`）、`Services/WorkspaceSessionService.cs:367` | `ConcurrentDictionary` 只增不删 | 长运行句柄泄漏 |
| `Services/SubAgentCoordinator.cs:71`（`_trees`）、`:209 Task.Run(...,None)`、`:260 Barrier` | 子 `Cts` 从未 `Dispose`；`Task.Run` 脱离调度；`Barrier=Task.WhenAll` 无超时 | 子卡死主永等；取消后资源不释放 |
| `LlmGateway/LlmGatewayClient.cs:84 Task.Run(None)` + `writer.Write(None)` | 取消后仍写 `Channel` | 取消竞争下旧轮事件串扰新轮 |
| `Tools/BuiltIn/AskUserGate.cs` | 靠 `CancelAgentTree:945` 解塞，漏调则死锁 | 问询取消链路脆弱 |

## 2. 超时与重试缺失

| 位置 | 现状 |
|---|---|
| `HttpClient`（`HaloHttpClientFactory`） | 无显式 `Timeout`，hung 住跟随进程 |
| `AgentOrchestrator.cs:303 effectiveMax==0` | 无限迭代，仅靠人工停止 |
| `LlmGatewayClient.cs:165,180` | 仅重试 `400 stream_options/strict` 各一次；无 429/5xx/超时退避 |
| `AgentOrchestrator.cs:634,790` | 空 `FunctionName` 静默丢弃；工具异常全转自愈文本，可空转 25 轮耗费 |

## 3. 压缩阻塞

| 位置 | 现状 |
|---|---|
| `Services/CompactionService.cs:104,200` | 持 `sessionAppendLock` 做全程 LLM 摘要（网络+推理耗时计入临界区） |
| `Bridge/HostBridgeFacade.cs:1272` | 无取消入口 |
| 后果 | 压缩期间同会话读写全饿死；压缩 hung 则会话永久卡死 |

## 4. 内存与事件

| 位置 | 现状 |
|---|---|
| `AgentOrchestrator.cs:397,420`（`textAcc/ArgsBuilder`）、`:660 imgCache`、`MultimodalPlanner` | 全轮累积 + 原图同步读盘转 `dataURL` 常驻，大输出/多图易 OOM |
| `Bridge/JsEventDispatcher.cs:21 EventQueue`、`:123` 16ms 合并、`:171 Shutdown` | 队列无界，渲染慢则堆积；`Shutdown` 直接清空丢事件（含 `TurnCompleted` 可能丢失） |
| `LlmGateway/Sse/SseStreamReader.cs:45` | `WaitAsync+ReadLineAsync` 取消语义弱 |

## 5. 用量失真

| 位置 | 现状 |
|---|---|
| `SubAgentCoordinator.cs:415 Barrier` | 只读子末条 `usage`，多轮子耗被低估 |
| `AgentOrchestrator.cs:696 TurnCompleted` | 子耗恒 0 回填前端 |
| `Services/UsageMetricsService.cs:104,281` | 单写线程 + `INSERT OR IGNORE(request_id)` 去重，重试用量静默丢失 |

## 6. 定级

| 等级 | 项 |
|---|---|
| P1 阻塞 | Barrier 无超时、压缩持锁、取消丢轮 |
| P1 严重 | 无 HTTP 超时、无 429 退避、无限迭代、用量失真 |
| P2 一般 | EventQueue 无界、空 FunctionName 静默丢弃 |
