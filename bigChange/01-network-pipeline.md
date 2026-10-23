# 01 网络装配线：Header 管道 + 绑定 + 捕获层（P0-1）

第二参考：`contextAndRender/01-network-provider-binding.md`（思想），本栈重写实现。

## 1. 前置检查（全部满足才开工）

- [ ] 打开 `LlmGatewayClient.cs::ExecuteStreamPipelineAsync`，确认当前顺序：序列化→`SendRequestAsync`→400 降级→解析流。记录所有直接 `new HttpRequestMessage` 的位置。
- [ ] 打开 `Resilience.cs::SendOnceAsync`，确认当前只做：Bearer 注入 + `ApplyCustomHeaders`。记录缺失：无清洗、无禁键、无长度上限、无捕获层。
- [ ] 打开 `HeaderVariableResolver.cs`，列出所有可解析变量（session-hash/requestId 等），确认自定义 Header 模板语法。
- [ ] 确认协议只有 `Chat/Resp` 两种（`LlmProtocolType`），无 Google/Copilot/OpenCode 专属分支——对应第二参考特例全部不抄。

## 2. 阶段目标

建立**唯一装配线**，顺序不可换：

```
Serialize(adapter) → NormalizeHeaders → BuildRequest(鉴权/业务分离)
→ Capture(观测) → Retry(02) → IdleWatchdog(02) → ParseStream(adapter)
```

任何请求不许绕过该线（含压缩摘要、标题生成、子代理）。

## 3. 实现办法

### 3.1 新建 `LlmHeaderPipeline.cs`（Infrastructure/Network）

- `NormalizeCustomHeaders(IEnumerable<CustomHeaderEntry>)`：
  - 丢弃：空 key/value、非 string 值、全角未折叠仍含 `>U+00FF`/控制字符（抛前清洗，避免 `Headers.Add` 抛 `FormatException` 中断整轮）。
  - 禁键（大小写不敏感）：`authorization/proxy-authorization/host/content-length/content-type/cookie/set-cookie/connection/transfer-encoding/x-api-key`。命中直接丢并 Warn 日志。
  - 上限：32 条，key ≤256B，value ≤8KB。去重按小写，后者覆盖前者。
- `ApplyHeaders(HttpRequestMessage, normalized, HeaderContext)`：业务头只 `TryAddWithoutValidation`，鉴权头只走第 3.2 步。

### 3.2 鉴权与业务分离（改 `SendOnceAsync`）

- 删除业务代码直接写 `Authorization` 的路径。唯一鉴权点：
  `rawApiKey = request.Provider.GetEffectiveApiKey()` → 为空则匿名（删 `api-key/x-api-key/authorization` 残留）→ 非空则 `Bearer`。
- `baseUrl` 归一化：去尾 `/`，Chat 补 `/chat/completions`、Resp 补 `/responses` 之前先统一；Anthropic 兼容端点去重 `/v1/v1`。
- 保留现有 `strict` 剥离 + `stream_options` 降级，但下沉为装配线上的显式 `DowngradeStep`，不散落在 `if` 里。

### 3.3 新建 `ProviderResponseCapture.cs`（观测层，被 02 消费）

- 包住 `HttpClient.SendAsync`：
  - 发前记 `requestBytes = Encoding.UTF8.GetByteCount(payload)`（只记长度）。
  - 成功记 `status + responseHeaders(小写)`；异常记 `errno`（`HttpRequestException.InnerException.SocketError/HttpStatusCode` 映射 + `TaskCanceledException` 区分取消/超时）。
  - 原样抛出，不改变调用方视角。
- 分类函数 `ClassifyProviderError(status, ex)`：`429→RateLimited`；鉴权/上下文超限（401/403/400 明确提及 context/token）→Terminal；其余 5xx/传输异常→Transient。Terminal 永不重试。

### 3.4 统一入口改造

- `LlmGatewayClient` 所有 `SendRequestAsync(client, request, url, payload, ct)` 调用点改为 `SendThroughPipelineAsync`（新名，旧方法删除）。
- 压缩（`CompactionService`）、标题（`SessionTitleBridge`）、子代理统一走 `ILlmGatewayClient.StreamAsync`，禁止自建 `HttpClient`。

## 4. 验收标准

- [ ] 单测：禁键/超长/全角/32 条上限/去重，后者覆盖前者；鉴权头无法被业务头覆盖。
- [ ] 单测：匿名请求发出前不含任何鉴权残留头；`/v1` 后缀不产生 `/v1/v1`。
- [ ] 集成：同一 payload 连续发两轮，`llm-trace` 中 `prefixHash` 一致且无 `Headers.Add` 异常。
- [ ] 全局搜索 `new HttpRequestMessage(` 只剩管道内一处；旧 `SendRequestAsync/SendOnceAsync` 已删除（破坏性，允许）。
- [ ] 第二参考 P0 对应项：无 429 误判为 terminal，无鉴权失败被重试。

## 5. 风险与回滚

- 破坏点：自定义 Header 曾依赖禁键（如有人把 token 写在自定义头）会被清洗掉。按 08 做一次性迁移提示，不兼容。
- 回滚：本阶段独立可回滚（仅网络层），回滚即还原两个文件。
