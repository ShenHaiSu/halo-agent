# 02 重试分池 + 空闲看门狗 + 传输重建（P0-2）

第二参考：`contextAndRender/02-network-retry-proxy.md`。Electron/undici 部分不抄，只取语义。

## 1. 前置检查

- [ ] 01 已合入：`ProviderResponseCapture` 可提供 `status/headers/requestBytes/errno`。
- [ ] 记录现重试语义（`Resilience.cs::SendRequestAsync`）：固定 1s/2s/4s、无 `Retry-After`、429 与 5xx 同池——本阶段全部删除。
- [ ] 确认 `HaloHttpClientFactory` 创建 `HttpClient` 的位置与复用方式（Handler 生命周期）。
- [ ] 确认 `LlmGatewayClient` 流解析是 `Channel + Task.Run` 结构，看门狗挂点在其上游。

## 2. 阶段目标

- 429 与 transient 分池、服务端 delay 优先、全局封顶、可取消 sleep。
- 只重试起流前；起流后交给轮次级恢复，不重放。
- 零事件流 180s 看门狗 + abort + 排空，防双计费。
- 同 origin 连续无响应才重建传输，30s 节流，先装新后关旧。

## 3. 实现办法

### 3.1 重写 `Resilience.cs` 为 `ProviderRetryPolicy.cs`

- 常量：`RateLimitedMax=10, TransientMax=10`（替代旧 3 次）；`RateInitial=2s 指数退避 +25% 抖动 cap 30s`；`Transient 1s→2s→4s→8s cap 8s`。
- `RateLimitDelayMs(attempt, headers)`：优先级 `retry-after-ms` → `Retry-After` 秒 → HTTP-date → 指数退避；全程 `min(serverDelay, cap)`，坏 header 不挂死 turn。
- `TransientDelayMs` 同理先看服务端 `Retry-After`（408/409/5xx 也带）。
- `DelayWithAbort(ms, ct)`：`Task.Delay` 可被 abort 打断，转 `OperationCanceledException`，abort 语义不丢。
- 不透明 400 修一次：`400/422 + "(no body)"` → 去 `max_tokens/max_completion_tokens/max_output_tokens` 重试一次，不耗预算；再坏原样抛。
- `CreateRetryStream`：仅 `!sawStart && error` 重试；`sawStart` 后直接透传，由 `AgentOrchestrator` 做消息级恢复。

### 3.2 空闲看门狗 `StreamIdleWatchdog.cs`

- 默认 180s，环境变量 `HALO_STREAM_IDLE_TIMEOUT_MS` 可调，`0` 关闭，正数钳 ≥30s。
- 每个事件重置 timer，只管零事件时长；超时以 `STREAM_FAILED` 形状结束，走 transient 预算。
- 必须 `onStall: abort 原请求 + 排空已弃流`，否则 backoff 醒来开第二个请求造成双计费。

### 3.3 传输健康 `ProviderTransportHealth.cs`

- `observeFailure(origin, errno, route)`：同一 origin 连续 2 次无响应才判死；成功即清零。
- `RebuildThrottled(origin)`：30s 一次；重建 = `HaloHttpClientFactory.Recreate(origin)` 先装新后关旧，在途请求不 abort。
- `route` 上报进 `llm-trace`：`direct/system-proxy/custom-proxy`，区分“网关挂”与“隧道死”。
- 删除 Electron 专属：`node-proxy/system-proxy-relay/authenticated-relay/system-ca` 不移植；`.NET` 用 `SocketsHttpHandler` 重建等价。

### 3.4 trace 升级

- `[REQ]` 追加 `requestBytes + route`；`[RES]` 追加 `hitRate + retryCount + idleTimeout?`。预测只记本地推测，不进账单口径。

## 4. 验收标准

- [ ] 单测：429 带 `Retry-After: 5` 退避 ≈5s 而非指数值；坏 header（`Retry-After: abc`）不挂死，落到指数退避。
- [ ] 单测：terminal（401/上下文超限）零重试；不透明 400 只修一次。
- [ ] 集成：起流后失败不重放整轮（assistant 消息不 duplication）；看门狗超时 abort 后无第二个计费请求（mock handler 计数=1）。
- [ ] 集成：kill 代理后连续 2 次同 origin 失败触发一次重建，30s 内不重复建。
- [ ] 旧固定退避代码已删除；`RetryDelayAsync` 测试钩子保留或等价保留。

## 5. 风险

- 重试次数从 3 放到 10，网关持续 500 时 turn 变长。用 cap + abort 兜底，不兼容旧“快速失败”手感，允许。
