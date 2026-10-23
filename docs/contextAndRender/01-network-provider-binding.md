# 网络请求构建（上）：Provider 绑定与 Header 管道

源码：`packages/agent-runtime/src/provider-binding.ts`，
`provider-headers.ts`，`opencode-session-headers.ts`，
装配点 `runtime.ts :: new Agent({ streamFn })`（约 L2123）。

## 1. 总装配线（顺序即正确性）

`runtime.ts` 中每个模型请求都走同一 `streamFn(m, context, options)`：

```
context.messages (已排好序的稳定前缀, 见 03)
  → estimateContextTokens (预算快照, 见 04)
  → withOpenCodeSessionHeaders (OpenCode 路由头)
  → withProviderHeaders (自定义头 + fetch wrapper)
  → providerRequestFetch (按 api 决定 fetch 去留)
  → captureProviderResponse (状态/字节/errno 捕获)
  → createProviderRetryStream (setup 重试)
  → withStreamIdleTimeout (180s 零事件看门狗, 见 02)
  → pi-ai adapter stream
```

**复刻铁律**：`fetch wrapper 是最后一写者**。因为 pi-ai 内各 adapter（Anthropic SDK、Codex）会在其后追加 `User-Agent`，只有 wrapper 能盖住它们。注释原文见 `provider-headers.ts:1-17`。

## 2. `apiBindingForStyle`：apiStyle → wire API

文件 `provider-binding.ts:160-207`。

```ts
opencode_go            → openai-completions + https://opencode.ai
responses              → openai-responses
anthropic_messages     → anthropic-messages
openai_codex_responses → openai-codex-responses (chatgpt.com/backend-api)
pi_messages            → pi-messages (radius.pi.dev)
google_generative_ai   → google-generative-ai
default                → openai-completions
```

`apiBindingForProviderModel` 再叠一层兼容判断（`providerRequestTransport`，L229-269）：

- custom 端点：用户存的 `apiStyle` 优先，catalog 不许静默改写 wire 协议。
- 非 custom：model 级与 provider 级相容才用 model 级（OpenAI 系三者互通），否则回落 provider 级。
- OpenCode Go + catalog baseUrl 分流时走 Responses 路由（issue #105）。

坑：`runtimeBaseUrlForApi`（L95-100）对 Anthropic 做 `/v1` 归一化，防止配了 `/v1` 后缀的 baseUrl 变成 `/v1/v1/messages`。

## 3. `buildProviderModel`：拼出 pi-ai Model

文件 `provider-binding.ts:353-463`。字段优先级：

```
id/api/provider/baseUrl ← binding + transport 解析
headers ← copilotDefaults + catalog.headers + (无鉴权google的 x-goog-api-key:"")
compat  ← sessionAffinityCompat(底层) + catalog.compat + transcriptCompat(顶层)
         + zhipu/deepseek/thinkingProtocol 特例
```

要点：

- `provider` 字段：插件 agent 用 row id，普通用 `vendorKey || rowId`。transcript 归属与鉴权绑定都靠它。
- Copilot 特殊：`nativeCopilotHeaders` 保留 pi-ai 原生 client 身份头；`copilotRequestAuth` 把行级 token 转成 `Authorization: Bearer` 并把 `X-Api-Key` 置 null（L309-333），否则 Anthropic SDK 走默认凭证链。
- Claude effort-ladder（无 `budget_tokens` 只有 `effort`）强制 `forceAdaptiveThinking: true`，否则 400（L342-351）。
- OpenAI 兼容网关默认**不**发 `developer` role（`supportsDeveloperRole` 须显式 true，L409-423）。

## 4. Header 管道：清洗 → 合并 → 双写

文件 `provider-headers.ts`。

### 4.1 清洗 `normalizeProviderHeaders`（L97-126）

- 非 string 值、空 key/value 直接丢。
- 全角→半角折叠（`@pi-desktop/shared/header-value.ts`），仍含 >U+00FF 或控制字符则丢（避免 `Headers.set` 抛 ByteString TypeError 中断整轮）。
- 禁止键（L33-51）：`authorization/proxy-authorization/host/content-type/content-length/cookie/set-cookie/connection/transfer-encoding/x-api-key/chatgpt-account-id/x-opencode-session` 等。鉴权头只能走 `resolveAuth`，业务头不许砸签名。
- 上限：32 个，key ≤256B，value ≤`HEADER_VALUE_MAX_BYTES`。去重按小写，后者覆盖前者。

### 4.2 合并与双写 `withProviderHeaders`（L189-216）

```ts
merged = merge(base=options.headers, extra=provider.headers) // extra 赢
if (adapterAcceptsCustomFetch(api)) {
  headers: merged,
  fetch: wrap(options.fetch ?? globalFetch) // wrapper 内再 set 一次
} else {
  headers: merged,   // Google 系只留 headers
  fetch: undefined   // 清掉调用方 fetch (issue #1072)
}
```

`adapterAcceptsCustomFetch`（`provider-binding.ts:111-113`）：只有 `google-generative-ai / google-vertex` 返回 false。

`runWithProviderHeaders + AsyncLocalStorage`（L223-251）：给 pi-ai 全局 `fetch`（OAuth 登录/刷新）也带上本次 override，空 store 零开销。

## 5. OpenCode 会话头

文件 `opencode-session-headers.ts`。

- 识别：`apiStyle==opencode_go` 或 vendor/providerId 为 `opencode(_-go)` 或 host 为 `*.opencode.ai`。
- 注入：`x-opencode-session: <desktop sessionId>` + `x-opencode-client: pi-desktop` + `User-Agent: pi-desktop/<APP_VERSION>`。
- 调用方显式头赢，但空的 `x-opencode-session` 会被回填（防网关 400）。
- 无调用方 sessionId 时每 call 一个 UUID，保证网关不拒；**重试复用同一 options 对象**所以 UUID 不变。
- Compaction 摘要必须传**运行时自己的 sessionId**（`compaction-request.ts:47-69`），让摘要打到同一后端，否则 `/compact` 在 OpenCode 上 400 而普通 turn 正常。

## 6. OAuth：sidecar 不持长凭证

`sidecar.ts :: withVendorAuth` + `provider-binding.ts :: createProviderModels`：

- main 下发的 launch payload 对 OAuth 行**不带任何 credential**。
- sidecar 给 pi-ai 的 `resolveAuth` 是一个回调：每次签请求前经 `hostProxy.call("provider.resolveAuth", {sessionId, providerId})` 向 main 要一个短期 `ModelAuth`。refresh token 永不过 sidecar 边界。
- `copilotRequestAuth` 在 resolve 后再做 Bearer 改写（见 §3）。

## 7. 无鉴权端点

`providerRequestFetch(api, fetchFn, authKind)`（L148-156）+ `withoutNoAuthCredential`（L118-139）：

- `authKind=="none"` 时 pi-ai 要求占位 `pi-desktop-no-auth`，发出前把 `api-key/x-api-key/authorization` 等于该占位的头删掉，网关看到的是干净的匿名请求。

## 复刻清单

1. 照搬 `FORBIDDEN_HEADER_KEYS` + `normalizeProviderHeaders` 清洗。
2. 实现 `withProviderHeaders` 双写（headers + fetch wrapper），Google 特例分支必留。
3. OpenCode 三头 + 空值回填 + 重试复用 options。
4. OAuth 用“按请求回调取短期 token”，不要在 agent 侧存 refresh token。
5. Anthropic baseUrl `/v1` 归一化 + Copilot Bearer 改写 + Claude adaptive 强制，三者都是 400 重灾区。
## 8. 增补：内层重试归零与扩展/亲和钩子（子代理调研回填）

- `runtime.ts:418 PROVIDER_REQUEST_MAX_RETRIES = 0`：传给 pi-ai 的 `maxRetries` 恒 0，`retryProviderRequest` 被禁用，重试只走 runtime 两套预算。`@pi-desktop/shared/provider-retry.ts:2 PROVIDER_RETRY_MAX_RETRIES = 10` 为两池共享上限。
- `streamFn` 精确行号（`runtime.ts:2124-2256`）：`transportHealth.reset()` → OpenCode 头 → Provider 头 → `withExtensionProviderHooks`（`before_provider_request` 串 `onPayload`、`after_provider_response` 串 `onResponse`，扩展头经 `mergeProviderHeaders` 合入，`:8967`）→ `withSessionAffinityOptions`（`x-session-affinity` 头 + `prompt_cache_key` 经 `onPayload` 尾部追加，`session-affinity.ts:146`）→ `maxTokens = clampOutputToContext` + `stallAbort` 合并 signal → `createProviderRetryStream(... accountModelStream(models.stream | streamSimple))` → `withStreamIdleTimeout`。
- `accountModelStream`（`request-usage.ts:38`）：每次物理 retry 独立 `operationId` 计 usage，`onUsage` 归因不串。
- `claimProviderRetry`（`runtime.ts:6237`）：`phase` 被 `void` 忽略，setup/stream 同一预算；`infiniteProviderRetry` 只去上限。
- OAuth token 端点独立重试（patch 847-883）：仅 429 最多 2 次，`Retry-After` 经 `tokenRetryDelay`，30s 总 deadline。
- 子代理同模式（`subagent-model-binding.ts:67` + `runtime.ts:4316-4326`）：`resolveSubagentModel` 后若 `authKind == oauth` 再包一层 `resolveAuth` 回调。
