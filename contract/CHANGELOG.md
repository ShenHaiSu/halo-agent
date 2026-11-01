# 契约变更记录

> **规则**：已冻结的字段**不允许**改语义或类型。任何破坏性变更需升 contractVersion 并写迁移说明。
> 只允许新增**可选**字段（不写 = 使用默认值）。

 ## [4] — 2026-11-01 · P18 上下文压缩实效修复（非破坏性，只做加法）
 
 **非破坏性增补**（`contractVersion` 3 → 4，只新增可选字段，无新方法）：
 
 - `session.compact result += totalBefore?/totalAfter?/keptCount?`（`removed/summary` 保持必填；`removed==0` 时 `totalBefore==totalAfter`，前端弹“消息太少”）。
 - `session.compacted += turns?/totalBefore?/totalAfter?`（`kept` 保留兼容，新前端优先用 `turns` 全量替换；老引擎无 `turns` 时新前端回退 `loadSnapshot()`）。
 - `system.diagnostics result += compact?{lastRemoved,lastBefore,lastAfter,lastTsMs}`（内存最新一次，不持久化）。
 - 前端跑 `node scripts/gen-contract-types.mjs` 重生成 `src/Web.Client/src/contract/agent.d.ts`。
 ## [3] — P17 用量分析修复（非破坏性）
 
 **非破坏性增补**（`contractVersion` 保持 `3`，只新增 1 个方法）：
 
 - 新增 `usage.exportFile {同 usage.export 筛选 + path!, maskMap?} → {path, wrote, truncated, degraded?}`（`path` 必需：绝对路径 `.csv`，非法/越界报 `params.invalid`；`maskMap: providerId→假名` 只做 provider 字段级替换；引擎先写 `path.tmp` 再 rename，引擎写 BOM；旧 `usage.export` 保留给浏览器/Mock）。
 - 其余 `usage.*` 不变（`sessionId` 参数保留）。
 - 前端跑 `node scripts/gen-contract-types.mjs` 重生成 `src/Web.Client/src/contract/agent.d.ts`。
 ## [3] — 2026-11-01 · P16 用量分析（非破坏性）
 
 **非破坏性增补**（`contractVersion` 保持 `3`，只新增 5 个方法 + 3 个类型）：
 
 - 新增 `usage.query {fromMs?,toMs?,providerId?,modelId?,sessionId?,tzOffsetMin?,page,pageSize} → {items: UsageRow[], total, degraded?}`（恒 desc，`order:'asc'` 直接 `params.invalid`）。
 - 新增 `usage.summary`（同筛选无分页）`→ {input,cached,output,total,requests,hitRate,byDay,byModel,degraded?}`（`hitRate` 小数 0~1；`byDay` 按 `tzOffsetMin` 切天补 0；`byModel` 恒双键）。
 - 新增 `usage.stats {} → {sizeBytes,rowCount,degraded?}`；`usage.export`（同筛选，上限 50000）`→ {csv,truncated,degraded?}`；`usage.clear {beforeMs} → {removed,degraded?}`（`beforeMs` 须为过去时间）。
 - 无金额字段（P16 否决单价表 + 费用估算）。
 - 前端跑 `node scripts/gen-contract-types.mjs` 重生成 `src/Web.Client/src/contract/agent.d.ts`。
## [3] — P15 回合渲染重构（破坏性）

**破坏性**（contractVersion 2 → 3，不向后兼容；frame.Protocol 仍 1；旧历史打开即重排为 TurnView，不写回）：

- 新增事件 round.ended {used,hasTools}（State 类，不合并、不丢）；text.delta/reasoning.delta 新增 used>=1；tool.started/finished 新增 used/argsTotal|outputTotal/truncated（头部 2k）；turn.finished 新增 FinalUsed>=1 必需。
- StateSnapshot.messages → turns: TurnView[]；session.historyLoaded.messages → turns；session.compacted.kept MessageView[] → TurnView[]；session.resume {count,messages} → {count,turns}。
- MessageView 新增 ord；ToolCallView 新增 argsTotal/outputTotal/truncated。
- 新增 tool.result {toolCallId,offset,limit} → {toolCallId,argsSlice,outputSlice,argsTotal,outputTotal,found,reason}（running→Found=false reason=running；释放后 Found=false）。
- 转写：旧 v2 messages/resync 快照打开走 resync→turns 强制路径；删除 stream.streamingMessageId（改 currentTurn{turnId,activeOrd}）、committed/mergeAdjacent 增量分支。
- 前端跑 node scripts/gen-contract-types.mjs 重生成 src/Web.Client/src/contract/agent.d.ts。

 ## [2] — 2026-10-30 · P13 responses 协议（非破坏性）

 **非破坏性增补**（`contractVersion` 保持 `2`，只加可选字段，缺字段回落旧语义）：

 - `MessageView` 新增可选 `reasoningKind ("full"|"summary"，缺省回落 full)` / `responseId (string|null，仅诊断展示，不做快照主键)`
 - `ToolCallView` 新增可选 `kind ("function"|"unknown"，缺省 function)` / `rawType (string|null)` / `payload (object|null，C 预留，本次置 null，原样透传，additionalProperties:true)`
 - 事件载荷新增可选：`turn.finished.responseId` / `reasoning.delta.kind` / `tool.started.kind,rawType`
 - `usage.dropped` 契约本就是 `boolean`；前端 `stream.usage.dropped:number` 混用清理为 `boolean`
 - 前端跑 `node scripts/gen-contract-types.mjs` 重生成 `src/Web.Client/src/contract/agent.d.ts`

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
