# PI-Desktop 三大优秀设计总览：网络 / 上下文缓存 / 前端渲染

> 路径：`migrationDocs/contextAndRender/`
> 目标读者：要把这套设计复刻到另一个 Agent 项目的工程师。
> 源码版本锚点：`pi-desktop 0.21.0-beta.6`，`@earendil-works/pi-* 1.3.0 + patches`。

## 文档地图

| 文档 | 主题 | 核心复刻点 |
|---|---|---|
| `01-network-provider-binding.md` | 网络请求构建（上）：Provider/Model 绑定与 Header 管道 | `apiBindingForProviderModel` → `buildProviderModel` → `withProviderHeaders` → `withOpenCodeSessionHeaders` → `providerRequestFetch` 的洋葱圈顺序 |
| `02-network-retry-proxy.md` | 网络请求构建（下）：重试/观测/代理传输 | `captureProviderResponse` + `createProviderRetryStream` + `withStreamIdleTimeout` + `node-proxy` dispatcher 热重建 |
| `03-context-stable-prefix.md` | 高缓存命中（上）：稳定前缀的 System Transcript | `desktopSectionNames` 固定排序 + `syncSystemSections` 增量补丁 + `transcriptCompat` 网关验证 |
| `04-context-budget-compaction.md` | 高缓存命中（下）：预算/估算/Compaction | `contextBudgetLimitsFor` 公式 + `estimateContextTokens` usage-anchor + `compactionRequestOptions` 会话亲和 |
| `05-render-transcript-store.md` | 高性能渲染（上）：Transcript 窗口与 Store 切片 | retained `SessionPane` + `useTranscriptView` + `useTranscriptScroll` 历史/尾部分离 + zustand 细粒度 selector + `frame-batcher` |
| `06-render-markdown.md` | 高性能渲染（下）：流式 Markdown | `advanceMarkdownBlocks` 稳定前缀复用 + `Block memo` + shiki 增量 token + mermaid 视口懒渲染 |

## 一句话速览

1. **网络**：所有请求共用一条 `streamFn` 装配线。顺序不可换：`OpenCode会话头 → 自定义Header(fetch wrapper兜底) → 会话亲和/prompt_cache_key → fetch捕获层 → 重试流 → 空闲看门狗`。Google 适配器是特例：不接受自定义 `fetch`，只收 `headers`。
2. **上下文**：缓存命中的本质是“字节级稳定”。System 用 `sections` 有序拼接（`runtime → tool_activation → skills* → context`），只追加 `timestamp+1` 的增量 system 行；估算用 provider 回传 `usage` 做锚点，只估算锚点之后；Compaction 摘要请求复用同一 `sessionId` 打到同一网关后端。
3. **渲染**：Transcript 按 `historyEntries + tailEntry` 分离，历史行 memo 冻结，尾部才随流更新；Markdown 按块切分 memo，流式只重解析 tail；重活（shiki/mermaid/katex）全部懒加载 + 视口门控 + 行级缓存。

## 复刻优先级建议

- P0：Header 管道顺序 + `captureProviderResponse`（否则 429 误判、鉴权重试全错）。
- P0：System sections 固定排序（否则 Anthropic prompt caching 直接失效，多花 30%+ token 钱）。
- P1：`estimateContextTokens` usage-anchor（否则预算抖动导致过早/过晚 compaction）。
- P1：Markdown 块级 memo + `frame-batcher`（否则长会话流式必掉帧）。
- P2：`rebuildNodeNetworkTransport` 节流重建 + `useTranscriptScroll` 双区滚动。

## 源码入口速查

```
网络装配：packages/agent-runtime/src/runtime.ts :: streamFn (约 L2123 附近 new Agent)
绑定：    packages/agent-runtime/src/provider-binding.ts
Header：  packages/agent-runtime/src/provider-headers.ts
OpenCode：packages/agent-runtime/src/opencode-session-headers.ts
重试：    packages/agent-runtime/src/provider-retry.ts
代理：    packages/agent-runtime/src/node-proxy.ts / system-proxy-relay.ts
Sidecar： packages/agent-runtime/src/sidecar.ts :: withVendorAuth / runtimeFor
上下文：  packages/agent-runtime/src/system-transcript.ts / context-budget.ts
          packages/agent-runtime/src/pi-runtime-estimates.ts / compaction-request.ts
渲染：    apps/desktop/src/components/SessionPane.tsx
          apps/desktop/src/features/chat/transcript/ChatTranscript.tsx
          apps/desktop/src/features/chat/transcript/hooks/useTranscriptScroll.ts
          apps/desktop/src/components/Markdown.tsx
          apps/desktop/src/lib/markdown-blocks.ts / frame-batcher.ts
```
