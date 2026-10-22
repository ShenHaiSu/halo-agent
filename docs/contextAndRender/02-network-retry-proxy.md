# 网络请求构建（下）：重试 / 观测 / 代理传输

源码：`packages/agent-runtime/src/provider-retry.ts`，
`provider-transport-recovery.ts`，`local-request-errors.ts`，
`node-proxy.ts`，`system-proxy-relay.ts`，`system-ca.ts`。

## 1. `captureProviderResponse`：把“看不见的失败”留下来

文件 `provider-retry.ts:328-368`。它是包在用户 `fetch` 外面的第一层：

```ts
onResponse(); // 先清掉上一轮的 429，避免旧证据污染新失败
const requestBytes = byteLength(init.body); // 只记长度，不记内容
try {
  const res = await baseFetch(input, init);
  onResponse({ status: res.status, headers: lowercased }, requestBytes);
  return res;
} catch (e) {
  onResponse(undefined, requestBytes, describeProviderFetchFailure(e, input));
  throw e; // 原样抛出，不改变 provider 视角
}
```

三信号（issue #234 的教训）：

- `status/headers`：pi-ai 的 `onResponse` 故意不暴露失败 429，这里补上。
- `requestBytes`：请求体大小，诊断“大包被网关杀”的唯一安全信号。
- `failure`（errno）：`error.cause` 里的 `ECONNRESET/ETIMEDOUT` 等，pi-ai 扁平化成 `fetch failed` 后就丢了，这里在最后能看到原文的位置记下来。

`classifyProviderError(error, providerStatus)`（L159-180）：当 `status==429` 且分类仍是普通可重试时，强制改写为 `PROVIDER_RATE_LIMITED`。但已明确不可重试的（鉴权、上下文超限）保持 terminal，不复活。

## 2. 共享重试预算：429 与 transient 分池

常量（`provider-retry.ts:24-42`）：

```
PROVIDER_RATE_LIMIT_MAX_RETRIES  (= shared PROVIDER_RETRY_MAX_RETRIES)
初始 2s，指数退避，抖动 25%，上限 30s
PROVIDER_TRANSIENT_MAX_RETRIES   (同预算)
1s → 2s → 4s → 8s，上限 8s
```

- 429 走 `providerRateLimitDelayMs`（L236-256）：优先级 `retry-after-ms` → `Retry-After` 秒 → `Retry-After` HTTP-date → 指数退避+jitter。全程 `boundedServerDelay` 封顶，坏 header 不能把 turn 挂死。
- 非 429 transient（`NETWORK_ERROR/TIMEOUT/STREAM_FAILED/PROVIDER_ERROR`，L51-61）走 `providerSetupRetryDelayMs`（L268-286）：同样先看服务端 `Retry-After`（408/409/5xx 也带，`carriesRetryDelayHeaders` L68-71），再 1/2/4/8s。
- `delayWithAbort`（L292-313）：退避 sleep 可被 `AbortSignal` 打断，转 `AbortError`，abort 语义不丢。
- 不透明 400/422 修一次：`isOpaqueBadRequest`（L85-93）识别 `400/422 + "(no body)"`，`withoutDerivedOutputLimit`（L114-124）去掉 `max_tokens/max_completion_tokens/max_output_tokens` 重试一次。不耗 transient 预算，第二次再坏就原样抛出。

`createProviderRetryStream`（L422-542）只处理**起流前**失败（`!sawStart && error`）：起流后失败交给 runtime 的 mid-stream 恢复去换 assistant 消息，不在这里重放。

## 3. 空闲看门狗：零事件流不许挂住 turn

`withStreamIdleTimeout(inner, model, timeoutMs, onStall)`（L602-659）：

- 默认 `180s`，环境变量 `PI_DESKTOP_STREAM_IDLE_TIMEOUT_MS` 可调，`0` 关闭，正数钳到 ≥30s（`STREAM_IDLE_TIMEOUT_FLOOR_MS`，因为退避最长 30s，看门狗不能比退避还短）。
- 每个事件重置 timer，只管“零事件时长”，不管总时长，长但有产出的流原样放行。
- 超时后以 `STREAM_FAILED` 形状结束，让已有 transient 预算接管，不另开恢复路径。
- **必须传 `onStall` 去 abort 原请求**：否则被抛弃的 adapter 会从 backoff 醒来再开第二个请求——同一 turn 扣两次费。抛弃的流还要 `drainAbandonedStream` 排空队列防堆积。

## 4. `node-proxy.ts`：sidecar 的 undici 传输

背景：Electron main 用 Chromium `net.fetch`，sidecar 无 Chromium，只能用 Node `undici`。`applyNodeNetworkProxy(settings, env, relayUrl)`（L118-147）是唯一入口，由 `sidecar.configure` 下发驱动。

- custom http(s)：`ProxyAgent`；custom socks：`Agent({connect: socksConnector})` + TLS 升级（L402-423）。
- bypass：Chromium 风格 bypass 列表编译成 `proxyBypassMatcher`（L390-400），支持 `host/.suffix/*.suffix/host:port/IPv4 CIDR/<local>`，命中走直连 `Agent`，其余走代理。用 `proxied.compose` 实现分流（L168-173）。
- system 模式：经 Electron 的 system/PAC 解析器拿到 relayUrl（socks），走 `createSystemProxyDispatcher`。
- 安装顺序（L208-215）：**先设新 dispatcher 为 global，再关旧的**。`close()` 是优雅关闭——在途请求跑完才拆池，所以一个 session 触发的重建永远不 abort 另一个 session 的请求。
- `rebuildNodeNetworkTransport`（L249-277）：同一 origin 连续无应答时由 `provider-transport-recovery` 判定重建（undici 池里死了的 socket 失败本身不退役，必须关池才失效）。节流 `30s` 一次（`PROVIDER_TRANSPORT_REBUILD_MIN_INTERVAL_MS`），`activeNodeTransportRoute()`（direct/environment-proxy/system-proxy/http-proxy/socks5-proxy）随失败上报，区分“provider 挂了”还是“隧道死了”。
- `NODE_USE_ENV_PROXY=1`（进程启动时一次读取）决定默认 dispatcher 是 `EnvHttpProxyAgent` 还是直连 `Agent`，重建时必须复现，不能回落直连。

`system-proxy-relay.ts / authenticated-proxy-relay.ts`：system 代理需要认证时，main 起本地 relay，sidecar 只拿 relayUrl，不直接碰系统钥匙串密码。

`system-ca.ts :: applyAdditiveDefaultCaCertificates()`：sidecar 启动即合并 bundled + system + extra CA。macOS 上 Electron 43 的 `--use-system-ca` 是**替换**而非合并（丢 GlobalSign R3 等公锚，issue #1187），所以 mac 由 sidecar 自己合并，launcher 省略该 flag；Win/Linux 保留 flag。

## 5. 启动/配置下发

- `PI_DESKTOP_PROXY_JSON` 环境变量：sidecar 启动第一包代理配置（`sidecar.ts` 尾部），无效则忽略，等 `sidecar.configure` 覆盖。
- `sidecar.configure { networkProxy, systemProxyRelayUrl }`：只改传输层，不碰 host-core 归属。

## 复刻清单

1. 先做 `captureProviderResponse` 三信号，再谈重试——没有 status/errno 的重试全是盲猜。
2. 429 与 transient 分池 + 服务端 delay 优先 + 全局封顶 + abortable sleep。
3. 重试只管起流前；起流后走消息级恢复；不透明 400 修一次输出上限。
4. 空闲看门狗 + `onStall` abort + 排空，防双计费。
5. 代理 dispatcher 热替换：先装新后关旧 + 30s 节流 + route 上报。
## 6. 增补：中继细节与 main 下发双路（子代理调研回填）

- `startSystemProxyRelay(resolveProxy)`（`system-proxy-relay.ts:30`）：127.0.0.1 随机口 SOCKS5，用户名 `system-auto` + 32B 随机密码（`timingSafeEqual` 比对）；读首字节判 http（字母）/ https（0x16），拼 URL 调 Electron `session.resolveProxy`，按 `DIRECT / PROXY / HTTP / HTTPS / SOCKS` 分流直连 / `connectTcp` / `connectViaProxy` / HTTP-forward。
- `startAuthenticatedProxyRelay`（`authenticated-proxy-relay.ts:17`，issue #490）：Chromium `proxyRules` 不能带 userinfo/SOCKS 账密时的 main 侧 loopback 中继，裸 `socks5://127.0.0.1:port` 给 Chromium，账密只留 main/Node 侧。
- 下发双路：启动继承 env（`sidecar.ts:682 PI_DESKTOP_PROXY_JSON → applyNodeNetworkProxy`）+ 热更新 `sidecar.configure { networkProxy, systemProxyRelayUrl }`（启动 `runtime/sidecar.ts:692`，设置保存 `ipc/settings-ipc.ts:103`，失败靠下次 spawn 的 env 兜底）。`sidecar.configure` handler（`sidecar.ts:387`）只做传输装配，注释“Main owns host-core”。
- `recoverProviderTransport`（`runtime.ts:6344`）：`observeFailure → rebuildNodeNetworkTransport()`，stderr 打 `rebuilt/skipped + route`；`resetRunRecoveryState`（`:6371`）每 run 清零。
- `provider-transport-recovery.ts:124/155/199`：`describeProviderFetchFailure`（errno/route 提取）、`createProviderTransportHealth`（连续同 origin 无响应 2 次才重建）、`withProviderFetchFailure`（把 transport cause 贴回分类错误）。
