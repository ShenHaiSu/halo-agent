# 契约变更记录

> **规则**：已冻结的字段**不允许**改语义或类型。任何破坏性变更需升 `contractVersion` 并写迁移说明。
> 只允许新增**可选**字段（不写 = 使用默认值）。

## [1] - 2026-10-27 · P3 增补（非破坏性）

## [2] - 2026-10-29 · P10 provider-model-rework（破坏性）

**破坏性**（`contractVersion` 1 → 2，不向后兼容；旧 config.json 不迁移，启动嗅探旧形状即视为无效并自动备份一次 `config.json.bak.*`，`settings.get.migrationRequired=true`，前端弹重建）：

- `settings.get` 整树：`{ activeProvider/activeModel/migrationRequired/providers[{id/displayName/baseUrl/apiKeySet/headersEnabled/headers/useProxy/modelCount/models[{id/displayName/protocol/contextWindow/maxTokens/acceptsImages/includeReasoningHistory}]}]/proxy/retry/maxTurns/persona/dataDir/configPath }`；删除顶层 `model/provider/baseUrl/apiKeySet/apiKeyEnv/contextWindow/headers/extraHeadersEnabled/network/providers(name,model,baseUrl)`。
- 新增 `settings.saveProviders { providers: Record<id, {displayName/baseUrl/apiKey/clearKey/headersEnabled/headers/useProxy/models[]}> }`（整包校验失败整包拒绝，原子性）；删除 `settings.saveModel`。
- 新增 `settings.setActive { providerId/modelId }`（持久化+热切，重建 HttpClient，无需重启；busy 拒绝，dirty 时前端禁用）。
- `settings.saveNetwork` 瘦身：仅 `{ mode/retry/url }`；`headers/headersEnabled/providerHeaders` 直接 422。
- `settings.probeModels` 加参 `{ providerId/headers/useProxy }`（带头、走对代理；含会话模板的头跳过）。
- 删除 `settings.saveExtraHeaders`、`state.setProvider`（改调 `settings.setActive`）。
- `AgentState` 改为 `{ provider/providerDisplay/model/modelDisplay/protocol/baseUrl/apiKeySet/contextWindow/maxTokens/acceptsImages/includeReasoning/proxyInUse/configured }`；`MessageView` 新增 `hasImages/attachmentCount`（不含 base64 全量）。
- `protocol=responses` 全链路 fail-fast（`save/setActive/Chat` 抛 `notSupported`，前端 disabled+tooltip；真形态 P11）。
- `maxTokens` 直通 `max_tokens`（只发该字段）；`acceptsImages=false` 硬拦含图消息；`includeReasoningHistory=false` 发送侧 strip（落盘保留）；`useProxy=false` 直连；全局 `auto` 钉死为直连。
- CLI/env 旁路删除：`--model/--base-url/--api-key/--provider/--proxy/--no-proxy` + `HALO_MODEL/HALO_BASE_URL/HALO_API_KEY/HALO_PROVIDER/HALO_PROXY/OPENAI_*`，仅保留 `--data-dir`。

## [1] — 2026-10-27 · P07 联调修复增补（非破坏性）

**非破坏性增补**（`contractVersion` 保持 `1`）：

- 新增 `asset.themes.list`（params: null）→ `{ items: ThemeSummary[] }`（只列元信息，不含 tokens）
- 新增 `asset.themes.get`（params: `{ id }`）→ `ThemeFile`（完整 token 集）
- `settings.get` 结果新增可选 `network` 子对象 `{ mode, url, retry, headers, headersEnabled, providerHeaders }`（顶层 `proxy/headers/extraHeadersEnabled` 保留兼容）
- `settings.saveNetwork` 参数 `providerHeaders` 修正为对象映射 `Record<provider, HeaderPair[]>`（旧 schema 误写为数组，前端实际发送对象；无存量数组客户端）
- `settings.saveExtraHeaders` 结果新增可选 `notes: string[]`
- 新增类型 `ThemeSummary` / `ThemeFile`

**背景**：P3 文档 §11.2 曾规划 `asset.listThemes` / `asset.readTheme`，但从未进入契约；P4 前端已按此调用导致 `method.notFound`。本次按既有 `asset.<资源>.<动作>` 命名规范正式纳入契约，旧名不再保留（代码未发布）。

**影响**：Engine.Server 注册表 +2 方法，`KnownMethods` 58 → 60，`agent.d.ts` 重新生成。

P3 按阶段文档要求补充分工元数据与宿主对话框方法。`contractVersion` 保持 1：
- 全部 58 个方法新增可选字段 `implementedIn`（`engine` | `host`，缺省按 `engine` 理解）
- 新增 4 个宿主实现方法：`fs.browse` / `fs.pickFiles` / `fs.pickSavePath` / `fs.readImageAsBase64`
  （`implementedIn: "host"`，由宿主 `AgentBridge` 就地处理，不经过引擎进程）
- 既有 54 个方法的 `params` / `result` 形状零改动；帧信封、事件、错误码零改动

## [1] - 2025-01-01 · 冻结

首次冻结。包含 54 个方法、24 个事件、15 个错误码。

**冻结内容**：
- 帧信封：`req` / `res` / `evt`，字段 `t/id/m/p/r/e/ok/seq/k`
- `state.snapshot` 为全量快照，前端据此完整重建界面
- 询问机制：单待答 + `askId` + 引擎侧政策兜底

**后续约束**：
- P2–P5 只允许新增可选字段
- 破坏性变更必须升版本并在本文档记录迁移方式
